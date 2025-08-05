using FishNet.Connection;
using FishNet.Object;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
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
using System.Collections;
using TMPro;
using UnityEngine;

namespace NoLazyWorkers.Handlers
{
  public static class StorageExtensions
  {
    public const int StorageEnum = 345543;
    public static StorageConfigPanel ConfigPanelTemplate;
    public static Dictionary<Guid, StorageConfiguration> StorageConfig = [];

    public static void InitializeStaticStorageConfigPanelTemplate()
    {
      GetStorageConfigPanelTemplate();
    }

    public static StorageConfigPanel GetStorageConfigPanelTemplate()
    {
      try
      {
        if (ConfigPanelTemplate != null)
          return ConfigPanelTemplate;
        else
        {
          Transform storageTransform = NoLazyUtilities.GetTransformTemplateFromConfigPanel(EConfigurableType.Pot, "");
          GameObject storageObj = UnityEngine.Object.Instantiate(storageTransform.gameObject);
          storageObj.name = "StorageConfigPanel";
          storageObj.AddComponent<CanvasRenderer>();
          var defaultScript = storageObj.GetComponent<PotConfigPanel>();
          if (defaultScript == null)
          {
            return null;
          }

          UnityEngine.Object.Destroy(defaultScript);
          StorageConfigPanel configPanel = storageObj.AddComponent<StorageConfigPanel>();
          if (configPanel == null)
          {
            MelonLogger.Error("StorageConfigPanel: configPanel template not found");
            return null;
          }

          foreach (string partName in new[] { "Additive1", "Additive2", "Additive3", "Botanist", "ObjectFieldUI" })
          {
            var part = storageObj.transform.Find(partName);
            if (part != null) UnityEngine.Object.Destroy(part.gameObject);
          }

          Transform itemFieldUITransform = storageObj.transform.Find("Seed");
          if (itemFieldUITransform == null)
          {
            if (DebugLogs.All || DebugLogs.Storage)
              MelonLogger.Error("StorageConfigPanel: itemFieldUITransform template not found");
            return null;
          }
          GameObject itemFieldUIObj = itemFieldUITransform.gameObject;
          itemFieldUIObj.name = "StorageItemUI";
          if (!itemFieldUIObj.GetComponent<CanvasRenderer>())
            itemFieldUIObj.AddComponent<CanvasRenderer>();
          TextMeshProUGUI titleText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>().FirstOrDefault(t => t.gameObject.name == "Title");
          if (titleText != null)
            titleText.text = "Assign Item";
          TextMeshProUGUI descText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>().FirstOrDefault(t => t.gameObject.name == "Description");
          if (descText != null)
            descText.text = "Select the item to assign to this shelf";
          else if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Warning("StorageConfigPanel: Description TextMeshProUGUI missing");

          ConfigPanelTemplate = configPanel;
          if (DebugLogs.All || DebugLogs.Storage)
            MelonLogger.Msg($"StorageConfigPanel: Template initialized successfully");
          return configPanel;
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel: Failed to initialize template, error: {e}");
        return null;
      }
    }
  }

