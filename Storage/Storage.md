# StorageManager API Documentation

**Date:** June 30, 2025

The `StorageManager` class provides a comprehensive API for managing storage operations in a networked environment, including initialization, cleanup, and slot operations for items. This documentation outlines how to utilize the public methods and structures effectively.

## Classes and Structs

### StorageManager

A static class responsible for managing storage operations, including initialization, cleanup, and slot operations for items.

#### Properties

- **IsInitialized** (`bool`, read-only): Indicates whether the `StorageManager` is initialized.

#### Methods

- **Initialize()**
  Initializes the `StorageManager` and sets up necessary resources. Must be called on the server before performing storage operations.

  ```csharp
  StorageManager.Initialize()
  ```

- **Cleanup()**
  Cleans up resources used by the `StorageManager`. Call this to release resources when no longer needed.

  ```csharp
  StorageManager.Cleanup()
  ```

- **ClearEntityCache(Property property, Guid guid)**
  Clears the cache for a specific entity identified by its GUID within a property.
  - **Parameters**:
    - `property`: The `Property` containing the entity.
    - `guid`: The `Guid` of the entity to clear from cache.

  ```csharp
  StorageManager.ClearEntityCache(property, entityGuid);
  ```

- **ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)**
  Reserves a slot for an item with a specified locker and reason.
  - **Parameters**:
    - `entityGuid`: The `Guid` of the entity containing the slot.
    - `slot`: The `ItemSlot` to reserve.
    - `locker`: The `NetworkObject` locking the slot.
    - `lockReason`: The reason for locking the slot.
    - `item`: Optional `ItemInstance` to reserve.
    - `quantity`: Optional quantity to reserve.
  - **Returns**: `bool` indicating success.

  ```csharp
  bool reserved = StorageManager.ReserveSlot(entityGuid, slot, locker, "Reservation for processing", item, 10);
  ```

- **ReleaseSlot(ItemSlot slot)**
  Releases a previously reserved slot.
  - **Parameters**:
    - `slot`: The `ItemSlot` to release.

  ```csharp
  StorageManager.ReleaseSlot(slot);
  ```

- **FindStorageWithItem(Property property, ItemInstance item, int needed, bool allowTargetHigherQuality = false)**
  Finds storage containing a specific item with the required quantity. Yields a `StorageResult`.
  - **Parameters**:
    - `property`: The `Property` to search within.
    - `item`: The `ItemInstance` to find.
    - `needed`: The quantity needed.
    - `allowTargetHigherQuality`: Whether to allow items of higher quality.
  - **Yields**: `StorageResult` with found storage details.

  ```csharp
  IEnumerator routine = StorageManager.FindStorageWithItem(property, item, 5, true);
  while (routine.MoveNext())
  {
      if (routine.Current is StorageResult result)
      {
          // Process result
      }
  }
  ```

- **UpdateStorageCache(Property property, Guid entityGuid, List<ItemSlot> slots, StorageType storageType)**
  Updates the storage cache for a specific entity and its slots.
  - **Parameters**:
    - `property`: The `Property` to update the cache for.
    - `entityGuid`: The `Guid` of the entity.
    - `slots`: The list of `ItemSlot` to update.
    - `storageType`: The `StorageType` of the storage.
  - **Yields**: Completes asynchronously.

  ```csharp
  IEnumerator routine = StorageManager.UpdateStorageCache(property, entityGuid, slots, StorageType.AnyShelf);
  while (routine.MoveNext()) { }
  ```

- **FindDeliveryDestination(Property property, ItemInstance item, int quantity, Guid sourceGuid)**
  Finds delivery destinations for an item with the specified quantity. Yields a list of `DeliveryDestination`.
  - **Parameters**:
    - `property`: The `Property` to search within.
    - `item`: The `ItemInstance` to deliver.
    - `quantity`: The quantity to deliver.
    - `sourceGuid`: The `Guid` of the source entity.
  - **Yields**: `List<DeliveryDestination>` with destination details.

  ```csharp
  IEnumerator routine = StorageManager.FindDeliveryDestination(property, item, 10, sourceGuid);
  while (routine.MoveNext())
  {
      if (routine.Current is List<DeliveryDestination> destinations)
      {
          // Process destinations
      }
  }
  ```

