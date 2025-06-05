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
using static NoLazyWorkers.Storage.Utilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.Property;
using ScheduleOne.Persistence.Datas;

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

      public MixingStationAdapter(MixingStation station)
      {
        if (!Extensions.IStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          Extensions.IStations[station.ParentProperty] = new();
          propertyStations = Extensions.IStations[station.ParentProperty];
        }
        propertyStations.Add(GUID, this);
        _stationState = new StationState<MixingStationStates>
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
      public List<ItemInstance> RefillList() => _routeManager.Refills.Where(item => item != null).ToList();
      public bool CanRefill(ItemInstance item) => item.CanRefill(_routeManager);
      public IStationState StationState
      {
        get => _stationState;
        set => _stationState = (StationState<MixingStationStates>)value;
      }
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
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"GetInputItemForProductSlot: Invalid station adapter, GUID={station?.GUID}",
              DebugLogger.Category.MixingStation);
          return null;
        }

        var productInSlot = adapter.Station.ProductSlot.ItemInstance?.Definition as ProductDefinition;
        if (productInSlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"GetInputItemForProductSlot: Product slot item is not a ProductDefinition for station={adapter.GUID}",
              DebugLogger.Category.MixingStation);
          return null;
        }

        var routeManager = RouteManagers.GetValueOrDefault(adapter.GUID);
        if (routeManager == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"GetInputItemForProductSlot: No StationRouteManager for station={adapter.GUID}",
              DebugLogger.Category.MixingStation);
          return null;
        }

        var matchingRoute = routeManager.Routes.FirstOrDefault(route => route.Product?.SelectedItem == productInSlot);
        if (matchingRoute == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"GetInputItemForProductSlot: No route matches product={productInSlot.Name} for station={adapter.GUID}",
              DebugLogger.Category.MixingStation);
          return null;
        }

        return matchingRoute.MixerItem;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetInputItemForProductSlot: Failed for station {station?.GUID}, error: {e}",
            DebugLogger.Category.MixingStation);
        return null;
      }
    }

    public static bool CanRefill(this ItemInstance item, StationRouteManager manager)
    {
      foreach (var instance in manager.Refills)
      {
        if (item.AdvCanStackWith(instance, allowHigherQuality: true))
          return true;
      }
      return false;
    }
  }

  public static class MixingStationConfigUtilities
  {
    public static void RestoreConfigurations()
    {
      try
      {
        MixingStation[] mixingStations = Object.FindObjectsOfType<MixingStation>();
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"RestoreConfigurations: Found {mixingStations.Length} MixingStations",
            DebugLogger.Category.MixingStation);

        foreach (var station in mixingStations)
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                "RestoreConfigurations: Encountered null station",
                DebugLogger.Category.MixingStation);
            continue;
          }

          if (station.stationConfiguration is MixingStationConfiguration config)
          {
            if (!RouteManagers.ContainsKey(station.GUID))
            {
              RouteManagers[station.GUID] = new StationRouteManager(config, station);
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"RestoreConfigurations: Initialized StationRouteManager for station {station.GUID}",
                  DebugLogger.Category.MixingStation);
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"RestoreConfigurations: Invalid or missing configuration for station {station.GUID}",
                DebugLogger.Category.MixingStation);
          }
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"RestoreConfigurations: Failed, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    public static void InitializeStaticRouteListTemplate()
    {
      var routeListTemplate = GetTransformTemplateFromConfigPanel(EConfigurableType.Packager, "RouteListFieldUI");
      if (routeListTemplate == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            "InitializeStaticRouteListTemplate: routeListTemplate is null",
            DebugLogger.Category.MixingStation);
        return;
      }

      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "InitializeStaticRouteListTemplate: Initializing MixingRouteListTemplate",
            DebugLogger.Category.MixingStation);
        var templateObj = Object.Instantiate(routeListTemplate.gameObject);
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);

        try
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "InitializeStaticRouteListTemplate: Adding MixingRouteListFieldUI",
              DebugLogger.Category.MixingStation);
          var routeListFieldUI = templateObj.AddComponent<MixingRouteListFieldUI>();
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"InitializeStaticRouteListTemplate: Failed to add MixingRouteListFieldUI, error: {e}",
              DebugLogger.Category.MixingStation);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "InitializeStaticRouteListTemplate: MixingRouteListFieldUI added",
            DebugLogger.Category.MixingStation);
        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
          Object.Destroy(defaultScript);

        if (templateObj.GetComponent<MixingRouteListFieldUI>() == null)
          templateObj.AddComponent<MixingRouteListFieldUI>();

        var contentsTransform = templateObj.transform.Find("Contents");
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeStaticRouteListTemplate: contentsTransform: {contentsTransform?.name}",
            DebugLogger.Category.MixingStation);
        var entryTransform = templateObj.transform.Find("Contents/Entry");
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeStaticRouteListTemplate: entryTransform: {entryTransform?.name}",
            DebugLogger.Category.MixingStation);

        if (entryTransform != null)
        {
          var productTransform = entryTransform.Find("Source");
          var mixerTransform = entryTransform.Find("Destination");
          var removeTransform = entryTransform.Find("Remove");
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"InitializeStaticRouteListTemplate: Entry hierarchy - Source: {productTransform?.name}, Destination: {mixerTransform?.name}, Remove: {removeTransform?.name}",
              DebugLogger.Category.MixingStation);
        }

        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeStaticRouteListTemplate: addNewLabel?.text: {addNewLabel?.text}",
            DebugLogger.Category.MixingStation);

        for (int i = 0; i <= StationRouteManager.MaxRoutes; i++)
        {
          Transform entry = contentsTransform.Find($"Entry ({i})") ?? (i == 0 ? contentsTransform.Find("Entry") : null);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"InitializeStaticRouteListTemplate: Processing Entry ({i})",
              DebugLogger.Category.MixingStation);

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
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "InitializeStaticRouteListTemplate: SetAsLastSibling",
            DebugLogger.Category.MixingStation);
        templateObj.transform.Find("Title")?.gameObject.SetActive(false);
        templateObj.transform.Find("From")?.gameObject.SetActive(false);
        templateObj.transform.Find("To")?.gameObject.SetActive(false);
        MixingRouteListTemplate = templateObj;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "InitializeStaticRouteListTemplate: Static MixingRouteListTemplate initialized successfully",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"InitializeStaticRouteListTemplate: Failed to initialize MixingRouteListTemplate, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }
  }

  public class StationRouteManager
  {
    public Guid StationGuid { get; }
    public MixingStationConfiguration Config { get; }
    public QualityField Quality { get; private set; }
    public List<MixingRoute> Routes { get; } = new List<MixingRoute>();
    public List<ItemInstance> Refills { get; } = new List<ItemInstance>();
    public const int MaxRoutes = 11;
    public const float MaxThreshold = 20f;

    public StationRouteManager(MixingStationConfiguration config, MixingStation station)
    {
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "StationRouteManager: Constructor failed, station is null", DebugLogger.Category.MixingStation);
        throw new ArgumentNullException(nameof(station));
      }
      StationGuid = station.GUID;
      Config = config;
      if (Config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StationRouteManager: Constructor failed for station {station.GUID}, Configuration is null or not MixingStationConfiguration",
            DebugLogger.Category.MixingStation);
        throw new InvalidOperationException("MixingStation Configuration is null or invalid");
      }
      Quality = new QualityField(Config);
      Quality.onValueChanged.AddListener(_ =>
      {
        UpdateRefillsQuality();
        Config.InvokeChanged();
      });
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StationRouteManager: Initialized for station {StationGuid}", DebugLogger.Category.MixingStation);
    }

    public void AddRoute()
    {
      if (Routes.Count >= MaxRoutes) return;
      var route = new MixingRoute(Config);
      Routes.Add(route);
      Refills.Add(null);
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Added route for station {StationGuid}, total routes: {Routes.Count}",
          DebugLogger.Category.MixingStation);
    }

    public void RemoveRoute(int index)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes.RemoveAt(index);
      Refills.RemoveAt(index);
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Removed route at index {index} for station {StationGuid}, total routes: {Routes.Count}",
          DebugLogger.Category.MixingStation);
    }

    public void UpdateProduct(int index, ItemDefinition product)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes[index].Product.SelectedItem = product;
      UpdateRefill(index);
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Updated product for route {index} in station {StationGuid}",
          DebugLogger.Category.MixingStation);
    }

    public void UpdateMixer(int index, ItemDefinition mixer)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes[index].MixerItem.SelectedItem = mixer;
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Updated mixer for route {index} in station {StationGuid}",
          DebugLogger.Category.MixingStation);
    }

    private void UpdateRefill(int index)
    {
      if (index < 0 || index >= Routes.Count) return;
      var product = Routes[index].Product.SelectedItem;
      if (product == null)
      {
        Refills[index] = null;
        return;
      }
      var prodItem = product.GetDefaultInstance() as ProductItemInstance;
      prodItem?.SetQuality(Quality.Value);
      Refills[index] = prodItem;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Updated refill for route {index} in station {StationGuid}",
          DebugLogger.Category.MixingStation);
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
          Refills.Add(null);
          UpdateRefill(Routes.Count - 1);
        }
      }
      if (json["Quality"]?.ToString() is string qualityStr && Enum.TryParse<EQuality>(qualityStr, out var quality))
      {
        Quality.SetValue(quality, false);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Deserialized {Routes.Count} routes for station {StationGuid}",
          DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "MixingRouteListFieldUI.Start: AddButton not found",
              DebugLogger.Category.MixingStation);
        }
        AddButton?.onClick.RemoveAllListeners();
        AddButton?.onClick.AddListener(AddClicked);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "MixingRouteListFieldUI.Start: Completed",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteListFieldUI.Start: Failed, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    public void Bind(List<StationRouteManager> routeManagers)
    {
      try
      {
        _routeManagers = routeManagers ?? new List<StationRouteManager>();
        RouteEntries = transform.Find("Contents")?.GetComponentsInChildren<MixingRouteEntryUI>(true) ?? new MixingRouteEntryUI[0];
        var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteListFieldUI.Bind: Found {RouteEntries.Length} route entries for stations [{guids}]",
            DebugLogger.Category.MixingStation);

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
        DebugLogger.Log(DebugLogger.LogLevel.Info, DebugListenerCount(), DebugLogger.Category.MixingStation);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteListFieldUI.Bind: Bound {_routeManagers.Count} route managers for stations [{guids}]",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteListFieldUI.Bind: Failed, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    public void Refresh()
    {
      try
      {
        int maxRouteCount = _routeManagers.Any() ? _routeManagers.Max(m => m.Routes.Count) : 0;
        var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteListFieldUI.Refresh: Refreshing with {maxRouteCount} routes for stations [{guids}]",
            DebugLogger.Category.MixingStation);

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
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                              $"MixingRouteListFieldUI: Delete clicked for route {index} in stations [{guids}]",
                              DebugLogger.Category.MixingStation);
              foreach (var manager in _routeManagers)
              {
                manager.RemoveRoute(index);
              }
              Refresh();
            });

            entry.OnProductClicked.AddListener(index =>
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                              $"MixingRouteListFieldUI: Product clicked for route {index} in stations [{guids}]",
                              DebugLogger.Category.MixingStation);
              var itemFields = _routeManagers.Select(m => m.Routes[index].Product).ToList();
              MixingRouteEntryUI.OpenItemSelectorScreen(index, _routeManagers.Select(m => m.Config).ToList(), itemFields, "Favorites");
            });

            entry.OnMixerClicked.AddListener(index =>
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                              $"MixingRouteListFieldUI: Mixer clicked for route {index} in stations [{guids}]",
                              DebugLogger.Category.MixingStation);
              var itemFields = _routeManagers.Select(m => m.Routes[index].MixerItem).ToList();
              MixingRouteEntryUI.OpenItemSelectorScreen(index, _routeManagers.Select(m => m.Config).ToList(), itemFields, "Mixers");
            });

            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MixingRouteListFieldUI.Refresh: Bound entry {i} for station {config.station.GUID}, " +
                $"listeners - Delete: {entry.OnDeleteClicked.GetListenerCount()}, " +
                $"Product: {entry.OnProductClicked.GetListenerCount()}, " +
                $"Mixer: {entry.OnMixerClicked.GetListenerCount()}",
                DebugLogger.Category.MixingStation);
          }
          else
          {
            entry.gameObject.SetActive(false);
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"MixingRouteListFieldUI.Refresh: No route or config for entry {i} in stations [{guids}]",
                DebugLogger.Category.MixingStation);
          }
        }

        for (int i = maxRouteCount; i < RouteEntries.Length; i++)
        {
          RouteEntries[i].gameObject.SetActive(false);
        }

        AddButton?.gameObject.SetActive(_routeManagers.All(m => m.Routes.Count < StationRouteManager.MaxRoutes));
        DebugLogger.Log(DebugLogger.LogLevel.Info, DebugListenerCount(), DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteListFieldUI.Refresh: Failed, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    private void AddClicked()
    {
      try
      {
        var guids = string.Join(", ", _routeManagers.Select(m => m.StationGuid.ToString()));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteListFieldUI.AddClicked: Add button clicked for stations [{guids}]",
            DebugLogger.Category.MixingStation);
        foreach (var manager in _routeManagers)
        {
          manager.AddRoute();
        }
        Refresh();
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteListFieldUI.AddClicked: Failed, error: {e}",
            DebugLogger.Category.MixingStation);
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

        if (ProductLabel == null) DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteEntryUI.Awake: ProductLabel not found for {gameObject.name}",
            DebugLogger.Category.MixingStation);
        if (MixerLabel == null) DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteEntryUI.Awake: MixerLabel not found for {gameObject.name}",
            DebugLogger.Category.MixingStation);
        if (ProductButton == null) DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteEntryUI.Awake: ProductButton not found for {gameObject.name}",
            DebugLogger.Category.MixingStation);
        if (MixerButton == null) DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteEntryUI.Awake: MixerButton not found for {gameObject.name}",
            DebugLogger.Category.MixingStation);
        if (RemoveButton == null) DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteEntryUI.Awake: RemoveButton not found for {gameObject.name}",
            DebugLogger.Category.MixingStation);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.Awake: Completed for {gameObject.name}",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteEntryUI.Awake: Failed for {gameObject.name}, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route, int index)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.Bind: Binding for {gameObject.name}, station: {config?.station.GUID}, route index: {index}, route: {route != null}",
            DebugLogger.Category.MixingStation);
        Config = config;
        Route = route;
        _index = index;

        if (route == null || config == null)
        {
          ProductLabel.text = "Product";
          MixerLabel.text = "Mixer";
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MixingRouteEntryUI.Bind: Null route or config for {gameObject.name}, station: {config?.station.GUID}, index: {index}",
              DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"MixingRouteEntryUI: ProductButton clicked for {gameObject.name}, station: {config.station.GUID}, index: {index}",
                      DebugLogger.Category.MixingStation);
          OnProductClicked.Invoke(index);
        });

        MixerButton?.onClick.RemoveAllListeners();
        MixerButton?.onClick.AddListener(() =>
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"MixingRouteEntryUI: MixerButton clicked for {gameObject.name}, station: {config.station.GUID}, index: {index}",
                      DebugLogger.Category.MixingStation);
          OnMixerClicked.Invoke(index);
        });

        RemoveButton?.onClick.RemoveAllListeners();
        RemoveButton?.onClick.AddListener(() =>
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"MixingRouteEntryUI: RemoveButton clicked for {gameObject.name}, station: {config.station.GUID}, index: {index}",
                      DebugLogger.Category.MixingStation);
          OnDeleteClicked.Invoke(index);
        });

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.Bind: Completed for {gameObject.name}, station: {config.station.GUID}, index: {index}, " +
            $"listeners - Delete: {OnDeleteClicked.GetListenerCount()}, " +
            $"Product: {OnProductClicked.GetListenerCount()}, " +
            $"Mixer: {OnMixerClicked.GetListenerCount()}",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteEntryUI.Bind: Failed for {gameObject.name}, station: {config?.station.GUID}, index: {index}, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    public static void OpenItemSelectorScreen(int index, List<MixingStationConfiguration> configs, List<ItemField> itemFields, string fieldName)
    {
      try
      {
        var guids = string.Join(", ", configs.Select(c => c.station.GUID.ToString()));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Called for index {index}, field: {fieldName}, stations: [{guids}], itemFields: {itemFields.Count}",
            DebugLogger.Category.MixingStation);

        if (!itemFields.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MixingRouteEntryUI.OpenItemSelectorScreen: No itemFields provided for stations [{guids}]",
              DebugLogger.Category.MixingStation);
          return;
        }

        var itemSelectorScreen = Singleton<ManagementInterface>.Instance?.ItemSelectorScreen;
        if (itemSelectorScreen == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"MixingRouteEntryUI.OpenItemSelectorScreen: ItemSelectorScreen is null for stations [{guids}]",
              DebugLogger.Category.MixingStation);
          return;
        }

        var options = itemFields[0].Options?.Select(opt => new ItemSelector.Option(opt.Name, opt)).ToList() ?? new List<ItemSelector.Option>();
        if (itemFields[0].CanSelectNone)
        {
          options.Insert(0, new ItemSelector.Option("None", null));
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Options count: {options.Count} for stations [{guids}]",
            DebugLogger.Category.MixingStation);

        var selectedOption = options.FirstOrDefault(opt => opt.Item == itemFields[0].SelectedItem);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Selected option: {(selectedOption != null ? selectedOption.Item.Name : "none")} for stations [{guids}]",
            DebugLogger.Category.MixingStation);

        itemSelectorScreen.Initialize(fieldName, options, selectedOption, selected =>
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"MixingRouteEntryUI.OpenItemSelectorScreen: Item selected: {(selected?.Item?.Name ?? "none")} for stations [{guids}]",
                      DebugLogger.Category.MixingStation);
          foreach (var (field, config) in itemFields.Zip(configs, (f, c) => (f, c)))
          {
            field.SelectedItem = selected.Item;
            field.onItemChanged?.Invoke(selected.Item);
            if (selected.Item?.GetDefaultInstance() is ProductItemInstance prodItem)
            {
              var manager = RouteManagers[config.station.GUID];
              prodItem.SetQuality(manager.Quality.Value);
              manager.Refills[index] = prodItem;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                          $"MixingRouteEntryUI.OpenItemSelectorScreen: Updated Refills[{index}] for station {config.station.GUID}",
                          DebugLogger.Category.MixingStation);
            }
          }
        });

        itemSelectorScreen.Open();
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: ItemSelectorScreen opened for stations [{guids}]",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingRouteEntryUI.OpenItemSelectorScreen: Failed for index {index}, field: {fieldName}, error: {e}",
            DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationConfigurationPatch.ConstructorPostfix: __instance or station is null", DebugLogger.Category.MixingStation);
          return;
        }

        var guid = station.GUID;
        __instance.StartThrehold.MaxValue = StationRouteManager.MaxThreshold;
        RouteManagers[guid] = new StationRouteManager(__instance, station);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationConfigurationPatch: Initialized StationRouteManager for station {guid}", DebugLogger.Category.MixingStation);

      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationConfigurationPatch.ConstructorPostfix: Failed for station {station?.GUID}, error: {e}", DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationConfigurationPatch.GetSaveStringPostfix: __instance or station is null", DebugLogger.Category.MixingStation);
          return;
        }

        var guid = __instance.station.GUID;
        var manager = RouteManagers.GetValueOrDefault(guid);
        if (manager == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationConfigurationPatch.GetSaveStringPostfix: No StationRouteManager for station {guid}", DebugLogger.Category.MixingStation);
          return;
        }

        var json = JObject.Parse(__result);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationConfigurationPatch.GetSaveStringPostfix: {guid} before:\n{__result}", DebugLogger.Category.MixingStation);
        json.Merge(manager.Serialize());
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationConfigurationPatch.GetSaveStringPostfix: {guid} after:\n{__result}", DebugLogger.Category.MixingStation);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationConfigurationPatch.GetSaveStringPostfix: Saved for station {guid}", DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationConfigurationPatch.GetSaveStringPostfix: Failed for station {__instance?.station?.GUID}, error: {e}", DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationConfigurationPatch.DestroyPostfix: __instance or station is null", DebugLogger.Category.MixingStation);
          return;
        }

        RouteManagers.Remove(__instance.station.GUID);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationConfigurationPatch.DestroyPostfix: Removed StationRouteManager for station {__instance.station.GUID}", DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationConfigurationPatch.DestroyPostfix: Failed for station {__instance?.station?.GUID}, error: {e}", DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "MixingStationConfigPanelPatch.BindPostfix: __instance or DestinationUI is null",
              DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "MixingStationConfigPanelPatch.BindPostfix: QualityFieldUI not found in DryingRackPanel",
              DebugLogger.Category.MixingStation);
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
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigPanelPatch.BindPostfix: Bound {routeManagers.Count} route managers for stations [{guids}]",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationConfigPanelPatch.BindPostfix: Failed, error: {e}",
            DebugLogger.Category.MixingStation);
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
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationLoaderPatch.LoadPostfix: Invalid or missing station for mainPath: {mainPath}", DebugLogger.Category.MixingStation);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationLoaderPatch.LoadPostfix: Configuration.json missing at {configPath}", DebugLogger.Category.MixingStation);
          return;
        }

        if (!__instance.TryLoadFile(mainPath, "Configuration", out string text))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationLoaderPatch.LoadPostfix: Failed to load Configuration.json for mainPath: {mainPath}", DebugLogger.Category.MixingStation);
          return;
        }

        var config = station.stationConfiguration;
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationLoaderPatch.LoadPostfix: No valid MixingStationConfiguration for station: {station.GUID}", DebugLogger.Category.MixingStation);
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
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationLoaderPatch.LoadPostfix: Loaded for station {station.GUID} with {manager.Routes.Count} routes", DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationLoaderPatch.LoadPostfix: Failed for mainPath: {mainPath}, error: {e}", DebugLogger.Category.MixingStation);
      }
    }
  }
}