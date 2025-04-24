using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using TMPro;
using UnityEngine.UI;
using ScheduleOne.EntityFramework;
using ScheduleOne.Persistence.Loaders;

namespace NoLazyWorkers.Chemists
{
  public static class MixingStationExtensions
  {
    public static Dictionary<MixingStation, ObjectField> MixingSupply = [];
    public static Dictionary<MixingStation, MixingStationConfiguration> MixingConfig = [];
    public static Dictionary<MixingStation, TransitRoute> MixingSourceRoute = [];
    public static Dictionary<MixingStation, List<MixingRoute>> MixingRoutes = [];
    public static GameObject MixingRouteListTemplate { get; set; }


    public static void SourceChanged(this MixingStationConfiguration mixerConfig, TransitRoute SourceRoute, ObjectField Supply, MixingStation station)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Called for MixerConfig: {mixerConfig}, Station: {station?.name ?? "null"}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
        if (mixerConfig == null || station == null)
        {
          MelonLogger.Error($"SourceChanged(MixingStation): MixerConfig or Station is null");
          return;
        }
        if (!MixingSupply.ContainsKey(station))
        {
          MixingSupply[station] = Supply; // Register if missing
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): Registered missing MixerSupply for MixerConfig: {mixerConfig}");
        }

        if (SourceRoute != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Destroying existing SourceRoute");
          SourceRoute.Destroy();
          MixingSourceRoute[station] = null;
        }

