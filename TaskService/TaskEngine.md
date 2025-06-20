# TaskEngine API Documentation

This documentation covers the core classes and utilities in the `NoLazyWorkers.TaskEngine` namespace for task management, validation, and execution in a warehouse simulation system using FishNet and Unity. The system handles employee tasks, inventory transfers, and station operations with a focus on performance optimization via Unity Jobs and Burst compilation.

## Namespace: NoLazyWorkers.TaskEngine

### Class: Utilities

Provides static utility methods for task initialization, execution, and validation.

#### Methods

- **InitializeTasks()**
  Initializes predefined task types.

  ```csharp
  Utilities.InitializeTasks()
  ```

- **TaskExecutionSwitch(TaskTypes taskType, TaskValidator taskValidator, string propertyName)**
  Executes task validation based on task type.

  ```csharp
  var validator = new TaskValidator { TaskType = TaskTypes.RefillStation };
  Utilities.TaskExecutionSwitch(TaskTypes.RefillStation, validator, "Warehouse1")
  ```

- **async Task<bool> ReValidateTask(Employee employee, StateData state, TaskTypes expectedTaskType)**
  Revalidates a task descriptor, ensuring pickup and dropoff slots are valid and reserved.

  ```csharp
  var employee = new Employee();
  var state = new StateData();
  bool isValid = await Utilities.ReValidateTask(employee, state, TaskTypes.RefillStation)
  ```

- **Property GetPropertyFromName(string name)**
  Retrieves a property by its name.

  ```csharp
  var property = Utilities.GetPropertyFromName("Warehouse1")
  ```

### Class: Extensions

Defines enums and structs for task management, including Burst-compiled jobs for validation.

#### Enums

- **TaskTypes**
  Defines task types for employee activities.
  Values: `RefillStation`, `EmptyLoadingDock`, `RestockSpecificShelf`, `DeliverInventory`, `RestockIngredients`, `OperateStation`, `HandleOutput`, `StartPackaging`, `DeliverOutput`

- **EmployeeTypes**
  Defines employee roles.
  Values: `Any`, `Chemist`, `Handler`, `Botanist`, `Driver`, `Cleaner`

- **NEQuality**
  Defines item quality levels.
  Values: `Trash`, `Poor`, `Standard`, `Premium`, `Heavenly`, `None`

- **TransitTypes**
  Defines entity types for task pickups and dropoffs.
  Values: `Inventory`, `LoadingDock`, `PlaceableStorageEntity`, `AnyStation`, `MixingStation`, `PackagingStation`, `BrickPress`, `LabOven`, `Pot`, `DryingRack`, `ChemistryStation`, `Cauldron`

#### Struct: TaskValidator

Validates tasks based on requirements and station availability.

- **Fields**
  - `FixedString32Bytes AssignedPropertyName`: Property name for the task.
  - `FixedString32Bytes TaskName`: Name of the task.
  - `NativeList<TaskDescriptor> ValidTasks`: List of valid tasks.
  - `NativeParallelHashSet<FixedString128Bytes> TaskKeys`: Unique task identifiers.
  - `float CurrentTime`: Current game time.
  - `StorageKey StationKey`: Key for the station.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationOutputSlots`: Output slots for stations.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StorageInputSlots`: Input slots for storages.
  - `NativeParallelHashMap<ItemKey, NativeList<StorageKey>> SpecificShelves`: Shelves for specific items.
  - `TaskTypes TaskType`: Type of task.
  - `EmployeeTypes RequiredEmployeeType`: Required employee type.
  - `bool RequiresPickup`: If pickup is required.
  - `bool RequiresDropoff`: If dropoff is required.
  - `TransitTypes PickupType`: Type of pickup entity.
  - `TransitTypes DropoffType`: Type of dropoff entity.

- **Methods**

  - **Execute()**: Executes task validation.

    ```csharp
    var validator = new TaskValidator { TaskType = TaskTypes.RefillStation };
    validator.Execute()
    ```

#### Struct: TaskDescriptor

Describes a task with pickup, dropoff, and item details.

- **Fields**
  - `FixedString32Bytes AssignedPropertyName`: Property name.
  - `TaskTypes Type`: Task type.
  - `EmployeeTypes EmployeeType`: Required employee type.
  - `int Priority`: Task priority (1=high, 3=low).
  - `Guid TaskId`: Unique task ID.
  - `Guid PickupGuid`: Pickup entity GUID.
  - `TransitTypes PickupType`: Pickup entity type.
  - `Guid DropoffGuid`: Dropoff entity GUID.
  - `TransitTypes DropoffType`: Dropoff entity type.
  - `ItemKey Item`: Item to transfer.
  - `int Quantity`: Quantity to transfer.
  - `int PickupSlotIndex1`, `PickupSlotIndex2`, `PickupSlotIndex3`: Pickup slot indices.
  - `int PickupSlotCount`: Number of pickup slots.
  - `int DropoffSlotIndex1`, `DropoffSlotIndex2`, `DropoffSlotIndex3`: Dropoff slot indices.
  - `int DropoffSlotCount`: Number of dropoff slots.
  - `float Timestamp`: Task creation time.
  - `FixedString128Bytes UniqueKey`: Unique task key.
  - `bool IsDisabled`: If task is disabled.

