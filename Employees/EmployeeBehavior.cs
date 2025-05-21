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

using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using ScheduleOne.Property;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using ScheduleOne.Delivery;
using ScheduleOne.NPCs.Behaviour;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.General;
using NoLazyWorkers.Stations;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using FishNet.Object;
using FishNet.Serializing;
using FishNet.Transporting;
using static FishNet.Object.NetworkBehaviour;

namespace NoLazyWorkers.Employees
{
  public static class EmployeeExtensions
  {
    public interface IEmployeeAdapter
    {
      bool HandleIdle(Employee behaviour, StateData state);
      bool HandlePlanning(Employee behaviour, StateData state);
      bool HandleMoving(Employee behaviour, StateData state);
      bool HandleTransfer(Employee behaviour, StateData state);
      bool HandleDelivery(Employee behaviour, StateData state);
      bool HandleOperating(Employee behaviour, StateData state);
      bool HandleCompleted(Employee behaviour, StateData state);
      bool HandleInventoryItems(Employee behaviour, StateData state);
      Property AssignedProperty { get; }
      NpcSubType SubType { get; }
      bool GetEmployeeBehaviour(NPC npc, out EmployeeBehaviour employeeBehaviour);
    }

    public enum NpcSubType { Chemist, Packager, Botanist, Cleaner, Driver }

    public enum EState
    {
      Idle, Planning, Moving, Transfer, Delivery, Operating, Completed
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
        Item = request.Item ?? throw new ArgumentNullException(nameof(request.Item));
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot ?? throw new ArgumentNullException(nameof(request.InventorySlot));
        Source = request.Source;
        PickupSlots = request.PickupSlots;
        Destination = request.Destination ?? throw new ArgumentNullException(nameof(request.Destination));
        DestinationSlots = request.DestinationSlots ?? throw new ArgumentNullException(nameof(request.DestinationSlots));
        Priority = priority;
        TransitRoute = new AdvancedTransitRoute(request.Source, request.Destination);
      }
    }

    public static readonly Dictionary<Type, Func<NPC, Behaviour>> StationTypeToBehaviourMap = new()
        {
            { typeof(MixingStation), npc => (npc as Chemist)?.StartMixingStationBehaviour },
            { typeof(MixingStationMk2), npc => (npc as Chemist)?.StartMixingStationBehaviour },
            { typeof(PackagingStation), npc => (npc as Packager)?.PackagingBehaviour },
        };

    public static Dictionary<Guid, IEmployeeAdapter> EmployeeAdapters = new();
    public static Dictionary<IStationAdapter, Employee> StationAdapterBehaviours = new();
    public static Dictionary<Guid, MoveItemBehaviour> AdvancedMoveItemBehaviours = new();
    public static Dictionary<Guid, EmployeeBehaviour> ActiveBehaviours = new();
    public static Dictionary<Guid, List<ItemSlot>> ReservedSlots = new();
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

    public static void RegisterStationBehaviour(Employee behaviour, IStationAdapter adapter)
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

    public static void SetReservedSlot(Employee employee, ItemSlot slot)
    {
      if (!ReservedSlots.TryGetValue(employee.GUID, out var reserved))
      {
        ReservedSlots[employee.GUID] = new();
        reserved = ReservedSlots[employee.GUID];
      }
      reserved.Add(slot);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Set slot {slot.SlotIndex} for NPC={employee.fullName}", DebugLogger.Category.Packager);
    }

