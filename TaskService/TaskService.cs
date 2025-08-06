using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.CacheManager.ManagedDictionaries;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Utilities;
using static NoLazyWorkers.Employees.Constants;
using ScheduleOne.Property;
using NoLazyWorkers.CacheManager;
using FishNet.Object;
using Random = UnityEngine.Random;
using FishNet.Managing.Object;
//using static NoLazyWorkers.Stations.PackagingStationExtensions;
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.TaskService.TaskRegistry;
using Funly.SkyStudio;
using Unity.Collections;
using NoLazyWorkers.Movement;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using static NoLazyWorkers.CacheManager.Extensions;
using static NoLazyWorkers.Debug;
using UnityEngine.InputSystem.EnhancedTouch;
using System.Reflection.Metadata;
using NoLazyWorkers.Extensions;
using NoLazyWorkers.SmartExecution;
using Unity.Burst;
using static NoLazyWorkers.Debug.Deferred;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static NoLazyWorkers.Extensions.PoolUtility;
using System.Text;

namespace NoLazyWorkers.TaskService
{
  /// <summary>
  /// Builds a task with configurable delegates for setup, entity selection, validation, and task creation.
  /// </summary>
  /// <typeparam name="TSetupOutput">The type of setup output, must be a struct and IDisposable.</typeparam>
  /// <typeparam name="TValidationSetupOutput">The type of validation setup output, must be a struct and IDisposable.</typeparam>
  public class TaskBuilder<TSetupOutput, TValidationSetupOutput>
      where TSetupOutput : struct, IDisposable
      where TValidationSetupOutput : struct, IDisposable
  {
    private readonly TaskName _taskType;
    private Action<int, int, int[], List<TSetupOutput>> _setupDelegate;
    private Action<NativeList<Guid>> _selectEntitiesDelegate;
    private Action<int, int, Guid[], List<TValidationSetupOutput>> _validationSetupDelegate;
    private Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> _validateEntityDelegate;
    private Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> _createTaskDelegate;

    /// <summary>
    /// Initializes a new TaskBuilder with the specified task type and default empty delegates.
    /// </summary>
    /// <param name="taskType">The type of task to build.</param>
    public TaskBuilder(TaskName taskType)
    {
      _taskType = taskType;
      _setupDelegate = (start, count, inputs, outputs) => { };
      _selectEntitiesDelegate = (outputs) => { };
      _validationSetupDelegate = (start, count, inputs, outputs) => { };
      _validateEntityDelegate = (index, keys, outputs) => { };
      _createTaskDelegate = (index, results, outputs) => { };
    }

    /// <summary>
    /// Sets the setup delegate for task preparation.
    /// </summary>
    /// <param name="setupDelegate">Delegate to handle task setup.</param>
    /// <returns>The current TaskBuilder instance for method chaining.</returns>
    public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithSetup(Action<int, int, int[], List<TSetupOutput>> setupDelegate)
    {
      _setupDelegate = setupDelegate;
      return this;
    }

    /// <summary>
    /// Sets the delegate for selecting entities for task processing.
    /// </summary>
    /// <param name="selectEntitiesDelegate">Delegate to select entities.</param>
    /// <returns>The current TaskBuilder instance for method chaining.</returns>
    public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithSelectEntities(Action<NativeList<Guid>> selectEntitiesDelegate)
    {
      _selectEntitiesDelegate = selectEntitiesDelegate;
      return this;
    }

    /// <summary>
    /// Sets the validation setup delegate for entity validation preparation.
    /// </summary>
    /// <param name="validationSetupDelegate">Delegate to prepare entity validation.</param>
    /// <returns>The current TaskBuilder instance for method chaining.</returns>
    public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithValidationSetup(Action<int, int, Guid[], List<TValidationSetupOutput>> validationSetupDelegate)
    {
      _validationSetupDelegate = validationSetupDelegate;
      return this;
    }

    /// <summary>
    /// Sets the delegate for validating entities.
    /// </summary>
    /// <param name="validateEntityDelegate">Delegate to validate entities.</param>
    /// <returns>The current TaskBuilder instance for method chaining.</returns>
    public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithValidateEntity(Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> validateEntityDelegate)
    {
      _validateEntityDelegate = validateEntityDelegate;
      return this;
    }

    /// <summary>
    /// Sets the delegate for creating tasks based on validation results.
    /// </summary>
    /// <param name="createTaskDelegate">Delegate to create tasks.</param>
    /// <returns>The current TaskBuilder instance for method chaining.</returns>
    public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithCreateTask(Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> createTaskDelegate)
    {
      _createTaskDelegate = createTaskDelegate;
      return this;
    }

    /// <summary>
    /// Gets the setup delegate.
    /// </summary>
    public Action<int, int, int[], List<TSetupOutput>> SetupDelegate => _setupDelegate;

    /// <summary>
    /// Gets the entity selection delegate.
    /// </summary>
    public Action<NativeList<Guid>> SelectEntitiesDelegate => _selectEntitiesDelegate;

    /// <summary>
    /// Gets the validation setup delegate.
    /// </summary>
    public Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate => _validationSetupDelegate;

    /// <summary>
    /// Gets the entity validation delegate.
    /// </summary>
    public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate => _validateEntityDelegate;

    /// <summary>
    /// Gets the task creation delegate.
    /// </summary>
    public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate => _createTaskDelegate;
  }

