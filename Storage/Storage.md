# NoLazyWorkers.Storage Namespace API Documentation

This namespace contains classes, structs, and utilities for managing storage-related operations in a warehouse simulation, optimized for Unity's Burst compiler and FishNet networking. The code handles item storage, delivery destinations, slot reservations, and caching for performance-critical operations.

---

## Jobs (Static Class)

Contains Burst-compatible job structs for efficient storage and delivery operations.

### FindItemJob : IJob

Finds a specific item within a storage using a Burst-compatible job.

**Fields:**

- `Property Property`: The property to search within.
- `ItemKey TargetItem`: The item to find.
- `int Needed`: Minimum quantity required.
- `int Wanted`: Desired quantity (optional).
- `Guid EmployeeGuid`: GUID of the employee requesting the storage.
- `NativeList<StorageResult> Results`: Stores the search results.
- `NativeList<LogEntry> Logs`: Logs for debugging and performance tracking.

**Methods:**

- `void Execute()`: Executes the job, checking the cache for the item and updating results and logs accordingly.

**Example:**

```csharp
var job = new FindItemJob
{
    Property = property,
    TargetItem = new ItemKey(itemInstance),
    Needed = 10,
    Wanted = 20,
    EmployeeGuid = employee.GUID,
    Results = new NativeList<StorageResult>(Allocator.TempJob),
    Logs = new NativeList<LogEntry>(Allocator.TempJob)
};
var handle = job.Schedule();
handle.Complete()
```

---

### FindDeliveryDestinationsJob : IJob

Finds delivery destinations for an item using a Burst-compatible job.

**Fields:**

- `Property Property`: The property to search within.
- `ItemKey Item`: The item to deliver.
- `int Quantity`: Quantity to deliver.
- `Guid SourceGuid`: GUID of the source entity to exclude.
- `NativeList<DeliveryDestination> Destinations`: Stores found destinations.
- `NativeList<LogEntry> Logs`: Logs for debugging.

**Methods:**

- `void Execute()`: Executes the job, finding suitable delivery destinations and logging results.

**Example:**

```csharp
var job = new FindDeliveryDestinationsJob
{
    Property = property,
    Item = new ItemKey(itemInstance),
    Quantity = 15,
    SourceGuid = sourceEntity.GUID,
    Destinations = new NativeList<DeliveryDestination>(Allocator.TempJob),
    Logs = new NativeList<LogEntry>(Allocator.TempJob)
};
var handle = job.Schedule();
handle.Complete()
```

---

### ReserveInputSlotsJob : IJobParallelFor

Validates and reserves input slots for an item in a Burst-compatible manner.

**Fields:**

- `NativeArray<SlotData> Slots` [ReadOnly]: Array of slot data to process.
- `ItemKey Item` [ReadOnly]: The item to reserve slots for.
- `NativeList<int> ReservedIndices`: Stores indices of reserved slots.
- `NativeList<LogEntry> Logs`: Logs for debugging.
- `bool AllowHigherQuality`: Whether to allow higher quality items.
- `int RemainingQuantity`: Quantity still needed to reserve.

**Methods:**

- `void Execute(int index)`: Processes a single slot, validating and reserving it if compatible.

**Example:**

```csharp
var slots = new NativeArray<SlotData>(slotList.Count, Allocator.TempJob);
for (int i = 0; i < slotList.Count; i++) slots[i] = new SlotData(slotList[i]);
var job = new ReserveInputSlotsJob
{
    Slots = slots,
    Item = new ItemKey(itemInstance),
    ReservedIndices = new NativeList<int>(Allocator.TempJob),
    Logs = new NativeList<LogEntry>(Allocator.TempJob),
    AllowHigherQuality = true,
    RemainingQuantity = itemInstance.Quantity
};
var handle = job.Schedule(slotList.Count, 64);
handle.Complete()
```

---

### StackCheckJob : IJobParallelFor

Checks if items can stack with a target item in a Burst-compatible manner.

**Fields:**

- `NativeArray<ItemKey> Items` [ReadOnly]: Array of items to check.
- `ItemKey Target` [ReadOnly]: The target item to compare against.
- `NativeArray<bool> Results`: Stores the results of stacking checks.
- `bool AllowHigherQuality`: Whether to allow higher quality items.

