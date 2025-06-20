using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.DevUtilities;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using static NoLazyWorkers.Stations.PackagingStationUtilities;
using static NoLazyWorkers.Stations.PackagingStationConfigUtilities;
using ScheduleOne.NPCs;
using ScheduleOne.Employees;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Storage.ShelfUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.Property;
using static NoLazyWorkers.Debug;

namespace NoLazyWorkers.Stations
{
  public static class PackagingStationExtensions
  {
    public static int MAXOPTIONS = 5;
    public static Dictionary<Guid, List<ItemField>> ItemFields { get; } = new();
    public static Dictionary<Guid, List<QualityField>> QualityFields { get; } = new();

    public class PackagingStationAdapter : IStationAdapter
    {
      private readonly PackagingStation _station;
      public Guid GUID => _station.GUID;
      public string Name => _station.Name;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public List<ItemSlot> InsertSlots => [_station.PackagingSlot];
      public List<ItemSlot> ProductSlots => [_station.ProductSlot];
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => (_station as IUsable).IsInUse;
      public bool HasActiveOperation => IsInUse;
      public int StartThreshold => 1;
      public List<ItemField> GetInputItemForProduct() => ItemFields[GUID];
      public void StartOperation(Employee employee) => (employee as Packager)?.PackagingBehaviour.StartPackaging();
      public int MaxProductQuantity => _station.ProductSlot?.ItemInstance.StackLimit ?? 0;
      public ITransitEntity TransitEntity => _station as ITransitEntity;
      public BuildableItem Buildable => _station as BuildableItem;
      public Property ParentProperty => _station.ParentProperty;
      public List<ItemInstance> RefillList() => GetRefillList(_station, ProductSlots);
      public bool CanRefill(ItemInstance item) => RefillList().Any(i => Utilities.AdvCanStackWith(i, item));
      public Type TypeOf => _station.GetType();

      public PackagingStationAdapter(PackagingStation station)
      {
        if (!IStations.TryGetValue(station.ParentProperty, out var propertyStations))
        {
          IStations[station.ParentProperty] = new();
          propertyStations = IStations[station.ParentProperty];
        }
        propertyStations.Add(GUID, this);
        _station = station ?? throw new ArgumentNullException(nameof(station));
        Log(Level.Info, $"PackagingStationAdapter: Initialized for station {station.GUID}", Category.PackagingStation);
      }
    }
  }

  public static class PackagingStationUtilities
  {
    private const int MAXOPTIONS = 5; // Maximum items in RefillList

    /// <summary>
    /// Retrieves the list of items for refilling the PackagingStation.
    /// If any ProductSlot has a non-null ItemInstance, returns the first matching item from the RefillList (or none).
    /// If all ProductSlots are null, returns the full RefillList (non-null, non-"None" items).
    /// </summary>
    /// <param name="station">The PackagingStation to refill.</param>
    /// <param name="productSlots">List of ProductSlot ItemSlots.</param>
    /// <returns>A list containing one matching item (if a ProductSlot is non-null) or the full RefillList (if all ProductSlots are null).</returns>
    public static List<ItemInstance> GetRefillList(PackagingStation station, List<ItemSlot> productSlots)
    {
      Log(Level.Verbose,
          $"GetRefillList: Start for station {station?.GUID.ToString() ?? "null"}, productSlots={productSlots?.Count ?? 0}",
          Category.PackagingStation);

      if (station == null || productSlots == null || !ItemFields.TryGetValue(station.GUID, out var fields) || !QualityFields.TryGetValue(station.GUID, out var qualities))
      {
        Log(Level.Warning,
            $"GetRefillList: Invalid inputs (station={station != null}, productSlots={productSlots != null} or itemfields={!ItemFields.ContainsKey(station.GUID)} or qualityfields={!QualityFields.ContainsKey(station.GUID)} do not contain GUID",
            Category.PackagingStation);
        return new List<ItemInstance>();
      }

      // Check for non-null ProductSlot.ItemInstance
      var activeSlot = productSlots.FirstOrDefault(slot => slot?.ItemInstance != null);
      if (activeSlot != null)
      {
        // Single-item mode: find first matching RefillList item
        var slotItem = activeSlot.ItemInstance;
        var items = new List<ItemInstance>(1);
        Log(Level.Verbose,
            $"GetRefillList: Found active slot with item={slotItem.ID}, quality={(slotItem as ProductItemInstance)?.Quality}",
            Category.PackagingStation);

        for (int i = 0; i < fields.Count; i++)
        {
          var itemDef = fields[i].SelectedItem;
          if (itemDef == null || itemDef.ID == "None")
            continue;

          if (itemDef.ID == "Any")
          {
            var targetQuality = qualities[i].Value;
            if (slotItem is ProductItemInstance slotProd && slotProd.Quality >= targetQuality)
            {
              var anyItem = new ProductItemInstance(itemDef, 1, targetQuality);
              items.Add(anyItem);
              Log(Level.Info,
                  $"GetRefillList: Matched 'Any' item with quality={targetQuality}, slot quality={slotProd.Quality} at index {i}",
                  Category.PackagingStation);
              break;
            }
          }
          else if (itemDef.GetDefaultInstance() is ProductItemInstance prodItem)
          {
            prodItem.SetQuality(qualities[i].Value);
            if (slotItem.AdvCanStackWith(prodItem, allowHigherQuality: false))
            {
              items.Add(prodItem);
              Log(Level.Info,
                  $"GetRefillList: Matched specific item {prodItem.ID}, quality={prodItem.Quality} at index {i}",
                  Category.PackagingStation);
              break;
            }
          }
        }

        Log(Level.Info,
            $"GetRefillList: Returned {items.Count} items (single-item mode) for station {station.GUID}",
            Category.PackagingStation);
        return items;
      }

      // Full RefillList mode: return all non-null, non-"None" items
      var fullItems = new List<ItemInstance>(MAXOPTIONS);
      for (int i = 0; i < fields.Count; i++)
      {
        var itemDef = fields[i].SelectedItem;
        if (itemDef == null || itemDef.ID == "None")
          continue;

        if (itemDef.ID == "Any")
        {
          var anyItem = new ProductItemInstance(itemDef, 1, qualities[i].Value);
          fullItems.Add(anyItem);
          Log(Level.Info,
              $"GetRefillList: Added 'Any' item with quality={qualities[i].Value} at index {i}",
              Category.PackagingStation);
        }
        else if (itemDef.GetDefaultInstance() is ProductItemInstance prodItem)
        {
          prodItem.SetQuality(qualities[i].Value);
          fullItems.Add(prodItem);
          Log(Level.Info,
              $"GetRefillList: Added {prodItem.ID} with quality={prodItem.Quality} at index {i}",
              Category.PackagingStation);
        }
      }

      Log(Level.Info,
          $"GetRefillList: Returned {fullItems.Count} items (full RefillList mode) for station {station.GUID}",
          Category.PackagingStation);
      return fullItems;
    }
  }

