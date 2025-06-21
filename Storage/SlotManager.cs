using FishNet.Object;
using ScheduleOne.ItemFramework;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using Unity.Collections;
using UnityEngine.Pool;
using NoLazyWorkers.Performance;
using System.Collections;
using HarmonyLib;
using ScheduleOne.UI;
using static NoLazyWorkers.Debug;
using Unity.Burst;
using Unity.Jobs;
using static NoLazyWorkers.Debug.Deferred;

namespace NoLazyWorkers.Storage
{
  /// <summary>
  /// Represents a unique key for a storage slot, combining entity GUID and slot index.
  /// </summary>
  public struct SlotKey : IEquatable<SlotKey>
  {
    /// <summary>
    /// The unique identifier of the entity owning the slot.
    /// </summary>
    public Guid EntityGuid;

    /// <summary>
    /// The index of the slot within the entity.
    /// </summary>
    public int SlotIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="SlotKey"/> struct.
    /// </summary>
    /// <param name="entityGuid">The unique identifier of the entity.</param>
    /// <param name="slotIndex">The index of the slot.</param>
    public SlotKey(Guid entityGuid, int slotIndex)
    {
      EntityGuid = entityGuid;
      SlotIndex = slotIndex;
    }

    /// <summary>
    /// Determines whether the specified <see cref="SlotKey"/> is equal to the current <see cref="SlotKey"/>.
    /// </summary>
    /// <param name="other">The <see cref="SlotKey"/> to compare with the current object.</param>
    /// <returns>True if the specified <see cref="SlotKey"/> is equal to the current <see cref="SlotKey"/>; otherwise, false.</returns>
    public bool Equals(SlotKey other) => EntityGuid == other.EntityGuid && SlotIndex == other.SlotIndex;

    /// <summary>
    /// Determines whether the specified object is equal to the current <see cref="SlotKey"/>.
    /// </summary>
    /// <param name="obj">The object to compare with the current object.</param>
    /// <returns>True if the specified object is equal to the current <see cref="SlotKey"/>; otherwise, false.</returns>
    public override bool Equals(object obj) => obj is SlotKey other && Equals(other);

    /// <summary>
    /// Returns a hash code for the current <see cref="SlotKey"/>.
    /// </summary>
    /// <returns>A hash code for the current <see cref="SlotKey"/>.</returns>
    public override int GetHashCode() => HashCode.Combine(EntityGuid, SlotIndex);
  }

  /// <summary>
  /// Represents a reservation for a storage slot, including locking details and item information.
  /// </summary>
  public struct SlotReservation
  {
    /// <summary>
    /// The unique identifier of the entity owning the slot.
    /// </summary>
    public Guid EntityGuid;

    /// <summary>
    /// The timestamp when the slot was reserved.
    /// </summary>
    public float Timestamp;

    /// <summary>
    /// The identifier of the locker reserving the slot.
    /// </summary>
    public FixedString32Bytes Locker;

    /// <summary>
    /// The reason for locking the slot.
    /// </summary>
    public FixedString128Bytes LockReason;

    /// <summary>
    /// The item key associated with the reservation.
    /// </summary>
    public ItemKey Item;

    /// <summary>
    /// The quantity of items reserved in the slot.
    /// </summary>
    public int Quantity;
  }

  /// <summary>
  /// Represents the result of a coroutine operation, encapsulating a value of type T.
  /// </summary>
  /// <typeparam name="T">The type of the result value.</typeparam>
  public struct CoroutineResult<T>
  {
    /// <summary>
    /// The result value of the coroutine.
    /// </summary>
    public T Value;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoroutineResult{T}"/> struct.
    /// </summary>
    /// <param name="value">The result value.</param>
    public CoroutineResult(T value) => Value = value;
  }

  /// <summary>
  /// Manages storage slots, including reservation, operation execution, and slot availability checks.
  /// </summary>
  public static class SlotManager
  {
    /// <summary>
    /// Cache of network objects used during slot operations.
    /// </summary>
    private static List<NetworkObject> NetworkObjectCache;

    /// <summary>
    /// A thread-safe hash map storing slot reservations.
    /// </summary>
    private static NativeParallelHashMap<SlotKey, SlotReservation> Reservations { get; set; }

