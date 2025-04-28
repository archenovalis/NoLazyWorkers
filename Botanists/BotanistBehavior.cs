using HarmonyLib;
using MelonLoader;
using ScheduleOne.Employees;
using ScheduleOne.Growing;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Quests;
using ScheduleOne.UI.Management;
using UnityEngine;

namespace NoLazyWorkers.Botanists
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior) { MelonLogger.Msg($"BotanistConfigPanelBindPatch: Processing configs, count: {configs?.Count ?? 0}"); }
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior) { MelonLogger.Msg("BotanistConfigPanelBindPatch: Hid SuppliesUI"); }

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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior) { MelonLogger.Msg($"BotanistConfigPanelBindPatch: Moved PotsUI to y={suppliesY}"); }
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
      try
      {
        if (__instance.AssignSuppliesEntry.State == EQuestState.Active)
        {
          foreach (Employee employee in __instance.GetEmployees())
          {
            Botanist botanist = employee as Botanist;
            if (botanist != null && botanist.Configuration is BotanistConfiguration botanistConfig)
            {
              foreach (Pot pot in botanistConfig.AssignedPots)
              {
                if (PotExtensions.Supply.TryGetValue(pot.GUID, out var potSupply) && potSupply != null)
                {
                  __instance.AssignSuppliesEntry.Complete();
                  if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior)
                  {
                    MelonLogger.Msg($"QuestBotanistsMinPassPatch: Completed AssignSuppliesEntry for botanist {botanist.name}, pot {pot.name}");
                  }
                  return true;
                }
              }
            }
          }
        }
        return true;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"QuestBotanistsMinPassPatch: Failed, error: {e}");
        return true;
      }
    }
  }

  [HarmonyPatch(typeof(BotanistConfiguration), "GetSaveString")]
  public static class BotanistConfigurationGetSaveStringPatch
  {
    [HarmonyPrefix]
    public static void Prefix(BotanistConfiguration __instance)
    {
      __instance.Supplies.SelectedObject = null; // Clear before serialization
    }
  }

  [HarmonyPatch(typeof(Botanist), "GetDryableInSupplies")]
  public static class BotanistGetDryableInSuppliesPatch //todo: add supply to racks then check each rack's supply shelf. otherwise, check shelves with product set matching dryable's product set
  {
    [HarmonyPrefix]
    public static bool Prefix(Botanist __instance, ref QualityItemInstance __result)
    {
      try
      {
        if (!(__instance.Configuration is BotanistConfiguration botanistConfig))
        {
          MelonLogger.Warning("BotanistGetDryableInSuppliesPatch: BotanistConfiguration is null");
          __result = null;
          return false;
        }

        foreach (Pot pot in botanistConfig.AssignedPots)
        {
          if (!PotExtensions.Supply.TryGetValue(pot.GUID, out var potSupply) || potSupply.SelectedObject == null)
          {
            continue;
          }
          botanistConfig.Supplies.SelectedObject = potSupply.SelectedObject;
          if (!__instance.Movement.CanGetTo(potSupply.SelectedObject as ITransitEntity))
          {
            continue;
          }

          List<ItemSlot> slots = [.. (potSupply.SelectedObject as ITransitEntity).OutputSlots];
          foreach (ItemSlot slot in slots)
          {
            if (slot.Quantity > 0 && ItemFilter_Dryable.IsItemDryable(slot.ItemInstance))
            {
              __result = slot.ItemInstance as QualityItemInstance;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior)
              {
                MelonLogger.Msg($"BotanistGetDryableInSuppliesPatch: Found dryable {__result?.ID ?? "null"} in pot {pot.name}'s supply");
              }
              return false;
            }
          }
        }

        __result = null;
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"BotanistGetDryableInSuppliesPatch: Failed, error: {e}");
        __result = null;
        return false;
      }
    }
  }

  [HarmonyPatch(typeof(PotActionBehaviour))]
  public static class PotActionBehaviourPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("Initialize")]
    public static bool InitializePrefix(PotActionBehaviour __instance, Pot pot, PotActionBehaviour.EActionType actionType)
    {
      if (PotExtensions.Supply.TryGetValue(pot.GUID, out var supply))
      {
        __instance.botanist.configuration.Supplies.SelectedObject = supply.SelectedObject;
        return true;
      }
      else
      {
        __instance.Disable();
        return false;
      }
    }
  }
}