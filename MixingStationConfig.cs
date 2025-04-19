using BepInEx.AssemblyPublicizer;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using TMPro;
using UnityEngine.UI;
using ScheduleOne.UI.Phone.ProductManagerApp;

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
    public List<MixingRouteData> MixingRoutes;

    public ExtendedMixingStationConfigurationData(ObjectFieldData destination, NumberFieldData threshold)
        : base(destination, threshold)
    {
      Supply = null;
      MixingRoutes = [];
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
        CanSelectNone = true,
        Options = ProductManager.DiscoveredProducts.Cast<ItemDefinition>().ToList() ?? []
      };
      MixerItem = new ItemField(config)
      {
        CanSelectNone = true,
        Options = NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.Cast<ItemDefinition>().ToList()
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

  [HarmonyPatch(typeof(ProductManagerApp))]
  public class ProductManagerAppPatch
  {
    [HarmonyPatch("SetOpen")]
    static bool Prefix(ProductManagerApp __instance, bool open)
    {
      try
      {
        if (open && SelectionHelper.ProductSelectionCallback != null)
        {
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("ProductManagerAppPatch: Opening in selection mode");
          if (__instance == null)
          {
            MelonLogger.Error("ProductManagerAppPatch: __instance is null");
            return false;
          }
          return true;
        }
        return true;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ProductManagerAppPatch.Prefix: Failed, error: {e}");
        return false;
      }
    }

    [HarmonyPatch("SetOpen")]
    static void Postfix(ProductManagerApp __instance, bool open)
    {
      try
      {
        if (!open)
        {
          SelectionHelper.ProductSelectionCallback = null;
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("ProductManagerAppPatch: Cleared selection callback on close");
        }
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"ProductManagerAppPatch: SetOpen completed, open={open}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ProductManagerAppPatch.Postfix: Failed, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(ProductAppDetailPanel), "SetActiveProduct")]
  public class ProductAppDetailPanelPatch
  {
    static void Postfix(ProductAppDetailPanel __instance, ProductDefinition productDefinition)
    {
      try
      {
        if (SelectionHelper.ProductSelectionCallback != null && productDefinition != null)
        {
          var nameLabelTransform = __instance.NameLabel.transform;
          var selectButton = nameLabelTransform.parent.Find("SelectButton")?.GetComponent<Button>();
          if (selectButton == null)
          {
            selectButton = new GameObject("SelectButton").AddComponent<Button>();
            selectButton.transform.SetParent(nameLabelTransform.parent, false);
            var selectRect = selectButton.GetComponent<RectTransform>();
            selectRect.sizeDelta = new Vector2(100, 30);
            selectRect.anchoredPosition = new Vector2(
                nameLabelTransform.GetComponent<RectTransform>().anchoredPosition.x,
                nameLabelTransform.GetComponent<RectTransform>().anchoredPosition.y + 35f);
            selectButton.gameObject.AddComponent<Image>().color = new Color(0.8f, 0.8f, 0.8f);
            var buttonText = new GameObject("Text").AddComponent<TextMeshProUGUI>();
            buttonText.transform.SetParent(selectButton.transform, false);
            buttonText.text = "Select";
            buttonText.alignment = TextAlignmentOptions.Center;
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg("ProductAppDetailPanelPatch: Added Select button");
          }
          selectButton.onClick.RemoveAllListeners();
          selectButton.onClick.AddListener(() =>
          {
            SelectionHelper.ProductSelectionCallback?.Invoke(productDefinition);
            SelectionHelper.ProductSelectionCallback = null;
            ProductManagerApp.Instance.SetOpen(false);
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"ProductAppDetailPanelPatch: Selected product {productDefinition.Name}");
          });
        }
        else
        {
          var selectButton = __instance.NameLabel.transform.parent.Find("SelectButton");
          if (selectButton != null)
          {
            UnityEngine.Object.Destroy(selectButton.gameObject);
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg("ProductAppDetailPanelPatch: Removed Select button");
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ProductAppDetailPanelPatch: Failed, error: {e}");
      }
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
        ConfigurationExtensions.MixingSupply[__instance] = Supply;

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
        ExtendedMixingStationConfigurationData data = new(
            __instance.Destination.GetData(),
            __instance.StartThrehold.GetData()
        )
        {
          Supply = ConfigurationExtensions.MixingSupply[__instance].GetData(),
          MixingRoutes = ConfigurationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes)
                ? routes.Select(r => new MixingRouteData(r.Product.GetData(), r.MixerItem.GetData())).ToList()
                : []
        };
        __result = data.GetJson(true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Saved for configHash={__instance.GetHashCode()}, supply={data.Supply?.ToString() ?? "null"}");
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
        ObjectField supply = ConfigurationExtensions.MixingSupply[__instance];
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
    [Header("References")]
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
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteListFieldUI: Started");

      // Initialize components
      if (FieldLabel == null)
        FieldLabel = transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
      if (FieldLabel != null)
      {
        FieldLabel.text = FieldText;
        FieldLabel.gameObject.SetActive(false);
      }

      if (MultiEditBlocker == null)
        MultiEditBlocker = transform.Find("Blocker")?.GetComponent<RectTransform>();
      MultiEditBlocker?.gameObject.SetActive(false);

      if (AddButton == null)
        AddButton = transform.Find("Contents/AddNew")?.GetComponent<Button>();
      if (AddButton == null)
        MelonLogger.Error("MixingRouteListFieldUI: AddButton not found");
      else
      {
        AddButton.onClick.RemoveAllListeners();
        AddButton.onClick.AddListener(AddClicked);
        AddButton.interactable = true;
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: AddButton listener added, interactable={AddButton.interactable}");
      }

      if (RouteEntries == null || RouteEntries.Length == 0)
        RouteEntries = transform.Find("Contents")?.GetComponentsInChildren<MixingRouteEntryUI>(true);
      if (RouteEntries == null || RouteEntries.Length == 0)
        MelonLogger.Error("MixingRouteListFieldUI: RouteEntries not found or empty");

      // Set up RouteEntries
      for (int i = 0; i < RouteEntries.Length; i++)
      {
        int index = i;
        var entry = RouteEntries[i];
        entry.onDeleteClicked.RemoveAllListeners();
        entry.onDeleteClicked.AddListener(() => EntryDeleteClicked(index));
        entry.gameObject.SetActive(false);
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: Delete listener added for entry {entry.name} at index {index}");
      }

      Refresh();
    }

    public void Bind(MixingStationConfiguration config, List<MixingRoute> routes, UnityAction onChangedCallback)
    {
      Config = config;
      Routes = routes ?? new List<MixingRoute>();
      OnChanged = onChangedCallback;
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Bound with {Routes.Count} routes");
      Refresh();
    }

    private void Refresh()
    {
      if (Routes == null)
      {
        Routes = new List<MixingRoute>();
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning("MixingRouteListFieldUI: Routes was null, initialized");
      }
      if (RouteEntries == null)
      {
        RouteEntries = new MixingRouteEntryUI[0];
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning("MixingRouteListFieldUI: RouteEntries was null, initialized");
      }

      for (int i = 0; i < RouteEntries.Length; i++)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): Length:{RouteEntries.Length} i:{i} Count:{Routes.Count}");
        if (RouteEntries[i]?.gameObject == null)
        {
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Warning($"MixingRouteListFieldUI: Refresh(): RouteEntries[{i}] gameObject is null");
          continue;
        }

        if (i < Routes.Count)
        {
          RouteEntries[i].gameObject.SetActive(true);
          // Update labels to reflect route data
          RouteEntries[i].ProductLabel.text = Routes[i].Product?.SelectedItem?.Name ?? "Product";
          RouteEntries[i].MixerLabel.text = Routes[i].MixerItem?.SelectedItem?.Name ?? "Mixer";
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): Activated RouteEntries[{i}]:{RouteEntries[i].name} for Route[{i}]");
        }
        else
        {
          RouteEntries[i].gameObject.SetActive(false);
          RouteEntries[i].ProductLabel.text = "Product";
          RouteEntries[i].MixerLabel.text = "Mixer";
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingRouteListFieldUI: Refresh(): Deactivated RouteEntries[{i}]:{RouteEntries[i].name}");
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
      if (Config == null)
      {
        MelonLogger.Error("MixingRouteListFieldUI: Config is null in AddClicked");
        return;
      }

      if (Routes.Count >= MaxRoutes)
      {
        MelonLogger.Warning("MixingRouteListFieldUI: Max routes reached");
        return;
      }

      Routes.Add(new MixingRoute(Config));
      Refresh();
      OnChanged?.Invoke();
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Added new route, total routes: {Routes.Count}");
    }

    private void EntryDeleteClicked(int index)
    {
      if (index < 0 || index >= Routes.Count)
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Warning($"MixingRouteListFieldUI: EntryDeleteClicked: Invalid index {index}, Routes.Count={Routes.Count}");
        return;
      }

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: EntryDeleteClicked: Index={index}, RouteEntries[{index}]={RouteEntries[index]?.name ?? "null"}");
      Routes.RemoveAt(index);
      Refresh();
      OnChanged?.Invoke();
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"MixingRouteListFieldUI: Deleted route at index {index}, remaining routes: {Routes.Count}");
    }
  }

  public class MixingRouteEntryUI : MonoBehaviour
  {
    [Header("References")]
    public TextMeshProUGUI ProductLabel;
    public TextMeshProUGUI MixerLabel;
    public UnityEvent onDeleteClicked = new UnityEvent();

    private void Awake()
    {
      if (ProductLabel == null)
        ProductLabel = transform.Find("Source/Label")?.GetComponent<TextMeshProUGUI>();
      ProductLabel.text = "Product";
      if (MixerLabel == null)
        MixerLabel = transform.Find("Destination/Label")?.GetComponent<TextMeshProUGUI>();
      MixerLabel.text = "Mixer";
      if (ProductLabel == null || MixerLabel == null)
        MelonLogger.Warning($"MixingRouteEntryUI: Missing labels in {gameObject.name}");

      var sourceButton = transform.Find("Source")?.GetComponent<Button>();
      if (sourceButton != null)
        sourceButton.onClick.AddListener(() => SourceClicked(OnChanged));
      else
        MelonLogger.Warning($"MixingRouteEntryUI: Source button not found in {gameObject.name}");

      var destButton = transform.Find("Destination")?.GetComponent<Button>();
      if (destButton != null)
        destButton.onClick.AddListener(() => DestinationClicked(OnChanged));
      else
        MelonLogger.Warning($"MixingRouteEntryUI: Destination button not found in {gameObject.name}");

      var deleteButton = transform.Find("Remove")?.GetComponent<Button>();
      if (deleteButton != null)
      {
        deleteButton.onClick.RemoveAllListeners();
        deleteButton.onClick.AddListener(DeleteClicked);
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteEntryUI: Delete button listener added for {gameObject.name}");
      }
      else
        MelonLogger.Error($"MixingRouteEntryUI: Remove button not found in {gameObject.name}");
    }

    private UnityAction OnChanged { get; set; }

    public void SourceClicked(UnityAction onChangedCallback)
    {
      OnChanged = onChangedCallback;
      if (ProductManagerApp.Instance == null)
      {
        MelonLogger.Error("MixingRouteEntryUI: ProductManagerApp.Instance is null");
        return;
      }

      SelectionHelper.ProductSelectionCallback = product =>
      {
        ProductLabel.text = product?.Name ?? "Product";
        onChangedCallback?.Invoke();
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingRouteEntryUI: Selected product {product?.Name ?? "null"}");
      };

      ProductManagerApp.Instance.SetOpen(true);
    }

    public void DestinationClicked(UnityAction onChangedCallback)
    {
      OnChanged = onChangedCallback;
      GameObject screenObj = new GameObject("MixingStationPresetEditScreen");
      screenObj.transform.SetParent(transform.root, false);
      var screen = screenObj.AddComponent<MixingStationPresetEditScreen>();
      if (screen == null)
      {
        MelonLogger.Error("MixingRouteEntryUI: Failed to create MixingStationPresetEditScreen");
        UnityEngine.Object.Destroy(screenObj);
        return;
      }

      SelectionHelper.ProductSelectionCallback = mixerItem =>
      {
        if (NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.Contains(mixerItem))
        {
          MixerLabel.text = mixerItem?.Name ?? "Mixer";
          onChangedCallback?.Invoke();
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"MixingRouteEntryUI: Selected mixer item {mixerItem?.Name ?? "null"}");
        }
        UnityEngine.Object.Destroy(screenObj);
      };

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg("MixingRouteEntryUI: Opened MixingStationPresetEditScreen");
    }

    public void DeleteClicked()
    {
      onDeleteClicked.Invoke();
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Warning($"MixingRouteEntryUI: DeleteClicked");
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

        GameObject templateObj = UnityEngine.Object.Instantiate(routeListTemplate.gameObject);
        templateObj.name = "MixingRouteListTemplate";
        templateObj.SetActive(false);

        var defaultScript = templateObj.GetComponent<RouteListFieldUI>();
        if (defaultScript != null)
        {
          UnityEngine.Object.Destroy(defaultScript);
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("MixingStationConfigPanelBindPatch: Removed RouteListFieldUI component");
        }

        var customRouteListUI = templateObj.GetComponent<MixingRouteListFieldUI>();
        if (customRouteListUI == null)
        {
          customRouteListUI = templateObj.AddComponent<MixingRouteListFieldUI>();
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("MixingStationConfigPanelBindPatch: Added MixingRouteListFieldUI component");
        }

        var contents = templateObj.transform.Find("Contents");
        if (contents == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: Contents not found in template");
          return;
        }

        var addNew = contents.Find("AddNew");
        if (addNew == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: AddNew not found in template Contents");
          return;
        }

        var addNewLabel = addNew.Find("Label")?.GetComponent<TextMeshProUGUI>();
        if (addNewLabel != null)
          addNewLabel.text = "Add Recipe";

        var firstEntry = contents.Find("Entry");
        if (firstEntry == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: Entry not found in template Contents");
          return;
        }

        var routeEntryUI = firstEntry.GetComponent<RouteEntryUI>();
        if (routeEntryUI != null)
        {
          UnityEngine.Object.Destroy(routeEntryUI);
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("MixingStationConfigPanelBindPatch: Removed RouteEntryUI from first Entry");
        }
        var mixingEntry = firstEntry.gameObject.GetComponent<MixingRouteEntryUI>();
        if (mixingEntry == null)
        {
          mixingEntry = firstEntry.gameObject.AddComponent<MixingRouteEntryUI>();
          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg("MixingStationConfigPanelBindPatch: Added MixingRouteEntryUI to first Entry");
        }
        mixingEntry.ProductLabel = firstEntry.Find("Source/Label")?.GetComponent<TextMeshProUGUI>();
        mixingEntry.MixerLabel = firstEntry.Find("Destination/Label")?.GetComponent<TextMeshProUGUI>();
        if (mixingEntry.ProductLabel == null || mixingEntry.MixerLabel == null)
          MelonLogger.Warning("MixingStationConfigPanelBindPatch: Missing labels in Entry");

        for (int i = 1; i < MixingRouteListFieldUI.MaxRoutes; i++)
        {
          GameObject newEntry = UnityEngine.Object.Instantiate(firstEntry.gameObject, contents, false);
          newEntry.name = $"Entry ({i})";
          var newRouteEntryUI = newEntry.GetComponent<RouteEntryUI>();
          if (newRouteEntryUI != null)
          {
            UnityEngine.Object.Destroy(newRouteEntryUI);
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Removed RouteEntryUI from Entry ({i})");
          }
          var newMixingEntry = newEntry.GetComponent<MixingRouteEntryUI>();
          if (newMixingEntry == null)
          {
            newMixingEntry = newEntry.AddComponent<MixingRouteEntryUI>();
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Added MixingRouteEntryUI to Entry ({i})");
          }
          newMixingEntry.ProductLabel = newEntry.transform.Find("Source/Label")?.GetComponent<TextMeshProUGUI>();
          newMixingEntry.MixerLabel = newEntry.transform.Find("Destination/Label")?.GetComponent<TextMeshProUGUI>();
          if (newMixingEntry.ProductLabel == null || newMixingEntry.MixerLabel == null)
            MelonLogger.Warning($"MixingStationConfigPanelBindPatch: Missing labels in Entry ({i})");

          newEntry.transform.SetSiblingIndex(i);
        }

        addNew.SetAsLastSibling();

        var title = templateObj.transform.Find("Title");
        title?.gameObject.SetActive(false);
        var from = templateObj.transform.Find("From");
        from?.gameObject.SetActive(false);
        var to = templateObj.transform.Find("To");
        to?.gameObject.SetActive(false);

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

        var destinationUI = __instance.DestinationUI;
        // Clean up existing UI
        foreach (Transform child in __instance.transform)
        {
          if (child.name.Contains("SupplyUI") || child.name.Contains("RouteListFieldUI"))
            UnityEngine.Object.Destroy(child.gameObject);
        }

        // Get template
        RouteListFieldUI routeListTemplate = NoLazyUtilities.GetUITemplateFromConfigPanel(
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
          // Instantiate SupplyUI
          GameObject supplyUIObj = UnityEngine.Object.Instantiate(destinationUI.gameObject, __instance.transform, false);
          supplyUIObj.name = $"SupplyUI_{config.station.GetInstanceID()}";
          ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
          supplyUIObj.AddComponent<CanvasRenderer>();
          foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title")
              child.text = "Supplies";
            else if (child.gameObject.name == "Description")
              child.gameObject.SetActive(false);
          }

          if (ConfigurationExtensions.MixingSupply.TryGetValue(config, out ObjectField supply))
          {
            supplyUI.Bind(new List<ObjectField> { supply });
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"Bound supply for MixingStation {config.station.GetInstanceID()}, SelectedObject: {(supply.SelectedObject != null ? supply.SelectedObject.ObjectId : "null")}");
          }

          // Update DestinationUI title
          foreach (TextMeshProUGUI child in destinationUI.gameObject.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title")
              child.text = "Destination";
            else if (child.gameObject.name == "Description")
              child.gameObject.SetActive(false);
          }

          // Instantiate RouteListFieldUI
          GameObject routeListUIObj = UnityEngine.Object.Instantiate(MixingRouteListTemplate, __instance.transform, false);
          routeListUIObj.name = $"RouteListFieldUI_{config.station.GetInstanceID()}";
          routeListUIObj.SetActive(true);
          routeListUIObj.AddComponent<CanvasRenderer>();

          // Ensure MixingRouteListFieldUI exists
          var customRouteListUI = routeListUIObj.GetComponent<MixingRouteListFieldUI>();
          if (customRouteListUI == null)
          {
            customRouteListUI = routeListUIObj.AddComponent<MixingRouteListFieldUI>();
            MelonLogger.Warning("MixingStationConfigPanelBindPatch: MixingRouteListFieldUI was missing, added to instantiated object");
          }

          // Configure layout for Contents
          var contents = routeListUIObj.transform.Find("Contents");
          if (contents != null)
          {
            var contentsLayout = contents.GetComponent<VerticalLayoutGroup>();
            if (contentsLayout == null)
            {
              contentsLayout = contents.gameObject.AddComponent<VerticalLayoutGroup>();
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg("MixingStationConfigPanelBindPatch: Added VerticalLayoutGroup to Contents");
            }
          }

          // Bind routes
          if (!ConfigurationExtensions.MixingRoutes.ContainsKey(config.station))
            ConfigurationExtensions.MixingRoutes[config.station] = new List<MixingRoute>();
          var routes = ConfigurationExtensions.MixingRoutes[config.station];
          customRouteListUI.Bind(config, routes, () => ConfigurationExtensions.InvokeChanged(config));

          // Position UI elements
          RectTransform supplyRect = supplyUIObj.GetComponent<RectTransform>();
          supplyRect.anchoredPosition = new Vector2(supplyRect.anchoredPosition.x, -135.76f);
          RectTransform destRect = destinationUI.GetComponent<RectTransform>();
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
        ConfigurationExtensions.MixingSupply.Remove(__instance);
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

  public class MixingStationPreset : Preset
  {
    private static MixingStationPreset DefaultPresetInstance;

    public ItemList MixerItems { get; set; }

    public override Preset GetCopy()
    {
      var preset = new MixingStationPreset();
      CopyTo(preset);
      return preset;
    }

    public override void CopyTo(Preset other)
    {
      base.CopyTo(other);
      if (other is MixingStationPreset targetPreset)
      {
        MixerItems.CopyTo(targetPreset.MixerItems);
      }
    }

    public override void InitializeOptions()
    {
      MixerItems = new ItemList(
          "Mixer",
          NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.Select(item => item.ID).ToList(),
          true,
          true
      );
      MixerItems.All = true;
      MixerItems.None = true;
    }

    public static MixingStationPreset GetDefaultPreset()
    {
      if (DefaultPresetInstance == null)
      {
        DefaultPresetInstance = new MixingStationPreset
        {
          PresetName = "Default",
          ObjectType = (ManageableObjectType)100,
          PresetColor = new Color32(180, 180, 180, 255)
        };
        DefaultPresetInstance.InitializeOptions();
      }
      return DefaultPresetInstance;
    }

    public static MixingStationPreset GetNewBlankPreset()
    {
      MixingStationPreset preset = GetDefaultPreset().GetCopy() as MixingStationPreset;
      preset.PresetName = "New Preset";
      return preset;
    }
  }

  public class MixingStationPresetEditScreen : PresetEditScreen
  {
    public GenericOptionUI MixerItemUI;
    private MixingStationPreset castedPreset;
    private ItemField boundItemField;

    public override void Awake()
    {
      base.Awake();
      if (MixerItemUI != null)
      {
        MixerItemUI.Button.onClick.RemoveAllListeners();
        MixerItemUI.Button.onClick.AddListener(MixerItemUIClicked);
      }
      else
      {
        MelonLogger.Error("MixingStationPresetEditScreen: MixerItemUI is null in Awake");
      }
    }

    protected virtual void Update()
    {
      if (isOpen)
        UpdateUI();
    }

    public override void Open(Preset preset)
    {
      base.Open(preset);
      castedPreset = preset as MixingStationPreset;
      boundItemField = null;
      if (castedPreset == null)
      {
        MelonLogger.Error("MixingStationPresetEditScreen: Preset is not a MixingStationPreset");
        return;
      }
      UpdateUI();
    }

    public void Open(ItemField itemField)
    {
      if (itemField == null)
      {
        MelonLogger.Error("MixingStationPresetEditScreen: ItemField is null");
        return;
      }

      boundItemField = itemField;
      var preset = MixingStationPreset.GetNewBlankPreset();
      preset.MixerItems = new ItemList(
          "Mixer",
          itemField.Options.Select(item => item.ID).ToList(),
          true,
          true
      );
      if (itemField.SelectedItem != null)
        preset.MixerItems.Selection = [itemField.SelectedItem.ID];
      base.Open(preset);
      castedPreset = preset;
      UpdateUI();
    }

    private void UpdateUI()
    {
      if (castedPreset == null)
      {
        MelonLogger.Error("MixingStationPresetEditScreen: castedPreset is null in UpdateUI");
        return;
      }

      if (MixerItemUI == null)
      {
        MelonLogger.Error("MixingStationPresetEditScreen: MixerItemUI is null in UpdateUI");
        return;
      }

      MixerItemUI.ValueLabel.text = boundItemField != null
          ? (boundItemField.SelectedItem?.Name ?? "Select Mixer")
          : castedPreset.MixerItems.GetDisplayString();
    }

    private void MixerItemUIClicked()
    {
      if (castedPreset == null)
      {
        MelonLogger.Error("MixingStationPresetEditScreen: castedPreset is null in MixerItemUIClicked");
        return;
      }

      Singleton<ItemSetterScreen>.Instance.Open(castedPreset.MixerItems);
    }
  }

  [HarmonyPatch(typeof(Preset), "GetDefault")]
  public class PresetPatch
  {
    static bool Prefix(ManageableObjectType type, ref Preset __result)
    {
      if (type == (ManageableObjectType)100)
      {
        __result = MixingStationPreset.GetDefaultPreset();
        return false;
      }
      return true;
    }
  }
}