    /// <summary>
    /// Object pool for managing lists of slot operations.
    /// </summary>
    private static readonly ObjectPool<List<SlotOperation>> _operationPool = new ObjectPool<List<SlotOperation>>(
        createFunc: () => new List<SlotOperation>(10),
        actionOnGet: null,
        actionOnRelease: list => list.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);

    /// <summary>
    /// Represents a single slot operation (insert or remove).
    /// </summary>
    private struct SlotOperation
    {
      /// <summary>
      /// The unique identifier of the entity owning the slot.
      /// </summary>
      public Guid EntityGuid;

      /// <summary>
      /// The key identifying the slot.
      /// </summary>
      public SlotKey SlotKey;

      /// <summary>
      /// The slot being operated on.
      /// </summary>
      public ItemSlot Slot;

      /// <summary>
      /// The item involved in the operation.
      /// </summary>
      public ItemInstance Item;

      /// <summary>
      /// The quantity of items to insert or remove.
      /// </summary>
      public int Quantity;

      /// <summary>
      /// Indicates whether the operation is an insert (true) or remove (false).
      /// </summary>
      public bool IsInsert;

      /// <summary>
      /// The network object locking the slot.
      /// </summary>
      public NetworkObject Locker;

      /// <summary>
      /// The reason for locking the slot.
      /// </summary>
      public string LockReason;
    }

    /// <summary>
    /// A job that finds available slots for an item.
    /// </summary>
    [BurstCompile]
    private struct AvailableSlotsJob : IJob
    {
      /// <summary>
      /// The array of slot data to process.
      /// </summary>
      [ReadOnly] public NativeArray<SlotData> Slots;

      /// <summary>
      /// The item key to check for availability.
      /// </summary>
      [ReadOnly] public ItemKey Item;

      /// <summary>
      /// The quantity of items to allocate.
      /// </summary>
      public int Quantity;

      /// <summary>
      /// The list of results containing available slots and their capacities.
      /// </summary>
      public NativeList<SlotResult> Results;

      /// <summary>
      /// The list of logs generated during execution.
      /// </summary>
      public NativeList<LogEntry> Logs;

      /// <summary>
      /// Executes the job, finding available slots for the specified item and quantity.
      /// </summary>
      public void Execute()
      {
        int remainingQty = Quantity;
        for (int i = 0; i < Slots.Length; i++)
        {
          var slot = Slots[i];
          if (!slot.IsValid || slot.IsLocked) continue;
          int capacity = SlotProcessingUtility.GetCapacityForItem(slot, Item);
          if (capacity <= 0) continue;
          int amount = Mathf.Min(capacity, remainingQty);
          Results.Add(new SlotResult { SlotIndex = slot.SlotIndex, Capacity = amount });
          Logs.Add(new LogEntry
          {
            Message = $"Found available slot {slot.SlotIndex} with capacity {amount}",
            Level = Level.Verbose,
            Category = Category.Tasks
          });
          remainingQty -= amount;
          if (remainingQty <= 0) break;
        }
      }
    }

    /// <summary>
    /// A job that processes slot operations (insert or remove).
    /// </summary>
    [BurstCompile]
    private struct SlotOperationsJob : IJob
    {
      /// <summary>
      /// The array of operations to process.
      /// </summary>
      [ReadOnly] public NativeArray<OperationData> Operations;

      /// <summary>
      /// The list of results from processing the operations.
      /// </summary>
      public NativeList<SlotOperationResult> Results;

      /// <summary>
      /// The list of logs generated during execution.
      /// </summary>
      public NativeList<LogEntry> Logs;

