using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

using static NoLazyWorkers.NoLazyUtilities;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Stations.MixingStationUtilities;
using static NoLazyWorkers.Stations.MixingStationConfigUtilities;
using ScheduleOne.Employees;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.CacheManager.ManagedDictionaries;
using ScheduleOne.EntityFramework;
using ScheduleOne.Property;
using ScheduleOne.Persistence.Datas;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.CacheManager.Extensions;
using static NoLazyWorkers.TaskService.Extensions;
using NoLazyWorkers.CacheManager;
using Unity.Collections;

namespace NoLazyWorkers.Stations
{
  public static class MixingStationExtensions
  {
    public static Dictionary<Guid, StationRouteManager> RouteManagers { get; } = new();
    public static GameObject MixingRouteListTemplate { get; set; }

    public class MixingStationAdapter : IStationAdapter
    {
      private readonly MixingStation _station;
      private readonly StationRouteManager _routeManager;
      private StationState<MixingStationStates> _stationState;
      private StationData _stationData;

      public MixingStationAdapter(MixingStation station)
      {
        CacheService.GetOrCreateService(station.ParentProperty).IStations.Add(GUID, this);

        _stationState = new StationState<MixingStationStates>(this)
        {
          State = MixingStationStates.Idle,
          LastValidatedTime = 0f
        };
        _station = station ?? throw new ArgumentNullException(nameof(station));
        if (!RouteManagers.ContainsKey(station.GUID))
        {
          RouteManagers[station.GUID] = new StationRouteManager(station.stationConfiguration, station);
        }
        _routeManager = RouteManagers[station.GUID];
        _stationData = new StationData(this);
      }

      public MixingStation Station => _station;
      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public List<ItemSlot> InsertSlots => [_station.MixerSlot];
      public List<ItemSlot> ProductSlots => [_station.ProductSlot];
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => _station.IsOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
      public bool HasActiveOperation => _station.CurrentMixOperation != null;
      public int StartThreshold => (int)(_station.Configuration as MixingStationConfiguration).StartThrehold.Value;
      public int MaxProductQuantity => _station.ProductSlot?.ItemInstance.StackLimit ?? 0;
      public ITransitEntity TransitEntity => _station as ITransitEntity;
      public BuildableItem Buildable => _station as BuildableItem;
      public Property ParentProperty => _station.ParentProperty;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemField> GetInputItemForProduct() => [GetInputItemForProductSlot(this)];
      public Type TypeOf => _station.GetType();
      public void StartOperation(Employee employee) => (employee as Chemist).StartMixingStation(_station);
      public NativeList<ItemKey> RefillList() => _routeManager.Refills;
      public StationData StationData => _stationData;
      public bool CanRefill(ItemInstance item) => item.CanRefill(_routeManager);
      public IStationState StationState
      {
        get => _stationState;
        set => _stationState = (StationState<MixingStationStates>)value;
      }

      public EntityType EntityType => EntityType.MixingStation;
    }

    public enum MixingStationStates
    {
      Idle,        // No action needed
      HasOutput,   // Output slot has items
      CanStart,    // Ready to operate
      NeedsRestock, // Requires ingredient restock
      InUse
    }
  }

  public static class MixingStationUtilities
  {
    public static ItemField GetInputItemForProductSlot(IStationAdapter station)
    {
      try
      {
        if (!(station is MixingStationAdapter adapter))
        {
          Log(Level.Error,
              $"GetInputItemForProductSlot: Invalid station adapter, GUID={station?.GUID}",
              Category.MixingStation);
          return null;
        }

        var productInSlot = adapter.Station.ProductSlot.ItemInstance?.Definition as ProductDefinition;
        if (productInSlot == null)
        {
          Log(Level.Warning,
              $"GetInputItemForProductSlot: Product slot item is not a ProductDefinition for station={adapter.GUID}",
              Category.MixingStation);
          return null;
        }

        var routeManager = RouteManagers.GetValueOrDefault(adapter.GUID);
        if (routeManager == null)
        {
          Log(Level.Warning,
              $"GetInputItemForProductSlot: No StationRouteManager for station={adapter.GUID}",
              Category.MixingStation);
          return null;
        }

        var matchingRoute = routeManager.Routes.FirstOrDefault(route => route.Product?.SelectedItem == productInSlot);
        if (matchingRoute == null)
        {
          Log(Level.Info,
              $"GetInputItemForProductSlot: No route matches product={productInSlot.Name} for station={adapter.GUID}",
              Category.MixingStation);
          return null;
        }

        return matchingRoute.MixerItem;
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"GetInputItemForProductSlot: Failed for station {station?.GUID}, error: {e}",
            Category.MixingStation);
        return null;
      }
    }

