using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using Unity.Collections;
using static NoLazyWorkers.Storage.ShelfExtensions;
using NoLazyWorkers.Performance;
using Unity.Burst;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using ScheduleOne.Delivery;
using HarmonyLib;
using ScheduleOne.Vehicles;
using ScheduleOne.EntityFramework;
using static NoLazyWorkers.Storage.ShelfConstants;
using static NoLazyWorkers.Storage.ManagedDictionaries;
using static NoLazyWorkers.Employees.Extensions;
using Unity.Collections.LowLevel.Unsafe;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using UnityEngine.Pool;
using NoLazyWorkers.Extensions;
using FishNet.Object;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using Newtonsoft.Json.Linq;
using NoLazyWorkers.TaskService;

namespace NoLazyWorkers.Storage
{
  public static class ManagedDictionaries
  {
    internal static readonly Dictionary<Property, CacheService> CacheServices = new();
    public static Dictionary<int, Property> IdToProperty = new();
    public static Dictionary<Property, Dictionary<Guid, StorageConfiguration>> StorageConfigs = new();
    public static Dictionary<Property, Dictionary<Guid, PlaceableStorageEntity>> Storages = new();
    public static Dictionary<Property, Dictionary<Guid, LoadingDock>> LoadingDocks = new();
    public static Dictionary<Property, Dictionary<Guid, IStationAdapter>> IStations = new();
    public static Dictionary<Property, Dictionary<Guid, IEmployeeAdapter>> IEmployees = new();
    public static Dictionary<Guid, JObject> PendingConfigData = new();
    public static readonly Dictionary<Property, EntityDisableService> DisabledEntityServices = new();
  }

  public class CacheService : IDisposable
  {
    private readonly Property _property;
    private bool _isActive;
    private bool IsInitialized { get; set; }
    internal readonly ObjectPool<List<StorageResultBurst>> _storageResultPool;
    internal readonly ObjectPool<List<DeliveryDestinationBurst>> _deliveryResultPool;
    internal NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)> _slotCache;
    internal NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> _itemToSlots;
    internal NativeParallelHashSet<ItemKey> _notFoundItems;
    internal NativeParallelHashSet<ItemKey> _noDropOffCache;
    internal NativeList<Guid> _entityGuids;
    internal NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> _guidToType;
    internal NativeParallelHashMap<Guid, int> _entityStates;
    internal NativeParallelHashMap<Guid, NativeList<SlotData>> _slotsCache;
    internal NativeParallelHashMap<Guid, LoadingDockData> _loadingDockDataCache;
    internal NativeParallelHashMap<Guid, EmployeeData> _employeeDataCache;
    internal NativeParallelHashMap<Guid, StationData> _stationDataCache;
    internal NativeParallelHashMap<Guid, StorageData> _storageDataCache;
    internal NativeList<SlotUpdate> _pendingSlotUpdates;

