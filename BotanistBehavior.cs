using BepInEx.AssemblyPublicizer;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.Growing;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Quests;
using ScheduleOne.UI.Management;
using UnityEngine;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0.1", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{

  public static class BotanistExtensions
  {
  }

  [HarmonyPatch(typeof(BotanistConfigPanel), "Bind")]
  public class BotanistConfigPanelBindPatch
  {
    static void Postfix(BotanistConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"BotanistConfigPanelBindPatch: Processing configs, count: {configs?.Count ?? 0}"); }
        if (__instance == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: __instance is null");
          return;
        }

        // Verify UI components
        if (__instance.SuppliesUI == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: SuppliesUI is null");
          return;
        }
        if (__instance.PotsUI == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: PotsUI is null");
          return;
        }

        // Hide SuppliesUI
        __instance.SuppliesUI.gameObject.SetActive(false);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg("BotanistConfigPanelBindPatch: Hid SuppliesUI"); }

        // Move PotsUI to SuppliesUI's y-coordinate
        RectTransform suppliesRect = __instance.SuppliesUI.GetComponent<RectTransform>();
        RectTransform potsRect = __instance.PotsUI.GetComponent<RectTransform>();
        if (suppliesRect == null || potsRect == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: SuppliesUI or PotsUI RectTransform is null");
          return;
        }

        float suppliesY = suppliesRect.anchoredPosition.y;
        potsRect.anchoredPosition = new Vector2(potsRect.anchoredPosition.x, suppliesY);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"BotanistConfigPanelBindPatch: Moved PotsUI to y={suppliesY}"); }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"BotanistConfigPanelBindPatch: Failed, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(Quest_Botanists), "MinPass")]
  public static class QuestBotanistsMinPassPatch
  {
    [HarmonyPrefix]
    public static bool Prefix(Quest_Botanists __instance)
    {
      if (__instance.AssignSuppliesEntry.State == EQuestState.Active)
      {
        foreach (Employee employee in __instance.GetEmployees())
        {
          Botanist botanist = employee as Botanist;
          foreach (Pot pot in botanist.AssignedProperty.Container.GetComponentsInChildren<Pot>())
            if (ConfigurationExtensions.PotSupply[pot] != null)
            {
              __instance.AssignSuppliesEntry.Complete();
              return true;
            }
        }
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(PotActionBehaviour), "StartAction")]
  public class PotActionBehaviourStartActionPatch
  {
    static void Prefix(PotActionBehaviour __instance)
    {
      try
      {
        if (__instance.AssignedPot == null)
        {
          MelonLogger.Warning("PotActionBehaviourStartActionPatch: AssignedPot is null");
          return;
        }

        Botanist botanist = __instance.Npc as Botanist;
        if (botanist == null || !(botanist.Configuration is BotanistConfiguration botanistConfig))
        {
          MelonLogger.Warning("PotActionBehaviourStartActionPatch: Botanist or BotanistConfiguration is null");
          return;
        }

        if (!ConfigurationExtensions.PotSupply.TryGetValue(__instance.AssignedPot, out var potSupply))
        {
          MelonLogger.Warning("PotActionBehaviourStartActionPatch: Pot supply not found");
          return;
        }

        botanistConfig.Supplies.SelectedObject = potSupply.SelectedObject;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"PotActionBehaviourStartActionPatch: Set Botanist.Supplies to {potSupply.SelectedObject?.name ?? "null"} for pot {__instance.AssignedPot}"); }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotActionBehaviourStartActionPatch: Failed, error: {e}");
      }
    }
  }
}