  public static class Extensions
  {
    public enum TaskName
    {
      DeliverInventory,
      PackagerRefillStation,
      PackagerEmptyLoadingDock,
      PackagerRestock,
      PackagingStation,
      MixingStation,
      SimpleExample
    }

    public enum TaskEmployeeType
    {
      Any,
      Chemist,
      Handler,
      Botanist,
      Driver,
      Cleaner
    }

    public enum EntityType
    {
      None,
      LoadingDock,
      Storage,
      ChemistryStation, // stations
      MixingStation,
      Pot,
      PackagingStation,
      Botanist, // employees
      Chemist,
    }

    [BurstCompile]
    public static StorageType EntityStorageTypeMap(EntityType entityType)
    {
      switch (entityType)
      {
        case EntityType.ChemistryStation:
        case EntityType.MixingStation:
        case EntityType.Pot:
        case EntityType.PackagingStation:
          return StorageType.Station;
        case EntityType.Botanist:
        case EntityType.Chemist:
          return StorageType.Employee;
        default:
          return 0;
      }
    }

    /// <summary>
    /// Represents an empty struct for use as a placeholder in generic task processing.
    /// </summary>
    public struct Empty : IDisposable
    {
      /// <summary>
      /// Disposes of the struct's resources.
      /// </summary>
      public void Dispose() { }
    }

    /// <summary>
    /// Describes a task with details for execution and tracking.
    /// </summary>
    [BurstCompile]
    public struct TaskDescriptor
    {
      public Guid EntityGuid;
      public Guid TaskId;
      public TaskName Type;
      public int ActionId;
      public TaskEmployeeType EmployeeType;
      public int Priority;
      public ItemData Item;
      public int Quantity;
      public Guid PickupGuid;
      public NativeArray<int> PickupSlotIndices;
      public Guid DropoffGuid;
      public NativeArray<int> DropoffSlotIndices;
      public FixedString32Bytes PropertyName;
      public float CreationTime;
      public NativeList<LogEntry> Logs;
      public Guid ReservedFor;

