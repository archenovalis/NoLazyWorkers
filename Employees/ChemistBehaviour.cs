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
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using NoLazyWorkers.Stations;
using static NoLazyWorkers.Employees.ChemistExtensions;
using NoLazyWorkers.General;
using FishNet.Managing.Object;
using FishNet.Object;
using Object = UnityEngine.Object;
using FishNet.Managing;
using NoLazyWorkers.Stations.NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    protected readonly Chemist _chemist;

    public ChemistBehaviour(Chemist chemist, IEmployeeAdapter adapter)
        : base(chemist, adapter, new List<IEmployeeTask>
        {
                new MixingStationBeh.Work_MixingStation(120, 0)
          // Add other tasks here in the future
        })
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour: Initialized for NPC {_chemist.fullName}", DebugLogger.Category.Chemist);
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
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix:NPC={__instance.fullName} at position={__instance.transform.position}", DebugLogger.Category.Chemist);
          if (__instance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, "UpdateBehaviourPrefix: Chemist instance is null", DebugLogger.Category.Chemist);
            return false;
          }

          if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employeeAdapter))
          {
            employeeAdapter = new ChemistAdapter(__instance);
            EmployeeAdapters[__instance.GUID] = employeeAdapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Registered ChemistAdapter for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
          }

          if (employeeAdapter.State == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UpdateBehaviourPrefix: employeeAdapter.State == null NPC={__instance.fullName}", DebugLogger.Category.Chemist);
          }
          var state = employeeAdapter.State;

          if (__instance.Fired || (__instance.behaviour.activeBehaviour != null && __instance.behaviour.activeBehaviour != __instance.WaitOutside))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Fired={__instance.Fired} or activeBehaviour={__instance.behaviour.activeBehaviour?.Name ?? "null"} for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            return false;
          }

          bool noWork = false;
          bool needsPay = false;
          if (__instance.GetBed() == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: No bed assigned for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            noWork = true;
            __instance.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
          }
          else if (NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: End of day for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            noWork = true;
            __instance.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
          }
          else if (!__instance.PaidForToday)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Not paid for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
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

          if (InstanceFinder.IsServer && needsPay && __instance.IsPayAvailable())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Processing payment for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            __instance.RemoveDailyWage();
            __instance.SetIsPaid();
          }

          if (!InstanceFinder.IsServer)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Client-side, skipping for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            return false;
          }

          if (!__instance.CanWork())
          {
            __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
            __instance.SetIdle(true);
            state.CurrentState = EState.Idle;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Cannot work for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            return false;
          }

          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UpdateBehaviourPrefix: 0 NPC={__instance.fullName}", DebugLogger.Category.Chemist);
          if (state.CurrentState != EState.Idle || __instance.AnyWorkInProgress())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UpdateBehaviourPrefix: 1 NPC={__instance.fullName}", DebugLogger.Category.Chemist);
            __instance.MarkIsWorking();
            return false;
          }
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UpdateBehaviourPrefix: 2 NPC={__instance.fullName}", DebugLogger.Category.Chemist);
          state.EmployeeBeh.Update();
          return false;
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefix: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
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
          var state = GetState(__instance);
          state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistFirePatch: Disabled MixingStationBeh for NPC={__instance.fullName}", DebugLogger.Category.Chemist);

          if (EmployeeAdapters.ContainsKey(__instance.GUID))
          {
            EmployeeAdapters.Remove(__instance.GUID);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistFirePatch: Removed ChemistAdapter for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
          }
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistFirePatch: Failed for Chemist {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
        }
      }
    }
  }
}
