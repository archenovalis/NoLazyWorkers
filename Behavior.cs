using HarmonyLib;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.Growing;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using System.Reflection;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{
  [HarmonyPatch(typeof(PotActionBehaviour), "DoesBotanistHaveMaterialsForTask")]
  public class DoesBotanistHaveMaterialsForTaskPatch
  {
    private static string[] GetRequiredItemIDs(PotActionBehaviour __instance, PotActionBehaviour.EActionType actionType, Pot pot)
    {
      PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
      switch (actionType)
      {
        case PotActionBehaviour.EActionType.PourSoil:
          return new string[] { "soil", "longlifesoil", "extralonglifesoil" };
        case PotActionBehaviour.EActionType.SowSeed:
          if (config.Seed.SelectedItem == null)
            return Singleton<Registry>.Instance.Seeds.ConvertAll<string>(x => x.ID).ToArray();
          return new string[] { config.Seed.SelectedItem.ID };
        case PotActionBehaviour.EActionType.ApplyAdditive:
          if (__instance.AdditiveNumber == 1)
            return new string[] { config.Additive1.SelectedItem.ID };
          if (__instance.AdditiveNumber == 2)
            return new string[] { config.Additive2.SelectedItem.ID };
          if (__instance.AdditiveNumber == 3)
            return new string[] { config.Additive3.SelectedItem.ID };
          break;
      }
      return new string[0];
    }

    static bool Prefix(PotActionBehaviour __instance, Botanist botanist, Pot pot, PotActionBehaviour.EActionType actionType, int additiveNumber, ref bool __result)
    {
      switch (actionType)
      {
        case PotActionBehaviour.EActionType.PourSoil:
          __result = botanist.GetItemInSupply(pot, "soil") != null ||
                      botanist.GetItemInSupply(pot, "longlifesoil") != null ||
                      botanist.GetItemInSupply(pot, "extralonglifesoil") != null ||
                      botanist.Inventory.GetMaxItemCount(GetRequiredItemIDs(__instance, actionType, pot)) > 0;
          return false;

        case PotActionBehaviour.EActionType.SowSeed:
          __result = botanist.GetSeedInSupply(pot) != null ||
                      botanist.Inventory.GetMaxItemCount(GetRequiredItemIDs(__instance, actionType, pot)) > 0;
          return false;

        case PotActionBehaviour.EActionType.ApplyAdditive:
          var additiveId = GetAdditiveId(pot, additiveNumber);
          __result = additiveId != null && botanist.GetItemInSupply(pot, additiveId) != null ||
                      botanist.Inventory.GetMaxItemCount(GetRequiredItemIDs(__instance, actionType, pot)) > 0;
          return false;

        default:
          return true; // Let original method handle other cases
      }
    }

    static string GetAdditiveId(Pot pot, int additiveNumber)
    {
      PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
      return additiveNumber switch
      {
        1 => config.Additive1?.SelectedItem?.ID,
        2 => config.Additive2?.SelectedItem?.ID,
        3 => config.Additive3?.SelectedItem?.ID,
        _ => null
      };
    }
  }

  [HarmonyPatch(typeof(StartMixingStationBehaviour), "ActiveMinPass")]
  public class StartMixingStationBehaviourPatch
  {
    private static readonly FieldInfo startRoutineField = typeof(StartMixingStationBehaviour)
        .GetField("startRoutine", BindingFlags.NonPublic | BindingFlags.Instance);

    static bool Prefix(StartMixingStationBehaviour __instance)
    {
      // Check if startRoutine is not null using reflection
      if (startRoutineField.GetValue(__instance) != null)
        return true; // Cooking is in progress, let original logic handle it

      Chemist chemist = __instance.Npc as Chemist;
      MixingStation station = __instance.targetStation;
      if (station == null)
      {
        __instance.Disable();
        return false; // Skip original logic
      }

      // Get the mixer item from the station's configuration
      MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
      ItemField mixerItem = ConfigurationExtensions.MixerItem[config];
      if (mixerItem?.SelectedItem == null)
      {
        __instance.Disable();
        return false; // Skip original logic
      }

      // Check if the chemist can reach the supply
      if (!chemist.behaviour.Npc.Movement.CanGetTo(station, 1f))
      {
        Debug.LogWarning("Chemist cannot reach supply for mixing station.");
        __instance.Disable();
        return false; // Skip original logic
      }

      // Check if the required mixer item is in the supply
      if (chemist.GetItemInSupply(station, mixerItem.SelectedItem.ID) == null)
      {
        Debug.LogWarning("Required mixer item not found in supply.");
        __instance.Disable();
        return false; // Skip original logic
      }

      return true; // Proceed with original logic
    }
  }
}