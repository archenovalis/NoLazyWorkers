using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Utilities;
using ScheduleOne.Property;
using NoLazyWorkers.CacheManager;
using FishNet.Object;
using FishNet.Managing.Object;
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.TaskService.Extensions;
using NoLazyWorkers.Movement;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.CacheManager.Extensions;
using static NoLazyWorkers.Debug;
using NoLazyWorkers.Extensions;
using NoLazyWorkers.SmartExecution;

namespace NoLazyWorkers.Employees
{
  public static class Extensions
  {
    public static void NotifyTaskAvailable(this Employee employee, Guid taskId)
    {

    }
    // Enum for movement status in callbacks
    public enum MovementStatus
    {
      Success, // Movement completed successfully
      Failure  // Movement failed (e.g., timeout, invalid route)
    }

    public interface IEmployeeAdapter
    {
      Guid Guid { get; }
      EntityType EntityType { get; }
      Property AssignedProperty { get; }
      AdvEmployeeBehaviour AdvBehaviour { get; }
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
      public MovementStatus? MovementStatus { get; set; }
      public object ValidationResult { get; set; }
      public float MoveDelay { get; set; }
      public float MoveElapsed { get; set; }
      public Action<Employee, EmployeeInfo, MovementStatus> MoveCallback { get; set; }
      public List<TransferRequest> TransferRoutes { get; set; }
      public TransferRequest CurrentRequest { get; set; }

      // Releases all resources associated with the task context
      public void Cleanup(Employee employee)
      {
        // Release slot locks and transfer requests
        if (TransferRoutes != null)
        {
          foreach (var request in TransferRoutes)
          {
            request.InventorySlot?.RemoveLock(employee.NetworkObject);
            request.PickupSlots?.ForEach(s => s.RemoveLock(employee.NetworkObject));
            request.DropOffSlots?.ForEach(s => s.RemoveLock(employee.NetworkObject));
            TransferRequest.Release(request);
          }
          TransferRoutes.Clear();
        }
        MoveCallback = null;
        MovementStatus = null;
        Log(Level.Info, $"TaskContext.Cleanup: Released resources for {employee.fullName}", Category.EmployeeCore);
      }
    }

    public enum NpcSubType { Chemist, Handler, Botanist, Cleaner, Driver }

    public enum EmployeeAction
    {
      Idle,
      Working,
      Moving
    }

    public class EmployeeInfo
    {
      public Employee Employee { get; }
      public AdvEmployeeBehaviour AdvBehaviour { get; }
      public AdvMoveItemBehaviour AdvMoveItemBehaviour { get; }
      public bool IsAdvBehInitialized { get; set; }

      public TaskDescriptor CurrentTask;
      public EmployeeAction CurrentAction;
      public TaskContext TaskContext;

      public EmployeeInfo(Employee employee, AdvEmployeeBehaviour behaviour)
      {
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        AdvBehaviour = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
        AdvMoveItemBehaviour = CreateAdvMoveItemBehaviour(employee);
        CacheService.GetOrCreateService(employee.AssignedProperty).EmployeeInfoDict[employee.GUID] = this;
        IsAdvBehInitialized = false;
      }

      public void Reset()
      {
        CurrentTask = new();
        CurrentAction = EmployeeAction.Idle;
        IsAdvBehInitialized = false;
      }
    }
  }

  public static class Constants
  {
    public const float ADAPTER_DELAY_SECONDS = 3f;
  }

  public static class Utilities
  {
    // Registers an employee adapter
    public static void RegisterEmployeeAdapter(Employee employee, IEmployeeAdapter adapter)
    {
      Log(Level.Verbose, $"RegisterEmployeeAdapter: Entered for NPC={employee?.fullName}, type={adapter?.EntityType}", Category.AnyEmployee);
      if (employee == null || adapter == null)
      {
        Log(Level.Error, $"RegisterEmployeeAdapter: NPC or adapter is null", Category.AnyEmployee);
        return;
      }
      CacheService.GetOrCreateService(employee.AssignedProperty).IEmployees[employee.GUID] = adapter;
      Log(Level.Info, $"RegisterEmployeeAdapter: Registered adapter for NPC {employee.fullName}, type={adapter.EntityType}", Category.AnyEmployee);
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

      var outputs = new List<int>();
      Action<int, int, int[], List<int>> setupBehaviour = (start, count, inputs, outList) =>
      {
        var advMove = employee.gameObject.AddComponent<AdvMoveItemBehaviour>();
        advMove.Setup(employee);
        var networkObject = employee.gameObject.GetComponent<NetworkObject>();
        ManagedObjects.InitializePrefab(networkObject, -1);
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
        employee.behaviour.behaviourStack.Add(advMove);
        employee.behaviour.behaviourStack = employee.behaviour.behaviourStack.OrderByDescending(x => x.Priority).ToList();
        Log(Level.Info, $"CreateAdvMoveItemBehaviour: Created and added behaviour for {employee.fullName}", Category.EmployeeCore);
        outList.Add(1);
      };
      Smart.Execute("SetupAdvMoveBehaviour", 1, setupBehaviour, outputs: outputs);

      return employee.gameObject.GetComponent<AdvMoveItemBehaviour>();
    }
  }