- **FindAvailableSlots(Property property, Guid entityGuid, List<ItemSlot> slots, ItemInstance item, int quantity)**
  Finds available slots for storing an item with the specified quantity. Yields a list of tuples containing slots and capacities.
  - **Parameters**:
    - `property`: The `Property` to search within.
    - `entityGuid`: The `Guid` of the entity containing the slots.
    - `slots`: The list of `ItemSlot` to check.
    - `item`: The `ItemInstance` to store.
    - `quantity`: The quantity to store.
  - **Yields**: `List<(ItemSlot, int)>` with available slots and their capacities.

  ```csharp
  IEnumerator routine = StorageManager.FindAvailableSlots(property, entityGuid, slots, item, 5);
  while (routine.MoveNext())
  {
      if (routine.Current is List<(ItemSlot, int)> availableSlots)
      {
          // Process available slots
      }
  }
  ```

- **ExecuteSlotOperations(Property property, List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations)**
  Executes a list of slot operations (insert or remove) for items. Yields a list of boolean results indicating success.
  - **Parameters**:
    - `property`: The `Property` to perform operations within.
    - `operations`: The list of operations to execute, each containing entity GUID, slot, item, quantity, insert/remove flag, locker, and lock reason.
  - **Yields**: `List<bool>` indicating success for each operation.

  ```csharp
  var operations = new List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)>
  {
      (entityGuid, slot, item, 5, true, locker, "Insert operation")
  };
  IEnumerator routine = StorageManager.ExecuteSlotOperations(property, operations);
  while (routine.MoveNext())
  {
      if (routine.Current is List<bool> results)
      {
          // Process results
      }
  }
  ```

### Extensions.SlotOperation

A struct representing a slot operation with details about the entity, slot, item, and locking information.

- **Fields**:
  - `EntityGuid`: `Guid` of the entity.
  - `SlotKey`: `SlotKey` identifying the slot.
  - `Slot`: `ItemSlot` for the operation.
  - `Item`: `ItemInstance` involved.
  - `Quantity`: Quantity of the item.
  - `IsInsert`: Indicates if the operation is an insert (`true`) or remove (`false`).
  - `Locker`: `NetworkObject` locking the slot.
  - `LockReason`: Reason for locking.

### Extensions.SlotResult

A struct representing the result of checking a slot's availability.

- **Fields**:
  - `SlotIndex`: Index of the slot.
  - `Capacity`: Available capacity in the slot.

### Extensions.SlotReservation

A struct representing a reservation for a slot.

- **Fields**:
  - `EntityGuid`: `Guid` of the entity.
  - `Timestamp`: Time of reservation.
  - `Locker`: Name of the locker.
  - `LockReason`: Reason for locking.
  - `Item`: `ItemData` of the reserved item.
  - `Quantity`: Reserved quantity.

### Extensions.StationData

A struct representing station data optimized for Burst compilation.

- **Fields**:
  - `GUID`: `Guid` of the station.
  - `PropertyId`: ID of the parent property.
  - `InsertSlots`: List of insert slots.
  - `ProductSlots`: List of product slots.
  - `OutputSlot`: Output slot.
  - `TypeName`: Type name of the station.
  - `IsInUse`: Indicates if the station is in use.
  - `RefillList`: List of items needed for refill.
- **Methods**:
  - `StationData(IStationAdapter station)`: Initializes from a station adapter.
  - `ToStationAdapter()`: Converts to an `IStationAdapter`.
  - `Dispose()`: Disposes of native collections.

### Extensions.StorageData

A struct representing storage data for a storage entity.

- **Fields**:
  - `GUID`: `Guid` of the storage entity.
  - `OutputSlots`: List of output slots.
  - `PropertyId`: ID of the parent property.
- **Methods**:
  - `StorageData(PlaceableStorageEntity shelf)`: Initializes from a storage entity.
  - `ToPlaceableStorageEntity()`: Converts to a `PlaceableStorageEntity`.
  - `Dispose()`: Disposes of native collections.

### Extensions.StorageType

An enum defining types of storage entities.

- **Values**:
  - `None`
  - `AnyShelf`
  - `SpecificShelf`
  - `Employee`
  - `Station`
  - `LoadingDock`

