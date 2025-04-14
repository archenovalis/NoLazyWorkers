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
using ScheduleOne.EntityFramework;

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
        TransitRoute SourceRoute = ConfigurationExtensions.MixerSourceRoute.TryGetValue(__instance, out var route) ? route : null;
        ObjectField Supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = true
        };
        Supply.onObjectChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
        });
        Supply.onObjectChanged.AddListener(new UnityAction<BuildableItem>(item => __instance.SourceChanged(item)));

        ConfigurationExtensions.MixerSupply[__instance] = Supply;
        ConfigurationExtensions.MixerConfig[station] = __instance;
        ItemField mixerItem = new(__instance)
        {
          CanSelectNone = true,
          Options = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients.Cast<ItemDefinition>().ToList()
        };
        mixerItem.onItemChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
        });
        ConfigurationExtensions.MixerItem[__instance] = mixerItem;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationPatch failed: {e}");
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
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationGetSaveStringPatch failed: {e}");
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
        __result |= supply?.SelectedObject != null || mixerItem?.SelectedItem != null;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationShouldSavePatch failed: {e}");
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
      if (MixerItemUI?.ValueLabel != null && castedPreset?.MixerItems != null)
      {
        MixerItemUI.ValueLabel.text = castedPreset.MixerItems.GetDisplayString();
      }
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


  [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
  public class MixingStationConfigPanelBindPatch
  {
    static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}");
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
        MelonLogger.Msg("MixingStationConfigPanelBindPatch: DestinationUI found");

        ItemFieldUI mixerItemUI = null;
        GameObject mixerItemUIObj = null;

        // Try to get ItemFieldUI template from PotConfigPanel prefab
        GameObject template = NoLazyUtilities.GetItemFieldUITemplateFromPotConfigPanel();
        if (template != null)
        {
          mixerItemUIObj = UnityEngine.Object.Instantiate(template, __instance.transform, false);
          mixerItemUIObj.name = "MixerItemUI";
          mixerItemUI = mixerItemUIObj.GetComponent<ItemFieldUI>();
          MelonLogger.Msg("MixingStationConfigPanelBindPatch: Instantiated MixerItemUI from PotConfigPanel prefab template");
        }
        // Configure MixerItemUI
        mixerItemUIObj.AddComponent<CanvasRenderer>();
        foreach (TextMeshProUGUI child in mixerItemUIObj.GetComponentsInChildren<TextMeshProUGUI>())
        {
          if (child.gameObject.name == "Title")
          {
            child.text = "Mixer";
            MelonLogger.Msg("MixingStationConfigPanelBindPatch: Set MixerItemUI Title to 'Mixer'");
            break;
          }
        }
        RectTransform mixerRect = mixerItemUIObj.GetComponent<RectTransform>();
        mixerRect.anchoredPosition = new Vector2(mixerRect.anchoredPosition.x, -135.76f);

        // Clone SupplyUI from DestinationUI
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(destinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
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
            }
            if (ConfigurationExtensions.MixerItem.TryGetValue(mixConfig, out ItemField mixerItem))
              if (mixerItem?.SelectedItem != null)
              {
                mixerItemList.Add(mixerItem);
                mixerItemUI.Bind(mixerItemList);
              }
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigPanelBindPatch: Postfix failed, error: {e}");
      }
    }
  }
}