- **Methods**

  - **static TaskDescriptor Create(...)**: Creates a task descriptor.

    ```csharp
    var task = TaskDescriptor.Create(
        TaskTypes.RefillStation, Guid.NewGuid(), new ItemKey("Item1", "", NEQuality.Standard),
        new[] { 1 }, Guid.NewGuid(), new[] { 1 }, 10, TransitTypes.PlaceableStorageEntity,
        TransitTypes.AnyStation, EmployeeTypes.Handler, 1, "Warehouse1", Time.time)
    ```

  - **bool IsValid(Employee employee)**: Validates task for an employee.

    ```csharp
    var employee = new Employee();
    bool isValid = task.IsValid(employee)
    ```

  - **Dispose()**: Disposes resources (no-op for this struct).

#### Struct: SlotData

Represents an item slot's state.

- **Fields**
  - `int SlotIndex`: Slot index.
  - `ItemKey ItemKey`: Item identifier.
  - `int Quantity`: Item quantity.
  - `bool IsLocked`: If slot is locked.

#### Struct: StorageKey

Unique identifier for storage entities.

- **Fields**
  - `Guid Guid`: Entity GUID.

- **Methods**

  - **StorageKey(Guid guid)**: Constructor.

    ```csharp
    var key = new StorageKey(Guid.NewGuid())
    ```

  - **bool Equals(StorageKey other)**: Compares keys.
  - **override int GetHashCode()**: Generates hash code.

#### Struct: ItemKey

Unique identifier for items.

- **Fields**
  - `FixedString32Bytes Id`: Item ID.
  - `FixedString32Bytes PackagingId`: Packaging ID.
  - `NEQuality Quality`: Item quality.

- **Methods**

  - **ItemKey(ItemInstance item)**: Constructor from item instance.

    ```csharp
    var item = new ItemInstance { ID = "Item1" };
    var key = new ItemKey(item)
    ```

  - **ItemKey(string id, string packagingId, NEQuality? quality)**: Constructor with parameters.

    ```csharp
    var key = new ItemKey("Item1", "Pack1", NEQuality.Standard)
    ```

  - **bool Equals(ItemKey other)**: Compares keys.
  - **override int GetHashCode()**: Generates hash code.

#### Struct: TaskValidationJob

Parallel job for validating tasks.

- **Fields**
  - `NativeArray<StorageKey> Stations`: Stations to validate.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationOutputSlots`: Station output slots.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StorageInputSlots`: Storage input slots.
  - `NativeParallelHashMap<ItemKey, NativeList<StorageKey>> SpecificShelves`: Specific shelves.
  - `NativeList<TaskValidator> Validators`: Task validators.
  - `NativeList<TaskDescriptor> ValidTasks`: Valid tasks.
  - `NativeParallelHashSet<FixedString128Bytes> TaskKeys`: Task keys.
  - `float CurrentTime`: Current time.
  - `FixedString32Bytes AssignedPropertyName`: Property name.
  - `NativeList<TaskDescriptor> TasksToRevalidate`: Tasks to revalidate.

- **Methods**

  - **Execute(int index)**: Executes validation for a station.

    ```csharp
    var job = new TaskValidationJob { /*Initialize fields*/ };
    job.Execute(0)
    ```

#### Struct: TaskRevalidationJob

Parallel job for revalidating tasks.

