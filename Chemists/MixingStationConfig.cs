using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.General.GeneralExtensions;
using ScheduleOne.NPCs.Behaviour;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.NPCs;

namespace NoLazyWorkers.Chemists
{
  public static class MixingStationExtensions
  {
    public static Dictionary<Guid, MixingStationConfiguration> Config = [];
    public static Dictionary<Guid, List<MixingRoute>> MixingRoutes = [];
    public static GameObject MixingRouteListTemplate { get; set; }
    public static Dictionary<Guid, QualityField> QualityFields = [];

    public class MixingStationAdapter : IStationAdapter<MixingStation>
    {
      private readonly MixingStation _station;

      public MixingStationAdapter(MixingStation station)
      {
        _station = station;
      }
      public MixingStation Station => _station;
      public MixingStationConfiguration Config => _station.stationConfiguration;

      public Guid GUID => _station.GUID;
      public ItemSlot InsertSlot => _station.MixerSlot;
      public List<ItemSlot> ProductSlots => [_station.ProductSlot];
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => _station.IsOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
      public bool HasActiveOperation => _station.CurrentMixOperation != null;
      public int StartThreshold => (int)(_station.Configuration as MixingStationConfiguration).StartThrehold.Value;
      public int MaxProductQuantity => 20;
      public ITransitEntity TransitEntity => _station as ITransitEntity;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemField> GetInputItemForProduct() => [MixingStationUtilities.GetInputItemForProductSlot(this)];
      public int GetInputQuantity() => _station.MixerSlot?.Quantity ?? 0;
      public void StartOperation(Behaviour behaviour)
      {
        (behaviour as StartMixingStationBehaviour).StartCook();
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationAdapter.StartOperation: Started cook for station {_station.GUID}",
            DebugLogger.Category.MixingStation);
      }
    }

