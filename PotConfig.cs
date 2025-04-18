using BepInEx.AssemblyPublicizer;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.UI.Management;
using UnityEngine;
using TMPro;


[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0.1", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{
  [HarmonyPatch(typeof(PotConfiguration))]
  public class PotConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(Pot) })]
    static void Postfix(PotConfiguration __instance, Pot pot)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs) MelonLogger.Msg($"PotConfigurationPatch: Initializing for pot: {pot?.name ?? "null"}");
        ObjectField supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = true
        };
        supply.onObjectChanged.RemoveAllListeners();
        supply.onObjectChanged.AddListener(item =>
        {
          try
          {
            ConfigurationExtensions.InvokeChanged(__instance);
            ConfigurationExtensions.SourceChanged(__instance, item); // Calls SourceChanged(BuildableItem)
          }
          catch (Exception e)
          {
            MelonLogger.Error($"PotConfigurationPatch: onObjectChanged failed for pot: {pot?.name ?? "null"}, error: {e}");
          }
        });
        // Ensure dictionary entries are created
        if (!ConfigurationExtensions.PotSupply.ContainsKey(__instance))
        {
          ConfigurationExtensions.PotSupply[__instance] = supply;
        }
        if (!ConfigurationExtensions.PotConfig.ContainsKey(pot))
        {
          ConfigurationExtensions.PotConfig[pot] = __instance;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs) MelonLogger.Msg($"PotConfigurationPatch: Registered supply and config for pot: {pot?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationPatch: Failed for pot: {pot?.name ?? "null"}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfiguration), "GetSaveString")]
  public class PotConfigurationGetSaveStringPatch
  {
    static void Postfix(PotConfiguration __instance, ref string __result)
    {
      try
      {
        ExtendedPotConfigurationData data = new(
            __instance.Seed.GetData(),
            __instance.Additive1.GetData(),
            __instance.Additive2.GetData(),
            __instance.Additive3.GetData(),
            __instance.Destination.GetData()
        );
        data.Supply = ConfigurationExtensions.PotSupply[__instance].GetData();
        __result = data.GetJson(true);
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationGetSaveStringPatch failed: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfiguration), "ShouldSave")]
  public class PotConfigurationShouldSavePatch
  {
    static void Postfix(PotConfiguration __instance, ref bool __result)
    {
      try
      {
        ObjectField supply = ConfigurationExtensions.PotSupply[__instance];
        __result |= supply.SelectedObject != null;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationShouldSavePatch failed: {e}");
      }
    }
  }

  [Serializable]
  public class ExtendedPotConfigurationData : PotConfigurationData
  {
    public ObjectFieldData Supply;

    public ExtendedPotConfigurationData(ItemFieldData seed, ItemFieldData additive1, ItemFieldData additive2, ItemFieldData additive3, ObjectFieldData destination)
        : base(seed, additive1, additive2, additive3, destination)
    {
      Supply = null;
    }
  }

  [HarmonyPatch(typeof(PotConfigPanel), "Bind")]
  public class PotConfigPanelBindPatch
  {
    static void Postfix(PotConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg($"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}"); }
        ObjectFieldUI destinationUI = __instance.DestinationUI;

        // Instantiate DestinationUI's GameObject to copy hierarchy
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(destinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg("PotConfigPanelBindPatch: Instantiated SupplyUI successfully"); }

        // Ensure CanvasRenderer and other components
        supplyUIObj.AddComponent<CanvasRenderer>();

        foreach (var child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
        {
          if (child.gameObject.name == "Title")
          {
            child.text = "Supplies";
          }
          else
          {
            if (child.gameObject.name == "Description")
            {
              child.gameObject.SetActive(false);
            }
          }
        }

        // Position DestinationUI: y = -340 (move -60 from cloned)
        RectTransform destinationRect = destinationUI.GetComponent<RectTransform>();
        destinationRect.anchoredPosition = new Vector2(destinationRect.anchoredPosition.x, -340f);

        // Bind supply data
        List<ObjectField> supplyList = new();
        foreach (EntityConfiguration config in configs)
        {
          if (config is PotConfiguration potConfig)
          {
            if (ConfigurationExtensions.PotSupply.TryGetValue(potConfig, out ObjectField supply))
            {
              supplyList.Add(supply);
              if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg($"PotConfigPanelBindPatch: Added supply for PotConfiguration, SelectedObject: {(supply.SelectedObject != null ? supply.SelectedObject.name : "null")}"); }
            }
            else
            {
              MelonLogger.Warning($"PotConfigPanelBindPatch: No supply found for PotConfiguration");
            }
          }
          else
          {
            MelonLogger.Warning($"PotConfigPanelBindPatch: Config is not a PotConfiguration, type: {config?.GetType().Name}");
          }
        }

        supplyUI.Bind(supplyList);
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationPatch: Failed, error: {e}");
      }
    }
  }
}