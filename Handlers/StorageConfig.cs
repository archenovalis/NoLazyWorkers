using FishNet.Connection;
using FishNet.Object;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Property;
using Grid = ScheduleOne.Tiles.Grid;
using ScheduleOne.UI.Management;
using System.Collections;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.UI;
using UnityEngine.Events;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.ConfigurationExtensions;
using static NoLazyWorkers.Handlers.StorageExtensions;
using static NoLazyWorkers.Handlers.StorageUtilities;
using FishNet.Managing;
using FishNet.Managing.Object;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Persistence;
using MelonLoader.TinyJSON;
using UnityEngine.EventSystems;
using ScheduleOne.Tools;

namespace NoLazyWorkers.Handlers
{
  public static class StorageExtensions
  {
    public const int StorageEnum = 345543;
    private static ConfigPanel ConfigPanelTemplate;
    private static Sprite CrossSprite;
    public static Dictionary<Guid, StorageConfiguration> Config = [];
    public static Dictionary<Guid, PlaceableStorageEntity> Storage = [];
    public static Dictionary<Guid, JObject> PendingConfigData = [];
    public static List<PlaceableStorageEntity> AnyShelves = [];
    public static Dictionary<ItemKey, Dictionary<PlaceableStorageEntity, ShelfInfo>> ShelfCache = new();
    public static readonly string[] InstanceIDs = ["smallstoragerack", "mediumstoragerack", "largestoragerack", "safe"];

    public enum StorageMode
    {
      None,    // No items allowed (red cross)
      Any,     // Any item allowed (blank)
      Specific // Specific item allowed (item's icon or packaged icon)
    }

    public class StorageItemOption : ItemSelector.Option
    {
      public ItemDefinition PackagingDefinition { get; private set; }

      public StorageItemOption(string title, ItemDefinition item, ItemDefinition packagingDefinition)
          : base(title, item)
      {
        PackagingDefinition = packagingDefinition;
      }
    }

