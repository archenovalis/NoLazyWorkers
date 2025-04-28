using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppNewtonsoft.Json.Linq;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.Management.UI;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.Persistence.Loaders;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.UI.Management;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

using static NoLazyWorkers_IL2CPP.NoLazyUtilities;
using Il2CppScheduleOne.Employees;

namespace NoLazyWorkers_IL2CPP.Chemists
{
  public static class MixingStationExtensions
  {
    public static Dictionary<Il2CppSystem.Guid, ObjectField> Supply = [];
    public static Dictionary<Il2CppSystem.Guid, MixingStationConfiguration> MixingConfig = [];
    public static Dictionary<Il2CppSystem.Guid, TransitRoute> SupplyRoute = [];
    public static Dictionary<Il2CppSystem.Guid, List<MixingRoute>> MixingRoutes = [];
    public static GameObject MixingRouteListTemplate { get; set; }


    public static void SourceChanged(this MixingStationConfiguration mixerConfig, TransitRoute SourceRoute, ObjectField Supply, MixingStation station)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Msg($"SourceChanged(MixingStation): Called for MixerConfig: {mixerConfig}, Station: {station?.name ?? "null"}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
        if (mixerConfig == null || station == null)
        {
          MelonLogger.Error($"SourceChanged(MixingStation): MixerConfig or Station is null");
          return;
        }
        if (!MixingStationExtensions.Supply.ContainsKey(station.GUID))
        {
          MixingStationExtensions.Supply[station.GUID] = Supply; // Register if missing
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Warning($"SourceChanged(MixingStation): Registered missing MixerSupply for MixerConfig: {mixerConfig}");
        }

        if (SourceRoute != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Msg("SourceChanged(MixingStation): Destroying existing SourceRoute");
          SourceRoute.Destroy();
          SupplyRoute[station.GUID] = null;
        }

        if (Supply.SelectedObject != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Msg($"SourceChanged(MixingStation): Creating new TransitRoute from {Supply.SelectedObject.name} to MixingStation");
          SourceRoute = new TransitRoute(Supply.SelectedObject.Cast<ITransitEntity>(), station.Cast<ITransitEntity>());
          SupplyRoute[station.GUID] = SourceRoute;
          if (station.Configuration.IsSelected)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Msg("SourceChanged(MixingStation): Station is selected, enabling TransitRoute visuals");
            SourceRoute.SetVisualsActive(true);
          }
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Msg("SourceChanged(MixingStation): Supply.SelectedObject is null, setting SourceRoute to null");
          SupplyRoute[station.GUID] = null;
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"SourceChanged(MixingStation): Failed for MixerConfig: {mixerConfig}, error: {e}");
      }
    }

    public static void SourceChanged(this MixingStationConfiguration mixerConfig, BuildableItem item)
    {
      try
      {
        if (mixerConfig == null)
        {
          MelonLogger.Error("SourceChanged(MixingStation): MixingStationConfiguration is null");
          return;
        }
        MixingStation station = mixerConfig.station;
        // Check if mixerConfig is registered
        if (!MixingStationExtensions.Supply.ContainsKey(station.GUID))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Warning($"SourceChanged(MixingStation): MixerSupply does not contain key for station: {station}");
          return;
        }
        if (!SupplyRoute.ContainsKey(station.GUID))
        {
          SupplyRoute[station.GUID] = null; // Initialize if missing
        }

        TransitRoute SourceRoute = SupplyRoute[station.GUID];
        if (SourceRoute != null)
        {
          SourceRoute.Destroy();
          SupplyRoute[station.GUID] = null;
        }

        ObjectField supply = Supply[station.GUID];
        if (supply.SelectedObject != null)
        {
          SourceRoute = new TransitRoute(supply.SelectedObject.Cast<ITransitEntity>(), station.Cast<ITransitEntity>());
          SupplyRoute[station.GUID] = SourceRoute;
          if (station.Configuration.IsSelected)
          {
            SourceRoute.SetVisualsActive(true);
            return;
          }
        }
        else
        {
          SupplyRoute[station.GUID] = null;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation) MelonLogger.Msg($"SourceChanged(MixingStation): Updated for MixerConfig: {mixerConfig}, Supply: {supply?.SelectedObject?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"SourceChanged(MixingStation): Failed for MixerConfig: {mixerConfig}, error: {e}");
      }
    }

    public static void SupplyOnObjectChangedInvoke(BuildableItem item, MixingStationConfiguration __instance, MixingStation station, ObjectField Supply)
    {
      ConfigurationExtensions.InvokeChanged(__instance);
      SourceChanged(__instance, item);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"SupplyOnObjectChangedInvoke: Supply changed for station {station?.GUID.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.GUID.ToString() ?? "null"}");
    }

    public static void RestoreConfigurations()
    {
      // Restore MixingStation configurations
      MixingStation[] mixingStations = UnityEngine.Object.FindObjectsOfType<MixingStation>();
      foreach (MixingStation station in mixingStations)
      {
        try
        {
          if (station.Configuration is MixingStationConfiguration mixerConfig)
          {
            if (!MixingConfig.ContainsKey(station.GUID))
            {
              MixingConfig[station.GUID] = mixerConfig;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Warning($"RestoreConfigurations: Registered missing MixerConfig for station: {station.GUID}");
            }
            // Initialize MixingSupply if missing
            if (!Supply.TryGetValue(station.GUID, out var supply))
            {
              supply = new ObjectField(mixerConfig)
              {
                TypeRequirements = new(),
                DrawTransitLine = true
              };
              Supply[station.GUID] = supply;
              Supply[station.GUID].onObjectChanged.RemoveAllListeners();
              Supply[station.GUID].onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>(SupplyOnObjectChangedInvoke));
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Msg($"RestoreConfigurations: Initialized MixingSupply for station: {station.GUID}");
            }

            // Initialize MixingRoutes if missing
            if (!MixingRoutes.ContainsKey(station.GUID))
            {
              MixingRoutes[station.GUID] = new();
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Msg($"RestoreConfigurations: Initialized MixingRoutes for station: {station.GUID}");
            }
          }
          else
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
              MelonLogger.Msg($"RestoreConfigurations: Skipped station {station.GUID}, no MixingStationConfiguration");
          }
        }
        catch (Exception e)
        {
          MelonLogger.Error($"RestoreConfigurations: Failed for station: {station?.GUID.ToString() ?? "null"}, error: {e}");
        }
      }
    }

    public static void InitializeStaticRouteListTemplate()
    {
      var routeListTemplate = NoLazyUtilities.GetTransformTemplateFromConfigPanel(
            EConfigurableType.Packager,
            "RouteListFieldUI");
      if (routeListTemplate == null)
      {
        MelonLogger.Error("InitializeStaticRouteListTemplate: routeListTemplate is null");
        return;
      }
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: Initializing MixingRouteListTemplate");

        // Instantiate the template
        var templateObj = UnityEngine.Object.Instantiate(routeListTemplate.gameObject);
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);
        try
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg("InitializeStaticRouteListTemplate: Adding MixingRouteListFieldUI");
          var routeListFieldUI = templateObj.AddComponent<MixingRouteListFieldUI>();
        }
        catch (Exception e)
        {
          MelonLogger.Error($"InitializeStaticRouteListTemplate: Failed to add MixingRouteListFieldUI via Il2CppType, error: {e}");
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: MixingRouteListFieldUI added");

        // Replace RouteListFieldUI with MixingRouteListFieldUI
        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
          UnityEngine.Object.Destroy(defaultScript);
        if (templateObj.GetComponent<MixingRouteListFieldUI>() == null)
          templateObj.AddComponent<MixingRouteListFieldUI>();

        // Replace RouteEntryUI with MixingRouteEntryUI for all entries
        var contentsTransform = templateObj.transform.Find("Contents");
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: contentsTransform: {contentsTransform?.name}");

        // Prepare newEntry transform
        var entryTransform = templateObj.transform.Find("Contents/Entry");
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: entryTransform: {entryTransform?.name}");

        // Update AddNew label
        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: addNewLabel?.text: {addNewLabel?.text}");

        for (int i = 0; i < MixingRouteListFieldUI.MaxRoutes; i++)
        {
          Transform entry = contentsTransform.Find($"Entry ({i})") ?? (i == 0 ? contentsTransform.Find("Entry") : null);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"InitializeStaticRouteListTemplate: Processing Entry ({i})");

          if (entry == null)
          {
            // Instantiate new entry from the full template
            GameObject newEntry = UnityEngine.Object.Instantiate(entryTransform.gameObject, contentsTransform, false);
            newEntry.name = $"Entry ({i})";
            newEntry.AddComponent<CanvasRenderer>();
            entry = newEntry.transform;

            // Rename Source and Destination
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
            // Update existing entry
            var productTransform = entry.Find("Source");
            if (productTransform != null)
              productTransform.name = "ProductIMGUI";
            var mixerTransform = entry.Find("Destination");
            if (mixerTransform != null)
              mixerTransform.name = "MixerItemIMGUI";
          }

          // Replace RouteEntryUI with MixingRouteEntryUI
          var routeEntryUI = entry.GetComponent<RouteEntryUI>();
          if (routeEntryUI != null)
            UnityEngine.Object.Destroy(routeEntryUI);
          if (entry.GetComponent<MixingRouteEntryUI>() == null)
            entry.gameObject.AddComponent<MixingRouteEntryUI>();
        }

        contentsTransform.Find("AddNew").SetAsLastSibling();
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: SetAsLastSibling");

        // Hide unnecessary elements
        templateObj.transform.Find("Title")?.gameObject.SetActive(false);
        templateObj.transform.Find("From")?.gameObject.SetActive(false);
        templateObj.transform.Find("To")?.gameObject.SetActive(false);

        MixingRouteListTemplate = templateObj;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: Static MixingRouteListTemplate initialized successfully");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"InitializeStaticRouteListTemplate: Failed to initialize MixingRouteListTemplate, error: {e}");
      }
    }
  }
  public static class SelectionHelper
  {
    public static Action<ProductDefinition> ProductSelectionCallback { get; set; }
  }

  [Serializable]
  public class MixingRouteDataWrapper : MonoBehaviour
  {
    public MixingRouteData[] Routes = [];
  }

  [Serializable]
  public class MixingRouteData : MonoBehaviour
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData()
    {
      {
        Product = new("");
        MixerItem = new("");
      }
    }

    public void SetData(ItemFieldData product, ItemFieldData mixerItem)
    {
      Product = product;
      MixerItem = mixerItem;
    }
  }

  [RegisterTypeInIl2Cpp]
  public class MixingRouteListFieldUI : MonoBehaviour
  {
    public string FieldText = "Recipes";
    public TextMeshProUGUI FieldLabel;
    public MixingRouteEntryUI[] RouteEntries;
    public RectTransform MultiEditBlocker;
    public Button AddButton;
    public static readonly int MaxRoutes = 7;
    private List<List<MixingRoute>> RoutesLists;
    private List<MixingStationConfiguration> Configs;
    private UnityAction OnChanged;

    private void Start()
    {
      if (MixingStationExtensions.MixingRouteListTemplate != null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: MixingRouteListTemplate already initialized, skipping");
      }
      else
      {
        MixingStationExtensions.InitializeStaticRouteListTemplate();
        if (MixingStationExtensions.MixingRouteListTemplate == null)
          MelonLogger.Warning("MixingRouteListFieldUI: Failed to initialize MixingRouteListTemplate");
      }

      FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
      if (FieldLabel != null) FieldLabel.text = FieldText;
      MultiEditBlocker = transform.Find("Blocker")?.GetComponent<RectTransform>();
      MultiEditBlocker?.gameObject.SetActive(false);
      AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
      if (AddButton != null) AddButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(AddClicked));

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg("MixingRouteListFieldUI: Start completed, awaiting Bind");
    }

    public void Bind(List<List<MixingRoute>> routesLists, List<MixingStationConfiguration> configs, UnityAction onChangedCallback)
    {
      RoutesLists = routesLists?.Select(list => list ?? new List<MixingRoute>()).ToList() ?? new List<List<MixingRoute>>();
      Configs = configs ?? new List<MixingStationConfiguration>();
      OnChanged = onChangedCallback;

      RouteEntries = transform.Find("Contents").GetComponentsInChildren<MixingRouteEntryUI>(true);
      if (RouteEntries == null || RouteEntries.Length == 0)
      {
        MelonLogger.Error("MixingRouteListFieldUI: No RouteEntries found in Contents");
        return;
      }

      for (int i = 0; i < RouteEntries.Length; i++)
      {
        int index = i;
        RouteEntries[i].onDeleteClicked.RemoveAllListeners();
        RouteEntries[i].onDeleteClicked.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => EntryDeleteClicked(index)));
        RouteEntries[i].onProductClicked.RemoveAllListeners();
        RouteEntries[i].onProductClicked.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => EntryProductClicked(index)));
        RouteEntries[i].onMixerClicked.RemoveAllListeners();
        RouteEntries[i].onMixerClicked.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => EntryMixerClicked(index)));
      }

      // Determine the maximum number of routes
      int maxRouteCount = RoutesLists.Any() ? RoutesLists.Max(list => list.Count) : 0;

      // Bind RouteEntries
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
            route.Product.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>((ItemDefinition item) => RefreshChanged(item, route)));
            route.MixerItem.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>((ItemDefinition item) => RefreshChanged(item, route)));
          }
        }
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: Bind completed, RoutesLists count={RoutesLists.Count}, RouteEntries count={RouteEntries.Length}");
      Refresh();
    }

    private void RefreshChanged(ItemDefinition item, MixingRoute route)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: RefreshChanged triggered for item={item?.Name ?? "null"}, route={route}");
      Refresh();
      OnChanged?.Invoke();
      foreach (var config in Configs)
      {
        ConfigurationExtensions.InvokeChanged(config);
      }
    }

    public void Refresh()
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: Refresh()");
      if (RoutesLists == null || RoutesLists.Count == 0)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Warning("MixingRouteListFieldUI: Refresh skipped due to null or empty RoutesLists");
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): AddButton active={AddButton.gameObject.activeSelf}");
      }
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: Refresh() Complete");
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
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
              MelonLogger.Msg($"MixingRouteListFieldUI: Added new route for config {i}");
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Warning($"MixingRouteListFieldUI: EntryProductClicked invalid state: RoutesLists={(RoutesLists == null ? "null" : RoutesLists.Count.ToString())}, index={index}");
        return;
      }

      MixingRoute route = RoutesLists[0][index];
      if (route == null || route.Product == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Warning($"MixingRouteListFieldUI: EntryProductClicked route or route.Product is null at index={index}");
        return;
      }

      var options = new Il2CppSystem.Collections.Generic.List<ItemDefinition>();
      if (ProductManager.FavouritedProducts != null)
      {
        foreach (var item in ProductManager.FavouritedProducts)
        {
          if (item != null)
            options.Add(item);
        }
      }

      route.Product.Options = options;
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: ProductClicked {route.Product.SelectedItem?.Name ?? "null"} | Options count={options.Count}");
      MixingRouteEntryUI.OpenItemSelectorScreen(route.Product, "Favorites");
    }

    private void EntryMixerClicked(int index)
    {
      if (RoutesLists == null || RoutesLists.Count == 0 || index < 0 || index >= RoutesLists[0].Count)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Warning($"MixingRouteListFieldUI: EntryMixerClicked invalid state: RoutesLists={(RoutesLists == null ? "null" : RoutesLists.Count.ToString())}, index={index}");
        return;
      }

      MixingRoute route = RoutesLists[0][index];
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Warning($"MixingRouteEntryUI: EntryMixerClicked {route.MixerItem.SelectedItem?.name} | {route.MixerItem.Options.Count}");
      MixingRouteEntryUI.OpenItemSelectorScreen(route.MixerItem, "Mixers");

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Warning($"MixingRouteEntryUI: EntryMixerClicked RoutesLists == null");
    }
  }

  [RegisterTypeInIl2Cpp]
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
      transform.Find("Remove")?.GetComponent<Button>()?.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(DeleteClicked));
      ProductButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(ProductClicked));
      MixerButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(MixerClicked));
    }

    private void OnProductItemChanged(ItemDefinition item)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OnProductItemChanged item={item?.Name ?? "null"}");
      ProductLabel.text = item?.Name ?? "Product";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    private void OnMixerItemChanged(ItemDefinition item)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OnMixerItemChanged item={item?.Name ?? "null"}");
      MixerLabel.text = item?.Name ?? "Mixer";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: Binding Route={(route != null ? "valid" : "null")}, Config={(config != null ? "valid" : "null")}");

      Config = config;
      Route = route;

      if (route == null || config == null)
      {
        MelonLogger.Warning("MixingRouteEntryUI: Bind called with null route or config");
        ProductLabel.text = "Product";
        MixerLabel.text = "Mixer";
        return;
      }

      ProductLabel.text = route.Product?.SelectedItem?.Name ?? "Product";
      MixerLabel.text = route.MixerItem?.SelectedItem?.Name ?? "Mixer";

      route.Product.onItemChanged.RemoveAllListeners();
      route.MixerItem.onItemChanged.RemoveAllListeners();
      route.Product.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>(OnProductItemChanged));
      route.MixerItem.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>(OnMixerItemChanged));
      ProductButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(ProductClicked));
      MixerButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(MixerClicked));

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: Bound Route Product={route.Product?.SelectedItem?.Name ?? "null"}, Mixer={route.MixerItem?.SelectedItem?.Name ?? "null"}");
    }

    public void ProductClicked() { onProductClicked.Invoke(); }
    public void MixerClicked() { onMixerClicked.Invoke(); }
    public void DeleteClicked() { onDeleteClicked.Invoke(); }

    public static void OpenItemSelectorScreen(ItemField itemField, string fieldName)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OpenItemSelectorScreen for {fieldName}");

      if (itemField == null)
      {
        MelonLogger.Warning("MixingRouteEntryUI: OpenItemSelectorScreen called with null itemField");
        return;
      }

      Il2CppSystem.Collections.Generic.List<ItemSelector.Option> list = new();
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

      var options = itemField.Options ?? new Il2CppSystem.Collections.Generic.List<ItemDefinition>();
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OpenItemSelectorScreen, Options count={options.Count}");

      foreach (var option in options)
      {
        if (option == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Warning("MixingRouteEntryUI: Skipping null option in Options");
          continue;
        }
        var opt = new ItemSelector.Option(option.Name, option);
        list.Add(opt);
        if (itemField.SelectedItem == option)
          selectedOption = opt;
      }

      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, list, selectedOption, new Action<ItemSelector.Option>(selected =>
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingRouteEntryUI: ItemSelectorScreen selected {selected?.Item?.Name ?? "null"} for {fieldName}");
        itemField.SelectedItem = selected.Item;
        // Explicitly invoke onItemChanged to ensure listeners are triggered
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
        Options = new()
      };
      var productOptions = new Il2CppSystem.Collections.Generic.List<ItemDefinition>();
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
        Options = new Il2CppSystem.Collections.Generic.List<ItemDefinition>()
      };
      var mixerOptions = new Il2CppSystem.Collections.Generic.List<ItemDefinition>();
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

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRoute: Initialized with Product Options={productOptions.Count}, Mixer Options={mixerOptions.Count}");
    }

    public void SetData(MixingRouteData data)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRoute: SetData: {data.Product?.ItemID} | {data.MixerItem?.ItemID}");
      if (data.Product != null)
        Product.Load(data.Product);
      if (data.MixerItem != null)
        MixerItem.Load(data.MixerItem);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingRoute: SetData: Loaded {Product.SelectedItem} | {MixerItem.SelectedItem}");
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration))]
  public class MixingStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, [typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation)])]
    static void Postfix(MixingStationConfiguration __instance, MixingStation station)
    {
      try
      {
        __instance.StartThrehold.MaxValue = 20f;

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Initializing for station: {station?.GUID.ToString() ?? "null"}, configHash={__instance.GetHashCode()}");

        if (!MixingStationExtensions.Supply.ContainsKey(station.GUID) || !MixingStationExtensions.Supply.TryGetValue(station.GUID, out var supply))
        {
          supply = new(__instance)
          {
            TypeRequirements = new(),
            DrawTransitLine = true
          };
          supply.onObjectChanged.RemoveAllListeners();
          supply.onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>(MixingStationExtensions.SupplyOnObjectChangedInvoke));
          MixingStationExtensions.Supply[station.GUID] = supply;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg("MixingStationConfigurationPatch: Initialized Supply");
        }
        if (!MixingStationExtensions.MixingConfig.ContainsKey(station.GUID) || MixingStationExtensions.MixingConfig[station.GUID] == null)
        {
          MixingStationExtensions.MixingConfig[station.GUID] = __instance;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg("MixingStationConfigurationPatch: Initialized MixingConfig");
        }
        if (!MixingStationExtensions.MixingRoutes.ContainsKey(station.GUID) || MixingStationExtensions.MixingRoutes[station.GUID] == null)
        {
          MixingStationExtensions.MixingRoutes[station.GUID] = new();
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg("MixingStationConfigurationPatch: Initialized MixingRoutes");
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Registered supply and config {__instance.GetHashCode()}, station: {station?.GUID}, supply={supply.SelectedObject?.GUID}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationPatch: Failed for station: {station?.GUID.ToString() ?? "null"}, error: {e}");
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MixingStationConfiguration), "GetSaveString")]
    static void Postfix(MixingStationConfiguration __instance, ref string __result)
    {
      try
      {
        JObject supplyObject = null;
        if ((!__instance.station || !MixingStationExtensions.Supply.ContainsKey(__instance.station.GUID)) && DebugConfig.EnableDebugLogs)
          MelonLogger.Warning($"MixingStationConfigurationGetSaveStringPatch: No MixingSupply entry for config {__instance.GetHashCode()} station {__instance.station.GUID}");
        else
        {
          supplyObject = new JObject
          {
            ["ObjectGUID"] = MixingStationExtensions.Supply[__instance.station.GUID].SelectedObject.GUID.ToString()
          };
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Warning($"MixingStationConfigurationGetSaveStringPatch: supplyData: {supplyObject["ObjectGUID"]}");
        }
        // Manually serialize MixingRoutes as a comma-separated string of JSON objects
        JArray mixingRoutesArray = new();
        if (MixingStationExtensions.MixingRoutes.TryGetValue(__instance.station.GUID, out var routes) && routes != null && routes.Count > 0)
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

        // Create the config data
        MixingStationConfigurationData data = new(
            __instance.Destination.GetData(),
            __instance.StartThrehold.GetData()
        );

        // Serialize config data to JSON
        string configJson = JsonUtility.ToJson(data, true);

        // Combine config data with additional fields using Newtonsoft.Json
        JObject jsonObject = JObject.Parse(configJson);
        if (mixingRoutesArray.Count > 0)
          jsonObject["MixingRoutes"] = mixingRoutesArray;
        if (supplyObject != null)
          jsonObject["Supply"] = supplyObject;

        __result = jsonObject.ToString(Il2CppNewtonsoft.Json.Formatting.Indented);

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        {
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Saved JSON for station={__instance.station.GUID}: {__result}");
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Routes JSON={mixingRoutesArray}");
          if (routes != null)
          {
            MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Routes count={routes.Count}");
            foreach (var route in routes)
            {
              MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Route Product.ItemID={route.Product.GetData()?.ItemID ?? "null"}, MixerItem.ItemID={route.MixerItem.GetData()?.ItemID ?? "null"}");
            }
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationGetSaveStringPatch: Failed for station GUID={__instance.station?.GUID.ToString() ?? "null"}, error: {e}");
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MixingStationConfiguration), "Destroy")]
    static void Postfix(MixingStationConfiguration __instance)
    {
      try
      {
        // Only remove if the station GameObject is actually being destroyed
        if (__instance.station == null || __instance.station.gameObject == null)
        {
          MixingStationExtensions.Supply.Remove(__instance.station.GUID);
          MixingStationExtensions.MixingRoutes.Remove(__instance.station.GUID);
          foreach (var pair in MixingStationExtensions.MixingConfig.Where(p => p.Value == __instance).ToList())
          {
            MixingStationExtensions.MixingConfig.Remove(pair.Key);
          }
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Removed station {__instance.station?.GUID} from dictionaries");
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Skipped removal for station {__instance.station?.GUID}, station still exists");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationDestroyPatch: Failed for station GUID={__instance.station?.GUID.ToString() ?? "null"}, error: {e}");
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
    static void Postfix(MixingStationConfigPanel __instance, Il2CppSystem.Collections.Generic.List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Binding configs, count: {configs?.Count ?? 0}");

        if (__instance == null || __instance.DestinationUI == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: __instance or DestinationUI is null");
          return;
        }

        // Clean up existing UI elements
        int childCount = __instance.transform.childCount;
        for (int i = 0; i < childCount; i++)
        {
          Transform child = __instance.transform.GetChild(i);
          if (child.name == "SupplyUI" || child.name == "RouteListFieldUI")
            UnityEngine.Object.Destroy(child.gameObject);
        }

        if (MixingStationExtensions.MixingRouteListTemplate == null)
        {
          MixingStationExtensions.InitializeStaticRouteListTemplate();
        }

        // Instantiate UI objects
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        GameObject routeListUIObj = UnityEngine.Object.Instantiate(MixingStationExtensions.MixingRouteListTemplate, __instance.transform, false);
        routeListUIObj.name = "RouteListFieldUI";
        routeListUIObj.SetActive(true);
        var customRouteListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();

        List<ObjectField> supplyList = [];
        List<List<MixingRoute>> routesLists = [];
        List<MixingStationConfiguration> configList = [];

        // Bind all selected stations
        foreach (var varConfig in configs)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationConfigPanelBindPatch: foreach configs {varConfig.GetType().FullName} | {varConfig.GetIl2CppType().FullName} ");

          if (varConfig.TryCast<MixingStationConfiguration>() is MixingStationConfiguration config)
          {
            config.StartThrehold.MaxValue = 20f;
            // Supply UI setup
            foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
            {
              if (child.gameObject.name == "Title") child.text = "Supplies";
              else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
            }
            if (MixingStationExtensions.Supply.TryGetValue(config.station.GUID, out ObjectField supply))
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Before Bind, station: {config.station.GUID}, SelectedObject: {supply.SelectedObject?.name ?? "null"}");
            }
            else
            {
              supply = new ObjectField(config);
              MixingStationExtensions.Supply[config.station.GUID] = supply;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Warning($"MixingStationConfigPanelBindPatch: No supply found for MixingStationConfiguration, station: {config.station.GUID}");
            }
            supplyList.Add(supply);
            // Destination UI update
            foreach (TextMeshProUGUI child in __instance.DestinationUI.GetComponentsInChildren<TextMeshProUGUI>())
            {
              if (child.gameObject.name == "Title") child.text = "Destination";
              else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
            }

            // Collect routes and configurations
            if (!MixingStationExtensions.MixingRoutes.ContainsKey(config.station.GUID))
              MixingStationExtensions.MixingRoutes[config.station.GUID] = new();
            var routes = MixingStationExtensions.MixingRoutes[config.station.GUID];
            routesLists.Add(routes);
            configList.Add(config);
          }
        }

        // Bind UI components
        customRouteListUI.Bind(routesLists, configList, DelegateSupport.ConvertDelegate<UnityAction>(() => configs.ForEach(new Action<EntityConfiguration>(c => ConfigurationExtensions.InvokeChanged(c)))));
        supplyUI.Bind(ConvertList(supplyList));

        // Position adjustments
        RectTransform supplyRect = supplyUIObj.GetComponent<RectTransform>();
        supplyRect.anchoredPosition = new Vector2(supplyRect.anchoredPosition.x, -135.76f);
        RectTransform destRect = __instance.DestinationUI.GetComponent<RectTransform>();
        destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, -195.76f);
        RectTransform routeListRect = routeListUIObj.GetComponent<RectTransform>();
        routeListRect.anchoredPosition = new Vector2(routeListRect.anchoredPosition.x, -290.76f);
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Postfix failed, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationLoader), "Load")]
  public class MixingStationLoaderPatch
  {
    private static void MixingSupplySourceChanged(MixingStationConfiguration config, MixingStation station, BuildableItem item)
    {
      MixingStationExtensions.SourceChanged(config, item);
      ConfigurationExtensions.InvokeChanged(config);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"MixingStationLoaderPatch: Supply changed for station {station.GUID}, newSupply={MixingStationExtensions.Supply[station.GUID].SelectedObject?.GUID.ToString() ?? "null"}");
    }

    [HarmonyPostfix]
    static void Postfix(string mainPath)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Processing Postfix for mainPath: {mainPath}");
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem.TryCast<MixingStation>() is not MixingStation station)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: GridItem is not a MixingStation for mainPath: {mainPath}, type: {gridItem.GetType().Name}");
          return;
        }
        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: No Configuration.json found at: {configPath}");
          return;
        }
        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
          return;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Loaded JSON: {text}");
        JObject jsonObject = JObject.Parse(text);
        JToken mixingRoutesJToken = jsonObject["MixingRoutes"];
        JToken supplyJToken = jsonObject["Supply"];
        jsonObject.Remove("MixingRoutes");
        jsonObject.Remove("Supply");
        string modifiedJson = jsonObject.ToString();

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        {
          MelonLogger.Msg($"MixingStationLoaderPatch: Stripped JSON: {modifiedJson}");
          MelonLogger.Msg($"MixingStationLoaderPatch: Extracted mixingRoutesJToken: {mixingRoutesJToken}");
          MelonLogger.Msg($"MixingStationLoaderPatch: Extracted supplyJToken: {supplyJToken}");
        }
        MixingStationConfiguration config = station.Configuration?.TryCast<MixingStationConfiguration>();
        if (config == null)
        {
          MelonLogger.Error($"MixingStationLoaderPatch: No valid MixingStationConfiguration for station: {station.GUID}");
          return;
        }
        if (!MixingStationExtensions.MixingConfig.ContainsKey(station.GUID))
        {
          MixingStationExtensions.MixingConfig[station.GUID] = config;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: Registered MixingConfig for station: {station.GUID}");
        }
        if (supplyJToken != null && supplyJToken["ObjectGUID"] != null)
        {
          if (!MixingStationExtensions.Supply.ContainsKey(station.GUID))
          {
            MixingStationExtensions.Supply[station.GUID] = new ObjectField(config)
            {
              TypeRequirements = new(),
              DrawTransitLine = true
            };
            MixingStationExtensions.Supply[station.GUID].onObjectChanged.RemoveAllListeners();
            MixingStationExtensions.Supply[station.GUID].onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>((BuildableItem item) => MixingSupplySourceChanged(config, station, item)));
          }
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: supplyJToken[ObjectGUID].ToString(): {supplyJToken["ObjectGUID"].ToString()}");
          MixingStationExtensions.Supply[station.GUID].Load(new(supplyJToken["ObjectGUID"].ToString()));
          if (MixingStationExtensions.Supply[station.GUID].SelectedObject != null)
            MixingStationExtensions.SourceChanged(config, MixingStationExtensions.Supply[station.GUID].SelectedObject);
          else
            MelonLogger.Warning($"MixingStationLoaderPatch: Supply.SelectedObject is null for station: {station.GUID}");
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: Loaded Supply for station: {station.GUID}");
        }
        else
        {
          MelonLogger.Warning($"MixingStationLoaderPatch: Supply data is null or missing ObjectGUID in config for mainPath: {mainPath}");
        }
        List<MixingRoute> routes = new();
        var array = mixingRoutesJToken?.TryCast<JArray>();
        if (array != null && array.Count > 0)
        {
          for (int i = 0; i < array.Count; i++)
          {
            var route = new MixingRoute(config);
            MixingRouteData data = new();
            var routeData = mixingRoutesJToken[i];
            if (routeData["Product"] != null)
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Msg($"MixingStationLoaderPatch: Loading Product={routeData["Product"].ToString()}");
              if (string.IsNullOrEmpty((string)routeData["Product"])) data.Product = null;
              else data.Product = new(routeData["Product"].ToString());
            }
            if (routeData["MixerItem"] != null)
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
                MelonLogger.Msg($"MixingStationLoaderPatch: Loading Mixer={routeData["MixerItem"].ToString()}");
              if (string.IsNullOrEmpty((string)routeData["MixerItem"])) data.MixerItem = null;
              else data.MixerItem = new(routeData["MixerItem"].ToString());
            }
            route.SetData(data);
            routes.Add(route);
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
              MelonLogger.Msg($"MixingStationLoaderPatch: Loaded route Product={route.Product.SelectedItem?.Name ?? "null"}, MixerItem={route.MixerItem.SelectedItem?.Name ?? "null"} for station={station.GUID}");
          }
          MixingStationExtensions.MixingRoutes[station.GUID] = routes;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: Loaded {routes.Count} MixingRoutes for station={station.GUID}");
        }
        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }
}