using FishNet;
using ScheduleOne.Employees;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using System.Collections.Concurrent;
using Unity.Jobs;
using Unity.Collections;
using ScheduleOne.Property;
using Unity.Burst;
using ScheduleOne.ObjectScripts;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Storage.Constants;
using ScheduleOne.Delivery;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Performance.FishNetExtensions;
using NoLazyWorkers.Performance;
using System.Collections;
using static NoLazyWorkers.Performance.Metrics;
using NoLazyWorkers.JobService;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.TaskService.StationTasks;

namespace NoLazyWorkers.TaskService
{
  public static class Extensions
  {
    public static List<ITask> InitializeTasks()
    {
      return [
        new MixingStationTask()
      ];
    }

    public enum TaskTypes
    {
      DeliverInventory,
      PackagerRefillStation,
      PackagerEmptyLoadingDock,
      PackagerRestock,
      PackagingStation,
      MixingStation
    }

    public enum EmployeeTypes
    {
      Any,
      Chemist,
      Handler,
      Botanist,
      Driver,
      Cleaner
    }

    [BurstCompile]
    public struct TaskDescriptor
    {
      public Guid EntityGuid;
      public Guid TaskId;
      public TaskTypes Type;
      public int ActionId;
      public EmployeeTypes EmployeeType;
      public int Priority;
      public ItemKey Item;
      public int Quantity;
      public Guid PickupGuid;
      public NativeArray<int> PickupSlotIndices;
      public Guid DropoffGuid;
      public NativeArray<int> DropoffSlotIndices;
      public FixedString32Bytes PropertyName;
      public float CreationTime;

      public static TaskDescriptor Create(
          Guid entityGuid, TaskTypes type, int actionId, EmployeeTypes employeeType, int priority,
          string propertyName, ItemKey item, int quantity,
          Guid pickupGuid, int[] pickupSlotIndices,
          Guid dropoffGuid, int[] dropoffSlotIndices,
          float creationTime)
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
          PickupSlotIndices = pickupSlotIndices != null ? new NativeArray<int>(pickupSlotIndices, Allocator.Persistent) : default,
          DropoffGuid = dropoffGuid,
          DropoffSlotIndices = dropoffSlotIndices != null ? new NativeArray<int>(dropoffSlotIndices, Allocator.Persistent) : default,
          CreationTime = creationTime
        };
#if DEBUG
        Log(Level.Verbose, $"Created task {descriptor.TaskId} for entity {entityGuid}, type {type}, action {actionId}", Category.Tasks);
#endif
        return descriptor;
      }

