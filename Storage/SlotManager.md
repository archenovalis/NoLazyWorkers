# SlotManager API Documentation

**Date**: June 20, 2025

## Overview

The `SlotManager` class in the `NoLazyWorkers.Storage` namespace provides functionality for managing storage slots in a networked environment using FishNet. It handles slot reservations, item insertion/removal, and availability checks, leveraging Unity's Burst compiler and Job system for performance.

## Structs

### SlotKey

A unique key for a storage slot, combining an entity GUID and slot index.

- **Fields**:
  - `EntityGuid`: `Guid` - The unique identifier of the entity owning the slot.
  - `SlotIndex`: `int` - The index of the slot within the entity.

- **Methods**:
  - `Equals(SlotKey)`: Returns `true` if the specified `SlotKey` is equal to the current instance.
  - `Equals(object)`: Returns `true` if the specified object is equal to the current `SlotKey`.
  - `GetHashCode()`: Returns a hash code for the current `SlotKey`.

**Example**:

```csharp
var slotKey = new SlotKey(Guid.NewGuid(), 1);
bool isEqual = slotKey.Equals(new SlotKey(slotKey.EntityGuid, 1));
Console.WriteLine($"Slot keys equal: {isEqual}"); // true
```

### SlotReservation

Represents a reservation for a storage slot, including locking details and item information.

- **Fields**:
  - `EntityGuid`: `Guid` - The unique identifier of the entity.
  - `Timestamp`: `float` - The time the slot was reserved.
  - `Locker`: `FixedString32Bytes` - The identifier of the locker.
  - `LockReason`: `FixedString128Bytes` - The reason for locking.
  - `Item`: `ItemKey` - The item key associated with the reservation.
  - `Quantity`: `int` - The quantity of items reserved.

### CoroutineResult<T>

Encapsulates the result of a coroutine operation.

- **Fields**:
  - `Value`: `T` - The result value of the coroutine.

**Example**:

```csharp
var result = new CoroutineResult<List<(ItemSlot, int)>>(new List<(ItemSlot, int)>());
Console.WriteLine($"Result value: {result.Value.Count}"); // 0
```

## Static Class: SlotManager

### Fields

- `NetworkObjectCache`: `List<NetworkObject>` - Cache of network objects used during slot operations.
- `Reservations`: `NativeParallelHashMap<SlotKey, SlotReservation>` - Thread-safe hash map for slot reservations.
- `_operationPool`: `ObjectPool<List<SlotOperation>>` - Pool for managing lists of slot operations.

### Methods

#### Initialize()

Initializes the `SlotManager`, setting up the reservations hash map.

**Example**:

```csharp
SlotManager.Initialize()
```

#### FindAvailableSlots(List<ItemSlot> slots, ItemInstance item, int quantity)

Finds available slots for an item using job, coroutine, or main-thread processing.

- **Parameters**:
  - `slots`: `List<ItemSlot>` - The slots to check.
  - `item`: `ItemInstance` - The item to allocate.
  - `quantity`: `int` - The quantity to allocate.
- **Returns**: `List<(ItemSlot Slot, int Capacity)>` - List of available slots and their capacities.

**Example**:

```csharp
var slots = new List<ItemSlot> { new ItemSlot() };
var item = new ItemInstance { ID = "item1" };
var availableSlots = SlotManager.FindAvailableSlots(slots, item, 10);
foreach (var (slot, capacity) in availableSlots)
{
    Console.WriteLine($"Slot {slot.SlotIndex} has capacity {capacity}");
}
```

#### ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)

Reserves a slot, applying a lock and storing reservation details.

- **Parameters**:
  - `entityGuid`: `Guid` - The entity identifier.
  - `slot`: `ItemSlot` - The slot to reserve.
  - `locker`: `NetworkObject` - The network object locking the slot.
  - `lockReason`: `string` - The reason for locking.
  - `item`: `ItemInstance` (optional) - The item to reserve.
  - `quantity`: `int` (optional) - The quantity to reserve.
- **Returns**: `bool` - True if reserved successfully; otherwise, false.

**Example**:

```csharp
var entityGuid = Guid.NewGuid();
var slot = new ItemSlot { SlotIndex = 1 };
var locker = new NetworkObject();
bool reserved = SlotManager.ReserveSlot(entityGuid, slot, locker, "Storage lock");
Console.WriteLine($"Slot reserved: {reserved}")
```

#### ReleaseSlot(ItemSlot slot)

Releases a reserved slot, removing the lock and reservation.

- **Parameters**:
  - `slot`: `ItemSlot` - The slot to release.

