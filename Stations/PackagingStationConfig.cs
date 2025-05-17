using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using static NoLazyWorkers.Employees.PackagingStationUtilities;
using static NoLazyWorkers.Employees.PackagingStationConfigUtilities;
using ScheduleOne.NPCs.Behaviour;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using Steamworks;
using ScheduleOne.Product;
using ScheduleOne.DevUtilities;
using ScheduleOne.NPCs;
using GameKit.Utilities;
using static NoLazyWorkers.Stations.StationExtensions;
using ScheduleOne.Employees;

namespace NoLazyWorkers.Employees
{
  public static class PackagingStationExtensions
  {
    public static int MAXOPTIONS = 5;
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, List<QualityField>> QualityFields { get; } = new();

    public class PackagingStationAdapter : IStationAdapter
    {
      private readonly PackagingStation _station;
      public PackagingStation Station => _station;
      public Guid GUID => _station.GUID;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public ItemSlot InsertSlot => _station.PackagingSlot;
      public List<ItemSlot> ProductSlots => new List<ItemSlot> { _station.ProductSlot };
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable).IsInUse;
      public bool HasActiveOperation => false;
      public int StartThreshold => 1;
      public int GetInputQuantity() => _station.InputSlots.Sum(s => s.Quantity);
      public List<ItemField> GetInputItemForProduct() => ItemFields[GUID];
      public void StartOperation(Employee employee) => (employee as Packager)?.PackagingBehaviour.StartPackaging();
      public int MaxProductQuantity => 20;
      public ITransitEntity TransitEntity => _station as ITransitEntity;
      public List<ItemInstance> RefillList() => PackagingStationUtilities.GetRefillList(_station);
      public bool CanRefill(ItemInstance item) => RefillList().Any(i => i.CanStackWith(item, false));
      public bool MoveOutputToShelf() => false;
      public Type TypeOf => _station.GetType();

