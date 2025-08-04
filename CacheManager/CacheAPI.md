# CacheManager API Documentation

**Date:** July 02, 2025

The `CacheManager` class provides a comprehensive API for managing storage operations in a networked environment, including initialization, cleanup, slot operations, and access to managed dictionaries and internal caches. This documentation outlines how to utilize the public methods, structures, managed dictionaries, and accessible internal caches effectively, with examples using `CoroutineRunner` for coroutine execution and result handling via callbacks.

## Classes and Structs

### CacheManager

A static class responsible for managing storage operations, including initialization, cleanup, and slot operations for items.

#### Properties

- **IsInitialized** (`bool`, read-only): Indicates whether the `CacheManager` is initialized.

#### Methods

- **Initialize()**
  Initializes the `CacheManager` and sets up necessary resources. Must be called on the server before performing storage operations.

  ```csharp
  CacheManager.Initialize()
  ```

- **Cleanup()**
  Cleans up resources used by the `CacheManager`. Call this to release resources when no longer needed.

  ```csharp
  CacheManager.Cleanup()
  ```

- **ClearEntityCache(Property property, Guid guid)**
  Clears the cache for a specific entity identified by its GUID within a property.
  - **Parameters**:
    - `property`: The `Property` containing the entity.
    - `guid`: The `Guid` of the entity to clear from cache.

  ```csharp
  CacheManager.ClearEntityCache(property, entityGuid)
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
  bool reserved = CacheManager.ReserveSlot(entityGuid, slot, locker, "Reservation for processing", item, 10)
  ```

