using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.DevUtilities;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.Property;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Stations.DryingRackExtensions;
using static NoLazyWorkers.Stations.DryingRackUtilities;
using ScheduleOne.NPCs;
using ScheduleOne.Employees;
using NoLazyWorkers.General;

namespace NoLazyWorkers.Stations
{
  /// <summary>
  /// Constants for DryingRack to avoid magic numbers and strings.
  /// </summary>
  public static class DryingRackConstants
  {
    public const int MaxOptions = 4;
    public const string ItemFieldUIPrefix = "ItemFieldUI_";
    public const string QualityFieldUIPrefix = "QualityFieldUI_";
    public const float ItemFieldUIOffsetY = -95f;
    public const float QualityFieldUIOffsetY = -163f;
    public const float UIVerticalSpacing = 104f;
  }

  /// <summary>
  /// Manages DryingRack-specific data and adapters.
  /// </summary>
  public static class DryingRackExtensions
  {
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, List<QualityField>> QualityFields { get; } = new();

    /// <summary>
    /// Adapter for DryingRack to integrate with station system.
    /// </summary>
    public class DryingRackAdapter : IStationAdapter
    {
      private readonly DryingRack _station;

      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemSlot> InsertSlots => new List<ItemSlot> { _station.InputSlot };
      public List<ItemSlot> ProductSlots => new List<ItemSlot> { _station.InputSlot };
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable)?.IsInUse ?? false;
      public bool HasActiveOperation => IsInUse;
      public int StartThreshold => 1;
      public List<ItemField> GetInputItemForProduct() => ItemFields.TryGetValue(GUID, out var fields) ? fields : new List<ItemField>();
      public void StartOperation(Employee employee) => (employee as Botanist)?.StartDryingRack(_station);
      public int MaxProductQuantity => _station.InputSlot?.ItemInstance?.StackLimit ?? 0;
      public ITransitEntity TransitEntity => _station;
      public List<ItemInstance> RefillList() => GetRefillList(_station);
      public bool CanRefill(ItemInstance item) => item != null && RefillList().Any(i => StorageUtilities.AdvCanStackWith(i, item));
      public Type TypeOf => _station.GetType();

      public DryingRackAdapter(DryingRack station)
      {
        // Validate input
        if (station == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "DryingRackAdapter: Station is null", DebugLogger.Category.DryingRack);
          throw new ArgumentNullException(nameof(station));
        }

        _station = station;

        // Initialize property stations list if not present
        if (!PropertyStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          propertyStations = new List<IStationAdapter>();
          PropertyStations[station.ParentProperty] = propertyStations;
        }

