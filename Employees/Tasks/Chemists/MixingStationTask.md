# MixingStationTask API Documentation

This documentation describes the `NoLazyWorkers.Employees.Tasks.Chemists` namespace, focusing on the `MixingStationTaskUtilities` and `MixingStationTask` classes for managing chemist tasks at mixing stations in a warehouse simulation using FishNet and Unity. The system handles station validation, ingredient restocking, operation execution, and output delivery with detailed logging for debugging.

## Namespace: NoLazyWorkers.Employees.Tasks.Chemists

### Class: MixingStationTaskUtilities

Provides utilities for validating mixing station states.

#### Class: ValidationResult

Holds the result of station state validation.

- **Properties**
  - `bool IsValid`: Indicates if the station state is valid for task execution.
  - `bool CanStart`: Indicates if the mixing operation can start.
  - `bool CanRestock`: Indicates if the station needs restocking.
  - `bool HasOutput`: Indicates if the station has output to process.
  - `ItemInstance RestockItem`: The item required for restocking.
  - `int RestockQuantity`: The quantity needed for restocking.
  - `ITransitEntity RestockShelf`: The storage entity for restocking items.
  - `List<ItemSlot> RestockPickupSlots`: Slots containing items for restocking.

#### Methods

- **async Task<ValidationResult> ValidateStationState(Chemist chemist, IStationAdapter stationAdapter)**  
  Validates the state of a mixing station for a chemist. Checks station availability, output presence, and restock needs.  

  ```csharp
  var chemist = new Chemist();
  var stationAdapter = IStations[property][stationGuid];
  var result = await MixingStationTaskUtilities.ValidateStationState(chemist, stationAdapter)
  ```

### Class: MixingStationTask

Manages task workflows for mixing stations, defining steps and logic for execution.

#### Enum: MixingStationSteps

Defines the steps in a mixing station task workflow.  
Values: `CheckStationState`, `RestockIngredients`, `OperateStation`, `HandleOutput`, `DeliverProduct`, `End`, `OnComplete`

#### Constants

- **float OPERATION_TIMEOUT_SECONDS**: Timeout duration for station operations (30 seconds).

#### Methods

- **EmployeeTask<MixingStationSteps> Create(Chemist chemist, int priority, int index)**  
  Creates a type-safe mixing station task with predefined work steps.  

  ```csharp
  var chemist = new Chemist();
  var task = MixingStationTask.Create(chemist, priority: 1, index: 0)
  ```

#### Nested Class: Logic

Contains static methods for validating and executing each task step.

##### Methods

- **async Task<bool> ValidateCheckStationState(Employee employee, StateData state)**  
  Validates if a mixing station is available and suitable for task execution.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  bool isValid = await MixingStationTask.Logic.ValidateCheckStationState(chemist, state)
  ```

- **async Task ExecuteCheckStationState(Employee employee, StateData state)**  
  Determines the next task step based on the station’s validation result (restock, start operation, handle output, or end).  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  await MixingStationTask.Logic.ExecuteCheckStationState(chemist, state)
  ```

- **async Task<bool> ValidateRestockIngredients(Employee employee, StateData state)**  
  Validates if restocking is possible based on shelf availability and pickup slots.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  bool isValid = await MixingStationTask.Logic.ValidateRestockIngredients(chemist, state)
  ```

- **async Task ExecuteRestockIngredients(Employee employee, StateData state)**  
  Executes restocking by initiating item transfer from a shelf to the station.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  await MixingStationTask.Logic.ExecuteRestockIngredients(chemist, state)
  ```

- **async Task<bool> ValidateOperateStation(Employee employee, StateData state)**  
  Validates if the station is ready to start a mixing operation.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  bool isValid = await MixingStationTask.Logic.ValidateOperateStation(chemist, state)
  ```

- **async Task ExecuteOperateStation(Employee employee, StateData state)**  
  Starts the mixing operation and monitors its completion or timeout.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  await MixingStationTask.Logic.ExecuteOperateStation(chemist, state)
  ```

- **async Task<bool> ValidateHandleOutput(Employee employee, StateData state)**  
  Validates if the station has output to process.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  bool isValid = await MixingStationTask.Logic.ValidateHandleOutput(chemist, state)
  ```

- **async Task ExecuteHandleOutput(Employee employee, StateData state)**  
  Processes station output by transferring it to product slots or initiating delivery.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  await MixingStationTask.Logic.ExecuteHandleOutput(chemist, state)
  ```

- **async Task<bool> ValidateDeliverProduct(Employee employee, StateData state)**  
  Validates if the output can be delivered to a destination.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  bool isValid = await MixingStationTask.Logic.ValidateDeliverProduct(chemist, state)
  ```

- **async Task ExecuteDeliverProduct(Employee employee, StateData state)**  
  Delivers the station’s output to a packaging station or storage entity.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  await MixingStationTask.Logic.ExecuteDeliverProduct(chemist, state)
  ```

- **async Task ExecuteEnd(Employee employee, StateData state)**  
  Cleans up task resources and disables the employee’s behavior.  

  ```csharp
  var chemist = new Chemist();
  var state = EmployeeBehaviour.GetState(chemist);
  await MixingStationTask.Logic.ExecuteEnd(chemist, state)
  ```