**Methods:**

- `void Execute(int index)`: Checks if the item at the given index can stack with the target item.

**Example:**

```csharp
var items = new NativeArray<ItemKey>(itemList.Count, Allocator.TempJob);
for (int i = 0; i < itemList.Count; i++) items[i] = new ItemKey(itemList[i]);
var results = new NativeArray<bool>(itemList.Count, Allocator.TempJob);
var job = new StackCheckJob
{
    Items = items,
    Target = new ItemKey(targetItem),
    Results = results,
    AllowHigherQuality = true
};
var handle = job.Schedule(itemList.Count, 64);
handle.Complete()
```

---

## Utilities (Static Class)

Provides utility methods for storage-related operations, often using Burst jobs for performance.

### AdvReserveInputSlotsForItemAsync

Reserves input slots for an item using a Burst job.

**Parameters:**

- `List<ItemSlot> slots`: The slots to reserve.
- `Guid disparity: Guid entityGuid`: GUID of the entity reserving the slots.
- `ItemInstance item`: The item to reserve slots for.
- `NetworkObject locker`: The network object locking the slots.
- `bool allowHigherQuality`: Whether to allow higher quality items.

**Returns:**

- `Task<List<ItemSlot>>`: List of reserved slots.

**Example:**

```csharp
var reservedSlots = await Utilities.AdvReserveInputSlotsForItemAsync(slots, entityGuid, itemInstance, locker, true);
foreach (var slot in reservedSlots)
{
    Debug.Log($"Reserved slot {slot.SlotIndex}");
}
```

---

### FindStorageWithItemAsync

Finds a storage shelf with the specified item using a Burst job.

**Parameters:**

- `NPC npc`: The NPC requesting the storage.
- `ItemInstance targetItem`: The item to find.
- `int needed`: Minimum quantity required.
- `int wanted`: Desired quantity (optional).
- `bool allowHigherQuality`: Whether to allow higher quality items.

**Returns:**

- `Task<KeyValuePair<PlaceableStorageEntity, int>>`: The found shelf and available quantity.

**Example:**

```csharp
var result = await Utilities.FindStorageWithItemAsync(npc, targetItem, 10, 20, true);
if (result.Key != null)
{
    Debug.Log($"Found shelf {result.Key.GUID} with {result.Value} items");
}
```

---

### FindStoragesForDelivery

Finds storage shelves for delivering an item using a Burst job.

**Parameters:**

- `Property property`: The property to search within.
- `ItemInstance item`: The item to deliver.
- `int qtyToDeliver`: Quantity to deliver.
- `bool allowAnyShelves`: Whether to allow non-specific shelves.
- `ITransitEntity source`: The source entity (optional).

**Returns:**

- `List<PlaceableStorageEntity>`: List of suitable shelves.

**Example:**

```csharp
var shelves = Utilities.FindStoragesForDelivery(property, itemInstance, 15, true, sourceEntity);
foreach (var shelf in shelves)
{
    Debug.Log($"Delivery shelf: {shelf.GUID}");
}
```

---

### AdvRemoveItemAsync

Removes an item from a slot asynchronously.

**Parameters:**

- `ItemSlot slot`: The slot to remove from.
- `int amount`: Quantity to remove.
- `Guid entityGuid`: GUID of the entity performing the operation.
- `Employee employee`: The employee performing the operation.

**Returns:**

- `Task<(bool success, ItemInstance item)>`: Success status and the removed item.

**Example:**

```csharp
var (success, item) = await slot.AdvRemoveItemAsync(5, entityGuid, employee);
if (success)
{
    Debug.Log($"Removed {item.ID} from slot");
}
```

---

### AdvInsertItemAsync

Inserts an item into a slot asynchronously.

**Parameters:**

- `ItemSlot slot`: The slot to insert into.
- `ItemInstance item`: The item to insert.
- `int amount`: Quantity to insert.
- `Guid entityGuid`: GUID of the entity performing the operation.
- `Employee employee`: The employee performing the operation.

**Returns:**

- `Task<bool>`: Whether the insertion was successful.

**Example:**

```csharp
var success = await slot.AdvInsertItemAsync(itemInstance, 5, entityGuid, employee);
if (success)
{
    Debug.Log($"Inserted {itemInstance.ID} into slot");
}
```

