using HarmonyLib;
using MelonLoader;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;

namespace NoLazyWorkers_IL2CPP.Handlers
{
  public static class PackagerExtensions
  {

  }

  [HarmonyPatch(typeof(AdvancedTransitRoute))]
  public class ChemistPatch
  {
    [HarmonyPatch("GetItemReadyToMove")]
    [HarmonyPrefix]
    static bool GetItemReadyToMovePrefix(AdvancedTransitRoute __instance, ref ItemInstance __result)
    {
      // Ensure the destination is a MixingStation
      var station = __instance.Destination.TryCast<MixingStation>();
      if (station == null)
      {
        return true;
      }
      // Check if the station is in use, in operation, or has items in the output slot
      if (station.TryCast<IUsable>().IsInUse || station.CurrentMixOperation != null || station.OutputSlot.Quantity > 0)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPackagerBehavior)
          MelonLogger.Msg($"Handlers: GetItemReadyToMovePrefix: Skipping {station.GUID} - Station in use, in operation, or output slot not empty (IsInUse={station.TryCast<IUsable>().IsInUse}, CurrentMixOperation={station.CurrentMixOperation != null}, OutputQuantity={station.OutputSlot.Quantity})");
        __result = null;
        return false;
      }
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPackagerBehavior)
        MelonLogger.Msg($"Handlers: GetItemReadyToMovePrefix: resume");
      return true;
    }
  }
}