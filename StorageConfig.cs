using FishNet.Connection;
using FishNet.Object;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;

namespace NoLazyWorkers
{
  public class ConfigurableStorageEntity : PlaceableStorageEntity, IConfigurable
  {
    public EntityConfiguration Configuration => storageConfiguration;
    public StorageConfiguration storageConfiguration { get; set; }
    public ConfigurationReplicator ConfigReplicator => configReplicator;
    public EConfigurableType ConfigurableType => EConfigurableType.MixingStation;
    public WorldspaceUIElement WorldspaceUI { get; set; }
    public NetworkObject CurrentPlayerConfigurer { get; set; }
    public bool IsBeingConfiguredByOtherPlayer => CurrentPlayerConfigurer != null && !CurrentPlayerConfigurer.IsOwner;
    public Sprite TypeIcon => null;
    public Transform Transform => transform;
    public Transform UIPoint => transform;
    public bool CanBeSelected => true;

    public ConfigurationReplicator configReplicator;


    public override void InitializeGridItem(ItemInstance instance, ScheduleOne.Tiles.Grid grid, Vector2 originCoordinate, int rotation, string GUID)
    {
      bool initialized = Initialized;
      base.InitializeGridItem(instance, grid, originCoordinate, rotation, GUID);

      if (!initialized && !isGhost)
      {
        if (configReplicator == null)
        {
          configReplicator = gameObject.AddComponent<ConfigurationReplicator>();
        }
        ParentProperty.AddConfigurable(this);
        storageConfiguration = new StorageConfiguration(configReplicator, this, this);
        CreateWorldspaceUI();

        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"ConfigurableStorageEntity: Initialized configuration for {name}");
      }
    }

