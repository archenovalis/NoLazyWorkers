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
using static NoLazyWorkers.Employees.PackagerBehaviour;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.DevUtilities;
using NoLazyWorkers.General;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using ScheduleOne.EntityFramework;
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public static class PackagerExtensions
  {
    public class PackagerAdapter : IEmployeeAdapter
    {
      private readonly Packager _packager;
      private readonly StateData _state;
      public Property AssignedProperty => _packager.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Packager;
      public StateData State => _state;

      public void ResetMovement(StateData state)
      {
        EmployeeBehaviour.IsFetchingPackaging.Remove(state.Station.GUID);
      }
      public PackagerAdapter(Packager packager)
      {
        _packager = packager ?? throw new ArgumentNullException(nameof(packager));
        var advMove = packager.gameObject.AddComponent<AdvancedMoveItemBehaviour>();
        advMove.Setup(packager);
        _state = new StateData(packager, new PackagerBehaviour(packager, this), advMove);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter: Initialized for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      }

      public bool HandleMovementComplete(Employee employee, StateData state)
      {
        if (!(employee is Packager) || !state.TryGetValue<ITransitEntity>("Destination", out var destination))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandleMovementComplete: Invalid employee or destination", DebugLogger.Category.Packager);
          return false;
        }

        if (StationAdapters.TryGetValue((destination as BuildableItem)?.GUID ?? Guid.Empty, out var stationAdapter))
        {
          state.Station = stationAdapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.HandleMovementComplete: Reached station {stationAdapter.GUID}", DebugLogger.Category.Packager);
          return true;
        }

        if (state.TargetItem != null && destination is PlaceableStorageEntity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.HandleMovementComplete: Reached shelf for item {state.TargetItem.ID}", DebugLogger.Category.Packager);
          return true;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandleMovementComplete: No valid destination", DebugLogger.Category.Packager);
        return false;
      }

      public bool GetEmployeeBehaviour(NPC npc, out EmployeeBehaviour employeeBehaviour)
      {
        employeeBehaviour = null;
        if (!(npc is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerAdapter.GetEmployeeBehaviour: NPC is not a Packager", DebugLogger.Category.Packager);
          return false;
        }

        employeeBehaviour = new PackagerBehaviour(packager, this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.GetEmployeeBehaviour: Created PackagerBehaviour for NPC {packager.fullName}", DebugLogger.Category.Packager);
        return true;
      }
    }
  }
}