**Example**:

```csharp
var slot = new ItemSlot { SlotIndex = 1 };
SlotManager.ReleaseSlot(slot)
```

#### ExecuteSlotOperations(List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)> operations)

Executes a list of slot operations (insert or remove) with reservation management.

- **Parameters**:
  - `operations`: `List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)>` - The operations to execute, where each tuple contains:
    - `EntityGuid`: The entity identifier.
    - `Slot`: The slot to operate on.
    - `Item`: The item to process.
    - `Quantity`: The quantity to process.
    - `IsInsert`: True for insert, false for remove.
    - `Locker`: The network object locking the slot.
    - `LockReason`: The reason for locking.
- **Returns**: `bool` - True if all operations were successful; otherwise, false.

**Example**:

```csharp
var operations = new List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)>
{
    (Guid.NewGuid(), new ItemSlot { SlotIndex = 1 }, new ItemInstance { ID = "item1" }, 5, true, new NetworkObject(), "Insert item")
};
bool success = SlotManager.ExecuteSlotOperations(operations);
Console.WriteLine($"Operations successful: {success}")
```

#### Cleanup()

Cleans up the `SlotManager`, disposing of the reservations hash map and clearing the operation pool.

**Example**:

```csharp
SlotManager.Cleanup()
```

### Nested Class: SlotProcessingUtility

Utility methods for processing slot operations.

#### GetCapacityForItem(SlotData slot, ItemKey item)

Calculates the available capacity for an item in a slot.

- **Parameters**:
  - `slot`: `SlotData` - The slot data.
  - `item`: `ItemKey` - The item key.
- **Returns**: `int` - The available capacity.

**Example**:

```csharp
var slot = new SlotData { StackLimit = 10, Quantity = 2 };
var item = new ItemKey { StackLimit = 10 };
int capacity = SlotManager.SlotProcessingUtility.GetCapacityForItem(slot, item);
Console.WriteLine($"Capacity: {capacity}"); // 8
```

#### CanInsert(SlotData slot, ItemKey item, int quantity)

Checks if an item can be inserted into a slot.

- **Parameters**:
  - `slot`: `SlotData` - The slot data.
  - `item`: `ItemKey` - The item key.
  - `quantity`: `int` - The quantity to insert.
- **Returns**: `bool` - True if the item can be inserted; otherwise, false.

**Example**:

```csharp
var slot = new SlotData { StackLimit = 10, Quantity = 2 };
var item = new ItemKey { Id = "item1" };
bool canInsert = SlotManager.SlotProcessingUtility.CanInsert(slot, item, 5);
Console.WriteLine($"Can insert: {canInsert}")
```

#### CanRemove(SlotData slot, ItemKey item, int quantity)

Checks if an item can be removed from a slot.

- **Parameters**:
  - `slot`: `SlotData` - The slot data.
  - `item`: `ItemKey` - The item key.
  - `quantity`: `int` - The quantity to remove.
- **Returns**: `bool` - True if the item can be removed; otherwise, false.

**Example**:

```csharp
var slot = new SlotData { Quantity = 5, Item = new ItemKey { Id = "item1" } };
var item = new ItemKey { Id = "item1" };
bool canRemove = SlotManager.SlotProcessingUtility.CanRemove(slot, item, 3);
Console.WriteLine($"Can remove: {canRemove}")
```

#### ValidateOperation(ItemSlot slot, ItemInstance item, int quantity, bool isInsert, out LogEntry log)

Validates a slot operation (insert or remove).

- **Parameters**:
  - `slot`: `ItemSlot` - The slot to validate.
  - `item`: `ItemInstance` - The item to process.
  - `quantity`: `int` - The quantity to process.
  - `isInsert`: `bool` - True for insert, false for remove.
  - `log`: `out LogEntry` - The log entry if validation fails.
- **Returns**: `bool` - True if the operation is valid; otherwise, false.

**Example**:

```csharp
var slot = new ItemSlot { SlotIndex = 1 };
var item = new ItemInstance { ID = "item1" };
bool valid = SlotManager.SlotProcessingUtility.ValidateOperation(slot, item, 5, true, out var log);
Console.WriteLine($"Valid operation: {valid}, Log: {log.Message}")
```

## Harmony Patches

### ItemSlotPatch

Extends `ItemSlot` locking behavior with Harmony patches.

- **ApplyLockPrefix**: Customizes the `ApplyLock` method to handle internal and networked locking.

### ItemSlotUIPatch (Debug Only)

Logs UI slot assignments.

- **AssignSlotPostfix**: Logs when a slot is assigned to the UI.
