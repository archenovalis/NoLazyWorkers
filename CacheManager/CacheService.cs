using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.CacheManager.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using Unity.Collections;
using static NoLazyWorkers.Storage.ShelfExtensions;
using NoLazyWorkers.SmartExecution;
using Unity.Burst;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using ScheduleOne.Delivery;
using HarmonyLib;
using ScheduleOne.Vehicles;
using ScheduleOne.EntityFramework;
using static NoLazyWorkers.Storage.ShelfConstants;
using static NoLazyWorkers.CacheManager.ManagedDictionaries;
using static NoLazyWorkers.Employees.Extensions;
using Unity.Collections.LowLevel.Unsafe;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.Extensions;
using FishNet.Object;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using NoLazyWorkers.TaskService;
using NoLazyWorkers.Storage;
using Newtonsoft.Json.Linq;

namespace NoLazyWorkers.CacheManager
{
  /// <summary>
  /// Manages static dictionaries for caching property and service data.
  /// </summary>
  public static class ManagedDictionaries
  {
    internal static readonly Dictionary<string, Property> PropertyByName = new();
    internal static Dictionary<Property, CacheService> CacheServiceByProperty = new();
    internal static Dictionary<Guid, bool> InitializedEmployees = new();
    internal static Dictionary<Guid, float> PendingAdapters = new();
    internal static Dictionary<Guid, JObject> PendingConfigData = new();


    /// <summary>
    /// Initializes property name mappings and logs the operation.
    /// </summary>
    public static void Initialize()
    {
      foreach (var property in Property.Properties)
        PropertyByName.Add(property.name, property);
      Log(Level.Info, "ManagedDictionaries initialized", Category.Storage);
    }

    /// <summary>
    /// Clears all managed dictionaries and logs the cleanup.
    /// </summary>
    public static void Cleanup()
    {
      PropertyByName.Clear();
      CacheServiceByProperty.Clear();
      PendingAdapters.Clear();
      Log(Level.Info, "ManagedDictionaries cleaned up", Category.Storage);
    }
  }

  /// <summary>
  /// Manages caching for property-related data, handling storage, slots, and entities.
  /// Implements IDisposable for resource cleanup.
  /// </summary>
  public class CacheService : IDisposable
  {
    private readonly Property _property;
    private bool _isActive;
    private bool IsInitialized { get; set; }
    internal NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)> SlotCache;
    internal NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> ItemToSlots;
    internal NativeParallelHashSet<ItemKey> NotFoundItems;
    internal NativeParallelHashSet<ItemKey> NoDropOffCache;
    internal NativeList<Guid> EntityGuids;
    internal NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> GuidToType;
    internal NativeParallelHashMap<Guid, NativeList<SlotData>> SlotsCache;
    internal NativeParallelHashMap<Guid, LoadingDockData> LoadingDockDataCache;
    internal NativeParallelHashMap<Guid, EmployeeData> EmployeeDataCache;
    internal NativeParallelHashMap<Guid, StationData> StationDataCache;
    internal NativeParallelHashMap<Guid, StorageData> StorageDataCache;
    internal NativeList<SlotUpdate> PendingSlotUpdates;

    internal Dictionary<Guid, StorageConfiguration> StorageConfigs = new();
    internal Dictionary<Guid, PlaceableStorageEntity> Storages = new();
    internal Dictionary<Guid, LoadingDock> LoadingDocks = new();
    internal Dictionary<Guid, IStationAdapter> IStations = new();
    internal Dictionary<Guid, IEmployeeAdapter> IEmployees = new();
    internal Dictionary<Guid, EmployeeInfo> EmployeeInfoDict = new();

