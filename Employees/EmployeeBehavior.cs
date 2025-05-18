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
using static NoLazyWorkers.Stations.LabOvenExtensions;
using static NoLazyWorkers.Stations.ChemistryStationExtensions;
using static NoLazyWorkers.Stations.CauldronExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using ScheduleOne.Property;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using ScheduleOne.Delivery;
using ScheduleOne.NPCs.Behaviour;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.Structures;

namespace NoLazyWorkers.Employees
{
  public static class EmployeeExtensions
  {
    public interface IEmployeeAdapter
    {
      bool CustomizePlanning(Behaviour behaviour, StateData state) => false;
      bool HandleGrabbing(Behaviour behaviour, StateData state) => false;
      bool HandleInserting(Behaviour behaviour, StateData state) => false;
      bool HandleOperating(Behaviour behaviour, StateData state) => false;
      bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item) => false;
      Property AssignedProperty { get; }
      NpcSubType SubType { get; }
    }
    public enum NpcSubType
    {
      Chemist,
      Packager,
      Botanist,
      Cleaner,
      Driver
    }

    public enum EState
    {
      Idle,               // Check for tasks or disable
      PlanningRoutes,     // Plan routes or select items to fetch
      Moving,             // Move to a location (shelf, station, dock)
      PickingUp,          // Pick up items from slots
      Inserting,          // Insert items into slots
      Operating,          // Start a station operation
      Completed           // Handle operation completion
    }
    public class StateData
    {
      public EState CurrentState { get; set; } = EState.Idle;
      public ItemInstance TargetItem { get; set; }
      public int QuantityInventory { get; set; }
      public int QuantityNeeded { get; set; }
      public ITransitEntity Destination { get; set; }
      public bool IsMoving { get; set; }
      public float MoveTimeout { get; set; }
      public float MoveElapsed { get; set; }
      public List<PrioritizedRoute> ActiveRoutes { get; set; } = new();
      public ITransitEntity PickupLocation { get; set; }
      public IStationAdapter Station { get; set; }
      public List<ItemSlot> PickupSlots { get; set; } = new();
      public List<ItemSlot> DeliverySlots { get; set; } = new();
    }
    public struct PrioritizedRoute
    {
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public ITransitEntity PickupLocation;
      public List<ItemSlot> PickupSlots;
      public ITransitEntity Destination;
      public List<ItemSlot> DeliverySlots;
      public int Priority;
      public TransitRoute TransitRoute;

      public PrioritizedRoute(TransferRequest request, int priority)
      {
        Item = request.Item;
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot;
        PickupLocation = request.PickupLocation;
        PickupSlots = request.PickupSlots;
        Destination = request.DeliveryLocation;
        DeliverySlots = request.DeliverySlots;
        Priority = priority;
      }

      // Class: TransferRequest
      // Purpose: Represents a request to transfer items between locations, supporting multiple pickup and delivery slots.
      // Fields:
      //   - Item: The item to transfer.
      //   - Quantity: The number of items to transfer.
      //   - PickupLocation: The source entity (null for inventory routes).
      //   - PickupSlots: The list of slots to pick up items from.
      //   - DeliveryLocation: The destination entity.
      //   - DeliverySlots: The list of slots to deliver items to.
      public class TransferRequest
      {
        public ItemInstance Item { get; }
        public int Quantity { get; }
        public ItemSlot InventorySlot { get; }
        public ITransitEntity PickupLocation { get; }
        public List<ItemSlot> PickupSlots { get; }
        public ITransitEntity DeliveryLocation { get; }
        public List<ItemSlot> DeliverySlots { get; }

        public TransferRequest(ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickupLocation, List<ItemSlot> pickupSlots,
            ITransitEntity deliveryLocation, List<ItemSlot> deliverySlots)
        {
          Item = item ?? throw new ArgumentNullException(nameof(item));
          Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
          InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
          PickupLocation = pickupLocation; // Can be null for inventory routes
          DeliveryLocation = deliveryLocation ?? throw new ArgumentNullException(nameof(deliveryLocation));

          // Validate pickupSlots
          PickupSlots = pickupSlots != null
              ? pickupSlots.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList()
              : new List<ItemSlot>();
          if (pickupSlots != null && pickupSlots.Any(slot => slot == null || slot.ItemInstance == null || slot.Quantity <= 0))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"TransferRequest: Filtered out {pickupSlots.Count - PickupSlots.Count} invalid pickup slots (null, no item, or empty)",
                DebugLogger.Category.Packager);
          }
          if (PickupLocation == null && PickupSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"TransferRequest: No valid pickup slots for inventory route with item {item.ID}",
                DebugLogger.Category.Packager);
            throw new ArgumentException("No valid pickup slots for inventory route");
          }

