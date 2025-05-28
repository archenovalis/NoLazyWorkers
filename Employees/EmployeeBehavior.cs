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
using Random = UnityEngine.Random;
using FishNet.Managing.Object;
using UnityEngine.InputSystem.EnhancedTouch;
using System.Threading.Tasks;
using Steamworks;
using UnityEngine.InputSystem.LowLevel;
using NoLazyWorkers.Employees.Tasks;
using static NoLazyWorkers.Stations.PackagingStationExtensions;

namespace NoLazyWorkers.Employees
{
  public static class EmployeeExtensions
  {
    // Enum for movement status in callbacks
    public enum Status
    {
      Success, // Movement completed successfully
      Failure  // Movement failed (e.g., timeout, invalid route)
    }

    public interface IEmployeeAdapter
    {
      NpcSubType SubType { get; }
      Property AssignedProperty { get; }
      EmployeeBehaviour EmpBehaviour { get; }
    }

    public class TaskContext
    {
      public ITransitEntity Pickup { get; set; }
      public ITransitEntity Dropoff { get; set; }
      public ItemInstance Item { get; set; }
      public int QuantityNeeded { get; set; }
      public int QuantityWanted { get; set; }
      public IStationAdapter Station { get; set; }
      public Status? MovementStatus { get; set; }
      public object ValidationResult { get; set; }
      public float MoveDelay { get; set; }
      public float MoveElapsed { get; set; }
      public Action<Employee, StateData, Status> MoveCallback { get; set; } // Updated callback with status
      public List<TransferRequest> Requests { get; set; }
      public TransferRequest CurrentRequest { get; set; }

      // Releases all resources associated with the task context
      public void Cleanup(Employee employee)
      {
        // Release slot locks and transfer requests
        if (Requests != null)
        {
          foreach (var request in Requests)
          {
            request.InventorySlot?.RemoveLock(employee.NetworkObject);
            request.PickupSlots?.ForEach(s => s.RemoveLock(employee.NetworkObject));
            request.DropOffSlots?.ForEach(s => s.RemoveLock(employee.NetworkObject));
            TransferRequest.Release(request);
          }
          Requests.Clear();
        }
        // Release reserved slots
        EmployeeUtilities.ReleaseReservations(employee);
        MoveCallback = null;
        MovementStatus = null;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"TaskContext.Cleanup: Released resources for {employee.fullName}", DebugLogger.Category.EmployeeCore);
      }
    }

    // Type-safe work step definition
    public class WorkStep<TStep> where TStep : Enum
    {
      public TStep Step { get; set; } // Enum-based step identifier
      public Func<Employee, StateData, Task<bool>> Validate { get; set; } // Validation logic
      public Func<Employee, StateData, Task> Execute { get; set; } // Execution logic
      public Dictionary<string, TStep> Transitions { get; set; } = new(); // Outcome to next step
    }

