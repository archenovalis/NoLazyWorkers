# MixingStationTask API Documentation

This documentation details the `NoLazyWorkers.TaskService.StationTasks` namespace, specifically the `MixingStationTask` class and related components for managing mixing station tasks in a warehouse simulation using FishNet, Unity, and Burst compilation. The system supports task validation, state management, and action execution for chemists operating mixing stations.

## Namespace: NoLazyWorkers.TaskService.StationTasks

### Class: MixingStationTask

Implements `ITask` and `IBurstTask` interfaces to manage tasks for mixing stations, handling validation, task creation, and action execution.

#### Interfaces

- **ITask**: Defines task-related methods for validation and action selection.
- **IBurstTask**: Supports Burst-compiled task validation and creation.

#### Enum: States

Defines possible states for a mixing station.  
Values: `Invalid`, `NeedsRestock`, `ReadyToOperate`, `HasOutput`, `NeedsDelivery`, `Idle`

#### Struct: MixingStationValidationResultData

Holds validation data for a mixing station, optimized for Burst compilation.

- **Fields**
  - `bool IsValid`: If the station state is valid.
  - `int ActionId`: Identifier for the selected action.
  - `int State`: Station state (e.g., 0=Idle, 1=NeedsRestock).
  - `ItemKey RestockItem`: Item needed for restocking.
  - `int RestockQuantity`: Quantity needed for restocking.
  - `Guid RestockShelfGuid`: GUID of the restock shelf.
  - `NativeList<int> RestockPickupSlotIndices`: Indices of pickup slots for restocking.
  - `ItemKey OutputItem`: Output item produced by the station.
  - `int OutputQuantity`: Quantity of output item.
  - `Guid DeliveryDestinationGuid`: GUID of the delivery destination.
  - `NativeList<int> DeliverySlotIndices`: Indices of delivery slots.
  - `int DeliveryCapacity`: Total delivery slot capacity.
  - `NativeList<int> LoopSlotIndices`: Indices of slots for looping output to product slots.

#### Class: MixingStationValidationResult

Extends `ValidationResult` and implements `IValidationResultData` to store validation results.

- **Properties**
  - `MixingStationValidationResultData Data`: Validation data.
  - `int ActionId`: Action identifier.
  - `int State`: Station state.
  - `ItemKey RestockItem`: Restock item.
  - `int RestockQuantity`: Restock quantity.
  - `Guid RestockShelfGuid`: Restock shelf GUID.
  - `ItemKey OutputItem`: Output item.
  - `int OutputQuantity`: Output quantity.
  - `Guid DeliveryDestinationGuid`: Delivery destination GUID.
  - `int DeliveryCapacity`: Delivery capacity.
  - `NativeList<int> DeliverySlotIndices`: Delivery slot indices.
  - `NativeList<int> LoopSlotIndices`: Loop slot indices.
  - `ItemInstance RestockItem`: Restock item instance.
  - `Guid RestockShelfGuid`: Restock shelf GUID (instance property).
  - `ItemInstance OutputItem`: Output item instance.
  - `Guid DeliveryDestinationGuid`: Delivery destination GUID (instance property).

#### Properties

- **ITaskDefinition TaskDefinition**: Returns a `MixingStationTaskDefinition`.  

  ```csharp
  var task = new MixingStationTask();
  var definition = task.TaskDefinition
  ```

- **IEntitySelector EntitySelector**: Returns a `MixingStationEntitySelector`.  

  ```csharp
  var selector = task.EntitySelector
  ```

#### Methods

- **async Task<ValidationResult> ValidateEntityState(object entity)**  
  Validates the state of a mixing station entity asynchronously.  

  ```csharp
  var task = new MixingStationTask();
  var station = EntityProvider.ResolveEntity(property, stationGuid) as IStationAdapter;
  var result = await task.ValidateEntityState(station)
  ```

- **(ITaskAction Action, int ActionId) ActionSelection()**  
  Selects an action based on the current validation result state.  

  ```csharp
  var task = new MixingStationTask();
  var (action, actionId) = task.ActionSelection()
  ```

- **ITaskAction GetActionFromId(int actionId)**  
  Retrieves an action instance by its ID.  

  ```csharp
  var task = new MixingStationTask();
  var action = task.GetActionFromId(0); // Restock action
  ```

- **TaskDescriptor CreateTaskForState(object entity, Property property)**  
  Creates a task descriptor for the current station state.  

  ```csharp
  var task = new MixingStationTask();
  var station = EntityProvider.ResolveEntity(property, stationGuid) as IStationAdapter;
  var taskDescriptor = task.CreateTaskForState(station, property)
  ```

- **ValidationResultData ValidateEntityState(object entity, TaskValidatorContext context, JobResourceManager resources, NativeList<LogEntry> logs)**  
  Validates station state synchronously, typically for Burst-compiled jobs.  

  ```csharp
  var task = new MixingStationTask();
  var resources = new JobResourceManager(Allocator.Temp);
  var logs = new NativeList<LogEntry>(Allocator.Temp);
  var result = task.ValidateEntityState(station, new TaskValidatorContext(), resources, logs)
  ```

- **TaskDescriptor CreateTaskForState(object entity, Property property, ValidationResultData result, TaskValidatorContext context, JobResourceManager resources, NativeList<LogEntry> logs)**  
  Creates a task descriptor synchronously for a given validation result.  

  ```csharp
  var task = new MixingStationTask();
  var resources = new JobResourceManager(Allocator.Temp);
  var logs = new NativeList<LogEntry>(Allocator.Temp);
  var taskDescriptor = task.CreateTaskForState(station, property, validationResult, new TaskValidatorContext(), resources, logs)
  ```

