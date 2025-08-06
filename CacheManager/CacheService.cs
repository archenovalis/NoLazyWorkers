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
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
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

    /// <summary>
    /// Updates a slot in the cache with item changes or clearing.
    /// </summary>
    /// <param name="slot">The slot to update.</param>
    /// <param name="property">The associated property.</param>
    /// <param name="instance">The item instance for set operations.</param>
    /// <param name="change">The quantity change for the slot.</param>
    /// <param name="isClear">Whether the operation clears the slot.</param>
    /// <param name="isSet">Whether the operation sets a new item.</param>
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

    public IEnumerator UpdateStorageConfiguration(PlaceableStorageEntity storage, ItemInstance assignedItem)
    {
      if (storage == null || !Storages.ContainsKey(storage.GUID))
      {
        Log(Level.Warning, $"UpdateStorageConfiguration: Invalid storage or not found in cache, GUID: {storage?.GUID}", Category.Storage);
        yield break;
      }

      List<object> outputs = null; // Dummy, not needed
      yield return Smart.Execute<object, object>(
          uniqueId: nameof(UpdateStorageConfiguration),
          itemCount: 1,
          action: (start, count, inputs, outList) =>
          {
            if (StorageConfigs.TryGetValue(storage.GUID, out var config))
            {
              var storageType = config.Mode == StorageMode.Specific ? StorageType.SpecificShelf : StorageType.AnyShelf;
              GuidToType[storage.GUID] = (EntityType.Storage, storageType);
              Log(Level.Verbose, $"Updated GuidToType for {storage.GUID} to {storageType}", Category.Storage);
            }
            else
            {
              Log(Level.Warning, $"UpdateStorageConfiguration: No config found for GUID: {storage.GUID}", Category.Storage);
            }
          },
          inputs: null,
          outputs: outputs,
          resultsAction: null,
          options: new SmartOptions { IsTaskSafe = true }
      );
    }

    public IEnumerator UpdateStorageCache(PlaceableStorageEntity storage)
    {
      if (storage == null || !Storages.ContainsKey(storage.GUID))
      {
        Log(Level.Warning, $"UpdateStorageCache: Invalid storage or not found in cache, GUID: {storage?.GUID}", Category.Storage);
        yield break;
      }

      List<object> outputs = null; // Dummy, not needed
      yield return Smart.Execute<object, object>(
          uniqueId: nameof(UpdateStorageCache),
          itemCount: 1,
          action: (start, count, inputs, outList) =>
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
            storageData.Dispose(); // Dispose to prevent leaks

            // Fixed bug: Only remove specific entries for this GUID, not entire keys
            var keysToProcess = new List<ItemKey>();
            var keyArray = ItemToSlots.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keyArray.Length; i++)
            {
              var key = keyArray[i];
              var hasMatch = false;
              var valuesEnumerator = ItemToSlots.GetValuesForKey(key);
              while (valuesEnumerator.MoveNext())
              {
                if (valuesEnumerator.Current.Guid == storage.GUID)
                {
                  hasMatch = true;
                  break;
                }
              }
              valuesEnumerator.Dispose();
              if (hasMatch)
              {
                keysToProcess.Add(key);
              }
            }
            keyArray.Dispose();

            foreach (var key in keysToProcess)
            {
              var keptEntries = new List<(Guid, int)>();
              var valuesEnumerator = ItemToSlots.GetValuesForKey(key);
              while (valuesEnumerator.MoveNext())
              {
                var entry = valuesEnumerator.Current;
                if (entry.Guid != storage.GUID)
                {
                  keptEntries.Add(entry);
                }
              }
              valuesEnumerator.Dispose();
              ItemToSlots.Remove(key);
              foreach (var entry in keptEntries)
              {
                ItemToSlots.Add(key, entry);
              }
            }

            // Add new entries
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
          },
          inputs: null,
          outputs: outputs,
          resultsAction: null,
          options: new SmartOptions { IsTaskSafe = true }
      );
    }

    /// <summary>
    /// Initializes property data caches asynchronously.
    /// </summary>
    /// <returns>An enumerator for asynchronous cache initialization.</returns>
    private IEnumerator InitializePropertyDataCaches()
    {
      var slotKeys = new NativeList<SlotKeyData>(Allocator.TempJob);
      var results = new NativeList<SlotCacheEntry>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        var storagesArray = Storages.Values.ToArray();
        yield return Smart.Execute<PlaceableStorageEntity, object>(
            uniqueId: nameof(InitializePropertyDataCaches) + "_Storages",
            itemCount: storagesArray.Length,
            action: (start, count, inputs, outputs) =>
            {
              for (int i = start; i < start + count; i++)
              {
                var storage = inputs[i];
                if (StorageConfigs.TryGetValue(storage.GUID, out var config) && config.Mode == StorageMode.None)
                  continue;
                var storageType = config.Mode == StorageMode.Any ? StorageType.AnyShelf : StorageType.SpecificShelf;
                GuidToType[storage.GUID] = (EntityType.Storage, storageType);
                EntityGuids.Add(storage.GUID);
                var storageData = new StorageData(storage);
                try
                {
                  var slots = new NativeList<SlotData>(Allocator.Persistent);
                  slots.AddRange(storageData.Slots);
                  SlotsCache[storage.GUID] = slots;
                  for (int j = 0; j < storage.OutputSlots.Count; j++)
                  {
                    slotKeys.Add(new SlotKeyData
                    {
                      EntityGuid = storage.GUID,
                      SlotIndex = j,
                      PropertyId = _property.NetworkObject.ObjectId,
                      Type = storageType
                    });
                    if (storage.OutputSlots[j].ItemInstance != null)
                      ItemToSlots.Add(new ItemKey(storage.OutputSlots[j].ItemInstance), (storage.GUID, j));
                  }
#if DEBUG
                  Log(Level.Verbose, $"Collecting slot data for storage {storage.GUID}, type={storageType}", Category.Storage);
#endif
                }
                finally
                {
                  storageData.Dispose();
                }
              }
            },
            inputs: storagesArray,
            options: SmartOptions.Default
        );

        var stationsArray = IStations.Values.ToArray();
        yield return Smart.Execute<IStationAdapter, object>(
            uniqueId: nameof(InitializePropertyDataCaches) + "_Stations",
            itemCount: stationsArray.Length,
            action: (start, count, inputs, outputs) =>
            {
              for (int i = start; i < start + count; i++)
              {
                var station = inputs[i];
                GuidToType[station.GUID] = (station.EntityType, StorageType.Station);
                EntityGuids.Add(station.GUID);
                var stationData = new StationData(station);
                try
                {
                  var slots = new NativeList<SlotData>(Allocator.Persistent);
                  slots.AddRange(stationData.InsertSlots);
                  slots.AddRange(stationData.ProductSlots);
                  slots.Add(stationData.OutputSlot);
                  SlotsCache[station.GUID] = slots;
                  var allSlots = station.InsertSlots.Concat(station.ProductSlots).Concat(new[] { station.OutputSlot }).ToArray();
                  for (int j = 0; j < allSlots.Length; j++)
                  {
                    var slot = allSlots[j];
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
                finally
                {
                  stationData.Dispose();
                }
              }
            },
            inputs: stationsArray,
            options: SmartOptions.Default
        );

        var docksArray = LoadingDocks.Values.ToArray();
        yield return Smart.Execute<LoadingDock, object>(
            uniqueId: nameof(InitializePropertyDataCaches) + "_Docks",
            itemCount: docksArray.Length,
            action: (start, count, inputs, outputs) =>
            {
              for (int i = start; i < start + count; i++)
              {
                var dock = inputs[i];
                GuidToType[dock.GUID] = (EntityType.LoadingDock, StorageType.LoadingDock);
                EntityGuids.Add(dock.GUID);
                var dockData = new LoadingDockData(dock);
                try
                {
                  var slots = new NativeList<SlotData>(Allocator.Persistent);
                  slots.AddRange(dockData.OutputSlots);
                  SlotsCache[dock.GUID] = slots;
                  for (int j = 0; j < dock.OutputSlots.Count; j++)
                  {
                    slotKeys.Add(new SlotKeyData
                    {
                      EntityGuid = dock.GUID,
                      SlotIndex = j,
                      PropertyId = _property.NetworkObject.ObjectId,
                      Type = StorageType.LoadingDock
                    });
                    if (dock.OutputSlots[j].ItemInstance != null)
                      ItemToSlots.Add(new ItemKey(dock.OutputSlots[j].ItemInstance), (dock.GUID, j));
                  }
#if DEBUG
                  Log(Level.Verbose, $"Collecting slot data for loading dock {dock.GUID}", Category.Storage);
#endif
                }
                finally
                {
                  dockData.Dispose();
                }
              }
            },
            inputs: docksArray,
            options: SmartOptions.Default
        );

        var employeesArray = IEmployees.Values.ToArray();
        yield return Smart.Execute<IEmployeeAdapter, object>(
            uniqueId: nameof(InitializePropertyDataCaches) + "_Employees",
            itemCount: employeesArray.Length,
            action: (start, count, inputs, outputs) =>
            {
              for (int i = start; i < start + count; i++)
              {
                var employee = inputs[i];
                GuidToType[employee.Guid] = (employee.EntityType, StorageType.Employee);
                EntityGuids.Add(employee.Guid);
                var employeeData = new EmployeeData(employee);
                try
                {
                  var slots = new NativeList<SlotData>(Allocator.Persistent);
                  slots.AddRange(employeeData.InventorySlots);
                  SlotsCache[employee.Guid] = slots;
                  for (int j = 0; j < employee.InventorySlots.Count; j++)
                  {
                    slotKeys.Add(new SlotKeyData
                    {
                      EntityGuid = employee.Guid,
                      SlotIndex = j,
                      PropertyId = _property.NetworkObject.ObjectId,
                      Type = StorageType.Employee
                    });
                    if (employee.InventorySlots[j].ItemInstance != null)
                      ItemToSlots.Add(new ItemKey(employee.InventorySlots[j].ItemInstance), (employee.Guid, j));
                  }
#if DEBUG
                  Log(Level.Verbose, $"Collecting slot data for employee {employee.Guid}", Category.Storage);
#endif
                }
                finally
                {
                  employeeData.Dispose();
                }
              }
            },
            inputs: employeesArray,
            options: SmartOptions.Default
        );

        // Existing Burst processing for slots
        var burstStruct = new ProcessSlotCacheItemBurstFor();
        yield return Smart.ExecuteBurstFor<SlotKeyData, SlotCacheEntry, ProcessSlotCacheItemBurstFor>(
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
    /// Clears an item from the not found cache.
    /// </summary>
    /// <param name="item">The item to clear from the not found cache.</param>
    [BurstCompile]
    public void ClearItemNotFoundCache(ItemKey item)
    {
      NotFoundItems.Remove(item);
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
    [HarmonyPatch]
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
  }
}