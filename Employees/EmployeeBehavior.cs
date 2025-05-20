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
using static NoLazyWorkers.Employees.EmployeeExtensions;
using ScheduleOne.Property;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using ScheduleOne.Delivery;
using ScheduleOne.NPCs.Behaviour;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.General;
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public static class EmployeeExtensions
  {
    public interface IEmployeeAdapter
    {
      bool HandleIdle(Behaviour behaviour, StateData state);
      bool HandlePlanning(Behaviour behaviour, StateData state);
      bool HandleMoving(Behaviour behaviour, StateData state);
      bool HandleGrabbing(Behaviour behaviour, StateData state);
      bool HandleDelivery(Behaviour behaviour, StateData state);
      bool HandleOperating(Behaviour behaviour, StateData state);
      bool HandleCompleted(Behaviour behaviour, StateData state);
      bool HandleInventoryItems(Behaviour behaviour, StateData state);
      Property AssignedProperty { get; }
      NpcSubType SubType { get; }
      bool GetEmployeeBehaviour(NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour);
    }

    public enum NpcSubType { Chemist, Packager, Botanist, Cleaner, Driver }

    public enum EState
    {
      Idle, Planning, Moving, Grabbing, Delivery, Operating, Completed
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

    public class TransferRequest
    {
      public ItemInstance Item { get; }
      public int Quantity { get; }
      public ItemSlot InventorySlot { get; }
      public ITransitEntity Source { get; }
      public List<ItemSlot> PickupSlots { get; }
      public ITransitEntity Destination { get; }
      public List<ItemSlot> DestinationSlots { get; }

      public TransferRequest(NPC npc, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity source, List<ItemSlot> pickupSlots,
          ITransitEntity destination, List<ItemSlot> destinationSlots)
      {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        Source = source;
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        PickupSlots = pickupSlots != null ? pickupSlots.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList() : new List<ItemSlot>();
        DestinationSlots = destinationSlots != null ? destinationSlots.Where(slot => slot != null).ToList() : throw new ArgumentNullException(nameof(destinationSlots));
        if (Source == null && PickupSlots.Count == 0)
          throw new ArgumentException("No valid pickup slots for inventory route");
        if (DestinationSlots.Count == 0)
          throw new ArgumentException("No valid delivery slots");
        inventorySlot.ApplyLock(npc.NetworkObject, "Route inventory lock");
        destinationSlots.ApplyLocks(npc, "Route delivery lock");
      }
    }

    public struct PrioritizedRoute
    {
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public ITransitEntity Source;
      public List<ItemSlot> PickupSlots;
      public ITransitEntity Destination;
      public List<ItemSlot> DestinationSlots;
      public int Priority;
      public AdvancedTransitRoute TransitRoute;

      public PrioritizedRoute(TransferRequest request, int priority)
      {
        Item = request.Item;
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot;
        Source = request.Source;
        PickupSlots = request.PickupSlots;
        Destination = request.Destination;
        DestinationSlots = request.DestinationSlots;
        Priority = priority;
        TransitRoute = request.Source != null ? new AdvancedTransitRoute(request.Source, request.Destination) : null;
      }
    }

    public static readonly Dictionary<Type, Func<NPC, Behaviour>> StationTypeToBehaviourMap = new()
        {
            { typeof(MixingStation), npc => (npc as Chemist)?.StartMixingStationBehaviour },
            { typeof(MixingStationMk2), npc => (npc as Chemist)?.StartMixingStationBehaviour },
        };

    public static Dictionary<Guid, IEmployeeAdapter> EmployeeAdapters = new();
    public static Dictionary<IStationAdapter, Behaviour> StationAdapterBehaviours = new();
    public static Dictionary<Guid, MoveItemBehaviour> ActiveMoveItemBehaviours = new();
    public static Dictionary<Guid, EmployeeBehaviour> ActiveBehaviours = new();
    public static Dictionary<MoveItemBehaviour, ItemSlot> ReservedSlots = new();
    public static Dictionary<Property, List<ItemInstance>> NoDestinationCache = new();
    public static Dictionary<Property, Dictionary<ItemInstance, float>> TimedOutItems = new();

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
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsItemTimedOut: Removed expired timeout for item {item.ID}", DebugLogger.Category.Packager);
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

    public static void SetReservedSlot(MoveItemBehaviour behaviour, ItemSlot slot)
    {
      ReservedSlots[behaviour] = slot;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Set slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}", DebugLogger.Category.Packager);
    }

    public static ItemSlot GetReservedSlot(MoveItemBehaviour behaviour)
    {
      ReservedSlots.TryGetValue(behaviour, out var slot);
      return slot;
    }

    public static void ReleaseReservations(MoveItemBehaviour behaviour)
    {
      if (ReservedSlots.TryGetValue(behaviour, out var slot) && slot != null)
      {
        slot.RemoveLock();
        ReservedSlots.Remove(behaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: Released slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}", DebugLogger.Category.Packager);
      }
    }

    // New: Centralized route creation method
    public static TransferRequest CreateTransferRequest(NPC npc, ItemInstance item, int quantity, ITransitEntity pickupLocation, List<ItemSlot> pickupSlots,
        ITransitEntity deliveryLocation, List<ItemSlot> deliverySlots, bool force = false)
    {
      if (item == null || quantity <= 0 || deliveryLocation == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateTransferRequest: Invalid parameters for NPC {npc.fullName}", DebugLogger.Category.AllEmployees);
        return null;
      }

      var property = (npc as Employee).AssignedProperty;
      if (property == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateTransferRequest: Property is null for NPC {npc.fullName}", DebugLogger.Category.AllEmployees);
        return null;
      }

      if (!force && IsItemTimedOut(property, item))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateTransferRequest: Item {item.ID} is timed out", DebugLogger.Category.AllEmployees);
        return null;
      }

      var inventorySlot = npc.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (inventorySlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"CreateTransferRequest: No inventory slot for item {item.ID}", DebugLogger.Category.AllEmployees);
        return null;
      }

      if (!npc.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(deliveryLocation, npc).position))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"CreateTransferRequest: Cannot reach destination {deliveryLocation.GUID}", DebugLogger.Category.AllEmployees);
        return null;
      }

      try
      {
        var request = new TransferRequest(npc, item, quantity, inventorySlot, pickupLocation, pickupSlots, deliveryLocation, deliverySlots);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreateTransferRequest: Created request for {quantity} of {item.ID} from {pickupLocation?.GUID.ToString() ?? "inventory"} to {deliveryLocation.GUID}", DebugLogger.Category.AllEmployees);
        return request;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateTransferRequest: Failed for item {item.ID}, error: {e}", DebugLogger.Category.AllEmployees);
        pickupSlots?.RemoveLock();
        deliverySlots?.RemoveLock();
        return null;
      }
    }
  }

  public abstract class EmployeeBehaviour
  {
    protected NPC Npc;
    protected readonly IEmployeeAdapter Adapter;
    protected readonly Dictionary<EState, Action<Behaviour, StateData>> StatePackagers;
    public static readonly Dictionary<Guid, StateData> States = new();
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
          { EState.Delivery, HandleDelivery },
          { EState.Operating, HandleOperating },
          { EState.Completed, HandleCompleted }
      };
      StateStartTimes[npc.GUID] = Time.time;
    }

    public virtual void Update(Behaviour behaviour)
    {
      if (Npc == null || behaviour == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmployeeBehaviour.Update: Npc or behaviour is null for {GetType().Name}", DebugLogger.Category.AllEmployees);
        return;
      }
      if (!States.TryGetValue(Npc.GUID, out var state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmployeeBehaviour.Update: Initializing state for NPC {Npc.fullName}", DebugLogger.Category.AllEmployees);
        States[Npc.GUID] = new StateData();
        state = States[Npc.GUID];
      }

      // Existing Update logic follows
      switch (state.CurrentState)
      {
        case EState.Idle:
          HandleIdle(behaviour, state);
          break;
        case EState.Planning:
          HandlePlanning(behaviour, state);
          break;
        case EState.Moving:
          HandleMoving(behaviour, state);
          break;
        case EState.Grabbing:
          HandleGrabbing(behaviour, state);
          break;
        case EState.Delivery:
          HandleDelivery(behaviour, state);
          break;
        case EState.Operating:
          HandleOperating(behaviour, state);
          break;
        case EState.Completed:
          HandleCompleted(behaviour, state);
          break;
        default:
          state.CurrentState = EState.Idle;
          HandleIdle(behaviour, state);
          break;
      }
    }

    public void TransitionState(Behaviour behaviour, StateData state, EState newState, string reason)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"TransitionState: {Npc.fullName} from {state.CurrentState} to {newState}, reason={reason}, invQty={state.QuantityInventory}, routes={state.ActiveRoutes.Count}", DebugLogger.Category.AllEmployees);
      state.CurrentState = newState;
      StateStartTimes[Npc.GUID] = Time.time;
    }

    public Behaviour GetInstancedBehaviour(NPC npc, IStationAdapter station)
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

    protected void HandleIdle(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandleIdle(behaviour, state) == false)
      {
        (behaviour.Npc as Employee).SetIdle(true);
        TransitionState(behaviour, state, EState.Planning, "Start route planning");
      }
    }

    public void HandlePlanning(Behaviour behaviour, StateData state)
    {
      // Check for inventory items
      if (Adapter?.HandleInventoryItems(behaviour, state) == false)
      {
        var inventorySlots = behaviour.Npc.Inventory.ItemSlots
                    .Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked)
                    .ToList();
        foreach (var slot in inventorySlots)
        {
          slot.ApplyLocks(behaviour.Npc, "Inventory route planning lock");
          if (HandleInventoryItem(behaviour, state, slot.ItemInstance))
          {
            slot.RemoveLock();
          }
          slot.RemoveLock();
        }
      }
      Adapter?.HandlePlanning(behaviour, state);
    }

    public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item)
    {
      var npc = behaviour.Npc as Employee;
      var shelf = FindShelfForDelivery(npc, item);
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInventoryItem: No shelf found for item {item.ID}", DebugLogger.Category.Chemist);
        EmployeeUtilities.AddItemTimeout(npc.AssignedProperty, item);
        return false;
      }

      var deliverySlots = (shelf as ITransitEntity).ReserveInputSlotsForItem(item, npc.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInventoryItem: No delivery slots for item {item.ID} on shelf {shelf.GUID}", DebugLogger.Category.Chemist);
        return false;
      }

      var inventorySlot = npc.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance != null && s.ItemInstance.CanStackWith(item, false));
      if (inventorySlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInventoryItem: No inventory slot with item {item.ID}", DebugLogger.Category.Chemist);
        return false;
      }

      int quantity = Mathf.Min(inventorySlot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(item)));
      var request = EmployeeUtilities.CreateTransferRequest(npc, item, quantity, null, new List<ItemSlot> { inventorySlot }, shelf, deliverySlots);
      if (request != null)
      {
        Adapter.GetEmployeeBehaviour(npc, state.Station.TransitEntity as BuildableItem, out var employeeBehaviour);
        employeeBehaviour.AddRoutes(behaviour, state, new List<TransferRequest> { request });
        TransitionState(behaviour, state, EState.Grabbing, "Inventory item delivery planned");
        return true;
      }
      return false;
    }

    protected void HandleMoving(Behaviour behaviour, StateData state)
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

    protected void HandleGrabbing(Behaviour behaviour, StateData state)
    {
      // Allow adapter to override grabbing handling if implemented
      if (Adapter?.HandleGrabbing(behaviour, state) == true) return;

      // Check if there are no active routes
      if (state.ActiveRoutes.Count <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleGrabbing: No active route for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        TransitionState(behaviour, state, EState.Idle, "No active route");
        return;
      }
      // Get the highest priority route
      state.ActiveRoutes.OrderByDescending(r => r.Priority);
      var route = state.ActiveRoutes.FirstOrDefault();
      if (route.Destination == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleGrabbing: No destination for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        TransitionState(behaviour, state, EState.Idle, "No destination");
        return;
      }

      // Validate route
      if (route.Source != null) // Standard route (from station or shelf)
      {
        state.PickupSlots = route.PickupSlots;
        if (state.PickupSlots.Count == 0 || state.PickupSlots.All(s => s.Quantity <= 0 || (s.IsLocked && s.ActiveLock.LockOwner != Npc.NetworkObject)))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleGrabbing: No valid pickup slots for item={route.Item.ID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          HandleFailedRoute(behaviour, state, route, "No valid pickup slots");
          return;
        }
      }
      else // Inventory route
      {
        if (route.InventorySlot == null || route.InventorySlot.Quantity < route.Quantity || !route.InventorySlot.ItemInstance.CanStackWith(route.Item, false))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleGrabbing: Invalid inventory slot for item={route.Item.ID}, qty={route.InventorySlot?.Quantity}, needed={route.Quantity} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          HandleFailedRoute(behaviour, state, route, "Invalid inventory slot");
          return;
        }
        state.QuantityInventory = route.Quantity;
      }

      // Initialize AdvancedMoveItemBehaviour
      if (ActiveMoveItemBehaviours.TryGetValue(Npc.GUID, out var moveItemBehaviour))
      {
        if (moveItemBehaviour is AdvancedMoveItemBehaviour advancedBehaviour)
        {
          advancedBehaviour.Initialize(route);
          advancedBehaviour.Enable_Networked(null);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleGrabbing: Initialized AdvancedMoveItemBehaviour for item={route.Item.ID}, qty={route.Quantity}, source={route.Source?.GUID.ToString() ?? "inventory"}, dest={route.Destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          state.ActiveRoutes.Remove(route);
          TransitionState(behaviour, state, EState.Grabbing, route.Source == null ? "Inventory route initialized" : "Pickup initialized");
          return;
        }
        else if (route.TransitRoute != null)
        {
          moveItemBehaviour.Initialize(route.TransitRoute, route.Item);
          moveItemBehaviour.Enable_Networked(null);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleGrabbing: Initialized AdvancedMoveItemBehaviour for item={route.Item.ID}, qty={route.Quantity}, source={route.Source?.GUID.ToString() ?? "inventory"}, dest={route.Destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          state.ActiveRoutes.Remove(route);
          TransitionState(behaviour, state, EState.Grabbing, route.Source == null ? "Inventory route initialized" : "Pickup initialized");
          return;
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleGrabbing: No AdvancedMoveItemBehaviour for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
      HandleFailedRoute(behaviour, state, route, "No AdvancedMoveItemBehaviour");
    }

    protected void HandleDelivery(Behaviour behaviour, StateData state)
    {
      // Allow adapter to override delivery handling if implemented
      if (Adapter?.HandleDelivery(behaviour, state) == true) return;

      // Check if the station has a delivery available
      if (state.Station?.OutputSlot?.Quantity > 0 && state.Station.OutputSlot.ItemInstance != null)
      {
        var outputItem = state.Station.OutputSlot.ItemInstance;
        int quantity = state.Station.OutputSlot.Quantity;

        var employee = Npc as Employee;
        // Try to find a packaging station or shelf for delivery
        ITransitEntity destination = EmployeeUtilities.FindPackagingStation(Adapter, outputItem) ?? FindShelfForDelivery(Npc, outputItem);
        if (destination == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: No destination found for item={outputItem.ID} in station={state.Station.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, outputItem);
          TransitionState(behaviour, state, EState.Idle, "No destination found");
          return;
        }

        // Reserve delivery slots
        var deliverySlots = destination.ReserveInputSlotsForItem(outputItem, Npc.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: No delivery slots available for item={outputItem.ID} at {destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, outputItem);
          TransitionState(behaviour, state, EState.Idle, "No delivery slots");
          return;
        }

        // Find an inventory slot
        var inventorySlot = Npc.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
        if (inventorySlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: No inventory slot available for item={outputItem.ID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          TransitionState(behaviour, state, EState.Idle, "No inventory slot");
          return;
        }

        // Create transfer request
        quantity = Math.Min(quantity, deliverySlots.Sum(s => s.GetCapacityForItem(outputItem)));
        var request = EmployeeUtilities.CreateTransferRequest(
            Npc,
            outputItem,
            quantity,
            state.Station.TransitEntity,
            new List<ItemSlot> { state.Station.OutputSlot },
            destination,
            deliverySlots
        );

        if (request == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleDelivery: Failed to create transfer request for item={outputItem.ID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          TransitionState(behaviour, state, EState.Idle, "Failed to create transfer request");
          return;
        }

        // Add the route to ActiveRoutes
        state.ActiveRoutes.Add(new PrioritizedRoute(request, 999));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: Added delivery route for item={outputItem.ID}, qty={quantity} to {destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);

        // Transition to Grabbing state
        TransitionState(behaviour, state, EState.Grabbing, "Delivery route planned");
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleDelivery: No delivery available for station={state.Station?.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        TransitionState(behaviour, state, EState.Idle, "No delivery available");
      }
    }

    protected void HandleOperating(Behaviour behaviour, StateData state)
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


    protected void HandleCompleted(Behaviour behaviour, StateData state)
    {
      if (Adapter?.HandleCompleted(behaviour, state) == true) return;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleCompleted: Operation complete for NPC {Npc.fullName}", DebugLogger.Category.AllEmployees);
      TransitionState(behaviour, state, EState.Idle, "Operation complete");
    }

    protected void MoveTo(Behaviour behaviour, StateData state, ITransitEntity destination)
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

    protected bool IsAtLocation(Behaviour behaviour, ITransitEntity location)
    {
      if (location == null) return false;
      float distance = Vector3.Distance(Npc.transform.position, NavMeshUtility.GetAccessPoint(location, Npc).position);
      bool atLocation = distance < 0.4f;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsAtLocation: Result={atLocation}, Distance={distance:F2}, location={location.GUID}", DebugLogger.Category.AllEmployees);
      return atLocation;
    }

    public void Disable(Behaviour behaviour)
    {
      if (States.TryGetValue(behaviour.Npc.GUID, out var state))
      {
        foreach (var route in state.ActiveRoutes)
          ReleaseRouteLocks(route);
        state.ActiveRoutes.Clear();
        state.IsMoving = false;
        States.Remove(behaviour.Npc.GUID);
      }
            (behaviour.Npc as Employee).ShouldIdle();
      behaviour.Disable();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Disable: Behaviour disabled for {Npc.fullName}", DebugLogger.Category.AllEmployees);
    }

    protected void HandleFailedRoute(Behaviour behaviour, StateData state, PrioritizedRoute route, string reason)
    {
      FailedRoutes[route] = (Time.time, reason);
      ReleaseRouteLocks(route);
      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, EState.Idle, $"Failed route: {reason}");
    }

    protected void ReleaseRouteLocks(PrioritizedRoute route)
    {
      route.PickupSlots?.RemoveLock();
      route.DestinationSlots?.RemoveLock();
      route.InventorySlot?.RemoveLock();
    }

    public void AddRoutes(Behaviour behaviour, StateData state, List<TransferRequest> requests)
    {
      int availableSlots = Npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      var filteredRequests = requests
          .Where(r => !FailedRoutes.Any(t => t.Key.Item.CanStackWith(r.Item, false) && Time.time <= t.Value.Time + RETRY_DELAY))
          .OrderByDescending(GetPriority)
          .Take(availableSlots)
          .ToList();
      foreach (var request in filteredRequests)
      {
        int priority = GetPriority(request);
        state.ActiveRoutes.Add(new PrioritizedRoute(request, priority));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"AddRoutes: Added route for {request.Item.ID} (qty={request.Quantity})", DebugLogger.Category.AllEmployees);
      }
    }

    public int GetPriority(TransferRequest request)
    {
      return request.Destination is IStationAdapter ? PRIORITY_STATION_REFILL :
             request.Source is LoadingDock ? PRIORITY_LOADING_DOCK :
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