    public static ItemSlot GetReservedSlot(Employee employee)
    {
      ItemSlot slot = null;
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots))
      {
        slot = slots[0];
        slots.RemoveAt(0);
      }
      return slot;
    }

    public static void ReleaseReservations(Employee employee)
    {
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots) && slots != null)
      {
        ReservedSlots.Remove(employee.GUID);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: Released {slots.Count()} slots for employee {employee.fullName}", DebugLogger.Category.Packager);
      }
    }
  }

  public abstract class EmployeeBehaviour
  {
    protected NPC Npc;
    protected readonly IEmployeeAdapter Adapter;
    protected readonly Dictionary<EState, Action<Employee, StateData>> StatePackagers;
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
      StatePackagers = new Dictionary<EState, Action<Employee, StateData>>
      {
          { EState.Idle, HandleIdle },
          { EState.Planning, HandlePlanning },
          { EState.Moving, HandleMoving },
          { EState.Transfer, HandleTransfer },
          { EState.Delivery, HandleDelivery },
          { EState.Operating, HandleOperating },
          { EState.Completed, HandleCompleted }
      };
      StateStartTimes[npc.GUID] = Time.time;
    }

    public virtual void Update(Employee employee)
    {
      if (Npc == null)
      {
        Npc = employee;
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
          HandleIdle(employee, state);
          break;
        case EState.Planning:
          HandlePlanning(employee, state);
          break;
        case EState.Moving:
          HandleMoving(employee, state);
          break;
        case EState.Transfer:
          HandleTransfer(employee, state);
          break;
        case EState.Delivery:
          HandleDelivery(employee, state);
          break;
        case EState.Operating:
          HandleOperating(employee, state);
          break;
        case EState.Completed:
          HandleCompleted(employee, state);
          break;
        default:
          state.CurrentState = EState.Idle;
          HandleIdle(employee, state);
          break;
      }
    }

    public void TransitionState(Employee employee, StateData state, EState newState, string reason)
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

    protected void HandleIdle(Employee employee, StateData state)
    {
      if (Adapter?.HandleIdle(employee, state) == false)
      {
        employee.SetIdle(true);
        TransitionState(employee, state, EState.Planning, "Start route planning");
      }
    }

    public void HandlePlanning(Employee employee, StateData state)
    {
      // Check for inventory items
      if (Adapter?.HandleInventoryItems(employee, state) == false)
      {
        var inventorySlots = employee.Inventory.ItemSlots
                    .Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked)
                    .ToList();
        foreach (var slot in inventorySlots)
        {
          if (HandleInventoryItem(employee, state, slot.ItemInstance))
          {
            slot.RemoveLock();
          }
          slot.RemoveLock();
        }
      }
      Adapter?.HandlePlanning(employee, state);
    }

    public bool HandleInventoryItem(Employee employee, StateData state, ItemInstance item)
    {
      var shelf = FindShelfForDelivery(employee, item);
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInventoryItem: No shelf found for item {item.ID}", DebugLogger.Category.Chemist);
        EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, item);
        return false;
      }

      var deliverySlots = (shelf as ITransitEntity).ReserveInputSlotsForItem(item, (FishNet.Object.NetworkObject)employee.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInventoryItem: No delivery slots for item {item.ID} on shelf {shelf.GUID}", DebugLogger.Category.Chemist);
        return false;
      }

      var inventorySlot = employee.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance != null && s.ItemInstance.CanStackWith(item, false));
      if (inventorySlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInventoryItem: No inventory slot with item {item.ID}", DebugLogger.Category.Chemist);
        return false;
      }

      int quantity = Mathf.Min(inventorySlot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(item)));
      var request = new TransferRequest(employee, item, quantity, inventorySlot, null, [inventorySlot], shelf, deliverySlots);
      if (request != null)
      {
        Adapter.GetEmployeeBehaviour(employee, out var employeeBehaviour);
        employeeBehaviour.AddRoutes(employee, state, new List<TransferRequest> { request });
        TransitionState(employee, state, EState.Transfer, "Inventory item delivery planned");
        return true;
      }
      return false;
    }

    protected void HandleMoving(Employee employee, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HandleMoving: NPC={employee?.fullName ?? "null"}, IsMoving={state.IsMoving}, MoveElapsed={state.MoveElapsed}, Npc.IsMoving={employee.Movement.IsMoving}",
          DebugLogger.Category.AllEmployees);

      if (!state.IsMoving)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleMoving: State.IsMoving is false for NPC={employee?.fullName ?? "null"}, transitioning to Idle",
            DebugLogger.Category.AllEmployees);
        TransitionState(employee, state, EState.Idle, "Not moving");
        return;
      }

      state.MoveElapsed += Time.deltaTime;
      if (state.MoveElapsed >= state.MoveTimeout)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleMoving: Timeout moving to {state.Destination?.Name ?? "null"} for NPC={employee?.fullName ?? "null"}",
            DebugLogger.Category.AllEmployees);
        state.IsMoving = false;
        TransitionState(employee, state, EState.Idle, "Movement timeout");
        return;
      }

      if (!employee.Movement.IsMoving)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleMoving: Npc.Movement.IsMoving is false, checking destination for NPC={employee?.fullName ?? "null"}",
            DebugLogger.Category.AllEmployees);
        state.IsMoving = false;
        if (state.Destination != null && NavMeshUtility.IsAtTransitEntity(state.Destination, employee, 0.4f))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleMoving: Reached {state.Destination.Name} for NPC={employee?.fullName ?? "null"}",
              DebugLogger.Category.AllEmployees);
          StatePackagers[state.CurrentState](employee, state);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"HandleMoving: Not at destination, transitioning to Idle for NPC={employee?.fullName ?? "null"}",
              DebugLogger.Category.AllEmployees);
          TransitionState(employee, state, EState.Idle, "Not at destination");
        }
      }
    }

    protected void HandleTransfer(Employee employee, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleTransfer: Starting for NPC={Npc.fullName}, ActiveRoutes={state.ActiveRoutes.Count}", DebugLogger.Category.AllEmployees);
      if (Adapter?.HandleTransfer(employee, state) == true) return;

      if (state.ActiveRoutes.Count <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleTransfer: No active routes for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        TransitionState(employee, state, EState.Idle, "No active route");
        return;
      }

      var route = state.ActiveRoutes.OrderByDescending(r => r.Priority).FirstOrDefault();
      if (route.Destination == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleTransfer: No destination for NPC={Npc.fullName}, Route Item={route.Item?.ID}", DebugLogger.Category.AllEmployees);
        HandleFailedRoute(employee, state, route, "No destination");
        return;
      }

      if (route.Source != null)
      {
        state.PickupSlots = route.PickupSlots;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleTransfer: Checking pickup slots for item={route.Item.ID}, slots={state.PickupSlots.Count}", DebugLogger.Category.AllEmployees);
        if (state.PickupSlots.Count == 0 || state.PickupSlots.All(s => s.Quantity <= 0 || (s.IsLocked && s.ActiveLock?.LockOwner != Npc.NetworkObject)))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleTransfer: No valid pickup slots for item={route.Item.ID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          HandleFailedRoute(employee, state, route, "No valid pickup slots");
          return;
        }
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleTransfer: Inventory route for item={route.Item.ID}, InventorySlot={route.InventorySlot?.GetHashCode()}", DebugLogger.Category.AllEmployees);
        if (route.InventorySlot == null || route.InventorySlot.Quantity < route.Quantity || !route.InventorySlot.ItemInstance.CanStackWith(route.Item, false))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleTransfer: Invalid inventory slot for item={route.Item.ID}, qty={route.InventorySlot?.Quantity}, needed={route.Quantity} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          HandleFailedRoute(employee, state, route, "Invalid inventory slot");
          return;
        }
        state.QuantityInventory = route.Quantity;
      }

      if (AdvancedMoveItemBehaviours.TryGetValue(Npc.GUID, out var moveItemBehaviour))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleTransfer: Found moveItemBehaviour for NPC={Npc.fullName}, Type={moveItemBehaviour.GetType().Name}", DebugLogger.Category.AllEmployees);
        if (moveItemBehaviour is AdvancedMoveItemBehaviour advancedBehaviour)
        {
          try
          {
            advancedBehaviour.Initialize(route);
            advancedBehaviour.Enable_Networked(null);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleTransfer: Initialized AdvancedMoveItemBehaviour for item={route.Item.ID}, qty={route.Quantity}, source={route.Source?.GUID.ToString() ?? "inventory"}, dest={route.Destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
            state.ActiveRoutes.Remove(route);
            TransitionState(employee, state, EState.Moving, route.Source == null ? "Inventory route initialized" : "Pickup initialized");
            return;
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleTransfer: Failed to initialize AdvancedMoveItemBehaviour for NPC={Npc.fullName}, error: {e}", DebugLogger.Category.AllEmployees);
            HandleFailedRoute(employee, state, route, "Failed to initialize AdvancedMoveItemBehaviour");
            return;
          }
        }
        else if (route.TransitRoute != null)
        {
          try
          {
            moveItemBehaviour.Initialize(route.TransitRoute, route.Item, route.Quantity);
            moveItemBehaviour.Enable_Networked(null);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleTransfer: Initialized MoveItemBehaviour for item={route.Item.ID}, qty={route.Quantity}, source={route.Source?.GUID.ToString() ?? "inventory"}, dest={route.Destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
            state.ActiveRoutes.Remove(route);
            TransitionState(employee, state, EState.Moving, route.Source == null ? "Inventory route initialized" : "Pickup initialized");
            return;
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleTransfer: Failed to initialize MoveItemBehaviour for NPC={Npc.fullName}, error: {e}", DebugLogger.Category.AllEmployees);
            HandleFailedRoute(employee, state, route, "Failed to initialize MoveItemBehaviour");
            return;
          }
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleTransfer: No AdvancedMoveItemBehaviour for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
      HandleFailedRoute(employee, state, route, "No AdvancedMoveItemBehaviour");
    }

    protected void HandleDelivery(Employee employee, StateData state)
    {
      // Allow adapter to override delivery handling if implemented
      if (Adapter?.HandleDelivery(employee, state) == true) return;

      // Check if the station has a delivery available
      if (state.Station?.OutputSlot?.Quantity > 0 && state.Station.OutputSlot.ItemInstance != null)
      {
        var outputItem = state.Station.OutputSlot.ItemInstance;
        int quantity = state.Station.OutputSlot.Quantity;

        // Try to find a packaging station or shelf for delivery
        ITransitEntity destination = EmployeeUtilities.FindPackagingStation(Adapter, outputItem) ?? FindShelfForDelivery(Npc, outputItem, allowAnyShelves: false);
        if (destination == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: No destination found for item={outputItem.ID} in station={state.Station.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          TransitionState(employee, state, EState.Idle, "No destination found");
          return;
        }

        // Reserve delivery slots
        var deliverySlots = destination.ReserveInputSlotsForItem(outputItem, Npc.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: No delivery slots available for item={outputItem.ID} at {destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          TransitionState(employee, state, EState.Idle, "No delivery slots");
          return;
        }

        // Find an inventory slot
        var inventorySlot = Npc.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
        if (inventorySlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: No inventory slot available for item={outputItem.ID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          TransitionState(employee, state, EState.Idle, "No inventory slot");
          return;
        }

        // Create transfer request
        quantity = Math.Min(quantity, deliverySlots.Sum(s => s.GetCapacityForItem(outputItem)));
        var request = new TransferRequest(Npc, outputItem, quantity, inventorySlot, state.Station.TransitEntity, [state.Station.OutputSlot], destination, deliverySlots);

        if (request == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleDelivery: Failed to create transfer request for item={outputItem.ID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
          TransitionState(employee, state, EState.Idle, "Failed to create transfer request");
          return;
        }

        // Add the route to ActiveRoutes
        state.ActiveRoutes.Add(new PrioritizedRoute(request, 999));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleDelivery: Added delivery route for item={outputItem.ID}, qty={quantity} to {destination.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);

        // Transition to Transfer state
        TransitionState(employee, state, EState.Transfer, "Delivery route planned");
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleDelivery: No delivery available for station={state.Station?.GUID} for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        TransitionState(employee, state, EState.Idle, "No delivery available");
      }
    }

    protected void HandleOperating(Employee employee, StateData state)
    {
      if (Adapter?.HandleOperating(employee, state) == true) return;
      if (!IsAtLocation(state.Station.TransitEntity))
      {
        MoveTo(employee, state, state.Station.TransitEntity);
        return;
      }

      if (state.Station.HasActiveOperation || state.Station.GetInputQuantity() < state.Station.StartThreshold)
      {
        TransitionState(employee, state, EState.Planning, "Cannot start operation");
        return;
      }

      state.Station.StartOperation(employee);
      TransitionState(employee, state, EState.Completed, "Operation started");
    }


    protected void HandleCompleted(Employee employee, StateData state)
    {
      if (Adapter?.HandleCompleted(employee, state) == true) return;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleCompleted: Operation complete for NPC {Npc.fullName}", DebugLogger.Category.AllEmployees);
      TransitionState(employee, state, EState.Idle, "Operation complete");
    }

    protected void MoveTo(Employee employee, StateData state, ITransitEntity destination)
    {
      if (destination == null || !Npc.Movement.CanGetTo(destination, 1f))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MoveTo: Cannot reach {destination?.Name}, disabling", DebugLogger.Category.AllEmployees);
        Disable(employee);
        return;
      }
      state.Destination = destination;
      state.MoveTimeout = 30f;
      state.MoveElapsed = 0f;
      state.IsMoving = true;
      Npc.Movement.SetDestination(NavMeshUtility.GetAccessPoint(destination, Npc).position);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"MoveTo: Moving to {destination.Name}", DebugLogger.Category.AllEmployees);
    }

    protected bool IsAtLocation(ITransitEntity entity)
    {
      if (entity == null) return false;
      for (int i = 0; i < entity.AccessPoints.Length; i++)
      {
        if (Vector3.Distance(Npc.transform.position, entity.AccessPoints[i].position) < 0.4f)
        {
          return true;
        }

        if (Npc.Movement.IsAsCloseAsPossible(entity.AccessPoints[i].transform.position, 0.4f))
        {
          return true;
        }
      }

      return false;
    }

    public void Disable(Employee employee)
    {
      if (States.TryGetValue(employee.GUID, out var state))
      {
        state.ActiveRoutes.Clear();
        state.IsMoving = false;
        States.Remove(employee.GUID);
      }
      employee.ShouldIdle();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Disable: Behaviour disabled for {Npc.fullName}", DebugLogger.Category.AllEmployees);
    }

    protected void HandleFailedRoute(Employee employee, StateData state, PrioritizedRoute route, string reason)
    {
      FailedRoutes[route] = (Time.time, reason);
      state.ActiveRoutes.Remove(route);
      TransitionState(employee, state, EState.Idle, $"Failed route: {reason}");
    }

    public void AddRoutes(Employee employee, StateData state, List<TransferRequest> requests)
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
    [HarmonyPatch("End")]
    public static void EndPostfix(MoveItemBehaviour __instance)
    {
      //TODO: Check for mroe routes goto Transfer else goto Idle
      EmployeeUtilities.ReleaseReservations(__instance.Npc as Employee);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EndPostfix: Released reservations for MoveItemBehaviour for NPC={__instance.Npc.fullName}", DebugLogger.Category.AllEmployees);
    }
  }

  /* [HarmonyPatch(typeof(NetworkBehaviour))]
  public static class NetworkBehaviourPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("SendObserversRpc")]
    public static bool SendObserversRpcPrefix(NetworkBehaviour __instance, uint hash, PooledWriter methodWriter, Channel channel, DataOrderType orderType, bool bufferLast, bool excludeServer, bool excludeOwner)
    {
      if (__instance.name != "Packager(Clone)" && __instance.name != "Chemist(Clone)") return true;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: __instance {__instance.name}", DebugLogger.Category.AllEmployees);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: hash {hash}", DebugLogger.Category.AllEmployees);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: methodWriter {methodWriter}", DebugLogger.Category.AllEmployees);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: channel {channel}", DebugLogger.Category.AllEmployees);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: orderType {orderType}", DebugLogger.Category.AllEmployees);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: bufferLast {bufferLast}", DebugLogger.Category.AllEmployees);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: excludeServer {excludeServer}", DebugLogger.Category.AllEmployees);
      if (!__instance.IsSpawnedWithWarning())
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 0", DebugLogger.Category.AllEmployees);
        return false;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 1", DebugLogger.Category.AllEmployees);
      __instance._transportManagerCache.CheckSetReliableChannel(methodWriter.Length + 10, ref channel);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 1a", DebugLogger.Category.AllEmployees);
      RpcLinkType value;
      PooledWriter pooledWriter = (!__instance._rpcLinks.TryGetValue(hash, out value)) ? __instance.CreateRpc(hash, methodWriter, PacketId.ObserversRpc, channel) : __instance.CreateLinkedRpc(value, methodWriter, channel);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 2", DebugLogger.Category.AllEmployees);
      __instance.SetNetworkConnectionCache(excludeServer, excludeOwner);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 3", DebugLogger.Category.AllEmployees);
      __instance._networkObjectCache.NetworkManager.TransportManager.SendToClients((byte)channel, pooledWriter.GetArraySegment(), __instance._networkObjectCache.Observers, __instance._networkConnectionCache, splitLargeMessages: true, orderType);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 4", DebugLogger.Category.AllEmployees);
      if (bufferLast)
      {
        if (__instance._bufferedRpcs.TryGetValue(hash, out var value2))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 5", DebugLogger.Category.AllEmployees);
          value2.Writer.StoreLength();
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 6", DebugLogger.Category.AllEmployees);
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 7", DebugLogger.Category.AllEmployees);
        __instance._bufferedRpcs[hash] = new BufferedRpc(pooledWriter, channel, orderType);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 8", DebugLogger.Category.AllEmployees);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 9", DebugLogger.Category.AllEmployees);
        pooledWriter.StoreLength();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 10", DebugLogger.Category.AllEmployees);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SendObserversRpcPrefix: 11", DebugLogger.Category.AllEmployees);
      return false;
    }
  } */
}