- **ReleaseSlot(ItemSlot slot)**
  Releases a previously reserved slot.
  - **Parameters**:
    - `slot`: The `ItemSlot` to release.

  ```csharp
  CacheManager.ReleaseSlot(slot)
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
  CoroutineRunner runner = CoroutineRunner.Instance; // Assumes singleton instance
  runner.RunCoroutineWithResult(CacheManager.FindStorageWithItem(property, item, 5, true), (StorageResult result) =>
  {
      if (result != null)
      {
          // Process result
      }
  })
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
  CoroutineRunner runner = CoroutineRunner.Instance;
  runner.RunCoroutine(CacheManager.UpdateStorageCache(property, entityGuid, slots, StorageType.AnyShelf))
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
  CoroutineRunner runner = CoroutineRunner.Instance;
  runner.RunCoroutineWithResult(CacheManager.FindDeliveryDestination(property, item, 10, sourceGuid), (List<DeliveryDestination> destinations) =>
  {
      if (destinations != null)
      {
          // Process destinations
      }
  })
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
  CoroutineRunner runner = CoroutineRunner.Instance;
  runner.RunCoroutineWithResult(CacheManager.FindAvailableSlots(property, entityGuid, slots, item, 5), (List<(ItemSlot, int)> availableSlots) =>
  {
      if (availableSlots != null)
      {
          // Process available slots
      }
  })
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
  CoroutineRunner runner = CoroutineRunner.Instance;
  runner.RunCoroutineWithResult(CacheManager.ExecuteSlotOperations(property, operations), (List<bool> results) =>
  {
      if (results != null)
      {
          // Process results
      }
  })
  ```

  - **Example Implementation**:

  ```csharp
  public static IEnumerator FindDeliveryDestination(Property property, ItemInstance item, int quantity, Guid sourceGuid)
  {
    if (!IsInitialized || !InstanceFinder.NetworkManager.IsServer || item == null || quantity <= 0)
    {
      Log(Level.Error, $"FindDeliveryDestinations: Invalid input or state (init={IsInitialized}, server={InstanceFinder.NetworkManager.IsServer}, item={item?.ID}, qty={quantity})", Category.Storage);
      yield return new List<DeliveryDestinationBurst>();
      yield break;
    }
    var cacheService = CacheService.GetOrCreateCacheService(property);
    var itemKey = new ItemKey(item);
    var itemData = new ItemData(item);
    var destinations = new NativeList<DeliveryDestinationBurst>(Allocator.TempJob);
    var inputs = new NativeList<(StorageKey, bool)>(Allocator.TempJob);
    var stationInputs = new NativeList<(Guid, NativeList<ItemKey>)>(Allocator.TempJob);
    var logs = new NativeList<LogEntry>(Allocator.TempJob);
    try
    {
      if (IStations.TryGetValue(property, out var stations))
      {
        foreach (var station in stations.Values)
        {
  # if DEBUG
          Log(Level.Verbose, $"Checking station {station.GUID} for delivery", Category.Storage);
  # endif
          if (station.TypeOf == typeof(PackagingStation) && !station.IsInUse && station.CanRefill(item))
          {
            var refillList = new NativeList<ItemKey>(Allocator.TempJob);
            inputs.Add((new StorageKey(station.GUID, StorageType.Station), true));
            foreach (var itemInstance in station.RefillList())
              refillList.Add(new ItemKey(itemInstance));
            stationInputs.Add((station.GUID, refillList));
          }
        }
      }
      if (cacheService._itemToStorageCache.TryGetValue(itemKey, out var specificKeys))
      {
        foreach (var key in specificKeys.AsArray())
        {
  # if DEBUG
          Log(Level.Verbose, $"Adding specific key {key.Guid} for item {itemKey.ToString()}", Category.Storage);
  # endif
          inputs.Add((key, false));
        }
      }
      foreach (var key in cacheService._anyShelfKeys.AsArray())
      {
  # if DEBUG
        Log(Level.Verbose, $"Adding any shelf key {key.Guid}", Category.Storage);
  # endif
        inputs.Add((key, false));
      }
      if (cacheService._noDropOffCache.Contains(itemKey))
      {
  # if DEBUG
        Log(Level.Verbose, $"Item {itemKey.ToString()} found in no drop-off cache, skipping delivery", Category.Storage);
  # endif
        yield return new List<DeliveryDestinationBurst>();
        yield break;
      }
      // Update station refill lists in a Burst job
      yield return SmartExecution.ExecuteBurstFor<(Guid, NativeList<ItemKey>), ItemKey>(
          uniqueId: nameof(FindDeliveryDestination) + "_RefillList",
          itemCount: stationInputs.Length,
          burstForDelegate: (index, inputs, outputs) => cacheService.UpdateStationRefillListBurst(index, inputs, itemKey, logs),
          burstResultsDelegate: null,
          inputs: stationInputs.AsArray(),
          outputs: default,
          options: default
      );
      int remainingQty = quantity;
      yield return SmartExecution.ExecuteBurstFor<(StorageKey, bool), DeliveryDestinationBurst>(
          uniqueId: nameof(FindDeliveryDestination),
          itemCount: inputs.Length,
          burstForDelegate: (i, inputs, outputs) => cacheService.FindDeliveryDestinationBurst(i, inputs, outputs, quantity, sourceGuid, logs, cacheService._storageSlotsCache, ref remainingQty),
          burstResultsDelegate: (results) => cacheService.FindDeliveryDestinationResults(results, logs),
          inputs: inputs.AsArray(),
          outputs: destinations,
          options: default
      );
      if (destinations.Length == 0)
      {
        cacheService.AddNoDropOffCache(itemKey, logs);
  # if DEBUG
        Log(Level.Verbose, $"No destinations found for item {itemKey.ToString()}, added to no drop-off cache", Category.Storage);
  # endif
      }
      else
      {
        cacheService.RemoveNoDropOffCache(itemKey, logs);
      }
      List<DeliveryDestination> managedResult = new();
      foreach (var destination in destinations)
      {
        Log(Level.Info, $"Returning destination {destination.Guid} with capacity {destination.Capacity}", Category.Storage);
        managedResult.Add(destination.GetResult());
      }
      yield return managedResult;
      yield return ProcessLogs(logs);
    }
    finally
    {
      foreach (var stationInput in stationInputs)
        if (stationInput.Item2.IsCreated)
          stationInput.Item2.Dispose();
      stationInputs.Dispose();
      inputs.Dispose();
      destinations.Dispose();
      logs.Dispose();
    }
  }
  [BurstCompile]
  public void UpdateStationRefillListBurst(
                  int index,
                  NativeArray<(Guid, NativeList<ItemKey>)> inputs,
                  ItemKey item,
                  NativeList<LogEntry> logs)
  {
    var (stationGuid, refillList) = inputs[index];
    if (!refillList.Contains(item))
    {
      refillList.Add(item);
  # if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Added item {item.CacheId} to station {stationGuid} refill list",
        Level = Level.Verbose,
        Category = Category.Storage
      });
  # endif
    }
  }
  
  [BurstCompile]
  public void FindDeliveryDestinationBurst(
              int index,
              NativeArray<(StorageKey Key, bool IsStation)> inputs,
              NativeList<DeliveryDestinationBurst> outputs,
              int quantity,
              Guid sourceGuid,
              NativeList<LogEntry> logs,
              NativeParallelHashMap<StorageKey, NativeList<SlotData>> storageSlotsCache,
              ref int remainingQty)
  {
    var (storageKey, isStation) = inputs[index];
    if (!isStation && storageKey.Guid == sourceGuid) return;
    if (storageSlotsCache.TryGetValue(storageKey, out var slots))
    {
      var inputSlots = new NativeList<SlotData>(1, Allocator.Temp);
      int capacity = 0;
      for (int j = 0; j < slots.Length; j++)
      {
  # if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Checking slot {slots[j].SlotIndex} for delivery destination",
          Level = Level.Verbose,
          Category = Category.Storage
        });
  # endif
        var slot = slots[j];
        if (slot.IsValid && slot.StackLimit > slot.Quantity)
        {
          int slotCapacity = slot.StackLimit - slot.Quantity;
          if (slotCapacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(slot.Item)))
          {
            inputSlots.Add(slot);
            capacity += slotCapacity;
          }
        }
      }
      if (capacity > 0)
      {
        var slotsPersistent = new NativeList<SlotData>(inputSlots.Length, Allocator.Persistent);
        slotsPersistent.AddRange(inputSlots);
        outputs.Add(new DeliveryDestinationBurst
        {
          Guid = storageKey.Guid,
          SlotData = slotsPersistent,
          Capacity = Mathf.Min(capacity, remainingQty)
        });
        remainingQty -= capacity;
        logs.Add(new LogEntry
        {
          Message = $"Found destination {storageKey.Guid} with capacity {capacity}",
          Level = Level.Info,
          Category = Category.Storage
        });
      }
      inputSlots.Dispose();
    }
  }
  
  [BurstCompile]
  public void FindDeliveryDestinationResults(
          NativeList<DeliveryDestinationBurst> destinations,
          NativeList<LogEntry> logs)
  {
    foreach (var destination in destinations)
    {
  # if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Found destination {destination.Guid} with capacity {destination.Capacity}",
        Level = Level.Verbose,
        Category = Category.Storage
      });
  # endif
    }
  }
  ```

