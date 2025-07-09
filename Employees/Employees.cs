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
using static NoLazyWorkers.Storage.ManagedDictionaries;
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
using static NoLazyWorkers.TimeManagerExtensions;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Debug;
using UnityEngine.InputSystem.EnhancedTouch;

namespace NoLazyWorkers.Employees
{
  public static class Extensions
  {
    public static void NotifyTaskAvailable(this Employee employee, Guid taskId)
    {

    }
    // Enum for movement status in callbacks
    public enum Status
    {
      Success, // Movement completed successfully
      Failure  // Movement failed (e.g., timeout, invalid route)
    }

    public interface IEmployeeAdapter
    {
      Guid Guid { get; }
      NpcSubType SubType { get; }
      Property AssignedProperty { get; }
      EmployeeBehaviour AdvBehaviour { get; }
      List<ItemSlot> InventorySlots { get; }
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
      public Action<Employee, EmployeeData, Status> MoveCallback { get; set; } // Updated callback with status
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
        Log(Level.Info, $"TaskContext.Cleanup: Released resources for {employee.fullName}", Category.EmployeeCore);
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

    public class EmployeeData
    {
      public Employee Employee { get; }
      public EmployeeBehaviour AdvBehaviour { get; }
      public IStationAdapter Station { get; set; }
      public AdvMoveItemBehaviour AdvMoveItemBehaviour { get; }
      public TaskDescriptor CurrentTask { get; set; }
      public EState CurrentState { get; set; }
      public EmployeeState State { get; set; }
      public TaskService.TaskService TaskService { get; set; }
      private readonly Queue<TaskDescriptor> _followUpTasks = new();

      public EmployeeData(Employee employee, EmployeeBehaviour behaviour)
      {
        // Initialize state with employee and behavior
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        AdvBehaviour = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
        AdvMoveItemBehaviour = CreateAdvMoveItemBehaviour(employee);
        State = new EmployeeState();
        States[employee.GUID] = this;
      }

      public void EnqueueFollowUpTask(TaskDescriptor task)
      {
        _followUpTasks.Enqueue(task);
        Log(Level.Info, $"StateData: Enqueued follow-up task {task.TaskId} for {Employee.fullName}", Category.EmployeeCore);
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
        State = null;
      }
    }
  }

  public static class Constants
  {
    public static Dictionary<IStationAdapter, Employee> StationAdapterBehaviours = new();
    public static Dictionary<Guid, List<ItemSlot>> ReservedSlots = new();
    public static Dictionary<Property, Dictionary<ItemInstance, float>> TimedOutItems = new();
    public static readonly Dictionary<Guid, float> PendingAdapters = new();
    public static readonly Dictionary<Guid, EmployeeData> States = new();
    public const float ADAPTER_DELAY_SECONDS = 3f;
  }

  public static class Utilities
  {
    // Registers an employee adapter
    public static void RegisterEmployeeAdapter(Employee employee, IEmployeeAdapter adapter)
    {
      Log(Level.Verbose, $"RegisterEmployeeAdapter: Entered for NPC={employee?.fullName}, type={adapter?.SubType}", Category.AnyEmployee);
      if (employee == null || adapter == null)
      {
        Log(Level.Error, $"RegisterEmployeeAdapter: NPC or adapter is null", Category.AnyEmployee);
        return;
      }
      IEmployees[employee.AssignedProperty][employee.GUID] = adapter;
      Log(Level.Info, $"RegisterEmployeeAdapter: Registered adapter for NPC {employee.fullName}, type={adapter.SubType}", Category.AnyEmployee);
    }