---

### GetOutputSlotsContainingItem

Retrieves output slots containing items matching the template item.

**Parameters:**

- `ITransitEntity entity`: The entity containing the slots.
- `ItemInstance item`: The template item.
- `bool allowHigherQuality`: Whether to allow higher quality items.

**Returns:**

- `List<ItemSlot>`: List of matching slots.

**Example:**

```csharp
var slots = Utilities.GetOutputSlotsContainingItem(entity, itemInstance, true);
foreach (var slot in slots)
{
    Debug.Log($"Found slot with {slot.ItemInstance.ID}, quantity={slot.Quantity}");
}
```

---

### FindPackagingStation

Finds a packaging station suitable for an item.

**Parameters:**

- `Property property`: The property to search within.
- `ItemInstance item`: The item to package.

**Returns:**

- `ITransitEntity`: The found packaging station or null.

**Example:**

```csharp
var station = Utilities.FindPackagingStation(property, itemInstance);
if (station != null)
{
    Debug.Log($"Found packaging station {station.GUID}");
}
```

---

### AdvCanStackWith

Checks if two items can stack based on ID, packaging, quality, and quantity.

**Parameters:**

- `ItemInstance item`: The source item.
- `ItemInstance targetItem`: The target item.
- `bool allowHigherQuality`: Whether to allow higher quality items.
- `bool checkQuantities`: Whether to check stack limits.

**Returns:**

- `bool`: Whether the items can stack.

**Example:**

```csharp
var canStack = itemInstance.AdvCanStackWith(targetItem, true, false);
Debug.Log($"Items can stack: {canStack}")
```

---

### UpdateStorageCache

Schedules an update to the storage cache for a shelf.

**Parameters:**

- `PlaceableStorageEntity shelf`: The shelf to update.

**Example:**

```csharp
Utilities.UpdateStorageCache(shelf);
Debug.Log($"Queued storage cache update for shelf {shelf.GUID}")
```

---

### UpdateStationCache

Schedules an update to the station cache for a station.

**Parameters:**

- `IStationAdapter station`: The station to update.

**Example:**

```csharp
Utilities.UpdateStationCache(station);
Debug.Log($"Queued station cache update for station {station.GUID}")
```

---

### GetItemQuantityInStorage

Gets the total quantity of an item in a shelf's output slots.

**Parameters:**

- `PlaceableStorageEntity shelf`: The shelf to query.
- `ItemInstance targetItem`: The item to count.
- `bool allowHigherQuality`: Whether to allow higher quality items.

**Returns:**

- `int`: Total quantity found.

**Example:**

```csharp
var quantity = shelf.GetItemQuantityInStorage(targetItem, true);
Debug.Log($"Found {quantity} of {targetItem.ID} in shelf")
```

---

### UpdateStorageConfiguration

Updates the storage configuration for a shelf.

**Parameters:**

- `PlaceableStorageEntity shelf`: The shelf to configure.
- `ItemInstance assignedItem`: The item to assign (optional).

**Example:**

```csharp
Utilities.UpdateStorageConfiguration(shelf, itemInstance);
Debug.Log($"Updated configuration for shelf {shelf.GUID}")
```

---

## Extensions (Static Class)

Contains structs and enums for storage operations.

### CacheKey : IEquatable<CacheKey>

A struct for caching item searches.

**Fields:**

- `ItemInstance Item`: The item instance.
- `string ID`: Item ID.
- `string PackagingId`: Packaging ID (optional).
- `EQuality? Quality`: Item quality (optional).
- `Property Property`: The associated property.

**Methods:**

- `bool Equals(CacheKey other)`: Compares two cache keys.
- `int GetHashCode()`: Generates a hash code.

**Example:**

```csharp
var cacheKey = new CacheKey(itemInstance.ID, packagingId, quality, property);
Debug.Log($"Cache key created for {cacheKey.ID}")
```

---

### StorageTypes (Enum)

Defines types of storage entities.

**Values:**

- `Employee`
- `AllShelves`
- `AnyShelf`
- `SpecificShelf`
- `Station`
- `LoadingDock`

---

### SlotData

A struct for slot data in Burst jobs.

**Fields:**