### ManagedDictionaries

A static class containing managed dictionaries for mapping properties, entities, and configurations.

- **Fields**:
  - **IdToProperty** (`Dictionary<int, Property>`): Maps property IDs to `Property` instances.
    - **Usage**: Retrieve a `Property` by its ID for operations requiring property context.

  - **StorageConfigs** (`Dictionary<Property, Dictionary<Guid, StorageConfiguration>>`): Maps properties to dictionaries of entity GUIDs and their `StorageConfiguration`.
    - **Usage**: Access configuration for a specific storage entity within a property.

  - **Storages** (`Dictionary<Property, Dictionary<Guid, PlaceableStorageEntity>>`): Maps properties to dictionaries of entity GUIDs and their `PlaceableStorageEntity`.
    - **Usage**: Retrieve a storage entity by GUID within a property.

  - **LoadingDocks** (`Dictionary<Property, Dictionary<Guid, LoadingDock>>`): Maps properties to dictionaries of entity GUIDs and their `LoadingDock`.
    - **Usage**: Access a loading dock by GUID within a property.

  - **IStations** (`Dictionary<Property, Dictionary<Guid, IStationAdapter>>`): Maps properties to dictionaries of station GUIDs and their `IStationAdapter`.
    - **Usage**: Retrieve a station adapter by GUID within a property.

  - **IEmployees** (`Dictionary<Property, Dictionary<Guid, IEmployeeAdapter>>`): Maps properties to dictionaries of employee GUIDs and their `IEmployeeAdapter`.
    - **Usage**: Access an employee adapter by GUID within a property.

  - **PendingConfigData** (`Dictionary<Guid, JObject>`): Stores pending configuration data by GUID.
    - **Usage**: Retrieve pending configuration data for an entity.

