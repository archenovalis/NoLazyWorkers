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

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0.1", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{
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
        Supply.onObjectChanged.AddListener(item => ConfigurationExtensions.SourceChanged(__instance, item));
        ConfigurationExtensions.MixingSupply[station] = Supply;

        ConfigurationExtensions.MixingConfig[station] = __instance;

        if (!ConfigurationExtensions.MixingRoutes.ContainsKey(station))
        {
          ConfigurationExtensions.MixingRoutes[station] = [];
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
        if (ConfigurationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes) && routes.Any())
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
          Supply = ConfigurationExtensions.MixingSupply[__instance.station].GetData(),
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
        ObjectField supply = ConfigurationExtensions.MixingSupply[__instance.station];
        bool hasRoutes = ConfigurationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes) &&
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

    private List<MixingRoute> Routes;
    private MixingStationConfiguration Config;
    private UnityAction OnChanged;

    private void Start()
    {
      FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
      if (FieldLabel != null) FieldLabel.text = FieldText;
      MultiEditBlocker = transform.Find("Blocker")?.GetComponent<RectTransform>();
      MultiEditBlocker?.gameObject.SetActive(false);
      AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
      if (AddButton != null) AddButton.onClick.AddListener(AddClicked);
      RouteEntries = transform.Find("Contents").GetComponentsInChildren<MixingRouteEntryUI>(true);
      for (int i = 0; i < RouteEntries.Length; i++)
      {
        int index = i;
        RouteEntries[i].onDeleteClicked.AddListener(() => EntryDeleteClicked(index));
      }
      Refresh();
    }

    public void Bind(MixingStationConfiguration config, List<MixingRoute> routes, UnityAction onChangedCallback)
    {
      Config = config;
      Routes = routes ?? new List<MixingRoute>();
      OnChanged = onChangedCallback;
      Refresh();
    }

    private void Refresh()
    {
      for (int i = 0; i < RouteEntries.Length; i++)
      {
        if (i < Routes.Count)
        {
          RouteEntries[i].gameObject.SetActive(true);
          RouteEntries[i].Bind(Config, Routes[i]);
        }
        else
        {
          RouteEntries[i].gameObject.SetActive(false);
        }
      }
      if (AddButton != null)
      {
        AddButton.gameObject.SetActive(Routes.Count < MaxRoutes);
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): AddButton active={AddButton.gameObject.activeSelf}");
      }
    }

    private void AddClicked()
    {
      if (Routes.Count < MaxRoutes) Routes.Add(new MixingRoute(Config));
      Refresh();
      OnChanged?.Invoke();
    }

    private void EntryDeleteClicked(int index)
    {
      if (index >= 0 && index < Routes.Count) Routes.RemoveAt(index);
      Refresh();
      OnChanged?.Invoke();
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
      ProductButton = transform.Find("ProductIMGUI")?.GetComponent<Button>();
      MixerButton = transform.Find("MixerItemIMGUI")?.GetComponent<Button>();
      ProductLabel = transform.Find("ProductIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
      MixerLabel = transform.Find("MixerItemIMGUI/Label")?.GetComponent<TextMeshProUGUI>();
      ProductButton?.onClick.AddListener(ProductClicked);
      MixerButton?.onClick.AddListener(MixerClicked);
      transform.Find("Remove")?.GetComponent<Button>()?.onClick.AddListener(DeleteClicked);
    }

    public void Bind(MixingStationConfiguration config, MixingRoute route)
    {
      Config = config;
      Route = route;
      ProductLabel.text = Route.Product?.SelectedItem?.Name ?? "Product";
      MixerLabel.text = Route.MixerItem?.SelectedItem?.Name ?? "Mixer";
      // Keep item selection listeners as they are
      Route.Product.onItemChanged.RemoveAllListeners();
      Route.Product.onItemChanged.AddListener(item =>
      {
        ProductLabel.text = item?.Name ?? "Product";
        ConfigurationExtensions.InvokeChanged(Config);
      });
      Route.MixerItem.onItemChanged.RemoveAllListeners();
      Route.MixerItem.onItemChanged.AddListener(item =>
      {
        MixerLabel.text = item?.Name ?? "Mixer";
        ConfigurationExtensions.InvokeChanged(Config);
      });
    }

    public void ProductClicked()
    {
      Route.Product.Options = [.. ProductManager.FavouritedProducts.Cast<ItemDefinition>()];
      OpenItemSelectorScreen(Route.Product, "Favorites", ProductLabel);
    }
    public void MixerClicked() { OpenItemSelectorScreen(Route.MixerItem, "Mixers", MixerLabel); }
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
    private static GameObject MixingRouteListTemplate { get; set; }

    private static void InitializeStaticTemplate(RouteListFieldUI routeListTemplate)
    {
      if (MixingRouteListTemplate != null)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: MixingRouteListTemplate already initialized, skipping");
        return;
      }
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

        MixingRouteListTemplate = templateObj;
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Static MixingRouteListTemplate initialized successfully");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Failed to initialize MixingRouteListTemplate, error: {e}");
      }
    }

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
          if (child.name.Contains("SupplyUI") || child.name.Contains("RouteListFieldUI"))
            UnityEngine.Object.Destroy(child.gameObject);
        }

        // Retrieve and initialize the template
        RouteListFieldUI routeListTemplate = NoLazyUtilities.GetComponentTemplateFromConfigPanel(
            EConfigurableType.Packager,
            panel => panel.GetComponentInChildren<RouteListFieldUI>());
        if (routeListTemplate == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: Failed to retrieve RouteListFieldUI template");
          return;
        }
        InitializeStaticTemplate(routeListTemplate);

        foreach (var config in configs.OfType<MixingStationConfiguration>())
        {
          // Supply UI setup
          GameObject supplyUIObj = UnityEngine.Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
          supplyUIObj.name = $"SupplyUI_{config.station.GetInstanceID()}";
          ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
          foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title") child.text = "Supplies";
            else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
          }
          if (ConfigurationExtensions.MixingSupply.TryGetValue(config.station, out ObjectField supply))
            supplyUI.Bind([supply]);

          // Destination UI update
          foreach (TextMeshProUGUI child in __instance.DestinationUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title") child.text = "Destination";
            else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
          }

          // Instantiate and bind MixingRouteListFieldUI
          GameObject routeListUIObj = UnityEngine.Object.Instantiate(MixingRouteListTemplate, __instance.transform, false);
          routeListUIObj.name = $"RouteListFieldUI_{config.station.GetInstanceID()}";
          routeListUIObj.SetActive(true);
          var customRouteListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();
          if (!ConfigurationExtensions.MixingRoutes.ContainsKey(config.station))
            ConfigurationExtensions.MixingRoutes[config.station] = [];
          var routes = ConfigurationExtensions.MixingRoutes[config.station];
          customRouteListUI.Bind(config, routes, () => ConfigurationExtensions.InvokeChanged(config));

          // Position adjustments
          RectTransform supplyRect = supplyUIObj.GetComponent<RectTransform>();
          supplyRect.anchoredPosition = new Vector2(supplyRect.anchoredPosition.x, -135.76f);
          RectTransform destRect = __instance.DestinationUI.GetComponent<RectTransform>();
          destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, -195.76f);
          RectTransform routeListRect = routeListUIObj.GetComponent<RectTransform>();
          routeListRect.anchoredPosition = new Vector2(routeListRect.anchoredPosition.x, -290.76f);
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Postfix failed, error: {e}");
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
        ConfigurationExtensions.MixingSupply.Remove(__instance.station);
        ConfigurationExtensions.MixingRoutes.Remove(__instance.station);
        foreach (var pair in ConfigurationExtensions.MixingConfig.Where(p => p.Value == __instance).ToList())
        {
          ConfigurationExtensions.MixingConfig.Remove(pair.Key);
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }
}