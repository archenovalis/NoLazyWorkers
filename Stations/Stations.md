# API Documentation for NoLazyWorkers.Stations

This document describes the public API of the `NoLazyWorkers.Stations` namespace, which provides interfaces and utilities for managing station adapters and their states in a game or simulation environment. The system integrates with Unity, FishNet, and other `NoLazyWorkers` namespaces for networked operations, supporting station operations, item management, and state tracking. The `MixingStationAdapter` is a specific implementation for mixing stations, handling item mixing operations.

**Namespace**: `NoLazyWorkers.Stations`

**Current Date**: June 15, 2025

## Classes and Structs

### Extensions

Contains interfaces and classes for station adapters and state management.

#### `static class Extensions`

##### Interfaces

- `IStationAdapter`
  - Defines a station adapter for managing station operations and item slots.
  - **Properties**:
    - `Guid GUID`: Unique identifier for the station.
    - `string Name`: Station name.
    - `List<ItemSlot> InsertSlots`: Slots for input items.
    - `List<ItemSlot> ProductSlots`: Slots for product items.
    - `ItemSlot OutputSlot`: Slot for output items.
    - `bool IsInUse`: Indicates if the station is currently in use.
    - `bool HasActiveOperation`: Indicates if an operation is active.
    - `int StartThreshold`: Minimum input quantity required to start an operation.
    - `int MaxProductQuantity`: Maximum quantity the product slot can hold.
    - `ITransitEntity TransitEntity`: Transit entity associated with the station.
    - `BuildableItem Buildable`: Buildable item representation of the station.
    - `Property ParentProperty`: Property owning the station.
    - `Type TypeOf`: Type of the underlying station object.
    - `IStationState StationState`: Gets or sets the station’s state.
  - **Methods**:
    - `Vector3 GetAccessPoint(NPC npc)`: Gets the access point position for an NPC.
      - **Parameters**:
        - `npc`: Non-player character accessing the station.
      - **Returns**: Access point position.
    - `List<ItemField> GetInputItemForProduct()`: Gets the input items required for the product.
      - **Returns**: List of input item fields.
    - `void StartOperation(Employee employee)`: Starts a station operation.
      - **Parameters**:
        - `employee`: Employee initiating the operation.
    - `List<ItemInstance> RefillList()`: Gets the list of items needed for refilling the station.
      - **Returns**: List of item instances for refill.
    - `bool CanRefill(ItemInstance item)`: Checks if an item can be used to refill the station.
      - **Parameters**:
        - `item`: Item to check.
      - **Returns**: `true` if the item can refill, `false` otherwise.

- `IStationState`
  - Defines a station’s state and data storage.
  - **Properties**:
    - `Enum State`: Gets or sets the station’s state (non-generic).
    - `float LastValidatedTime`: Time of the last state validation.
  - **Methods**:
    - `bool IsValid(float currentTime)`: Checks if the state is valid based on the current time.
      - **Parameters**:
        - `currentTime`: Current time in seconds.
      - **Returns**: `true` if valid (within 5 seconds of last validation), `false` otherwise.
    - `void SetData<T>(string key, T value)`: Sets state data for a key.
      - **Parameters**:
        - `key`: Data key.
        - `value`: Data value.
    - `T GetData<T>(string key, T defaultValue = default)`: Gets state data for a key.
      - **Parameters**:
        - `key`: Data key.
        - `defaultValue`: Default value if key is not found.
      - **Returns**: Data value or default.

##### Classes

- `StationState<TStates> : IStationState where TStates : Enum`
  - Manages a station’s state with type-safe enum states.
  - **Properties**:
    - `TStates State`: Type-safe state of the station.
    - `float LastValidatedTime`: Time of the last state validation.
    - `Dictionary<string, object> StateData`: Key-value store for state data.
  - **Constructor**: `StationState(IStationAdapter adapter)`
    - Initializes the state and registers slots with `CacheManager`.
    - **Parameters**:
      - `adapter`: Station adapter.
  - **Methods**:
    - `bool IsValid(float currentTime)`: Checks if the state is valid (within 5 seconds).
      - **Parameters**:
        - `currentTime`: Current time in seconds.
      - **Returns**: `true` if valid, `false` otherwise.
    - `void SetData<T>(string key, T value)`: Sets state data.
      - **Parameters**:
        - `key`: Data key.
        - `value`: Data value.
    - `T GetData<T>(string key, T defaultValue = default)`: Gets state data.
      - **Parameters**:
        - `key`: Data key.
        - `defaultValue`: Default value.
      - **Returns**: Data value or default.

## Usage Notes

- **Dependencies**: Relies on `NoLazyWorkers.Storage` for `CacheManager`, `StorageKey`, and `SlotData`, and `ScheduleOne` namespaces for `MixingStation`, `Employee`, `Property`, and `ItemSlot`.
- **FishNet Integration**: Uses FishNet’s `TimeManager` indirectly via `CacheManager` for networked updates.
- **State Management**: `StationState<TStates>` ensures type-safe state transitions, with a 5-second validity window.
- **Caching**: Slots are registered with `CacheManager` during `StationState` initialization for efficient lookups.
- **Logging**: Uses `DebugLogger` with categories like `Storage` for error and debug logging.
- **Thread Safety**: `RouteManagers` and `IStations` are assumed to be thread-safe dictionaries, managed externally.

## Example Usage

### Mixingstation

- **Enums**:
  - `MixingStationStates`
    - Defines states for mixing stations.
    - **Values**:
      - `Idle`: No action needed.
      - `HasOutput`: Output slot contains items.
      - `CanStart`: Ready to start mixing operation.
      - `NeedsRestock`: Requires ingredient restock.
      - `InUse`: Station is currently in use.

#### Initializing a Mixing Station Adapter

```csharp
void SetupMixingStation(MixingStation station)
{
    var adapter = new MixingStationAdapter(station);
    Log($"Initialized adapter for station {adapter.Name} ({adapter.GUID})");
}
```

#### Checking Station State

```csharp
bool IsStationReady(IStationAdapter adapter)
{
    var state = adapter.StationState as StationState<MixingStationStates>;
    bool isValid = state.IsValid(Time.time);
    bool canStart = state.State == MixingStationStates.CanStart;
    Log($"Station {adapter.Name} is {(isValid && canStart ? "ready" : "not ready")}");
    return isValid && canStart;
}
```

#### Starting a Mixing Operation

```csharp
void StartMixing(IStationAdapter adapter, Chemist chemist)
{
    adapter.StartOperation(chemist);
    Log($"Started mixing operation for {chemist.fullName} at {adapter.Name}");
}
```

#### Getting Refill Items

```csharp
List<ItemInstance> GetRefills(IStationAdapter adapter)
{
    var refills = adapter.RefillList();
    Log($"Station {adapter.Name} needs {refills.Count} items for refill");
    return refills;
}
```
