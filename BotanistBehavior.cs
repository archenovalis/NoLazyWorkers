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
using ScheduleOne.UI.Management;
using UnityEngine;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0.1", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{

  public static class BotanistExtensions
  {
    public static ItemInstance GetItemInSupply(this Botanist botanist, Pot pot, string id)
    {
      PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
      ObjectField supply = ConfigurationExtensions.PotSupply[config];

      List<ItemSlot> list = new();
      BuildableItem supplyEntity = supply.SelectedObject;
      if (supplyEntity != null && botanist.behaviour.Npc.Movement.CanGetTo(supplyEntity as ITransitEntity, 1f))
      {
        list.AddRange((supplyEntity as ITransitEntity).OutputSlots);
        for (int i = 0; i < list.Count; i++)
        {
          if (list[i].Quantity > 0 && list[i].ItemInstance.ID.ToLower() == id.ToLower())
          {
            return list[i].ItemInstance;
          }
        }
      }
      return null;
    }

    public static ItemInstance GetSeedInSupply(this Botanist botanist, Pot pot)
    {
      PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
      ObjectField supply = ConfigurationExtensions.PotSupply[config];

      List<ItemSlot> list = new();
      BuildableItem supplyEntity = supply.SelectedObject;
      if (supplyEntity != null && botanist.behaviour.Npc.Movement.CanGetTo(supplyEntity as ITransitEntity, 1f))
      {
        list.AddRange((supplyEntity as ITransitEntity).OutputSlots);
        for (int i = 0; i < list.Count; i++)
        {
          if (list[i].Quantity > 0 && list[i].ItemInstance.Definition is SeedDefinition)
          {
            return list[i].ItemInstance;
          }
        }
      }
      return null;
    }
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

        if (!ConfigurationExtensions.PotConfig.TryGetValue(__instance.AssignedPot, out var potConfig) ||
            !ConfigurationExtensions.PotSupply.TryGetValue(potConfig, out var potSupply))
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