      /// <summary>
      /// Executes the job, validating and processing slot operations.
      /// </summary>
      public void Execute()
      {
        for (int i = 0; i < Operations.Length; i++)
        {
          var op = Operations[i];
          if (!op.IsValid || op.Quantity <= 0)
          {
            Logs.Add(new LogEntry
            {
              Message = $"Invalid operation for slot {op.SlotKey.SlotIndex}",
              Level = Level.Warning,
              Category = Category.Tasks
            });
            Results.Add(new SlotOperationResult { IsValid = false });
            continue;
          }
          bool canProcess = op.IsInsert
              ? SlotProcessingUtility.CanInsert(op.Slot, op.Item, op.Quantity)
              : SlotProcessingUtility.CanRemove(op.Slot, op.Item, op.Quantity);
          if (!canProcess)
          {
            Logs.Add(new LogEntry
            {
              Message = $"{(op.IsInsert ? "Insert" : "Remove")} failed for {op.Quantity} of {op.Item.Id} in slot {op.SlotKey.SlotIndex}",
              Level = Level.Warning,
              Category = Category.Tasks
            });
            Results.Add(new SlotOperationResult { IsValid = false });
            continue;
          }
          Results.Add(new SlotOperationResult
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
    }

    /// <summary>
    /// Represents the result of a slot availability check.
    /// </summary>
    private struct SlotResult
    {
      /// <summary>
      /// The index of the slot.
      /// </summary>
      public int SlotIndex;

      /// <summary>
      /// The available capacity in the slot.
      /// </summary>
      public int Capacity;
    }

    /// <summary>
    /// Represents the data for a single slot operation.
    /// </summary>
    private struct OperationData
    {
      /// <summary>
      /// The key identifying the slot.
      /// </summary>
      public SlotKey SlotKey;

      /// <summary>
      /// The slot data.
      /// </summary>
      public SlotData Slot;

      /// <summary>
      /// The item key involved in the operation.
      /// </summary>
      public ItemKey Item;

      /// <summary>
      /// The quantity of items to process.
      /// </summary>
      public int Quantity;

      /// <summary>
      /// Indicates whether the operation is an insert (true) or remove (false).
      /// </summary>
      public bool IsInsert;

      /// <summary>
      /// The identifier of the locker.
      /// </summary>
      public int LockerId;

      /// <summary>
      /// The reason for locking the slot.
      /// </summary>
      public FixedString128Bytes LockReason;

      /// <summary>
      /// Gets a value indicating whether the operation is valid.
      /// </summary>
      public bool IsValid => Slot.IsValid && LockerId != 0 && Quantity > 0;
    }

    /// <summary>
    /// Represents the result of a slot operation.
    /// </summary>
    private struct SlotOperationResult
    {
      /// <summary>
      /// Indicates whether the operation was valid.
      /// </summary>
      public bool IsValid;

      /// <summary>
      /// The unique identifier of the entity.
      /// </summary>
      public Guid EntityGuid;

      /// <summary>
      /// The index of the slot.
      /// </summary>
      public int SlotIndex;

      /// <summary>
      /// The item key involved in the operation.
      /// </summary>
      public ItemKey Item;

      /// <summary>
      /// The quantity of items processed.
      /// </summary>
      public int Quantity;

      /// <summary>
      /// Indicates whether the operation was an insert (true) or remove (false).
      /// </summary>
      public bool IsInsert;

      /// <summary>
      /// The identifier of the locker.
      /// </summary>
      public int LockerId;

      /// <summary>
      /// The reason for locking the slot.
      /// </summary>
      public FixedString128Bytes LockReason;
    }

    /// <summary>
    /// Initializes the SlotManager, setting up the reservations hash map and logging initialization.
    /// </summary>
    public static void Initialize()
    {
      Reservations = new NativeParallelHashMap<SlotKey, SlotReservation>(1000, Allocator.Persistent);
#if DEBUG
      Log(Level.Info, "SlotManager initialized", Category.Tasks);
#endif
    }

    /// <summary>
    /// Retrieves the <see cref="ItemSlot"/> for the specified entity GUID and slot index from a list of operations.
    /// </summary>
    /// <param name="entityGuid">The unique identifier of the entity.</param>
    /// <param name="slotIndex">The index of the slot.</param>
    /// <param name="operations">The list of operations containing slot information.</param>
    /// <returns>The <see cref="ItemSlot"/> if found; otherwise, null.</returns>
    private static ItemSlot GetItemSlot(Guid entityGuid, int slotIndex, List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)> operations)
    {
      var operation = operations.FirstOrDefault(op => op.Item1 == entityGuid && op.Item2.SlotIndex == slotIndex);
      return operation.Item2; // Returns null if not found, handled by caller
    }

    /// <summary>
    /// Finds available slots for an item, using SmartExecution to choose between job, coroutine, or main-thread processing.
    /// </summary>
    /// <param name="slots">The list of slots to check.</param>
    /// <param name="item">The item to allocate.</param>
    /// <param name="quantity">The quantity to allocate.</param>
    /// <returns>A list of tuples containing available slots and their capacities.</returns>
    public static List<(ItemSlot Slot, int Capacity)> FindAvailableSlots(List<ItemSlot> slots, ItemInstance item, int quantity)
    {
      if (slots == null || item == null || quantity <= 0)
      {
#if DEBUG
        Log(Level.Warning, $"FindAvailableSlots: Invalid input (slots={slots?.Count ?? 0}, item={item != null}, qty={quantity})", Category.Tasks);
#endif
        return new List<(ItemSlot, int)>();
      }

      List<(ItemSlot, int)> result = null;
      CoroutineRunner.Instance.RunCoroutineWithResult<List<(ItemSlot, int)>>(
          Metrics.TrackExecutionCoroutine(
              nameof(FindAvailableSlots),
              FindAvailableSlotsCoroutine(slots, item, quantity),
              slots.Count),
          success => result = success);
      return result ?? new List<(ItemSlot, int)>();
    }

    /// <summary>
    /// Coroutine that orchestrates finding available slots, sharing results across execution paths.
    /// </summary>
    /// <param name="slots">The list of slots to check.</param>
    /// <param name="item">The item to allocate.</param>
    /// <param name="quantity">The quantity to allocate.</param>
    /// <returns>An <see cref="IEnumerator"/> yielding the list of available slots and their capacities.</returns>
    private static IEnumerator FindAvailableSlotsCoroutine(List<ItemSlot> slots, ItemInstance item, int quantity)
    {
#if DEBUG
      Log(Level.Verbose, $"FindAvailableSlotsCoroutine: Started for item={item.ID}, qty={quantity}, slots={slots.Count}", Category.Tasks);
#endif

      var results = new List<(ItemSlot, int Capacity)>();
      yield return SmartExecution.Execute(
          uniqueId: $"{nameof(FindAvailableSlotsCoroutine)}_ItemSlot",
          itemCount: slots.Count,
          jobCallback: () =>
          {
            var slotData = new NativeArray<SlotData>(slots.Count, Allocator.TempJob);
            var jobResults = new NativeList<SlotResult>(slots.Count, Allocator.TempJob);
            var logs = new NativeList<LogEntry>(slots.Count, Allocator.TempJob);
            try
            {
              for (int i = 0; i < slots.Count; i++)
                slotData[i] = new SlotData(slots[i]);
              var job = new AvailableSlotsJob
              {
                Slots = slotData,
                Item = new ItemKey(item),
                Quantity = quantity,
                Results = jobResults,
                Logs = logs
              };
              var handle = job.Schedule();
              handle.Complete();
              foreach (var res in jobResults)
                results.Add((slots[res.SlotIndex], res.Capacity));
              ProcessLogs(logs);
            }
            finally
            {
              slotData.Dispose();
              jobResults.Dispose();
              logs.Dispose();
            }
          },
          coroutineCallback: () => FindAvailableSlotsCoroutineImpl(slots, item, quantity, results),
          mainThreadCallback: () =>
          {
            int remainingQty = quantity;
            foreach (var slot in slots)
            {
              if (slot == null || slot.IsLocked) continue;
              int capacity = slot.GetCapacityForItem(item);
              if (capacity <= 0) continue;
              int amount = Mathf.Min(capacity, remainingQty);
              results.Add((slot, amount));
              remainingQty -= amount;
#if DEBUG
              Log(Level.Verbose, $"Found available slot {slot.SlotIndex} with capacity {amount}", Category.Tasks);
#endif
              if (remainingQty <= 0) break;
            }
          }
      );

#if DEBUG
      Log(Level.Info, $"FindAvailableSlotsCoroutine: Found {results.Count} slots, remaining qty={quantity - results.Sum(r => r.Capacity)}", Category.Tasks);
#endif
      yield return new CoroutineResult<List<(ItemSlot, int)>>(results);
    }

    /// <summary>
    /// Coroutine implementation for finding available slots, processing in batches across frames.
    /// </summary>
    /// <param name="slots">The list of slots to check.</param>
    /// <param name="item">The item to allocate.</param>
    /// <param name="quantity">The quantity to allocate.</param>
    /// <param name="results">The shared list to store results.</param>
    /// <returns>An <see cref="IEnumerator"/> yielding the results.</returns>
    private static IEnumerator FindAvailableSlotsCoroutineImpl(List<ItemSlot> slots, ItemInstance item, int quantity, List<(ItemSlot, int)> results)
    {
      int remainingQty = quantity;
      int batchSize = FishNetExtensions.GetDynamicBatchSize(slots.Count, 0.1f, nameof(FindAvailableSlotsCoroutine));
      for (int i = 0; i < slots.Count; i += batchSize)
      {
        int end = Mathf.Min(i + batchSize, slots.Count);
        for (int j = i; j < end; j++)
        {
          var slot = slots[j];
          if (slot == null || slot.IsLocked) continue;
          int capacity = slot.GetCapacityForItem(item);
          if (capacity <= 0) continue;
          int amount = Mathf.Min(capacity, remainingQty);
          results.Add((slot, amount));
          remainingQty -= amount;
#if DEBUG
          Log(Level.Verbose, $"Found available slot {slot.SlotIndex} with capacity {amount}", Category.Tasks);
#endif
          if (remainingQty <= 0) break;
        }
        yield return null;
      }
      yield return new CoroutineResult<List<(ItemSlot, int)>>(results);
    }

    /// <summary>
    /// Reserves a slot for an item, applying a lock and storing reservation details.
    /// </summary>
    /// <param name="entityGuid">The unique identifier of the entity.</param>
    /// <param name="slot">The slot to reserve.</param>
    /// <param name="locker">The network object locking the slot.</param>
    /// <param name="lockReason">The reason for locking the slot.</param>
    /// <param name="item">The item to reserve (optional).</param>
    /// <param name="quantity">The quantity to reserve (optional).</param>
    /// <returns>True if the slot was reserved successfully; otherwise, false.</returns>
    public static bool ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)
    {
      if (slot == null || locker == null)
      {
#if DEBUG
        Log(Level.Warning, $"ReserveSlot: Invalid slot or locker", Category.Tasks);
#endif
        return false;
      }

      var slotKey = new SlotKey(entityGuid, slot.SlotIndex);
      if (Reservations.ContainsKey(slotKey))
      {
#if DEBUG
        Log(Level.Verbose, $"ReserveSlot: Slot {slotKey} already reserved", Category.Tasks);
#endif
        return false;
      }

      slot.ApplyLock(locker, lockReason);
      Reservations.Add(slotKey, new SlotReservation
      {
        EntityGuid = entityGuid,
        Timestamp = Time.time,
        Locker = locker.ToString(),
        LockReason = lockReason,
        Item = new ItemKey(item?.GetCopy()),
        Quantity = quantity
      });

#if DEBUG
      Log(Level.Verbose, $"Reserved slot {slotKey} for entity {entityGuid}, reason: {lockReason}", Category.Tasks);
#endif
      return true;
    }