        if (Supply.SelectedObject != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Creating new TransitRoute from {Supply.SelectedObject.name} to MixingStation");
          SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, station);
          MixingSourceRoute[station] = SourceRoute;
          if (station.Configuration.IsSelected)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Station is selected, enabling TransitRoute visuals");
            SourceRoute.SetVisualsActive(true);
          }
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Supply.SelectedObject is null, setting SourceRoute to null");
          MixingSourceRoute[station] = null;
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
        // Check if station is registered
        if (!MixingSupply.ContainsKey(station))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): MixerSupply does not contain key for station: {station}");
          return;
        }
        if (!MixingSourceRoute.ContainsKey(station))
        {
          MixingSourceRoute[station] = null; // Initialize if missing
        }

        TransitRoute SourceRoute = MixingSourceRoute[station];
        if (SourceRoute != null)
        {
          SourceRoute.Destroy();
          MixingSourceRoute[station] = null;
        }

        ObjectField Supply = MixingSupply[station];
        if (Supply.SelectedObject != null)
        {
          SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, station);
          MixingSourceRoute[station] = SourceRoute;
          if (station.Configuration.IsSelected)
          {
            SourceRoute.SetVisualsActive(true);
            return;
          }
        }
        else
        {
          MixingSourceRoute[station] = null;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Updated for MixerConfig: {mixerConfig}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"SourceChanged(MixingStation): Failed for MixerConfig: {mixerConfig}, error: {e}");
      }
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
            if (!MixingConfig.ContainsKey(station))
            {
              MixingConfig[station] = mixerConfig;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                MelonLogger.Warning($"RestoreConfigurations: Registered missing MixerConfig for station: {station.name}");
            }
            // No need to restore Supply or MixingRoutes here, as they are loaded directly in MixingStationLoaderPatch
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
              MelonLogger.Msg($"RestoreConfigurations: Registered configuration for station: {station.name}");
          }
        }
        catch (Exception e)
        {
          MelonLogger.Error($"RestoreConfigurations: Failed to restore configuration for station: {station?.name ?? "null"}, error: {e}");
        }
      }
    }

    public static void InitializeStaticTemplate(RouteListFieldUI routeListTemplate)
    {
      if (routeListTemplate == null)
      {
        MelonLogger.Error("MixingStationConfigPanelBindPatch: routeListTemplate is null");
        return;
      }
      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Initializing MixingRouteListTemplate");

        // Instantiate the template
        GameObject templateObj = UnityEngine.Object.Instantiate(routeListTemplate.gameObject);
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);

        // Replace RouteListFieldUI with MixingRouteListFieldUI
        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
          UnityEngine.Object.Destroy(defaultScript);
        if (templateObj.GetComponent<MixingRouteListFieldUI>() == null)
          templateObj.AddComponent<MixingRouteListFieldUI>();

        // Update AddNew label
        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";

        // Get the Entry template from the config panel
        Transform entryTemplate = NoLazyUtilities.GetTransformTemplateFromConfigPanel(EConfigurableType.Packager, "RouteListFieldUI/Contents/Entry");
        if (entryTemplate == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: Failed to retrieve Entry template from Packager config panel");
          return;
        }

        // Replace RouteEntryUI with MixingRouteEntryUI for all entries
        var contents = templateObj.transform.Find("Contents");
        for (int i = 0; i < MixingRouteListFieldUI.MaxRoutes; i++)
        {
          Transform entry = contents.Find($"Entry ({i})") ?? (i == 0 ? contents.Find("Entry") : null);
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Processing Entry ({i})");

          if (entry == null)
          {
            // Instantiate new entry from the full template
            GameObject newEntry = UnityEngine.Object.Instantiate(entryTemplate.gameObject, contents, false);
            newEntry.name = $"Entry ({i})";
            newEntry.AddComponent<CanvasRenderer>();
            entry = newEntry.transform;

            // Rename Source and Destination
            var productTransform = entry.Find("Source");
            if (productTransform != null)
              productTransform.name = "ProductIMGUI";
            else
              MelonLogger.Warning($"MixingStationConfigPanelBindPatch: Source not found in Entry ({i})");

            var mixerTransform = entry.Find("Destination");
            if (mixerTransform != null)
              mixerTransform.name = "MixerItemIMGUI";
            else
              MelonLogger.Warning($"MixingStationConfigPanelBindPatch: Destination not found in Entry ({i})");

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

        contents.Find("AddNew").SetAsLastSibling();
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: SetAsLastSibling");

        // Hide unnecessary elements
        templateObj.transform.Find("Title")?.gameObject.SetActive(false);
        templateObj.transform.Find("From")?.gameObject.SetActive(false);
        templateObj.transform.Find("To")?.gameObject.SetActive(false);

        MixingStationExtensions.MixingRouteListTemplate = templateObj;
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Static MixingRouteListTemplate initialized successfully");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Failed to initialize MixingRouteListTemplate, error: {e}");
      }
    }
  }
  public static class SelectionHelper
  {
    public static Action<ProductDefinition> ProductSelectionCallback { get; set; }
  }

  [Serializable]
  public class ExtendedMixingStationConfigurationData : MixingStationConfigurationData
  {
    public ObjectFieldData Supply;
    public string MixingRoutes;

    public ExtendedMixingStationConfigurationData(ObjectFieldData destination, NumberFieldData threshold)
        : base(destination, threshold)
    {
      Supply = null;
      MixingRoutes = "[]";
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
      };
      MixerItem = new ItemField(config)
      {
        CanSelectNone = false,
        Options = [.. NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.Cast<ItemDefinition>()]
      };
    }

    public void SetData(MixingRouteData data)
    {
      Product.Load(data.Product);
      MixerItem.Load(data.MixerItem);
    }
  }

  [Serializable]
  public class MixingRouteData
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData(ItemFieldData product, ItemFieldData mixerItem)
    {
      Product = product;
      MixerItem = mixerItem;
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration))]
  public class MixingStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation) })]
    static void Postfix(MixingStationConfiguration __instance, MixingStation station)
    {
      try
      {
        __instance.StartThrehold.Configure(1f, 20f, wholeNumbers: true);

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Initializing for station: {station?.ObjectId.ToString() ?? "null"}, configHash={__instance.GetHashCode()}");

        ObjectField Supply = new(__instance)
        {
          TypeRequirements = [typeof(PlaceableStorageEntity)],
          DrawTransitLine = true
        };
        Supply.onObjectChanged.RemoveAllListeners();
        Supply.onObjectChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"MixingStationConfigurationPatch: Supply changed for station {station?.ObjectId.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.ObjectId.ToString() ?? "null"}");
        });
        Supply.onObjectChanged.AddListener(item => MixingStationExtensions.SourceChanged(__instance, item));
        MixingStationExtensions.MixingSupply[station] = Supply;

        MixingStationExtensions.MixingConfig[station] = __instance;

        if (!MixingStationExtensions.MixingRoutes.ContainsKey(station))
        {
          MixingStationExtensions.MixingRoutes[station] = [];
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Registered supply and config for station: {station?.ObjectId.ToString() ?? "null"}, supplyHash={Supply.GetHashCode()}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationPatch: Failed for station: {station?.ObjectId.ToString() ?? "null"}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration), "GetSaveString")]
  public class MixingStationConfigurationGetSaveStringPatch
  {
    static void Postfix(MixingStationConfiguration __instance, ref string __result)
    {
      try
      {
        // Manually serialize MixingRoutes as a comma-separated string of JSON objects
        string mixingRoutesJson = "";
        if (MixingStationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes) && routes.Any())
        {
          List<string> routeJsonList = new List<string>();
          foreach (var route in routes)
          {
            string productItemID = route.Product.GetData()?.ItemID ?? "null";
            string mixerItemID = route.MixerItem.GetData()?.ItemID ?? "null";
            string routeJson = $"{{\"Product\":\"{productItemID}\",\"MixerItem\":\"{mixerItemID}\"}}";
            routeJsonList.Add(routeJson);
          }
          mixingRoutesJson = string.Join(",", routeJsonList);
        }

        // Create the config data
        ExtendedMixingStationConfigurationData data = new(
            __instance.Destination.GetData(),
            __instance.StartThrehold.GetData()
        )
        {
          Supply = MixingStationExtensions.MixingSupply[__instance.station].GetData(),
          MixingRoutes = mixingRoutesJson
        };

        __result = JsonUtility.ToJson(data, true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
        {
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Saved JSON for station={__instance.station.ObjectId}: {__result}");
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Routes JSON={mixingRoutesJson}");
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
        MelonLogger.Error($"MixingStationConfigurationGetSaveStringPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration), "ShouldSave")]
  public class MixingStationConfigurationShouldSavePatch
  {
    static void Postfix(MixingStationConfiguration __instance, ref bool __result)
    {
      try
      {
        ObjectField supply = MixingStationExtensions.MixingSupply[__instance.station];
        bool hasRoutes = MixingStationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes) &&
                        routes.Any(r => r.Product.SelectedItem != null || r.MixerItem.SelectedItem != null);
        __result |= supply.SelectedObject != null || hasRoutes;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationShouldSavePatch failed: {e}");
      }
    }
  }

  public class MixingRouteListFieldUI : MonoBehaviour
  {
    public string FieldText = "Recipes";
    public TextMeshProUGUI FieldLabel;
    public MixingRouteEntryUI[] RouteEntries;
    public RectTransform MultiEditBlocker;
    public Button AddButton;
    public static readonly int MaxRoutes = 7;
    private List<List<MixingRoute>> RoutesLists; // List of route lists for each configuration
    private List<MixingStationConfiguration> Configs; // Corresponding configurations
    private UnityAction OnChanged;

    private void Start()
    {
      if (MixingStationExtensions.MixingRouteListTemplate != null)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: MixingRouteListTemplate already initialized, skipping");
      }
      else
      {
        // Retrieve and initialize the template
        RouteListFieldUI routeListTemplate = NoLazyUtilities.GetComponentTemplateFromConfigPanel(
            EConfigurableType.Packager,
            panel => panel.GetComponentInChildren<RouteListFieldUI>());
        if (routeListTemplate == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Error("OnSceneWasLoaded: Failed to retrieve RouteListFieldUI template");
        }
        MixingStationExtensions.InitializeStaticTemplate(routeListTemplate);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Warning("OnSceneWasLoaded: SetupConfigPanelsComplete failed");
        else if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Warning("OnSceneWasLoaded: SetupConfigPanelsComplete success");
      }

      FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
      if (FieldLabel != null) FieldLabel.text = FieldText;
      MultiEditBlocker = transform.Find("Blocker")?.GetComponent<RectTransform>();
      MultiEditBlocker?.gameObject.SetActive(false);
      AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
      if (AddButton != null) AddButton.onClick.AddListener(AddClicked);

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteListFieldUI: Start completed, awaiting Bind");
    }

    public void Bind(List<List<MixingRoute>> routesLists, List<MixingStationConfiguration> configs = null, UnityAction onChangedCallback = null)
    {
      RoutesLists = routesLists?.Select(list => list ?? new List<MixingRoute>()).ToList() ?? new List<List<MixingRoute>>();
      Configs = configs ?? new List<MixingStationConfiguration>();
      OnChanged = onChangedCallback;

      // Initialize RouteEntries
      RouteEntries = transform.Find("Contents").GetComponentsInChildren<MixingRouteEntryUI>(true);
      if (RouteEntries == null || RouteEntries.Length == 0)
      {
        MelonLogger.Error("MixingRouteListFieldUI: No RouteEntries found in Contents");
        return;
      }

      // Set up onDeleteClicked listeners
      for (int i = 0; i < RouteEntries.Length; i++)
      {
        int index = i;
        RouteEntries[i].onDeleteClicked.RemoveAllListeners();
        RouteEntries[i].onDeleteClicked.AddListener(() => EntryDeleteClicked(index));
      }

      // Set up change listeners for each MixingRoute
      foreach (var routes in RoutesLists)
      {
        foreach (var route in routes)
        {
          route.Product.onItemChanged.RemoveAllListeners();
          route.MixerItem.onItemChanged.RemoveAllListeners();
          route.Product.onItemChanged.AddListener(item => Refresh());
          route.MixerItem.onItemChanged.AddListener(item => Refresh());
        }
      }

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Bind completed, RoutesLists count={RoutesLists.Count}, RouteEntries count={RouteEntries.Length}");
      Refresh();
    }

    private void Refresh()
    {
      if (RoutesLists == null || RoutesLists.Count == 0)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning("MixingRouteListFieldUI: Refresh skipped due to null or empty RoutesLists");
        for (int i = 0; i < RouteEntries.Length; i++)
        {
          RouteEntries[i].gameObject.SetActive(false);
        }
        if (AddButton != null)
          AddButton.gameObject.SetActive(false);
        return;
      }

      // Determine the maximum number of routes across all configurations
      int maxRouteCount = RoutesLists.Max(list => list.Count);

      // Bind RouteEntries to the corresponding MixingRoute from each configuration
      for (int i = 0; i < RouteEntries.Length; i++)
      {
        if (i < maxRouteCount)
        {
          RouteEntries[i].gameObject.SetActive(true);

          // For multi-selection, bind to the first configuration's route if available
          // If configs differ, UI will show the first valid route or a placeholder
          MixingRoute routeToBind = null;
          MixingStationConfiguration configToBind = null;
          for (int j = 0; j < RoutesLists.Count; j++)
          {
            if (i < RoutesLists[j].Count)
            {
              routeToBind = RoutesLists[j][i];
              configToBind = Configs.ElementAtOrDefault(j);
              break; // Use the first valid route
            }
          }

          if (routeToBind != null && configToBind != null)
          {
            RouteEntries[i].Bind(configToBind, routeToBind);
          }
          else
          {
            // Disable entry if no valid route is available
            RouteEntries[i].gameObject.SetActive(false);
          }
        }
        else
        {
          RouteEntries[i].gameObject.SetActive(false);
        }
      }

      // Enable AddButton only if all configurations can add more routes
      if (AddButton != null)
      {
        bool canAdd = RoutesLists.All(list => list.Count < MaxRoutes);
        AddButton.gameObject.SetActive(canAdd);
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): AddButton active={AddButton.gameObject.activeSelf}");
      }
    }

    private void AddClicked()
    {
      if (RoutesLists.All(list => list.Count < MaxRoutes))
      {
        for (int i = 0; i < RoutesLists.Count; i++)
        {
          if (Configs.ElementAtOrDefault(i) != null)
          {
            RoutesLists[i].Add(new MixingRoute(Configs[i]));
          }
        }
        Refresh();
        OnChanged?.Invoke();
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
      }
    }
  }

  public class MixingRouteEntryUI : MonoBehaviour
  {
    public TextMeshProUGUI ProductLabel;
    public TextMeshProUGUI MixerLabel;
    public Button ProductButton;
    public Button MixerButton;
    public UnityEvent onDeleteClicked = new UnityEvent();
    public MixingRoute Route;
    public MixingStationConfiguration Config;

    private void Awake()
    {
      ProductLabel = transform.Find("ProductIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
      MixerLabel = transform.Find("MixerItemIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
      ProductButton = transform.Find("ProductIMGUI")?.GetComponent<Button>();
      MixerButton = transform.Find("MixerItemIMGUI")?.GetComponent<Button>();
      transform.Find("Remove")?.GetComponent<Button>()?.onClick.AddListener(DeleteClicked);
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route)
    {
      Config = config;
      Route = route;
      ProductLabel.text = Route.Product?.SelectedItem?.Name ?? "Product";
      MixerLabel.text = Route.MixerItem?.SelectedItem?.Name ?? "Mixer";

      ProductButton?.onClick.RemoveAllListeners();
      MixerButton?.onClick.RemoveAllListeners();
      ProductButton?.onClick.AddListener(ProductClicked);
      MixerButton?.onClick.AddListener(MixerClicked);

      Route.Product.onItemChanged.RemoveAllListeners();
      Route.MixerItem.onItemChanged.RemoveAllListeners();
      Route.Product.onItemChanged.AddListener(item =>
      {
        ProductLabel.text = item?.Name ?? "Product";
        ConfigurationExtensions.InvokeChanged(Config);
      });
      Route.MixerItem.onItemChanged.AddListener(item =>
      {
        MixerLabel.text = item?.Name ?? "Mixer";
        ConfigurationExtensions.InvokeChanged(Config);
      });

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteEntryUI: Bound Route={Route}, Config={Config}");
    }

    public void ProductClicked()
    {
      if (Route == null || Config == null)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning("MixingRouteEntryUI: ProductClicked ignored due to null Route or Config");
        return;
      }
      Route.Product.Options = [.. ProductManager.FavouritedProducts.Cast<ItemDefinition>()];
      OpenItemSelectorScreen(Route.Product, "Favorites", ProductLabel);
    }

    public void MixerClicked()
    {
      if (Route == null || Config == null)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning("MixingRouteEntryUI: MixerClicked ignored due to null Route or Config");
        return;
      }
      OpenItemSelectorScreen(Route.MixerItem, "Mixers", MixerLabel);
    }

    public void DeleteClicked() { onDeleteClicked.Invoke(); }

    private void OpenItemSelectorScreen(ItemField itemField, string fieldName, TextMeshProUGUI label)
    {
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteEntryUI: OpenItemSelectorScreen");

      List<ItemSelector.Option> list = [];
      ItemSelector.Option selectedOption = null;

      if (itemField.CanSelectNone)
      {
        list.Add(new ItemSelector.Option("None", null));
        if (itemField.SelectedItem == null)
          selectedOption = list[0];
      }
      else
      {
        ItemSelector.Option none = new ItemSelector.Option("None", null);
        if (list.IndexOf(none) != -1)
        {
          list.Remove(none);
        }
      }

      foreach (var option in itemField.Options)
      {
        var opt = new ItemSelector.Option(option.Name, option);
        list.Add(opt);
        if (itemField.SelectedItem == option)
          selectedOption = opt;
      }

      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, list, selectedOption, (selected) =>
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteEntryUI: ItemSelectorScreen selected {selected?.Item?.Name ?? "null"} for {fieldName}");
        itemField.SelectedItem = selected.Item;
        label.text = selected.Item.name;
      });
      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Open();
    }
  }

  [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
  public class MixingStationConfigPanelBindPatch
  {
    static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Binding configs, count: {configs?.Count ?? 0}");

        if (__instance == null || __instance.DestinationUI == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: __instance or DestinationUI is null");
          return;
        }

        // Clean up existing UI elements
        foreach (Transform child in __instance.transform)
        {
          if (child.name == "SupplyUI" || child.name == "RouteListFieldUI")
            UnityEngine.Object.Destroy(child.gameObject);
        }

        if (MixingStationExtensions.MixingRouteListTemplate == null)
        {
          RouteListFieldUI routeListTemplate = NoLazyUtilities.GetComponentTemplateFromConfigPanel(
              EConfigurableType.Packager,
              panel => panel.GetComponentInChildren<RouteListFieldUI>());
          if (routeListTemplate == null)
          {
            MelonLogger.Error("MixingStationConfigPanelBindPatch: Failed to retrieve RouteListFieldUI template");
            return;
          }
          MixingStationExtensions.InitializeStaticTemplate(routeListTemplate);
        }

        // Instantiate UI objects
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = $"SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        GameObject routeListUIObj = UnityEngine.Object.Instantiate(MixingStationExtensions.MixingRouteListTemplate, __instance.transform, false);
        routeListUIObj.name = $"RouteListFieldUI";
        routeListUIObj.SetActive(true);
        var customRouteListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();

        List<ObjectField> supplyList = new();
        List<List<MixingRoute>> routesLists = new();
        List<MixingStationConfiguration> configList = new();

        // Bind all selected stations
        foreach (var config in configs.OfType<MixingStationConfiguration>())
        {
          // Supply UI setup
          foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title") child.text = "Supplies";
            else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
          }
          if (MixingStationExtensions.MixingSupply.TryGetValue(config.station, out ObjectField supply))
          {
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"PotConfigPanelBindPatch: Before Bind, pot: {config.station.GUID}, SelectedObject: {supply.SelectedObject?.name ?? "null"}");
            supplyList.Add(supply);
          }
          else
          {
            MelonLogger.Warning($"PotConfigPanelBindPatch: No supply found for PotConfiguration, pot: {config.station.GUID}");
          }

          // Destination UI update
          foreach (TextMeshProUGUI child in __instance.DestinationUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title") child.text = "Destination";
            else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
          }

          // Collect routes and configurations
          if (!MixingStationExtensions.MixingRoutes.ContainsKey(config.station))
            MixingStationExtensions.MixingRoutes[config.station] = [];
          var routes = MixingStationExtensions.MixingRoutes[config.station];
          routesLists.Add(routes);
          configList.Add(config);
        }

        // Bind UI components
        customRouteListUI.Bind(routesLists, configList, () => configs.ForEach(c => ConfigurationExtensions.InvokeChanged(c)));
        supplyUI.Bind(supplyList);

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
    [Serializable]
    private class SerializableRouteData
    {
      public string Product;
      public string MixerItem;
    }

    static void Postfix(string mainPath)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Msg($"MixingStationLoaderPatch: Processing Postfix for mainPath: {mainPath}");
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Warning($"MixingStationLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem is not MixingStation station)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Warning($"MixingStationLoaderPatch: GridItem is not a MixingStation for mainPath: {mainPath}, type: {gridItem.GetType().Name}");
          return;
        }
        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Warning($"MixingStationLoaderPatch: No Configuration.json found at: {configPath}");
          return;
        }
        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Warning($"MixingStationLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
          return;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationLoaderPatch: Loaded JSON: {text}");
        ExtendedMixingStationConfigurationData configData = JsonUtility.FromJson<ExtendedMixingStationConfigurationData>(text);
        if (configData == null)
        {
          MelonLogger.Error($"MixingStationLoaderPatch: Failed to deserialize Configuration.json for mainPath: {mainPath}");
          return;
        }
        // Parse MixingRoutes
        List<MixingRouteData> mixingRoutes = [];
        if (!string.IsNullOrEmpty(configData.MixingRoutes))
        {
          try
          {
            // Split the MixingRoutes string into individual JSON objects
            // Use a simple split, accounting for commas outside of quotes
            List<string> routeJsonList = new List<string>();
            int braceLevel = 0;
            int startIndex = 0;
            for (int i = 0; i < configData.MixingRoutes.Length; i++)
            {
              char c = configData.MixingRoutes[i];
              if (c == '{') braceLevel++;
              else if (c == '}') braceLevel--;
              else if (c == ',' && braceLevel == 0)
              {
                routeJsonList.Add(configData.MixingRoutes.Substring(startIndex, i - startIndex));
                startIndex = i + 1;
              }
            }
            if (startIndex < configData.MixingRoutes.Length)
            {
              routeJsonList.Add(configData.MixingRoutes.Substring(startIndex));
            }

            foreach (string routeJson in routeJsonList)
            {
              if (!string.IsNullOrWhiteSpace(routeJson))
              {
                SerializableRouteData routeData = JsonUtility.FromJson<SerializableRouteData>(routeJson);
                if (routeData != null)
                {
                  mixingRoutes.Add(new MixingRouteData(
                      routeData.Product != null ? new ItemFieldData(routeData.Product == "null" ? null : routeData.Product) : null,
                      routeData.MixerItem != null ? new ItemFieldData(routeData.MixerItem == "null" ? null : routeData.MixerItem) : null
                  ));
                }
              }
            }
          }
          catch (Exception e)
          {
            MelonLogger.Error($"MixingStationLoaderPatch: Failed to parse MixingRoutes JSON for station={station.ObjectId}, error: {e}");
          }
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
        {
          MelonLogger.Msg($"MixingStationLoaderPatch: Deserialized configData for station={station.ObjectId}");
          MelonLogger.Msg($"MixingStationLoaderPatch: Supply={configData.Supply?.ToString() ?? "null"}");
          MelonLogger.Msg($"MixingStationLoaderPatch: MixingRoutes count={mixingRoutes.Count}");
          foreach (var route in mixingRoutes)
          {
            MelonLogger.Msg($"MixingStationLoaderPatch: Route Product.ItemID={route.Product?.ItemID ?? "null"}, MixerItem.ItemID={route.MixerItem?.ItemID ?? "null"}");
          }
        }
        MixingStationConfiguration config = MixingStationExtensions.MixingConfig.TryGetValue(station, out var cfg)
            ? cfg
            : station.Configuration as MixingStationConfiguration;
        if (config == null)
        {
          MelonLogger.Error($"MixingStationLoaderPatch: No valid MixingStationConfiguration for station: {station.ObjectId}");
          return;
        }
        if (!MixingStationExtensions.MixingConfig.ContainsKey(station))
        {
          MixingStationExtensions.MixingConfig[station] = config;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"MixingStationLoaderPatch: Registered MixingConfig for station: {station.ObjectId}");
        }
        if (configData.Supply != null)
        {
          if (!MixingStationExtensions.MixingSupply.ContainsKey(station))
          {
            MixingStationExtensions.MixingSupply[station] = new ObjectField(config)
            {
              TypeRequirements = [typeof(PlaceableStorageEntity)],
              DrawTransitLine = true
            };
            MixingStationExtensions.MixingSupply[station].onObjectChanged.RemoveAllListeners();
            MixingStationExtensions.MixingSupply[station].onObjectChanged.AddListener(delegate
            {
              ConfigurationExtensions.InvokeChanged(config);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
                MelonLogger.Msg($"MixingStationLoaderPatch: Supply changed for station {station.ObjectId}, newSupply={MixingStationExtensions.MixingSupply[station].SelectedObject?.ObjectId.ToString() ?? "null"}");
            });
            MixingStationExtensions.MixingSupply[station].onObjectChanged.AddListener(item => MixingStationExtensions.SourceChanged(config, item));
          }
          MixingStationExtensions.MixingSupply[station].Load(configData.Supply);
          MixingStationExtensions.SourceChanged(config, MixingStationExtensions.MixingSupply[station].SelectedObject);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"MixingStationLoaderPatch: Loaded Supply for station: {station.ObjectId}");
        }
        else
        {
          MelonLogger.Warning($"MixingStationLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
        }
        if (mixingRoutes.Count > 0)
        {
          List<MixingRoute> routes = [];
          foreach (var routeData in mixingRoutes)
          {
            if (routeData.Product != null && routeData.MixerItem != null)
            {
              var route = new MixingRoute(config);
              if (routeData.Product.ItemID == "null") route.Product.SelectedItem = null;
              else route.Product.Load(routeData.Product);
              if (routeData.MixerItem.ItemID == "null") route.MixerItem.SelectedItem = null;
              else route.MixerItem.Load(routeData.MixerItem);
              routes.Add(route);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
                MelonLogger.Msg($"MixingStationLoaderPatch: Loaded route Product={route.Product.SelectedItem?.Name ?? "null"}, MixerItem={route.MixerItem.SelectedItem?.Name ?? "null"} for station={station.ObjectId}");
            }
            else
            {
              MelonLogger.Warning($"MixingStationLoaderPatch: Skipping route with null Product or MixerItem for station={station.ObjectId}");
            }
          }
          MixingStationExtensions.MixingRoutes[station] = routes;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"MixingStationLoaderPatch: Loaded {routes.Count} MixingRoutes for station={station.ObjectId}");
        }
        else
        {
          MixingStationExtensions.MixingRoutes[station] = [];
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"MixingStationLoaderPatch: No MixingRoutes found in config for station: {station.ObjectId}");
        }
        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Msg($"MixingStationLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }


  [HarmonyPatch(typeof(MixingStationConfiguration), "Destroy")]
  public class MixingStationConfigurationDestroyPatch
  {
    static void Postfix(MixingStationConfiguration __instance)
    {
      try
      {
        MixingStationExtensions.MixingSupply.Remove(__instance.station);
        MixingStationExtensions.MixingRoutes.Remove(__instance.station);
        foreach (var pair in MixingStationExtensions.MixingConfig.Where(p => p.Value == __instance).ToList())
        {
          MixingStationExtensions.MixingConfig.Remove(pair.Key);
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }
}