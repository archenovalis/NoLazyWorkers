using FishNet.Object;
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
using static NoLazyWorkers.Employees.Constants;
using ScheduleOne.Employees;
using System.Collections.Concurrent;
using Unity.Collections;
using FishNet;
using System.Diagnostics;
using static NoLazyWorkers.Storage.ShelfExtensions;
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using static NoLazyWorkers.TimeManagerExtensions;
using UnityEngine.Pool;
using NoLazyWorkers.Metrics;
using Unity.Burst;
using Unity.Jobs;
using static NoLazyWorkers.NoLazyUtilities;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.TaskExtensions;
using ScheduleOne.Delivery;
using HarmonyLib;
using ScheduleOne.Vehicles;
using ScheduleOne.EntityFramework;
using static NoLazyWorkers.Storage.ShelfConstants;
using static NoLazyWorkers.Storage.Constants;
using static NoLazyWorkers.Employees.Extensions;
using Unity.Collections.LowLevel.Unsafe;
using NoLazyWorkers.JobService;
using static NoLazyWorkers.Storage.Jobs;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;

namespace NoLazyWorkers.Storage
{
  public static class Jobs
  {
    /// <summary>
    /// Burst-compatible job to find storage with an item.
    /// </summary>
    [BurstCompile]
    public struct FindItemJob : IJob
    {
      [ReadOnly] public NativeList<StorageKey> StorageKeys;
      [ReadOnly] public ItemKey TargetItem;
      public int Needed;
      public bool AllowHigherQuality;
      public NativeList<StorageResult> Results;
      public NativeList<LogEntry> Logs;
      [ReadOnly] public NativeParallelHashMap<StorageKey, NativeList<SlotData>> SpecificShelfSlotsCache;
      [ReadOnly] public NativeParallelHashMap<StorageKey, NativeList<SlotData>> AnyShelfSlotsCache;

      public void Execute()
      {
        for (int i = 0; i < StorageKeys.Length; i++)
        {
          var storageKey = StorageKeys[i];
          NativeList<SlotData> slots;
          if (storageKey.Type == StorageTypes.SpecificShelf && SpecificShelfSlotsCache.TryGetValue(storageKey, out slots))
          {
            ProcessSlots(storageKey, slots, ref Results, ref Logs);
          }
          else if (storageKey.Type == StorageTypes.AnyShelf && AnyShelfSlotsCache.TryGetValue(storageKey, out slots))
          {
            ProcessSlots(storageKey, slots, ref Results, ref Logs);
          }
        }
      }

      private void ProcessSlots(StorageKey storageKey, NativeList<SlotData> slots, ref NativeList<StorageResult> results, ref NativeList<LogEntry> logs)
      {
        int totalQty = 0;
        var slotIndices = new NativeList<int>(slots.Length, Allocator.Temp);
        for (int j = 0; j < slots.Length; j++)
        {
          var slot = slots[j];
          if (slot.IsValid && slot.Item.AdvCanStackWithBurst(TargetItem, AllowHigherQuality) && slot.Quantity > 0)
          {
            totalQty += slot.Quantity;
            slotIndices.Add(slot.SlotIndex);
          }
        }
        if (totalQty >= Needed)
        {
          results.Add(new StorageResult
          {
            ShelfGuid = storageKey.Guid,
            AvailableQuantity = totalQty,
            SlotIndices = slotIndices
          });
          logs.Add(new LogEntry { Message = $"Found shelf {storageKey.Guid} with {totalQty} of {TargetItem.Id}", Level = Level.Info, Category = Category.Storage });
        }
        else
        {
          slotIndices.Dispose();
        }
      }
    }

    /// <summary>
    /// Burst-compatible job to find delivery destinations.
    /// </summary>
    [BurstCompile]
    public struct FindDeliveryDestinationsJob : IJob
    {
      [ReadOnly] public NativeList<StorageKey> StorageKeys;
      [ReadOnly] public ItemKey Item;
      public int Quantity;
      public int PropertyId;
      public Guid SourceGuid;
      public NativeList<DeliveryDestination> Destinations;
      public NativeList<LogEntry> Logs;
      [ReadOnly] public NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationSlotsCache;
      [ReadOnly] public NativeParallelHashMap<StorageKey, NativeList<SlotData>> SpecificShelfSlotsCache;
      [ReadOnly] public NativeParallelHashMap<StorageKey, NativeList<SlotData>> AnyShelfSlotsCache;

      public void Execute()
      {
        int remainingQty = Quantity;
        foreach (var stationKey in StationSlotsCache.GetKeyArray(Allocator.TempJob))
        {
          var station = IStations[CacheManager.GetPropertyById(PropertyId)][stationKey.Guid];
          if (station.TypeOf != typeof(PackagingStation) || station.IsInUse || !station.CanRefill(Item.CreateItemInstance()))
            continue;
          var storageKey = new StorageKey(station.GUID, StorageTypes.Station);
          if (StationSlotsCache.TryGetValue(storageKey, out var slots))
          {
            ProcessSlots(storageKey, slots, ref remainingQty, ref Destinations, ref Logs);
            if (remainingQty <= 0) return;
          }
        }
        for (int i = 0; i < StorageKeys.Length; i++)
        {
          var storageKey = StorageKeys[i];
          if (storageKey.Guid == SourceGuid) continue;
          NativeList<SlotData> slots;
          if (storageKey.Type == StorageTypes.SpecificShelf && SpecificShelfSlotsCache.TryGetValue(storageKey, out slots))
          {
            ProcessSlots(storageKey, slots, ref remainingQty, ref Destinations, ref Logs);
          }
          else if (storageKey.Type == StorageTypes.AnyShelf && AnyShelfSlotsCache.TryGetValue(storageKey, out slots))
          {
            ProcessSlots(storageKey, slots, ref remainingQty, ref Destinations, ref Logs);
          }
          if (remainingQty <= 0) break;
        }
      }

      private void ProcessSlots(StorageKey storageKey, NativeList<SlotData> slots, ref int remainingQty, ref NativeList<DeliveryDestination> destinations, ref NativeList<LogEntry> logs)
      {
        var inputSlots = new NativeList<int>(slots.Length, Allocator.Temp);
        int capacity = 0;
        for (int i = 0; i < slots.Length; i++)
        {
          var slot = slots[i];
          if (slot.IsValid && slot.StackLimit > slot.Quantity)
          {
            int slotCapacity = slot.StackLimit - slot.Quantity;
            if (slotCapacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(Item)))
            {
              inputSlots.Add(slot.SlotIndex);
              capacity += slotCapacity;
            }
          }
        }
        if (capacity > 0)
        {
          var slotsPersistent = new NativeList<int>(inputSlots.Length, Allocator.Persistent);
          slotsPersistent.AddRange(inputSlots);
          destinations.Add(new DeliveryDestination
          {
            DestinationGuid = storageKey.Guid,
            SlotIndices = slotsPersistent,
            Capacity = Mathf.Min(capacity, remainingQty)
          });
          remainingQty -= capacity;
          logs.Add(new LogEntry { Message = $"Found destination {storageKey.Guid} with capacity {capacity} for {Item.Id}", Level = Level.Info, Category = Category.Storage });
        }
        inputSlots.Dispose();
      }
    }

    /// <summary>
    /// Burst-compatible job to validate and reserve input slots for an item.
    /// </summary>
    [BurstCompile]
    public struct ReserveInputSlotsJob : IJobParallelFor
    {
      [ReadOnly] public NativeArray<SlotData> Slots;
      [ReadOnly] public ItemKey Item;
      public NativeList<int> ReservedIndices;
      public NativeList<LogEntry> Logs;
      public bool AllowHigherQuality;
      public int RemainingQuantity;

      public void Execute(int index)
      {
        var slot = Slots[index];
        if (!slot.IsValid || slot.IsLocked)
        {
          Logs.Add(new LogEntry
          {
            Message = $"Slot {slot.SlotIndex} invalid or locked",
            Level = Level.Verbose,
            Category = Category.Storage
          });
          return;
        }

        int capacity = 0;
        if (slot.Item.Id != "" && slot.Item.AdvCanStackWithBurst(Item, AllowHigherQuality))
        {
          capacity = slot.StackLimit - slot.Quantity;
        }
        else if (slot.Item.Id == "")
        {
          capacity = Item.CreateItemInstance().StackLimit;
        }

        if (capacity <= 0)
        {
          Logs.Add(new LogEntry
          {
            Message = $"Slot {slot.SlotIndex} full or incompatible",
            Level = Level.Verbose,
            Category = Category.Storage
          });
          return;
        }

        int amountToReserve = Mathf.Min(capacity, RemainingQuantity);
        if (amountToReserve <= 0) return;

        ReservedIndices.Add(index);
        Logs.Add(new LogEntry
        {
          Message = $"Marked slot {slot.SlotIndex} for reservation (capacity={capacity}, amount={amountToReserve})",
          Level = Level.Verbose,
          Category = Category.Storage
        });
      }
    }

    [BurstCompile]
    public struct StackCheckJob : IJobParallelFor
    {
      [ReadOnly] public NativeArray<ItemKey> Items;
      [ReadOnly] public ItemKey Target;
      public NativeArray<bool> Results;
      public bool AllowHigherQuality;

