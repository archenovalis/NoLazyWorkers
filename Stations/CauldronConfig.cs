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
using static NoLazyWorkers.Stations.CauldronExtensions;
using static NoLazyWorkers.Stations.CauldronUtilities;
using NoLazyWorkers.Stations;
using NoLazyWorkers;
using ScheduleOne;
using ScheduleOne.Property;
using NoLazyWorkers.General;
using ScheduleOne.StationFramework;

namespace NoLazyWorkers.Stations
{
  public static class CauldronConstants
  {
    public const int MaxOptions = 1;
    public const string QualityFieldUIPrefix = "QualityFieldUI";
  }

  public static class CauldronExtensions
  {
    public static Dictionary<Guid, QualityField> QualityField { get; } = new();

    public class CauldronAdapter : IStationAdapter
    {
      private readonly Cauldron _station;

      public CauldronAdapter(Cauldron station)
      {
        _station = station ?? throw new ArgumentNullException(nameof(station));
        if (!PropertyStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          propertyStations = new List<IStationAdapter>();
          PropertyStations[station.ParentProperty] = propertyStations;
        }
        propertyStations.Add(this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"CauldronAdapter: Initialized for station {station.GUID}", DebugLogger.Category.Cauldron);
      }

      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemSlot> InsertSlots => new List<ItemSlot> { _station.LiquidSlot };
      public List<ItemSlot> ProductSlots => _station.IngredientSlots.ToList();
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable)?.IsInUse ?? false;
      public bool HasActiveOperation => IsInUse;
      public int StartThreshold => 1;
      public List<ItemField> GetInputItemForProduct() => new List<ItemField>();
      public void StartOperation(Employee employee) => (employee as Chemist)?.StartCauldron(_station);
      public int MaxProductQuantity => Registry.GetItem("cocaleaf").GetDefaultInstance().StackLimit * 3;
      public ITransitEntity TransitEntity => _station;
      public List<ItemInstance> RefillList() => new List<ItemInstance>();
      public bool CanRefill(ItemInstance item) => item?.ID == "cocaleaf" && (item as ProductItemInstance)?.Quality >= QualityField[GUID].Value;
      public Type TypeOf => _station.GetType();
    }
  }

  public static class CauldronUtilities
  {
    /// <summary>
    /// Cleans up data associated with the DryingRack.
    /// </summary>
    public static void Cleanup(Cauldron station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Cleanup: Starting cleanup for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.DryingRack);

      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "Cleanup: Station is null", DebugLogger.Category.DryingRack);
        return;
      }

      CauldronExtensions.QualityField.Remove(station.GUID);
      StationRefills.Remove(station.GUID);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Cleanup: Removed data for station {station.GUID}", DebugLogger.Category.DryingRack);
    }

    public static void InitializeQualityFields(Cauldron station, CauldronConfiguration config)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeQualityFields: Starting for station {station?.GUID.ToString() ?? "null"}", DebugLogger.Category.Cauldron);
      try
      {
        if (station == null || config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InitializeQualityFields: Station or config is null", DebugLogger.Category.Cauldron);
          return;
        }

        Guid guid = station.GUID;
        if (!StationAdapters.ContainsKey(guid))
        {
          StationAdapters[guid] = new CauldronAdapter(station);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeQualityFields: Created adapter for station {guid}", DebugLogger.Category.Cauldron);
        }

        var targetQuality = new QualityField(config);
        targetQuality.onValueChanged.RemoveAllListeners();
        targetQuality.onValueChanged.AddListener(quality =>
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeQualityFields: Set quality {quality} for station {guid}", DebugLogger.Category.Cauldron);
          config.InvokeChanged();
        });

        CauldronExtensions.QualityField[guid] = targetQuality;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeQualityFields: Initialized quality field for station {guid}", DebugLogger.Category.Cauldron);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeQualityFields: Failed, error: {e.Message}", DebugLogger.Category.Cauldron);
      }
    }
  }

  [HarmonyPatch(typeof(CauldronConfigPanel))]
  public class CauldronPanelPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(CauldronConfigPanel __instance, List<EntityConfiguration> configs)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"BindPostfix: Starting for {configs?.Count ?? 0} configs", DebugLogger.Category.Cauldron);
      try
      {
        __instance.DestinationUI?.gameObject.SetActive(false);
        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        var qualityUITemplate = dryingRackPanelObj.transform.Find("QualityFieldUI");
        if (qualityUITemplate == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "BindPostfix: Missing quality field template", DebugLogger.Category.Cauldron);
          return;
        }

        var qualityList = new List<QualityField>();
        foreach (var config in configs.OfType<CauldronConfiguration>())
        {
          if (config?.Station == null) continue;
          if (!CauldronExtensions.QualityField.TryGetValue(config.Station.GUID, out var qualityField))
          {
            InitializeQualityFields(config.Station, config);
            qualityField = CauldronExtensions.QualityField.GetValueOrDefault(config.Station.GUID);
          }
          if (qualityField != null)
            qualityList.Add(qualityField);
        }
        var qualityUIObj = Object.Instantiate(qualityUITemplate.gameObject, __instance.transform, false);
        qualityUIObj.name = CauldronConstants.QualityFieldUIPrefix;
        qualityUIObj.SetActive(true);

        var qualTitle = qualityUIObj.transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
        if (qualTitle != null)
          qualTitle.text = "Min Quality";

        var destRect = __instance.DestinationUI.GetComponent<RectTransform>();
        var qualRect = qualityUIObj.GetComponent<RectTransform>();
        if (destRect != null && qualRect != null)
          qualRect.anchoredPosition = destRect.anchoredPosition;

        qualityUIObj.GetComponent<QualityFieldUI>().Bind(qualityList);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"BindPostfix: Bound {CauldronConstants.QualityFieldUIPrefix} to {qualityList.Count} fields", DebugLogger.Category.Cauldron);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"BindPostfix: Failed, error: {e.Message}", DebugLogger.Category.Cauldron);
      }
    }
  }

  [HarmonyPatch(typeof(CauldronConfiguration))]
  public class CauldronConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(Cauldron) })]
    static void ConstructorPostfix(CauldronConfiguration __instance, Cauldron cauldron)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ConstructorPostfix: Starting for station {cauldron?.GUID.ToString() ?? "null"}", DebugLogger.Category.Cauldron);
      try
      {
        if (!CauldronExtensions.QualityField.ContainsKey(cauldron.GUID))
          InitializeQualityFields(cauldron, __instance);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ConstructorPostfix: Failed, error: {e.Message}", DebugLogger.Category.Cauldron);
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
    static void GetSaveStringPostfix(CauldronConfiguration __instance, ref string __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetSaveStringPostfix: Starting for station {__instance?.Station?.GUID.ToString() ?? "null"}", DebugLogger.Category.Cauldron);
      try
      {
        if (__instance?.Station == null) return;
        var json = JObject.Parse(__result);
        if (CauldronExtensions.QualityField.TryGetValue(__instance.Station.GUID, out var field))
          json["Quality"] = field.Value.ToString();
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetSaveStringPostfix: Saved JSON for station {__instance.Station.GUID}", DebugLogger.Category.Cauldron);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetSaveStringPostfix: Failed, error: {e.Message}", DebugLogger.Category.Cauldron);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(CauldronConfiguration __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DestroyPostfix: Starting for station {__instance?.Station?.GUID.ToString() ?? "null"}", DebugLogger.Category.Cauldron);
      try
      {
        if (__instance?.Station != null)
          Cleanup(__instance.Station);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"DestroyPostfix: Failed, error: {e.Message}", DebugLogger.Category.Cauldron);
      }
    }
  }

  [HarmonyPatch(typeof(CauldronLoader))]
  public class CauldronLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(string mainPath)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadPostfix: Starting for {mainPath}", DebugLogger.Category.Cauldron);
      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || gridItem == null || !(gridItem is Cauldron station))
          return;

        string configPath = System.IO.Path.Combine(mainPath, "Configuration.json");
        if (!System.IO.File.Exists(configPath))
          return;

        string json = System.IO.File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.cauldronConfiguration;
        if (config == null)
          return;

        if (config.Destination.SelectedObject != null)
        {
          config.Destination.SelectedObject = null;
          config.DestinationRoute = null;
        }

        Guid guid = station.GUID;
        if (!CauldronExtensions.QualityField.ContainsKey(guid))
          InitializeQualityFields(station, config);

        var qualityField = CauldronExtensions.QualityField[guid];
        var qualityFieldData = jsonObject["Quality"];
        if (qualityFieldData != null)
          qualityField.SetValue(Enum.Parse<EQuality>(qualityFieldData.ToString()), false);

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"LoadPostfix: Loaded config for station {guid}", DebugLogger.Category.Cauldron);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadPostfix: Failed, error: {e.Message}", DebugLogger.Category.Cauldron);
      }
    }
  }
}