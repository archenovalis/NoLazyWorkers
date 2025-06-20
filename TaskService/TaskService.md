# API Documentation for NoLazyWorkers.TaskService

This document describes the public API of the `NoLazyWorkers.TaskService` namespace, which provides functionality for managing tasks, task validation, and task execution in a game or simulation environment. The system uses Unity's Burst compiler for performance optimization and integrates with FishNet for networked operations. It includes utilities for task execution timing, task queuing, validation, and creation, as well as Burst-compatible jobs for efficient task processing.

**Namespace**: `NoLazyWorkers.TaskService`

**Current Date**: June 15, 2025

## Classes and Structs

### Utilities

Provides helper methods for task execution timing and delays.

#### `static class Utilities`

- **Fields**:
  - `ConcurrentDictionary<string, float> _executionTimes`: Stores task execution times by task ID.

- **Methods**:
  - `void LogExecutionTime(string taskId, long milliseconds)`
    - Logs the execution time for a task.
    - **Parameters**:
      - `taskId`: Unique task identifier.
      - `milliseconds`: Execution time in milliseconds.
  - `float GetAverageTaskExecutionTime()`
    - Calculates the average execution time across all logged tasks.
    - **Returns**: Average execution time in milliseconds, defaulting to 100f if no tasks are logged.
  - `async Task DelayAsync(float seconds)`
    - Delays execution for the specified time, aligning with the next server tick.
    - **Parameters**:
      - `seconds`: Delay duration in seconds.

### Extensions

Contains structs, enums, and extension methods for task-related data and operations.

#### `enum TaskTypes`

Defines types of tasks.

- **Values**:
  - `DeliverInventory`
  - `PackagerRefillStation`
  - `PackagerEmptyLoadingDock`
  - `PackagerRestock`
  - `PackagingStation`
  - `MixingStation`

#### `struct TaskResult`

Encapsulates task completion results.

- **Properties**:
  - `TaskDescriptor Task`: The completed task.
  - `bool Success`: Indicates if the task succeeded.
  - `string FailureReason`: Reason for failure, if applicable.

- **Constructor**: `TaskResult(TaskDescriptor task, bool success, string failureReason = null)`
  - Initializes a task result, setting `FailureReason` to an empty string on success or "Unknown failure" if unspecified.

#### `struct ValidationResultData` [BurstCompile]

Burst-compatible validation result data.

- **Fields**:
  - `bool IsValid`: Indicates if the validation passed.
  - `int ActionId`: Identifier for the action.
  - `FixedString128Bytes StateData`: Simplified state data for Burst compatibility.

#### `struct TaskDescriptor : IDisposable` [BurstCompile]

Describes a task for Burst-compatible processing.

- **Fields**:
  - `Guid EntityGuid`: Entity associated with the task.
  - `Guid TaskId`: Unique task identifier.
  - `int ActionId`: Action identifier.
  - `TaskTypes Type`: Task type.
  - `ItemKey Item`: Item involved in the task.
  - `int Quantity`: Item quantity.
  - `TransitTypes PickupType`: Type of pickup location.
  - `Guid PickupGuid`: Pickup location identifier.
  - `int PickupSlotIndex1`, `PickupSlotIndex2`, `PickupSlotIndex3`: Pickup slot indices.
  - `int PickupSlotCount`: Number of pickup slots.
  - `TransitTypes DropoffType`: Type of dropoff location.
  - `Guid DropoffGuid`: Dropoff location identifier.
  - `int DropoffSlotIndex1`, `DropoffSlotIndex2`, `DropoffSlotIndex3`: Dropoff slot indices.
  - `int DropoffSlotCount`: Number of dropoff slots.
  - `EmployeeTypes EmployeeType`: Required employee type.
  - `int Priority`: Task priority.
  - `FixedString32Bytes PropertyName`: Associated property name.
  - `float CreationTime`: Task creation timestamp.
  - `Guid FollowUpEmployeeGUID`: Employee for follow-up tasks.
  - `bool IsFollowUp`: Indicates if the task is a follow-up.
  - `FixedString128Bytes UniqueKey`: Unique task key.

- **Methods**:
  - `static TaskDescriptor Create(...)`
    - Creates a task descriptor with specified parameters.
    - **Parameters**:
      - `entityGuid`, `type`, `actionId`, `employeeType`, `priority`, `propertyName`, `item`, `quantity`, `pickupType`, `pickupGUID`, `pickupSlotIndices`, `dropoffType`, `dropoffGUID`, `dropoffSlotIndices`, `creationTime`, `followUpEmployeeGUID`, `isFollowUp`.
    - **Returns**: Initialized `TaskDescriptor`.
  - `void Dispose()`: Placeholder for cleanup (no-op).

#### `abstract class ValidationResult`

Base class for validation results.