    // Reserves a slot for an employee
    public static void SetReservedSlot(Employee employee, ItemSlot slot)
    {
      Log(Level.Verbose, $"SetReservedSlot: Entered for employee={employee?.fullName}, slot={slot?.SlotIndex}", Category.Handler);
      if (employee == null || slot == null)
      {
        Log(Level.Warning, $"SetReservedSlot: Employee or slot is null", Category.Handler);
        return;
      }
      // Initialize reserved slots list if not present
      if (!ReservedSlots.TryGetValue(employee.GUID, out var reserved))
      {
        ReservedSlots[employee.GUID] = new();
        reserved = ReservedSlots[employee.GUID];
        Log(Level.Verbose, $"SetReservedSlot: Initialized reserved slots for employee={employee.fullName}", Category.Handler);
      }
      reserved.Add(slot);
      Log(Level.Info, $"SetReservedSlot: Added slot {slot.SlotIndex} for NPC={employee.fullName}", Category.Handler);
    }

    // Releases all reserved slots for an employee
    public static void ReleaseReservations(Employee employee)
    {
      Log(Level.Verbose, $"ReleaseReservations: Entered for employee={employee?.fullName}", Category.Handler);
      if (employee == null)
      {
        Log(Level.Warning, $"ReleaseReservations: Employee is null", Category.Handler);
        return;
      }
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots) && slots != null)
      {
        Log(Level.Info, $"ReleaseReservations: Released {slots.Count} slots for employee {employee.fullName}", Category.Handler);
        ReservedSlots.Remove(employee.GUID);
      }
      else
      {
        Log(Level.Verbose, $"ReleaseReservations: No reserved slots for employee {employee.fullName}", Category.Handler);
      }
    }

    public static List<TransferRequest> CreateTransferRequest(Employee employee, TaskDescriptor task)
    {
      var source = Storage.ManagedDictionaries.Storages[employee.AssignedProperty].TryGetValue(task.PickupGuid, out var pickup) ? pickup : null;
      var destination = Storage.ManagedDictionaries.Storages[employee.AssignedProperty].TryGetValue(task.DropoffGuid, out var dropoff) ? dropoff : null;
      if (source == null || destination == null)
        return new List<TransferRequest>();

      var item = task.Item.CreateItemInstance();
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
      Log(Level.Verbose, $"CreatePrioritizedRoute: Entered for request with item={request?.Item?.ID}, priority={priority}", Category.EmployeeCore);
      var route = new PrioritizedRoute(request, priority);
      Log(Level.Info, $"CreatePrioritizedRoute: Created route for item {request.Item.ID}, priority={priority}", Category.EmployeeCore);
      return route;
    }

    // Creates multiple prioritized routes
    public static List<PrioritizedRoute> CreatePrioritizedRoutes(List<TransferRequest> requests, int priority)
    {
      Log(Level.Verbose, $"CreatePrioritizedRoutes: Entered with {requests?.Count ?? 0} requests, priority={priority}", Category.EmployeeCore);
      var routes = requests.Select(r => new PrioritizedRoute(r, priority)).ToList();
      Log(Level.Info, $"CreatePrioritizedRoutes: Created {routes.Count} routes with priority={priority}", Category.EmployeeCore);
      return routes;
    }

    // Creates an AdvancedMoveItemBehaviour component
    internal static AdvMoveItemBehaviour CreateAdvMoveItemBehaviour(Employee employee)
    {
      Log(Level.Verbose, $"CreateAdvMoveItemBehaviour: Entered for employee={employee?.fullName}", Category.EmployeeCore);
      if (employee == null)
      {
        Log(Level.Error, $"CreateAdvMoveItemBehaviour: Employee is null", Category.EmployeeCore);
        throw new ArgumentNullException(nameof(employee));
      }
      // Add behavior component to employee’s game object
      var advMove = employee.gameObject.AddComponent<AdvMoveItemBehaviour>();
      advMove.Setup(employee);
      var networkObject = employee.gameObject.GetComponent<NetworkObject>();
      ManagedObjects.InitializePrefab(networkObject, -1);
      // Initialize based on server/client context
      if (FishNetExtensions.IsServer)
      {
        advMove.Preinitialize_Internal(networkObject, true);
        Log(Level.Verbose, $"CreateAdvMoveItemBehaviour: Preinitialized as server for {employee.fullName}", Category.EmployeeCore);
      }
      else
      {
        advMove.Preinitialize_Internal(networkObject, false);
        Log(Level.Verbose, $"CreateAdvMoveItemBehaviour: Preinitialized as client for {employee.fullName}", Category.EmployeeCore);
      }
      advMove.NetworkInitializeIfDisabled();
      // Add to behavior stack
      employee.behaviour.behaviourStack.Add(advMove);
      employee.behaviour.behaviourStack = employee.behaviour.behaviourStack.OrderByDescending(x => x.Priority).ToList();
      Log(Level.Info, $"CreateAdvMoveItemBehaviour: Created and added behaviour for {employee.fullName}", Category.EmployeeCore);
      return advMove;
    }

