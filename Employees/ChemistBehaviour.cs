using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.Extensions;
using NoLazyWorkers.Storage;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Employees.Constants;
using static NoLazyWorkers.Storage.Constants;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.Extensions;
using NoLazyWorkers.Stations;
using FishNet.Managing.Object;
using FishNet.Object;
using Object = UnityEngine.Object;
using FishNet.Managing;
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.TaskService.TaskRegistry;
using static NoLazyWorkers.TaskService.Extensions;
using Unity.Collections;
using static NoLazyWorkers.Debug;

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    protected readonly Chemist _chemist;

    public ChemistBehaviour(Chemist chemist, IEmployeeAdapter adapter)
        : base(chemist, adapter)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      Log(Level.Info, $"ChemistBehaviour: Initialized for NPC {_chemist.fullName}", Category.Chemist);
    }

    [HarmonyPatch(typeof(Chemist))]
    public class ChemistPatch
    {
      [HarmonyPrefix]
      [HarmonyPatch("UpdateBehaviour")]
      public static bool UpdateBehaviourPrefix(Chemist __instance)
      {
        try
        {
          Log(Level.Verbose, $"UpdateBehaviourPrefix:NPC={__instance.fullName} at position={__instance.transform.position}", Category.Chemist);
          if (__instance == null)
          {
            Log(Level.Error, "UpdateBehaviourPrefix: Chemist instance is null", Category.Chemist);
            return false;
          }

          if (!IEmployees[__instance.AssignedProperty].TryGetValue(__instance.GUID, out var employeeAdapter))
          {
            // Check if this NPC is already pending adapter creation
            if (PendingAdapters.TryGetValue(__instance.GUID, out float requestTime))
            {
              // Check if 5 seconds have elapsed since first request
              float elapsed = Time.time - requestTime;
              if (elapsed < ADAPTER_DELAY_SECONDS)
              {
                Log(Level.Verbose,
                    $"UpdateBehaviourPrefix: Delaying adapter for NPC={__instance.fullName}, {ADAPTER_DELAY_SECONDS - elapsed:F2}s remaining",
                    Category.Chemist);
                return false;
              }

              // Delay elapsed, create adapter
              employeeAdapter = new ChemistAdapter(__instance);
              IEmployees[__instance.AssignedProperty][__instance.GUID] = employeeAdapter;
              PendingAdapters.Remove(__instance.GUID); // Cleanup
              Log(Level.Info,
                  $"UpdateBehaviourPrefix: Registered ChemistAdapter for NPC={__instance.fullName} after {elapsed:F2}s delay",
                  Category.Chemist);
            }
            else
            {
              // First request, record timestamp and skip behavior
              PendingAdapters[__instance.GUID] = Time.time;
              Log(Level.Info,
                  $"UpdateBehaviourPrefix: Initiated {ADAPTER_DELAY_SECONDS}s delay for NPC={__instance.fullName}",
                  Category.Chemist);
              return false;
            }
          }

          var state = GetState(__instance);

          if (__instance.Fired || (__instance.behaviour.activeBehaviour != null && __instance.behaviour.activeBehaviour != __instance.WaitOutside))
          {
            Log(Level.Verbose, $"UpdateBehaviourPrefix: Fired={__instance.Fired} or activeBehaviour={__instance.behaviour.activeBehaviour?.Name ?? "null"} for NPC={__instance.fullName}", Category.Chemist);
            return false;
          }

          bool noWork = false;
          bool needsPay = false;
          if (__instance.GetBed() == null)
          {
            Log(Level.Verbose, $"UpdateBehaviourPrefix: No bed assigned for NPC={__instance.fullName}", Category.Chemist);
            noWork = true;
            __instance.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
          }
          else if (NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay)
          {
            Log(Level.Verbose, $"UpdateBehaviourPrefix: End of day for NPC={__instance.fullName}", Category.Chemist);
            noWork = true;
            __instance.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
          }
          else if (!__instance.PaidForToday)
          {
            Log(Level.Verbose, $"UpdateBehaviourPrefix: Not paid for NPC={__instance.fullName}", Category.Chemist);
            if (__instance.IsPayAvailable())
            {
              needsPay = true;
            }
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

          if (FishNetExtensions.IsServer && needsPay && __instance.IsPayAvailable())
          {
            Log(Level.Verbose, $"UpdateBehaviourPrefix: Processing payment for NPC={__instance.fullName}", Category.Chemist);
            __instance.RemoveDailyWage();
            __instance.SetIsPaid();
          }

          if (!FishNetExtensions.IsServer)
          {
            Log(Level.Verbose, $"UpdateBehaviourPrefix: Client-side, skipping for NPC={__instance.fullName}", Category.Chemist);
            return false;
          }

          if (!__instance.CanWork())
          {
            __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
            __instance.SetIdle(true);
            state.CurrentState = EState.Idle;
            Log(Level.Verbose, $"UpdateBehaviourPrefix: Cannot work for NPC={__instance.fullName}", Category.Chemist);
            return false;
          }

          if (__instance.AnyWorkInProgress())
          {
            Log(Level.Warning, $"UpdateBehaviourPrefix: 1 NPC={__instance.fullName}", Category.Chemist);
            __instance.MarkIsWorking();
            return false;
          }
          Log(Level.Warning, $"UpdateBehaviourPrefix: 2 NPC={__instance.fullName}", Category.Chemist);
          state.AdvBehaviour.Update().GetAwaiter().GetResult();
          return false;
        }
        catch (Exception e)
        {
          Log(Level.Error, $"UpdateBehaviourPrefix: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", Category.Chemist);
          if (__instance != null)
            __instance.SetIdle(true);
          return false;
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch("Fire")]
      public static void FirePostfix(Chemist __instance)
      {
        try
        {
          GetState(__instance).AdvBehaviour.Disable().GetAwaiter().GetResult();
          Log(Level.Info, $"ChemistFirePatch: Disabled MixingStationBeh for NPC={__instance.fullName}", Category.Chemist);

          if (IEmployees[__instance.AssignedProperty].ContainsKey(__instance.GUID))
          {
            IEmployees[__instance.AssignedProperty].Remove(__instance.GUID);
            Log(Level.Info, $"ChemistFirePatch: Removed ChemistAdapter for NPC={__instance.fullName}", Category.Chemist);
          }
        }
        catch (Exception e)
        {
          Log(Level.Error, $"ChemistFirePatch: Failed for Chemist {__instance?.fullName ?? "null"}, error: {e}", Category.Chemist);
        }
      }
    }
  }
}
