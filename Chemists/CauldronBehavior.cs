using HarmonyLib;
using MelonLoader;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using Registry = ScheduleOne.Registry;

using static NoLazyWorkers.Handlers.StorageUtilities;
using FishNet;
using static NoLazyWorkers.Chemists.ChemistBehaviour;

namespace NoLazyWorkers.Chemists
{
  [HarmonyPatch(typeof(Chemist))]
  public class CauldronChemistPatch
  {
    // Patch GetCauldronsReadyToStart to consider fetchable ingredients
    [HarmonyPatch("GetCauldronsReadyToStart")]
    [HarmonyPrefix]
    static bool GetCauldronsReadyToStartPrefix(Chemist __instance, ref List<Cauldron> __result)
    {
      try
      {
        List<Cauldron> list = new();
        foreach (Cauldron cauldron in __instance.configuration.Cauldrons)
        {
          if (!((IUsable)cauldron).IsInUse && cauldron.RemainingCookTime <= 0 && cauldron.GetState() == Cauldron.EState.Ready)
          {
            list.Add(cauldron);
            continue;
          }

          int gasolineNeeded = cauldron.LiquidSlot.Quantity < 1 ? 1 : 0;
          int cocaLeafNeeded = Cauldron.COCA_LEAF_REQUIRED - cauldron.IngredientSlots.Sum(slot => slot.Quantity);
          if (CanSourceIngredients(__instance, gasolineNeeded, cocaLeafNeeded))
            list.Add(cauldron);
        }

        __result = list;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.GetCauldronsReadyToStart: Found {list.Count} cauldrons for {__instance?.fullName ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.GetCauldronsReadyToStart: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}");
        __result = new List<Cauldron>();
        return false;
      }
    }

    // Patch StartCauldron to handle ingredient fetching
    [HarmonyPatch("StartCauldron")]
    [HarmonyPrefix]
    static bool StartCauldronPrefix(Chemist __instance, Cauldron cauldron)
    {
      if (!InstanceFinder.IsServer)
        return false;

      try
      {
        var behaviour = __instance.StartCauldronBehaviour;
        if (!states.TryGetValue(behaviour, out var state))
        {
          state = new StateData { CurrentState = EState.Idle };
          states[behaviour] = state;
        }

        if (state.CurrentState != EState.Idle)
          return false;

        int gasolineNeeded = cauldron.LiquidSlot.Quantity < 1 ? 1 : 0;
        int cocaLeafNeeded = Cauldron.COCA_LEAF_REQUIRED - cauldron.IngredientSlots.Sum(slot => slot.Quantity);
        state.TargetItem = gasolineNeeded > 0 ? Registry.Instance._GetItem("gasoline").GetDefaultInstance() :
                          cocaLeafNeeded > 0 ? Registry.Instance._GetItem("cocaleaf").GetDefaultInstance() : null;
        state.QuantityToFetch = gasolineNeeded > 0 ? gasolineNeeded : cocaLeafNeeded;

        if (state.TargetItem == null)
        {
          InsertItemsFromInventory(behaviour, state);
          state.CurrentState = EState.Cooking;
          behaviour.StartWork();
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"ChemistPatch.StartCauldron: Starting cook for {__instance?.fullName ?? "null"}, cauldron={cauldron.GUID}");
          return false;
        }

        PrepareToFetchItems(behaviour, state);
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.StartCauldron: Failed for chemist: {__instance?.fullName ?? "null"}, cauldron: {cauldron?.GUID}, error: {e}");
        Disable(__instance.StartCauldronBehaviour);
        return false;
      }
    }

    // Patch GetCauldronsReadyToMove to use FindShelfForDelivery
    [HarmonyPatch("GetCauldronsReadyToMove")]
    [HarmonyPrefix]
    static bool GetCauldronsReadyToMovePrefix(Chemist __instance, ref List<Cauldron> __result)
    {
      try
      {
        List<Cauldron> list = new();
        foreach (Cauldron cauldron in __instance.configuration.Cauldrons)
        {
          ItemSlot outputSlot = cauldron.OutputSlot;
          if (outputSlot.Quantity > 0 && FindShelfForDelivery(__instance, outputSlot.ItemInstance) != null)
            list.Add(cauldron);
        }

        __result = list;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.GetCauldronsReadyToMove: Found {list.Count} cauldrons for {__instance?.fullName ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.GetCauldronsReadyToMove: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}");
        __result = new List<Cauldron>();
        return false;
      }
    }

    private static bool CanSourceIngredients(Chemist chemist, int gasolineNeeded, int cocaLeafNeeded)
    {
      int gasolineInInventory = chemist.Inventory._GetItemAmount("gasoline");
      int cocaLeafInInventory = chemist.Inventory._GetItemAmount("cocaleaf");
      return (gasolineNeeded <= gasolineInInventory || FindShelfWithItem(chemist, Registry.Instance._GetItem("gasoline").GetDefaultInstance(), gasolineNeeded - gasolineInInventory) != null) &&
             (cocaLeafNeeded <= cocaLeafInInventory || FindShelfWithItem(chemist, Registry.Instance._GetItem("cocaleaf").GetDefaultInstance(), cocaLeafNeeded - cocaLeafInInventory) != null);
    }
  }
}