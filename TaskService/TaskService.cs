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
    /// Configures a task's processing steps using a fluent builder pattern.
    /// </summary>
    public class TaskBuilder<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        private readonly TaskName _taskType;
        private Action<int, int, int[], List<TSetupOutput>> _setupDelegate;
        private Action<NativeArray<int>, NativeList<Guid>> _selectEntitiesDelegate;
        private Action<int, int, Guid[], List<TValidationSetupOutput>> _validationSetupDelegate;
        private Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> _validateEntityDelegate;
        private Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> _createTaskDelegate;

        public TaskBuilder(TaskName taskType)
        {
            _taskType = taskType;
            _setupDelegate = (start, count, inputs, outputs) => { };
            _selectEntitiesDelegate = (inputs, outputs) => { };
            _validationSetupDelegate = (start, count, inputs, outputs) => { };
            _validateEntityDelegate = (index, guids, outputs) => { };
            _createTaskDelegate = (index, results, outputs) => { };
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithSetup(
            Action<int, int, int[], List<TSetupOutput>> setupDelegate)
        {
            _setupDelegate = setupDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithSelectEntities(
            Action<NativeArray<int>, NativeList<Guid>> selectEntitiesDelegate)
        {
            _selectEntitiesDelegate = selectEntitiesDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithValidationSetup(
            Action<int, int, Guid[], List<TValidationSetupOutput>> validationSetupDelegate)
        {
            _validationSetupDelegate = validationSetupDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithValidateEntity(
            Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> validateEntityDelegate)
        {
            _validateEntityDelegate = validateEntityDelegate;
            return this;
        }

        public TaskBuilder<TSetupOutput, TValidationSetupOutput> WithCreateTask(
            Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> createTaskDelegate)
        {
            _createTaskDelegate = createTaskDelegate;
            return this;
        }

        public Action<int, int, int[], List<TSetupOutput>> SetupDelegate => _setupDelegate;
        public Action<NativeArray<int>, NativeList<Guid>> SelectEntitiesDelegate => _selectEntitiesDelegate;
        public Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate => _validationSetupDelegate;
        public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate => _validateEntityDelegate;
        public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate => _createTaskDelegate;
    }

    /// <summary>
    /// Defines task-related enums and Job-compatible structs.
    /// </summary>
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

        public enum TaskEntityType
        {
            Employee,
            ChemistryStation,
            MixingStation,
            Pot,
            Botanist,
            Chemist
        }

        /// <summary>
        /// Empty struct for default generic type arguments.
        /// </summary>
        [BurstCompile]
        public struct Empty : IDisposable
        {
            public void Dispose() { }
        }

        /// <summary>
        /// Represents a task with its properties and resource management.
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
                logs.Add(new LogEntry
                {
                    Message = $"Created task {descriptor.TaskId} for entity {entityGuid}, type {type}, action {actionId}",
                    Level = Level.Verbose,
                    Category = Category.Tasks
                });
                return descriptor;
            }

            public void Dispose()
            {
                if (PickupSlotIndices.IsCreated) PickupSlotIndices.Dispose();
                if (DropoffSlotIndices.IsCreated) DropoffSlotIndices.Dispose();
            }
        }

        /// <summary>
        /// Key for tracking tasks by type and entity.
        /// </summary>
        [BurstCompile]
        public struct TaskTypeEntityKey : IEquatable<TaskTypeEntityKey>
        {
            public TaskName Type;
            public Guid EntityGuid;
            public bool Equals(TaskTypeEntityKey other) => EntityGuid == other.EntityGuid && Type == other.Type;
            public override bool Equals(object obj) => obj is TaskTypeEntityKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(EntityGuid, Type);
        }

        /// <summary>
        /// Stores validation results for entities.
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
        }

        /// <summary>
        /// Represents the result of task creation.
        /// </summary>
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

        /// <summary>
        /// Data for disabled entities, including reasons and required items.
        /// </summary>
        [BurstCompile]
        public struct DisabledEntityData
        {
            public int ActionId;
            public DisabledReasonType ReasonType;
            public NativeList<ItemData> RequiredItems;
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

    /// <summary>
    /// Defines the interface for tasks, including execution and validation.
    /// </summary>
    public interface ITask
    {
        TaskName Type { get; }
        StorageType[] SupportedEntityTypes { get; }
        bool IsValidState(int state);
        IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
        IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
    }

    /// <summary>
    /// Defines a generic task interface with processing delegates.
    /// </summary>
    public interface ITask<TSetupOutput, TValidationSetupOutput> : ITask
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        Action<int, int, int[], List<TSetupOutput>> SetupDelegate { get; }
        Action<NativeArray<int>, NativeList<Guid>> SelectEntitiesDelegate { get; }
        Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate { get; }
        Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate { get; }
        Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate { get; }
        void ConfigureProcessor<TProcessorSetupOutput, TProcessorValidationSetupOutput>(
            ref TaskProcessor<TProcessorSetupOutput, TProcessorValidationSetupOutput> processor)
            where TProcessorSetupOutput : struct, IDisposable
            where TProcessorValidationSetupOutput : struct, IDisposable;
    }

    /// <summary>
    /// Base class for tasks, providing default implementations.
    /// </summary>
    public abstract class BaseTask<TSetupOutput, TValidationSetupOutput> : ITask<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        public abstract TaskName Type { get; }
        public virtual StorageType[] SupportedEntityTypes => new[] { StorageType.AnyShelf, StorageType.Station };
        public abstract bool IsValidState(int state);
        public abstract IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
        public abstract IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options);
        public abstract Action<int, int, int[], List<TSetupOutput>> SetupDelegate { get; }
        public abstract Action<NativeArray<int>, NativeList<Guid>> SelectEntitiesDelegate { get; }
        public abstract Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate { get; }
        public abstract Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate { get; }
        public abstract Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate { get; }

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
                throw new ArgumentException($"Type mismatch in ConfigureProcessor: expected {typeof(TSetupOutput)}, {typeof(TValidationSetupOutput)}");
            }
        }
    }

    /// <summary>
    /// Disables entities based on validation results in a Burst-compatible manner.
    /// </summary>
    [BurstCompile]
    public struct DisableEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
    {
        private readonly ITask<TSetupOutput, TValidationSetupOutput> _task;
        public NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntities;
        public NativeList<LogEntry> Logs;

        public DisableEntitiesBurst(ITask<TSetupOutput, TValidationSetupOutput> task)
        {
            _task = task;
            DisabledEntities = default;
            Logs = default;
        }

        public void Execute(int index, NativeArray<ValidationResultData> results, NativeList<DisabledEntityData> outputs)
        {
            var result = results[index];
            if (!result.IsValid)
            {
                var requiredItems = new NativeList<ItemData>(1, Allocator.Persistent);
                requiredItems.Add(result.Item);
                var data = new DisabledEntityData
                {
                    ActionId = result.State - 1,
                    ReasonType = DisabledEntityData.DisabledReasonType.MissingItem,
                    RequiredItems = requiredItems,
                    AnyItem = true
                };
                DisabledEntities[result.EntityGuid] = data;
                outputs.Add(data);
                Logs.Add(new LogEntry
                {
                    Message = $"Disabled entity {result.EntityGuid} for action {data.ActionId}, reason: {data.ReasonType}",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
            }
        }
    }

    /// <summary>
    /// Processes tasks through a five-step pipeline (Setup, Select, ValidateSetup, Validate, Create).
    /// </summary>
    public class TaskProcessor<TSetupOutput, TValidationSetupOutput>
    where TSetupOutput : struct, IDisposable
    where TValidationSetupOutput : struct, IDisposable
    {
        public ITask Task;
        public Action<int, int, int[], List<TSetupOutput>> SetupDelegate;
        public Action<NativeArray<int>, NativeList<Guid>> SelectEntitiesDelegate;
        public Action<int, int, Guid[], List<TValidationSetupOutput>> ValidationSetupDelegate;
        public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate;
        public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate;
        public Action<int, NativeArray<ValidationResultData>, NativeList<DisabledEntityData>> DisableEntityDelegate;

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
                throw new ArgumentException($"Task {task.Type} must implement ITask<{typeof(TSetupOutput)}, {typeof(TValidationSetupOutput)}>");
            }
        }

        /// <summary>
        /// Validates that required delegates are set, logging warnings for unset delegates.
        /// </summary>
        private void ValidateDelegates(TaskName taskType)
        {
            if (SetupDelegate == null)
                Log(Level.Warning, $"SetupDelegate is not set for task {taskType}", Category.Tasks);
            if (SelectEntitiesDelegate == null)
                Log(Level.Warning, $"SelectEntitiesDelegate is not set for task {taskType}", Category.Tasks);
            if (ValidateEntityDelegate == null)
                Log(Level.Warning, $"ValidateEntityDelegate is not set for task {taskType}", Category.Tasks);
            if (CreateTaskDelegate == null)
                Log(Level.Warning, $"CreateTaskDelegate is not set for task {taskType}", Category.Tasks);
            // ValidationSetupDelegate and DisableEntityDelegate are optional
        }
    }

    /// <summary>
    /// Utility methods for selecting entities by type.
    /// </summary>
    public static class TaskUtilities
    {
        public static void SelectEntitiesByType<T>(Property property, NativeList<Guid> output)
            where T : class
        {
            if (typeof(T) == typeof(IEmployeeAdapter) && ManagedDictionaries.IEmployees.TryGetValue(property, out var employees))
            {
                foreach (var kvp in employees)
                    output.Add(kvp.Key);
            }
            else if (typeof(T) == typeof(IStationAdapter) && ManagedDictionaries.IStations.TryGetValue(property, out var stations))
            {
                foreach (var kvp in stations)
                    output.Add(kvp.Key);
            }
            else if (typeof(T) == typeof(PlaceableStorageEntity) && ManagedDictionaries.Storages.TryGetValue(property, out var storages))
            {
                foreach (var kvp in storages)
                    output.Add(kvp.Key);
            }
        }
    }

    /// <summary>
    /// Manages disabled entities and their reasons.
    /// </summary>
    public class EntityDisableService
    {
        internal NativeParallelHashMap<Guid, DisabledEntityData> _disabledEntities;
        private readonly NativeListPool<DisabledEntityData> _outputPool;

        public EntityDisableService()
        {
            _disabledEntities = new NativeParallelHashMap<Guid, DisabledEntityData>(100, Allocator.Persistent);
            _outputPool = new NativeListPool<DisabledEntityData>(() => new NativeList<DisabledEntityData>(100, Allocator.TempJob));
            Log(Level.Info, "Initialized EntityDisableService", Category.Tasks);
        }

        public IEnumerator AddDisabledEntity<TSetupOutput, TValidationSetupOutput>(
            Guid entityGuid, int actionId, DisabledEntityData.DisabledReasonType reasonType,
            NativeList<ItemData> requiredItems, bool anyItem, Property property, TaskName taskType)
            where TSetupOutput : unmanaged, IDisposable
            where TValidationSetupOutput : unmanaged, IDisposable
        {
            using (var scope = new DisposableScope())
            {
                var logs = scope.Add(GetLogPool().Get());
                var outputs = scope.Add(_outputPool.Get());
                var selectedEntities = scope.Add(new NativeList<Guid>(1, Allocator.TempJob) { [0] = entityGuid });
                var validationResults = scope.Add(new NativeList<ValidationResultData>(1, Allocator.TempJob));
                var results = scope.Add(new NativeArray<ValidationResultData>(1, Allocator.TempJob));

                var taskRegistry = TaskServiceManager.GetRegistry(property);
                var task = taskRegistry.GetTask(taskType) as ITask<TSetupOutput, TValidationSetupOutput>;
                if (task == null)
                {
                    Log(Level.Error, $"Task type {taskType} not registered", Category.Tasks);
                    yield return ProcessLogs(logs);
                    yield break;
                }

                var processor = new TaskProcessor<TSetupOutput, TValidationSetupOutput>(task);
                var setupOutputs = new List<TSetupOutput>();
                var setupInputs = new[] { 0 };
                Log(Level.Info, $"Executing setup for disabling entity {entityGuid}, task {taskType}", Category.Tasks);
                yield return SmartExecution.Execute<int, TSetupOutput>(
                    uniqueId: $"{property.name}_{taskType}_Disable_Setup",
                    itemCount: 1,
                    nonBurstDelegate: processor.SetupDelegate,
                    nonBurstResultsDelegate: null,
                    inputs: setupInputs,
                    outputs: setupOutputs,
                    options: default
                );

                var selectJob = new SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
                {
                    Logs = logs,
                    SelectEntitiesDelegate = processor.SelectEntitiesDelegate
                };
                yield return SmartExecution.ExecuteBurst<int, Guid>(
                    uniqueId: $"{property.name}_{taskType}_Disable_SelectEntities",
                    burstDelegate: selectJob.Execute
                );

                var validationSetupOutputs = new List<TValidationSetupOutput>();
                var validationSetupInputs = selectedEntities.ToArray();
                if (processor.ValidationSetupDelegate != null && selectedEntities.Length > 0)
                {
                    Log(Level.Info, $"Executing validation setup for disabling entity {entityGuid}, task {taskType}", Category.Tasks);
                    yield return SmartExecution.Execute<Guid, TValidationSetupOutput>(
                        uniqueId: $"{property.name}_{taskType}_Disable_ValidationSetup",
                        itemCount: selectedEntities.Length,
                        nonBurstDelegate: processor.ValidationSetupDelegate
                    );
                }

                var validateJob = new ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
                {
                    Logs = logs,
                    ValidateEntityDelegate = processor.ValidateEntityDelegate
                };
                yield return SmartExecution.ExecuteBurstFor<Guid, ValidationResultData>(
                    uniqueId: $"{property.name}_{taskType}_Disable_ValidateEntities",
                    itemCount: selectedEntities.Length,
                    burstForDelegate: validateJob.Execute
                );

                yield return SmartExecution.ExecuteBurstFor<ValidationResultData, DisabledEntityData>(
                    uniqueId: $"{property.name}_{taskType}_Disable_Entities",
                    itemCount: validationResults.Length,
                    burstForDelegate: processor.DisableEntityDelegate,
                    burstResultsDelegate: null,
                    inputs: results,
                    outputs: outputs,
                    options: default
                );

                yield return ProcessLogs(logs);
            }
        }

        public void Dispose()
        {
            foreach (var data in _disabledEntities.GetValueArray(Allocator.Temp))
                if (data.RequiredItems.IsCreated)
                    data.RequiredItems.Dispose();
            if (_disabledEntities.IsCreated)
                _disabledEntities.Dispose();
            _outputPool.Dispose();
            Log(Level.Info, "Disposed EntityDisableService", Category.Tasks);
        }
    }

    /// <summary>
    /// Manages task queuing and dispatching for employees.
    /// </summary>
    public class TaskQueue
    {
        private readonly Dictionary<TaskEmployeeType, NativeList<TaskDescriptor>> _highPriorityTasks;
        private readonly Dictionary<TaskEmployeeType, NativeList<TaskDescriptor>> _normalPriorityTasks;
        private readonly NativeList<TaskDescriptor> _anyEmployeeTasks;
        private NativeParallelHashMap<Guid, TaskDescriptor> _activeTasks;
        public NativeParallelHashMap<TaskTypeEntityKey, bool> _activeTasksByType;

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
            _activeTasksByType = new NativeParallelHashMap<TaskTypeEntityKey, bool>(capacity, Allocator.Persistent);
            Log(Level.Info, $"Initialized TaskQueue with capacity {capacity}", Category.Tasks);
        }

        public void Enqueue(TaskDescriptor task)
        {
            var key = new TaskTypeEntityKey { Type = task.Type, EntityGuid = task.EntityGuid };
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
            Log(Level.Info, $"Enqueued task {task.TaskId} for {task.EmployeeType}, priority {task.Priority}", Category.Tasks);
        }

        public bool SelectTask(TaskEmployeeType employeeType, Guid employeeGuid, out TaskDescriptor task)
        {
            task = default;
            var highPriorityList = _highPriorityTasks[employeeType];
            if (highPriorityList.Length > 0)
            {
                task = highPriorityList[0];
                highPriorityList.RemoveAt(0);
                Log(Level.Info, $"Selected high-priority task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
                return true;
            }
            var normalPriorityList = _normalPriorityTasks[employeeType];
            if (normalPriorityList.Length > 0)
            {
                task = normalPriorityList[0];
                normalPriorityList.RemoveAt(0);
                Log(Level.Info, $"Selected normal-priority task {task.TaskId} for employee {employeeGuid}", Category.Tasks);
                return true;
            }
            if (_anyEmployeeTasks.Length > 0)
            {
                task = _anyEmployeeTasks[0];
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
                _activeTasksByType.Remove(key);
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
            if (_activeTasksByType.IsCreated)
                _activeTasksByType.Dispose();
            Log(Level.Info, "Disposed TaskQueue", Category.Tasks);
        }
    }

    /// <summary>
    /// Manages task creation and assignment for a property.
    /// </summary>
    public class TaskService
    {
        private readonly Property _property;
        private readonly CacheService _cacheService;
        private readonly TaskRegistry _taskRegistry;
        private readonly TaskQueue _taskQueue;
        private readonly EntityDisableService _disableService;
        private readonly NativeList<Guid> _entityGuids;
        private NativeArray<ValidationResultData> _validationResults;
        private readonly NativeListPool<LogEntry> _logPool;
        private readonly NativeListPool<TaskResult> _taskResultPool;
        private readonly NativeListPool<ValidationResultData> _validationResultPool;
        private readonly NativeParallelHashMap<Guid, NativeList<TaskDescriptor>> _employeeSpecificTasks;
        private bool _isProcessing;
        private Coroutine _validationCoroutine;

        /// <summary>
        /// Initializes a new instance of the TaskService for the specified property.
        /// </summary>
        /// <param name="property">The property associated with this service.</param>
        public TaskService(Property property)
        {
            _property = property ?? throw new ArgumentNullException(nameof(property));
            _taskRegistry = TaskServiceManager.GetRegistry(property);
            _taskQueue = new TaskQueue(100);
            _cacheService = CacheService.GetOrCreateCacheService(_property);
            _disableService = new EntityDisableService();
            _entityGuids = new NativeList<Guid>(Allocator.Persistent);
            _validationResults = new NativeArray<ValidationResultData>(0, Allocator.Persistent);
            _logPool = PoolUtility.InitializeNativeListPool<LogEntry>(() => new NativeList<LogEntry>(100, Allocator.TempJob), 10, "TaskService_LogPool");
            _taskResultPool = PoolUtility.InitializeNativeListPool<TaskResult>(() => new NativeList<TaskResult>(100, Allocator.TempJob), 10, "TaskService_TaskResultPool");
            _validationResultPool = PoolUtility.InitializeNativeListPool<ValidationResultData>(() => new NativeList<ValidationResultData>(100, Allocator.TempJob), 10, "TaskService_ValidationResultPool");
            _employeeSpecificTasks = new NativeParallelHashMap<Guid, NativeList<TaskDescriptor>>(100, Allocator.Persistent);
#if DEBUG
            Log(Level.Verbose, $"Initialized TaskService for property {property.name}", Category.Tasks);
#endif
            _validationCoroutine = CoroutineRunner.Instance.RunCoroutine(ProcessTasksCoroutine());
        }

        /// <summary>
        /// Manages disposable native collections for Job-compatible code.
        /// </summary>
        internal struct DisposableScope : IDisposable
        {
            private NativeList<NativeList<LogEntry>> _logDisposables;
            private NativeList<NativeList<TaskResult>> _taskResultDisposables;
            private NativeList<NativeList<ValidationResultData>> _validationResultDisposables;
            private NativeList<NativeList<DisabledEntityData>> _disabledEntityDisposables;
            private NativeList<NativeArray<int>> _intArrayDisposables;
            private NativeList<NativeArray<ValidationResultData>> _validationArrayDisposables;
            private NativeList<NativeList<Guid>> _guidListDisposables;

            public DisposableScope(int initialCapacity = 10)
            {
                _logDisposables = new NativeList<NativeList<LogEntry>>(initialCapacity, Allocator.TempJob);
                _taskResultDisposables = new NativeList<NativeList<TaskResult>>(initialCapacity, Allocator.TempJob);
                _validationResultDisposables = new NativeList<NativeList<ValidationResultData>>(initialCapacity, Allocator.TempJob);
                _disabledEntityDisposables = new NativeList<NativeList<DisabledEntityData>>(initialCapacity, Allocator.TempJob);
                _intArrayDisposables = new NativeList<NativeArray<int>>(initialCapacity, Allocator.TempJob);
                _validationArrayDisposables = new NativeList<NativeArray<ValidationResultData>>(initialCapacity, Allocator.TempJob);
                _guidListDisposables = new NativeList<NativeList<Guid>>(initialCapacity, Allocator.TempJob);
            }

            public NativeList<LogEntry> Add(NativeList<LogEntry> disposable)
            {
                _logDisposables.Add(disposable);
                return disposable;
            }

            public NativeList<TaskResult> Add(NativeList<TaskResult> disposable)
            {
                _taskResultDisposables.Add(disposable);
                return disposable;
            }

            public NativeList<ValidationResultData> Add(NativeList<ValidationResultData> disposable)
            {
                _validationResultDisposables.Add(disposable);
                return disposable;
            }

            public NativeList<DisabledEntityData> Add(NativeList<DisabledEntityData> disposable)
            {
                _disabledEntityDisposables.Add(disposable);
                return disposable;
            }

            public NativeArray<int> Add(NativeArray<int> disposable)
            {
                _intArrayDisposables.Add(disposable);
                return disposable;
            }

            public NativeArray<ValidationResultData> Add(NativeArray<ValidationResultData> disposable)
            {
                _validationArrayDisposables.Add(disposable);
                return disposable;
            }

            public NativeList<Guid> Add(NativeList<Guid> disposable)
            {
                _guidListDisposables.Add(disposable);
                return disposable;
            }

            public void Dispose()
            {
                foreach (var disposable in _logDisposables)
                    if (disposable.IsCreated)
                        GetLogPool().Return(disposable);
                foreach (var disposable in _taskResultDisposables)
                    if (disposable.IsCreated)
                        GetTaskResultPool().Return(disposable); // Use pool for TaskResult
                foreach (var disposable in _validationResultDisposables)
                    if (disposable.IsCreated)
                        GetValidationResultPool().Return(disposable); // Use pool for ValidationResultData
                foreach (var disposable in _disabledEntityDisposables)
                    if (disposable.IsCreated)
                        disposable.Dispose();
                foreach (var disposable in _intArrayDisposables)
                    if (disposable.IsCreated)
                        disposable.Dispose();
                foreach (var disposable in _validationArrayDisposables)
                    if (disposable.IsCreated)
                        disposable.Dispose();
                foreach (var disposable in _guidListDisposables)
                    if (disposable.IsCreated)
                        disposable.Dispose();
                if (_logDisposables.IsCreated) _logDisposables.Dispose();
                if (_taskResultDisposables.IsCreated) _taskResultDisposables.Dispose();
                if (_validationResultDisposables.IsCreated) _validationResultDisposables.Dispose();
                if (_disabledEntityDisposables.IsCreated) _disabledEntityDisposables.Dispose();
                if (_intArrayDisposables.IsCreated) _intArrayDisposables.Dispose();
                if (_validationArrayDisposables.IsCreated) _validationArrayDisposables.Dispose();
                if (_guidListDisposables.IsCreated) _guidListDisposables.Dispose();
            }

            private static NativeListPool<TaskResult> GetTaskResultPool()
            {
                return GetTaskResultPool();
            }

            private static NativeListPool<ValidationResultData> GetValidationResultPool()
            {
                return GetValidationResultPool();
            }
        }

        /// <summary>
        /// Retrieves the TaskResult pool for use in DisposableScope.
        /// </summary>
        /// <returns>The NativeListPool for TaskResult.</returns>
        public NativeListPool<TaskResult> GetTaskResultPool()
        {
#if DEBUG
            Log(Level.Verbose, "Retrieved TaskResultPool", Category.Pooling);
#endif
            return _taskResultPool;
        }

        /// <summary>
        /// Retrieves the ValidationResultData pool for use in DisposableScope.
        /// </summary>
        /// <returns>The NativeListPool for ValidationResultData.</returns>
        public NativeListPool<ValidationResultData> GetValidationResultPool()
        {
#if DEBUG
            Log(Level.Verbose, "Retrieved ValidationResultPool", Category.Pooling);
#endif
            return _validationResultPool;
        }

        /// <summary>
        /// Selects entities in a Burst-compatible manner.
        /// </summary>
        [BurstCompile]
        public struct SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
            where TSetupOutput : struct, IDisposable
            where TValidationSetupOutput : struct, IDisposable
        {
            public Action<NativeArray<int>, NativeList<Guid>> SelectEntitiesDelegate;
            public NativeList<LogEntry> Logs;

            public void Execute(NativeArray<int> inputs, NativeList<Guid> outputs)
            {
                SelectEntitiesDelegate(inputs, outputs);
                Logs.Add(new LogEntry
                {
                    Message = $"Selected {outputs.Length} entities",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
            }
        }

        /// <summary>
        /// Validates entities in a Burst-compatible manner.
        /// </summary>
        [BurstCompile]
        public struct ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
            where TSetupOutput : struct, IDisposable
            where TValidationSetupOutput : struct, IDisposable
        {
            public Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate;
            public NativeList<LogEntry> Logs;

            public void Execute(int index, NativeArray<Guid> inputs, NativeList<ValidationResultData> outputs)
            {
                ValidateEntityDelegate(index, inputs, outputs);
                Logs.Add(new LogEntry
                {
                    Message = $"Validated {outputs.Length} entities",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
            }
        }

        /// <summary>
        /// Creates tasks in a Burst-compatible manner.
        /// </summary>
        [BurstCompile]
        public struct CreateTasksBurst<TSetupOutput, TValidationSetupOutput>
            where TSetupOutput : struct, IDisposable
            where TValidationSetupOutput : struct, IDisposable
        {
            public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate;
            public NativeList<LogEntry> Logs;

            public void Execute(int index, NativeArray<ValidationResultData> inputs, NativeList<TaskResult> outputs)
            {
                CreateTaskDelegate(index, inputs, outputs);
                Logs.Add(new LogEntry
                {
                    Message = $"Created {outputs.Length} tasks",
                    Level = Level.Info,
                    Category = Category.Tasks
                });
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
                    using (var scope = new DisposableScope())
                    {
                        var logs = scope.Add(_logPool.Get());
                        var taskResults = scope.Add(_taskResultPool.Get());
                        try
                        {
                            foreach (var task in _taskRegistry.AllTasks)
                            {
#if DEBUG
                                Log(Level.Verbose, $"Processing tasks for type {task.Type}", Category.Tasks);
#endif
                                var selectedEntities = scope.Add(new NativeList<Guid>(Allocator.TempJob));
                                foreach (var entityType in task.SupportedEntityTypes)
                                {
                                    if (entityType == StorageType.Employee && ManagedDictionaries.IEmployees.TryGetValue(_property, out var employees))
                                        foreach (var kvp in employees)
                                            selectedEntities.Add(kvp.Key);
                                    else if (entityType == StorageType.Station && ManagedDictionaries.IStations.TryGetValue(_property, out var stations))
                                        foreach (var kvp in stations)
                                            selectedEntities.Add(kvp.Key);
                                    else if (entityType == StorageType.AnyShelf && ManagedDictionaries.Storages.TryGetValue(_property, out var storages))
                                        foreach (var kvp in storages)
                                            selectedEntities.Add(kvp.Key);
                                }

                                if (selectedEntities.Length == 0)
                                {
#if DEBUG
                                    Log(Level.Verbose, $"No entities found for task type {task.Type}", Category.Tasks);
#endif
                                    continue;
                                }

                                yield return CreateTaskGeneric<Empty, Empty>(Guid.Empty, Guid.Empty, _property, task, logs, taskResults, selectedEntities, scope.Add(_validationResultPool.Get()));
                            }
                            yield return ProcessLogs(logs);
                        }
                        finally
                        {
                            _logPool.Return(logs);
                            _taskResultPool.Return(taskResults);
                        }
                    }
                    _isProcessing = false;
                }
                yield return null;
            }
        }

        /// <summary>
        /// Attempts to get a task for an employee.
        /// </summary>
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
                Log(Level.Info, $"Selected task {task.TaskId} for employee {employee.GUID}", Category.Tasks);
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

        /// <summary>
        /// Creates tasks for a specific employee and entity or all tasks.
        /// </summary>
        public IEnumerator CreateTaskAsync(
            Property property,
            TaskName? taskType = null,
            Guid? employeeGuid = null,
            Guid? entityGuid = null)
        {
            using (var scope = new DisposableScope())
            {
                var logs = scope.Add(_logPool.Get());
                var taskResults = scope.Add(_taskResultPool.Get());
                var validationResults = scope.Add(_validationResultPool.Get());

                try
                {
                    IEnumerable<ITask> tasks = taskType.HasValue
                        ? new[] { _taskRegistry.GetTask(taskType.Value) }
                        : _taskRegistry.AllTasks;
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
                        var selectedEntities = scope.Add(new NativeList<Guid>(Allocator.TempJob));
                        if (employeeGuid.HasValue && entityGuid.HasValue)
                            selectedEntities.Add(entityGuid.Value);
                        else
                            foreach (var entityType in task.SupportedEntityTypes)
                            {
                                if (entityType == StorageType.Employee && ManagedDictionaries.IEmployees.TryGetValue(property, out var employees))
                                {
                                    foreach (var kvp in employees)
                                        if (!employeeGuid.HasValue || kvp.Key == employeeGuid.Value)
                                            selectedEntities.Add(kvp.Key);
                                }
                                else if (entityType == StorageType.Station && ManagedDictionaries.IStations.TryGetValue(property, out var stations))
                                {
                                    foreach (var kvp in stations)
                                        if (!entityGuid.HasValue || kvp.Key == entityGuid.Value)
                                            selectedEntities.Add(kvp.Key);
                                }
                                else if (entityType == StorageType.AnyShelf && ManagedDictionaries.Storages.TryGetValue(property, out var storages))
                                {
                                    foreach (var kvp in storages)
                                        if (!entityGuid.HasValue || kvp.Key == entityGuid.Value)
                                            selectedEntities.Add(kvp.Key);
                                }
                            }

                        if (selectedEntities.Length == 0)
                        {
#if DEBUG
                            Log(Level.Verbose, $"No entities found for task type {task.Type}", Category.Tasks);
#endif
                            continue;
                        }

                        yield return CreateTaskGeneric<Empty, Empty>(employeeGuid ?? Guid.Empty, entityGuid ?? Guid.Empty, property, task, logs, taskResults, selectedEntities, validationResults);
                    }
                    yield return ProcessLogs(logs);
                }
                finally
                {
                    _logPool.Return(logs);
                    _taskResultPool.Return(taskResults);
                }
            }
        }

        /// <summary>
        /// Executes the five-step task creation process.
        /// </summary>
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

            using (var scope = new DisposableScope())
            {
                var processor = new TaskProcessor<TSetupOutput, TValidationSetupOutput>(task);
                var setupOutputs = new List<TSetupOutput>();
                var setupInputs = new[] { 0 };
                Log(Level.Info, $"Executing setup for task {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
                yield return SmartExecution.Execute<int, TSetupOutput>(
                    uniqueId: $"{property.name}_{task.Type}_Setup",
                    itemCount: 1,
                    nonBurstDelegate: processor.SetupDelegate
                );

                var selectJob = new SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
                {
                    Logs = logs,
                    SelectEntitiesDelegate = processor.SelectEntitiesDelegate
                };
                yield return SmartExecution.ExecuteBurst<int, Guid>(
                    uniqueId: $"{property.name}_{task.Type}_SelectEntities",
                    burstDelegate: selectJob.Execute
                );

                var validationSetupOutputs = new List<TValidationSetupOutput>();
                var validationSetupInputs = entityGuids.ToArray();
                if (processor.ValidationSetupDelegate != null && entityGuids.Length > 0)
                {
                    Log(Level.Info, $"Executing validation setup for task {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
                    yield return SmartExecution.Execute<Guid, TValidationSetupOutput>(
                        uniqueId: $"{property.name}_{task.Type}_ValidationSetup",
                        itemCount: entityGuids.Length,
                        nonBurstDelegate: processor.ValidationSetupDelegate
                    );
                }

                var results = scope.Add(new NativeArray<ValidationResultData>(entityGuids.Length, Allocator.TempJob));
                var validateJob = new ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
                {
                    Logs = logs,
                    ValidateEntityDelegate = processor.ValidateEntityDelegate
                };
                yield return SmartExecution.ExecuteBurstFor<Guid, ValidationResultData>(
                    uniqueId: $"{property.name}_{task.Type}_ValidateEntities",
                    itemCount: entityGuids.Length,
                    burstForDelegate: validateJob.Execute
                );

                var createJob = new CreateTasksBurst<TSetupOutput, TValidationSetupOutput>
                {
                    Logs = logs,
                    CreateTaskDelegate = processor.CreateTaskDelegate
                };
                yield return SmartExecution.ExecuteBurstFor<ValidationResultData, TaskResult>(
                    uniqueId: $"{property.name}_{task.Type}_CreateTasks",
                    itemCount: validationResults.Length,
                    burstForDelegate: createJob.Execute,
                    nonBurstResultsDelegate: outputs =>
                    {
                        Log(Level.Info, $"Enqueuing {outputs.Count} tasks for type {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
                        for (int i = 0; i < outputs.Count; i++)
                        {
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
        }

        /// <summary>
        /// Completes a task and removes it from the queue.
        /// </summary>
        public void CompleteTask(TaskDescriptor task)
        {
            _taskQueue.CompleteTask(task.TaskId);
        }

        /// <summary>
        /// Retrieves an employee-specific task, if available.
        /// </summary>
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

        /// <summary>
        /// Disposes of all resources used by the TaskService.
        /// </summary>
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
            PoolUtility.DisposeNativeListPool(_taskResultPool, "TaskService_TaskResultPool");
            PoolUtility.DisposeNativeListPool(_validationResultPool, "TaskService_ValidationResultPool");
            Log(Level.Info, $"Disposed TaskService for property {_property.name}", Category.Tasks);
        }
    }

    /// <summary>
    /// Manages TaskService and TaskRegistry instances for properties.
    /// </summary>
    public static class TaskServiceManager
    {
        private static readonly Dictionary<Property, TaskService> _services = new();
        private static readonly Dictionary<Property, TaskRegistry> _registries = new();

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

        public static TaskRegistry GetRegistry(Property property)
        {
            if (!_registries.ContainsKey(property))
            {
                _registries[property] = new TaskRegistry();
                Log(Level.Info, $"Created TaskRegistry for property {property.name}", Category.Tasks);
            }
            return _registries[property];
        }

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

    public partial class TaskRegistry
    {
        private readonly List<ITask> tasks = new();

        public IEnumerable<ITask> AllTasks => tasks;

        public void Initialize()
        {
            // This method will be extended by the generated code
        }

        public void Register(ITask task)
        {
            if (!tasks.Contains(task))
            {
                tasks.Add(task);
                Log(Level.Info, $"Registered task: {task.Type}", Category.Tasks);
            }
        }

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

        public ITask GetTask(TaskName type)
        {
            return tasks.FirstOrDefault(t => t.Type == type);
        }

        public void Dispose()
        {
            tasks.Clear();
            Log(Level.Info, "Disposed TaskRegistry", Category.Tasks);
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class EntityTaskAttribute : Attribute
    {
    }

    [Generator]
    public class TaskTypeSourceGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new TaskTypeSyntaxReceiver());
        }

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

                // Validate ITask implementation
                if (!classSymbol.AllInterfaces.Any(i => i.ToString().StartsWith("NoLazyWorkers.TaskService.ITask")))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "NLW002", "Invalid Task Implementation",
                            $"Class {classSymbol.Name} marked with EntityTaskAttribute must implement ITask or ITask<TSetupOutput, TValidationSetupOutput>", "TaskRegistration", DiagnosticSeverity.Error, true),
                        classDeclaration.GetLocation()));
                    continue;
                }

                // Validate ITask.Type property
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

                // Register the task
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