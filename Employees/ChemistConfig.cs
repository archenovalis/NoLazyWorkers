using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.Employees;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Employees.ChemistExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.General;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using ScheduleOne.EntityFramework;
using NoLazyWorkers.Stations.NoLazyWorkers.Stations;
using FishNet.Object;
using FishNet.Managing.Object;

namespace NoLazyWorkers.Employees
{
  public static class ChemistExtensions
  {
    public class ChemistAdapter : IEmployeeAdapter
    {
      private readonly Chemist _chemist;
      private readonly StateData _state;
      public Property AssignedProperty => _chemist.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Chemist;
      public StateData State => _state;

      public ChemistAdapter(Chemist chemist)
      {
        _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
        var advMove = chemist.gameObject.AddComponent<AdvancedMoveItemBehaviour>();
        advMove.Setup(chemist);
        var networkObject = chemist.gameObject.GetComponent<NetworkObject>();
        ManagedObjects.InitializePrefab(networkObject, -1);
        if (InstanceFinder.IsServer)
          advMove.Preinitialize_Internal(networkObject, true);
        else
          advMove.Preinitialize_Internal(networkObject, false);
        advMove.NetworkInitializeIfDisabled();
        chemist.behaviour.behaviourStack.Add(advMove);
        chemist.behaviour.behaviourStack = chemist.behaviour.behaviourStack.OrderByDescending(x => x.Priority).ToList();
        _state = new StateData(chemist, new ChemistBehaviour(chemist, this), advMove);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAdapter: Initialized for NPC {_chemist.fullName}", DebugLogger.Category.Chemist);
      }

      public bool HandleMovementComplete(Employee employee, StateData state)
      {
        if (!(employee is Chemist) || !state.TryGetValue<ITransitEntity>("Destination", out var destination))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ChemistAdapter.HandleMovementComplete: Invalid employee or destination for {employee?.fullName ?? "null"}", DebugLogger.Category.Chemist);
          return false;
        }

        if (StationAdapters.TryGetValue((destination as BuildableItem)?.GUID ?? Guid.Empty, out var stationAdapter))
        {
          state.Station = stationAdapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAdapter.HandleMovementComplete: Reached station {stationAdapter.GUID} for {employee.fullName}", DebugLogger.Category.Chemist);
          return true;
        }

        if (state.TargetItem != null && destination is PlaceableStorageEntity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAdapter.HandleMovementComplete: Reached shelf for item {state.TargetItem.ID} for {employee.fullName}", DebugLogger.Category.Chemist);
          return true;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ChemistAdapter.HandleMovementComplete: No valid destination for {employee.fullName}", DebugLogger.Category.Chemist);
        return false;
      }

      public bool GetEmployeeBehaviour(NPC npc, out EmployeeBehaviour employeeBehaviour)
      {
        employeeBehaviour = null;
        if (!(npc is Chemist chemist))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistAdapter.GetEmployeeBehaviour: NPC is not a Chemist for {npc?.fullName ?? "null"}", DebugLogger.Category.Chemist);
          return false;
        }
        employeeBehaviour = new MixingStationBeh(chemist, this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAdapter.GetEmployeeBehaviour: Created MixingStationBeh for {chemist.fullName}", DebugLogger.Category.Chemist);
        return true;
      }

      public void ResetMovement(StateData state)
      {
        if (state.Station != null)
        {
          EmployeeBehaviour.IsFetchingPackaging.Remove(state.Station.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistAdapter.ResetMovement: Cleared _isFetchingPackaging for station {state.Station.GUID}", DebugLogger.Category.Chemist);
        }
      }
    }
  }
}
