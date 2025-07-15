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
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.Storage.ManagedDictionaries;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.ShelfConstants;
using static NoLazyWorkers.Storage.ShelfExtensions;
using static NoLazyWorkers.Storage.ShelfUtilities;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Stations.Extensions;
using FishNet.Managing;
using FishNet.Managing.Object;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Persistence;
using NoLazyWorkers.Employees;
using ScheduleOne.Employees;
using System.Collections.Concurrent;
using Unity.Collections;
using GameKit.Utilities;
using FishNet;
using System.Diagnostics;
using NoLazyWorkers.TaskService;

namespace NoLazyWorkers.Storage
{
  public static class ShelfExtensions
  {
    public static ConfigPanel ConfigPanelTemplate { get; set; }
    public static Sprite CrossSprite { get; set; }
    public enum StorageMode
    {
      None,    // No items allowed (red cross)
      Any,     // Any item allowed (blank)
      Specific // Specific item allowed (item's icon or packaged icon)
    }

    public class StorageItemOption : ItemSelector.Option
    {
      public ItemDefinition PackagingDefinition { get; }

      public StorageItemOption(string title, ItemDefinition item, ItemDefinition packagingDefinition)
          : base(title, item)
      {
        PackagingDefinition = packagingDefinition;
      }
    }
    public struct ShelfInfo
    {
      public int Quantity { get; set; }
      public bool IsConfigured { get; set; }

      public ShelfInfo(int quantity, bool isConfigured)
      {
        Quantity = quantity;
        IsConfigured = isConfigured;
      }
    }
  }
  public static class ShelfConstants
  {
    public const int StorageEnum = 345543;
    public static class StorageInstanceIDs
    {
      public const string SmallStorageRack = "smallstoragerack";
      public const string MediumStorageRack = "mediumstoragerack";
      public const string LargeStorageRack = "largestoragerack";
      public const string Safe = "safe";
      public static readonly string[] All = [SmallStorageRack, MediumStorageRack, LargeStorageRack, Safe];
    };
  }

  public static class ShelfUtilities
  {
    public static void InitializeStorageModule()
    {
      Log(Level.Info, "InitializeStorageModule: Starting", Category.Storage);
      GetStorageConfigPanelTemplate();
      CacheCrossSprite();
      Log(Level.Info, $"InitializeStorageModule: ConfigPanelTemplate {(ConfigPanelTemplate != null ? "initialized" : "null")}", Category.Storage);
    }