    public CacheService(Property property)
    {
      _loadingDockDataCache = new NativeParallelHashMap<Guid, LoadingDockData>(10, Allocator.Persistent);
      _property = property ?? throw new ArgumentNullException(nameof(property));
      _slotsCache = new NativeParallelHashMap<Guid, NativeList<SlotData>>(100, Allocator.Persistent);
      _itemToSlots = new NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)>(100, Allocator.Persistent);
      _slotCache = new NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)>(100, Allocator.Persistent);
      _entityGuids = new NativeList<Guid>(100, Allocator.Persistent);
      _guidToType = new NativeParallelHashMap<Guid, (EntityType, StorageType)>(100, Allocator.Persistent);
      _notFoundItems = new NativeParallelHashSet<ItemKey>(100, Allocator.Persistent);
      _noDropOffCache = new NativeParallelHashSet<ItemKey>(100, Allocator.Persistent);
      _pendingSlotUpdates = new NativeList<SlotUpdate>(100, Allocator.Persistent);
      _storageResultPool = new ObjectPool<List<StorageResultBurst>>(
          createFunc: () => new List<StorageResultBurst>(10),
          actionOnGet: null,
          actionOnRelease: list => list.Clear(),
          actionOnDestroy: null,
          collectionCheck: false,
          defaultCapacity: 10,
          maxSize: 100);
      _deliveryResultPool = new ObjectPool<List<DeliveryDestinationBurst>>(
          createFunc: () => new List<DeliveryDestinationBurst>(10),
          actionOnGet: null,
          actionOnRelease: list => list.Clear(),
          actionOnDestroy: null,
          collectionCheck: false,
          defaultCapacity: 10,
          maxSize: 100);
      CoroutineRunner.Instance.RunCoroutine(InitializePropertyDataCaches());
      IsInitialized = true;
      Log(Level.Info, $"CacheService initialized for {_property.name}", Category.Storage);
    }

    /// <summary>
    /// Activates the cache service, enabling update processing.
    /// </summary>
    public void Activate()
    {
      if (_isActive) return;
      _isActive = true;
      foreach (var loadingDock in _property.LoadingDocks)
        LoadingDocks[_property].Add(loadingDock.GUID, loadingDock);
      Log(Level.Info, $"[{_property.name}] CacheService activated", Category.Tasks);
    }

    /// <summary>
    /// Deactivates the cache service, stopping update processing.
    /// </summary>
    public void Deactivate()
    {
      if (!_isActive) return;
      _isActive = false;
      Cleanup();
      Log(Level.Info, $"[{_property.name}] CacheService deactivated", Category.Tasks);
    }

    public void Dispose()
    {
      if (!IsInitialized)
      {
        Log(Level.Warning, $"CacheService for property {_property} not initialized, skipping dispose", Category.Storage);
        return;
      }
      Cleanup();
      if (_loadingDockDataCache.IsCreated)
      {
        foreach (var kvp in _loadingDockDataCache)
          kvp.Value.Dispose();
        _loadingDockDataCache.Dispose();
      }
      if (_slotsCache.IsCreated)
      {
        var slotsArray = _slotsCache.GetValueArray(Allocator.Temp);
        foreach (var slots in slotsArray) slots.Dispose();
        _slotCache.Dispose();
        slotsArray.Dispose();
        _slotsCache.Dispose();
      }
      if (_itemToSlots.IsCreated)
      {
        _itemToSlots.Dispose();
      }
      if (_entityGuids.IsCreated) _entityGuids.Dispose();
      if (_guidToType.IsCreated) _guidToType.Dispose();
      if (_notFoundItems.IsCreated) _notFoundItems.Dispose();
      if (_noDropOffCache.IsCreated) _noDropOffCache.Dispose();
      if (_pendingSlotUpdates.IsCreated) _pendingSlotUpdates.Dispose();
      _storageResultPool.Clear();
      _deliveryResultPool.Clear();
      IsInitialized = false;
      Log(Level.Info, $"CacheService disposed for property {_property.name}", Category.Storage);
    }

    /// <summary>
    /// Cleans up pending updates and event subscriptions.
    /// </summary>
    private void Cleanup()
    {
      _pendingSlotUpdates.Clear();
      Log(Level.Info, "CacheService cleaned up", Category.Storage);
    }

    private IEnumerator ProcessPendingSlotUpdates()
    {
      while (_isActive)
      {
        if (_pendingSlotUpdates.Length > 0)
        {
          var logs = new NativeList<LogEntry>(Allocator.TempJob);
          var updates = new NativeArray<SlotUpdate>(_pendingSlotUpdates.AsArray(), Allocator.TempJob);
          _pendingSlotUpdates.Clear();
          yield return SmartExecution.ExecuteBurstFor<SlotUpdate, SlotData>(
              uniqueId: nameof(ProcessPendingSlotUpdates),
              itemCount: updates.Length,
              burstForDelegate: (index, inputs, outputs, logs) => UpdateStorageCacheBurst(index, inputs, outputs, _slotsCache, _itemToSlots, _entityGuids, _notFoundItems, logs)
          );
          yield return ProcessLogs(logs);
          updates.Dispose();
          logs.Dispose();
        }
        yield return null;
      }
    }

    // Add method to consolidate slot update logic
    public IEnumerator UpdateSlot(ItemSlot slot, Property property, ItemInstance instance, int change, bool isClear, bool isSet, NativeParallelHashMap<Guid, DisabledEntityData> disabledEntities)
    {
      if (slot.SlotOwner is NPC && slot.SlotOwner is not Employee) yield break;
      if (property == null || (isClear && slot.ItemInstance == null) || (isSet && instance == null)) yield break;

      var slotKey = slot.GetSlotKey();
      var guid = (slot.SlotOwner is BuildableItem) ? (slot.SlotOwner as BuildableItem).GUID : slot.SlotOwner is LoadingDock loadingDock ? loadingDock.GUID : (slot.SlotOwner as Employee).GUID;
      var type = _guidToType[guid];
      _slotCache[slotKey] = (property.NetworkObject.ObjectId, type.StorageType, slot.SlotIndex);
      var slotData = new SlotData(slotKey.EntityGuid, slot, type.StorageType);
      _pendingSlotUpdates.Add(new SlotUpdate
      {
        OwnerGuid = slotKey.EntityGuid,
        SlotData = slotData
      });

      string logMessage = isClear
          ? $"ClearStoredInstancePostfix: Slot={slot.SlotIndex}, entity={slotKey.EntityGuid}, type={type.EntityType}, storageType={type.StorageType}"
          : isSet
              ? $"SetStoredItemPostfix: Slot={slot.SlotIndex}, item={instance.ID}{(instance is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}, entity={slotKey.EntityGuid}, type={type}"
              : $"ChangeQuantityPostfix: Slot={slot.SlotIndex}, item={slot.ItemInstance.ID}{(slot.ItemInstance is QualityItemInstance qual1 ? $" Quality={qual1.Quality}" : "")}, change={change}, newQty={slot.Quantity}, entity={slotKey.EntityGuid}, type={type}";
      Log(Level.Info, logMessage, Category.Storage);

      if ((isSet || (change > 0 && !isClear)) && slot.ItemInstance != null)
        ClearItemNotFoundCache(new ItemKey(slot.ItemInstance));

      if (slot.SlotOwner is PlaceableStorageEntity shelf && slot.Quantity <= 0 && slot.ItemInstance != null)
        yield return ClearZeroQuantityEntries(shelf, new[] { slot.ItemInstance });

      yield return HandleSlotUpdate(slotKey.EntityGuid, slotData, disabledEntities);
      yield return StorageManager.UpdateStorageCache(property, slotKey.EntityGuid, [slot], type.StorageType);
    }

    public IEnumerator HandleSlotUpdate(Guid ownerGuid, SlotData slotData, NativeParallelHashMap<Guid, DisabledEntityData> disabledEntities)
    {
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      var keysToRemove = new NativeList<Guid>(Allocator.TempJob);
      var inputs = new NativeArray<SlotUpdate>([new SlotUpdate() { OwnerGuid = ownerGuid, SlotData = slotData }], Allocator.TempJob);
      var handleSlotUpdateBurst = new HandleSlotUpdateBurst
      {
        DisabledEntities = disabledEntities,
        KeysToRemove = keysToRemove
      };
      yield return SmartExecution.ExecuteBurst<SlotUpdate, Empty>(
          uniqueId: nameof(HandleSlotUpdate),
          burstDelegate: handleSlotUpdateBurst.Execute
      );

      foreach (var guid in keysToRemove)
      {
        if (disabledEntities.TryGetValue(guid, out var data))
        {
          if (data.RequiredItems.IsCreated)
            data.RequiredItems.Dispose();
          disabledEntities.Remove(guid);
        }
      }

      yield return ProcessLogs(logs);
      keysToRemove.Dispose();
      logs.Dispose();
    }

    /// <summary>
    /// Retrieves or creates a CacheService for a given property.
    /// </summary>
    /// <param name="property">The property to associate with the cache service.</param>
    /// <returns>The CacheService instance, or null if not on server.</returns>
    public static CacheService GetOrCreateCacheService(Property property)
    {
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Error, "CacheService creation skipped: not server", Category.Storage);
        return null;
      }
      if (!CacheServices.TryGetValue(property, out var cacheService) || !cacheService.IsInitialized)
      {
        cacheService = new CacheService(property);
        CacheServices[property] = cacheService;
      }
      return cacheService;
    }

    /// <summary>
    /// Updates the storage cache for a slot in a Burst-compatible manner.
    /// </summary>
    /// <param name="index">The index of the slot to process.</param>
    /// <param name="inputs">The array of slot data.</param>
    /// <param name="key">The storage key.</param>
    /// <param name="propertyId">The ID of the property.</param>
    /// <param name="storageSlotsCache">The cache of storage slots.</param>
    /// <param name="anyShelfKeys">The list of any shelf keys.</param>
    /// <param name="notFoundItems">The set of items not found.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    internal void UpdateStorageCacheBurst(
            int index,
            NativeArray<SlotUpdate> inputs,
            NativeList<SlotData> outputs,
            NativeParallelHashMap<Guid, NativeList<SlotData>> storageSlotsCache,
            NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> itemToSlotIndices,
            NativeList<Guid> anyShelfKeys,
            NativeParallelHashSet<ItemKey> notFoundItems,
            NativeList<LogEntry> logs)
    {
      var update = inputs[index];
      var guid = update.OwnerGuid;
      var slotData = update.SlotData;

      // Update storage slots cache
      if (!storageSlotsCache.TryGetValue(guid, out var slots))
      {
        slots = new NativeList<SlotData>(Allocator.Persistent);
        storageSlotsCache[guid] = slots;
      }
      if (slotData.IsValid)
      {
        bool found = false;
        for (int i = 0; i < slots.Length; i++)
        {
          if (slots[i].SlotIndex == slotData.SlotIndex)
          {
            slots[i] = slotData;
            found = true;
            break;
          }
        }
        if (!found)
          slots.Add(slotData);
      }
      else
      {
        for (int i = 0; i < slots.Length; i++)
        {
          if (slots[i].SlotIndex == slotData.SlotIndex)
          {
            slots.RemoveAtSwapBack(i);
            break;
          }
        }
      }

      // Update item-to-slot indices
      if (slotData.IsValid && slotData.Item.Id != "")
      {
        var itemKey = new ItemKey(slotData.Item);
        var tempEntries = new NativeList<(Guid, int)>(Allocator.Temp);

        // Collect existing entries, excluding the current slot if it exists
        var enumerator = itemToSlotIndices.GetValuesForKey(itemKey);
        while (enumerator.MoveNext())
        {
          var entry = enumerator.Current;
          if (entry.Guid != guid || entry.Quantity != slotData.SlotIndex)
          {
            tempEntries.Add(entry);
          }
        }
        enumerator.Dispose();

        // Add the new slot index
        tempEntries.Add((guid, slotData.SlotIndex));

        // Update the map
        itemToSlotIndices.Remove(itemKey);
        foreach (var entry in tempEntries)
        {
          itemToSlotIndices.Add(itemKey, entry);
        }

        notFoundItems.Remove(itemKey);

        tempEntries.Dispose();
      }
      else if (!slotData.IsValid || slotData.Item.Id == "")
      {
        var itemKey = new ItemKey(slotData.Item);
        var tempEntries = new NativeList<(Guid, int)>(Allocator.Temp);

        // Collect entries, excluding the slot to remove
        var enumerator = itemToSlotIndices.GetValuesForKey(itemKey);
        while (enumerator.MoveNext())
        {
          var entry = enumerator.Current;
          if (entry.Guid != guid || entry.Quantity != slotData.SlotIndex)
          {
            tempEntries.Add(entry);
          }
        }
        enumerator.Dispose();

        // Update the map
        itemToSlotIndices.Remove(itemKey);
        foreach (var entry in tempEntries)
        {
          itemToSlotIndices.Add(itemKey, entry);
        }

        tempEntries.Dispose();
      }

#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Updated cache for slot {slotData.SlotIndex} on {guid}, item={slotData.Item.Id}, qty={slotData.Quantity}",
        Level = Level.Verbose,
        Category = Category.Storage
      });
