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
using static NoLazyWorkers.Employees.EmployeeExtensions;
using ScheduleOne.Property;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using ScheduleOne.Delivery;
using ScheduleOne.NPCs.Behaviour;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.Structures;
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public static class EmployeeExtensions
  {
    public interface IEmployeeAdapter
    {
      bool HandleIdle(Behaviour behaviour, StateData state) => false;
      bool HandlePlanning(Behaviour behaviour, StateData state) => false;
      bool HandleMoving(Behaviour behaviour, StateData state) => false;
      bool HandleGrabbing(Behaviour behaviour, StateData state) => false;
      bool HandleInserting(Behaviour behaviour, StateData state) => false;
      bool HandleOperating(Behaviour behaviour, StateData state) => false;
      bool HandleCompleted(Behaviour behaviour, StateData state) => false;
      bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item) => false;
      Property AssignedProperty { get; }
      NpcSubType SubType { get; }
      bool GetEmployeeBehaviour(NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour);
    }
    public static readonly Dictionary<Type, Func<NPC, Behaviour>> StationTypeToBehaviourMap = new()
    {
        { typeof(MixingStation), npc => (npc as Chemist)?.StartMixingStationBehaviour },
        { typeof(MixingStationMk2), npc => (npc as Chemist)?.StartMixingStationBehaviour },
        // Add other station types as needed
        // { typeof(ChemistryStation), npc => (npc as Chemist)?.StartChemistryStationBehaviour },
        // { typeof(LabOven), npc => (npc as Chemist)?.StartLabOvenBehaviour },
        // { typeof(Cauldron), npc => (npc as Chemist)?.StartCauldronBehaviour },
        { typeof(PackagingStation), npc => (npc as Packager)?.PackagingBehaviour }
    };
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
      Planning,     // Plan routes or select items to fetch
      Moving,             // Move to a location (shelf, station, dock)
      Grabbing,          // Pick up items from slots
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
      public int QuantityWanted { get; set; }
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
      public AdvancedTransitRoute TransitRoute;

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
        TransitRoute = request.PickupLocation != null ? new AdvancedTransitRoute(request.PickupLocation, request.DeliveryLocation) : null;
      }
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

      public TransferRequest(NPC npc, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickupLocation, List<ItemSlot> pickupSlots,
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

        inventorySlot.ApplyLock(npc.NetworkObject, "Route inventory lock");
        deliverySlots.ApplyLocks(npc, "Route delivery lock");
      }
    }

    public static void ClearAll()
    {
      MixingRoutes.Clear();
      QualityFields.Clear();
      StationAdapterBehaviours.Clear();
      ActiveBehaviours.Clear();
      EmployeeAdapters.Clear();
      NoDestinationCache.Clear();
      TimedOutItems.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, "EmployeeExtensions: Cleared all dictionaries", DebugLogger.Category.AllEmployees);
    }

    public static Dictionary<Guid, IEmployeeAdapter> EmployeeAdapters = new();
    public static Dictionary<IStationAdapter, Behaviour> StationAdapterBehaviours = new();
    public static Dictionary<Guid, MoveItemBehaviour> ActiveMoveItemBehaviours = new();
    public static Dictionary<Guid, EmployeeBehaviour> ActiveBehaviours = new();
    public static Dictionary<MoveItemBehaviour, ItemSlot> ReservedSlots = new();
    public static Dictionary<Property, List<ItemInstance>> NoDestinationCache = new();
    public static Dictionary<Property, Dictionary<ItemInstance, float>> TimedOutItems = new();

  }
  public static class EmployeeUtilities
  {
    public static bool IsItemTimedOut(Property property, ItemInstance item)
    {
      if (!TimedOutItems.TryGetValue(property, out var timedOutItems))
        return false;
      var key = timedOutItems.Keys.FirstOrDefault(i => i.CanStackWith(item, false));
      if (key == null)
        return false;
      if (Time.time >= timedOutItems[key])
      {
        timedOutItems.Remove(key);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsItemTimedOut: Removed expired timeout for item {item.ID} in property {property}", DebugLogger.Category.Packager);
        return false;
      }
      return true;
    }

    public static void AddItemTimeout(Property property, ItemInstance item)
    {
      if (!TimedOutItems.ContainsKey(property))
        TimedOutItems[property] = new Dictionary<ItemInstance, float>();
      var key = TimedOutItems[property].Keys.FirstOrDefault(i => i.CanStackWith(item, false)) ?? item;
      TimedOutItems[property][key] = Time.time + 30f;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AddItemTimeout: Timed out item {item.ID} for 30s in property {property}", DebugLogger.Category.Packager);
    }

    public static void RegisterEmployeeAdapter(NPC npc, IEmployeeAdapter adapter)
    {
      EmployeeAdapters[npc.GUID] = adapter;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Registered adapter for NPC {npc.fullName}, type={adapter.SubType}", DebugLogger.Category.AllEmployees);
    }

    public static IEmployeeAdapter GetEmployee(NPC npc)
    {
      if (EmployeeAdapters.TryGetValue(npc.GUID, out var adapter))
        return adapter;
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"No adapter found for NPC {npc.fullName}", DebugLogger.Category.AllEmployees);
      return null;
    }

    public static void RegisterStationBehaviour(Behaviour behaviour, IStationAdapter adapter)
    {
      StationAdapterBehaviours[adapter] = behaviour;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Registered station adapter for behaviour {behaviour.GetHashCode()}", DebugLogger.Category.AllEmployees);
    }

    public static ITransitEntity FindPackagingStation(IEmployeeAdapter employee, ItemInstance item)
    {
      if (employee == null || item == null) return null;
      return PropertyStations.GetValueOrDefault(employee.AssignedProperty)
          .FirstOrDefault(s => s is PackagingStation p && !s.IsInUse && s.CanRefill(item))?.TransitEntity;
    }

    public static void ApplyLocks(this List<ItemSlot> slots, NPC npc, string reason)
    {
      foreach (var slot in slots.Where(s => s != null))
        slot.ApplyLock(npc.NetworkObject, reason);
    }

    public static void ApplyLocks(this ItemSlot slot, NPC npc, string reason)
    {
      slot.ApplyLock(npc.NetworkObject, reason);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ApplyLock: Locked slot {slot.GetHashCode()} for {reason} by NPC={npc.fullName}", DebugLogger.Category.Packager);
    }

    public static void RemoveLock(this List<ItemSlot> slots)
    {
      foreach (var slot in slots.Where(s => s != null))
      {
        slot.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RemoveLock: Unlocked slot {slot.GetHashCode()}", DebugLogger.Category.Packager);
      }
    }

    // Method: SetReservedSlot
    // Purpose: Assigns a reserved slot to a MoveItemBehaviour instance for item pickup.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to associate with the slot.
    //   - slot: The ItemSlot to reserve.
    public static void SetReservedSlot(MoveItemBehaviour behaviour, ItemSlot slot)
    {
      ReservedSlots[behaviour] = slot;
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
      ReservedSlots.TryGetValue(behaviour, out var slot);
      return slot;
    }

    // Method: ReleaseReservations
    // Purpose: Releases the reserved slot for a MoveItemBehaviour instance, unlocking it.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to release.
    public static void ReleaseReservations(MoveItemBehaviour behaviour)
    {
      if (ReservedSlots.TryGetValue(behaviour, out var slot) && slot != null)
      {
        slot.RemoveLock();
        ReservedSlots.Remove(behaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ReleaseReservations: Released slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}",
            DebugLogger.Category.Packager);
      }
    }
  }

  public abstract class EmployeeBehaviour
  {
    protected readonly NPC Npc;
    protected readonly IEmployeeAdapter Adapter;
    protected readonly Dictionary<EState, Action<Behaviour, StateData>> StatePackagers;
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
      ActiveBehaviours[npc.GUID] = this;
      Npc = npc ?? throw new ArgumentNullException(nameof(npc));
      Adapter = adapter;
      EmployeeUtilities.RegisterEmployeeAdapter(npc, adapter);
      StatePackagers = new Dictionary<EState, Action<Behaviour, StateData>>
      {
          { EState.Idle, HandleIdle },
          { EState.Planning, HandlePlanning },
          { EState.Moving, HandleMoving },
          { EState.Grabbing, HandleGrabbing },
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
      StatePackagers[state.CurrentState](behaviour, state);
    }

    protected void TransitionState(Behaviour behaviour, StateData state, EState newState, string reason)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"TransitionState: {Npc.fullName} from {state.CurrentState} to {newState}, reason={reason}, invQty={state.QuantityInventory}, routes={state.ActiveRoutes.Count}", DebugLogger.Category.AllEmployees);
      state.CurrentState = newState;
      StateStartTimes[Npc.GUID] = Time.time;
    }

    public virtual Behaviour GetInstancedBehaviour(NPC npc, IStationAdapter station)
    {
      if (npc == null || station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetInstancedBehaviour: NPC or station is null", DebugLogger.Category.AllEmployees);
        return null;
      }

      Type stationType = station.TypeOf;
      if (!StationTypeToBehaviourMap.TryGetValue(stationType, out var behaviourRetriever))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetInstancedBehaviour: No behaviour mapped for station type {stationType.Name}", DebugLogger.Category.AllEmployees);
        return null;
      }

      var behaviour = behaviourRetriever(npc);
      if (behaviour == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetInstancedBehaviour: Behaviour is null for NPC {npc.fullName} and station type {stationType.Name}", DebugLogger.Category.AllEmployees);
        return null;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetInstancedBehaviour: Retrieved behaviour for NPC {npc.fullName} and station {station.GUID}", DebugLogger.Category.AllEmployees);
      return behaviour;
    }

    protected virtual void HandleIdle(Behaviour behaviour, StateData state)
    {
      (behaviour.Npc as Employee).SetIdle(true);
      state.Station = StationUtilities.GetStation(behaviour);
      if (state.Station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleIdle: No station for {Npc.fullName}, disabling", DebugLogger.Category.AllEmployees);
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
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleIdle: Added inventory route for {slot.ItemInstance.ID} (qty={slot.Quantity})", DebugLogger.Category.AllEmployees);
          }
          else
          {
            slot.RemoveLock();
          }
        }
      }
      //TransitionState(behaviour, state, EState.Planning, "Start route planning");
    }

    public virtual void HandlePlanning(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandlePlanning(behaviour, state) == true) return;
      state.ActiveRoutes.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandlePlanning: No routes planned for NPC {Npc.fullName}, transitioning to Idle", DebugLogger.Category.AllEmployees);
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
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleMoving: Timeout moving to {state.Destination?.Name}, to Idle", DebugLogger.Category.AllEmployees);
          TransitionState(behaviour, state, EState.Idle, "Movement timeout");
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleMoving: Reached {state.Destination?.Name}", DebugLogger.Category.AllEmployees);
          StatePackagers[state.CurrentState](behaviour, state);
        }
      }
    }

    protected virtual void HandleGrabbing(Behaviour behaviour, StateData state)
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
      int remaining = route.Quantity;
      foreach (var slot in state.PickupSlots)
      {
        if (slot.Quantity <= 0 || (slot.IsLocked && slot.ActiveLock.LockOwner != Npc.NetworkObject))
          continue;
        int amount = Mathf.Min(slot.Quantity, remaining);
        if (amount <= 0) continue;
        slot.RemoveLock();
        slot.ChangeQuantity(-amount);
        route.InventorySlot.RemoveLock();
        route.InventorySlot.InsertItem(route.Item.GetCopy(amount));
        slot.ApplyLock(behaviour.Npc.NetworkObject, "Grabbing");
        route.InventorySlot.ApplyLock(behaviour.Npc.NetworkObject, "Grabbing");
        remaining -= amount;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleGrabbing: Picked up {amount} of {route.Item.ID} from slot {slot.GetHashCode()}", DebugLogger.Category.AllEmployees);
        if (remaining <= 0) break;
      }
      state.QuantityInventory += route.Quantity - remaining;

      if (remaining > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleGrabbing: Insufficient items, {remaining} remaining", DebugLogger.Category.AllEmployees);
        HandleFailedRoute(behaviour, state, route, "Insufficient items");
        return;
      }

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
      int remaining = state.QuantityInventory;
      foreach (var slot in state.DeliverySlots)
      {
        int capacity = slot.GetCapacityForItem(route.Item);
        int amount = Mathf.Min(remaining, capacity);
        if (amount <= 0) continue;
        slot.RemoveLock();
        slot.InsertItem(route.Item.GetCopy(amount));
        route.InventorySlot.RemoveLock();
        route.InventorySlot.ChangeQuantity(-amount);
        remaining -= amount;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInserting: Delivered {amount} of {route.Item.ID} to slot {slot.GetHashCode()}", DebugLogger.Category.AllEmployees);
        if (remaining <= 0) break;
      }
      state.QuantityInventory = remaining;

      if (remaining > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleInserting: Could not deliver {remaining} items", DebugLogger.Category.AllEmployees);
        state.ActiveRoutes.Remove(route);
        HandleFailedRoute(behaviour, state, route, "Insufficient slot capacity");
        return;
      }

      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, state.ActiveRoutes.Count > 0 ? EState.Grabbing : EState.Idle, "Delivery complete");
    }

    protected virtual void HandleOperating(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandleOperating(behaviour, state) == true) return;
      if (!IsAtLocation(behaviour, state.Station.TransitEntity))
      {
        MoveTo(behaviour, state, state.Station.TransitEntity);
        return;
      }

      if (state.Station.HasActiveOperation || state.Station.GetInputQuantity() < state.Station.StartThreshold)
      {
        TransitionState(behaviour, state, EState.Planning, "Cannot start operation");
        return;
      }

      state.Station.StartOperation(behaviour);
      TransitionState(behaviour, state, EState.Completed, "Operation started");
    }


    protected virtual void HandleCompleted(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleCompleted: Operation complete for NPC {Npc.fullName}, transitioning to Idle", DebugLogger.Category.AllEmployees);
      TransitionState(behaviour, state, EState.Idle, "Operation complete");
    }

    protected virtual void MoveTo(Behaviour behaviour, StateData state, ITransitEntity destination)
    {
      if (destination == null || !Npc.Movement.CanGetTo(destination, 1f))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MoveTo: Cannot reach {destination?.Name}, disabling", DebugLogger.Category.AllEmployees);
        Disable(behaviour);
        return;
      }

      state.Destination = destination;
      state.MoveTimeout = 30f;
      state.MoveElapsed = 0f;
      state.IsMoving = true;
      Npc.Movement.SetDestination(NavMeshUtility.GetAccessPoint(destination, Npc).position);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"MoveTo: Moving to {destination.Name}", DebugLogger.Category.AllEmployees);
    }

    protected virtual bool IsAtLocation(Behaviour behaviour, ITransitEntity location)
    {
      if (location == null) return false;
      float distance = Vector3.Distance(Npc.transform.position, NavMeshUtility.GetAccessPoint(location, Npc).position);
      bool atLocation = distance < 0.4f;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsAtLocation: Result={atLocation}, Distance={distance:F2}, location={location.GUID}", DebugLogger.Category.AllEmployees);
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
      (behaviour.Npc as Employee).ShouldIdle();
      behaviour.Disable();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Disable: Behaviour disabled for {Npc.fullName}", DebugLogger.Category.AllEmployees);
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

    public virtual void AddRoutes(Behaviour behaviour, StateData state, List<TransferRequest> requests)
    {
      int availableSlots = behaviour.Npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      var filteredRequests = requests
          .Where(r => !FailedRoutes.Any(t => t.Key.Item.CanStackWith(r.Item, false) && Time.time <= t.Value.Time + RETRY_DELAY))
          .OrderByDescending(GetPriority)
          .Take(availableSlots)
          .ToList();

      foreach (var request in filteredRequests)
      {
        int priority = GetPriority(request); ;
        state.ActiveRoutes.Add(new PrioritizedRoute(request, priority));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"AddRoutes: Added route for {request.Item.ID} (qty={request.Quantity})", DebugLogger.Category.AllEmployees);
      }
    }

    public virtual int GetPriority(TransferRequest request)
    {
      return request.DeliveryLocation is IStationAdapter ? PRIORITY_STATION_REFILL :
             request.PickupLocation is LoadingDock ? PRIORITY_LOADING_DOCK :
             PRIORITY_SHELF_RESTOCK;
    }
  }

  [HarmonyPatch(typeof(MoveItemBehaviour))]
  public static class MoveItemBehaviourPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Awake")]
    public static void AwakePostfix(MoveItemBehaviour __instance)
    {
      ActiveMoveItemBehaviours[__instance.Npc.GUID] = __instance;
    }

    [HarmonyPostfix]
    [HarmonyPatch("End")]
    public static void EndPostfix(MoveItemBehaviour __instance)
    {
      ActiveMoveItemBehaviours.Remove(__instance.Npc.GUID);
      EmployeeUtilities.ReleaseReservations(__instance);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"End: Released reservations for MoveItemBehaviour for NPC={__instance.Npc.fullName}", DebugLogger.Category.AllEmployees);

    }
  }
}