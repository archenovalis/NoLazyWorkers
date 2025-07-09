# TaskService API Documentation

**Date**: July 09, 2025

## Overview

The `NoLazyWorkers.TaskService` namespace provides a robust system for managing tasks in a networked environment, integrated with Unity's Job System and Burst compilation for performance optimization. It supports task creation, queuing, execution, and entity disabling, with a focus on modularity and extensibility. Tasks are defined with generic setup and validation outputs, enabling flexible workflows for various task types, such as inventory delivery or station operations. The system uses `SmartExecution` for efficient job scheduling and `StorageManager` for storage operations, ensuring minimal main thread impact.

Key components include:

- **TaskBuilder**: Configures tasks with delegates for setup, entity selection, validation, and task creation.
- **TaskService**: Manages task creation and execution for a property.
- **TaskQueue**: Prioritizes and manages task queues by employee type.
- **EntityDisableService**: Handles entity disabling based on validation results.
- **TaskRegistry**: Registers and manages tasks.
- **TaskTypeSourceGenerator**: Automatically generates task registration code for classes marked with `EntityTaskAttribute`.

Tasks are executed as coroutines, leveraging `CoroutineRunner` for asynchronous operations, and use `NativeList` and `NativeArray` for Burst-compatible data handling. Logging is performed using `Deferred.LogEntry` for Burst jobs and `Debug.Log` for non-Burst scenarios, with verbose logging wrapped in `#if DEBUG`.

## Classes and Structs

### TaskBuilder<TSetupOutput, TValidationSetupOutput>

Builds a task with configurable delegates for setup, entity selection, validation, and task creation.

- **Type Parameters**:
  - `TSetupOutput`: The type of setup output, must be a struct and implement `IDisposable`.
  - `TValidationSetupOutput`: The type of validation setup output, must be a struct and implement `IDisposable`.

- **Constructor**:
  - `TaskBuilder(TaskName taskType)`: Initializes a new instance with the specified task type.
    - **Parameters**:
      - `taskType`: The type of task to build (e.g., `TaskName.DeliverInventory`).

- **Methods**:
  - `WithSetup(Action<int, int, int[], List<TSetupOutput>> setupDelegate)`: Configures the setup delegate. Returns the builder for chaining.
    - **Parameters**:
      - `setupDelegate`: Delegate to handle setup logic, processing input integers into setup outputs.
  - `WithSelectEntities(Action<int, NativeList<Guid>> selectEntitiesDelegate)`: Configures the entity selection delegate. Returns the builder for chaining.
    - **Parameters**:
      - `selectEntitiesDelegate`: Delegate to select entity GUIDs for processing.
  - `WithValidationSetup(Action<int, int, Guid[], List<TValidationSetupOutput>> validationSetupDelegate)`: Configures the validation setup delegate. Returns the builder for chaining.
    - **Parameters**:
      - `validationSetupDelegate`: Delegate to prepare validation data for entities.
  - `WithValidateEntity(Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> validateEntityDelegate)`: Configures the entity validation delegate. Returns the builder for chaining.
    - **Parameters**:
      - `validateEntityDelegate`: Delegate to validate entities, producing validation results.
  - `WithCreateTask(Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> createTaskDelegate)`: Configures the task creation delegate. Returns the builder for chaining.
    - **Parameters**:
      - `createTaskDelegate`: Delegate to create tasks from validation results.

- **Properties**:
  - `SetupDelegate`: Gets the configured setup delegate.
  - `SelectEntitiesDelegate`: Gets the configured entity selection delegate.
  - `ValidationSetupDelegate`: Gets the configured validation setup delegate.
  - `ValidateEntityDelegate`: Gets the configured entity validation delegate.
  - `CreateTaskDelegate`: Gets the configured task creation delegate.

- **Usage**:
  - Create a `TaskBuilder` to define a task, configure its delegates, and use it to create tasks via `TaskService.CreateTaskAsync`.

### TaskService

Manages task creation, queuing, and execution for a specific property.

- **Constructor**:
  - `TaskService(Property property)`: Initializes a new task service for the specified property.
    - **Parameters**:
      - `property`: The `Property` to manage tasks for.

