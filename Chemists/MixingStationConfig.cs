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

namespace NoLazyWorkers_IL2CPP.Chemists
{
  public static class MixingStationExtensions
  {
    public static Dictionary<Il2CppSystem.Guid, ObjectField> MixingSupply = [];
    public static Dictionary<Il2CppSystem.Guid, MixingStationConfiguration> MixingConfig = [];
    public static Dictionary<Il2CppSystem.Guid, TransitRoute> MixingSourceRoute = [];
    public static Dictionary<Il2CppSystem.Guid, List<MixingRoute>> MixingRoutes = [];
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
        if (!MixingSupply.ContainsKey(station.GUID))
        {
          MixingSupply[station.GUID] = Supply; // Register if missing
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): Registered missing MixerSupply for MixerConfig: {mixerConfig}");
        }

        if (SourceRoute != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Destroying existing SourceRoute");
          SourceRoute.Destroy();
          MixingSourceRoute[station.GUID] = null;
        }

        if (Supply.SelectedObject != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Creating new TransitRoute from {Supply.SelectedObject.name} to MixingStation");
          SourceRoute = new TransitRoute(Supply.SelectedObject.Cast<ITransitEntity>(), station.Cast<ITransitEntity>());
          MixingSourceRoute[station.GUID] = SourceRoute;
          if (station.Configuration.IsSelected)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Station is selected, enabling TransitRoute visuals");
            SourceRoute.SetVisualsActive(true);
          }
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Supply.SelectedObject is null, setting SourceRoute to null");
          MixingSourceRoute[station.GUID] = null;
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
        if (!MixingSupply.ContainsKey(station.GUID))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): MixerSupply does not contain key for station: {station}");
          return;
        }
        if (!MixingSourceRoute.ContainsKey(station.GUID))
        {
          MixingSourceRoute[station.GUID] = null; // Initialize if missing
        }

        TransitRoute SourceRoute = MixingSourceRoute[station.GUID];
        if (SourceRoute != null)
        {
          SourceRoute.Destroy();
          MixingSourceRoute[station.GUID] = null;
        }

        ObjectField Supply = MixingSupply[station.GUID];
        if (Supply.SelectedObject != null)
        {
          SourceRoute = new TransitRoute(Supply.SelectedObject.Cast<ITransitEntity>(), station.Cast<ITransitEntity>());
          MixingSourceRoute[station.GUID] = SourceRoute;
          if (station.Configuration.IsSelected)
          {
            SourceRoute.SetVisualsActive(true);
            return;
          }
        }
        else
        {
          MixingSourceRoute[station.GUID] = null;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Updated for MixerConfig: {mixerConfig}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
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
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
        MelonLogger.Msg($"MixingStationConfigurationPatch: Supply changed for station {station?.ObjectId.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.ObjectId.ToString() ?? "null"}");
    }

    public static void RestoreConfigurations()
    {
      // Restore MixingStation configurations
      InitializeStaticRouteListTemplate();
      MixingStation[] mixingStations = UnityEngine.Object.FindObjectsOfType<MixingStation>();
      foreach (MixingStation station in mixingStations)
      {
        try
        {
          if (station.Configuration is MixingStationConfiguration mixerConfig)
          {
            // Register MixingConfig
            if (!MixingConfig.ContainsKey(station.GUID))
            {
              MixingConfig[station.GUID] = mixerConfig;
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"RestoreConfigurations: Registered MixingConfig for station: {station.ObjectId}");
            }

            // Initialize MixingSupply if missing
            if (!MixingSupply.ContainsKey(station.GUID))
            {
              MixingSupply[station.GUID] = new ObjectField(mixerConfig)
              {
                TypeRequirements = new(),
                DrawTransitLine = true
              };
              MixingSupply[station.GUID].onObjectChanged.RemoveAllListeners();
              MixingSupply[station.GUID].onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>(SupplyOnObjectChangedInvoke));
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"RestoreConfigurations: Initialized MixingSupply for station: {station.ObjectId}");
            }

            // Initialize MixingRoutes if missing
            if (!MixingRoutes.ContainsKey(station.GUID))
            {
              MixingRoutes[station.GUID] = new List<MixingRoute>();
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"RestoreConfigurations: Initialized MixingRoutes for station: {station.ObjectId}");
            }
          }
          else
          {
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"RestoreConfigurations: Skipped station {station.ObjectId}, no MixingStationConfiguration");
          }
        }
        catch (Exception e)
        {
          MelonLogger.Error($"RestoreConfigurations: Failed for station: {station?.ObjectId.ToString() ?? "null"}, error: {e}");
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
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: Initializing MixingRouteListTemplate");

        // Instantiate the template
        var templateObj = UnityEngine.Object.Instantiate(routeListTemplate.gameObject);
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);
        try
        {
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("InitializeStaticRouteListTemplate: Adding MixingRouteListFieldUI via Il2CppType");
          var routeListFieldUI = templateObj.AddComponent<MixingRouteListFieldUI>();
        }
        catch (Exception e)
        {
          MelonLogger.Error($"InitializeStaticRouteListTemplate: Failed to add MixingRouteListFieldUI via Il2CppType, error: {e}");
        }
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: MixingRouteListFieldUI added");

        // Replace RouteListFieldUI with MixingRouteListFieldUI
        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
          UnityEngine.Object.Destroy(defaultScript);
        if (templateObj.GetComponent<MixingRouteListFieldUI>() == null)
          templateObj.AddComponent<MixingRouteListFieldUI>();

        // Replace RouteEntryUI with MixingRouteEntryUI for all entries
        var contentsTransform = templateObj.transform.Find("Contents");
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: contentsTransform: {contentsTransform?.name}");

        // Prepare newEntry transform
        var entryTransform = templateObj.transform.Find("Contents/Entry");
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: entryTransform: {entryTransform?.name}");

        // Update AddNew label
        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: addNewLabel?.text: {addNewLabel?.text}");

        for (int i = 0; i < MixingRouteListFieldUI.MaxRoutes; i++)
        {
          Transform entry = contentsTransform.Find($"Entry ({i})") ?? (i == 0 ? contentsTransform.Find("Entry") : null);
          if (DebugConfig.EnableDebugLogs)
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
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: SetAsLastSibling");

        // Hide unnecessary elements
        templateObj.transform.Find("Title")?.gameObject.SetActive(false);
        templateObj.transform.Find("From")?.gameObject.SetActive(false);
        templateObj.transform.Find("To")?.gameObject.SetActive(false);

        MixingRouteListTemplate = templateObj;
        if (DebugConfig.EnableDebugLogs)
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
  public class ExtendedMixingStationConfigurationData : MixingStationConfigurationData
  {
    public ObjectFieldData Supply;

    public ExtendedMixingStationConfigurationData(ObjectFieldData destination, NumberFieldData threshold, ObjectFieldData supply)
        : base(destination, threshold)
    {
      Supply = supply;
    }
  }

  [Serializable]
  public class MixingRouteDataWrapper : MonoBehaviour
  {
    public MixingRouteData[] Routes;
  }

  [Serializable]
  public class MixingRouteData : MonoBehaviour
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData(ItemFieldData product, ItemFieldData mixerItem)
    {
      Product = product;
      MixerItem = mixerItem;
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
      MixerItem = new ItemField(config)
      {
        CanSelectNone = false,
        Options = mixerOptions
      };
    }

    public void SetData(MixingRouteData data)
    {
      Product.Load(data.Product);
      MixerItem.Load(data.MixerItem);
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
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: MixingRouteListTemplate already initialized, skipping");
      }
      else
      {
        MixingStationExtensions.InitializeStaticRouteListTemplate();
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
      if (AddButton != null) AddButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(AddClicked));

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteListFieldUI: Start completed, awaiting Bind");
    }

    public void Bind(List<List<MixingRoute>> routesLists, List<MixingStationConfiguration> configs, UnityAction onChangedCallback)
    {
      RoutesLists = routesLists?.Select(list => list ?? []).ToList() ?? [];
      Configs = configs ?? [];
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
        RouteEntries[i].onDeleteClicked.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => EntryDeleteClicked(index)));
        RouteEntries[i].onProductClicked.RemoveAllListeners();
        RouteEntries[i].onProductClicked.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => EntryProductClicked(index)));
        RouteEntries[i].onMixerClicked.RemoveAllListeners();
        RouteEntries[i].onMixerClicked.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => EntryMixerClicked(index)));
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
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteListFieldUI: Bind: step 1");

      // Set up change listeners for each MixingRoute
      foreach (var routes in RoutesLists)
      {
        foreach (var route in routes)
        {
          route.Product.onItemChanged.RemoveAllListeners();
          route.MixerItem.onItemChanged.RemoveAllListeners();
          route.Product.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>(RefreshChanged));
          route.MixerItem.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>(RefreshChanged));
        }
      }

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Bind completed, RoutesLists count={RoutesLists.Count}, RouteEntries count={RouteEntries.Length}");
      Refresh();
    }

    public void RefreshChanged(ItemDefinition item)
    {
      Refresh();
      OnChanged?.Invoke();
    }

    public void Refresh()
    {
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Refresh()");
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
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Refresh() step 1");

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
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Refresh() step 2");

      // Enable AddButton only if all configurations can add more routes
      if (AddButton != null)
      {
        bool canAdd = RoutesLists.All(list => list.Count < MaxRoutes);
        AddButton.gameObject.SetActive(canAdd);
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): AddButton active={AddButton.gameObject.activeSelf}");
      }
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
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"MixingRouteListFieldUI: AddClicked: Route added: {route}");
          }
          else if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("MixingRouteListFieldUI: AddClicked: Configs.ElementAtOrDefault(i) == null");
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

    private void EntryProductClicked(int index)
    {
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Warning($"MixingRouteEntryUI: EntryProductClicked index: {index} RoutesLists: {RoutesLists?.Count}");
      if (RoutesLists != null)
      {
        MixingRoute route = RoutesLists.First()[index];
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning($"MixingRouteEntryUI: EntryProductClicked route: {route} FavouritedProducts: {ProductManager.FavouritedProducts?.Count}");
        var options = new Il2CppSystem.Collections.Generic.List<ItemDefinition>();
        foreach (ProductDefinition item in ProductManager.FavouritedProducts)
        {
          if (item != null)
            options.Add(item);
        }
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: EntryProductClicked {route.Product.SelectedItem?.name} | {options.Count}");
        route.Product.Options = options;
        MixingRouteEntryUI.OpenItemSelectorScreen(route.Product, "Favorites");
      }
      else if (DebugConfig.EnableDebugLogs)
        MelonLogger.Warning($"MixingRouteEntryUI: EntryProductClicked RoutesLists == null");
    }

    private void EntryMixerClicked(int index)
    {
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Warning($"MixingRouteEntryUI: EntryMixerClicked index: {index} RoutesLists: {RoutesLists?.Count}");
      if (RoutesLists != null)
      {
        MixingRoute route = RoutesLists[0][index];
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning($"MixingRouteEntryUI: EntryMixerClicked {route.MixerItem.SelectedItem?.name} | {route.MixerItem.Options.Count}");
        MixingRouteEntryUI.OpenItemSelectorScreen(route.MixerItem, "Mixers");
      }
      else if (DebugConfig.EnableDebugLogs)
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
      ProductLabel.text = item?.Name ?? "Product";
      ConfigurationExtensions.InvokeChanged(Config);
    }
    private void OnMixerItemChanged(ItemDefinition item)
    {
      MixerLabel.text = item?.Name ?? "Mixer";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route)
    {
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteEntryUI: started Route={Route}, Config={Config}");
      Config = config;
      Route = route;
      ProductLabel.text = Route.Product?.SelectedItem?.Name ?? "Product";
      MixerLabel.text = Route.MixerItem?.SelectedItem?.Name ?? "Mixer";

      Route.Product.onItemChanged.RemoveAllListeners();
      Route.MixerItem.onItemChanged.RemoveAllListeners();
      Route.Product.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>(OnProductItemChanged));
      Route.MixerItem.onItemChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<ItemDefinition>>(OnMixerItemChanged));
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteEntryUI: Bound Route={Route}, Config={Config}");
    }

    public void ProductClicked() { onProductClicked.Invoke(); }

    public void MixerClicked() { onMixerClicked.Invoke(); }

    public void DeleteClicked() { onDeleteClicked.Invoke(); }

    public static void OpenItemSelectorScreen(ItemField itemField, string fieldName)
    {
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteEntryUI: OpenItemSelectorScreen");

      List<ItemSelector.Option> list = new();
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
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteEntryUI: OpenItemSelectorScreen None set");

      // Ensure Options is not null
      var options = itemField.Options ?? new Il2CppSystem.Collections.Generic.List<ItemDefinition>();
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteEntryUI: OpenItemSelectorScreen, Options count={options.Count}");

      foreach (var option in options)
      {
        if (option == null)
        {
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Warning("MixingRouteEntryUI: Skipping null option in Options");
          continue;
        }
        var opt = new ItemSelector.Option(option.Name, option);
        list.Add(opt);
        if (itemField.SelectedItem == option)
          selectedOption = opt;
      }
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteEntryUI: OpenItemSelectorScreen Options set");

      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, ConvertList(list), selectedOption, new Action<ItemSelector.Option>(selected =>
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteEntryUI: ItemSelectorScreen selected {selected?.Item?.Name ?? "null"} for {fieldName}");
        itemField.SelectedItem = selected.Item;
      }));
      Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Open();
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
        __instance.StartThrehold.Configure(1f, 20f, wholeNumbers: true);

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Initializing for station: {station?.ObjectId.ToString() ?? "null"}, configHash={__instance.GetHashCode()}");

        ObjectField Supply = new(__instance)
        {
          TypeRequirements = new(),
          DrawTransitLine = true
        };
        Supply.onObjectChanged.RemoveAllListeners();
        Supply.onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>(MixingStationExtensions.SupplyOnObjectChangedInvoke));

        if (!MixingStationExtensions.MixingSupply.ContainsKey(station.GUID))
          MixingStationExtensions.MixingSupply[station.GUID] = null;
        MixingStationExtensions.MixingSupply[station.GUID] = Supply;

        if (!MixingStationExtensions.MixingConfig.ContainsKey(station.GUID))
          MixingStationExtensions.MixingConfig[station.GUID] = null;
        MixingStationExtensions.MixingConfig[station.GUID] = __instance;

        if (!MixingStationExtensions.MixingRoutes.ContainsKey(station.GUID))
        {
          MixingStationExtensions.MixingRoutes[station.GUID] = [];
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
        if (MixingStationExtensions.MixingRoutes.TryGetValue(__instance.station.GUID, out var routes) && routes.Any())
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

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"{__instance.Destination.GetData()} | {__instance.StartThrehold.GetData()} {MixingStationExtensions.MixingSupply[__instance.station.GUID].GetData()}");
        // Create the config data
        ExtendedMixingStationConfigurationData data = new ExtendedMixingStationConfigurationData(
          __instance.Destination.GetData(),
          __instance.StartThrehold.GetData(),
          MixingStationExtensions.MixingSupply[__instance.station.GUID].GetData()
      );

        // Serialize config data to JSON
        string configJson = JsonUtility.ToJson(data, true);

        // Combine config data with additional fields using Newtonsoft.Json
        JObject jsonObject = JObject.Parse(configJson);
        if (!string.IsNullOrEmpty(mixingRoutesJson))
          jsonObject["MixingRoutes"] = mixingRoutesJson;

        __result = jsonObject.ToString(Il2CppNewtonsoft.Json.Formatting.Indented);

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
        ObjectField supply = MixingStationExtensions.MixingSupply[__instance.station.GUID];
        bool hasRoutes = MixingStationExtensions.MixingRoutes.TryGetValue(__instance.station.GUID, out var routes) &&
                        routes.Any(r => r.Product.SelectedItem != null || r.MixerItem.SelectedItem != null);
        __result |= supply.SelectedObject != null || hasRoutes;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationShouldSavePatch failed: {e}");
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
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Binding configs, count: {configs?.Count ?? 0}");

        if (__instance == null || __instance.DestinationUI == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: __instance or DestinationUI is null");
          return;
        }

        // Clean up existing UI elements
        int childCount = __instance.transform.GetChildCount();
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
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingStationConfigPanelBindPatch: foreach configs {varConfig.GetType().FullName} | {varConfig.GetIl2CppType().FullName} ");

          if (varConfig.TryCast<MixingStationConfiguration>() is MixingStationConfiguration config)
          {
            // Supply UI setup
            foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
            {
              if (child.gameObject.name == "Title") child.text = "Supplies";
              else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
            }
            if (MixingStationExtensions.MixingSupply.TryGetValue(config.station.GUID, out ObjectField supply))
            {
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Before Bind, station: {config.station.GUID}, SelectedObject: {supply.SelectedObject?.name ?? "null"}");
            }
            else
            {
              supply = new ObjectField(config);
              MixingStationExtensions.MixingSupply[config.station.GUID] = supply;
              if (DebugConfig.EnableDebugLogs)
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
              MixingStationExtensions.MixingRoutes[config.station.GUID] = [];
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
    [Serializable]
    private class SerializableRouteData
    {
      public string Product;
      public string MixerItem;
    }
    private static void MixingSupplySourceChanged(MixingStationConfiguration config, MixingStation station, BuildableItem item)
    {
      MixingStationExtensions.SourceChanged(config, item);
      ConfigurationExtensions.InvokeChanged(config);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
        MelonLogger.Msg($"MixingStationLoaderPatch: Supply changed for station {station.ObjectId}, newSupply={MixingStationExtensions.MixingSupply[station.GUID].SelectedObject?.ObjectId.ToString() ?? "null"}");
    }

    [HarmonyPostfix]
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
        if (gridItem.TryCast<MixingStation>() is not MixingStation station)
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

        // Parse JSON using Newtonsoft.Json
        JObject jsonObject = JObject.Parse(text);
        string mixingRoutesString = jsonObject["MixingRoutes"]?.ToString();
        jsonObject.Remove("MixingRoutes");
        string modifiedJson = jsonObject.ToString();

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationLoaderPatch: Modified JSON (without MixingRoutes): {modifiedJson}");
        MelonLogger.Msg($"MixingStationLoaderPatch: Extracted MixingRoutes: {mixingRoutesString}");

        // Deserialize modified JSON into ExtendedMixingStationConfigurationData
        ExtendedMixingStationConfigurationData configData = JsonUtility.FromJson<ExtendedMixingStationConfigurationData>(modifiedJson);
        if (configData == null)
        {
          MelonLogger.Error($"MixingStationLoaderPatch: Failed to deserialize modified JSON for mainPath: {mainPath}, configData is null");
          return;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationLoaderPatch: Deserialized configData - DataType: {configData.DataType}, DataVersion: {configData.DataVersion}, GameVersion: {configData.GameVersion}");

        // Process MixingRoutes separately (as in original code)
        List<MixingRouteData> mixingRoutes = [];
        if (!string.IsNullOrEmpty(mixingRoutesString))
        {
          try
          {
            // Split the MixingRoutes string into individual JSON objects
            // Use a simple split, accounting for commas outside of quotes
            List<string> routeJsonList = new List<string>();
            int braceLevel = 0;
            int startIndex = 0;
            for (int i = 0; i < mixingRoutesString.Length; i++)
            {
              char c = mixingRoutesString[i];
              if (c == '{') braceLevel++;
              else if (c == '}') braceLevel--;
              else if (c == ',' && braceLevel == 0)
              {
                routeJsonList.Add(mixingRoutesString.Substring(startIndex, i - startIndex));
                startIndex = i + 1;
              }
            }
            if (startIndex < mixingRoutesString.Length)
            {
              routeJsonList.Add(mixingRoutesString.Substring(startIndex));
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
        MixingStationConfiguration config = MixingStationExtensions.MixingConfig.TryGetValue(station.GUID, out var cfg)
            ? cfg
            : station.Configuration as MixingStationConfiguration;
        if (config == null)
        {
          MelonLogger.Error($"MixingStationLoaderPatch: No valid MixingStationConfiguration for station: {station.ObjectId}");
          return;
        }
        if (!MixingStationExtensions.MixingConfig.ContainsKey(station.GUID))
        {
          MixingStationExtensions.MixingConfig[station.GUID] = config;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"MixingStationLoaderPatch: Registered MixingConfig for station: {station.ObjectId}");
        }
        if (configData.Supply != null)
        {
          if (!MixingStationExtensions.MixingSupply.ContainsKey(station.GUID))
          {
            MixingStationExtensions.MixingSupply[station.GUID] = new ObjectField(config)
            {
              TypeRequirements = new(),
              DrawTransitLine = true
            };
            MixingStationExtensions.MixingSupply[station.GUID].onObjectChanged.RemoveAllListeners();
            MixingStationExtensions.MixingSupply[station.GUID].onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>(MixingSupplySourceChanged));
          }
          MixingStationExtensions.MixingSupply[station.GUID].Load(configData.Supply);
          MixingStationExtensions.SourceChanged(config, MixingStationExtensions.MixingSupply[station.GUID].SelectedObject);
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
          MixingStationExtensions.MixingRoutes[station.GUID] = routes;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"MixingStationLoaderPatch: Loaded {routes.Count} MixingRoutes for station={station.ObjectId}");
        }
        else
        {
          MixingStationExtensions.MixingRoutes[station.GUID] = [];
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
      {// Only remove if the station GameObject is actually being destroyed
        if (__instance.station == null || __instance.station.gameObject == null)
        {
          MixingStationExtensions.MixingSupply.Remove(__instance.station.GUID);
          MixingStationExtensions.MixingRoutes.Remove(__instance.station.GUID);
          foreach (var pair in MixingStationExtensions.MixingConfig.Where(p => p.Value == __instance).ToList())
          {
            MixingStationExtensions.MixingConfig.Remove(pair.Key);
          }
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Removed station {__instance.station?.ObjectId} from dictionaries");
        }
        else
        {
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Skipped removal for station {__instance.station?.ObjectId}, station still exists");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }
}