#endif
    }

    /// <summary>
    /// Checks if an item is marked as not found in the cache.
    /// </summary>
    /// <param name="cacheKey">The key of the item to check.</param>
    /// <returns>True if the item is marked as not found, false otherwise.</returns>
    internal bool IsItemNotFound(ItemKey cacheKey)
    {
      return _notFoundItems.Contains(cacheKey);
    }

    /// <summary>
    /// Clears the cache for a specific entity.
    /// </summary>
    /// <param name="guid">The GUID of the entity to clear from cache.</param>
    public void ClearCacheForEntity(Guid guid)
    {
      if (_slotsCache.TryGetValue(guid, out var slots))
      {
        slots.Dispose();
        _slotsCache.Remove(guid);
      }
#if DEBUG
      Log(Level.Verbose, $"Cleared cache for entity {guid}", Category.Storage);
#endif
    }

    private IEnumerator InitializePropertyDataCaches()
    {
      var slotKeys = new NativeList<SlotKeyData>(Allocator.TempJob);
      var outputs = new NativeList<SlotCacheEntry>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        var storages = ManagedDictionaries.Storages.TryGetValue(_property, out var storageDict) ? storageDict.Values.ToList() : new List<PlaceableStorageEntity>();
        foreach (var storage in storages)
        {
          if (ManagedDictionaries.StorageConfigs[_property].TryGetValue(storage.GUID, out var config) && config.Mode == StorageMode.None)
            continue;
          var storageType = config.Mode == StorageMode.Any ? StorageType.AnyShelf : StorageType.SpecificShelf;
          _guidToType[storage.GUID] = (EntityType.Storage, storageType);
          _entityGuids.Add(storage.GUID);
          var storageData = new StorageData(storage);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(storageData.OutputSlots);
          _slotsCache[storage.GUID] = slots;
          for (int i = 0; i < storage.OutputSlots.Count; i++)
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = storage.GUID,
              SlotIndex = i,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = storageType
            });
            if (storage.OutputSlots[i].ItemInstance != null)
              _itemToSlots.Add(new ItemKey(storage.OutputSlots[i].ItemInstance), (storage.GUID, i));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for storage {storage.GUID}, type={storageType}", Category.Storage);
#endif
        }
        var stations = ManagedDictionaries.IStations.TryGetValue(_property, out var stationDict) ? stationDict.Values.ToList() : new List<IStationAdapter>();
        foreach (var station in stations)
        {
          var entityKey = station.GUID;
          _guidToType[station.GUID] = (station.EntityType, StorageType.Station);
          _entityGuids.Add(station.GUID);
          var stationData = new StationData(station);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(stationData.InsertSlots);
          slots.AddRange(stationData.ProductSlots);
          slots.Add(stationData.OutputSlot);
          _slotsCache[station.GUID] = slots;
          foreach (var slot in station.InsertSlots.Concat(station.ProductSlots).Concat(new[] { station.OutputSlot }))
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = station.GUID,
              SlotIndex = slot.SlotIndex,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = StorageType.Station
            });
            if (slot.ItemInstance != null)
              _itemToSlots.Add(new ItemKey(slot.ItemInstance), (station.GUID, slot.SlotIndex));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for station {station.GUID}", Category.Storage);
#endif
        }
        var docks = ManagedDictionaries.LoadingDocks.TryGetValue(_property, out var dockDict) ? dockDict.Values.ToList() : new List<LoadingDock>();
        foreach (var dock in docks)
        {
          var entityKey = dock.GUID;
          _guidToType[dock.GUID] = (EntityType.LoadingDock, StorageType.LoadingDock);
          _entityGuids.Add(dock.GUID);
          var storageData = new LoadingDockData(dock);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(storageData.OutputSlots);
          _slotsCache[dock.GUID] = slots;
          for (int i = 0; i < dock.OutputSlots.Count; i++)
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = dock.GUID,
              SlotIndex = i,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = StorageType.LoadingDock
            });
            if (dock.OutputSlots[i].ItemInstance != null)
              _itemToSlots.Add(new ItemKey(dock.OutputSlots[i].ItemInstance), (dock.GUID, i));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for loading dock {dock.GUID}", Category.Storage);
#endif
        }
        var employees = ManagedDictionaries.IEmployees.TryGetValue(_property, out var employeeDict) ? employeeDict.Values.ToList() : new List<IEmployeeAdapter>();
        foreach (var employee in employees)
        {
          _guidToType[employee.Guid] = (employee.EntityType, StorageType.Employee);
          _entityGuids.Add(employee.Guid);
          var employeeData = new EmployeeData(employee);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(employeeData.InventorySlots);
          _slotsCache[employee.Guid] = slots;
          for (int i = 0; i < employee.InventorySlots.Count; i++)
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = employee.Guid,
              SlotIndex = i,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = StorageType.Employee
            });
            if (employee.InventorySlots[i].ItemInstance != null)
              _itemToSlots.Add(new ItemKey(employee.InventorySlots[i].ItemInstance), (employee.Guid, i));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for employee {employee.Guid}", Category.Storage);