      public void Dispose()
      {
        if (PickupSlotIndices.IsCreated) PickupSlotIndices.Dispose();
        if (DropoffSlotIndices.IsCreated) DropoffSlotIndices.Dispose();
      }
    }

    [BurstCompile]
    public struct TaskTypeEntityKey : IEquatable<TaskTypeEntityKey>
    {
      public TaskTypes Type;
      public Guid EntityGuid;

      public bool Equals(TaskTypeEntityKey other) => EntityGuid == other.EntityGuid && Type == other.Type;
      public override bool Equals(object obj) => obj is TaskTypeEntityKey other && Equals(other);
      public override int GetHashCode() => HashCode.Combine(EntityGuid, Type);
    }

    public interface IEntitySelector
    {
      NativeList<Guid> SelectEntities(Property property, Allocator allocator);
    }

    public interface ITask
    {
      TaskTypes Type { get; }
      IEntitySelector EntitySelector { get; }
      JobHandle ScheduleValidationJob(
        NativeList<Guid> entityGuids,
        NativeArray<ValidationResultData> results,
        NativeList<LogEntry> logs,
        Property property,
        TaskService taskService,
        CacheManager cacheManager,
        DisabledEntityService disabledService
      );
      Task ValidateEntityStateAsync(object entity);
      Task ExecuteAsync(Employee employee, TaskDescriptor task);
      Task FollowUpAsync(Employee employee, TaskDescriptor task);
      IEnumerator CreateTaskForState(object entity, Property property, ValidationResultData result, TaskDispatcher dispatcher, DisabledEntityService disabledService, NativeList<LogEntry> logs);
    }

    public struct TaskResult
    {
      public TaskDescriptor Task { get; }
      public bool Success { get; }
      public string FailureReason { get; }
      public TaskResult(TaskDescriptor task, bool success, string failureReason = null)
      {
        Task = task;
        Success = success;
        FailureReason = success ? "" : failureReason ?? "Unknown failure";
      }
    }

    [BurstCompile]
    public struct ValidationResultData
    {
      public bool IsValid;
      public int State;
      public ItemKey Item;
      public int Quantity;
      public int DestinationCapacity;
    }

    [BurstCompile]
    public struct DisabledEntityData
    {
      public int ActionId;
      public DisabledReasonType ReasonType;
      public NativeList<ItemKey> RequiredItems;
      public bool AnyItem;

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

  public class DisabledEntityService
  {
    public NativeParallelHashMap<Guid, DisabledEntityData> disabledEntities;
    private readonly CacheManager cacheManager;

    public DisabledEntityService(CacheManager cache)
    {
      cacheManager = cache ?? throw new ArgumentNullException(nameof(cache));
      disabledEntities = new NativeParallelHashMap<Guid, DisabledEntityData>(100, Allocator.Persistent);
      cacheManager.OnStorageSlotUpdated += HandleStorageSlotUpdated;
      Log(Level.Info, "Initialized DisabledEntityService", Category.Tasks);
    }

    public void AddDisabledEntity(Guid entityGuid, int actionId, DisabledEntityData.DisabledReasonType reasonType, NativeList<ItemKey> requiredItems, bool anyItem = true)
    {
      var data = new DisabledEntityData
      {
        ActionId = actionId,
        ReasonType = reasonType,
        RequiredItems = new NativeList<ItemKey>(requiredItems.Length, Allocator.Persistent),
        AnyItem = anyItem
      };
      data.RequiredItems.AddRange(requiredItems);
      disabledEntities[entityGuid] = data;
      Log(Level.Verbose, $"Disabled entity {entityGuid} for action {actionId}, reason: {reasonType}, items: {string.Join(", ", requiredItems.Select(i => i.Id))}", Category.Tasks);
    }

    public bool IsEntityDisabled(Guid entityGuid, int actionId)
    {
      if (disabledEntities.TryGetValue(entityGuid, out var data))
        return data.ActionId == actionId;
      return false;
    }

    private void HandleStorageSlotUpdated(StorageKey storageKey, SlotData slotData)
    {
      var keysToRemove = new NativeList<Guid>(Allocator.TempJob);
      foreach (var kvp in disabledEntities)
      {
        var entityGuid = kvp.Key;
        var data = kvp.Value;
        if (data.ReasonType == DisabledEntityData.DisabledReasonType.MissingItem)
        {
          bool allItemsAvailable = true;
          bool anyItemAvailable = false;
          for (int i = 0; i < data.RequiredItems.Length; i++)
          {
            if (data.RequiredItems[i].Equals(slotData.Item) && slotData.Quantity > 0)
              anyItemAvailable = true;
            else
              allItemsAvailable = false;
            if (anyItemAvailable && !allItemsAvailable) break;
          }
          if ((data.AnyItem && anyItemAvailable) || allItemsAvailable)
          {
            keysToRemove.Add(entityGuid);
            Log(Level.Verbose, $"Re-enabled entity {entityGuid} for action {data.ActionId} due to item availability", Category.Tasks);
          }
        }
      }

      foreach (var key in keysToRemove)
      {
        if (disabledEntities.TryGetValue(key, out var data))
        {
          data.RequiredItems.Dispose();
          disabledEntities.Remove(key);
        }
      }
      keysToRemove.Dispose();
    }

    public void Dispose()
    {
      cacheManager.OnStorageSlotUpdated -= HandleStorageSlotUpdated;
      foreach (var data in disabledEntities.GetValueArray(Allocator.Temp))
        if (data.RequiredItems.IsCreated)
          data.RequiredItems.Dispose();
      if (disabledEntities.IsCreated)
        disabledEntities.Dispose();
      Log(Level.Info, "Disposed DisabledEntityService", Category.Tasks);
    }
  }

  public class TaskDispatcher
  {
    private readonly Dictionary<EmployeeTypes, NativeList<TaskDescriptor>> _highPriorityTasks;
    private readonly Dictionary<EmployeeTypes, NativeList<TaskDescriptor>> _normalPriorityTasks;
    private readonly NativeList<TaskDescriptor> _anyEmployeeTasks;
    private NativeParallelHashMap<Guid, TaskDescriptor> _activeTasks;
    public NativeParallelHashMap<TaskTypeEntityKey, bool> activeTasksByType;

    public TaskDispatcher(int capacity)
    {
      _highPriorityTasks = new Dictionary<EmployeeTypes, NativeList<TaskDescriptor>>();
      _normalPriorityTasks = new Dictionary<EmployeeTypes, NativeList<TaskDescriptor>>();
      foreach (EmployeeTypes type in Enum.GetValues(typeof(EmployeeTypes)))
      {
        _highPriorityTasks[type] = new NativeList<TaskDescriptor>(capacity, Allocator.Persistent);
        _normalPriorityTasks[type] = new NativeList<TaskDescriptor>(capacity, Allocator.Persistent);
      }
      _anyEmployeeTasks = new NativeList<TaskDescriptor>(capacity, Allocator.Persistent);
      _activeTasks = new NativeParallelHashMap<Guid, TaskDescriptor>(capacity, Allocator.Persistent);
      activeTasksByType = new NativeParallelHashMap<TaskTypeEntityKey, bool>(capacity, Allocator.Persistent);
    }

    public void Enqueue(TaskDescriptor task)
    {
      var key = new TaskTypeEntityKey { Type = task.Type, EntityGuid = task.EntityGuid };
      if (activeTasksByType.ContainsKey(key))
      {
        Log(Level.Verbose, $"Task of type {task.Type} already exists for entity {task.EntityGuid}, skipping", Category.Tasks);
        return;
      }

      var taskList = task.EmployeeType == EmployeeTypes.Any
          ? _anyEmployeeTasks
          : task.Priority >= 100
              ? _highPriorityTasks[task.EmployeeType]
              : _normalPriorityTasks[task.EmployeeType];
      taskList.Add(task);
      activeTasksByType[key] = true;
#if DEBUG
      Log(Level.Verbose, $"Enqueued task {task.TaskId} for {task.EmployeeType}, priority {task.Priority}", Category.Tasks);
#endif
    }

    public bool SelectTask(EmployeeTypes employeeType, Guid employeeGuid, out TaskDescriptor task)
    {
      task = default;
      var highPriorityList = _highPriorityTasks[employeeType];
      if (highPriorityList.Length > 0)
      {
        var candidate = highPriorityList[0];
        task = candidate;
        _activeTasks[candidate.TaskId] = task;
        highPriorityList.RemoveAt(0);
        Log(Level.Info, $"Selected high-priority task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
        return true;
      }

      var normalPriorityList = _normalPriorityTasks[employeeType];
      if (normalPriorityList.Length > 0)
      {
        var candidate = normalPriorityList[0];
        task = candidate;
        _activeTasks[candidate.TaskId] = task;
        normalPriorityList.RemoveAt(0);
        Log(Level.Info, $"Selected normal-priority task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
        return true;
      }

      if (_anyEmployeeTasks.Length > 0)
      {
        var candidate = _anyEmployeeTasks[0];
        task = candidate;
        _activeTasks[candidate.TaskId] = task;
        _anyEmployeeTasks.RemoveAt(0);
        Log(Level.Info, $"Selected any-employee task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
        return true;
      }
      return false;
    }

    public void CompleteTask(Guid taskId)
    {
      if (_activeTasks.TryGetValue(taskId, out var task))
      {
        var key = new TaskTypeEntityKey { Type = task.Type, EntityGuid = task.EntityGuid };
        activeTasksByType.Remove(key);
        _activeTasks.Remove(taskId);
        task.Dispose();
        Log(Level.Info, $"Completed task {taskId}", Category.Tasks);
      }
    }

    public void Dispose()
    {
      foreach (var list in _highPriorityTasks.Values.Concat(_normalPriorityTasks.Values).Append(_anyEmployeeTasks))
      {
        for (int i = 0; i < list.Length; i++)
          list[i].Dispose();
        list.Dispose();
      }
      if (_activeTasks.IsCreated)
        _activeTasks.Dispose();
      if (activeTasksByType.IsCreated)
        activeTasksByType.Dispose();
    }
  }

  public class TaskService
  {
    private readonly Property _property;
    private readonly CacheManager _cacheManager;
    private readonly TaskRegistry _taskRegistry;
    private readonly TaskDispatcher _taskDispatcher;
    private readonly DisabledEntityService _disabledService;
    private readonly NativeList<Guid> _entityGuids;
    private NativeArray<ValidationResultData> _validationResults;
    private bool _isProcessing;
    private Coroutine _validationCoroutine;
    private readonly Dictionary<Guid, NativeList<TaskDescriptor>> _employeeSpecificTasks;

    public TaskService(Property prop)
    {
      _property = prop ?? throw new ArgumentNullException(nameof(prop));
      _cacheManager = CacheManager.GetOrCreateCacheManager(_property);
      _cacheManager.Activate();
      _taskRegistry = TaskServiceManager.GetRegistry(prop);
      _taskDispatcher = new TaskDispatcher(100);
      _disabledService = new DisabledEntityService(_cacheManager);
      _entityGuids = new NativeList<Guid>(Allocator.Persistent);
      _validationResults = new NativeArray<ValidationResultData>(0, Allocator.Persistent);
      _employeeSpecificTasks = new Dictionary<Guid, NativeList<TaskDescriptor>>();
      Log(Level.Info, $"Initialized TaskService for property {_property.name}", Category.Tasks);
      _validationCoroutine = CoroutineRunner.Instance.RunCoroutine(ProcessTasksCoroutine());
    }

    private IEnumerator ProcessTasksCoroutine()
    {
      while (true)
      {
        if (!_isProcessing)
        {
          _isProcessing = true;
          yield return TrackExecutionCoroutine(nameof(ProcessTasksCoroutine), ValidateAndCreateTasks(), itemCount: _entityGuids.Length);
          _isProcessing = false;
        }
        yield return null;
      }
    }

    private IEnumerator ValidateAndCreateTasks()
    {
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        var getPropData = _cacheManager.TryGetPropertyDataAsync().AsCoroutine();
        yield return getPropData;
        var (success, stations, _) = getPropData.Result;
        if (!success)
        {
          Log(Level.Warning, $"[{_property.name}] No property data", Category.Tasks);
          yield break;
        }

        foreach (var iTask in _taskRegistry.AllTasks)
        {
          _entityGuids.Clear();
          using var selectedEntities = iTask.EntitySelector.SelectEntities(_property, Allocator.TempJob);
          var filteredEntities = new NativeList<Guid>(selectedEntities.Length, Allocator.TempJob);
          for (int i = 0; i < selectedEntities.Length; i++)
          {
            var entityGuid = selectedEntities[i];
            var key = new TaskTypeEntityKey { Type = iTask.Type, EntityGuid = entityGuid };
            if (!_taskDispatcher.activeTasksByType.ContainsKey(key))
              filteredEntities.Add(entityGuid);
          }
          _entityGuids.AddRange(filteredEntities);
          filteredEntities.Dispose();

          if (_entityGuids.Length == 0)
            continue;

          if (_validationResults.Length != _entityGuids.Length)
          {
            _validationResults.Dispose();
            _validationResults = new NativeArray<ValidationResultData>(_entityGuids.Length, Allocator.Persistent);
          }

          var validationJobHandle = iTask.ScheduleValidationJob(_entityGuids, _validationResults, logs, _property, this, _cacheManager, _disabledService);
          yield return new WaitUntil(() => validationJobHandle.IsCompleted);
          validationJobHandle.Complete();

          for (int i = 0; i < _entityGuids.Length; i++)
          {
            if (!_validationResults[i].IsValid)
              continue;

            var entityGuid = _entityGuids[i];
            var entity = ResolveEntity(entityGuid);
            if (entity == null)
              continue;

            yield return iTask.CreateTaskForState(entity, _property, _validationResults[i], _taskDispatcher, _disabledService, logs);

            var metrics = new NativeArray<Metric>(1, Allocator.TempJob);
            var metricsJob = new SingleEntityMetricsJob
            {
              TaskType = new FixedString64Bytes(iTask.Type.ToString()),
              Timestamp = Time.realtimeSinceStartup * 1000f,
              Metrics = metrics
            };
            metricsJob.Schedule();
          }

          ProcessLogs(logs);
        }
      }
      finally
      {
        logs.Dispose();
      }
    }

    public async Task<(bool Success, TaskDescriptor Task, ITask ITask)> TryGetTaskAsync(Employee employee)
    {
      return await TrackExecutionAsync(nameof(TryGetTaskAsync), async () =>
      {
        if (employee.AssignedProperty != _property)
          return (false, default, null);

        if (_taskDispatcher.SelectTask(Enum.Parse<EmployeeTypes>(employee.Type.ToString()), employee.GUID, out var task))
        {
          var iTask = _taskRegistry.GetTask(task.Type);
          return (true, task, iTask);
        }
        return (false, default, null);
      });
    }

    public void CompleteTask(TaskDescriptor task)
    {
      _taskDispatcher.CompleteTask(task.TaskId);
    }

    public IEnumerator CreateEmployeeSpecificTaskAsync(Guid employeeGuid, Guid entityGuid, Property prop, TaskTypes taskType)
    {
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        var iTask = _taskRegistry.GetTask(taskType);
        if (iTask == null)
        {
          Log(Level.Error, $"Task type {taskType} not registered", Category.Tasks);
          yield break;
        }

        var entity = ResolveEntity(entityGuid);
        if (entity == null)
        {
          Log(Level.Error, $"Entity {entityGuid} not found", Category.Tasks);
          yield break;
        }

        var entityGuids = new NativeList<Guid>(1, Allocator.TempJob) { entityGuid };
        var validationResults = new NativeArray<ValidationResultData>(1, Allocator.TempJob);

        var validationJobHandle = iTask.ScheduleValidationJob(entityGuids, validationResults, logs, _property, this, _cacheManager, _disabledService);
        yield return new WaitUntil(() => validationJobHandle.IsCompleted);
        validationJobHandle.Complete();

        if (validationResults[0].IsValid)
        {
          yield return iTask.CreateTaskForState(entity, prop, validationResults[0], _taskDispatcher, _disabledService, logs);
        }

        entityGuids.Dispose();
        validationResults.Dispose();

        if (!_employeeSpecificTasks.ContainsKey(employeeGuid))
          _employeeSpecificTasks[employeeGuid] = new NativeList<TaskDescriptor>(Allocator.Persistent);

        while (_employeeSpecificTasks.ContainsKey(employeeGuid) && _employeeSpecificTasks[employeeGuid].Length > 0)
        {
          yield return null;
        }

        var metrics = new NativeArray<Metric>(1, Allocator.TempJob);
        var metricsJob = new SingleEntityMetricsJob
        {
          TaskType = new FixedString64Bytes(iTask.Type.ToString()),
          Timestamp = Time.realtimeSinceStartup * 1000f,
          Metrics = metrics
        };
        metricsJob.Schedule();
      }
      finally
      {
        logs.Dispose();
      }
    }

    public TaskDescriptor GetEmployeeSpecificTask(Guid employeeGuid)
    {
      if (_employeeSpecificTasks.TryGetValue(employeeGuid, out var tasks) && tasks.Length > 0)
      {
        var task = tasks[0];
        tasks.RemoveAtSwapBack(0);
        if (tasks.Length == 0)
        {
          tasks.Dispose();
          _employeeSpecificTasks.Remove(employeeGuid);
        }
        return task;
      }
      return default;
    }

    public void Dispose()
    {
      if (_validationCoroutine != null)
        CoroutineRunner.Instance.StopCoroutine(_validationCoroutine);
      _entityGuids.Dispose();
      _validationResults.Dispose();
      _taskDispatcher.Dispose();
      _disabledService.Dispose();
      foreach (var tasks in _employeeSpecificTasks.Values)
        tasks.Dispose();
      _employeeSpecificTasks.Clear();
      _cacheManager.Deactivate();
      Log(Level.Info, $"Disposed TaskService for property {_property.name}", Category.Tasks);
    }

    public object ResolveEntity(Guid guid)
    {
      if (IEmployees[_property].TryGetValue(guid, out var entity))
        return entity;
      return null;
    }
  }

  public static class TaskServiceManager
  {
    private static readonly Dictionary<Property, TaskService> _services = new();
    private static readonly Dictionary<Property, TaskRegistry> _registries = new();

    public static TaskService GetOrCreateService(Property property)
    {
      if (!_services.TryGetValue(property, out var service))
      {
        _registries[property] = new TaskRegistry();
        _registries[property].Initialize(new List<ITask> { new MixingStationTask() });
        _registries[property].Initialize(InitializeTasks());
        service = new TaskService(property);
        _services[property] = service;
      }
      return service;
    }

    public static TaskRegistry GetRegistry(Property property)
    {
      if (!_registries.ContainsKey(property))
        _registries[property] = new TaskRegistry();
      return _registries[property];
    }

    public static void Clear()
    {
      foreach (var service in _services.Values)
        service.Dispose();
      _services.Clear();
      foreach (var registry in _registries.Values)
        registry.Dispose();
      _registries.Clear();
      Log(Level.Info, "TaskServiceManager cleaned up", Category.Tasks);
    }
  }

  public class TaskRegistry
  {
    private readonly List<ITask> tasks = new();
    public IEnumerable<ITask> AllTasks => tasks;

    public void Initialize(List<ITask> tasks)
    {
      foreach (var task in tasks)
        Register(task);
    }

    public void Register(ITask task)
    {
      if (!tasks.Contains(task))
      {
        tasks.Add(task);
        Log(Level.Info, $"Registered task: {task.Type}", Category.Tasks);
      }
    }

    public ITask GetTask(TaskTypes type)
    {
      return tasks.FirstOrDefault(t => t.Type == type);
    }

    public void Dispose()
    {
      tasks.Clear();
    }
  }
}