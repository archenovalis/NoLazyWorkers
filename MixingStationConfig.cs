using BepInEx.AssemblyPublicizer;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using TMPro;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{
  [HarmonyPatch(typeof(MixingStationConfiguration))]
  public class MixingStationConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation) })]
    static void Postfix(MixingStationConfiguration __instance, MixingStation station)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Initializing for station: {station?.ObjectId.ToString() ?? "null"}, configHash={__instance.GetHashCode()}");

        ObjectField Supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = true
        };
        Supply.onObjectChanged.RemoveAllListeners();
        Supply.onObjectChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"MixingStationConfigurationPatch: Supply changed for station {station?.ObjectId.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.ObjectId.ToString() ?? "null"}");
        });
        Supply.onObjectChanged.AddListener(item =>
        {
          try
          {
            ConfigurationExtensions.SourceChanged(__instance, item);
          }
          catch (Exception e)
          {
            MelonLogger.Error($"MixingStationConfigurationPatch: onObjectChanged failed for station: {station?.ObjectId.ToString() ?? "null"}, error: {e}");
          }
        });

        // Check for existing key to prevent overwrites
        if (ConfigurationExtensions.MixerSupply.ContainsKey(__instance))
        {
          MelonLogger.Warning($"MixingStationConfigurationPatch: MixerSupply already contains key for configHash={__instance.GetHashCode()}, station={station?.ObjectId.ToString() ?? "null"}. Overwriting.");
        }
        ConfigurationExtensions.MixerSupply[__instance] = Supply;

        if (ConfigurationExtensions.MixerConfig.ContainsKey(station))
        {
          MelonLogger.Warning($"MixingStationConfigurationPatch: MixerConfig already contains key for station={station?.ObjectId.ToString() ?? "null"}. Overwriting.");
        }
        ConfigurationExtensions.MixerConfig[station] = __instance;

        ItemField mixerItem = new(__instance)
        {
          CanSelectNone = true,
          Options = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients.Cast<ItemDefinition>().ToList()
        };
        mixerItem.onItemChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"MixingStationConfigurationPatch: MixerItem changed for station {station?.ObjectId.ToString() ?? "null"}, newItem={mixerItem.SelectedItem?.Name ?? "null"}");
        });

        if (ConfigurationExtensions.MixerItem.ContainsKey(__instance))
        {
          MelonLogger.Warning($"MixingStationConfigurationPatch: MixerItem already contains key for configHash={__instance.GetHashCode()}, station={station?.ObjectId.ToString() ?? "null"}. Overwriting.");
        }
        ConfigurationExtensions.MixerItem[__instance] = mixerItem;

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Registered supply, config, and mixer item for station: {station?.ObjectId.ToString() ?? "null"}, supplyHash={Supply.GetHashCode()}, mixerItemHash={mixerItem.GetHashCode()}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationPatch: Failed for station: {station?.ObjectId.ToString() ?? "null"}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration), "GetSaveString")]
  public class MixingStationConfigurationGetSaveStringPatch
  {
    static void Postfix(MixingStationConfiguration __instance, ref string __result)
    {
      try
      {
        ExtendedMixingStationConfigurationData data = new(
            __instance.Destination.GetData(),
            __instance.StartThrehold.GetData()
        );
        data.Supply = ConfigurationExtensions.MixerSupply[__instance].GetData();
        data.MixerItem = ConfigurationExtensions.MixerItem[__instance].GetData();
        __result = data.GetJson(true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationGetSaveStringPatch: Saved for configHash={__instance.GetHashCode()}, supply={data.Supply?.ToString() ?? "null"}, mixerItem={data.MixerItem?.ToString() ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationGetSaveStringPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration), "ShouldSave")]
  public class MixingStationConfigurationShouldSavePatch
  {
    static void Postfix(MixingStationConfiguration __instance, ref bool __result)
    {
      try
      {
        ObjectField supply = ConfigurationExtensions.MixerSupply[__instance];
        ItemField mixerItem = ConfigurationExtensions.MixerItem[__instance];
        __result |= supply.SelectedObject != null || mixerItem?.SelectedItem != null;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationShouldSavePatch failed: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
  public class MixingStationConfigPanelBindPatch
  {
    static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Binding configs, count: {configs?.Count ?? 0}");

        if (__instance == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: __instance is null");
          return;
        }

        ObjectFieldUI destinationUI = __instance.DestinationUI;
        if (destinationUI == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: DestinationUI is null");
          return;
        }
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: DestinationUI found");

        // Instantiate MixerItemUI
        GameObject template = NoLazyUtilities.GetItemFieldUITemplateFromPotConfigPanel();
        GameObject mixerItemUIObj = UnityEngine.Object.Instantiate(template, __instance.transform, false);
        mixerItemUIObj.name = "MixerItemUI";
        ItemFieldUI mixerItemUI = mixerItemUIObj.GetComponent<ItemFieldUI>();
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Instantiated MixerItemUI from PotConfigPanel prefab template");

        mixerItemUIObj.AddComponent<CanvasRenderer>();
        TextMeshProUGUI titleTextComponent = mixerItemUIObj.GetComponentsInChildren<TextMeshProUGUI>()
            .FirstOrDefault(t => t.gameObject.name == "Title");
        titleTextComponent.text = "Mixer";
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Set MixerItemUI Title to 'Mixer'");

        RectTransform mixerRect = mixerItemUIObj.GetComponent<RectTransform>();
        mixerRect.anchoredPosition = new Vector2(mixerRect.anchoredPosition.x, -135.76f);

        Button mixerButton = mixerItemUIObj.GetComponentsInChildren<Button>()
            .FirstOrDefault(b => b.name == "Selection");
        if (mixerButton == null)
        {
          MelonLogger.Error("MixingStationConfigPanelBindPatch: Selection Button not found in MixerItemUI");
          return;
        }
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Found Selection Button");

        // Instantiate SupplyUI
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(destinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Instantiated SupplyUI successfully");

        supplyUIObj.AddComponent<CanvasRenderer>();
        foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
        {
          if (child.gameObject.name == "Title")
            child.text = "Supplies";
          else if (child.gameObject.name == "Description")
            child.gameObject.SetActive(false);
        }

        // Position UI elements
        RectTransform destRect = destinationUI.GetComponent<RectTransform>();
        destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, -245.76f);
        RectTransform supplyRect = supplyUIObj.GetComponent<RectTransform>();
        supplyRect.anchoredPosition = new Vector2(supplyRect.anchoredPosition.x, -185.76f);

        // Bind data
        List<ObjectField> supplyList = new();
        List<ItemField> mixerItemList = new();

        foreach (EntityConfiguration config in configs)
        {
          if (config is MixingStationConfiguration mixConfig)
          {
            if (ConfigurationExtensions.MixerSupply.TryGetValue(mixConfig, out ObjectField supply))
            {
              supplyList.Add(supply);
              supplyUI.Bind(supplyList);
              // Add listener to clear highlights after selection
              supply.onObjectChanged.AddListener(item =>
              {
                ClearShelfHighlights();
                if (DebugConfig.EnableDebugLogs)
                  MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Supply selected, item={item?.ObjectId.ToString() ?? "null"}, cleared highlights");
              });
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Added supply, SelectedObject: {(supply.SelectedObject != null ? supply.SelectedObject.ObjectId : "null")}");
            }
            else
            {
              MelonLogger.Warning("MixingStationConfigPanelBindPatch: MixerSupply not found");
            }

            if (ConfigurationExtensions.MixerItem.TryGetValue(mixConfig, out ItemField mixerItem))
            {
              mixerItemList.Add(mixerItem);
              mixerItemUI.Bind(mixerItemList);
              if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Bound MixerItemUI, SelectedItem: {mixerItem.SelectedItem?.Name ?? "null"}");

              // Update ValueLabel
              var valueLabel = mixerItemUI.GetComponentsInChildren<TextMeshProUGUI>()
                  .FirstOrDefault(t => t.gameObject.name.Contains("Value"));
              if (valueLabel != null)
              {
                valueLabel.text = mixerItem.SelectedItem?.Name ?? "None";
                if (DebugConfig.EnableDebugLogs)
                  MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Set ValueLabel to: {valueLabel.text}");
              }
              else
              {
                MelonLogger.Warning("MixingStationConfigPanelBindPatch: ValueLabel not found");
              }
            }
            else
            {
              MelonLogger.Warning("MixingStationConfigPanelBindPatch: MixerItem not found");
            }
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Postfix failed, error: {e}");
      }
    }

    private static void ClearShelfHighlights() //todo: this did not clear the outline from the bugged shelf
    {
      try
      {
        foreach (var shelf in GameObject.FindObjectsOfType<PlaceableStorageEntity>())
        {
          shelf.HideOutline();
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Failed to clear shelf highlights, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration), "Destroy")]
  public class MixingStationConfigurationDestroyPatch
  {
    static void Postfix(MixingStationConfiguration __instance)
    {
      try
      {
        if (ConfigurationExtensions.MixerSupply.Remove(__instance))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Removed MixerSupply for configHash={__instance.GetHashCode()}");
        }
        if (ConfigurationExtensions.MixerItem.Remove(__instance))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Removed MixerItem for configHash={__instance.GetHashCode()}");
        }
        foreach (var pair in ConfigurationExtensions.MixerConfig.Where(p => p.Value == __instance).ToList())
        {
          ConfigurationExtensions.MixerConfig.Remove(pair.Key);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"MixingStationConfigurationDestroyPatch: Removed MixerConfig for station={pair.Key?.ObjectId.ToString() ?? "null"}");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }

  [Serializable]
  public class ExtendedMixingStationConfigurationData : MixingStationConfigurationData
  {
    public ObjectFieldData Supply;
    public ItemFieldData MixerItem;

    public ExtendedMixingStationConfigurationData(ObjectFieldData destination, NumberFieldData threshold)
        : base(destination, threshold)
    {
      Supply = null;
      MixerItem = null;
    }
  }

  public class MixingStationPreset : Preset
  {
    private static MixingStationPreset DefaultPresetInstance;

    public ItemList MixerItems { get; set; }

    public override Preset GetCopy()
    {
      var preset = new MixingStationPreset();
      CopyTo(preset);
      return preset;
    }

    public override void CopyTo(Preset other)
    {
      base.CopyTo(other);
      if (other is MixingStationPreset targetPreset)
      {
        MixerItems.CopyTo(targetPreset.MixerItems);
      }
    }

    public override void InitializeOptions()
    {
      MixerItems = new ItemList("Mixer", NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.ToArray().Select(item => item.ID).ToList(), true, true);
      MixerItems.All = true;
      MixerItems.None = true;
    }

    public static MixingStationPreset GetDefaultPreset()
    {
      if (DefaultPresetInstance == null)
      {
        DefaultPresetInstance = new MixingStationPreset
        {
          PresetName = "Default",
          ObjectType = (ManageableObjectType)100,
          PresetColor = new Color32(180, 180, 180, 255)
        };
        DefaultPresetInstance.InitializeOptions();
      }
      return DefaultPresetInstance;
    }

    public static MixingStationPreset GetNewBlankPreset()
    {
      MixingStationPreset preset = GetDefaultPreset().GetCopy() as MixingStationPreset;
      preset.PresetName = "New Preset";
      return preset;
    }
  }

  public class MixingStationPresetEditScreen : PresetEditScreen
  {
    public GenericOptionUI MixerItemUI { get; set; }
    private MixingStationPreset castedPreset { get; set; }

    public override void Awake()
    {
      base.Awake();
      MixerItemUI.Button.onClick.AddListener(new UnityAction(MixerItemUIClicked));
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
      castedPreset = (MixingStationPreset)EditedPreset;
      UpdateUI();
    }

    private void UpdateUI()
    {
      MixerItemUI.ValueLabel.text = castedPreset.MixerItems.GetDisplayString();
    }

    private void MixerItemUIClicked()
    {
      Singleton<ItemSetterScreen>.Instance.Open(castedPreset.MixerItems);
    }
  }

  [HarmonyPatch(typeof(Preset), "GetDefault")]
  public class PresetPatch
  {
    static bool Prefix(ManageableObjectType type, ref Preset __result)
    {
      if (type == (ManageableObjectType)100)
      {
        __result = MixingStationPreset.GetDefaultPreset();
        return false;
      }
      return true;
    }
  }
}