      /// <summary>
      /// Creates a new TaskDescriptor with the specified parameters and logs its creation.
      /// </summary>
      /// <param name="entityGuid">The GUID of the entity associated with the task.</param>
      /// <param name="type">The type of the task.</param>
      /// <param name="actionId">The action ID for the task.</param>
      /// <param name="employeeType">The type of employee required for the task.</param>
      /// <param name="priority">The priority level of the task.</param>
      /// <param name="propertyName">The name of the property associated with the task.</param>
      /// <param name="item">The item data for the task.</param>
      /// <param name="quantity">The quantity of items for the task.</param>
      /// <param name="pickupGuid">The GUID of the pickup location.</param>
      /// <param name="pickupSlotIndices">The slot indices for pickup.</param>
      /// <param name="dropoffGuid">The GUID of the dropoff location.</param>
      /// <param name="dropoffSlotIndices">The slot indices for dropoff.</param>
      /// <param name="creationTime">The time the task was created.</param>
      /// <param name="logs">The log entries for the task.</param>
      /// <param name="reservedFor">The GUID of the employee reserved for the task, if any.</param>
      /// <returns>The created TaskDescriptor.</returns>
      public static TaskDescriptor Create(
                      Guid entityGuid, TaskName type, int actionId, TaskEmployeeType employeeType, int priority,
                      FixedString32Bytes propertyName, ItemData item, int quantity,
                      Guid pickupGuid, NativeArray<int> pickupSlotIndices,
                      Guid dropoffGuid, NativeArray<int> dropoffSlotIndices,
                      float creationTime, NativeList<LogEntry> logs, Guid reservedFor = default)
      {
        var descriptor = new TaskDescriptor
        {
          EntityGuid = entityGuid,
          TaskId = Guid.NewGuid(),
          Type = type,
          ActionId = actionId,
          EmployeeType = employeeType,
          Priority = priority,
          PropertyName = propertyName,
          Item = item,
          Quantity = quantity,
          PickupGuid = pickupGuid,
          PickupSlotIndices = pickupSlotIndices,
          DropoffGuid = dropoffGuid,
          DropoffSlotIndices = dropoffSlotIndices,
          CreationTime = creationTime,
          Logs = logs,
          ReservedFor = reservedFor
        };
        logs.Add(new LogEntry
        {
          Message = $"Created task {descriptor.TaskId} for entity {entityGuid}, type {type}, action {actionId}",
          Level = Level.Verbose,
          Category = Category.Tasks
        });
        return descriptor;
      }
    }

    /// <summary>
    /// Holds validation results for an entity during task processing.
    /// </summary>
    [BurstCompile]
    public struct ValidationResultData
    {
      public Guid EntityGuid;
      public bool IsValid;
      public int State;
      public ItemData Item;
      public int Quantity;
      public int DestinationCapacity;
      public Guid PickupGuid;
      public NativeArray<int> PickupSlotIndices;
      public Guid DropoffGuid;
      public NativeArray<int> DropoffSlotIndices;
    }

    /// <summary>
    /// Represents the result of a task creation attempt.
    /// </summary>
    [BurstCompile]
    public struct TaskResult


    {
      public TaskDescriptor Task;
      public bool Success;
      public FixedString128Bytes FailureReason;
      /// <summary>
      /// Initializes a new TaskResult with the specified task and success status.
      /// </summary>
      /// <param name="task">The task descriptor.</param>
      /// <param name="success">Whether the task creation was successful.</param>
      /// <param name="failureReason">The reason for failure, if applicable.</param>
      public TaskResult(TaskDescriptor task, bool success, FixedString128Bytes failureReason = default)
      {
        Task = task;
        Success = success;
        FailureReason = success ? default : failureReason.IsEmpty ? "Unknown failure" : failureReason;
      }
    }

    /// <summary>
    /// Stores data about disabled entities and their reasons.
    /// </summary>
    [BurstCompile]
    public struct DisabledEntityData
    {
      public int ActionId;
      public DisabledReasonType ReasonType;
      public NativeList<ItemData> RequiredItems;
      public bool AnyItem;
      public int State;

      public enum DisabledReasonType
      {
        MissingItem,
        NoDestination
      }
    }

    public enum EQualityBurst
    {
      Trash,
      Poor,
      Standard,
      Premium,
      Heavenly,
      None
    }
  }

  public interface ITask
  {
    TaskName Type { get; }
    EntityType[] SupportedEntityTypes { get; }
    ITaskBurstFor CreateTaskStruct { get; }
    IEnumerator Execute(Employee employee, TaskDescriptor task);
  }

  // Interface for task-specific structs
  public interface ITaskBurstFor
  {
    void ExecuteFor(int index, NativeArray<Guid> inputs, NativeList<TaskResult> outputs, NativeList<LogEntry> logs);
  }

  [BurstCompile]
  public struct TaskBurstForExample : ITaskBurstFor
  {
    public NativeParallelHashMap<Guid, StationData> StationCacheMap;

    public void ExecuteFor(int index, NativeArray<Guid> inputs, NativeList<TaskResult> outputs, NativeList<LogEntry> logs)
    {
    }
  }

  public interface ITaskBurst
  {
    void Execute(Guid input, NativeList<TaskResult> outputs, NativeList<LogEntry> logs);
  }

  [BurstCompile]
  public struct TaskBurstExample : ITaskBurst
  {
    public FixedString32Bytes PropertyName;