  public class ConfigurableStorageEntity : PlaceableStorageEntity, IConfigurable
  {
    public EntityConfiguration Configuration => configuration;
    public StorageConfiguration configuration;
    public ConfigurationReplicator ConfigReplicator => configReplicator;
    public EConfigurableType ConfigurableType => (EConfigurableType)StorageExtensions.StorageEnum;
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
        configuration = new StorageConfiguration(configReplicator, this, this);

        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"ConfigurableStorageEntity: Initialized configuration for {name}");
      }
    }

    public void SetConfigurer(NetworkObject player)
    {
      CurrentPlayerConfigurer = player;
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"ConfigurableStorageEntity: Configurer set to {player?.name ?? "null"} for {name}");
    }

    public void Selected() => Configuration?.Selected();
    public void Deselected() => Configuration?.Deselected();

    public override void DestroyItem(bool callOnServer = true)
    {
      if (configuration != null)
      {
        configuration.Destroy();
        ParentProperty.RemoveConfigurable(this);
        StorageExtensions.StorageConfig.Remove(GUID);
      }
      DestroyWorldspaceUI();
      base.DestroyItem(callOnServer);
    }

    public override List<string> WriteData(string parentFolderPath)
    {
      try
      {
        List<string> list = base.WriteData(parentFolderPath);
        if (StorageExtensions.StorageConfig.TryGetValue(GUID, out var config) && config.ShouldSave())
        {
          list.Add("Configuration.json");
          File.WriteAllText(Path.Combine(parentFolderPath, "Configuration.json"), config.GetSaveString());
        }
        return list;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ConfigurableStorageEntityWriteDataPatch: Failed for {name ?? "null"}, error: {e}");
        return [];
      }
    }

    public WorldspaceUIElement CreateWorldspaceUI() => null;

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

      IEnumerator WaitForConfig()
      {
        yield return new WaitUntil(() => Configuration != null);
        Configuration.ReplicateAllFields(conn);
      }
    }
  }

  public class StorageConfiguration : EntityConfiguration
  {
    public ItemField StorageItem { get; private set; }
    public ConfigurableStorageEntity Storage { get; private set; }
    public event Action OnChanged;

    public StorageConfiguration(ConfigurationReplicator replicator, IConfigurable configurable, ConfigurableStorageEntity storage)
        : base(replicator, configurable)
    {
      Storage = storage;
      StorageItem = new ItemField(this)
      {
        CanSelectNone = true,
        Options = []
      };
      StorageItem.onItemChanged.AddListener(item => InvokeChanged());

      StorageExtensions.StorageConfig[storage.GUID] = this;
      if (DebugLogs.All || DebugLogs.Storage)
        MelonLogger.Msg($"StorageConfiguration: Created for storage: {storage.GUID}");
    }

    public override bool ShouldSave() => StorageItem != null;

    public override string GetSaveString()
    {
      try
      {
        JObject jsonObject = new()
        {
          ["StorageItem"] = JObject.FromObject(StorageItem.GetData())
        };
        string json = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfiguration: Saved JSON: {json}");
        return json;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfiguration: GetSaveString failed, error: {e}");
        return "";
      }
    }
  }

  public class StorageConfigPanel : ConfigPanel
  {
    private ItemFieldUI storageItemUI;

    public override void Bind(List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"StorageConfigPanel: Binding configs, count: {configs?.Count ?? 0}");

        if (this == null || gameObject == null)
        {
          MelonLogger.Error("StorageConfigPanel: Instance or GameObject is null");
          return;
        }

        List<ItemField> storageItemList = [];
        foreach (StorageConfiguration config in configs.OfType<StorageConfiguration>())
        {
          storageItemList.Add(config.StorageItem);
        }
        storageItemUI.Bind(storageItemList);
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageConfigPanel: Bind failed, error: {e}");
      }
    }
  }

  public class StoragePreset : Preset
  {
    private static StoragePreset DefaultPresetInstance;
    public ItemList StorageItem { get; set; }
    public string PresetDescription { get; set; } // New metadata field

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
        targetPreset.StorageItem = new ItemList(StorageItem.Name, new List<string>(StorageItem.OptionList), false, true)
        {
          All = StorageItem.All,
          None = StorageItem.None
        };
        targetPreset.PresetDescription = PresetDescription;
      }
    }

    public override void InitializeOptions()
    {
      StorageItem = new ItemList("Assign Item", [], true, true)
      {
        All = false,
        None = true
      };
      PresetDescription = "Configures the item stored in the rack.";
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
    public GenericOptionUI StorageItemUI { get; set; }
    private StoragePreset castedPreset;

    public override void Awake()
    {
      base.Awake();
      if (StorageItemUI != null && StorageItemUI.Button != null)
      {
        StorageItemUI.Button.onClick.AddListener(() => StorageItemUIClicked());
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
      try
      {
        if (castedPreset == null || castedPreset.StorageItem == null)
          return;
        if (StorageItemUI != null)
        {
          StorageItemUI.ValueLabel.text = castedPreset.StorageItem.GetDisplayString();
          var descField = GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name == "Description");
          if (descField != null)
            descField.text = castedPreset.PresetDescription ?? "";
          else
            MelonLogger.Warning("StoragePresetEditScreen: Description TextMeshProUGUI missing");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StoragePresetEditScreenUpdateUIPatch: Failed, error: {e}");
      }
    }

    private void StorageItemUIClicked()
    {
      if (castedPreset == null)
      {
        MelonLogger.Error("StoragePresetEditScreen: Null preset");
        return;
      }
      List<string> optionList = [];
      // todo: how to populate the option list?

      castedPreset.StorageItem.OptionList = optionList;
      Singleton<ItemSetterScreen>.Instance.Open(castedPreset.StorageItem);
    }
  }

  [HarmonyPatch(typeof(StorageRackLoader))]
  public class StorageRackLoaderLoadPatch
  {

    [HarmonyPostfix]
    [HarmonyPatch("Load", new Type[] { typeof(string) })]
    static void Postfix(StorageRackLoader __instance, string mainPath)
    {
      try
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"MixingStationLoaderPatch: Processing Postfix for mainPath: {mainPath}");
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          if (DebugLogs.All || DebugLogs.MixingStation)
            MelonLogger.Warning($"MixingStationLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem is ConfigurableStorageEntity storageEntity)
        {
          string configPath = Path.Combine(mainPath, "Configuration.json");
          if (File.Exists(configPath))
          {
            string json = File.ReadAllText(configPath);
            JObject jsonObject = JObject.Parse(json);
            JToken storageItemToken = jsonObject["StorageItem"];
            if (storageItemToken != null && StorageExtensions.StorageConfig.TryGetValue(storageEntity.GUID, out var config))
            {
              var storageItem = config.StorageItem;
              storageItem.Load(JsonUtility.FromJson<ItemFieldData>(storageItemToken.ToString()));
              if (DebugLogs.All || DebugLogs.Storage)
                MelonLogger.Msg($"StorageRackLoaderLoadPatch: Loaded StorageItem for {storageEntity.name}");
            }
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StorageRackLoaderLoadPatch: Failed for {mainPath}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(ConfigurableType), "GetTypeName")]
  public class ConfigurableTypeGetTypeNamePatch
  {
    static bool Prefix(EConfigurableType type, ref string __result)
    {
      if ((int)type == StorageExtensions.StorageEnum)
      {
        __result = "Storage Rack";
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"ConfigurableType GetTypeNamePrefix: Returned StorageConfigPanel for StorageEnum");
        return false;
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(ManagementInterface), "GetConfigPanelPrefab")]
  public class ManagementInterfaceGetConfigPanelPrefabPatch
  {
    static bool Prefix(EConfigurableType type, ref ConfigPanel __result)
    {
      if ((int)type == StorageExtensions.StorageEnum)
      {
        __result = StorageExtensions.GetStorageConfigPanelTemplate();
        if (DebugLogs.All || DebugLogs.Storage)
          MelonLogger.Msg($"ManagementInterface GetConfigPanelPrefabPatch: Returned StorageConfigPanel for StorageEnum");
        return false;
      }
      return true;
    }
  }
}