        // Add adapter to property stations
        propertyStations.Add(this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DryingRackAdapter: Initialized for station {station.GUID}", DebugLogger.Category.DryingRack);
      }
    }
  }

  /// <summary>
  /// Utility methods for DryingRack operations.
  /// </summary>
  public static class DryingRackUtilities
  {
    /// <summary>
    /// Retrieves the list of items for refilling the DryingRack.
    /// </summary>
    public static List<ItemInstance> GetRefillList(DryingRack station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Retrieving refill list for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetRefillList: Station is null", DebugLogger.Category.DryingRack);
        return new List<ItemInstance>();
      }

      var items = new List<ItemInstance>(DryingRackConstants.MaxOptions);
      if (ItemFields.TryGetValue(station.GUID, out var fields) &&
          QualityFields.TryGetValue(station.GUID, out var qualities) &&
          fields != null && qualities != null && fields.Count == qualities.Count)
      {
        for (int i = 0; i < fields.Count; i++)
        {
          var item = fields[i]?.SelectedItem;
          if (item == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Skipping null item at index {i} for station {station.GUID}", DebugLogger.Category.DryingRack);
            continue;
          }

          var prodItem = item.GetDefaultInstance() as ProductItemInstance;
          if (prodItem != null)
          {
            prodItem.SetQuality(qualities[i].Value);
            items.Add(prodItem);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Added item {item.ID} with quality {qualities[i].Value} at index {i}", DebugLogger.Category.DryingRack);
          }
        }
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetRefillList: Fields or qualities missing or mismatched for station {station.GUID}", DebugLogger.Category.DryingRack);
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetRefillList: Returned {items.Count} items for station {station.GUID}", DebugLogger.Category.DryingRack);
      return items;
    }

    /// <summary>
    /// Cleans up data associated with the DryingRack.
    /// </summary>
    public static void Cleanup(DryingRack station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Cleanup: Starting cleanup for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "Cleanup: Station is null", DebugLogger.Category.DryingRack);
        return;
      }

      ItemFields.Remove(station.GUID);
      QualityFields.Remove(station.GUID);
      StationRefills.Remove(station.GUID);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Cleanup: Removed data for station {station.GUID}", DebugLogger.Category.DryingRack);
    }

    /// <summary>
    /// Initializes item and quality fields for the DryingRack.
    /// </summary>
    public static void InitializeFields(DryingRack station, DryingRackConfiguration config)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeFields: Starting for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      try
      {
        if (station == null || config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeFields: Station or config is null", DebugLogger.Category.DryingRack);
          return;
        }

        Guid guid = station.GUID;

        // Initialize adapter if not present
        if (!StationAdapters.TryGetValue(guid, out var adapter))
        {
          adapter = new DryingRackAdapter(station);
          StationAdapters[guid] = adapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeFields: Created new adapter for station {guid}", DebugLogger.Category.DryingRack);
        }

        // Initialize station refills list
        if (!StationRefills.ContainsKey(guid))
        {
          StationRefills[guid] = new List<ItemInstance>(DryingRackConstants.MaxOptions + 1);
        }

        // Ensure refills list has enough slots
        while (StationRefills[guid].Count < DryingRackConstants.MaxOptions + 1)
        {
          StationRefills[guid].Add(null);
        }

        // Initialize item and quality fields
        var itemFields = new List<ItemField>(DryingRackConstants.MaxOptions + 1);
        var qualityFields = new List<QualityField>(DryingRackConstants.MaxOptions + 1);

        for (int i = 0; i < DryingRackConstants.MaxOptions + 1; i++)
        {
          // Create and configure quality field
          var qualityField = new QualityField(config);
          qualityField.onValueChanged.RemoveAllListeners();
          qualityField.onValueChanged.AddListener(quality =>
          {
            try
            {
              var refills = StationRefills[guid];
              if (i < refills.Count && refills[i] is ProductItemInstance prodItem)
              {
                prodItem.SetQuality(quality);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeFields: Set quality {quality} for index {i} in station {guid}", DebugLogger.Category.DryingRack);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeFields: Failed to set quality for index {i} in station {guid}, error: {e.Message}", DebugLogger.Category.DryingRack);
            }
          });
          qualityFields.Add(qualityField);

          // Create and configure item field
          var itemField = new ItemField(config) { CanSelectNone = false };
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(item =>
          {
            try
            {
              var refills = StationRefills[guid];
              if (i < refills.Count)
              {
                refills[i] = item?.GetDefaultInstance();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeFields: Set item {item?.ID ?? "null"} for index {i} in station {guid}", DebugLogger.Category.DryingRack);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeFields: Failed to set item for index {i} in station {guid}, error: {e.Message}", DebugLogger.Category.DryingRack);
            }
          });
          itemFields.Add(itemField);
        }

        // Store fields
        ItemFields[guid] = itemFields;
        QualityFields[guid] = qualityFields;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeFields: Initialized {itemFields.Count} ItemFields and {qualityFields.Count} QualityFields for station {guid}", DebugLogger.Category.DryingRack);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeFields: Failed for station {station?.GUID.ToString() ?? "null"}, error: {e.Message}", DebugLogger.Category.DryingRack);
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeFields: Completed for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);
    }
  }

  /// <summary>
  /// Harmony patch for DryingRackConfigPanel to customize UI binding.
  /// </summary>
  [HarmonyPatch(typeof(DryingRackConfigPanel))]
  public class DryingRackPanelPatch
  {
    private static ItemFieldUI _cachedItemFieldUITemplate;

    /// <summary>
    /// Caches UI prefabs to avoid repeated lookups.
    /// </summary>
    private static void CachePrefabs()
    {
      if (_cachedItemFieldUITemplate == null)
      {
        var potPanel = GetPrefabGameObject("PotConfigPanel");
        _cachedItemFieldUITemplate = potPanel?.GetComponentInChildren<ItemFieldUI>();
        if (_cachedItemFieldUITemplate == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "CachePrefabs: Failed to find ItemFieldUI template", DebugLogger.Category.DryingRack);
        }
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(DryingRackConfigPanel __instance, List<EntityConfiguration> configs)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"BindPostfix: Starting binding for {configs?.Count ?? 0} configs", DebugLogger.Category.DryingRack);

      try
      {
        if (configs == null || !configs.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, "BindPostfix: No configurations provided", DebugLogger.Category.DryingRack);
          return;
        }

        // Disable destination UI
        if (__instance.DestinationUI != null)
        {
          __instance.DestinationUI.gameObject.SetActive(false);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, "BindPostfix: Disabled DestinationUI", DebugLogger.Category.DryingRack);
        }

        // Cache prefabs
        CachePrefabs();
        if (_cachedItemFieldUITemplate == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "BindPostfix: Missing UI templates, aborting", DebugLogger.Category.DryingRack);
          return;
        }
        __instance.QualityUI.transform.Find("Description").gameObject.SetActive(false);
        // Initialize favorite items
        var favorites = new List<ItemDefinition>
                {
                    new ItemDefinition { Name = "None", ID = "None", Icon = StorageConfigUtilities.GetCrossSprite() },
                    new ItemDefinition { Name = "Any", ID = "Any" }
                };
        if (ProductManager.FavouritedProducts != null)
        {
          favorites.AddRange(ProductManager.FavouritedProducts.Where(item => item != null));
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"BindPostfix: Loaded {favorites.Count} favorite items", DebugLogger.Category.DryingRack);
        }

        // Initialize field lists
        var itemFieldLists = new List<ItemField>[DryingRackConstants.MaxOptions];
        var qualityFieldLists = new List<QualityField>[DryingRackConstants.MaxOptions];
        for (int i = 0; i < DryingRackConstants.MaxOptions; i++)
        {
          itemFieldLists[i] = new List<ItemField>();
          qualityFieldLists[i] = new List<QualityField>();
        }

        // Process configurations
        foreach (var config in configs.OfType<DryingRackConfiguration>())
        {
          if (config?.Rack == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, "BindPostfix: Skipping null config or rack", DebugLogger.Category.DryingRack);
            continue;
          }

          // Initialize fields if missing
          if (!ItemFields.TryGetValue(config.Rack.GUID, out var itemFields) ||
              !QualityFields.TryGetValue(config.Rack.GUID, out var qualityFields) ||
              itemFields?.Count < DryingRackConstants.MaxOptions ||
              qualityFields?.Count < DryingRackConstants.MaxOptions)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"BindPostfix: Initializing fields for station {config.Rack.GUID}", DebugLogger.Category.DryingRack);
            InitializeFields(config.Rack, config);
            itemFields = ItemFields.GetValueOrDefault(config.Rack.GUID);
            qualityFields = QualityFields.GetValueOrDefault(config.Rack.GUID);
          }

          if (itemFields == null || qualityFields == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"BindPostfix: Failed to initialize fields for station {config.Rack.GUID}", DebugLogger.Category.DryingRack);
            continue;
          }

          // Assign favorites to item fields
          foreach (var field in itemFields)
          {
            field.Options = favorites;
          }

          // Populate field lists
          for (int i = 0; i < DryingRackConstants.MaxOptions; i++)
          {
            itemFieldLists[i].Add(itemFields[i]);
            qualityFieldLists[i].Add(qualityFields[i]);
          }
        }

        // Create and bind UI elements
        for (int i = 0; i < DryingRackConstants.MaxOptions; i++)
        {
          // Instantiate item field UI
          var uiObj = Object.Instantiate(_cachedItemFieldUITemplate.gameObject, __instance.transform, false);
          uiObj.name = $"{DryingRackConstants.ItemFieldUIPrefix}{i}";
          var itemFieldUI = uiObj.GetComponent<ItemFieldUI>() ?? uiObj.AddComponent<ItemFieldUI>();
          itemFieldUI.ShowNoneAsAny = false;
          uiObj.SetActive(true);

          // Instantiate quality field UI
          var qualityUIObj = Object.Instantiate(__instance.QualityUI.gameObject, __instance.transform, false);
          qualityUIObj.name = $"{DryingRackConstants.QualityFieldUIPrefix}{i}";
          var qualityFieldUI = qualityUIObj.GetComponent<QualityFieldUI>() ?? qualityUIObj.AddComponent<QualityFieldUI>();
          qualityUIObj.SetActive(true);

          // Position UI elements
          var rect = itemFieldUI.GetComponent<RectTransform>();
          var qualRect = qualityFieldUI.GetComponent<RectTransform>();
          if (rect != null && qualRect != null)
          {
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, DryingRackConstants.ItemFieldUIOffsetY - DryingRackConstants.UIVerticalSpacing * i);
            qualRect.anchoredPosition = new Vector2(rect.anchoredPosition.x, DryingRackConstants.QualityFieldUIOffsetY - DryingRackConstants.UIVerticalSpacing * i);
          }

          // Set item field title
          foreach (var text in itemFieldUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (text.gameObject.name == "Title" || text.gameObject.name.Contains("Title"))
            {
              text.text = $"Item {i + 1}";
              break;
            }
          }

          // Set quality field title
          var qualTitle = qualityUIObj.transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
          if (qualTitle != null)
          {
            qualTitle.text = "Min Quality";
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"BindPostfix: QualityFieldUI_{i} missing Title TextMeshProUGUI", DebugLogger.Category.DryingRack);
          }

          // Bind UI elements
          itemFieldUI.Bind(itemFieldLists[i]);
          qualityFieldUI.Bind(qualityFieldLists[i]);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"BindPostfix: Bound {DryingRackConstants.ItemFieldUIPrefix}{i} to {itemFieldLists[i].Count} ItemFields and {DryingRackConstants.QualityFieldUIPrefix}{i} to {qualityFieldLists[i].Count} QualityFields", DebugLogger.Category.DryingRack);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"BindPostfix: Completed binding {DryingRackConstants.MaxOptions} ItemFieldUIs for {configs.Count} configs", DebugLogger.Category.DryingRack);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"BindPostfix: Failed, error: {e.Message}", DebugLogger.Category.DryingRack, DebugLogger.Category.Stacktrace);
      }
    }
  }

  /// <summary>
  /// Harmony patch for DryingRackConfiguration to customize save and destroy behavior.
  /// </summary>
  [HarmonyPatch(typeof(DryingRackConfiguration))]
  public class DryingRackConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(DryingRack) })]
    static void DryingRackConstructorPostfix(DryingRackConfiguration __instance, DryingRack rack)
    {
      try
      {
        if (rack == null || __instance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InitializeFields: rack or Configuration is null", DebugLogger.Category.DryingRack);
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DryingRackConfigurationPatch.ConstructorPostfix: Initializing for rack {rack?.GUID}", DebugLogger.Category.DryingRack);
        if (!ItemFields.ContainsKey(rack.GUID))
        {
          InitializeFields(rack, __instance);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"DryingRackConfigurationPatch.ConstructorPostfix: Failed for rack, error: {e}", DebugLogger.Category.DryingRack);
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("ShouldSave")]
    static bool ShouldSavePrefix(DryingRackConfiguration __instance, ref bool __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ShouldSavePrefix: Forcing save for station {__instance?.Rack?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);
      __result = true;
      return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(DryingRackConfiguration __instance, ref string __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetSaveStringPostfix: Saving for station {__instance?.Rack?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      try
      {
        if (__instance?.Rack == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetSaveStringPostfix: Rack is null", DebugLogger.Category.DryingRack);
          return;
        }

        var json = JObject.Parse(__result);
        var guid = __instance.Rack.GUID;

        // Save item fields
        if (ItemFields.TryGetValue(guid, out var itemFields))
        {
          var itemFieldsData = new JArray();
          for (int i = 0; i < DryingRackConstants.MaxOptions; i++)
          {
            var field = itemFields[i];
            itemFieldsData.Add(new JObject { ["ItemID"] = field.SelectedItem?.ID });
          }
          json["ItemFields"] = itemFieldsData;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetSaveStringPostfix: Saved {itemFieldsData.Count} item fields for station {guid}", DebugLogger.Category.DryingRack);
        }

        // Save quality fields
        if (QualityFields.TryGetValue(guid, out var qualityFields))
        {
          var qualityFieldsArray = new JArray();
          for (int i = 0; i < DryingRackConstants.MaxOptions; i++)
          {
            qualityFieldsArray.Add(new JObject { ["Quality"] = qualityFields[i].Value.ToString() });
          }
          json["Qualities"] = qualityFieldsArray;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetSaveStringPostfix: Saved {qualityFieldsArray.Count} quality fields for station {guid}", DebugLogger.Category.DryingRack);
        }

        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetSaveStringPostfix: Saved JSON for station {guid}", DebugLogger.Category.DryingRack);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetSaveStringPostfix: Failed for station {__instance?.Rack?.GUID.ToString() ?? "null"}, error: {e.Message}", DebugLogger.Category.DryingRack, DebugLogger.Category.Stacktrace);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(DryingRackConfiguration __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DestroyPostfix: Cleaning up for station {__instance?.Rack?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      try
      {
        if (__instance?.Rack == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, "DestroyPostfix: Rack is null", DebugLogger.Category.DryingRack);
          return;
        }

        Cleanup(__instance.Rack);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DestroyPostfix: Completed cleanup for station {__instance.Rack.GUID}", DebugLogger.Category.DryingRack);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"DestroyPostfix: Failed for station {__instance?.Rack?.GUID.ToString() ?? "null"}, error: {e.Message}", DebugLogger.Category.DryingRack, DebugLogger.Category.Stacktrace);
      }
    }
  }

  /// <summary>
  /// Harmony patch for DryingRackLoader to handle configuration loading.
  /// </summary>
  [HarmonyPatch(typeof(DryingRackLoader))]
  public class DryingRackLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(string mainPath)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Loading configuration from {mainPath}", DebugLogger.Category.DryingRack);

      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || gridItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"LoadPostfix: No grid item found for {mainPath}", DebugLogger.Category.DryingRack);
          return;
        }

        if (!(gridItem is DryingRack station))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"LoadPostfix: Grid item is not a DryingRack for {mainPath}", DebugLogger.Category.DryingRack);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"LoadPostfix: Configuration file not found at {configPath}", DebugLogger.Category.DryingRack);
          return;
        }

        string json = File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.stationConfiguration;
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Station configuration is null for {station.GUID}", DebugLogger.Category.DryingRack);
          return;
        }

        // Clear destination settings
        if (config.Destination.SelectedObject != null)
        {
          config.Destination.SelectedObject = null;
          config.DestinationRoute = null;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Cleared destination for station {station.GUID}", DebugLogger.Category.DryingRack);
        }

        Guid guid = station.GUID;
        if (!ItemFields.ContainsKey(guid))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"LoadPostfix: Initializing fields for station {guid}", DebugLogger.Category.DryingRack);
          InitializeFields(station, config);
        }

        // Retrieve fields
        var itemFields = ItemFields.GetValueOrDefault(guid);
        var qualityFields = QualityFields.GetValueOrDefault(guid);
        if (itemFields == null || qualityFields == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Failed to retrieve fields for station {guid}", DebugLogger.Category.DryingRack);
          return;
        }

        // Load item and quality fields from JSON
        var itemFieldsData = jsonObject["ItemFields"] as JArray;
        var qualityFieldsData = jsonObject["Qualities"] as JArray;

        if (itemFieldsData != null && itemFieldsData.Count <= DryingRackConstants.MaxOptions)
        {
          for (int i = 0; i < DryingRackConstants.MaxOptions; i++)
          {
            // Load item field
            var itemFieldData = itemFieldsData[i] as JObject;
            if (itemFieldData != null && itemFieldData["ItemID"] != null)
            {
              string itemID = itemFieldData["ItemID"].ToString();
              if (!string.IsNullOrEmpty(itemID))
              {
                var item = Registry.GetItem(itemID);
                if (item != null)
                {
                  itemFields[i].SelectedItem = item;
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Loaded item {itemID} for index {i} in station {guid}", DebugLogger.Category.DryingRack);
                }
                else
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Warning, $"LoadPostfix: Item {itemID} not found for index {i} in station {guid}", DebugLogger.Category.DryingRack);
                }
              }
            }

            // Load quality field
            var qualityData = qualityFieldsData?[i] as JObject;
            if (qualityData != null && qualityData["Quality"] != null)
            {
              try
              {
                var quality = Enum.Parse<EQuality>(qualityData["Quality"].ToString());
                qualityFields[i].SetValue(quality, false);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Loaded quality {quality} for index {i} in station {guid}", DebugLogger.Category.DryingRack);
              }
              catch (ArgumentException e)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Invalid quality value {qualityData["Quality"]} for index {i} in station {guid}, error: {e.Message}", DebugLogger.Category.DryingRack);
              }
            }
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"LoadPostfix: Invalid or missing ItemFields/Qualities data for station {guid}", DebugLogger.Category.DryingRack);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"LoadPostfix: Completed loading config for station {guid}", DebugLogger.Category.DryingRack);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Failed for {mainPath}, error: {e.Message}", DebugLogger.Category.DryingRack, DebugLogger.Category.Stacktrace);
      }
    }
  }
}