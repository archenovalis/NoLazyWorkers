using HarmonyLib;
using MelonLoader;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Events;
using ScheduleOne.EntityFramework;


[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
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
        TransitRoute SourceRoute = ConfigurationExtensions.PotSourceRoute.TryGetValue(__instance, out var route) ? route : null;
        ObjectField Supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = true
        };
        Supply.onObjectChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
          //ConfigurationExtensions.SourceChanged(__instance, SourceRoute, Supply, pot);
        });
        Supply.onObjectChanged.AddListener(new UnityAction<BuildableItem>(item => __instance.SourceChanged(item)));

        ConfigurationExtensions.PotSupply[__instance] = Supply;
        ConfigurationExtensions.PotConfig[pot] = __instance;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationPatch failed: {e}");
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
        __result |= supply?.SelectedObject != null;
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
        MelonLogger.Msg($"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}");
        ObjectFieldUI destinationUI = __instance.DestinationUI;

        // Instantiate DestinationUI's GameObject to copy hierarchy
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(destinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        MelonLogger.Msg("PotConfigPanelBindPatch: Instantiated SupplyUI successfully");

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
              MelonLogger.Msg($"PotConfigPanelBindPatch: Added supply for PotConfiguration, SelectedObject: {(supply.SelectedObject != null ? supply.SelectedObject.ToString() : "null")}");
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
        MelonLogger.Error($"PotConfigPanelBindPatch: Postfix failed, error: {e}");
      }
    }
  }
}