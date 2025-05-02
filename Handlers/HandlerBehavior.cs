using HarmonyLib;
using MelonLoader;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;

namespace NoLazyWorkers.Handlers
{
  public static class PackagerExtensions
  {

  }

  [HarmonyPatch(typeof(AdvancedTransitRoute))]
  public class AdvancedTransitRoutePatch
  {
    [HarmonyPatch("GetItemReadyToMove")]
    [HarmonyPrefix]
    static bool GetItemReadyToMovePrefix(AdvancedTransitRoute __instance, ref ItemInstance __result)
    {
      // Ensure the destination is a MixingStation
      if (__instance.Destination is not MixingStation station)
      {
        return true;
      }

      // Check if the station is in use, in operation, or has items in the output slot
      if (((IUsable)station).IsInUse || station.CurrentMixOperation != null || station.OutputSlot.Quantity > 0)
      {
        if (DebugLogs.All || DebugLogs.Packager)
          MelonLogger.Msg($"Handlers: GetItemReadyToMovePrefix: Skipping {station.GUID} - Station in use, in operation, or output slot not empty (IsInUse={((IUsable)station).IsInUse}, CurrentMixOperation={station.CurrentMixOperation != null}, OutputQuantity={station.OutputSlot.Quantity})");
        __result = null;
        return false;
      }
      if (DebugLogs.All || DebugLogs.Packager)
        MelonLogger.Msg($"Handlers: GetItemReadyToMovePrefix: resume");
      return true;
    }
  }
}