### Class: MixingStationTaskDefinition

Implements `ITaskDefinition` to define mixing station task properties and creation logic.

- **Properties**
  - `TaskTypes Type`: Returns `TaskTypes.MixingStation`.
  - `int Priority`: Returns 10.
  - `IEntitySelector EntitySelector`: Returns a `MixingStationEntitySelector`.
  - `TaskTypes FollowUpTask`: Returns `TaskTypes.MixingStation`.

- **Methods**

  - **void CreateTasks(Guid entityGuid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)**  
    Creates tasks for a mixing station entity, validated using Burst compilation.  

    ```csharp
    var definition = new MixingStationTaskDefinition();
    var validTasks = new NativeList<TaskDescriptor>(Allocator.Temp);
    definition.CreateTasks(stationGuid, new TaskValidatorContext(), property, validTasks)
    ```

### Class: MixingStationEntitySelector

Implements `IEntitySelector` to select mixing station entities.

- **Methods**

  - **NativeList<Guid> SelectEntities(Property property, Allocator allocator)**  
    Selects GUIDs of mixing stations (including Mk2 variants) for a property.  

    ```csharp
    var selector = new MixingStationEntitySelector();
    var entities = selector.SelectEntities(property, Allocator.Temp)
    ```

### Static Class: MixingStationLogic

Contains Burst-compiled logic for validating station states and creating tasks.

- **Methods**

  - **ValidationResultData ValidateEntityState(IStationAdapter station, TaskValidatorContext context, JobResourceManager resources, NativeList<LogEntry> logs)**  
    Validates the state of a mixing station, determining if it needs restocking, is ready to operate, has output, or needs delivery.  

    ```csharp
    var resources = new JobResourceManager(Allocator.Temp);
    var logs = new NativeList<LogEntry>(Allocator.Temp);
    var result = MixingStationTask.MixingStationLogic.ValidateEntityState(station, new TaskValidatorContext(), resources, logs)
    ```

  - **TaskDescriptor CreateTask(IStationAdapter station, Property property, ValidationResultData result, TaskValidatorContext context, JobResourceManager resources, NativeList<LogEntry> logs)**  
    Creates a task descriptor based on the validation result and station state.  

    ```csharp
    var resources = new JobResourceManager(Allocator.Temp);
    var logs = new NativeList<LogEntry>(Allocator.Temp);
    var task = MixingStationTask.MixingStationLogic.CreateTask(station, property, validationResult, new TaskValidatorContext(), resources, logs)
    ```

### Static Class: Actions

Contains action classes for executing specific task steps.

#### Class: Restock

Implements `ITaskAction` for restocking a mixing station.

- **Methods**

  - **async Task ValidateStateAsync(Chemist chemist, IStationAdapter station)**  
    Validates if the station requires restocking.  

    ```csharp
    var restock = new MixingStationTask.Actions.Restock();
    await restock.ValidateStateAsync(chemist, station)
    ```

  - **async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)**  
    Executes restocking by transferring items from a shelf to the station, followed by an operation if successful.  

    ```csharp
    var restock = new MixingStationTask.Actions.Restock();
    await restock.ExecuteAsync(chemist, state, taskDescriptor)
    ```

#### Class: Operate

Implements `ITaskAction` for operating a mixing station.

- **Constants**
  - `float OPERATION_TIMEOUT_SECONDS`: Timeout duration (30 seconds).

- **Methods**

  - **async Task ValidateStateAsync(Chemist chemist, IStationAdapter station)**  
    Validates if the station is ready to operate.  

    ```csharp
    var operate = new MixingStationTask.Actions.Operate();
    await operate.ValidateStateAsync(chemist, station)
    ```

  - **async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)**  
    Starts the mixing operation, monitoring for completion or timeout.  

    ```csharp
    var operate = new MixingStationTask.Actions.Operate();
    await operate.ExecuteAsync(chemist, state, taskDescriptor)
    ```

#### Class: Loop

Implements `ITaskAction` for looping output to product slots.

- **Methods**

  - **async Task ValidateStateAsync(Chemist chemist, IStationAdapter station)**  
    Validates if the station has output to loop.  

    ```csharp
    var loop = new MixingStationTask.Actions.Loop();
    await loop.ValidateStateAsync(chemist, station)
    ```

  - **async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)**  
    Transfers output from the station’s output slot to its product slots, scheduling a follow-up task.  

    ```csharp
    var loop = new MixingStationTask.Actions.Loop();
    await loop.ExecuteAsync(chemist, state, taskDescriptor)
    ```

#### Class: Deliver

Implements `ITaskAction` for delivering output to a destination.

- **Methods**

  - **async Task ValidateStateAsync(Chemist chemist, IStationAdapter station)**  
    Validates if the station’s output needs delivery.  

    ```csharp
    var deliver = new MixingStationTask.Actions.Deliver();
    await deliver.ValidateStateAsync(chemist, station)
    ```

  - **async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)**  
    Transfers output from the station to a packaging station or storage entity.  

    ```csharp
    var deliver = new MixingStationTask.Actions.Deliver();
    await deliver.ExecuteAsync(chemist, state, taskDescriptor)
    ```