    public void Execute(Guid input, NativeList<TaskResult> outputs, NativeList<LogEntry> logs)
    {
    }
  }

  public abstract class BaseTask : ITask
  {
    public abstract TaskName Type { get; }
    public abstract EntityType[] SupportedEntityTypes { get; }
    public abstract ITaskBurstFor CreateTaskStruct { get; }
    public abstract IEnumerator Execute(Employee employee, TaskDescriptor task);
  }

  public class TaskQueue
  {
    internal readonly NativeParallelHashSet<Guid> PendingTaskEntities = new(20, Allocator.Persistent);
    private readonly Dictionary<TaskEmployeeType, List<TaskDescriptor>> _specificTasks = new(20);
    private readonly List<TaskDescriptor> _anyTasks = new(20);
    private readonly TaskService _taskService;

    public TaskQueue(TaskService taskService)
    {
      _taskService = taskService;
      foreach (TaskEmployeeType type in Enum.GetValues(typeof(TaskEmployeeType)))
      {
        _specificTasks[type] = new();
      }
    }

    public void Enqueue(TaskDescriptor task)
    {
      PendingTaskEntities.Add(task.EntityGuid);
      if (task.ReservedFor != default)
      {
        _specificTasks[task.EmployeeType].Add(task);
      }
      else if (task.EmployeeType == TaskEmployeeType.Any)
      {
        _anyTasks.Add(task);
      }
      else
      {
        _specificTasks[task.EmployeeType].Add(task);
      }
    }

    public bool SelectTask(TaskEmployeeType employeeType, Guid employeeGuid, out TaskDescriptor task)
    {
      task = default;

      // Check reserved tasks first
      var specificTasks = _specificTasks[employeeType];
      int reservedIndex = specificTasks.FindIndex(t => t.ReservedFor == employeeGuid);
      if (reservedIndex >= 0)
      {
        task = specificTasks[reservedIndex];
        specificTasks.RemoveAt(reservedIndex);
        return true;
      }

      // Check type-specific tasks
      if (specificTasks.Count > 0)
      {
        task = specificTasks[0];
        specificTasks.RemoveAt(0);
        return true;
      }

      // Check any-employee tasks
      if (_anyTasks.Count > 0)
      {
        task = _anyTasks[0];
        _anyTasks.RemoveAt(0);
        return true;
      }

      return false;
    }

    public void CompleteTask(Guid taskId)
    {
      foreach (var list in _specificTasks.Values)
      {
        int index = list.FindIndex(t => t.TaskId == taskId);
        if (index >= 0)
        {
          PendingTaskEntities.Remove(list[index].EntityGuid);
          list.RemoveAt(index);
        }
      }
      int anyIndex = _anyTasks.FindIndex(t => t.TaskId == taskId);
      if (anyIndex >= 0)
      {
        PendingTaskEntities.Remove(_anyTasks[anyIndex].EntityGuid);
        _anyTasks.RemoveAt(anyIndex);
      }
    }

    internal void Dispose()
    {
      if (PendingTaskEntities.IsCreated) PendingTaskEntities.Dispose();
      _specificTasks.Clear();
      _anyTasks.Clear();
    }
  }

  /// <summary>
  /// Manages task creation, queuing, and execution for a property.
  /// </summary>
  public class TaskService
  {
    private readonly Property _property;
    private readonly NativeList<Guid> _entityGuids;
    private readonly NativeListPool<TaskResult> _taskResultPool;
    private readonly NativeListPool<ValidationResultData> _validationResultPool;
    private readonly NativeParallelHashMap<Guid, NativeList<TaskDescriptor>> _employeeSpecificTasks;
    private Coroutine _processTasksCoroutine;
    internal readonly NativeListPool<LogEntry> LogPool;
    internal readonly CacheService CacheService;
    internal readonly TaskRegistry TaskRegistry;
    internal readonly EntityStateService EntityStateService;
    internal readonly TaskQueue TaskQueue;