    /// <summary>
    /// Initializes a new CacheService instance for a specific property.
    /// </summary>
    /// <param name="property">The property to associate with the cache service.</param>
    /// <exception cref="ArgumentNullException">Thrown if property is null.</exception>
    public CacheService(Property property)
    {
      LoadingDockDataCache = new NativeParallelHashMap<Guid, LoadingDockData>(10, Allocator.Persistent);
      _property = property ?? throw new ArgumentNullException(nameof(property));
      SlotsCache = new NativeParallelHashMap<Guid, NativeList<SlotData>>(100, Allocator.Persistent);
      ItemToSlots = new NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)>(100, Allocator.Persistent);
      SlotCache = new NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)>(100, Allocator.Persistent);
      EntityGuids = new NativeList<Guid>(100, Allocator.Persistent);
      GuidToType = new NativeParallelHashMap<Guid, (EntityType, StorageType)>(100, Allocator.Persistent);
      NotFoundItems = new NativeParallelHashSet<ItemKey>(100, Allocator.Persistent);
      NoDropOffCache = new NativeParallelHashSet<ItemKey>(100, Allocator.Persistent);
      PendingSlotUpdates = new NativeList<SlotUpdate>(100, Allocator.Persistent);
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
        LoadingDocks.Add(loadingDock.GUID, loadingDock);
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

    /// <summary>
    /// Disposes of all native collections and cleans up resources.
    /// </summary>
    public void Dispose()
    {
      if (!IsInitialized)
      {
        Log(Level.Warning, $"CacheService for property {_property} not initialized, skipping dispose", Category.Storage);
        return;
      }
      Cleanup();
      DisposeAllNativeCollections();
      IsInitialized = false;
      Log(Level.Info, $"CacheService disposed for property {_property.name}", Category.Storage);
    }

    private void DisposeAllNativeCollections()
    {
      if (LoadingDockDataCache.IsCreated)
      {
        foreach (var kvp in LoadingDockDataCache)
          kvp.Value.Dispose();
        LoadingDockDataCache.Dispose();
      }
      if (EmployeeDataCache.IsCreated)
      {
        foreach (var kvp in EmployeeDataCache)
          kvp.Value.Dispose();
        EmployeeDataCache.Dispose();
      }
      if (StationDataCache.IsCreated)
      {
        foreach (var kvp in StationDataCache)
          kvp.Value.Dispose();
        StationDataCache.Dispose();
      }
      if (StorageDataCache.IsCreated)
      {
        foreach (var kvp in StorageDataCache)
          kvp.Value.Dispose();
        StorageDataCache.Dispose();
      }
      if (ItemToSlots.IsCreated)
      {
        ItemToSlots.Dispose();
      }
      if (EntityGuids.IsCreated) EntityGuids.Dispose();
      if (GuidToType.IsCreated) GuidToType.Dispose();
      if (NotFoundItems.IsCreated) NotFoundItems.Dispose();
      if (NoDropOffCache.IsCreated) NoDropOffCache.Dispose();
      if (PendingSlotUpdates.IsCreated) PendingSlotUpdates.Dispose();
      if (SlotsCache.IsCreated)
      {
        var slotsArray = SlotsCache.GetValueArray(Allocator.Temp);
        foreach (var slots in slotsArray) if (slots.IsCreated) slots.Dispose();
        slotsArray.Dispose();
        SlotsCache.Dispose();
      }
      if (SlotCache.IsCreated) SlotCache.Dispose();
    }

    /// <summary>
    /// Cleans up pending updates and event subscriptions.
    /// </summary>
    private void Cleanup()
    {
      StorageConfigs.Clear();
      Storages.Clear();
      LoadingDocks.Clear();
      IStations.Clear();
      IEmployees.Clear();
      PendingAdapters.Clear();
      PendingSlotUpdates.Clear();
      Log(Level.Info, "CacheService cleaned up", Category.Storage);
    }

    /// <summary>
    /// Processes pending slot updates asynchronously.
    /// </summary>
    /// <returns>An enumerator for asynchronous processing.</returns>
    private IEnumerator ProcessPendingSlotUpdates()
    {
      while (_isActive)
      {
        if (PendingSlotUpdates.Length > 0)
        {
          var logs = new NativeList<LogEntry>(Allocator.TempJob);
          var updates = new NativeArray<SlotUpdate>(PendingSlotUpdates.AsArray(), Allocator.TempJob);
          PendingSlotUpdates.Clear();
          var updateStorageCacheBurstFor = new UpdateStorageCacheBurstFor
          {
            StorageSlotsCache = SlotsCache,
            ItemToSlotIndices = ItemToSlots,
            AnyShelfKeys = EntityGuids,
            NotFoundItems = NotFoundItems
          };
          yield return SmartExecution.Smart.ExecuteBurstFor<SlotUpdate, SlotData, UpdateStorageCacheBurstFor>(
              uniqueId: nameof(ProcessPendingSlotUpdates),
              itemCount: updates.Length,
              burstForAction: updateStorageCacheBurstFor.ExecuteFor,
              inputs: updates
          );
          yield return ProcessLogs(logs);
          updates.Dispose();
          logs.Dispose();
        }
        yield return null;
      }
    }

    // Add method to consolidate slot update logic
    /// <summary>
    /// Updates a slot in the cache with item changes or clearing.
    /// </summary>
    /// <param name="slot">The slot to update.</param>
    /// <param name="property">The associated property.</param>
    /// <param name="instance">The item instance for set operations.</param>
    /// <param name="change">The quantity change for the slot.</param>
    /// <param name="isClear">Whether the operation clears the slot.</param>
    /// <param name="isSet">Whether the operation sets a new item.</param>
    /// <param name="disabledEntities">Cache of disabled entities.</param>
    /// <returns>An enumerator for asynchronous cache updates.</returns>
    public IEnumerator UpdateSlot(ItemSlot slot, Property property, ItemInstance instance, int change, bool isClear, bool isSet)
    {
      if (slot.SlotOwner is NPC && slot.SlotOwner is not Employee) yield break;
      if (property == null || (isClear && slot.ItemInstance == null) || (isSet && instance == null)) yield break;

      var slotKey = slot.GetSlotKey();
      var guid = (slot.SlotOwner is BuildableItem) ? (slot.SlotOwner as BuildableItem).GUID : slot.SlotOwner is LoadingDock loadingDock ? loadingDock.GUID : (slot.SlotOwner as Employee).GUID;
      var type = GuidToType[guid];
      SlotCache[slotKey] = (property.NetworkObject.ObjectId, type.StorageType, slot.SlotIndex);
      var slotData = new SlotData(slotKey.EntityGuid, slot, type.StorageType);
      PendingSlotUpdates.Add(new SlotUpdate
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

      yield return CacheManager.UpdateStorageCache(property, slotKey.EntityGuid, [slot], type.StorageType);
    }

    /// <summary>
    /// Retrieves or creates a CacheService for a given property.
    /// </summary>
    /// <param name="property">The property to associate with the cache service.</param>
    /// <returns>The CacheService instance, or null if not on server.</returns>
    public static CacheService GetOrCreateService(Property property)
    {
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Error, "CacheService creation skipped: not server", Category.Storage);
        return null;
      }
      if (!CacheServiceByProperty.TryGetValue(property, out var cacheService) || !cacheService.IsInitialized)
      {
        cacheService = new CacheService(property);
        CacheServiceByProperty[property] = cacheService;
      }
      return cacheService;
    }

    /// <summary>
    /// Burst-compiled struct for updating storage cache.
    /// </summary>
    [BurstCompile]
    public struct UpdateStorageCacheBurstFor
    {
      [ReadOnly] public NativeParallelHashMap<Guid, NativeList<SlotData>> StorageSlotsCache;
      [ReadOnly] public NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> ItemToSlotIndices;
      [ReadOnly] public NativeList<Guid> AnyShelfKeys;
      [ReadOnly] public NativeParallelHashSet<ItemKey> NotFoundItems;

      /// <summary>
      /// Executes cache update for a single slot update.
      /// </summary>
      /// <param name="index">The index of the update.</param>
      /// <param name="inputs">The array of slot updates.</param>
      /// <param name="outputs">The list to store updated slot data.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<SlotUpdate> inputs, NativeList<SlotData> outputs, NativeList<LogEntry> logs)
      {
        var update = inputs[index];
        var guid = update.OwnerGuid;
        var slotData = update.SlotData;
        if (!StorageSlotsCache.TryGetValue(guid, out var slots))
        {
          slots = new NativeList<SlotData>(Allocator.Persistent);
          StorageSlotsCache[guid] = slots;
        }

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

        if (slotData.Item.ID != "")
        {
          var itemKey = new ItemKey(slotData.Item);
          var tempEntries = new NativeList<(Guid, int)>(Allocator.Temp);
          try
          {
            var enumerator = ItemToSlotIndices.GetValuesForKey(itemKey);
            while (enumerator.MoveNext())
            {
              var entry = enumerator.Current;
              if (entry.Guid != guid || entry.Quantity != slotData.SlotIndex)
              {
                tempEntries.Add(entry);
              }
            }
            enumerator.Dispose();
            tempEntries.Add((guid, slotData.SlotIndex));
            ItemToSlotIndices.Remove(itemKey);
            foreach (var entry in tempEntries)
            {
              ItemToSlotIndices.Add(itemKey, entry);
            }
            NotFoundItems.Remove(itemKey);
          }
          finally
          {
            if (tempEntries.IsCreated)
              tempEntries.Dispose();
          }
        }
        else if (slotData.Item.ID == "")
        {
          var itemKey = new ItemKey(slotData.Item);
          var tempEntries = new NativeList<(Guid, int)>(Allocator.Temp);
          try
          {
            var enumerator = ItemToSlotIndices.GetValuesForKey(itemKey);
            while (enumerator.MoveNext())
            {
              var entry = enumerator.Current;
              if (entry.Guid != guid || entry.Quantity != slotData.SlotIndex)
              {
                tempEntries.Add(entry);
              }
            }
            enumerator.Dispose();
            ItemToSlotIndices.Remove(itemKey);
            foreach (var entry in tempEntries)
            {
              ItemToSlotIndices.Add(itemKey, entry);
            }
          }
          finally
          {
            if (tempEntries.IsCreated)
              tempEntries.Dispose();
          }
        }
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Updated cache for slot {slotData.SlotIndex} on {guid}, item={slotData.Item.ID}, qty={slotData.Quantity}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
      }
    }

    /// <summary>
    /// Checks if an item is marked as not found in the cache.
    /// </summary>
    /// <param name="cacheKey">The key of the item to check.</param>
    /// <returns>True if the item is marked as not found, false otherwise.</returns>
    internal bool IsItemNotFound(ItemKey cacheKey)
    {
      return NotFoundItems.Contains(cacheKey);
    }

    /// <summary>
    /// Clears the cache for a specific entity.
    /// </summary>
    /// <param name="guid">The GUID of the entity to clear from cache.</param>
    public void ClearCacheForEntity(Guid guid)
    {
      if (SlotsCache.TryGetValue(guid, out var slots))
      {
        slots.Dispose();
        SlotsCache.Remove(guid);
      }
#if DEBUG
      Log(Level.Verbose, $"Cleared cache for entity {guid}", Category.Storage);
#endif
    }

    public void UpdateStorageConfiguration(PlaceableStorageEntity storage, ItemInstance assignedItem)
    {
      if (storage == null || !Storages.ContainsKey(storage.GUID))
      {
        Log(Level.Warning, $"UpdateStorageConfiguration: Invalid storage or not found in cache, GUID: {storage?.GUID}", Category.Storage);
        return;
      }
      SmartExecution.Smart.Execute( //TODO: needs to properly implement smart execute
          uniqueId: nameof(UpdateStorageConfiguration),
          () =>
          {
            if (StorageConfigs.TryGetValue(storage.GUID, out var config))
            {
              var storageType = config.Mode == StorageMode.Any ? StorageType.AnyShelf : StorageType.SpecificShelf;
              GuidToType[storage.GUID] = (EntityType.Storage, storageType);
              Log(Level.Verbose, $"Updated GuidToType for {storage.GUID} to {storageType}", Category.Storage);
            }
            else
            {
              Log(Level.Warning, $"UpdateStorageConfiguration: No config found for GUID: {storage.GUID}", Category.Storage);
            }
          });
    }

    public void UpdateStorageCache(PlaceableStorageEntity storage)
    {
      if (storage == null || !Storages.ContainsKey(storage.GUID))
      {
        Log(Level.Warning, $"UpdateStorageCache: Invalid storage or not found in cache, GUID: {storage?.GUID}", Category.Storage);
        return;
      }
      SmartExecution.Smart.Execute( //TODO: needs to properly implement smart execute
          uniqueId: nameof(UpdateStorageCache),
          () =>
          {
            if (SlotsCache.TryGetValue(storage.GUID, out var slots))
            {
              slots.Dispose();
              SlotsCache.Remove(storage.GUID);
            }
            var storageData = new StorageData(storage);
            var newSlots = new NativeList<SlotData>(Allocator.Persistent);
            newSlots.AddRange(storageData.Slots);
            SlotsCache[storage.GUID] = newSlots;

            var itemKeys = new NativeList<ItemKey>(Allocator.Temp);
            var enumerator = ItemToSlots.GetKeyArray(Allocator.Temp);
            foreach (var key in enumerator)
            {
              var values = ItemToSlots.GetValuesForKey(key);
              while (values.MoveNext())
              {
                if (values.Current.Guid == storage.GUID)
                {
                  itemKeys.Add(key);
                }
              }
              values.Dispose();
            }
            enumerator.Dispose();
            foreach (var key in itemKeys)
            {
              ItemToSlots.Remove(key);
            }
            itemKeys.Dispose();

            for (int i = 0; i < storage.OutputSlots.Count; i++)
            {
              var slot = storage.OutputSlots[i];
              if (slot.ItemInstance != null)
              {
                var itemKey = new ItemKey(slot.ItemInstance);
                ItemToSlots.Add(itemKey, (storage.GUID, i));
              }
            }
            Log(Level.Verbose, $"Updated cache for storage {storage.GUID}", Category.Storage);
          });
    }

    /// <summary>
    /// Initializes property data caches asynchronously.
    /// </summary>
    /// <returns>An enumerator for asynchronous cache initialization.</returns>
    private IEnumerator InitializePropertyDataCaches() //TODO: this seems possibly heavy, so it should use smartexecute.execute for non-burst processing.
    {
      var slotKeys = new NativeList<SlotKeyData>(Allocator.TempJob);
      var results = new NativeList<SlotCacheEntry>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        var storages = Storages.Values.ToList();
        foreach (var storage in storages)
        {
          if (StorageConfigs.TryGetValue(storage.GUID, out var config) && config.Mode == StorageMode.None)
            continue;
          var storageType = config.Mode == StorageMode.Any ? StorageType.AnyShelf : StorageType.SpecificShelf;
          GuidToType[storage.GUID] = (EntityType.Storage, storageType);
          EntityGuids.Add(storage.GUID);
          var storageData = new StorageData(storage);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(storageData.Slots);
          SlotsCache[storage.GUID] = slots;
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
              ItemToSlots.Add(new ItemKey(storage.OutputSlots[i].ItemInstance), (storage.GUID, i));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for storage {storage.GUID}, type={storageType}", Category.Storage);
#endif
        }
        var stations = IStations.Values.ToList();
        foreach (var station in stations)
        {
          var entityKey = station.GUID;
          GuidToType[station.GUID] = (station.EntityType, StorageType.Station);
          EntityGuids.Add(station.GUID);
          var stationData = new StationData(station);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(stationData.InsertSlots);
          slots.AddRange(stationData.ProductSlots);
          slots.Add(stationData.OutputSlot);
          SlotsCache[station.GUID] = slots;
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
              ItemToSlots.Add(new ItemKey(slot.ItemInstance), (station.GUID, slot.SlotIndex));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for station {station.GUID}", Category.Storage);
#endif
        }
        var docks = LoadingDocks.Values.ToList();
        foreach (var dock in docks)
        {
          var entityKey = dock.GUID;
          GuidToType[dock.GUID] = (EntityType.LoadingDock, StorageType.LoadingDock);
          EntityGuids.Add(dock.GUID);
          var storageData = new LoadingDockData(dock);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(storageData.OutputSlots);
          SlotsCache[dock.GUID] = slots;
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
              ItemToSlots.Add(new ItemKey(dock.OutputSlots[i].ItemInstance), (dock.GUID, i));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for loading dock {dock.GUID}", Category.Storage);
#endif
        }
        var employees = IEmployees.Values.ToList();
        foreach (var employee in employees)
        {
          GuidToType[employee.Guid] = (employee.EntityType, StorageType.Employee);
          EntityGuids.Add(employee.Guid);
          var employeeData = new EmployeeData(employee);
          var slots = new NativeList<SlotData>(Allocator.Persistent);
          slots.AddRange(employeeData.InventorySlots);
          SlotsCache[employee.Guid] = slots;
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
              ItemToSlots.Add(new ItemKey(employee.InventorySlots[i].ItemInstance), (employee.Guid, i));
          }
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for employee {employee.Guid}", Category.Storage);
#endif
        }
        var burstStruct = new ProcessSlotCacheItemBurstFor();
        yield return SmartExecution.Smart.ExecuteBurstFor<SlotKeyData, SlotCacheEntry, ProcessSlotCacheItemBurstFor>(
            uniqueId: nameof(InitializePropertyDataCaches),
            itemCount: slotKeys.Length,
            burstForAction: burstStruct.ExecuteFor,
            burstResultsAction: ProcessSlotCacheItemResults,
            inputs: slotKeys.AsArray(),
            outputs: results
        );
        yield return ProcessLogs(logs);
        IsInitialized = true;
        Log(Level.Info, $"CacheService initialized for property {_property.name}", Category.Storage);
      }
      finally
      {
        if (slotKeys.IsCreated) slotKeys.Dispose();
        if (results.IsCreated) results.Dispose();
        if (logs.IsCreated) logs.Dispose();
      }
    }

    [BurstCompile]
    public struct ProcessSlotCacheItemBurstFor
    {

      public void ExecuteFor(int index, NativeArray<SlotKeyData> inputs, NativeList<SlotCacheEntry> outputs, NativeList<LogEntry> logs)
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
        SlotCache[entry.Key] = (entry.PropertyId, entry.Type, entry.SlotIndex);
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
    internal bool TryGetSlots(Guid key, out NativeList<SlotData> slots, NativeList<LogEntry> logs)
    {
      bool hit = SlotsCache.TryGetValue(key, out slots);
      SmartMetrics.TrackCacheAccess("StorageSlotsCache", hit, logs);
      return hit;
    }

    /// <summary>
    /// Marks an item as not found in the cache.
    /// </summary>
    /// <param name="cacheKey">The key of the item to mark as not found.</param>
    public void AddItemNotFound(ItemKey cacheKey)
    {
      NotFoundItems.Add(cacheKey);
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
      NotFoundItems.Remove(item);
    }

    /// <summary>
    /// Adds an item to the no drop-off cache.
    /// </summary>
    /// <param name="item">The item to add to the no drop-off cache.</param>
    /// <param name="logs">The list to store log entries.</param>
    [BurstCompile]
    public void AddNoDropOffCache(ItemKey item, NativeList<LogEntry> logs)
    {
      NoDropOffCache.Add(item);
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
      NoDropOffCache.Remove(item);
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

      var inputs = new NativeArray<ItemKey>(items.Length, Allocator.TempJob);
      var logs = new NativeList<LogEntry>(items.Length, Allocator.TempJob);
      try
      {
        for (int i = 0; i < items.Length; i++)
          inputs[i] = new ItemKey(items[i]);
        var burstStruct = new ClearZeroQuantityEntriesBurstFor
        {
          ItemToSlotIndices = ItemToSlots,
          ShelfGuid = shelf.GUID
        };
        yield return SmartExecution.Smart.ExecuteBurstFor<ItemKey, ItemKey, ClearZeroQuantityEntriesBurstFor>(
            uniqueId: nameof(ClearZeroQuantityEntries),
            itemCount: items.Length,
            burstForAction: burstStruct.ExecuteFor,
            inputs: inputs
        );
        yield return ProcessLogs(logs);
      }
      finally
      {
        if (inputs.IsCreated) inputs.Dispose();
        if (logs.IsCreated) logs.Dispose();
      }
    }

    /// <summary>
    /// Burst-compiled struct for clearing zero quantity entries.
    /// </summary>
    [BurstCompile]
    public struct ClearZeroQuantityEntriesBurstFor
    {
      [ReadOnly] public NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> ItemToSlotIndices;
      [ReadOnly] public Guid ShelfGuid;

      /// <summary>
      /// Executes clearing of zero quantity entries for an item.
      /// </summary>
      /// <param name="index">The index of the item to process.</param>
      /// <param name="inputs">The array of item keys.</param>
      /// <param name="outputs">The list to store processed item keys.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<ItemKey> inputs, NativeList<ItemKey> outputs, NativeList<LogEntry> logs)
      {
        var itemKey = inputs[index];
        var tempEntries = new NativeList<(Guid, int)>(Allocator.Temp);
        try
        {
          var enumerator = ItemToSlotIndices.GetValuesForKey(itemKey);
          while (enumerator.MoveNext())
          {
            var entry = enumerator.Current;
            if (entry.Guid != ShelfGuid && entry.Quantity > 0)
            {
              tempEntries.Add((entry.Guid, entry.Quantity));
            }
          }
          enumerator.Dispose();
          ItemToSlotIndices.Remove(itemKey);
          foreach (var entry in tempEntries)
          {
            ItemToSlotIndices.Add(itemKey, entry);
          }
          logs.Add(new LogEntry
          {
            Message = tempEntries.Length > 0
                  ? $"Cleared zero quantity entry for {itemKey.ToString()} on shelf {ShelfGuid}, retained {tempEntries.Length} entries"
                  : $"No valid entries found for {itemKey.ToString()} on shelf {ShelfGuid}",
            Level = Level.Info,
            Category = Category.Storage
          });
        }
        finally
        {
          if (tempEntries.IsCreated)
            tempEntries.Dispose();
        }
      }
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

    /// <summary>
    /// Retrieves all station data for a specific entity type.
    /// </summary>
    /// <param name="entityType">The entity type to filter by.</param>
    /// <returns>A NativeList containing station data for the specified entity type.</returns>
    public NativeList<StationData> GetAllStationData(EntityType entityType)
    {
      var results = new NativeList<StationData>(Allocator.Persistent);
      var guids = GuidToType.GetKeyArray(Allocator.Temp);
      for (int i = 0; i < guids.Length; i++)
      {
        var guid = guids[i];
        if (GuidToType.TryGetValue(guid, out var typeInfo) && typeInfo.Item1 == entityType && typeInfo.StorageType == StorageType.Station)
        {
          var station = IStations[guid];
          results.Add(new StationData(station));
        }
      }
      guids.Dispose();
      return results;
    }

    /// <summary>
    /// Retrieves all storage data for a specific storage type.
    /// </summary>
    /// <param name="storageType">The storage type to filter by.</param>
    /// <returns>A NativeList containing storage data for the specified storage type.</returns>
    public NativeList<StorageData> GetAllStorageDataByType(StorageType storageType)
    {
      var results = new NativeList<StorageData>(Allocator.Persistent);
      var guids = GuidToType.GetKeyArray(Allocator.Temp);
      for (int i = 0; i < guids.Length; i++)
      {
        var guid = guids[i];
        if (GuidToType.TryGetValue(guid, out var typeInfo) && typeInfo.StorageType == storageType)
        {
          var storage = Storages[guid];
          results.Add(new StorageData(storage));
        }
      }
      guids.Dispose();
      return results;
    }

    /// <summary>
    /// Retrieves all employee data for a specific entity type.
    /// </summary>
    /// <param name="entityType">The entity type to filter by.</param>
    /// <returns>A NativeList containing employee data for the specified entity type.</returns>
    public NativeList<EmployeeData> GetAllEmployeeData(EntityType entityType)
    {
      var results = new NativeList<EmployeeData>(Allocator.Persistent);
      var guids = GuidToType.GetKeyArray(Allocator.Temp);
      for (int i = 0; i < guids.Length; i++)
      {
        var guid = guids[i];
        if (GuidToType.TryGetValue(guid, out var typeInfo) && typeInfo.EntityType == entityType && typeInfo.StorageType == StorageType.Employee)
        {
          var employee = IEmployees[guid];
          results.Add(new EmployeeData(employee));
        }
      }
      guids.Dispose();
      return results;
    }

    /// <summary>
    /// Registers an item slot in the cache.
    /// </summary>
    /// <param name="slot">The item slot to register.</param>
    /// <param name="ownerGuid">The GUID of the slot owner.</param>
    public void RegisterItemSlot(ItemSlot slot, Guid ownerGuid)
    {
      if (slot.ItemInstance != null)
      {
        var cacheKey = new ItemKey(slot.ItemInstance);
        ItemToSlots.Add(cacheKey, (ownerGuid, slot.SlotIndex));
        if (GuidToType.TryGetValue(ownerGuid, out var typeInfo))
        {
          switch (typeInfo.StorageType)
          {
            case StorageType.AnyShelf:
            case StorageType.SpecificShelf:
              var storageData = new StorageData(Storages[ownerGuid]);
              if (storageData.ItemToSlots.IsCreated)
                storageData.ItemToSlots.Add(cacheKey, slot.SlotIndex);
              break;
            case StorageType.Station:
              var stationData = new StationData(IStations[ownerGuid]);
              if (stationData.ItemToSlots.IsCreated)
                stationData.ItemToSlots.Add(cacheKey, slot.SlotIndex);
              break;
            case StorageType.Employee:
              var employeeData = new EmployeeData(IEmployees[ownerGuid]);
              if (employeeData.ItemToSlots.IsCreated)
                employeeData.ItemToSlots.Add(cacheKey, slot.SlotIndex);
              break;
          }
        }
      }
    }

    /// <summary>
    /// Unregisters an item slot from the cache.
    /// </summary>
    /// <param name="slot">The item slot to unregister.</param>
    /// <param name="storageGuid">The GUID of the storage entity.</param>
    public void UnregisterItemSlot(ItemSlot slot, Guid storageGuid)
    {
      if (slot.ItemInstance != null)
      {
        var cacheKey = new ItemKey(slot.ItemInstance);
        var tempList = new NativeList<(Guid, int)>(Allocator.Temp);
        var globalEnumerator = ItemToSlots.GetValuesForKey(cacheKey);
        while (globalEnumerator.MoveNext())
        {
          var entry = globalEnumerator.Current;
          if (entry.Item1 != storageGuid || entry.Item2 != slot.SlotIndex)
            tempList.Add(entry);
        }
        globalEnumerator.Dispose();
        ItemToSlots.Remove(cacheKey);
        foreach (var entry in tempList)
          ItemToSlots.Add(cacheKey, entry);
        tempList.Dispose();

        if (GuidToType.TryGetValue(storageGuid, out var typeInfo))
        {
          switch (typeInfo.StorageType)
          {
            case StorageType.AnyShelf:
            case StorageType.SpecificShelf:
              var storageData = new StorageData(Storages[storageGuid]);
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
              var stationData = new StationData(IStations[storageGuid]);
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
              var employeeData = new EmployeeData(IEmployees[storageGuid]);
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

    /// <summary>
    /// Applies patches to various classes for cache management.
    /// </summary>
    internal static class CacheServicePatches
    {
      /// <summary>
      /// Patches for the Property class to manage cache activation.
      /// </summary>
      [HarmonyPatch(typeof(Property))]
      public class PropertyPatch
      {
        /// <summary>
        /// Activates the cache service after a property is set as owned.
        /// </summary>
        /// <param name="__instance">The property instance.</param>
        [HarmonyPostfix]
        [HarmonyPatch("SetOwned")]
        public static void SetOwnedPostfix(Property __instance)
        {
          GetOrCreateService(__instance).Activate();
        }
      }

      /// <summary>
      /// Patches for the LoadingDock class to manage cache updates.
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
          var cacheService = GetOrCreateService(__instance.ParentProperty);
          foreach (var slot in __instance.OutputSlots)
            cacheService.UnregisterItemSlot(slot, __instance.GUID);
          foreach (var slot in __instance.OutputSlots)
            cacheService.RegisterItemSlot(slot, __instance.GUID);
          CacheManager.UpdateStorageCache(__instance.ParentProperty, __instance.GUID, __instance.OutputSlots, StorageType.LoadingDock);
        }
      }

      /// <summary>
      /// Patches for the GridItem class to manage storage initialization and cleanup.
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
            GetOrCreateService(__instance.ParentProperty).ClearCacheForEntity(__instance.GUID);
        }
      }

      /// <summary>
      /// Patches for the ItemSlot class to manage cache updates on slot operations.
      /// </summary>
      [HarmonyPatch(typeof(ItemSlot))]
      public class ItemSlotPatch
      {
        /// <summary>
        /// Updates the cache when an item slot's quantity changes.
        /// </summary>
        /// <param name="__instance">The item slot instance.</param>
        /// <param name="change">The quantity change.</param>
        /// <param name="_internal">Whether the change is internal.</param>
        [HarmonyPostfix]
        [HarmonyPatch("ChangeQuantity")]
        public static void ChangeQuantityPostfix(ItemSlot __instance, int change, bool _internal)
        {
          var property = __instance.GetProperty();
          if (property == null) return;
          var cacheService = GetOrCreateService(property);
          CoroutineRunner.Instance.RunCoroutine(cacheService.UpdateSlot(__instance, property, null, change, false, false));
        }

        /// <summary>
        /// Updates the cache when an item is set in a slot.
        /// </summary>
        /// <param name="__instance">The item slot instance.</param>
        /// <param name="instance">The item instance to set.</param>
        /// <param name="_internal">Whether the change is internal.</param>
        [HarmonyPostfix]
        [HarmonyPatch("SetStoredItem")]
        public static void SetStoredItemPostfix(ItemSlot __instance, ItemInstance instance, bool _internal)
        {
          var property = __instance.GetProperty();
          if (property == null) return;
          var cacheService = GetOrCreateService(property);
          CoroutineRunner.Instance.RunCoroutine(cacheService.UpdateSlot(__instance, property, instance, 0, false, true));
        }

        /// <summary>
        /// Updates the cache when an item is cleared from a slot.
        /// </summary>
        /// <param name="__instance">The item slot instance.</param>
        /// <param name="_internal">Whether the change is internal.</param>
        [HarmonyPostfix]
        [HarmonyPatch("ClearStoredInstance")]
        public static void ClearStoredInstancePostfix(ItemSlot __instance, bool _internal)
        {
          var property = __instance.GetProperty();
          if (property == null) return;
          var cacheService = GetOrCreateService(property);
          CoroutineRunner.Instance.RunCoroutine(cacheService.UpdateSlot(__instance, property, null, 0, true, false));
        }
      }
    }

    /// <summary>
    /// Burst-compiled struct for finding items in storage.
    /// </summary>
    [BurstCompile]
    public struct FindItemBurstFor
    {
      [ReadOnly] public NativeParallelHashMap<Guid, NativeList<SlotData>> StorageSlotsCache;
      [ReadOnly] public NativeParallelMultiHashMap<ItemKey, (Guid Guid, int Quantity)> ItemToSlots;
      [ReadOnly] public NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> GuidToType;
      [ReadOnly] public NativeParallelHashMap<Guid, StorageData> StorageDataCache;
      [ReadOnly] public ItemData TargetItem;
      [ReadOnly] public int Needed;
      [ReadOnly] public bool AllowTargetHigherQuality;

      /// <summary>
      /// Executes item search for a specific storage entity.
      /// </summary>
      /// <param name="index">The index of the storage entity.</param>
      /// <param name="inputs">The array of storage GUIDs.</param>
      /// <param name="outputs">The list to store search results.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<Guid> inputs, NativeList<StorageResultBurst> outputs, NativeList<LogEntry> logs)
      {
        var itemKey = new ItemKey(TargetItem);
        var guid = inputs[index];
        var slotData = new NativeList<SlotData>(Allocator.Persistent);
        try
        {
          int totalQty = 0;
          if (StorageSlotsCache.TryGetValue(guid, out var slots) && GuidToType.TryGetValue(guid, out var typeInfo))
          {
            NativeParallelMultiHashMap<ItemKey, int> entityItemToSlots = default;
            if (typeInfo.StorageType == StorageType.AnyShelf || typeInfo.StorageType == StorageType.SpecificShelf)
            {
              if (StorageDataCache.TryGetValue(guid, out var storageData))
                entityItemToSlots = storageData.ItemToSlots;
            }
            if (entityItemToSlots.IsCreated)
            {
              var enumerator = entityItemToSlots.GetValuesForKey(itemKey);
              while (enumerator.MoveNext())
              {
                int slotIndex = enumerator.Current;
                for (int j = 0; j < slots.Length; j++)
                {
                  if (slots[j].SlotIndex == slotIndex && slots[j].Item.AdvCanStackWithBurst(TargetItem, AllowTargetHigherQuality) && slots[j].Quantity > 0)
                  {
                    totalQty += slots[j].Quantity;
                    slotData.Add(slots[j]);
#if DEBUG
                    logs.Add(new LogEntry
                    {
                      Message = $"Found slot {slotIndex} for item {TargetItem.ID} in {guid} (entity-specific)",
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
          var globalEnumerator = ItemToSlots.GetValuesForKey(itemKey);
          while (globalEnumerator.MoveNext())
          {
            var (storageGuid, slotIndex) = globalEnumerator.Current;
            if (storageGuid == guid && StorageSlotsCache.TryGetValue(storageGuid, out var globalSlots))
            {
              for (int j = 0; j < globalSlots.Length; j++)
              {
                if (globalSlots[j].SlotIndex == slotIndex && globalSlots[j].Item.AdvCanStackWithBurst(TargetItem, AllowTargetHigherQuality) && globalSlots[j].Quantity > 0)
                {
                  totalQty += globalSlots[j].Quantity;
                  slotData.Add(globalSlots[j]);
#if DEBUG
                  logs.Add(new LogEntry
                  {
                    Message = $"Found slot {slotIndex} for item {TargetItem.ID} in {storageGuid} (global)",
                    Level = Level.Verbose,
                    Category = Category.Storage
                  });
#endif
                }
              }
            }
          }
          globalEnumerator.Dispose();
          if (totalQty >= Needed)
          {
            outputs.Add(new StorageResultBurst
            {
              ShelfGuid = slotData.Length > 0 ? slotData[0].EntityGuid : Guid.Empty,
              AvailableQuantity = totalQty,
              SlotData = slotData,
              PropertyName = slotData.Length > 0 ? slotData[0].PropertyName : string.Empty
            });
            logs.Add(new LogEntry
            {
              Message = $"Found storage with {totalQty} of {TargetItem.ID}",
              Level = Level.Info,
              Category = Category.Storage
            });
          }
          else
          {
            slotData.Dispose();
          }
        }
        catch
        {
          if (slotData.IsCreated)
            slotData.Dispose();
          throw;
        }
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
    /// Burst-compiled struct for updating station refill lists.
    /// </summary>
    [BurstCompile]
    public struct UpdateStationRefillListBurstFor //TODO: this doesn't seem to work correctly. we should be updating the station's stationdata.
    {
      /// <summary>
      /// Updates the station's refill list with an item.
      /// </summary>
      /// <param name="index">The index of the station.</param>
      /// <param name="inputs">The array of station GUIDs and item keys.</param>
      /// <param name="outputs">The list to store empty outputs.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<(Guid Guid, NativeList<ItemKey> ItemKey)> inputs, NativeList<Empty> outputs, NativeList<LogEntry> logs)
      {
        var (stationGuid, refillList) = inputs[index];
        var item = inputs[index].ItemKey[0];
        refillList.Clear();
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
    }

    /// <summary>
    /// Burst-compiled struct for finding delivery destinations.
    /// </summary>
    [BurstCompile]
    public struct FindDeliveryDestinationBurstFor
    {
      [ReadOnly] public NativeParallelHashMap<Guid, NativeList<SlotData>> StorageSlotsCache;
      [ReadOnly] public NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> GuidToType;
      [ReadOnly] public int Quantity;
      [ReadOnly] public Guid SourceGuid;

      /// <summary>
      /// Executes search for delivery destinations.
      /// </summary>
      /// <param name="index">The index of the storage entity.</param>
      /// <param name="inputs">The array of storage GUIDs and station flags.</param>
      /// <param name="outputs">The list to store delivery destinations.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<(Guid Guid, bool IsStation)> inputs, NativeList<DeliveryDestinationBurst> outputs, NativeList<LogEntry> logs)
      {
        var (guid, isStation) = inputs[index];
        if (!isStation && guid == SourceGuid)
          return;
        if (StorageSlotsCache.TryGetValue(guid, out var slots) && GuidToType.TryGetValue(guid, out var typeInfo))
        {
          var inputSlots = new NativeList<SlotData>(1, Allocator.Temp);
          try
          {
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
              if (slot.StackLimit > slot.Quantity)
              {
                int slotCapacity = slot.StackLimit - slot.Quantity;
                if (slotCapacity > 0 && (slot.Item.ID == "" || slot.Item.AdvCanStackWithBurst(slot.Item)))
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
                Capacity = Mathf.Min(capacity, Quantity)
              });
              logs.Add(new LogEntry
              {
                Message = $"Found destination {guid} with capacity {capacity}",
                Level = Level.Info,
                Category = Category.Storage
              });
            }
          }
          finally
          {
            if (inputSlots.IsCreated)
              inputSlots.Dispose();
          }
        }
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
    /// Burst-compiled struct for finding available slots.
    /// </summary>
    [BurstCompile]
    public struct FindAvailableSlotsBurstFor
    {
      /// <summary>
      /// Executes search for available slots.
      /// </summary>
      /// <param name="index">The index of the slot.</param>
      /// <param name="inputs">The array of slot data.</param>
      /// <param name="outputs">The list to store slot results.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<SlotData> inputs, NativeList<SlotResult> outputs, NativeList<LogEntry> logs)
      {
        var slot = inputs[index];
        if (slot.IsLocked)
          return;
        int capacity = SlotService.SlotProcessingUtility.GetCapacityForItem(slot, inputs[index].Item);
        if (capacity <= 0)
          return;
        int amount = Mathf.Min(capacity, inputs[index].Quantity);
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
    /// Burst-compiled struct for processing slot operations.
    /// </summary>
    [BurstCompile]
    public struct ProcessSlotOperationsBurstFor
    {
      [ReadOnly] public NativeParallelHashMap<Guid, NativeList<SlotData>> StorageSlotsCache;
      [ReadOnly] public NativeParallelHashMap<Guid, (EntityType EntityType, StorageType StorageType)> GuidToType;

      /// <summary>
      /// Executes slot operation processing.
      /// </summary>
      /// <param name="index">The index of the operation.</param>
      /// <param name="inputs">The array of operation data.</param>
      /// <param name="outputs">The list to store operation results.</param>
      /// <param name="logs">The list to store log entries.</param>
      public void ExecuteFor(int index, NativeArray<OperationData> inputs, NativeList<SlotOperationResult> outputs, NativeList<LogEntry> logs)
      {
        var op = inputs[index];
        if (!op.IsValid || op.Quantity <= 0)
        {
          logs.Add(new LogEntry
          {
            Message = $"Invalid operation or inactive entity for slot {op.SlotKey.SlotIndex}",
            Level = Level.Warning,
            Category = Category.Tasks
          });
          outputs.Add(new SlotOperationResult { IsValid = false });
          return;
        }
        bool canProcess = op.IsInsert
            ? SlotService.SlotProcessingUtility.CanInsert(op.Slot, op.Item, op.Quantity)
            : SlotService.SlotProcessingUtility.CanRemove(op.Slot, op.Item, op.Quantity);
        if (!canProcess)
        {
          logs.Add(new LogEntry
          {
            Message = $"{(op.IsInsert ? "Insert" : "Remove")} failed for {op.Quantity} of {op.Item.ID} in slot {op.SlotKey.SlotIndex}",
            Level = Level.Warning,
            Category = Category.Tasks
          });
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