    public static bool InitializeStorage(Guid guid, PlaceableStorageEntity storage)
    {
      try
      {
        Log(Level.Info, $"InitializeStorage: Called for GUID: {guid}, storage: {(storage != null ? storage.name : "null")}", Category.Storage);

        if (storage == null || guid == Guid.Empty)
        {
          Log(Level.Error, $"InitializeStorage: storage is null or guid is empty ({guid})", Category.Storage);
          return false;
        }

        if (!Storages[storage.ParentProperty].ContainsKey(guid))
        {
          Storages[storage.ParentProperty][guid] = storage;
          Log(Level.Info, $"InitializeStorage: Added storage to dictionary for GUID: {guid}, Storage count: {Storages.Count}", Category.Storage);
        }

        var proxy = StorageConfigurableProxy.AttachToStorage(storage);
        if (proxy == null)
        {
          Log(Level.Error, $"InitializeStorage: Failed to attach proxy for GUID: {guid}", Category.Storage);
          return false;
        }

        bool initialized = false;
        CoroutineRunner.Instance.RunCoroutineWithResult<bool>(WaitForProxyInitialization(guid, storage, proxy), success => initialized = success);

        return initialized;
      }
      catch (Exception e)
      {
        Log(Level.Error, $"InitializeStorage: Failed for GUID: {guid}, error: {e.Message}", Category.Storage);
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

            Log(Level.Info, $"WaitForProxyInitialization: Completed for GUID: {guid}", Category.Storage);
            yield return true;
            yield break;
          }
        }
        yield return new WaitForSeconds(delay);
      }
      Log(Level.Error, $"WaitForProxyInitialization: Failed for GUID: {guid} after {retries} retries", Category.Storage);
      yield return false;
    }

    public static void CacheCrossSprite()
    {
      CrossSprite = GetCrossSprite();
      if (CrossSprite == null)
        Log(Level.Warning, "CacheCrossSprite: Failed to load cross sprite", Category.Storage);
      else
        Log(Level.Info, "CacheCrossSprite: Successfully cached cross sprite", Category.Storage);
    }

    public static Sprite GetCrossSprite()
    {
      if (CrossSprite != null)
        return CrossSprite;

      var prefab = GetPrefabGameObject("Supplier Stash");
      if (prefab == null)
      {
        Log(Level.Warning, "GetCrossSprite: Supplier Stash prefab not found", Category.Storage);
        return null;
      }
      else
      {
        Log(Level.Stacktrace, $"GetCrossSprite: Prefab hierarchy:\n{DebugTransformHierarchy(prefab.transform)}", Category.Storage);
      }

      var iconObj = prefab.transform.Find("Dead drop hopper/Icon");
      if (iconObj == null)
      {
        Log(Level.Warning, "GetCrossSprite: Icon GameObject not found in Supplier Stash prefab", Category.Storage);
        return null;
      }

      var renderer = iconObj.GetComponent<MeshRenderer>();
      if (renderer == null || renderer.material == null)
      {
        Log(Level.Warning, "GetCrossSprite: No MeshRenderer or material found on Icon GameObject", Category.Storage);
        return null;
      }

      var texture = renderer.material.mainTexture as Texture2D;
      if (texture == null)
      {
        Log(Level.Warning, "GetCrossSprite: No Texture2D found in material", Category.Storage);
        return null;
      }
      // Create Sprite from Texture2D
      var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
      if (sprite != null)
      {
        sprite.name = "Cross";
        Log(Level.Info, $"GetCrossSprite: Created sprite '{sprite.name}' from Texture2D '{texture.name}'", Category.Storage);
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
        Log(Level.Warning, "GetPackagedSprite: ProductIconManager instance is null", Category.Storage);
        return product?.Icon;
      }

      var sprite = productIconManager.GetIcon(product.ID, packaging.ID);
      if (sprite == null)
      {
        Log(Level.Warning, "GetPackagedSprite: Sprite is null", Category.Storage);
        return product?.Icon;
      }
      sprite.name = $"{product.Name} ({packaging.Name})";
      Log(Level.Info, $"GetPackagedSprite: Created sprite '{sprite.name}' for product {product.ID}, packaging {packaging.ID}", Category.Storage);
      return sprite;
    }

    public static ConfigPanel GetStorageConfigPanelTemplate()
    {
      try
      {
        Log(Level.Info,
            "GetStorageConfigPanelTemplate: Starting",
            Category.Storage);

        if (ConfigPanelTemplate != null)
        {
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Returning cached template",
              Category.Storage);
          return ConfigPanelTemplate;
        }

        // Get template from PotConfigPanel
        Transform storageTransform = GetTransformTemplateFromConfigPanel(EConfigurableType.Pot, "");
        if (storageTransform == null)
        {
          Log(Level.Error,
              "GetStorageConfigPanelTemplate: storageTransform is null",
              Category.Storage);
          return null;
        }

        GameObject storageObj = Object.Instantiate(storageTransform.gameObject);
        storageObj.name = "StorageConfigPanel";
        if (storageObj.GetComponent<CanvasRenderer>() == null)
        {
          storageObj.AddComponent<CanvasRenderer>();
        }
        Log(Level.Info,
            "GetStorageConfigPanelTemplate: Instantiated StorageConfigPanel",
            Category.Storage);

        // Remove default script
        var defaultScript = storageObj.GetComponent<PotConfigPanel>();
        if (defaultScript != null)
        {
          Object.Destroy(defaultScript);
        }

        // Add StorageConfigPanel
        StorageConfigPanel configPanel = storageObj.GetComponent<StorageConfigPanel>();
        if (configPanel == null)
        {
          configPanel = storageObj.AddComponent<StorageConfigPanel>();
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Added StorageConfigPanel",
              Category.Storage);
        }

        // Remove unnecessary components
        foreach (string partName in new[] { "Additive2", "Additive3", "Botanist", "ObjectFieldUI", "SupplyUI", "Seed" })
        {
          var part = storageObj.transform.Find(partName);
          if (part != null)
          {
            Object.Destroy(part.gameObject);
          }
        }

        // Setup StorageItemUI
        Transform itemFieldUITransform = storageObj.transform.Find("Additive1");
        if (itemFieldUITransform == null)
        {
          // Fallback: Instantiate from PotConfigPanel prefab
          var potPanel = GetPrefabGameObject("PotConfigPanel");
          if (potPanel == null)
          {
            Log(Level.Error,
                "GetStorageConfigPanelTemplate: PotConfigPanel prefab not found",
                Category.Storage);
            return null;
          }
          var templateUI = potPanel.GetComponentInChildren<ItemFieldUI>();
          if (templateUI == null)
          {
            Log(Level.Error,
                "GetStorageConfigPanelTemplate: ItemFieldUI not found in PotConfigPanel",
                Category.Storage);
            return null;
          }
          itemFieldUITransform = Object.Instantiate(templateUI.gameObject, storageObj.transform, false).transform;
          itemFieldUITransform.name = "StorageItemUI";
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Instantiated StorageItemUI from prefab",
              Category.Storage);
        }

        GameObject itemFieldUIObj = itemFieldUITransform.gameObject;
        itemFieldUIObj.SetActive(true);
        var itemFieldUI = itemFieldUIObj.GetComponent<ItemFieldUI>();
        if (itemFieldUI == null)
        {
          itemFieldUI = itemFieldUIObj.AddComponent<ItemFieldUI>();
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Added ItemFieldUI to StorageItemUI",
              Category.Storage);
        }
        if (itemFieldUIObj.GetComponent<CanvasRenderer>() == null)
        {
          itemFieldUIObj.AddComponent<CanvasRenderer>();
        }

        configPanel.StorageItemUI = itemFieldUI;
        if (configPanel.StorageItemUI == null)
        {
          Log(Level.Error,
              "GetStorageConfigPanelTemplate: Failed to assign StorageItemUI",
              Category.Storage);
          return null;
        }

        // Setup Title and Description
        TextMeshProUGUI titleText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>()
            .FirstOrDefault(t => t.gameObject.name == "Title");
        if (titleText != null)
        {
          titleText.text = "Assign Item";
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Set Title to 'Assign Item'",
              Category.Storage);
        }
        TextMeshProUGUI descText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>()
            .FirstOrDefault(t => t.gameObject.name == "Description");
        if (descText != null)
        {
          descText.text = "Select the item to assign to this shelf";
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Set Description",
              Category.Storage);
        }

        // Setup QualityFieldUI
        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        if (dryingRackPanelObj == null)
        {
          Log(Level.Error,
              "GetStorageConfigPanelTemplate: DryingRackPanel prefab not found",
              Category.Storage);
          return null;
        }
        var qualityFieldUIObj = dryingRackPanelObj.GetComponentInChildren<QualityFieldUI>();
        if (qualityFieldUIObj == null)
        {
          Log(Level.Error,
              "GetStorageConfigPanelTemplate: QualityFieldUI not found in DryingRackPanel",
              Category.Storage);
          return null;
        }
        var qualityUIObj = Object.Instantiate(qualityFieldUIObj.gameObject, storageObj.transform, false);
        qualityUIObj.name = "QualityFieldUI";
        qualityUIObj.transform.Find("Description").gameObject.SetActive(false);
        qualityUIObj.transform.Find("Title").GetComponent<TextMeshProUGUI>().text = "Quality";
        qualityUIObj.SetActive(false);

        var qualityRect = qualityUIObj.GetComponent<RectTransform>();
        var itemFieldRect = itemFieldUIObj.GetComponent<RectTransform>();
        if (qualityRect != null && itemFieldRect != null)
        {
          qualityRect.anchoredPosition = new Vector2(itemFieldRect.anchoredPosition.x, itemFieldRect.anchoredPosition.y - 75f);
          itemFieldRect.anchoredPosition = new Vector2(itemFieldRect.anchoredPosition.x, itemFieldRect.anchoredPosition.y + 10f);
        }

        configPanel.QualityUI = qualityUIObj.GetComponent<QualityFieldUI>();
        if (configPanel.QualityUI == null)
        {
          configPanel.QualityUI = qualityUIObj.AddComponent<QualityFieldUI>();
          Log(Level.Info,
              "GetStorageConfigPanelTemplate: Added QualityFieldUI to QualityFieldUI",
              Category.Storage);
        }
        if (qualityUIObj.GetComponent<CanvasRenderer>() == null)
        {
          qualityUIObj.AddComponent<CanvasRenderer>();
        }

        ConfigPanelTemplate = configPanel;
        Log(Level.Info,
            "GetStorageConfigPanelTemplate: Template initialized successfully",
            Category.Storage);
        return configPanel;
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"GetStorageConfigPanelTemplate: Failed, error: {e.Message}\nStackTrace: {e.StackTrace}",
            Category.Storage);
        return null;
      }
    }
  }

  public class StorageConfigPanel : ConfigPanel
  {
    [SerializeField]
    public ItemFieldUI StorageItemUI;
    [SerializeField]
    public QualityFieldUI QualityUI;
    private Button Button;

    private void InitializeUIComponents()
    {
      try
      {
        Log(Level.Verbose,
            "StorageConfigPanel.InitializeUIComponents: Starting",
            Category.Storage);

        // Initialize StorageItemUI
        if (StorageItemUI == null)
        {
          var storageItemUITransform = transform.Find("StorageItemUI");
          if (storageItemUITransform == null)
          {
            Log(Level.Error,
                "StorageConfigPanel.InitializeUIComponents: StorageItemUI transform not found",
                Category.Storage);
            return;
          }
          StorageItemUI = storageItemUITransform.GetComponent<ItemFieldUI>();
          if (StorageItemUI == null)
          {
            StorageItemUI = storageItemUITransform.gameObject.AddComponent<ItemFieldUI>();
            Log(Level.Info,
                "StorageConfigPanel.InitializeUIComponents: Added ItemFieldUI",
                Category.Storage);
          }
          if (StorageItemUI.gameObject.GetComponent<CanvasRenderer>() == null)
            StorageItemUI.gameObject.AddComponent<CanvasRenderer>();
        }
        StorageItemUI.gameObject.SetActive(true);

        // Initialize QualityUI
        if (QualityUI == null)
        {
          var qualityUITransform = transform.Find("QualityFieldUI");
          if (qualityUITransform == null)
          {
            Log(Level.Error,
                "StorageConfigPanel.InitializeUIComponents: QualityFieldUI transform not found",
                Category.Storage);
            return;
          }
          QualityUI = qualityUITransform.GetComponent<QualityFieldUI>();
          if (QualityUI == null)
          {
            QualityUI = qualityUITransform.gameObject.AddComponent<QualityFieldUI>();
            Log(Level.Info,
                "StorageConfigPanel.InitializeUIComponents: Added QualityFieldUI",
                Category.Storage);
          }
          if (QualityUI.gameObject.GetComponent<CanvasRenderer>() == null)
            QualityUI.gameObject.AddComponent<CanvasRenderer>();
        }
        QualityUI.gameObject.SetActive(false);

        Log(Level.Info,
            "StorageConfigPanel.InitializeUIComponents: Completed",
            Category.Storage);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"StorageConfigPanel.InitializeUIComponents: Failed, error={e.Message}",
            Category.Storage);
      }
    }

    public override void Bind(List<EntityConfiguration> configs)
    {
      try
      {
        transform.gameObject.SetActive(true);
        Log(Level.Verbose,
            $"StorageConfigPanel.Bind: Binding {configs.Count} configs",
            Category.Storage);

        // Initialize UI components (unchanged)
        InitializeUIComponents();

        List<ItemField> itemFieldList = new();
        List<QualityField> qualityList = new();
        bool hasNone = false, hasAny = false, hasSpecific = false;
        ItemInstance item = null;
        bool isConsistent = true;
        List<ItemSlot> itemSlots = new();

        foreach (var config in configs.OfType<StorageConfiguration>())
        {
          itemFieldList.Add(config.StorageItem);
          itemSlots.AddRange(config.Storage.StorageEntity.ItemSlots);
          qualityList.Add(config.Quality);

          switch (config.Mode)
          {
            case StorageMode.None:
              hasNone = true;
              break;
            case StorageMode.Any:
              hasAny = true;
              break;
            case StorageMode.Specific:
              hasSpecific = true;
              if (item == null)
              {
                item = config.AssignedItem;
              }
              else if (!item.AdvCanStackWith(config.AssignedItem, allowTargetHigherQuality: true))
              {
                isConsistent = false;
                Log(Level.Warning,
                    $"StorageConfigPanel.Bind: Inconsistent items detected",
                    Category.Storage);
              }
              break;
          }
        }

        StorageItemUI.Bind(itemFieldList);
        QualityUI.Bind(qualityList);

        UpdateUIState(hasNone, hasAny, hasSpecific, isConsistent, item);

        Button = StorageItemUI.transform.Find("Selection")?.GetComponent<Button>();
        Button.onClick.RemoveAllListeners();
        Button.onClick.AddListener(() => OpenItemSelectorScreen(itemFieldList, "Assign Item", itemSlots));

        //if (DebugLogger.IsDebugMode)
        //AddCacheDebugUI();

        Log(Level.Info,
            $"StorageConfigPanel.Bind: Bound {itemFieldList.Count} ItemFields",
            Category.Storage);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"StorageConfigPanel.Bind: Failed, error: {e.Message}",
            Category.Storage);
      }
    }

    private void UpdateUIState(bool hasNone, bool hasAny, bool hasSpecific, bool isConsistent, ItemInstance item)
    {
      if (!isConsistent)
        StorageItemUI.SelectionLabel.text = "Mixed";
      else
      {
        if (hasNone)
          StorageItemUI.SelectionLabel.text = "None";
        else if (hasAny)
          StorageItemUI.SelectionLabel.text = "Any";
        else if (hasSpecific && item != null)
        {
          if (item is ProductItemInstance prodItem && prodItem.AppliedPackaging != null)
            StorageItemUI.IconImg.sprite = GetPackagedSprite(item.Definition, prodItem.AppliedPackaging);
          StorageItemUI.SelectionLabel.text = item.Definition.Name;
          QualityUI.gameObject.SetActive(item is ProductItemInstance);
        }
      }
    }

    private void AddCacheDebugUI()
    {
      var debugText = gameObject.AddComponent<TextMeshProUGUI>();
      debugText.text = "Cache Debug: Enable verbose logging for details";
      debugText.fontSize = 12;
      debugText.color = Color.gray;
      debugText.rectTransform.anchoredPosition = new Vector2(0, -100);
    }

    public void OpenItemSelectorScreen(List<ItemField> itemFields, string fieldName, List<ItemSlot> itemSlots)
    {
      if (itemFields == null || itemFields.Count == 0 || itemFields.Any(f => f == null))
      {
        Log(Level.Warning, "StorageConfigPanel: OpenItemSelectorScreen called with null or empty itemFields", Category.Storage);
        return;
      }

      if (Singleton<ManagementInterface>.Instance?.ItemSelectorScreen == null)
      {
        Log(Level.Error, "StorageConfigPanel: ItemSelectorScreen is null or ManagementInterface is not initialized", Category.Storage);
        return;
      }

      ItemField primaryField = itemFields[0];
      var primaryConfig = (StorageConfiguration)primaryField.ParentConfig;
      List<ItemSelector.Option> list = [];
      ItemSelector.Option selectedOption = null;

      // Add None and Any options
      list.Add(new StorageItemOption("None", new ItemDefinition { Name = "None", ID = "None", Icon = GetCrossSprite() }, null));
      list.Add(new StorageItemOption("Any", new ItemDefinition { Name = "Any", ID = "Any" }, null));

      // Set selected option based on mode
      if (primaryConfig.Mode == StorageMode.None)
        selectedOption = list[0];
      else if (primaryConfig.Mode == StorageMode.Any)
        selectedOption = list[1];

      itemSlots.AddRange([.. Player.Local.Inventory]);
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
        var (product, packaging) = pair.Value[0];
        if (product == null)
          continue;

        string title = packaging != null ? $"{product.Name} ({packaging.Name})" : product.Name;
        var opt = new StorageItemOption(title, product, packaging);
        list.Add(opt);

        if (primaryConfig.Mode == StorageMode.Specific &&
            primaryField.SelectedItem == product &&
            primaryConfig.AssignedItem is ProductItemInstance prodItem && prodItem.AdvCanStackWith(
                Registry.GetItem(product.ID).GetDefaultInstance(), allowTargetHigherQuality: true))
        {
          selectedOption = opt;
        }
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
                                              selectedItem?.Name ?? "None";
          StorageItemUI.gameObject.SetActive(true);

          Log(Level.Info, $"StorageConfigPanel: Selected mode: {newMode}, item: {selectedItem?.Name ?? "null"}, packaging: {selectedPackaging?.Name ?? "null"} for {fieldName}", Category.Storage);
        }));

        Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Open();
      }
      catch (Exception e)
      {
        Log(Level.Error, $"StorageConfigPanel: Failed to open ItemSelectorScreen for {fieldName}, error: {e.Message}", Category.Storage);
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
    public QualityField Quality { get; private set; }

    public StorageConfiguration(ConfigurationReplicator replicator, IConfigurable configurable, PlaceableStorageEntity storage)
        : base(replicator, configurable)
    {
      Log(Level.Info, $"StorageConfiguration: Initializing for GUID: {storage?.GUID}", Category.Storage);

      if (replicator == null || configurable == null || storage == null)
      {
        Log(Level.Error, $"StorageConfiguration: Null parameters - replicator: {replicator == null}, configurable: {configurable == null}, storage: {storage == null}, GUID: {storage?.GUID}", Category.Storage);
        throw new ArgumentNullException("StorageConfiguration: One or more parameters are null");
      }

      replicator.Configuration = this;
      Proxy = configurable as StorageConfigurableProxy;
      Storage = storage;

      if (StorageConfigs.TryGetValue(storage.ParentProperty, out var configs) && configs.TryGetValue(storage.GUID, out var config))
      {
        if (config.Mode == StorageMode.None)
          return;
        var storageKey = new EntityKey
        {
          Guid = storage.GUID,
          Type = config.Mode == StorageMode.Specific ? StorageType.SpecificShelf : StorageType.AnyShelf
        };
        var cacheManager = CacheService.GetOrCreateCacheManager(storage.ParentProperty);
        foreach (var slot in storage.InputSlots) // InputSlots and OutputSlots share ItemSlots
          cacheManager.RegisterItemSlot(slot, storageKey);

        // Queue initial slot update
        var slotData = storage.InputSlots.Select(s => new SlotData
        {
          Item = s.ItemInstance != null ? new ItemData(s.ItemInstance) : ItemData.Empty,
          Quantity = s.Quantity,
          SlotIndex = s.SlotIndex,
          StackLimit = s.ItemInstance?.StackLimit ?? -1,
          IsValid = true
        });
        cacheManager.QueueSlotUpdate(storageKey, slotData);
      }

      try
      {
        Quality = new QualityField(this);
        Quality.onValueChanged.AddListener(quality =>
        {
          if (AssignedItem is ProductItemInstance prodItem)
            prodItem.SetQuality(quality);
          InvokeChanged();
        });
        Quality.SetValue(EQuality.Premium, network: false);

        StorageItem = new ItemField(this)
        {
          CanSelectNone = false,
          Options = []
        };
        StorageItem.onItemChanged.AddListener(_ =>
        {
          UpdateStorageConfiguration(Storage, AssignedItem);
          RefreshChanged();
        });
        StorageConfigs[storage.ParentProperty][storage.GUID] = this;
        Log(Level.Info, $"StorageConfiguration: Initialized for GUID: {storage.GUID}", Category.Storage);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"StorageConfiguration: Failed to initialize for GUID: {storage.GUID}, error: {e.Message}", Category.Storage);
        throw;
      }
    }

    private void RefreshChanged()
    {
      var worldSpaceUI = (StorageUIElement)Proxy.WorldspaceUI;
      worldSpaceUI.RefreshUI();
      InvokeChanged();
    }

    public void Load(JObject jsonObject)
    {
      try
      {
        Log(Level.Verbose, $"StorageConfiguration.Load: Loading for GUID={Storage.GUID}", Category.Storage);

        if (jsonObject["StorageMode"] != null)
        {
          var str = jsonObject["StorageMode"].ToString();
          try
          {
            Mode = (StorageMode)Enum.Parse(typeof(StorageMode), str);
            if (Mode == StorageMode.Any)
            {
              if (!AnyShelves.TryGetValue(Storage.ParentProperty, out var anyShelves))
              {
                AnyShelves[Storage.ParentProperty] = new();
              }
              AnyShelves[Storage.ParentProperty].Add(Storage);
            }
          }
          catch (Exception e)
          {
            Log(Level.Warning, $"StorageConfiguration.Load: Invalid StorageMode '{str}' for GUID={Storage.GUID}, error: {e.Message}", Category.Storage);
            Mode = StorageMode.None;
          }
        }
        else
        {
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
            if (itemInstance is ProductItemInstance && jsonObject["Quality"] != null)
              Quality.SetValue(Enum.Parse<EQuality>(jsonObject["Quality"].ToString()), false);
          }
          else
          {
            StorageItem.SelectedItem = null;
            AssignedItem = null;
          }
        }
        else
        {
          StorageItem.SelectedItem = null;
          AssignedItem = null;
        }

        UpdateStorageConfiguration(Storage, AssignedItem);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"StorageConfiguration.Load: Failed for GUID={Storage.GUID}, error: {e.Message}", Category.Storage);
      }
    }

    public void SetModeAndItem(StorageMode mode, ItemInstance item)
    {
      Log(Level.Verbose,
          $"StorageConfiguration.SetModeAndItem: GUID={Storage.GUID}, mode={mode}, item={item?.ID ?? "null"}",
          Category.Storage);

      Mode = mode;
      ItemInstance previousItem = AssignedItem;
      StorageItem.SelectedItem = mode == StorageMode.Specific ? item?.Definition : null;
      AssignedItem = mode == StorageMode.Specific ? item : null;

      if (mode == StorageMode.Specific && item != null && previousItem != null)
      {
        // Validate quality compatibility
        if (!item.AdvCanStackWith(previousItem, allowTargetHigherQuality: true))
        {
          Log(Level.Warning,
              $"StorageConfiguration.SetModeAndItem: Quality mismatch for {item.ID}, resetting to default",
              Category.Storage);
          if (item is ProductItemInstance prodItem)
            prodItem.SetQuality(EQuality.Premium);
        }
      }

      StorageItem.onItemChanged?.Invoke(StorageItem.SelectedItem);
      UpdateStorageConfiguration(Storage, AssignedItem);
      RefreshChanged();
    }

    // Cleanup to prevent memory leaks
    public void Dispose()
    {
      Log(Level.Info,
          $"StorageConfiguration.Dispose: GUID={Storage.GUID}",
          Category.Storage);

      StorageConfigs[Storage.ParentProperty].Remove(Storage.GUID);

      var property = Storage.ParentProperty;
      if (property != null)
      {
        // Clear all related cache entries
        var cacheKeysToRemove = ShelfCache
            .Where(kvp => kvp.Value.ContainsKey(Storage))
            .Select(kvp => CreateCacheKey(kvp.Key, property))
            .ToList();
      }

      if (StorageConfigs.TryGetValue(Storage.ParentProperty, out var configs) && configs.TryGetValue(Storage.GUID, out var config))
      {
        if (config.Mode == StorageMode.None)
          return;
        var storageKey = new EntityKey
        {
          Guid = Storage.GUID,
          Type = config.Mode == StorageMode.Specific ? StorageType.SpecificShelf : StorageType.AnyShelf
        };
        var cacheManager = CacheService.GetOrCreateCacheManager(Storage.ParentProperty);
        foreach (var slot in Storage.InputSlots)
          cacheManager.UnregisterItemSlot(slot, storageKey);
        Storages[Storage.ParentProperty].Remove(Storage.GUID);
      }

      RemoveStorageFromLists(Storage);
    }
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
        Log(Level.Info, $"PlaceableStorageEntityPatch.Awake: Added ConfigurationReplicator to {__instance.gameObject.name}", Category.Storage);
      }
      return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(PlaceableStorageEntity __instance, ref string __result)
    {
      if (!StorageConfigs[__instance.ParentProperty].TryGetValue(__instance.GUID, out var config))
        return;

      JObject jsonObject = string.IsNullOrEmpty(__result) ? new JObject() : JObject.Parse(__result);
      jsonObject["StorageMode"] = config.Mode.ToString();

      if (config.StorageItem.SelectedItem != null)
      {
        var item = config.AssignedItem;
        jsonObject["StorageItem"] = item.GetItemData().GetJson(true);
        if (item is ProductItemInstance prodItem)
        {
          jsonObject["Quality"] = prodItem.Quality.ToString();
        }
      }

      __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
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
        Log(Level.Verbose, $"StorageRackLoaderPatch: Processing for {mainPath}", Category.Storage);
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          Log(Level.Warning, $"StorageRackLoaderPatch: No GridItem found for mainPath: {mainPath}", Category.Storage);
          return;
        }
        if (gridItem is not PlaceableStorageEntity storage)
        {
          Log(Level.Warning, $"StorageRackLoaderPatch: GridItem is not a PlaceableStorageEntity for mainPath: {mainPath}", Category.Storage);
          return;
        }
        string dataPath = Path.Combine(mainPath, "Data.json");
        if (!File.Exists(dataPath))
        {
          Log(Level.Warning, $"StorageRackLoaderPatch: No Data.json found at: {dataPath}", Category.Storage);
          return;
        }
        string json = File.ReadAllText(dataPath);
        Log(Level.Stacktrace, $"StorageRackLoaderPatch: Read JSON for GUID: {gridItem.GUID}, JSON: {json}", Category.Storage);
        JObject jsonObject = JObject.Parse(json);
        PendingConfigData[storage.GUID] = jsonObject;
        Log(Level.Stacktrace, $"StorageRackLoaderPatch: Stored pending config data for GUID: {gridItem.GUID}, StorageMode={jsonObject["StorageMode"]}, StorageItem={jsonObject["StorageItem"]}", Category.Storage);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"StorageRackLoaderPatch: Failed for {mainPath}, error: {e}", Category.Storage);
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
        Log(Level.Error, "StorageConfigurableProxy: storage or storage.gameObject is null", Category.Storage);
        throw new ArgumentNullException(nameof(storage));
      }

      var existingProxy = storage.gameObject.GetComponent<StorageConfigurableProxy>();
      if (existingProxy != null)
      {
        Log(Level.Info, $"StorageConfigurableProxy: Proxy already exists for {storage.gameObject.name}, GUID: {storage.GUID}", Category.Storage);
        return existingProxy;
      }

      var proxy = storage.gameObject.AddComponent<StorageConfigurableProxy>();
      proxy.Storage = storage;
      proxy.Initialize();
      return proxy;
    }

    private void Initialize()
    {
      Log(Level.Info, $"StorageConfigurableProxy: Initializing for {Storage.gameObject.name}, GUID: {Storage.GUID}", Category.Storage);

      // Verify NetworkObject
      var networkObject = gameObject.GetComponent<NetworkObject>();
      if (networkObject == null)
      {
        Log(Level.Error, $"StorageConfigurableProxy: No NetworkObject on {Storage.gameObject.name}, GUID: {Storage.GUID}", Category.Storage);
        throw new InvalidOperationException("NetworkObject is required");
      }

      // Attach ConfigurationReplicator
      configReplicator = gameObject.GetComponent<ConfigurationReplicator>();
      Log(Level.Info, $"StorageConfigurableProxy: Replicator: {configReplicator.name}, NetworkObject: {networkObject != null}, IsSpawned: {networkObject.IsSpawned}, GUID: {Storage.GUID}", Category.Storage);

      configReplicator.NetworkInitializeIfDisabled();
      Log(Level.Info, $"StorageConfigurableProxy: NetworkInitializeIfDisabled for ConfigurationReplicator on {Storage.GUID}", Category.Storage);
      CoroutineRunner.Instance.RunCoroutine(InitializeConfiguration());
    }

    private IEnumerator InitializeConfiguration()
    {
      yield return null;
      try
      {
        if (configReplicator == null || Storage == null)
        {
          Log(Level.Error, $"StorageConfigurableProxy: configReplicator or storage is null for GUID: {Storage.GUID}", Category.Storage);
          yield break;
        }
        configuration = new StorageConfiguration(configReplicator, this, Storage);
        configReplicator.Configuration = configuration;
        Log(Level.Info, $"StorageConfigurableProxy: Configuration created for GUID: {Storage.GUID}", Category.Storage);
      }
      catch (Exception e)
      {
        Log(Level.Error, $"StorageConfigurableProxy: Failed to create StorageConfiguration for GUID: {Storage.GUID}, error: {e.Message}", Category.Storage);
      }
      yield return CoroutineRunner.Instance.RunCoroutine(LoadConfigurationWhenReady());
    }

    private IEnumerator LoadConfigurationWhenReady()
    {
      var networkObject = gameObject.GetComponent<NetworkObject>();
      if (networkObject == null)
      {
        Log(Level.Error, $"LoadConfigurationWhenReady: No NetworkObject for GUID: {Storage.GUID}", Category.Storage);
        yield break;
      }
      int retries = 10;
      while (!networkObject.IsSpawned && retries > 0)
      {
        Log(Level.Verbose, $"LoadConfigurationWhenReady: Waiting for NetworkObject to spawn for GUID: {Storage.GUID}, retries left: {retries}", Category.Storage);
        yield return new WaitForSeconds(1f);
        retries--;
      }
      if (!networkObject.IsSpawned)
      {
        Log(Level.Error, $"LoadConfigurationWhenReady: NetworkObject failed to spawn after retries for GUID: {Storage.GUID}", Category.Storage);
        yield break;
      }
      ManagedObjects.InitializePrefab(networkObject, -1);
      configReplicator._transportManagerCache = NetworkManager.TransportManager;
      Log(Level.Verbose, $"StorageConfigurableProxy: InitializePrefab for ConfigurationReplicator on {Storage.GUID}", Category.Storage);
      yield return new WaitForSeconds(1f);
      if (PendingConfigData.TryGetValue(Storage.GUID, out var data))
      {
        try
        {
          configuration.Load(data);
          if (WorldspaceUI != null)
            ((StorageUIElement)WorldspaceUI).RefreshUI();
          UpdateStorageConfiguration(Storage, configuration.AssignedItem);

          // Network cache update
          if (IsServer)
            RpcUpdateClientCache(Storage.ParentProperty, Storage.GUID, configuration.AssignedItem);

          Log(Level.Info,
              $"LoadConfigurationWhenReady: Loaded for GUID={Storage.GUID}",
              Category.Storage);
        }
        catch (Exception e)
        {
          Log(Level.Error,
              $"LoadConfigurationWhenReady: Failed for GUID={Storage.GUID}, error={e.Message}",
              Category.Storage);
        }
        PendingConfigData.Remove(Storage.GUID);
      }

      yield return null;
    }

    [ServerRpc]
    private void RpcUpdateClientCache(Property property, Guid shelfGuid, ItemInstance item)
    {
      if (Storages[property].TryGetValue(shelfGuid, out var shelf))
      {
        UpdateStorageCache(shelf);
        TargetUpdateCache();
      }
    }

    [TargetRpc]
    private void TargetUpdateCache()
    {
      UpdateStorageCache(Storage);
    }

    private void OnEnable()
    {
      Log(Level.Info, $"StorageConfigurableProxy: OnEnable for GUID: {Storage.GUID}", Category.Storage);
    }

    private void OnDisable()
    {
      Log(Level.Info, $"StorageConfigurableProxy: OnDisable for GUID: {Storage.GUID}", Category.Storage);
    }

    private void OnDestroy()
    {
      Log(Level.Info, $"StorageConfigurableProxy: OnDestroy for GUID: {Storage.GUID}", Category.Storage);
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
      Log(Level.Info, $"OnSpawnServer: Called for GUID: {Storage.GUID}", Category.Storage);
      Storage.OnSpawnServer(connection);
      ((IItemSlotOwner)this).SendItemsToClient(connection);
      SendConfigurationToClient(connection);
    }

    public void SendConfigurationToClient(NetworkConnection conn)
    {
      Log(Level.Info, $"SendConfigurationToClient: Called for GUID: {Storage.GUID}, conn: {(conn != null ? conn.ClientId : -1)}", Category.Storage);

      if (!conn.IsHost)
      {
        CoroutineRunner.Instance.RunCoroutine(WaitForConfig());
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
        Log(Level.Error, $"CreateWorldspaceUI: ParentProperty is null for GUID: {Storage.GUID}", Category.Storage);
        return null;
      }
      GameObject uiPrefab = GetPrefabGameObject("PackagingStationUI");
      if (uiPrefab == null)
      {
        Log(Level.Error, $"CreateWorldspaceUI: Failed to load PackagingStationUI prefab for GUID: {Storage.GUID}", Category.Storage);
        return null;
      }
      var uiInstance = Instantiate(uiPrefab, ParentProperty.WorldspaceUIContainer);
      if (uiInstance == null)
      {
        Log(Level.Error, $"CreateWorldspaceUI: uiInstance is null for GUID: {Storage.GUID}", Category.Storage);
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
      else
        Log(Level.Warning, $"StorageUIElement.Initialize: Container (Outline) not found for GUID: {proxy.Storage.GUID}", Category.Storage);

      var titleLabel = GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name.Contains("Title"));
      if (titleLabel != null)
        titleLabel.text = proxy.Storage.Name;
      else
        Log(Level.Warning, $"StorageUIElement.Initialize: No Title TextMeshProUGUI found for GUID: {proxy.Storage.GUID}", Category.Storage);

      var iconImg = GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.gameObject.name.Contains("Icon"));
      if (iconImg != null)
        Icon = iconImg;
      else
        Log(Level.Warning, $"StorageUIElement.Initialize: No Icon Image found for GUID: {proxy.Storage.GUID}", Category.Storage);
      if (proxy.configuration.Mode == default)
      {
        proxy.configuration.Mode = StorageMode.None;
        Icon.sprite = GetCrossSprite();
        Icon.color = Color.red;
      }
      Log(Level.Verbose, $"StorageUIElement.Initialize: Sprite: {Icon.sprite?.name}", Category.Storage);
      Proxy = proxy;
      Proxy.Configuration.onChanged.AddListener(RefreshUI);
      RefreshUI();
      Log(Level.Info, $"StorageUIElement.Initialize: Completed for GUID: {proxy.Storage.GUID}", Category.Storage);
    }

    public void RefreshUI()
    {
      UpdateWorldspaceUIIcon();
    }

    public void UpdateWorldspaceUIIcon()
    {
      if (Proxy.WorldspaceUI == null || Icon == null)
        return;

      var config = Proxy.configuration;
      Sprite newSprite = null;
      Color iconColor = Color.white;

      if (config == null)
      {
        Log(Level.Warning,
            $"StorageUIElement.UpdateWorldspaceUIIcon: Config null for GUID={Proxy.Storage.GUID}",
            Category.Storage);
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
      else if (config.Mode == StorageMode.Specific && config.AssignedItem != null)
      {
        var defaultItem = Registry.GetItem(config.AssignedItem.ID).GetDefaultInstance();
        if (config.AssignedItem.AdvCanStackWith(defaultItem, allowTargetHigherQuality: true))
          newSprite = GetPackagedSprite(config.AssignedItem.Definition,
              (config.AssignedItem as ProductItemInstance)?.AppliedPackaging);
        iconColor = Color.white;
      }

      Icon.sprite = newSprite;
      Icon.color = iconColor;
      Icon.enabled = newSprite != null;
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
        if (!__instance.isGhost && GUID != "00000000-0000-0000-0000-000000000000" && __instance.GUID.ToString() != "00000000-0000-0000-0000-000000000000" && StorageInstanceIDs.All.Contains(instance.ID))
        {
          if (__instance is PlaceableStorageEntity entity)
          {
            Log(Level.Info, $"GridItemPatch: Initializing storage for GUID: {__instance.GUID}", Category.Storage);
            InitializeStorage(__instance.GUID, entity);
          }
          else
          {
            Log(Level.Warning, $"GridItemPatch: {__instance.GUID} is not a PlaceableStorageEntity", Category.Storage);
          }
        }
        return true;
      }
      catch (Exception e)
      {
        Log(Level.Error, $"GridItemPatch: Failed for GUID: {GUID}, error: {e.Message}", Category.Storage);
        return true;
      }
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
        var btnComponent = button.GetComponent<Button>();
        Log(Level.Verbose, $"ItemSelectorPatch.CreateOptionsPostfix: Button {i}, Title={options[i].Title}, interactable={btnComponent?.interactable}, active={button.gameObject.activeSelf}", Category.Storage);
        var option = options[i];
        if (option is StorageItemOption storageOption && storageOption.PackagingDefinition != null && storageOption.Item != null)
        {
          var icon = button.transform.Find("Icon").gameObject.GetComponent<Image>();
          icon.sprite = GetPackagedSprite(storageOption.Item, storageOption.PackagingDefinition);
          icon.gameObject.SetActive(icon.sprite != null);
        }
        if (option.Title == "None")
          button.transform.Find("Icon").gameObject.GetComponent<Image>().color = Color.red;
        if (option.Title == "Any")
          button.transform.Find("Icon").gameObject.GetComponent<Image>().gameObject.SetActive(false);
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