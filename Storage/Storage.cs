using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using UnityEngine;
using ScheduleOne.Product;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using System.Collections.Concurrent;
using Unity.Collections;
using FishNet;
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
using ScheduleOne.UI;

namespace NoLazyWorkers.Storage
{
  /// <summary>
  /// Manages storage operations, including initialization, cleanup, and slot operations for items in a networked environment.
  /// </summary>
  public static class StorageManager
  {
    /// <summary>
    /// Indicates whether the StorageManager is initialized.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    internal static readonly ObjectPool<List<int>> _intListPool = new ObjectPool<List<int>>(
        createFunc: () => new List<int>(10),
        actionOnGet: null,
        actionOnRelease: list => list.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);

    private static readonly List<Action<ItemSlot, ItemInstance, int>> _slotListeners = new();
    private static readonly List<Action<PlaceableStorageEntity, StorageType>> _shelfListeners = new();

    /// <summary>
    /// Initializes the StorageManager, setting up necessary services if running on the server.
    /// </summary>
    public static void Initialize()
    {
      if (IsInitialized)
      {
        Log(Level.Warning, "StorageManager already initialized", Category.Storage);
        return;
      }
      if (!InstanceFinder.NetworkManager.IsServer)
      {
        Log(Level.Warning, "StorageManager initialization skipped: not server", Category.Storage);
        return;
      }
      SlotService.Initialize();
      IsInitialized = true;
      Log(Level.Info, "StorageManager initialized", Category.Storage);
    }

    /// <summary>
    /// Cleans up resources and resets the StorageManager state.
    /// </summary>
    public static void Cleanup()
    {
      if (!IsInitialized)
      {
        Log(Level.Warning, "StorageManager not initialized, skipping cleanup", Category.Storage);
        return;
      }
      if (!InstanceFinder.NetworkManager.IsServer)
      {
        Log(Level.Warning, "StorageManager cleanup skipped: not server", Category.Storage);
        return;
      }
      SlotService.Cleanup();
      foreach (var cacheService in CacheServices.Values.ToList())
        cacheService.Dispose();
      CacheServices.Clear();
      _intListPool.Clear();
      _slotListeners.Clear();
      _shelfListeners.Clear();
      IsInitialized = false;
      Log(Level.Info, "StorageManager cleaned up", Category.Storage);
    }

    /// <summary>
    /// Processes a collection of log entries and logs them at the specified level and category.
    /// </summary>
    /// <param name="logs">The collection of log entries to process.</param>
    private static void ProcessLogs(NativeList<LogEntry> logs)
    {
      foreach (var log in logs)
      {
#if DEBUG
        Log(Level.Verbose, log.Message.ToString(), log.Category);
#else
        Log(log.Level, log.Message.ToString(), log.Category);
#endif
      }
    }

    /// <summary>
    /// Clears the cache for a specific entity identified by its GUID within a property.
    /// </summary>
    /// <param name="property">The property containing the entity.</param>
    /// <param name="guid">The GUID of the entity to clear from cache.</param>
    public static void ClearEntityCache(Property property, Guid guid)
    {
      CacheService.GetOrCreateCacheService(property).ClearCacheForEntity(guid);
    }