  public static class PackagingStationConfigUtilities
  {
    public static void Cleanup(PackagingStation station)
    {
      if (station == null)
      {
        Log(Level.Warning, "Cleanup: Station is null", Category.PackagingStation);
        return;
      }
      ItemFields.Remove(station.GUID);
      Log(Level.Info,
          $"PackagingExtensions.Cleanup: Removed data for station {station.GUID}", Category.PackagingStation);
    }

    public static void InitializeItemFields(PackagingStation station, PackagingStationConfiguration config)
    {
      try
      {
        Guid guid = station.GUID;
        if (!IStations[station.ParentProperty].TryGetValue(guid, out var adapter))
        {
          adapter = new PackagingStationAdapter(station);
          IStations[station.ParentProperty][guid] = adapter;
        }
        Log(Level.Info, $"InitializeItemFields: Initializing for station {guid}", Category.PackagingStation);

        // Ensure StationRefills is initialized
        if (!StationRefillLists.ContainsKey(guid))
          StationRefillLists[guid] = new List<ItemInstance>(MAXOPTIONS + 1);
        while (StationRefillLists[guid].Count < MAXOPTIONS + 1)
          StationRefillLists[guid].Add(null);

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
              if (i < StationRefillLists[guid].Count && StationRefillLists[guid][i] is ProductItemInstance prodItem)
              {
                prodItem.SetQuality(quality);
                Log(Level.Verbose, $"InitializeItemFields: Set quality {quality} for index {i} in station {guid}", Category.PackagingStation);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              Log(Level.Error, $"InitializeItemFields: Failed to set quality for index {i} in station {guid}, error: {e}", Category.PackagingStation);
            }
          });
          qualityFields.Add(targetQuality);

          var itemField = new ItemField(config) { CanSelectNone = false };
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(item =>
          {
            try
            {
              if (i < StationRefillLists[guid].Count)
              {
                StationRefillLists[guid][i] = item?.GetDefaultInstance();
                Log(Level.Verbose, $"InitializeItemFields: Set item {item?.ID ?? "null"} for index {i} in station {guid}", Category.PackagingStation);
              }
              config.InvokeChanged();
            }
            catch (Exception e)
            {
              Log(Level.Error, $"InitializeItemFields: Failed to set item for index {i} in station {guid}, error: {e}", Category.PackagingStation);
            }
          });
          itemFields.Add(itemField);
        }

        // Register fields
        ItemFields[guid] = itemFields;
        QualityFields[guid] = qualityFields;
        Log(Level.Info, $"InitializeItemFields: Initialized {itemFields.Count} ItemFields for station {guid}", Category.PackagingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"InitializeItemFields: Failed for station {station?.GUID.ToString() ?? "null"}, error: {e}", Category.PackagingStation);
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
        Log(Level.Info,
            $"PackagingStationPanelPatch.BindPostfix: Binding {configs.Count} configs", Category.PackagingStation);