      public PackagingStationAdapter(PackagingStation station)
      {
        if (!PropertyStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          PropertyStations[station.ParentProperty] = new List<IStationAdapter>();
          propertyStations = PropertyStations[station.ParentProperty];
        }
        propertyStations.Add(this);
        _station = station ?? throw new ArgumentNullException(nameof(station));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagingStationAdapter: Initialized for station {station.GUID}", DebugLogger.Category.Packager);
      }
    }
  }

  public static class PackagingStationUtilities
  {
    public static List<ItemInstance> GetRefillList(PackagingStation station)
    {
      List<ItemInstance> items = [];
      var fields = ItemFields[station.GUID];
      var qualities = QualityFields[station.GUID];
      for (int i = 0; i < fields.Count; i++)
      {
        var item = fields[i].SelectedItem;
        if (item == null)
          continue;
        var prodItem = item.GetDefaultInstance() as ProductItemInstance;
        prodItem.SetQuality(qualities[i].Value);
        items.AddUnique(prodItem);
      }
      return items;
    }
  }

  public static class PackagingStationConfigUtilities
  {

    public static void RegisterConfig(PackagingStation station, PackagingStationConfiguration config)
    {
      if (station == null || config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "RegisterConfig: Station or config is null", DebugLogger.Category.PackagingStation);
        return;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.RegisterConfig: Registered config for station {station.GUID}", DebugLogger.Category.PackagingStation);
    }

    public static void Cleanup(PackagingStation station)
    {
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "Cleanup: Station is null", DebugLogger.Category.PackagingStation);
        return;
      }
      ItemFields.Remove(station.GUID);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.Cleanup: Removed data for station {station.GUID}", DebugLogger.Category.PackagingStation);
    }

    public static void InitializeItemFields(PackagingStation station, PackagingStationConfiguration config)
    {
      try
      {
        if (station == null || config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InitializeItemFields: Station or Configuration is null", DebugLogger.Category.PackagingStation);
          return;
        }
        Guid guid = station.GUID;
        if (!StationAdapters.TryGetValue(guid, out var adapter))
        {
          adapter = new PackagingStationAdapter(station);
          StationAdapters[guid] = adapter;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeItemFields: Initializing for station {guid}", DebugLogger.Category.PackagingStation);

        // Ensure StationRefills is initialized
        if (!StationRefills.ContainsKey(guid))
          StationRefills[guid] = new List<ItemInstance>(MAXOPTIONS + 1);
        while (StationRefills[guid].Count < MAXOPTIONS + 1)
          StationRefills[guid].Add(null);

        var itemFields = new List<ItemField>();
        var qualityFields = new List<QualityField>();

        // Create fields up to min of MAXOPTIONS + 1 and station.ItemFields.Count
        int fieldCount = MAXOPTIONS + 1;
        for (int i = 0; i < fieldCount; i++)
        {
          var targetQuality = new QualityField(config);
          targetQuality.onValueChanged.RemoveAllListeners();
          targetQuality.onValueChanged.AddListener(quality =>
          {
            try
            {
              if (i < StationRefills[guid].Count && StationRefills[guid][i] is ProductItemInstance prodItem)
              {
                prodItem.SetQuality(quality);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Set quality {quality} for index {i} in station {guid}", DebugLogger.Category.PackagingStation);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed to set quality for index {i} in station {guid}, error: {e}", DebugLogger.Category.PackagingStation);
            }
          });
          qualityFields.Add(targetQuality);

          var itemField = new ItemField(config) { CanSelectNone = false };
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(item =>
          {
            try
            {
              if (i < StationRefills[guid].Count)
              {
                StationRefills[guid][i] = item?.GetDefaultInstance();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitializeItemFields: Set item {item?.ID ?? "null"} for index {i} in station {guid}", DebugLogger.Category.PackagingStation);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed to set item for index {i} in station {guid}, error: {e}", DebugLogger.Category.PackagingStation);
            }
          });
          itemFields.Add(itemField);
        }

        // Register fields
        QualityFields[guid] = qualityFields;
        RegisterItemFields(station, itemFields);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeItemFields: Initialized {itemFields.Count} ItemFields for station {guid}", DebugLogger.Category.PackagingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeItemFields: Failed for station {station?.GUID.ToString() ?? "null"}, error: {e}", DebugLogger.Category.PackagingStation);
      }
    }

    public static void RegisterItemFields(PackagingStation station, List<ItemField> itemFields)
    {
      if (station == null || itemFields == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "RegisterItemFields: Station or itemFields is null", DebugLogger.Category.PackagingStation);
        return;
      }
      ItemFields[station.GUID] = itemFields;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.RegisterItemFields: Registered {itemFields.Count} ItemFields for station {station.GUID}", DebugLogger.Category.PackagingStation);
    }

  }

  [HarmonyPatch(typeof(PackagingStationConfigPanel))]
  public class PackagingStationPanelPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Bind")]
    static void BindPostfix(PackagingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        __instance.DestinationUI.gameObject.SetActive(false);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationPanelPatch.BindPostfix: Binding {configs.Count} configs", DebugLogger.Category.PackagingStation);

        var configPanel = __instance.GetComponent<PackagingStationConfigPanel>();
        if (configPanel == null)
        {
          configPanel = __instance.gameObject.AddComponent<PackagingStationConfigPanel>();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"PackagingStationPanelPatch.BindPostfix: Added PackagingStationConfigPanel to {__instance.gameObject.name}", DebugLogger.Category.PackagingStation);
        }

        ItemFieldUI templateUI = null;
        var potPanel = GetPrefabGameObject("PotConfigPanel");
        if (potPanel != null)
        {
          templateUI = potPanel.GetComponentInChildren<ItemFieldUI>();
        }

        var favorites = new List<ItemDefinition>
        {
          new ItemDefinition() { Name = "None", ID = "None" },
          new ItemDefinition() { Name = "Any", ID = "Any" }
        };
        if (ProductManager.FavouritedProducts != null)
        {
          foreach (var item in ProductManager.FavouritedProducts)
          {
            if (item != null)
              favorites.Add(item);
          }
        }

        List<ItemField> itemFieldList1 = new();
        List<ItemField> itemFieldList2 = new();
        List<ItemField> itemFieldList3 = new();
        List<ItemField> itemFieldList4 = new();
        List<ItemField> itemFieldList5 = new();
        List<ItemField> itemFieldList6 = new();
        List<QualityField> qualityList1 = new();
        List<QualityField> qualityList2 = new();
        List<QualityField> qualityList3 = new();
        List<QualityField> qualityList4 = new();
        List<QualityField> qualityList5 = new();
        List<QualityField> qualityList6 = new();
        foreach (var config in configs.OfType<PackagingStationConfiguration>())
        {
          if (!ItemFields.TryGetValue(config.Station.GUID, out var itemFields))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"PackagingStationPanelPatch.BindPostfix: No ItemFields for station {config.Station.GUID}", DebugLogger.Category.PackagingStation);
            continue;
          }
          if (!QualityFields.TryGetValue(config.Station.GUID, out var qualityFields))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"PackagingStationPanelPatch.BindPostfix: No ItemFields for station {config.Station.GUID}", DebugLogger.Category.PackagingStation);
            continue;
          }

          foreach (var field in itemFields)
          {
            field.Options = favorites;
          }
          itemFieldList1.Add(itemFields[0]);
          itemFieldList2.Add(itemFields[1]);
          itemFieldList3.Add(itemFields[2]);
          itemFieldList4.Add(itemFields[3]);
          itemFieldList5.Add(itemFields[4]);
          itemFieldList6.Add(itemFields[5]);

          qualityList1.Add(qualityFields[0]);
          qualityList2.Add(qualityFields[1]);
          qualityList3.Add(qualityFields[2]);
          qualityList4.Add(qualityFields[3]);
          qualityList5.Add(qualityFields[4]);
          qualityList6.Add(qualityFields[5]);
        }

        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        var qualityFieldUIObj = dryingRackPanelObj.transform.Find("QualityFieldUI")?.gameObject;
        qualityFieldUIObj.transform.Find("Description").gameObject.SetActive(false);
        for (int i = 0; i < 5; i++)
        {
          var uiObj = Object.Instantiate(templateUI.gameObject, __instance.transform, false);
          uiObj.name = $"ItemFieldUI_{i}";
          var itemFieldUI = uiObj.GetComponent<ItemFieldUI>();
          if (itemFieldUI == null)
          {
            itemFieldUI = uiObj.AddComponent<ItemFieldUI>();
            itemFieldUI.gameObject.AddComponent<CanvasRenderer>();
          }
          itemFieldUI.ShowNoneAsAny = false;
          itemFieldUI.gameObject.SetActive(true);


          var qualityUIObj = Object.Instantiate(qualityFieldUIObj, __instance.transform, false);
          qualityUIObj.SetActive(true);
          qualityUIObj.transform.Find("Title").GetComponent<TextMeshProUGUI>().text = "Min Quality";

          var rect = itemFieldUI.GetComponent<RectTransform>();
          var qualRect = qualityUIObj.GetComponent<RectTransform>();
          if (rect != null)
          {
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -15 - 104f * i);
            qualRect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -83 - 104f * i);
          }

          foreach (var text in itemFieldUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (text.gameObject.name == "Title" || text.gameObject.name.Contains("Title"))
            {
              text.text = $"Item {i + 1}";
              break;
            }
          }

          var iFieldsForIndex = i switch
          {
            0 => itemFieldList1,
            1 => itemFieldList2,
            2 => itemFieldList3,
            3 => itemFieldList4,
            4 => itemFieldList5,
            5 => itemFieldList6,
            _ => new List<ItemField>()
          };
          itemFieldUI.Bind(iFieldsForIndex);

          var qFieldsForIndex = i switch
          {
            0 => qualityList1,
            1 => qualityList2,
            2 => qualityList3,
            3 => qualityList4,
            4 => qualityList5,
            5 => qualityList6,
            _ => new List<QualityField>()
          };
          qualityUIObj.GetComponent<QualityFieldUI>().Bind(qFieldsForIndex);

          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"PackagingStationPanelPatch.BindPostfix: Bound ItemFieldUI_{i} to {iFieldsForIndex.Count} ItemFields", DebugLogger.Category.PackagingStation);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationPanelPatch.BindPostfix: Added and bound 6 ItemFieldUIs for {configs.Count} configs", DebugLogger.Category.PackagingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationPanelPatch.BindPostfix: Failed, error: {e}", DebugLogger.Category.PackagingStation);
      }
    }
  }

  [HarmonyPatch(typeof(PackagingStationConfiguration))]
  public class PackagingStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(PackagingStation) })]
    static void ConstructorPostfix(PackagingStationConfiguration __instance, PackagingStation station)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationConfigurationPatch.ConstructorPostfix: Initializing for station {station.GUID}", DebugLogger.Category.PackagingStation);
        RegisterConfig(station, __instance);
        if (!ItemFields.ContainsKey(station.GUID))
        {
          InitializeItemFields(station, __instance);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationConfigurationPatch.ConstructorPostfix: Failed for station {station.GUID}, error: {e}", DebugLogger.Category.PackagingStation);
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("ShouldSave")]
    static bool ShouldSavePrefix(PackagingStationConfiguration __instance, ref bool __result)
    {
      __result = true;
      return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(PackagingStationConfiguration __instance, ref string __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Starting station {__instance.Station.GUID}", DebugLogger.Category.PackagingStation);
      try
      {
        if (__instance.Station == null) return;

        var json = JObject.Parse(__result);
        if (ItemFields.TryGetValue(__instance.Station.GUID, out var itemFields))
        {
          var itemFieldsData = new JArray();
          for (int i = 0; i < 6; i++)
          {
            var field = itemFields[i];
            itemFieldsData.Add(new JObject
            {
              ["ItemID"] = field.SelectedItem?.ID
            });
          }
          json["ItemFields"] = itemFieldsData;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"PackagingStationConfigurationPatch.GetSaveStringPostfix: station {__instance.Station.GUID} itemfields: {json["ItemFields"].ToString(Newtonsoft.Json.Formatting.Indented)}", DebugLogger.Category.PackagingStation);
        JArray qualityFieldsArray = [];
        if (QualityFields.TryGetValue(__instance.Station.GUID, out var fields) && fields.Any())
        {
          foreach (var field in fields)
          {
            string quality = field.Value.ToString();

            var qualityObj = new JObject
            {
              ["Quality"] = quality,
            };
            qualityFieldsArray.Add(qualityObj);
          }
        }
        json["Qualities"] = qualityFieldsArray;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"PackagingStationConfigurationPatch.GetSaveStringPostfix: station {__instance.Station.GUID} itemfields: {json["Qualities"].ToString(Newtonsoft.Json.Formatting.Indented)}", DebugLogger.Category.PackagingStation);
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Saved JSON for station {__instance.Station.GUID}: {__result}", DebugLogger.Category.PackagingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Failed for station {__instance.Station.GUID}, error: {e}", DebugLogger.Category.PackagingStation);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(PackagingStationConfiguration __instance)
    {
      try
      {
        if (__instance.Station == null) return;
        Cleanup(__instance.Station);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationConfigurationPatch.DestroyPostfix: Failed for station {__instance.Station?.GUID}, error: {e}", DebugLogger.Category.PackagingStation);
      }
    }
  }

  [HarmonyPatch(typeof(PackagingStationLoader))]
  public class PackagingStationLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(string mainPath)
    {
      try
      {
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out var gridItem) || gridItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: No GridItem for {mainPath}", DebugLogger.Category.PackagingStation);
          return;
        }

        if (!(gridItem is PackagingStation station))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: GridItem is not a PackagingStation for {mainPath}", DebugLogger.Category.PackagingStation);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: No Configuration.json at {configPath}", DebugLogger.Category.PackagingStation);
          return;
        }

        string json = File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.stationConfiguration;
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"PackagingStationLoaderPatch.LoadPostfix: No valid PackagingStationConfiguration for station {station.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        // Clear hidden destination
        if (config.Destination.SelectedObject != null)
        {
          config.Destination.SelectedObject = null;
          config.DestinationRoute = null;
        }

        Guid guid = station.GUID;
        if (!ItemFields.ContainsKey(guid))
        {
          InitializeItemFields(station, config);
        }

        var itemFields = ItemFields[guid];
        var itemFieldsData = jsonObject["ItemFields"] as JArray;
        var qualityFields = QualityFields[guid];
        var mixingRoutesJToken = jsonObject["Qualities"] as JArray;
        if (itemFieldsData != null && itemFieldsData.Count <= 6)
        {
          for (int i = 0; i < 6; i++)
          {
            var qualityData = mixingRoutesJToken[i] as JObject;
            qualityFields[i].SetValue(Enum.Parse<EQuality>(qualityData["Quality"]?.ToString()), false);
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
                  DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"PackagingStationLoaderPatch.LoadPostfix: Loaded ItemField {i} for station {guid}, ItemID: {itemID}", DebugLogger.Category.PackagingStation);
                }
                else
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Warning,
                      $"PackagingStationLoaderPatch.LoadPostfix: Failed to load item for ItemField {i}, ItemID: {itemID}", DebugLogger.Category.PackagingStation);
                }
              }
            }
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: Invalid or missing ItemFields data for station {guid}", DebugLogger.Category.PackagingStation);
        }

        RegisterConfig(station, config);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationLoaderPatch.LoadPostfix: Loaded config for station {station.GUID}", DebugLogger.Category.PackagingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationLoaderPatch.LoadPostfix: Failed for {mainPath}, error: {e}", DebugLogger.Category.PackagingStation);
      }
    }
  }
}