    /// <summary>
    /// Releases a reserved slot, removing the lock and reservation.
    /// </summary>
    /// <param name="slot">The slot to release.</param>
    public static void ReleaseSlot(ItemSlot slot)
    {
      if (slot == null) return;
      var slotKey = Reservations.FirstOrDefault(s => s.Key.SlotIndex == slot.SlotIndex);
      if (slotKey.Key.SlotIndex == slot.SlotIndex)
      {
        Reservations.Remove(slotKey.Key);
        slot.RemoveLock();
#if DEBUG
        Log(Level.Verbose, $"Released slot {slotKey}", Category.Tasks);
#endif
      }
    }

    /// <summary>
    /// Executes a list of slot operations (insert or remove) and manages reservations.
    /// </summary>
    /// <param name="operations">The list of operations to execute.</param>
    /// <returns>True if all operations were successful; otherwise, false.</returns>
    public static bool ExecuteSlotOperations(List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations)
    {
      foreach (var operation in operations)
        NetworkObjectCache.Add(operation.Locker);
      bool result = false;
      CoroutineRunner.Instance.RunCoroutineWithResult<bool>(
          Metrics.TrackExecutionCoroutine(
              nameof(ExecuteSlotOperations),
              ExecuteSlotOperationsCoroutine(operations),
              operations.Count),
          success => result = success);
      foreach (var operation in operations)
        NetworkObjectCache.Remove(operation.Locker);
      return result;
    }