- `int SlotIndex`: Slot index.
- `ItemKey Item`: Item in the slot.
- `int Quantity`: Item quantity.
- `bool IsLocked`: Whether the slot is locked.
- `int StackLimit`: Maximum stack size.
- `bool IsValid`: Whether the slot is valid.

**Example:**

```csharp
var slotData = new SlotData(slot);
Debug.Log($"Slot data: {slotData.SlotIndex}, {slotData.Quantity}")
```

---

### StorageKey : IEquatable<StorageKey>

A struct for identifying storage entities.

**Fields:**

- `Guid Guid`: Entity GUID.
- `StorageTypes Type`: Type of storage.

**Methods:**

- `bool Equals(StorageKey other)`: Compares two storage keys.
- `int GetHashCode()`: Generates a hash code.

**Example:**

```csharp
var storageKey = new StorageKey(entity.GUID, StorageTypes.SpecificShelf);
Debug.Log($"Storage key: {storageKey.Guid}, {storageKey.Type}")
```

---

### ItemKey : IEquatable<ItemKey>

A struct for item identification in Burst jobs.

**Fields:**

- `FixedString32Bytes Id`: Item ID.
- `FixedString32Bytes PackagingId`: Packaging ID.
- `EQualityBurst Quality`: Item quality.
- `static ItemKey Empty`: An empty item key.

**Methods:**

- `ItemKey(ItemInstance item)`: Constructor from an item instance.
- `ItemKey(string id, string packagingId, EQualityBurst? quality)`: Constructor with specific values.
- `bool Equals(ItemKey other)`: Compares two item keys.
- `int GetHashCode()`: Generates a hash code.
- `ItemInstance CreateItemInstance()`: Creates an item instance from the key.
- `bool AdvCanStackWithBurst(ItemKey targetItem, bool allowHigherQuality, bool checkQuantities, int stackLimit, int itemQuantity, int targetQuantity)`: Checks if items can stack (Burst-compatible).

**Example:**

```csharp
var itemKey = new ItemKey(itemInstance);
var canStack = itemKey.AdvCanStackWithBurst(new ItemKey(targetItem), true);
Debug.Log($"Item key can stack: {canStack}")
```

---

### StorageResult

A struct for storage search results in Burst jobs.

**Fields:**

- `Guid ShelfGuid`: Shelf GUID.
- `int AvailableQuantity`: Available item quantity.
- `NativeList<int> SlotIndices`: Indices of slots containing the item.

**Example:**

```csharp
var result = new StorageResult { ShelfGuid = Guid.NewGuid(), AvailableQuantity = 10 };
Debug.Log($"Storage result: {result.ShelfGuid}, {result.AvailableQuantity}")
```

---

### DeliveryDestination

A struct for delivery destination results in Burst jobs.

**Fields:**

- `Guid DestinationGuid`: Destination GUID.
- `NativeList<int> SlotIndices`: Slot indices for delivery.
- `int Capacity`: Available capacity.

**Example:**

```csharp
var destination = new DeliveryDestination { DestinationGuid = Guid.NewGuid(), Capacity = 20 };
Debug.Log($"Delivery destination: {destination.DestinationGuid}, {destination.Capacity}")
```

---

## Constants (Static Class)

Holds static storage-related constants and collections.

**Fields:**

- `ConcurrentDictionary<Property, CacheManager> CacheManagers`: Cache managers per property.
- `Dictionary<int, Property> IdToProperty`: Maps property IDs to properties.
- `Dictionary<Guid, JObject> PendingConfigData`: Pending configuration data.
- `Dictionary<Property, Dictionary<Guid, StorageConfiguration>> StorageConfigs`: Storage configurations.
- `Dictionary<Property, Dictionary<Guid, PlaceableStorageEntity>> Storages`: Storage entities.
- `Dictionary<Property, List<PlaceableStorageEntity>> AnyShelves`: Shelves accepting any items.
- `Dictionary<Property, Dictionary<ItemInstance, List<PlaceableStorageEntity>>> SpecificShelves`: Shelves for specific items.
- `ConcurrentDictionary<ItemInstance, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>> ShelfCache`: Cached shelf data.
- `Dictionary<Property, Dictionary<Guid, IEmployeeAdapter>> IEmployees`: Employee adapters.
- `Dictionary<Property, Dictionary<Guid, IStationAdapter>> IStations`: Station adapters.
- `Dictionary<Guid, List<ItemInstance>> StationRefillLists`: Station refill lists.
- `ObjectPool<List<ItemSlot>> SlotListPool`: Pool for slot lists.

