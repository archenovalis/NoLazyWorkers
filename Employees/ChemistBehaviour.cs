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
using static NoLazyWorkers.Structures.StorageUtilities;

using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.EmployeeExtensions;

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    public ChemistBehaviour(Behaviour behaviour, IStationAdapter station, IEmployeeAdapter employee) : base(behaviour.Npc, employee)
    {
      RegisterStationBehaviour(behaviour, station);
    }

    protected override void HandleCompleted(Behaviour behaviour, StateData state)
    {
      if (state.Station.HasActiveOperation) return;
      if (state.Station.OutputSlot.Quantity > 0)
      {
        var item = state.Station.OutputSlot.ItemInstance;
        var destination = FindPackagingStation(Adapter, item) ?? FindShelfForDelivery(Npc, item);
        if (destination != null)
        {
          var slots = destination.ReserveInputSlotsForItem(item, Npc.NetworkObject);
          var request = new TransferRequest(item, state.Station.OutputSlot.Quantity, Npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), state.Station.TransitEntity, new List<ItemSlot> { state.Station.OutputSlot }, destination, slots);
          state.ActiveRoutes.Add(new PrioritizedRoute(request, PRIORITY_STATION_REFILL));
          TransitionState(behaviour, state, EState.Grabbing, "Output ready for delivery");
        }
      }
      else
      {
        TransitionState(behaviour, state, EState.Idle, "Operation complete");
      }
    }
  }
}