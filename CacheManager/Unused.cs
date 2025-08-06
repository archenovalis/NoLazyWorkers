
using ScheduleOne.ItemFramework;
using UnityEngine;
using static NoLazyWorkers.CacheManager.Extensions;
using Unity.Collections;
using NoLazyWorkers.SmartExecution;
using Unity.Burst;
using static NoLazyWorkers.TaskService.Extensions;
using Unity.Collections.LowLevel.Unsafe;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.Extensions;
using FishNet.Object;
using NoLazyWorkers.Storage;

namespace NoLazyWorkers.CacheManagerUnused
{
  public class Unused
  {
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
    public struct UpdateStationRefillListBurstFor //TO DO: this doesn't seem to work correctly. we should be updating the station's stationdata.
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

    /* public partial class CacheService
    {
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
    } */
  }
}