    /// <summary>
    /// Initializes a new TaskService for the specified property.
    /// </summary>
    /// <param name="property">The property to manage tasks for.</param>
    public TaskService(Property property)
    {
      _property = property ?? throw new ArgumentNullException(nameof(property));
      TaskRegistry = TaskServiceManager.TaskRegistries[property];
      TaskQueue = new TaskQueue(this);
      CacheService = CacheService.GetOrCreateService(property);
      EntityStateService = TaskServiceManager.StateServices[property];
      _entityGuids = new NativeList<Guid>(Allocator.Persistent);
      LogPool = PoolUtility.InitializeNativeListPool<LogEntry>(() => new NativeList<LogEntry>(100, Allocator.Persistent), 10, "TaskService_LogPool");
      _taskResultPool = PoolUtility.InitializeNativeListPool<TaskResult>(() => new NativeList<TaskResult>(100, Allocator.Persistent), 10, "TaskService_TaskResultPool");
      _validationResultPool = PoolUtility.InitializeNativeListPool<ValidationResultData>(() => new NativeList<ValidationResultData>(100, Allocator.Persistent), 10, "TaskService_ValidationResultPool");
      _employeeSpecificTasks = new NativeParallelHashMap<Guid, NativeList<TaskDescriptor>>(100, Allocator.Persistent);
      Log(Level.Info, $"Initialized TaskService for property {property.name}", Category.Tasks);
      _processTasksCoroutine = CoroutineRunner.Instance.RunCoroutine(ProcessTasks());
    }

    private IEnumerator ProcessTasks()
    {
      while (true)
      {
        var scope = new DisposableScope(this);
        var taskResults = _taskResultPool.Get();
        scope.Add(taskResults);
        foreach (var task in TaskRegistry.AllTasks)
        {
          yield return CreateTasks(task, taskResults, scope);
        }
        scope.Dispose();
        yield return null;
      }
    }

    private IEnumerator CreateTasks(BaseTask task, NativeList<TaskResult> taskResults, DisposableScope scope)
    {
      if (task == null || task.SupportedEntityTypes == null || task.SupportedEntityTypes.Length == 0)
      {
        Log(Level.Warning, $"Invalid task or supported entity types for {task?.Type}", Category.Tasks);
        yield break;
      }
      var logs = LogPool.Get();
      scope.Add(logs);
      var entityTypes = new NativeArray<EntityType>(task.SupportedEntityTypes, Allocator.TempJob);
      var entities = new NativeList<Guid>(Allocator.TempJob);
      scope.Add(entities);

      // Select entities
      yield return SmartExecution.Smart.ExecuteBurstFor<EntityType, Guid, SelectEntitiesBurstFor>(
          uniqueId: $"{_property.name}_{task.Type}_Select",
          itemCount: entityTypes.Count(),
          burstForAction: new SelectEntitiesBurstFor
          {
            PendingTaskEntities = TaskQueue.PendingTaskEntities,
            EntityStates = EntityStateService.EntityStates,
            EmployeeDataCache = CacheService.EmployeeDataCache,
            LoadingDockDataCache = CacheService.LoadingDockDataCache,
            StationDataCache = CacheService.StationDataCache,
            StorageDataCache = CacheService.StorageDataCache
          }.ExecuteFor,
          inputs: entityTypes,
          outputs: entities,
          logs: logs
      );

      // Create tasks
      yield return SmartExecution.Smart.ExecuteBurstFor<Guid, TaskResult, TaskBurstForExample>(
          uniqueId: $"{_property.name}_{task.Type}_Create",
          itemCount: entities.Length,
          burstForAction: task.CreateTaskStruct.ExecuteFor,
          inputs: entities.AsArray(),
          outputs: taskResults,
          logs: logs,
          nonBurstResultsAction: outputs =>
          {
            foreach (var result in outputs)
            {
              if (result.Success) TaskQueue.Enqueue(result.Task);
            }
          }
      );

      yield return ProcessLogs(logs);
    }

