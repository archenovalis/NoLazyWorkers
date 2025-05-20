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

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    private readonly Chemist _chemist;
    public ChemistBehaviour(Chemist chemist, IEmployeeAdapter employee) : base(chemist, employee)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      Employee = employee;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour: Initialized for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
    }
  }

  [HarmonyPatch(typeof(Chemist))]
  public class ChemistPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Awake")]
    public static void AwakePostfix(Chemist __instance)
    {
      try
      {
        if (__instance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "ChemistAwakePatch: Chemist is null", DebugLogger.Category.Chemist);
          return;
        }
        // Replace MoveItemBehaviour with AdvancedMoveItemBehaviour
        if (__instance.MoveItemBehaviour != null && !(__instance.MoveItemBehaviour is AdvancedMoveItemBehaviour))
        {
          __instance.MoveItemBehaviour = new AdvancedMoveItemBehaviour(__instance);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAwakePatch: Replaced MoveItemBehaviour with AdvancedMoveItemBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employeeAdapter))
        {
          employeeAdapter = new ChemistAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = employeeAdapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAwakePatch: Registered ChemistAdapter for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        if (!ActiveBehaviours.TryGetValue(__instance.GUID, out var behaviour))
        {
          behaviour = new ChemistBehaviour(__instance, employeeAdapter);
          ActiveBehaviours[__instance.GUID] = behaviour;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAwakePatch: Initialized ChemistBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistAwakePatch: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("UpdateBehaviour")]
    public static bool UpdateBehaviourPrefix(Chemist __instance)
    {
      try
      {
        if (!InstanceFinder.IsServer)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Skipping client-side for chemist={__instance?.fullName ?? "null"}", DebugLogger.Category.Chemist);
          return true;
        }
        var chemistBehaviour = ChemistUtilities.GetChemistBehaviour(__instance);
        if (chemistBehaviour == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefix: No ChemistBehaviour for {__instance.fullName}, initializing", DebugLogger.Category.Chemist);
          var adapter = new ChemistAdapter(__instance);
          chemistBehaviour = new ChemistBehaviour(__instance, adapter);
          ActiveBehaviours[__instance.GUID] = chemistBehaviour;
          EmployeeAdapters[__instance.GUID] = adapter;
        }
        if (__instance.Fired)
        {
          __instance.LeavePropertyAndDespawn();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Chemist {__instance.fullName} is fired, despawning", DebugLogger.Category.Chemist);
          return false;
        }
        if (!__instance.CanWork())
        {
          __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
          __instance.SetIdle(true);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Chemist {__instance.fullName} cannot work, setting idle", DebugLogger.Category.Chemist);
          return false;
        }
        if (!EmployeeBehaviour.States.ContainsKey(__instance.GUID))
        {
          EmployeeBehaviour.States[__instance.GUID] = new StateData();
        }
        var state = EmployeeBehaviour.States[__instance.GUID];
        chemistBehaviour.Update(__instance.MoveItemBehaviour);
        if (state.CurrentState != EState.Idle || state.ActiveRoutes.Count > 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Processing state={state.CurrentState}, routes={state.ActiveRoutes.Count} for {__instance.fullName}", DebugLogger.Category.Chemist);
          if (__instance.MoveItemBehaviour is AdvancedMoveItemBehaviour moveItemBehaviour)
          {
            moveItemBehaviour.Enable_Networked(null);
          }
          __instance.MarkIsWorking();
          return false;
        }
        if (__instance.configuration?.TotalStations > 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: No mod tasks, trying game tasks for {__instance.fullName}", DebugLogger.Category.Chemist);
          __instance.TryStartNewTask();
        }
        else
        {
          __instance.SubmitNoWorkReason("I haven't been assigned any stations", "You can use your management clipboards to assign stations to me.");
          __instance.SetIdle(true);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: No stations assigned for {__instance.fullName}, setting idle", DebugLogger.Category.Chemist);
        }
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefix: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
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
        if (ActiveBehaviours.TryGetValue(__instance.GUID, out var behaviour))
        {
          if (ActiveMoveItemBehaviours.TryGetValue(__instance.GUID, out var moveItemBehaviour))
          {
            behaviour.Disable(moveItemBehaviour);
          }
          ActiveBehaviours.Remove(__instance.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistFirePatch: Disabled ChemistBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        if (EmployeeAdapters.ContainsKey(__instance.GUID))
        {
          EmployeeAdapters.Remove(__instance.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistFirePatch: Removed ChemistAdapter for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        if (ActiveMoveItemBehaviours.ContainsKey(__instance.GUID))
        {
          ActiveMoveItemBehaviours.Remove(__instance.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistFirePatch: Removed AdvancedMoveItemBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistFirePatch: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
      }
    }
  }
}
