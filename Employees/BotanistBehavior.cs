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
using static NoLazyWorkers.Debug;

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
        Log(Level.Info,
            $"BotanistConfigPanelBindPatch: Processing configs, count: {configs?.Count ?? 0}",
            Category.Botanist, Category.Botanist);

        if (__instance == null)
        {
          Log(Level.Error,
              "BotanistConfigPanelBindPatch: __instance is null",
              Category.Botanist, Category.Botanist);
          return;
        }

        // Verify UI components
        if (__instance.SuppliesUI == null)
        {
          Log(Level.Error,
              "BotanistConfigPanelBindPatch: SuppliesUI is null",
              Category.Botanist, Category.Botanist);
          return;
        }
        if (__instance.PotsUI == null)
        {
          Log(Level.Error,
              "BotanistConfigPanelBindPatch: PotsUI is null",
              Category.Botanist, Category.Botanist);
          return;
        }

        // Hide SuppliesUI
        __instance.SuppliesUI.gameObject.SetActive(false);
        Log(Level.Info,
            "BotanistConfigPanelBindPatch: Hid SuppliesUI",
            Category.Botanist, Category.Botanist);

        // Move PotsUI to SuppliesUI's y-coordinate
        RectTransform suppliesRect = __instance.SuppliesUI.GetComponent<RectTransform>();
        RectTransform potsRect = __instance.PotsUI.GetComponent<RectTransform>();
        if (suppliesRect == null || potsRect == null)
        {
          Log(Level.Error,
              "BotanistConfigPanelBindPatch: SuppliesUI or PotsUI RectTransform is null",
              Category.Botanist, Category.Botanist);
          return;
        }

        float suppliesY = suppliesRect.anchoredPosition.y;
        potsRect.anchoredPosition = new Vector2(potsRect.anchoredPosition.x, suppliesY);
        Log(Level.Info,
            $"BotanistConfigPanelBindPatch: Moved PotsUI to y={suppliesY}",
            Category.Botanist, Category.Botanist);
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"BotanistConfigPanelBindPatch: Failed, error: {e}",
            Category.Botanist, Category.Botanist);
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
                  Log(Level.Info,
                      $"QuestBotanistsMinPassPatch: Completed AssignSuppliesEntry for botanist {botanist.name}, pot {pot.name}",
                      Category.Botanist, Category.Pot);
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
        Log(Level.Error,
            $"QuestBotanistsMinPassPatch: Failed, error: {e}",
            Category.Botanist, Category.Pot);
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
      Log(Level.Verbose,
          $"BotanistConfigurationGetSaveStringPatch: Cleared Supplies.SelectedObject for serialization",
          Category.Botanist, Category.Botanist);
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
          Log(Level.Warning,
              "BotanistGetDryableInSuppliesPatch: BotanistConfiguration is null",
              Category.Botanist);
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
              Log(Level.Info,
                  $"BotanistGetDryableInSuppliesPatch: Found dryable {__result?.ID ?? "null"} in pot {pot.name}'s supply",
                  Category.Botanist, Category.Pot);
              return false;
            }
          }
        }

        __result = null;
        return false;
      }
      catch (Exception e)
      {
        Log(Level.Error,
            $"BotanistGetDryableInSuppliesPatch: Failed, error: {e}",
            Category.Botanist, Category.Pot);
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
        Log(Level.Info,
            $"PotActionBehaviourPatch InitializePrefix: Found supply {supply.SelectedObject.GUID} for {__instance.botanist.fullName}",
            Category.Botanist, Category.Pot);
        __instance.botanist.configuration.Supplies.SelectedObject = supply.SelectedObject;
        return true;
      }
      else
      {
        Log(Level.Warning,
            $"PotActionBehaviourPatch InitializePrefix: Pot {pot.GUID} does not have a supply for {__instance.botanist.fullName}",
            Category.Botanist, Category.Pot);
        __instance.Disable();
        return false;
      }
    }
  }
}