**Example:**

```csharp
var cacheManager = Constants.CacheManagers.GetOrAdd(property, p => new CacheManager(p));
Debug.Log($"Cache manager for property: {property.name}")
```

---

## CacheManager (Class)

Manages storage and station-related caches with thread-safe access.

**Fields:**

- `Property _property`: The associated property.
- `bool _isProcessingUpdates`: Whether updates are being processed.
- `bool _isActive`: Whether the cache manager is active.
- `ObjectPool<List<KeyValuePair<PlaceableStorageEntity, int>>> _shelfListPool`: Pool for shelf lists.
- `ObjectPool<Dictionary<ItemInstance, int>> _itemQuantitiesPool`: Pool for item quantities.
- `Dictionary<(Guid, Guid), float> _travelTimeCache`: Cached travel times.
- `Dictionary<Guid, ITransitEntity> _travelTimeEntities`: Cached transit entities.
- `ConcurrentDictionary<CacheKey, bool> _itemNotFoundCache`: Cache for items not found.
- `ConcurrentDictionary<CacheKey, List<KeyValuePair<PlaceableStorageEntity, int>>> _shelfSearchCache`: Cache for shelf searches.
- `ConcurrentDictionary<ItemInstance, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>> _shelfCache`: Cache for shelf data.
- `ConcurrentDictionary<EQuality?, List<KeyValuePair<PlaceableStorageEntity, ShelfInfo>>> _anyQualityCache`: Cache for any quality items.
- `ConcurrentQueue<PlaceableStorageEntity> _pendingUpdates`: Pending shelf updates.
- `ConcurrentQueue<IStationAdapter> _pendingStationUpdates`: Pending station updates.
- `NativeParallelHashMap<ItemKey, NativeList<StorageKey>> _specificShelvesCache`: Cache for specific shelves.
- `Dictionary<Guid, StorageKey> _loadingDockKeys`: Loading dock keys.
- `NativeParallelHashMap<StorageKey, NativeList<SlotData>> _anyShelfSlotsCache`: Cache for any shelf slots.
- `NativeParallelHashMap<StorageKey, NativeList<SlotData>> _specificShelfSlotsCache`: Cache for specific shelf slots.
- `NativeParallelHashMap<StorageKey, NativeList<SlotData>> _stationSlotsCache`: Cache for station slots.
- `NativeParallelHashMap<StorageKey, NativeList<SlotData>> _loadingDockSlotsCache`: Cache for loading dock slots.
- `NativeParallelHashMap<StorageKey, NativeList<SlotData>> _employeeSlotsCache`: Cache for employee slots.
- `ConcurrentQueue<(StorageKey key, NativeList<SlotData> slots)> _pendingSlotUpdates`: Pending slot updates.

**Methods:**

