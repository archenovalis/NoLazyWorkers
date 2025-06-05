using FishNet;
using HarmonyLib;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Employees.PackagerExtensions;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Constants;
using NoLazyWorkers.Stations;
using static NoLazyWorkers.Storage.Utilities;
using NoLazyWorkers.Employees.Tasks.Packagers;
using UnityEngine;

namespace NoLazyWorkers.Employees
{
  public class PackagerBehaviour : EmployeeBehaviour
  {
    private readonly Packager _packager;

    public PackagerBehaviour(Packager packager, IEmployeeAdapter adapter)
        : base(packager, adapter)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour: Initialized for NPC {_packager.fullName}", DebugLogger.Category.Handler);
    }

    [HarmonyPatch(typeof(Packager))]
    public static class PackagerPatch
    {
      [HarmonyPrefix]
      [HarmonyPatch("UpdateBehaviour")]
      public static bool UpdateBehaviourPrefix(Packager __instance)
      {
        try
        {
          if (__instance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, "UpdateBehaviourPrefix: Packager instance is null", DebugLogger.Category.Handler);
            return false;
          }

          if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employeeAdapter))
          {
            // Check if this NPC is already pending adapter creation
            if (PendingAdapters.TryGetValue(__instance.GUID, out float requestTime))
            {
              // Check if 5 seconds have elapsed since first request
              float elapsed = Time.time - requestTime;
              if (elapsed < ADAPTER_DELAY_SECONDS)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"UpdateBehaviourPrefix: Delaying adapter for NPC={__instance.fullName}, {ADAPTER_DELAY_SECONDS - elapsed:F2}s remaining",
                    DebugLogger.Category.Chemist);
                return false;
              }

              // Delay elapsed, create adapter
              employeeAdapter = new PackagerAdapter(__instance);
              EmployeeAdapters[__instance.GUID] = employeeAdapter;
              PendingAdapters.Remove(__instance.GUID); // Cleanup
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"UpdateBehaviourPrefix: Registered ChemistAdapter for NPC={__instance.fullName} after {elapsed:F2}s delay",
                  DebugLogger.Category.Chemist);
            }
            else
            {
              // First request, record timestamp and skip behavior
              PendingAdapters[__instance.GUID] = Time.time;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"UpdateBehaviourPrefix: Initiated {ADAPTER_DELAY_SECONDS}s delay for NPC={__instance.fullName}",
                  DebugLogger.Category.Chemist);
              return false;
            }
          }

          var state = GetState(__instance);
          if (__instance.Fired || (__instance.behaviour.activeBehaviour != null && __instance.behaviour.activeBehaviour != __instance.WaitOutside))
          {
            return false;
          }

          bool noWork = false;
          bool needsPay = false;
          if (__instance.GetBed() == null)
          {
            noWork = true;
            __instance.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
          }
          else if (NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay)
          {
            noWork = true;
            __instance.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
          }
          else if (!__instance.PaidForToday)
          {
            if (__instance.IsPayAvailable())
              needsPay = true;
            else
            {
              noWork = true;
              __instance.SubmitNoWorkReason("I haven't been paid yet", "You can place cash in my briefcase on my bed.");
            }
          }

          if (noWork)
          {
            __instance.SetWaitOutside(true);
            state.CurrentState = EState.Idle;
            return false;
          }

          if (InstanceFinder.IsServer && needsPay && __instance.IsPayAvailable())
          {
            __instance.RemoveDailyWage();
            __instance.SetIsPaid();
          }

          if (!InstanceFinder.IsServer)
            return false;

          if (!__instance.CanWork())
          {
            __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
            __instance.SetIdle(true);
            state.CurrentState = EState.Idle;
            return false;
          }

          if (state.CurrentState != EState.Idle || __instance.PackagingBehaviour.Active)
          {
            __instance.MarkIsWorking();
            return false;
          }
          state.EmployeeBeh.Update().GetAwaiter().GetResult();
          return false;
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefix: Failed for packager {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Handler);
          __instance?.SetIdle(true);
          return false;
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch("Fire")]
      public static void FirePostfix(Packager __instance)
      {
        try
        {
          GetState(__instance).EmployeeBeh.Disable().GetAwaiter().GetResult();
          EmployeeAdapters.Remove(__instance.GUID);
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerFirePatch: Failed for Packager {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Handler);
        }
      }
    }
  }
}