#endif
        }
        yield return SmartExecution.ExecuteBurstFor<SlotKeyData, SlotCacheEntry>(
            uniqueId: nameof(InitializePropertyDataCaches),
            itemCount: slotKeys.Length,
            burstForDelegate: (index, inputs, outputs, logs) => ProcessSlotCacheItemBurst(index, inputs, outputs, logs),
            burstResultsDelegate: (outputs, logs) => ProcessSlotCacheItemResults(outputs, logs));
        yield return ProcessLogs(logs);
      }
      finally
      {
        if (slotKeys.IsCreated) slotKeys.Dispose();
        if (outputs.IsCreated) outputs.Dispose();
        if (logs.IsCreated) logs.Dispose();
      }
    }

    /// <summary>
    /// Processes a slot cache item in a Burst-compatible manner.
    /// </summary>
    /// <param name="index">The index of the slot to process.</param>
    /// <param name="inputs">The array of slot key data.</param>
    /// <param name="outputs">The list to store cache entries.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    private void ProcessSlotCacheItemBurst(
            int index,
            NativeArray<SlotKeyData> inputs,
            NativeList<SlotCacheEntry> outputs,
            NativeList<LogEntry> logs)
    {
      var keyData = inputs[index];
      outputs.Add(new SlotCacheEntry
      {
        Key = new SlotKey(keyData.EntityGuid, keyData.SlotIndex),
        PropertyId = keyData.PropertyId,
        Type = keyData.Type,
        SlotIndex = keyData.SlotIndex
      });
      logs.Add(new LogEntry
      {
        Message = $"Processed slot cache item for {keyData.EntityGuid}, slot {keyData.SlotIndex}",
        Level = Level.Info,
        Category = Category.Storage
      });
    }

    /// <summary>
    /// Processes results of slot cache item processing.
    /// </summary>
    /// <param name="results">The list of slot cache entries.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    private void ProcessSlotCacheItemResults(
            NativeList<SlotCacheEntry> results,
            NativeList<LogEntry> logs)
    {
      foreach (var entry in results)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Updating slot cache for {entry.Key.EntityGuid}, slot {entry.SlotIndex}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
        _slotCache[entry.Key] = (entry.PropertyId, entry.Type, entry.SlotIndex);
      }
      logs.Add(new LogEntry
      {
        Message = $"InitializePropertyDataCaches: Updated for {_property.name}, entities={results.Length}",
        Level = Level.Info,
        Category = Category.Storage
      });
    }

    /// <summary>
    /// Represents data for a slot key used in cache initialization.
    /// </summary>
    [BurstCompile]
    public struct SlotKeyData
    {
      public Guid EntityGuid;
      public int SlotIndex;
      public int PropertyId;
      public StorageType Type;
    }

    /// <summary>
    /// Represents a cache entry for a slot.
    /// </summary>
    [BurstCompile]
    public struct SlotCacheEntry
    {
      public SlotKey Key;
      public int PropertyId;
      public StorageType Type;
      public int SlotIndex;
    }

    /// <summary>
    /// Attempts to retrieve slots for a storage key from the cache.
    /// </summary>
    /// <param name="key">The storage key to retrieve slots for.</param>
    /// <param name="slots">The retrieved slots, if any.</param>
    /// <returns>True if slots were found, false otherwise.</returns>
    [BurstCompile]
    internal bool TryGetSlots(Guid key, out NativeList<SlotData> slots)
    {
      bool hit = _slotsCache.TryGetValue(key, out slots);
      Metrics.TrackCacheAccess("StorageSlotsCache", hit);
      return hit;
    }

    /// <summary>
    /// Marks an item as not found in the cache.
    /// </summary>
    /// <param name="cacheKey">The key of the item to mark as not found.</param>
    public void AddItemNotFound(ItemKey cacheKey)
    {
      _notFoundItems.Add(cacheKey);
#if DEBUG
      Log(Level.Verbose, $"Added {cacheKey.ToString()} to not found cache", Category.Storage);
#endif
    }

    /// <summary>
    /// Clears an item from the not found cache.
    /// </summary>
    /// <param name="item">The item to clear from the not found cache.</param>
    [BurstCompile]
    public void ClearItemNotFoundCache(ItemKey item)
    {
      _notFoundItems.Remove(item);
    }

    /// <summary>
    /// Adds an item to the no drop-off cache.
    /// </summary>
    /// <param name="item">The item to add to the no drop-off cache.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void AddNoDropOffCache(ItemKey item, NativeList<LogEntry> logs)
    {
      _noDropOffCache.Add(item);
#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Added {item.ToString()} to no drop-off cache",
        Level = Level.Verbose,
        Category = Category.Storage
      });
#endif
    }

    /// <summary>
    /// Removes an item from the no drop-off cache.
    /// </summary>
    /// <param name="item">The item to remove from the no drop-off cache.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void RemoveNoDropOffCache(ItemKey item, NativeList<LogEntry> logs)
    {
      _noDropOffCache.Remove(item);
#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Removed {item.ToString()} from no drop-off cache",
        Level = Level.Verbose,
        Category = Category.Storage
      });
#endif
    }

    /// <summary>
    /// Clears zero quantity entries from the shelf search cache.
    /// </summary>
    /// <param name="shelf">The storage entity to process.</param>
    /// <param name="items">The array of items to clear.</param>
    /// <returns>An enumerator for asynchronous cache update.</returns>
    public IEnumerator ClearZeroQuantityEntries(PlaceableStorageEntity shelf, ItemInstance[] items)
    {
      if (shelf == null || items == null || items.Length == 0)
      {
#if DEBUG
        Log(Level.Verbose, $"ClearZeroQuantityEntries: Invalid input (shelf={shelf?.GUID}, items={items?.Length ?? 0})", Category.Storage);
#endif
        yield break;
      }

      var itemKeys = new NativeArray<ItemKey>(items.Length, Allocator.TempJob);
      var logs = new NativeList<LogEntry>(items.Length, Allocator.TempJob);
      try
      {
        for (int i = 0; i < items.Length; i++)
          itemKeys[i] = new ItemKey(items[i]);

        yield return SmartExecution.ExecuteBurstFor<ItemKey, ItemKey>(
            uniqueId: nameof(ClearZeroQuantityEntries),
            itemCount: items.Length,
            burstForDelegate: (index, inputs, outputs, logs) => ClearZeroQuantityEntriesBurst(index, inputs, _itemToSlots, logs, shelf.GUID),
            burstResultsDelegate: null,
            inputs: itemKeys
        );
        yield return ProcessLogs(logs);
      }
      finally
      {
        if (itemKeys.IsCreated) itemKeys.Dispose();
        if (logs.IsCreated) logs.Dispose();
      }
    }

    [BurstCompile]
    private void ClearZeroQuantityEntriesBurst(
            int index,
            NativeArray<ItemKey> inputs,
            NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> itemToSlotIndices,
            NativeList<LogEntry> logs,
            Guid shelfGuid)
    {
      var itemKey = inputs[index];
      var tempEntries = new NativeList<(Guid, int)>(Allocator.Temp);

      // Collect all valid entries for the itemKey
      var enumerator = itemToSlotIndices.GetValuesForKey(itemKey);
      while (enumerator.MoveNext())
      {
        var entry = enumerator.Current;
        if (entry.Guid != shelfGuid && entry.Quantity > 0)
        {
          tempEntries.Add((entry.Guid, entry.Quantity));
        }
      }
      enumerator.Dispose();

      // Remove the key and re-add valid entries
      itemToSlotIndices.Remove(itemKey);
      foreach (var entry in tempEntries)
      {
        itemToSlotIndices.Add(itemKey, entry);
      }

      // Log the result
      logs.Add(new LogEntry
      {
        Message = tempEntries.Length > 0
              ? $"Cleared zero quantity entry for {itemKey.ToString()} on shelf {shelfGuid}, retained {tempEntries.Length} entries"
              : $"No valid entries found for {itemKey.ToString()} on shelf {shelfGuid}",
        Level = Level.Info,
        Category = Category.Storage
      });

      tempEntries.Dispose();
    }

    /// <summary>
    /// Removes a shelf search cache entry in a Burst-compatible manner.
    /// </summary>
    /// <param name="index">The index of the item to process.</param>
    /// <param name="inputs">The array of item keys.</param>
    /// <param name="shelfSearchCache">The shelf search cache.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    private void RemoveShelfSearchCacheEntriesBurst(
            int index,
            NativeArray<ItemKey> inputs,
            NativeParallelHashMap<ItemKey, (Guid, int, NativeList<SlotData>)> shelfSearchCache,
            NativeList<LogEntry> logs)
    {
      var cacheKey = inputs[index];
      shelfSearchCache.Remove(cacheKey);
#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Removed shelf search cache entry for {cacheKey.ToString()}",
        Level = Level.Verbose,
        Category = Category.Storage
      });