    /// <summary>
    /// Retrieves a network object by its ID from the cache.
    /// </summary>
    /// <param name="id">The ID of the network object.</param>
    /// <returns>The <see cref="NetworkObject"/> if found; otherwise, null.</returns>
    private static NetworkObject GetNetworkObject(int id)
    {
      return NetworkObjectCache.First(n => n.ObjectId == id);
    }

    /// <summary>
    /// Coroutine that orchestrates the execution of slot operations.
    /// </summary>
    /// <param name="operations">The list of operations to execute.</param>
    /// <returns>An <see cref="IEnumerator"/> yielding the success status of the operations.</returns>
    private static IEnumerator ExecuteSlotOperationsCoroutine(List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations)
    {
#if DEBUG
      Log(Level.Verbose, $"ExecuteSlotOperationsCoroutine: Started with {operations?.Count ?? 0} operations", Category.Tasks);
#endif

      if (operations == null || operations.Count == 0)
      {
#if DEBUG
        Log(Level.Warning, $"ExecuteSlotOperationsCoroutine: No operations provided", Category.Tasks);
#endif
        yield return new CoroutineResult<bool>(false);
        yield break;
      }

      var opList = _operationPool.Get();
      bool success = false;
      try
      {
        yield return SmartExecution.Execute(
            uniqueId: $"{nameof(ExecuteSlotOperationsCoroutine)}_SlotOperation",
            itemCount: operations.Count,
            jobCallback: () =>
            {
              var opData = new NativeArray<OperationData>(operations.Count, Allocator.TempJob);
              var results = new NativeList<SlotOperationResult>(operations.Count, Allocator.TempJob);
              var logs = new NativeList<LogEntry>(operations.Count, Allocator.TempJob);
              try
              {
                for (int i = 0; i < operations.Count; i++)
                {
                  var op = operations[i];
                  opData[i] = new OperationData
                  {
                    SlotKey = new SlotKey(op.EntityGuid, op.Slot.SlotIndex),
                    Slot = new SlotData(op.Slot),
                    Item = new ItemKey(op.Item),
                    Quantity = op.Quantity,
                    IsInsert = op.IsInsert,
                    LockerId = op.Locker != null ? op.Locker.ObjectId : 0,
                    LockReason = op.LockReason
                  };
                }
                var job = new SlotOperationsJob
                {
                  Operations = opData,
                  Results = results,
                  Logs = logs
                };
                var handle = job.Schedule();
                handle.Complete();
                if (results.Any(r => !r.IsValid))
                {
                  ProcessLogs(logs);
                  return;
                }
                foreach (var res in results)
                {
                  var slot = GetItemSlot(res.EntityGuid, res.SlotIndex, operations);
                  if (slot == null)
                  {
                    Log(Level.Warning, $"Failed to resolve slot {res.SlotIndex} for operation", Category.Tasks);
                    return;
                  }
                  var locker = GetNetworkObject(res.LockerId);
                  if (locker == null)
                  {
                    Log(Level.Warning, $"Failed to resolve NetworkObject for LockerId {res.LockerId}", Category.Tasks);
                    return;
                  }
                  opList.Add(new SlotOperation
                  {
                    EntityGuid = res.EntityGuid,
                    SlotKey = new SlotKey(res.EntityGuid, res.SlotIndex),
                    Slot = slot,
                    Item = res.Item.CreateItemInstance(),
                    Quantity = res.Quantity,
                    IsInsert = res.IsInsert,
                    Locker = locker,
                    LockReason = res.LockReason.ToString()
                  });
                }
                success = ProcessOperations(opList);
                ProcessLogs(logs);
              }
              finally
              {
                opData.Dispose();
                results.Dispose();
                logs.Dispose();
              }
            },
            coroutineCallback: () => ExecuteSlotOperationsCoroutineImpl(operations, opList),
            mainThreadCallback: () =>
            {
              foreach (var op in operations)
              {
                if (!SlotProcessingUtility.ValidateOperation(op.Slot, op.Item, op.Quantity, op.IsInsert, out var log))
                {
                  Log(log.Level, log.Message.ToString(), log.Category);
                  return;
                }
                opList.Add(new SlotOperation
                {
                  EntityGuid = op.EntityGuid,
                  SlotKey = new SlotKey(op.EntityGuid, op.Slot.SlotIndex),
                  Slot = op.Slot,
                  Item = op.Item,
                  Quantity = op.Quantity,
                  IsInsert = op.IsInsert,
                  Locker = op.Locker,
                  LockReason = op.LockReason
                });
              }
              success = ProcessOperations(opList);
            }
        );

        if (success) // Early exit if main-thread or job callback set success
          yield return new CoroutineResult<bool>(success);
      }
      finally
      {
        if (!success)
        {
          foreach (var op in opList)
            ReleaseSlot(op.Slot);
        }
        _operationPool.Release(opList);
      }

#if DEBUG
      Log(Level.Info, $"ExecuteSlotOperationsCoroutine: Completed with success={success}", Category.Tasks);
#endif
      yield return new CoroutineResult<bool>(success);
    }