- **Methods**:
  - `TryGetTaskAsync(Employee employee)`: Attempts to retrieve a task for the specified employee. Yields a tuple of `(bool, TaskDescriptor, ITask)`.
    - **Parameters**:
      - `employee`: The `Employee` to get a task for.
    - **Yields**: `(success, task, taskImpl)` where `success` indicates if a task was found, `task` is the `TaskDescriptor`, and `taskImpl` is the `ITask` implementation.
  - `CreateTaskAsync(Property property, TaskName? taskType = null, Guid? employeeGuid = null, Guid? entityGuid = null)`: Creates tasks for the specified property, optionally filtered by task type, employee, or entity GUID. Yields via coroutine.
    - **Parameters**:
      - `property`: The `Property` to create tasks for.
      - `taskType`: Optional `TaskName` to filter tasks (e.g., `TaskName.PackagingStation`).
      - `employeeGuid`: Optional GUID to filter tasks for a specific employee.
      - `entityGuid`: Optional GUID to filter tasks for a specific entity.
  - `CompleteTask(TaskDescriptor task)`: Completes the specified task and removes it from the queue.
    - **Parameters**:
      - `task`: The `TaskDescriptor` to complete.
  - `GetEmployeeSpecificTask(Guid employeeGuid)`: Retrieves an employee-specific task, if available.
    - **Parameters**:
      - `employeeGuid`: The GUID of the employee.
    - **Returns**: The `TaskDescriptor` or default if none exists.
  - `Dispose()`: Disposes of the service’s resources, including task queues and native collections.

- **Usage**:
  - Use `TaskServiceManager.GetOrCreateService` to obtain a `TaskService` for a property, then call `CreateTaskAsync` to generate tasks or `TryGetTaskAsync` to assign tasks to employees.

### TaskQueue

Manages a prioritized queue of tasks by employee type.

- **Constructor**:
  - `TaskQueue(int capacity)`: Initializes a new task queue with the specified capacity.
    - **Parameters**:
      - `capacity`: The initial capacity for task lists.

- **Methods**:
  - `Enqueue(TaskDescriptor task)`: Enqueues a task based on its employee type and priority.
    - **Parameters**:
      - `task`: The `TaskDescriptor` to enqueue.
  - `SelectTask(TaskEmployeeType employeeType, Guid employeeGuid, out TaskDescriptor task)`: Selects a task for the specified employee type and GUID.
    - **Parameters**:
      - `employeeType`: The `TaskEmployeeType` (e.g., `TaskEmployeeType.Handler`).
      - `employeeGuid`: The GUID of the employee.
      - `task`: Output `TaskDescriptor`, if a task is selected.
    - **Returns**: `true` if a task was selected, `false` otherwise.
  - `CompleteTask(Guid taskId)`: Completes a task and removes it from the queue.
    - **Parameters**:
      - `taskId`: The ID of the task to complete.
  - `Dispose()`: Disposes of all task lists and resources.

- **Usage**:
  - Typically used internally by `TaskService` to manage task assignment and completion.

### EntityDisableService

Manages disabled entities and their associated data.

- **Constructor**:
  - `EntityDisableService()`: Initializes a new service for managing disabled entities.

- **Methods**:
  - `AddDisabledEntity<TSetupOutput, TValidationSetupOutput>(Guid entityGuid, int actionId, DisabledEntityData.DisabledReasonType reasonType, NativeList<ItemData> requiredItems, bool anyItem, Property property, TaskName taskType)`: Adds a disabled entity with specified parameters. Yields via coroutine.
    - **Parameters**:
      - `entityGuid`: The GUID of the entity to disable.
      - `actionId`: The action identifier.
      - `reasonType`: The reason for disabling (e.g., `DisabledEntityData.DisabledReasonType.MissingItem`).
      - `requiredItems`: The required items for the entity.
      - `anyItem`: Whether any item is acceptable.
      - `property`: The `Property` containing the entity.
      - `taskType`: The `TaskName` of the task.
  - `Dispose()`: Disposes of the service’s resources, including disabled entity data.

- **Usage**:
  - Used to disable entities that fail validation, typically called during task processing.

### TaskRegistry

Registers and manages tasks for a property.