### CacheService

An internal class managing caching for storage-related data and operations. Accessible via `ManagedDictionaries.CacheServices`.

- **Accessing CacheService**:
  - Retrieve the `CacheService` instance for a property using `ManagedDictionaries.CacheServices`.

- **Fields** (Accessible via `CacheService` instance):
  - **_storageResultPool** (`ObjectPool<List<StorageResultBurst>>`): Pool for `StorageResultBurst` lists used in item searches.
    - **Usage**: Typically internal, but can be used to manage pooled results if needed.

  - **_deliveryResultPool** (`ObjectPool<List<DeliveryDestinationBurst>>`): Pool for `DeliveryDestinationBurst` lists used in delivery searches.
    - **Usage**: Typically internal, but can be used to manage pooled delivery results.

  - **_slotCache** (`NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)>`): Cache mapping slot keys to slot details.
    - **Usage**: Check slot details for a specific `SlotKey`.

  - **_storageSlotsCache** (`NativeParallelHashMap<StorageKey, NativeList<SlotData>>`): Cache mapping storage keys to slot data lists.
    - **Usage**: Retrieve slots for a storage entity.

  - **_itemToSlotIndices** (`NativeParallelHashMap<ItemKey, NativeList<(StorageKey, NativeList<int>)>>`): Cache mapping item keys to storage keys and slot indices.
    - **Usage**: Find slots containing a specific item.

  - **_anyShelfKeys** (`NativeList<StorageKey>`): List of storage keys for shelves.
    - **Usage**: Iterate over all shelf keys in the cache.

  - **_notFoundItems** (`NativeParallelHashSet<ItemKey>`): Set of items marked as not found.
    - **Usage**: Check if an item is not found in the cache.

  - **_ownerToStorageType** (`NativeParallelHashMap<Guid, StorageType>`): Maps entity GUIDs to storage types.
    - **Usage**: Retrieve the storage type for an entity.

  - **_noDropOffCache** (`NativeParallelHashSet<ItemKey>`): Set of items with no drop-off locations.
    - **Usage**: Check if an item has no valid drop-off.

  - **_stationData** (`NativeParallelHashSet<StationData>`): Set of station data.
    - **Usage**: Access station data for processing.

  - **_pendingSlotUpdates** (`NativeList<SlotUpdate>`): List of pending slot updates.
    - **Usage**: Typically internal, but can be inspected for pending updates.

  - **_shelfSearchCache** (`NativeParallelHashMap<ItemKey, (StorageKey, int, NativeList<SlotData>)>`): Cache for shelf search results.
    - **Usage**: Retrieve cached shelf search results for an item.

  - **_itemToStorageCache** (`NativeParallelHashMap<ItemKey, NativeList<StorageKey>>`): Cache mapping items to storage keys.
    - **Usage**: Find storage entities containing an item.

  - **_storagePropertyMap** (`NativeParallelHashMap<StorageKey, int>`): Maps storage keys to property IDs.
    - **Usage**: Retrieve the property ID for a storage key.

