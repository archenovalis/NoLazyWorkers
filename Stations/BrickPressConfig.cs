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
using Object = UnityEngine.Object;
using static NoLazyWorkers.NoLazyUtilities;
using ScheduleOne.NPCs;
using ScheduleOne.Employees;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Stations.BrickPressExtensions;
using static NoLazyWorkers.Stations.BrickPressUtilities;
using NoLazyWorkers.Stations;
using NoLazyWorkers;
using ScheduleOne;
using ScheduleOne.Property;
using NoLazyWorkers.General;

namespace NoLazyWorkers.Stations
{
  public static class BrickPressConstants
  {
    public const int MaxOptions = 5;
    public const string ItemFieldUIPrefix = "ItemFieldUI_";
    public const string QualityFieldUIPrefix = "QualityFieldUI_";
    public const float ItemFieldUIOffsetY = -15f;
    public const float QualityFieldUIOffsetY = -83f;
    public const float UIVerticalSpacing = 104f;
  }

  public static class BrickPressExtensions
  {
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, List<QualityField>> QualityFields { get; } = new();

    public class BrickPressAdapter : IStationAdapter
    {
      private readonly BrickPress _station;

      public BrickPressAdapter(BrickPress station)
      {
        _station = station ?? throw new ArgumentNullException(nameof(station));
        if (!PropertyStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          propertyStations = new List<IStationAdapter>();
          PropertyStations[station.ParentProperty] = propertyStations;
        }
        propertyStations.Add(this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"BrickPressAdapter: Initialized for station {station.GUID}", DebugLogger.Category.BrickPress);
      }

      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemSlot> InsertSlots => _station.ProductSlots.ToList();
      public List<ItemSlot> ProductSlots => _station.ProductSlots.ToList();
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable)?.IsInUse ?? false;
      public bool HasActiveOperation => IsInUse;
      public int StartThreshold => 1;
      public List<ItemField> GetInputItemForProduct() => ItemFields.TryGetValue(GUID, out var fields) ? fields : new List<ItemField>();
      public void StartOperation(Employee employee) => (employee as Packager)?.StartPress(_station);
      public int MaxProductQuantity => ProductSlots?.FirstOrDefault(s => s.ItemInstance != null)?.ItemInstance?.StackLimit * 2 ?? Registry.GetItem("ogkush").GetDefaultInstance().StackLimit * 2;
      public ITransitEntity TransitEntity => _station;
      public List<ItemInstance> RefillList() => GetRefillList(_station);
      public bool CanRefill(ItemInstance item) => item != null && RefillList().Any(i => item.AdvCanStackWith(i, allowHigherQuality: true));
      public Type TypeOf => _station.GetType();
    }
  }

  public static class BrickPressUtilities
  {
    /// <summary>
    /// Cleans up data associated with the DryingRack.
    /// </summary>
    public static void Cleanup(BrickPress station)
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

    public static List<ItemInstance> GetRefillList(BrickPress station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Retrieving for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.BrickPress);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetRefillList: Station is null", DebugLogger.Category.BrickPress);
        return new List<ItemInstance>();
      }

      var items = new List<ItemInstance>(BrickPressConstants.MaxOptions);
      if (ItemFields.TryGetValue(station.GUID, out var fields) &&
          QualityFields.TryGetValue(station.GUID, out var qualities) &&
          fields != null && qualities != null && fields.Count == qualities.Count)
      {
        for (int i = 0; i < fields.Count; i++)
        {
          var item = fields[i]?.SelectedItem;
          if (item == null) continue;
          var prodItem = item.GetDefaultInstance() as ProductItemInstance;
          if (prodItem != null)
          {
            prodItem.SetQuality(qualities[i].Value);
            items.Add(prodItem);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Added item {item.ID} with quality {qualities[i].Value} at index {i}", DebugLogger.Category.BrickPress);
          }
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetRefillList: Returned {items.Count} items for station {station.GUID}", DebugLogger.Category.BrickPress);
      return items;
    }

    public static void InitializeItemFields(BrickPress station, BrickPressConfiguration config)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Starting for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.BrickPress);
      try
      {
        if (station == null || config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InitializeItemFields: Station or config is null", DebugLogger.Category.BrickPress);
          return;
        }

        Guid guid = station.GUID;
        if (!StationAdapters.ContainsKey(guid))
        {
          StationAdapters[guid] = new BrickPressAdapter(station);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeItemFields: Created adapter for station {guid}", DebugLogger.Category.BrickPress);
        }

        if (!StationRefills.ContainsKey(guid))
          StationRefills[guid] = new List<ItemInstance>(BrickPressConstants.MaxOptions);

        while (StationRefills[guid].Count < BrickPressConstants.MaxOptions)
          StationRefills[guid].Add(null);

        var itemFields = new List<ItemField>(BrickPressConstants.MaxOptions);
        var qualityFields = new List<QualityField>(BrickPressConstants.MaxOptions);

        for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
        {
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
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Set quality {quality} for index {i}", DebugLogger.Category.BrickPress);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed to set quality for index {i}, error: {e.Message}", DebugLogger.Category.BrickPress);
            }
          });
          qualityFields.Add(qualityField);

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
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Set item {item?.ID ?? "null"} for index {i}", DebugLogger.Category.BrickPress);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed to set item for index {i}, error: {e.Message}", DebugLogger.Category.BrickPress);
            }
          });
          itemFields.Add(itemField);
        }

        ItemFields[guid] = itemFields;
        QualityFields[guid] = qualityFields;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeItemFields: Initialized {itemFields.Count} fields for station {guid}", DebugLogger.Category.BrickPress);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed, error: {e.Message}", DebugLogger.Category.BrickPress);
      }
    }
  }

  [HarmonyPatch(typeof(BrickPressConfigPanel))]
  public class BrickPressPanelPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(BrickPressConfigPanel __instance, List<EntityConfiguration> configs)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"BindPostfix: Starting for {configs?.Count ?? 0} configs", DebugLogger.Category.BrickPress);
      try
      {
        if (configs == null || !configs.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, "BindPostfix: No configurations provided", DebugLogger.Category.BrickPress);
          return;
        }

        __instance.DestinationUI?.gameObject.SetActive(false);
        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        var qualityUITemplate = dryingRackPanelObj.transform.Find("QualityFieldUI");
        var potPanel = GetPrefabGameObject("PotConfigPanel");
        var itemUITemplate = potPanel.GetComponentInChildren<ItemFieldUI>();

        if (itemUITemplate == null || qualityUITemplate == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "BindPostfix: Missing UI templates", DebugLogger.Category.BrickPress);
          return;
        }

        var favorites = new List<ItemDefinition>
                {
                    new ItemDefinition { Name = "None", ID = "None", Icon = StorageConfigUtilities.GetCrossSprite()  },
                    new ItemDefinition { Name = "Any", ID = "Any" }
                };
        if (ProductManager.FavouritedProducts != null)
          favorites.AddRange(ProductManager.FavouritedProducts.Where(item => item != null));

        var itemFieldLists = new List<ItemField>[BrickPressConstants.MaxOptions];
        var qualityFieldLists = new List<QualityField>[BrickPressConstants.MaxOptions];
        for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
        {
          itemFieldLists[i] = new List<ItemField>();
          qualityFieldLists[i] = new List<QualityField>();
        }

        foreach (var config in configs.OfType<BrickPressConfiguration>())
        {
          if (config?.BrickPress == null) continue;
          if (!ItemFields.TryGetValue(config.BrickPress.GUID, out var itemFields) ||
              !QualityFields.TryGetValue(config.BrickPress.GUID, out var qualityFields))
          {
            InitializeItemFields(config.BrickPress, config);
            itemFields = ItemFields.GetValueOrDefault(config.BrickPress.GUID);
            qualityFields = QualityFields.GetValueOrDefault(config.BrickPress.GUID);
          }

          if (itemFields == null || qualityFields == null) continue;

          foreach (var field in itemFields)
            field.Options = favorites;

          for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
          {
            itemFieldLists[i].Add(itemFields[i]);
            qualityFieldLists[i].Add(qualityFields[i]);
          }
        }

        for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
        {
          var itemUIObj = Object.Instantiate(itemUITemplate.gameObject, __instance.transform, false);
          itemUIObj.name = $"{BrickPressConstants.ItemFieldUIPrefix}{i}";
          var itemFieldUI = itemUIObj.GetComponent<ItemFieldUI>();
          itemFieldUI.ShowNoneAsAny = false;
          itemUIObj.SetActive(true);

          var qualityUIObj = Object.Instantiate(qualityUITemplate.gameObject, __instance.transform, false);
          qualityUIObj.name = $"{BrickPressConstants.QualityFieldUIPrefix}{i}";
          qualityUIObj.SetActive(true);

          var rect = itemFieldUI.GetComponent<RectTransform>();
          var qualRect = qualityUIObj.GetComponent<RectTransform>();
          if (rect != null && qualRect != null)
          {
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, BrickPressConstants.ItemFieldUIOffsetY - BrickPressConstants.UIVerticalSpacing * i);
            qualRect.anchoredPosition = new Vector2(rect.anchoredPosition.x, BrickPressConstants.QualityFieldUIOffsetY - BrickPressConstants.UIVerticalSpacing * i);
          }

          foreach (var text in itemFieldUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (text.gameObject.name == "Title" || text.gameObject.name.Contains("Title"))
            {
              text.text = $"Item {i + 1}";
              break;
            }
          }

          var qualTitle = qualityUIObj.transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
          if (qualTitle != null)
            qualTitle.text = "Min Quality";

          itemFieldUI.Bind(itemFieldLists[i]);
          qualityUIObj.GetComponent<QualityFieldUI>().Bind(qualityFieldLists[i]);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"BindPostfix: Bound {BrickPressConstants.ItemFieldUIPrefix}{i} to {itemFieldLists[i].Count} fields", DebugLogger.Category.BrickPress);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"BindPostfix: Failed, error: {e.Message}", DebugLogger.Category.BrickPress, DebugLogger.Category.Stacktrace);
      }
    }
  }

  [HarmonyPatch(typeof(BrickPressConfiguration))]
  public class BrickPressConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(BrickPress) })]
    static void ConstructorPostfix(BrickPressConfiguration __instance, BrickPress station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ConstructorPostfix: Starting for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.BrickPress);
      try
      {
        if (!ItemFields.ContainsKey(station.GUID))
          InitializeItemFields(station, __instance);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ConstructorPostfix: Failed, error: {e.Message}", DebugLogger.Category.BrickPress);
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("ShouldSave")]
    static bool ShouldSavePrefix(ref bool __result)
    {
      __result = true;
      return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(BrickPressConfiguration __instance, ref string __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetSaveStringPostfix: Starting for station {__instance?.BrickPress?.GUID.ToString() ?? "null"}", DebugLogger.Category.BrickPress);
      try
      {
        if (__instance?.BrickPress == null) return;
        var json = JObject.Parse(__result);
        var guid = __instance.BrickPress.GUID;

        if (ItemFields.TryGetValue(guid, out var itemFields))
        {
          var itemFieldsData = new JArray();
          for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
            itemFieldsData.Add(new JObject { ["ItemID"] = itemFields[i].SelectedItem?.ID });
          json["ItemFields"] = itemFieldsData;
        }

        if (QualityFields.TryGetValue(guid, out var qualityFields))
        {
          var qualityFieldsArray = new JArray();
          for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
            qualityFieldsArray.Add(new JObject { ["Quality"] = qualityFields[i].Value.ToString() });
          json["Qualities"] = qualityFieldsArray;
        }

        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetSaveStringPostfix: Saved JSON for station {guid}", DebugLogger.Category.BrickPress);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetSaveStringPostfix: Failed, error: {e.Message}", DebugLogger.Category.BrickPress);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(BrickPressConfiguration __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DestroyPostfix: Starting for station {__instance?.BrickPress?.GUID.ToString() ?? "null"}", DebugLogger.Category.BrickPress);
      try
      {
        if (__instance?.BrickPress != null)
          Cleanup(__instance.BrickPress);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"DestroyPostfix: Failed, error: {e.Message}", DebugLogger.Category.BrickPress);
      }
    }
  }

  [HarmonyPatch(typeof(BrickPressLoader))]
  public class BrickPressLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(string mainPath)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Starting for {mainPath}", DebugLogger.Category.BrickPress);
      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || gridItem == null || !(gridItem is BrickPress station))
          return;

        string configPath = System.IO.Path.Combine(mainPath, "Configuration.json");
        if (!System.IO.File.Exists(configPath))
          return;

        string json = System.IO.File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.stationConfiguration;
        if (config == null)
          return;

        if (config.Destination.SelectedObject != null)
        {
          config.Destination.SelectedObject = null;
          config.DestinationRoute = null;
        }

        Guid guid = station.GUID;
        if (!ItemFields.ContainsKey(guid))
          InitializeItemFields(station, config);

        var itemFields = ItemFields[guid];
        var qualityFields = QualityFields[guid];
        var itemFieldsData = jsonObject["ItemFields"] as JArray;
        var qualityFieldsData = jsonObject["Qualities"] as JArray;

        if (itemFieldsData != null && itemFieldsData.Count <= BrickPressConstants.MaxOptions)
        {
          for (int i = 0; i < BrickPressConstants.MaxOptions; i++)
          {
            var qualityData = qualityFieldsData?[i] as JObject;
            if (qualityData != null && qualityData["Quality"] != null)
              qualityFields[i].SetValue(Enum.Parse<EQuality>(qualityData["Quality"].ToString()), false);

            var itemFieldData = itemFieldsData[i] as JObject;
            if (itemFieldData != null && itemFieldData["ItemID"] != null)
            {
              string itemID = itemFieldData["ItemID"].ToString();
              if (!string.IsNullOrEmpty(itemID))
              {
                var item = Registry.GetItem(itemID);
                if (item != null)
                  itemFields[i].SelectedItem = item;
              }
            }
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"LoadPostfix: Loaded config for station {guid}", DebugLogger.Category.BrickPress);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Failed, error: {e.Message}", DebugLogger.Category.BrickPress);
      }
    }
  }
}