- **Methods**:
  - `Initialize()`: Initializes the task registry, auto-registering tasks marked with `EntityTaskAttribute`.
  - `Register(ITask task)`: Registers a task.
    - **Parameters**:
      - `task`: The `ITask` to register.
  - `RegisterExternal(Type taskType, TaskName taskTypeEnum)`: Registers an external task type.
    - **Parameters**:
      - `taskType`: The `Type` of the task.
      - `taskTypeEnum`: The `TaskName` enum value.
  - `GetTask(TaskName type)`: Retrieves a task by its type.
    - **Parameters**:
      - `type`: The `TaskName` of the task.
    - **Returns**: The `ITask` or `null` if not found.
  - `Dispose()`: Disposes of the task registry.

- **Usage**:
  - Use `TaskServiceManager.GetRegistry` to access the registry and register or retrieve tasks.

### TaskServiceManager

Manages task services and registries for properties.

- **Static Methods**:
  - `GetOrCreateService(Property property)`: Gets or creates a task service for the specified property.
    - **Parameters**:
      - `property`: The `Property` to get or create a service for.
    - **Returns**: The `TaskService` instance.
  - `GetRegistry(Property property)`: Gets the task registry for the specified property.
    - **Parameters**:
      - `property`: The `Property` to get the registry for.
    - **Returns**: The `TaskRegistry` instance.
  - `Cleanup()`: Disposes of all services and registries.

- **Usage**:
  - Primary entry point for accessing task services and registries.

### Extensions.TaskDescriptor

Describes a task with metadata and resources.

- **Fields**:
  - `EntityGuid`: GUID of the entity associated with the task.
  - `TaskId`: Unique GUID of the task.
  - `Type`: `TaskName` of the task (e.g., `TaskName.DeliverInventory`).
  - `ActionId`: Action identifier.
  - `EmployeeType`: Required `TaskEmployeeType` (e.g., `TaskEmployeeType.Handler`).
  - `Priority`: Task priority (higher values indicate higher priority).
  - `Item`: `ItemData` for the task.
  - `Quantity`: Quantity of the item.
  - `PickupGuid`: GUID of the pickup location.
  - `PickupSlotIndices`: Indices of pickup slots.
  - `DropoffGuid`: GUID of the dropoff location.
  - `DropoffSlotIndices`: Indices of dropoff slots.
  - `PropertyName`: Name of the property.
  - `CreationTime`: Time the task was created.

- **Static Methods**:
  - `Create(Guid entityGuid, TaskName type, int actionId, TaskEmployeeType employeeType, int priority, string propertyName, ItemData item, int quantity, Guid pickupGuid, int[] pickupSlotIndices, Guid dropoffGuid, int[] dropoffSlotIndices, float creationTime, NativeList<LogEntry> logs)`: Creates a new `TaskDescriptor`.
    - **Parameters**:
      - `entityGuid`, `type`, `actionId`, `employeeType`, `priority`, `propertyName`, `item`, `quantity`, `pickupGuid`, `pickupSlotIndices`, `dropoffGuid`, `dropoffSlotIndices`, `creationTime`, `logs`: Parameters defining the task and logging.
    - **Returns**: A new `TaskDescriptor`.

- **Methods**:
  - `Dispose()`: Disposes of the task’s native arrays.

### Extensions.TaskResult

Represents the result of a task execution.

- **Fields**:
  - `Task`: The `TaskDescriptor`.
  - `Success`: Whether the task was successful.
  - `FailureReason`: Reason for failure, if applicable.

- **Constructor**:
  - `TaskResult(TaskDescriptor task, bool success, FixedString128Bytes failureReason = default)`: Initializes a new `TaskResult`.

### Extensions.ValidationResultData

Holds validation results for an entity.

- **Fields**:
  - `EntityGuid`: GUID of the entity.
  - `IsValid`: Whether the entity is valid.
  - `State`: Validation state.
  - `Item`: `ItemData` associated with the entity.
  - `Quantity`: Quantity of the item.
  - `DestinationCapacity`: Available capacity at the destination.

### Extensions.DisabledEntityData

Represents data for a disabled entity.

- **Fields**:
  - `ActionId`: Action identifier.
  - `ReasonType`: Reason for disabling (e.g., `MissingItem`).
  - `RequiredItems`: List of required items.
  - `AnyItem`: Whether any item is acceptable.