    public static bool CanRefill(this ItemInstance item, StationRouteManager manager)
    {
      if (new ItemKey(item).AdvCanStackWithBurst(manager.Refills, allowTargetHigherQuality: true))
        return true;
      return false;
    }
  }

  public class StationRouteManager
  {
    public Guid StationGuid { get; }
    public MixingStationConfiguration Config { get; }
    public QualityField Quality { get; private set; }
    public List<MixingRoute> Routes { get; } = new List<MixingRoute>();
    public NativeList<ItemKey> Refills = new(Allocator.Persistent);
    public const int MaxRoutes = 11;
    public const float MaxThreshold = 20f;

    public StationRouteManager(MixingStationConfiguration config, MixingStation station)
    {
      if (station == null)
      {
        Log(Level.Error, "StationRouteManager: Constructor failed, station is null", Category.MixingStation);
        throw new ArgumentNullException(nameof(station));
      }
      StationGuid = station.GUID;
      Config = config;
      if (Config == null)
      {
        Log(Level.Error,
            $"StationRouteManager: Constructor failed for station {station.GUID}, Configuration is null or not MixingStationConfiguration",
            Category.MixingStation);
        throw new InvalidOperationException("MixingStation Configuration is null or invalid");
      }
      Quality = new QualityField(Config);
      Quality.onValueChanged.AddListener(_ =>
      {
        UpdateRefillsQuality();
        Config.InvokeChanged();
      });
      Log(Level.Info, $"StationRouteManager: Initialized for station {StationGuid}", Category.MixingStation);
    }

    public void AddRoute()
    {
      if (Routes.Count >= MaxRoutes) return;
      var route = new MixingRoute(Config);
      Routes.Add(route);
      Refills.Add(new ItemKey());
      Config.InvokeChanged();
      Log(Level.Info,
          $"StationRouteManager: Added route for station {StationGuid}, total routes: {Routes.Count}",
          Category.MixingStation);
    }

    public void RemoveRoute(int index)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes.RemoveAt(index);
      Refills.RemoveAt(index);
      Config.InvokeChanged();
      Log(Level.Info,
          $"StationRouteManager: Removed route at index {index} for station {StationGuid}, total routes: {Routes.Count}",
          Category.MixingStation);
    }

    public void UpdateProduct(int index, ItemDefinition product)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes[index].Product.SelectedItem = product;
      UpdateRefill(index);
      Config.InvokeChanged();
      Log(Level.Info,
          $"StationRouteManager: Updated product for route {index} in station {StationGuid}",
          Category.MixingStation);
    }

    public void UpdateMixer(int index, ItemDefinition mixer)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes[index].MixerItem.SelectedItem = mixer;
      Config.InvokeChanged();
      Log(Level.Info,
          $"StationRouteManager: Updated mixer for route {index} in station {StationGuid}",
          Category.MixingStation);
    }

    private void UpdateRefill(int index)
    {
      if (index < 0 || index >= Routes.Count) return;
      var product = Routes[index].Product.SelectedItem;
      if (product == null)
      {
        Refills[index] = new ItemKey();
        return;
      }
      var prodItem = product.GetDefaultInstance() as ProductItemInstance;
      prodItem?.SetQuality(Quality.Value);
      Refills[index] = new ItemKey(prodItem);
      Log(Level.Info,
          $"StationRouteManager: Updated refill for route {index} in station {StationGuid}",
          Category.MixingStation);
    }

    private void UpdateRefillsQuality()
    {
      for (int i = 0; i < Routes.Count; i++)
      {
        UpdateRefill(i);
      }
    }

    public JObject Serialize()
    {
      var json = new JObject
      {
        ["Quality"] = Quality.Value.ToString(),
        ["MixingRoutes"] = new JArray(Routes.Select(route => new JObject
        {
          ["Product"] = route.Product?.GetData()?.ItemID,
          ["MixerItem"] = route.MixerItem?.GetData()?.ItemID
        }))
      };
      return json;
    }

    public void Deserialize(JObject json)
    {
      Routes.Clear();
      Refills.Clear();
      if (json["MixingRoutes"] is JArray routesArray)
      {
        foreach (var routeData in routesArray)
        {
          var route = new MixingRoute(Config);
          var data = new MixingRouteData
          {
            Product = new ItemFieldData(routeData["Product"]?.ToString()),
            MixerItem = new ItemFieldData(routeData["MixerItem"]?.ToString())
          };
          route.SetData(data);
          Routes.Add(route);
          Refills.Add(new ItemKey());
          UpdateRefill(Routes.Count - 1);
        }
      }
      if (json["Quality"]?.ToString() is string qualityStr && Enum.TryParse<EQuality>(qualityStr, out var quality))
      {
        Quality.SetValue(quality, false);
      }
      Log(Level.Info,
          $"StationRouteManager: Deserialized {Routes.Count} routes for station {StationGuid}",
          Category.MixingStation);
    }
  }

  public static class MixingStationConfigUtilities
  {
    public static void RestoreConfigurations()
    {
      try
      {
        MixingStation[] mixingStations = Object.FindObjectsOfType<MixingStation>();
        Log(Level.Info,
            $"RestoreConfigurations: Found {mixingStations.Length} MixingStations",
            Category.MixingStation);

        foreach (var station in mixingStations)
        {
          if (station == null)
          {
            Log(Level.Warning,
                "RestoreConfigurations: Encountered null station",
                Category.MixingStation);
            continue;
          }

          if (station.stationConfiguration is MixingStationConfiguration config)
          {
            if (!RouteManagers.ContainsKey(station.GUID))
            {
              RouteManagers[station.GUID] = new StationRouteManager(config, station);
              Log(Level.Info,
                  $"RestoreConfigurations: Initialized StationRouteManager for station {station.GUID}",
                  Category.MixingStation);
            }
          }
          else
          {
            Log(Level.Warning,
                $"RestoreConfigurations: Invalid or missing configuration for station {station.GUID}",
                Category.MixingStation);
          }
        }
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"RestoreConfigurations: Failed, error: {e}",
            Category.MixingStation);
      }
    }

    public static void InitializeStaticRouteListTemplate()
    {
      var routeListTemplate = GetTransformTemplateFromConfigPanel(EConfigurableType.Packager, "RouteListFieldUI");
      if (routeListTemplate == null)
      {
        Log(Level.Error,
            "InitializeStaticRouteListTemplate: routeListTemplate is null",
            Category.MixingStation);
        return;
      }

      try
      {
        Log(Level.Info,
            "InitializeStaticRouteListTemplate: Initializing MixingRouteListTemplate",
            Category.MixingStation);
        var templateObj = Object.Instantiate(routeListTemplate.gameObject);
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);

        try
        {
          Log(Level.Info,
              "InitializeStaticRouteListTemplate: Adding MixingRouteListFieldUI",
              Category.MixingStation);
          var routeListFieldUI = templateObj.AddComponent<MixingRouteListFieldUI>();
        }
        catch (Exception e)
        {
          Log(Level.Error,
              $"InitializeStaticRouteListTemplate: Failed to add MixingRouteListFieldUI, error: {e}",
              Category.MixingStation);
        }

        Log(Level.Info,
            "InitializeStaticRouteListTemplate: MixingRouteListFieldUI added",
            Category.MixingStation);
        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
          Object.Destroy(defaultScript);

        if (templateObj.GetComponent<MixingRouteListFieldUI>() == null)
          templateObj.AddComponent<MixingRouteListFieldUI>();

        var contentsTransform = templateObj.transform.Find("Contents");
        Log(Level.Info,
            $"InitializeStaticRouteListTemplate: contentsTransform: {contentsTransform?.name}",
            Category.MixingStation);
        var entryTransform = templateObj.transform.Find("Contents/Entry");
        Log(Level.Info,
            $"InitializeStaticRouteListTemplate: entryTransform: {entryTransform?.name}",
            Category.MixingStation);

        if (entryTransform != null)
        {
          var productTransform = entryTransform.Find("Source");
          var mixerTransform = entryTransform.Find("Destination");
          var removeTransform = entryTransform.Find("Remove");
          Log(Level.Info,
              $"InitializeStaticRouteListTemplate: Entry hierarchy - Source: {productTransform?.name}, Destination: {mixerTransform?.name}, Remove: {removeTransform?.name}",
              Category.MixingStation);
        }

        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";
        Log(Level.Info,
            $"InitializeStaticRouteListTemplate: addNewLabel?.text: {addNewLabel?.text}",
            Category.MixingStation);

        for (int i = 0; i <= StationRouteManager.MaxRoutes; i++)
        {
          Transform entry = contentsTransform.Find($"Entry ({i})") ?? (i == 0 ? contentsTransform.Find("Entry") : null);
          Log(Level.Info,
              $"InitializeStaticRouteListTemplate: Processing Entry ({i})",
              Category.MixingStation);

          if (entry == null)
          {
            GameObject newEntry = Object.Instantiate(entryTransform.gameObject, contentsTransform, false);
            newEntry.name = $"Entry ({i})";
            newEntry.AddComponent<CanvasRenderer>();
            entry = newEntry.transform;
            var productTransform = entry.Find("Source");
            if (productTransform != null)
              productTransform.name = "ProductIMGUI";
            var mixerTransform = entry.Find("Destination");
            if (mixerTransform != null)
              mixerTransform.name = "MixerItemIMGUI";
            newEntry.transform.SetSiblingIndex(i);
          }
          else
          {
            var productTransform = entry.Find("Source");
            if (productTransform != null)
              productTransform.name = "ProductIMGUI";
            var mixerTransform = entry.Find("Destination");
            if (mixerTransform != null)
              mixerTransform.name = "MixerItemIMGUI";
          }

          var routeEntryUI = entry.GetComponent<RouteEntryUI>();
          if (routeEntryUI != null)
            Object.Destroy(routeEntryUI);
          if (entry.GetComponent<MixingRouteEntryUI>() == null)
            entry.gameObject.AddComponent<MixingRouteEntryUI>();
        }

        contentsTransform.Find("AddNew").SetAsLastSibling();
        Log(Level.Info,
            "InitializeStaticRouteListTemplate: SetAsLastSibling",
            Category.MixingStation);
        templateObj.transform.Find("Title")?.gameObject.SetActive(false);
        templateObj.transform.Find("From")?.gameObject.SetActive(false);
        templateObj.transform.Find("To")?.gameObject.SetActive(false);
        MixingRouteListTemplate = templateObj;
        Log(Level.Info,
            "InitializeStaticRouteListTemplate: Static MixingRouteListTemplate initialized successfully",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"InitializeStaticRouteListTemplate: Failed to initialize MixingRouteListTemplate, error: {e}",
            Category.MixingStation);
      }
    }
  }

  public class RouteListener
  {
    public UnityEvent OnDeleteClicked { get; } = new UnityEvent();
    public UnityEvent OnProductClicked { get; } = new UnityEvent();
    public UnityEvent OnMixerClicked { get; } = new UnityEvent();
  }

  [Serializable]
  public class MixingRoute
  {
    public ItemField Product { get; set; }
    public ItemField MixerItem { get; set; }

    public MixingRoute(MixingStationConfiguration config)
    {
      Product = new ItemField(config)
      {
        CanSelectNone = false,
        Options = ProductManager.FavouritedProducts?.Where(item => item != null).ToList<ItemDefinition>() ?? []
      };
      MixerItem = new ItemField(config)
      {
        CanSelectNone = false,
        Options = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients?.Where(item => item != null).ToList<ItemDefinition>() ?? []
      };
    }

    public void SetData(MixingRouteData data)
    {
      if (data.Product != null)
        Product.Load(data.Product);
      if (data.MixerItem != null)
        MixerItem.Load(data.MixerItem);
    }
  }

  [Serializable]
  public class MixingRouteData
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData()
    {
      Product = new ItemFieldData("");
      MixerItem = new ItemFieldData("");
    }
  }


  public class MixingRouteListFieldUI : MonoBehaviour
  {
    public string FieldText = "Recipes";
    public TextMeshProUGUI FieldLabel;
    public MixingRouteEntryUI[] RouteEntries;
    public Button AddButton;
    private List<StationRouteManager> _routeManagers;

    private void Start()
    {
      try
      {
        if (MixingRouteListTemplate == null)
        {
          InitializeStaticRouteListTemplate();
        }

        FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
        if (FieldLabel != null) FieldLabel.text = FieldText;
        AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
        if (AddButton == null)
        {
          Log(Level.Error,
              "MixingRouteListFieldUI.Start: AddButton not found",
              Category.MixingStation);
        }
        AddButton?.onClick.RemoveAllListeners();
        AddButton?.onClick.AddListener(AddClicked);
        Log(Level.Info,
            "MixingRouteListFieldUI.Start: Completed",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteListFieldUI.Start: Failed, error: {e}",
            Category.MixingStation);
      }
    }

    public void Bind(List<StationRouteManager> routeManagers)
    {
      try
      {
        _routeManagers = routeManagers ?? new List<StationRouteManager>();
        RouteEntries = transform.Find("Contents")?.GetComponentsInChildren<MixingRouteEntryUI>(true) ?? new MixingRouteEntryUI[0];
        var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
        Log(Level.Info,
            $"MixingRouteListFieldUI.Bind: Found {RouteEntries.Length} route entries for stations [{guids}]",
            Category.MixingStation);

        // Initialize route counts to match the maximum
        int maxRouteCount = _routeManagers.Any() ? _routeManagers.Max(m => m.Routes.Count) : 0;
        foreach (var manager in _routeManagers)
        {
          while (manager.Routes.Count < maxRouteCount)
          {
            manager.AddRoute();
          }
        }

        Refresh();
        Log(Level.Info, DebugListenerCount(), Category.MixingStation);
        Log(Level.Info,
            $"MixingRouteListFieldUI.Bind: Bound {_routeManagers.Count} route managers for stations [{guids}]",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteListFieldUI.Bind: Failed, error: {e}",
            Category.MixingStation);
      }
    }

    public void Refresh()
    {
      try
      {
        int maxRouteCount = _routeManagers.Any() ? _routeManagers.Max(m => m.Routes.Count) : 0;
        var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
        Log(Level.Info,
            $"MixingRouteListFieldUI.Refresh: Refreshing with {maxRouteCount} routes for stations [{guids}]",
            Category.MixingStation);

        for (int i = 0; i < RouteEntries.Length && i < maxRouteCount; i++)
        {
          var entry = RouteEntries[i];
          entry.gameObject.SetActive(true);
          var route = _routeManagers.FirstOrDefault(m => i < m.Routes.Count)?.Routes[i];
          var config = _routeManagers.FirstOrDefault(m => i < m.Routes.Count)?.Config;
          if (route != null && config != null)
          {
            entry.Bind(config, route, i);

            // Set listeners for this entry
            entry.OnDeleteClicked.RemoveAllListeners();
            entry.OnProductClicked.RemoveAllListeners();
            entry.OnMixerClicked.RemoveAllListeners();

            entry.OnDeleteClicked.AddListener(index =>
            {
              Log(Level.Info,
                              $"MixingRouteListFieldUI: Delete clicked for route {index} in stations [{guids}]",
                              Category.MixingStation);
              foreach (var manager in _routeManagers)
              {
                manager.RemoveRoute(index);
              }
              Refresh();
            });

            entry.OnProductClicked.AddListener(index =>
            {
              Log(Level.Info,
                              $"MixingRouteListFieldUI: Product clicked for route {index} in stations [{guids}]",
                              Category.MixingStation);
              var itemFields = _routeManagers.Select(m => m.Routes[index].Product).ToList();
              MixingRouteEntryUI.OpenItemSelectorScreen(index, _routeManagers.Select(m => m.Config).ToList(), itemFields, "Favorites");
            });

            entry.OnMixerClicked.AddListener(index =>
            {
              Log(Level.Info,
                              $"MixingRouteListFieldUI: Mixer clicked for route {index} in stations [{guids}]",
                              Category.MixingStation);
              var itemFields = _routeManagers.Select(m => m.Routes[index].MixerItem).ToList();
              MixingRouteEntryUI.OpenItemSelectorScreen(index, _routeManagers.Select(m => m.Config).ToList(), itemFields, "Mixers");
            });

            Log(Level.Info,
                $"MixingRouteListFieldUI.Refresh: Bound entry {i} for station {config.station.GUID}, " +
                $"listeners - Delete: {entry.OnDeleteClicked.GetListenerCount()}, " +
                $"Product: {entry.OnProductClicked.GetListenerCount()}, " +
                $"Mixer: {entry.OnMixerClicked.GetListenerCount()}",
                Category.MixingStation);
          }
          else
          {
            entry.gameObject.SetActive(false);
            Log(Level.Warning,
                $"MixingRouteListFieldUI.Refresh: No route or config for entry {i} in stations [{guids}]",
                Category.MixingStation);
          }
        }

        for (int i = maxRouteCount; i < RouteEntries.Length; i++)
        {
          RouteEntries[i].gameObject.SetActive(false);
        }

        AddButton?.gameObject.SetActive(_routeManagers.All(m => m.Routes.Count < StationRouteManager.MaxRoutes));
        Log(Level.Info, DebugListenerCount(), Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteListFieldUI.Refresh: Failed, error: {e}",
            Category.MixingStation);
      }
    }

    private void AddClicked()
    {
      try
      {
        var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
        Log(Level.Info,
            $"MixingRouteListFieldUI.AddClicked: Add button clicked for stations [{guids}]",
            Category.MixingStation);
        foreach (var manager in _routeManagers)
        {
          manager.AddRoute();
        }
        Refresh();
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteListFieldUI.AddClicked: Failed, error: {e}",
            Category.MixingStation);
      }
    }

    private string DebugListenerCount()
    {
      var sb = new System.Text.StringBuilder();
      var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
      for (int i = 0; i < RouteEntries.Length; i++)
      {
        var entry = RouteEntries[i];
        if (entry.gameObject.activeSelf)
        {
          sb.AppendLine($"MixingRouteListFieldUI: Entry {i} listeners for stations [{guids}] - " +
                        $"Delete: {entry.OnDeleteClicked.GetListenerCount()}, " +
                        $"Product: {entry.OnProductClicked.GetListenerCount()}, " +
                        $"Mixer: {entry.OnMixerClicked.GetListenerCount()}");
        }
      }
      return sb.ToString();
    }
  }

  public class MixingRouteEntryUI : MonoBehaviour
  {
    public TextMeshProUGUI ProductLabel;
    public TextMeshProUGUI MixerLabel;
    public Button ProductButton;
    public Button MixerButton;
    public Button RemoveButton;
    public UnityEvent<int> OnDeleteClicked { get; } = new UnityEvent<int>();
    public UnityEvent<int> OnProductClicked { get; } = new UnityEvent<int>();
    public UnityEvent<int> OnMixerClicked { get; } = new UnityEvent<int>();
    public MixingRoute Route;
    public MixingStationConfiguration Config;
    private int _index;

    private void Awake()
    {
      try
      {
        ProductLabel = transform.Find("ProductIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
        MixerLabel = transform.Find("MixerItemIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
        ProductButton = transform.Find("ProductIMGUI")?.GetComponent<Button>();
        MixerButton = transform.Find("MixerItemIMGUI")?.GetComponent<Button>();
        RemoveButton = transform.Find("Remove")?.GetComponent<Button>();

        if (ProductLabel == null) Log(Level.Warning,
            $"MixingRouteEntryUI.Awake: ProductLabel not found for {gameObject.name}",
            Category.MixingStation);
        if (MixerLabel == null) Log(Level.Warning,
            $"MixingRouteEntryUI.Awake: MixerLabel not found for {gameObject.name}",
            Category.MixingStation);
        if (ProductButton == null) Log(Level.Warning,
            $"MixingRouteEntryUI.Awake: ProductButton not found for {gameObject.name}",
            Category.MixingStation);
        if (MixerButton == null) Log(Level.Warning,
            $"MixingRouteEntryUI.Awake: MixerButton not found for {gameObject.name}",
            Category.MixingStation);
        if (RemoveButton == null) Log(Level.Warning,
            $"MixingRouteEntryUI.Awake: RemoveButton not found for {gameObject.name}",
            Category.MixingStation);

        Log(Level.Info,
            $"MixingRouteEntryUI.Awake: Completed for {gameObject.name}",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteEntryUI.Awake: Failed for {gameObject.name}, error: {e}",
            Category.MixingStation);
      }
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route, int index)
    {
      try
      {
        Log(Level.Info,
            $"MixingRouteEntryUI.Bind: Binding for {gameObject.name}, station: {config?.station.GUID}, route index: {index}, route: {route != null}",
            Category.MixingStation);
        Config = config;
        Route = route;
        _index = index;

        if (route == null || config == null)
        {
          ProductLabel.text = "Product";
          MixerLabel.text = "Mixer";
          Log(Level.Warning,
              $"MixingRouteEntryUI.Bind: Null route or config for {gameObject.name}, station: {config?.station.GUID}, index: {index}",
              Category.MixingStation);
          return;
        }

        ProductLabel.text = route.Product?.SelectedItem?.Name ?? "Product";
        MixerLabel.text = route.MixerItem?.SelectedItem?.Name ?? "Mixer";

        route.Product.onItemChanged.RemoveAllListeners();
        route.Product.onItemChanged.AddListener(item => ProductLabel.text = item?.Name ?? "Product");
        route.MixerItem.onItemChanged.RemoveAllListeners();
        route.MixerItem.onItemChanged.AddListener(item => MixerLabel.text = item?.Name ?? "Mixer");

        ProductButton?.onClick.RemoveAllListeners();
        ProductButton?.onClick.AddListener(() =>
        {
          Log(Level.Info,
                      $"MixingRouteEntryUI: ProductButton clicked for {gameObject.name}, station: {config.station.GUID}, index: {index}",
                      Category.MixingStation);
          OnProductClicked.Invoke(index);
        });

        MixerButton?.onClick.RemoveAllListeners();
        MixerButton?.onClick.AddListener(() =>
        {
          Log(Level.Info,
                      $"MixingRouteEntryUI: MixerButton clicked for {gameObject.name}, station: {config.station.GUID}, index: {index}",
                      Category.MixingStation);
          OnMixerClicked.Invoke(index);
        });

        RemoveButton?.onClick.RemoveAllListeners();
        RemoveButton?.onClick.AddListener(() =>
        {
          Log(Level.Info,
                      $"MixingRouteEntryUI: RemoveButton clicked for {gameObject.name}, station: {config.station.GUID}, index: {index}",
                      Category.MixingStation);
          OnDeleteClicked.Invoke(index);
        });

        Log(Level.Info,
            $"MixingRouteEntryUI.Bind: Completed for {gameObject.name}, station: {config.station.GUID}, index: {index}, " +
            $"listeners - Delete: {OnDeleteClicked.GetListenerCount()}, " +
            $"Product: {OnProductClicked.GetListenerCount()}, " +
            $"Mixer: {OnMixerClicked.GetListenerCount()}",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteEntryUI.Bind: Failed for {gameObject.name}, station: {config?.station.GUID}, index: {index}, error: {e}",
            Category.MixingStation);
      }
    }

    public static void OpenItemSelectorScreen(int index, List<MixingStationConfiguration> configs, List<ItemField> itemFields, string fieldName)
    {
      try
      {
        var guids = string.Join(", ", configs.Select(c => c.station.GUID.ToString()));
        Log(Level.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Called for index {index}, field: {fieldName}, stations: [{guids}], itemFields: {itemFields.Count}",
            Category.MixingStation);

        if (!itemFields.Any())
        {
          Log(Level.Warning,
              $"MixingRouteEntryUI.OpenItemSelectorScreen: No itemFields provided for stations [{guids}]",
              Category.MixingStation);
          return;
        }

        var itemSelectorScreen = Singleton<ManagementInterface>.Instance?.ItemSelectorScreen;
        if (itemSelectorScreen == null)
        {
          Log(Level.Error,
              $"MixingRouteEntryUI.OpenItemSelectorScreen: ItemSelectorScreen is null for stations [{guids}]",
              Category.MixingStation);
          return;
        }

        var options = itemFields[0].Options?.Select(opt => new ItemSelector.Option(opt.Name, opt)).ToList() ?? new List<ItemSelector.Option>();
        if (itemFields[0].CanSelectNone)
        {
          options.Insert(0, new ItemSelector.Option("None", null));
        }

        Log(Level.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Options count: {options.Count} for stations [{guids}]",
            Category.MixingStation);

        var selectedOption = options.FirstOrDefault(opt => opt.Item == itemFields[0].SelectedItem);
        Log(Level.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Selected option: {(selectedOption != null ? selectedOption.Item.Name : "none")} for stations [{guids}]",
            Category.MixingStation);

        itemSelectorScreen.Initialize(fieldName, options, selectedOption, selected =>
        {
          Log(Level.Info,
                      $"MixingRouteEntryUI.OpenItemSelectorScreen: Item selected: {(selected?.Item?.Name ?? "none")} for stations [{guids}]",
                      Category.MixingStation);
          foreach (var (field, config) in itemFields.Zip(configs, (f, c) => (f, c)))
          {
            field.SelectedItem = selected.Item;
            field.onItemChanged?.Invoke(selected.Item);
            if (selected.Item?.GetDefaultInstance() is ProductItemInstance prodItem)
            {
              var manager = RouteManagers[config.station.GUID];
              prodItem.SetQuality(manager.Quality.Value);
              manager.Refills[index] = new ItemKey(prodItem);
              Log(Level.Info,
                          $"MixingRouteEntryUI.OpenItemSelectorScreen: Updated Refills[{index}] for station {config.station.GUID}",
                          Category.MixingStation);
            }
          }
        });

        itemSelectorScreen.Open();
        Log(Level.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: ItemSelectorScreen opened for stations [{guids}]",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Failed for index {index}, field: {fieldName}, error: {e}",
            Category.MixingStation);
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration))]
  public class MixingStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation) })]
    static void ConstructorPostfix(MixingStationConfiguration __instance, MixingStation station)
    {
      try
      {
        if (__instance == null || station == null)
        {
          Log(Level.Error, $"MixingStationConfigurationPatch.ConstructorPostfix: __instance or station is null", Category.MixingStation);
          return;
        }

        var guid = station.GUID;
        __instance.StartThrehold.MaxValue = StationRouteManager.MaxThreshold;
        new MixingStationAdapter(station);
        RouteManagers[guid] = new StationRouteManager(__instance, station);
        Log(Level.Info, $"MixingStationConfigurationPatch: Initialized StationRouteManager for station {guid}", Category.MixingStation);

      }
      catch (Exception e)
      {
        Log(Level.Error, $"MixingStationConfigurationPatch.ConstructorPostfix: Failed for station {station?.GUID}, error: {e}", Category.MixingStation);
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("ShouldSave")]
    static bool ShouldSavePrefix(MixingStationConfiguration __instance, ref bool __result)
    {
      __result = true;
      return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(MixingStationConfiguration __instance, ref string __result)
    {
      try
      {
        if (__instance?.station == null)
        {
          Log(Level.Error, $"MixingStationConfigurationPatch.GetSaveStringPostfix: __instance or station is null", Category.MixingStation);
          return;
        }

        var guid = __instance.station.GUID;
        var manager = RouteManagers.GetValueOrDefault(guid);
        if (manager == null)
        {
          Log(Level.Warning, $"MixingStationConfigurationPatch.GetSaveStringPostfix: No StationRouteManager for station {guid}", Category.MixingStation);
          return;
        }

        var json = JObject.Parse(__result);
        Log(Level.Verbose, $"MixingStationConfigurationPatch.GetSaveStringPostfix: {guid} before:\n{__result}", Category.MixingStation);
        json.Merge(manager.Serialize());
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        Log(Level.Verbose, $"MixingStationConfigurationPatch.GetSaveStringPostfix: {guid} after:\n{__result}", Category.MixingStation);
        Log(Level.Info, $"MixingStationConfigurationPatch.GetSaveStringPostfix: Saved for station {guid}", Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"MixingStationConfigurationPatch.GetSaveStringPostfix: Failed for station {__instance?.station?.GUID}, error: {e}", Category.MixingStation);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(MixingStationConfiguration __instance)
    {
      try
      {
        if (__instance?.station == null)
        {
          Log(Level.Warning, $"MixingStationConfigurationPatch.DestroyPostfix: __instance or station is null", Category.MixingStation);
          return;
        }

        RouteManagers.Remove(__instance.station.GUID);
        Log(Level.Info, $"MixingStationConfigurationPatch.DestroyPostfix: Removed StationRouteManager for station {__instance.station.GUID}", Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"MixingStationConfigurationPatch.DestroyPostfix: Failed for station {__instance?.station?.GUID}, error: {e}", Category.MixingStation);
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfigPanel))]
  public class MixingStationConfigPanelPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (__instance == null || __instance.DestinationUI == null)
        {
          Log(Level.Error,
              "MixingStationConfigPanelPatch.BindPostfix: __instance or DestinationUI is null",
              Category.MixingStation);
          return;
        }

        if (MixingRouteListTemplate == null)
        {
          InitializeStaticRouteListTemplate();
        }

        __instance.DestinationUI.gameObject.SetActive(false);
        var routeListUIObj = Object.Instantiate(MixingRouteListTemplate, __instance.transform, false);
        routeListUIObj.name = "RouteListFieldUI";
        routeListUIObj.SetActive(true);
        var routeListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();

        var sliderObj = __instance.transform.Find("NumberFieldUI")?.gameObject;
        if (sliderObj != null)
        {
          sliderObj.transform.Find("Description")?.gameObject.SetActive(false);
        }

        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        var qualityFieldUIObj = dryingRackPanelObj?.transform.Find("QualityFieldUI")?.gameObject;
        if (qualityFieldUIObj == null)
        {
          Log(Level.Error,
              "MixingStationConfigPanelPatch.BindPostfix: QualityFieldUI not found in DryingRackPanel",
              Category.MixingStation);
          return;
        }

        qualityFieldUIObj.transform.Find("Description")?.gameObject.SetActive(false);
        var qualityUIObj = Object.Instantiate(qualityFieldUIObj, __instance.transform, false);
        qualityUIObj.SetActive(true);

        var routeManagers = configs
            .OfType<MixingStationConfiguration>()
            .Select(config =>
            {
              var guid = config.station.GUID;
              if (!RouteManagers.ContainsKey(guid))
              {
                RouteManagers[guid] = new StationRouteManager(config, config.station);
              }
              return RouteManagers[guid];
            })
            .ToList();

        routeListUI.Bind(routeManagers);
        qualityUIObj.GetComponent<QualityFieldUI>().Bind(routeManagers.Select(m => m.Quality).ToList());
        qualityUIObj.transform.Find("Title").GetComponent<TextMeshProUGUI>().text = "Min Quality";

        var routeListRect = routeListUIObj.GetComponent<RectTransform>();
        routeListRect.anchoredPosition = new Vector2(routeListRect.anchoredPosition.x, -165.76f);
        var qualityRect = qualityUIObj.GetComponent<RectTransform>();
        qualityRect.anchoredPosition = new Vector2(routeListRect.anchoredPosition.x, routeListRect.anchoredPosition.y + 70f);

        var guids = string.Join(", ", routeManagers.Select(m => m.StationGuid.ToString()));
        Log(Level.Info,
            $"MixingStationConfigPanelPatch.BindPostfix: Bound {routeManagers.Count} route managers for stations [{guids}]",
            Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"MixingStationConfigPanelPatch.BindPostfix: Failed, error: {e}",
            Category.MixingStation);
      }
    }
  }


  [HarmonyPatch(typeof(MixingStationLoader))]
  public static class MixingStationLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(MixingStationLoader __instance, string mainPath)
    {
      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || !(gridItem is MixingStation station))
        {
          Log(Level.Warning, $"MixingStationLoaderPatch.LoadPostfix: Invalid or missing station for mainPath: {mainPath}", Category.MixingStation);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          Log(Level.Warning, $"MixingStationLoaderPatch.LoadPostfix: Configuration.json missing at {configPath}", Category.MixingStation);
          return;
        }

        if (!__instance.TryLoadFile(mainPath, "Configuration", out string text))
        {
          Log(Level.Warning, $"MixingStationLoaderPatch.LoadPostfix: Failed to load Configuration.json for mainPath: {mainPath}", Category.MixingStation);
          return;
        }

        var config = station.stationConfiguration;
        if (config == null)
        {
          Log(Level.Error, $"MixingStationLoaderPatch.LoadPostfix: No valid MixingStationConfiguration for station: {station.GUID}", Category.MixingStation);
          return;
        }

        // Clear hidden destination
        if (config.Destination.SelectedObject != null)
        {
          config.Destination.SelectedObject = null;
          config.DestinationRoute = null;
        }

        var manager = new StationRouteManager(config, station);
        RouteManagers[station.GUID] = manager;
        manager.Deserialize(JObject.Parse(text));
        Log(Level.Info, $"MixingStationLoaderPatch.LoadPostfix: Loaded for station {station.GUID} with {manager.Routes.Count} routes", Category.MixingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"MixingStationLoaderPatch.LoadPostfix: Failed for mainPath: {mainPath}, error: {e}", Category.MixingStation);
      }
    }
  }
}