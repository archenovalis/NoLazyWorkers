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
using static NoLazyWorkers.Handlers.PackagingStationExtensions;
using static NoLazyWorkers.Handlers.PackagingStationUtilities;
using static NoLazyWorkers.General.GeneralExtensions;
using ScheduleOne.NPCs.Behaviour;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using Steamworks;
using ScheduleOne.Product;
using ScheduleOne.DevUtilities;
using ScheduleOne.NPCs;

namespace NoLazyWorkers.Handlers
{
  public class PackagingStationAdapter : IStationAdapter<PackagingStation>
  {
    private readonly PackagingStation _station;

    public PackagingStationAdapter(PackagingStation station)
    {
      _station = station ?? throw new ArgumentNullException(nameof(station));
    }

    public PackagingStation Station => _station;

    public Guid GUID => _station.GUID;
    public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
    public ItemSlot InsertSlot => _station.InputSlots.FirstOrDefault();
    public List<ItemSlot> ProductSlots => _station.OutputSlots;
    public ItemSlot OutputSlot => _station.OutputSlots.FirstOrDefault();
    public bool IsInUse => (_station as IUsable).IsInUse;
    public bool HasActiveOperation => false;
    public int StartThreshold => 1;
    public int GetInputQuantity() => _station.InputSlots.Sum(s => s.Quantity);
    public List<ItemField> GetInputItemForProduct() => ItemFields[GUID];
    public void StartOperation(Behaviour behaviour) => (behaviour as PackagingStationBehaviour).StartPackaging();
    public int MaxProductQuantity => 20;
    public ITransitEntity TransitEntity => _station as ITransitEntity;
  }

  public static class PackagingStationExtensions
  {
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, List<QualityField>> QualityFields { get; } = new();
    public static Dictionary<Guid, PackagingStationConfiguration> Config { get; } = new();

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

    public static void RegisterConfig(PackagingStation station, PackagingStationConfiguration config)
    {
      if (station == null || config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "RegisterConfig: Station or config is null", DebugLogger.Category.PackagingStation);
        return;
      }
      Config[station.GUID] = config;
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
      Config.Remove(station.GUID);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.Cleanup: Removed data for station {station.GUID}", DebugLogger.Category.PackagingStation);
    }
  }

  public static class PackagingStationUtilities
  {
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
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeItemFields: Initializing for station {guid}", DebugLogger.Category.PackagingStation);

        var itemFields = new List<ItemField>();
        var qualityFields = new List<QualityField>();
        for (int i = 0; i < 6; i++)
        {
          var targetQuality = new QualityField(config);
          targetQuality.onValueChanged.RemoveAllListeners();
          targetQuality.onValueChanged.AddListener(delegate
          {
            config.InvokeChanged();
          });
          qualityFields.Add(targetQuality);

          var itemField = new ItemField(config)
          {
            CanSelectNone = true
          };
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(delegate
          {
            config.InvokeChanged();
          });
          itemFields.Add(itemField);
        }
        QualityFields[station.GUID] = qualityFields;

        RegisterItemFields(station, itemFields);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeItemFields: Initialized 6 ItemFields for station {guid}", DebugLogger.Category.PackagingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"InitializeItemFields: Failed for station {station?.GUID.ToString() ?? "null"}, error: {e}", DebugLogger.Category.PackagingStation);
      }
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

        var favorites = new List<ItemDefinition>();
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
          itemFieldUI.gameObject.SetActive(true);


          var qualityUIObj = Object.Instantiate(qualityFieldUIObj, __instance.transform, false);
          qualityUIObj.SetActive(true);
          qualityUIObj.transform.Find("Title").GetComponent<TextMeshProUGUI>().text = "Min Quality";

          var rect = itemFieldUI.GetComponent<RectTransform>();
          var qualRect = qualityUIObj.GetComponent<RectTransform>();
          if (rect != null)
          {
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -11 - 104f * i);
            qualRect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -79 - 104f * i);
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

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(PackagingStationConfiguration __instance, ref string __result)
    {
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