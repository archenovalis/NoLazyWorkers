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
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;

namespace NoLazyWorkers
{
  public class ConfigurableStorageEntity : PlaceableStorageEntity, IConfigurable
  {
    public EntityConfiguration Configuration { get; protected set; }
    public ConfigurationReplicator ConfigReplicator => _configReplicator;
    public EConfigurableType ConfigurableType => EConfigurableType.MixingStation; // Placeholder
    public WorldspaceUIElement WorldspaceUI { get; set; }
    public NetworkObject CurrentPlayerConfigurer { get; set; }
    public bool IsBeingConfiguredByOtherPlayer => CurrentPlayerConfigurer != null && !CurrentPlayerConfigurer.IsOwner;
    public Sprite TypeIcon => null;
    public Transform Transform => transform;
    public Transform UIPoint => transform;
    public bool CanBeSelected => true;

    [SerializeField] protected ConfigurationReplicator _configReplicator;

    protected StorageConfiguration storageConfiguration { get; set; }

    public override void InitializeGridItem(ItemInstance instance, ScheduleOne.Tiles.Grid grid, Vector2 originCoordinate, int rotation, string GUID)
    {
      bool initialized = Initialized;
      base.InitializeGridItem(instance, grid, originCoordinate, rotation, GUID);

      if (!initialized && !isGhost)
      {
        if (_configReplicator == null)
        {
          _configReplicator = gameObject.AddComponent<ConfigurationReplicator>();
        }
        storageConfiguration = new StorageConfiguration(_configReplicator, this, this);
        Configuration = storageConfiguration;
        ParentProperty.AddConfigurable(this);

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

    public override void OnSpawnServer(NetworkConnection connection)
    {
      base.OnSpawnServer(connection);
      SendConfigurationToClient(connection);
    }

    public override bool CanBeDestroyed(out string reason)
    {
      if (!base.CanBeDestroyed(out reason))
        return false;

      if (storageConfiguration != null && storageConfiguration.ShouldSave())
      {
        reason = "Has active configuration";
        return false;
      }

      return true;
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
          Options = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients.Cast<ItemDefinition>().ToList()
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

    private static readonly Dictionary<string, ConfigPanel> StorageConfigPanelTemplates = new Dictionary<string, ConfigPanel>();

    private static void InitializeStaticTemplate(string storageType, int itemFieldCount)
    {
      if (StorageConfigPanelTemplates.ContainsKey(storageType))
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Template for {storageType} already initialized, skipping");
        return;
      }

      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Initializing template for {storageType} with {itemFieldCount} item fields");

        // Create a base ConfigPanel GameObject
        GameObject templateObj = new GameObject($"StorageConfigPanel_{storageType}");
        templateObj.AddComponent<CanvasRenderer>();
        templateObj.SetActive(false);

        // Add StorageConfigPanel component
        StorageConfigPanel configPanel = templateObj.AddComponent<StorageConfigPanel>();

        // Add RectTransform for UI layout
        RectTransform rectTransform = templateObj.GetComponent<RectTransform>();
        if (rectTransform == null)
        {
          rectTransform = templateObj.AddComponent<RectTransform>();
        }
        rectTransform.sizeDelta = new Vector2(300, 400); // Adjust as needed

        StorageConfigPanelTemplates[storageType] = configPanel;
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"StorageConfigPanel: Template for {storageType} initialized successfully");
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel: Failed to initialize template for {storageType}, error: {e}");
      }
    }

    static bool GetConfigPanelPrefab(EConfigurableType type, ref ConfigPanel __result, ManagementInterface __instance)
    {
      if (type == EConfigurableType.MixingStation && __instance.Configurables.Any(c => c is ConfigurableStorageEntity))
      {
        ConfigurableStorageEntity storageEntity = __instance.Configurables.OfType<ConfigurableStorageEntity>().FirstOrDefault();
        if (storageEntity == null)
        {
          MelonLogger.Error("StorageConfigPanel: No ConfigurableStorageEntity found in Configurables");
          return true;
        }

        string storageType = storageEntity?.name?.ToLower();
        int itemFieldCount = StorageConfiguration.GetItemFieldCount(storageEntity);

        // Initialize template if not already cached
        InitializeStaticTemplate(storageType, itemFieldCount);

        // Retrieve cached template
        if (StorageConfigPanelTemplates.TryGetValue(storageType, out ConfigPanel template))
        {
          __result = template;
          return false;
        }

        MelonLogger.Error($"StorageConfigPanel: Template for {storageType} not found after initialization");
        return true;
      }
      return true;
    }

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