    /// <summary>
    /// Coroutine implementation for executing slot operations in batches.
    /// </summary>
    /// <param name="operations">The list of operations to execute.</param>
    /// <param name="opList">The list to store processed operations.</param>
    /// <returns>An <see cref="IEnumerator"/> yielding the success status of the operations.</returns>
    private static IEnumerator ExecuteSlotOperationsCoroutineImpl(
        List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations,
        List<SlotOperation> opList)
    {
      int batchSize = FishNetExtensions.GetDynamicBatchSize(operations.Count, 0.1f, nameof(ExecuteSlotOperationsCoroutine));
      for (int i = 0; i < operations.Count; i += batchSize)
      {
        int end = Mathf.Min(i + batchSize, operations.Count);
        for (int j = i; j < end; j++)
        {
          var op = operations[j];
          if (!SlotProcessingUtility.ValidateOperation(op.Slot, op.Item, op.Quantity, op.IsInsert, out var log))
          {
            Log(log.Level, log.Message.ToString(), log.Category);
            yield return new CoroutineResult<bool>(false);
            yield break;
          }
          opList.Add(new SlotOperation
          {
            EntityGuid = op.EntityGuid,
            SlotKey = new SlotKey(op.EntityGuid, op.Slot.SlotIndex),
            Slot = op.Slot,
            Item = op.Item,
            Quantity = op.Quantity,
            IsInsert = op.IsInsert,
            Locker = op.Locker,
            LockReason = op.LockReason
          });
        }
        yield return null;
      }
      bool success = ProcessOperations(opList);
      yield return new CoroutineResult<bool>(success);
    }