  public class AdvEmployeeUpdater : MonoBehaviour
  {
    private AdvEmployeeBehaviour _behaviour;

    public void Setup(AdvEmployeeBehaviour behaviour)
    {
      _behaviour = behaviour ?? throw new ArgumentNullException(nameof(behaviour));
      Log(Level.Verbose, $"AdvEmployeeUpdater.Setup: Initialized for {_behaviour.Employee.fullName}", Category.EmployeeCore);
    }

    private void Update()
    {
      if (_behaviour.Info.IsAdvBehInitialized)
        _behaviour.Update();
    }

    public new Coroutine StartCoroutine(IEnumerator routine)
    {
      return base.StartCoroutine(routine);
    }
  }

  public class AdvEmployeeBehaviour
  {
    protected readonly IEmployeeAdapter _adapter;
    internal readonly Employee Employee;
    private readonly CacheService _cacheService;
    private readonly TaskService.TaskService _taskService;
    public EmployeeData BurstData;
    public EmployeeInfo Info;
    private AdvEmployeeUpdater _updater;
    public AdvEmployeeBehaviour(Employee employee, IEmployeeAdapter adapter)
    {
      Log(Level.Verbose, $"AdvEmployeeBehaviour: Entered for employee={employee?.fullName}", Category.EmployeeCore);
      Employee = employee ?? throw new ArgumentNullException(nameof(employee));
      _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
      _cacheService = CacheService.GetOrCreateService(employee.AssignedProperty);
      BurstData = new EmployeeData(_adapter);
      _cacheService.EmployeeDataCache[employee.GUID] = BurstData;
      Info = new EmployeeInfo(employee, this);
      RegisterEmployeeAdapter(employee, adapter);
      Log(Level.Info, $"AdvEmployeeBehaviour: Initialized for NPC {employee.fullName}", Category.EmployeeCore);
      _taskService = TaskServiceManager.GetOrCreateService(employee.AssignedProperty);
      foreach (var slot in employee.Inventory.ItemSlots)
        _cacheService.RegisterItemSlot(slot, employee.GUID);
      var slotData = employee.Inventory.ItemSlots.Select(s => new SlotData
      {
        Item = s.ItemInstance != null ? new ItemData(s.ItemInstance) : ItemData.Empty,
        Quantity = s.Quantity,
        SlotIndex = s.SlotIndex,
        StackLimit = s.ItemInstance?.StackLimit ?? -1
      });
    }

    public void InitializeEmployee()
    {
      Log(Level.Info, $"InitializeEmployee: Setting up for {Employee.fullName}", Category.EmployeeCore);
      var updater = Employee.gameObject.AddComponent<AdvEmployeeUpdater>();
      updater.Setup(this);
      _updater = updater;
      Info.IsAdvBehInitialized = true;
      Employee.behaviour.enabled = true;
    }

    public void Update()
    {
      if (!Info.IsAdvBehInitialized)
        return;

      switch (BurstData.CurrentAction)
      {
        case EmployeeAction.Idle:
          HandleIdle();
          break;
        case EmployeeAction.Working:
          HandleWorking();
          break;
        case EmployeeAction.Moving:
          HandleMoving();
          break;
      }
    }

    protected virtual bool CheckIdleConditions()
    {
      var nowork = false;
      if (!Employee.CanWork())
      {
        Employee.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
        Employee.SetIdle(true);
        Employee.SetWaitOutside(true);
        Info.CurrentAction = EmployeeAction.Idle;
        Log(Level.Verbose, $"CheckIdleConditions: Cannot work for NPC={Employee.fullName}", Category.EmployeeCore);
        nowork = true;
      }
      return nowork;
    }

