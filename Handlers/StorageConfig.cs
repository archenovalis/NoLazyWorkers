using FishNet.Connection;
using FishNet.Object;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
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
using ScheduleOne;
using static NoLazyWorkers.NoLazyUtilities;
using ScheduleOne.Product;
using static NoLazyWorkers.Handlers.StorageExtensions;
using FishNet.Managing;
using FishNet.Managing.Object;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Persistence;

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

    public static IEnumerator WaitForProxyInitialization(Guid guid, PlaceableStorageEntity storage, StorageConfigurableProxy proxy)
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

            // Start the delayed check coroutine after successful initialization
            if (DebugLogs.All || DebugLogs.Storage)
              MelonLogger.Msg($"WaitForProxyInitialization: Starting 10-second delayed check for GUID: {guid}");
            //bool initialized = false;
            //CoroutineRunner.Instance.RunCoroutineWithResult<bool>(DelayedInitializationCheck(guid, storage, proxy), success => initialized = success);

            yield return true;
            yield break;
          }
        }
        yield return new WaitForSeconds(delay);
      }
      MelonLogger.Error($"WaitForProxyInitialization: Failed for GUID: {guid} after {retries} retries");
      yield return false;
    }

    /* private static IEnumerator DelayedInitializationCheck(Guid guid, PlaceableStorageEntity storage, StorageConfigurableProxy proxy)
    {
      yield return new WaitForSeconds(10f);

      try
      {
        bool isProxyValid = proxy != null;
        bool isConfigurationValid = proxy != null && proxy.configuration != null;
        bool isParentPropertyValid = storage != null && storage.ParentProperty != null;
        bool isWorldspaceUIValid = proxy != null && proxy.WorldspaceUI != null;
        bool isReplicatorValid = proxy != null && proxy.configReplicator != null;
        bool isNetworkObjectValid = proxy != null && proxy.configReplicator != null && proxy.configReplicator.NetworkObject != null;
        bool isReplicatorSpawned = proxy != null && proxy.configReplicator != null && proxy.configReplicator.NetworkObject != null && proxy.configReplicator.NetworkObject.IsSpawned;
        bool isStorageInDictionary = Storage.ContainsKey(guid);
        bool isConfigInDictionary = Config.ContainsKey(guid);

        MelonLogger.Msg($"DelayedInitializationCheck: Status after 10 seconds for GUID: {guid}");
        MelonLogger.Msg($"  - Proxy exists: {isProxyValid}");
        MelonLogger.Msg($"  - Configuration exists: {isConfigurationValid}");
        MelonLogger.Msg($"  - ParentProperty exists: {isParentPropertyValid}");
        MelonLogger.Msg($"  - WorldspaceUI exists: {isWorldspaceUIValid}");
        MelonLogger.Msg($"  - ConfigurationReplicator exists: {isReplicatorValid}");
        MelonLogger.Msg($"  - NetworkObject exists: {isNetworkObjectValid}");
        MelonLogger.Msg($"  - ConfigurationReplicator spawned: {isReplicatorSpawned}");
        MelonLogger.Msg($"  - Storage in dictionary: {isStorageInDictionary}");
        MelonLogger.Msg($"  - Config in dictionary: {isConfigInDictionary}");

        if (isProxyValid)
          MelonLogger.Msg($"  - Proxy GameObject active: {proxy.gameObject.activeInHierarchy}, Name: {proxy.gameObject.name}");
        if (isConfigurationValid)
          MelonLogger.Msg($"  - Configuration Mode: {proxy.configuration.Mode}, Item: {proxy.configuration.StorageItem?.SelectedItem?.Name ?? "null"}");
        if (isParentPropertyValid)
          MelonLogger.Msg($"  - ParentProperty Name: {storage.ParentProperty?.GetType()?.Name ?? "null"}");
        if (isWorldspaceUIValid)
          MelonLogger.Msg($"  - WorldspaceUI Type: {proxy.WorldspaceUI?.GetType()?.Name}, Active: {proxy.WorldspaceUI?.gameObject?.activeInHierarchy}");
        if (isNetworkObjectValid)
          MelonLogger.Msg($"  - Replicator NetworkObject ID: {proxy.configReplicator?.NetworkObject?.ObjectId}, IsSpawned: {proxy.configReplicator?.NetworkObject?.IsSpawned}");

        if (PendingConfigData.TryGetValue(guid, out var data))
        {
          Config[guid]?.Load(data);
          PendingConfigData.Remove(guid);
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"DelayedInitializationCheck: Failed for GUID: {guid}, error: {e.Message}\nStackTrace: {e.StackTrace}");
      }
    } */

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
    public static PlaceableStorageEntity FindShelfWithItem(NPC npc, ItemInstance targetItem, int requiredQuantity, bool prioritizeAny = false)
    {
      if (targetItem == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("FindShelfWithItem: Target item is null");
        return null;
      }

      // Get all storage entities
      var shelves = Storage.Values.ToList();
      if (!shelves.Any())
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg("FindShelfWithItem: No shelves available");
        return null;
      }

      PlaceableStorageEntity selectedShelf = null;
      int availableQuantity = 0;

      if (prioritizeAny)  // "any" then item-assigned shelves
      {
        // First, check "any" shelves (no assigned item)
        foreach (var shelf in shelves)
        {
          var config = Config.TryGetValue(shelf.GUID, out var cfg) ? cfg : null;
          if (config == null || config.StorageItem?.SelectedItem != null)
            continue;

          if (!npc.behaviour.Npc.Movement.CanGetTo(shelf, 1f))
            continue;

          int quantity = GetItemQuantityInShelf(shelf, targetItem);
          if (quantity >= requiredQuantity)
          {
            selectedShelf = shelf;
            availableQuantity = quantity;
            break;
          }
        }
        if (selectedShelf == null)
        {
          foreach (var shelf in shelves)
          {
            var config = Config.TryGetValue(shelf.GUID, out var cfg) ? cfg : null;
            if (config == null || config.StorageItem?.SelectedItem == null || config.StorageItem.SelectedItem.ID != targetItem.ID)
              continue;

            if (!npc.behaviour.Npc.Movement.CanGetTo(shelf, 1f))
              continue;

            int quantity = GetItemQuantityInShelf(shelf, targetItem);
            if (quantity >= requiredQuantity)
            {
              selectedShelf = shelf;
              availableQuantity = quantity;
              break;
            }
          }
        }
      }
      else // item-assigned then "any" shelves
      {
        foreach (var shelf in shelves)
        {
          var config = Config.TryGetValue(shelf.GUID, out var cfg) ? cfg : null;
          if (config == null || config.StorageItem?.SelectedItem == null || config.StorageItem.SelectedItem.ID != targetItem.ID)
            continue;

          if (!npc.behaviour.Npc.Movement.CanGetTo(shelf, 1f))
            continue;

          int quantity = GetItemQuantityInShelf(shelf, targetItem);
          if (quantity >= requiredQuantity)
          {
            selectedShelf = shelf;
            availableQuantity = quantity;
            break;
          }
        }
        if (selectedShelf == null)
        {
          foreach (var shelf in shelves)
          {
            var config = Config.TryGetValue(shelf.GUID, out var cfg) ? cfg : null;
            if (config == null || config.StorageItem?.SelectedItem != null)
              continue;

            if (!npc.behaviour.Npc.Movement.CanGetTo(shelf, 1f))
              continue;

            int quantity = GetItemQuantityInShelf(shelf, targetItem);
            if (quantity >= requiredQuantity)
            {
              selectedShelf = shelf;
              availableQuantity = quantity;
              break;
            }
          }
        }
      }

      if (selectedShelf != null && (DebugLogs.All || DebugLogs.Storage))
        MelonLogger.Msg($"FindShelfWithItem: Selected shelf {selectedShelf.GUID} with {availableQuantity}/{requiredQuantity} of {targetItem.ID}");

      return selectedShelf;
    }

    // Find a shelf to deliver an item to, prioritizing matching item-assigned shelves, then any shelves
    public static PlaceableStorageEntity FindShelfForDelivery(NPC npc, ItemInstance targetItem)
    {
      if (targetItem == null)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Warning("FindShelfForDelivery: Target item is null");
        return null;
      }

      var shelves = Storage.Values.ToList();
      if (!shelves.Any())
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg("FindShelfForDelivery: No shelves available");
        return null;
      }

      PlaceableStorageEntity selectedShelf = null;
      int quantityToDeliver = targetItem.Quantity;

      // First, check item-assigned shelves
      foreach (var shelf in shelves)
      {
        var config = Config.TryGetValue(shelf.GUID, out var cfg) ? cfg : null;
        if (config == null || config.StorageItem?.SelectedItem == null || config.StorageItem.SelectedItem.ID != targetItem.ID)
          continue;

        if (!npc.behaviour.Npc.Movement.CanGetTo(shelf, 1f))
          continue;

        if (shelf.StorageEntity.CanItemFit(targetItem, quantityToDeliver))
        {
          selectedShelf = shelf;
          break;
        }
      }

      // If no matching shelf or full, check "any" shelves
      if (selectedShelf == null)
      {
        foreach (var shelf in shelves)
        {
          var config = Config.TryGetValue(shelf.GUID, out var cfg) ? cfg : null;
          if (config == null || config.StorageItem?.SelectedItem != null)
            continue;

          if (!npc.behaviour.Npc.Movement.CanGetTo(shelf, 1f))
            continue;

          if (shelf.StorageEntity.CanItemFit(targetItem, quantityToDeliver))
          {
            selectedShelf = shelf;
            break;
          }
        }
      }

      if (selectedShelf != null && (DebugLogs.All || DebugLogs.Storage))
        MelonLogger.Msg($"FindShelfForDelivery: Selected shelf {selectedShelf.GUID} for {quantityToDeliver} of {targetItem.ID}");

      return selectedShelf;
    }
    private static int GetItemQuantityInShelf(PlaceableStorageEntity shelf, ItemInstance item)
    {
      if (shelf?.OutputSlots == null)
        return 0;

      return shelf.OutputSlots
          .Where(s => s?.ItemInstance != null && s.ItemInstance.ID.ToLower() == item.ID.ToLower())
          .Sum(s => s.Quantity);
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
      ConfigurationExtensions.InvokeChanged(config);
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
        ItemDefinition sharedItem = null;
        ItemDefinition sharedPackaging = null;
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
            if (sharedItem == null)
            {
              sharedItem = config.StorageItem.SelectedItem;
              sharedPackaging = config.PackagingDefinition;
            }
            else if (sharedItem != config.StorageItem.SelectedItem || sharedPackaging != config.PackagingDefinition)
              isConsistent = false;
          }
        }
        StorageItemUI.Bind(itemFieldList);

        if (hasNone && !hasAny && !hasSpecific)
          StorageItemUI.SelectionLabel.text = "None";
        else if (hasAny && !hasNone && !hasSpecific)
          StorageItemUI.SelectionLabel.text = "Any";
        else if (hasSpecific && !hasNone && !hasAny && isConsistent)
          StorageItemUI.SelectionLabel.text = sharedPackaging != null ? $"{sharedItem?.Name} ({sharedPackaging.Name})" : sharedItem?.Name ?? "None";
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
            primaryConfig.PackagingDefinition == packaging)
          selectedOption = opt;
      }

      try
      {
        Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, list, selectedOption, new Action<ItemSelector.Option>(selected =>
        {
          StorageMode newMode;
          ItemDefinition selectedItem = selected.Item;
          PackagingDefinition selectedPackaging = (selected is StorageItemOption storageOption ? storageOption.PackagingDefinition : null) as PackagingDefinition;

          if (selected.Title == "None")
            newMode = StorageMode.None;
          else if (selected.Title == "Any")
            newMode = StorageMode.Any;
          else
            newMode = StorageMode.Specific;

          foreach (var itemField in itemFields)
          {
            var config = (StorageConfiguration)itemField.ParentConfig;
            config.SetModeAndItem(newMode, selectedItem, selectedPackaging);
          }

          StorageItemUI.SelectionLabel.text = newMode == StorageMode.Any ? "Any" :
                                                       newMode == StorageMode.None ? "None" :
                                                       selectedPackaging != null ? $"{selectedItem?.Name} ({selectedPackaging.Name})" :
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
    public PackagingDefinition PackagingDefinition { get; set; }

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
      try
      {
        StorageItem = new ItemField(this)
        {
          CanSelectNone = true,
          Options = []
        };
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
        if (jsonObject["StorageMode"] != null)
          Mode = (StorageMode)Enum.Parse(typeof(StorageMode), jsonObject["StorageMode"].ToString());

        if (jsonObject["StorageItem"] != null)
        {
          string itemInstanceJson = jsonObject["StorageItem"].ToString();
          ItemInstance itemInstance = ItemDeserializer.LoadItem(itemInstanceJson);
          if (itemInstance != null)
          {
            StorageItem.SelectedItem = itemInstance.Definition;
            if (itemInstance is ProductItemInstance productInstance && productInstance.AppliedPackaging != null)
            {
              PackagingDefinition = productInstance.AppliedPackaging;
              if (DebugLogs.All || DebugLogs.Storage)
                MelonLogger.Msg($"StorageConfiguration.Load: Loaded ProductItemInstance with Item={StorageItem.SelectedItem?.Name}, Packaging={PackagingDefinition?.Name} for GUID: {Storage.GUID}");
            }
            else
            {
              PackagingDefinition = null;
              if (DebugLogs.All || DebugLogs.Storage)
                MelonLogger.Msg($"StorageConfiguration.Load: Loaded ItemInstance with Item={StorageItem.SelectedItem?.Name} for GUID: {Storage.GUID}");
            }
          }
          else
          {
            MelonLogger.Warning($"StorageConfiguration.Load: Failed to deserialize StorageItemInstance for GUID: {Storage.GUID}, JSON: {itemInstanceJson}");
          }
        }

        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfiguration.Load: Loaded Mode={Mode}, Item={StorageItem.SelectedItem?.Name ?? "null"}, Packaging={PackagingDefinition?.Name ?? "null"} for GUID: {Storage.GUID}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfiguration.Load: Failed for GUID={Storage.GUID}, error: {e.Message}\nStackTrace: {e.StackTrace}");
      }
    }

    public void SetModeAndItem(StorageMode mode, ItemDefinition item, PackagingDefinition packaging)
    {
      Mode = mode;
      StorageItem.SelectedItem = mode == StorageMode.Specific ? item : null;
      PackagingDefinition = mode == StorageMode.Specific ? packaging : null;
      if (mode == StorageMode.Specific && item != null && packaging != null)
      {
        if (!(item is ProductDefinition))
          MelonLogger.Warning($"SetModeAndItem: Item {item.Name} is not a ProductDefinition but has packaging {packaging.Name} for GUID: {Storage.GUID}");
      }
      StorageItem.onItemChanged?.Invoke(StorageItem.SelectedItem);
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
          ItemInstance itemInstance = config.StorageItem.SelectedItem.GetDefaultInstance();
          if (config.PackagingDefinition != null)
            (itemInstance as ProductItemInstance).SetPackaging(config.PackagingDefinition);
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
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageRackLoaderPatch: Processing for {mainPath}");

        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Warning($"StorageRackLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem is not PlaceableStorageEntity storage)
        {
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Warning($"StorageRackLoaderPatch: GridItem is not a PlaceableStorageEntity for mainPath: {mainPath}");
          return;
        }
        string dataPath = Path.Combine(mainPath, "Data.json");
        if (!File.Exists(dataPath))
        {
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Warning($"StorageRackLoaderPatch: No Data.json found at: {dataPath}");
          return;
        }

        string json = File.ReadAllText(dataPath);
        JObject jsonObject = JObject.Parse(json);
        if (jsonObject["StorageMode"] != null)
          PendingConfigData[storage.GUID] = jsonObject;

        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageRackLoaderPatch: Stored pending config data for GUID: {gridItem.GUID}, JSON: {json}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageRackLoaderPatch: Failed for {mainPath}, error: {e}");
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
        MelonLogger.Error($"LoadConfigurationWhenReady: No NetworkObject for GUID: {Storage.GUID}");
        yield break;
      }
      int retries = 10;
      while (!networkObject.IsSpawned && retries > 0)
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"LoadConfigurationWhenReady: Waiting for NetworkObject to spawn for GUID: {Storage.GUID}, retries left: {retries}");
        yield return new WaitForSeconds(1f);
        retries--;
      }
      if (!networkObject.IsSpawned)
      {
        MelonLogger.Error($"LoadConfigurationWhenReady: NetworkObject failed to spawn after retries for GUID: {Storage.GUID}");
        yield break;
      }
      ManagedObjects.InitializePrefab(networkObject, -1);
      configReplicator._transportManagerCache = NetworkManager.TransportManager;
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfigurableProxy: InitializePrefab for ConfigurationReplicator on {Storage.GUID}");
      yield return new WaitForSeconds(1f);

      if (PendingConfigData.TryGetValue(Storage.GUID, out var data))
      {
        try
        {
          configuration.Load(data);
          if (WorldspaceUI != null)
            ((StorageUIElement)WorldspaceUI).RefreshUI();
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg($"LoadConfigurationWhenReady: Loaded configuration for GUID: {Storage.GUID}");
        }
        catch (Exception e)
        {
          MelonLogger.Error($"LoadConfigurationWhenReady: Failed to load configuration for GUID: {Storage.GUID}, error: {e.Message}");
        }
        PendingConfigData.Remove(Storage.GUID);
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
        if (config.PackagingDefinition != null && config.StorageItem?.SelectedItem != null)
          newSprite = GetPackagedSprite(config.StorageItem.SelectedItem, config.PackagingDefinition);
        else
          newSprite = config.StorageItem?.SelectedItem?.Icon;
        iconColor = Color.white;
      }

      Icon.sprite = newSprite;
      Icon.color = iconColor;
      Icon.enabled = newSprite != null;

      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageUIElement.UpdateWorldspaceUIIcon: Mode: {config?.Mode}, Sprite: {(newSprite != null ? newSprite.name : "null")}, Color: {iconColor}, Packaging: {config?.PackagingDefinition?.Name ?? "null"} for GUID: {Proxy.Storage.GUID}");
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
        var option = options[i];
        if (option is StorageItemOption storageOption && storageOption.PackagingDefinition != null && storageOption.Item != null)
        {
          var button = __instance.optionButtons[i];
          var icon = button.transform.Find("Icon").gameObject.GetComponent<Image>();
          icon.sprite = GetPackagedSprite(storageOption.Item, storageOption.PackagingDefinition);
          icon.gameObject.SetActive(icon.sprite != null);
        }
      }
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
}