    /// <summary>
    /// Processes a list of slot operations, reserving slots and performing inserts or removals.
    /// </summary>
    /// <param name="opList">The list of operations to process.</param>
    /// <returns>True if all operations were successful; otherwise, false.</returns>
    private static bool ProcessOperations(List<SlotOperation> opList)
    {
      foreach (var op in opList)
      {
        if (!ReserveSlot(op.EntityGuid, op.Slot, op.Locker, op.LockReason, op.Item, op.Quantity))
        {
          foreach (var reservedOp in opList.Take(opList.IndexOf(op)))
            ReleaseSlot(reservedOp.Slot);
          return false;
        }
      }
      foreach (var op in opList)
      {
        if (op.IsInsert)
        {
          op.Slot.InsertItem(op.Item.GetCopy(op.Quantity));
#if DEBUG
          Log(Level.Verbose, $"Inserted {op.Quantity} of {op.Item.ID} into slot {op.SlotKey}", Category.Tasks);
#endif
        }
        else
        {
          op.Slot.ChangeQuantity(-op.Quantity);
#if DEBUG
          Log(Level.Verbose, $"Removed {op.Quantity} of {op.Item.ID} from slot {op.SlotKey}", Category.Tasks);
#endif
        }
      }
      foreach (var op in opList)
        ReleaseSlot(op.Slot);
      return true;
    }

    /// <summary>
    /// Cleans up the SlotManager, disposing of the reservations hash map and clearing the operation pool.
    /// </summary>
    public static void Cleanup()
    {
      if (Reservations.IsCreated)
        Reservations.Dispose();
      _operationPool.Clear();
#if DEBUG
      Log(Level.Info, "SlotManager cleaned up", Category.Tasks);
#endif
    }

    /// <summary>
    /// Utility class for processing slot operations.
    /// </summary>
    public static class SlotProcessingUtility
    {
      /// <summary>
      /// Calculates the available capacity for an item in a slot.
      /// </summary>
      /// <param name="slot">The slot data.</param>
      /// <param name="item">The item key.</param>
      /// <returns>The available capacity for the item.</returns>
      [BurstCompile]
      public static int GetCapacityForItem(SlotData slot, ItemKey item)
      {
        if (!slot.IsValid || slot.IsLocked) return 0;
        if (slot.Item.Id == "")
          return item.StackLimit;
        return item.AdvCanStackWithBurst(slot.Item) ? slot.StackLimit - slot.Quantity : 0;
      }

