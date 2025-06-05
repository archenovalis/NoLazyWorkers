using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Utilities;
using static NoLazyWorkers.Employees.Constants;
using ScheduleOne.Property;
using NoLazyWorkers.Storage;
using FishNet.Object;
using Random = UnityEngine.Random;
using FishNet.Managing.Object;
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.TaskService.TaskRegistry;
using Funly.SkyStudio;
using Unity.Collections;
using static NoLazyWorkers.Movement.Utilities;
using NoLazyWorkers.Movement;
using static NoLazyWorkers.Movement.Extensions;

namespace NoLazyWorkers.Employees
{
  public static class Extensions
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
      public TaskDescriptor Task { get; set; }
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
      public Action<Employee, EmployeeStateData, Status> MoveCallback { get; set; } // Updated callback with status
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
        Utilities.ReleaseReservations(employee);
        MoveCallback = null;
        MovementStatus = null;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"TaskContext.Cleanup: Released resources for {employee.fullName}", DebugLogger.Category.EmployeeCore);
      }
    }

    public enum NpcSubType { Chemist, Handler, Botanist, Cleaner, Driver }

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

    public class EmployeeStateData
    {
      public Employee Employee { get; }
      public EmployeeBehaviour EmployeeBeh { get; }
      public IStationAdapter Station { get; set; }
      public AdvMoveItemBehaviour AdvMoveItemBehaviour { get; }
      public TaskDescriptor CurrentTask { get; set; }
      public EState CurrentState { get; set; }
      public EmployeeState EmployeeState { get; set; }
      public TaskService.TaskService TaskService { get; set; }
      private readonly Queue<TaskDescriptor> _followUpTasks = new();

      public EmployeeStateData(Employee employee, EmployeeBehaviour behaviour)
      {
        // Initialize state with employee and behavior
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        EmployeeBeh = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
        AdvMoveItemBehaviour = Utilities.CreateAdvMoveItemBehaviour(employee);
        EmployeeState = new EmployeeState();
        States[employee.GUID] = this;
      }

      public void EnqueueFollowUpTask(TaskDescriptor task)
      {
        _followUpTasks.Enqueue(task);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StateData: Enqueued follow-up task {task.TaskId} for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      }

      public bool TryGetFollowUpTask(out TaskDescriptor task)
      {
        return _followUpTasks.TryDequeue(out task);
      }

      // Clears task and state
      public void Clear()
      {
        CurrentTask = new();
        CurrentState = EState.Idle;
        EmployeeState = null;
      }
    }
  }

  public static class Constants
  {
    public static Dictionary<Guid, IEmployeeAdapter> EmployeeAdapters = new();
    public static Dictionary<IStationAdapter, Employee> StationAdapterBehaviours = new();
    public static Dictionary<Guid, List<ItemSlot>> ReservedSlots = new();
    public static Dictionary<Property, List<ItemInstance>> NoDropOffCache = new();
    public static Dictionary<Property, Dictionary<ItemInstance, float>> TimedOutItems = new();
    public static readonly Dictionary<Guid, float> PendingAdapters = new();
    public static readonly Dictionary<Guid, EmployeeStateData> States = new();
    public const float ADAPTER_DELAY_SECONDS = 3f;
  }

  public static class Utilities
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
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"IsItemTimedOut: Removed expired timeout for item {item.ID}", DebugLogger.Category.Handler);
        return false;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"IsItemTimedOut: Item {item.ID} is timed out", DebugLogger.Category.Handler);
      return true;
    }

    // Adds a timeout for an item
    public static void AddItemTimeout(Property property, ItemInstance item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AddItemTimeout: Entered for property={property.name}, item={item?.ID}", DebugLogger.Category.Handler);
      // Initialize timeout dictionary if not present
      if (!TimedOutItems.ContainsKey(property))
      {
        TimedOutItems[property] = new Dictionary<ItemInstance, float>();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AddItemTimeout: Initialized timeout dictionary for property={property.name}", DebugLogger.Category.Handler);
      }
      // Set timeout for item (30 seconds)
      var key = TimedOutItems[property].Keys.FirstOrDefault(i => item.AdvCanStackWith(i)) ?? item;
      TimedOutItems[property][key] = Time.time + 30f;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AddItemTimeout: Timed out item {item.ID} for 30s in property {property.name}", DebugLogger.Category.Handler);
    }

    // Registers an employee adapter
    public static void RegisterEmployeeAdapter(Employee employee, IEmployeeAdapter adapter)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RegisterEmployeeAdapter: Entered for NPC={employee?.fullName}, type={adapter?.SubType}", DebugLogger.Category.AllEmployees);
      if (employee == null || adapter == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RegisterEmployeeAdapter: NPC or adapter is null", DebugLogger.Category.AllEmployees);
        return;
      }
      EmployeeAdapters[employee.GUID] = adapter;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"RegisterEmployeeAdapter: Registered adapter for NPC {employee.fullName}, type={adapter.SubType}", DebugLogger.Category.AllEmployees);
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
      if (!Stations.Extensions.IStations.TryGetValue(employee.AssignedProperty, out var stations) || stations == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindPackagingStation: No stations found for property {employee.AssignedProperty}", DebugLogger.Category.AllEmployees);
        return null;
      }
      // Find a suitable packaging station
      var suitableStation = stations.Values.FirstOrDefault(s => s is PackagingStationAdapter p && !s.IsInUse && s.CanRefill(item))?.TransitEntity;
      if (suitableStation == null)
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindPackagingStation: No suitable packaging station for item {item.ID} in property {employee.AssignedProperty}", DebugLogger.Category.AllEmployees);
      else
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindPackagingStation: Found station {suitableStation.GUID} for item {item.ID}", DebugLogger.Category.AllEmployees);
      return suitableStation;
    }

    // Reserves a slot for an employee
    public static void SetReservedSlot(Employee employee, ItemSlot slot)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Entered for employee={employee?.fullName}, slot={slot?.SlotIndex}", DebugLogger.Category.Handler);
      if (employee == null || slot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"SetReservedSlot: Employee or slot is null", DebugLogger.Category.Handler);
        return;
      }
      // Initialize reserved slots list if not present
      if (!ReservedSlots.TryGetValue(employee.GUID, out var reserved))
      {
        ReservedSlots[employee.GUID] = new();
        reserved = ReservedSlots[employee.GUID];
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Initialized reserved slots for employee={employee.fullName}", DebugLogger.Category.Handler);
      }
      reserved.Add(slot);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"SetReservedSlot: Added slot {slot.SlotIndex} for NPC={employee.fullName}", DebugLogger.Category.Handler);
    }

    // Releases all reserved slots for an employee
    public static void ReleaseReservations(Employee employee)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: Entered for employee={employee?.fullName}", DebugLogger.Category.Handler);
      if (employee == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReleaseReservations: Employee is null", DebugLogger.Category.Handler);
        return;
      }
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots) && slots != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ReleaseReservations: Released {slots.Count} slots for employee {employee.fullName}", DebugLogger.Category.Handler);
        ReservedSlots.Remove(employee.GUID);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: No reserved slots for employee {employee.fullName}", DebugLogger.Category.Handler);
      }
    }

    public static List<TransferRequest> CreateTransferRequest(Employee employee, TaskDescriptor task)
    {
      var source = Storage.Extensions.Storages[employee.AssignedProperty].TryGetValue(task.PickupGuid, out var pickup) ? pickup : null;
      var destination = Storage.Extensions.Storages[employee.AssignedProperty].TryGetValue(task.DropoffGuid, out var dropoff) ? dropoff : null;
      if (source == null || destination == null)
        return new List<TransferRequest>();

      var item = CreateItemInstance(task.Item);
      var sourceSlots = GetOutputSlotsContainingItem(source, item);
      var deliverySlots = destination.InputSlots.Where(s => s.SlotIndex == task.DropoffSlotIndex1).ToList();
      var inventorySlot = employee.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);

      if (sourceSlots.Count == 0 || deliverySlots.Count == 0 || inventorySlot == null)
        return new List<TransferRequest>();

      var request = TransferRequest.Get(employee, item, task.Quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
      return new List<TransferRequest> { request };
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
    internal static AdvMoveItemBehaviour CreateAdvMoveItemBehaviour(Employee employee)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateAdvMoveItemBehaviour: Entered for employee={employee?.fullName}", DebugLogger.Category.EmployeeCore);
      if (employee == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateAdvMoveItemBehaviour: Employee is null", DebugLogger.Category.EmployeeCore);
        throw new ArgumentNullException(nameof(employee));
      }
      // Add behavior component to employee’s game object
      var advMove = employee.gameObject.AddComponent<AdvMoveItemBehaviour>();
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
      foreach (var state in States.Values)
        state.Clear();
      States.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ClearAll: Cleared {States.Count} employee states", DebugLogger.Category.AllEmployees);
      EmployeeAdapters.Clear();
      NoDropOffCache.Clear();
      TimedOutItems.Clear();
      IStations.Clear();
      ReservedSlots.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ClearAll: Cleared all dictionaries", DebugLogger.Category.AllEmployees);
    }
  }

  public class EmployeeBehaviour
  {
    protected readonly IEmployeeAdapter _adapter;
    public EmployeeStateData State { get; }
    protected readonly Employee _employee;

    public EmployeeBehaviour(Employee employee, IEmployeeAdapter adapter)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmployeeBehaviour: Entered for employee={employee?.fullName}", DebugLogger.Category.EmployeeCore);
      _employee = employee ?? throw new ArgumentNullException(nameof(employee));
      _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
      State = new EmployeeStateData(employee, this);
      States[employee.GUID] = State;
      RegisterEmployeeAdapter(employee, adapter);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour: Initialized for NPC {_employee.fullName}", DebugLogger.Category.EmployeeCore);

      // Activate TaskService if first employee
      if (_employee.AssignedProperty.Employees.Count == 1)
        TaskServiceManager.ActivateProperty(_employee.AssignedProperty);
      State.TaskService = TaskServiceManager.GetOrCreateService(_employee.AssignedProperty);

    }

    public static EmployeeStateData GetState(Employee employee)
    {
      if (!States.TryGetValue(employee.GUID, out var state))
        throw new InvalidOperationException($"No state found for employee {employee.fullName}");
      return state;
    }

    public async Task ExecuteTask()
    {
      var definition = TaskDefinitionRegistry.Get(State.CurrentTask.Type);
      if (definition != null)
      {
        await definition.Executor.ExecuteAsync(_employee, State, State.CurrentTask);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ExecuteTask: Executed task {State.CurrentTask.Type} for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
      }

      State.CurrentTask.Dispose();
      State.CurrentTask = default;

      // Check for follow-up tasks
      while (State.TryGetFollowUpTask(out var followUpTask))
      {
        if (followUpTask.IsValid(_employee))
        {
          State.CurrentTask = followUpTask;
          State.CurrentState = EState.Working;
          State.EmployeeState.TaskContext = new TaskContext { Task = followUpTask };
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteTask: Starting follow-up task {followUpTask.TaskId} for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
          await definition.Executor.ExecuteAsync(_employee, State, followUpTask);
          State.CurrentTask.Dispose();
          State.CurrentTask = default;
        }
        else
        {
          // Enqueue invalid follow-up task to TaskService
          State.TaskService.EnqueueTask(followUpTask, followUpTask.Priority);
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ExecuteTask: Follow-up task {followUpTask.TaskId} invalid for {_employee.fullName}, enqueued to TaskService", DebugLogger.Category.EmployeeCore);
        }
      }

      State.CurrentState = EState.Idle;
    }

    public async Task Update()
    {
      if (State.CurrentState != EState.Idle)
        return;

      // Step 1: Check employee-initiated tasks (e.g., DeliverInventory)
      var deliverInventoryTask = await CheckEmployeeInitiatedTasks();
      if (deliverInventoryTask.HasValue)
      {
        State.EmployeeState.TaskContext = new TaskContext { Task = deliverInventoryTask.Value };
        State.CurrentTask = deliverInventoryTask.Value;
        State.CurrentState = EState.Working;
        State.EmployeeState.LastScanIndex = 0;
        _employee.MarkIsWorking();
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour.Update: Starting employee-initiated task {deliverInventoryTask.Value.TaskId} for {_employee.fullName}", DebugLogger.Category.Chemist);
        await ExecuteTask();
        return;
      }

      // Step 2: Check TaskService queue
      (bool got, TaskDescriptor taskDescriptor) = await State.TaskService.TryGetTaskAsync(_employee);
      if (got)
      {
        State.EmployeeState.TaskContext = new TaskContext { Task = taskDescriptor };
        State.CurrentTask = taskDescriptor;
        State.CurrentState = EState.Working;
        State.EmployeeState.LastScanIndex = 0;
        _employee.MarkIsWorking();
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour.Update: Starting task {taskDescriptor.TaskId} ({taskDescriptor.Type}) for {_employee.fullName}", DebugLogger.Category.Chemist);
        await ExecuteTask();
        return;
      }

      _employee.SetIdle(true);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"No executable tasks found for {_employee.fullName}", DebugLogger.Category.Chemist);
    }

    private async Task<TaskDescriptor?> CheckEmployeeInitiatedTasks()
    {
      // Check DeliverInventory
      var definition = TaskDefinitionRegistry.Get(TaskTypes.DeliverInventory);
      if (definition == null)
        return null;

      var context = new TaskValidatorContext
      {
        AssignedPropertyName = _employee.AssignedProperty.name,
        CurrentTime = Time.time
      };
      var validTasks = new NativeList<TaskDescriptor>(Allocator.Temp);
      definition.Validator.Validate(definition, new EntityKey { Guid = _employee.GUID, Type = TransitTypes.Inventory }, context, _employee.AssignedProperty, validTasks);

      if (validTasks.Length > 0 && validTasks[0].IsValid(_employee))
      {
        var task = validTasks[0];
        validTasks.Dispose();
        return task;
      }

      validTasks.Dispose();
      return null;
    }

    protected void HandleWorking()
    {
      try
      {
        if (State.CurrentState != EState.Working)
        {
          State.CurrentTask = new();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleWorking: Task completed or interrupted for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleWorking: Error executing task for {_employee.fullName}: {ex.Message}", DebugLogger.Category.EmployeeCore);
        State.CurrentTask = new();
        State.CurrentState = EState.Idle;
      }
    }

    protected void HandleMoving()
    {
      var context = State.EmployeeState.TaskContext;
      if (context.MoveDelay > 0)
      {
        context.MoveDelay -= Time.deltaTime;
        return;
      }
      if (_employee.Movement.IsMoving)
      {
        context.MoveElapsed += Time.deltaTime;
        if (context.MoveElapsed >= 30f)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleMoving: Movement timeout for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
          ResetMovement(Status.Failure);
        }
        return;
      }
      if (context.MoveCallback != null)
      {
        context.MoveCallback.Invoke(_employee, State, Status.Success);
        context.MoveCallback = null;
      }
      State.CurrentState = EState.Working;
    }

    public void StartMovement<TStep>(List<PrioritizedRoute> routes, TStep nextStep, Action<Employee, EmployeeStateData, Status> onComplete = null) where TStep : Enum
    {
      if (routes?.Any() != true)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StartMovement: No valid routes for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
        onComplete?.Invoke(_employee, State, Status.Failure);
        return;
      }
      if (onComplete != null)
        State.EmployeeState.HasCallback = true;
      _employee.StartCoroutine(StartMovementCoroutine(routes, nextStep, onComplete));
    }

    private IEnumerator StartMovementCoroutine<TStep>(List<PrioritizedRoute> routes, TStep nextStep, Action<Employee, EmployeeStateData, Status> onComplete) where TStep : Enum
    {
      var moveCompleted = false;
      State.EmployeeState.TaskContext = new TaskContext
      {
        Requests = routes.Select(r => TransferRequest.Get(_employee, r.Item, r.Quantity, r.InventorySlot, r.PickUp, r.PickupSlots, r.DropOff, r.DropoffSlots)).ToList(),
        MoveCallback = async (emp, s, status) =>
        {
          moveCompleted = true;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartMovement: Callback with status={status}, next step={nextStep} for {emp.fullName}", DebugLogger.Category.EmployeeCore);
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
      State.AdvMoveItemBehaviour.Initialize(routes, State, State.EmployeeState.TaskContext.MoveCallback);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartMovement: Started with {routes.Count} routes for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
      State.CurrentState = EState.Moving;
      float timeoutSeconds = 60.0f;
      float startTime = Time.time;
      while (!moveCompleted && Time.time - startTime < timeoutSeconds)
      {
        yield return null;
      }
      if (!moveCompleted)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartMovement: Timeout after {timeoutSeconds}s for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
        yield break;
      }
    }

    public void ResetMovement(Status status)
    {
      var context = State.EmployeeState.TaskContext;
      if (context != null)
      {
        if (context.MoveCallback != null)
        {
          context.MoveCallback.Invoke(_employee, State, status);
          context.MoveCallback = null;
        }
        context.Cleanup(_employee);
      }
      State.CurrentState = EState.Working;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ResetMovement: Reset with status={status} for {_employee.fullName}", DebugLogger.Category.EmployeeCore);
    }

    public async Task Disable()
    {
      Utilities.ReleaseReservations(_employee);
      var state = GetState(_employee);
      state.EmployeeState.TaskContext?.Cleanup(_employee);
      state.EmployeeState.Clear();
      state.CurrentTask = new();
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

    [HarmonyPrefix]
    [HarmonyPatch("Fire")]
    public static void FirePrefix(Employee __instance)
    {
      States.Remove(__instance.GUID);
      EmployeeAdapters.Remove(__instance.GUID);
      if (__instance.AssignedProperty.Employees.Count == 1)
        TaskServiceManager.DeactivateProperty(__instance.AssignedProperty);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour: Cleaned up for {__instance.fullName}", DebugLogger.Category.EmployeeCore);
    }
  }
}