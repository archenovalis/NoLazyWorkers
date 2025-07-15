using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using ScheduleOne.Product;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using Unity.Collections;
using FishNet;
using static NoLazyWorkers.Storage.ShelfExtensions;
using NoLazyWorkers.Performance;
using Unity.Burst;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using ScheduleOne.Delivery;
using static NoLazyWorkers.Storage.ManagedDictionaries;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using UnityEngine.Pool;
using NoLazyWorkers.Extensions;
using FishNet.Object;

namespace NoLazyWorkers.Storage
{
  public static class Extensions
  {
    [BurstCompile]
    public struct SlotUpdate
    {
      public Guid OwnerGuid;
      public SlotData SlotData;
    }

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

    /// <summary>
    /// Defines types of storage entities.
    /// </summary>
    public enum StorageType
    {
      None,
      AnyShelf,
      SpecificShelf,
      Employee,
      Station,
      LoadingDock
    }

    public readonly struct ItemKey : IEquatable<ItemKey>
    {
      public readonly FixedString32Bytes ItemId;
      public readonly FixedString32Bytes PackagingId;
      public readonly EQualityBurst Quality;

      public ItemKey(ItemInstance item)
      {
        if (item == null)
        {
          ItemId = "";
          PackagingId = "";
          Quality = EQualityBurst.None;
        }
        else
        {
          var itemKey = new ItemData(item);
          ItemId = itemKey.Id;
          PackagingId = itemKey.PackagingId;
          Quality = itemKey.Quality;
        }
      }

      public ItemKey(ItemData itemKey)
      {
        ItemId = itemKey.Id;
        PackagingId = itemKey.PackagingId;
        Quality = itemKey.Quality;
      }

      public new string ToString()
      {
        return ItemId.ToString() +
               (PackagingId.ToString() != "" ? " Packaging:" + PackagingId.ToString() : "") +
               (Quality != EQualityBurst.None ? " Quality:" + Quality.ToString() : "");
      }

      public bool Equals(ItemKey other) => ItemId == other.ItemId && PackagingId == other.PackagingId && Quality == other.Quality;
      public override bool Equals(object obj) => obj is ItemKey other && Equals(other);
      public override int GetHashCode() => HashCode.Combine(ItemId, PackagingId, Quality);
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

    [BurstCompile]
    public struct StorageData : IEquatable<StorageData>, IDisposable
    {
      public readonly Guid Guid;
      public readonly int PropertyId;
      public NativeList<SlotData> OutputSlots;
      public NativeParallelMultiHashMap<ItemKey, int> ItemToSlots;
      public readonly StorageType StorageType;

      public StorageData(PlaceableStorageEntity shelf)
      {
        Guid = shelf.GUID;
        PropertyId = shelf.ParentProperty.NetworkObject.ObjectId;
        OutputSlots = new NativeList<SlotData>(Allocator.Persistent);
        ItemToSlots = new NativeParallelMultiHashMap<ItemKey, int>(shelf.StorageEntity.SlotCount, Allocator.Persistent);
        StorageType = StorageConfigs.TryGetValue(shelf.ParentProperty, out var configs) &&
                      configs.TryGetValue(shelf.GUID, out var config) && config.Mode == StorageMode.Specific
            ? StorageType.SpecificShelf
            : StorageType.AnyShelf;
        foreach (var slot in shelf.OutputSlots)
        {
          OutputSlots.Add(new SlotData(shelf.GUID, slot, StorageType));
          if (slot.ItemInstance != null)
            ItemToSlots.Add(new ItemKey(slot.ItemInstance), slot.SlotIndex);
        }
      }

      public PlaceableStorageEntity ToPlaceableStorageEntity()
      {
        return Storages[IdToProperty[PropertyId]][Guid];
      }

      public void Dispose()
      {
        if (OutputSlots.IsCreated) OutputSlots.Dispose();
        if (ItemToSlots.IsCreated) ItemToSlots.Dispose();
      }

      public bool Equals(StorageData other) => Guid == other.Guid;
      public override bool Equals(object obj) => obj is StorageData other && Equals(other);
      public override int GetHashCode() => Guid.GetHashCode();
    }

    [BurstCompile]
    public struct LoadingDockData : IEquatable<LoadingDockData>, IDisposable
    {
      public readonly Guid Guid;
      public readonly int PropertyId;
      public Guid OccupantGuid;
      public NativeList<SlotData> OutputSlots;
      public NativeParallelMultiHashMap<ItemKey, int> ItemToSlots;
      public readonly StorageType StorageType;

      public LoadingDockData(LoadingDock dock)
      {
        Guid = dock.GUID;
        PropertyId = dock.ParentProperty.NetworkObject.ObjectId;
        OccupantGuid = dock.DynamicOccupant != null ? dock.DynamicOccupant.GUID : Guid.Empty;
        OutputSlots = new NativeList<SlotData>(Allocator.Persistent);
        ItemToSlots = new NativeParallelMultiHashMap<ItemKey, int>(8, Allocator.Persistent);
        StorageType = StorageType.LoadingDock;

        if (dock.DynamicOccupant != null)
        {
          foreach (var slot in dock.OutputSlots)
          {
            OutputSlots.Add(new SlotData(dock.GUID, slot, StorageType.LoadingDock));
            if (slot.ItemInstance != null)
              ItemToSlots.Add(new ItemKey(slot.ItemInstance), slot.SlotIndex);
          }
        }
      }

      public void Dispose()
      {
        if (OutputSlots.IsCreated) OutputSlots.Dispose();
        if (ItemToSlots.IsCreated) ItemToSlots.Dispose();
      }

      public bool Equals(LoadingDockData other) => Guid == other.Guid;
      public override bool Equals(object obj) => obj is LoadingDockData other && Equals(other);
      public override int GetHashCode() => Guid.GetHashCode();
    }

    [BurstCompile]
    public struct StationData : IEquatable<StationData>, IDisposable
    {
      public readonly Guid Guid;
      public readonly int PropertyId;
      public readonly EntityType EntityType;
      public NativeList<SlotData> InsertSlots;
      public NativeList<SlotData> ProductSlots;
      public SlotData OutputSlot;
      public NativeParallelMultiHashMap<ItemKey, int> ItemToSlots;
      public bool IsInUse;
      public NativeList<ItemKey> RefillList;
      public int State;

      public StationData(IStationAdapter station)
      {
        Guid = station.GUID;
        PropertyId = station.ParentProperty.NetworkObject.ObjectId;
        InsertSlots = new NativeList<SlotData>(Allocator.Persistent);
        ProductSlots = new NativeList<SlotData>(Allocator.Persistent);
        ItemToSlots = new NativeParallelMultiHashMap<ItemKey, int>(10, Allocator.Persistent);
        RefillList = new NativeList<ItemKey>(Allocator.Persistent);
        var guid = station.GUID;
        foreach (var slot in station.InsertSlots)
        {
          InsertSlots.Add(new SlotData(guid, slot, StorageType.Station));
          if (slot.ItemInstance != null)
            ItemToSlots.Add(new ItemKey(slot.ItemInstance), slot.SlotIndex);
        }
        foreach (var slot in station.ProductSlots)
        {
          ProductSlots.Add(new SlotData(guid, slot, StorageType.Station));
          if (slot.ItemInstance != null)
            ItemToSlots.Add(new ItemKey(slot.ItemInstance), slot.SlotIndex);
        }
        OutputSlot = new SlotData(guid, station.OutputSlot, StorageType.Station);
        if (station.OutputSlot.ItemInstance != null)
          ItemToSlots.Add(new ItemKey(station.OutputSlot.ItemInstance), station.OutputSlot.SlotIndex);
        EntityType = station.EntityType;
        IsInUse = station.IsInUse;
        foreach (var item in station.RefillList())
          RefillList.Add(new ItemKey(item));
      }

      public IStationAdapter ToStationAdapter()
      {
        return IStations[IdToProperty[PropertyId]][Guid];
      }

      public void Dispose()
      {
        if (InsertSlots.IsCreated) InsertSlots.Dispose();
        if (ProductSlots.IsCreated) ProductSlots.Dispose();
        if (ItemToSlots.IsCreated) ItemToSlots.Dispose();
        if (RefillList.IsCreated) RefillList.Dispose();
      }

      public bool Equals(StationData other) => Guid == other.Guid;
      public override bool Equals(object obj) => obj is StationData other && Equals(other);
      public override int GetHashCode() => Guid.GetHashCode();
    }

    [BurstCompile]
    public struct EmployeeData : IEquatable<EmployeeData>, IDisposable
    {
      public readonly Guid Guid;
      public readonly int PropertyId;
      public NativeList<SlotData> InventorySlots;
      public NativeParallelMultiHashMap<ItemKey, int> ItemToSlots;
      public readonly EntityType EntityType;

      public EmployeeData(IEmployeeAdapter employee)
      {
        Guid = employee.Guid;
        PropertyId = employee.AssignedProperty.NetworkObject.ObjectId;
        InventorySlots = new NativeList<SlotData>(Allocator.Persistent);
        ItemToSlots = new NativeParallelMultiHashMap<ItemKey, int>(10, Allocator.Persistent);
        EntityType = employee.EntityType;
        foreach (var slot in employee.InventorySlots)
        {
          InventorySlots.Add(new SlotData(employee.Guid, slot, StorageType.Employee));
          if (slot.ItemInstance != null)
            ItemToSlots.Add(new ItemKey(slot.ItemInstance), slot.SlotIndex);
        }
      }

      public IEmployeeAdapter ToEmployeeAdapter()
      {
        return IEmployees[IdToProperty[PropertyId]][Guid];
      }

      public void Dispose()
      {
        if (InventorySlots.IsCreated) InventorySlots.Dispose();
        if (ItemToSlots.IsCreated) ItemToSlots.Dispose();
      }

      public bool Equals(EmployeeData other) => Guid == other.Guid;
      public override bool Equals(object obj) => obj is EmployeeData other && Equals(other);
      public override int GetHashCode() => Guid.GetHashCode();
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
        PropertyId = slot.GetProperty()?.NetworkObject.ObjectId ?? 0;
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

    /// <summary>
    /// Represents a key for identifying a slot within an entity.
    /// </summary>
    public struct SlotKey : IEquatable<SlotKey>
    {
      public Guid EntityGuid;
      public int SlotIndex;

      /// <summary>
      /// Initializes a SlotKey with an entity GUID and slot index.
      /// </summary>
      /// <param name="entityGuid">The GUID of the entity.</param>
      /// <param name="slotIndex">The index of the slot.</param>
      public SlotKey(Guid entityGuid, int slotIndex)
      {
        EntityGuid = entityGuid;
        SlotIndex = slotIndex;
      }

      /// <summary>
      /// Checks equality with another SlotKey.
      /// </summary>
      /// <param name="other">The SlotKey to compare with.</param>
      /// <returns>True if equal, false otherwise.</returns>
      public bool Equals(SlotKey other) => EntityGuid == other.EntityGuid && SlotIndex == other.SlotIndex;
      public override bool Equals(object obj) => obj is SlotKey other && Equals(other);
      /// <summary>
      /// Gets the hash code for the SlotKey.
      /// </summary>
      /// <returns>The hash code.</returns>
      public override int GetHashCode() => HashCode.Combine(EntityGuid, SlotIndex);

      /// <summary>
      /// Retrieves the ItemSlot associated with this SlotKey.
      /// </summary>
      /// <param name="property">The property containing the slot.</param>
      /// <returns>The ItemSlot associated with the key.</returns>
      /// <exception cref="KeyNotFoundException">Thrown if the slot is not found.</exception>
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
              if (Storages.TryGetValue(property, out var storages) &&
                  storages.TryGetValue(EntityGuid, out var shelf))
                return shelf.OutputSlots[SlotIndex];
              break;
            case StorageType.Station:
              if (IStations.TryGetValue(property, out var stations) &&
                  stations.TryGetValue(EntityGuid, out var station))
                return station.InsertSlots.Concat(station.ProductSlots).Concat(new[] { station.OutputSlot })
                  .FirstOrDefault(s => s.SlotIndex == _this.SlotIndex);
              break;
            case StorageType.LoadingDock:
              if (LoadingDocks.TryGetValue(property, out var docks) &&
                  docks.TryGetValue(EntityGuid, out var dock))
                return dock.OutputSlots[SlotIndex];
              break;
            case StorageType.Employee:
              if (IEmployees.TryGetValue(property, out var employees) &&
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

    internal static readonly List<Action<ItemSlot, ItemInstance, int>> _slotListeners = new();
    internal static readonly List<Action<PlaceableStorageEntity, StorageType>> _shelfListeners = new();

    /// <summary>
    /// Initializes the SlotService, setting up necessary resources.
    /// </summary>
    public static void Initialize()
    {
      if (IsInitialized)
      {
        Log(Level.Warning, "StorageManager already initialized", Category.Storage);
        return;
      }
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Warning, "StorageManager initialization skipped: not server", Category.Storage);
        return;
      }
      SlotService.Initialize();
      IsInitialized = true;
      Log(Level.Info, "StorageManager initialized", Category.Storage);
    }

    /// <summary>
    /// Cleans up resources used by the SlotService.
    /// </summary>
    public static void Cleanup()
    {
      if (!IsInitialized)
      {
        Log(Level.Warning, "StorageManager not initialized, skipping cleanup", Category.Storage);
        return;
      }
      if (!FishNetExtensions.IsServer)
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
    /// Finds storage containing the specified item with the required quantity.
    /// </summary>
    /// <param name="property">The property to search in.</param>
    /// <param name="item">The item to find.</param>
    /// <param name="needed">The quantity needed.</param>
    /// <param name="allowTargetHigherQuality">Whether to allow higher quality items.</param>
    /// <returns>An enumerator yielding the storage result.</returns>
    public static IEnumerator FindStorageWithItem(Property property, ItemInstance item, int needed, bool allowTargetHigherQuality = false)
    {
      if (!IsInitialized)
      {
        Log(Level.Error, "StorageManager not initialized, cannot find storage", Category.Storage);
        yield return new StorageResult();
        yield break;
      }
      if (!FishNetExtensions.IsServer)
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
        Log(Level.Info, $"Item {itemKey.ToString()} not found in cache", Category.Storage);
        yield return new StorageResult();
        yield break;
      }

      var storageKeys = new NativeList<Guid>(Allocator.TempJob);
      var results = new NativeList<StorageResultBurst>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      try
      {
        storageKeys.AddRange(cacheService._entityGuids.AsArray());

        yield return SmartExecution.ExecuteBurstFor<Guid, StorageResultBurst>(
            uniqueId: nameof(FindStorageWithItem),
            itemCount: storageKeys.Length,
            burstForDelegate: (i, inputs, outputs, logs) => cacheService.FindItem(i, inputs, outputs, itemData, needed, allowTargetHigherQuality, logs, cacheService._slotsCache, cacheService._itemToSlots, cacheService._guidToType)
        );

        if (results.Length > 0 && results[0].SlotData.Length > 0)
        {
          var result = results[0].GetResult();
          Log(Level.Info, $"Found storage for item {itemKey.ToString()}, qty={result.AvailableQuantity}, shelf={result.Shelf.GUID}", Category.Storage);
          yield return result;
        }
        else
        {
          cacheService.AddItemNotFound(itemKey);
          Log(Level.Info, $"No storage found for item {itemKey.ToString()}, added to not found cache", Category.Storage);
          yield return new StorageResult();
        }

        yield return ProcessLogs(logs);
      }
      finally
      {
        if (storageKeys.IsCreated) storageKeys.Dispose();
        if (results.IsCreated)
        {
          foreach (var result in results)
            result.SlotData.Dispose();
          results.Dispose();
        }
        if (logs.IsCreated) logs.Dispose();
        cacheService._storageResultPool.Release(new List<StorageResultBurst>());
      }
    }

    /// <summary>
    /// Updates the storage cache for a specific entity and its slots.
    /// </summary>
    /// <param name="property">The property to update the cache for.</param>
    /// <param name="entityGuid">The GUID of the entity.</param>
    /// <param name="slots">The list of slots to update.</param>
    /// <param name="storageType">The type of storage.</param>
    /// <returns>An enumerator for asynchronous cache update.</returns>
    public static IEnumerator UpdateStorageCache(Property property, Guid entityGuid, List<ItemSlot> slots, StorageType storageType)
    {
      if (!IsInitialized || !InstanceFinder.NetworkManager.IsServer || property == null || slots == null || slots.Count == 0)
      {
        Log(Level.Warning, $"UpdateStorageCache: Invalid input (init={IsInitialized}, server={InstanceFinder.NetworkManager.IsServer}, property={property != null}, slots={slots?.Count ?? 0})", Category.Storage);
        yield break;
      }

      var cacheService = CacheService.GetOrCreateCacheService(property);

      var slotUpdates = new NativeList<SlotUpdate>(slots.Count, Allocator.TempJob);
      var logs = new NativeList<LogEntry>(slots.Count, Allocator.TempJob);
      try
      {
        foreach (var slot in slots)
        {
          var slotKey = slot.GetSlotKey();
          if (slotKey.EntityGuid != entityGuid) continue;
          cacheService._slotCache[slotKey] = (property.NetworkObject.ObjectId, storageType, slot.SlotIndex);
          slotUpdates.Add(new SlotUpdate
          {
            OwnerGuid = entityGuid,
            SlotData = new SlotData(entityGuid, slot, storageType)
          });
#if DEBUG
          Log(Level.Verbose, $"Queued update for slot {slot.SlotIndex} on {entityGuid}, item={slot.ItemInstance?.ID ?? "None"}", Category.Storage);
#endif
        }

        if (slotUpdates.Length > 0)
        {
          cacheService._pendingSlotUpdates.AddRange(slotUpdates.AsArray());
        }

        yield return ProcessLogs(logs);
      }
      finally
      {
        if (slotUpdates.IsCreated) slotUpdates.Dispose();
        if (logs.IsCreated) logs.Dispose();
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
      var inputs = new NativeList<(Guid, bool)>(Allocator.TempJob);
      var stationInputs = new NativeList<(Guid, NativeList<ItemKey>)>(Allocator.TempJob);
      var logs = new NativeList<LogEntry>(Allocator.TempJob);

      try
      {
        // Prioritize packaging stations
        if (IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values)
          {
#if DEBUG
            Log(Level.Verbose, $"Checking station {station.GUID} for delivery", Category.Storage);
#endif
            if (station.EntityType == EntityType.PackagingStation && !station.IsInUse && station.CanRefill(item))
            {
              var refillList = new NativeList<ItemKey>(Allocator.TempJob);
              inputs.Add((station.GUID, true));
              foreach (var itemInstance in station.RefillList())
                refillList.Add(new ItemKey(itemInstance));
              stationInputs.Add((station.GUID, refillList));
            }
          }
        }

        // Then specific shelves
        var specificShelfGuids = cacheService._guidToType.GetKeyArray(Allocator.Temp)
            .Where(g => cacheService._guidToType[g].StorageType == StorageType.SpecificShelf)
            .ToArray();
        foreach (var guid in specificShelfGuids)
        {
#if DEBUG
          Log(Level.Verbose, $"Adding specific shelf {guid} for item {itemKey.ToString()}", Category.Storage);
#endif
          inputs.Add((guid, false));
        }

        // Then any shelves
        var anyShelfGuids = cacheService._guidToType.GetKeyArray(Allocator.Temp)
            .Where(g => cacheService._guidToType[g].StorageType == StorageType.AnyShelf)
            .ToArray();
        foreach (var guid in anyShelfGuids)
        {
#if DEBUG
          Log(Level.Verbose, $"Adding any shelf {guid}", Category.Storage);
#endif
          inputs.Add((guid, false));
        }

        if (cacheService._noDropOffCache.Contains(itemKey))
        {
#if DEBUG
          Log(Level.Verbose, $"Item {itemKey.ToString()} found in no drop-off cache, skipping delivery", Category.Storage);
#endif
          yield return new List<DeliveryDestinationBurst>();
          yield break;
        }

        yield return SmartExecution.ExecuteBurstFor<(Guid, NativeList<ItemKey>), Empty>(
            uniqueId: nameof(FindDeliveryDestination) + "_RefillList",
            itemCount: stationInputs.Length,
            burstForDelegate: (index, inputs, outputs, logs) => cacheService.UpdateStationRefillListBurst(index, inputs, itemKey, logs)
        );

        int remainingQty = quantity;
        yield return SmartExecution.ExecuteBurstFor<(Guid, bool), DeliveryDestinationBurst>(
            uniqueId: nameof(FindDeliveryDestination),
            itemCount: inputs.Length,
            burstForDelegate: (i, inputs, outputs, logs) => cacheService.FindDeliveryDestinationBurst(i, inputs, outputs, quantity, sourceGuid, logs, cacheService._slotsCache, cacheService._guidToType, ref remainingQty),
            burstResultsDelegate: cacheService.FindDeliveryDestinationResults,
            inputs: inputs.AsArray(),
            outputs: destinations
        );

        if (destinations.Length == 0)
        {
          cacheService.AddNoDropOffCache(itemKey, logs);
#if DEBUG
          Log(Level.Verbose, $"No destinations found for item {itemKey.ToString()}, added to no drop-off cache", Category.Storage);
#endif
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

    /// <summary>
    /// Finds available slots for storing an item with the specified quantity.
    /// </summary>
    /// <param name="property">The property to search within.</param>
    /// <param name="entityGuid">The GUID of the entity containing the slots.</param>
    /// <param name="slots">The list of slots to check.</param>
    /// <param name="item">The item to store.</param>
    /// <param name="quantity">The quantity to store.</param>
    /// <returns>An enumerator yielding a list of tuples containing available slots and their capacities.</returns>
    public static IEnumerator FindAvailableSlots(Property property, Guid entityGuid, List<ItemSlot> slots, ItemInstance item, int quantity)
    {
      if (!IsInitialized || !FishNetExtensions.IsServer || slots == null || item == null || quantity <= 0)
      {
        Log(Level.Error, $"FindAvailableSlots: Invalid input or state (init={IsInitialized}, server={FishNetExtensions.IsServer}, slots={slots?.Count ?? 0}, item={item != null}, qty={quantity})", Category.Tasks);
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
            burstForDelegate: (index, inputs, outputs, logs) => cacheService.FindAvailableSlotsBurst(index, inputs, outputs, new ItemData(item), quantity, logs),
            burstResultsDelegate: (results, logs) => cacheService.FindAvailableSlotsBurstResults(results, slots, logs),
            inputs: slotData,
            outputs: results
        );
        List<(ItemSlot, int)> managedResult = [.. results.Select(r => (slots[r.SlotIndex], r.Capacity))];
        yield return managedResult;
        yield return ProcessLogs(logs);
      }
      finally
      {
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
      if (!IsInitialized || !FishNetExtensions.IsServer || operations == null || operations.Count == 0)
      {
        Log(Level.Error, $"ExecuteSlotOperations: Invalid input or state (init={IsInitialized}, server={FishNetExtensions.IsServer}, ops={operations?.Count ?? 0})", Category.Tasks);
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
            burstForDelegate: (index, inputs, outputs, logs) => cacheService.ProcessSlotOperationsBurst(index, inputs, outputs, logs),
            burstResultsDelegate: (results, logs) => cacheService.ProcessOperationResults(results, operations, opList, SlotService.NetworkObjectCache, logs),
            inputs: opData,
            outputs: results
        );
        List<bool> managedResult = [.. results.Select(r => r.IsValid)];
        yield return managedResult;
        yield return ProcessLogs(logs);
        SlotService._operationPool.Release(opList);
      }
      finally
      {
        opData.Dispose();
        results.Dispose();
        logs.Dispose();
        SlotService.NetworkObjectCache.Clear();
      }
    }

    #endregion
  }
}