#endif
    }

    public NativeList<StationData> GetAllStationData(EntityType entityType)
    {
      var results = new NativeList<StationData>(Allocator.Persistent);
      var guids = _guidToType.GetKeyArray(Allocator.Temp);
      for (int i = 0; i < guids.Length; i++)
      {
        var guid = guids[i];
        if (_guidToType.TryGetValue(guid, out var typeInfo) && typeInfo.Item1 == entityType && typeInfo.StorageType == StorageType.Station)
        {
          var station = ManagedDictionaries.IStations[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][guid];
          results.Add(new StationData(station));
        }
      }
      guids.Dispose();
      return results;
    }

    public NativeList<StorageData> GetAllStorageDataByType(StorageType storageType)
    {
      var results = new NativeList<StorageData>(Allocator.Persistent);
      var guids = _guidToType.GetKeyArray(Allocator.Temp);
      for (int i = 0; i < guids.Length; i++)
      {
        var guid = guids[i];
        if (_guidToType.TryGetValue(guid, out var typeInfo) && typeInfo.StorageType == storageType)
        {
          var storage = ManagedDictionaries.Storages[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][guid];
          results.Add(new StorageData(storage));
        }
      }
      guids.Dispose();
      return results;
    }

    public NativeList<EmployeeData> GetAllEmployeeData(EntityType entityType)
    {
      var results = new NativeList<EmployeeData>(Allocator.Persistent);
      var guids = _guidToType.GetKeyArray(Allocator.Temp);
      for (int i = 0; i < guids.Length; i++)
      {
        var guid = guids[i];
        if (_guidToType.TryGetValue(guid, out var typeInfo) && typeInfo.EntityType == entityType && typeInfo.StorageType == StorageType.Employee)
        {
          var employee = ManagedDictionaries.IEmployees[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][guid];
          results.Add(new EmployeeData(employee));
        }
      }
      guids.Dispose();
      return results;
    }

    public void RegisterItemSlot(ItemSlot slot, Guid storageGuid)
    {
      if (slot.ItemInstance != null)
      {
        var cacheKey = new ItemKey(slot.ItemInstance);
        _itemToSlots.Add(cacheKey, (storageGuid, slot.SlotIndex));
        if (_guidToType.TryGetValue(storageGuid, out var typeInfo))
        {
          switch (typeInfo.StorageType)
          {
            case StorageType.AnyShelf:
            case StorageType.SpecificShelf:
              var storageData = new StorageData(ManagedDictionaries.Storages[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][storageGuid]);
              if (storageData.ItemToSlots.IsCreated)
                storageData.ItemToSlots.Add(cacheKey, slot.SlotIndex);
              break;
            case StorageType.Station:
              var stationData = new StationData(ManagedDictionaries.IStations[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][storageGuid]);
              if (stationData.ItemToSlots.IsCreated)
                stationData.ItemToSlots.Add(cacheKey, slot.SlotIndex);
              break;
            case StorageType.Employee:
              var employeeData = new EmployeeData(ManagedDictionaries.IEmployees[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][storageGuid]);
              if (employeeData.ItemToSlots.IsCreated)
                employeeData.ItemToSlots.Add(cacheKey, slot.SlotIndex);
              break;
          }
        }
      }
    }

    public void UnregisterItemSlot(ItemSlot slot, Guid storageGuid)
    {
      if (slot.ItemInstance != null)
      {
        var cacheKey = new ItemKey(slot.ItemInstance);
        var tempList = new NativeList<(Guid, int)>(Allocator.Temp);
        var globalEnumerator = _itemToSlots.GetValuesForKey(cacheKey);
        while (globalEnumerator.MoveNext())
        {
          var entry = globalEnumerator.Current;
          if (entry.Item1 != storageGuid || entry.Item2 != slot.SlotIndex)
            tempList.Add(entry);
        }
        globalEnumerator.Dispose();
        _itemToSlots.Remove(cacheKey);
        foreach (var entry in tempList)
          _itemToSlots.Add(cacheKey, entry);
        tempList.Dispose();

        if (_guidToType.TryGetValue(storageGuid, out var typeInfo))
        {
          switch (typeInfo.StorageType)
          {
            case StorageType.AnyShelf:
            case StorageType.SpecificShelf:
              var storageData = new StorageData(ManagedDictionaries.Storages[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][storageGuid]);
              if (storageData.ItemToSlots.IsCreated)
              {
                var tempEntityList = new NativeList<int>(Allocator.Temp);
                var entityEnumerator = storageData.ItemToSlots.GetValuesForKey(cacheKey);
                while (entityEnumerator.MoveNext())
                {
                  if (entityEnumerator.Current != slot.SlotIndex)
                    tempEntityList.Add(entityEnumerator.Current);
                }
                entityEnumerator.Dispose();
                storageData.ItemToSlots.Remove(cacheKey);
                foreach (var slotIndex in tempEntityList)
                  storageData.ItemToSlots.Add(cacheKey, slotIndex);
                tempEntityList.Dispose();
              }
              break;
            case StorageType.Station:
              var stationData = new StationData(ManagedDictionaries.IStations[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][storageGuid]);
              if (stationData.ItemToSlots.IsCreated)
              {
                var tempEntityList = new NativeList<int>(Allocator.Temp);
                var entityEnumerator = stationData.ItemToSlots.GetValuesForKey(cacheKey);
                while (entityEnumerator.MoveNext())
                {
                  if (entityEnumerator.Current != slot.SlotIndex)
                    tempEntityList.Add(entityEnumerator.Current);
                }
                entityEnumerator.Dispose();
                stationData.ItemToSlots.Remove(cacheKey);
                foreach (var slotIndex in tempEntityList)
                  stationData.ItemToSlots.Add(cacheKey, slotIndex);
                tempEntityList.Dispose();
              }
              break;
            case StorageType.Employee:
              var employeeData = new EmployeeData(ManagedDictionaries.IEmployees[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][storageGuid]);
              if (employeeData.ItemToSlots.IsCreated)
              {
                var tempEntityList = new NativeList<int>(Allocator.Temp);
                var entityEnumerator = employeeData.ItemToSlots.GetValuesForKey(cacheKey);
                while (entityEnumerator.MoveNext())
                {
                  if (entityEnumerator.Current != slot.SlotIndex)
                    tempEntityList.Add(entityEnumerator.Current);
                }
                entityEnumerator.Dispose();
                employeeData.ItemToSlots.Remove(cacheKey);
                foreach (var slotIndex in tempEntityList)
                  employeeData.ItemToSlots.Add(cacheKey, slotIndex);
                tempEntityList.Dispose();
              }
              break;
          }
        }
      }
    }

    [BurstCompile]
    public struct HandleSlotUpdateBurst
    {
      public NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntities;
      public NativeList<Guid> KeysToRemove;

      public void Execute(SlotUpdate input, NativeList<Empty> _, NativeList<LogEntry> logs)
      {
        KeysToRemove.Clear();
        var enumerator = DisabledEntities.GetEnumerator();
        while (enumerator.MoveNext())
        {
          var kvp = enumerator.Current;
          var entityGuid = kvp.Key;
          var data = kvp.Value;
          if (data.ReasonType == DisabledEntityData.DisabledReasonType.MissingItem)
          {
            bool allItemsAvailable = true;
            bool anyItemAvailable = false;
            for (int i = 0; i < data.RequiredItems.Length; i++)
            {
              if (data.RequiredItems[i].Equals(input.SlotData.Item) && input.SlotData.Quantity > 0)
                anyItemAvailable = true;
              else
                allItemsAvailable = false;
              if (anyItemAvailable && !allItemsAvailable) break;
            }
            if ((data.AnyItem && anyItemAvailable) || allItemsAvailable)
            {
              KeysToRemove.Add(entityGuid);
              logs.Add(new LogEntry
              {
                Message = $"Re-enabled entity {entityGuid} for action {data.ActionId} due to item availability",
                Level = Level.Info,
                Category = Category.Tasks
              });
            }
          }
        }
      }
    }

    internal static class CacheServicePatches
    {
      /// <summary>
      /// Applies patches to the Property class for cache management.
      /// </summary>
      [HarmonyPatch(typeof(Property))]
      public class PropertyPatch
      {
        /// <summary>
        /// Activates the cache service after a property is set as owned.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("SetOwned")]
        public static void SetOwnedPostfix(Property __instance)
        {
          GetOrCreateCacheService(__instance).Activate();
        }
      }

      /// <summary>
      /// Applies patches to the LoadingDock class for cache updates.
      /// </summary>
      [HarmonyPatch(typeof(LoadingDock))]
      public class LoadingDockPatch
      {
        /// <summary>
        /// Updates the cache when a loading dock's occupant is set.
        /// </summary>
        /// <param name="__instance">The loading dock instance.</param>
        /// <param name="occupant">The vehicle occupying the loading dock.</param>
        [HarmonyPostfix]
        [HarmonyPatch("SetOccupant")]
        public static void SetOccupant_Postfix(LoadingDock __instance, LandVehicle occupant)
        {
          var cacheService = GetOrCreateCacheService(__instance.ParentProperty);
          foreach (var slot in __instance.OutputSlots)
            cacheService.UnregisterItemSlot(slot, __instance.GUID);
          foreach (var slot in __instance.OutputSlots)
            cacheService.RegisterItemSlot(slot, __instance.GUID);
          StorageManager.UpdateStorageCache(__instance.ParentProperty, __instance.GUID, __instance.OutputSlots, StorageType.LoadingDock);
        }
      }

      /// <summary>
      /// Applies patches to the GridItem class for storage initialization and cleanup.
      /// </summary>
      [HarmonyPatch(typeof(GridItem))]
      public class GridItemPatch
      {
        /// <summary>
        /// Initializes storage for a grid item if applicable.
        /// </summary>
        /// <param name="__instance">The grid item instance.</param>
        /// <param name="instance">The item instance to initialize.</param>
        /// <param name="grid">The grid the item is placed on.</param>
        /// <param name="originCoordinate">The origin coordinate of the item.</param>
        /// <param name="rotation">The rotation of the item.</param>
        /// <param name="id">The ID of the item.</param>
        /// <returns>True if initialization should proceed, false otherwise.</returns>
        [HarmonyPrefix]
        [HarmonyPatch("InitializeGridItem", new Type[] { typeof(ItemInstance), typeof(Grid), typeof(Vector2), typeof(int), typeof(string) })]
        public static bool InitializeGridItemPrefix(GridItem __instance, ItemInstance instance, Grid grid, Vector2 originCoordinate, int rotation, string id)
        {
          try
          {
            if (!__instance.isGhost && id != Guid.Empty.ToString() && __instance.GUID != Guid.Empty && StorageInstanceIDs.All.Contains(instance.ID))
            {
              if (__instance is PlaceableStorageEntity entity)
              {
                Log(Level.Info, $"InitializeGridItemPrefix: Initializing storage for GUID: {__instance.GUID}", Category.Storage);
                ShelfUtilities.InitializeStorage(__instance.GUID, entity);
              }
              else
              {
                Log(Level.Warning, $"InitializeGridItemPrefix: {__instance.GUID} is not a PlaceableStorageEntity", Category.Storage);
              }
            }
            return true;
          }
          catch (Exception e)
          {
            Log(Level.Error, $"InitializeGridItemPrefix: Failed for GUID: {id}, error: {e.Message}", Category.Storage);
            return true;
          }
        }

        /// <summary>
        /// Cleans up cache entries when a grid item is destroyed.
        /// </summary>
        /// <param name="__instance">The grid item instance.</param>
        /// <param name="callOnServer">Whether to call the destroy on the server.</param>
        [HarmonyPostfix]
        [HarmonyPatch("DestroyItem")]
        public static void DestroyItemPostfix(GridItem __instance, bool callOnServer = true)
        {
          if (__instance is ITransitEntity)
            GetOrCreateCacheService(__instance.ParentProperty).ClearCacheForEntity(__instance.GUID);
        }
      }

      [HarmonyPatch(typeof(ItemSlot))]
      public class ItemSlotPatch
      {
        [HarmonyPostfix]
        [HarmonyPatch("ChangeQuantity")]
        public static void ChangeQuantityPostfix(ItemSlot __instance, int change, bool _internal)
        {
          var property = __instance.GetProperty();
          if (property == null) return;
          var cacheService = GetOrCreateCacheService(property);
          var disabledEntities = DisabledEntityServices[property]._disabledEntities;
          CoroutineRunner.Instance.RunCoroutine(cacheService.UpdateSlot(__instance, property, null, change, false, false, disabledEntities));
        }

        [HarmonyPostfix]
        [HarmonyPatch("SetStoredItem")]
        public static void SetStoredItemPostfix(ItemSlot __instance, ItemInstance instance, bool _internal)
        {
          var property = __instance.GetProperty();
          if (property == null) return;
          var cacheService = GetOrCreateCacheService(property);
          var disabledEntities = DisabledEntityServices[property]._disabledEntities;
          CoroutineRunner.Instance.RunCoroutine(cacheService.UpdateSlot(__instance, property, instance, 0, false, true, disabledEntities));
        }

        [HarmonyPostfix]
        [HarmonyPatch("ClearStoredInstance")]
        public static void ClearStoredInstancePostfix(ItemSlot __instance, bool _internal)
        {
          var property = __instance.GetProperty();
          if (property == null) return;
          var cacheService = GetOrCreateCacheService(property);
          var disabledEntities = DisabledEntityServices[property]._disabledEntities;
          CoroutineRunner.Instance.RunCoroutine(cacheService.UpdateSlot(__instance, property, null, 0, true, false, disabledEntities));
        }
      }
    }

    [BurstCompile]
    public void FindItem(
                int index,
                NativeArray<Guid> inputs,
                NativeList<StorageResultBurst> outputs,
                ItemData targetItem,
                int needed,
                bool allowTargetHigherQuality,
                NativeList<LogEntry> logs,
                NativeParallelHashMap<Guid, NativeList<SlotData>> storageSlotsCache,
                NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> itemToSlots,
                NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> guidToType)
    {
      var itemKey = new ItemKey(targetItem);
      var slotData = new NativeList<SlotData>(Allocator.Persistent);
      int totalQty = 0;
      var guid = inputs[index];

      if (storageSlotsCache.TryGetValue(guid, out var slots) && guidToType.TryGetValue(guid, out var typeInfo))
      {
        NativeParallelMultiHashMap<ItemKey, int> entityItemToSlots = default;
        switch (typeInfo.StorageType)
        {
          case StorageType.AnyShelf:
          case StorageType.SpecificShelf:
            var storageData = new StorageData(ManagedDictionaries.Storages[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][guid]);
            entityItemToSlots = storageData.ItemToSlots;
            break;
          case StorageType.Station:
            var stationData = new StationData(ManagedDictionaries.IStations[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][guid]);
            entityItemToSlots = stationData.ItemToSlots;
            break;
          case StorageType.Employee:
            var employeeData = new EmployeeData(ManagedDictionaries.IEmployees[ManagedDictionaries.IdToProperty[_property.NetworkObject.ObjectId]][guid]);
            entityItemToSlots = employeeData.ItemToSlots;
            break;
          case StorageType.LoadingDock:
            if (_loadingDockDataCache.TryGetValue(guid, out var dockData))
              entityItemToSlots = dockData.ItemToSlots;
            break;
        }

        if (entityItemToSlots.IsCreated)
        {
          var enumerator = entityItemToSlots.GetValuesForKey(itemKey);
          while (enumerator.MoveNext())
          {
            int slotIndex = enumerator.Current;
            for (int j = 0; j < slots.Length; j++)
            {
              if (slots[j].SlotIndex == slotIndex && slots[j].IsValid && slots[j].Item.AdvCanStackWithBurst(targetItem, allowTargetHigherQuality) && slots[j].Quantity > 0)
              {
                totalQty += slots[j].Quantity;
                slotData.Add(slots[j]);
#if DEBUG
                logs.Add(new LogEntry
                {
                  Message = $"Found slot {slotIndex} for item {targetItem.Id} in {guid} (entity-specific)",
                  Level = Level.Verbose,
                  Category = Category.Storage
                });
#endif
              }
            }
          }
          enumerator.Dispose();
        }
      }

      var globalEnumerator = itemToSlots.GetValuesForKey(itemKey);
      while (globalEnumerator.MoveNext())
      {
        var (storageGuid, slotIndex) = globalEnumerator.Current;
        if (storageSlotsCache.TryGetValue(storageGuid, out var globalSlots))
        {
          for (int j = 0; j < globalSlots.Length; j++)
          {
            if (globalSlots[j].SlotIndex == slotIndex && globalSlots[j].IsValid && globalSlots[j].Item.AdvCanStackWithBurst(targetItem, allowTargetHigherQuality) && globalSlots[j].Quantity > 0)
            {
              totalQty += globalSlots[j].Quantity;
              slotData.Add(globalSlots[j]);
#if DEBUG
              logs.Add(new LogEntry
              {
                Message = $"Found slot {slotIndex} for item {targetItem.Id} in {storageGuid} (global)",
                Level = Level.Verbose,
                Category = Category.Storage
              });
#endif
            }
          }
        }
      }
      globalEnumerator.Dispose();

      if (totalQty >= needed)
      {
        outputs.Add(new StorageResultBurst
        {
          ShelfGuid = slotData.Length > 0 ? slotData[0].EntityGuid : Guid.Empty,
          AvailableQuantity = totalQty,
          SlotData = slotData,
          PropertyId = _property.NetworkObject.ObjectId
        });
        logs.Add(new LogEntry
        {
          Message = $"Found storage with {totalQty} of {targetItem.Id}",
          Level = Level.Info,
          Category = Category.Storage
        });
      }
      else
      {
        slotData.Dispose();
      }
    }

    /// <summary>
    /// Processes results of item search and updates caches.
    /// </summary>
    /// <param name="results">The list of storage results.</param>
    /// <param name="shelfSearchCache">The cache for shelf search results.</param>
    /// <param name="filteredStorageKeys">The cache for filtered storage keys.</param>
    /// <param name="storagePropertyMap">The map of storage keys to property IDs.</param>
    /// <param name="propertyId">The ID of the property.</param>
    /// <param name="targetItem">The target item data.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void FindItemResults(
            NativeList<StorageResultBurst> results,
            NativeParallelHashMap<ItemKey, (Guid, int, NativeList<SlotData>)> shelfSearchCache,
            NativeParallelHashMap<ItemKey, NativeList<Guid>> filteredStorageKeys,
            NativeParallelHashMap<Guid, int> storagePropertyMap,
            int propertyId,
            ItemData targetItem,
            NativeList<LogEntry> logs)
    {
      var cacheUpdates = new NativeList<(ItemKey, (Guid, int, NativeList<SlotData>))>(results.Length, Allocator.Temp);
      foreach (var result in results)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Processing result for shelf {result.ShelfGuid}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
        if (storagePropertyMap.TryGetValue(result.ShelfGuid, out var propId) && propId == propertyId)
        {
          var itemKey = new ItemKey(targetItem);
          cacheUpdates.Add((itemKey, (result.ShelfGuid, result.AvailableQuantity, result.SlotData)));
          var filteredKeys = new NativeList<Guid>(1, Allocator.Persistent);
          filteredKeys.Add(result.ShelfGuid);
          filteredStorageKeys[itemKey] = filteredKeys;
          break;
        }
        result.SlotData.Dispose();
      }
      foreach (var update in cacheUpdates)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Updating cache for item {update.Item1.ToString()}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
        shelfSearchCache[update.Item1] = update.Item2;
      }
      cacheUpdates.Dispose();
    }

    /// <summary>
    /// Updates the station refill list in a Burst-compatible manner.
    /// </summary>
    /// <param name="index">The index of the station to process.</param>
    /// <param name="inputs">The array of station data.</param>
    /// <param name="item">The item to add to the refill list.</param>
    /// <param name="logs">The list to store log entries.</param>
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
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Added item {item.ToString()} to station {stationGuid} refill list",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
      }
    }

    [BurstCompile]
    public void FindDeliveryDestinationBurst(
                int index,
                NativeArray<(Guid Guid, bool IsStation)> inputs,
                NativeList<DeliveryDestinationBurst> outputs,
                int quantity,
                Guid sourceGuid,
                NativeList<LogEntry> logs,
                NativeParallelHashMap<Guid, NativeList<SlotData>> storageSlotsCache,
                NativeParallelHashMap<Guid, (EntityType, StorageType)> guidToType,
                ref int remainingQty)
    {
      var (guid, isStation) = inputs[index];
      if (!isStation && guid == sourceGuid) return;

      if (storageSlotsCache.TryGetValue(guid, out var slots) && guidToType.TryGetValue(guid, out var typeInfo))
      {
        var inputSlots = new NativeList<SlotData>(1, Allocator.Temp);
        int capacity = 0;

        for (int j = 0; j < slots.Length; j++)
        {
#if DEBUG
          logs.Add(new LogEntry
          {
            Message = $"Checking slot {slots[j].SlotIndex} for delivery destination",
            Level = Level.Verbose,
            Category = Category.Storage
          });
#endif
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
            Guid = guid,
            SlotData = slotsPersistent,
            Capacity = Mathf.Min(capacity, remainingQty)
          });
          remainingQty -= capacity;
          logs.Add(new LogEntry
          {
            Message = $"Found destination {guid} with capacity {capacity}",
            Level = Level.Info,
            Category = Category.Storage
          });
        }

        inputSlots.Dispose();
      }
    }

    /// <summary>
    /// Processes delivery destination results.
    /// </summary>
    /// <param name="destinations">The list of delivery destinations.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void FindDeliveryDestinationResults(
            NativeList<DeliveryDestinationBurst> destinations,
            NativeList<LogEntry> logs)
    {
      foreach (var destination in destinations)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Found destination {destination.Guid} with capacity {destination.Capacity}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
      }
    }

    /// <summary>
    /// Finds available slots for an item.
    /// </summary>
    /// <param name="index">The index of the slot to process.</param>
    /// <param name="inputs">The array of slot data.</param>
    /// <param name="outputs">The list to store results.</param>
    /// <param name="item">The item to store.</param>
    /// <param name="quantity">The quantity to store.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void FindAvailableSlotsBurst(
                int index,
                NativeArray<SlotData> inputs,
                NativeList<SlotResult> outputs,
                ItemData item,
                int quantity,
                NativeList<LogEntry> logs)
    {
      var slot = inputs[index];
      if (!slot.IsValid || slot.IsLocked) return;
      int capacity = SlotService.SlotProcessingUtility.GetCapacityForItem(slot, item);
      if (capacity <= 0) return;
      int amount = Mathf.Min(capacity, quantity);
      outputs.Add(new SlotResult { SlotIndex = slot.SlotIndex, Capacity = amount });
#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Found available slot {slot.SlotIndex} with capacity {amount}",
        Level = Level.Verbose,
        Category = Category.Tasks
      });