### Extensions.StorageKey

A struct representing a key for identifying a storage entity.

- **Fields**:
  - `PropertyId`: ID of the property.
  - `Guid`: `Guid` of the entity.
  - `Type`: `StorageType` of the entity.
- **Methods**:
  - `StorageKey(Guid guid, StorageType type)`: Initializes with GUID and type.
  - `Equals(StorageKey other)`: Checks equality.

### Extensions.ItemData

A struct storing item data, including ID, packaging, quality, and quantity.

- **Fields**:
  - `Id`: Item ID.
  - `PackagingId`: Packaging ID.
  - `Quality`: Quality of the item.
  - `Quantity`: Quantity of the item.
  - `StackLimit`: Maximum stack size.
- **Methods**:
  - `ItemData(ItemInstance item)`: Initializes from an item instance.
  - `CreateItemInstance()`: Creates an `ItemInstance`.
  - `AdvCanStackWithBurst(ItemData targetItem, bool allowTargetHigherQuality, bool checkQuantities)`: Checks if items can stack.

### Extensions.ItemKey

A struct representing a key for identifying an item.

- **Fields**:
  - `ItemId`: Item ID.
  - `PackagingId`: Packaging ID.
  - `Quality`: Quality of the item.
  - `CacheId`: Cached ID string.
- **Methods**:
  - `ItemKey(ItemInstance item)`: Initializes from an item instance.
  - `Equals(ItemKey other)`: Checks equality.

### Extensions.SlotData

A struct representing data for a slot.

- **Fields**:
  - `PropertyId`: ID of the property.
  - `EntityGuid`: `Guid` of the entity.
  - `SlotIndex`: Index of the slot.
  - `Item`: `ItemData` in the slot.
  - `Quantity`: Quantity in the slot.
  - `IsLocked`: Indicates if the slot is locked.
  - `StackLimit`: Maximum stack size.
  - `IsValid`: Indicates if the slot is valid.
  - `Type`: `StorageType` of the slot.
- **Methods**:
  - `SlotData(Guid guid, ItemSlot slot, StorageType type)`: Initializes from a slot and type.
  - `Equals(SlotData other)`: Checks equality.

### Extensions.SlotKey

A struct representing a key for identifying a slot within an entity.

- **Fields**:
  - `EntityGuid`: `Guid` of the entity.
  - `SlotIndex`: Index of the slot.
- **Methods**:
  - `SlotKey(Guid entityGuid, int slotIndex)`: Initializes with GUID and slot index.
  - `Equals(SlotKey other)`: Checks equality.
  - `GetItemSlotFromKey(Property property)`: Retrieves the associated `ItemSlot`.

### Extensions.StorageResult

A struct representing a storage result with shelf and slot details.

- **Fields**:
  - `Shelf`: The `PlaceableStorageEntity` found.
  - `AvailableQuantity`: Available quantity of the item.
  - `ItemSlots`: List of `ItemSlot` containing the item.

### Extensions.DeliveryDestination

A struct representing a delivery destination with entity and slot details.

- **Fields**:
  - `Entity`: The `ITransitEntity` for delivery.
  - `ItemSlots`: List of `ItemSlot` for delivery.
  - `Capacity`: Available capacity.

## Usage Notes

- **Server-Side Operations**: Most methods require server-side execution (`FishNetExtensions.IsServer`). Ensure calls are made in a server context.
- **Initialization**: Call `Initialize()` before using any storage operations and `Cleanup()` when done to manage resources.
- **Coroutines**: Methods like `FindStorageWithItem`, `UpdateStorageCache`, `FindDeliveryDestination`, `FindAvailableSlots`, and `ExecuteSlotOperations` are coroutines and must be executed within a coroutine context (e.g., using `MonoBehaviour.StartCoroutine`).
- **Dependencies**: The API assumes dependencies like `FishNetExtensions`, `SlotService`, `CacheService`, and others are defined and accessible. Ensure these are properly set up in your project.
- **Burst Compilation**: Structures like `StationData`, `StorageData`, `ItemData`, and others are optimized for Burst compilation, improving performance in compute-intensive operations.

This API is designed for efficient storage management in a networked environment, leveraging caching and Burst compilation for performance.