- **Properties**:
  - `bool IsValid`: Indicates if the validation passed.

#### `static bool IsEmployeeTypeValid(this TaskDescriptor task, Employee employee)`

Extension method to check if an employee’s type matches the task’s required type.

- **Parameters**:
  - `task`: Task descriptor.
  - `employee`: Employee to validate.
- **Returns**: `true` if the employee type is valid or the task allows any type, `false` otherwise.

#### `interface ITaskDefinition`

Defines a task’s metadata and creation logic.

- **Properties**:
  - `TaskTypes Type`: Task type.
  - `int Priority`: Task priority.
  - `IEntitySelector EntitySelector`: Selects entities for task creation.
- **Methods**:
  - `void CreateTasks(Guid entityGuid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)`: Creates tasks for an entity.

#### `interface ITaskAction`

Defines task validation and execution logic.

- **Methods**:
  - `Task ValidateStateAsync(Chemist chemist, IStationAdapter station)`: Validates the task state.
  - `Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)`: Executes the task.

#### `interface IEntitySelector`

Selects entities for task creation.

- **Methods**:
  - `NativeList<Guid> SelectEntities(Property property, Allocator allocator)`: Selects entity GUIDs for a property.

#### `interface ITaskValidator`

Validates tasks for an entity.

- **Methods**:
  - `void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)`: Validates tasks.

#### `enum EmployeeTypes`

Defines employee types.

- **Values**:
  - `Any`
  - `Chemist`
  - `Handler`
  - `Botanist`
  - `Driver`
  - `Cleaner`

#### `enum NEQuality`

Defines item quality levels.

- **Values**:
  - `Trash`
  - `Poor`
  - `Standard`
  - `Premium`
  - `Heavenly`
  - `None`

#### `enum TransitTypes`

Defines transit location types.

- **Values**:
  - `None`
  - `Inventory`
  - `LoadingDock`
  - `PlaceableStorageEntity`
  - `AnyStation`
  - `MixingStation`
  - `PackagingStation`
  - `BrickPress`
  - `LabOven`
  - `Pot`
  - `DryingRack`
  - `ChemistryStation`
  - `Cauldron`

#### `struct TaskValidatorContext`

Context for task validation.

- **Fields**:
  - `FixedString32Bytes AssignedPropertyName`: Property name.
  - `NativeParallelHashMap<SlotKey, SlotReservation> ReservedSlots`: Reserved slots.
  - `NativeParallelHashMap<ItemKey, NativeList<StorageKey>> SpecificShelves`: Shelves for specific items.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StorageInputSlots`: Storage input slots.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationInputSlots`: Station input slots.
  - `float CurrentTime`: Current timestamp.
  - `TaskDescriptor Task`: Current task.
  - `IStationAdapter StationAdapter`: Station adapter.

#### `interface ITaskLogic`

Defines Burst-compatible task validation and creation logic.

- **Methods**:
  - `ValidationResultData ValidateEntityState(object entity, TaskValidatorContext context)`: Validates entity state.
  - `TaskDescriptor CreateTask(object entity, Property property, ValidationResultData result)`: Creates a task.

#### `interface ITask`

Base interface for tasks.

- **Properties**:
  - `ITaskDefinition TaskDefinition`: Task definition.
  - `IEntitySelector EntitySelector`: Entity selector.
- **Methods**:
  - `Task<ValidationResult> ValidateEntityState(object entity)`: Validates entity state.
  - `(ITaskAction Action, int ActionId) ActionSelection()`: Selects an action.
  - `ITaskAction GetActionFromId(int actionId)`: Retrieves an action by ID.
  - `TaskDescriptor CreateTaskForState(object entity, Property property)`: Creates a task for an entity.

#### `interface IBurstTask : ITask`

Extended interface for Burst-compatible tasks.

- **Methods**:
  - `ValidationResultData ValidateEntityState(object entity, TaskValidatorContext context)`: Validates entity state (Burst-compatible).
  - `TaskDescriptor CreateTaskForState(object entity, Property property, ValidationResultData result)`: Creates a task (Burst-compatible).

#### `struct BurstTaskLogicAdapter : ITaskLogic` [BurstCompile]

Adapts `IBurstTask` to `ITaskLogic` for Burst jobs.

- **Constructor**: `BurstTaskLogicAdapter(IBurstTask burstTask)`
- **Methods**:
  - `ValidationResultData ValidateEntityState(object entity, TaskValidatorContext context)`: Delegates to the burst task.
  - `TaskDescriptor CreateTask(object entity, Property property, ValidationResultData result)`: Delegates to the burst task.

### Jobs

Contains Burst-compiled jobs for task validation and creation.

#### `struct TaskValidationJob : IJobParallelFor` [BurstCompile]

Validates entities for tasks in parallel.