    [BurstCompile]
    public struct SelectEntitiesBurstFor
    {
      public NativeParallelHashSet<Guid> PendingTaskEntities;
      public NativeParallelHashMap<Guid, int> EntityStates;
      public NativeParallelHashMap<Guid, StationData> StationDataCache;
      public NativeParallelHashMap<Guid, EmployeeData> EmployeeDataCache;
      public NativeParallelHashMap<Guid, StorageData> StorageDataCache;
      public NativeParallelHashMap<Guid, LoadingDockData> LoadingDockDataCache;
      public void ExecuteFor(int index, NativeArray<EntityType> inputs, NativeList<Guid> outputs, NativeList<LogEntry> logs)
      {
        var input = inputs[index];
        if (input == EntityType.Storage)
        {
          foreach (var storage in StorageDataCache)
            if (!PendingTaskEntities.Contains(storage.Key) && EntityStates.TryGetValue(storage.Key, out var state) && state != 0)
              outputs.Add(storage.Key);
        }
        else if (input == EntityType.LoadingDock)
        {
          foreach (var loadingDock in LoadingDockDataCache)
            if (!PendingTaskEntities.Contains(loadingDock.Key) && EntityStates.TryGetValue(loadingDock.Key, out var state) && state != 0)
              outputs.Add(loadingDock.Key);
        }
        else
        {
          var type = EntityStorageTypeMap(input);
          if (type == StorageType.Station)
          {
            foreach (var station in StationDataCache)
              if (!PendingTaskEntities.Contains(station.Key) && EntityStates.TryGetValue(station.Key, out var state) && state != 0)
                outputs.Add(station.Key);
          }
          else if (type == StorageType.Employee)
          {
            foreach (var employee in EmployeeDataCache)
              if (!PendingTaskEntities.Contains(employee.Key) && EntityStates.TryGetValue(employee.Key, out var state) && state != 0)
                outputs.Add(employee.Key);
          }
        }
      }
    }

    public IEnumerator CreateFollowUpTask(Guid employeeGuid, Guid entityGuid, TaskName taskType)
    {
      var task = TaskRegistry.GetTask(taskType);
      var scope = new DisposableScope(this);
      var taskResults = _taskResultPool.Get();
      scope.Add(taskResults);
      var logs = LogPool.Get();
      scope.Add(logs);
      var entities = new NativeList<Guid>(1, Allocator.TempJob) { entityGuid };
      scope.Add(entities);

      yield return SmartExecution.Smart.ExecuteBurstFor<Guid, TaskResult, TaskBurstForExample>(
          uniqueId: $"{_property.name}_{task.Type}_Create_FollowUp",
          itemCount: 1,
          burstForAction: task.CreateTaskStruct.ExecuteFor,
          inputs: entities.AsArray(),
          outputs: taskResults,
          logs: logs,
          nonBurstResultsAction: outputs =>
          {
            if (outputs.Count() == 1 && outputs[0].Success)
            {
              var taskDescriptor = outputs[0].Task;
              taskDescriptor.ReservedFor = employeeGuid;
              TaskQueue.Enqueue(taskDescriptor);
            }
          }
      );

      scope.Dispose();
    }

    public bool TryGetTask(Employee employee, out TaskDescriptor task, out BaseTask taskImpl)
    {
      TaskEmployeeType employeeType;
      try
      {
        employeeType = Enum.Parse<TaskEmployeeType>(employee.EmployeeType.ToString());
      }
      catch
      {
        task = default;
        taskImpl = null;
        return false;
      }
      if (TaskQueue.SelectTask(employeeType, employee.GUID, out task))
      {
        taskImpl = TaskRegistry.GetTask(task.Type);
        return taskImpl != null;
      }
      taskImpl = null;
      return false;
    }

    public void CompleteTask(TaskDescriptor task)
    {
      TaskQueue.CompleteTask(task.TaskId);
    }
    internal struct DisposableScope : IDisposable
    {
      private TaskService _taskService;
      private NativeList<NativeList<LogEntry>> _logEntries;
      private NativeList<NativeList<TaskResult>> _taskResults;
      private NativeList<NativeList<int>> _ints;
      private NativeList<NativeList<Guid>> _guids;

      public DisposableScope(TaskService service)
      {
        _taskService = service;
        _logEntries = new NativeList<NativeList<LogEntry>>(10, Allocator.Persistent);
        _ints = new NativeList<NativeList<int>>(10, Allocator.Persistent);
        _taskResults = new NativeList<NativeList<TaskResult>>(10, Allocator.Persistent);
        _guids = new NativeList<NativeList<Guid>>(10, Allocator.Persistent);
      }

      public void Add(NativeList<LogEntry> logEntries) => _logEntries.Add(logEntries);
      public void Add(NativeList<int> ints) => _ints.Add(ints);
      public void Add(NativeList<TaskResult> taskResults) => _taskResults.Add(taskResults);
      public void Add(NativeList<Guid> guids) => _guids.Add(guids);