- **Methods**:
  - **GetOrCreateCacheService(Property property)**: Retrieves or creates a `CacheService` for a property.
    - **Parameters**:
      - `property`: The `Property` to associate with the cache service.
    - **Returns**: The `CacheService` instance, or `null` if not on server.

  - **ClearCacheForEntity(Guid guid)**: Clears the cache for a specific entity.
    - **Parameters**:
      - `guid`: The `Guid` of the entity to clear from cache.

  - **AddItemNotFound(ItemKey cacheKey)**: Marks an item as not found in the cache.
    - **Parameters**:
      - `cacheKey`: The `ItemKey` of the item to mark.

  - **ClearItemNotFoundCache(ItemKey item)**: Clears an item from the not found cache.
    - **Parameters**:
      - `item`: The `ItemKey` to clear.

  - **AddNoDropOffCache(ItemKey item, NativeList<LogEntry> logs)**: Adds an item to the no drop-off cache.
    - **Parameters**:
      - `item`: The `ItemKey` to add.
      - `logs`: The list to store log entries (typically internal).

  - **RemoveNoDropOffCache(ItemKey item, NativeList<LogEntry> logs)**: Removes an item from the no drop-off cache.
    - **Parameters**:
      - `item`: The `ItemKey` to remove.
      - `logs`: The list to store log entries (typically internal).

  - **ClearZeroQuantityEntries(PlaceableStorageEntity shelf, ItemInstance[] items)**: Clears zero quantity entries from the shelf search cache.
    - **Parameters**:
      - `shelf`: The `PlaceableStorageEntity` to process.
      - `items`: Array of `ItemInstance` to clear.
    - **Yields**: Completes asynchronously.

    ```csharp
    CoroutineRunner runner = CoroutineRunner.Instance;
    if (ManagedDictionaries.CacheServices.TryGetValue(property, out var cacheService))
    {
        runner.RunCoroutine(cacheService.ClearZeroQuantityEntries(shelf, items));
    }
    ```

  - **RemoveShelfSearchCacheEntries(List<ItemKey> cacheKeys)**: Removes entries from the shelf search cache.
    - **Parameters**:
      - `cacheKeys`: List of `ItemKey` to remove.
    - **Yields**: Completes asynchronously.

    ```csharp
    CoroutineRunner runner = CoroutineRunner.Instance;
    if (ManagedDictionaries.CacheServices.TryGetValue(property, out var cacheService))
    {
        runner.RunCoroutine(cacheService.RemoveShelfSearchCacheEntries(cacheKeys));
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
- **Coroutines**: Methods like `FindStorageWithItem`, `UpdateStorageCache`, `FindDeliveryDestination`, `FindAvailableSlots`, `ExecuteSlotOperations`, `ClearZeroQuantityEntries`, and `RemoveShelfSearchCacheEntries` are coroutines. Use `CoroutineRunner.RunCoroutineWithResult` for methods yielding results or `RunCoroutine` for those without, as shown in examples.
- **Managed Dictionaries**: Access `ManagedDictionaries` fields directly to retrieve mappings for properties, storage entities, configurations, and more. Ensure proper error handling (e.g., `TryGetValue`) to avoid null reference exceptions.
- **CacheService Access**: Use `CacheService.GetOrCreateCacheService(Property property)` to access `CacheService` instances and their internal caches. Be cautious when modifying cache contents directly, as they are optimized for internal use and may require Burst-compatible handling.
- **Burst Compilation**: Structures like `StationData`, `StorageData`, `ItemData`, and others, as well as `CacheService` methods, are optimized for Burst compilation, improving performance in compute-intensive operations. Ensure proper disposal of native collections to avoid memory leaks.
- **Thread Safety**: Cache access via `CacheService` fields (e.g., `_slotCache`, `_storageSlotsCache`) uses `NativeParallelHashMap` and `NativeList`, which are thread-safe for Burst operations but require careful handling in managed code.