- **Fields**:
  - `NativeList<Guid> Entities` [ReadOnly]: Entities to validate.
  - `Property Property` [ReadOnly]: Associated property.
  - `TaskValidatorContext Context` [ReadOnly]: Validation context.
  - `ITaskLogic TaskLogic`: Task logic for validation.
  - `NativeList<ValidationResultData> ValidationResults`: Validation results.
  - `NativeList<LogEntry> Logs`: Debug logs.
  - `JobService.JobResourceManager Resources`: Manages job resources.
  - `NativeArray<PerformanceMetric> Metrics`: Performance metrics.
  - `float _startTimeMs`: Job start time.

- **Method**: `void Execute(int index)`
  - Validates an entity and stores results in `ValidationResults` if valid.
  - Logs errors and tracks performance metrics.

#### `struct TaskCreationJob : IJobParallelFor` [BurstCompile]

Creates task descriptors for validated entities.

- **Fields**:
  - `NativeList<Guid> Entities` [ReadOnly]: Entities to process.
  - `NativeList<ValidationResultData> ValidationResults` [ReadOnly]: Validation results.
  - `Property Property` [ReadOnly]: Associated property.
  - `TaskValidatorContext Context` [ReadOnly]: Validation context.
  - `ITaskLogic TaskLogic`: Task logic for creation.
  - `NativeList<TaskDescriptor> ValidTasks`: Created tasks.
  - `NativeList<LogEntry> Logs`: Debug logs.
  - `JobService.JobResourceManager Resources`: Manages job resources.
  - `NativeArray<PerformanceMetric> Metrics`: Performance metrics.
  - `float _startTimeMs`: Job start time.

- **Method**: `void Execute(int index)`
  - Creates a task descriptor for a validated entity and adds it to `ValidTasks`.
  - Logs results and tracks performance metrics.

### TaskService

Manages task assignment, validation, and queuing for a property.

#### `class TaskService`

- **Constructor**: `TaskService(Property property)`
  - Initializes the service for a property, creating task queues for each `EmployeeTypes`.

- **Fields**:
  - `ConcurrentDictionary<Guid, ConcurrentQueue<(ITask, TaskDescriptor)>> _employeeSpecificTasks`: Employee-specific task queues.
  - `ConcurrentDictionary<EmployeeTypes, ConcurrentQueue<(ITask, TaskDescriptor)>> _taskQueues`: Task queues by employee type.
  - `ConcurrentDictionary<string, bool> _activeTasks`: Tracks active tasks.
  - `ConcurrentQueue<(Property, int)> _pendingValidations`: Pending validation requests.
  - `ConcurrentDictionary<string, float> _lastValidationTimes`: Last validation times by property.
  - `SemaphoreSlim _validationSemaphore`: Limits concurrent validations.
  - `const float VALIDATION_INTERVAL`: Base validation interval (5 seconds).

- **Methods**:
  - `async Task CompleteTaskAsync(TaskDescriptor task, Employee employee, bool success, string failureReason = null)`
    - Marks a task as completed and notifies the employee’s behavior.
    - **Parameters**:
      - `task`: Completed task.
      - `employee`: Employee who completed the task.
      - `success`: Task success status.
      - `failureReason`: Reason for failure (optional).
  - `void Activate()`
    - Activates the service, subscribing to server tick events.
  - `void Deactivate()`
    - Deactivates the service, clearing queues and unsubscribing from events.
  - `void SubmitPriorityTask(Employee employee, TaskDescriptor task, bool isEmployeeInitiated = false)`
    - Submits a priority task for an employee.
    - **Parameters**:
      - `employee`: Employee submitting the task.
      - `task`: Task descriptor.
      - `isEmployeeInitiated`: Indicates if the task is employee-initiated.
  - `void EnqueueTask(ITask iTask, TaskDescriptor task)`
    - Enqueues a task to an employee-specific or employee-type queue.
    - **Parameters**:
      - `iTask`: Task implementation.
      - `task`: Task descriptor.
  - `void EnqueueValidation(int priority = 1)`
    - Enqueues a validation request for the property.
    - **Parameters**:
      - `priority`: Validation priority.
  - `async Task EnqueueFollowUpTasksAsync(TaskDescriptor task, Employee employee)`
    - Enqueues follow-up tasks for an employee based on entity state.
    - **Parameters**:
      - `task`: Original task.
      - `employee`: Employee for follow-up tasks.
  - `async Task<(bool Success, TaskDescriptor Task, ITask ITask, string Error)> TryGetTaskAsync(Employee employee)`
    - Retrieves a valid task for an employee.
    - **Parameters**:
      - `employee`: Employee requesting a task.
    - **Returns**: Tuple with success status, task descriptor, task implementation, and error message (if any).
  - `void Dispose()`
    - Disposes the service, unsubscribing from events.
  - `void Cleanup()`
    - Cleans up resources and deactivates the service.