    public static void RestoreConfigurations()
    {
      MixingStation[] mixingStations = Object.FindObjectsOfType<MixingStation>();
      foreach (MixingStation station in mixingStations)
      {
        try
        {
          if (station.Configuration is MixingStationConfiguration mixerConfig)
          {
            Guid guid = station.GUID;
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"RestoreConfigurations: Started for station: {guid}",
                DebugLogger.Category.MixingStation);

            Config[guid] = mixerConfig;
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"RestoreConfigurations: Registered missing MixerConfig for station: {guid}",
                DebugLogger.Category.MixingStation);

            if (!MixingRoutes.ContainsKey(guid))
            {
              MixingRoutes[guid] = [];
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"RestoreConfigurations: Initialized MixingRoutes for station: {guid}",
                  DebugLogger.Category.MixingStation);
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"RestoreConfigurations: Skipped station {station?.GUID}, no MixingStationConfiguration",
                DebugLogger.Category.MixingStation);
          }
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"RestoreConfigurations: Failed for station: {station?.GUID.ToString() ?? "null"}, error: {e}",
              DebugLogger.Category.MixingStation);
        }
      }
    }

    public static void InitializeStaticRouteListTemplate()
    {
      var routeListTemplate = GetTransformTemplateFromConfigPanel(
          EConfigurableType.Packager,
          "RouteListFieldUI");
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

        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeStaticRouteListTemplate: addNewLabel?.text: {addNewLabel?.text}",
            DebugLogger.Category.MixingStation);

        for (int i = 0; i < MixingRouteListFieldUI.MaxRoutes; i++)
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

  [Serializable]
  public class MixingRouteData
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData()
    {
      Product = new("");
      MixerItem = new("");
    }
  }

  public class MixingRouteListFieldUI : MonoBehaviour
  {
    public string FieldText = "Recipes";
    public TextMeshProUGUI FieldLabel;
    public MixingRouteEntryUI[] RouteEntries;
    public RectTransform MultiEditBlocker;
    public Button AddButton;
    public static readonly int MaxRoutes = 12;
    private List<List<MixingRoute>> RoutesLists;
    private List<MixingStationConfiguration> Configs;
    private UnityAction OnChanged;

    private void Start()
    {
      if (MixingStationExtensions.MixingRouteListTemplate != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "MixingStationConfigPanelBindPatch: MixingRouteListTemplate already initialized, skipping",
            DebugLogger.Category.MixingStation);
      }
      else
      {
        MixingStationExtensions.InitializeStaticRouteListTemplate();
        if (MixingStationExtensions.MixingRouteListTemplate == null)
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "MixingRouteListFieldUI: Failed to initialize MixingRouteListTemplate",
              DebugLogger.Category.MixingStation);
      }

      FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
      if (FieldLabel != null) FieldLabel.text = FieldText;
      MultiEditBlocker = transform.Find("Blocker")?.GetComponent<RectTransform>();
      MultiEditBlocker?.gameObject.SetActive(false);
      AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
      AddButton?.onClick.AddListener(AddClicked);

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          "MixingRouteListFieldUI: Start completed, awaiting Bind",
          DebugLogger.Category.MixingStation);
    }

    public void Bind(List<List<MixingRoute>> routesLists, List<MixingStationConfiguration> configs, UnityAction onChangedCallback)
    {
      RoutesLists = routesLists?.Select(list => list ?? []).ToList() ?? [];
      Configs = configs ?? [];
      OnChanged = onChangedCallback;

      RouteEntries = transform.Find("Contents").GetComponentsInChildren<MixingRouteEntryUI>(true);
      if (RouteEntries == null || RouteEntries.Length == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            "MixingRouteListFieldUI: No RouteEntries found in Contents",
            DebugLogger.Category.MixingStation);
        return;
      }

      for (int i = 0; i < RouteEntries.Length; i++)
      {
        int index = i;
        RouteEntries[i].onDeleteClicked.RemoveAllListeners();
        RouteEntries[i].onDeleteClicked.AddListener(() => EntryDeleteClicked(index));
        RouteEntries[i].onProductClicked.RemoveAllListeners();
        RouteEntries[i].onProductClicked.AddListener(() => EntryProductClicked(index));
        RouteEntries[i].onMixerClicked.RemoveAllListeners();
        RouteEntries[i].onMixerClicked.AddListener(() => EntryMixerClicked(index));
      }

      int maxRouteCount = RoutesLists.Any() ? RoutesLists.Max(list => list.Count) : 0;

      for (int i = 0; i < RouteEntries.Length; i++)
      {
        if (i < maxRouteCount)
        {
          RouteEntries[i].gameObject.SetActive(true);
          MixingRoute routeToBind = null;
          MixingStationConfiguration configToBind = null;
          for (int j = 0; j < RoutesLists.Count; j++)
          {
            if (i < RoutesLists[j].Count && RoutesLists[j][i] != null)
            {
              routeToBind = RoutesLists[j][i];
              configToBind = Configs.ElementAtOrDefault(j);
              break;
            }
          }

          if (routeToBind != null && configToBind != null)
          {
            RouteEntries[i].Bind(configToBind, routeToBind);
          }
          else
          {
            RouteEntries[i].gameObject.SetActive(false);
          }
        }
        else
        {
          RouteEntries[i].gameObject.SetActive(false);
        }
      }

      foreach (var routes in RoutesLists)
      {
        foreach (var route in routes)
        {
          if (route != null)
          {
            route.Product.onItemChanged.RemoveAllListeners();
            route.MixerItem.onItemChanged.RemoveAllListeners();
            route.Product.onItemChanged.AddListener(item => RefreshChanged(item, route));
            route.MixerItem.onItemChanged.AddListener(item => RefreshChanged(item, route));
          }
        }
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteListFieldUI: Bind completed, RoutesLists count={RoutesLists.Count}, RouteEntries count={RouteEntries.Length}",
          DebugLogger.Category.MixingStation);
      Refresh();
    }

    private void RefreshChanged(ItemDefinition item, MixingRoute route)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteListFieldUI: RefreshChanged triggered for item={item?.Name ?? "null"}, route={route}",
          DebugLogger.Category.MixingStation);
      Refresh();
      OnChanged?.Invoke();
      foreach (var config in Configs)
      {
        ConfigurationExtensions.InvokeChanged(config);
      }
    }

    public void Refresh()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteListFieldUI: Refresh()",
          DebugLogger.Category.MixingStation);
      if (RoutesLists == null || RoutesLists.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            "MixingRouteListFieldUI: Refresh skipped due to null or empty RoutesLists",
            DebugLogger.Category.MixingStation);
        for (int i = 0; i < RouteEntries.Length; i++)
        {
          RouteEntries[i].gameObject.SetActive(false);
        }
        if (AddButton != null)
          AddButton.gameObject.SetActive(false);
        return;
      }

      int maxRouteCount = RoutesLists.Any() ? RoutesLists.Max(list => list.Count) : 0;

      for (int i = 0; i < RouteEntries.Length; i++)
      {
        if (i < maxRouteCount)
        {
          RouteEntries[i].gameObject.SetActive(true);
          MixingRoute routeToBind = null;
          MixingStationConfiguration configToBind = null;
          for (int j = 0; j < RoutesLists.Count; j++)
          {
            if (i < RoutesLists[j].Count && RoutesLists[j][i] != null)
            {
              routeToBind = RoutesLists[j][i];
              configToBind = Configs.ElementAtOrDefault(j);
              break;
            }
          }

          if (routeToBind != null && configToBind != null)
          {
            RouteEntries[i].Bind(configToBind, routeToBind);
          }
          else
          {
            RouteEntries[i].gameObject.SetActive(false);
          }
        }
        else
        {
          RouteEntries[i].gameObject.SetActive(false);
        }
      }

      if (AddButton != null)
      {
        bool canAdd = RoutesLists.All(list => list.Count < MaxRoutes);
        AddButton.gameObject.SetActive(canAdd);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingRouteListFieldUI: Refresh(): AddButton active={AddButton.gameObject.activeSelf}",
            DebugLogger.Category.MixingStation);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteListFieldUI: Refresh() Complete",
          DebugLogger.Category.MixingStation);
    }

    private void AddClicked()
    {
      if (RoutesLists.All(list => list.Count < MaxRoutes))
      {
        for (int i = 0; i < RoutesLists.Count; i++)
        {
          if (Configs.ElementAtOrDefault(i) != null)
          {
            MixingRoute route = new(Configs[i]);
            RoutesLists[i].Add(route);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MixingRouteListFieldUI: Added new route for config {i}",
                DebugLogger.Category.MixingStation);
          }
        }
        Refresh();
        OnChanged?.Invoke();
        foreach (var config in Configs)
        {
          ConfigurationExtensions.InvokeChanged(config);
        }
      }
    }

    private void EntryDeleteClicked(int index)
    {
      bool anyRemoved = false;
      foreach (var routes in RoutesLists)
      {
        if (index >= 0 && index < routes.Count)
        {
          routes.RemoveAt(index);
          anyRemoved = true;
        }
      }
      if (anyRemoved)
      {
        Refresh();
        OnChanged?.Invoke();
        foreach (var config in Configs)
        {
          ConfigurationExtensions.InvokeChanged(config);
        }
      }
    }

    private void EntryProductClicked(int index)
    {
      if (RoutesLists == null || RoutesLists.Count == 0 || index < 0 || index >= RoutesLists[0].Count)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteListFieldUI: EntryProductClicked invalid state: RoutesLists={(RoutesLists == null ? "null" : RoutesLists.Count.ToString())}, index={index}",
            DebugLogger.Category.MixingStation);
        return;
      }

      MixingRoute route = RoutesLists[0][index];
      if (route == null || route.Product == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteListFieldUI: EntryProductClicked route or route.Product is null at index={index}",
            DebugLogger.Category.MixingStation);
        return;
      }

      var options = new List<ItemDefinition>();
      if (ProductManager.FavouritedProducts != null)
      {
        foreach (var item in ProductManager.FavouritedProducts)
        {
          if (item != null)
            options.Add(item);
        }
      }

      route.Product.Options = options;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteListFieldUI: ProductClicked {route.Product.SelectedItem?.Name ?? "null"} | Options count={options.Count}",
          DebugLogger.Category.MixingStation);
      MixingRouteEntryUI.OpenItemSelectorScreen(route.Product, "Favorites");
    }

    private void EntryMixerClicked(int index)
    {
      if (RoutesLists == null || RoutesLists.Count == 0 || index < 0 || index >= RoutesLists[0].Count)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MixingRouteListFieldUI: EntryMixerClicked invalid state: RoutesLists={(RoutesLists == null ? "null" : RoutesLists.Count.ToString())}, index={index}",
            DebugLogger.Category.MixingStation);
        return;
      }

      MixingRoute route = RoutesLists[0][index];
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: EntryMixerClicked {route.MixerItem.SelectedItem?.name} | {route.MixerItem.Options.Count}",
          DebugLogger.Category.MixingStation);
      MixingRouteEntryUI.OpenItemSelectorScreen(route.MixerItem, "Mixers");
    }
  }

  public class MixingRouteEntryUI : MonoBehaviour
  {
    public TextMeshProUGUI ProductLabel;
    public TextMeshProUGUI MixerLabel;
    public Button ProductButton;
    public Button MixerButton;
    public UnityEvent onDeleteClicked = new();
    public UnityEvent onProductClicked = new();
    public UnityEvent onMixerClicked = new();
    public MixingRoute Route;
    public MixingStationConfiguration Config;

    private void Awake()
    {
      ProductLabel = transform.Find("ProductIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
      MixerLabel = transform.Find("MixerItemIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
      ProductButton = transform.Find("ProductIMGUI")?.GetComponent<Button>();
      MixerButton = transform.Find("MixerItemIMGUI")?.GetComponent<Button>();
      transform.Find("Remove")?.GetComponent<Button>()?.onClick.AddListener(DeleteClicked);
      ProductButton.onClick.AddListener(ProductClicked);
      MixerButton.onClick.AddListener(MixerClicked);
    }

    private void OnProductItemChanged(ItemDefinition item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: OnProductItemChanged item={item?.Name ?? "null"}",
          DebugLogger.Category.MixingStation);
      ProductLabel.text = item?.Name ?? "Product";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    private void OnMixerItemChanged(ItemDefinition item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: OnMixerItemChanged item={item?.Name ?? "null"}",
          DebugLogger.Category.MixingStation);
      MixerLabel.text = item?.Name ?? "Mixer";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: Binding Route={(route != null ? "valid" : "null")}, Config={(config != null ? "valid" : "null")}",
          DebugLogger.Category.MixingStation);

      Config = config;
      Route = route;

      if (route == null || config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            "MixingRouteEntryUI: Bind called with null route or config",
            DebugLogger.Category.MixingStation);
        ProductLabel.text = "Product";
        MixerLabel.text = "Mixer";
        return;
      }

      ProductLabel.text = route.Product?.SelectedItem?.Name ?? "Product";
      MixerLabel.text = route.MixerItem?.SelectedItem?.Name ?? "Mixer";

      route.Product.onItemChanged.RemoveAllListeners();
      route.MixerItem.onItemChanged.RemoveAllListeners();
      route.Product.onItemChanged.AddListener(OnProductItemChanged);
      route.MixerItem.onItemChanged.AddListener(OnMixerItemChanged);
      ProductButton.onClick.AddListener(ProductClicked);
      MixerButton.onClick.AddListener(MixerClicked);

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: Bound Route Product={route.Product?.SelectedItem?.Name ?? "null"}, Mixer={route.MixerItem?.SelectedItem?.Name ?? "null"}",
          DebugLogger.Category.MixingStation);
    }

    public void ProductClicked() { onProductClicked.Invoke(); }
    public void MixerClicked() { onMixerClicked.Invoke(); }
    public void DeleteClicked() { onDeleteClicked.Invoke(); }

    public static void OpenItemSelectorScreen(ItemField itemField, string fieldName)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: OpenItemSelectorScreen for {fieldName}",
          DebugLogger.Category.MixingStation);

      if (itemField == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            "MixingRouteEntryUI: OpenItemSelectorScreen called with null itemField",
            DebugLogger.Category.MixingStation);
        return;
      }

      List<ItemSelector.Option> list = [];
      ItemSelector.Option selectedOption = null;

      if (itemField.CanSelectNone)
      {
        ItemSelector.Option none = new("None", null);
        list.Add(none);
        if (itemField.SelectedItem == null)
          selectedOption = list[0];
      }
      else
      {
        ItemSelector.Option none = new("None", null);
        if (list.IndexOf(none) != -1)
        {
          list.Remove(none);
        }
      }

      var options = itemField.Options ?? [];
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRouteEntryUI: OpenItemSelectorScreen, Options count={options.Count}",
          DebugLogger.Category.MixingStation);

      foreach (var option in options)
      {
        if (option == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              "MixingRouteEntryUI: Skipping null option in Options",
              DebugLogger.Category.MixingStation);
          continue;
        }
        var opt = new ItemSelector.Option(option.Name, option);
        list.Add(opt);
        if (itemField.SelectedItem == option)
          selectedOption = opt;
      }

      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, list, selectedOption, new Action<ItemSelector.Option>(selected =>
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"MixingRouteEntryUI: ItemSelectorScreen selected {selected?.Item?.Name ?? "null"} for {fieldName}",
                  DebugLogger.Category.MixingStation);
        itemField.SelectedItem = selected.Item;
        itemField.onItemChanged?.Invoke(selected.Item);
      }));
      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Open();
    }
  }

  public class MixingRoute
  {
    public ItemField Product { get; set; }
    public ItemField MixerItem { get; set; }

    public MixingRoute(MixingStationConfiguration config)
    {
      Product = new ItemField(config)
      {
        CanSelectNone = false,
        Options = []
      };
      var productOptions = new List<ItemDefinition>();
      if (ProductManager.FavouritedProducts != null)
      {
        foreach (var item in ProductManager.FavouritedProducts)
        {
          if (item != null)
            productOptions.Add(item);
        }
      }
      Product.Options = productOptions;

      MixerItem = new ItemField(config)
      {
        CanSelectNone = false,
        Options = []
      };
      var mixerOptions = new List<ItemDefinition>();
      var validMixIngredients = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients;
      if (validMixIngredients != null)
      {
        foreach (var item in validMixIngredients)
        {
          if (item != null)
            mixerOptions.Add(item);
        }
      }
      MixerItem.Options = mixerOptions;

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRoute: Initialized with Product Options={productOptions.Count}, Mixer Options={mixerOptions.Count}",
          DebugLogger.Category.MixingStation);
    }

    public void SetData(MixingRouteData data)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRoute: SetData: {data.Product?.ItemID} | {data.MixerItem?.ItemID}",
          DebugLogger.Category.MixingStation);
      if (data.Product != null)
        Product.Load(data.Product);
      if (data.MixerItem != null)
        MixerItem.Load(data.MixerItem);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MixingRoute: SetData: Loaded {Product.SelectedItem} | {MixerItem.SelectedItem}",
          DebugLogger.Category.MixingStation);
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration))]
  public class MixingStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, [typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation)])]
    static void ConstructorPostfix(MixingStationConfiguration __instance, MixingStation station)
    {
      try
      {
        if (__instance == null || station == null)
          return;
        Guid guid = station.GUID;
        __instance.StartThrehold.MaxValue = 20f;

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigurationPatch: Initializing for station: {station?.GUID.ToString() ?? "null"}, configHash={__instance.GetHashCode()}",
            DebugLogger.Category.MixingStation);

        if (!MixingStationExtensions.Config.ContainsKey(guid) || MixingStationExtensions.Config[guid] == null)
        {
          MixingStationExtensions.Config[guid] = __instance;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "MixingStationConfigurationPatch: Initialized MixingConfig",
              DebugLogger.Category.MixingStation);
        }
        if (!MixingStationExtensions.MixingRoutes.ContainsKey(guid) || MixingStationExtensions.MixingRoutes[guid] == null)
        {
          MixingStationExtensions.MixingRoutes[guid] = [];
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "MixingStationConfigurationPatch: Initialized MixingRoutes",
              DebugLogger.Category.MixingStation);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigurationPatch: Registered config {__instance.GetHashCode()}, station: {station?.GUID}",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationConfigurationPatch: Failed for station: {station?.GUID.ToString() ?? "null"}, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(MixingStationConfiguration __instance, ref string __result)
    {
      try
      {
        if (__instance == null || __instance.station == null)
          return;
        Guid guid = __instance.station.GUID;

        JArray mixingRoutesArray = [];
        if (MixingStationExtensions.MixingRoutes.TryGetValue(guid, out var routes) && routes.Any())
        {
          foreach (var route in routes)
          {
            string productItemID = route.Product.GetData()?.ItemID ?? "null";
            string mixerItemID = route.MixerItem.GetData()?.ItemID ?? "null";

            var routeObject = new JObject
            {
              ["Product"] = productItemID,
              ["MixerItem"] = mixerItemID
            };
            mixingRoutesArray.Add(routeObject);
          }
        }

        MixingStationConfigurationData data = new(
            __instance.Destination.GetData(),
            __instance.StartThrehold.GetData()
        );

        string configJson = JsonUtility.ToJson(data, true);
        JObject jsonObject = JObject.Parse(configJson);
        if (mixingRoutesArray.Count > 0)
          jsonObject["MixingRoutes"] = mixingRoutesArray;

        if (MixingStationExtensions.QualityFields.TryGetValue(guid, out var field))
          jsonObject["Quality"] = field.Value.ToString();
        __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigurationGetSaveStringPatch: Saved JSON for station={guid}: {__result}",
            DebugLogger.Category.MixingStation);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigurationGetSaveStringPatch: Routes JSON={mixingRoutesArray}",
            DebugLogger.Category.MixingStation);
        if (routes != null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationConfigurationGetSaveStringPatch: Routes count={routes.Count}",
              DebugLogger.Category.MixingStation);
          foreach (var route in routes)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MixingStationConfigurationGetSaveStringPatch: Route Product.ItemID={route.Product?.GetData()?.ItemID ?? "null"}, MixerItem.ItemID={route.MixerItem?.GetData()?.ItemID ?? "null"}",
                DebugLogger.Category.MixingStation);
          }
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationConfigurationGetSaveStringPatch: Failed for station GUID={__instance.station?.GUID.ToString() ?? "null"}, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(MixingStationConfiguration __instance)
    {
      try
      {
        if (__instance.station == null || __instance.station.gameObject == null)
        {
          Guid guid = __instance.station.GUID;
          MixingStationExtensions.MixingRoutes.Remove(guid);
          foreach (var pair in MixingStationExtensions.Config.Where(p => p.Value == __instance).ToList())
          {
            MixingStationExtensions.Config.Remove(pair.Key);
          }
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationConfigurationDestroyPatch: Removed station {guid} from dictionaries",
              DebugLogger.Category.MixingStation);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationConfigurationDestroyPatch: Skipped removal for station {__instance.station?.GUID}, station still exists",
              DebugLogger.Category.MixingStation);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationConfigurationDestroyPatch: Failed for station GUID={__instance.station?.GUID.ToString() ?? "null"}, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
  public class MixingStationConfigPanelBindPatch
  {
    private static void RouteListInvokeChanged(MixingStationConfiguration config)
    {
      ConfigurationExtensions.InvokeChanged(config);
    }
    private static Component GetRouteListComponent(ConfigPanel panel)
    {
      return panel.GetComponentInChildren<RouteListFieldUI>();
    }
    static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigPanelBindPatch: Binding configs, count: {configs?.Count ?? 0}",
            DebugLogger.Category.MixingStation);

        if (__instance == null || __instance.DestinationUI == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "MixingStationConfigPanelBindPatch: __instance or DestinationUI is null",
              DebugLogger.Category.MixingStation);
          return;
        }

        if (MixingStationExtensions.MixingRouteListTemplate == null)
        {
          MixingStationExtensions.InitializeStaticRouteListTemplate();
        }

        __instance.DestinationUI.gameObject.SetActive(false);

        GameObject routeListUIObj = Object.Instantiate(MixingStationExtensions.MixingRouteListTemplate, __instance.transform, false);
        routeListUIObj.name = "RouteListFieldUI";
        routeListUIObj.SetActive(true);
        var customRouteListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();

        var sliderObj = __instance.transform.Find("NumberFieldUI").gameObject;
        sliderObj.transform.Find("Description").gameObject.SetActive(false);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}",
            DebugLogger.Category.MixingStation);

        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        var qualityFieldUIObj = dryingRackPanelObj.transform.Find("QualityFieldUI")?.gameObject;
        qualityFieldUIObj.transform.Find("Description").gameObject.SetActive(false);
        var qualityUIObj = Object.Instantiate(qualityFieldUIObj, __instance.transform, false);
        qualityUIObj.SetActive(true);

        List<List<MixingRoute>> routesLists = [];
        List<MixingStationConfiguration> configList = [];
        List<QualityField> qualityList = new List<QualityField>();
        foreach (var config in configs.OfType<MixingStationConfiguration>())
        {
          Guid guid = config.station.GUID;
          config.StartThrehold.MaxValue = 20f;

          if (!MixingStationExtensions.MixingRoutes.ContainsKey(guid))
            MixingStationExtensions.MixingRoutes[guid] = [];
          routesLists.Add(MixingStationExtensions.MixingRoutes[guid]);
          configList.Add(config);
          if (!MixingStationExtensions.QualityFields.ContainsKey(guid))
            MixingStationExtensions.QualityFields[guid] = new QualityField(config);
          qualityList.Add(MixingStationExtensions.QualityFields[guid]);
        }
        customRouteListUI.Bind(routesLists, configList, () => configs.ForEach(c => ConfigurationExtensions.InvokeChanged(c)));
        qualityUIObj.GetComponent<QualityFieldUI>().Bind(qualityList);
        qualityUIObj.transform.Find("Title").GetComponent<TextMeshProUGUI>().text = "Min Quality";

        RectTransform routeListRect = routeListUIObj.GetComponent<RectTransform>();
        routeListRect.anchoredPosition = new Vector2(routeListRect.anchoredPosition.x, -165.76f);
        var qualityRect = qualityUIObj.GetComponent<RectTransform>();
        qualityRect.anchoredPosition = new Vector2(routeListRect.anchoredPosition.x, routeListRect.anchoredPosition.y + 70f);

      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationConfigPanelBindPatch: Postfix failed, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationLoader), "Load")]
  public class MixingStationLoaderPatch
  {
    [HarmonyPostfix]
    static void Postfix(string mainPath)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationLoaderPatch: Processing Postfix for mainPath: {mainPath}",
            DebugLogger.Category.MixingStation);
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MixingStationLoaderPatch: No GridItem found for mainPath: {mainPath}",
              DebugLogger.Category.MixingStation);
          return;
        }
        if (gridItem is not MixingStation station)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MixingStationLoaderPatch: GridItem is not a MixingStation for mainPath: {mainPath}, type: {gridItem.GetType().Name}",
              DebugLogger.Category.MixingStation);
          return;
        }
        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MixingStationLoaderPatch: No Configuration.json found at: {configPath}",
              DebugLogger.Category.MixingStation);
          return;
        }
        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MixingStationLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}",
              DebugLogger.Category.MixingStation);
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationLoaderPatch: Loaded JSON: {text}",
            DebugLogger.Category.MixingStation);

        JObject jsonObject = JObject.Parse(text);
        JToken mixingRoutesJToken = jsonObject["MixingRoutes"];

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationLoaderPatch: Extracted mixingRoutesJToken: {mixingRoutesJToken}",
            DebugLogger.Category.MixingStation);

        Guid guid = station.GUID;
        MixingStationConfiguration config = station.Configuration as MixingStationConfiguration;
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"MixingStationLoaderPatch: No valid MixingStationConfiguration for station: {guid}",
              DebugLogger.Category.MixingStation);
          return;
        }
        if (!MixingStationExtensions.Config.ContainsKey(guid))
        {
          MixingStationExtensions.Config[guid] = config;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationLoaderPatch: Registered MixingConfig for station: {guid}",
              DebugLogger.Category.MixingStation);
        }

        List<MixingRoute> routes = [];
        if (mixingRoutesJToken is JArray array && array?.Count > 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationLoaderPatch: array.count: {array.Count}",
              DebugLogger.Category.MixingStation);
          for (int i = 0; i < array.Count; i++)
          {
            var route = new MixingRoute(config);
            MixingRouteData data = new();
            var routeData = array[i];
            if (routeData["Product"] != null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"MixingStationLoaderPatch: Loading Product={routeData["Product"].ToString()}",
                  DebugLogger.Category.MixingStation);
              if (string.IsNullOrEmpty((string)routeData["Product"])) data.Product = null;
              else data.Product = new(routeData["Product"].ToString());
            }
            if (routeData["MixerItem"] != null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"MixingStationLoaderPatch: Loading Mixer={routeData["MixerItem"].ToString()}",
                  DebugLogger.Category.MixingStation);
              if (string.IsNullOrEmpty((string)routeData["MixerItem"])) data.MixerItem = null;
              else data.MixerItem = new(routeData["MixerItem"].ToString());
            }
            route.SetData(data);
            routes.Add(route);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MixingStationLoaderPatch: Loaded route Product={route.Product.SelectedItem?.Name ?? "null"}, MixerItem={route.MixerItem.SelectedItem?.Name ?? "null"} for station={guid}",
                DebugLogger.Category.MixingStation);
          }
          MixingStationExtensions.MixingRoutes[guid] = routes;

          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationLoaderPatch: Loaded {routes.Count} MixingRoutes for station={guid}",
              DebugLogger.Category.MixingStation);

          var targetQuality = new QualityField(config);
          targetQuality.onValueChanged.AddListener(delegate
          {
            config.InvokeChanged();
          });
          targetQuality.SetValue(Enum.Parse<EQuality>(jsonObject["Quality"]?.ToString()), network: false);
          MixingStationExtensions.QualityFields[guid] = targetQuality;
        }
        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems",
            DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}",
            DebugLogger.Category.MixingStation);
      }
    }
  }
}