    public interface IEmployeeTask
    {
      int Priority { get; }
      int ScanIndex { get; }
      void SetScanIndex(int index);
      Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null);
      Task Execute(Employee employee, StateData state);
    }

    public enum NpcSubType { Chemist, Packager, Botanist, Cleaner, Driver }

    public enum EState
    {
      Idle,
      Working,
      Moving
    }

    public class EmployeeState
    {
      public string CurrentTaskId { get; set; }
      public object CurrentWorkStep { get; set; } // Stores enum (e.g., DeliverInventorySteps)
      public TaskContext TaskContext { get; set; }
      public int LastScanIndex { get; set; } = -1;
      public bool HasCallback { get; set; } = false;

      // Clears task state
      public void Clear()
      {
        CurrentTaskId = null;
        CurrentWorkStep = null;
        TaskContext = null;
        LastScanIndex = -1;
      }
    }

    public class StateData
    {
      public Employee Employee { get; }
      public EmployeeBehaviour EmployeeBeh { get; }
      public IStationAdapter Station { get; set; }
      public AdvancedMoveItemBehaviour AdvMoveItemBehaviour { get; }
      public IEmployeeTask CurrentTask { get; set; }
      public EState CurrentState { get; set; }
      public EmployeeState EmployeeState { get; set; }

      public StateData(Employee employee, EmployeeBehaviour behaviour)
      {
        // Initialize state with employee and behavior
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        EmployeeBeh = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
        AdvMoveItemBehaviour = EmployeeUtilities.CreateAdvMoveItemBehaviour(employee);
        EmployeeState = new EmployeeState();
        EmployeeBehaviour.States[employee.GUID] = this;
      }

      // Clears task and state
      public void Clear()
      {
        CurrentTask = null;
        CurrentState = EState.Idle;
        EmployeeState = null;
      }
    }

    public class TransferRequest
    {
      private static readonly Stack<TransferRequest> Pool = new();
      public Employee Employee { get; private set; }
      public ItemInstance Item { get; private set; }
      public int Quantity { get; private set; }
      public ItemSlot InventorySlot { get; private set; }
      public ITransitEntity PickUp { get; private set; }
      public List<ItemSlot> PickupSlots { get; private set; }
      public ITransitEntity DropOff { get; private set; }
      public List<ItemSlot> DropOffSlots { get; private set; }

      private TransferRequest(Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickup, List<ItemSlot> pickupSlots,
          ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        // Initialize transfer request with validation
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        PickUp = pickup;
        DropOff = dropOff ?? throw new ArgumentNullException(nameof(dropOff));
        PickupSlots = pickupSlots?.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList() ?? new List<ItemSlot>();
        DropOffSlots = dropOffSlots?.Where(slot => slot != null).ToList() ?? throw new ArgumentNullException(nameof(dropOffSlots));
        if (PickUp == null && PickupSlots.Count == 0)
          throw new ArgumentException("No valid pickup slots for inventory route");
        if (DropOffSlots.Count == 0)
          throw new ArgumentException("No valid delivery slots");
      }

      // Retrieves or creates a transfer request
      public static TransferRequest Get(Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickup, List<ItemSlot> pickupSlots,
          ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        if (Pool.Count > 0)
        {
          var request = Pool.Pop();
          request.Employee = employee;
          request.Item = item;
          request.Quantity = quantity;
          request.InventorySlot = inventorySlot;
          request.PickUp = pickup;
          request.PickupSlots = pickupSlots?.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList() ?? new List<ItemSlot>();
          request.DropOff = dropOff;
          request.DropOffSlots = dropOffSlots?.Where(slot => slot != null).ToList() ?? new List<ItemSlot>();
          return request;
        }
        return new TransferRequest(employee, item, quantity, inventorySlot, pickup, pickupSlots, dropOff, dropOffSlots);
      }

      // Releases a transfer request back to the pool
      public static void Release(TransferRequest request)
      {
        if (request == null)
          return;
        request.Employee = null;
        request.Item = null;
        request.Quantity = 0;
        request.InventorySlot = null;
        request.PickUp = null;
        request.PickupSlots?.Clear();
        request.DropOff = null;
        request.DropOffSlots?.Clear();
        Pool.Push(request);
      }
    }

    public struct PrioritizedRoute
    {
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public ITransitEntity PickUp;
      public List<ItemSlot> PickupSlots;
      public ITransitEntity DropOff;
      public List<ItemSlot> DropoffSlots;
      public int Priority;
      public AdvancedTransitRoute TransitRoute;

      public PrioritizedRoute(TransferRequest request, int priority)
      {
        Item = request.Item ?? throw new ArgumentNullException(nameof(request.Item));
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot ?? throw new ArgumentNullException(nameof(request.InventorySlot));
        PickUp = request.PickUp;
        PickupSlots = request.PickupSlots;
        DropOff = request.DropOff ?? throw new ArgumentNullException(nameof(request.DropOff));
        DropoffSlots = request.DropOffSlots ?? throw new ArgumentNullException(nameof(request.DropOffSlots));
        Priority = priority;
        TransitRoute = new AdvancedTransitRoute(request.PickUp, request.DropOff);
      }
    }

    public static Dictionary<Guid, IEmployeeAdapter> EmployeeAdapters = new();
    public static Dictionary<IStationAdapter, Employee> StationAdapterBehaviours = new();
    public static Dictionary<Guid, List<ItemSlot>> ReservedSlots = new();
    public static Dictionary<Property, List<ItemInstance>> NoDropOffCache = new();
    public static Dictionary<Property, Dictionary<ItemInstance, float>> TimedOutItems = new();
    public static readonly Dictionary<Guid, float> PendingAdapters = new();
    public const float ADAPTER_DELAY_SECONDS = 3f;
  }

  public static class EmployeeUtilities
  {
    // Checks if an item is timed out
    public static bool IsItemTimedOut(Property property, ItemInstance item)
    {
      // Return false if no timeout dictionary exists for the property
      if (!TimedOutItems.TryGetValue(property, out var timedOutItems))
        return false;
      // Find matching item in timeout dictionary
      var key = timedOutItems.Keys.FirstOrDefault(i => item.AdvCanStackWith(i));
      if (key == null)
        return false;
      // Check if timeout has expired
      if (Time.time >= timedOutItems[key])
      {
        timedOutItems.Remove(key);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"IsItemTimedOut: Removed expired timeout for item {item.ID}", DebugLogger.Category.Packager);
        return false;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"IsItemTimedOut: Item {item.ID} is timed out", DebugLogger.Category.Packager);
      return true;
    }

    // Adds a timeout for an item
    public static void AddItemTimeout(Property property, ItemInstance item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AddItemTimeout: Entered for property={property.name}, item={item?.ID}", DebugLogger.Category.Packager);
      // Initialize timeout dictionary if not present
      if (!TimedOutItems.ContainsKey(property))
      {
        TimedOutItems[property] = new Dictionary<ItemInstance, float>();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AddItemTimeout: Initialized timeout dictionary for property={property.name}", DebugLogger.Category.Packager);
      }
      // Set timeout for item (30 seconds)
      var key = TimedOutItems[property].Keys.FirstOrDefault(i => item.AdvCanStackWith(i)) ?? item;
      TimedOutItems[property][key] = Time.time + 30f;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AddItemTimeout: Timed out item {item.ID} for 30s in property {property.name}", DebugLogger.Category.Packager);
    }

    // Registers an employee adapter
    public static void RegisterEmployeeAdapter(NPC npc, IEmployeeAdapter adapter)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RegisterEmployeeAdapter: Entered for NPC={npc?.fullName}, type={adapter?.SubType}", DebugLogger.Category.AllEmployees);
      if (npc == null || adapter == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RegisterEmployeeAdapter: NPC or adapter is null", DebugLogger.Category.AllEmployees);
        return;
      }
      EmployeeAdapters[npc.GUID] = adapter;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"RegisterEmployeeAdapter: Registered adapter for NPC {npc.fullName}, type={adapter.SubType}", DebugLogger.Category.AllEmployees);
    }

    // Finds a packaging station for an item
    public static ITransitEntity FindPackagingStation(IEmployeeAdapter employee, ItemInstance item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindPackagingStation: Entered for employee={employee?.SubType}, item={item?.ID}", DebugLogger.Category.AllEmployees);
      if (employee == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindPackagingStation: Employee or item is null", DebugLogger.Category.AllEmployees);
        return null;
      }
      // Get stations for the assigned property
      if (!PropertyStations.TryGetValue(employee.AssignedProperty, out var stations) || stations == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindPackagingStation: No stations found for property {employee.AssignedProperty}", DebugLogger.Category.AllEmployees);
        return null;
      }
      // Find a suitable packaging station
      var suitableStation = stations.FirstOrDefault(s => s is PackagingStationAdapter p && !s.IsInUse && s.CanRefill(item))?.TransitEntity;
      if (suitableStation == null)
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindPackagingStation: No suitable packaging station for item {item.ID} in property {employee.AssignedProperty}", DebugLogger.Category.AllEmployees);
      else
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindPackagingStation: Found station {suitableStation.GUID} for item {item.ID}", DebugLogger.Category.AllEmployees);
      return suitableStation;
    }

    // Reserves a slot for an employee
    public static void SetReservedSlot(Employee employee, ItemSlot slot)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Entered for employee={employee?.fullName}, slot={slot?.SlotIndex}", DebugLogger.Category.Packager);
      if (employee == null || slot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"SetReservedSlot: Employee or slot is null", DebugLogger.Category.Packager);
        return;
      }
      // Initialize reserved slots list if not present
      if (!ReservedSlots.TryGetValue(employee.GUID, out var reserved))
      {
        ReservedSlots[employee.GUID] = new();
        reserved = ReservedSlots[employee.GUID];
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Initialized reserved slots for employee={employee.fullName}", DebugLogger.Category.Packager);
      }
      reserved.Add(slot);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"SetReservedSlot: Added slot {slot.SlotIndex} for NPC={employee.fullName}", DebugLogger.Category.Packager);
    }

    // Releases all reserved slots for an employee
    public static void ReleaseReservations(Employee employee)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: Entered for employee={employee?.fullName}", DebugLogger.Category.Packager);
      if (employee == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReleaseReservations: Employee is null", DebugLogger.Category.Packager);
        return;
      }
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots) && slots != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ReleaseReservations: Released {slots.Count} slots for employee {employee.fullName}", DebugLogger.Category.Packager);
        ReservedSlots.Remove(employee.GUID);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: No reserved slots for employee {employee.fullName}", DebugLogger.Category.Packager);
      }
    }

    // Creates a prioritized route from a transfer request
    public static PrioritizedRoute CreatePrioritizedRoute(TransferRequest request, int priority)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreatePrioritizedRoute: Entered for request with item={request?.Item?.ID}, priority={priority}", DebugLogger.Category.EmployeeCore);
      var route = new PrioritizedRoute(request, priority);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreatePrioritizedRoute: Created route for item {request.Item.ID}, priority={priority}", DebugLogger.Category.EmployeeCore);
      return route;
    }

    // Creates multiple prioritized routes
    public static List<PrioritizedRoute> CreatePrioritizedRoutes(List<TransferRequest> requests, int priority)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreatePrioritizedRoutes: Entered with {requests?.Count ?? 0} requests, priority={priority}", DebugLogger.Category.EmployeeCore);
      var routes = requests.Select(r => new PrioritizedRoute(r, priority)).ToList();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreatePrioritizedRoutes: Created {routes.Count} routes with priority={priority}", DebugLogger.Category.EmployeeCore);
      return routes;
    }

    // Creates an AdvancedMoveItemBehaviour component
    internal static AdvancedMoveItemBehaviour CreateAdvMoveItemBehaviour(Employee employee)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateAdvMoveItemBehaviour: Entered for employee={employee?.fullName}", DebugLogger.Category.EmployeeCore);
      if (employee == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateAdvMoveItemBehaviour: Employee is null", DebugLogger.Category.EmployeeCore);
        throw new ArgumentNullException(nameof(employee));
      }
      // Add behavior component to employee’s game object
      var advMove = employee.gameObject.AddComponent<AdvancedMoveItemBehaviour>();
      advMove.Setup(employee);
      var networkObject = employee.gameObject.GetComponent<NetworkObject>();
      ManagedObjects.InitializePrefab(networkObject, -1);
      // Initialize based on server/client context
      if (InstanceFinder.IsServer)
      {
        advMove.Preinitialize_Internal(networkObject, true);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateAdvMoveItemBehaviour: Preinitialized as server for {employee.fullName}", DebugLogger.Category.EmployeeCore);
      }
      else
      {
        advMove.Preinitialize_Internal(networkObject, false);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateAdvMoveItemBehaviour: Preinitialized as client for {employee.fullName}", DebugLogger.Category.EmployeeCore);
      }
      advMove.NetworkInitializeIfDisabled();
      // Add to behavior stack
      employee.behaviour.behaviourStack.Add(advMove);
      employee.behaviour.behaviourStack = employee.behaviour.behaviourStack.OrderByDescending(x => x.Priority).ToList();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreateAdvMoveItemBehaviour: Created and added behaviour for {employee.fullName}", DebugLogger.Category.EmployeeCore);
      return advMove;
    }

    // Clears all static data
    public static void ClearAll()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ClearAll: Entered", DebugLogger.Category.AllEmployees);
      StationAdapterBehaviours.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ClearAll: Cleared StationAdapterBehaviours", DebugLogger.Category.AllEmployees);
      foreach (var state in EmployeeBehaviour.States.Values)
        state.Clear();
      EmployeeBehaviour.States.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ClearAll: Cleared {EmployeeBehaviour.States.Count} employee states", DebugLogger.Category.AllEmployees);
      EmployeeAdapters.Clear();
      NoDropOffCache.Clear();
      TimedOutItems.Clear();
      PropertyStations.Clear();
      StationAdapters.Clear();
      ReservedSlots.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ClearAll: Cleared all dictionaries", DebugLogger.Category.AllEmployees);
    }
  }

  // Type-safe employee task
  public class EmployeeTask<TStep> : IEmployeeTask where TStep : Enum
  {
    private readonly Employee _employee;
    private readonly string _taskId;
    private readonly int _priority;
    private int _scanIndex;
    private readonly List<WorkStep<TStep>> _workSteps;

    public int Priority => _priority;
    public int ScanIndex => _scanIndex;

    public EmployeeTask(Employee employee, string taskId, int priority, List<WorkStep<TStep>> workSteps)
    {
      // Initialize task with validation
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmployeeTask.Constructor: Entered for employee={employee?.fullName}, taskId={taskId}, priority={priority}", DebugLogger.Category.EmployeeCore);
      _employee = employee ?? throw new ArgumentNullException(nameof(employee));
      _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
      _priority = priority;
      _workSteps = workSteps ?? throw new ArgumentNullException(nameof(workSteps));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeTask.Constructor: Initialized task {taskId} for {employee.fullName} with {workSteps.Count} work steps", DebugLogger.Category.EmployeeCore);
    }

    // Sets the scan index for task prioritization
    public void SetScanIndex(int index)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetScanIndex: Entered for task {_taskId}, index={index}", DebugLogger.Category.EmployeeCore);
      _scanIndex = index;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"SetScanIndex: Set scan index to {index} for task {_taskId}", DebugLogger.Category.EmployeeCore);
    }

    // Validates if the task can start
    public async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CanExecute: Entered for task {_taskId}, employee={employee?.fullName}, recheck={recheck?.GUID}", DebugLogger.Category.EmployeeCore);
      var state = EmployeeBehaviour.GetState(employee);
      var firstStep = _workSteps.FirstOrDefault();
      if (firstStep == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"CanExecute: No work steps defined for task {_taskId}", DebugLogger.Category.EmployeeCore);
        return false;
      }
      try
      {
        bool isValid = await firstStep.Validate(employee, state);
        if (isValid)
        {
          state.EmployeeState.CurrentTaskId = _taskId;
          state.EmployeeState.CurrentWorkStep = firstStep.Step;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"CanExecute: Task {_taskId} validated successfully for {employee.fullName}, starting with step {firstStep.Step}", DebugLogger.Category.EmployeeCore);
          return true;
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"CanExecute: Task {_taskId} not validated for {employee.fullName}", DebugLogger.Category.EmployeeCore);
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CanExecute: Exception in task {_taskId} for {employee.fullName}: {ex.Message}", DebugLogger.Category.EmployeeCore);
      }
      return false;
    }

    // Executes the current work step
    public async Task Execute(Employee employee, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"Execute: Entered for task {_taskId}, employee={employee?.fullName}, workStep={state.EmployeeState?.CurrentWorkStep}",
          DebugLogger.Category.EmployeeCore);

      if (employee == null || state.EmployeeState.CurrentWorkStep == null || state.EmployeeState?.CurrentTaskId != _taskId)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"Execute: Invalid employee or state for task {_taskId}",
            DebugLogger.Category.EmployeeCore);
        ResetTask(state);
        return;
      }

      var currentStep = _workSteps.FirstOrDefault(s => s.Step.Equals(state.EmployeeState.CurrentWorkStep));
      if (currentStep == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"Execute: No work step found for {state.EmployeeState.CurrentWorkStep} in task {_taskId}",
            DebugLogger.Category.EmployeeCore);
        ResetTask(state);
        return;
      }

      try
      {
        await currentStep.Execute(employee, state);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"Execute: Completed step {currentStep.Step} for task {_taskId} and employee {employee.fullName}",
            DebugLogger.Category.EmployeeCore);

        if (!state.EmployeeState.HasCallback)
        {
          // Apply default transition logic
          string outcome = "Success"; // Default outcome
          if (currentStep.Transitions.TryGetValue(outcome, out var nextStep))
          {
            state.EmployeeState.CurrentWorkStep = nextStep;
            if (!nextStep.Equals(default(TStep)))
              await Execute(employee, state);
            else
              ResetTask(state);
          }
          else
          {
            ResetTask(state);
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"Execute: Transition deferred to callback for step {currentStep.Step} in task {_taskId}",
              DebugLogger.Category.EmployeeCore);
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"Execute: Error in task {_taskId}, step {currentStep.Step} for {employee.fullName}: {ex.Message}",
            DebugLogger.Category.EmployeeCore);
        ResetTask(state);
      }
    }

    // Resets the task and cleans up resources
    private void ResetTask(StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ResetTask: Entered for task {_taskId}, employee={state.Employee?.fullName}", DebugLogger.Category.EmployeeCore);
      state.EmployeeState.TaskContext?.Cleanup(state.Employee);
      state.CurrentTask = null;
      state.CurrentState = EState.Idle;
      state.EmployeeState.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ResetTask: Task {_taskId} reset for {state.Employee?.fullName}", DebugLogger.Category.EmployeeCore);
    }
  }

  public class EmployeeBehaviour
  {
    protected readonly IEmployeeAdapter _adapter;
    protected readonly List<IEmployeeTask> _tasks;
    public StateData State { get; }
    protected readonly Employee _employee;
    private readonly float _scanDelay;
    private float _lastScanTime;
    public static readonly Dictionary<Guid, StateData> States = new();

    public EmployeeBehaviour(Employee employee, IEmployeeAdapter adapter, List<IEmployeeTask> tasks)
    {
      // Initialize behavior with employee, adapter, and tasks
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmployeeBehaviour: Entered for employee={employee?.fullName}", DebugLogger.Category.EmployeeCore);
      _employee = employee ?? throw new ArgumentNullException(nameof(employee));
      _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
      _tasks = tasks?.OrderByDescending(t => t.Priority).ToList() ?? new List<IEmployeeTask>();
      _tasks.Add(DeliverInventoryTask.Create(employee, 40));
      for (int i = 0; i < _tasks.Count; i++)
        _tasks[i].SetScanIndex(i);
      _scanDelay = Random.Range(0f, 0.5f);
      State = new StateData(employee, this);
      States[employee.GUID] = State;
      EmployeeUtilities.RegisterEmployeeAdapter(employee, adapter);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour: Initialized for NPC {_employee.fullName}, TaskCount={_tasks.Count}, ScanDelay={_scanDelay}", DebugLogger.Category.EmployeeCore);
    }

    // Retrieves state for an employee
    public static StateData GetState(Employee employee)
    {
      if (!States.TryGetValue(employee.GUID, out var state))
        throw new InvalidOperationException($"No state found for employee {employee.fullName}");
      return state;
    }

    // Executes the current task
    public async Task ExecuteTask()
    {
      if (State.CurrentTask != null)
      {
        await State.CurrentTask.Execute(_employee, State);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ExecuteTask: Executed task {State.CurrentTask.GetType().Name} for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
      }
    }

    // Updates the behavior state machine
    public async Task Update()
    {
      switch (State.CurrentState)
      {
        case EState.Idle:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Update: Idle State for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
          await HandleIdle();
          break;
        case EState.Working:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Update: Working State for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
          await HandleWorking();
          break;
        case EState.Moving:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Update: Moving State for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
          HandleMoving();
          break;
      }
    }

    // Handles idle state, scanning for tasks
    protected async Task HandleIdle()
    {
      // Wait for scan delay
      if (Time.time < _lastScanTime + _scanDelay)
        return;
      _lastScanTime = Time.time;
      int lastScanIndex = State.EmployeeState.LastScanIndex;
      int startIndex = (lastScanIndex + 1) % _tasks.Count;
      // Iterate through tasks to find one to execute
      for (int i = 0; i < _tasks.Count; i++)
      {
        int currentIndex = (startIndex + i) % _tasks.Count;
        var task = _tasks[currentIndex];
        try
        {
          if (await task.CanExecute(_employee))
          {
            State.CurrentTask = task;
            State.CurrentState = EState.Working;
            State.EmployeeState.LastScanIndex = task.ScanIndex;
            _employee.MarkIsWorking();
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleIdle: Selected task {task.GetType().Name} (Index={task.ScanIndex}) for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
            await HandleWorking();
            return;
          }
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleIdle: Error executing task {task.GetType().Name} for {_employee.fullName}: {ex.Message}", DebugLogger.Category.EmployeeCore);
        }
      }
      // No tasks available, set idle
      _employee.SetIdle(true);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleIdle: No tasks available for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
    }

    // Handles working state, executing the current task
    protected async Task HandleWorking()
    {
      if (State.CurrentTask == null)
      {
        State.CurrentState = EState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleWorking: No task to execute for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
        return;
      }
      try
      {
        await State.CurrentTask.Execute(_employee, State);
        if (State.CurrentState != EState.Working)
        {
          State.CurrentTask = null;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleWorking: Task completed or interrupted for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleWorking: Error executing task for {_employee.fullName}: {ex.Message}", DebugLogger.Category.EmployeeCore);
        State.CurrentTask = null;
        State.CurrentState = EState.Idle;
      }
    }

    // Handles moving state, monitoring movement progress
    protected virtual void HandleMoving()
    {
      var context = State.EmployeeState.TaskContext;
      // Apply move delay if active
      if (context.MoveDelay > 0)
      {
        context.MoveDelay -= Time.deltaTime;
        return;
      }
      // Check if employee is still moving
      if (_employee.Movement.IsMoving)
      {
        context.MoveElapsed += Time.deltaTime;
        // Timeout after 30 seconds
        if (context.MoveElapsed >= 30f)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleMoving: Movement timeout for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
          ResetMovement(Status.Failure);
        }
        return;
      }
      // Movement complete, invoke callback
      if (context.MoveCallback != null)
      {
        context.MoveCallback.Invoke(_employee, State, Status.Success);
        context.MoveCallback = null;
      }
      State.CurrentState = EState.Working;
    }

    // Starts movement with type-safe next step and status callback
    public void StartMovement<TStep>(
    List<PrioritizedRoute> routes,
    TStep nextStep,
    Action<Employee, StateData, Status> onComplete = null)
    where TStep : Enum
    {
      if (routes?.Any() != true)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"StartMovement: No valid routes for {_employee.fullName}",
            DebugLogger.Category.EmployeeCore);
        onComplete?.Invoke(_employee, State, Status.Failure);
        return;
      }
      if (onComplete != null)
        State.EmployeeState.HasCallback = true;
      // Start coroutine to handle movement
      _employee.StartCoroutine(StartMovementCoroutine(routes, nextStep, onComplete));
    }

    private IEnumerator StartMovementCoroutine<TStep>(
        List<PrioritizedRoute> routes,
        TStep nextStep,
        Action<Employee, StateData, Status> onComplete)
        where TStep : Enum
    {
      // Initialize task context for movement
      var moveCompleted = false;

      State.EmployeeState.TaskContext = new TaskContext
      {
        Requests = routes.Select(r => TransferRequest.Get(_employee, r.Item, r.Quantity, r.InventorySlot, r.PickUp, r.PickupSlots, r.DropOff, r.DropoffSlots)).ToList(),
        MoveCallback = async (emp, s, status) =>
        {
          moveCompleted = true;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"StartMovement: Callback with status={status}, next step={nextStep} for {emp.fullName}",
              DebugLogger.Category.EmployeeCore);

          s.EmployeeState.TaskContext.MovementStatus = status;
          s.EmployeeState.CurrentWorkStep = nextStep;
          s.CurrentState = EState.Working;

          if (onComplete != null)
          {
            State.EmployeeState.HasCallback = false;
            onComplete?.Invoke(emp, s, status);
          }
          else
          {
            s.EmployeeState.CurrentWorkStep = nextStep;
            await s.EmployeeBeh.ExecuteTask();
          }
        },
        MoveDelay = 0.5f,
        MoveElapsed = 0f
      };

      // Initialize movement behavior
      State.AdvMoveItemBehaviour.Initialize(routes, State, State.EmployeeState.TaskContext.MoveCallback);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StartMovement: Started with {routes.Count} routes for {_employee.fullName}",
          DebugLogger.Category.EmployeeCore);
      State.CurrentState = EState.Moving;

      // Wait for movement to complete
      float timeoutSeconds = 60.0f; // Prevent infinite wait
      float startTime = Time.time;
      while (!moveCompleted && Time.time - startTime < timeoutSeconds)
      {
        yield return null;
      }

      if (!moveCompleted)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StartMovement: Timeout after {timeoutSeconds}s for {_employee.fullName}",
            DebugLogger.Category.EmployeeCore);
        yield break;
      }
    }

    // Resets movement with the specified status
    public void ResetMovement(Status status)
    {
      var context = State.EmployeeState.TaskContext;
      if (context != null)
      {
        // Invoke callback with status
        if (context.MoveCallback != null)
        {
          context.MoveCallback.Invoke(_employee, State, status);
          context.MoveCallback = null;
        }
        // Cleanup resources
        context.Cleanup(_employee);
      }
      State.CurrentState = EState.Working; // Allow task to handle failure
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ResetMovement: Reset with status={status} for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
    }

    // Disables the behavior and cleans up
    public async Task Disable()
    {
      EmployeeUtilities.ReleaseReservations(_employee);
      var state = GetState(_employee);
      state.EmployeeState.TaskContext?.Cleanup(_employee);
      state.EmployeeState.Clear();
      state.CurrentTask = null;
      state.Station = null;
      state.CurrentState = EState.Idle;
      _employee.ShouldIdle();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Disable: Behaviour disabled for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
      await Task.CompletedTask;
    }
  }

  [HarmonyPatch(typeof(Employee))]
  public class EmployeePatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("OnDestroy")]
    public static void OnDestroyPostfix(Employee __instance)
    {
      EmployeeBehaviour.GetState(__instance).EmployeeBeh.Disable().GetAwaiter().GetResult();
    }
  }
}