### TaskServiceManager

Manages `TaskService` instances for properties.

#### `static class TaskServiceManager`

- **Fields**:
  - `ConcurrentDictionary<Property, TaskService> _services`: Task services by property.

- **Methods**:
  - `void Initialize()`
    - Initializes the manager.
  - `TaskService GetOrCreateService(Property property)`
    - Gets or creates a task service for a property.
    - **Parameters**:
      - `property`: Target property.
    - **Returns**: `TaskService` instance.
  - `void ActivateProperty(Property property)`
    - Activates the task service for a property.
    - **Parameters**:
      - `property`: Target property.
  - `void DeactivateProperty(Property property)`
    - Deactivates the task service for a property.
    - **Parameters**:
      - `property`: Target property.
  - `void UpdatePropertyPriority(Property property, int priority)`
    - Updates the priority for a property.
    - **Parameters**:
      - `property`: Target property.
      - `priority`: New priority.
  - `async Task<(bool Success, TaskDescriptor Task, ITask ITask, string Error)> TryGetTask(Employee employee)`
    - Retrieves a task for an employee from their assigned property’s service.
    - **Parameters**:
      - `employee`: Employee requesting a task.
    - **Returns**: Tuple with success status, task descriptor, task implementation, and error message.
  - `void Cleanup()`
    - Cleans up all task services.

### EntityProvider

Resolves entities for tasks.

#### `static class EntityProvider`

- **Methods**:
  - `object ResolveEntity(Property property, Guid guid, TransitTypes expectedType)`
    - Resolves an entity by GUID and type.
    - **Parameters**:
      - `property`: Associated property.
      - `guid`: Entity identifier.
      - `expectedType`: Expected transit type.
    - **Returns**: Resolved entity or `null` if not found.
  - `bool IsValidTransitType(object entity, TransitTypes type)`
    - Checks if an entity matches the expected transit type.
    - **Parameters**:
      - `entity`: Entity to check.
      - `type`: Expected transit type.
    - **Returns**: `true` if valid, `false` otherwise.

### TaskRegistry

Manages task registrations.

#### `static class TaskRegistry`

- **Fields**:
  - `List<ITask> _tasks`: Registered tasks.

- **Properties**:
  - `IEnumerable<ITask> AllTasks`: All registered tasks.

- **Methods**:
  - `void Register(ITask task)`
    - Registers a task.
    - **Parameters**:
      - `task`: Task to register.
  - `ITask GetTask(TaskTypes type)`
    - Retrieves a task by type.
    - **Parameters**:
      - `type`: Task type.
    - **Returns**: Matching `ITask` or `null`.
  - `void Initialize()`
    - Initializes the registry, registering default tasks (e.g., `MixingStationTask`).

## Usage Notes

- **Burst Compilation**: Jobs and structs like `TaskValidationJob`, `TaskDescriptor`, and `ValidationResultData` are Burst-compiled for performance. Use Burst-compatible types (e.g., `NativeList`, `FixedString128Bytes`).
- **Task Queuing**: The `TaskService` manages tasks in employee-specific and employee-type queues, ensuring thread-safe operations with `ConcurrentQueue` and `ConcurrentDictionary`.
- **Validation and Creation**: Use `ValidatePropertyTasksAsync` for batch validation and task creation, leveraging Burst jobs for efficiency.
- **Logging**: Methods log verbose, info, warning, or error messages via `DebugLogger` under the `Tasks` category.
- **FishNet Integration**: The service aligns with FishNet’s `TimeManager` for networked operations, using coroutines and async methods for tick alignment.
- **Async Operations**: Methods like `TryGetTaskAsync` and `CompleteTaskAsync` are asynchronous to avoid blocking the main thread.

## Example Usage

### Submitting a Priority Task

```csharp
void SubmitTask(Employee employee, TaskDescriptor task)
{
    var taskService = TaskServiceManager.GetOrCreateService(employee.AssignedProperty);
    taskService.SubmitPriorityTask(employee, task, isEmployeeInitiated: true);
    Log($"Submitted priority task {task.TaskId} for {employee.fullName}");
}
```

### Retrieving a Task for an Employee

```csharp
async Task GetTaskForEmployee(Employee employee)
{
    var result = await TaskServiceManager.TryGetTask(employee);
    if (result.Success)
    {
        Log($"Assigned task {result.Task.TaskId} to {employee.fullName}");
    }
    else
    {
        Log($"No task for {employee.fullName}: {result.Error}");
    }
}
```

### Validating and Creating Tasks

```csharp
async Task ValidateTasks(Property property)
{
    var taskService = TaskServiceManager.GetOrCreateService(property);
    var validTasks = await taskService.ValidatePropertyTasksAsync(property);
    Log($"Validated {validTasks.Length} tasks for property {property.name}");
    validTasks.Dispose();
}
```