    protected virtual void HandleIdle()
    {
      if (CheckIdleConditions())
        return;
      if (_taskService.TryGetTask(Employee, out var task, out var taskImpl))
      {
        Employee.SetWaitOutside(false);
        Info.CurrentTask = task;
        Log(Level.Info, $"HandleIdle: Assigned task {task.TaskId} to {Employee.fullName}", Category.EmployeeCore);
        StartMyCoroutine(ExecuteTaskCoroutine(taskImpl, task), task);
      }
      else
      {
        Employee.SetWaitOutside(true);
        Info.CurrentAction = EmployeeAction.Idle;
        Log(Level.Verbose, $"HandleIdle: No task available for {Employee.fullName}", Category.EmployeeCore);
      }
    }

    protected void HandleWorking()
    {
      try
      {
        if (BurstData.CurrentAction != EmployeeAction.Working)
        {
          Info.CurrentTask = new();
          Log(Level.Info, $"HandleWorking: Task completed or interrupted for {Employee.fullName}", Category.EmployeeCore);
        }
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"HandleWorking: Error executing task for {Employee.fullName}: {ex.Message}", Category.EmployeeCore);
        Info.CurrentTask = new();
        BurstData.CurrentAction = EmployeeAction.Idle;
      }
    }

    protected void HandleMoving()
    {
      var context = Info.TaskContext;
      if (context == null) return;
      if (context.MoveDelay > 0)
      {
        context.MoveDelay -= Time.deltaTime;
        return;
      }
      if (Employee.Movement.IsMoving)
      {
        context.MoveElapsed += Time.deltaTime;
        if (context.MoveElapsed >= 30f)
        {
          Log(Level.Warning, $"HandleMoving: Movement timeout for {Employee.fullName}", Category.EmployeeCore);
          ResetMovement(MovementStatus.Failure);
        }
        return;
      }
      if (context.MoveCallback != null)
      {
        context.MoveCallback.Invoke(Employee, Info, MovementStatus.Success);
        context.MoveCallback = null;
      }
      BurstData.CurrentAction = EmployeeAction.Working;
    }

    internal IEnumerator StartMovement(TransferRequest request, TaskDescriptor task, Action<Employee, EmployeeInfo, MovementStatus> onComplete = null)
    {
      yield return StartMovement(new List<TransferRequest> { request }, task, onComplete);
    }

    internal IEnumerator StartMovement(List<TransferRequest> requests, TaskDescriptor task, Action<Employee, EmployeeInfo, MovementStatus> onComplete = null)
    {
      var moveCompleted = false;
      Info.TaskContext = new TaskContext
      {
        TransferRoutes = requests.Select(r => TransferRequest.Get(Employee, r.Item, r.Quantity, r.InventorySlot, r.PickUp, r.PickupSlots, r.DropOff, r.DropOffSlots)).ToList(),
        MoveCallback = (emp, s, status) =>
        {
          moveCompleted = true;
          Log(Level.Info, $"StartMovement: Callback with status={status}, for {emp.fullName}", Category.EmployeeCore);
          s.TaskContext.MovementStatus = status;
          s.CurrentAction = EmployeeAction.Working;
          onComplete?.Invoke(emp, s, status);
        },
        MoveDelay = 0.5f,
        MoveElapsed = 0f
      };
      Info.AdvMoveItemBehaviour.Initialize(requests, Info, Info.TaskContext.MoveCallback);
      Log(Level.Info, $"StartMovement: Started with {requests.Count} routes for {Employee.fullName}", Category.EmployeeCore);
      BurstData.CurrentAction = EmployeeAction.Moving;
      float timeoutSeconds = 60.0f;
      float startTime = Time.time;
      while (!moveCompleted && Time.time - startTime < timeoutSeconds)
      {
        yield return null;
      }
      if (!moveCompleted)
      {
        Log(Level.Warning, $"StartMovement: Timeout after {timeoutSeconds}s for {Employee.fullName}", Category.EmployeeCore);
        Info.TaskContext.MovementStatus = MovementStatus.Failure;
        Info.TaskContext.Cleanup(Employee);
        _taskService.CompleteTask(task);
        BurstData.CurrentAction = EmployeeAction.Idle;
        onComplete?.Invoke(Employee, Info, MovementStatus.Failure);
      }
    }