    /// <summary>
    /// Reserves a slot for an item with a specified locker and reason.
    /// </summary>
    /// <param name="entityGuid">The GUID of the entity containing the slot.</param>
    /// <param name="slot">The slot to reserve.</param>
    /// <param name="locker">The network object locking the slot.</param>
    /// <param name="lockReason">The reason for locking the slot.</param>
    /// <param name="item">The item to reserve (optional).</param>
    /// <param name="quantity">The quantity to reserve (optional).</param>
    /// <returns>True if the slot was reserved successfully, false otherwise.</returns>
    public static bool ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)
    {
      return SlotService.ReserveSlot(entityGuid, slot, locker, lockReason, item, quantity);
    }

    /// <summary>
    /// Releases a previously reserved slot.
    /// </summary>
    /// <param name="slot">The slot to release.</param>
    public static void ReleaseSlot(ItemSlot slot)
    {
      SlotService.ReleaseSlot(slot);
    }

    #region Coroutine Entrypoints

    /// <summary>
    /// Finds storage containing a specific item with the required quantity.
    /// </summary>
    /// <param name="property">The property to search within.</param>
    /// <param name="item">The item to find.</param>
    /// <param name="needed">The quantity needed.</param>
    /// <param name="allowTargetHigherQuality">Whether to allow items of higher quality.</param>
    /// <returns>An enumerator yielding a StorageResult with the found storage details.</returns>
    public static IEnumerator FindStorageWithItem(Property property, ItemInstance item, int needed, bool allowTargetHigherQuality = false)
    {
      if (!IsInitialized)
      {
        Log(Level.Error, "StorageManager not initialized, cannot find storage", Category.Storage);
        yield return new StorageResult();
        yield break;
      }
      if (!InstanceFinder.NetworkManager.IsServer)
      {
        Log(Level.Error, "FindStorageWithItem skipped: not server", Category.Storage);
        yield return new StorageResult();
        yield break;
      }
      if (item == null || needed <= 0)
      {
        Log(Level.Warning, $"FindStorageWithItem: Invalid input item={item?.ID}, needed={needed}", Category.Storage);
        yield return new StorageResult();
        yield break;
      }
      var cacheService = CacheService.GetOrCreateCacheService(property);
      var itemKey = new ItemKey(item);
      var itemData = new ItemData(item);
      if (cacheService.IsItemNotFound(itemKey))
      {
#if DEBUG
        Log(Level.Verbose, $"Item {itemKey.ToString()} not found in cache", Category.Storage);
#endif
        yield return new StorageResult();
        yield break;
      }
      if (cacheService._filteredStorageKeys.TryGetValue(itemKey, out var cachedKeys) && cachedKeys.Length > 0)
      {
        if (Storages[property].TryGetValue(cachedKeys[0].Guid, out var shelf))
        {
          if (cacheService._shelfSearchCache.TryGetValue(itemKey, out var cachedResult) && cachedResult.Item2 >= needed)
          {
#if DEBUG
            Log(Level.Verbose, $"Using cached result for item {itemKey.ToString()}", Category.Storage);
#endif
            var persistentSlotData = new NativeList<SlotData>(Allocator.Persistent);
            foreach (var slot in cachedResult.Item3)
              persistentSlotData.Add(slot);
            yield return new StorageResultBurst
            {
              ShelfGuid = shelf.GUID,
              AvailableQuantity = cachedResult.Item2,
              SlotData = persistentSlotData
            }.GetResult();
            yield break;
          }
        }
      }
      var storageKeys = new NativeList<StorageKey>(Allocator.TempJob);
      var results = new NativeList<StorageResultBurst>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        if (cacheService._itemToStorageCache.TryGetValue(itemKey, out var specificKeys))
          storageKeys.AddRange(specificKeys.AsArray());
        storageKeys.AddRange(cacheService._anyShelfKeys.AsArray());
        yield return SmartExecution.ExecuteBurstFor<StorageKey, StorageResultBurst>(
            uniqueId: nameof(FindStorageWithItem),
            itemCount: storageKeys.Length,
            burstForDelegate: (i, inputs, outputs) => cacheService.FindItem(i, inputs, outputs, itemData, needed, allowTargetHigherQuality, logs, cacheService._storageSlotsCache),
            burstResultsDelegate: (results) => cacheService.FindItemResults(results, cacheService._shelfSearchCache, cacheService._filteredStorageKeys, cacheService._storagePropertyMap, property.NetworkObject.ObjectId, itemData),
            inputs: storageKeys.AsArray(),
            outputs: results,
            options: default
        );
        StorageResult managedResult = results[0].GetResult();
        if (managedResult.ItemSlots?.Count <= 0)
        {
          cacheService.AddItemNotFound(itemKey);
#if DEBUG
          Log(Level.Verbose, $"No storage found for item {itemKey.ToString()}, added to not found cache", Category.Storage);
#endif
        }
        yield return managedResult;
      }
      finally
      {
        ProcessLogs(logs);
        storageKeys.Dispose();
        results.Dispose();
        logs.Dispose();
        cacheService._storageResultPool.Release(new List<StorageResultBurst>());
      }
    }

    /// <summary>
    /// Finds delivery destinations for an item with the specified quantity.
    /// </summary>
    /// <param name="property">The property to search within.</param>
    /// <param name="item">The item to deliver.</param>
    /// <param name="quantity">The quantity to deliver.</param>
    /// <param name="sourceGuid">The GUID of the source entity.</param>
    /// <returns>An enumerator yielding a list of DeliveryDestinationBurst objects.</returns>
    public static IEnumerator FindDeliveryDestinations(Property property, ItemInstance item, int quantity, Guid sourceGuid)
    {
      if (!IsInitialized || !InstanceFinder.NetworkManager.IsServer || item == null || quantity <= 0)
      {
        Log(Level.Error, $"FindDeliveryDestinations: Invalid input or state (init={IsInitialized}, server={InstanceFinder.NetworkManager.IsServer}, item={item?.ID}, qty={quantity})", Category.Storage);
        yield return new List<DeliveryDestinationBurst>();
        yield break;
      }
      var cacheService = CacheService.GetOrCreateCacheService(property);
      var itemKey = new ItemKey(item);
      var destinations = new NativeList<DeliveryDestinationBurst>(Allocator.TempJob);
      var inputs = new NativeList<(StorageKey, bool)>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        if (IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values)
          {
#if DEBUG
            Log(Level.Verbose, $"Checking station {station.GUID} for delivery", Category.Storage);
#endif
            if (station.TypeOf == typeof(PackagingStation) && !station.IsInUse && station.CanRefill(item))
              inputs.Add((new StorageKey(station.GUID, StorageType.Station), true));
          }
        }
        if (cacheService._itemToStorageCache.TryGetValue(itemKey, out var specificKeys))
        {
          foreach (var key in specificKeys.AsArray())
          {
#if DEBUG
            Log(Level.Verbose, $"Adding specific key {key.Guid} for item {itemKey.ToString()}", Category.Storage);
#endif
            inputs.Add((key, false));
          }
        }
        foreach (var key in cacheService._anyShelfKeys.AsArray())
        {
#if DEBUG
          Log(Level.Verbose, $"Adding any shelf key {key.Guid}", Category.Storage);
#endif
          inputs.Add((key, false));
        }
        int remainingQty = quantity;
        yield return SmartExecution.ExecuteBurstFor<(StorageKey, bool), DeliveryDestinationBurst>(
            uniqueId: nameof(FindDeliveryDestinations),
            itemCount: inputs.Length,
            burstForDelegate: (i, inputs, outputs) => cacheService.FindDeliveryDestination(i, inputs, outputs, quantity, sourceGuid, logs, cacheService._storageSlotsCache, ref remainingQty),
            burstResultsDelegate: cacheService.FindDeliveryDestinationResults,
            inputs: inputs.AsArray(),
            outputs: destinations,
            options: default
        );
        List<DeliveryDestination> managedResult = new();
        foreach (var destination in destinations)
        {
#if DEBUG
          Log(Level.Verbose, $"Returning destination {destination.Guid} with capacity {destination.Capacity}", Category.Storage);
#endif
          managedResult.Add(destination.GetResult());
        }
        yield return managedResult;
      }
      finally
      {
        ProcessLogs(logs);
        inputs.Dispose();
        destinations.Dispose();
        logs.Dispose();
      }
    }

    /// <summary>
    /// Finds available slots for storing an item with the specified quantity.
    /// </summary>
    /// <param name="property">The property to search within.</param>
    /// <param name="slots">The list of slots to check.</param>
    /// <param name="item">The item to store.</param>
    /// <param name="quantity">The quantity to store.</param>
    /// <returns>An enumerator yielding a list of tuples containing available slots and their capacities.</returns>
    public static IEnumerator FindAvailableSlots(Property property, Guid entityGuid, List<ItemSlot> slots, ItemInstance item, int quantity)
    {
      if (!IsInitialized || !InstanceFinder.NetworkManager.IsServer || slots == null || item == null || quantity <= 0)
      {
        Log(Level.Error, $"FindAvailableSlots: Invalid input or state (init={IsInitialized}, server={InstanceFinder.NetworkManager.IsServer}, slots={slots?.Count ?? 0}, item={item != null}, qty={quantity})", Category.Tasks);
        yield return new List<(ItemSlot, int)>();
        yield break;
      }
      var cacheService = CacheService.GetOrCreateCacheService(property);
      var results = new NativeList<SlotResult>(slots.Count, Allocator.TempJob);
      var logs = new NativeList<LogEntry>(slots.Count, Allocator.TempJob);
      var slotData = new NativeArray<SlotData>(slots.Count, Allocator.TempJob);
      try
      {
        for (int i = 0; i < slots.Count; i++)
        {
#if DEBUG
          Log(Level.Verbose, $"Processing slot {slots[i].SlotIndex} for availability", Category.Tasks);
#endif
          slotData[i] = new SlotData(entityGuid, slots[i], slots[i].OwnerType());
        }
        yield return SmartExecution.ExecuteBurstFor<SlotData, SlotResult>(
            uniqueId: nameof(FindAvailableSlots),
            itemCount: slots.Count,
            burstForDelegate: (index, inputs, outputs) => cacheService.FindAvailableSlotsDelegate(index, inputs, outputs, new ItemData(item), quantity, logs),
            burstResultsDelegate: (results) => cacheService.ProcessSlotResults(results, slots),
            inputs: slotData,
            outputs: results,
            options: default
        );
        List<(ItemSlot, int)> managedResult = [.. results.Select(r => (slots[r.SlotIndex], r.Capacity))];
        yield return managedResult;
      }
      finally
      {
        ProcessLogs(logs);
        slotData.Dispose();
        results.Dispose();
        logs.Dispose();
      }
    }

    /// <summary>
    /// Executes a list of slot operations (insert or remove) for items.
    /// </summary>
    /// <param name="property">The property to perform operations within.</param>
    /// <param name="operations">The list of operations to execute.</param>
    /// <returns>An enumerator yielding a list of boolean results indicating success for each operation.</returns>
    public static IEnumerator ExecuteSlotOperations(Property property, List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations)
    {
      if (!IsInitialized || !InstanceFinder.NetworkManager.IsServer || operations == null || operations.Count == 0)
      {
        Log(Level.Error, $"ExecuteSlotOperations: Invalid input or state (init={IsInitialized}, server={InstanceFinder.NetworkManager.IsServer}, ops={operations?.Count ?? 0})", Category.Tasks);
        yield return new List<bool>();
        yield break;
      }
      var cacheService = CacheService.GetOrCreateCacheService(property);
      var opList = SlotService._operationPool.Get();
      var opData = new NativeArray<OperationData>(operations.Count, Allocator.TempJob);
      var results = new NativeList<SlotOperationResult>(operations.Count, Allocator.TempJob);
      var logs = new NativeList<LogEntry>(operations.Count, Allocator.TempJob);
      try
      {
        foreach (var operation in operations)
        {
#if DEBUG
          Log(Level.Verbose, $"Adding operation for slot {operation.Slot.SlotIndex}, item {operation.Item?.ID}, quantity {operation.Quantity}", Category.Tasks);
#endif
          SlotService.NetworkObjectCache.Add(operation.Locker);
        }
        for (int i = 0; i < operations.Count; i++)
        {
          var op = operations[i];
          opData[i] = new OperationData
          {
            SlotKey = new SlotKey(op.EntityGuid, op.Slot.SlotIndex),
            Slot = new SlotData(op.EntityGuid, op.Slot, op.Slot.OwnerType()),
            Item = new ItemData(op.Item),
            Quantity = op.Quantity,
            IsInsert = op.IsInsert,
            LockerId = op.Locker != null ? op.Locker.ObjectId : 0,
            LockReason = op.LockReason
          };
        }
        yield return SmartExecution.ExecuteBurstFor<OperationData, SlotOperationResult>(
            uniqueId: nameof(ExecuteSlotOperations),
            itemCount: operations.Count,
            burstForDelegate: (index, inputs, outputs) => cacheService.ProcessSlotOperationsDelegate(index, inputs, outputs, logs),
            burstResultsDelegate: (results) => cacheService.ProcessOperationResults(results, operations, opList, SlotService.NetworkObjectCache),
            inputs: opData,
            outputs: results,
            options: default
        );
        List<bool> managedResult = [.. results.Select(r => r.IsValid)];
        yield return managedResult;
        SlotService._operationPool.Release(opList);
      }
      finally
      {
        ProcessLogs(logs);
        opData.Dispose();
        results.Dispose();
        logs.Dispose();
        SlotService.NetworkObjectCache.Clear();
      }
    }

    #endregion
  }

  public static class Extensions
  {
    /// <summary>
    /// Represents a slot operation with details about the entity, slot, item, and locking information.
    /// </summary>
    public struct SlotOperation
    {
      public Guid EntityGuid;
      public SlotKey SlotKey;
      public ItemSlot Slot;
      public ItemInstance Item;
      public int Quantity;
      public bool IsInsert;
      public NetworkObject Locker;
      public string LockReason;
    }

    /// <summary>
    /// Represents the result of checking a slot's availability.
    /// </summary>
    public struct SlotResult
    {
      public int SlotIndex;
      public int Capacity;
    }

    /// <summary>
    /// Contains data for a slot operation, including slot and item details.
    /// </summary>
    public struct OperationData
    {
      public SlotKey SlotKey;
      public SlotData Slot;
      public ItemData Item;
      public int Quantity;
      public bool IsInsert;
      public int LockerId;
      public FixedString128Bytes LockReason;
      public bool IsValid => Slot.IsValid && LockerId != 0 && Quantity > 0;
    }

    /// <summary>
    /// Represents the result of a slot operation.
    /// </summary>
    public struct SlotOperationResult
    {
      public bool IsValid;
      public Guid EntityGuid;
      public int SlotIndex;
      public ItemData Item;
      public int Quantity;
      public bool IsInsert;
      public int LockerId;
      public FixedString128Bytes LockReason;
    }

    /// <summary>
    /// Represents a reservation for a slot.
    /// </summary>
    public struct SlotReservation
    {
      public Guid EntityGuid;
      public float Timestamp;
      public FixedString32Bytes Locker;
      public FixedString128Bytes LockReason;
      public ItemData Item;
      public int Quantity;
    }

    [BurstCompile]
    public struct StationData
    {
      public Guid GUID;
      public int PropertyId;
      public NativeList<SlotData> InsertSlots;
      public NativeList<SlotData> ProductSlots;
      public SlotData OutputSlot;
      public FixedString32Bytes TypeName;
      public bool IsInUse;
      public NativeList<ItemData> RefillList;

      public StationData(IStationAdapter station)
      {
        GUID = station.GUID;
        PropertyId = station.ParentProperty.NetworkObject.ObjectId;
        InsertSlots = new NativeList<SlotData>(Allocator.Persistent);
        ProductSlots = new NativeList<SlotData>(Allocator.Persistent);
        RefillList = new NativeList<ItemData>(Allocator.Persistent);
        var guid = station.GUID;
        foreach (var slot in station.InsertSlots)
          InsertSlots.Add(new SlotData(guid, slot, StorageType.Station));
        foreach (var slot in station.ProductSlots)
          ProductSlots.Add(new SlotData(guid, slot, StorageType.Station));
        OutputSlot = new SlotData(guid, station.OutputSlot, StorageType.Station);
        TypeName = station.TypeOf.ToString();
        IsInUse = station.IsInUse;
        foreach (var item in station.RefillList())
          RefillList.Add(new ItemData(item));
      }

      public IStationAdapter ToStationAdapter()
      {
        return ManagedDictionaries.IStations[ManagedDictionaries.IdToProperty[PropertyId]][GUID];
      }

      public void Dispose()
      {
        if (InsertSlots.IsCreated) InsertSlots.Dispose();
        if (ProductSlots.IsCreated) ProductSlots.Dispose();
        if (RefillList.IsCreated) RefillList.Dispose();
      }
    }

    [BurstCompile]
    public struct StorageData
    {
      public Guid GUID;
      public NativeList<SlotData> OutputSlots;
      public int PropertyId;

      public StorageData(PlaceableStorageEntity shelf)
      {
        GUID = shelf.GUID;
        OutputSlots = new NativeList<SlotData>(Allocator.Persistent);
        var type = ManagedDictionaries.StorageConfigs.TryGetValue(shelf.ParentProperty, out var configs) &&
                   configs.TryGetValue(shelf.GUID, out var config) && config.Mode == StorageMode.Specific
                   ? StorageType.SpecificShelf
                   : StorageType.AnyShelf;
        foreach (var slot in shelf.OutputSlots)
          OutputSlots.Add(new SlotData(shelf.GUID, slot, type));
        PropertyId = shelf.ParentProperty.NetworkObject.ObjectId;
      }

      public PlaceableStorageEntity ToPlaceableStorageEntity()
      {
        return ManagedDictionaries.Storages[ManagedDictionaries.IdToProperty[PropertyId]][GUID];
      }

      public void Dispose()
      {
        if (OutputSlots.IsCreated) OutputSlots.Dispose();
      }
    }

    public enum StorageType
    {
      Employee,
      AnyShelf,
      SpecificShelf,
      Station,
      LoadingDock
    }

    public struct StorageKey : IEquatable<StorageKey>
    {
      public int PropertyId;
      public Guid Guid;
      public StorageType Type;

      public StorageKey(Guid guid, StorageType type)
      {
        PropertyId = 0;
        Guid = guid != Guid.Empty ? guid : throw new ArgumentException("Invalid GUID");
        Type = type;
      }

      public bool Equals(StorageKey other) => Guid.Equals(other.Guid);
      public override bool Equals(object obj) => obj is StorageKey other && Equals(other);
      public override int GetHashCode() => Guid.GetHashCode();
    }

    /// <summary>
    /// Stores item data, including ID, packaging, quality, and quantity.
    /// </summary>
    [BurstCompile]
    public struct ItemData : IEquatable<ItemData>
    {
      public FixedString32Bytes Id;
      public FixedString32Bytes PackagingId;
      public EQualityBurst Quality;
      public int Quantity;
      public int StackLimit;
      public static ItemData Empty => new ItemData("", "", EQualityBurst.None, 0, -1);

      public ItemData(ItemInstance item)
      {
        Id = item.ID ?? throw new ArgumentNullException(nameof(Id));
        PackagingId = (item as ProductItemInstance)?.AppliedPackaging?.ID ?? "";
        Quality = (item is ProductItemInstance prodItem) ? Enum.Parse<EQualityBurst>(prodItem.Quality.ToString()) : EQualityBurst.None;
        Quantity = item.Quantity;
        StackLimit = item.StackLimit;
      }

      public ItemData(string id, string packagingId, EQualityBurst? quality, int quantity, int stackLimit)
      {
        Id = id ?? "";
        PackagingId = packagingId ?? "";
        Quality = quality ?? EQualityBurst.None;
        Quantity = quantity;
        StackLimit = stackLimit;
      }

      public string ItemId => CacheId + " Quantity=" + Quantity;
      public string CacheId => Id.ToString() + (PackagingId.ToString() != "" ? " Packaging:" + PackagingId.ToString() : "") + (Quality != EQualityBurst.None ? " Quality:" + Quality.ToString() : "") + " Stacklimit=" + StackLimit;

      public bool Equals(ItemData other) => Id == other.Id && PackagingId == other.PackagingId && Quality == other.Quality;
      public override bool Equals(object obj) => obj is ItemData other && Equals(other);
      public override int GetHashCode() => HashCode.Combine(Id, PackagingId, Quality);

      public ItemInstance CreateItemInstance()
      {
        string itemId = Id.ToString();
        if (PackagingId != "")
        {
          return new ProductItemInstance
          {
            ID = itemId,
            Quality = Enum.Parse<EQuality>(Quality.ToString()),
            PackagingID = PackagingId.ToString()
          };
        }
        if (Quality != EQualityBurst.None)
        {
          return new QualityItemInstance
          {
            ID = itemId,
            Quality = Enum.Parse<EQuality>(Quality.ToString())
          };
        }
        return Registry.GetItem(itemId)?.GetDefaultInstance() ??
               throw new ArgumentException($"No default item instance for ID: {itemId}");
      }

      [BurstCompile]
      internal readonly bool AdvCanStackWithBurst(ItemData targetItem, bool allowTargetHigherQuality = false, bool checkQuantities = false)
      {
        bool qualityMatch;
        if (targetItem.Id == "Any")
        {
          qualityMatch = allowTargetHigherQuality ? Quality <= targetItem.Quality : Quality == targetItem.Quality;
          return qualityMatch;
        }
        if (Id != targetItem.Id)
          return false;
        if (checkQuantities)
        {
          if (StackLimit == -1)
            throw new ArgumentException($"AdvCanStackWith: CheckQuantities requires a stackLimit");
          if (StackLimit < targetItem.Quantity + Quantity)
            return false;
        }
        if (!ArePackagingsCompatible(targetItem))
          return false;
        qualityMatch = allowTargetHigherQuality ? Quality <= targetItem.Quality : Quality == targetItem.Quality;
        return qualityMatch;
      }

      [BurstCompile]
      private readonly bool ArePackagingsCompatible(ItemData targetItem)
      {
        return (PackagingId == "" && targetItem.PackagingId == "") ||
               (PackagingId != "" && targetItem.PackagingId != "" && PackagingId == targetItem.PackagingId);
      }
    }

    public readonly struct ItemKey : IEquatable<ItemKey>
    {
      public readonly FixedString32Bytes ItemId;
      public readonly FixedString32Bytes PackagingId;
      public readonly EQualityBurst Quality;
      public readonly FixedString64Bytes CacheId;

      public ItemKey(ItemInstance item)
      {
        if (item == null) throw new ArgumentNullException(nameof(item));
        var itemKey = new ItemData(item);
        ItemId = itemKey.Id;
        PackagingId = itemKey.PackagingId;
        Quality = itemKey.Quality;
        CacheId = itemKey.CacheId;
      }

      public ItemKey(ItemData itemKey)
      {
        ItemId = itemKey.Id;
        PackagingId = itemKey.PackagingId;
        Quality = itemKey.Quality;
        CacheId = itemKey.CacheId;
      }

      internal new string ToString() => CacheId.ToString();
      public bool Equals(ItemKey other) => CacheId == other.CacheId;
      public override bool Equals(object obj) => obj is ItemKey other && Equals(other);
      public override int GetHashCode() => HashCode.Combine(ItemId, PackagingId, Quality);
    }

    [BurstCompile]
    public struct SlotData : IEquatable<SlotData>
    {
      public int PropertyId;
      public Guid EntityGuid;
      public int SlotIndex;
      public ItemData Item;
      public int Quantity;
      public bool IsLocked;
      public int StackLimit;
      public bool IsValid;
      public StorageType Type;

      public SlotData(Guid guid, ItemSlot slot, StorageType type)
      {
        SlotIndex = slot.SlotIndex;
        Item = slot.ItemInstance != null ? new ItemData(slot.ItemInstance) : ItemData.Empty;
        Quantity = slot.Quantity;
        IsLocked = slot.IsLocked;
        StackLimit = slot.ItemInstance?.StackLimit ?? -1;
        IsValid = true;
        EntityGuid = guid;
        Type = type;
      }

      public ItemSlot ItemSlot => new SlotKey(EntityGuid, SlotIndex).GetItemSlotFromKey(IdToProperty[PropertyId]);

      public bool Equals(SlotData other) => EntityGuid == other.EntityGuid && SlotIndex == other.SlotIndex;
      public override bool Equals(object obj) => obj is SlotData other && Equals(other);
      public override int GetHashCode() => HashCode.Combine(EntityGuid, SlotIndex);
    }

    public struct SlotKey : IEquatable<SlotKey>
    {
      public Guid EntityGuid;
      public int SlotIndex;

      public SlotKey(Guid entityGuid, int slotIndex)
      {
        EntityGuid = entityGuid;
        SlotIndex = slotIndex;
      }

      public bool Equals(SlotKey other) => EntityGuid == other.EntityGuid && SlotIndex == other.SlotIndex;
      public override bool Equals(object obj) => obj is SlotKey other && Equals(other);
      public override int GetHashCode() => HashCode.Combine(EntityGuid, SlotIndex);

      internal ItemSlot GetItemSlotFromKey(Property property)
      {
        var cacheService = CacheService.GetOrCreateCacheService(property);
        if (cacheService._slotCache.TryGetValue(this, out var slotInfo))
        {
          var _this = this;
          switch (slotInfo.Type)
          {
            case StorageType.AnyShelf:
            case StorageType.SpecificShelf:
              if (ManagedDictionaries.Storages.TryGetValue(property, out var storages) &&
                  storages.TryGetValue(EntityGuid, out var shelf))
                return shelf.OutputSlots[SlotIndex];
              break;
            case StorageType.Station:
              if (ManagedDictionaries.IStations.TryGetValue(property, out var stations) &&
                  stations.TryGetValue(EntityGuid, out var station))
                return station.InsertSlots.Concat(station.ProductSlots).Concat(new[] { station.OutputSlot })
                  .FirstOrDefault(s => s.SlotIndex == _this.SlotIndex);
              break;
            case StorageType.LoadingDock:
              if (ManagedDictionaries.LoadingDocks.TryGetValue(property, out var docks) &&
                  docks.TryGetValue(EntityGuid, out var dock))
                return dock.OutputSlots[SlotIndex];
              break;
            case StorageType.Employee:
              if (ManagedDictionaries.IEmployees.TryGetValue(property, out var employees) &&
                  employees.TryGetValue(EntityGuid, out var employee))
                return employee.InventorySlots[SlotIndex];
              break;
          }
        }
        throw new KeyNotFoundException($"Slot {SlotIndex} for entity {EntityGuid} not found");
      }
    }

    /// <summary>
    /// Represents a storage result optimized for Burst compilation.
    /// </summary>
    [BurstCompile]
    public struct StorageResultBurst
    {
      public int PropertyId;
      public Guid ShelfGuid;
      public int AvailableQuantity;
      public NativeList<SlotData> SlotData;

      /// <summary>
      /// Converts the Burst-optimized result to a managed StorageResult.
      /// </summary>
      /// <returns>The managed StorageResult.</returns>
      public StorageResult GetResult()
      {
        var shelf = Storages[IdToProperty[PropertyId]][ShelfGuid];
        List<ItemSlot> itemSlots = new();
        foreach (var slot in SlotData)
          itemSlots.Add(shelf.InputSlots[slot.SlotIndex]);
        SlotData.Dispose();
        return new StorageResult()
        {
          Shelf = shelf,
          AvailableQuantity = AvailableQuantity,
          ItemSlots = itemSlots
        };
      }
    }

    /// <summary>
    /// Represents a storage result with shelf and slot details.
    /// </summary>
    public struct StorageResult
    {
      public PlaceableStorageEntity Shelf;
      public int AvailableQuantity;
      public List<ItemSlot> ItemSlots;
    }

    /// <summary>
    /// Represents a delivery destination optimized for Burst compilation.
    /// </summary>
    [BurstCompile]
    public struct DeliveryDestinationBurst
    {
      public Guid Guid;
      public NativeList<SlotData> SlotData;
      public int Capacity;

      /// <summary>
      /// Converts the Burst-optimized result to a managed DeliveryDestination.
      /// </summary>
      /// <returns>The managed DeliveryDestination.</returns>
      public DeliveryDestination GetResult()
      {
        var result = new DeliveryDestination();
        List<ItemSlot> itemSlots = new();
        foreach (var slot in SlotData)
          itemSlots.Add(slot.ItemSlot);
        SlotData.Dispose();
        return result;
      }
    }

    /// <summary>
    /// Represents a delivery destination with entity and slot details.
    /// </summary>
    public struct DeliveryDestination
    {
      public ITransitEntity Entity;
      public List<ItemSlot> ItemSlots;
      public int Capacity;
    }
  }

  internal static class ManagedDictionaries
  {
    public static readonly Dictionary<Property, CacheService> CacheServices = new();
    public static Dictionary<int, Property> IdToProperty = new();
    public static Dictionary<Property, Dictionary<Guid, StorageConfiguration>> StorageConfigs = new();
    public static Dictionary<Property, Dictionary<Guid, PlaceableStorageEntity>> Storages = new();
    public static Dictionary<Property, Dictionary<Guid, LoadingDock>> LoadingDocks = new();
    public static Dictionary<Property, Dictionary<Guid, IStationAdapter>> IStations = new();
    public static Dictionary<Property, Dictionary<Guid, IEmployeeAdapter>> IEmployees = new();
    public static Dictionary<Guid, JObject> PendingConfigData = new();
    public static Dictionary<Guid, List<ItemInstance>> StationRefillLists = [];
    public static readonly Dictionary<Property, HashSet<ItemInstance>> NoDropOffCache = new();
  }

  /// <summary>
  /// Manages object pools for various data types used in storage operations.
  /// </summary>
  internal class PoolManager
  {
    private static PoolManager _instance;
    public static PoolManager Instance => _instance ??= new PoolManager();

    public ObjectPool<List<int>> IntListPool { get; }
    public ObjectPool<List<StorageResultBurst>> StorageResultListPool { get; }
    public ObjectPool<List<DeliveryDestinationBurst>> DeliveryDestinationListPool { get; }
    public ObjectPool<List<bool>> BoolListPool { get; }
    public ObjectPool<List<SlotData>> SlotDataListPool { get; }
    public ObjectPool<List<(ItemData, int)>> ItemQuantityListPool { get; }

    private PoolManager()
    {
      IntListPool = CreatePool<List<int>>(10);
      StorageResultListPool = CreatePool<List<StorageResultBurst>>(10);
      DeliveryDestinationListPool = CreatePool<List<DeliveryDestinationBurst>>(10);
      BoolListPool = CreatePool<List<bool>>(10);
      SlotDataListPool = CreatePool<List<SlotData>>(10);
      ItemQuantityListPool = CreatePool<List<(ItemData, int)>>(10);
    }

    /// <summary>
    /// Creates an object pool for a specified type with a default capacity.
    /// </summary>
    /// <param name="defaultCapacity">The default capacity of the pool.</param>
    /// <returns>An ObjectPool for the specified type.</returns>
    private ObjectPool<T> CreatePool<T>(int defaultCapacity) where T : class, new()
    {
      return new ObjectPool<T>(
          createFunc: () => new T(),
          actionOnGet: null,
          actionOnRelease: list => { if (list is IList items) items.Clear(); },
          actionOnDestroy: null,
          collectionCheck: false,
          defaultCapacity: defaultCapacity,
          maxSize: 100);
    }

    /// <summary>
    /// Clears all object pools.
    /// </summary>
    public void Cleanup()
    {
      IntListPool.Clear();
      StorageResultListPool.Clear();
      DeliveryDestinationListPool.Clear();
      BoolListPool.Clear();
      SlotDataListPool.Clear();
      ItemQuantityListPool.Clear();
    }
  }

  /// <summary>
  /// Manages caching for storage-related data and operations.
  /// </summary>
  internal class CacheService : IDisposable
  {
    private readonly Property _property;
    private bool _isActive;
    private bool IsInitialized { get; set; }
    private ConcurrentQueue<(StorageKey, NativeList<SlotData>)> _pendingSlotUpdates;
    internal NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)> _slotCache;
    internal NativeParallelHashMap<StorageKey, NativeList<SlotData>> _storageSlotsCache;
    internal NativeParallelHashMap<ItemKey, NativeList<StorageKey>> _itemToStorageCache;
    internal NativeList<StorageKey> _anyShelfKeys;
    internal readonly ObjectPool<List<StorageResultBurst>> _storageResultPool;
    internal readonly ObjectPool<List<DeliveryDestinationBurst>> _deliveryResultPool;
    internal NativeList<LogEntry> _logs;
    internal NativeParallelHashSet<ItemKey> _notFoundItems;
    internal NativeParallelHashMap<ItemKey, (StorageKey, int, NativeList<SlotData>)> _shelfSearchCache;
    internal NativeParallelHashMap<Guid, StorageKey> _loadingDockKeys;
    internal NativeParallelHashMap<ItemKey, NativeList<StorageKey>> _filteredStorageKeys;
    internal NativeParallelHashMap<StorageKey, int> _storagePropertyMap;

    public CacheService(Property property)
    {
      _property = property ?? throw new ArgumentNullException(nameof(property));
      _storageSlotsCache = new NativeParallelHashMap<StorageKey, NativeList<SlotData>>(100, Allocator.Persistent);
      _itemToStorageCache = new NativeParallelHashMap<ItemKey, NativeList<StorageKey>>(100, Allocator.Persistent);
      _slotCache = new NativeParallelHashMap<SlotKey, (int PropertyId, StorageType Type, int SlotIndex)>(100, Allocator.Persistent);
      _anyShelfKeys = new NativeList<StorageKey>(100, Allocator.Persistent);
      _pendingSlotUpdates = new ConcurrentQueue<(StorageKey, NativeList<SlotData>)>();
      _storageResultPool = new ObjectPool<List<StorageResultBurst>>(
          createFunc: () => new List<StorageResultBurst>(10),
          actionOnGet: null,
          actionOnRelease: list => list.Clear(),
          actionOnDestroy: null,
          collectionCheck: false,
          defaultCapacity: 10,
          maxSize: 100);
      _logs = new NativeList<LogEntry>(100, Allocator.Persistent);
      _notFoundItems = new NativeParallelHashSet<ItemKey>(100, Allocator.Persistent);
      _shelfSearchCache = new NativeParallelHashMap<ItemKey, (StorageKey, int, NativeList<SlotData>)>(100, Allocator.Persistent);
      _storagePropertyMap = new NativeParallelHashMap<StorageKey, int>(100, Allocator.Persistent);
      _filteredStorageKeys = new NativeParallelHashMap<ItemKey, NativeList<StorageKey>>(100, Allocator.Persistent);
      _loadingDockKeys = new NativeParallelHashMap<Guid, StorageKey>(10, Allocator.Persistent);
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
      if (InstanceFinder.NetworkManager.IsServer)
        InstanceFinder.TimeManager.OnTick += ProcessPendingUpdates;
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

    /// <summary>
    /// Disposes of all native collections and clears pools.
    /// </summary>
    public void Dispose()
    {
      if (!IsInitialized)
      {
        Log(Level.Warning, $"CacheService for property {_property} not initialized, skipping dispose", Category.Storage);
        return;
      }
      Cleanup();
      if (_storageSlotsCache.IsCreated)
      {
        var slotsArray = _storageSlotsCache.GetValueArray(Allocator.Temp);
        foreach (var slots in slotsArray)
          slots.Dispose();
        _slotCache.Dispose();
        slotsArray.Dispose();
        _storageSlotsCache.Dispose();
      }
      if (_itemToStorageCache.IsCreated)
      {
        var keysArray = _itemToStorageCache.GetValueArray(Allocator.Temp);
        foreach (var keys in keysArray)
          keys.Dispose();
        keysArray.Dispose();
        _itemToStorageCache.Dispose();
      }
      if (_anyShelfKeys.IsCreated)
        _anyShelfKeys.Dispose();
      if (_logs.IsCreated)
        _logs.Dispose();
      if (_notFoundItems.IsCreated)
        _notFoundItems.Dispose();
      if (_shelfSearchCache.IsCreated)
      {
        var cacheArray = _shelfSearchCache.GetValueArray(Allocator.Temp);
        foreach (var cache in cacheArray)
          cache.Item3.Dispose();
        cacheArray.Dispose();
        _shelfSearchCache.Dispose();
      }
      if (_storagePropertyMap.IsCreated)
        _storagePropertyMap.Dispose();
      if (_filteredStorageKeys.IsCreated)
      {
        var keysArray = _filteredStorageKeys.GetValueArray(Allocator.Temp);
        foreach (var keys in keysArray)
          keys.Dispose();
        keysArray.Dispose();
        _filteredStorageKeys.Dispose();
      }
      _storageResultPool.Clear();
      _deliveryResultPool.Clear();
      IsInitialized = false;
      Log(Level.Info, $"CacheService for property {_property} disposed", Category.Storage);
    }

    /// <summary>
    /// Cleans up pending updates and event subscriptions.
    /// </summary>
    private void Cleanup()
    {
      if (InstanceFinder.NetworkManager.IsServer)
        InstanceFinder.TimeManager.OnTick -= ProcessPendingUpdates;
      _pendingSlotUpdates.Clear();
      Log(Level.Info, "CacheService cleaned up", Category.Storage);
    }

    /// <summary>
    /// Retrieves or creates a CacheService for a given property.
    /// </summary>
    /// <param name="property">The property to associate with the cache service.</param>
    /// <returns>The CacheService instance.</returns>
    public static CacheService GetOrCreateCacheService(Property property)
    {
      if (!InstanceFinder.NetworkManager.IsServer)
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
    /// Updates the cache for a specific storage entity.
    /// </summary>
    /// <param name="shelf">The storage entity to update.</param>
    public void UpdateStorageCache(PlaceableStorageEntity shelf)
    {
      if (shelf == null || shelf.ParentProperty == null)
      {
        Log(Level.Warning, "UpdateStorageCache: Shelf or ParentProperty is null", Category.Storage);
        return;
      }
      if (!StorageConfigs.TryGetValue(shelf.ParentProperty, out var configs) ||
          !configs.TryGetValue(shelf.GUID, out var config))
      {
        Log(Level.Warning, $"UpdateStorageCache: No config for shelf {shelf.GUID}", Category.Storage);
        return;
      }
      var type = config.Mode == StorageMode.Specific ? StorageType.SpecificShelf : StorageType.AnyShelf;
      var key = new StorageKey(shelf.GUID, type);
      var slots = shelf.OutputSlots.Select(s => new SlotData(shelf.GUID, s, type));
      QueueSlotUpdate(key, slots);
      _storagePropertyMap[key] = shelf.ParentProperty.NetworkObject.ObjectId;
      foreach (var slot in slots)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing slot {slot.SlotIndex} for cache update", Category.Storage);
#endif
        if (slot.Item.Id != "")
          ClearItemNotFoundCache(slot.Item.CreateItemInstance());
      }
#if DEBUG
      Log(Level.Verbose, $"UpdateStorageCache: Queued for shelf={shelf.GUID}", Category.Storage);
#endif
    }

    /// <summary>
    /// Queues a slot update for a storage entity.
    /// </summary>
    /// <param name="key">The storage key identifying the entity.</param>
    /// <param name="slots">The collection of slot data to update.</param>
    internal void QueueSlotUpdate(StorageKey key, IEnumerable<SlotData> slots)
    {
      var slotList = new NativeList<SlotData>(slots.Count(), Allocator.Persistent);
      foreach (var slot in slots)
      {
#if DEBUG
        Log(Level.Verbose, $"Queuing slot {slot.SlotIndex} for update", Category.Storage);
#endif
        slotList.Add(slot);
      }
      _pendingSlotUpdates.Enqueue((key, slotList));
#if DEBUG
      Log(Level.Verbose, $"Queued slot update for {key.Type} {key.Guid}", Category.Storage);
#endif
    }

    /// <summary>
    /// Retrieves loading dock keys.
    /// </summary>
    /// <returns>A list of loading dock storage keys.</returns>
    internal NativeList<StorageKey> GetLoadingDockKeys()
    {
      var keys = new NativeList<StorageKey>(_loadingDockKeys.Count(), Allocator.Temp);
      foreach (var kvp in _loadingDockKeys)
      {
#if DEBUG
        Log(Level.Verbose, $"Adding loading dock key {kvp.Value.Guid}", Category.Storage);
#endif
        keys.Add(kvp.Value);
      }
      return keys;
    }

    /// <summary>
    /// Processes pending slot updates on the server tick.
    /// </summary>
    private void ProcessPendingUpdates()
    {
      if (_pendingSlotUpdates.IsEmpty) return;
      CoroutineRunner.Instance.RunCoroutine(ProcessPendingUpdatesCoroutine());
    }

    /// <summary>
    /// Coroutine to process pending slot updates.
    /// </summary>
    /// <returns>An enumerator for processing pending updates.</returns>
    private IEnumerator ProcessPendingUpdatesCoroutine()
    {
      if (_pendingSlotUpdates.IsEmpty) yield break;
      var pendingUpdates = new NativeArray<(StorageKey, NativeList<SlotData>)>(_pendingSlotUpdates.Count, Allocator.TempJob);
      var results = new NativeList<(StorageKey, NativeList<SlotData>)>(Allocator.TempJob);
      int index = 0;
      while (_pendingSlotUpdates.TryDequeue(out var update))
      {
#if DEBUG
        Log(Level.Verbose, $"Dequeueing update for {update.Item1.Guid}", Category.Storage);
#endif
        pendingUpdates[index++] = update;
      }
      yield return SmartExecution.ExecuteBurstFor<(StorageKey, NativeList<SlotData>), (StorageKey, NativeList<SlotData>)>(
          uniqueId: nameof(ProcessPendingUpdatesCoroutine),
          itemCount: pendingUpdates.Length,
          burstForDelegate: (i, inputs, outputs) => ProcessPendingUpdate(i, inputs, outputs, _logs, _storageSlotsCache, _itemToStorageCache, _anyShelfKeys),
          burstResultsDelegate: ProcessPendingUpdateResults,
          inputs: pendingUpdates,
          outputs: results,
          options: default
      );
      pendingUpdates.Dispose();
      results.Dispose();
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
      var storageKey = new StorageKey(guid, StorageType.AnyShelf);
      if (_storageSlotsCache.TryGetValue(storageKey, out var slots))
      {
        slots.Dispose();
        _storageSlotsCache.Remove(storageKey);
      }
      _storagePropertyMap.Remove(storageKey);
#if DEBUG
      Log(Level.Verbose, $"Cleared cache for entity {guid}", Category.Storage);
#endif
    }

    [BurstCompile]
    private static void ProcessSlotCacheItem(int index, NativeArray<SlotKeyData> inputs, NativeList<SlotCacheEntry> outputs)
    {
      var keyData = inputs[index];
      outputs.Add(new SlotCacheEntry
      {
        Key = new SlotKey(keyData.EntityGuid, keyData.SlotIndex),
        PropertyId = keyData.PropertyId,
        Type = keyData.Type,
        SlotIndex = keyData.SlotIndex
      });
    }

    private IEnumerator InitializePropertyDataCaches()
    {
      // Collect slot metadata
      var slotKeys = new NativeList<SlotKeyData>(Allocator.TempJob);
      var results = new NativeList<SlotCacheEntry>(Allocator.TempJob);

      try
      {
        // Storages
        var storages = ManagedDictionaries.Storages.TryGetValue(_property, out var storageDict) ? storageDict.Values.ToList() : new List<PlaceableStorageEntity>();
        foreach (var storage in storages)
        {
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for storage {storage.GUID}", Category.Storage);
#endif
          var type = ManagedDictionaries.StorageConfigs.TryGetValue(_property, out var configs) &&
                     configs.TryGetValue(storage.GUID, out var config) && config.Mode == StorageMode.Specific
                     ? StorageType.SpecificShelf
                     : StorageType.AnyShelf;
          for (int i = 0; i < storage.OutputSlots.Count; i++)
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = storage.GUID,
              SlotIndex = i,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = type
            });
          }
        }

        // Stations
        var stations = ManagedDictionaries.IStations.TryGetValue(_property, out var stationDict) ? stationDict.Values.ToList() : new List<IStationAdapter>();
        foreach (var station in stations)
        {
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for station {station.GUID}", Category.Storage);
#endif
          foreach (var slot in station.InsertSlots.Concat(station.ProductSlots).Concat(new[] { station.OutputSlot }))
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = station.GUID,
              SlotIndex = slot.SlotIndex,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = StorageType.Station
            });
          }
        }

        // LoadingDocks
        var docks = ManagedDictionaries.LoadingDocks.TryGetValue(_property, out var dockDict) ? dockDict.Values.ToList() : new List<LoadingDock>();
        foreach (var dock in docks)
        {
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for loading dock {dock.GUID}", Category.Storage);
#endif
          for (int i = 0; i < dock.OutputSlots.Count; i++)
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = dock.GUID,
              SlotIndex = i,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = StorageType.LoadingDock
            });
          }
        }

        // Employees
        var employees = ManagedDictionaries.IEmployees.TryGetValue(_property, out var employeeDict) ? employeeDict.Values.ToList() : new List<IEmployeeAdapter>();
        foreach (var employee in employees)
        {
#if DEBUG
          Log(Level.Verbose, $"Collecting slot data for employee {employee.Guid}", Category.Storage);
#endif
          for (int i = 0; i < employee.InventorySlots.Count; i++)
          {
            slotKeys.Add(new SlotKeyData
            {
              EntityGuid = employee.Guid,
              SlotIndex = i,
              PropertyId = _property.NetworkObject.ObjectId,
              Type = StorageType.Employee
            });
          }
        }

        yield return SmartExecution.ExecuteBurstFor(
            uniqueId: nameof(InitializePropertyDataCaches),
            itemCount: slotKeys.Length,
            burstForDelegate: ProcessSlotCacheItem,
            burstResultsDelegate: null, // Handled by coroutine
            inputs: slotKeys.AsArray(),
            outputs: results,
            options: default
        );
        yield return ApplySlotCacheResultsCoroutine(results, slotKeys.Length);
      }
      finally
      {
        if (slotKeys.IsCreated) slotKeys.Dispose();
        if (results.IsCreated) results.Dispose();
      }
    }

    [BurstCompile]
    public struct SlotKeyData
    {
      public Guid EntityGuid;
      public int SlotIndex;
      public int PropertyId;
      public StorageType Type;
    }

    [BurstCompile]
    public struct SlotCacheEntry
    {
      public SlotKey Key;
      public int PropertyId;
      public StorageType Type;
      public int SlotIndex;
    }

    private IEnumerator ApplySlotCacheResultsCoroutine(NativeList<SlotCacheEntry> results, int totalItems)
    {
      var batchSize = SmartExecution.GetDynamicBatchSize(totalItems, 0.15f, nameof(ApplySlotCacheResultsCoroutine));
      for (int i = 0; i < results.Length; i++)
      {
        var entry = results[i];
        _slotCache[entry.Key] = (entry.PropertyId, entry.Type, entry.SlotIndex);
        if ((i + 1) % batchSize == 0)
          yield return null;
      }
      Log(Level.Info, $"InitializePropertyDataCaches: Updated for {_property.name}, entities={totalItems}", Category.Storage);
    }

    private IEnumerator ApplySlotCacheResultsCoroutine(NativeList<SlotKeyData> slotKeys, NativeList<SlotCacheEntry> results)
    {
      var batchSize = SmartExecution.GetDynamicBatchSize(slotKeys.Length, 0.15f, nameof(ApplySlotCacheResultsCoroutine));
      for (int i = 0; i < slotKeys.Length; i++)
      {
        var keyData = slotKeys[i];
        var entry = new SlotCacheEntry
        {
          Key = new SlotKey(keyData.EntityGuid, keyData.SlotIndex),
          PropertyId = keyData.PropertyId,
          Type = keyData.Type,
          SlotIndex = keyData.SlotIndex
        };
        _slotCache[entry.Key] = (entry.PropertyId, entry.Type, entry.SlotIndex);
        if ((i + 1) % batchSize == 0)
          yield return null;
      }
      Log(Level.Info, $"InitializePropertyDataCaches: Updated for {_property.name}, entities={slotKeys.Length}", Category.Storage);
    }

    /// <summary>
    /// Attempts to retrieve slots for a storage key.
    /// </summary>
    /// <param name="key">The storage key to retrieve slots for.</param>
    /// <param name="slots">The retrieved slots, if any.</param>
    /// <returns>True if slots were found, false otherwise.</returns>
    [BurstCompile]
    internal bool TryGetSlots(StorageKey key, out NativeList<SlotData> slots)
    {
      bool hit = _storageSlotsCache.TryGetValue(key, out slots);
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
      Log(Level.Verbose, $"Added {cacheKey.CacheId} to not found cache", Category.Storage);
#endif
    }

    /// <summary>
    /// Clears an item from the not found cache.
    /// </summary>
    /// <param name="item">The item to clear from the not found cache.</param>
    public void ClearItemNotFoundCache(ItemInstance item)
    {
      var cacheKey = new ItemKey(item);
      _notFoundItems.Remove(cacheKey);
#if DEBUG
      Log(Level.Verbose, $"Cleared {item.ID} from not found cache", Category.Storage);
#endif
    }

    /// <summary>
    /// Clears entries with zero quantity for a shelf.
    /// </summary>
    /// <param name="shelf">The shelf to clear entries for.</param>
    /// <param name="items">The items to check for zero quantity.</param>
    public void ClearZeroQuantityEntries(PlaceableStorageEntity shelf, ItemInstance[] items)
    {
      foreach (var item in items)
      {
#if DEBUG
        Log(Level.Verbose, $"Clearing zero quantity entry for {item.ID} on shelf {shelf.GUID}", Category.Storage);
#endif
        var cacheKey = new ItemKey(item);
        _shelfSearchCache.Remove(cacheKey);
      }
    }

    /// <summary>
    /// Removes shelf search cache entries for a list of item keys.
    /// </summary>
    /// <param name="cacheKeys">The list of item keys to remove.</param>
    internal void RemoveShelfSearchCacheEntries(List<ItemKey> cacheKeys)
    {
      foreach (var cacheKey in cacheKeys)
      {
#if DEBUG
        Log(Level.Verbose, $"Removed shelf search cache entry for {cacheKey.CacheId}", Category.Storage);
#endif
        _shelfSearchCache.Remove(cacheKey);
      }
    }

    /// <summary>
    /// Registers an item slot in the cache.
    /// </summary>
    /// <param name="slot">The item slot to register.</param>
    /// <param name="storageKey">The storage key associated with the slot.</param>
    internal void RegisterItemSlot(ItemSlot slot, StorageKey storageKey)
    {
      if (slot.ItemInstance != null)
      {
        var cacheKey = new ItemKey(slot.ItemInstance);
        if (!_itemToStorageCache.TryGetValue(cacheKey, out var storageKeys))
        {
          storageKeys = new NativeList<StorageKey>(Allocator.Persistent);
          _itemToStorageCache[cacheKey] = storageKeys;
        }
        if (!storageKeys.Contains(storageKey))
          storageKeys.Add(storageKey);
      }
    }

    /// <summary>
    /// Unregisters an item slot from the cache.
    /// </summary>
    /// <param name="slot">The item slot to unregister.</param>
    /// <param name="storageKey">The storage key associated with the slot.</param>
    internal void UnregisterItemSlot(ItemSlot slot, StorageKey storageKey)
    {
      if (slot.ItemInstance != null)
      {
        var cacheKey = new ItemKey(slot.ItemInstance);
        if (_itemToStorageCache.TryGetValue(cacheKey, out var storageKeys) && storageKeys.Contains(storageKey))
        {
          storageKeys.Remove(storageKey);
          if (storageKeys.Length == 0)
          {
            storageKeys.Dispose();
            _itemToStorageCache.Remove(cacheKey);
          }
        }
      }
    }

    internal static class CacheManagerPatches
    {
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
          var cacheManager = GetOrCreateCacheService(__instance);
          cacheManager.Activate();
        }
      }

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
          var storageKey = new StorageKey(__instance.GUID, StorageType.LoadingDock);
          var cacheManager = GetOrCreateCacheService(__instance.ParentProperty);
          cacheManager._loadingDockKeys[__instance.GUID] = storageKey;
          foreach (var slot in __instance.OutputSlots)
            cacheManager.UnregisterItemSlot(slot, storageKey);
          foreach (var slot in __instance.OutputSlots)
            cacheManager.RegisterItemSlot(slot, storageKey);
          var slotData = __instance.OutputSlots.Select(s => new SlotData
          {
            Item = s.ItemInstance != null ? new ItemData(s.ItemInstance) : ItemData.Empty,
            Quantity = s.Quantity,
            SlotIndex = s.SlotIndex,
            StackLimit = s.ItemInstance?.StackLimit ?? -1,
            IsValid = true
          });
          cacheManager.QueueSlotUpdate(storageKey, slotData);
        }
      }

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
          if (__instance is PlaceableStorageEntity storage)
          {
            if (StorageConfigs[__instance.ParentProperty].TryGetValue(__instance.GUID, out var config))
              config.Dispose();
            var cacheKeys = GetOrCreateCacheService(__instance.ParentProperty)._shelfSearchCache
                .Where(kvp => kvp.Value.Item1.Guid == storage.GUID)
                .Select(kvp => kvp.Key)
                .ToList();
            GetOrCreateCacheService(__instance.ParentProperty).RemoveShelfSearchCacheEntries(cacheKeys);
          }
          if (__instance is ITransitEntity)
            GetOrCreateCacheService(__instance.ParentProperty).ClearCacheForEntity(__instance.GUID);
        }
      }

      [HarmonyPatch(typeof(ItemSlot))]
      public class ItemSlotPatch
      {
        /// <summary>
        /// Updates cache when an item slot's quantity changes.
        /// </summary>
        /// <param name="__instance">The item slot instance.</param>
        /// <param name="change">The quantity change.</param>
        /// <param name="_internal">Whether the change is internal.</param>
        [HarmonyPostfix]
        [HarmonyPatch("ChangeQuantity")]
        public static void ChangeQuantityPostfix(ItemSlot __instance, int change, bool _internal)
        {
          if (change == 0 || __instance.ItemInstance == null) return;
          var owner = __instance.SlotOwner;
          if (owner == null) return;
          var slotKey = __instance.GetSlotKey();
          var property = __instance.GetProperty();
          var type = __instance.OwnerType();

          var cacheService = GetOrCreateCacheService(property);
          cacheService._slotCache[slotKey] = (property.NetworkObject.ObjectId, type, __instance.SlotIndex);
#if DEBUG
          Log(Level.Verbose,
              $"ItemSlotPatch.ChangeQuantityPostfix: Slot={__instance.SlotIndex}, item={__instance.ItemInstance.ID}{(__instance.ItemInstance is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}, change={change}, newQty={__instance.Quantity}, entity={slotKey.EntityGuid}, type={type}",
              Category.Storage);
#endif
          if (change > 0)
            cacheService.ClearItemNotFoundCache(__instance.ItemInstance);
          if (owner is PlaceableStorageEntity shelf && __instance.Quantity <= 0)
            cacheService.ClearZeroQuantityEntries(shelf, new[] { __instance.ItemInstance });
        }

        /// <summary>
        /// Updates cache when an item is set in a slot.
        /// </summary>
        /// <param name="__instance">The item slot instance.</param>
        /// <param name="instance">The item instance set in the slot.</param>
        /// <param name="_internal">Whether the change is internal.</param>
        [HarmonyPostfix]
        [HarmonyPatch("SetStoredItem")]
        public static void SetStoredItemPostfix(ItemSlot __instance, ItemInstance instance, bool _internal)
        {
          var owner = __instance.SlotOwner as PlaceableStorageEntity;
          if (owner == null || instance == null) return;
#if DEBUG
          Log(Level.Verbose,
              $"ItemSlotPatch.SetStoredItemPostfix: Slot={__instance.SlotIndex}, item={(instance?.ID ?? "null")}{(instance is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}, shelf={owner.GUID}",
              Category.Storage);
#endif
          var cacheManager = GetOrCreateCacheService(owner.ParentProperty);
          cacheManager.ClearItemNotFoundCache(instance);
          cacheManager.UpdateStorageCache(owner);
        }

        /// <summary>
        /// Updates cache when an item is cleared from a slot.
        /// </summary>
        /// <param name="__instance">The item slot instance.</param>
        /// <param name="_internal">Whether the change is internal.</param>
        [HarmonyPostfix]
        [HarmonyPatch("ClearStoredInstance")]
        public static void ClearStoredInstancePostfix(ItemSlot __instance, bool _internal)
        {
          var owner = __instance.SlotOwner as PlaceableStorageEntity;
          if (owner == null) return;
#if DEBUG
          Log(Level.Verbose,
              $"ItemSlotPatch.ClearStoredInstancePostfix: Slot={__instance.SlotIndex}, shelf={owner.GUID}",
              Category.Storage);
#endif
          var cacheManager = GetOrCreateCacheService(owner.ParentProperty);
          if (__instance.ItemInstance != null)
            cacheManager.ClearZeroQuantityEntries(owner, new[] { __instance.ItemInstance });
          cacheManager.UpdateStorageCache(owner);
        }
      }
    }

    /// <summary>
    /// Finds items in storage that match the target item and required quantity.
    /// </summary>
    /// <param name="index">The index of the storage key to process.</param>
    /// <param name="inputs">The array of storage keys.</param>
    /// <param name="outputs">The list to store results.</param>
    /// <param name="targetItem">The target item to find.</param>
    /// <param name="needed">The required quantity.</param>
    /// <param name="allowTargetHigherQuality">Whether to allow higher quality items.</param>
    /// <param name="logs">The list to store log entries.</param>
    /// <param name="storageSlotsCache">The cache of storage slots.</param>
    [BurstCompile]
    public void FindItem(
        int index,
        NativeArray<StorageKey> inputs,
        NativeList<StorageResultBurst> outputs,
        ItemData targetItem,
        int needed,
        bool allowTargetHigherQuality,
        NativeList<LogEntry> logs,
        NativeParallelHashMap<StorageKey, NativeList<SlotData>> storageSlotsCache)
    {
      var storageKey = inputs[index];
      if (TryGetSlots(storageKey, out var slots))
      {
        int totalQty = 0;
        var slotData = new NativeList<SlotData>(1, Allocator.Persistent);
        for (int j = 0; j < slots.Length; j++)
        {
#if DEBUG
          Log(Level.Verbose, $"Checking slot {slots[j].SlotIndex} for item {targetItem.Id}", Category.Storage);
#endif
          var slot = slots[j];
          if (slot.IsValid && slot.Item.AdvCanStackWithBurst(targetItem, allowTargetHigherQuality) && slot.Quantity > 0)
          {
            totalQty += slot.Quantity;
            slotData.Add(slot);
          }
        }
        if (totalQty >= needed)
        {
          outputs.Add(new StorageResultBurst
          {
            ShelfGuid = storageKey.Guid,
            AvailableQuantity = totalQty,
            SlotData = slotData
          });
          logs.Add(new LogEntry
          {
            Message = $"Found shelf {storageKey.Guid} with {totalQty} of {targetItem.Id}",
            Level = Level.Info,
            Category = Category.Storage
          });
        }
        else
        {
          slotData.Dispose();
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
    [BurstCompile]
    public void FindItemResults(
        NativeList<StorageResultBurst> results,
        NativeParallelHashMap<ItemKey, (StorageKey, int, NativeList<SlotData>)> shelfSearchCache,
        NativeParallelHashMap<ItemKey, NativeList<StorageKey>> filteredStorageKeys,
        NativeParallelHashMap<StorageKey, int> storagePropertyMap,
        int propertyId,
        ItemData targetItem)
    {
      var cacheUpdates = new NativeList<(ItemKey, (StorageKey, int, NativeList<SlotData>))>(results.Length, Allocator.Temp);
      foreach (var result in results)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing result for shelf {result.ShelfGuid}", Category.Storage);
#endif
        if (storagePropertyMap.TryGetValue(new StorageKey(result.ShelfGuid, StorageType.SpecificShelf), out var propId) && propId == propertyId)
        {
          var itemKey = new ItemKey(targetItem);
          cacheUpdates.Add((itemKey, (new StorageKey(result.ShelfGuid, StorageType.SpecificShelf), result.AvailableQuantity, result.SlotData)));
          var filteredKeys = new NativeList<StorageKey>(1, Allocator.Persistent);
          filteredKeys.Add(new StorageKey(result.ShelfGuid, StorageType.SpecificShelf));
          filteredStorageKeys[itemKey] = filteredKeys;
          break;
        }
        result.SlotData.Dispose();
      }
      foreach (var update in cacheUpdates)
      {
#if DEBUG
        Log(Level.Verbose, $"Updating cache for item {update.Item1.ToString()}", Category.Storage);
#endif
        shelfSearchCache[update.Item1] = update.Item2;
      }
      cacheUpdates.Dispose();
    }

    /// <summary>
    /// Finds delivery destinations for an item.
    /// </summary>
    /// <param name="index">The index of the input to process.</param>
    /// <param name="inputs">The array of storage keys and station flags.</param>
    /// <param name="outputs">The list to store results.</param>
    /// <param name="quantity">The quantity to deliver.</param>
    /// <param name="sourceGuid">The GUID of the source entity.</param>
    /// <param name="logs">The list to store log entries.</param>
    /// <param name="storageSlotsCache">The cache of storage slots.</param>
    /// <param name="remainingQty">The remaining quantity to allocate.</param>
    [BurstCompile]
    public void FindDeliveryDestination(
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
#if DEBUG
          Log(Level.Verbose, $"Checking slot {slots[j].SlotIndex} for delivery destination", Category.Storage);
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

    /// <summary>
    /// Processes delivery destination results.
    /// </summary>
    /// <param name="destinations">The list of delivery destinations.</param>
    [BurstCompile]
    public void FindDeliveryDestinationResults(NativeList<DeliveryDestinationBurst> destinations)
    {
      foreach (var destination in destinations)
      {
#if DEBUG
        Log(Level.Verbose, $"Found destination {destination.Guid} with capacity {destination.Capacity}", Category.Storage);
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
    public void FindAvailableSlotsDelegate(
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
    [BurstCompile]
    public void ProcessSlotResults(NativeList<SlotResult> results, List<ItemSlot> slots)
    {
      foreach (var result in results)
      {
#if DEBUG
        Log(Level.Verbose, $"Processed slot {result.SlotIndex} with capacity {result.Capacity}", Category.Tasks);
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
    public void ProcessSlotOperationsDelegate(
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
    [BurstCompile]
    public void ProcessOperationResults(
        NativeList<SlotOperationResult> results,
        List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)> operations,
        List<SlotOperation> opList,
        List<NetworkObject> networkObjectCache)
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
        NativeArray<(StorageKey, NativeList<SlotData>)> inputs,
        NativeList<(StorageKey, NativeList<SlotData>)> outputs,
        NativeList<LogEntry> logs,
        NativeParallelHashMap<StorageKey, NativeList<SlotData>> storageSlotsCache,
        NativeParallelHashMap<ItemKey, NativeList<StorageKey>> itemToStorageCache,
        NativeList<StorageKey> anyShelfKeys)
    {
      var update = inputs[index];
      if (storageSlotsCache.ContainsKey(update.Item1))
      {
        var existing = storageSlotsCache[update.Item1];
        existing.Dispose();
        storageSlotsCache.Remove(update.Item1);
      }
      storageSlotsCache[update.Item1] = update.Item2;
      if (update.Item1.Type == StorageType.AnyShelf && !anyShelfKeys.Contains(update.Item1))
        anyShelfKeys.Add(update.Item1);
      foreach (var slot in update.Item2)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Processed slot update for {update.Item1.Guid}, slot {slot.SlotIndex}",
          Level = Level.Verbose,
          Category = Category.Storage
        });
#endif
      }
      outputs.Add(update);
    }

    /// <summary>
    /// Processes results of pending slot updates.
    /// </summary>
    /// <param name="results">The list of processed updates.</param>
    [BurstCompile]
    private static void ProcessPendingUpdateResults(NativeList<(StorageKey, NativeList<SlotData>)> results)
    {
      foreach (var result in results)
      {
#if DEBUG
        Log(Level.Verbose, $"Processed update result for {result.Item1.Guid}", Category.Storage);
#endif
        // No additional processing needed; results are already stored in caches
      }
    }
  }

  /// <summary>
  /// Manages slot operations and reservations in a networked environment.
  /// </summary>
  internal class SlotService
  {
    private static SlotService _instance;
    public static SlotService Instance => _instance ??= new SlotService();

    internal static readonly ObjectPool<List<SlotOperation>> _operationPool = new ObjectPool<List<SlotOperation>>(
        createFunc: () => new List<SlotOperation>(10),
        actionOnGet: null,
        actionOnRelease: list => list.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);

    internal static readonly List<NetworkObject> NetworkObjectCache = new();
    private static readonly Dictionary<SlotKey, SlotReservation> _reservations = new();

    private bool IsInitialized { get; set; }

    /// <summary>
    /// Initializes the SlotService, setting up necessary resources.
    /// </summary>
    public static void Initialize()
    {
      if (Instance.IsInitialized)
      {
        Log(Level.Warning, "SlotService already initialized", Category.Storage);
        return;
      }
      Instance.IsInitialized = true;
      Log(Level.Info, "SlotService initialized", Category.Storage);
    }

    /// <summary>
    /// Cleans up resources used by the SlotService.
    /// </summary>
    public static void Cleanup()
    {
      if (!Instance.IsInitialized)
      {
        Log(Level.Warning, "SlotService not initialized, skipping cleanup", Category.Storage);
        return;
      }
      _reservations.Clear();
      _operationPool.Clear();
      NetworkObjectCache.Clear();
      Instance.IsInitialized = false;
      Log(Level.Info, "SlotService cleaned up", Category.Storage);
    }

    /// <summary>
    /// Reserves a slot for an item with specified locking details.
    /// </summary>
    /// <param name="entityGuid">The GUID of the entity containing the slot.</param>
    /// <param name="slot">The slot to reserve.</param>
    /// <param name="locker">The network object locking the slot.</param>
    /// <param name="lockReason">The reason for locking the slot.</param>
    /// <param name="item">The item to reserve (optional).</param>
    /// <param name="quantity">The quantity to reserve (optional).</param>
    /// <returns>True if the slot was reserved successfully, false otherwise.</returns>
    internal static bool ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)
    {
      if (slot == null || locker == null)
      {
        Log(Level.Warning, $"ReserveSlot: Invalid slot or locker", Category.Tasks);
        return false;
      }
      var slotKey = new SlotKey(entityGuid, slot.SlotIndex);
      if (_reservations.ContainsKey(slotKey))
      {
#if DEBUG
        Log(Level.Verbose, $"ReserveSlot: Slot {slotKey} already reserved", Category.Tasks);
#endif
        return false;
      }
      slot.ApplyLock(locker, lockReason);
      _reservations.Add(slotKey, new SlotReservation
      {
        EntityGuid = entityGuid,
        Timestamp = Time.time,
        Locker = locker.ToString(),
        LockReason = lockReason,
        Item = new ItemData(item?.GetCopy()),
        Quantity = quantity
      });
#if DEBUG
      Log(Level.Verbose, $"Reserved slot {slotKey} for entity {entityGuid}, reason: {lockReason}", Category.Tasks);
#endif
      return true;
    }

    /// <summary>
    /// Releases a previously reserved slot.
    /// </summary>
    /// <param name="slot">The slot to release.</param>
    internal static void ReleaseSlot(ItemSlot slot)
    {
      if (slot == null) return;
      var slotKey = _reservations.FirstOrDefault(s => s.Key.SlotIndex == slot.SlotIndex).Key;
      if (slotKey.SlotIndex == slot.SlotIndex)
      {
        _reservations.Remove(slotKey);
        slot.RemoveLock();
#if DEBUG
        Log(Level.Verbose, $"Released slot {slotKey}", Category.Tasks);
#endif
      }
    }

    internal static class SlotProcessingUtility
    {
      /// <summary>
      /// Determines the capacity of a slot for a given item.
      /// </summary>
      /// <param name="slot">The slot data to check.</param>
      /// <param name="item">The item data to store.</param>
      /// <returns>The available capacity for the item in the slot.</returns>
      [BurstCompile]
      public static int GetCapacityForItem(SlotData slot, ItemData item)
      {
        if (!slot.IsValid || slot.IsLocked) return 0;
        if (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(item))
          return slot.StackLimit - slot.Quantity;
        return 0;
      }

      /// <summary>
      /// Checks if an item can be inserted into a slot.
      /// </summary>
      /// <param name="slot">The slot data to check.</param>
      /// <param name="item">The item data to insert.</param>
      /// <param name="quantity">The quantity to insert.</param>
      /// <returns>True if the item can be inserted, false otherwise.</returns>
      [BurstCompile]
      public static bool CanInsert(SlotData slot, ItemData item, int quantity)
      {
        if (!slot.IsValid || slot.IsLocked || quantity <= 0) return false;
        if (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(item))
          return slot.StackLimit >= slot.Quantity + quantity;
        return false;
      }

      /// <summary>
      /// Checks if an item can be removed from a slot.
      /// </summary>
      /// <param name="slot">The slot data to check.</param>
      /// <param name="item">The item data to remove.</param>
      /// <param name="quantity">The quantity to remove.</param>
      /// <returns>True if the item can be removed, false otherwise.</returns>
      [BurstCompile]
      public static bool CanRemove(SlotData slot, ItemData item, int quantity)
      {
        if (!slot.IsValid || slot.IsLocked || quantity <= 0) return false;
        return slot.Item.Id != "" && slot.Item.AdvCanStackWithBurst(item) && slot.Quantity >= quantity;
      }
    }

    [HarmonyPatch(typeof(ItemSlot))]
    internal class ItemSlotPatch
    {
#if DEBUG
      [HarmonyPostfix]
      [HarmonyPatch("ApplyLock", new Type[] { typeof(NetworkObject), typeof(string), typeof(bool) })]
      static void ApplyLockPostfix(ItemSlot __instance, NetworkObject lockOwner, string lockReason, bool _internal)
      {
        if (!__instance.IsLocked)
        {
          Log(Level.Warning, $"ApplyLock: Failed to lock slot={__instance.SlotIndex}, owner={lockOwner}, reason={lockReason}, internal={_internal}", Category.Storage);
        }
        else
        {
          Log(Level.Verbose, $"ApplyLock: Locked slot={__instance.SlotIndex}, owner={lockOwner}, reason={lockReason}, internal={_internal}", Category.Storage);
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch("RemoveLock", new Type[] { typeof(bool) })]
      static void RemoveLockPostfix(ItemSlot __instance, bool _internal)
      {
        Log(Level.Verbose, $"RemoveLock: Unlocked slot {__instance.SlotIndex}, internal={_internal}", Category.Storage);
      }

      [HarmonyPostfix]
      [HarmonyPatch("get_IsLocked")]
      static void IsLockedPostfix(ItemSlot __instance, ref bool __result)
      {
        Log(Level.Verbose, $"IsLocked: Slot {__instance.SlotIndex} is {(__result ? "locked" : "unlocked")}", Category.Storage);
      }
#endif

      [HarmonyPrefix]
      [HarmonyPatch("ApplyLock", new Type[] { typeof(NetworkObject), typeof(string), typeof(bool) })]
      static bool ApplyLockPrefix(ItemSlot __instance, NetworkObject lockOwner, string lockReason, bool _internal, ref ItemSlotLock ___ActiveLock)
      {
        if (_internal || __instance.SlotOwner == null)
        {
#if DEBUG
          Log(Level.Verbose, $"ApplyLock: Internal slot={__instance.SlotIndex}, owner={lockOwner}, reason={lockReason}", Category.Storage);
#endif
          ___ActiveLock = new ItemSlotLock(__instance, lockOwner, lockReason);
          __instance.onLocked?.Invoke();
        }
        else
        {
#if DEBUG
          Log(Level.Verbose, $"ApplyLock: Initial slot={__instance.SlotIndex}, owner={lockOwner}, reason={lockReason}", Category.Storage);
#endif
          ___ActiveLock = new ItemSlotLock(__instance, lockOwner, lockReason);
          __instance.onLocked?.Invoke();
          __instance.SlotOwner.SetSlotLocked(null, __instance.SlotIndex, true, lockOwner, lockReason);
        }
        return false;
      }
    }

#if DEBUG
    [HarmonyPatch(typeof(ItemSlotUI))]
    internal class ItemSlotUIPatch
    {
      [HarmonyPostfix]
      [HarmonyPatch("AssignSlot")]
      static void AssignSlotPostfix(ItemSlotUI __instance, ItemSlot s)
      {
        Log(Level.Verbose, $"AssignSlot: UI assigned to slot {s?.SlotIndex ?? -1}, locked={s?.IsLocked ?? false}", Category.Storage);
      }
    }
#endif
  }
}