      /// <summary>
      /// Checks if an item can be inserted into a slot.
      /// </summary>
      /// <param name="slot">The slot data.</param>
      /// <param name="item">The item key.</param>
      /// <param name="quantity">The quantity to insert.</param>
      /// <returns>True if the item can be inserted; otherwise, false.</returns>
      [BurstCompile]
      public static bool CanInsert(SlotData slot, ItemKey item, int quantity)
      {
        if (slot.Item.Id == "")
          return true;
        return item.AdvCanStackWithBurst(slot.Item) && quantity <= (slot.StackLimit - slot.Quantity);
      }

      /// <summary>
      /// Checks if an item can be removed from a slot.
      /// </summary>
      /// <param name="slot">The slot data.</param>
      /// <param name="item">The item key.</param>
      /// <param name="quantity">The quantity to remove.</param>
      /// <returns>True if the item can be removed; otherwise, false.</returns>
      [BurstCompile]
      public static bool CanRemove(SlotData slot, ItemKey item, int quantity)
      {
        return item.AdvCanStackWithBurst(slot.Item) && slot.Quantity >= quantity;
      }

      /// <summary>
      /// Validates a slot operation (insert or remove).
      /// </summary>
      /// <param name="slot">The slot to validate.</param>
      /// <param name="item">The item to process.</param>
      /// <param name="quantity">The quantity to process.</param>
      /// <param name="isInsert">Indicates whether the operation is an insert (true) or remove (false).</param>
      /// <param name="log">The log entry if validation fails.</param>
      /// <returns>True if the operation is valid; otherwise, false.</returns>
      public static bool ValidateOperation(ItemSlot slot, ItemInstance item, int quantity, bool isInsert, out LogEntry log)
      {
        log = default;
        if (slot == null || (isInsert && item == null) || quantity <= 0)
        {
          log = new LogEntry
          {
            Message = $"Invalid operation (slot={slot != null}, item={item != null}, qty={quantity})",
            Level = Level.Warning,
            Category = Category.Tasks
          };
          return false;
        }
        var slotData = new SlotData(slot);
        bool valid = isInsert
            ? CanInsert(slotData, new ItemKey(item), quantity)
            : CanRemove(slotData, new ItemKey(item), quantity);
        if (!valid)
        {
          log = new LogEntry
          {
            Message = $"{(isInsert ? "Insert" : "Remove")} failed for {quantity} of {item.ID} in slot {slot.SlotIndex}",
            Level = Level.Warning,
            Category = Category.Tasks
          };
        }
        return valid;
      }
    }

    /// <summary>
    /// Harmony patch for <see cref="ItemSlot"/> to extend locking behavior.
    /// </summary>
    [HarmonyPatch(typeof(ItemSlot))]
    public class ItemSlotPatch
    {
#if DEBUG
      /// <summary>
      /// Logs the result of an ApplyLock operation.
      /// </summary>
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

      /// <summary>
      /// Logs the result of a RemoveLock operation.
      /// </summary>
      [HarmonyPostfix]
      [HarmonyPatch("RemoveLock", new Type[] { typeof(bool) })]
      static void RemoveLockPostfix(ItemSlot __instance, bool _internal)
      {
        Log(Level.Verbose, $"RemoveLock: Unlocked slot {__instance.SlotIndex}, internal={_internal}", Category.Storage);
      }

      /// <summary>
      /// Logs the lock status of a slot.
      /// </summary>
      [HarmonyPostfix]
      [HarmonyPatch("get_IsLocked")]
      static void IsLockedPostfix(ItemSlot __instance, ref bool __result)
      {
        Log(Level.Verbose, $"IsLocked: Slot {__instance.SlotIndex} is {(__result ? "locked" : "unlocked")}", Category.Storage);
      }
#endif
      /// <summary>
      /// Overrides the ApplyLock method to customize locking behavior.
      /// </summary>
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
    /// <summary>
    /// Harmony patch for <see cref="ItemSlotUI"/> to log UI assignments.
    /// </summary>
    [HarmonyPatch(typeof(ItemSlotUI))]
    public class ItemSlotUIPatch
    {
      /// <summary>
      /// Logs the assignment of a slot to the UI.
      /// </summary>
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