    public static void InitializeStorageModule()
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg("InitializeStorageModule: Starting");
      GetStorageConfigPanelTemplate();
      CacheCrossSprite();
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"InitializeStorageModule: ConfigPanelTemplate {(ConfigPanelTemplate != null ? "initialized" : "null")}");
    }

    public static bool InitializeStorage(Guid guid, PlaceableStorageEntity storage)
    {
      try
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"InitializeStorage: Called for GUID: {guid}, storage: {(storage != null ? storage.name : "null")}");

        if (storage == null || guid == Guid.Empty)
        {
          MelonLogger.Error($"InitializeStorage: storage is null or guid is empty ({guid})");
          return false;
        }

        if (!Storage.ContainsKey(guid))
        {
          Storage[guid] = storage;
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg($"InitializeStorage: Added storage to dictionary for GUID: {guid}, Storage count: {Storage.Count}");
        }

        var proxy = StorageConfigurableProxy.AttachToStorage(storage);
        if (proxy == null)
        {
          MelonLogger.Error($"InitializeStorage: Failed to attach proxy for GUID: {guid}");
          return false;
        }

        bool initialized = false;
        CoroutineRunner.Instance.RunCoroutineWithResult<bool>(WaitForProxyInitialization(guid, storage, proxy), success => initialized = success);

        return initialized;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"InitializeStorage: Failed for GUID: {guid}, error: {e.Message}");
        return false;
      }
    }

    private static IEnumerator WaitForProxyInitialization(Guid guid, PlaceableStorageEntity storage, StorageConfigurableProxy proxy)
    {
      int retries = 30;
      float delay = 0.05f;
      for (int i = 0; i < retries; i++)
      {
        if (proxy != null && proxy.configuration != null)
        {
          Property property = storage.GetProperty(storage.transform);
          if (property != null)
          {
            storage.ParentProperty = property;
            proxy.CreateWorldspaceUI();
            property.AddConfigurable(proxy);

            if (DebugLogs.All || DebugLogs.Storage)
              MelonLogger.Msg($"WaitForProxyInitialization: Completed for GUID: {guid}");
            yield return true;
            yield break;
          }
        }
        yield return new WaitForSeconds(delay);
      }
      MelonLogger.Error($"WaitForProxyInitialization: Failed for GUID: {guid} after {retries} retries");
      yield return false;
    }

    private static void CacheCrossSprite()
    {
      CrossSprite = GetCrossSprite();
      if (CrossSprite == null && (DebugLogs.All || DebugLogs.Storage))
        MelonLogger.Warning("CacheCrossSprite: Failed to load cross sprite");
      else if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg("CacheCrossSprite: Successfully cached cross sprite");
    }

    public static Sprite GetCrossSprite()
    {
      if (CrossSprite != null)
        return CrossSprite;

      var prefab = GetPrefabGameObject("Supplier Stash");
      if (prefab == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("GetCrossSprite: Supplier Stash prefab not found");
        return null;
      }
      else if (DebugLogs.All || (DebugLogs.Storage && DebugLogs.Stacktrace))
        MelonLogger.Msg($"GetCrossSprite: Prefab hierarchy:\n{DebugTransformHierarchy(prefab.transform)}");

      var iconObj = prefab.transform.Find("Dead drop hopper/Icon");
      if (iconObj == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("GetCrossSprite: Icon GameObject not found in Supplier Stash prefab");
        return null;
      }

      var renderer = iconObj.GetComponent<MeshRenderer>();
      if (renderer == null || renderer.material == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("GetCrossSprite: No MeshRenderer or material found on Icon GameObject");
        return null;
      }

      var texture = renderer.material.mainTexture as Texture2D;
      if (texture == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("GetCrossSprite: No Texture2D found in material");
        return null;
      }
      // Create Sprite from Texture2D
      var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
      if (sprite != null)
      {
        sprite.name = "Cross";
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"GetCrossSprite: Created sprite '{sprite.name}' from Texture2D '{texture.name}'");
      }
      CrossSprite = sprite;
      return sprite;
    }

    public static Sprite GetPackagedSprite(ItemDefinition product, ItemDefinition packaging)
    {
      if (product == null || packaging == null)
        return product?.Icon;

      var productIconManager = ProductIconManager.Instance;
      if (productIconManager == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("GetPackagedSprite: ProductIconManager instance is null");
        return product?.Icon;
      }

      var sprite = productIconManager.GetIcon(product.ID, packaging.ID);
      if (sprite == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("GetPackagedSprite: Sprite is null");
        return product?.Icon;
      }
      sprite.name = $"{product.Name} ({packaging.Name})";
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"GetPackagedSprite: Created sprite '{sprite.name}' for product {product.ID}, packaging {packaging.ID}");
      return sprite;
    }

    public static ConfigPanel GetStorageConfigPanelTemplate()
    {
      try
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg("GetStorageConfigPanelTemplate: Starting");

        if (ConfigPanelTemplate != null)
        {
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg("GetStorageConfigPanelTemplate: Returning cached template");
          return ConfigPanelTemplate;
        }
        Transform storageTransform = GetTransformTemplateFromConfigPanel(EConfigurableType.Pot, "");
        if (storageTransform == null)
        {
          MelonLogger.Error("GetStorageConfigPanelTemplate: storageTransform is null");
          return null;
        }

        GameObject storageObj = Object.Instantiate(storageTransform.gameObject);
        storageObj.name = "StorageConfigPanel";
        if (storageObj.GetComponent<CanvasRenderer>() == null)
          storageObj.AddComponent<CanvasRenderer>();
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg("GetStorageConfigPanelTemplate: Instantiated StorageConfigPanel");

        var defaultScript = storageObj.GetComponent<PotConfigPanel>();
        if (defaultScript == null)
        {
          MelonLogger.Error("GetStorageConfigPanelTemplate: PotConfigPanel script not found");
          return null;
        }
        Object.Destroy(defaultScript);

        StorageConfigPanel configPanel = storageObj.AddComponent<StorageConfigPanel>();
        if (configPanel == null)
        {
          MelonLogger.Error("GetStorageConfigPanelTemplate: Failed to add StorageConfigPanel");
          return null;
        }

        foreach (string partName in new[] { "Additive2", "Additive3", "Botanist", "ObjectFieldUI", "SupplyUI" })
        {
          var part = storageObj.transform.Find(partName);
          if (part != null)
            Object.Destroy(part.gameObject);
        }
        storageObj.transform.Find("Seed").gameObject.SetActive(false);
        Transform itemFieldUITransform = storageObj.transform.Find("Additive1");
        if (itemFieldUITransform == null)
        {
          MelonLogger.Error("GetStorageConfigPanelTemplate: itemFieldUITransform (Additive1) not found");
          return null;
        }

        GameObject itemFieldUIObj = itemFieldUITransform.gameObject;
        itemFieldUIObj.name = "StorageItemUI";
        itemFieldUITransform.gameObject.SetActive(true);
        if (itemFieldUIObj.GetComponent<ItemFieldUI>() == null)
          itemFieldUIObj.AddComponent<ItemFieldUI>();
        if (itemFieldUIObj.GetComponent<CanvasRenderer>() == null)
          itemFieldUIObj.AddComponent<CanvasRenderer>();
        var itemFieldRect = itemFieldUIObj.GetComponent<RectTransform>();
        itemFieldRect.anchoredPosition = new Vector2(itemFieldRect.anchoredPosition.x, itemFieldRect.anchoredPosition.y - 50f);
        itemFieldUIObj.SetActive(true);

        configPanel.StorageItemUI = itemFieldUIObj.GetComponent<ItemFieldUI>();
        if (configPanel.StorageItemUI == null)
        {
          MelonLogger.Error("GetStorageConfigPanelTemplate: Failed to assign StorageItemUI");
          return null;
        }

        TextMeshProUGUI titleText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>().FirstOrDefault(t => t.gameObject.name == "Title");
        if (titleText != null)
        {
          titleText.text = "Assign Item";
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg("GetStorageConfigPanelTemplate: Set Title to 'Assign Item'");
        }

        TextMeshProUGUI descText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>().FirstOrDefault(t => t.gameObject.name == "Description");
        if (descText != null)
        {
          descText.text = "Select the item to assign to this shelf";
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg("GetStorageConfigPanelTemplate: Set Description");
        }

        ConfigPanelTemplate = configPanel;
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg("GetStorageConfigPanelTemplate: Template initialized successfully");
        return configPanel;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"GetStorageConfigPanelTemplate: Failed, error: {e.Message}");
        return null;
      }
    }
  }

  public static class StorageUtilities
  {
    public struct ItemKey
    {
      public string ItemID { get; private set; }
      public string PackagingID { get; private set; }

      public ItemKey(ItemInstance item)
      {
        ItemID = item?.ID?.ToLower() ?? throw new ArgumentNullException(nameof(item));
        PackagingID = item is ProductItemInstance prodItem && prodItem.AppliedPackaging != null
            ? prodItem.AppliedPackaging.ID.ToLower()
            : null;
      }

      public ItemKey(string itemId, string packagingId = null)
      {
        ItemID = itemId?.ToLower() ?? throw new ArgumentNullException(nameof(itemId));
        PackagingID = packagingId?.ToLower();
      }

      public override bool Equals(object obj) =>
          obj is ItemKey other && ItemID == other.ItemID && PackagingID == other.PackagingID;

      public override int GetHashCode() =>
          HashCode.Combine(ItemID, PackagingID);

      public override string ToString() =>
          PackagingID != null ? $"{ItemID} (pkg: {PackagingID})" : ItemID;
    }

    public struct ShelfInfo
    {
      public PlaceableStorageEntity Shelf { get; set; }
      public int Quantity { get; set; }
      public bool IsConfigured { get; set; }

      public ShelfInfo(PlaceableStorageEntity shelf, int quantity, bool isConfigured)
      {
        Shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        Quantity = quantity;
        IsConfigured = isConfigured;
      }
    }

    public static void UpdateShelfConfiguration(PlaceableStorageEntity shelf, ItemInstance assignedItem)
    {
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"UpdateShelfConfiguration: Shelf is null", isStorage: true);
        return;
      }

      if (!Config.TryGetValue(shelf.GUID, out var config))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"UpdateShelfConfiguration: No config for shelf {shelf.GUID}", isStorage: true);
        return;
      }

      // Remove shelf from all lists
      RemoveShelfFromLists(shelf);

      if (config.Mode == StorageMode.Any)
      {
        AnyShelves.Add(shelf);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"UpdateShelfConfiguration: Added shelf {shelf.GUID} to AnyShelves, count={AnyShelves.Count}",
            isStorage: true);
      }
      else if (config.Mode == StorageMode.Specific && assignedItem != null)
      {
        ItemKey key = new ItemKey(assignedItem);
        if (!ShelfCache.ContainsKey(key))
          ShelfCache[key] = new Dictionary<PlaceableStorageEntity, ShelfInfo>();

        int quantity = GetItemQuantityInShelf(shelf, assignedItem);
        ShelfCache[key][shelf] = new ShelfInfo(shelf, quantity, true);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"UpdateShelfConfiguration: Added shelf {shelf.GUID} to ShelfCache for {key}, quantity={quantity}, isConfigured=true",
            isStorage: true);
      }

      // Update quantities for non-configured shelves
      UpdateShelfCache(shelf);
    }

    public static void UpdateShelfCache(PlaceableStorageEntity shelf)
    {
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"UpdateShelfCache: Shelf is null", isStorage: true);
        return;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateShelfCache: Updating for shelf {shelf.GUID}", isStorage: true);

      // Update quantities for all items in shelf
      var slots = (shelf.InputSlots ?? Enumerable.Empty<ItemSlot>()).Concat(shelf.OutputSlots ?? Enumerable.Empty<ItemSlot>());
      var itemQuantities = new Dictionary<ItemKey, int>();

      foreach (var slot in slots)
      {
        if (slot?.ItemInstance == null || slot.Quantity <= 0)
          continue;

        ItemKey key = new ItemKey(slot.ItemInstance);
        itemQuantities[key] = itemQuantities.GetValueOrDefault(key, 0) + slot.Quantity;
      }

      // Update ShelfCache with current quantities
      foreach (var kvp in itemQuantities)
      {
        ItemKey key = kvp.Key;
        int quantity = kvp.Value;

        if (!ShelfCache.ContainsKey(key))
          ShelfCache[key] = new Dictionary<PlaceableStorageEntity, ShelfInfo>();

        bool isConfigured = Config.TryGetValue(shelf.GUID, out var config) &&
                            config.Mode == StorageMode.Specific &&
                            config.AssignedItem != null &&
                            new ItemKey(config.AssignedItem).Equals(key);

        ShelfCache[key][shelf] = new ShelfInfo(shelf, quantity, isConfigured);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"UpdateShelfCache: Updated shelf {shelf.GUID} for {key}, quantity={quantity}, isConfigured={isConfigured}",
            isStorage: true);
      }

      // Remove shelf from keys where it has no items
      foreach (var itemCache in ShelfCache.ToList())
      {
        if (itemCache.Value.ContainsKey(shelf) && !itemQuantities.ContainsKey(itemCache.Key))
        {
          itemCache.Value.Remove(shelf);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateShelfCache: Removed shelf {shelf.GUID} from {itemCache.Key} (no items)",
              isStorage: true);
        }
        if (itemCache.Value.Count == 0)
        {
          ShelfCache.Remove(itemCache.Key);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateShelfCache: Removed empty key {itemCache.Key}",
              isStorage: true);
        }
      }
    }

    public static void RemoveShelfFromLists(PlaceableStorageEntity shelf)
    {
      if (shelf == null)
        return;

      AnyShelves.Remove(shelf);
      foreach (var itemCache in ShelfCache.Values)
      {
        itemCache.Remove(shelf);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"RemoveShelfFromLists: Removed shelf {shelf.GUID} from all lists",
          isStorage: true);
    }

    public static Dictionary<PlaceableStorageEntity, int> FindShelvesWithItem(NPC npc, ItemInstance targetItem, int needed, int wanted = 0)
    {
      if (targetItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"FindShelvesWithItem: Target item is null for {npc.fullName}",
            isChemist: true);
        return new Dictionary<PlaceableStorageEntity, int>();
      }

      ItemKey key = new ItemKey(targetItem);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindShelvesWithItem: Searching for {key}, needed={needed}, wanted={wanted} for {npc.fullName}",
          isChemist: true);

      var result = new Dictionary<PlaceableStorageEntity, int>();
      if (ShelfCache.TryGetValue(key, out var shelves))
      {
        foreach (var shelfInfo in shelves.Values)
        {
          if (shelfInfo.Quantity >= needed)
          {
            int assignedQty = wanted > 0 ? Math.Min(shelfInfo.Quantity, wanted) : shelfInfo.Quantity;
            result[shelfInfo.Shelf] = assignedQty;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindShelvesWithItem: Found shelf {shelfInfo.Shelf.GUID} with {shelfInfo.Quantity} of {key}, assigned {assignedQty}, isConfigured={shelfInfo.IsConfigured}",
                isChemist: true);
          }
        }
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindShelvesWithItem: Found {result.Count} shelves for {key}, totalQty={result.Values.Sum()}",
          isChemist: true);
      return result;
    }

    public static PlaceableStorageEntity FindShelfForDelivery(NPC npc, ItemInstance targetItem)
    {
      if (targetItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"FindShelfForDelivery: Target item is null", isStorage: true);
        return null;
      }

      ItemKey key = new ItemKey(targetItem);
      int quantityToDeliver = targetItem.Quantity;

      PlaceableStorageEntity selectedShelf = null;
      if (ShelfCache.TryGetValue(key, out var itemShelves))
      {
        foreach (var shelfInfo in itemShelves.Values)
        {
          if (!shelfInfo.IsConfigured) continue;
          if (!shelfInfo.Shelf.StorageEntity.CanItemFit(targetItem, quantityToDeliver)) continue;
          if (!npc.movement.CanGetTo(shelfInfo.Shelf)) continue;
          selectedShelf = shelfInfo.Shelf;
          break;
        }
      }

      if (selectedShelf == null)
      {
        foreach (var shelf in AnyShelves)
        {
          if (!shelf.StorageEntity.CanItemFit(targetItem, quantityToDeliver)) continue;
          if (!npc.movement.CanGetTo(shelf)) continue;
          selectedShelf = shelf;
          break;
        }
      }

      if (selectedShelf != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"FindShelfForDelivery: Selected shelf {selectedShelf.GUID} for {quantityToDeliver} of {key}",
            isStorage: true);
      }
      return selectedShelf;
    }

    public static int GetItemQuantityInShelf(PlaceableStorageEntity shelf, ItemInstance targetItem)
    {
      if (shelf == null || targetItem == null)
        return 0;

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetItemQuantityInShelf: Checking shelf {shelf.GUID} for {targetItem.ID}", isStorage: true);

      int qty = 0;
      foreach (var slot in (shelf.OutputSlots ?? Enumerable.Empty<ItemSlot>()).Concat(shelf.InputSlots ?? Enumerable.Empty<ItemSlot>()))
      {
        if (slot?.ItemInstance != null && slot.ItemInstance.ID.ToLower() == targetItem.ID.ToLower())
          qty += slot.Quantity;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetItemQuantityInShelf: Found {qty} of {targetItem.ID} in shelf {shelf.GUID}", isStorage: true);
      return qty;
    }

    public struct ShelfSearchContext
    {
      public NPC Npc { get; set; }
      public ItemInstance TargetItem { get; set; }
      public int Needed { get; set; }
      public int Wanted { get; set; }
    }
  }

  public class StorageConfigPanel : ConfigPanel
  {
    [SerializeField]
    public ItemFieldUI StorageItemUI;
    private Button Button;
    private UnityAction OnChanged;

    private void RefreshChanged(ItemDefinition item, StorageConfiguration config)
    {
      OnChanged?.Invoke();
      var worldSpaceUI = (StorageUIElement)config.Proxy.WorldspaceUI;
      worldSpaceUI.RefreshUI();
      InvokeChanged(config);
    }

    public override void Bind(List<EntityConfiguration> configs)
    {
      try
      {
        transform.gameObject.SetActive(true);

        if (StorageItemUI == null)
        {
          var storageItemUITransform = gameObject.transform.Find("StorageItemUI");
          if (storageItemUITransform != null)
          {
            StorageItemUI = storageItemUITransform.gameObject.GetComponent<ItemFieldUI>();
            if (StorageItemUI == null)
              StorageItemUI = storageItemUITransform.gameObject.AddComponent<ItemFieldUI>();
          }
        }

        StorageItemUI.gameObject.SetActive(true);
        if (StorageItemUI.gameObject.GetComponent<CanvasRenderer>() == null)
          StorageItemUI.gameObject.AddComponent<CanvasRenderer>();

        List<ItemField> itemFieldList = [];
        bool hasNone = false, hasAny = false, hasSpecific = false;
        ItemInstance item = null;
        bool isConsistent = true;

        foreach (var config in configs.OfType<StorageConfiguration>())
        {
          var itemField = config.StorageItem;
          itemField.onItemChanged.RemoveAllListeners();
          itemField.onItemChanged.AddListener(item => RefreshChanged(item, config));
          itemFieldList.Add(itemField);

          if (config.Mode == StorageMode.None)
            hasNone = true;
          else if (config.Mode == StorageMode.Any)
            hasAny = true;
          else if (config.Mode == StorageMode.Specific)
          {
            hasSpecific = true;
            if (item == null)
            {
              item = config.AssignedItem;
            }
            else if (item.ID != config.AssignedItem.ID || (item as ProductItemInstance)?.AppliedPackaging?.ID != (config.AssignedItem as ProductItemInstance)?.AppliedPackaging?.ID)
            {
              isConsistent = false;
              break;
            }
          }
        }
        StorageItemUI.Bind(itemFieldList);

        if (hasNone && !hasAny && !hasSpecific)
          StorageItemUI.SelectionLabel.text = "None";
        else if (hasAny && !hasNone && !hasSpecific)
          StorageItemUI.SelectionLabel.text = "Any";
        else if (hasSpecific && !hasNone && !hasAny && isConsistent)
          StorageItemUI.SelectionLabel.text = item.Name ?? "None"; //TODO verify full name
        else
          StorageItemUI.SelectionLabel.text = "Mixed";

        Button = StorageItemUI.transform.Find("Selection")?.GetComponent<Button>();
        Button.onClick.RemoveAllListeners();
        Button.onClick.AddListener(() => OpenItemSelectorScreen(itemFieldList, "Assign Item", hasAny && !hasNone && !hasSpecific));

        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfigPanel.Bind: Bound {itemFieldList.Count} items");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel.Bind: Failed, error: {e.Message}");
      }
    }

    public void OpenItemSelectorScreen(List<ItemField> itemFields, string fieldName, bool isAnyMode)
    {
      if (itemFields == null || itemFields.Count == 0 || itemFields.Any(f => f == null))
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("StorageConfigPanel: OpenItemSelectorScreen called with null or empty itemFields");
        return;
      }

      if (Singleton<ManagementInterface>.Instance?.ItemSelectorScreen == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Error("StorageConfigPanel: ItemSelectorScreen is null or ManagementInterface is not initialized");
        return;
      }

      ItemField primaryField = itemFields[0];
      var primaryConfig = (StorageConfiguration)primaryField.ParentConfig;
      List<ItemSelector.Option> list = [];
      ItemSelector.Option selectedOption = null;

      // Add None and Any options
      list.Add(new StorageItemOption("None", null, null));
      list.Add(new StorageItemOption("Any", null, null));

      // Set selected option based on mode
      if (primaryConfig.Mode == StorageMode.None)
        selectedOption = list[0];
      else if (primaryConfig.Mode == StorageMode.Any)
        selectedOption = list[1];

      // Build options from inventory
      var itemSlots = Player.Local.Inventory.ToList();
      //TODO: add the shelf's items too
      Dictionary<string, List<(ItemDefinition product, ItemDefinition packaging)>> productOptions = [];

      foreach (var slot in itemSlots)
      {
        if (slot.ItemInstance == null)
          continue;

        var instance = slot.ItemInstance;
        ItemDefinition product = instance.Definition;
        ItemDefinition packaging = null;

        if (instance is ProductItemInstance productInstance && productInstance.AppliedPackaging != null)
          packaging = productInstance.AppliedPackaging;

        string key = packaging != null ? $"{product.ID}_{packaging.ID}" : product.ID;
        if (!productOptions.ContainsKey(key))
          productOptions[key] = [];
        productOptions[key].Add((product, packaging));
      }

      foreach (var pair in productOptions)
      {
        var (product, packaging) = pair.Value[0]; // Take first instance
        if (product == null)
        {
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Warning("StorageConfigPanel: Skipping null product in Options");
          continue;
        }

        string title = packaging != null ? $"{product.Name} ({packaging.Name})" : product.Name;
        var opt = new StorageItemOption(title, product, packaging);
        list.Add(opt);

        if (primaryConfig.Mode == StorageMode.Specific &&
            primaryField.SelectedItem == product &&
            (primaryConfig.AssignedItem as ProductItemInstance)?.AppliedPackaging == packaging)
          selectedOption = opt;
      }

      try
      {
        Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, list, selectedOption, new Action<ItemSelector.Option>(selected =>
        {
          StorageMode newMode;
          ItemDefinition selectedItem = selected.Item;
          PackagingDefinition selectedPackaging = (selected is StorageItemOption storageOption ? storageOption?.PackagingDefinition : null) as PackagingDefinition;

          if (selected.Title == "None")
            newMode = StorageMode.None;
          else if (selected.Title == "Any")
            newMode = StorageMode.Any;
          else
            newMode = StorageMode.Specific;

          foreach (var itemField in itemFields)
          {
            ItemInstance item = null;
            var config = (StorageConfiguration)itemField.ParentConfig;
            if (newMode == StorageMode.Specific)
            {
              item = Registry.GetItem(selectedItem.ID).GetDefaultInstance();
              if (selectedPackaging != null)
                (item as ProductItemInstance).SetPackaging(selectedPackaging);
            }
            config.SetModeAndItem(newMode, item);
          }

          StorageItemUI.SelectionLabel.text = newMode == StorageMode.Any ? "Any" :
                                              newMode == StorageMode.None ? "None" :
                                              selectedPackaging != null ? $"{selectedItem?.Name} ({selectedPackaging?.Name})" :
                                              selectedItem?.Name ?? "None";
          StorageItemUI.gameObject.SetActive(true);

          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg($"StorageConfigPanel: Selected mode: {newMode}, item: {selectedItem?.Name ?? "null"}, packaging: {selectedPackaging?.Name ?? "null"} for {fieldName}");
        }));

        Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Open();
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel: Failed to open ItemSelectorScreen for {fieldName}, error: {e.Message}");
      }
    }
  }

  public class StorageConfiguration : EntityConfiguration
  {
    public ItemField StorageItem { get; private set; }
    public PlaceableStorageEntity Storage { get; private set; }
    public StorageConfigurableProxy Proxy { get; private set; }
    public StorageMode Mode { get; set; }
    public ItemInstance AssignedItem { get; set; }

    public StorageConfiguration(ConfigurationReplicator replicator, IConfigurable configurable, PlaceableStorageEntity storage)
        : base(replicator, configurable)
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfiguration: Initializing for GUID: {storage?.GUID}");

      if (replicator == null || configurable == null || storage == null)
      {
        MelonLogger.Error($"StorageConfiguration: Null parameters - replicator: {replicator == null}, configurable: {configurable == null}, storage: {storage == null}, GUID: {storage?.GUID}");
        throw new ArgumentNullException("StorageConfiguration: One or more parameters are null");
      }

      replicator.Configuration = this;
      Proxy = configurable as StorageConfigurableProxy;
      Storage = storage;
      Storage.StorageEntity.onContentsChanged.AddListener(() => UpdateShelfCache(Storage));

      try
      {
        StorageItem = new ItemField(this)
        {
          CanSelectNone = false,
          Options = []
        };
        StorageItem.onItemChanged.AddListener(_ => UpdateShelfConfiguration(Storage, AssignedItem));
        Config[storage.GUID] = this;
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfiguration: Initialized for GUID: {storage.GUID}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfiguration: Failed to initialize StorageItem for GUID: {storage.GUID}, error: {e.Message}");
        throw;
      }
    }

    public void Load(JObject jsonObject)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"StorageConfiguration.Load: Loading for GUID={Storage.GUID}, JSON={jsonObject.ToString()}", isStorage: true);

        if (jsonObject["StorageMode"] != null)
        {
          var str = jsonObject["StorageMode"].ToString();
          try
          {
            Mode = (StorageMode)Enum.Parse(typeof(StorageMode), str);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"StorageConfiguration.Load: Set Mode={Mode} for GUID={Storage.GUID}", isChemist: true);
            if (Mode == StorageMode.Any)
            {
              AnyShelves.Add(Storage);
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"StorageConfiguration.Load: Added shelf {Storage.GUID} to AnyShelves, count={AnyShelves.Count}", isChemist: true);
            }
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"StorageConfiguration.Load: Invalid StorageMode '{str}' for GUID={Storage.GUID}, error: {e.Message}", isStorage: true);
            Mode = StorageMode.None;
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"StorageConfiguration.Load: StorageMode missing for GUID={Storage.GUID}", isStorage: true);
          Mode = StorageMode.None;
        }

        if (jsonObject["StorageItem"] != null)
        {
          string itemInstanceJson = jsonObject["StorageItem"].ToString();
          ItemInstance itemInstance = ItemDeserializer.LoadItem(itemInstanceJson);
          if (itemInstance != null)
          {
            StorageItem.SelectedItem = itemInstance.Definition;
            AssignedItem = itemInstance;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"StorageConfiguration.Load: Loaded Item={StorageItem.SelectedItem?.Name}, Packaging={(itemInstance is ProductItemInstance pi ? pi.AppliedPackaging?.Name : "null")} for GUID={Storage.GUID}", isStorage: true);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"StorageConfiguration.Load: Failed to deserialize StorageItem for GUID={Storage.GUID}, JSON={itemInstanceJson}", isStorage: true);
            StorageItem.SelectedItem = null;
            AssignedItem = null;
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"StorageConfiguration.Load: StorageItem missing for GUID={Storage.GUID}", isStorage: true);
          StorageItem.SelectedItem = null;
          AssignedItem = null;
        }

        UpdateShelfConfiguration(Storage, AssignedItem);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StorageConfiguration.Load: Completed for GUID={Storage.GUID}, Mode={Mode}, Item={StorageItem.SelectedItem?.Name ?? "null"}, ShelfCache count={ShelfCache.Count}", isStorage: true);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StorageConfiguration.Load: Failed for GUID={Storage.GUID}, error: {e.Message}\nStackTrace: {e.StackTrace}", isStorage: true);
      }
    }

    public void SetModeAndItem(StorageMode mode, ItemInstance item)
    {
      Mode = mode;
      StorageItem.SelectedItem = mode == StorageMode.Specific ? item?.Definition : null;
      AssignedItem = mode == StorageMode.Specific ? item : null;
      StorageItem.onItemChanged?.Invoke(StorageItem.SelectedItem);
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"SetModeAndItem: Set Mode={mode}, Item={item?.Name ?? "null"}, Packaging={(item as ProductItemInstance)?.AppliedPackaging?.Name ?? "null"} for GUID: {Storage.GUID}");
    }

    public bool IsAnyMode => Mode == StorageMode.Any;
  }

  [HarmonyPatch(typeof(PlaceableStorageEntity))]
  public class PlaceableStorageEntityPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("Awake")]
    static bool AwakePrefix(PlaceableStorageEntity __instance)
    {
      if (!__instance.gameObject.GetComponent<ConfigurationReplicator>())
      {
        __instance.gameObject.AddComponent<ConfigurationReplicator>();
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"PlaceableStorageEntityPatch.Awake: Added ConfigurationReplicator to {__instance.gameObject.name}");
      }
      return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(PlaceableStorageEntity __instance, ref string __result)
    {
      try
      {
        if (__instance == null || __instance.GUID == Guid.Empty)
        {
          MelonLogger.Error("PlaceableStorageEntityPatch.GetSaveString: Instance is null or GUID is empty");
          return;
        }
        if (!Config.TryGetValue(__instance.GUID, out var config) || config == null)
        {
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Warning($"PlaceableStorageEntityPatch.GetSaveString: No valid StorageConfiguration for GUID={__instance.GUID}");
          return;
        }

        // Parse the original result (if any) or create a new JObject
        JObject jsonObject = string.IsNullOrEmpty(__result) ? [] : JObject.Parse(__result);

        // Save StorageMode
        jsonObject["StorageMode"] = config.Mode.ToString();

        // Save StorageItem as ItemInstance JSON
        if (config.StorageItem.SelectedItem != null)
        {
          ItemInstance itemInstance = config.AssignedItem;
          jsonObject["StorageItem"] = itemInstance.GetItemData().GetJson(true);
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg($"GetSaveStringPostfix: Saved StorageItem for GUID={__instance.GUID}: {jsonObject["StorageItemInstance"]}");
        }

        __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"PlaceableStorageEntityPatch.GetSaveString: Saved JSON for GUID={__instance.GUID}: {__result}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PlaceableStorageEntityPatch.GetSaveString: Failed for GUID={__instance.GUID}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(StorageRackLoader))]
  public class StorageRackLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(StorageRackLoader __instance, string mainPath)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"StorageRackLoaderPatch: Processing for {mainPath}", isChemist: true);
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"StorageRackLoaderPatch: No GridItem found for mainPath: {mainPath}", isChemist: true);
          return;
        }
        if (gridItem is not PlaceableStorageEntity storage)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"StorageRackLoaderPatch: GridItem is not a PlaceableStorageEntity for mainPath: {mainPath}", isChemist: true);
          return;
        }
        string dataPath = Path.Combine(mainPath, "Data.json");
        if (!File.Exists(dataPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"StorageRackLoaderPatch: No Data.json found at: {dataPath}", isChemist: true);
          return;
        }
        string json = File.ReadAllText(dataPath);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"StorageRackLoaderPatch: Read JSON for GUID: {gridItem.GUID}, JSON: {json}", isChemist: true);
        JObject jsonObject = JObject.Parse(json);
        PendingConfigData[storage.GUID] = jsonObject;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StorageRackLoaderPatch: Stored pending config data for GUID: {gridItem.GUID}, StorageMode={jsonObject["StorageMode"]}, StorageItem={jsonObject["StorageItem"]}", isChemist: true);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StorageRackLoaderPatch: Failed for {mainPath}, error: {e}", isChemist: true);
      }
    }
  }

  public class StorageConfigurableProxy : NetworkBehaviour, IConfigurable
  {
    public PlaceableStorageEntity Storage { get; private set; }
    public StorageConfiguration configuration { get; private set; }
    public WorldspaceUIElement WorldspaceUI { get; set; }
    [SerializeField]
    public ConfigurationReplicator configReplicator { get; private set; }

    public static StorageConfigurableProxy AttachToStorage(PlaceableStorageEntity storage)
    {
      if (storage == null || storage.gameObject == null)
      {
        MelonLogger.Error("StorageConfigurableProxy: storage or storage.gameObject is null");
        throw new ArgumentNullException(nameof(storage));
      }

      var existingProxy = storage.gameObject.GetComponent<StorageConfigurableProxy>();
      if (existingProxy != null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfigurableProxy: Proxy already exists for {storage.gameObject.name}, GUID: {storage.GUID}");
        return existingProxy;
      }

      var proxy = storage.gameObject.AddComponent<StorageConfigurableProxy>();
      proxy.Storage = storage;
      proxy.Initialize();
      return proxy;
    }

    private void Initialize()
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: Initializing for {Storage.gameObject.name}, GUID: {Storage.GUID}");

      // Verify NetworkObject
      var networkObject = gameObject.GetComponent<NetworkObject>();
      if (networkObject == null)
      {
        MelonLogger.Error($"StorageConfigurableProxy: No NetworkObject on {Storage.gameObject.name}, GUID: {Storage.GUID}");
        throw new InvalidOperationException("NetworkObject is required");
      }

      // Attach ConfigurationReplicator
      configReplicator = gameObject.GetComponent<ConfigurationReplicator>();
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: Replicator: {configReplicator.name}, NetworkObject: {networkObject != null}, IsSpawned: {networkObject.IsSpawned}, GUID: {Storage.GUID}");

      configReplicator.NetworkInitializeIfDisabled();
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: NetworkInitializeIfDisabled for ConfigurationReplicator on {Storage.GUID}");
      StartCoroutine(InitializeConfiguration());
    }

    private IEnumerator InitializeConfiguration()
    {
      yield return null;
      try
      {
        if (configReplicator == null || Storage == null)
        {
          MelonLogger.Error($"StorageConfigurableProxy: configReplicator or storage is null for GUID: {Storage.GUID}");
          yield break;
        }
        configuration = new StorageConfiguration(configReplicator, this, Storage);
        configReplicator.Configuration = configuration;
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfigurableProxy: Configuration created for GUID: {Storage.GUID}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfigurableProxy: Failed to create StorageConfiguration for GUID: {Storage.GUID}, error: {e.Message}");
      }
      yield return StartCoroutine(LoadConfigurationWhenReady());
    }

    private IEnumerator LoadConfigurationWhenReady()
    {
      var networkObject = gameObject.GetComponent<NetworkObject>();
      if (networkObject == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"LoadConfigurationWhenReady: No NetworkObject for GUID: {Storage.GUID}", isChemist: true);
        yield break;
      }
      int retries = 10;
      while (!networkObject.IsSpawned && retries > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"LoadConfigurationWhenReady: Waiting for NetworkObject to spawn for GUID: {Storage.GUID}, retries left: {retries}", isChemist: true);
        yield return new WaitForSeconds(1f);
        retries--;
      }
      if (!networkObject.IsSpawned)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"LoadConfigurationWhenReady: NetworkObject failed to spawn after retries for GUID: {Storage.GUID}", isChemist: true);
        yield break;
      }
      ManagedObjects.InitializePrefab(networkObject, -1);
      configReplicator._transportManagerCache = NetworkManager.TransportManager;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"StorageConfigurableProxy: InitializePrefab for ConfigurationReplicator on {Storage.GUID}", isChemist: true);
      yield return new WaitForSeconds(1f);
      if (PendingConfigData.TryGetValue(Storage.GUID, out var data))
      {
        try
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"LoadConfigurationWhenReady: Applying config for GUID: {Storage.GUID}, JSON={data.ToString()}", isChemist: true);
          configuration.Load(data);
          if (WorldspaceUI != null)
            ((StorageUIElement)WorldspaceUI).RefreshUI();
          // Ensure shelf is added to lists
          UpdateShelfConfiguration(Storage, configuration.AssignedItem);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"LoadConfigurationWhenReady: Loaded configuration for GUID: {Storage.GUID}, Mode={configuration.Mode}, Item={configuration.StorageItem?.SelectedItem?.Name ?? "null"}, SpecificShelves count={ShelfCache.Count}", isChemist: true);
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"LoadConfigurationWhenReady: Failed to load configuration for GUID: {Storage.GUID}, error: {e.Message}", isChemist: true);
        }
        PendingConfigData.Remove(Storage.GUID);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"LoadConfigurationWhenReady: No pending config data for GUID: {Storage.GUID}", isChemist: true);
      }
    }

    private void OnEnable()
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: OnEnable for GUID: {Storage.GUID}");
    }

    private void OnDisable()
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: OnDisable for GUID: {Storage.GUID}");
    }

    private void OnDestroy()
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: OnDestroy for GUID: {Storage.GUID}");
    }

    // IConfigurable implementation (unchanged)
    public EntityConfiguration Configuration => configuration;
    public ConfigurationReplicator ConfigReplicator => configReplicator;
    public EConfigurableType ConfigurableType => (EConfigurableType)StorageEnum;
    public NetworkObject CurrentPlayerConfigurer { get; set; }
    public Transform Transform => Storage.transform;
    public Transform UIPoint => Storage.transform;
    public bool CanBeSelected => true;
    public Sprite TypeIcon => typeIcon;
    public bool IsBeingConfiguredByOtherPlayer => CurrentPlayerConfigurer != null && !CurrentPlayerConfigurer.IsOwner;
    public Sprite typeIcon;
    public Property ParentProperty { get => Storage.ParentProperty; set => Storage.ParentProperty = value; }
    public bool IsDestroyed => Storage.IsDestroyed;
    public void ShowOutline(Color color)
    {
      Storage.ShowOutline(color);
    }
    public void HideOutline()
    {
      Storage.HideOutline();
    }
    public override void OnSpawnServer(NetworkConnection connection)
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"OnSpawnServer: Called for GUID: {Storage.GUID}");
      Storage.OnSpawnServer(connection);
      ((IItemSlotOwner)this).SendItemsToClient(connection);
      SendConfigurationToClient(connection);
    }

    public void SendConfigurationToClient(NetworkConnection conn)
    {
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"SendConfigurationToClient: Called for GUID: {Storage.GUID}, conn: {(conn != null ? conn.ClientId : -1)}");

      if (!conn.IsHost)
      {
        StartCoroutine(WaitForConfig());
      }

      IEnumerator WaitForConfig()
      {
        yield return new WaitUntil(() => configuration != null);
        configuration.ReplicateAllFields(conn);
      }
    }

    public WorldspaceUIElement CreateWorldspaceUI()
    {
      if (ParentProperty == null)
      {
        MelonLogger.Error($"CreateWorldspaceUI: ParentProperty is null for GUID: {Storage.GUID}");
        return null;
      }
      GameObject uiPrefab = GetPrefabGameObject("PackagingStationUI");
      if (uiPrefab == null)
      {
        MelonLogger.Error($"CreateWorldspaceUI: Failed to load PackagingStationUI prefab for GUID: {Storage.GUID}");
        return null;
      }
      var uiInstance = Instantiate(uiPrefab, ParentProperty.WorldspaceUIContainer);
      if (uiInstance == null)
      {
        MelonLogger.Error($"CreateWorldspaceUI: uiInstance is null for GUID: {Storage.GUID}");
        return null;
      }
      uiInstance.name = "StorageUI";
      var component = uiInstance.AddComponent<StorageUIElement>();
      var iconImg = uiInstance.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.gameObject.name.Contains("Icon"));
      iconImg.sprite = null;
      var old = uiInstance.GetComponent<PackagingStationUIElement>();
      if (old != null)
      {
        var oldRectTransform = old.GetComponent<RectTransform>();
        var rectTransform = component.GetComponent<RectTransform>();
        rectTransform.anchoredPosition = oldRectTransform.anchoredPosition;
        rectTransform.anchorMax = oldRectTransform.anchorMax;
        rectTransform.anchorMin = oldRectTransform.anchorMin;
        rectTransform.offsetMax = oldRectTransform.offsetMax;
        rectTransform.offsetMin = oldRectTransform.offsetMin;
        rectTransform.position = oldRectTransform.position;
        Destroy(old);
      }
      component.Initialize(this);
      WorldspaceUI = component;
      return component;
    }

    public void DestroyWorldspaceUI()
    {
      if (WorldspaceUI != null)
      {
        WorldspaceUI.Destroy();
        WorldspaceUI = null;
      }
    }

    public void Selected() => Configuration?.Selected();
    public void Deselected() => Configuration?.Deselected();
    public void SetConfigurer(NetworkObject player) => CurrentPlayerConfigurer = player;
  }

  public class StorageUIElement : WorldspaceUIElement
  {
    public StorageConfigurableProxy Proxy { get; set; }
    public Image Icon { get; set; }

    public void Initialize(StorageConfigurableProxy proxy)
    {
      RectTransform = (RectTransform)transform;
      RectTransform containerRectTransform = (RectTransform)transform.Find("Outline");
      if (containerRectTransform != null)
        Container = containerRectTransform;
      else if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Warning($"StorageUIElement.Initialize: Container (Outline) not found for GUID: {proxy.Storage.GUID}");

      var titleLabel = GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name.Contains("Title"));
      if (titleLabel != null)
        titleLabel.text = proxy.Storage.Name;
      else if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Warning($"StorageUIElement.Initialize: No Title TextMeshProUGUI found for GUID: {proxy.Storage.GUID}");

      var iconImg = GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.gameObject.name.Contains("Icon"));
      if (iconImg != null)
        Icon = iconImg;
      else if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Warning($"StorageUIElement.Initialize: No Icon Image found for GUID: {proxy.Storage.GUID}");
      if (proxy.configuration.Mode == default)
      {
        proxy.configuration.Mode = StorageMode.None;
        Icon.sprite = GetCrossSprite();
        Icon.color = Color.red;
      }
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Warning($"StorageUIElement.Initialize: Sprite: {Icon.sprite?.name}");
      Proxy = proxy;
      Proxy.Configuration.onChanged.AddListener(RefreshUI);
      RefreshUI();
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageUIElement.Initialize: Completed for GUID: {proxy.Storage.GUID}");
    }

    public void RefreshUI()
    {
      UpdateWorldspaceUIIcon();
    }

    public void UpdateWorldspaceUIIcon()
    {
      if (Proxy.WorldspaceUI == null || Icon == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning($"StorageUIElement.UpdateWorldspaceUIIcon: WorldspaceUI or Icon is null for GUID: {Proxy.Storage.GUID}");
        return;
      }

      var config = Proxy.configuration;
      Sprite newSprite = null;
      Color iconColor = Color.white;

      if (config == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning($"StorageUIElement.UpdateWorldspaceUIIcon: Configuration is null for GUID: {Proxy.Storage.GUID}");
      }
      else if (config.Mode == StorageMode.None)
      {
        newSprite = GetCrossSprite();
        iconColor = Color.red;
      }
      else if (config.Mode == StorageMode.Any)
      {
        newSprite = null;
        iconColor = Color.white;
      }
      else if (config.Mode == StorageMode.Specific)
      {
        if (config.AssignedItem != null)
          newSprite = GetPackagedSprite(config.AssignedItem.definition, (config.AssignedItem as ProductItemInstance)?.AppliedPackaging);
        else
          newSprite = config.StorageItem?.SelectedItem?.Icon;
        iconColor = Color.white;
      }

      Icon.sprite = newSprite;
      Icon.color = iconColor;
      Icon.enabled = newSprite != null;

      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageUIElement.UpdateWorldspaceUIIcon: Mode: {config?.Mode}, Sprite: {(newSprite != null ? newSprite.name : "null")}, Color: {iconColor}, Packaging: {(config?.AssignedItem as ProductItemInstance)?.AppliedPackaging?.Name ?? "null"} for GUID: {Proxy.Storage.GUID}");
    }
  }

  [HarmonyPatch(typeof(GridItem))]
  public class GridItemPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("InitializeGridItem", new Type[] { typeof(ItemInstance), typeof(Grid), typeof(Vector2), typeof(int), typeof(string) })]
    static bool InitializeGridItemPrefix(GridItem __instance, ItemInstance instance, Grid grid, Vector2 originCoordinate, int rotation, string GUID)
    {
      try
      {
        if (!__instance.isGhost && GUID != "00000000-0000-0000-0000-000000000000" && __instance.GUID.ToString() != "00000000-0000-0000-0000-000000000000" && InstanceIDs.Contains(instance.ID))
        {
          if (__instance is PlaceableStorageEntity entity)
          {
            if (DebugLogs.All || DebugLogs.Storage)
              MelonLogger.Msg($"GridItemPatch: Initializing storage for GUID: {__instance.GUID}");
            InitializeStorage(__instance.GUID, entity);
          }
          else
          {
            MelonLogger.Warning($"GridItemPatch: {__instance.GUID} is not a PlaceableStorageEntity");
          }
        }
        return true;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"GridItemPatch: Failed for GUID: {GUID}, error: {e.Message}");
        return true;
      }
    }
  }

  [HarmonyPatch(typeof(ConfigurableType))]
  public class ConfigurableTypePatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetTypeName")]
    static bool GetTypeNamePrefix(EConfigurableType type, ref string __result)
    {
      if (type == (EConfigurableType)StorageEnum)
      {
        __result = "Storage Rack";
        return false;
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(ManagementInterface))]
  public class ManagementInterfacePatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetConfigPanelPrefab")]
    static bool GetConfigPanelPrefabPrefix(ManagementInterface __instance, EConfigurableType type, ref ConfigPanel __result)
    {
      if (type == (EConfigurableType)StorageEnum)
      {
        __result = GetStorageConfigPanelTemplate();
        return false;
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(ItemSelector))]
  public class ItemSelectorPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("CreateOptions")]
    static void CreateOptionsPostfix(ItemSelector __instance, List<ItemSelector.Option> options)
    {
      for (int i = 0; i < __instance.optionButtons.Count; i++)
      {
        var button = __instance.optionButtons[i];
        var btnComponent = button.GetComponent<UnityEngine.UI.Button>();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ItemSelectorPatch.CreateOptionsPostfix: Button {i}, Title={options[i].Title}, interactable={btnComponent?.interactable}, active={button.gameObject.activeSelf}",
            isStorage: true);
        var option = options[i];
        if (option is StorageItemOption storageOption && storageOption.PackagingDefinition != null && storageOption.Item != null)
        {
          var icon = button.transform.Find("Icon").gameObject.GetComponent<Image>();
          icon.sprite = GetPackagedSprite(storageOption.Item, storageOption.PackagingDefinition);
          icon.gameObject.SetActive(icon.sprite != null);
        }
      }
    }
  }
}