### Extensions.TaskName

Enum defining task types.

- **Values**:
  - `DeliverInventory`
  - `PackagerRefillStation`
  - `PackagerEmptyLoadingDock`
  - `PackagerRestock`
  - `PackagingStation`
  - `MixingStation`
  - `SimpleExample`

### Extensions.TaskEmployeeType

Enum defining employee types for tasks.

- **Values**:
  - `Any`
  - `Chemist`
  - `Handler`
  - `Botanist`
  - `Driver`
  - `Cleaner`

### Extensions.Empty

Empty struct implementing `IDisposable` for use as a placeholder.

- **Methods**:
  - `Dispose()`: Empty implementation.

## Example: DeliverInventory Task

This example demonstrates creating and executing a `DeliverInventory` task to move items from a source to a destination storage entity, using `TaskBuilder`, `TaskService`, and `StorageManager`.

```csharp

[EntityTask]
public class DeliverInventoryTask : BaseTask<Empty, Empty>
{
    public override TaskName Type => TaskName.DeliverInventory;
    public override StorageType[] SupportedEntityTypes => new[] { StorageType.AnyShelf, StorageType.Station };

    public override bool IsValidState(int state) => state >= 0;

    public override Action<int, int, int[], List<Empty>> SetupDelegate => 
        (start, count, inputs, outputs) => outputs.Add(new Empty());

    public override Action<int, NativeList<Guid>> SelectEntitiesDelegate => 
        (inputs, outputs) => TaskUtilities.SelectEntitiesByType<PlaceableStorageEntity>(_property, outputs);

    public override Action<int, int, Guid[], List<Empty>> ValidationSetupDelegate => 
        (start, count, inputs, outputs) => { for (int i = 0; i < count; i++) outputs.Add(new Empty()); };

    public override Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate =>
        (index, guids, outputs) =>
        {
            var guid = guids[index];
            var cacheService = CacheService.GetOrCreateCacheService(_property);
            var logs = new NativeList<LogEntry>(Allocator.TempJob);
            try
            {
                if (ManagedDictionaries.Storages.TryGetValue(_property, out var storages) && storages.TryGetValue(guid, out var storage))
                {
                    var slots = storage.GetSlots();
                    foreach (var slot in slots)
                    {
                        if (slot.Quantity > 0)
                        {
                            var item = slot.ItemInstance;
                            var result = new ValidationResultData
                            {
                                EntityGuid = guid,
                                IsValid = true,
                                State = 1,
                                Item = new ItemData(item),
                                Quantity = slot.Quantity,
                                DestinationCapacity = 0
                            };
                            outputs.Add(result);
                            #if DEBUG
                            logs.Add(new LogEntry
                            {
                                Message = $"Validated storage {guid} with item {item.ID}, quantity {slot.Quantity}",
                                Level = Level.Verbose,
                                Category = Category.Tasks
                            });
                            #endif
                        }
                    }
                }
            }
            finally
            {
                if (logs.IsCreated) logs.Dispose();
            }
        };

    public override Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate =>
        (index, results, outputs) =>
        {
            var result = results[index];
            var logs = new NativeList<LogEntry>(Allocator.TempJob);
            try
            {
                if (result.IsValid)
                {
                    var task = TaskDescriptor.Create(
                        entityGuid: result.EntityGuid,
                        type: TaskName.DeliverInventory,
                        actionId: 1,
                        employeeType: TaskEmployeeType.Handler,
                        priority: 100,
                        propertyName: _property.name,
                        item: result.Item,
                        quantity: result.Quantity,
                        pickupGuid: result.EntityGuid,
                        pickupSlotIndices: new[] { 0 },
                        dropoffGuid: Guid.Empty,
                        dropoffSlotIndices: null,
                        creationTime: Time.time,
                        logs: logs
                    );
                    outputs.Add(new TaskResult(task, true));
                }
            }
            finally
            {
                if (logs.IsCreated) logs.Dispose();
            }
        };

    public override IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
    {
        var cacheService = CacheService.GetOrCreateCacheService(_property);
        var logs = new NativeList<LogEntry>(Allocator.TempJob);
        try
        {
            yield return StorageManager.FindDeliveryDestination(_property, task.Item.CreateItemInstance(), task.Quantity, task.PickupGuid,
                destinations =>
                {
                    if (destinations != null && destinations.Count > 0)
                    {
                        var destination = destinations[0];
                        var operations = new List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)>
                        {
                            (task.PickupGuid, task.PickupSlotIndices.Length > 0 ? new ItemSlot(task.PickupSlotIndices[0]) : null, task.Item.CreateItemInstance(), task.Quantity, false, employee.NetworkObject, "Pickup for delivery"),
                            (destination.Entity.Guid, destination.ItemSlots[0], task.Item.CreateItemInstance(), task.Quantity, true, employee.NetworkObject, "Dropoff for delivery")
                        };
                        CoroutineRunner.Instance.RunCoroutineWithResult(StorageManager.ExecuteSlotOperations(_property, operations), results =>
                        {
                            if (results.All(r => r))
                            {
                                Log(Level.Info, $"Delivered {task.Quantity} of item {task.Item.Id} from {task.PickupGuid} to {destination.Entity.Guid}", Category.Tasks);
                            }
                            else
                            {
                                Log(Level.Error, $"Failed to deliver item {task.Item.Id}", Category.Tasks);
                            }
                        });
                    }
                    else
                    {
                        Log(Level.Warning, $"No delivery destination found for item {task.Item.Id}", Category.Tasks);
                    }
                });
            yield return ProcessLogs(logs);
        }
        finally
        {
            if (logs.IsCreated) logs.Dispose();
        }
    }

    public override IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
    {
        yield return null; // No follow-up actions required
    }

    private readonly Property _property;

    public DeliverInventoryTask(Property property)
    {
        _property = property;
    }
}

// Usage Example
public class TaskExample : MonoBehaviour
{
    public Property property;
    public Employee employee;

    private IEnumerator Start()
    {
        // Initialize StorageManager
        StorageManager.Initialize();

        // Register the task
        var registry = TaskServiceManager.GetRegistry(property);
        registry.Register(new DeliverInventoryTask(property));

        // Create a task service
        var taskService = TaskServiceManager.GetOrCreateService(property);

        // Create tasks
        yield return taskService.CreateTaskAsync(property, TaskName.DeliverInventory);

        // Assign a task to an employee
        yield return taskService.TryGetTaskAsync(employee, result =>
        {
            if (result.Item1)
            {
                var (success, task, taskImpl) = result;
                Log(Level.Info, $"Assigned task {task.TaskId} to employee {employee.GUID}", Category.Tasks);
                CoroutineRunner.Instance.RunCoroutine(taskImpl.ExecuteCoroutine(employee, task, default));
                taskService.CompleteTask(task);
            }
            else
            {
                Log(Level.Warning, $"No task available for employee {employee.GUID}", Category.Tasks);
            }
        });
    }

    private void OnDestroy()
    {
        TaskServiceManager.Cleanup();
        StorageManager.Cleanup();
    }
}
```

## Usage Notes

- Server-Side Execution: Task operations (e.g., CreateTaskAsync, TryGetTaskAsync) require server-side execution (FishNetExtensions.IsServer).
- Coroutine Handling: Use CoroutineRunner.RunCoroutine or RunCoroutineWithResult for methods yielding results, as shown in the example.
- Resource Management: Ensure proper disposal of native collections (NativeList, NativeArray) using DisposableScope or try-finally blocks to prevent memory leaks.
- Burst Compilation: Structs like TaskDescriptor, TaskResult, and smartexecute structs (SelectEntitiesBurst, ValidateEntitiesBurst, CreateTasksBurst) are Burst-compiled for performance. Ensure delegates and data structures are Burst-compatible.
- Logging: Use Deferred.LogEntry for Burst jobs and Debug.Log for non-Burst scenarios. Verbose logging is wrapped in #if DEBUG.
- Task Registration: Classes marked with EntityTaskAttribute are automatically registered via TaskTypeSourceGenerator. Ensure tasks implement ITask or ITask<TSetupOutput, TValidationSetupOutput>.
- Dependencies: Assumes StorageManager, ManagedDictionaries, and SmartExecution are available and initialized. Ensure StorageManager.Initialize() is called before task operations involving storage.