#endif
    }

    /// <summary>
    /// Processes slot availability results.
    /// </summary>
    /// <param name="results">The list of slot results.</param>
    /// <param name="slots">The list of item slots.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void FindAvailableSlotsBurstResults(
                NativeList<SlotResult> results,
                List<ItemSlot> slots,
                NativeList<LogEntry> logs)
    {
      foreach (var result in results)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Processed slot {result.SlotIndex} with capacity {result.Capacity}",
          Level = Level.Verbose,
          Category = Category.Tasks
        });
#endif
      }
    }

    /// <summary>
    /// Processes slot operations (insert/remove).
    /// </summary>
    /// <param name="index">The index of the operation to process.</param>
    /// <param name="inputs">The array of operation data.</param>
    /// <param name="outputs">The list to store results.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void ProcessSlotOperationsBurst(
                int index,
                NativeArray<OperationData> inputs,
                NativeList<SlotOperationResult> outputs,
                NativeList<LogEntry> logs)
    {
      var op = inputs[index];
      if (!op.IsValid || op.Quantity <= 0)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Invalid operation for slot {op.SlotKey.SlotIndex}",
          Level = Level.Warning,
          Category = Category.Tasks
        });
#endif
        outputs.Add(new SlotOperationResult { IsValid = false });
        return;
      }
      bool canProcess = op.IsInsert
          ? SlotService.SlotProcessingUtility.CanInsert(op.Slot, op.Item, op.Quantity)
          : SlotService.SlotProcessingUtility.CanRemove(op.Slot, op.Item, op.Quantity);
      if (!canProcess)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"{(op.IsInsert ? "Insert" : "Remove")} failed for {op.Quantity} of {op.Item.Id} in slot {op.SlotKey.SlotIndex}",
          Level = Level.Warning,
          Category = Category.Tasks
        });
