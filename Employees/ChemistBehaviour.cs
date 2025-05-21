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

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    public readonly Chemist _chemist;
    public ChemistBehaviour(Chemist chemist, IEmployeeAdapter employee) : base(chemist, employee)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      Employee = employee ?? throw new ArgumentNullException(nameof(employee));
      if (Npc == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistBehaviour: Npc is null after base constructor for chemist {chemist.fullName}", DebugLogger.Category.Chemist);
        Npc = chemist;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour: Initialized for NPC {chemist.fullName}, Npc={Npc?.fullName ?? "null"}", DebugLogger.Category.Chemist);
    }
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
        if (__instance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "UpdateBehaviourPrefix: Chemist instance is null", DebugLogger.Category.Chemist);
          return false;
        }
        if (__instance.MoveItemBehaviour is not AdvancedMoveItemBehaviour)
        {
          Object.Destroy(__instance.MoveItemBehaviour);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Removed existing MoveItemBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
          var advancedBehaviour = __instance.gameObject.AddComponent<AdvancedMoveItemBehaviour>();
          advancedBehaviour.Name = "Advanced Move items";
          advancedBehaviour.Priority = 4;
          advancedBehaviour.EnabledOnAwake = false;
          var networkObject = __instance.gameObject.GetComponent<NetworkObject>();
          ManagedObjects.InitializePrefab(networkObject, -1);
          advancedBehaviour._transportManagerCache = __instance.NetworkManager.TransportManager;
          __instance.MoveItemBehaviour = advancedBehaviour;
          (__instance.MoveItemBehaviour as AdvancedMoveItemBehaviour).employee = __instance;
          __instance.behaviour.behaviourStack.Add(advancedBehaviour);
          __instance.MoveItemBehaviour.Awake();
          __instance.MoveItemBehaviour.beh = __instance.behaviour;
          __instance.MoveItemBehaviour.beh.Npc = __instance;
          ActiveMoveItemBehaviours[__instance.GUID] = __instance.MoveItemBehaviour;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Initialized for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employeeAdapter))
        {
          employeeAdapter = new ChemistAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = employeeAdapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Registered ChemistAdapter for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        if (!ActiveBehaviours.TryGetValue(__instance.GUID, out var employeeBehaviour))
        {
          employeeBehaviour = new ChemistBehaviour(__instance, employeeAdapter);
          ActiveBehaviours[__instance.GUID] = employeeBehaviour;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Initialized ChemistBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Chemist);
        }
        //base
        if (__instance.Fired || (!(__instance.behaviour.activeBehaviour == null) && !(__instance.behaviour.activeBehaviour == __instance.WaitOutside)))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.Fired || (!(__instance.behaviour.activeBehaviour == null) && !(__instance.behaviour.activeBehaviour == __instance.WaitOutside)) {__instance.Fired} | {!(__instance.behaviour.activeBehaviour == null)} | {!(__instance.behaviour.activeBehaviour == __instance.WaitOutside)} for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          return false;
        }

        bool flag = false;
        bool flag2 = false;
        if (__instance.GetBed() == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.GetBed() == null for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          flag = true;
          __instance.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
        }
        else if (NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          flag = true;
          __instance.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
        }
        else if (!__instance.PaidForToday)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: !__instance.PaidForToday for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          if (__instance.IsPayAvailable())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.IsPayAvailable() for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            flag2 = true;
          }
          else
          {
            flag = true;
            __instance.SubmitNoWorkReason("I haven't been paid yet", "You can place cash in my briefcase on my bed.");
          }
        }

        if (flag)
        {
          __instance.SetWaitOutside(wait: true);
        }
        else if (InstanceFinder.IsServer && flag2 && __instance.IsPayAvailable())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: InstanceFinder.IsServer && flag2 && __instance.IsPayAvailable() for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          __instance.RemoveDailyWage();
          __instance.SetIsPaid();
        }
        if (!InstanceFinder.IsServer)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefixPrefix: Skipping client-side for chemist={__instance.fullName}", DebugLogger.Category.Chemist);
          return false;
        }
        else if (EmployeeBehaviour.States.TryGetValue(__instance.GUID, out var state) && state.CurrentState != EState.Idle)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviour: CurrentState {state.CurrentState} != EState.Idle for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          employeeBehaviour.Update(__instance);
          __instance.MarkIsWorking();
        }
        else if (__instance.Fired)
        {
          __instance.LeavePropertyAndDespawn();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefixPrefix: Chemist {__instance.fullName} is fired, despawning", DebugLogger.Category.Chemist);
        }
        else
        {
          if (!__instance.CanWork())
          {
            __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
            __instance.SetIdle(true);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefixPrefix: Chemist {__instance.fullName} cannot work, setting idle", DebugLogger.Category.Chemist);
            return false;
          }

          if (__instance.configuration?.TotalStations > 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefixPrefix: No mod tasks, trying game tasks for {__instance.fullName}", DebugLogger.Category.Chemist);
            __instance.TryStartNewTask();
          }
          else
          {
            __instance.SubmitNoWorkReason("I haven't been assigned any stations", "You can use your management clipboards to assign stations to me.");
            __instance.SetIdle(true);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefixPrefix: No stations assigned for {__instance.fullName}, setting idle", DebugLogger.Category.Chemist);
          }
        }
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefixPrefix: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
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
        if (ActiveBehaviours.TryGetValue(__instance.GUID, out var behaviour))
        {
          if (ActiveMoveItemBehaviours.TryGetValue(__instance.GUID, out var moveItemBehaviour))
          {
            behaviour.Disable(__instance);
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