      public void Dispose()
      {
        foreach (var log in _logEntries) _taskService.LogPool.Return(log);
        foreach (var result in _taskResults) _taskService._taskResultPool.Return(result);
        foreach (var guid in _guids) guid.Dispose();
        _logEntries.Dispose();
        _taskResults.Dispose();
        _guids.Dispose();
      }
    }

    public void Dispose()
    {
      TaskQueue.Dispose();
      PoolUtility.DisposeNativeListPool(LogPool, "TaskService_LogPool");
      PoolUtility.DisposeNativeListPool(_taskResultPool, "TaskService_TaskResultPool");
      PoolUtility.DisposeNativeListPool(_validationResultPool, "TaskService_ValidationResultPool");
      if (_entityGuids.IsCreated) _entityGuids.Dispose();
      if (_employeeSpecificTasks.IsCreated) _employeeSpecificTasks.Dispose();
    }

    internal void DeactivateProperty()
    {
      CoroutineRunner.Instance.StopCoroutine(_processTasksCoroutine);
    }
  }

  /// <summary>
  /// Manages task services and registries for properties.
  /// </summary>
  public static class TaskServiceManager
  {
    private static readonly Dictionary<Property, TaskService> _services = new();
    internal static readonly Dictionary<Property, EntityStateService> StateServices = new();
    internal static readonly Dictionary<Property, TaskRegistry> TaskRegistries = new();

    /// <summary>
    /// Gets or creates a task service for the specified property.
    /// </summary>
    /// <param name="property">The property to get or create a service for.</param>
    /// <returns>The task service for the property.</returns>
    public static TaskService GetOrCreateService(Property property)
    {
      if (!_services.TryGetValue(property, out var service))
      {
        StateServices[property] = new EntityStateService(property);
        TaskRegistries[property] = new TaskRegistry(property);
        TaskRegistries[property].Initialize();
        service = new TaskService(property);
        _services[property] = service;
        Log(Level.Info, $"Created TaskService for property {property.name}", Category.Tasks);
      }
      return service;
    }

    /// <summary>
    /// Disposes of all services and registries.
    /// </summary>
    public static void Cleanup()
    {
      foreach (var service in _services.Values)
        service.Dispose();
      _services.Clear();
      foreach (var service in StateServices.Values)
        service.Dispose();
      StateServices.Clear();
      foreach (var registry in TaskRegistries.Values)
        registry.Dispose();
      TaskRegistries.Clear();
      Log(Level.Info, "TaskServiceManager cleaned up", Category.Tasks);
    }
  }

  /// <summary>
  /// Registers and manages tasks.
  /// </summary>
  public partial class TaskRegistry
  {
    private readonly Property _property;
    private readonly List<BaseTask> tasks = new();
    public IEnumerable<BaseTask> AllTasks => tasks;

    public TaskRegistry(Property property)
    {
      _property = property;
    }

    /// <summary>
    /// Initializes the task registry.
    /// </summary>
    public void Initialize()
    {
      // This method will be extended by the generated code
      Log(Level.Info, "Initializing TaskRegistry with auto-registered tasks", Category.Tasks);
    }

    /// <summary>
    /// Registers a task.
    /// </summary>
    /// <param name="task">The task to register.</param>
    public void Register(BaseTask task)
    {
      if (!tasks.Contains(task))
      {
        tasks.Add(task);
        Log(Level.Info, $"Registered task: {task.Type}", Category.Tasks);
      }
    }

    /// <summary>
    /// Gets a task by its type.
    /// </summary>
    /// <param name="type">The type of the task.</param>
    /// <returns>The task, or null if not found.</returns>
    public BaseTask GetTask(TaskName type)
    {
      return tasks.FirstOrDefault(t => t.Type == type);
    }

    /// <summary>
    /// Disposes of the task registry.
    /// </summary>
    public void Dispose()
    {
      tasks.Clear();
      Log(Level.Info, "Disposed TaskRegistry", Category.Tasks);
    }
  }

  /// <summary>
  /// Attribute to mark classes as entity tasks for source generation.
  /// </summary>
  [AttributeUsage(AttributeTargets.Class)]
  public class EntityTaskAttribute : Attribute
  {
  }
}