    public void SetConfigurer(NetworkObject player)
    {
      CurrentPlayerConfigurer = player;
      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"ConfigurableStorageEntity: Configurer set to {player?.name ?? "null"} for {name}");
    }

    public void Selected()
    {
      Configuration?.Selected();
    }

    public void Deselected()
    {
      Configuration?.Deselected();
    }

    public override void DestroyItem(bool callOnServer = true)
    {
      if (storageConfiguration != null)
      {
        storageConfiguration.Destroy();
        ParentProperty.RemoveConfigurable(this);
        StorageConfigurationExtensions.StorageConfig.Remove(this);
      }

      DestroyWorldspaceUI();
      base.DestroyItem(callOnServer);
    }

    public override List<string> WriteData(string parentFolderPath)
    {
      List<string> list = base.WriteData(parentFolderPath);

      if (storageConfiguration != null && storageConfiguration.ShouldSave())
      {
        list.Add("Configuration.json");
        System.IO.File.WriteAllText(System.IO.Path.Combine(parentFolderPath, "Configuration.json"), storageConfiguration.GetSaveString());
      }

      return list;
    }

    public WorldspaceUIElement CreateWorldspaceUI()
    {
      return null;
    }

    public void DestroyWorldspaceUI()
    {
      if (WorldspaceUI != null)
      {
        WorldspaceUI.Destroy();
        WorldspaceUI = null;
      }
    }
    public override void OnSpawnServer(NetworkConnection connection)
    {
      base.OnSpawnServer(connection);
      if (!connection.IsLocalClient && Initialized)
      {
        SendInitToClient(connection);
      }

      SendConfigurationToClient(connection);
    }
    public void SendConfigurationToClient(NetworkConnection conn)
    {
      if (!conn.IsHost)
      {
        Singleton<CoroutineService>.Instance.StartCoroutine(WaitForConfig());
      }

      System.Collections.IEnumerator WaitForConfig()
      {
        yield return new WaitUntil(() => Configuration != null);
        Configuration.ReplicateAllFields(conn);
      }
    }
  }

  public class StorageConfiguration : EntityConfiguration
  {
    public int itemFieldCount;
    public List<ItemField> StorageItems { get; private set; }
    public ConfigurableStorageEntity Storage { get; private set; }
    public event Action OnChanged;

    public StorageConfiguration(ConfigurationReplicator replicator, IConfigurable configurable, ConfigurableStorageEntity storage)
        : base(replicator, configurable)
    {
      Storage = storage;
      StorageItems = new List<ItemField>();

      itemFieldCount = GetItemFieldCount(storage);

      for (int i = 0; i < itemFieldCount; i++)
      {
        ItemField storageItem = new ItemField(this)
        {
          CanSelectNone = true,
          Options = []
        };
        storageItem.onItemChanged.AddListener(item => InvokeChanged());
        StorageItems.Add(storageItem);
      }

      StorageConfigurationExtensions.StorageConfig[storage] = this;

      if (DebugConfig.EnableDebugLogs)
        MelonLogger.Msg($"StorageConfiguration: Created with {itemFieldCount} item fields for storage: {storage?.name ?? "null"}");
    }

    public override bool ShouldSave()
    {
      return StorageItems.Any(item => item?.SelectedItem != null);
    }

    public override string GetSaveString()
    {
      ExtendedStorageConfigurationData data = new ExtendedStorageConfigurationData
      {
        StorageItems = StorageItems.Select(item => item.GetData()).ToList()
      };
      return JsonUtility.ToJson(data, true);
    }

    public static int GetItemFieldCount(ConfigurableStorageEntity storage)
    {
      string storageName = storage?.name?.ToLower() ?? "";
      return GetItemFieldCount(storageName);
    }
    public static int GetItemFieldCount(string storageName)
    {
      if (storageName.Contains("safe_built")) return 8;
      if (storageName.Contains("storagerack_large")) return 8;
      if (storageName.Contains("storagerack_medium")) return 6;
      if (storageName.Contains("storagerack_small")) return 4;
      return 4;
    }
  }

  public class StorageConfigPanel : ConfigPanel
  {
    private List<ItemFieldUI> storageItemUIs;

    public override void Bind(List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Binding configs, count: {configs?.Count ?? 0}");

        if (this == null || gameObject == null)
        {
          MelonLogger.Error("StorageConfigPanel: Instance or GameObject is null");
          return;
        }
        foreach (StorageConfiguration config in configs.OfType<StorageConfiguration>())
        {
          storageItemUIs = [.. config.Storage.GetComponentsInChildren<ItemFieldUI>()];
          for (int i = 0; i < config.StorageItems.Count && i < storageItemUIs.Count; i++)
          {
            storageItemUIs[i].Bind(new List<ItemField> { config.StorageItems[i] });
            var valueLabel = storageItemUIs[i].GetComponentsInChildren<TextMeshProUGUI>()
                .FirstOrDefault(t => t.gameObject.name.Contains("Value"));
            if (valueLabel != null)
            {
              valueLabel.text = config.StorageItems[i].SelectedItem?.Name ?? "None";
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"StorageConfigPanel: Set ValueLabel {i} to: {valueLabel.text}");
            }
            else
            {
              MelonLogger.Warning($"StorageConfigPanel: Value TextMeshProUGUI missing on StorageItemUI_{i}");
            }
          }
        }
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel: Bind failed, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(StorageRackLoader), "Load")]
  public class StorageRackLoaderLoadPatch
  {
    static void Postfix(StorageRackLoader __instance, string mainPath)
    {
      try
      {
        GridItem gridItem = AccessTools.Field(typeof(GridItemLoader), "loadedItem").GetValue(__instance) as GridItem;
        if (gridItem is ConfigurableStorageEntity storageEntity)
        {
          string configPath = System.IO.Path.Combine(mainPath, "Configuration.json");
          if (System.IO.File.Exists(configPath))
          {
            string json = System.IO.File.ReadAllText(configPath);
            ExtendedStorageConfigurationData data = JsonUtility.FromJson<ExtendedStorageConfigurationData>(json);
            if (data != null && data.StorageItems != null)
            {
              if (StorageConfigurationExtensions.StorageConfig.TryGetValue(storageEntity, out var config))
              {
                for (int i = 0; i < config.StorageItems.Count && i < data.StorageItems.Count; i++)
                {
                  config.StorageItems[i].Load(data.StorageItems[i]);
                }
                if (DebugConfig.EnableDebugLogs)
                  MelonLogger.Msg($"StorageRackLoaderLoadPatch: Loaded configuration for {storageEntity.name}");
              }
            }
          }
        }
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"StorageRackLoaderLoadPatch: Failed to load configuration for {mainPath}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(ConfigurableStorageEntity), "WriteData")]
  public class ConfigurableStorageEntityWriteDataPatch
  {
    static void Postfix(ConfigurableStorageEntity __instance, string parentFolderPath, ref List<string> __result)
    {
      try
      {
        if (StorageConfigurationExtensions.StorageConfig.TryGetValue(__instance, out var config) && config.ShouldSave())
        {
          __result.Add("Configuration.json");
          System.IO.File.WriteAllText(System.IO.Path.Combine(parentFolderPath, "Configuration.json"), config.GetSaveString());
        }
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"ConfigurableStorageEntityWriteDataPatch: Failed for {__instance?.name ?? "null"}, error: {e}");
      }
    }
  }

  [System.Serializable]
  public class ExtendedStorageConfigurationData
  {
    public List<ItemFieldData> StorageItems;

    public ExtendedStorageConfigurationData()
    {
      StorageItems = new List<ItemFieldData>();
    }
  }

  public static class StorageConfigurationExtensions
  {
    public static readonly Dictionary<ConfigurableStorageEntity, StorageConfiguration> StorageConfig = new Dictionary<ConfigurableStorageEntity, StorageConfiguration>();

    private static ConfigPanel InitializeStaticTemplate(string storageType, int itemFieldCount)
    {
      if (ConfigurationExtensions.StorageConfigPanelTemplates.TryGetValue(storageType, out ConfigPanel template))
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Template for {storageType} already initialized, skipping");
        return template;
      }

      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Initializing template for {storageType} with {itemFieldCount} item fields");

        // Create a base ConfigPanel GameObject
        Transform storageTransform = NoLazyUtilities.GetTransformTemplateFromConfigPanel(EConfigurableType.Pot, "");
        GameObject storageObj = UnityEngine.Object.Instantiate(storageTransform.gameObject);
        storageObj.name = storageType;
        storageObj.AddComponent<CanvasRenderer>();
        // Add StorageConfigPanel component
        var defaultScript = storageObj.GetComponent<PotConfigPanel>();
        if (defaultScript == null)
          return null;
        UnityEngine.Object.Destroy(defaultScript);
        StorageConfigPanel configPanel = storageObj.AddComponent<StorageConfigPanel>();
        if (configPanel == null)
        {
          MelonLogger.Error("StorageConfigPanel: configPanel template not found");
          return null;
        }
        Transform itemFieldUITransform = storageTransform.Find("Seed");
        if (itemFieldUITransform == null)
        {
          MelonLogger.Error("StorageConfigPanel: itemFieldUITransform template not found");
          return null;
        }

        // clean template
        var part = storageTransform.Find("Seed");
        if (part == null)
          return null;
        RectTransform partRect = part.GetComponent<RectTransform>();
        var initialYPosition = partRect.anchoredPosition.y;
        UnityEngine.Object.Destroy(part);
        part = storageTransform.Find("Additive1");
        if (part == null)
          return null;
        partRect = part.GetComponent<RectTransform>();
        var yOffset = initialYPosition - partRect.anchoredPosition.y;
        UnityEngine.Object.Destroy(part);
        part = storageTransform.Find("Additive2");
        if (part == null)
          return null;
        UnityEngine.Object.Destroy(part);
        part = storageTransform.Find("Additive3");
        if (part == null)
          return null;
        UnityEngine.Object.Destroy(part);
        part = storageTransform.Find("Botanist");
        if (part == null)
          return null;
        UnityEngine.Object.Destroy(part);
        part = storageTransform.Find("ObjectFieldUI");
        if (part == null)
          return null;
        UnityEngine.Object.Destroy(part);

        for (int i = 0; i < itemFieldCount; i++)
        {
          GameObject itemFieldUIObj = UnityEngine.Object.Instantiate(itemFieldUITransform.gameObject, storageTransform, false);
          itemFieldUIObj.name = $"StorageItemUI_{i}";

          if (!itemFieldUIObj.GetComponent<CanvasRenderer>())
            itemFieldUIObj.AddComponent<CanvasRenderer>();

          TextMeshProUGUI titleTextComponent = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>()
              .FirstOrDefault(t => t.gameObject.name == "Title");
          if (titleTextComponent != null)
            titleTextComponent.text = $"Storage Item {i + 1}";
          else
            MelonLogger.Warning($"StorageConfigPanel: Title TextMeshProUGUI missing on StorageItemUI_{i}");

          RectTransform storageRect = itemFieldUIObj.GetComponent<RectTransform>();
          storageRect.anchoredPosition = new Vector2(storageRect.anchoredPosition.x, initialYPosition + (i * yOffset));

          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"StorageConfigPanel: Created StorageItemUI_{i} at y-position: {storageRect.anchoredPosition.y}");
        }
        ConfigurationExtensions.StorageConfigPanelTemplates[storageType] = configPanel;
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Template for {storageType} initialized successfully");
        return configPanel;
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel: Failed to initialize template for {storageType}, error: {e}");
        return null;
      }
    }

    public static ConfigPanel SetupConfigPanel(GameObject storage)
    {
      if (storage == null)
      {
        MelonLogger.Error("StorageConfigPanel: storage is null");
        return null;
      }
      PlaceableStorageEntity defaultScript = storage.GetComponent<PlaceableStorageEntity>();
      if (defaultScript == null)
      {
        MelonLogger.Error("StorageConfigPanel: defaultScript is null");
        return null;
      }
      ConfigurableStorageEntity newComponent = storage.AddComponent<ConfigurableStorageEntity>();
      if (newComponent == null)
      {
        MelonLogger.Error("StorageConfigPanel: newComponent is null");
        return null;
      }
      // duplicate all settings from defaultscript.gameObject to newComponent
      UnityEngine.Object.Destroy(defaultScript);
      string storageType = storage?.name?.ToLower();
      return InitializeStaticTemplate(storageType, StorageConfiguration.GetItemFieldCount(storageType));
    }

    public static ConfigPanel GetConfigPanel(string storageType)
    {
      // Retrieve cached template
      if (ConfigurationExtensions.StorageConfigPanelTemplates.TryGetValue(storageType, out ConfigPanel template))
      {
        return template;
      }
      MelonLogger.Error($"StorageConfigPanel: Template for {storageType} not found after initialization");
      return InitializeStaticTemplate(storageType, StorageConfiguration.GetItemFieldCount(storageType));
    }
  }

  public class StoragePreset : Preset
  {
    private static StoragePreset DefaultPresetInstance;
    public List<ItemList> StorageItems { get; set; }
    public int ItemFieldCount { get; set; }

    public override Preset GetCopy()
    {
      var preset = new StoragePreset();
      CopyTo(preset);
      return preset;
    }

    public override void CopyTo(Preset other)
    {
      base.CopyTo(other);
      if (other is StoragePreset targetPreset)
      {
        targetPreset.StorageItems = new List<ItemList>();
        foreach (var item in StorageItems)
        {
          var newItem = new ItemList(item.Name, new List<string>(item.OptionList), false, true)
          {
            All = item.All,
            None = item.None
          };
          targetPreset.StorageItems.Add(newItem);
        }
        targetPreset.ItemFieldCount = ItemFieldCount;
      }
    }

    public override void InitializeOptions()
    {
      StorageItems = new List<ItemList>();
      ItemFieldCount = 4;
      List<string> ingredients = [];

      for (int i = 0; i < ItemFieldCount; i++)
      {
        var itemList = new ItemList($"Storage Item {i + 1}", ingredients, true, true)
        {
          All = false,
          None = true
        };
        StorageItems.Add(itemList);
      }
    }

    public static StoragePreset GetDefaultPreset()
    {
      if (DefaultPresetInstance == null)
      {
        DefaultPresetInstance = new StoragePreset
        {
          PresetName = "Default",
          ObjectType = (ManageableObjectType)200,
          PresetColor = new Color32(180, 180, 180, 255)
        };
        DefaultPresetInstance.InitializeOptions();
      }
      return DefaultPresetInstance;
    }

    public static StoragePreset GetNewBlankPreset()
    {
      StoragePreset preset = GetDefaultPreset().GetCopy() as StoragePreset;
      preset.PresetName = "New Preset";
      return preset;
    }
  }

  public class StoragePresetEditScreen : PresetEditScreen
  {
    public GenericOptionUI StorageItemUI_0 { get; set; }
    public GenericOptionUI StorageItemUI_1 { get; set; }
    public GenericOptionUI StorageItemUI_2 { get; set; }
    public GenericOptionUI StorageItemUI_3 { get; set; }
    public GenericOptionUI StorageItemUI_4 { get; set; }
    public GenericOptionUI StorageItemUI_5 { get; set; }
    public GenericOptionUI StorageItemUI_6 { get; set; }
    public GenericOptionUI StorageItemUI_7 { get; set; }

    private StoragePreset castedPreset;
    private List<GenericOptionUI> storageItemUIs;

    public override void Awake()
    {
      base.Awake();
      storageItemUIs = new List<GenericOptionUI>
            {
                StorageItemUI_0, StorageItemUI_1, StorageItemUI_2, StorageItemUI_3,
                StorageItemUI_4, StorageItemUI_5, StorageItemUI_6, StorageItemUI_7
            };

      // Add click listeners with index to identify which UI was clicked
      for (int i = 0; i < storageItemUIs.Count; i++)
      {
        if (storageItemUIs[i] != null && storageItemUIs[i].Button != null)
        {
          int index = i; // Capture index in closure
          storageItemUIs[i].Button.onClick.AddListener(() => StorageItemUIClicked(index));
        }
      }
    }

    protected virtual void Update()
    {
      if (isOpen)
      {
        UpdateUI();
      }
    }

    public override void Open(Preset preset)
    {
      base.Open(preset);
      castedPreset = (StoragePreset)EditedPreset;
      UpdateUI();
    }

    private void UpdateUI()
    {
      if (castedPreset == null || castedPreset.StorageItems == null)
        return;

      for (int i = 0; i < storageItemUIs.Count; i++)
      {
        if (storageItemUIs[i] != null && i < castedPreset.StorageItems.Count)
        {
          storageItemUIs[i].ValueLabel.text = castedPreset.StorageItems[i].GetDisplayString();
          storageItemUIs[i].gameObject.SetActive(true);
        }
        else if (storageItemUIs[i] != null)
        {
          storageItemUIs[i].gameObject.SetActive(false); // Hide unused UIs
        }
      }
    }

    private void StorageItemUIClicked(int index)
    {
      if (castedPreset == null || index >= castedPreset.StorageItems.Count)
      {
        MelonLogger.Error($"StoragePresetEditScreen: Invalid click index {index} or null preset");
        return;
      }

      var itemList = castedPreset.StorageItems[index];
      itemList.OptionList = GetAllStorableItems();
      Singleton<ItemSetterScreen>.Instance.Open(itemList);
    }

    [HarmonyPatch(typeof(Preset), "GetDefault")]
    public class PresetPatch
    {
      static bool Prefix(ManageableObjectType type, ref Preset __result)
      {
        if (type == (ManageableObjectType)200)
        {
          __result = StoragePreset.GetDefaultPreset();
          return false;
        }
        return true;
      }
    }
  }
}