        var configPanel = __instance.GetComponent<PackagingStationConfigPanel>();
        if (configPanel == null)
        {
          configPanel = __instance.gameObject.AddComponent<PackagingStationConfigPanel>();
          Log(Level.Info,
              $"PackagingStationPanelPatch.BindPostfix: Added PackagingStationConfigPanel to {__instance.gameObject.name}", Category.PackagingStation);
        }

        ItemFieldUI templateUI = null;
        var potPanel = GetPrefabGameObject("PotConfigPanel");
        if (potPanel != null)
        {
          templateUI = potPanel.GetComponentInChildren<ItemFieldUI>();
        }

        var favorites = new List<ItemDefinition>
        {
          new ItemDefinition() { Name = "None", ID = "None", Icon = GetCrossSprite() },
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
            Log(Level.Warning,
                $"PackagingStationPanelPatch.BindPostfix: No ItemFields for station {config.Station.GUID}", Category.PackagingStation);
            continue;
          }
          if (!QualityFields.TryGetValue(config.Station.GUID, out var qualityFields))
          {
            Log(Level.Warning,
                $"PackagingStationPanelPatch.BindPostfix: No ItemFields for station {config.Station.GUID}", Category.PackagingStation);
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

          Log(Level.Info,
              $"PackagingStationPanelPatch.BindPostfix: Bound ItemFieldUI_{i} to {iFieldsForIndex.Count} ItemFields", Category.PackagingStation);
        }

        Log(Level.Info,
            $"PackagingStationPanelPatch.BindPostfix: Added and bound 6 ItemFieldUIs for {configs.Count} configs", Category.PackagingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"PackagingStationPanelPatch.BindPostfix: Failed, error: {e}", Category.PackagingStation);
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
        if (station == null || __instance == null)
        {
          Log(Level.Error, "InitializeItemFields: Station or Configuration is null", Category.PackagingStation);
          return;
        }
        Log(Level.Info,
            $"PackagingStationConfigurationPatch.ConstructorPostfix: Initializing for station {station.GUID}", Category.PackagingStation);
        if (!ItemFields.ContainsKey(station.GUID))
        {
          InitializeItemFields(station, __instance);
        }
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"PackagingStationConfigurationPatch.ConstructorPostfix: Failed for station {station.GUID}, error: {e}", Category.PackagingStation);
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
      Log(Level.Verbose,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Starting station {__instance.Station.GUID}", Category.PackagingStation);
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
        Log(Level.Verbose,
              $"PackagingStationConfigurationPatch.GetSaveStringPostfix: station {__instance.Station.GUID} itemfields: {json["ItemFields"].ToString(Newtonsoft.Json.Formatting.Indented)}", Category.PackagingStation);
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
        Log(Level.Verbose,
              $"PackagingStationConfigurationPatch.GetSaveStringPostfix: station {__instance.Station.GUID} itemfields: {json["Qualities"].ToString(Newtonsoft.Json.Formatting.Indented)}", Category.PackagingStation);
        __result = json.ToString(Newtonsoft.Json.Formatting.Indented);

        Log(Level.Info,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Saved JSON for station {__instance.Station.GUID}: {__result}", Category.PackagingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"PackagingStationConfigurationPatch.GetSaveStringPostfix: Failed for station {__instance.Station.GUID}, error: {e}", Category.PackagingStation);
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
        Log(Level.Error,
            $"PackagingStationConfigurationPatch.DestroyPostfix: Failed for station {__instance.Station?.GUID}, error: {e}", Category.PackagingStation);
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
          Log(Level.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: No GridItem for {mainPath}", Category.PackagingStation);
          return;
        }

        if (!(gridItem is PackagingStation station))
        {
          Log(Level.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: GridItem is not a PackagingStation for {mainPath}", Category.PackagingStation);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          Log(Level.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: No Configuration.json at {configPath}", Category.PackagingStation);
          return;
        }

        string json = File.ReadAllText(configPath);
        var jsonObject = JObject.Parse(json);
        var config = station.stationConfiguration;
        if (config == null)
        {
          Log(Level.Error,
              $"PackagingStationLoaderPatch.LoadPostfix: No valid PackagingStationConfiguration for station {station.GUID}", Category.PackagingStation);
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
                  Log(Level.Info,
                      $"PackagingStationLoaderPatch.LoadPostfix: Loaded ItemField {i} for station {guid}, ItemID: {itemID}", Category.PackagingStation);
                }
                else
                {
                  Log(Level.Warning,
                      $"PackagingStationLoaderPatch.LoadPostfix: Failed to load item for ItemField {i}, ItemID: {itemID}", Category.PackagingStation);
                }
              }
            }
          }
        }
        else
        {
          Log(Level.Warning,
              $"PackagingStationLoaderPatch.LoadPostfix: Invalid or missing ItemFields data for station {guid}", Category.PackagingStation);
        }
        Log(Level.Info,
            $"PackagingStationLoaderPatch.LoadPostfix: Loaded config for station {station.GUID}", Category.PackagingStation);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"PackagingStationLoaderPatch.LoadPostfix: Failed for {mainPath}, error: {e}", Category.PackagingStation);
      }
    }
  }
}