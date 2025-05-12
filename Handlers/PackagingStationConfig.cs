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
    public PackagingStationConfiguration Config => _station.stationConfiguration;

    public Guid GUID => _station.GUID;
    public Vector3 GetAccessPoint() => _station.AccessPoints.FirstOrDefault()?.position ?? _station.transform.position;
    public ItemSlot InsertSlot => _station.InputSlots.FirstOrDefault();
    public List<ItemSlot> ProductSlots => _station.OutputSlots;
    public ItemSlot OutputSlot => _station.OutputSlots.FirstOrDefault();
    public bool IsInUse => (_station as IUsable).IsInUse;
    public bool HasActiveOperation => false;
    public int StartThreshold => 1;
    public int GetInputQuantity() => _station.InputSlots.Sum(s => s.Quantity);
    public List<ItemField> GetInputItemForProduct() => PackagingStationExtensions.ItemFields[GUID];
    public void StartOperation(Behaviour behaviour) => (behaviour as PackagingStationBehaviour).StartPackaging();
    public int MaxProductQuantity => 20;
    public ITransitEntity TransitEntity => _station as ITransitEntity;
  }

  public static class PackagingStationExtensions
  {
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, PackagingStationConfiguration> Config { get; } = new();

    public static void RegisterItemFields(PackagingStation station, List<ItemField> itemFields)
    {
      if (station == null || itemFields == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "RegisterItemFields: Station or itemFields is null", DebugLogger.Category.Handler);
        return;
      }
      ItemFields[station.GUID] = itemFields;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.RegisterItemFields: Registered {itemFields.Count} ItemFields for station {station.GUID}", DebugLogger.Category.Handler);
    }

    public static void RegisterConfig(PackagingStation station, PackagingStationConfiguration config)
    {
      if (station == null || config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "RegisterConfig: Station or config is null", DebugLogger.Category.Handler);
        return;
      }
      Config[station.GUID] = config;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.RegisterConfig: Registered config for station {station.GUID}", DebugLogger.Category.Handler);
    }

    public static void Cleanup(PackagingStation station)
    {
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "Cleanup: Station is null", DebugLogger.Category.Handler);
        return;
      }
      ItemFields.Remove(station.GUID);
      Config.Remove(station.GUID);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagingExtensions.Cleanup: Removed data for station {station.GUID}", DebugLogger.Category.Handler);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InitializeItemFields: Station or Configuration is null", DebugLogger.Category.Handler);
          return;
        }

        Guid guid = station.GUID;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeItemFields: Initializing for station {guid}", DebugLogger.Category.Handler);

        var itemFields = new List<ItemField>();
        for (int i = 0; i < 6; i++)
        {
          var itemField = new ItemField(config);
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(item => RefreshChanged(item, config));
          itemFields.Add(itemField);
        }

        PackagingStationExtensions.RegisterItemFields(station, itemFields);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InitializeItemFields: Initialized 6 ItemFields for station {guid}", DebugLogger.Category.Handler);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"InitializeItemFields: Failed for station {station?.GUID.ToString() ?? "null"}, error: {e}", DebugLogger.Category.Handler);
      }
    }

    public static void RefreshChanged(ItemDefinition item, PackagingStationConfiguration config)
    {
      try
      {
        if (config == null || config.Station == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "RefreshChanged: PackagingStationConfiguration or Station is null", DebugLogger.Category.Handler);
          return;
        }

        Guid guid = config.Station.GUID;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"RefreshChanged: Called for station {guid}, Item: {item?.ID ?? "null"}", DebugLogger.Category.Handler);

        if (!PackagingStationExtensions.ItemFields.ContainsKey(guid))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"RefreshChanged: No ItemFields for station {guid}", DebugLogger.Category.Handler);
          return;
        }

        var itemFields = PackagingStationExtensions.ItemFields[guid];
        bool itemChanged = false;
        foreach (var field in itemFields)
        {
          if (field.SelectedItem?.ID == item?.ID)
          {
            itemChanged = true;
            break;
          }
        }

        if (itemChanged)
        {
          ConfigurationExtensions.InvokeChanged(config);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"RefreshChanged: Updated ItemFields for station {guid}, Item: {item?.ID ?? "null"}", DebugLogger.Category.Handler);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"RefreshChanged: Failed for station {config?.Station?.GUID.ToString() ?? "null"}, error: {e}", DebugLogger.Category.Handler);
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
            $"PackagingStationPanelPatch.BindPostfix: Binding {configs.Count} configs", DebugLogger.Category.Handler);

        var configPanel = __instance.GetComponent<PackagingStationConfigPanel>();
        if (configPanel == null)
        {
          configPanel = __instance.gameObject.AddComponent<PackagingStationConfigPanel>();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"PackagingStationPanelPatch.BindPostfix: Added PackagingStationConfigPanel to {__instance.gameObject.name}", DebugLogger.Category.Handler);
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
        foreach (var config in configs.OfType<PackagingStationConfiguration>())
        {
          if (!PackagingStationExtensions.ItemFields.TryGetValue(config.Station.GUID, out var itemFields))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"PackagingStationPanelPatch.BindPostfix: No ItemFields for station {config.Station.GUID}", DebugLogger.Category.Handler);
            continue;
          }

          foreach (var field in itemFields)
          {
            field.Options = favorites;
            field.onItemChanged.RemoveAllListeners();
            field.onItemChanged.AddListener(item => PackagingStationUtilities.RefreshChanged(item, config));
          }
          itemFieldList1.Add(itemFields[0]);
          itemFieldList2.Add(itemFields[1]);
          itemFieldList3.Add(itemFields[2]);
          itemFieldList4.Add(itemFields[3]);
          itemFieldList5.Add(itemFields[4]);
          itemFieldList6.Add(itemFields[5]);
        }

        for (int i = 0; i < 6; i++)
        {
          var uiObj = Object.Instantiate(templateUI.gameObject, __instance.transform, false);
          uiObj.name = $"ItemFieldUI_{i}";
          var ui = uiObj.GetComponent<ItemFieldUI>();
          if (ui == null)
          {
            ui = uiObj.AddComponent<ItemFieldUI>();
            ui.gameObject.AddComponent<CanvasRenderer>();
          }
          ui.gameObject.SetActive(true);

          var rect = ui.GetComponent<RectTransform>();
          if (rect != null)
          {
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -30 - 60f * i);
          }

          foreach (var text in ui.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (text.gameObject.name == "Title" || text.gameObject.name.Contains("Title"))
            {
              text.text = $"Item {i + 1}";
              break;
            }
          }

          var fieldsForIndex = i switch
          {
            0 => itemFieldList1,
            1 => itemFieldList2,
            2 => itemFieldList3,
            3 => itemFieldList4,
            4 => itemFieldList5,
            5 => itemFieldList6,
            _ => new List<ItemField>()
          };
          ui.Bind(fieldsForIndex);

          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"PackagingStationPanelPatch.BindPostfix: Bound ItemFieldUI_{i} to {fieldsForIndex.Count} ItemFields", DebugLogger.Category.Handler);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationPanelPatch.BindPostfix: Added and bound 6 ItemFieldUIs for {configs.Count} configs", DebugLogger.Category.Handler);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationPanelPatch.BindPostfix: Failed, error: {e}", DebugLogger.Category.Handler);
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
            $"PackagingStationConfigurationPatch.ConstructorPostfix: Initializing for station {station.GUID}", DebugLogger.Category.Handler);
        PackagingStationExtensions.RegisterConfig(station, __instance);
        if (!PackagingStationExtensions.ItemFields.ContainsKey(station.GUID))
        {
          PackagingStationUtilities.InitializeItemFields(station, __instance);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationConfigurationPatch.ConstructorPostfix: Failed for station {station.GUID}, error: {e}", DebugLogger.Category.Handler);
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
        if (PackagingStationExtensions.ItemFields.TryGetValue(__instance.Station.GUID, out var itemFields))
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
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Saved JSON for station {__instance.Station.GUID}: {__result}", DebugLogger.Category.Handler);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Failed for station {__instance.Station.GUID}, error: {e}", DebugLogger.Category.Handler);
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(PackagingStationConfiguration __instance)
    {
      try
      {
        if (__instance.Station == null) return;
        PackagingStationExtensions.Cleanup(__instance.Station);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationConfigurationPatch.DestroyPostfix: Failed for station {__instance.Station?.GUID}, error: {e}", DebugLogger.Category.Handler);
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
              $"PackagingStationLoaderPatch.LoadPostfix: No GridItem for {mainPath}", DebugLogger.Category.Handler);
          return;
        }

        if (!(gridItem is PackagingStation station))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: GridItem is not a PackagingStation for {mainPath}", DebugLogger.Category.Handler);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: No Configuration.json at {configPath}", DebugLogger.Category.Handler);
          return;
        }

        string json = File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.stationConfiguration;
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"PackagingStationLoaderPatch.LoadPostfix: No valid PackagingStationConfiguration for station {station.GUID}", DebugLogger.Category.Handler);
          return;
        }

        Guid guid = station.GUID;
        if (!PackagingStationExtensions.ItemFields.ContainsKey(guid))
        {
          PackagingStationUtilities.InitializeItemFields(station, config);
        }

        var itemFields = PackagingStationExtensions.ItemFields[guid];
        var itemFieldsData = jsonObject["ItemFields"] as JArray;
        if (itemFieldsData != null && itemFieldsData.Count == 6)
        {
          for (int i = 0; i < 6; i++)
          {
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
                      $"PackagingStationLoaderPatch.LoadPostfix: Loaded ItemField {i} for station {guid}, ItemID: {itemID}", DebugLogger.Category.Handler);
                }
                else
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Warning,
                      $"PackagingStationLoaderPatch.LoadPostfix: Failed to load item for ItemField {i}, ItemID: {itemID}", DebugLogger.Category.Handler);
                }
              }
            }
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: Invalid or missing ItemFields data for station {guid}", DebugLogger.Category.Handler);
        }

        PackagingStationExtensions.RegisterConfig(station, config);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PackagingStationLoaderPatch.LoadPostfix: Loaded config for station {station.GUID}", DebugLogger.Category.Handler);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PackagingStationLoaderPatch.LoadPostfix: Failed for {mainPath}, error: {e}", DebugLogger.Category.Handler);
      }
    }
  }
}