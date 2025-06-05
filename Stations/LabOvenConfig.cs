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
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Stations.LabOvenExtensions;
using static NoLazyWorkers.Stations.LabOvenUtilities;
using NoLazyWorkers.Stations;
using NoLazyWorkers;
using ScheduleOne;
using ScheduleOne.Property;
using NoLazyWorkers.Storage;
using ScheduleOne.EntityFramework;

namespace NoLazyWorkers.Stations
{
  public static class LabOvenConstants
  {
    public const int MaxOptions = 5;
    public const string ItemFieldUIPrefix = "ItemFieldUI_";
    public const string QualityFieldUIPrefix = "QualityFieldUI_";
    public const float ItemFieldUIOffsetY = -15f;
    public const float QualityFieldUIOffsetY = -83f;
    public const float UIVerticalSpacing = 104f;
  }

  public static class LabOvenExtensions
  {
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, List<QualityField>> QualityFields { get; } = new();

    public class LabOvenAdapter : IStationAdapter
    {
      private readonly LabOven _station;

      public LabOvenAdapter(LabOven station)
      {
        _station = station ?? throw new ArgumentNullException(nameof(station));
        if (!Extensions.IStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          propertyStations = new();
          Extensions.IStations[station.ParentProperty] = propertyStations;
        }
        propertyStations.Add(GUID, this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"LabOvenAdapter: Initialized for station {station.GUID}", DebugLogger.Category.LabOven);
      }

      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemSlot> InsertSlots => new List<ItemSlot>();
      public List<ItemSlot> ProductSlots => new List<ItemSlot> { _station.IngredientSlot };
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable)?.IsInUse ?? false;
      public bool HasActiveOperation => IsInUse;
      public int StartThreshold => 1;
      public List<ItemField> GetInputItemForProduct() => ItemFields.TryGetValue(GUID, out var fields) ? fields : new List<ItemField>();
      public void StartOperation(Employee employee) => (employee as Chemist)?.StartLabOven(_station);
      public int MaxProductQuantity => _station.IngredientSlot?.ItemInstance?.StackLimit ?? 0;
      public ITransitEntity TransitEntity => _station as ITransitEntity;
      public BuildableItem Buildable => _station as BuildableItem;
      public Property ParentProperty => _station.ParentProperty;
      public List<ItemInstance> RefillList() => GetRefillList(_station);
      public bool CanRefill(ItemInstance item) => item != null && RefillList().Any(i => Utilities.AdvCanStackWith(i, item));
      public Type TypeOf => _station.GetType();
    }
  }

  public static class LabOvenUtilities
  {
    /// <summary>
    /// Cleans up data associated with the DryingRack.
    /// </summary>
    public static void Cleanup(LabOven station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Cleanup: Starting cleanup for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "Cleanup: Station is null", DebugLogger.Category.DryingRack);
        return;
      }

      ItemFields.Remove(station.GUID);
      QualityFields.Remove(station.GUID);
      StationRefillLists.Remove(station.GUID);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Cleanup: Removed data for station {station.GUID}", DebugLogger.Category.DryingRack);
    }

    public static List<ItemInstance> GetRefillList(LabOven station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Retrieving for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.LabOven);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetRefillList: Station is null", DebugLogger.Category.LabOven);
        return new List<ItemInstance>();
      }

      var items = new List<ItemInstance>(LabOvenConstants.MaxOptions);
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
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetRefillList: Added item {item.ID} with quality {qualities[i].Value} at index {i}", DebugLogger.Category.LabOven);
          }
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetRefillList: Returned {items.Count} items for station {station.GUID}", DebugLogger.Category.LabOven);
      return items;
    }

    public static void InitializeItemFields(LabOven station, LabOvenConfiguration config)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Starting for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.LabOven);
      try
      {
        if (station == null || config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InitializeItemFields: Station or config is null", DebugLogger.Category.LabOven);
          return;
        }

        Guid guid = station.GUID;
        if (!IStations[station.ParentProperty].ContainsKey(guid))
        {
          IStations[station.ParentProperty][guid] = new LabOvenAdapter(station);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeItemFields: Created adapter for station {guid}", DebugLogger.Category.LabOven);
        }

        if (!StationRefillLists.ContainsKey(guid))
          StationRefillLists[guid] = new List<ItemInstance>(LabOvenConstants.MaxOptions);

        while (StationRefillLists[guid].Count < LabOvenConstants.MaxOptions)
          StationRefillLists[guid].Add(null);

        var itemFields = new List<ItemField>(LabOvenConstants.MaxOptions);
        var qualityFields = new List<QualityField>(LabOvenConstants.MaxOptions);

        for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
        {
          var qualityField = new QualityField(config);
          qualityField.onValueChanged.RemoveAllListeners();
          qualityField.onValueChanged.AddListener(quality =>
          {
            try
            {
              var refills = StationRefillLists[guid];
              if (i < refills.Count && refills[i] is ProductItemInstance prodItem)
              {
                prodItem.SetQuality(quality);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Set quality {quality} for index {i}", DebugLogger.Category.LabOven);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed to set quality for index {i}, error: {e.Message}", DebugLogger.Category.LabOven);
            }
          });
          qualityFields.Add(qualityField);

          var itemField = new ItemField(config) { CanSelectNone = false };
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(item =>
          {
            try
            {
              var refills = StationRefillLists[guid];
              if (i < refills.Count)
              {
                refills[i] = item?.GetDefaultInstance();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Set item {item?.ID ?? "null"} for index {i}", DebugLogger.Category.LabOven);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed to set item for index {i}, error: {e.Message}", DebugLogger.Category.LabOven);
            }
          });
          itemFields.Add(itemField);
        }

        ItemFields[guid] = itemFields;
        QualityFields[guid] = qualityFields;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeItemFields: Initialized {itemFields.Count} fields for station {guid}", DebugLogger.Category.LabOven);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed, error: {e.Message}", DebugLogger.Category.LabOven);
      }
    }
  }

  [HarmonyPatch(typeof(LabOvenConfigPanel))]
  public class LabOvenPanelPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(LabOvenConfigPanel __instance, List<EntityConfiguration> configs)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"BindPostfix: Starting for {configs?.Count ?? 0} configs", DebugLogger.Category.LabOven);
      try
      {
        __instance.DestinationUI?.gameObject.SetActive(false);

        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        var qualityFieldTemplate = dryingRackPanelObj.transform.Find("QualityFieldUI");
        var potPanel = GetPrefabGameObject("PotConfigPanel");
        var itemFieldTemplate = potPanel.GetComponentInChildren<ItemFieldUI>();

        if (itemFieldTemplate == null || qualityFieldTemplate == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "BindPostfix: Missing UI templates", DebugLogger.Category.LabOven);
          return;
        }

        var favorites = new List<ItemDefinition>
                {
                    new ItemDefinition { Name = "None", ID = "None", Icon = ShelfUtilities.GetCrossSprite()  },
                    new ItemDefinition { Name = "Any", ID = "Any" }
                };
        if (ProductManager.FavouritedProducts != null)
          favorites.AddRange(ProductManager.FavouritedProducts.Where(item => item != null));

        var itemFieldLists = new List<ItemField>[LabOvenConstants.MaxOptions];
        var qualityFieldLists = new List<QualityField>[LabOvenConstants.MaxOptions];
        for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
        {
          itemFieldLists[i] = new List<ItemField>();
          qualityFieldLists[i] = new List<QualityField>();
        }

        foreach (var config in configs.OfType<LabOvenConfiguration>())
        {
          if (config?.Oven == null) continue;
          if (!ItemFields.TryGetValue(config.Oven.GUID, out var itemFields) ||
              !QualityFields.TryGetValue(config.Oven.GUID, out var qualityFields))
          {
            InitializeItemFields(config.Oven, config);
            itemFields = ItemFields.GetValueOrDefault(config.Oven.GUID);
            qualityFields = QualityFields.GetValueOrDefault(config.Oven.GUID);
          }

          if (itemFields == null || qualityFields == null) continue;

          foreach (var field in itemFields)
            field.Options = favorites;

          for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
          {
            itemFieldLists[i].Add(itemFields[i]);
            qualityFieldLists[i].Add(qualityFields[i]);
          }
        }

        for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
        {
          var uiObj = Object.Instantiate(itemFieldTemplate.gameObject, __instance.transform, false);
          uiObj.name = $"{LabOvenConstants.ItemFieldUIPrefix}{i}";
          var itemFieldUI = uiObj.GetComponent<ItemFieldUI>() ?? uiObj.AddComponent<ItemFieldUI>();
          itemFieldUI.ShowNoneAsAny = false;
          uiObj.SetActive(true);

          var qualityUIObj = Object.Instantiate(qualityFieldTemplate.gameObject, __instance.transform, false);
          qualityUIObj.name = $"{LabOvenConstants.QualityFieldUIPrefix}{i}";
          qualityUIObj.SetActive(true);

          var rect = itemFieldUI.GetComponent<RectTransform>();
          var qualRect = qualityUIObj.GetComponent<RectTransform>();
          if (rect != null && qualRect != null)
          {
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, LabOvenConstants.ItemFieldUIOffsetY - LabOvenConstants.UIVerticalSpacing * i);
            qualRect.anchoredPosition = new Vector2(rect.anchoredPosition.x, LabOvenConstants.QualityFieldUIOffsetY - LabOvenConstants.UIVerticalSpacing * i);
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
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"BindPostfix: Bound {LabOvenConstants.ItemFieldUIPrefix}{i} to {itemFieldLists[i].Count} fields", DebugLogger.Category.LabOven);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"BindPostfix: Failed, error: {e.Message}", DebugLogger.Category.LabOven);
      }
    }
  }

  [HarmonyPatch(typeof(LabOvenConfiguration))]
  public class LabOvenConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(LabOven) })]
    static void ConstructorPostfix(LabOvenConfiguration __instance, LabOven oven)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ConstructorPostfix: Starting for station {oven?.GUID.ToString() ?? "null"}", DebugLogger.Category.LabOven);
      try
      {
        if (!ItemFields.ContainsKey(oven.GUID))
          InitializeItemFields(oven, __instance);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ConstructorPostfix: Failed, error: {e.Message}", DebugLogger.Category.LabOven);
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
    static void GetSaveStringPostfix(LabOvenConfiguration __instance, ref string __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetSaveStringPostfix: Starting for station {__instance?.Oven?.GUID.ToString() ?? "null"}", DebugLogger.Category.LabOven);
      try
      {
        if (__instance?.Oven == null) return;
        var json = JObject.Parse(__result);
        var guid = __instance.Oven.GUID;

        if (ItemFields.TryGetValue(guid, out var itemFields))
        {
          var itemFieldsData = new JArray();
          for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
            itemFieldsData.Add(new JObject { ["ItemID"] = itemFields[i].SelectedItem?.ID });
          json["ItemFields"] = itemFieldsData;
        }

        if (QualityFields.TryGetValue(guid, out var qualityFields))
        {
          var qualityFieldsArray = new JArray();
          for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
            qualityFieldsArray.Add(new JObject { ["Quality"] = qualityFields[i].Value.ToString() });
          json["Qualities"] = qualityFieldsArray;
        }

        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetSaveStringPostfix: Saved JSON for station {guid}", DebugLogger.Category.LabOven);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetSaveStringPostfix: Failed, error: {e.Message}", DebugLogger.Category.LabOven);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(LabOvenConfiguration __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DestroyPostfix: Starting for station {__instance?.Oven?.GUID.ToString() ?? "null"}", DebugLogger.Category.LabOven);
      try
      {
        if (__instance?.Oven != null)
          Cleanup(__instance.Oven);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"DestroyPostfix: Failed, error: {e.Message}", DebugLogger.Category.LabOven);
      }
    }
  }

  [HarmonyPatch(typeof(LabOvenLoader))]
  public class LabOvenLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(string mainPath)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Starting for {mainPath}", DebugLogger.Category.LabOven);
      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || gridItem == null || !(gridItem is LabOven station))
          return;

        string configPath = System.IO.Path.Combine(mainPath, "Configuration.json");
        if (!System.IO.File.Exists(configPath))
          return;

        string json = System.IO.File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.ovenConfiguration;
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

        if (itemFieldsData != null && itemFieldsData.Count <= LabOvenConstants.MaxOptions)
        {
          for (int i = 0; i < LabOvenConstants.MaxOptions; i++)
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

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"LoadPostfix: Loaded config for station {guid}", DebugLogger.Category.LabOven);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Failed, error: {e.Message}", DebugLogger.Category.LabOven);
      }
    }
  }
}