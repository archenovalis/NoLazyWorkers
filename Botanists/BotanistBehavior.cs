using HarmonyLib;
using MelonLoader;
using ScheduleOne.Employees;
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
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"BotanistConfigPanelBindPatch: Processing configs, count: {configs?.Count ?? 0}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Settings);

        if (__instance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "BotanistConfigPanelBindPatch: __instance is null",
              DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
          return;
        }

        // Verify UI components
        if (__instance.SuppliesUI == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "BotanistConfigPanelBindPatch: SuppliesUI is null",
              DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
          return;
        }
        if (__instance.PotsUI == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "BotanistConfigPanelBindPatch: PotsUI is null",
              DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
          return;
        }

        // Hide SuppliesUI
        __instance.SuppliesUI.gameObject.SetActive(false);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "BotanistConfigPanelBindPatch: Hid SuppliesUI",
            DebugLogger.Category.Botanist, DebugLogger.Category.Settings);

        // Move PotsUI to SuppliesUI's y-coordinate
        RectTransform suppliesRect = __instance.SuppliesUI.GetComponent<RectTransform>();
        RectTransform potsRect = __instance.PotsUI.GetComponent<RectTransform>();
        if (suppliesRect == null || potsRect == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "BotanistConfigPanelBindPatch: SuppliesUI or PotsUI RectTransform is null",
              DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
          return;
        }

        float suppliesY = suppliesRect.anchoredPosition.y;
        potsRect.anchoredPosition = new Vector2(potsRect.anchoredPosition.x, suppliesY);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"BotanistConfigPanelBindPatch: Moved PotsUI to y={suppliesY}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"BotanistConfigPanelBindPatch: Failed, error: {e}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
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
                  DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"QuestBotanistsMinPassPatch: Completed AssignSuppliesEntry for botanist {botanist.name}, pot {pot.name}",
                      DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
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
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"QuestBotanistsMinPassPatch: Failed, error: {e}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
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
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"BotanistConfigurationGetSaveStringPatch: Cleared Supplies.SelectedObject for serialization",
          DebugLogger.Category.Botanist, DebugLogger.Category.Settings);
    }
  }

  [HarmonyPatch(typeof(Botanist), "GetDryableInSupplies")]
  public static class BotanistGetDryableInSuppliesPatch
  {
    [HarmonyPrefix]
    public static bool Prefix(Botanist __instance, ref QualityItemInstance __result)
    {
      try
      {
        if (!(__instance.Configuration is BotanistConfiguration botanistConfig))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              "BotanistGetDryableInSuppliesPatch: BotanistConfiguration is null",
              DebugLogger.Category.Botanist);
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
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"BotanistGetDryableInSuppliesPatch: Found dryable {__result?.ID ?? "null"} in pot {pot.name}'s supply",
                  DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
              return false;
            }
          }
        }

        __result = null;
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"BotanistGetDryableInSuppliesPatch: Failed, error: {e}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
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
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PotActionBehaviourPatch InitializePrefix: Found supply {supply.SelectedObject.GUID} for {__instance.botanist.fullName}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
        __instance.botanist.configuration.Supplies.SelectedObject = supply.SelectedObject;
        return true;
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"PotActionBehaviourPatch InitializePrefix: Pot {pot.GUID} does not have a supply for {__instance.botanist.fullName}",
            DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
        __instance.Disable();
        return false;
      }
    }
  }
}