- `CacheManager(Property property)`: Constructor.
- `static CacheManager GetOrCreateCacheManager(Property property)`: Gets or creates a cache manager.
- `void Activate()`: Activates the cache manager.
- `void Deactivate()`: Deactivates the cache manager.
- `void Cleanup()`: Cleans up caches.
- `void RegisterItemSlot(ItemSlot slot, StorageKey parentKey)`: Registers a slot for updates.
- `void UnregisterItemSlot(ItemSlot slot, StorageKey parentKey)`: Unregisters a slot.
- `void QueueSlotUpdate(StorageKey key, IEnumerable<SlotData> slots)`: Queues a slot update.
- `static void InitializePropertyIdMap()`: Initializes property ID map.
- `static void AddOrUpdatePropertyToIdMap(Property property)`: Adds or updates a property in the ID map.
- `static void RemovePropertyFromIdMap(Property property)`: Removes a property from the ID map.
- `static Property GetPropertyById(int id)`: Gets a property by ID.
- `bool TryGetShelfSlots(StorageKey shelfKey, out NativeList<SlotData> slots)`: Gets shelf slots.
- `bool TryGetStationSlots(StorageKey stationKey, out NativeList<SlotData> slots)`: Gets station slots.
- `bool TryGetLoadingDockSlots(StorageKey dockKey, out NativeList<SlotData> slots)`: Gets loading dock slots.
- `bool TryGetEmployeeSlots(StorageKey employeeKey, out NativeList<SlotData> slots)`: Gets employee slots.
- `bool TryFindStorageWithItem(Property property, ItemKey targetItem, int needed, out Guid shelfGuid, out int availableQty, out NativeList<int> slotIndices)`: Finds storage with an item.
- `bool TryGetDeliveryDestinations(Property property, ItemKey item, int quantity, Guid sourceGuid, out NativeList<(Guid, NativeList<int>, int)> destinations)`: Finds delivery destinations.
- `float GetTravelTime(ITransitEntity entityA, ITransitEntity entityB)`: Gets travel time between entities.
- `Dictionary<(Guid, Guid), float> GetTravelTimeCache(Guid source)`: Gets travel time cache.
- `void UpdateTravelTimeCache(Guid sourceGuid, Guid destGuid, float travelTime)`: Updates travel time cache.
- `void ClearCacheForEntity(Guid entityGuid)`: Clears cache for an entity.
- `Task<(bool, List<IStationAdapter>, List<PlaceableStorageEntity>)> TryGetPropertyDataAsync()`: Gets property data asynchronously.
- `(bool, List<IStationAdapter>, List<PlaceableStorageEntity>) TryGetPropertyData()`: Gets property data synchronously.
- `void UpdatePropertyDataCache()`: Updates property data cache.
- `void QueueStationUpdate(IStationAdapter station)`: Queues a station update.
- `void QueueStorageUpdate(PlaceableStorageEntity shelf)`: Queues a shelf update.
- `void ClearItemNotFoundCache(ItemInstance item)`: Clears item not found cache.
- `void ClearZeroQuantityEntries(PlaceableStorageEntity shelf, IEnumerable<ItemInstance> itemsToRemove)`: Clears zero-quantity entries.
- `bool IsItemNotFound(CacheKey key)`: Checks if an item is not found.
- `void AddItemNotFound(CacheKey key)`: Marks an item as not found.
- `bool TryGetCachedShelves(CacheKey key, out List<KeyValuePair<PlaceableStorageEntity, int>> shelves)`: Gets cached shelves.
- `void UpdateShelfSearchCache(CacheKey key, PlaceableStorageEntity shelf, int quantity)`: Updates shelf search cache.
- `void RemoveShelfSearchCacheEntries(IEnumerable<CacheKey> keys)`: Removes shelf search cache entries.

**Example:**

```csharp
var cacheManager = CacheManager.GetOrCreateCacheManager(property);
cacheManager.Activate();
cacheManager.QueueStorageUpdate(shelf);
Debug.Log($"Cache manager activated for {property.name}")
```

---

## CacheManagerPatches (Static Class)

Contains Harmony patches for integrating with other systems.

### PropertyPatch

Patches `Property` class.

**Methods:**

- `static void SetOwnedPostfix(Property __instance)`: Activates cache manager when property is owned.

---

### LoadingDockPatch

Patches `LoadingDock` class.

**Methods:**

- `static void LoadingDock_SetOccupant_Postfix(LoadingDock __instance, LandVehicle occupant)`: Updates slot registrations for loading dock.

---

### GridItemPatch

Patches `GridItem` class.

**Methods:**

- `static bool InitializeGridItemPrefix(GridItem __instance, ItemInstance instance, Grid grid, Vector2 originCoordinate, int rotation, string GUID)`: Initializes storage for grid items.
- `static void DestroyItemPostfix(GridItem __instance, bool callOnServer)`: Cleans up cache for destroyed items.

**Example:**

---

### ItemSlotPatch

Patches `ItemSlot` class.

**Methods:**

- `static void ChangeQuantityPostfix(ItemSlot __instance, int change, bool _internal)`: Updates cache on quantity change.
- `static void SetStoredItemPostfix(ItemSlot __instance, ItemInstance instance, bool _internal)`: Updates cache on item set.
- `static void ClearStoredInstancePostfix(ItemSlot __instance, bool _internal)`: Updates cache on item clear.
