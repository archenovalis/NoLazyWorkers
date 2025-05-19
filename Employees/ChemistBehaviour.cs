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
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    private readonly Chemist _chemist;

    public ChemistBehaviour(Chemist chemist, IStationAdapter station, IEmployeeAdapter employee) : base(chemist, employee)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      Employee = employee;
      if (station != null)
      {
        RegisterStationBehaviour(GetInstancedBehaviour(chemist, station), station);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour: Initialized for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
    }

    public bool Planning(Behaviour behaviour, StateData state)
    {
      var station = StationUtilities.GetStation(behaviour);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandlePlanning: No station for {_chemist.fullName}", DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "No station");
        return false;
      }

      if (MixingStationUtilities.ValidateStationState(_chemist, station, out bool canStart, out bool canRestock))
      {
        if (canStart)
        {
          TransitionState(behaviour, state, EState.Operating, "Station ready to start");
          return true;
        }
        if (canRestock)
        {
          var requests = FindItemsNeedingMovement(_chemist, state, station);
          if (requests.Count > 0)
          {
            AddRoutes(behaviour, state, requests);
            TransitionState(behaviour, state, EState.Grabbing, "Restock routes planned");
            return true;
          }
        }
      }
      state.ActiveRoutes.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandlePlanning: No routes planned for NPC {_chemist.fullName}", DebugLogger.Category.Chemist);
      TransitionState(behaviour, state, EState.Idle, "No routes planned");
      return false;
    }

    private List<TransferRequest> FindItemsNeedingMovement(Chemist chemist, StateData state, IStationAdapter station)
    {
      var requests = new List<TransferRequest>();
      var property = chemist.AssignedProperty;
      if (property == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"FindItemsNeedingMovement: Property is null for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
        return requests;
      }

      int maxRoutes = Mathf.Min(MAX_ROUTES_PER_CYCLE, chemist.Inventory.ItemSlots.Count(s => s.ItemInstance == null) - state.ActiveRoutes.Count);
      var inputItems = station.GetInputItemForProduct();
      if (inputItems == null || inputItems.Count == 0 || inputItems[0]?.SelectedItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindItemsNeedingMovement: No input items for station {station.GUID}", DebugLogger.Category.Chemist);
        return requests;
      }

      var item = inputItems[0].SelectedItem.GetDefaultInstance();
      if (IsItemTimedOut(property, item))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Item {item.ID} is timed out", DebugLogger.Category.Chemist);
        return requests;
      }

      var shelves = FindShelvesWithItem(chemist, item, state.QuantityNeeded, state.QuantityWanted);
      var source = shelves.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
      if (source == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindItemsNeedingMovement: No shelves with item {item.ID}", DebugLogger.Category.Chemist);
        return requests;
      }

      var sourceSlots = GetOutputSlotsContainingTemplateItem(source, item);
      if (sourceSlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindItemsNeedingMovement: No output slots for item {item.ID} on shelf {source.GUID}", DebugLogger.Category.Chemist);
        return requests;
      }

      sourceSlots.ApplyLocks(chemist, "Route planning lock");
      var destination = station.TransitEntity;
      var deliverySlots = destination.ReserveInputSlotsForItem(item, chemist.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        sourceSlots.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindItemsNeedingMovement: No delivery slots for item {item.ID} on station {station.GUID}", DebugLogger.Category.Chemist);
        return requests;
      }

      int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
      if (quantity <= 0)
      {
        sourceSlots.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindItemsNeedingMovement: No quantity needed for item {item.ID}", DebugLogger.Category.Chemist);
        return requests;
      }

      if (!chemist.Movement.CanGetTo(station.GetAccessPoint(chemist)))
      {
        sourceSlots.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindItemsNeedingMovement: Cannot reach station {station.GUID}", DebugLogger.Category.Chemist);
        return requests;
      }

      var inventorySlot = chemist.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
      if (inventorySlot == null)
      {
        sourceSlots.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindItemsNeedingMovement: No inventory slot for item {item.ID}", DebugLogger.Category.Chemist);
        return requests;
      }

      var request = new TransferRequest(chemist, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
      requests.Add(request);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindItemsNeedingMovement: Created restock request for {quantity} of {item.ID} to station {station.GUID}", DebugLogger.Category.Chemist);
      return requests;
    }
  }
}