using HarmonyLib;
using MelonLoader;
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
using Object = UnityEngine.Object;

using Il2CppInterop.Runtime;
using Guid = Il2CppSystem.Guid;
using static NoLazyWorkers_IL2CPP.NoLazyUtilities;

namespace NoLazyWorkers_IL2CPP.Chemists
{
  public static class MixingStationExtensions
  {
    public static Dictionary<Guid, ObjectField> Supply = [];
    public static Dictionary<Guid, MixingStationConfiguration> Config = [];
    public static Dictionary<Guid, TransitRoute> SupplyRoute = [];
    public static Dictionary<Guid, ObjectFieldData> FailedSupply = [];
    public static Dictionary<Guid, List<MixingRoute>> MixingRoutes = [];
    public static GameObject MixingRouteListTemplate { get; set; }

    public static void SourceChanged(this MixingStationConfiguration mixerConfig, BuildableItem item)
    {
      try
      {
        if (mixerConfig == null)
        {
          MelonLogger.Error("SourceChanged(station): MixingStationConfiguration is null");
          return;
        }
        MixingStation station = mixerConfig.station;
        if (station == null)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning("SourceChanged(station): Station is null");
          return;
        }
        Guid guid = station.GUID;
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"SourceChanged(station): Called for MixerConfig: {guid.ToString() ?? "null"}, Item: {item?.name ?? "null"}");
        if (!Supply.ContainsKey(guid))
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"SourceChanged(station): MixerSupply does not contain key for station: {guid}");
          return;
        }
        if (!SupplyRoute.ContainsKey(guid))
        {
          SupplyRoute[guid] = null; // initialize
        }

        if (SupplyRoute[guid]?.TryCast<TransitRoute>() is TransitRoute supplyRoute)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"SourceChanged(station): Destroying existing TransitRoute for station: {guid}");
          supplyRoute.Destroy();
          SupplyRoute[guid] = null;
        }

        ObjectField supply = Supply[guid];
        ITransitEntity entity = null;
        if ((supply.SelectedObject != null && supply.SelectedObject.TryCast<ITransitEntity>() is ITransitEntity) || item?.TryCast<ITransitEntity>() is ITransitEntity)
        {
          entity = supply.SelectedObject.TryCast<ITransitEntity>() ?? item?.TryCast<ITransitEntity>();
          supplyRoute = new TransitRoute(entity, station.TryCast<ITransitEntity>());
          SupplyRoute[guid] = supplyRoute;
          if (station.Configuration.IsSelected)
          {
            supplyRoute.SetVisualsActive(true);
          }
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"SourceChanged(station): Created new TransitRoute for station: {guid}, Supply: {item.name}");
        }
        else
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"SourceChanged(station): Item is null, no TransitRoute created for station: {guid}");
          SupplyRoute[guid] = null;
        }
        if (DebugLogs.All || DebugLogs.MixingStation) MelonLogger.Msg($"SourceChanged(MixingStation): Updated for MixerConfig: {mixerConfig}, Supply: {supply?.SelectedObject?.name ?? "null"}");
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
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"SupplyOnObjectChangedInvoke: Supply changed for station {station?.GUID.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.GUID.ToString() ?? "null"}");
    }

    public static void RestoreConfigurations()
    {
      // Restore MixingStation configurations
      MixingStation[] mixingStations = Object.FindObjectsOfType<MixingStation>();
      foreach (MixingStation station in mixingStations)
      {
        try
        {
          if (station.Configuration is MixingStationConfiguration mixerConfig)
          {
            Guid guid = station.GUID;
            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Warning($"RestoreConfigurations: Started for station: {guid}");

            Config[guid] = mixerConfig;
            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Warning($"RestoreConfigurations: Registered missing MixerConfig for station: {guid}");

            // Initialize Supply if missing
            if (!Supply.TryGetValue(guid, out var supply))
            {
              supply = new ObjectField(mixerConfig)
              {
                TypeRequirements = new(),
                DrawTransitLine = true
              };
              Supply[guid] = supply;
              Supply[guid].onObjectChanged.RemoveAllListeners();
              Supply[guid].onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>((BuildableItem item) => SupplyOnObjectChangedInvoke(item, mixerConfig, station, supply)));
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Msg($"RestoreConfigurations: Initialized Supply for station: {guid}");
            }

            // Initialize MixingRoutes if missing
            if (!MixingRoutes.ContainsKey(guid))
            {
              MixingRoutes[guid] = [];
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Msg($"RestoreConfigurations: Initialized MixingRoutes for station: {guid}");
            }
          }
          else
          {
            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Msg($"RestoreConfigurations: Skipped station {station?.GUID}, no MixingStationConfiguration");
          }
        }
        catch (Exception e)
        {
          MelonLogger.Error($"RestoreConfigurations: Failed for station: {station?.GUID.ToString() ?? "null"}, error: {e}");
        }
      }

      // Reload failed Supply entries
      try
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"RestoreConfigurations: Reloading {FailedSupply.Count} failed Supply entries");

        // Use ToList to avoid modifying collection during iteration
        foreach (var entry in FailedSupply.ToList())
        {
          Guid guid = entry.Key;
          ObjectFieldData supplyData = entry.Value;

          try
          {
            if (!Supply.TryGetValue(guid, out ObjectField supply) || supply == null)
            {
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Warning($"RestoreConfigurations: Skipping reload for station: {guid.ToString() ?? "null"}, supply not found");
              FailedSupply.Remove(guid);
              continue;
            }

            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Msg($"RestoreConfigurations: Reloading Supply for station: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}");

            supply.Load(supplyData);

            if (supply.SelectedObject == null)
            {
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Warning($"RestoreConfigurations: Reload failed to set SelectedObject for station: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}");
            }
            else
            {
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Msg($"RestoreConfigurations: Reload succeeded, SelectedObject: {supply.SelectedObject.name} for station: {guid}");
              if (Config.TryGetValue(guid, out var config))
              {
                SourceChanged(config, supply.SelectedObject);
              }
            }
            FailedSupply.Remove(guid);
          }
          catch (Exception e)
          {
            MelonLogger.Error($"RestoreConfigurations: Failed to reload Supply for station: {guid.ToString() ?? "null"}, error: {e}");
            FailedSupply.Remove(guid);
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"RestoreConfigurations: Failed to process FailedSupply entries, error: {e}");
      }
    }

    public static void InitializeStaticRouteListTemplate()
    {
      var routeListTemplate = GetTransformTemplateFromConfigPanel(
            EConfigurableType.Packager,
            "RouteListFieldUI");
      if (routeListTemplate == null)
      {
        MelonLogger.Error("InitializeStaticRouteListTemplate: routeListTemplate is null");
        return;
      }
      try
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: Initializing MixingRouteListTemplate");

        // Instantiate the template
        var templateObj = Object.Instantiate(routeListTemplate.gameObject);
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);
        try
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg("InitializeStaticRouteListTemplate: Adding MixingRouteListFieldUI");
          var routeListFieldUI = templateObj.AddComponent<MixingRouteListFieldUI>();
        }
        catch (Exception e)
        {
          MelonLogger.Error($"InitializeStaticRouteListTemplate: Failed to add MixingRouteListFieldUI, error: {e}");
        }
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: MixingRouteListFieldUI added");

        // Replace RouteListFieldUI with MixingRouteListFieldUI
        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
          Object.Destroy(defaultScript);
        if (templateObj.GetComponent<MixingRouteListFieldUI>() == null)
          templateObj.AddComponent<MixingRouteListFieldUI>();

        // Replace RouteEntryUI with MixingRouteEntryUI for all entries
        var contentsTransform = templateObj.transform.Find("Contents");
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: contentsTransform: {contentsTransform?.name}");

        // Prepare newEntry transform
        var entryTransform = templateObj.transform.Find("Contents/Entry");
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: entryTransform: {entryTransform?.name}");

        // Update AddNew label
        var addNewLabel = templateObj.transform.Find("Contents/AddNew/Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "  Add Recipe";
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"InitializeStaticRouteListTemplate: addNewLabel?.text: {addNewLabel?.text}");

        for (int i = 0; i < MixingRouteListFieldUI.MaxRoutes; i++)
        {
          Transform entry = contentsTransform.Find($"Entry ({i})") ?? (i == 0 ? contentsTransform.Find("Entry") : null);
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"InitializeStaticRouteListTemplate: Processing Entry ({i})");

          if (entry == null)
          {
            // Instantiate new entry from the full template
            GameObject newEntry = Object.Instantiate(entryTransform.gameObject, contentsTransform, false);
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
            Object.Destroy(routeEntryUI);
          if (entry.GetComponent<MixingRouteEntryUI>() == null)
            entry.gameObject.AddComponent<MixingRouteEntryUI>();
        }

        contentsTransform.Find("AddNew").SetAsLastSibling();
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg("InitializeStaticRouteListTemplate: SetAsLastSibling");

        // Hide unnecessary elements
        templateObj.transform.Find("Title")?.gameObject.SetActive(false);
        templateObj.transform.Find("From")?.gameObject.SetActive(false);
        templateObj.transform.Find("To")?.gameObject.SetActive(false);

        MixingRouteListTemplate = templateObj;
        if (DebugLogs.All || DebugLogs.MixingStation)
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
  public class MixingRouteData
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
    public static readonly int MaxRoutes = 8;
    private List<List<MixingRoute>> RoutesLists;
    private List<MixingStationConfiguration> Configs;
    private UnityAction OnChanged;

    private void Start()
    {
      if (MixingStationExtensions.MixingRouteListTemplate != null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: MixingRouteListTemplate already initialized, skipping");
      }
      else
      {
        MixingStationExtensions.InitializeStaticRouteListTemplate();
        if (MixingStationExtensions.MixingRouteListTemplate == null)
          MelonLogger.Error("MixingRouteListFieldUI: Failed to initialize MixingRouteListTemplate");
      }

      FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
      if (FieldLabel != null) FieldLabel.text = FieldText;
      MultiEditBlocker = transform.Find("Blocker")?.GetComponent<RectTransform>();
      MultiEditBlocker?.gameObject.SetActive(false);
      AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
      AddButton?.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(AddClicked));

      if (DebugLogs.All || DebugLogs.MixingStation)
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

      // Set up listeners
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

      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: Bind completed, RoutesLists count={RoutesLists.Count}, RouteEntries count={RouteEntries.Length}");
      Refresh();
    }

    private void RefreshChanged(ItemDefinition item, MixingRoute route)
    {
      if (DebugLogs.All || DebugLogs.MixingStation)
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
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: Refresh()");
      if (RoutesLists == null || RoutesLists.Count == 0)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
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
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): AddButton active={AddButton.gameObject.activeSelf}");
      }
      if (DebugLogs.All || DebugLogs.MixingStation)
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
            if (DebugLogs.All || DebugLogs.MixingStation)
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
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Warning($"MixingRouteListFieldUI: EntryProductClicked invalid state: RoutesLists={(RoutesLists == null ? "null" : RoutesLists.Count.ToString())}, index={index}");
        return;
      }

      MixingRoute route = RoutesLists[0][index];
      if (route == null || route.Product == null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
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
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteListFieldUI: ProductClicked {route.Product.SelectedItem?.Name ?? "null"} | Options count={options.Count}");
      MixingRouteEntryUI.OpenItemSelectorScreen(route.Product, "Favorites");
    }

    private void EntryMixerClicked(int index)
    {
      if (RoutesLists == null || RoutesLists.Count == 0 || index < 0 || index >= RoutesLists[0].Count)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Warning($"MixingRouteListFieldUI: EntryMixerClicked invalid state: RoutesLists={(RoutesLists == null ? "null" : RoutesLists.Count.ToString())}, index={index}");
        return;
      }

      MixingRoute route = RoutesLists[0][index];
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Warning($"MixingRouteEntryUI: EntryMixerClicked {route.MixerItem.SelectedItem?.name} | {route.MixerItem.Options.Count}");
      MixingRouteEntryUI.OpenItemSelectorScreen(route.MixerItem, "Mixers");

      if (DebugLogs.All || DebugLogs.MixingStation)
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
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OnProductItemChanged item={item?.Name ?? "null"}");
      ProductLabel.text = item?.Name ?? "Product";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    private void OnMixerItemChanged(ItemDefinition item)
    {
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OnMixerItemChanged item={item?.Name ?? "null"}");
      MixerLabel.text = item?.Name ?? "Mixer";
      ConfigurationExtensions.InvokeChanged(Config);
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route)
    {
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: Binding Route={(route != null ? "valid" : "null")}, Config={(config != null ? "valid" : "null")}");

      Config = config;
      Route = route;

      if (route == null || config == null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
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

      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: Bound Route Product={route.Product?.SelectedItem?.Name ?? "null"}, Mixer={route.MixerItem?.SelectedItem?.Name ?? "null"}");
    }

    public void ProductClicked() { onProductClicked.Invoke(); }
    public void MixerClicked() { onMixerClicked.Invoke(); }
    public void DeleteClicked() { onDeleteClicked.Invoke(); }

    public static void OpenItemSelectorScreen(ItemField itemField, string fieldName)
    {
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OpenItemSelectorScreen for {fieldName}");

      if (itemField == null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
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
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRouteEntryUI: OpenItemSelectorScreen, Options count={options.Count}");

      foreach (var option in options)
      {
        if (option == null)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
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
        if (DebugLogs.All || DebugLogs.MixingStation)
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
      MixerItem.Options = mixerOptions;

      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRoute: Initialized with Product Options={productOptions.Count}, Mixer Options={mixerOptions.Count}");
    }

    public void SetData(MixingRouteData data)
    {
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRoute: SetData: {data.Product?.ItemID} | {data.MixerItem?.ItemID}");
      if (data.Product != null)
        Product.Load(data.Product);
      if (data.MixerItem != null)
        MixerItem.Load(data.MixerItem);
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingRoute: SetData: Loaded {Product.SelectedItem} | {MixerItem.SelectedItem}");
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

        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Initializing for station: {station?.GUID.ToString() ?? "null"}, configHash={__instance.GetHashCode()}");

        if (!MixingStationExtensions.Supply.ContainsKey(guid) || !MixingStationExtensions.Supply.TryGetValue(guid, out var supply))
        {
          supply = new(__instance)
          {
            TypeRequirements = new(),
            DrawTransitLine = true
          };
          supply.onObjectChanged.RemoveAllListeners();
          supply.onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>((BuildableItem item) => MixingStationExtensions.SupplyOnObjectChangedInvoke(item, __instance, station, supply)));
          MixingStationExtensions.Supply[guid] = supply;
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg("MixingStationConfigurationPatch: Initialized Supply");
        }
        if (!MixingStationExtensions.Config.ContainsKey(guid) || MixingStationExtensions.Config[guid] == null)
        {
          MixingStationExtensions.Config[guid] = __instance;
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg("MixingStationConfigurationPatch: Initialized MixingConfig");
        }
        if (!MixingStationExtensions.MixingRoutes.ContainsKey(guid) || MixingStationExtensions.MixingRoutes[guid] == null)
        {
          MixingStationExtensions.MixingRoutes[guid] = [];
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg("MixingStationConfigurationPatch: Initialized MixingRoutes");
        }

        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Registered supply and config {__instance.GetHashCode()}, station: {station?.GUID}, supply={supply.SelectedObject?.GUID}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationPatch: Failed for station: {station?.GUID.ToString() ?? "null"}, error: {e}");
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
        JObject supplyObject = null;
        if (MixingStationExtensions.Supply.TryGetValue(guid, out var supply) && supply != null)
        {
          supplyObject = new JObject
          {
            ["ObjectGUID"] = supply?.GetData()?.ObjectGUID
          };
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationConfigurationGetSaveStringPatch: supplyData: {supplyObject["ObjectGUID"]}");
        }
        else if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Warning($"MixingStationConfigurationGetSaveStringPatch: No MixingSupply entry for config {__instance.GetHashCode()} station {guid}");

        // Manually serialize MixingRoutes as a comma-separated string of JSON objects
        JArray mixingRoutesArray = new();
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

        if (DebugLogs.All || DebugLogs.MixingStation)
        {
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Saved JSON for station={guid}: {__result}");
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Routes JSON={mixingRoutesArray}");
          if (routes != null)
          {
            MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Routes count={routes.Count}");
            foreach (var route in routes)
            {
              MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Route Product.ItemID={route.Product?.GetData()?.ItemID ?? "null"}, MixerItem.ItemID={route.MixerItem?.GetData()?.ItemID ?? "null"}");
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
    [HarmonyPatch("Reset")]
    static void ResetPostfix(MixingStationConfiguration __instance)
    {
      try
      {
        // Only remove if the station GameObject is actually being destroyed
        if (__instance.station == null || __instance.station.gameObject == null)
        {
          Guid guid = __instance.station.GUID;
          MixingStationExtensions.Supply.Remove(guid);
          MixingStationExtensions.SupplyRoute.Remove(guid);
          MixingStationExtensions.MixingRoutes.Remove(guid);
          foreach (var pair in MixingStationExtensions.Config.Where(p => p.Value == __instance).ToList())
          {
            MixingStationExtensions.Config.Remove(pair.Key);
          }
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Removed station {guid} from dictionaries");
        }
        else
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
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
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Binding configs, count: {configs?.Count ?? 0}");

        if (__instance == null || __instance.DestinationUI == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: __instance or DestinationUI is null");
          return;
        }

        if (MixingStationExtensions.MixingRouteListTemplate == null)
        {
          MixingStationExtensions.InitializeStaticRouteListTemplate();
        }

        // Destination UI update
        foreach (TextMeshProUGUI child in __instance.DestinationUI.GetComponentsInChildren<TextMeshProUGUI>())
          if (child.gameObject.name == "Description") { child.gameObject.SetActive(false); break; }

        // Instantiate UI objects
        GameObject supplyUIObj = Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
          if (child.gameObject.name == "Title") { child.text = "Supplies"; break; }
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        supplyUI.InstructionText = "Select supply";
        supplyUI.ExtendedInstructionText = "Select supply for mixer item input";

        GameObject routeListUIObj = Object.Instantiate(MixingStationExtensions.MixingRouteListTemplate, __instance.transform, false);
        routeListUIObj.name = "RouteListFieldUI";
        routeListUIObj.SetActive(true);
        var customRouteListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();

        List<ObjectField> supplyList = [];
        List<List<MixingRoute>> routesLists = [];
        List<MixingStationConfiguration> configList = [];

        // Bind all selected stations
        foreach (var varConfig in configs)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"MixingStationConfigPanelBindPatch: foreach configs {varConfig.GetType().FullName} | {varConfig.GetIl2CppType().FullName} ");

          if (varConfig.TryCast<MixingStationConfiguration>() is MixingStationConfiguration config)
          {
            Guid guid = config.station.GUID;
            config.StartThrehold.MaxValue = 20f;
            if (MixingStationExtensions.Supply.TryGetValue(guid, out ObjectField supply) && (DebugLogs.All || DebugLogs.MixingStation))
              MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Before Bind, station: {guid}, SelectedObject: {supply.SelectedObject?.name ?? "null"}");
            else if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Warning($"MixingStationConfigPanelBindPatch: No supply found for MixingStationConfiguration, station: {guid}");
            MixingStationExtensions.Supply[guid] = supply ?? new(config);
            supplyList.Add(MixingStationExtensions.Supply[guid]);

            // Collect routes and configurations
            if (!MixingStationExtensions.MixingRoutes.ContainsKey(guid))
              MixingStationExtensions.MixingRoutes[guid] = [];
            routesLists.Add(MixingStationExtensions.MixingRoutes[guid]);
            configList.Add(config);
          }
        }

        // Bind UI components
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}");
        customRouteListUI.Bind(routesLists, configList, DelegateSupport.ConvertDelegate<UnityAction>(() => configs.ForEach(new Action<EntityConfiguration>(c => ConfigurationExtensions.InvokeChanged(c)))));
        supplyUI.Bind(ConvertList(supplyList));

        // Position adjustments
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

  [HarmonyPatch(typeof(MixingStationLoader))]
  public class MixingStationLoaderPatch
  {
    private static void SupplySourceChanged(MixingStationConfiguration config, MixingStation station, BuildableItem item)
    {
      MixingStationExtensions.SourceChanged(config, item);
      ConfigurationExtensions.InvokeChanged(config);
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"MixingStationLoaderPatch: Supply changed for station {station.GUID}, newSupply={MixingStationExtensions.Supply[station.GUID].SelectedObject?.GUID.ToString() ?? "null"}");
    }

    [HarmonyPostfix]
    [HarmonyPatch("Load", new Type[] { typeof(string) })]
    static void Postfix(string mainPath)
    {
      try
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Processing Postfix for mainPath: {mainPath}");
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem.TryCast<MixingStation>() is not MixingStation station)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: GridItem is not a MixingStation for mainPath: {mainPath}, type: {gridItem.GetType().Name}");
          return;
        }
        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: No Configuration.json found at: {configPath}");
          return;
        }
        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
          return;
        }
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Loaded JSON: {text}");

        // Parse JSON using Newtonsoft.Json
        JObject jsonObject = JObject.Parse(text);
        JToken mixingRoutesJToken = jsonObject["MixingRoutes"];
        JToken supplyJToken = jsonObject["Supply"];

        if (DebugLogs.All || DebugLogs.MixingStation)
        {
          MelonLogger.Msg($"MixingStationLoaderPatch: Extracted mixingRoutesJToken: {mixingRoutesJToken}");
          MelonLogger.Msg($"MixingStationLoaderPatch: Extracted supplyJToken: {supplyJToken}");
        }

        Guid guid = station.GUID;
        MixingStationConfiguration config = station.Configuration?.TryCast<MixingStationConfiguration>();
        if (config == null)
        {
          MelonLogger.Error($"MixingStationLoaderPatch: No valid MixingStationConfiguration for station: {guid}");
          return;
        }
        if (!MixingStationExtensions.Config.ContainsKey(guid))
        {
          MixingStationExtensions.Config[guid] = config;
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: Registered MixingConfig for station: {guid}");
        }
        if (supplyJToken != null && supplyJToken["ObjectGUID"] != null)
        {
          if (!MixingStationExtensions.Supply.ContainsKey(guid))
          {
            MixingStationExtensions.Supply[guid] = new ObjectField(config)
            {
              TypeRequirements = new(),
              DrawTransitLine = true
            };
            MixingStationExtensions.Supply[guid].onObjectChanged.RemoveAllListeners();
            MixingStationExtensions.Supply[guid].onObjectChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<BuildableItem>>((BuildableItem item) => SupplySourceChanged(config, station, item)));
          }
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: supplyJToken[ObjectGUID].ToString(): {supplyJToken["ObjectGUID"].ToString()}");
          MixingStationExtensions.Supply[guid].Load(new(supplyJToken["ObjectGUID"].ToString()));
          if (MixingStationExtensions.Supply[guid].SelectedObject != null)
          {
            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Msg($"MixingStationLoaderPatch: Loaded Supply for station: {guid}");
            MixingStationExtensions.SourceChanged(config, MixingStationExtensions.Supply[guid].SelectedObject);
          }
          else
          {
            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Warning($"MixingStationLoaderPatch: Supply.SelectedObject is null for station: {guid}");
            MixingStationExtensions.FailedSupply[guid] = new(supplyJToken["ObjectGUID"].ToString());
          }
        }
        else
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
        }

        List<MixingRoute> routes = [];
        if (mixingRoutesJToken?.TryCast<JArray>() is JArray array && array?.Count > 0)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: array.count: {array.Count}");
          for (int i = 0; i < array.Count; i++)
          {
            var route = new MixingRoute(config);
            MixingRouteData data = new();
            var routeData = array[i];
            if (routeData["Product"] != null)
            {
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Msg($"MixingStationLoaderPatch: Loading Product={routeData["Product"].ToString()}");
              if (string.IsNullOrEmpty((string)routeData["Product"])) data.Product = null;
              else data.Product = new(routeData["Product"].ToString());
            }
            if (routeData["MixerItem"] != null)
            {
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Msg($"MixingStationLoaderPatch: Loading Mixer={routeData["MixerItem"].ToString()}");
              if (string.IsNullOrEmpty((string)routeData["MixerItem"])) data.MixerItem = null;
              else data.MixerItem = new(routeData["MixerItem"].ToString());
            }
            route.SetData(data);
            routes.Add(route);
            if (DebugLogs.All || DebugLogs.MixingStation)
              MelonLogger.Msg($"MixingStationLoaderPatch: Loaded route Product={route.Product.SelectedItem?.Name ?? "null"}, MixerItem={route.MixerItem.SelectedItem?.Name ?? "null"} for station={guid}");
          }
          MixingStationExtensions.MixingRoutes[guid] = routes;
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Msg($"MixingStationLoaderPatch: Loaded {routes.Count} MixingRoutes for station={guid}");
        }
        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }
}