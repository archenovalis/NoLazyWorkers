# StorageManager API Documentation

**Date**: June 26, 2025

This document describes the public API of the `StorageManager` class in the `NoLazyWorkers.Storage` namespace, which manages storage operations, including slot reservations, item searches, and cache handling for storage entities.

## Classes and Structs

### StorageManager

**Description**: A static class that manages storage operations, including initialization, cleanup, slot reservations, and item searches across storage entities.

#### Properties

- `IsInitialized` (bool, read-only): Indicates whether the StorageManager is initialized.

#### Methods

##### Initialize

**Description**: Initializes the StorageManager, setting up slot services. Must be called on the server.
**Signature**:

```csharp
public static void Initialize()
```

**Remarks**: Logs a warning and returns if already initialized or not running on the server.
**Example**:

```csharp
StorageManager.Initialize()
```

##### Cleanup

**Description**: Cleans up the StorageManager, disposing of caches and clearing slot services. Must be called on the server.
**Signature**:

```csharp
public static void Cleanup()
```

**Remarks**: Logs a warning and returns if not initialized or not running on the server.
**Example**:

```csharp
StorageManager.Cleanup()
```

##### ReserveSlot

**Description**: Reserves a slot for an item, locking it with a specified reason.
**Signature**:

```csharp
public static bool ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)
```

**Parameters**:

- `entityGuid`: The GUID of the storage entity.
- `slot`: The slot to reserve.
- `locker`: The network object locking the slot.
- `lockReason`: The reason for locking the slot.
- `item`: The item to reserve (optional).
- `quantity`: The quantity to reserve (optional).
**Returns**: `true` if the slot was reserved successfully; otherwise, `false`.
**Example**:

```csharp
var entityGuid = Guid.NewGuid();
var slot = new ItemSlot(); 
var locker = new NetworkObject(); 
bool reserved = StorageManager.ReserveSlot(entityGuid, slot, locker, "OrderProcessing");
if (reserved)
{
    Console.WriteLine("Slot reserved successfully.");
}
```

##### ReleaseSlot

**Description**: Releases a previously reserved slot, removing its lock.
**Signature**:

```csharp
public static void ReleaseSlot(ItemSlot slot)
```

**Parameters**:

- `slot`: The slot to release.
**Example**:

```csharp
var slot = new ItemSlot(); 
StorageManager.ReleaseSlot(slot);
```

##### FindStorageWithItem

**Description**: Finds storage locations containing the specified item and quantity.
**Signature**:

```csharp
public static IEnumerator FindStorageWithItem(Property property, ItemInstance item, int needed, bool allowTargetHigherQuality = false)
```

**Parameters**:

- `property`: The property to search within.
- `item`: The item to locate.
- `needed`: The required quantity.
- `allowTargetHigherQuality`: Whether to allow items of higher quality.
**Returns**: An IEnumerator yielding a `List<StorageResult>` containing matching storage locations. Callers should convert `NativeList<int> SlotIndices` to a managed list and dispose of it.
**Caller Responsibilities**:
- Use `yield return` to execute the coroutine and retrieve the result.
- Convert `StorageResult.SlotIndices` (a `NativeList<int>`) to a managed list for further use.
- Dispose of `StorageResult.SlotIndices` to prevent memory leaks.
**Example**:

```csharp
var property = new Property(); 
var item = new ItemInstance { ID = "Item123" }; 
int needed = 10;
IEnumerator coroutine = StorageManager.FindStorageWithItem(property, item, needed);
while (coroutine.MoveNext())
{
    if (coroutine.Current is List<StorageResult> results)
    {
        foreach (var result in results)
        {
            var slotIndices = result.SlotIndices.ToArray().ToList(); // Convert to managed list
            Console.WriteLine($"Found shelf {result.ShelfGuid} with {result.AvailableQuantity} items in slots {string.Join(",", slotIndices)}");
            result.SlotIndices.Dispose(); // Dispose NativeList
        }
    }
}
```

##### FindDeliveryDestinations

**Description**: Finds suitable delivery destinations for an item and quantity.
**Signature**:

```csharp
public static IEnumerator FindDeliveryDestinations(Property property, ItemInstance item, int quantity, Guid sourceGuid)
```

**Parameters**:

- `property`: The property to search within.
- `item`: The item to deliver.
- `quantity`: The quantity to deliver.
- `sourceGuid`: The GUID of the source entity.
**Returns**: An IEnumerator yielding a `List<DeliveryDestination>` containing suitable destinations. Callers should convert `DeliveryDestination.SlotIndices` to a managed list and dispose of it.
**Caller Responsibilities**:
- Use `yield return` to execute the coroutine and retrieve the result.
- Convert `DeliveryDestination.SlotIndices` (a `NativeList<int>`) to a managed list for further use.
- Dispose of `DeliveryDestination.SlotIndices` to prevent memory leaks.
**Example**:

```csharp
var property = new Property(); 
var item = new ItemInstance { ID = "Item123" }; 
var sourceGuid = Guid.NewGuid();
IEnumerator coroutine = StorageManager.FindDeliveryDestinations(property, item, 5, sourceGuid);
yield return coroutine;
var destinations = coroutine.Current
foreach (var dest in destinations)
{
    var slotIndices = dest.SlotIndices.ToArray().ToList(); // Convert to managed list
    Console.WriteLine($"Found destination {dest.DestinationGuid} with capacity {dest.Capacity} in slots {string.Join(",", slotIndices)}");
    dest.SlotIndices.Dispose(); // Dispose NativeList
}
```

##### FindAvailableSlots

**Description**: Finds available slots for storing an item and quantity.
**Signature**:

```csharp
public static IEnumerator FindAvailableSlots(List<ItemSlot> slots, ItemInstance item, int quantity)
```

**Parameters**:

- `slots`: The list of slots to check.
- `item`: The item to store.
- `quantity`: The quantity to store.
**Returns**: An IEnumerator yielding a `List<(ItemSlot, int)>` containing available slots and their capacities.
**Caller Responsibilities**:
- Use `yield return` to execute the coroutine and retrieve the result.
- No `Native*` collections are returned, so no disposal is needed.
**Example**:

```csharp
var slots = new List<ItemSlot> { new ItemSlot(), new ItemSlot() }; 
var item = new ItemInstance { ID = "Item123" }; 
IEnumerator coroutine = StorageManager.FindAvailableSlots(slots, item, 5);
while (coroutine.MoveNext())
{
    if (coroutine.Current is List<(ItemSlot, int)> results)
    {
        foreach (var (slot, capacity) in results)
        {
            Console.WriteLine($"Slot {slot.SlotIndex} has capacity {capacity}.");
        }
    }
}
```

##### ExecuteSlotOperations

**Description**: Executes a batch of slot operations (insert or remove).
**Signature**:

```csharp
public static IEnumerator ExecuteSlotOperations(List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations)
```

**Parameters**:

- `operations`: The list of operations to execute.
**Returns**: An IEnumerator yielding a `List<bool>` indicating success for each operation.
**Caller Responsibilities**:
- Use `yield return` to execute the coroutine and retrieve the result.
- No `Native*` collections are returned, so no disposal is needed.
**Example**:

```csharp
var operations = new List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)>
{
    (Guid.NewGuid(), new ItemSlot(), new ItemInstance { ID = "Item123" }, 5, true, new NetworkObject(), "OrderProcessing")
};
IEnumerator coroutine = StorageManager.ExecuteSlotOperations(operations);
while (coroutine.MoveNext())
{
    if (coroutine.Current is List<bool> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            Console.WriteLine($"Operation {i} {(results[i] ? "succeeded" : "failed")}.");
        }
    }
}
```

### Structs

#### StorageResult

**Description**: Represents a storage location with available items.
**Fields**:

- `ShelfGuid` (Guid): The GUID of the storage shelf.
- `AvailableQuantity` (int): The total quantity available.
- `SlotIndices` (NativeList<int>): The indices of the slots containing the items. Must be converted to a managed list and disposed of by the caller.

#### DeliveryDestination

**Description**: Represents a destination for item delivery.
**Fields**:

- `DestinationGuid` (Guid): The GUID of the destination.
- `SlotIndices` (NativeList<int>): The indices of available slots. Must be converted to a managed list and disposed of by the caller.
- `Capacity` (int): The total capacity for the item.

## Usage Notes

- All operations require `StorageManager` to be initialized and executed on the server (`InstanceFinder.NetworkManager.IsServer` must be true).
- Coroutine entrypoints (`FindStorageWithItem`, `FindDeliveryDestinations`, `FindAvailableSlots`, `ExecuteSlotOperations`) must be executed using `yield return` in a coroutine context (e.g., within a MonoBehaviour or via `CoroutineRunner.Instance.RunCoroutine`).
- For `FindStorageWithItem` and `FindDeliveryDestinations`, callers must convert `NativeList<int> SlotIndices` to a managed list (e.g., using `ToArray().ToList()`) and call `Dispose()` on the `NativeList<int>` to prevent memory leaks.