        storageItemUIs = new List<ItemFieldUI>();
        Transform templateTransform = NoLazyUtilities.GetTransformTemplateFromConfigPanel(EConfigurableType.Pot, "Seed");
        if (templateTransform == null)
        {
          MelonLogger.Error("StorageConfigPanel: Transform template not found");
          return;
        }
        ItemFieldUI templateItemFieldUI = templateTransform.GetComponentInChildren<ItemFieldUI>();
        if (templateItemFieldUI == null)
        {
          MelonLogger.Error("StorageConfigPanel: ItemFieldUI template not found");
          return;
        }

        int itemFieldCount = 4;
        StorageConfiguration firstConfig = configs.OfType<StorageConfiguration>().FirstOrDefault();
        if (firstConfig != null)
        {
          itemFieldCount = firstConfig.StorageItems.Count;
        }
        else
        {
          MelonLogger.Warning("StorageConfigPanel: No StorageConfiguration found in configs");
        }

        float initialYPosition = -135.76f;
        float yOffset = -50f;

        for (int i = 0; i < itemFieldCount; i++)
        {
          GameObject storageItemUIObj = UnityEngine.Object.Instantiate(templateItemFieldUI.gameObject, transform, false);
          storageItemUIObj.name = $"StorageItemUI_{i}";
          ItemFieldUI storageItemUI = storageItemUIObj.GetComponent<ItemFieldUI>();
          if (storageItemUI == null)
          {
            MelonLogger.Error($"StorageConfigPanel: ItemFieldUI component missing on StorageItemUI_{i}");
            continue;
          }

          if (!storageItemUIObj.GetComponent<CanvasRenderer>())
            storageItemUIObj.AddComponent<CanvasRenderer>();

          TextMeshProUGUI titleTextComponent = storageItemUIObj.GetComponentsInChildren<TextMeshProUGUI>()
              .FirstOrDefault(t => t.gameObject.name == "Title");
          if (titleTextComponent != null)
            titleTextComponent.text = $"Storage Item {i + 1}";
          else
            MelonLogger.Warning($"StorageConfigPanel: Title TextMeshProUGUI missing on StorageItemUI_{i}");

          RectTransform storageRect = storageItemUIObj.GetComponent<RectTransform>();
          storageRect.anchoredPosition = new Vector2(0, initialYPosition + (i * yOffset));

          storageItemUIs.Add(storageItemUI);

          if (DebugConfig.EnableDebugLogs)
            MelonLogger.Msg($"StorageConfigPanel: Created StorageItemUI_{i} at y-position: {storageRect.anchoredPosition.y}");
        }

        foreach (StorageConfiguration config in configs.OfType<StorageConfiguration>())
        {
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
  }

  public class StoragePreset : Preset
  {
    private static StoragePreset DefaultPresetInstance;

    public ItemList Items { get; set; }

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
        Items.CopyTo(targetPreset.Items);
      }
    }

    public override void InitializeOptions()
    {
      Items = new ItemList("Mixer", NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.ToArray().Select(item => item.ID).ToList(), true, true);
      Items.All = false;
      Items.None = true;
    }

    public static StoragePreset GetDefaultPreset()
    {
      if (DefaultPresetInstance == null)
      {
        DefaultPresetInstance = new StoragePreset
        {
          PresetName = "Default",
          ObjectType = (ManageableObjectType)100,
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
    private StoragePreset castedPreset { get; set; }

    public override void Awake()
    {
      base.Awake();
      StorageItemUI_0.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_1.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_2.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_3.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_4.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_5.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_6.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
      StorageItemUI_7.Button.onClick.AddListener(new UnityAction(StorageItemUIClicked));
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
      StorageItemUI_0.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_1.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_2.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_3.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_4.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_5.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_6.ValueLabel.text = castedPreset.Items.GetDisplayString();
      StorageItemUI_7.ValueLabel.text = castedPreset.Items.GetDisplayString();
    }

    private void StorageItemUIClicked()
    {
      castedPreset.Items = new ItemList("every item that can be stored in a shelf by group", [], false, true);
      Singleton<ItemSetterScreen>.Instance.Open(castedPreset.Items);
    }
  }

  [HarmonyPatch(typeof(Preset), "GetDefault")]
  public class PresetPatch
  {
    static bool Prefix(ManageableObjectType type, ref Preset __result)
    {
      if (type == (ManageableObjectType)100)
      {
        __result = StoragePreset.GetDefaultPreset();
        return false;
      }
      return true;
    }
  }

}