- **Fields**
  - `NativeArray<TaskDescriptor> Tasks`: Tasks to revalidate.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationOutputSlots`: Station output slots.
  - `NativeParallelHashMap<StorageKey, NativeList<SlotData>> StorageInputSlots`: Storage input slots.
  - `NativeParallelHashMap<ItemKey, NativeList<StorageKey>> SpecificShelves`: Specific shelves.
  - `NativeList<TaskDescriptor> ValidTasks`: Valid tasks.
  - `EmployeeTypes EmployeeType`: Employee type.
  - `FixedString32Bytes PropertyName`: Property name.

- **Methods**

  - **Execute(int index)**: Revalidates a task.

    ```csharp
    var job = new TaskRevalidationJob { /*Initialize fields*/ };
    job.Execute(0)
    ```

### Class: TaskValidationManager

Manages task validation and caching.

#### Fields

- **ConcurrentDictionary<StorageKey, NativeList<TaskDescriptor>> _validTasksCache**: Cache of valid tasks.
- **ConcurrentDictionary<StorageKey, JobHandle> _jobHandles**: Active job handles.
- **ConcurrentDictionary<Guid, TaskDescriptor> _revalidatedSingleTasks**: Revalidated tasks.

#### Methods

- **Initialize()**: Initializes manager and subscribes to ticks.

  ```csharp
  TaskValidationManager.Initialize()
  ```

- **Cleanup()**: Disposes resources.

  ```csharp
  TaskValidationManager.Cleanup()
  ```

- **RegisterValidator(TaskValidator validator)**: Registers a validator.

  ```csharp
  var validator = new TaskValidator { TaskType = TaskTypes.RefillStation };
  TaskValidationManager.RegisterValidator(validator)
  ```

- **QueueValidation(Property property)**: Queues property for validation.

  ```csharp
  var property = new Property();
  TaskValidationManager.QueueValidation(property)
  ```

- **bool TryGetValidatedTasks(Property property, out NativeList<TaskDescriptor> tasks)**: Retrieves validated tasks.

  ```csharp
  var property = new Property();
  if (TaskValidationManager.TryGetValidatedTasks(property, out var tasks))
  {
      // Process tasks
  }
  ```

- **bool FindShelfForItem(ItemKey itemKey, ..., out Guid shelfGuid, out int slotIndex)**: Finds a shelf for an item.

  ```csharp
  var itemKey = new ItemKey("Item1", "", NEQuality.Standard);
  if (TaskValidationManager.FindShelfForItem(itemKey, specificShelves, storageInputSlots, out var shelfGuid, out var slotIndex))
  {
      // Use shelfGuid and slotIndex
  }
  ```

- **bool FindShelfWithItem(ItemKey itemKey, ..., out Guid shelfGuid, out int slotIndex)**: Finds a shelf with an item.

  ```csharp
  var itemKey = new ItemKey("Item1", "", NEQuality.Standard);
  if (TaskValidationManager.FindShelfWithItem(itemKey, stationOutputSlots, out var shelfGuid, out var slotIndex))
  {
      // Use shelfGuid and slotIndex
  }
  ```

- **bool TryRevalidateSingleTask(TaskDescriptor task, Employee employee, out TaskDescriptor revalidatedTask)**: Revalidates a single task.

  ```csharp
  var task = new TaskDescriptor();
  var employee = new Employee();
  if (TaskValidationManager.TryRevalidateSingleTask(task, employee, out var revalidatedTask))
  {
      // Use revalidatedTask
  }
  ```

### Class: TaskManager

Manages task queuing and execution.

#### Fields

- **ConcurrentDictionary<(string EmployeeType, Property Property), ConcurrentQueue<TaskDescriptor>> _taskQueues**: Task queues.
- **ConcurrentDictionary<FixedString128Bytes, TaskDescriptor> _disabledTasks**: Disabled tasks.
- **ConcurrentDictionary<Property, NativeArray<StorageKey>> _nativeStationsCache**: Cached stations.
- **ConcurrentDictionary<Property, NativeParallelHashMap<StorageKey, NativeList<SlotData>>> _nativeStationOutputSlotsCache**: Cached output slots.
- **ConcurrentDictionary<Property, NativeParallelHashMap<StorageKey, NativeList<SlotData>>> _nativeStorageInputSlotsCache**: Cached input slots.
- **ConcurrentDictionary<Property, NativeParallelHashMap<ItemKey, NativeList<StorageKey>>> _nativeSpecificShelvesCache**: Cached shelves.

#### Methods

- **Initialize()**: Initializes manager and subscribes to ticks.

  ```csharp
  TaskManager.Initialize()
  ```

- **ActivateProperty(Property property)**: Activates a property for task management.

  ```csharp
  var property = new Property();
  TaskManager.ActivateProperty(property)
  ```

- **QueueUpdate(Property property)**: Queues property for task update.

  ```csharp
  var property = new Property();
  TaskManager.QueueUpdate(property)
  ```

- **CleanupProperty(Property property)**: Cleans up property tasks.

  ```csharp
  var property = new Property();
  TaskManager.CleanupProperty(property)
  ```

- **Cleanup()**: Disposes resources.

  ```csharp
  TaskManager.Cleanup()
  ```

- **bool IsTaskRequirementsMet(TaskDescriptor task, Property property)**: Checks if task requirements are met.

  ```csharp
  var task = new TaskDescriptor();
  var property = new Property();
  bool met = TaskManager.IsTaskRequirementsMet(task, property)
  ```

- **TaskValidator? GetValidatorForTaskType(TaskTypes taskType)**: Retrieves validator for task type.

  ```csharp
  var validator = TaskManager.GetValidatorForTaskType(TaskTypes.RefillStation)
  ```

- **EnqueueTask(TaskDescriptor task)**: Enqueues a task.

  ```csharp
  var task = new TaskDescriptor();
  TaskManager.EnqueueTask(task)
  ```

- **bool TryGetTask(Employee employee, string employeeType, Property property, out TaskDescriptor task)**: Retrieves a task for an employee.

  ```csharp
  var employee = new Employee();
  var property = new Property();
  if (TaskManager.TryGetTask(employee, "Handler", property, out var task))
  {
      // Use task
  }
  ```