    // Clears all static data
    public static void Cleanup()
    {
      Log(Level.Verbose, $"ClearAll: Entered", Category.AnyEmployee);
      StationAdapterBehaviours.Clear();
      Log(Level.Info, $"ClearAll: Cleared StationAdapterBehaviours", Category.AnyEmployee);
      foreach (var state in States.Values)
        state.Clear();
      States.Clear();
      Log(Level.Info, $"ClearAll: Cleared {States.Count} employee states", Category.AnyEmployee);
      IEmployees.Clear();
      NoDropOffCache.Clear();
      TimedOutItems.Clear();
      IStations.Clear();
      ReservedSlots.Clear();
      Log(Level.Info, $"ClearAll: Cleared all dictionaries", Category.AnyEmployee);
    }
  }

  public class EmployeeBehaviour
  {
    protected readonly IEmployeeAdapter _adapter;
    public EmployeeData State { get; }
    protected readonly Employee _employee;
    private readonly CacheService _cacheManager;

    public EmployeeBehaviour(Employee employee, IEmployeeAdapter adapter)
    {
      Log(Level.Verbose, $"EmployeeBehaviour: Entered for employee={employee?.fullName}", Category.EmployeeCore);
      _employee = employee ?? throw new ArgumentNullException(nameof(employee));
      _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
      State = new EmployeeData(employee, this);
      States[employee.GUID] = State;
      RegisterEmployeeAdapter(employee, adapter);
      Log(Level.Info, $"EmployeeBehaviour: Initialized for NPC {employee.fullName}", Category.EmployeeCore);

      State.TaskService = TaskServiceManager.GetOrCreateService(employee.AssignedProperty);

      // Add employee to CacheManager
      var _cacheManager = CacheService.GetOrCreateCacheManager(employee.AssignedProperty);
      var storageKey = new StorageKey { Guid = employee.GUID, Type = StorageType.Employee };
      foreach (var slot in employee.Inventory.ItemSlots)
        _cacheManager.RegisterItemSlot(slot, storageKey);
      var slotData = employee.Inventory.ItemSlots.Select(s => new SlotData
      {
        Item = s.ItemInstance != null ? new ItemData(s.ItemInstance) : ItemData.Empty,
        Quantity = s.Quantity,
        SlotIndex = s.SlotIndex,
        StackLimit = s.ItemInstance?.StackLimit ?? -1,
        IsValid = true
      });
      _cacheManager.QueueSlotUpdate(storageKey, slotData);
    }

    public static EmployeeData GetState(Employee employee)
    {
      if (!States.TryGetValue(employee.GUID, out var state))
        throw new InvalidOperationException($"No state found for employee {employee.fullName}");
      return state;
    }

    public async Task ExecuteTask()
    {
      var definition = TaskRegistry.Get(State.CurrentTask.Type);
      if (definition != null)
      {
        await definition.Executor.ExecuteAsync(_employee, State, State.CurrentTask);
        Log(Level.Verbose, $"ExecuteTask: Executed task {State.CurrentTask.Type} for {_employee.fullName}", Category.EmployeeCore);
      }

      State.CurrentTask.Dispose();
      State.CurrentTask = default;

      // Check for follow-up tasks
      while (State.TryGetFollowUpTask(out var followUpTask))
      {
        if (followUpTask.IsEmployeeTypeValid(_employee))
        {
          State.CurrentTask = followUpTask;
          State.CurrentState = EState.Working;
          State.State.TaskContext = new TaskContext { Task = followUpTask };
          Log(Level.Info, $"ExecuteTask: Starting follow-up task {followUpTask.TaskId} for {_employee.fullName}", Category.EmployeeCore);
          await definition.Executor.ExecuteAsync(_employee, State, followUpTask);
          State.CurrentTask.Dispose();
          State.CurrentTask = default;
        }
        else
        {
          // Enqueue invalid follow-up task to TaskService
          State.TaskService.EnqueueTask(followUpTask, followUpTask.Priority);
          Log(Level.Warning, $"ExecuteTask: Follow-up task {followUpTask.TaskId} invalid for {_employee.fullName}, enqueued to TaskService", Category.EmployeeCore);
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
        State.State.TaskContext = new TaskContext { Task = deliverInventoryTask.Value };
        State.CurrentTask = deliverInventoryTask.Value;
        State.CurrentState = EState.Working;
        State.State.LastScanIndex = 0;
        _employee.MarkIsWorking();
        Log(Level.Info, $"ChemistBehaviour.Update: Starting employee-initiated task {deliverInventoryTask.Value.TaskId} for {_employee.fullName}", Category.Chemist);
        await ExecuteTask();
        return;
      }

      // Step 2: Check TaskService queue
      (bool got, TaskDescriptor taskDescriptor) = await State.TaskService.TryGetTaskAsync(_employee);
      if (got)
      {
        State.State.TaskContext = new TaskContext { Task = taskDescriptor };
        State.CurrentTask = taskDescriptor;
        State.CurrentState = EState.Working;
        State.State.LastScanIndex = 0;
        _employee.MarkIsWorking();
        Log(Level.Info, $"ChemistBehaviour.Update: Starting task {taskDescriptor.TaskId} ({taskDescriptor.Type}) for {_employee.fullName}", Category.Chemist);
        await ExecuteTask();
        return;
      }

      _employee.SetIdle(true);
      Log(Level.Verbose, $"No executable tasks found for {_employee.fullName}", Category.Chemist);
    }

    private async Task<TaskDescriptor?> CheckEmployeeInitiatedTasks()
    {
      // Check DeliverInventory
      var definition = TaskRegistry.Get(TaskName.DeliverInventory);
      if (definition == null)
        return null;

      var context = new TaskValidatorContext
      {
        AssignedPropertyName = _employee.AssignedProperty.name,
        CurrentTime = Time.time
      };
      var validTasks = new NativeList<TaskDescriptor>(Allocator.Temp);
      definition.Validator.Validate(definition, new EntityKey { Guid = _employee.GUID, Type = TransitTypes.Inventory }, context, _employee.AssignedProperty, validTasks);

      if (validTasks.Length > 0 && validTasks[0].IsEmployeeTypeValid(_employee))
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
          Log(Level.Info, $"HandleWorking: Task completed or interrupted for {_employee.fullName}", Category.EmployeeCore);
        }
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"HandleWorking: Error executing task for {_employee.fullName}: {ex.Message}", Category.EmployeeCore);
        State.CurrentTask = new();
        State.CurrentState = EState.Idle;
      }
    }

    protected void HandleMoving()
    {
      var context = State.State.TaskContext;
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
          Log(Level.Warning, $"HandleMoving: Movement timeout for {_employee.fullName}", Category.EmployeeCore);
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

    public void StartMovement<TStep>(List<PrioritizedRoute> routes, TStep nextStep, Action<Employee, EmployeeData, Status> onComplete = null) where TStep : Enum
    {
      if (routes?.Any() != true)
      {
        Log(Level.Warning, $"StartMovement: No valid routes for {_employee.fullName}", Category.EmployeeCore);
        onComplete?.Invoke(_employee, State, Status.Failure);
        return;
      }
      if (onComplete != null)
        State.State.HasCallback = true;
      _employee.StartCoroutine(StartMovementCoroutine(routes, nextStep, onComplete));
    }

    private IEnumerator StartMovementCoroutine<TStep>(List<PrioritizedRoute> routes, TStep nextStep, Action<Employee, EmployeeData, Status> onComplete) where TStep : Enum
    {
      var moveCompleted = false;
      State.State.TaskContext = new TaskContext
      {
        Requests = routes.Select(r => TransferRequest.Get(_employee, r.Item, r.Quantity, r.InventorySlot, r.PickUp, r.PickupSlots, r.DropOff, r.DropoffSlots)).ToList(),
        MoveCallback = async (emp, s, status) =>
        {
          moveCompleted = true;
          Log(Level.Info, $"StartMovement: Callback with status={status}, next step={nextStep} for {emp.fullName}", Category.EmployeeCore);
          s.State.TaskContext.MovementStatus = status;
          s.State.CurrentWorkStep = nextStep;
          s.CurrentState = EState.Working;
          if (onComplete != null)
          {
            State.State.HasCallback = false;
            onComplete?.Invoke(emp, s, status);
          }
          else
          {
            s.State.CurrentWorkStep = nextStep;
            await s.AdvBehaviour.ExecuteTask();
          }
        },
        MoveDelay = 0.5f,
        MoveElapsed = 0f
      };
      State.AdvMoveItemBehaviour.Initialize(routes, State, State.State.TaskContext.MoveCallback);
      Log(Level.Info, $"StartMovement: Started with {routes.Count} routes for {_employee.fullName}", Category.EmployeeCore);
      State.CurrentState = EState.Moving;
      float timeoutSeconds = 60.0f;
      float startTime = Time.time;
      while (!moveCompleted && Time.time - startTime < timeoutSeconds)
      {
        yield return null;
      }
      if (!moveCompleted)
      {
        Log(Level.Error, $"StartMovement: Timeout after {timeoutSeconds}s for {_employee.fullName}", Category.EmployeeCore);
        yield break;
      }
    }

    /// <summary>
    /// Handles the result of a completed task, transitioning to idle and triggering TryGetTask.
    /// </summary>
    /// <param name="result">The task completion result.</param>
    public void HandleTaskResult(TaskResult result)
    {
      Log(Level.Verbose, $"HandleTaskResult: Task {result.Task.TaskId} for {_employee.fullName}, success: {result.Success}, reason: {result.FailureReason ?? "N/A"}", Category.EmployeeCore);

      // Transition to idle state
      State.CurrentState = EState.Idle;

      // Log failure reason if applicable
      if (!result.Success)
      {
        Log(Level.Warning, $"Task {result.Task.TaskId} failed: {result.FailureReason}", Category.EmployeeCore);
      }
    }

    public void ResetMovement(Status status)
    {
      var context = State.State.TaskContext;
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
      Log(Level.Info, $"ResetMovement: Reset with status={status} for {_employee.fullName}", Category.EmployeeCore);
    }

    public async Task Disable()
    {
      Utilities.ReleaseReservations(_employee);
      var state = GetState(_employee);
      state.State.TaskContext?.Cleanup(_employee);
      state.State.Clear();
      state.CurrentTask = new();
      state.Station = null;
      state.CurrentState = EState.Idle;
      _employee.ShouldIdle();
      Log(Level.Info, $"Disable: Behaviour disabled for {_employee.fullName}", Category.EmployeeCore);
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
      EmployeeBehaviour.GetState(__instance).AdvBehaviour.Disable().GetAwaiter().GetResult();
    }

    [HarmonyPrefix]
    [HarmonyPatch("Fire")]
    public static void FirePrefix(Employee __instance)
    {
      States.Remove(__instance.GUID);
      IEmployees[__instance.AssignedProperty].Remove(__instance.GUID);
      if (__instance.AssignedProperty.Employees.Count == 1)
        TaskServiceManager.DeactivateProperty(__instance.AssignedProperty);
      Log(Level.Info, $"EmployeeBehaviour: Cleaned up for {__instance.fullName}", Category.EmployeeCore);
    }
  }
}