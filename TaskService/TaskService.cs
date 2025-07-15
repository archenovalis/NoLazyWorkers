using ScheduleOne.Employees;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using Unity.Collections;
using ScheduleOne.Property;
using Unity.Burst;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.TaskService.Extensions;
using System.Collections;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.TaskService.StationTasks;
using NoLazyWorkers.Extensions;
using NoLazyWorkers.Performance;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.TaskService.TaskService;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using static NoLazyWorkers.Extensions.PoolUtility;

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

        public TaskBuilder(TaskName taskType)
        {
            _taskType = taskType;
            _setupDelegate = (start, count, inputs, outputs) => { };
            _selectEntitiesDelegate = (outputs) => { };
            _validationSetupDelegate = (start, count, inputs, outputs) => { };
            _validateEntityDelegate = (index, keys, outputs) => { };
            _createTaskDelegate = (index, results, outputs) => { };
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithSetup(Action<int, int, int[], List<TSetupOutput>> setupDelegate)
        {
            _setupDelegate = setupDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithSelectEntities(Action<NativeList<Guid>> selectEntitiesDelegate)
        {
            _selectEntitiesDelegate = selectEntitiesDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithValidationSetup(Action<int, int, Guid[], List<TValidationSetupOutput>> validationSetupDelegate)
        {
            _validationSetupDelegate = validationSetupDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithValidateEntity(Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> validateEntityDelegate)
        {
            _validateEntityDelegate = validateEntityDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithCreateTask(Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> createTaskDelegate)
        {
            _createTaskDelegate = createTaskDelegate;
            return this;
        }

        public Action<int, int, int[], List<TSetupOutput>> SetupDelegate => _setupDelegate;
        public Action<NativeList<Guid>> SelectEntitiesDelegate => _selectEntitiesDelegate;
        public Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate => _validationSetupDelegate;
        public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate => _validateEntityDelegate;
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
        public struct Empty : IDisposable
        {
            public void Dispose() { }
        }

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

            public static TaskDescriptor Create(
                Guid entityGuid, TaskName type, int actionId, TaskEmployeeType employeeType, int priority,
                string propertyName, ItemData item, int quantity,
                Guid pickupGuid, int[] pickupSlotIndices,
                Guid dropoffGuid, int[] dropoffSlotIndices,
                float creationTime,
                NativeList<LogEntry> logs)
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
                logs.Add(new LogEntry
                {
                    Message = $"Created task {descriptor.TaskId} for entity {entityGuid}, type {type}, action {actionId}",
                    Level = Level.Verbose,
                    Category = Category.Tasks
                });
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
        public struct ValidationResultData
        {
            public Guid EntityGuid;
            public bool IsValid;
            public int State;
            public ItemData Item;
            public int Quantity;
            public int DestinationCapacity;
        }

        [BurstCompile]
        public struct TaskResult
        {
            public TaskDescriptor Task;
            public bool Success;
            public FixedString128Bytes FailureReason;
            public TaskResult(TaskDescriptor task, bool success, FixedString128Bytes failureReason = default)
            {
                Task = task;
                Success = success;
                FailureReason = success ? default : failureReason.IsEmpty ? "Unknown failure" : failureReason;
            }
        }

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
        EntityType[] SupportedEntityTypes { get; } // Updated to TaskEntityType
        bool IsValidState(int state);
        IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
        IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
    }

    public interface ITask<TSetupOutput, TValidationSetupOutput> : ITask
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        Action<int, int, int[], List<TSetupOutput>> SetupDelegate { get; }
        Action<int, NativeArray<Guid>, NativeList<ValidationResultData>, NativeList<LogEntry>> ValidateEntityDelegate { get; }
        Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>, NativeList<LogEntry>> CreateTaskDelegate { get; }
        void ConfigureProcessor<TProcessorSetupOutput, TProcessorValidationSetupOutput>(
            ref TaskProcessor<TProcessorSetupOutput, TProcessorValidationSetupOutput> processor)
            where TProcessorSetupOutput : struct, IDisposable
            where TProcessorValidationSetupOutput : struct, IDisposable;
    }

    public abstract class BaseTask<TSetupOutput, TValidationSetupOutput> : ITask<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        public abstract TaskName Type { get; }
        public virtual EntityType[] SupportedEntityTypes => new[] { EntityType.MixingStation }; // Updated
        public abstract bool IsValidState(int state);
        public abstract IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
        public abstract IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
        public abstract Action<int, int, int[], List<TSetupOutput>> SetupDelegate { get; }
        public abstract Action<NativeList<Guid>, NativeList<LogEntry>> SelectEntitiesDelegate { get; }
        public abstract Action<int, int, Guid[], List<TValidationSetupOutput>, NativeList<LogEntry>> ValidationSetupDelegate { get; }
        public abstract Action<int, NativeArray<Guid>, NativeList<ValidationResultData>, NativeList<LogEntry>> ValidateEntityDelegate { get; }
        public abstract Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>, NativeList<LogEntry>> CreateTaskDelegate { get; }

        public virtual void ConfigureProcessor<TProcessorSetupOutput, TProcessorValidationSetupOutput>(
            ref TaskProcessor<TProcessorSetupOutput, TProcessorValidationSetupOutput> processor)
            where TProcessorSetupOutput : struct, IDisposable
            where TProcessorValidationSetupOutput : struct, IDisposable
        {
            if (typeof(TProcessorSetupOutput) == typeof(TSetupOutput) && typeof(TProcessorValidationSetupOutput) == typeof(TValidationSetupOutput))
            {
                processor.SetupDelegate = SetupDelegate as Action<int, int, int[], List<TProcessorSetupOutput>>;
                processor.SelectEntitiesDelegate = SelectEntitiesDelegate;
                processor.ValidationSetupDelegate = ValidationSetupDelegate as Action<int, int, Guid[], List<TProcessorValidationSetupOutput>>;
                processor.ValidateEntityDelegate = ValidateEntityDelegate;
                processor.CreateTaskDelegate = CreateTaskDelegate;
                processor.DisableEntityDelegate = new DisableEntitiesBurst<TSetupOutput, TValidationSetupOutput>(this).Execute;
            }
            else
            {
                Log(Level.Error, $"Type mismatch in ConfigureProcessor: expected {typeof(TSetupOutput)}, {typeof(TValidationSetupOutput)}", Category.Tasks);
            }
        }
    }

    public class TaskProcessor<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        public ITask Task;
        public Action<int, int, int[], List<TSetupOutput>> SetupDelegate;
        public Action<NativeList<Guid>, NativeList<LogEntry>> SelectEntitiesDelegate;
        public Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate;
        public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>, NativeList<LogEntry>> ValidateEntityDelegate;
        public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>, NativeList<LogEntry>> CreateTaskDelegate;
        public Action<int, NativeArray<ValidationResultData>, NativeList<DisabledEntityData>, NativeList<LogEntry>> DisableEntityDelegate;

        public TaskProcessor(ITask task)
        {
            Task = task;
            SetupDelegate = null;
            SelectEntitiesDelegate = null;
            ValidationSetupDelegate = null;
            ValidateEntityDelegate = null;
            CreateTaskDelegate = null;
            DisableEntityDelegate = null;
            if (task is ITask<TSetupOutput, TValidationSetupOutput> genericTask)
            {
                var _this = this;
                genericTask.ConfigureProcessor(ref _this);
                ValidateDelegates(task.Type);
            }
            else
            {
                Log(Level.Error, $"Task {task.Type} must implement ITask<{typeof(TSetupOutput)}, {typeof(TValidationSetupOutput)}>", Category.Tasks);
            }
        }

        private void ValidateDelegates(TaskName taskType)
        {
#if DEBUG
            if (SetupDelegate == null) Log(Level.Warning, $"SetupDelegate is not set for task {taskType}", Category.Tasks);
            if (SelectEntitiesDelegate == null) Log(Level.Warning, $"SelectEntitiesDelegate is not set for task {taskType}", Category.Tasks);
            if (ValidateEntityDelegate == null) Log(Level.Warning, $"ValidateEntityDelegate is not set for task {taskType}", Category.Tasks);
            if (CreateTaskDelegate == null) Log(Level.Warning, $"CreateTaskDelegate is not set for task {taskType}", Category.Tasks);
#endif
        }
    }

    public static class TaskUtilities
    {
        public static void SelectEntitiesByType<T>(Property property, NativeList<Guid> output)
            where T : class
        {
            if (typeof(T) == typeof(IEmployeeAdapter) && ManagedDictionaries.IEmployees.TryGetValue(property, out var employees))
            {
                foreach (var kvp in employees) output.Add(kvp.Key);
            }
            else if (typeof(T) == typeof(IStationAdapter) && ManagedDictionaries.IStations.TryGetValue(property, out var stations))
            {
                foreach (var kvp in stations) output.Add(kvp.Key);
            }
            else if (typeof(T) == typeof(PlaceableStorageEntity) && ManagedDictionaries.Storages.TryGetValue(property, out var storages))
            {
                foreach (var kvp in storages) output.Add(kvp.Key);
            }
        }
    }

    public class TaskQueue
    {
        private readonly Dictionary<TaskEmployeeType, NativeList<TaskDescriptor>> _highPriorityTasks;
        private readonly Dictionary<TaskEmployeeType, NativeList<TaskDescriptor>> _normalPriorityTasks;
        private readonly NativeList<TaskDescriptor> _anyEmployeeTasks;
        private NativeParallelHashMap<Guid, TaskDescriptor> _activeTasks;
        public NativeParallelHashMap<Guid, bool> _activeTasksByType; // Updated

        public TaskQueue(int capacity)
        {
            _highPriorityTasks = new Dictionary<TaskEmployeeType, NativeList<TaskDescriptor>>();
            _normalPriorityTasks = new Dictionary<TaskEmployeeType, NativeList<TaskDescriptor>>();
            foreach (TaskEmployeeType type in Enum.GetValues(typeof(TaskEmployeeType)))
            {
                _highPriorityTasks[type] = new NativeList<TaskDescriptor>(capacity, Allocator.Persistent);
                _normalPriorityTasks[type] = new NativeList<TaskDescriptor>(capacity, Allocator.Persistent);
            }
            _anyEmployeeTasks = new NativeList<TaskDescriptor>(capacity, Allocator.Persistent);
            _activeTasks = new NativeParallelHashMap<Guid, TaskDescriptor>(capacity, Allocator.Persistent);
            _activeTasksByType = new NativeParallelHashMap<Guid, bool>(capacity, Allocator.Persistent);
#if DEBUG
            Log(Level.Info, $"Initialized TaskQueue with capacity {capacity}", Category.Tasks);
#endif
        }

        public void Enqueue(TaskDescriptor task)
        {
            var key = task.EntityGuid;
            if (_activeTasksByType.ContainsKey(key))
            {
#if DEBUG
                Log(Level.Verbose, $"Task of type {task.Type} already exists for entity {task.EntityGuid}, skipping", Category.Tasks);
#endif
                return;
            }
            var taskList = task.EmployeeType == TaskEmployeeType.Any
                ? _anyEmployeeTasks
                : task.Priority >= 100
                    ? _highPriorityTasks[task.EmployeeType]
                    : _normalPriorityTasks[task.EmployeeType];
            taskList.Add(task);
            _activeTasksByType[key] = true;
            _activeTasks[task.TaskId] = task;
#if DEBUG
            Log(Level.Info, $"Enqueued task {task.TaskId} for {task.EmployeeType}, priority {task.Priority}", Category.Tasks);
#endif
        }

        public bool SelectTask(TaskEmployeeType employeeType, Guid employeeGuid, out TaskDescriptor task)
        {
            task = default;
            var highPriorityList = _highPriorityTasks[employeeType];
            if (highPriorityList.Length > 0)
            {
                task = highPriorityList[0];
                highPriorityList.RemoveAt(0);
#if DEBUG
                Log(Level.Info, $"Selected high-priority task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
#endif
                return true;
            }
            var normalPriorityList = _normalPriorityTasks[employeeType];
            if (normalPriorityList.Length > 0)
            {
                task = normalPriorityList[0];
                normalPriorityList.RemoveAt(0);
#if DEBUG
                Log(Level.Info, $"Selected normal-priority task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
#endif
                return true;
            }
            if (_anyEmployeeTasks.Length > 0)
            {
                task = _anyEmployeeTasks[0];
                _anyEmployeeTasks.RemoveAt(0);
#if DEBUG
                Log(Level.Info, $"Selected any-employee task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
#endif
                return true;
            }
            return false;
        }

        public void CompleteTask(Guid taskId)
        {
            if (_activeTasks.TryGetValue(taskId, out var task))
            {
                var key = task.EntityGuid;
                _activeTasksByType.Remove(key);
                _activeTasks.Remove(taskId);
                task.Dispose();
#if DEBUG
                Log(Level.Info, $"Completed task {taskId}", Category.Tasks);
#endif
            }
        }

        public void Dispose()
        {
            foreach (var list in _highPriorityTasks.Values.Concat(_normalPriorityTasks.Values).Append(_anyEmployeeTasks))
            {
                for (int i = 0; i < list.Length; i++)
                {
#if DEBUG
                    Log(Level.Verbose, $"Disposing task {list[i].TaskId}", Category.Tasks);
#endif
                    list[i].Dispose();
                }
                list.Dispose();
            }
            if (_activeTasks.IsCreated) _activeTasks.Dispose();
            if (_activeTasksByType.IsCreated) _activeTasksByType.Dispose();
#if DEBUG
            Log(Level.Info, "Disposed TaskQueue", Category.Tasks);
#endif
        }
    }

    /// <summary>
    /// Manages task creation, queuing, and execution for a property.
    /// </summary>
    public class TaskService
    {
        private readonly Property _property;
        private readonly CacheService _cacheService;
        private readonly TaskRegistry _taskRegistry;
        private readonly TaskQueue _taskQueue;
        private readonly EntityDisableService _disableService;
        private readonly EntityStateService _entityStateService;
        private readonly NativeList<Guid> _entityGuids;
        private NativeArray<ValidationResultData> _validationResults;
        private readonly NativeListPool<LogEntry> _logPool;
        private readonly NativeListPool<TaskResult> _taskResultPool;
        private readonly NativeListPool<ValidationResultData> _validationResultPool;
        private readonly NativeParallelHashMap<Guid, NativeList<TaskDescriptor>> _employeeSpecificTasks;
        private bool _isProcessing;
        private Coroutine _validationCoroutine;
        private DisposableScope scope;

        public TaskService(Property property)
        {
            scope = new DisposableScope(this);
            _property = property ?? throw new ArgumentNullException(nameof(property));
            _taskRegistry = TaskServiceManager.GetRegistry(property);
            _taskQueue = new TaskQueue(100);
            _cacheService = CacheService.GetOrCreateCacheService(property);
            _disableService = EntityDisableService.GetOrCreateService(property);
            _entityStateService = EntityStateService.GetOrCreateService(property);
            _entityGuids = new NativeList<Guid>(Allocator.Persistent);
            _validationResults = new NativeArray<ValidationResultData>(0, Allocator.Persistent);
            _logPool = PoolUtility.InitializeNativeListPool<LogEntry>(() => new NativeList<LogEntry>(100, Allocator.TempJob), 10, "TaskService_LogPool");
            _taskResultPool = PoolUtility.InitializeNativeListPool<TaskResult>(() => new NativeList<TaskResult>(100, Allocator.TempJob), 10, "TaskService_TaskResultPool");
            _validationResultPool = PoolUtility.InitializeNativeListPool<ValidationResultData>(() => new NativeList<ValidationResultData>(100, Allocator.TempJob), 10, "TaskService_ValidationResultPool");
            _employeeSpecificTasks = new NativeParallelHashMap<Guid, NativeList<TaskDescriptor>>(100, Allocator.Persistent);
            Log(Level.Info, $"Initialized TaskService for property {property.name}", Category.Tasks);
            _validationCoroutine = CoroutineRunner.Instance.RunCoroutine(ProcessTasksCoroutine());
        }

        internal struct DisposableScope : IDisposable
        {
            private TaskService _taskService;
            private NativeList<NativeList<LogEntry>> _logDisposables;
            private NativeList<NativeList<TaskResult>> _taskResultDisposables;
            private NativeList<NativeList<ValidationResultData>> _validationResultDisposables;
            private NativeList<NativeList<DisabledEntityData>> _disabledEntityDisposables;
            private NativeList<NativeArray<int>> _intArrayDisposables;
            private NativeList<NativeArray<ValidationResultData>> _validationArrayDisposables;
            private NativeList<NativeList<Guid>> _guidListDisposables;

            public DisposableScope(TaskService taskService, int initialCapacity = 10)
            {
                _taskService = taskService;
                _logDisposables = new NativeList<NativeList<LogEntry>>(initialCapacity, Allocator.TempJob);
                _taskResultDisposables = new NativeList<NativeList<TaskResult>>(initialCapacity, Allocator.TempJob);
                _validationResultDisposables = new NativeList<NativeList<ValidationResultData>>(initialCapacity, Allocator.TempJob);
                _disabledEntityDisposables = new NativeList<NativeList<DisabledEntityData>>(initialCapacity, Allocator.TempJob);
                _intArrayDisposables = new NativeList<NativeArray<int>>(initialCapacity, Allocator.TempJob);
                _validationArrayDisposables = new NativeList<NativeArray<ValidationResultData>>(initialCapacity, Allocator.TempJob);
                _guidListDisposables = new NativeList<NativeList<Guid>>(initialCapacity, Allocator.TempJob);
            }

            public void Add(NativeList<LogEntry> disposable) => _logDisposables.Add(disposable);
            public void Add(NativeList<TaskResult> disposable) => _taskResultDisposables.Add(disposable);
            public void Add(NativeList<ValidationResultData> disposable) => _validationResultDisposables.Add(disposable);
            public void Add(NativeList<DisabledEntityData> disposable) => _disabledEntityDisposables.Add(disposable);
            public void Add(NativeArray<int> disposable) => _intArrayDisposables.Add(disposable);
            public void Add(NativeArray<ValidationResultData> disposable) => _validationArrayDisposables.Add(disposable);
            public void Add(NativeList<Guid> disposable) => _guidListDisposables.Add(disposable);

            public void Dispose()
            {
                foreach (var disposable in _logDisposables) if (disposable.IsCreated) _taskService._logPool.Return(disposable);
                foreach (var disposable in _taskResultDisposables) if (disposable.IsCreated) _taskService._taskResultPool.Return(disposable);
                foreach (var disposable in _validationResultDisposables) if (disposable.IsCreated) _taskService._validationResultPool.Return(disposable);
                foreach (var disposable in _disabledEntityDisposables) if (disposable.IsCreated) disposable.Dispose();
                foreach (var disposable in _intArrayDisposables) if (disposable.IsCreated) disposable.Dispose();
                foreach (var disposable in _validationArrayDisposables) if (disposable.IsCreated) disposable.Dispose();
                foreach (var disposable in _guidListDisposables) if (disposable.IsCreated) disposable.Dispose();
                if (_logDisposables.IsCreated) _logDisposables.Dispose();
                if (_taskResultDisposables.IsCreated) _taskResultDisposables.Dispose();
                if (_validationResultDisposables.IsCreated) _validationResultDisposables.Dispose();
                if (_disabledEntityDisposables.IsCreated) _disabledEntityDisposables.Dispose();
                if (_intArrayDisposables.IsCreated) _intArrayDisposables.Dispose();
                if (_validationArrayDisposables.IsCreated) _validationArrayDisposables.Dispose();
                if (_guidListDisposables.IsCreated) _guidListDisposables.Dispose();
            }
        }

        public NativeListPool<TaskResult> GetTaskResultPool()
        {
#if DEBUG
            Log(Level.Verbose, "Retrieved TaskResultPool", Category.Pooling);
#endif
            return _taskResultPool;
        }

        public NativeListPool<ValidationResultData> GetValidationResultPool()
        {
#if DEBUG
            Log(Level.Verbose, "Retrieved ValidationResultPool", Category.Pooling);
#endif
            return _validationResultPool;
        }

        [BurstCompile]
        private struct FilterNonDisabledEntitiesJob
        {
            [ReadOnly] public NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> OwnerToType;
            [ReadOnly] public NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntities;
            [ReadOnly] public EntityType TargetType;
            public NativeList<Guid> Outputs;
            [ReadOnly] public Property Property;
            [ReadOnly] public NativeListPool<LogEntry> LogPool;

            public void Execute(int index, NativeArray<Guid> inputs, NativeList<Guid> outputs, NativeList<LogEntry> logs)
            {
                var guid = inputs[index];
                if (!DisabledEntities.ContainsKey(guid))
                {
                    if (OwnerToType.TryGetValue(guid, out var typeInfo))
                    {
                        if (typeInfo.EntityType == TargetType)
                            Outputs.Add(guid);
                    }
                }
#if DEBUG
                logs.Add(new LogEntry
                {
                    Message = $"Filtered entity {guid} for type {TargetType}, included: {Outputs.Contains(guid)}",
                    Level = Level.Verbose,
                    Category = Category.Tasks
                });
#endif
            }
        }

        public IEnumerator GetNonDisabledEntities(EntityType entityType, NativeList<Guid> outputs)
        {
            var logs = _logPool.Get();
            var inputs = new NativeList<Guid>(Allocator.TempJob);
            foreach (var kvp in _cacheService._guidToType)
                inputs.Add(kvp.Key);
            var job = new FilterNonDisabledEntitiesJob
            {
                OwnerToType = _cacheService._guidToType,
                DisabledEntities = _disableService._disabledEntities,
                TargetType = entityType,
                Outputs = outputs,
                Property = _property,
                LogPool = _logPool
            };
            yield return SmartExecution.ExecuteBurstFor<Guid, Guid>(
                uniqueId: $"GetNonDisabledEntities_{entityType}",
                itemCount: inputs.Length,
                burstForDelegate: job.Execute,
                inputs: inputs.AsArray(),
                outputs: outputs
            );
            yield return ProcessLogs(logs);
            inputs.Dispose();
            _logPool.Return(logs);
            Log(Level.Info, $"Retrieved {outputs.Length} non-disabled entities for type {entityType}", Category.Tasks);
        }

        [SmartExecute]
        [BurstCompile]
        public struct SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
        {
            public Action<NativeList<Guid>, NativeList<LogEntry>> SelectEntitiesDelegate;
            public void Execute(int input, NativeList<Guid> outputs, NativeList<LogEntry> logs)
            {
                SelectEntitiesDelegate(outputs, logs);
#if DEBUG
                logs.Add(new LogEntry
                {
                    Message = $"Selected {outputs.Length} entities",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
#endif
            }
        }

        [SmartExecute]
        [BurstCompile]
        public struct ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
            where TSetupOutput : struct, IDisposable
            where TValidationSetupOutput : struct, IDisposable
        {
            public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>, NativeList<LogEntry>> ValidateEntityDelegate;
            public void ExecuteFor(int index, NativeArray<Guid> inputs, NativeList<ValidationResultData> outputs, NativeList<LogEntry> logs)
            {
                ValidateEntityDelegate(index, inputs, outputs, logs);
#if DEBUG
                logs.Add(new LogEntry
                {
                    Message = $"Validated {outputs.Length} entities",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
#endif
            }
        }

        [SmartExecute]
        [BurstCompile]
        public struct CreateTasksBurst<TSetupOutput, TValidationSetupOutput>
            where TSetupOutput : struct, IDisposable
            where TValidationSetupOutput : struct, IDisposable
        {
            public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>, NativeList<LogEntry>> CreateTaskDelegate;
            public void ExecuteFor(int index, NativeArray<ValidationResultData> inputs, NativeList<TaskResult> outputs, NativeList<LogEntry> logs)
            {
                CreateTaskDelegate(index, inputs, outputs, logs);
#if DEBUG
                logs.Add(new LogEntry
                {
                    Message = $"Created {outputs.Length} tasks",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
#endif
            }
        }

        /// <summary>
        /// Processes tasks periodically for all entities.
        /// </summary>
        private IEnumerator ProcessTasksCoroutine()
        {
            while (true)
            {
                if (!_isProcessing)
                {
                    _isProcessing = true;
                    var logs = _logPool.Get();
                    scope.Add(logs);
                    var taskResults = _taskResultPool.Get();
                    scope.Add(taskResults);
                    try
                    {
                        foreach (var task in _taskRegistry.AllTasks)
                        {
#if DEBUG
                            Log(Level.Verbose, $"Processing tasks for type {task.Type}", Category.Tasks);
#endif
                            var selectedEntities = new NativeList<Guid>(Allocator.TempJob);
                            scope.Add(selectedEntities);
                            foreach (var entityType in task.SupportedEntityTypes)
                            {
                                yield return GetNonDisabledEntities(entityType, selectedEntities);
                            }
                            if (selectedEntities.Length == 0)
                            {
#if DEBUG
                                Log(Level.Verbose, $"No non-disabled entities found for task type {task.Type}", Category.Tasks);
#endif
                                continue;
                            }
                            yield return CreateTaskGeneric<Empty, Empty>(Guid.Empty, Guid.Empty, _property, task, logs, taskResults, selectedEntities, _validationResultPool.Get());
                        }
                        yield return ProcessLogs(logs);
                    }
                    finally
                    {
                        scope.Dispose();
                    }
                    _isProcessing = false;
                }
                yield return null;
            }
        }

        public IEnumerator TryGetTaskAsync(Employee employee)
        {
            if (employee.AssignedProperty != _property)
            {
                Log(Level.Error, $"Employee {employee.GUID} not assigned to property {_property.name}", Category.Tasks);
                yield return new ValueTask<(bool, TaskDescriptor, ITask)>((false, default, null));
                yield break;
            }
            if (_taskQueue.SelectTask(Enum.Parse<TaskEmployeeType>(employee.Type.ToString()), employee.GUID, out var task))
            {
                var taskImpl = _taskRegistry.GetTask(task.Type);
#if DEBUG
                Log(Level.Info, $"Selected task {task.TaskId} for employee {employee.GUID}", Category.Tasks);
#endif
                yield return new ValueTask<(bool, TaskDescriptor, ITask)>((true, task, taskImpl));
            }
            else
            {
#if DEBUG
                Log(Level.Verbose, $"No tasks available for employee {employee.GUID}", Category.Tasks);
#endif
                yield return new ValueTask<(bool, TaskDescriptor, ITask)>((false, default, null));
            }
        }

        public IEnumerator CreateTaskAsync(Property property, TaskName? taskType = null, Guid? employeeGuid = null, Guid? entityGuid = null)
        {
            var logs = _logPool.Get();
            scope.Add(logs);
            var taskResults = _taskResultPool.Get();
            scope.Add(taskResults);
            var validationResults = _validationResultPool.Get();
            scope.Add(validationResults);
            try
            {
                IEnumerable<ITask> tasks = taskType.HasValue ? new[] { _taskRegistry.GetTask(taskType.Value) } : _taskRegistry.AllTasks;
                foreach (var task in tasks)
                {
                    if (task == null)
                    {
                        Log(Level.Error, $"Task type {taskType} not registered", Category.Tasks);
                        continue;
                    }
#if DEBUG
                    Log(Level.Verbose, $"Processing tasks for type {task.Type}", Category.Tasks);
#endif
                    var selectedEntities = new NativeList<Guid>(Allocator.TempJob);
                    scope.Add(selectedEntities);
                    if (employeeGuid.HasValue && entityGuid.HasValue)
                    {
                        selectedEntities.Add(entityGuid.Value);
                    }
                    else
                    {
                        foreach (var entityType in task.SupportedEntityTypes)
                        {
                            yield return GetNonDisabledEntities(entityType, selectedEntities);
                        }
                    }
                    if (selectedEntities.Length == 0)
                    {
#if DEBUG
                        Log(Level.Verbose, $"No non-disabled entities found for task type {task.Type}", Category.Tasks);
#endif
                        continue;
                    }
                    yield return CreateTaskGeneric<Empty, Empty>(employeeGuid ?? Guid.Empty, entityGuid ?? Guid.Empty, property, task, logs, taskResults, selectedEntities, validationResults);
                }
                yield return ProcessLogs(logs);
            }
            finally
            {
                scope.Dispose();
            }
        }

        private IEnumerator CreateTaskGeneric<TSetupOutput, TValidationSetupOutput>(
            Guid employeeGuid, Guid entityGuid, Property property, ITask task,
            NativeList<LogEntry> logs, NativeList<TaskResult> taskResults, NativeList<Guid> entityGuids,
            NativeList<ValidationResultData> validationResults)
            where TSetupOutput : unmanaged, IDisposable
            where TValidationSetupOutput : unmanaged, IDisposable
        {
            if (!(task is ITask<TSetupOutput, TValidationSetupOutput> genericTask))
            {
                Log(Level.Error, $"Task {task.Type} does not support generic type {typeof(TSetupOutput)}", Category.Tasks);
                yield break;
            }
            var processor = new TaskProcessor<TSetupOutput, TValidationSetupOutput>(task);
            var setupOutputs = new List<TSetupOutput>();
            var setupInputs = new[] { 0 };
#if DEBUG
            Log(Level.Info, $"Executing setup for task {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
#endif
            yield return SmartExecution.Execute<int, TSetupOutput>(
                uniqueId: $"{property.name}_{task.Type}_Setup",
                itemCount: 1,
                nonBurstDelegate: processor.SetupDelegate,
                inputs: setupInputs,
                outputs: setupOutputs
            );
            var selectJob = new SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
            {
                SelectEntitiesDelegate = processor.SelectEntitiesDelegate
            };
            yield return SmartExecution.ExecuteBurst<int, Guid>(
                uniqueId: $"{property.name}_{task.Type}_SelectEntities",
                burstDelegate: selectJob.Execute,
                outputs: entityGuids
            );
            var validationSetupOutputs = new List<TValidationSetupOutput>();
            var validationSetupInputs = entityGuids.ToArray();
            if (processor.ValidationSetupDelegate != null && entityGuids.Length > 0)
            {
#if DEBUG
                Log(Level.Info, $"Executing validation setup for task {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
#endif
                yield return SmartExecution.Execute<Guid, TValidationSetupOutput>(
                    uniqueId: $"{property.name}_{task.Type}_ValidationSetup",
                    itemCount: entityGuids.Length,
                    nonBurstDelegate: processor.ValidationSetupDelegate,
                    inputs: validationSetupInputs,
                    outputs: validationSetupOutputs
                );
            }
            var results = new NativeArray<ValidationResultData>(entityGuids.Length, Allocator.TempJob);
            scope.Add(results);
            var validateJob = new ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
            {
                ValidateEntityDelegate = processor.ValidateEntityDelegate
            };
            yield return SmartExecution.ExecuteBurstFor<Guid, ValidationResultData>(
                uniqueId: $"{property.name}_{task.Type}_ValidateEntities",
                itemCount: entityGuids.Length,
                burstForDelegate: validateJob.ExecuteFor,
                inputs: CreateTaskEntityKeys(entityGuids, task.Type),
                outputs: validationResults
            );
            var createJob = new CreateTasksBurst<TSetupOutput, TValidationSetupOutput>
            {
                CreateTaskDelegate = processor.CreateTaskDelegate
            };
            yield return SmartExecution.ExecuteBurstFor<ValidationResultData, TaskResult>(
                uniqueId: $"{property.name}_{task.Type}_CreateTasks",
                itemCount: validationResults.Length,
                burstForDelegate: createJob.ExecuteFor,
                inputs: validationResults.AsArray(),
                outputs: taskResults,
                nonBurstResultsDelegate: outputs =>
                {
#if DEBUG
                    Log(Level.Info, $"Enqueuing {outputs.Count} tasks for type {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
#endif
                    for (int i = 0; i < outputs.Count; i++)
                    {
#if DEBUG
                        Log(Level.Info, $"Enqueuing task {outputs[i].Task.TaskId}", Category.Tasks);
#endif
                        if (employeeGuid != Guid.Empty)
                        {
                            if (!_employeeSpecificTasks.ContainsKey(employeeGuid))
                                _employeeSpecificTasks.Add(employeeGuid, new NativeList<TaskDescriptor>(Allocator.Persistent));
                            _employeeSpecificTasks[employeeGuid].Add(outputs[i].Task);
                        }
                        _taskQueue.Enqueue(outputs[i].Task);
                    }
                }
            );
        }

        private NativeArray<Guid> CreateTaskEntityKeys(NativeList<Guid> guids, TaskName taskType)
        {
            var keys = new NativeArray<Guid>(guids.Length, Allocator.TempJob);
            for (int i = 0; i < guids.Length; i++)
            {
                var guid = guids[i];
                keys[i] = guid;
            }
            return keys;
        }

        public void CompleteTask(TaskDescriptor task)
        {
            _taskQueue.CompleteTask(task.TaskId);
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
                Log(Level.Info, $"Retrieved employee-specific task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
                return task;
            }
            return default;
        }

        public void Dispose()
        {
            if (_validationCoroutine != null)
                CoroutineRunner.Instance.StopCoroutine(_validationCoroutine);
            if (_entityGuids.IsCreated) _entityGuids.Dispose();
            if (_validationResults.IsCreated) _validationResults.Dispose();
            foreach (var tasks in _employeeSpecificTasks.GetValueArray(Allocator.Temp))
                if (tasks.IsCreated) tasks.Dispose();
            if (_employeeSpecificTasks.IsCreated) _employeeSpecificTasks.Dispose();
            _taskQueue.Dispose();
            _disableService.Dispose();
            scope.Dispose();
            PoolUtility.DisposeNativeListPool(_taskResultPool, "TaskService_TaskResultPool");
            PoolUtility.DisposeNativeListPool(_validationResultPool, "TaskService_ValidationResultPool");
            Log(Level.Info, $"Disposed TaskService for property {_property.name}", Category.Tasks);
        }
    }

    /// <summary>
    /// Manages task services and registries for properties.
    /// </summary>
    public static class TaskServiceManager
    {
        private static readonly Dictionary<Property, TaskService> _services = new();
        private static readonly Dictionary<Property, TaskRegistry> _registries = new();

        /// <summary>
        /// Gets or creates a task service for the specified property.
        /// </summary>
        /// <param name="property">The property to get or create a service for.</param>
        /// <returns>The task service for the property.</returns>
        public static TaskService GetOrCreateService(Property property)
        {
            if (!_services.TryGetValue(property, out var service))
            {
                _registries[property] = new TaskRegistry();
                _registries[property].Initialize();
                service = new TaskService(property);
                _services[property] = service;
                Log(Level.Info, $"Created TaskService for property {property.name}", Category.Tasks);
            }
            return service;
        }

        /// <summary>
        /// Gets the task registry for the specified property.
        /// </summary>
        /// <param name="property">The property to get the registry for.</param>
        /// <returns>The task registry for the property.</returns>
        public static TaskRegistry GetRegistry(Property property)
        {
            if (!_registries.ContainsKey(property))
            {
                _registries[property] = new TaskRegistry();
                Log(Level.Info, $"Created TaskRegistry for property {property.name}", Category.Tasks);
            }
            return _registries[property];
        }

        /// <summary>
        /// Disposes of all services and registries.
        /// </summary>
        public static void Cleanup()
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

    /// <summary>
    /// Registers and manages tasks.
    /// </summary>
    public partial class TaskRegistry
    {
        private readonly List<ITask> tasks = new();
        public IEnumerable<ITask> AllTasks => tasks;

        /// <summary>
        /// Initializes the task registry.
        /// </summary>
        public void Initialize()
        {
            // This method will be extended by the generated code
        }

        /// <summary>
        /// Registers a task.
        /// </summary>
        /// <param name="task">The task to register.</param>
        public void Register(ITask task)
        {
            if (!tasks.Contains(task))
            {
                tasks.Add(task);
                Log(Level.Info, $"Registered task: {task.Type}", Category.Tasks);
            }
        }

        /// <summary>
        /// Registers an external task type.
        /// </summary>
        /// <param name="taskType">The type of the task.</param>
        /// <param name="taskTypeEnum">The task name enum value.</param>
        public void RegisterExternal(Type taskType, TaskName taskTypeEnum)
        {
            if (!typeof(ITask).IsAssignableFrom(taskType))
            {
                Log(Level.Error, $"Type {taskType.Name} does not implement ITask", Category.Tasks);
                return;
            }
            try
            {
                var task = Activator.CreateInstance(taskType) as ITask;
                if (task != null && task.Type == taskTypeEnum)
                {
                    Register(task);
                    Log(Level.Info, $"Registered external task type {taskTypeEnum} ({taskType.FullName})", Category.Tasks);
                }
                else
                {
                    Log(Level.Error, $"Failed to register external task {taskType.Name}: Task.Type ({task?.Type}) does not match expected {taskTypeEnum}", Category.Tasks);
                }
            }
            catch (Exception ex)
            {
                Log(Level.Error, $"Error registering external task {taskType.Name}: {ex.Message}", Category.Tasks);
            }
        }

        /// <summary>
        /// Gets a task by its type.
        /// </summary>
        /// <param name="type">The type of the task.</param>
        /// <returns>The task, or null if not found.</returns>
        public ITask GetTask(TaskName type)
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

    /// <summary>
    /// Generates source code for task registration.
    /// </summary>
    [Generator]
    public class TaskTypeSourceGenerator : ISourceGenerator
    {
        /// <summary>
        /// Initializes the source generator.
        /// </summary>
        /// <param name="context">The generator initialization context.</param>
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new TaskTypeSyntaxReceiver());
        }

        /// <summary>
        /// Generates source code for task registration based on syntax analysis.
        /// </summary>
        /// <param name="context">The generator execution context.</param>
        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is TaskTypeSyntaxReceiver receiver))
                return;
            var compilation = context.Compilation;
            var taskTypeAttributeSymbol = compilation.GetTypeByMetadataName("NoLazyWorkers.TaskService.EntityTaskAttribute");
            if (taskTypeAttributeSymbol == null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "NLW001", "Missing EntityTaskAttribute",
                        "Could not find EntityTaskAttribute in compilation", "TaskRegistration", DiagnosticSeverity.Error, true),
                    null));
                return;
            }
            var taskRegistrations = new StringBuilder();
            taskRegistrations.AppendLine("using System;");
            taskRegistrations.AppendLine("using UnityEngine;");
            taskRegistrations.AppendLine("using NoLazyWorkers.TaskService;");
            taskRegistrations.AppendLine("using NoLazyWorkers.TaskService.Extensions;");
            taskRegistrations.AppendLine();
            taskRegistrations.AppendLine("namespace NoLazyWorkers.TaskService");
            taskRegistrations.AppendLine("{");
            taskRegistrations.AppendLine("    public partial class TaskRegistry");
            taskRegistrations.AppendLine("    {");
            taskRegistrations.AppendLine("        public void Initialize()");
            taskRegistrations.AppendLine("        {");
            taskRegistrations.AppendLine("            // Auto-generated task registrations");
            taskRegistrations.AppendLine("            Log(Level.Info, \"Initializing TaskRegistry with auto-registered tasks\", Category.Tasks);");
            foreach (var classDeclaration in receiver.CandidateClasses)
            {
                var semanticModel = compilation.GetSemanticModel(classDeclaration.SyntaxTree);
                var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                if (classSymbol == null)
                    continue;
                var entityTaskAttribute = classSymbol.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, taskTypeAttributeSymbol));
                if (entityTaskAttribute == null)
                    continue;
                if (!classSymbol.AllInterfaces.Any(i => i.ToString().StartsWith("NoLazyWorkers.TaskService.ITask")))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "NLW002", "Invalid Task Implementation",
                            $"Class {classSymbol.Name} marked with EntityTaskAttribute must implement ITask or ITask<TSetupOutput, TValidationSetupOutput>", "TaskRegistration", DiagnosticSeverity.Error, true),
                        classDeclaration.GetLocation()));
                    continue;
                }
                var taskTypeProperty = classSymbol.GetMembers("Type")
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.Type.ToString() == "NoLazyWorkers.TaskService.Extensions.TaskName");
                if (taskTypeProperty == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "NLW003", "Missing ITask.Type Property",
                            $"Class {classSymbol.Name} marked with EntityTaskAttribute must implement ITask.Type property", "TaskRegistration", DiagnosticSeverity.Error, true),
                        classDeclaration.GetLocation()));
                    continue;
                }
                taskRegistrations.AppendLine($"                // Register task {classSymbol.Name}");
                taskRegistrations.AppendLine($"                Register(new {classSymbol.ToDisplayString()}());");
                taskRegistrations.AppendLine($"                Log(Level.Info, $\"Registered task type {classSymbol.Name} ({classSymbol.ToDisplayString()})\", Category.Tasks);");
            }
            taskRegistrations.AppendLine("        }");
            taskRegistrations.AppendLine("    }");
            taskRegistrations.AppendLine("}");
            context.AddSource("TaskRegistry.g.cs", taskRegistrations.ToString());
        }

        private class TaskTypeSyntaxReceiver : ISyntaxReceiver
        {
            public List<ClassDeclarationSyntax> CandidateClasses { get; } = new();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is ClassDeclarationSyntax classDeclaration)
                {
                    if (classDeclaration.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString().Contains("EntityTask"))))
                    {
                        CandidateClasses.Add(classDeclaration);
                    }
                }
            }
        }
    }
}