#endif
        outputs.Add(new SlotOperationResult { IsValid = false });
        return;
      }
      outputs.Add(new SlotOperationResult
      {
        IsValid = true,
        EntityGuid = op.SlotKey.EntityGuid,
        SlotIndex = op.SlotKey.SlotIndex,
        Item = op.Item,
        Quantity = op.Quantity,
        IsInsert = op.IsInsert,
        LockerId = op.LockerId,
        LockReason = op.LockReason
      });
    }

    /// <summary>
    /// Processes results of slot operations and updates the operation list.
    /// </summary>
    /// <param name="results">The list of slot operation results.</param>
    /// <param name="operations">The list of slot operations.</param>
    /// <param name="opList">The list to store processed operations.</param>
    /// <param name="networkObjectCache">The cache of network objects.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void ProcessOperationResults(
            NativeList<SlotOperationResult> results,
            List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)> operations,
            List<SlotOperation> opList,
            List<NetworkObject> networkObjectCache,
            NativeList<LogEntry> logs)
    {
      foreach (var res in results)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing operation result for slot {res.SlotIndex}", Category.Tasks);
#endif
        if (res.IsValid)
        {
          var op = operations.FirstOrDefault(o => o.Item1 == res.EntityGuid && o.Item2.SlotIndex == res.SlotIndex);
          var locker = networkObjectCache.FirstOrDefault(n => n.ObjectId == res.LockerId);
          if (op.Item2 != null && locker != null)
          {
            opList.Add(new SlotOperation
            {
              EntityGuid = res.EntityGuid,
              SlotKey = new SlotKey(res.EntityGuid, res.SlotIndex),
              Slot = op.Item2,
              Item = res.Item.CreateItemInstance(),
              Quantity = res.Quantity,
              IsInsert = res.IsInsert,
              Locker = locker,
              LockReason = res.LockReason.ToString()
            });
          }
        }
      }
    }

    /// <summary>
    /// Processes pending slot updates for a storage entity.
    /// </summary>
    /// <param name="index">The index of the update to process.</param>
    /// <param name="inputs">The array of pending updates.</param>
    /// <param name="outputs">The list to store processed updates.</param>
    /// <param name="logs">The list to store log entries.</param>
    /// <param name="storageSlotsCache">The cache of storage slots.</param>
    /// <param name="itemToStorageCache">The cache mapping items to storage keys.</param>
    /// <param name="anyShelfKeys">The list of any shelf keys.</param>
    [BurstCompile]
    private static void ProcessPendingUpdate(
        int index,
        NativeArray<(Guid Guid, NativeList<SlotData> SlotData)> inputs,
        NativeList<(Guid Guid, NativeList<SlotData> SlotData)> outputs,
        NativeList<LogEntry> logs,
        NativeParallelHashMap<Guid, NativeList<SlotData>> storageSlotsCache,
        NativeParallelHashMap<ItemKey, NativeList<Guid>> itemToStorageCache
    )
    {
      var update = inputs[index];
      if (storageSlotsCache.ContainsKey(update.Guid))
      {
        var existing = storageSlotsCache[update.Guid];
        existing.Dispose();
        storageSlotsCache.Remove(update.Guid);
      }
      storageSlotsCache[update.Guid] = update.SlotData;
#if DEBUG
      foreach (var slot in update.SlotData)
      {
        logs.Add(new LogEntry
        {
          Message = $"Processed slot update for {update.Guid}, slot {slot.SlotIndex}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
      }
#endif
      outputs.Add(update);
    }
  }
}