      public void Execute(int index)
      {
        Results[index] = Items[index].AdvCanStackWithBurst(Target, allowHigherQuality: AllowHigherQuality);
      }
    }
  }

  public static class Utilities
  {
    /// <summary>
    /// Coroutine to find storage with item, using IJob for processing.
    /// </summary>
    public static IEnumerator FindStorageWithItemCoroutine(
        Property property,
        ItemInstance targetItem,
        int needed,
        int wanted = 0,
        bool allowHigherQuality = false)
    {
      if (targetItem == null || needed <= 0)
      {
        Log(Level.Warning, $"Invalid input: item={targetItem}, needed={needed}", Category.Storage);
        yield return (false, new KeyValuePair<PlaceableStorageEntity, int>(null, 0), (NativeList<int>)default);
      }

      var cacheManager = CacheManager.GetOrCreateCacheManager(property);
      var cacheKey = new CacheKey(targetItem, property);

      if (cacheManager.IsItemNotFound(cacheKey))
      {
        Log(Level.Verbose, $"Item {targetItem.ID} not found in cache", Category.Storage);
        yield return (false, new KeyValuePair<PlaceableStorageEntity, int>(null, 0), (NativeList<int>)default);
      }

      var logs = new NativeList<LogEntry>(10, Allocator.TempJob);
      var results = new NativeList<StorageResult>(1, Allocator.TempJob);
      try
      {
        var storageKeys = new NativeList<StorageKey>(Allocator.TempJob);
        if (cacheManager.SpecificShelvesCache.TryGetValue(new ItemKey(targetItem), out var specificKeys))
          storageKeys.AddRange(specificKeys);
        storageKeys.AddRange(cacheManager.AnyShelfSlotsCache.GetKeyArray(Allocator.Temp));

        int batchSize = GetDynamicBatchSize(storageKeys.Length, 0.1f, nameof(FindStorageWithItemCoroutine));
        for (int i = 0; i < storageKeys.Length; i += batchSize)
        {
          var batchKeys = new NativeList<StorageKey>(batchSize, Allocator.TempJob);
          for (int j = i; j < Mathf.Min(i + batchSize, storageKeys.Length); j++)
            batchKeys.Add(storageKeys[j]);

          var job = new FindItemJob
          {
            StorageKeys = batchKeys,
            TargetItem = new ItemKey(targetItem),
            Needed = needed,
            AllowHigherQuality = allowHigherQuality,
            Results = results,
            Logs = logs,
            SpecificShelfSlotsCache = cacheManager.SpecificShelfSlotsCache,
            AnyShelfSlotsCache = cacheManager.AnyShelfSlotsCache
          };

          var handle = job.Schedule();
          yield return new WaitUntil(() => handle.IsCompleted);
          handle.Complete();
          batchKeys.Dispose();

          if (results.Length > 0)
          {
            var result = results[0];
            cacheManager.UpdateShelfSearchCache(cacheKey, Storages[property][result.ShelfGuid], result.AvailableQuantity);
            ProcessLogs(logs);
            yield return (true, new KeyValuePair<PlaceableStorageEntity, int>(Storages[property][result.ShelfGuid], result.AvailableQuantity), result.SlotIndices);
          }
        }

        cacheManager.AddItemNotFound(cacheKey);
        logs.Add(new LogEntry { Message = $"No shelf found for {targetItem.ID}", Level = Level.Verbose, Category = Category.Storage });
        ProcessLogs(logs);
        yield return (false, new KeyValuePair<PlaceableStorageEntity, int>(null, 0), (NativeList<int>)default);
      }
      finally
      {
        results.Dispose();
        logs.Dispose();
      }
    }

    /// <summary>
    /// Coroutine to find delivery destinations, using IJob for processing.
    /// </summary>
    public static IEnumerator FindDeliveryDestinationsCoroutine(
        Property property,
        ItemKey item,
        int quantity,
        Guid sourceGuid)
    {
      var destinations = new NativeList<DeliveryDestination>(10, Allocator.TempJob);
      var logs = new NativeList<LogEntry>(10, Allocator.TempJob);
      try
      {
        var storageKeys = new NativeList<StorageKey>(Allocator.TempJob);
        var cacheManager = CacheManager.GetOrCreateCacheManager(property);
        if (cacheManager.SpecificShelvesCache.TryGetValue(item, out var specificKeys))
          storageKeys.AddRange(specificKeys);
        storageKeys.AddRange(cacheManager.AnyShelfSlotsCache.GetKeyArray(Allocator.Temp));

        int batchSize = GetDynamicBatchSize(storageKeys.Length + (IStations.ContainsKey(property) ? IStations[property].Count : 0), 0.1f, nameof(FindDeliveryDestinationsCoroutine));
        NativeArray<Guid> stationKeys = IStations.TryGetValue(property, out var stations) && stations.Keys.Count() > 0 ? new(stations.Keys.ToArray(), Allocator.Temp) : default;
        for (int i = 0; i < storageKeys.Length + stationKeys.Length; i += batchSize)
        {
          var batchKeys = new NativeList<StorageKey>(batchSize, Allocator.TempJob);
          for (int j = i; j < Mathf.Min(i + batchSize, storageKeys.Length + stationKeys.Length); j++)
          {
            if (j < stationKeys.Length)
              batchKeys.Add(new StorageKey(stationKeys[j], StorageTypes.Station));
            else
              batchKeys.Add(storageKeys[j - stationKeys.Length]);
          }

          var job = new FindDeliveryDestinationsJob
          {
            StorageKeys = batchKeys,
            Item = item,
            Quantity = quantity,
            SourceGuid = sourceGuid,
            Destinations = destinations,
            Logs = logs,
            StationSlotsCache = cacheManager.StationSlotsCache,
            SpecificShelfSlotsCache = cacheManager.SpecificShelfSlotsCache,
            AnyShelfSlotsCache = cacheManager.AnyShelfSlotsCache,
          };

          var handle = job.Schedule();
          yield return new WaitUntil(() => handle.IsCompleted);
          handle.Complete();
          batchKeys.Dispose();

          if (destinations.Length > 0)
          {
            ProcessLogs(logs);
            yield return (true, destinations);
          }
        }

        logs.Add(new LogEntry { Message = $"No delivery destinations found for {item.Id}", Level = Level.Verbose, Category = Category.Storage });
        ProcessLogs(logs);
        yield return (false, (NativeList<DeliveryDestination>)default);
      }
      finally
      {
        destinations.Dispose();
        logs.Dispose();
      }
    }

    /// <summary>
    /// Reserves input slots for an item using a Burst job.
    /// </summary>
    public static async Task<List<ItemSlot>> AdvReserveInputSlotsForItemAsync(this List<ItemSlot> slots, Guid entityGuid, ItemInstance item, NetworkObject locker, bool allowHigherQuality = false)
    {
      return await Performance.TrackExecutionAsync(nameof(AdvReserveInputSlotsForItemAsync), async () =>
      {
        var reservedSlots = SlotListPool.Get();
        try
        {
          Log(Level.Verbose,
                    $"AdvReserveInputSlotsForItemAsync: item={item?.ID ?? "null"}, slots={slots?.Count ?? 0}, allowHigherQuality={allowHigherQuality}",
                    Category.Storage);

          if (!ValidateReserveInputs(slots, item, locker, out string error))
          {
            Log(Level.Warning, $"AdvReserveInputSlotsForItemAsync: {error}", Category.Storage);
            return reservedSlots;
          }

          var slotData = new NativeArray<SlotData>(slots.Count, Allocator.TempJob);
          var reservedIndices = new NativeList<int>(slots.Count, Allocator.TempJob);
          var logs = new NativeList<LogEntry>(slots.Count, Allocator.TempJob);

          for (int i = 0; i < slots.Count; i++)
          {
            slotData[i] = new SlotData(slots[i]);
          }

          var job = new ReserveInputSlotsJob
          {
            Slots = slotData,
            Item = new ItemKey(item),
            ReservedIndices = reservedIndices,
            Logs = logs,
            AllowHigherQuality = allowHigherQuality,
            RemainingQuantity = item?.Quantity ?? 0
          };

          var handle = job.Schedule(slots.Count, 64);
          handle.Complete();

          // Reserve slots post-job using mapper
          int remaining = item?.Quantity ?? 0;
          await JobScheduler.ExecuteInBatchesAsync(reservedIndices.Length, i =>
                {
                  int slotDataIndex = reservedIndices[i];
                  var slotDataItem = slotData[slotDataIndex];
                  int slotIndex = slotDataItem.SlotIndex;
                  var slot = slots[slotIndex];
                  int capacity = slotDataItem.Item.Id != "" && slotDataItem.Item.AdvCanStackWithBurst(new ItemKey(item), allowHigherQuality)
                            ? slotDataItem.StackLimit - slotDataItem.Quantity
                            : item.StackLimit;
                  int amountToReserve = Mathf.Min(capacity, remaining);

                  if (SlotManager.ReserveSlot(entityGuid, slot, locker, "Employee reserving slot for refill", item, amountToReserve))
                  {
                    reservedSlots.Add(slot);
                    remaining -= amountToReserve;
                    Log(Level.Verbose,
                              $"Reserved slot {slot.SlotIndex} (capacity={capacity}, amount={amountToReserve})",
                              Category.Storage);
                  }
                }, nameof(AdvReserveInputSlotsForItemAsync), 0.1f);

          ProcessLogs(logs);

          Log(reservedSlots.Count > 0 ? Level.Info : Level.Warning,
                    $"AdvReserveInputSlotsForItemAsync: Reserved {reservedSlots.Count} slots, remaining={remaining}",
                    Category.Storage);

          if (reservedSlots.Count == 0)
          {
            SlotListPool.Release(reservedSlots);
            return new List<ItemSlot>();
          }

          return reservedSlots;
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"AdvReserveInputSlotsForItemAsync: Error - {ex}", Category.Storage);
          foreach (var slot in reservedSlots)
            SlotManager.ReleaseSlot(slot);
          SlotListPool.Release(reservedSlots);
          return new List<ItemSlot>();
        }
      });
    }

    public static async Task<(bool success, ItemInstance item)> AdvRemoveItemAsync(this ItemSlot slot, int amount, Guid entityGuid, Employee employee)
    {
      ItemInstance item = null;
      Log(Level.Verbose, $"AdvRemoveItemAsync: Attempting to remove {amount} from slot", Category.EmployeeCore);
      if (slot == null || amount <= 0)
      {
        return (false, item);
      }

      item = slot.ItemInstance;
      var operations = new List<(Guid entityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)>
        {
          (entityGuid, slot, item, amount, false, employee.NetworkObject, "remove")
        };

      bool success = await SlotManager.ExecuteSlotOperationsAsync(operations);
      Log(Level.Verbose, $"AdvRemoveItemAsync: {(success ? "Success" : "Failed")}, new quantity={slot.Quantity}", Category.EmployeeCore);
      return (success, item);
    }

    public static async Task<bool> AdvInsertItemAsync(this ItemSlot slot, ItemInstance item, int amount, Guid entityGuid, Employee employee)
    {
      Log(Level.Verbose, $"AdvInsertItemAsync: Attempting to insert {amount} of {item?.ID}", Category.EmployeeCore);
      if (slot == null || item == null || amount <= 0)
        return false;

      var operations = new List<(Guid entityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)>
        {
          (entityGuid, slot, item, amount, true, employee.NetworkObject, "insert")
        };

      bool success = await SlotManager.ExecuteSlotOperationsAsync(operations);
      Log(Level.Verbose, $"AdvInsertItemAsync: {(success ? "Success" : "Failed")}, new quantity={slot.Quantity}", Category.EmployeeCore);
      return success;
    }

    /// <summary>
    /// Retrieves output slots containing items matching the template item.
    /// When allowHigherQuality is true, the slot's item quality can be ≥ the target item's quality.
    /// </summary>
    public static List<ItemSlot> GetOutputSlotsContainingItem(ITransitEntity entity, ItemInstance item, bool allowHigherQuality = false)
    {
      Log(Level.Verbose,
          $"GetOutputSlotsContainingItem: Start for item={item?.ID ?? "null"}, entity={entity?.GUID.ToString() ?? "null"}, allowHigherQuality={allowHigherQuality}",
          Category.Storage);

      if (entity == null || item == null)
      {
        Log(Level.Warning,
            $"GetOutputSlotsContainingItem: Invalid input (entity={entity != null}, item={item != null})",
            Category.Storage);
        return new List<ItemSlot>();
      }

      var result = new List<ItemSlot>(entity.OutputSlots.Count);
      foreach (var slot in entity.OutputSlots)
      {
        if (slot?.ItemInstance != null && !slot.IsLocked && slot.Quantity > 0 &&
            slot.ItemInstance.AdvCanStackWith(item, allowHigherQuality: allowHigherQuality))
        {
          Log(Level.Verbose,
              $"GetOutputSlotsContainingItem: Found slot with item={slot.ItemInstance.ID}, quality={(slot.ItemInstance as ProductItemInstance)?.Quality}, qty={slot.Quantity}",
              Category.Storage);
          result.Add(slot);
        }
      }

      Log(Level.Info,
          $"GetOutputSlotsContainingItem: Found {result.Count} slots for item={item.ID}, quality={(item as ProductItemInstance)?.Quality}",
          Category.Storage);
      return result;
    }

    // Finds a packaging station for an item
    public static ITransitEntity FindPackagingStation(Property property, ItemInstance item)
    {
      // Get stations for the assigned property
      if (!IStations.TryGetValue(property, out var stations) || stations == null)
      {
        Log(Level.Warning, $"FindPackagingStation: No stations found for property {property.name}", Category.Storage);
        return null;
      }
      // Find a suitable packaging station
      var suitableStation = stations.Values.FirstOrDefault(s => s is PackagingStationAdapter p && !s.IsInUse && s.CanRefill(item))?.TransitEntity;
      if (suitableStation == null)
        Log(Level.Info, $"FindPackagingStation: No suitable packaging station for item {item.ID} in property {property.name}", Category.Storage);
      else
        Log(Level.Info, $"FindPackagingStation: Found station {suitableStation.GUID} for item {item.ID}", Category.Storage);
      return suitableStation;
    }

    /// <summary>
    /// Checks if two items can stack based on ID, packaging, quality, and quantity.
    /// Supports ItemKey comparisons for Job System compatibility.
    /// </summary>
    public static bool AdvCanStackWith(this ItemInstance item, ItemInstance targetItem, bool allowHigherQuality = false, bool checkQuantities = false)
    {
      Log(Level.Verbose,
          $"AdvCanStackWith: item={item?.ID ?? "null"} vs target={targetItem?.ID ?? "null"}",
          Category.Storage);

      if (!ValidateStackingInputs(item, targetItem, out string error))
      {
        Log(Level.Verbose, $"AdvCanStackWith: {error}", Category.Storage);
        return false;
      }

      // Convert to ItemKey for consistency
      var itemKey = new ItemKey(item);
      var targetKey = new ItemKey(targetItem);
      return itemKey.AdvCanStackWithBurst(targetKey, allowHigherQuality, checkQuantities, item.Quantity, targetItem.Quantity);
    }

    /// <summary>
    /// Schedules an update to the storage cache for a shelf's parent property using FishNet's TimeManager.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    public static void UpdateStorageCache(PlaceableStorageEntity shelf)
    {
      if (shelf == null || shelf.ParentProperty == null)
      {
        Log(Level.Warning,
            "UpdateStorageCache: Shelf or ParentProperty is null",
            Category.Storage);
        return;
      }

      CacheManager.GetOrCreateCacheManager(shelf.ParentProperty).QueueStorageUpdate(shelf);
      Log(Level.Verbose,
          $"UpdateStorageCache: Queued for shelf={shelf.GUID}",
          Category.Storage);
    }

    /// <summary>
    /// Schedules an update to the station cache for a station's parent property using FishNet's TimeManager.
    /// </summary>
    /// <param name="station">The station adapter to update.</param>
    public static void UpdateStationCache(IStationAdapter station)
    {
      if (station == null || station.Buildable?.ParentProperty == null)
      {
        Log(Level.Warning,
            "UpdateStationCache: Station or ParentProperty is null",
            Category.Storage);
        return;
      }

      CacheManager.GetOrCreateCacheManager(station.ParentProperty).QueueStationUpdate(station);
      Log(Level.Verbose,
          $"UpdateStationCache: Queued for station={station.GUID}",
          Category.Storage);
    }

    /// <summary>
    /// Gets the total quantity of an item in a shelf's output slots.
    /// </summary>
    public static int GetItemQuantityInStorage(this PlaceableStorageEntity shelf, ItemInstance targetItem, bool allowHigherQuality = false)
    {
      Log(Level.Verbose,
          $"GetItemQuantityInStorage: Start for shelf={shelf?.GUID.ToString() ?? "null"}, item={targetItem?.ID ?? "null"}",
          Category.Storage);

      if (shelf == null || targetItem == null)
        return 0;

      int qty = 0;
      foreach (var slot in shelf.OutputSlots)
      {
        if (slot?.ItemInstance != null && slot.ItemInstance.AdvCanStackWith(targetItem, allowHigherQuality: allowHigherQuality))
          qty += slot.Quantity;
      }

      Log(Level.Info, $"GetItemQuantityInStorage: Found {qty} of {targetItem.ID}", Category.Storage);
      return qty;
    }

    /// <summary>
    /// Updates the storage configuration for a shelf.
    /// </summary>
    public static void UpdateStorageConfiguration(PlaceableStorageEntity shelf, ItemInstance assignedItem)
    {
      Log(Level.Verbose,
          $"UpdateStorageConfiguration: Start for shelf={shelf?.GUID.ToString() ?? "null"}, item={assignedItem?.ID ?? "null"}",
          Category.Storage);

      if (!ValidateConfigurationInputs(shelf, out StorageConfiguration config, out string error))
      {
        Log(Level.Warning, $"UpdateStorageConfiguration: {error}", Category.Storage);
        return;
      }

      UpdateStorageConfigurationInternal(shelf, assignedItem, config);
    }

    private static bool ValidateReserveInputs(List<ItemSlot> slots, ItemInstance item, NetworkObject locker, out string error)
    {
      if (item == null)
      {
        error = "Invalid input: item is null";
        return false;
      }
      if (locker == null)
      {
        error = "Invalid input: locker is null";
        return false;
      }
      if (slots == null)
      {
        error = "Invalid input: slots is null";
        return false;
      }
      error = string.Empty;
      return true;
    }

    private static bool ValidateStackingInputs(ItemInstance item, ItemInstance targetItem, out string error)
    {
      if (item == null)
      {
        error = "Item is null";
        return false;
      }
      if (targetItem == null)
      {
        error = "Target item is null";
        return false;
      }
      error = string.Empty;
      return true;
    }

    /// <summary>
    /// Creates a CacheKey from an ItemInstance and Property.
    /// </summary>
    public static CacheKey CreateCacheKey(ItemInstance item, Property property)
    {
      return item is ProductItemInstance prodItem
          ? new CacheKey(item.ID, prodItem.AppliedPackaging?.ID, prodItem.Quality, property)
          : new CacheKey(item.ID, null, null, property);
    }

    private static bool TryFindSpecificShelves(Property property, ItemInstance item, int qtyToDeliver, out List<PlaceableStorageEntity> shelves)
    {
      shelves = new List<PlaceableStorageEntity>();
      if (!NoDropOffCache.TryGetValue(property, out var noDropOffCache))
      {
        noDropOffCache = new HashSet<ItemInstance>();
        NoDropOffCache.TryAdd(property, noDropOffCache);
      }
      if (noDropOffCache.Contains(item))
        return false;

      if (SpecificShelves.TryGetValue(property, out var specificShelves) &&
          specificShelves.TryGetValue(item, out shelves) && shelves.Count > 0)
      {
        foreach (var shelf in shelves)
        {
          if (shelf.GetItemQuantityInStorage(item) < qtyToDeliver)
            shelves.Remove(shelf);
        }
        if (shelves.Count > 0)
          return true;
        else return false;
      }

      noDropOffCache.Add(item);
      Log(Level.Verbose,
          $"TryFindSpecificShelf: Added {item.ID} to NoDropOffCache",
          Category.Storage);
      return false;
    }

    private static bool ValidateConfigurationInputs(PlaceableStorageEntity shelf, out StorageConfiguration config, out string error)
    {
      config = null;
      if (shelf == null || shelf.GUID == Guid.Empty)
      {
        error = "Invalid shelf: null or empty GUID";
        return false;
      }
      if (!Storages[shelf.ParentProperty].ContainsKey(shelf.GUID))
      {
        error = $"Shelf {shelf.GUID} not found";
        return false;
      }
      if (!StorageConfigs[shelf.ParentProperty].TryGetValue(shelf.GUID, out config))
      {
        error = $"No configuration for shelf {shelf.GUID}";
        return false;
      }
      error = string.Empty;
      return true;
    }

    private static void UpdateStorageConfigurationInternal(PlaceableStorageEntity shelf, ItemInstance assignedItem, StorageConfiguration config)
    {
      Log(Level.Verbose,
          $"UpdateStorageConfigurationInternal: Start for shelf={shelf.GUID}, item={assignedItem?.ID ?? "null"}",
          Category.Storage);

      var property = shelf.ParentProperty;
      var oldAssignedItem = config.AssignedItem;

      // Ensure NoDropOffCache exists
      if (!NoDropOffCache.TryGetValue(property, out var cache))
      {
        cache = new HashSet<ItemInstance>();
        NoDropOffCache.TryAdd(property, cache);
      }
      RemoveStorageFromLists(shelf);

      if (config.Mode == StorageMode.Any)
      {
        AddShelfToAnyShelves(shelf, property);
      }
      else if (config.Mode == StorageMode.Specific && assignedItem != null)
      {
        AddShelfToSpecificShelves(shelf, assignedItem, property, cache);
      }

      // Clean up caches
      var cacheKeysToRemove = new List<CacheKey>();
      if (oldAssignedItem != null)
        cacheKeysToRemove.Add(CreateCacheKey(oldAssignedItem, property));
      if (assignedItem != null)
        cacheKeysToRemove.Add(CreateCacheKey(assignedItem, property));
      foreach (var itemCache in ShelfCache.Where(kvp => kvp.Value.ContainsKey(shelf)))
        cacheKeysToRemove.Add(CreateCacheKey(itemCache.Key, shelf.ParentProperty));

      CacheManager.GetOrCreateCacheManager(property).RemoveShelfSearchCacheEntries(cacheKeysToRemove);
      UpdateStorageCache(shelf);

      Log(Level.Info,
          $"UpdateStorageConfigurationInternal: Completed for shelf={shelf.GUID}",
          Category.Storage);
    }

    private static void AddShelfToAnyShelves(PlaceableStorageEntity shelf, Property property)
    {
      if (!AnyShelves.TryGetValue(property, out var anyShelves))
        AnyShelves[property] = new List<PlaceableStorageEntity> { shelf };

      else if (!anyShelves.Contains(shelf))
      {
        anyShelves.Add(shelf);
        Log(Level.Verbose,
            $"AddShelfToAnyShelves: Added shelf={shelf.GUID}",
            Category.Storage);
      }
    }

    private static void AddShelfToSpecificShelves(PlaceableStorageEntity shelf, ItemInstance assignedItem, Property property, HashSet<ItemInstance> cache)
    {
      if (!ShelfCache.TryGetValue(assignedItem, out var shelfDict))
      {
        shelfDict = new ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>();
        ShelfCache[assignedItem] = shelfDict;
      }

      if (!SpecificShelves.TryGetValue(property, out var specificShelves))
      {
        specificShelves = new Dictionary<ItemInstance, List<PlaceableStorageEntity>>();
        SpecificShelves[property] = specificShelves;
      }

      foreach (var itemShelves in specificShelves.Values)
        itemShelves.Remove(shelf);

      if (!specificShelves.TryGetValue(assignedItem, out var shelves))
      {
        shelves = new List<PlaceableStorageEntity>();
        specificShelves[assignedItem] = shelves;
      }
      if (!shelves.Contains(shelf))
      {
        shelves.Add(shelf);
        Log(Level.Info,
            $"AddShelfToSpecificShelves: Added shelf={shelf.GUID} for {assignedItem.ID}",
            Category.Storage);
      }

      var cacheList = cache.ToList();
      for (int i = cacheList.Count - 1; i >= 0; i--)
      {
        if (cacheList[i].ID == assignedItem.ID)
        {
          cache.Remove(cacheList[i]);
          Log(Level.Verbose,
              $"AddShelfToSpecificShelves: Removed {assignedItem.ID} from NoDropOffCache",
              Category.Storage);
        }
      }

      int qty = GetItemQuantityInStorage(shelf, assignedItem);
      shelfDict[shelf] = new ShelfInfo(qty, true);
    }

    public static void RemoveStorageFromLists(PlaceableStorageEntity shelf)
    {
      Log(Level.Verbose,
          $"RemoveStorageFromLists: Start for shelf={shelf?.GUID.ToString() ?? "null"}",
          Category.Storage);

      if (shelf == null)
        return;

      if (AnyShelves.TryGetValue(shelf.ParentProperty, out var anyShelves))
        anyShelves.Remove(shelf);

      foreach (var itemCache in ShelfCache.Values)
        itemCache.TryRemove(shelf, out var _);

      if (SpecificShelves.TryGetValue(shelf.ParentProperty, out var specificShelves))
      {
        foreach (var itemShelves in specificShelves.Values)
          itemShelves.Remove(shelf);
        Log(Level.Verbose,
            $"RemoveStorageFromLists: Removed shelf={shelf.GUID} from SpecificShelves",
            Category.Storage);
      }

      Log(Level.Info,
          $"RemoveStorageFromLists: Completed for shelf={shelf.GUID}",
          Category.Storage);
    }
  }

  public static class Extensions
  {
    public readonly struct CacheKey : IEquatable<CacheKey>
    {
      public readonly ItemInstance Item;
      public readonly string ID;
      public readonly string PackagingId;
      public readonly EQuality? Quality;
      public readonly Property Property;

      public CacheKey(string id, string packagingId, EQuality? quality, Property property)
      {
        ID = id ?? throw new ArgumentNullException(nameof(id));
        PackagingId = packagingId;
        Quality = quality;
        Property = property ?? throw new ArgumentNullException(nameof(property));
      }
      public CacheKey(ItemInstance item, Property property)
      {
        if (item == null) throw new ArgumentNullException(nameof(item));
        ID = item.ID;
        PackagingId = (item as ProductItemInstance)?.PackagingID;
        Quality = (item as QualityItemInstance)?.Quality;
        Property = property ?? throw new ArgumentNullException(nameof(property));
      }

      public bool Equals(CacheKey other) =>
          ID == other.ID && PackagingId == other.PackagingId && Quality == other.Quality && ReferenceEquals(Property, other.Property);

      public override bool Equals(object obj) => obj is CacheKey other && Equals(other);

      public override int GetHashCode() => HashCode.Combine(ID, PackagingId, Quality, Property);
    }

    public enum StorageTypes
    {
      Employee,
      AllShelves,
      AnyShelf,
      SpecificShelf,
      Station,
      LoadingDock
    }

    public struct SlotData(ItemSlot slot)
    {
      public int SlotIndex = slot.SlotIndex;
      public ItemKey Item = new(slot.ItemInstance);
      public int Quantity = slot.Quantity;
      public bool IsLocked = slot.IsLocked;
      public int StackLimit = slot.ItemInstance?.StackLimit ?? -1;
      public bool IsValid = true;
    }

    public struct StorageKey(Guid guid, StorageTypes type) : IEquatable<StorageKey>
    {
      public Guid Guid = guid != Guid.Empty ? guid : throw new ArgumentException("Invalid GUID");
      public StorageTypes Type = type;

      public bool Equals(StorageKey other) => Guid.Equals(other.Guid);
      public override bool Equals(object obj) => obj is StorageKey other && Equals(other);
      public override int GetHashCode() => Guid.GetHashCode();
    }

    public struct ItemKey : IEquatable<ItemKey>
    {
      public FixedString32Bytes Id;
      public FixedString32Bytes PackagingId;
      public EQualityBurst Quality;
      public static ItemKey Empty => new ItemKey("", "", EQualityBurst.None);
      public ItemKey(ItemInstance item)
      {
        Id = item.ID ?? throw new ArgumentNullException(nameof(Id));
        PackagingId = (item as ProductItemInstance)?.AppliedPackaging?.ID ?? "";
        Quality = (item is ProductItemInstance prodItem) ? Enum.Parse<EQualityBurst>(prodItem.Quality.ToString()) : EQualityBurst.None;
      }
      public ItemKey(string id, string packagingId, EQualityBurst? quality)
      {
        Id = id ?? "";
        PackagingId = packagingId ?? "";
        Quality = quality ?? EQualityBurst.None;
      }
      public bool Equals(ItemKey other) => Id == other.Id && PackagingId == other.PackagingId && Quality == other.Quality;
      public override bool Equals(object obj) => obj is ItemKey other && Equals(other);
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
      /// <summary>
      /// Checks if two ItemKeys can stack based on ID, packaging, and quality.
      /// </summary>
      internal readonly bool AdvCanStackWithBurst(ItemKey targetItem, bool allowHigherQuality = false, bool checkQuantities = false, int stackLimit = -1, int itemQuantity = 0, int targetQuantity = 0)
      {
        bool qualityMatch;
        if (targetItem.Id == "Any")
        {
          qualityMatch = Quality >= targetItem.Quality;
          return qualityMatch;
        }

        if (Id != targetItem.Id)
          return false;

        if (checkQuantities)
        {
          if (stackLimit == -1)
            throw new ArgumentException($"AdvCanStackWith: CheckQuantities requires a stackLimit");
          if (stackLimit < targetQuantity + itemQuantity)
            return false;
        }

        if (!ArePackagingsCompatible(targetItem))
          return false;

        qualityMatch = allowHigherQuality ? Quality >= targetItem.Quality : Quality == targetItem.Quality;
        return qualityMatch;
      }

      [BurstCompile]
      private readonly bool ArePackagingsCompatible(ItemKey targetItem)
      {
        return (PackagingId == "" && targetItem.PackagingId == "") ||
               (PackagingId != "" && targetItem.PackagingId != "" && PackagingId == targetItem.PackagingId);
      }
    }

    /// <summary>
    /// Result struct for storage search in Burst jobs.
    /// </summary>
    [BurstCompile]
    public struct StorageResult
    {
      public Guid ShelfGuid;
      public int AvailableQuantity;
      public NativeList<int> SlotIndices;
    }

    /// <summary>
    /// Result struct for delivery destinations in Burst jobs.
    /// </summary>
    [BurstCompile]
    public struct DeliveryDestination
    {
      public Guid DestinationGuid;
      public NativeList<int> SlotIndices;
      public int Capacity;
    }
  }

  public static class Constants
  {
    public static readonly ConcurrentDictionary<Property, CacheManager> CacheManagers = new();
    public static Dictionary<int, Property> IdToProperty;
    public static Dictionary<Guid, JObject> PendingConfigData { get; } = new(10);
    public static Dictionary<Property, Dictionary<Guid, StorageConfiguration>> StorageConfigs { get; } = new(10);
    public static Dictionary<Property, Dictionary<Guid, PlaceableStorageEntity>> Storages { get; } = new(10);
    public static Dictionary<Property, List<PlaceableStorageEntity>> AnyShelves { get; } = new(10);
    public static Dictionary<Property, Dictionary<ItemInstance, List<PlaceableStorageEntity>>> SpecificShelves { get; } = new(10);
    public static ConcurrentDictionary<ItemInstance, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>> ShelfCache { get; } = new();
    public static Dictionary<Property, Dictionary<Guid, IEmployeeAdapter>> IEmployees = new();
    public static Dictionary<Property, Dictionary<Guid, IStationAdapter>> IStations = [];
    public static Dictionary<Guid, List<ItemInstance>> StationRefillLists = [];
    public static readonly ObjectPool<List<ItemSlot>> SlotListPool = new ObjectPool<List<ItemSlot>>(
        createFunc: () => new List<ItemSlot>(10),
        actionOnGet: null,
        actionOnRelease: list => list.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);
  }

  /// <summary>
  /// Manages storage and station-related caches with thread-safe access for efficient task validation and item lookup.
  /// </summary>
  public class CacheManager
  {
    private readonly Property _property;
    private bool _isProcessingUpdates;
    private bool _isActive;
    private readonly ObjectPool<List<KeyValuePair<PlaceableStorageEntity, int>>> _shelfListPool = new ObjectPool<List<KeyValuePair<PlaceableStorageEntity, int>>>(
            createFunc: () => new List<KeyValuePair<PlaceableStorageEntity, int>>(10),
            actionOnGet: null,
            actionOnRelease: list => list.Clear(),
            actionOnDestroy: null,
            collectionCheck: false,
            defaultCapacity: 10,
            maxSize: 100);
    private readonly ObjectPool<Dictionary<ItemInstance, int>> _itemQuantitiesPool = new ObjectPool<Dictionary<ItemInstance, int>>(
        createFunc: () => new Dictionary<ItemInstance, int>(10),
        actionOnGet: null,
        actionOnRelease: dict => dict.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);
    private readonly Dictionary<(Guid, Guid), float> _travelTimeCache = new(1024);
    private readonly Dictionary<Guid, ITransitEntity> _travelTimeEntities = new(100);
    private readonly ConcurrentDictionary<CacheKey, List<KeyValuePair<PlaceableStorageEntity, int>>> _shelfSearchCache = new();
    private readonly ConcurrentDictionary<ItemInstance, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>> _shelfCache = new();
    private readonly ConcurrentDictionary<EQuality?, List<KeyValuePair<PlaceableStorageEntity, ShelfInfo>>> _anyQualityCache = new();
    private readonly ConcurrentQueue<PlaceableStorageEntity> _pendingUpdates = new();
    private readonly ConcurrentQueue<IStationAdapter> _pendingStationUpdates = new();
    private readonly Dictionary<Guid, StorageKey> _loadingDockKeys = new();
    public readonly NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationSlotsCache = new();
    private readonly NativeParallelHashMap<StorageKey, NativeList<SlotData>> _loadingDockSlotsCache = new();
    private readonly NativeParallelHashMap<StorageKey, NativeList<SlotData>> _employeeSlotsCache = new();
    private readonly ConcurrentQueue<(StorageKey key, NativeList<SlotData> slots)> _pendingSlotUpdates = new();

    public NativeParallelHashMap<ItemKey, NativeList<StorageKey>> SpecificShelvesCache = new();
    public readonly NativeParallelHashMap<StorageKey, NativeList<SlotData>> AnyShelfSlotsCache = new();
    public readonly NativeParallelHashMap<StorageKey, NativeList<SlotData>> SpecificShelfSlotsCache = new();
    public readonly ConcurrentDictionary<CacheKey, bool> ItemNotFoundCache = new();
    public event Action<StorageKey, SlotData> OnStorageSlotUpdated;

    public CacheManager(Property property)
    {
      _property = property ?? throw new ArgumentNullException(nameof(property));
      _isActive = false;
      Log(Level.Info, $"CacheManager initialized for {property.name}", Category.Storage);
    }

    /// <summary>
    /// Gets or creates a CacheManager for the specified property.
    /// </summary>
    /// <param name="property">The property to manage.</param>
    /// <returns>The CacheManager instance.</returns>
    public static CacheManager GetOrCreateCacheManager(Property property)
    {
      return CacheManagers.GetOrAdd(property, p => new CacheManager(p));
    }

    /// <summary>
    /// Activates the CacheManager, subscribing to server tick events.
    /// </summary>
    public void Activate()
    {
      if (_isActive) return;
      if (InstanceFinder.IsServer)
      {
        TimeManagerInstance.OnTick += ProcessPendingUpdates;
        TimeManagerInstance.OnTick += ProcessPendingSlotUpdates;
        UpdatePropertyDataCache();
      }
      _isActive = true;
      Log(Level.Info, $"[{_property.name}] CacheManager activated", Category.Tasks);
    }

    /// <summary>
    /// Deactivates the CacheManager, clearing queues and unsubscribing from events.
    /// </summary>
    public void Deactivate()
    {
      if (!_isActive) return;
      _isActive = false;
      Cleanup();
      Log(Level.Info, $"[{_property.name}] CacheManager deactivated", Category.Tasks);
    }

    /// <summary>
    /// Cleans up all caches, ensuring proper disposal of Native Containers.
    /// </summary>
    public void Cleanup()
    {
      Log(Level.Info, "CacheManager.Cleanup: Unsubscribing and clearing caches", Category.Storage);
      if (InstanceFinder.IsServer)
      {
        InstanceFinder.TimeManager.OnTick -= ProcessPendingUpdates;
        InstanceFinder.TimeManager.OnTick -= ProcessPendingSlotUpdates;
      }

      IdToProperty.Clear();
      Performance.Cleanup();
      CoroutineRunner.Instance.StopAllCoroutines();
      ItemNotFoundCache.Clear();
      _shelfSearchCache.Clear();
      _shelfCache.Clear();
      _anyQualityCache.Clear();
      _pendingUpdates.Clear();
      _pendingStationUpdates.Clear();
      _pendingSlotUpdates.Clear();
      _travelTimeCache.Clear();
      _travelTimeEntities.Clear();
      _shelfListPool.Clear();

      // Dispose Native Containers
      if (SpecificShelvesCache.IsCreated)
      {
        foreach (var list in SpecificShelvesCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        SpecificShelvesCache.Dispose();
      }
      if (AnyShelfSlotsCache.IsCreated)
      {
        foreach (var list in AnyShelfSlotsCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        AnyShelfSlotsCache.Dispose();
      }
      if (SpecificShelfSlotsCache.IsCreated)
      {
        foreach (var list in SpecificShelfSlotsCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        SpecificShelfSlotsCache.Dispose();
      }
      if (StationSlotsCache.IsCreated)
      {
        foreach (var list in StationSlotsCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        StationSlotsCache.Dispose();
      }
      if (_loadingDockSlotsCache.IsCreated)
      {
        foreach (var list in _loadingDockSlotsCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        _loadingDockSlotsCache.Dispose();
      }
      if (_employeeSlotsCache.IsCreated)
      {
        foreach (var list in _employeeSlotsCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        _employeeSlotsCache.Dispose();
      }
    }

    private void ProcessPendingSlotUpdates()
    {
      const long MAX_TIME_MS = 1;
      if (_pendingSlotUpdates.IsEmpty) return;
      int totalItems = _pendingSlotUpdates.Count;
      Performance.TrackExecution(nameof(ProcessPendingSlotUpdates), () =>
      {
        var stopwatch = Stopwatch.StartNew();
        int maxItems = GetDynamicBatchSize(totalItems, 0.15f, nameof(ProcessPendingSlotUpdates)); int processedCount = 0; try
        {
          while (processedCount < maxItems && stopwatch.ElapsedMilliseconds < MAX_TIME_MS && _pendingSlotUpdates.TryDequeue(out var update))
          {
            var (key, slots) = update;
            NativeParallelHashMap<StorageKey, NativeList<SlotData>> targetCache = key.Type switch
            {
              StorageTypes.AnyShelf => AnyShelfSlotsCache,
              StorageTypes.SpecificShelf => SpecificShelfSlotsCache,
              StorageTypes.Station => StationSlotsCache,
              StorageTypes.LoadingDock => _loadingDockSlotsCache,
              StorageTypes.Employee => _employeeSlotsCache,
              _ => default
            };
            if (!targetCache.IsCreated)
            {
              slots.Dispose();
              continue;
            }
            if (targetCache.TryGetValue(key, out var existingSlots))
              existingSlots.Dispose();
            targetCache[key] = slots;
            if (key.Type == StorageTypes.SpecificShelf && slots.Length > 0 && slots[0].Item.Id != "")
            {
              var itemKey = slots[0].Item;
              if (!SpecificShelvesCache.TryGetValue(itemKey, out var storageKeys))
              {
                storageKeys = new NativeList<StorageKey>(Allocator.Persistent);
                SpecificShelvesCache[itemKey] = storageKeys;
              }
              if (!storageKeys.Contains(key))
                storageKeys.Add(key);
            }
            if (slots.Length > 0 && slots[0].Item.Id != "" && slots[0].Quantity > 0)
              ClearItemNotFoundCache(slots[0].Item.CreateItemInstance());
            processedCount++; // Invoke event for each slot 
            for (int i = 0; i < slots.Length; i++)
              OnStorageSlotUpdated?.Invoke(key, slots[i]);
#if DEBUG
            Log(Level.Verbose, $"Updated slot for {key.Type} {key.Guid}, SlotIndex={slots[0].SlotIndex}", Category.Storage);
#endif
          }
          if (processedCount > 0) { double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount); DynamicProfiler.AddSample(nameof(ProcessPendingSlotUpdates), avgItemTimeMs); }
        }
        catch (Exception ex) { Log(Level.Error, $"ProcessPendingSlotUpdates: Error - {ex}", Category.Storage); }
      }, itemCount: totalItems);
    }

    public void RegisterItemSlot(ItemSlot slot, StorageKey parentKey)
    {
      if (slot == null) return;
      slot.onItemDataChanged += () => OnItemSlotChanged(slot, parentKey);
      Log(Level.Verbose, $"Registered ItemSlot for {parentKey.Type} {parentKey.Guid}", Category.Storage);
    }

    public void UnregisterItemSlot(ItemSlot slot, StorageKey parentKey)
    {
      if (slot == null) return;
      slot.onItemDataChanged -= () => OnItemSlotChanged(slot, parentKey);
      Log(Level.Verbose, $"Unregistered ItemSlot for {parentKey.Type} {parentKey.Guid}", Category.Storage);
    }

    public void QueueSlotUpdate(StorageKey key, IEnumerable<SlotData> slots)
    {
      var slotList = new NativeList<SlotData>(slots.Count(), Allocator.Temp);
      foreach (var slot in slots) slotList.Add(slot);
      _pendingSlotUpdates.Enqueue((key, slotList));
      Log(Level.Verbose, $"Queued slot update for {key.Type} {key.Guid}", Category.Storage);
    }

    private void OnItemSlotChanged(ItemSlot slot, StorageKey parentKey)
    {
      var slots = new NativeList<SlotData>(8, Allocator.Temp);
      var entity = _travelTimeEntities.TryGetValue(parentKey.Guid, out var transitEntity) ? transitEntity : null;
      if (entity is PlaceableStorageEntity storage)
      {
        foreach (var s in storage.OutputSlots)
          slots.Add(new SlotData
          {
            Item = new ItemKey(s.ItemInstance),
            Quantity = s.Quantity,
            SlotIndex = s.SlotIndex,
            StackLimit = s.ItemInstance?.StackLimit ?? -1,
            IsValid = true
          });
      }
      else if (entity is IStationAdapter station)
      {
        for (int i = 0; i < station.InsertSlots.Count; i++)
          slots.Add(new SlotData
          {
            Item = new ItemKey(station.InsertSlots[i].ItemInstance),
            Quantity = station.InsertSlots[i].Quantity,
            SlotIndex = station.InsertSlots[i].SlotIndex,
            StackLimit = station.InsertSlots[i].ItemInstance != null ? station.InsertSlots[i].GetCapacityForItem(station.InsertSlots[i].ItemInstance) : -1,
            IsValid = true
          });
        for (int i = 0; i < station.ProductSlots.Count; i++)
          slots.Add(new SlotData
          {
            Item = new ItemKey(station.ProductSlots[i].ItemInstance),
            Quantity = station.ProductSlots[i].Quantity,
            SlotIndex = station.InsertSlots[i].SlotIndex,
            StackLimit = station.ProductSlots[i].ItemInstance != null ? station.ProductSlots[i].GetCapacityForItem(station.ProductSlots[i].ItemInstance) : -1,
            IsValid = true
          });
        slots.Add(new SlotData
        {
          Item = new ItemKey(station.OutputSlot.ItemInstance),
          Quantity = station.OutputSlot.Quantity,
          SlotIndex = station.OutputSlot.SlotIndex,
          StackLimit = station.OutputSlot.ItemInstance != null ? station.OutputSlot.GetCapacityForItem(station.OutputSlot.ItemInstance) : -1,
          IsValid = true
        });
      }
      else if (entity is LoadingDock dock)
      {
        foreach (var s in dock.OutputSlots) // Use OutputSlots as per SetOccupant
          slots.Add(new SlotData
          {
            Item = new ItemKey(s.ItemInstance),
            Quantity = s.Quantity,
            SlotIndex = s.SlotIndex,
            StackLimit = s.ItemInstance?.StackLimit ?? -1,
            IsValid = true
          });
      }
      else if (entity is Employee employee)
      {
        foreach (var s in employee.Inventory.ItemSlots)
          slots.Add(new SlotData
          {
            Item = new ItemKey(s.ItemInstance),
            Quantity = s.Quantity,
            SlotIndex = s.SlotIndex,
            StackLimit = s.ItemInstance?.StackLimit ?? -1,
            IsValid = true
          });
      }

      if (slots.Length > 0)
      {
        _pendingSlotUpdates.Enqueue((parentKey, slots));
        Log(Level.Verbose, $"Queued slot update for {parentKey.Type} {parentKey.Guid}", Category.Storage);
      }
      else
      {
        slots.Dispose();
      }
    }

    /// <summary>
    /// Initializes the property name-to-id mapping using all known properties.
    /// </summary>
    /// <param name="properties">List of all properties in the game.</param>
    public static void InitializePropertyIdMap()
    {
      var properties = Property.Properties.Where(p => p != null);
      IdToProperty = new Dictionary<int, Property>(properties.Count());

      foreach (var prop in properties)
        AddOrUpdatePropertyToIdMap(prop);

      Log(Level.Info, $"Initialized property id map with {IdToProperty.Count()} entries", Category.Tasks);
    }

    public static void AddOrUpdatePropertyToIdMap(Property property)
    {
      if (IdToProperty.ContainsKey(property.NetworkObject.ObjectId))
        IdToProperty.Remove(property.NetworkObject.ObjectId);
      IdToProperty.Add(property.NetworkObject.ObjectId, property);
    }

    public static void RemovePropertyFromIdMap(Property property)
    {
      if (IdToProperty.ContainsKey(property.NetworkObject.ObjectId))
        IdToProperty.Remove(property.NetworkObject.ObjectId);
    }

    /// <summary>
    /// Retrieves a Property by its Guid in a managed context.
    /// </summary>
    /// <param name="id">NetworkObject.ObjectId of the property.</param>
    /// <returns>Property object or null if not found.</returns>
    public static Property GetPropertyById(int id)
    {
      return IdToProperty.TryGetValue(id, out var prop) ? prop : null;
    }

    [BurstCompile]
    public bool TryGetShelfSlots(StorageKey shelfKey, out NativeList<SlotData> slots)
    {
      var cache = shelfKey.Type == StorageTypes.AnyShelf ? AnyShelfSlotsCache : SpecificShelfSlotsCache;
      if (cache.TryGetValue(shelfKey, out slots))
        return true;
      slots = default;
      return false;
    }

    [BurstCompile]
    public bool TryGetStationSlots(StorageKey stationKey, out NativeList<SlotData> slots)
    {
      if (StationSlotsCache.TryGetValue(stationKey, out slots))
        return true;
      slots = default;
      return false;
    }

    [BurstCompile]
    public bool TryGetLoadingDockSlots(StorageKey dockKey, out NativeList<SlotData> slots)
    {
      if (_loadingDockSlotsCache.TryGetValue(dockKey, out slots))
        return true;
      slots = default;
      return false;
    }

    [BurstCompile]
    public bool TryGetEmployeeSlots(StorageKey employeeKey, out NativeList<SlotData> slots)
    {
      if (_employeeSlotsCache.TryGetValue(employeeKey, out slots))
        return true;
      slots = default;
      return false;
    }

    /// <summary>
    /// Synchronously finds delivery destinations for an item, optimized for Burst.
    /// </summary>
    /// <param name="property">The property to search.</param>
    /// <param name="item">The item to deliver.</param>
    /// <param name="quantity">The quantity to deliver.</param>
    /// <param name="sourceGuid">The source entity GUID to exclude.</param>
    /// <param name="destinations">List of destination GUIDs, slot indices, and capacities.</param>
    /// <returns>True if destinations are found; otherwise, false.</returns>
    [BurstCompile]
    public bool TryGetDeliveryDestinations(
        Property property,
        ItemKey item,
        int quantity,
        Guid sourceGuid,
        out NativeList<(Guid DestinationGuid, NativeList<int> Slots, int Capacity)> destinations)
    {
      destinations = new NativeList<(Guid, NativeList<int>, int)>(Allocator.Temp);
      int remainingQty = quantity;

      // Check packaging stations
      if (IStations.TryGetValue(property, out var stations))
      {
        foreach (var station in stations.Values)
        {
          if (station.TypeOf != typeof(PackagingStation) || station.IsInUse || !station.CanRefill(item.CreateItemInstance()))
            continue;

          if (StationSlotsCache.TryGetValue(new StorageKey(station.GUID, StorageTypes.Station), out var slots))
          {
            var productSlots = new NativeList<int>(Allocator.Temp);
            int capacity = 0;
            for (int i = 0; i < slots.Length; i++)
            {
              var slot = slots[i];
              if (slot.IsValid && slot.StackLimit > slot.Quantity)
              {
                int slotCapacity = slot.StackLimit - slot.Quantity;
                if (slotCapacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(item)))
                {
                  productSlots.Add(slot.SlotIndex);
                  capacity += slotCapacity;
                }
              }
            }

            if (capacity > 0)
            {
              destinations.Add((station.GUID, productSlots, Mathf.Min(capacity, remainingQty)));
              remainingQty -= capacity;
              if (remainingQty <= 0)
                return true;
            }
            else
            {
              productSlots.Dispose();
            }
          }
        }
      }

      // Check specific shelves
      if (SpecificShelvesCache.TryGetValue(item, out var storageKeys))
      {
        foreach (var storageKey in storageKeys)
        {
          if (storageKey.Guid == sourceGuid || !Storages[property].ContainsKey(storageKey.Guid))
            continue;

          if (SpecificShelfSlotsCache.TryGetValue(storageKey, out var slots))
          {
            var inputSlots = new NativeList<int>(Allocator.Temp);
            int capacity = 0;
            for (int i = 0; i < slots.Length; i++)
            {
              var slot = slots[i];
              if (slot.IsValid && slot.StackLimit > slot.Quantity)
              {
                int slotCapacity = slot.StackLimit - slot.Quantity;
                if (slotCapacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(item)))
                {
                  inputSlots.Add(slot.SlotIndex);
                  capacity += slotCapacity;
                }
              }
            }

            if (capacity > 0)
            {
              destinations.Add((storageKey.Guid, inputSlots, Mathf.Min(capacity, remainingQty)));
              remainingQty -= capacity;
              if (remainingQty <= 0)
                return true;
            }
            else
            {
              inputSlots.Dispose();
            }
          }
        }
      }

      // Check any shelves
      foreach (var storageKey in AnyShelfSlotsCache.GetKeyArray(Allocator.Temp))
      {
        if (storageKey.Guid == sourceGuid || !Storages[property].ContainsKey(storageKey.Guid))
          continue;

        if (AnyShelfSlotsCache.TryGetValue(storageKey, out var slots))
        {
          var inputSlots = new NativeList<int>(Allocator.Temp);
          int capacity = 0;
          for (int i = 0; i < slots.Length; i++)
          {
            var slot = slots[i];
            if (slot.IsValid && slot.StackLimit > slot.Quantity)
            {
              int slotCapacity = slot.StackLimit - slot.Quantity;
              if (slotCapacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(item)))
              {
                inputSlots.Add(slot.SlotIndex);
                capacity += slotCapacity;
              }
            }
          }

          if (capacity > 0)
          {
            destinations.Add((storageKey.Guid, inputSlots, Mathf.Min(capacity, remainingQty)));
            remainingQty -= capacity;
            if (remainingQty <= 0)
              return true;
          }
          else
          {
            inputSlots.Dispose();
          }
        }
      }

      return destinations.Length > 0;
    }

    /// <summary>
    /// Gets the travel time between two transit entities, returning a default value if not cached.
    /// </summary>
    /// <param name="entityA">First transit entity.</param>
    /// <param name="entityB">Second transit entity.</param>
    /// <param name="employee">Employee for NavMesh access point calculation.</param>
    /// <returns>Travel time (seconds), or float.MaxValue if invalid.</returns>
    public float GetTravelTime(ITransitEntity entityA, ITransitEntity entityB)
    {
      if (entityA == null || entityB == null)
      {
        Log(Level.Warning,
            $"GetTravelTime: Invalid input: entityA={entityA?.GUID.ToString() ?? "null"}, entityB={entityB?.GUID}, employee={_property.name ?? "null"}",
            Category.EmployeeCore);
        return float.MaxValue;
      }

      var key = entityA.GUID.CompareTo(entityB.GUID) > 0
          ? (entityA.GUID, entityB.GUID)
          : (entityB.GUID, entityA.GUID);

      if (_travelTimeCache.TryGetValue(key, out float travelTime))
      {
        Log(Level.Verbose,
            $"GetTravelTime: Cache hit for {key.Item1} -> {key.Item2}: {travelTime:F2}s",
            Category.EmployeeCore);
        return travelTime;
      }

      // Default travel time (will be refined by actual movement)
      travelTime = float.MaxValue;
      _travelTimeEntities.TryAdd(entityA.GUID, entityA);
      _travelTimeEntities.TryAdd(entityB.GUID, entityB);

      Log(Level.Verbose,
          $"GetTravelTime: No cache entry for {key.Item1} -> {key.Item2}, using default {travelTime:F2}s",
          Category.EmployeeCore);
      return travelTime;
    }

    public Dictionary<(Guid, Guid), float> GetTravelTimeCache(Guid source)
    {
      return (Dictionary<(Guid, Guid), float>)_travelTimeCache.Where(c => c.Key.Item1 == source || c.Key.Item2 == source);
    }

    /// <summary>
    /// Updates the travel time cache with actual movement time.
    /// </summary>
    /// <param name="sourceGuid">Source entity GUID.</param>
    /// <param name="destGuid">Destination entity GUID.</param>
    /// <param name="travelTime">Actual travel time (seconds).</param>
    public void UpdateTravelTimeCache(Guid sourceGuid, Guid destGuid, float travelTime)
    {
      if (travelTime <= 0)
      {
        Log(Level.Warning,
            $"UpdateTravelTimeCache: Invalid travel time {travelTime:F2}s for {sourceGuid} -> {destGuid}",
            Category.Storage);
        return;
      }

      var key = sourceGuid.CompareTo(destGuid) > 0 ? (sourceGuid, destGuid) : (destGuid, sourceGuid);
      _travelTimeCache[key] = travelTime;

      Log(Level.Info,
          $"UpdateTravelTimeCache: Updated {key.Item1} -> {key.Item2} to {travelTime:F2}s",
          Category.Storage);
    }

    /// <summary>
    /// Clears cache entries for a destroyed transit entity.
    /// </summary>
    /// <param name="entityGuid">GUID of the destroyed entity.</param>
    public void ClearCacheForEntity(Guid entityGuid)
    {
      Log(Level.Verbose,
          $"ClearCacheForEntity: Clearing cache for {entityGuid}",
                Category.EmployeeCore);

      _travelTimeEntities.Remove(entityGuid);

      var keysToRemove = _travelTimeCache.Keys
          .Where(k => k.Item1 == entityGuid || k.Item2 == entityGuid)
          .ToList();

      foreach (var key in keysToRemove)
      {
        _travelTimeCache.Remove(key);
        Log(Level.Verbose,
            $"ClearCacheForEntity: Removed {key.Item1} -> {key.Item2}",
            Category.EmployeeCore);
      }
    }

    /// <summary>
    /// Asynchronously retrieves property data.
    /// </summary>
    public async Task<(bool, List<IStationAdapter>, List<PlaceableStorageEntity>)> TryGetPropertyDataAsync()
    {
      await AwaitNextFishNetTickAsync();
      try
      {
        return TryGetPropertyData();
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"TryGetPropertyDataAsync: Failed for {_property.name} - {ex}", Category.Tasks);
        var stations = new List<IStationAdapter>();
        var storages = new List<PlaceableStorageEntity>();
        return (false, stations, storages);
      }
    }

    /// <summary>
    /// Retrieves cached station and storage data for a given property.
    /// </summary>
    /// <param name="property">The property to query.</param>
    /// <param name="stations">The list of station adapters if found; otherwise, null.</param>
    /// <param name="storages">The list of storage entities if found; otherwise, null.</param>
    /// <returns>True if the cache exists; otherwise, false.</returns>
    public (bool, List<IStationAdapter>, List<PlaceableStorageEntity>) TryGetPropertyData()
    {
      List<IStationAdapter> stations = [];
      List<PlaceableStorageEntity> storages = [];
      if (IStations.TryGetValue(_property, out var stationData))
        stations = stationData.Values.ToList();
      if (Storages.TryGetValue(_property, out var storageData))
        storages = storageData.Values.ToList();
      return (stations.Count > 0 || storages.Count > 0, stations, storages);
    }

    /// <summary>
    /// Updates the specific shelves cache to ensure Burst compatibility.
    /// </summary>
    public void UpdatePropertyDataCache()
    {
      var stations = IStations.TryGetValue(_property, out var stationList) ? stationList.Values.ToList() : new List<IStationAdapter>();
      var storages = Storages.TryGetValue(_property, out var storageDict) ? storageDict.Values.ToList() : new List<PlaceableStorageEntity>();

      // Dispose existing specific shelves cache
      if (SpecificShelvesCache.IsCreated)
      {
        foreach (var list in SpecificShelvesCache.GetValueArray(Allocator.Temp))
          list.Dispose();
        SpecificShelvesCache.Dispose();
      }

      // Rebuild specific shelves cache
      SpecificShelvesCache = new NativeParallelHashMap<ItemKey, NativeList<StorageKey>>(storages.Count, Allocator.Persistent);
      foreach (var storage in storages)
      {
        if (StorageConfigs.TryGetValue(_property, out var configs) &&
            configs.TryGetValue(storage.GUID, out var config) &&
            config.Mode == StorageMode.Specific &&
            config.AssignedItem != null)
        {
          var itemKey = new ItemKey(config.AssignedItem);
          if (!SpecificShelvesCache.TryGetValue(itemKey, out var storageKeys))
          {
            storageKeys = new NativeList<StorageKey>(Allocator.Persistent);
            SpecificShelvesCache[itemKey] = storageKeys;
          }
          storageKeys.Add(new StorageKey(storage.GUID, StorageTypes.SpecificShelf));
        }
      }

      Log(Level.Info,
          $"UpdatePropertyDataCache: Updated for property {_property.name}, stations={stations.Count}, storages={storages.Count}, specificShelves={SpecificShelvesCache.Count()}",
          Category.Storage);
    }

    /// <summary>
    /// Queues a station for cache update, triggering property data refresh and task revalidation.
    /// </summary>
    /// <param name="station">The station adapter to update.</param>
    public void QueueStationUpdate(IStationAdapter station)
    {
      if (station == null || station.Buildable?.ParentProperty == null)
      {
        Log(Level.Warning,
            "QueueStationUpdate: Invalid station or no parent property",
            Category.Storage);
        return;
      }

      _pendingStationUpdates.Enqueue(station);
      Log(Level.Verbose,
          $"QueueStationUpdate: Queued station {station.GUID} for property {station.Buildable.ParentProperty.name}",
          Category.Storage);
    }

    /// <summary>
    /// Queues a shelf for cache update, processed once per tick.
    /// </summary>
    /// <param name="shelf">The storage entity to update.</param>
    public void QueueStorageUpdate(PlaceableStorageEntity shelf)
    {
      if (shelf == null) return;
      _pendingUpdates.Enqueue(shelf);
      Log(Level.Verbose,
          $"QueueUpdate: Queued shelf {shelf.GUID}",
          Category.Storage);
    }

    /// <summary>
    /// Clears item not found cache entries for a specific item and property.
    /// </summary>
    /// <param name="item">The item to clear from the cache.</param>
    /// <param name="property">The property associated with the cache.</param>
    public void ClearItemNotFoundCache(ItemInstance item)
    {
      var cacheKey = Utilities.CreateCacheKey(item, _property);
      if (ItemNotFoundCache.TryRemove(cacheKey, out _))
      {
        Log(Level.Verbose,
            $"ClearItemNotFoundCache: Removed key={cacheKey.ID}{(cacheKey.Quality.HasValue ? $" Quality={cacheKey.Quality}" : "")}",
            Category.Storage);
      }
    }

    /// <summary>
    /// Processes pending shelf and station updates in a single tick, with deduplication and performance limits.
    /// </summary>
    private void ProcessPendingUpdates()
    {
      const long MAX_TIME_MS = 1;
      if (_isProcessingUpdates || (_pendingUpdates.IsEmpty && _pendingStationUpdates.IsEmpty))
        return;

      _isProcessingUpdates = true;
      int totalItems = _pendingUpdates.Count + _pendingStationUpdates.Count;
      Performance.TrackExecution(nameof(ProcessPendingUpdates), () =>
      {
        var processedShelves = new HashSet<Guid>();
        var processedProperties = new HashSet<string>();
        var stopwatch = Stopwatch.StartNew();
        int maxItems = GetDynamicBatchSize(totalItems, 0.15f, nameof(ProcessPendingUpdates));
        int processedCount = 0;

        try
        {
          // Process shelves
          while (processedCount < maxItems && stopwatch.ElapsedMilliseconds < MAX_TIME_MS &&
                 _pendingUpdates.TryDequeue(out var shelf))
          {
            if (shelf == null || !processedShelves.Add(shelf.GUID))
              continue;

            PerformStorageCacheUpdate(shelf);
            processedCount++;
          }

          // Process stations
          while (processedCount < maxItems && stopwatch.ElapsedMilliseconds < MAX_TIME_MS &&
                 _pendingStationUpdates.TryDequeue(out var station))
          {
            if (station == null || station.Buildable?.ParentProperty == null ||
                !processedProperties.Add(station.Buildable.ParentProperty.name))
              continue;

            var property = station.Buildable.ParentProperty;
            var stations = IStations.GetOrAdd(property, _ => new());
            var typeCountsBefore = stations.Values.GroupBy(s => s.TransitEntity.GetType())
                .ToDictionary(g => g.Key, g => g.Count());

            UpdatePropertyDataCache();

            if (stations.ContainsKey(station.GUID))
              stations[station.GUID] = station;
            else
              stations.Add(station.GUID, station);

            var typeCountsAfter = stations.Values.GroupBy(s => s.TransitEntity.GetType())
                .ToDictionary(g => g.Key, g => g.Count());

            bool needsRevalidation = typeCountsBefore.Keys.Concat(typeCountsAfter.Keys).Distinct()
                .Any(type => typeCountsBefore.GetValueOrDefault(type, 0) != typeCountsAfter.GetValueOrDefault(type, 0));

            // not needed anymore
            // TaskServiceManager.GetOrCreateService(property).EnqueueValidation(needsRevalidation ? 100 : 1);
            processedCount++;
            Log(Level.Info,
                $"ProcessPendingUpdates: Processed station {station.GUID} for property {property.name}",
                Category.Storage);
          }

          if (processedCount > 0)
          {
            double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
            DynamicProfiler.AddSample(nameof(ProcessPendingUpdates), avgItemTimeMs);
          }
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"ProcessPendingUpdates: Error - {ex}", Category.Storage);
        }
      }, itemCount: totalItems);

      _isProcessingUpdates = false;
    }

    /// <summary>
    /// Performs the storage cache update for a single shelf, starting a coroutine to process slots asynchronously.
    /// </summary>
    private void PerformStorageCacheUpdate(PlaceableStorageEntity shelf)
    {
      Log(Level.Verbose, $"PerformStorageCacheUpdate: Start for shelf={shelf.GUID}", Category.Storage);
      try
      {
        Performance.TrackExecution(nameof(PerformStorageCacheUpdate), () =>
        {
          CoroutineRunner.Instance.RunCoroutine(PerformStorageCacheUpdateCoroutine(shelf));
        });
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"PerformStorageCacheUpdate: Error for shelf={shelf.GUID} - {ex}", Category.Storage);
      }
    }

    /// <summary>
    /// Coroutine to process shelf slots in batches, yielding with AwaitNextTickAsync to align with FishNet ticks.
    /// </summary>
    private IEnumerator PerformStorageCacheUpdateCoroutine(PlaceableStorageEntity shelf)
    {
      Log(Level.Verbose, $"PerformStorageCacheUpdateCoroutine: Start for shelf={shelf.GUID}", Category.Storage);
      var outputSlots = shelf.OutputSlots.ToArray();
      var property = shelf.ParentProperty;
      var itemQuantities = _itemQuantitiesPool.Get();
      int batchSize = GetDynamicBatchSize(outputSlots.Length, 0.1f, nameof(PerformStorageCacheUpdateCoroutine));
      int processedCount = 0;
      var stopwatch = Stopwatch.StartNew(); // Track per-batch time

      for (int i = 0; i < outputSlots.Length; i++)
      {
        var slot = outputSlots[i];
        if (slot?.ItemInstance == null || slot.Quantity <= 0)
          continue;

        itemQuantities[slot.ItemInstance] = itemQuantities.GetValueOrDefault(slot.ItemInstance) + slot.Quantity;
        ClearItemNotFoundCache(slot.ItemInstance);
        Log(Level.Verbose,
            $"PerformStorageCacheUpdateCoroutine: Found {slot.Quantity} of {slot.ItemInstance.ID}",
            Category.Storage);

        processedCount++;
        if ((i + 1) % batchSize == 0)
        {
          // Update dynamic profiler for this batch
          if (processedCount > 0)
          {
            double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
            DynamicProfiler.AddSample(nameof(PerformStorageCacheUpdateCoroutine), avgItemTimeMs);
            stopwatch.Restart();
          }
          var tickTask = AwaitNextFishNetTickAsync();
          yield return new TaskYieldInstruction(tickTask);
          processedCount = 0;
        }
      }

      // Update profiler for remaining items
      if (processedCount > 0)
      {
        double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
        DynamicProfiler.AddSample(nameof(PerformStorageCacheUpdateCoroutine), avgItemTimeMs);
      }

      try
      {
        UpdateShelfCache(shelf, itemQuantities);
        UpdateAnyQualityCache(shelf, itemQuantities);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"PerformStorageCacheUpdateCoroutine: Error for shelf={shelf.GUID} - {ex}", Category.Storage);
      }
      finally
      {
        _itemQuantitiesPool.Release(itemQuantities);
        Log(Level.Info,
            $"PerformStorageCacheUpdateCoroutine: Completed for shelf={shelf.GUID}, items={itemQuantities.Count}",
            Category.Storage);
      }
    }

    /// <summary>
    /// Clears zero-quantity entries for a specific shelf, optionally for specific items.
    /// </summary>
    /// <param name="shelf">The shelf to clear entries for.</param>
    /// <param name="itemsToRemove">Optional list of items to remove; if null, clears all zero-quantity entries.</param>
    public void ClearZeroQuantityEntries(PlaceableStorageEntity shelf, IEnumerable<ItemInstance> itemsToRemove = null)
    {
      if (shelf == null) return;

      var keysToRemove = _shelfSearchCache
          .Where(kvp => kvp.Value.Any(r => r.Key == shelf && (itemsToRemove == null || r.Value <= 0) &&
              (itemsToRemove == null || itemsToRemove.Any(item => item.AdvCanStackWith(kvp.Key.Item)))))
          .Select(kvp => kvp.Key)
          .ToList();

      foreach (var key in keysToRemove)
      {
        _shelfSearchCache.AddOrUpdate(
            key,
            _ => _shelfListPool.Get(),
            (_, list) =>
            {
              list.RemoveAll(r => r.Key == shelf);
              return list;
            });

        if (!_shelfSearchCache[key].Any())
        {
          if (_shelfSearchCache.TryRemove(key, out var removedList))
            _shelfListPool.Release(removedList);
        }

        Log(Level.Verbose,
            $"ClearZeroQuantityEntries: Removed shelf={shelf.GUID} for key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}",
            Category.Storage);
      }
    }

    /// <summary>
    /// Checks if an item is marked as not found in the cache.
    /// </summary>
    /// <param name="key">The cache key to check.</param>
    /// <returns>True if the item is not found; otherwise, false.</returns>
    public bool IsItemNotFound(CacheKey key)
    {
      bool found = ItemNotFoundCache.ContainsKey(key);
      Log(Level.Verbose,
          $"IsItemNotFound: {(found ? "Hit" : "Miss")}",
          Category.Storage);
      return found;
    }

    /// <summary>
    /// Marks an item as not found in the cache.
    /// </summary>
    /// <param name="key">The cache key to mark.</param>
    public void AddItemNotFound(CacheKey key)
    {
      ItemNotFoundCache.TryAdd(key, true);
      Log(Level.Verbose,
          $"AddItemNotFound: Added key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}",
          Category.Storage);
    }

    /// <summary>
    /// Retrieves cached shelves for an item.
    /// </summary>
    /// <param name="key">The cache key to query.</param>
    /// <param name="shelves">The list of shelves and quantities if found; otherwise, null.</param>
    /// <returns>True if the cache exists; otherwise, false.</returns>
    public bool TryGetCachedShelves(CacheKey key, out List<KeyValuePair<PlaceableStorageEntity, int>> shelves)
    {
      bool found = _shelfSearchCache.TryGetValue(key, out var shelfList);
      shelves = found ? _shelfListPool.Get() : null;
      if (found && shelfList != null)
      {
        shelves.AddRange(shelfList);
        Performance.TrackCacheAccess("ShelfSearchCache", true);
      }
      else
      {
        Performance.TrackCacheAccess("ShelfSearchCache", false);
      }
      Log(Level.Verbose,
          $"TryGetCachedShelves: {(found ? "Hit" : "Miss")} for key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}, shelves={shelves?.Count ?? 0}",
          Category.Storage);
      return found;
    }

    /// <summary>
    /// Updates the shelf search cache with a new quantity.
    /// </summary>
    /// <param name="key">The cache key to update.</param>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="quantity">The new quantity.</param>
    public void UpdateShelfSearchCache(CacheKey key, PlaceableStorageEntity shelf, int quantity)
    {
      if (quantity <= 0)
      {
        Log(Level.Verbose,
            $"UpdateShelfSearchCache: Skipping update for key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}, shelf={shelf.GUID}, qty={quantity}",
            Category.Storage);
        return;
      }

      _shelfSearchCache.AddOrUpdate(
          key,
          _ =>
          {
            var list = _shelfListPool.Get();
            list.Add(new KeyValuePair<PlaceableStorageEntity, int>(shelf, quantity));
            return list;
          },
          (_, list) =>
          {
            list.RemoveAll(kvp => kvp.Key == shelf);
            list.Add(new KeyValuePair<PlaceableStorageEntity, int>(shelf, quantity));
            return list;
          });

      Log(Level.Verbose,
          $"UpdateShelfSearchCache: Updated key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}, shelf={shelf.GUID}, qty={quantity}",
          Category.Storage);
    }

    /// <summary>
    /// Removes specified cache entries from the shelf search cache.
    /// </summary>
    /// <param name="keys">The cache keys to remove.</param>
    public void RemoveShelfSearchCacheEntries(IEnumerable<CacheKey> keys)
    {
      int count = 0;
      foreach (var key in keys)
      {
        if (_shelfSearchCache.TryRemove(key, out _))
          count++;
      }
      Log(Level.Verbose,
          $"RemoveShelfSearchCacheEntries: Removed {count} entries",
          Category.Storage);
    }

    /// <summary>
    /// Updates shelf cache with current quantities and removes obsolete search cache entries.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="itemQuantities">The current item quantities on the shelf.</param>
    /// <param name="property">The property containing the shelf.</param>
    internal void UpdateShelfCache(PlaceableStorageEntity shelf, Dictionary<ItemInstance, int> itemQuantities)
    {
      Log(Level.Verbose,
          $"UpdateShelfCache: Updating for shelf={shelf.GUID}, items={itemQuantities.Count}",
          Category.Storage);

      var cacheKeysToRemove = new List<CacheKey>();
      foreach (var kvp in _shelfCache)
      {
        if (kvp.Value.TryGetValue(shelf, out var shelfInfo) &&
            (!itemQuantities.ContainsKey(kvp.Key) || itemQuantities[kvp.Key] <= 0))
        {
          kvp.Value.TryRemove(shelf, out _);
          if (kvp.Value.IsEmpty)
            _shelfCache.TryRemove(kvp.Key, out _);
          cacheKeysToRemove.Add(Utilities.CreateCacheKey(kvp.Key, _property));
        }
      }

      foreach (var kvp in itemQuantities)
      {
        var item = kvp.Key;
        var qty = kvp.Value;
        if (qty <= 0)
          continue;

        var shelfDict = _shelfCache.GetOrAdd(item, _ => new ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>());
        bool isConfigured = StorageConfigs.TryGetValue(shelf.ParentProperty, out var configs) &&
                           configs.TryGetValue(shelf.GUID, out var config) &&
                           config.Mode == StorageMode.Specific &&
                           config.AssignedItem?.AdvCanStackWith(item) == true;
        shelfDict[shelf] = new ShelfInfo(qty, isConfigured);
        Log(Level.Verbose,
            $"UpdateShelfCache: Updated item={item.ID}, qty={qty}, configured={isConfigured} for shelf={shelf.GUID}",
            Category.Storage);
      }

      if (cacheKeysToRemove.Any())
        RemoveShelfSearchCacheEntries(cacheKeysToRemove);
    }

    /// <summary>
    /// Updates AnyQualityCache for a shelf.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="itemQuantities">The current item quantities on the shelf.</param>
    /// <param name="property">The property containing the shelf.</param>
    internal void UpdateAnyQualityCache(PlaceableStorageEntity shelf, Dictionary<ItemInstance, int> itemQuantities)
    {
      // Clean up existing entries
      foreach (var kvp in _anyQualityCache)
      {
        kvp.Value.RemoveAll(r => r.Key == shelf);
        if (!kvp.Value.Any())
          _anyQualityCache.TryRemove(kvp.Key, out _);
      }

      foreach (var kvp in _anyQualityCache)
      {
        kvp.Value.RemoveAll(r => r.Key == shelf);
        if (!kvp.Value.Any())
          _anyQualityCache.TryRemove(kvp.Key, out _);
      }

      // Rebuild for current items
      foreach (var item in itemQuantities.Keys.OfType<ProductItemInstance>())
      {
        var qualityThresholds = _anyQualityCache.Keys
            .Where(k => item.Quality > k)
            .ToList();
        foreach (var threshold in qualityThresholds)
        {
          var shelfInfo = new ShelfInfo(itemQuantities[item], _shelfCache[item][shelf].IsConfigured);
          _anyQualityCache[threshold].Add(new KeyValuePair<PlaceableStorageEntity, ShelfInfo>(shelf, shelfInfo));
          Log(Level.Verbose,
              $"UpdateAnyQualityCache: Added shelf={shelf.GUID} for quality>{threshold}",
              Category.Storage);
        }
      }
    }

    public static class CacheManagerPatches
    {
      [HarmonyPatch(typeof(Property))]
      public class PropertyPatch
      {
        [HarmonyPostfix]
        [HarmonyPatch("SetOwned")]
        public static void SetOwnedPostfix(Property __instance)
        {
          var cacheManager = GetOrCreateCacheManager(__instance);
          cacheManager.Activate();
        }
      }

      [HarmonyPatch(typeof(LoadingDock))]
      public class LoadingDockPatch
      {
        [HarmonyPostfix]
        [HarmonyPatch("SetOccupant")]
        public static void LoadingDock_SetOccupant_Postfix(LoadingDock __instance, LandVehicle occupant)
        {
          var storageKey = new StorageKey { Guid = __instance.GUID, Type = StorageTypes.LoadingDock };
          var cacheManager = GetOrCreateCacheManager(__instance.ParentProperty);
          cacheManager._loadingDockKeys[__instance.GUID] = storageKey;

          // Unregister previous slots
          foreach (var slot in __instance.OutputSlots)
            cacheManager.UnregisterItemSlot(slot, storageKey);

          // Register new slots
          foreach (var slot in __instance.OutputSlots) // Updated by SetOccupant
            cacheManager.RegisterItemSlot(slot, storageKey);

          // Queue slot update
          var slotData = __instance.OutputSlots.Select(s => new SlotData
          {
            Item = s.ItemInstance != null ? new ItemKey(s.ItemInstance) : ItemKey.Empty,
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
        [HarmonyPrefix]
        [HarmonyPatch("InitializeGridItem", new Type[] { typeof(ItemInstance), typeof(Grid), typeof(Vector2), typeof(int), typeof(string) })]
        static bool InitializeGridItemPrefix(GridItem __instance, ItemInstance instance, Grid grid, Vector2 originCoordinate, int rotation, string GUID)
        {
          try
          {
            if (!__instance.isGhost && GUID != "00000000-0000-0000-0000-000000000000" && __instance.GUID.ToString() != "00000000-0000-0000-0000-000000000000" && StorageInstanceIDs.All.Contains(instance.ID))
            {
              if (__instance is PlaceableStorageEntity entity)
              {
                Log(Level.Info, $"GridItemPatch: Initializing storage for GUID: {__instance.GUID}", Category.Storage);
                ShelfUtilities.InitializeStorage(__instance.GUID, entity);
              }
              else
              {
                Log(Level.Warning, $"GridItemPatch: {__instance.GUID} is not a PlaceableStorageEntity", Category.Storage);
              }
            }
            return true;
          }
          catch (Exception e)
          {
            Log(Level.Error, $"GridItemPatch: Failed for GUID: {GUID}, error: {e.Message}", Category.Storage);
            return true;
          }
        }

        [HarmonyPostfix]
        [HarmonyPatch("DestroyItem")]
        static void DestroyItemPostfix(GridItem __instance, bool callOnServer = true)
        {
          if (__instance is PlaceableStorageEntity storage)
          {
            if (StorageConfigs[__instance.ParentProperty].TryGetValue(__instance.GUID, out var config))
              config.Dispose();
            var cacheKeys = ShelfCache
                .Where(kvp => kvp.Value.ContainsKey(storage))
                .Select(kvp => Utilities.CreateCacheKey(kvp.Key, storage.ParentProperty))
                .ToList();
            GetOrCreateCacheManager(__instance.ParentProperty).RemoveShelfSearchCacheEntries(cacheKeys);
          }
          if (__instance is ITransitEntity)
            GetOrCreateCacheManager(__instance.ParentProperty).ClearCacheForEntity(__instance.GUID);
        }
      }

      [HarmonyPatch(typeof(ItemSlot))]
      public class ItemSlotPatch
      {
        [HarmonyPostfix]
        [HarmonyPatch("ChangeQuantity")]
        public static void ChangeQuantityPostfix(ItemSlot __instance, int change, bool _internal)
        {
          if (change == 0 || __instance.ItemInstance == null) return;
          var owner = __instance.SlotOwner as PlaceableStorageEntity;
          if (owner == null) return;

          Log(Level.Verbose,
              $"ItemSlotPatch.ChangeQuantityPostfix: Slot={__instance.SlotIndex}, item={__instance.ItemInstance.ID}{(__instance.ItemInstance is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}, change={change}, newQty={__instance.Quantity}, shelf={owner.GUID}",
              Category.Storage);

          var cacheManager = GetOrCreateCacheManager(owner.ParentProperty);
          if (change > 0)
            cacheManager.ClearItemNotFoundCache(__instance.ItemInstance);
          if (__instance.Quantity <= 0)
            cacheManager.ClearZeroQuantityEntries(owner, new[] { __instance.ItemInstance });
          Utilities.UpdateStorageCache(owner);
        }

        [HarmonyPostfix]
        [HarmonyPatch("SetStoredItem")]
        public static void SetStoredItemPostfix(ItemSlot __instance, ItemInstance instance, bool _internal)
        {
          var owner = __instance.SlotOwner as PlaceableStorageEntity;
          if (owner == null || instance == null) return;

          Log(Level.Verbose,
              $"ItemSlotPatch.SetStoredItemPostfix: Slot={__instance.SlotIndex}, item={(instance?.ID ?? "null")}{(instance is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}, shelf={owner.GUID}",
              Category.Storage);

          GetOrCreateCacheManager(owner.ParentProperty).ClearItemNotFoundCache(instance);
          Utilities.UpdateStorageCache(owner);
        }

        [HarmonyPostfix]
        [HarmonyPatch("ClearStoredInstance")]
        public static void ClearStoredInstancePostfix(ItemSlot __instance, bool _internal)
        {
          var owner = __instance.SlotOwner as PlaceableStorageEntity;
          if (owner == null) return;

          Log(Level.Verbose,
              $"ItemSlotPatch.ClearStoredInstancePostfix: Slot={__instance.SlotIndex}, shelf={owner.GUID}",
              Category.Storage);

          Utilities.UpdateStorageCache(owner);
          if (__instance.ItemInstance != null)
            GetOrCreateCacheManager(owner.ParentProperty).ClearZeroQuantityEntries(owner, new[] { __instance.ItemInstance });
        }
      }
    }
  }
}