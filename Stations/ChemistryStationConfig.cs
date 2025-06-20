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
using static NoLazyWorkers.Stations.ChemistryStationExtensions;
using static NoLazyWorkers.Stations.ChemistryStationUtilities;
using NoLazyWorkers.Stations;
using NoLazyWorkers;
using ScheduleOne;
using ScheduleOne.Property;
using NoLazyWorkers.Storage;
using ScheduleOne.StationFramework;
using ScheduleOne.EntityFramework;
using static NoLazyWorkers.Debug;

namespace NoLazyWorkers.Stations
{
  public static class ChemistryStationConstants
  {
    public const int MaxOptions = 6;
    public const string RecipeFieldUIPrefix = "RecipeFieldUI_";
    public const float UIVerticalSpacing = 60f;
  }

  public static class ChemistryStationExtensions
  {
    public static Dictionary<Guid, List<StationRecipeField>> RecipeFields { get; } = new();

    public class ChemistryStationAdapter : IStationAdapter
    {
      private readonly ChemistryStation _station;

      public ChemistryStationAdapter(ChemistryStation station)
      {
        _station = station ?? throw new ArgumentNullException(nameof(station));
        if (!Extensions.IStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          propertyStations = new();
          Extensions.IStations[station.ParentProperty] = propertyStations;
        }
        propertyStations.Add(GUID, this);
        Log(Level.Info, $"ChemistryStationAdapter: Initialized for station {station.GUID}", Category.ChemistryStation);
      }

      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemSlot> InsertSlots => _station.IngredientSlots.ToList();
      public List<ItemSlot> ProductSlots => _station.IngredientSlots.ToList();
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable)?.IsInUse ?? false;
      public bool HasActiveOperation => IsInUse;
      public int StartThreshold => 1;
      public List<ItemField> GetInputItemForProduct() => new List<ItemField>();
      public void StartOperation(Employee employee) => (employee as Chemist)?.StartChemistryStation(_station);
      public int MaxProductQuantity => ProductSlots.FirstOrDefault()?.ItemInstance?.StackLimit ?? 0;
      public ITransitEntity TransitEntity => _station as ITransitEntity;
      public BuildableItem Buildable => _station as BuildableItem;
      public Property ParentProperty => _station.ParentProperty;
      public List<ItemInstance> RefillList() => new List<ItemInstance>();
      public bool CanRefill(ItemInstance item) => false;
      public Type TypeOf => _station.GetType();
    }
  }

  public static class ChemistryStationUtilities
  {
    /// <summary>
    /// Cleans up data associated with the DryingRack.
    /// </summary>
    public static void Cleanup(ChemistryStation station)
    {
      Log(Level.Verbose, $"Cleanup: Starting cleanup for station {station?.GUID.ToString() ?? "null"}", CategoryDryingRack);

      if (station == null)
      {
        Log(Level.Warning, "Cleanup: Station is null", CategoryDryingRack);
        return;
      }

      RecipeFields.Remove(station.GUID);
      StationRefillLists.Remove(station.GUID);
      Log(Level.Info, $"Cleanup: Removed data for station {station.GUID}", CategoryDryingRack);
    }

    public static void InitializeRecipeFields(ChemistryStation station, ChemistryStationConfiguration config)
    {
      Log(Level.Verbose, $"InitializeRecipeFields: Starting for station {station?.GUID.ToString() ?? "null"}", Category.ChemistryStation);
      try
      {
        if (station == null || config == null)
        {
          Log(Level.Error, "InitializeRecipeFields: Station or config is null", Category.ChemistryStation);
          return;
        }

        Guid guid = station.GUID;
        if (!IStations[station.ParentProperty].ContainsKey(guid))
        {
          IStations[station.ParentProperty][guid] = new ChemistryStationAdapter(station);
          Log(Level.Info, $"InitializeRecipeFields: Created adapter for station {guid}", Category.ChemistryStation);
        }

        if (!StationRefillLists.ContainsKey(guid))
          StationRefillLists[guid] = new List<ItemInstance>(ChemistryStationConstants.MaxOptions);

        var recipeFields = new List<StationRecipeField>(ChemistryStationConstants.MaxOptions);
        for (int i = 0; i < ChemistryStationConstants.MaxOptions; i++)
        {
          var recipeField = new StationRecipeField(config);
          recipeField.onRecipeChanged.RemoveAllListeners();
          recipeField.onRecipeChanged.AddListener(recipe =>
          {
            Log(Level.Verbose, $"InitializeRecipeFields: Set recipe {recipe?.RecipeID ?? "null"} for index {i}", Category.ChemistryStation);
            config.InvokeChanged();
          });
          recipeFields.Add(recipeField);
        }

        RecipeFields[guid] = recipeFields;
        Log(Level.Info, $"InitializeRecipeFields: Initialized {recipeFields.Count} fields for station {guid}", Category.ChemistryStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"InitializeRecipeFields: Failed, error: {e.Message}", Category.ChemistryStation);
      }
    }
  }

  [HarmonyPatch(typeof(ChemistryStationConfigPanel))]
  public class ChemistryStationPanelPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(ChemistryStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      Log(Level.Verbose, $"BindPostfix: Starting for {configs?.Count ?? 0} configs", Category.ChemistryStation);
      try
      {
        __instance.DestinationUI?.gameObject.SetActive(false);
        var recipeFieldTemplate = __instance.RecipeUI.gameObject;
        if (recipeFieldTemplate == null)
        {
          Log(Level.Error, "BindPostfix: Missing recipe field template", Category.ChemistryStation);
          return;
        }

        var recipeFieldLists = new List<StationRecipeField>[ChemistryStationConstants.MaxOptions];
        for (int i = 0; i < ChemistryStationConstants.MaxOptions; i++)
          recipeFieldLists[i] = new List<StationRecipeField>();

        foreach (var config in configs.OfType<ChemistryStationConfiguration>())
        {
          if (config?.Station == null) continue;
          if (!RecipeFields.TryGetValue(config.Station.GUID, out var recipeFields))
          {
            InitializeRecipeFields(config.Station, config);
            recipeFields = RecipeFields.GetValueOrDefault(config.Station.GUID);
          }
          if (recipeFields == null) continue;

          for (int i = 0; i < ChemistryStationConstants.MaxOptions; i++)
            recipeFieldLists[i].Add(recipeFields[i]);
        }

        float priorEntryRectY = 0f;
        for (int i = 0; i < ChemistryStationConstants.MaxOptions; i++)
        {
          var uiObj = Object.Instantiate(recipeFieldTemplate, __instance.transform, false);
          uiObj.name = $"{ChemistryStationConstants.RecipeFieldUIPrefix}{i}";
          var recipeFieldUI = uiObj.GetComponent<StationRecipeFieldUI>();
          uiObj.SetActive(true);

          var rect = recipeFieldUI.GetComponent<RectTransform>();
          if (rect != null)
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, priorEntryRectY);

          priorEntryRectY -= ChemistryStationConstants.UIVerticalSpacing;

          foreach (var text in recipeFieldUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (text.gameObject.name == "Title" || text.gameObject.name.Contains("Title"))
            {
              text.text = $"Recipe {i + 1}";
              break;
            }
          }

          recipeFieldUI.Bind(recipeFieldLists[i]);
          Log(Level.Info, $"BindPostfix: Bound {ChemistryStationConstants.RecipeFieldUIPrefix}{i} to {recipeFieldLists[i].Count} fields", Category.ChemistryStation);
        }
      }
      catch (Exception e)
      {
        Log(Level.Error, $"BindPostfix: Failed, error: {e.Message}", Category.ChemistryStation);
      }
    }
  }

  [HarmonyPatch(typeof(ChemistryStationConfiguration))]
  public class ChemistryStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(ChemistryStation) })]
    static void ConstructorPostfix(ChemistryStationConfiguration __instance, ChemistryStation station)
    {
      Log(Level.Verbose, $"ConstructorPostfix: Starting for station {station?.GUID.ToString() ?? "null"}", Category.ChemistryStation);
      try
      {
        if (!RecipeFields.ContainsKey(station.GUID))
          InitializeRecipeFields(station, __instance);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"ConstructorPostfix: Failed, error: {e.Message}", Category.ChemistryStation);
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
    static void GetSaveStringPostfix(ChemistryStationConfiguration __instance, ref string __result)
    {
      Log(Level.Verbose, $"GetSaveStringPostfix: Starting for station {__instance?.Station?.GUID.ToString() ?? "null"}", Category.ChemistryStation);
      try
      {
        if (__instance?.Station == null) return;
        var json = JObject.Parse(__result);
        if (RecipeFields.TryGetValue(__instance.Station.GUID, out var recipeFields))
        {
          var recipeFieldsData = new JArray();
          for (int i = 0; i < ChemistryStationConstants.MaxOptions; i++)
            recipeFieldsData.Add(new JObject { ["RecipeID"] = recipeFields[i].SelectedRecipe?.RecipeID });
          json["RecipeFields"] = recipeFieldsData;
        }
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);
        Log(Level.Info, $"GetSaveStringPostfix: Saved JSON for station {__instance.Station.GUID}", Category.ChemistryStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"GetSaveStringPostfix: Failed, error: {e.Message}", Category.ChemistryStation);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(ChemistryStationConfiguration __instance)
    {
      Log(Level.Verbose, $"DestroyPostfix: Starting for station {__instance?.Station?.GUID.ToString() ?? "null"}", Category.ChemistryStation);
      try
      {
        if (__instance?.Station != null)
          Cleanup(__instance.Station);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"DestroyPostfix: Failed, error: {e.Message}", Category.ChemistryStation);
      }
    }
  }

  [HarmonyPatch(typeof(ChemistryStationLoader))]
  public class ChemistryStationLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(string mainPath)
    {
      Log(Level.Verbose, $"LoadPostfix: Starting for {mainPath}", Category.ChemistryStation);
      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || gridItem == null || !(gridItem is ChemistryStation station))
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
        if (!RecipeFields.ContainsKey(guid))
          InitializeRecipeFields(station, config);

        var recipeFields = RecipeFields[guid];
        var recipeFieldsData = jsonObject["RecipeFields"] as JArray;

        if (recipeFieldsData != null && recipeFieldsData.Count <= ChemistryStationConstants.MaxOptions)
        {
          for (int i = 0; i < ChemistryStationConstants.MaxOptions; i++)
          {
            var recipeFieldData = recipeFieldsData[i] as JObject;
            if (recipeFieldData != null && recipeFieldData["RecipeID"] != null)
            {
              string recipeID = recipeFieldData["RecipeID"].ToString();
              if (!string.IsNullOrEmpty(recipeID))
              {
                var recipe = recipeFields[i].Options.Find(x => x.RecipeID == recipeID);
                if (recipe != null)
                  recipeFields[i].SelectedRecipe = recipe;
              }
            }
          }
        }
        Log(Level.Info, $"LoadPostfix: Loaded config for station {guid}", Category.ChemistryStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"LoadPostfix: Failed, error: {e.Message}", Category.ChemistryStation);
      }
    }
  }
}