    /// <summary>
    /// Handles the result of a completed task, transitioning to idle and triggering TryGetTask.
    /// </summary>
    /// <param name="result">The task completion result.</param>
    public void HandleTaskResult(TaskResult result)
    {
      Log(Level.Verbose, $"HandleTaskResult: Task {result.Task.TaskId} for {Employee.fullName}, success: {result.Success}, reason: {result.FailureReason.ToString() ?? "N/A"}", Category.EmployeeCore);
      BurstData.CurrentAction = EmployeeAction.Idle; // Ensure idle after result
      if (!result.Success)
      {
        Log(Level.Warning, $"Task {result.Task.TaskId} failed: {result.FailureReason}", Category.EmployeeCore);
      }
    }

    public void ResetMovement(MovementStatus status)
    {
      var context = Info.TaskContext;
      if (context != null)
      {
        if (context.MoveCallback != null)
        {
          context.MoveCallback.Invoke(Employee, Info, status);
          context.MoveCallback = null;
        }
        context.Cleanup(Employee);
      }
      BurstData.CurrentAction = EmployeeAction.Working;
      Log(Level.Info, $"ResetMovement: Reset with status={status} for {Employee.fullName}", Category.EmployeeCore);
    }

    public void Disable()
    {
      Info.TaskContext?.Cleanup(Employee);
      Info.CurrentTask = new();
      BurstData.CurrentAction = EmployeeAction.Idle;
      Employee.SetWaitOutside(true);
      Log(Level.Info, $"Disable: Behaviour disabled for {Employee.fullName}", Category.EmployeeCore);
    }

    public Coroutine StartMyCoroutine(IEnumerator routine, TaskDescriptor task = default)
    {
      return _updater.StartCoroutine(CoroutineRunner.RunThrowingIterator(routine, ex =>
      {
        Log(Level.Error, $"Coroutine failed for {Employee.fullName}: {ex.Message}", Category.EmployeeCore);
        if (task.TaskId != default)
        {
          _taskService.CompleteTask(task);
          HandleTaskResult(new TaskResult(task, false, ex.Message));
        }
      }));
    }

    private IEnumerator ExecuteTaskCoroutine(BaseTask taskImpl, TaskDescriptor task)
    {
      Info.CurrentAction = EmployeeAction.Working;
      var enumerator = taskImpl.Execute(Employee, task);
      while (true)
      {
        object current;
        bool moveNext;
        try
        {
          moveNext = enumerator.MoveNext();
          if (!moveNext) break;
          current = enumerator.Current;
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"Task {task.TaskId} execution error: {ex.Message}", Category.EmployeeCore);
          _taskService.CompleteTask(task);
          HandleTaskResult(new TaskResult(task, false, ex.Message));
          yield break;
        }
        yield return current;
      }
      _taskService.CompleteTask(task);
      HandleTaskResult(new TaskResult(task, true));
    }
  }

  [HarmonyPatch(typeof(Employee))]
  public class EmployeePatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("OnDestroy")]
    public static void OnDestroyPostfix(Employee __instance)
    {
      try
      {
        CacheService.GetOrCreateService(__instance.AssignedProperty).IEmployees[__instance.GUID].AdvBehaviour.Disable();
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"OnDestroyPostfix error: {ex}", Category.EmployeeCore);
      }
    }
    [HarmonyPrefix]
    [HarmonyPatch("Fire")]
    public static void FirePrefix(Employee __instance)
    {
      try
      {
        var taskService = TaskServiceManager.GetOrCreateService(__instance.AssignedProperty);
        var cacheService = taskService.CacheService;
        if (taskService.EntityStateService.EntityStates.ContainsKey(__instance.GUID))
          taskService.EntityStateService.EntityStates.Remove(__instance.GUID);
        if (cacheService.IEmployees.ContainsKey(__instance.GUID))
          cacheService.IEmployees.Remove(__instance.GUID);
        if (__instance.AssignedProperty.Employees.Count == 1)
          taskService.DeactivateProperty();
        Log(Level.Info, $"EmployeeBehaviour: Cleaned up for {__instance.fullName}", Category.EmployeeCore);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"FirePrefix error: {ex}", Category.EmployeeCore);
      }
    }
  }
}