          // Validate deliverySlots
          DeliverySlots = deliverySlots != null
              ? deliverySlots.Where(slot => slot != null).ToList()
              : throw new ArgumentNullException(nameof(deliverySlots));
          if (deliverySlots != null && deliverySlots.Any(slot => slot == null))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"TransferRequest: Filtered out {deliverySlots.Count - DeliverySlots.Count} null delivery slots",
                DebugLogger.Category.Packager);
          }
          if (DeliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"TransferRequest: No valid delivery slots for item {item.ID} to {deliveryLocation.Name}",
                DebugLogger.Category.Packager);
            throw new ArgumentException("No valid delivery slots");
          }
        }
      }
    }
  }

  public static class EmployeeUtilities
  {
    // Method: GetOutputSlotsContainingTemplateItem
    // Purpose: Retrieves all output slots from an ITransitEntity that contain the specified item template and match the slot type.
    // Parameters:
    //   - entity: The ITransitEntity to search.
    //   - item: The ItemInstance template to match.
    //   - slotType: The ESlotType to filter (e.g., Output).
    // Returns: A List<ItemSlot> containing all matching slots that are not null, have the item, and are not locked.
    public static List<ItemSlot> GetOutputSlotsContainingTemplateItem(ITransitEntity entity, ItemInstance item)
    {
      if (entity == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"GetOutputSlotsContainingTemplateItem: Invalid input (entity={entity?.GUID}, item={item?.ID})",
            DebugLogger.Category.Packager);
        return new List<ItemSlot>();
      }

      var matchingSlots = entity.OutputSlots
          .Where(slot => slot != null &&
                         slot.ItemInstance != null &&
                         slot.ItemInstance.CanStackWith(item, false) &&
                         slot.Quantity > 0 &&
                         !slot.IsLocked)
          .ToList();

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetOutputSlotsContainingTemplateItem: Found {matchingSlots.Count} slots for item {item.ID} in {entity.GUID}",
          DebugLogger.Category.Packager);

      return matchingSlots;
    }

    // Method: SetReservedSlot
    // Purpose: Assigns a reserved slot to a MoveItemBehaviour instance for item pickup.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to associate with the slot.
    //   - slot: The ItemSlot to reserve.
    public static void SetReservedSlot(MoveItemBehaviour behaviour, ItemSlot slot)
    {
      RouteQueueManager._reservedSlots[behaviour] = slot;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"SetReservedSlot: Set slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}",
          DebugLogger.Category.Packager);
    }

    // Method: GetReservedSlot
    // Purpose: Retrieves the reserved slot for a MoveItemBehaviour instance.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to query.
    // Returns: The reserved ItemSlot, or null if none exists.
    public static ItemSlot GetReservedSlot(MoveItemBehaviour behaviour)
    {
      RouteQueueManager._reservedSlots.TryGetValue(behaviour, out var slot);
      return slot;
    }

    // Method: ReleaseReservations
    // Purpose: Releases the reserved slot for a MoveItemBehaviour instance, unlocking it.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to release.
    public static void ReleaseReservations(MoveItemBehaviour behaviour)
    {
      if (RouteQueueManager._reservedSlots.TryGetValue(behaviour, out var slot) && slot != null)
      {
        slot.RemoveLock();
        RouteQueueManager._reservedSlots.Remove(behaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ReleaseReservations: Released slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}",
            DebugLogger.Category.Packager);
      }
    }

    public static void ApplyLocks(this List<ItemSlot> slots, NPC npc, string reason)
    {
      foreach (var slot in slots.Where(s => s != null))
      {
        ApplyLocks(slot, npc, reason);
      }
    }
    public static void ApplyLocks(this ItemSlot slot, NPC npc, string reason)
    {
      slot.ApplyLock(npc.NetworkObject, reason);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ApplyLock: Locked slot {slot.GetHashCode()} for {reason} by NPC={npc.fullName}",
          DebugLogger.Category.Packager);
    }

    public static void RemoveLock(this List<ItemSlot> slots)
    {
      foreach (var slot in slots.Where(s => s != null))
      {
        slot.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"RemoveLock: Unlocked slot {slot.GetHashCode()}",
            DebugLogger.Category.Packager);
      }
    }
  }

  public abstract class EmployeeBehaviour
  {
    public static Dictionary<Guid, MoveItemBehaviour> ActiveBehaviours = new();
    protected readonly NPC Npc;
    protected readonly IEmployeeAdapter Adapter;
    protected readonly Dictionary<EState, Action<Behaviour, StateData>> StateHandlers;
    protected static readonly Dictionary<Behaviour, StateData> States = new();
    private static readonly Dictionary<Guid, float> StateStartTimes = new();
    private static readonly Dictionary<PrioritizedRoute, (float Time, string Reason)> FailedRoutes = new();
    public const int MAX_ROUTES_PER_CYCLE = 5;
    public const int PRIORITY_STATION_REFILL = 100;
    public const int PRIORITY_LOADING_DOCK = 50;
    public const int PRIORITY_SHELF_RESTOCK = 10;
    public const float RETRY_DELAY = 30f;

    public EmployeeBehaviour(NPC npc, IEmployeeAdapter adapter = null)
    {
      Npc = npc ?? throw new ArgumentNullException(nameof(npc));
      Adapter = adapter;
      StateHandlers = new Dictionary<EState, Action<Behaviour, StateData>>
            {
                { EState.Idle, HandleIdle },
                { EState.PlanningRoutes, HandlePlanningRoutes },
                { EState.Moving, HandleMoving },
                { EState.PickingUp, HandlePickingUp },
                { EState.Inserting, HandleInserting },
                { EState.Operating, HandleOperating },
                { EState.Completed, HandleCompleted }
            };
      StateStartTimes[npc.GUID] = Time.time;
    }

    public void Update(Behaviour behaviour)
    {
      if (!States.TryGetValue(behaviour, out var state))
      {
        state = new StateData();
        States[behaviour] = state;
      }
      StateHandlers[state.CurrentState](behaviour, state);
    }

    protected void TransitionState(Behaviour behaviour, StateData state, EState newState, string reason)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"TransitionState: {Npc.fullName} from {state.CurrentState} to {newState}, reason={reason}, invQty={state.QuantityInventory}, routes={state.ActiveRoutes.Count}",
          DebugLogger.Category.AllEmployees);
      state.CurrentState = newState;
      StateStartTimes[Npc.GUID] = Time.time;
    }

    protected virtual void HandleIdle(Behaviour behaviour, StateData state)
    {
      state.Station = GetStation(behaviour);
      if (state.Station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"HandleIdle: No station for {Npc.fullName}, disabling",
            DebugLogger.Category.AllEmployees);
        Disable(behaviour);
        return;
      }

      // Check for player-inserted items
      var inventorySlots = Npc.Inventory.ItemSlots
          .Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked)
          .ToList();
      if (inventorySlots.Any())
      {
        foreach (var slot in inventorySlots)
        {
          slot.ApplyLocks(Npc, "Inventory route planning lock");
          if (Adapter?.HandleInventoryItem(behaviour, state, slot.ItemInstance) == true)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"HandleIdle: Added inventory route for {slot.ItemInstance.ID} (qty={slot.Quantity})",
                DebugLogger.Category.AllEmployees);
          }
          else
          {
            slot.RemoveLock();
          }
        }
      }

      TransitionState(behaviour, state, EState.PlanningRoutes, "Start route planning");
    }

    protected virtual void HandlePlanningRoutes(Behaviour behaviour, StateData state)
    {
      if (Adapter?.CustomizePlanning(behaviour, state) == true) return;

      // Generic route planning
      state.ActiveRoutes.Clear();
      var requests = FindItemsNeedingMovement(Npc, state);
      if (requests.Count > 0)
      {
        AddRoutes(behaviour, state, requests);
        TransitionState(behaviour, state, EState.PickingUp, "Routes planned");
        HandlePickingUp(behaviour, state);
      }
      else
      {
        TransitionState(behaviour, state, EState.Operating, "No routes, try operating");
      }
    }

    protected virtual void HandleMoving(Behaviour behaviour, StateData state)
    {
      if (!state.IsMoving) return;

      state.MoveElapsed += Time.deltaTime;
      if (state.MoveElapsed >= state.MoveTimeout || !Npc.Movement.IsMoving)
      {
        state.IsMoving = false;
        if (state.MoveElapsed >= state.MoveTimeout)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"HandleMoving: Timeout moving to {state.Destination?.Name}, to Idle",
              DebugLogger.Category.AllEmployees);
          TransitionState(behaviour, state, EState.Idle, "Movement timeout");
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleMoving: Reached {state.Destination?.Name}",
              DebugLogger.Category.AllEmployees);
          StateHandlers[state.CurrentState](behaviour, state);
        }
      }
    }

    protected virtual void HandlePickingUp(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandleGrabbing(behaviour, state) == true) return;

      var route = state.ActiveRoutes.FirstOrDefault();
      if (route.PickupLocation == null) // Inventory route
      {
        TransitionState(behaviour, state, EState.Inserting, "Inventory route, skip pickup");
        MoveTo(behaviour, state, route.Destination);
        return;
      }

      if (!IsAtLocation(behaviour, route.PickupLocation))
      {
        MoveTo(behaviour, state, route.PickupLocation);
        return;
      }

      state.PickupSlots = route.PickupSlots;
      state.PickupSlots.ApplyLocks(Npc, "Pickup lock");
      int remaining = route.Quantity;
      foreach (var slot in state.PickupSlots)
      {
        if (slot.Quantity <= 0 || slot.IsLocked && slot.ActiveLock.LockOwner != Npc.NetworkObject)
          continue;

        int amount = Mathf.Min(slot.Quantity, remaining);
        if (amount <= 0) continue;

        slot.ChangeQuantity(-amount);
        route.InventorySlot.InsertItem(route.Item.GetCopy(amount));
        remaining -= amount;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandlePickingUp: Picked up {amount} of {route.Item.ID} from slot {slot.GetHashCode()}",
            DebugLogger.Category.AllEmployees);
        if (remaining <= 0) break;
      }
      state.PickupSlots.RemoveLock();
      state.QuantityInventory += route.Quantity - remaining;

      if (remaining > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandlePickingUp: Insufficient items, {remaining} remaining",
            DebugLogger.Category.AllEmployees);
        HandleFailedRoute(behaviour, state, route, "Insufficient items");
        return;
      }

      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, EState.Inserting, "Items picked up");
      MoveTo(behaviour, state, route.Destination);
    }

    protected virtual void HandleInserting(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandleInserting(behaviour, state) == true) return;

      var route = state.ActiveRoutes.FirstOrDefault();
      if (route.Destination == null)
      {
        TransitionState(behaviour, state, EState.Idle, "No destination");
        return;
      }

      if (!IsAtLocation(behaviour, route.Destination))
      {
        MoveTo(behaviour, state, route.Destination);
        return;
      }

      state.DeliverySlots = route.DeliverySlots;
      state.DeliverySlots.ApplyLocks(Npc, "Delivery lock");
      int remaining = state.QuantityInventory;
      foreach (var slot in state.DeliverySlots)
      {
        int capacity = slot.GetCapacityForItem(route.Item);
        int amount = Mathf.Min(remaining, capacity);
        if (amount <= 0) continue;

        slot.InsertItem(route.Item.GetCopy(amount));
        route.InventorySlot.ChangeQuantity(-amount);
        remaining -= amount;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleInserting: Delivered {amount} of {route.Item.ID} to slot {slot.GetHashCode()}",
            DebugLogger.Category.AllEmployees);
        if (remaining <= 0) break;
      }
      state.DeliverySlots.RemoveLock();
      state.QuantityInventory = remaining;

      if (remaining > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleInserting: Could not deliver {remaining} items",
            DebugLogger.Category.AllEmployees);
        HandleFailedRoute(behaviour, state, route, "Insufficient slot capacity");
        return;
      }

      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, state.ActiveRoutes.Count > 0 ? EState.PickingUp : EState.Idle, "Delivery complete");
    }

    protected virtual void HandleOperating(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandleOperating(behaviour, state) == true) return;

      if (!IsAtLocation(behaviour, (ITransitEntity)state.Station))
      {
        MoveTo(behaviour, state, (ITransitEntity)state.Station);
        return;
      }

      if (state.Station.HasActiveOperation || state.Station.GetInputQuantity() < state.Station.StartThreshold)
      {
        TransitionState(behaviour, state, EState.PlanningRoutes, "Cannot start operation");
        return;
      }

      state.Station.StartOperation(behaviour);
      TransitionState(behaviour, state, EState.Completed, "Operation started");
    }

    protected virtual void HandleCompleted(Behaviour behaviour, StateData state)
    {
      if (state.Station.HasActiveOperation) return;

      if (state.Station.OutputSlot.Quantity > 0)
      {
        IEmployeeAdapter employee = GetEmployee(Npc);
        var item = state.Station.OutputSlot.ItemInstance;
        var destination = FindShelfForDelivery(Npc, item) ?? FindPackagingStation(employee, item);
        if (destination != null)
        {
          var slots = destination.ReserveInputSlotsForItem(item, Npc.NetworkObject);
          var request = new TransferRequest(item, state.Station.OutputSlot.Quantity, Npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), (ITransitEntity)state.Station, new List<ItemSlot> { state.Station.OutputSlot }, destination, slots);
          state.ActiveRoutes.Add(new PrioritizedRoute(request, PRIORITY_STATION_REFILL));
          TransitionState(behaviour, state, EState.PickingUp, "Output ready for delivery");
        }
      }
      else
      {
        TransitionState(behaviour, state, EState.Idle, "Operation complete");
      }
    }

    protected virtual void MoveTo(Behaviour behaviour, StateData state, ITransitEntity destination)
    {
      if (destination == null || !Npc.Movement.CanGetTo(destination, 1f))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MoveTo: Cannot reach {destination?.Name}, disabling",
            DebugLogger.Category.AllEmployees);
        Disable(behaviour);
        return;
      }

      state.Destination = destination;
      state.MoveTimeout = 30f;
      state.MoveElapsed = 0f;
      state.IsMoving = true;
      Npc.Movement.SetDestination(NavMeshUtility.GetAccessPoint(destination, Npc).position);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"MoveTo: Moving to {destination.Name}",
          DebugLogger.Category.AllEmployees);
    }

    protected virtual bool IsAtLocation(Behaviour behaviour, ITransitEntity location)
    {
      if (location == null) return false;
      float distance = Vector3.Distance(Npc.transform.position, NavMeshUtility.GetAccessPoint(location, Npc).position);
      bool atLocation = distance < 0.4f;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"IsAtLocation: Result={atLocation}, Distance={distance:F2}, location={location.GUID}",
          DebugLogger.Category.AllEmployees);
      return atLocation;
    }

    protected virtual void Disable(Behaviour behaviour)
    {
      if (States.TryGetValue(behaviour, out var state))
      {
        foreach (var route in state.ActiveRoutes)
          ReleaseRouteLocks(route);
        state.ActiveRoutes.Clear();
        state.IsMoving = false;
        States.Remove(behaviour);
      }
      behaviour.Disable();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"Disable: Behaviour disabled for {Npc.fullName}",
          DebugLogger.Category.AllEmployees);
    }

    // Integrated from HandlerBehaviourUtilities
    protected virtual List<TransferRequest> FindItemsNeedingMovement(NPC npc, StateData state)
    {
      var employee = GetEmployee(npc);
      var requests = new List<TransferRequest>();
      var property = employee.AssignedProperty;
      if (property == null) return requests;

      int maxRoutes = Mathf.Min(MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
      var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();

      // Station Refill
      if (PropertyStations.TryGetValue(property, out var stations))
      {
        foreach (var station in stations)
        {
          if (requests.Count >= maxRoutes) break;
          if (station.IsInUse || station.HasActiveOperation) continue;

          var items = station.RefillList();
          foreach (var item in items)
          {
            var shelves = FindShelvesWithItem(npc, item, 1);
            var source = shelves.Keys.FirstOrDefault(s => GetOutputSlotsContainingTemplateItem(s, item).Count > 0);
            if (source == null) continue;

            var sourceSlots = GetOutputSlotsContainingTemplateItem(source, item);
            if (sourceSlots.Count == 0) continue;

            sourceSlots.ApplyLocks(npc, "Route planning lock");
            var destination = station.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }

            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }

            if (!npc.Movement.CanGetTo(station.GetAccessPoint(npc)))
            {
              sourceSlots.RemoveLock();
              continue;
            }

            var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), source, sourceSlots, destination, deliverySlots);
            if (!pickupGroups.ContainsKey(source)) pickupGroups[source] = new List<TransferRequest>();
            pickupGroups[source].Add(request);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: StationRefill request for {quantity} of {item.ID}",
                DebugLogger.Category.AllEmployees);
            break;
          }
        }
      }

      // Loading Dock
      foreach (var dock in property.LoadingDocks)
      {
        if (requests.Count >= maxRoutes) break;
        if (!dock.IsInUse) continue;

        foreach (var slot in dock.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;

          var item = slot.ItemInstance;
          var sourceSlots = GetOutputSlotsContainingTemplateItem(dock, item);
          if (sourceSlots.Count == 0) continue;

          sourceSlots.ApplyLocks(npc, "Route planning lock");
          var destination = FindShelfForDelivery(npc, item);
          if (destination == null)
          {
            sourceSlots.RemoveLock();
            continue;
          }
          var transitEntity = destination as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            sourceSlots.RemoveLock();
            continue;
          }

          int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), transitEntity.GetInputCapacityForItem(item, npc));
          if (quantity <= 0)
          {
            sourceSlots.RemoveLock();
            continue;
          }

          if (!npc.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, npc)))
          {
            sourceSlots.RemoveLock();
            continue;
          }

          var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), dock, sourceSlots, destination, deliverySlots);
          if (!pickupGroups.ContainsKey(dock)) pickupGroups[dock] = new List<TransferRequest>();
          pickupGroups[dock].Add(request);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: LoadingDock request for {quantity} of {item.ID}",
              DebugLogger.Category.AllEmployees);
        }
      }

      // Shelf Restock
      foreach (var shelf in StorageExtensions.AnyShelves)
      {
        if (requests.Count >= maxRoutes) break;
        if (shelf?.OutputSlots == null) continue;

        foreach (var slot in shelf.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;

          var item = slot.ItemInstance;
          if (RouteQueueManager.NoDestinationCache.TryGetValue(shelf.ParentProperty, out var cache) && cache.Any(i => i.CanStackWith(item, false)))
            continue;

          slot.ApplyLocks(npc, "Route planning lock");
          var assignedShelf = FindShelfForDelivery(npc, item, false);
          if (assignedShelf == null || assignedShelf == shelf)
          {
            slot.RemoveLock();
            continue;
          }

          var transitEntity = assignedShelf as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            slot.RemoveLock();
            continue;
          }

          int quantity = Mathf.Min(slot.Quantity, transitEntity.GetInputCapacityForItem(item, npc));
          if (quantity <= 0)
          {
            slot.RemoveLock();
            continue;
          }

          var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            slot.RemoveLock();
            continue;
          }

          var request = new TransferRequest(item, quantity, inventorySlot, shelf, new List<ItemSlot> { slot }, assignedShelf, deliverySlots);
          if (!pickupGroups.ContainsKey(shelf)) pickupGroups[shelf] = new List<TransferRequest>();
          pickupGroups[shelf].Add(request);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: ShelfRestock request for {quantity} of {item.ID}",
              DebugLogger.Category.AllEmployees);
        }
      }

      foreach (var group in pickupGroups.OrderByDescending(g => g.Value.Max(r => GetPriority(r))))
      {
        requests.AddRange(group.Value.OrderByDescending(r => GetPriority(r)).Take(maxRoutes));
        break;
      }

      foreach (var slot in pickupGroups.SelectMany(g => g.Value).SelectMany(r => r.PickupSlots).Distinct())
      {
        if (!requests.Any(r => r.PickupSlots.Contains(slot)))
          slot.RemoveLock();
      }

      return requests;
    }

    public static void AddRoutes(Behaviour behaviour, StateData state, List<TransferRequest> requests)
    {
      state.ActiveRoutes.Clear();
      int availableSlots = behaviour.Npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      var filteredRequests = requests
          .Where(r => !FailedRoutes.Any(t => t.Key.Item.CanStackWith(r.Item, false) && Time.time <= t.Value.Time + RETRY_DELAY))
          .OrderByDescending(GetPriority)
          .Take(availableSlots)
          .ToList();

      foreach (var request in filteredRequests)
      {
        int priority = GetPriority(request);
        request.PickupSlots?.ApplyLocks(behaviour.Npc, "Route pickup lock");
        request.DeliverySlots.ApplyLocks(behaviour.Npc, "Route destination lock");
        state.ActiveRoutes.Add(new PrioritizedRoute(request, priority));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"AddRoutes: Added route for {request.Item.ID} (qty={request.Quantity})",
            DebugLogger.Category.AllEmployees);
      }
    }

    protected static int GetPriority(TransferRequest request)
    {
      return request.DeliveryLocation is IStationAdapter ? PRIORITY_STATION_REFILL :
             request.PickupLocation is LoadingDock ? PRIORITY_LOADING_DOCK :
             PRIORITY_SHELF_RESTOCK;
    }

    protected virtual void HandleFailedRoute(Behaviour behaviour, StateData state, PrioritizedRoute route, string reason)
    {
      FailedRoutes[route] = (Time.time, reason);
      ReleaseRouteLocks(route);
      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, EState.Idle, $"Failed route: {reason}");
    }

    protected virtual void ReleaseRouteLocks(PrioritizedRoute route)
    {
      route.PickupSlots?.RemoveLock();
      route.DeliverySlots?.RemoveLock();
      route.InventorySlot?.RemoveLock();
    }

    // Integrated from HandlerBehaviourExtensions
    protected static List<ItemSlot> GetOutputSlotsContainingTemplateItem(ITransitEntity entity, ItemInstance item)
    {
      if (entity == null || item == null)
        return new List<ItemSlot>();

      var slots = entity.OutputSlots
          .Where(s => s != null && s.ItemInstance != null && s.ItemInstance.CanStackWith(item, false) && s.Quantity > 0 && !s.IsLocked)
          .ToList();

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetOutputSlotsContainingTemplateItem: Found {slots.Count} slots for {item.ID}",
          DebugLogger.Category.AllEmployees);
      return slots;
    }

    protected ITransitEntity FindPackagingStation(IEmployeeAdapter employee, ItemInstance item)
    {

      // Logic to find a packaging station that accepts the item
      return PropertyStations.GetValueOrDefault(employee.AssignedProperty, new List<IStationAdapter>())
          .FirstOrDefault(s => s is PackagingStation p && CanAcceptItem(p, item))?.TransitEntity;
    }
  }

  // Class: RouteQueueManager
  // Purpose: Manages prioritized item transfer routes for a Packager NPC, handling pickup and delivery tasks.
  // Dependencies: Packager, TransitRoute, ItemInstance, ItemSlot, NavMeshUtility, CoroutineRunner, DebugLogger
  // Assumptions: Packager has valid Inventory and Movement components; routes are processed sequentially.
  public class RouteQueueManager
  {
    public static readonly int MAX_ROUTES_PER_CYCLE = 5;
    public const int PRIORITY_STATION_REFILL = 100;
    public const int PRIORITY_LOADING_DOCK = 50;
    public const int PRIORITY_SHELF_RESTOCK = 10;
    public static readonly Dictionary<MoveItemBehaviour, ItemSlot> _reservedSlots = new();

    public static Dictionary<Property, List<ItemInstance>> NoDestinationCache = new();
    private readonly NPC _packager;
    public readonly List<PrioritizedRoute> _routeQueue = new();
    private readonly Dictionary<PrioritizedRoute, (float Time, string Reason)> _failedRoutes = new();
    public static readonly Dictionary<Guid, float> _stateStartTimes = new();
    public int RouteCount => _routeQueue.Count;
    public bool HasInventoryRoute() => _routeQueue.Any(r => r.TransitRoute == null);

    // Constructor
    // Purpose: Initializes a RouteQueueManager for a specific Packager NPC.
    // Parameters:
    //   - packager: The Packager NPC to manage routes for.
    // Throws: ArgumentNullException if packager is null.
    public RouteQueueManager(NPC packager)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
    }

    // Method: GetPriority
    // Purpose: Calculates the priority of a transfer request based on its locations.
    // Parameters:
    //   - request: The TransferRequest to evaluate.
    // Returns: The priority value (PRIORITY_STATION_REFILL, PRIORITY_LOADING_DOCK, or PRIORITY_SHELF_RESTOCK).
    public static int GetPriority(TransferRequest request)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetPriority: Calculating priority for TransferRequest with PickupLocation={request.PickupLocation.GUID}, DeliveryLocation={request.DeliveryLocation.GUID}",
          DebugLogger.Category.Packager);
      int priority = request.DeliveryLocation is IStationAdapter
          ? PRIORITY_STATION_REFILL
          : request.PickupLocation is LoadingDock
              ? PRIORITY_LOADING_DOCK
              : PRIORITY_SHELF_RESTOCK;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetPriority: Returning priority={priority} for TransferRequest",
          DebugLogger.Category.Packager);
      return priority;
    }

    // Method: AddRoutes
    // Purpose: Adds a list of transfer requests to the route queue, prioritizing based on destination type.
    // Parameters:
    //   - requests: The list of TransferRequest objects to add.
    public void AddRoutes(List<TransferRequest> requests)
    {
      _routeQueue.Clear();
      int availableSlots = _packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      var filteredRequests = requests
          .Where(r => !_failedRoutes.Any(t => t.Key.Item.CanStackWith(r.Item, false) || Time.time > t.Value.Time + PackagerPatch.RETRY_DELAY))
          .OrderByDescending(GetPriority)
          .Take(availableSlots)
          .ToList();
      foreach (var request in filteredRequests)
      {
        int priority = GetPriority(request);
        if (request.PickupSlots.Count > 0)
        {
          request.PickupSlots.ApplyLocks(_packager, "Route pickup lock");
        }
        request.DeliverySlots.ApplyLocks(_packager, "Route destination lock");
        _routeQueue.Add(new PrioritizedRoute(request, priority));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"AddRoutes: Added route for {request.Item.ID} (qty={request.Quantity}) from {request.PickupLocation?.GUID} to {request.DeliveryLocation.GUID}, locked slots for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
      }
    }

    // Method: PickupItemsFromLocation
    // Purpose: Initiates item pickup from a specified location for a list of routes.
    // Parameters:
    //   - pickupLocation: The ITransitEntity to pick up items from.
    //   - routes: The list of PrioritizedRoute objects to process.
    public void PickupItemsFromLocation(ITransitEntity pickupLocation, List<PrioritizedRoute> routes)
    {
      try
      {
        if (routes.Count == 0 || _packager == null || pickupLocation == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"PickupItemsFromLocation: Invalid input (routes={routes.Count}, packager={_packager?.fullName}, location={pickupLocation?.GUID}) for NPC={_packager?.fullName}",
              DebugLogger.Category.Packager);
          PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
          _stateStartTimes[_packager.GUID] = Time.time;
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PickupItemsFromLocation: Attempting to move to {pickupLocation.GUID} with {routes.Count} routes (items={string.Join(", ", routes.Select(r => $"{r.Item.ID} (qty={r.Quantity})"))}) for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        var accessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PickupItemsFromLocation: Setting destination to {pickupLocation.GUID} at position {accessPoint.position} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Moving;
        _stateStartTimes[_packager.GUID] = Time.time;
        _packager.Movement.SetDestination(accessPoint.position);
        CoroutineRunner.Instance.RunCoroutine(WaitForMultiSlotPickup(pickupLocation, routes));
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PickupItemsFromLocation: Exception: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        foreach (var route in routes)
        {
          _failedRoutes[route] = (Time.time, $"Exception: {ex.Message}");
          ReleaseRouteLocks(route);
          _routeQueue.Remove(route);
        }
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        _stateStartTimes[_packager.GUID] = Time.time;
      }
    }

    // Method: WaitForMultiSlotPickup
    // Purpose: Picks up items from multiple slots at a location to fulfill routes.
    // Parameters:
    //   - pickupLocation: The ITransitEntity to pick up from.
    //   - routes: The list of PrioritizedRoute objects to process.
    // Remarks: Supports picking up from multiple slots to meet the required quantity (e.g., 12 and 8 for a total of 20).

    private IEnumerator WaitForMultiSlotPickup(ITransitEntity pickupLocation, List<PrioritizedRoute> routes)
    {
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.PickingUp;
      yield return new WaitUntil(() => NavMeshUtility.IsAtTransitEntity(pickupLocation, _packager, 0.4f));
      var failedRoutes = new List<PrioritizedRoute>();

      try
      {
        foreach (var route in routes)
        {
          if (route.PickupSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"WaitForMultiSlotPickup: No pickup slots for {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                DebugLogger.Category.Packager);
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"No pickup slots at {pickupLocation.GUID}");
            continue;
          }

          int remainingQuantity = route.Quantity;
          foreach (var sourceSlot in route.PickupSlots)
          {
            if (sourceSlot == null || sourceSlot.ItemInstance == null ||
                !sourceSlot.ItemInstance.CanStackWith(route.Item, false))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"WaitForMultiSlotPickup: Invalid source slot for {route.Item.ID} (slotID={sourceSlot?.GetHashCode()}) at {pickupLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Packager);
              continue;
            }

            if (sourceSlot.IsLocked && sourceSlot.ActiveLock.LockOwner != _packager.NetworkObject)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"WaitForMultiSlotPickup: Slot {sourceSlot.GetHashCode()} locked by another owner for {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Packager);
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Slot locked by another owner");
              break;
            }

            sourceSlot.RemoveLock();
            int amountToGrab = Mathf.Min(remainingQuantity, sourceSlot.Quantity);
            if (amountToGrab <= 0)
            {
              sourceSlot.ApplyLocks(_packager, "Post-pickup lock");
              continue;
            }

            try
            {
              sourceSlot.ChangeQuantity(-amountToGrab, true);
              sourceSlot.ApplyLocks(_packager, "Post-pickup lock");
              route.InventorySlot.RemoveLock();
              route.InventorySlot.InsertItem(route.Item.GetCopy(amountToGrab));
              route.InventorySlot.ApplyLocks(_packager, "Post-pickup lock");
              _packager.SetAnimationTrigger_Networked(null, "GrabItem");
              remainingQuantity -= amountToGrab;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"WaitForMultiSlotPickup: Picked up {amountToGrab} of {route.Item.ID} from slot {sourceSlot.GetHashCode()} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Packager);
              if (remainingQuantity <= 0) break;
            }
            catch (Exception ex)
            {
              sourceSlot.ApplyLocks(_packager, "Post-pickup lock");
              DebugLogger.Log(DebugLogger.LogLevel.Error,
                  $"WaitForMultiSlotPickup: Failed to pick up {amountToGrab} of {route.Item.ID}: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
                  DebugLogger.Category.Packager);
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Pickup exception: {ex.Message}");
              break;
            }
          }

          if (remainingQuantity > 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"WaitForMultiSlotPickup: Could not pick up remaining {remainingQuantity} of {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                DebugLogger.Category.Packager);
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"Insufficient quantity in slots");
          }
          else
          {
            _routeQueue.Remove(route);
          }
        }

        foreach (var failedRoute in failedRoutes)
        {
          ReleaseRouteLocks(failedRoute);
          _routeQueue.Remove(failedRoute);
        }

        if (routes.Any(r => !failedRoutes.Contains(r)))
        {
          ProcessDelivery();
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"WaitForMultiSlotPickup: No items picked up, transitioning to Idle for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
          _stateStartTimes[_packager.GUID] = Time.time;
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WaitForMultiSlotPickup: Coroutine exception: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        foreach (var route in routes)
        {
          _failedRoutes[route] = (Time.time, $"Coroutine exception: {ex.Message}");
          ReleaseRouteLocks(route);
          _routeQueue.Remove(route);
        }
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        _stateStartTimes[_packager.GUID] = Time.time;
      }
    }

    // Method: ProcessDelivery
    // Purpose: Initiates delivery of items for the first route in the queue.
    private void ProcessDelivery()
    {
      if (_routeQueue.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ProcessDelivery: No routes to process for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ProcessDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return;
      }

      var route = _routeQueue[0];
      var accessPoint = NavMeshUtility.GetAccessPoint(route.Destination, _packager);
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ProcessDelivery: No access point for {route.Destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        _failedRoutes[route] = (Time.time, $"No access point for {route.Destination.GUID}");
        HandleFailedDelivery(route);
        return;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ProcessDelivery: Moving to {route.Destination.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Moving;
      _packager.Movement.SetDestination(accessPoint.position);
      CoroutineRunner.Instance.RunCoroutine(WaitForDelivery());
    }

    // Method: WaitForDelivery
    // Purpose: Delivers items to multiple destination slots at the target location.
    // Remarks: Distributes items across destination slots based on capacity (e.g., 8 to a slot with 19, 1 to a slot with 0).
    private IEnumerator WaitForDelivery()
    {
      var route = _routeQueue[0];
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Delivering;
      yield return new WaitUntil(() => NavMeshUtility.IsAtTransitEntity(route.Destination, _packager, 0.4f));

      try
      {
        var itemInInventory = route.InventorySlot?.ItemInstance != null && route.InventorySlot.ItemInstance.ID == route.Item.ID ? route.InventorySlot.ItemInstance : null;
        if (itemInInventory == null || itemInInventory.Quantity < route.Quantity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForDelivery: Item {route.Item.ID} not found or insufficient quantity for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          _failedRoutes[route] = (Time.time, "Item not found or insufficient quantity");
          HandleFailedDelivery(route);
          yield break;
        }

        int remainingQuantity = route.Quantity;
        foreach (var destinationSlot in route.DeliverySlots)
        {
          if (destinationSlot.IsLocked && destinationSlot.ActiveLock.LockOwner != _packager.NetworkObject)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"WaitForDelivery: Slot {destinationSlot.GetHashCode()} locked by another owner for {route.Item.ID} at {route.Destination.GUID} for NPC={_packager.fullName}",
                DebugLogger.Category.Packager);
            _failedRoutes[route] = (Time.time, $"Slot locked by another owner");
            HandleFailedDelivery(route);
            yield break;
          }

          destinationSlot.RemoveLock();
          int capacity = destinationSlot.GetCapacityForItem(itemInInventory);
          int depositAmount = Mathf.Min(remainingQuantity, capacity);
          if (depositAmount <= 0)
          {
            destinationSlot.ApplyLocks(_packager, "Post-delivery lock");
            continue;
          }

          destinationSlot.InsertItem(itemInInventory.GetCopy(depositAmount));
          destinationSlot.ApplyLocks(_packager, "Post-delivery lock");
          route.InventorySlot.RemoveLock();
          itemInInventory.ChangeQuantity(-depositAmount);
          route.InventorySlot.ApplyLocks(_packager, "Post-delivery lock");
          remainingQuantity -= depositAmount;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"WaitForDelivery: Delivered {depositAmount} of {route.Item.ID} to slot {destinationSlot.GetHashCode()} at {route.Destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          if (remainingQuantity <= 0) break;
        }

        if (remainingQuantity > 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForDelivery: Could not deliver remaining {remainingQuantity} of {route.Item.ID} to {route.Destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          _failedRoutes[route] = (Time.time, $"Insufficient slot capacity");
          HandleFailedDelivery(route);
          yield break;
        }

        _packager.SetAnimationTrigger_Networked(null, "GrabItem");
        ReleaseRouteLocks(route); // Unlock all slots on successful delivery
        _routeQueue.RemoveAt(0);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"WaitForDelivery: Transitioned to Idle, checking for more routes for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        if (_routeQueue.Count > 0)
        {
          ProcessDelivery();
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WaitForDelivery: Exception: {ex.Message} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        _failedRoutes[route] = (Time.time, $"Exception: {ex.Message}");
        HandleFailedDelivery(route);
      }
    }

    // Method: HandleFailedDelivery
    // Purpose: Handles a failed delivery for a specific item, attempting to return it or reroute to a shelf.
    // Parameters:
    //   - item: The ItemInstance that failed delivery.
    public void HandleFailedDelivery(ItemInstance item)
    {
      var route = _routeQueue.FirstOrDefault(r => r.Item.ID == item.ID);
      if (route.Item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleFailedDelivery: No route found for item {item.ID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return;
      }
      var itemInInventory = route.InventorySlot.ItemInstance;
      if (itemInInventory == null)
      {
        ReleaseRouteLocks(route);
        _routeQueue.Remove(route);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return;
      }
      int returnQuantity = Mathf.Min(itemInInventory.Quantity, route.Quantity);
      foreach (var pickupSlot in route.PickupSlots)
      {
        int capacity = pickupSlot.GetCapacityForItem(itemInInventory);
        int amountToReturn = Mathf.Min(returnQuantity, capacity);
        if (amountToReturn <= 0) continue;
        pickupSlot.ChangeQuantity(amountToReturn);
        itemInInventory.ChangeQuantity(-amountToReturn);
        returnQuantity -= amountToReturn;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleFailedDelivery: Returned {amountToReturn} of {route.Item.ID} to pickup slot {pickupSlot.GetHashCode()} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        if (returnQuantity <= 0) break;
      }
      if (returnQuantity <= 0)
      {
        ReleaseRouteLocks(route);
        _routeQueue.Remove(route);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle after return for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return;
      }
    }


    // Method: HandleFailedDelivery
    // Purpose: Handles a failed delivery for a specific route, attempting to return it or reroute to a shelf.
    // Parameters:
    //   - route: The PrioritizedRoute that failed.
    public void HandleFailedDelivery(PrioritizedRoute route)
    {
      var itemInInventory = route.InventorySlot.ItemInstance;
      if (itemInInventory == null)
      {
        ReleaseRouteLocks(route);
        _routeQueue.Remove(route);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return;
      }

      // Attempt to return to pickup slots
      if (route.PickupSlots?.Count > 0)
      {
        int returnQuantity = Mathf.Min(itemInInventory.Quantity, route.Quantity);
        foreach (var pickupSlot in route.PickupSlots)
        {
          pickupSlot.RemoveLock();
          int capacity = pickupSlot.GetCapacityForItem(itemInInventory);
          int amountToReturn = Mathf.Min(returnQuantity, capacity);
          if (amountToReturn <= 0)
          {
            pickupSlot.ApplyLocks(_packager, "Post-return lock");
            continue;
          }

          pickupSlot.ChangeQuantity(amountToReturn);
          pickupSlot.ApplyLocks(_packager, "Post-return lock");
          route.InventorySlot.RemoveLock();
          itemInInventory.ChangeQuantity(-amountToReturn);
          route.InventorySlot.ApplyLocks(_packager, "Post-return lock");
          returnQuantity -= amountToReturn;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleFailedDelivery: Returned {amountToReturn} of {route.Item.ID} to pickup slot {pickupSlot.GetHashCode()} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          if (returnQuantity <= 0) break;
        }

        if (returnQuantity <= 0)
        {
          ReleaseRouteLocks(route);
          _routeQueue.Remove(route);
          PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"HandleFailedDelivery: Transitioned to Idle after return for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          return;
        }
      }
    }

    // Method: HandlePlayerInsertedItem
    // Purpose: Processes items inserted into the NPC’s inventory by a player, routing them to appropriate shelves.
    // Parameters:
    //   - item: The ItemInstance inserted by the player.
    // Returns: True if a route was successfully added, false otherwise.
    public bool HandleInventoryItem(ItemInstance item)
    {
      if (item == null || item.Quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleInventoryItem: Invalid item or quantity for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return false;
      }
      var destination = FindShelfForDelivery(_packager, item);
      if (destination == null)
      {
        var employee = GetEmployee(_packager);
        NoDestinationCache[employee.AssignedProperty].Add(item);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
        $"HandleInventoryItem: No accessible shelf for item {item.ID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return false;
      }
      var inventorySlots = _packager.Inventory.ItemSlots
          .Where(s => s != null && s.ItemInstance != null && s.ItemInstance.CanStackWith(item) && s.Quantity > 0)
          .ToList();
      bool success = false;
      foreach (var slot in inventorySlots)
      {
        var destinationSlots = (destination as ITransitEntity).ReserveInputSlotsForItem(item, _packager.NetworkObject);
        if (destinationSlots == null || destinationSlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"HandleInventoryItem: No delivery slots for item {item.ID} on {destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          continue;
        }
        int quantity = slot.Quantity; // Respect stack limit (20)
        if (quantity <= 0)
        {
          destinationSlots.RemoveLock();
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"HandleInventoryItem: No quantity to transfer for item {item.ID} in slot {slot.GetHashCode()} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          continue;
        }
        try
        {
          AddInventoryRoute(slot, item, quantity, destination, destinationSlots);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleInventoryItem: Added route for {quantity} of {item.ID} from slot {slot.GetHashCode()} to {destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          success = true;
        }
        catch (Exception ex)
        {
          destinationSlots.RemoveLock();
          DebugLogger.Log(DebugLogger.LogLevel.Error,
          $"HandleInventoryItem: Failed to add route for {item.ID}: {ex.Message} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
        }
      }

      return success;
    }

    // Method: AddInventoryRoute
    // Purpose: Adds an inventory route for an item in the NPC’s inventory, using inventory slots as pickup slots.
    // Parameters:
    //   - item: The ItemInstance to route.
    //   - quantity: The quantity to transfer.
    //   - destination: The target ITransitEntity.
    //   - destinationSlots: The reserved delivery slots.
    // Returns: A PrioritizedRoute if successful; throws ArgumentException on invalid input.
    // Remarks: Uses NPC inventory slots as PickupSlots and sets PickupLocation to null.
    private PrioritizedRoute AddInventoryRoute(ItemSlot inventorySlot, ItemInstance item, int quantity, ITransitEntity destination, List<ItemSlot> destinationSlots)
    {
      if (item == null || destination == null || destinationSlots == null || destinationSlots.Count == 0 || inventorySlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AddInventoryRoute: Invalid input for item={item?.ID}, destination={destination?.GUID}, slots={destinationSlots?.Count}, inventorySlot={inventorySlot?.GetHashCode()} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        throw new ArgumentException("Invalid input parameters for AddInventoryRoute");
      }
      if (quantity <= 0 || quantity > inventorySlot.Quantity || quantity > item.StackLimit)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
        $"AddInventoryRoute: Invalid quantity {quantity} for {item.ID} (slot qty={inventorySlot.Quantity}, stack limit={item.StackLimit}) to {destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        throw new ArgumentException($"Invalid quantity: {quantity}");
      }

      if (quantity > destination.GetInputCapacityForItem(item, _packager))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AddInventoryRoute: Quantity {quantity} exceeds capacity for {item.ID} at {destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        throw new ArgumentException($"Quantity exceeds destination capacity");
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AddInventoryRoute: Found locked inventory slots for item {item.ID} (requested={quantity}) for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AddInventoryRoute: Creating TransferRequest with {destinationSlots.Count} delivery slots for item {item.ID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        var request = new TransferRequest(item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, destinationSlots);
        var route = new PrioritizedRoute(request, 100);
        _routeQueue.Add(route);
        inventorySlot.ApplyLocks(_packager, "Inventory route inventory lock");
        destinationSlots.ApplyLocks(_packager, "Inventory route destination lock");
        DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"AddInventoryRoute: Added inventory route for {quantity} of {item.ID} from slot {inventorySlot.GetHashCode()} to {destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        return route;
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"AddInventoryRoute: Failed to add route for {item.ID}: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        throw;
      }
    }

    // Method: ProcessInventoryRoute
    // Purpose: Processes the first inventory route in the queue, initiating delivery.
    public void ProcessInventoryRoute()
    {
      if (_routeQueue.Count == 0 || _routeQueue[0].TransitRoute != null)
      {
        TransitionToIdle("No inventory routes");
        return;
      }
      var route = _routeQueue[0];
      var deliveryLocation = route.Destination;
      if (deliveryLocation == null)
      {
        HandleRouteFailure(route, "Delivery location is null");
        return;
      }
      try
      {
        var accessPoint = NavMeshUtility.GetAccessPoint(deliveryLocation, _packager);
        if (accessPoint == null)
        {
          HandleRouteFailure(route, $"No accessible access point for {deliveryLocation.GUID}");
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ProcessInventoryRoute: Moving to {deliveryLocation.GUID} for {route.Item.ID} (qty={route.Quantity}) for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Moving;
        _stateStartTimes[_packager.GUID] = Time.time;
        _packager.Movement.SetDestination(accessPoint.position);

        // Filter inventory routes for the same destination
        var routes = _routeQueue.Where(r => r.TransitRoute == null && r.Destination == deliveryLocation).ToList();
        CoroutineRunner.Instance.RunCoroutine(WaitForInventoryDelivery(deliveryLocation, routes));
      }
      catch (Exception ex)
      {
        HandleRouteFailure(route, $"Exception: {ex.Message}");
      }
    }

    // Helper method to transition to Idle state
    private void TransitionToIdle(string reason)
    {
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
      _stateStartTimes[_packager.GUID] = Time.time;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"TransitionToIdle: {reason} for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
    }

    // Helper method to handle route failure
    private void HandleRouteFailure(PrioritizedRoute route, string reason)
    {
      _failedRoutes[route] = (Time.time, reason);
      ReleaseRouteLocks(route);
      _routeQueue.Remove(route);
      TransitionToIdle($"Failed route for {route.Item.ID}: {reason}");
    }

    // Method: WaitForInventoryDelivery
    // Purpose: Delivers items from NPC inventory to multiple destination slots.
    // Parameters:
    //   - deliveryLocation: The ITransitEntity to deliver to.
    //   - routes: The list of inventory routes to process.
    // Remarks: Sources items from multiple inventory slots and distributes to destination slots.
    private IEnumerator WaitForInventoryDelivery(ITransitEntity deliveryLocation, List<PrioritizedRoute> routes)
    {
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Delivering;
      float timeout = Time.time + 30f;
      while (!NavMeshUtility.IsAtTransitEntity(deliveryLocation, _packager, 0.4f) && Time.time < timeout)
        yield return null;
      if (Time.time >= timeout)
      {
        foreach (var route in routes)
          HandleRouteFailure(route, $"Timed out reaching {deliveryLocation.GUID}");
        yield break;
      }
      var failedRoutes = new List<PrioritizedRoute>();
      try
      {
        foreach (var route in routes.ToList())
        {
          if (!ValidateRouteSlots(route, out var sourceSlot))
          {
            HandleRouteFailure(route, $"Validation Failure");
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"Validation Failure");
            continue;
          }
          int remainingQuantity = route.Quantity;
          foreach (var destinationSlot in route.DeliverySlots)
          {
            if (destinationSlot.IsLocked && destinationSlot.ActiveLock.LockOwner != _packager.NetworkObject)
            {
              HandleRouteFailure(route, $"Destination slot {destinationSlot.GetHashCode()} locked by another owner");
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Destination slot {destinationSlot.GetHashCode()} locked by another owner");
              break;
            }
            destinationSlot.RemoveLock();
            int depositAmount = Mathf.Min(remainingQuantity, destinationSlot.GetCapacityForItem(route.Item));
            if (depositAmount <= 0)
              continue;
            try
            {
              destinationSlot.InsertItem(route.Item.GetCopy(depositAmount));
              sourceSlot.RemoveLock();
              sourceSlot.ItemInstance.ChangeQuantity(-depositAmount);
              remainingQuantity -= depositAmount;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"WaitForInventoryDelivery: Deposited {depositAmount} of {route.Item.ID} to slot {destinationSlot.GetHashCode()} at {deliveryLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Packager);
              if (remainingQuantity <= 0) break;
            }
            catch (Exception ex)
            {
              destinationSlot.ApplyLocks(_packager, "Post-delivery lock");
              sourceSlot.ApplyLocks(_packager, "Post-delivery lock");
              HandleRouteFailure(route, $"Deposit exception: {ex.Message}");
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Deposit exception: {ex.Message}");
              break;
            }
          }
          if (remainingQuantity > 0)
          {
            HandleRouteFailure(route, $"Insufficient slot capacity: {remainingQuantity} remaining");
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"Insufficient slot capacity: {remainingQuantity} remaining");
          }
          else
          {
            ReleaseRouteLocks(route); // Unlock all slots on successful delivery
            _routeQueue.Remove(route);
          }
        }

        foreach (var failedRoute in failedRoutes)
        {
          ReleaseRouteLocks(failedRoute);
          _routeQueue.Remove(failedRoute);
        }
        TransitionToIdle($"Completed delivery to {deliveryLocation.GUID}");
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WaitForInventoryDelivery: Exception: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        foreach (var route in routes)
          HandleRouteFailure(route, $"Coroutine exception: {ex.Message}");
      }
    }

    // Helper method to validate route slots
    private bool ValidateRouteSlots(PrioritizedRoute route, out ItemSlot sourceSlot)
    {
      sourceSlot = route.InventorySlot;
      if (route.PickupSlots == null || route.PickupSlots.Count == 0 || !route.PickupSlots.Any(s => s?.ItemInstance != null && s.ItemInstance.CanStackWith(route.Item, false)))
      {
        HandleRouteFailure(route, $"No valid inventory slots for {route.Item.ID}");
        return false;
      }
      if (sourceSlot.IsLocked && sourceSlot.ActiveLock.LockOwner != _packager.NetworkObject)
      {
        HandleRouteFailure(route, $"Inventory slot {sourceSlot.GetHashCode()} locked by another owner");
        return false;
      }
      return true;
    }

    public void ProcessRoutes()
    {
      if (_routeQueue.Count == 0 || _routeQueue[0].TransitRoute == null)
      {
        TransitionToIdle("No regular routes");
        return;
      }
      var route = _routeQueue[0];
      var pickupLocation = route.TransitRoute.Source;
      var deliveryLocation = route.TransitRoute.Destination;
      try
      {
        var accessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
        if (accessPoint == null)
        {
          HandleRouteFailure(route, $"No accessible access point for {pickupLocation.GUID}");
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ProcessRoutes: Processing route for {route.Item.ID} (qty={route.Quantity}) from {pickupLocation.GUID} to {deliveryLocation.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Packager);
        PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Moving;
        PackagerPatch._stateStartTimes[_packager.GUID] = Time.time;
        _packager.Movement.SetDestination(accessPoint.position);
        CoroutineRunner.Instance.RunCoroutine(WaitForRouteProcessing(route, pickupLocation, deliveryLocation));
      }
      catch (Exception ex)
      {
        HandleRouteFailure(route, $"Exception: {ex.Message}");
      }
    }

    private IEnumerator WaitForRouteProcessing(PrioritizedRoute route, ITransitEntity pickupLocation, ITransitEntity deliveryLocation)
    {
      var pickupAccessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
      if (pickupAccessPoint == null)
      {
        HandleRouteFailure(route, $"Invalid or unreachable access point for {pickupLocation.GUID}");
        yield break;
      }
      float moveTimeout = Time.time + 30f;
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Moving;
      PackagerPatch._stateStartTimes[_packager.GUID] = Time.time;
      _packager.Movement.SetDestination(pickupAccessPoint.position);
      while (_packager.Movement.IsMoving && Time.time < moveTimeout)
        yield return null;
      if (Time.time >= moveTimeout || Vector3.Distance(_packager.transform.position, pickupAccessPoint.position) > 2f)
      {
        HandleRouteFailure(route, $"Failed to reach pickup location {pickupLocation.GUID} (distance={Vector3.Distance(_packager.transform.position, pickupAccessPoint.position)})");
        yield break;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"WaitForRouteProcessing: Reached pickup location {pickupLocation.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.PickingUp;
      PackagerPatch._stateStartTimes[_packager.GUID] = Time.time;
      int remainingQuantity = route.Quantity;
      foreach (var slot in route.PickupSlots)
      {
        if (slot == null || slot.Quantity <= 0 || slot.IsLocked)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForRouteProcessing: Skipping invalid slot for {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          continue;
        }
        int quantityToTake = Mathf.Min(slot.Quantity, remainingQuantity);
        if (quantityToTake <= 0) continue;
        try
        {
          slot.RemoveLock();
          var itemInstance = slot.ItemInstance.GetCopy(quantityToTake);

          route.InventorySlot.RemoveLock();
          route.InventorySlot.InsertItem(itemInstance);
          route.InventorySlot.ApplyLocks(_packager, "Route Inventory Slot");
          slot.ChangeQuantity(-quantityToTake);
          slot.ApplyLocks(_packager, "Route Return Slot");
          remainingQuantity -= quantityToTake;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"WaitForRouteProcessing: Picked up {quantityToTake} of {route.Item.ID} from slot at {pickupLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          if (remainingQuantity <= 0) break;
        }
        catch (Exception ex)
        {
          HandleRouteFailure(route, $"Pickup failed: {ex.Message}");
          yield break;
        }
      }
      if (remainingQuantity > 0)
      {
        HandleRouteFailure(route, $"Insufficient items picked up: {remainingQuantity} remaining");
        yield break;
      }
      var deliveryAccessPoint = NavMeshUtility.GetAccessPoint(deliveryLocation, _packager);
      if (deliveryAccessPoint == null)
      {
        HandleRouteFailure(route, $"No accessible access point for {deliveryLocation.GUID}");
        yield break;
      }
      moveTimeout = Time.time + 30f;
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Moving;
      PackagerPatch._stateStartTimes[_packager.GUID] = Time.time;
      _packager.Movement.SetDestination(deliveryAccessPoint.position);
      while (_packager.Movement.IsMoving && Time.time < moveTimeout)
        yield return null;
      if (Time.time >= moveTimeout || Vector3.Distance(_packager.transform.position, deliveryAccessPoint.position) > 1f)
      {
        HandleRouteFailure(route, $"Failed to reach delivery location {deliveryLocation.GUID}");
        yield break;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"WaitForRouteProcessing: Reached delivery location {deliveryLocation.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Delivering;
      PackagerPatch._stateStartTimes[_packager.GUID] = Time.time;
      foreach (var slot in route.DeliverySlots)
      {
        if (slot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForRouteProcessing: Skipping invalid delivery slot for {route.Item.ID} at {deliveryLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          continue;
        }
        try
        {
          slot.RemoveLock();
          slot.InsertItem(route.Item.GetCopy(route.Quantity));
          route.InventorySlot.RemoveLock();
          route.InventorySlot.ChangeQuantity(-route.Quantity);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"WaitForRouteProcessing: Delivered {route.Quantity} of {route.Item.ID} to slot at {deliveryLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Packager);
          break;
        }
        catch (Exception ex)
        {
          HandleRouteFailure(route, $"Delivery failed: {ex.Message}");
          yield break;
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"WaitForRouteProcessing: Completed route for {route.Item.ID} from {pickupLocation.GUID} to {deliveryLocation.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
      ReleaseRouteLocks(route);
      _routeQueue.RemoveAt(0);
      TransitionToIdle("Route completed");
    }

    // Method: ReleaseRouteLocks
    // Purpose: Releases all locks associated with a specific route, clearing destination and pickup slots.
    // Parameters:
    //   - route: The PrioritizedRoute whose locks should be released.
    private void ReleaseRouteLocks(PrioritizedRoute route)
    {
      route.DeliverySlots?.RemoveLock();
      route.PickupSlots?.RemoveLock();
      route.InventorySlot?.RemoveLock();
    }

    // Method: Clear
    // Purpose: Clears all routes, locked slots, and reservations for the NPC.
    public void Clear()
    {
      foreach (var route in _routeQueue)
        ReleaseRouteLocks(route);
      _routeQueue.Clear();
      _failedRoutes.Clear();
      PackagerPatch._states[_packager.GUID] = PackagerPatch.PackagerState.Idle;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
      $"RouteQueueManager.Clear: Cleared all routes, locks, and failed routes for NPC={_packager.fullName}",
          DebugLogger.Category.Packager);
      EmployeeBehaviour.ActiveBehaviours[_packager.GUID].End();
    }
  }
}