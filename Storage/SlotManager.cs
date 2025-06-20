using FishNet.Object;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using UnityEngine;
using ScheduleOne.NPCs;
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
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using static NoLazyWorkers.TimeManagerExtensions;
using UnityEngine.Pool;
using NoLazyWorkers.Metrics;
using Unity.Burst;
using Unity.Jobs;
using static NoLazyWorkers.NoLazyUtilities;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using ScheduleOne.UI.Items;
using HarmonyLib;
using ScheduleOne.UI;
using static NoLazyWorkers.Debug;

namespace NoLazyWorkers.Storage
{
  public struct SlotKey : IEquatable<SlotKey>
  {
    public readonly Guid EntityGuid;
    public readonly int SlotIndex;
    public SlotKey(Guid entityGuid, int slotIndex)
    {
      EntityGuid = entityGuid;
      SlotIndex = slotIndex;
    }
    public bool Equals(SlotKey other) => EntityGuid == other.EntityGuid && SlotIndex == other.SlotIndex;
    public override bool Equals(object obj) => obj is SlotKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(EntityGuid, SlotIndex);
  }

  public struct SlotReservation
  {
    public Guid EntityGuid;
    public float Timestamp;
    public FixedString32Bytes Locker;
    public FixedString128Bytes LockReason;
    public ItemKey Item;
    public int Quantity;
  }

  public static class SlotManager
  {
    private static NativeParallelHashMap<SlotKey, SlotReservation> Reservations { get; set; }
    private static readonly ObjectPool<List<SlotOperation>> _operationPool = new ObjectPool<List<SlotOperation>>(
        createFunc: () => new List<SlotOperation>(10),
        actionOnGet: null,
        actionOnRelease: list => list.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);

    private struct SlotOperation
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

    public static void Initialize()
    {
      Reservations = new NativeParallelHashMap<SlotKey, SlotReservation>(1000, Allocator.Persistent);
      Log(Level.Info, "SlotManager initialized", Category.Tasks);
    }

    /// <summary>
    /// Finds available slots for an item without locking them.
    /// </summary>
    /// <param name="slots">List of slots to search.</param>
    /// <param name="item">Item to place.</param>
    /// <param name="quantity">Total quantity to place.</param>
    /// <param name="allowHigherQuality">Allow higher quality items.</param>
    /// <returns>List of slots with available capacity.</returns>
    public static async Task<List<(ItemSlot Slot, int Capacity)>> FindAvailableSlotsAsync(List<ItemSlot> slots, ItemInstance item, int quantity)
    {
      return await Performance.TrackExecutionAsync(nameof(FindAvailableSlotsAsync), async () =>
      {
        var result = new List<(ItemSlot, int)>();
        if (slots == null || item == null || quantity <= 0)
        {
          Log(Level.Warning, $"FindAvailableSlots: Invalid input (slots={slots?.Count ?? 0}, item={item != null}, qty={quantity})", Category.Tasks);
          return result;
        }

        int remainingQty = quantity;
        int batchSize = GetDynamicBatchSize(slots.Count, 0.1f, nameof(FindAvailableSlotsAsync));
        int processedCount = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var slot in slots)
        {
          if (slot == null || slot.IsLocked || slot.IsAddLocked)
            continue;

          int capacity = slot.GetCapacityForItem(item);
          if (capacity <= 0)
            continue;

          int amount = Mathf.Min(capacity, remainingQty);
          result.Add((slot, amount));
          remainingQty -= amount;

          processedCount++;
          if (processedCount % batchSize == 0)
          {
            if (processedCount > 0)
            {
              double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
              DynamicProfiler.AddSample(nameof(FindAvailableSlotsAsync), avgItemTimeMs);
              stopwatch.Restart();
            }
            await AwaitNextFishNetTickAsync();
            processedCount = 0;
          }

          if (remainingQty <= 0)
            break;
        }

        if (processedCount > 0)
        {
          double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
          DynamicProfiler.AddSample(nameof(FindAvailableSlotsAsync), avgItemTimeMs);
        }

        Log(Level.Verbose, $"Found {result.Count} available slots for {item.ID}, remaining qty: {remainingQty}", Category.Tasks);
        return result;
      }, itemCount: slots?.Count ?? 0);
    }

    /// <summary>
    /// Reserves a slot for a task, applying a lock.
    /// </summary>
    public static bool ReserveSlot(Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)
    {
      if (slot == null || locker == null)
      {
        Log(Level.Warning, $"ReserveSlot: Invalid slot or locker", Category.Tasks);
        return false;
      }

      var slotKey = new SlotKey(entityGuid, slot.SlotIndex);
      if (Reservations.ContainsKey(slotKey))
      {
        Log(Level.Verbose, $"ReserveSlot: Slot {slotKey} already reserved", Category.Tasks);
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

      Log(Level.Verbose, $"Reserved slot {slotKey} for entity {entityGuid}, reason: {lockReason}", Category.Tasks);
      return true;
    }

    /// <summary>
    /// Releases a slot reservation and removes the lock.
    /// </summary>
    public static void ReleaseSlot(ItemSlot slot)
    {
      if (slot == null)
        return;

      var slotKey = Reservations.FirstOrDefault(s => s.Key.SlotIndex == slot.SlotIndex);
      if (slotKey.Key.SlotIndex == slot.SlotIndex)
      {
        Reservations.Remove(slotKey.Key);
        slot.RemoveLock();
        Log(Level.Verbose, $"Released slot {slotKey}", Category.Tasks);
      }
    }

    /// <summary>
    /// Performs a batch of slot operations (insert or remove) atomically.
    /// </summary>
    public static async Task<bool> ExecuteSlotOperationsAsync(List<(Guid EntityGuid, ItemSlot Slot, ItemInstance Item, int Quantity, bool IsInsert, NetworkObject Locker, string LockReason)> operations)
    {
      return await Performance.TrackExecutionAsync(nameof(ExecuteSlotOperationsAsync), async () =>
      {
        var opList = _operationPool.Get();
        try
        {
          // Validate and prepare operations
          foreach (var (EntityGuid, Slot, Item, Quantity, IsInsert, Locker, LockReason) in operations)
          {
            if (Slot == null || (IsInsert && Item == null) || Quantity <= 0 || Locker == null)
            {
              Log(Level.Warning, $"ExecuteSlotOperations: Invalid operation (slot={Slot != null}, item={Item != null}, qty={Quantity}, locker={Locker != null})", Category.Tasks);
              return false;
            }

            var slotKey = new SlotKey(EntityGuid, Slot.SlotIndex);
            if (IsInsert && !CanInsert(Slot, Item, Quantity))
            {
              Log(Level.Warning, $"ExecuteSlotOperations: Cannot insert {Quantity} of {Item.ID} into slot {slotKey}", Category.Tasks);
              return false;
            }
            if (!IsInsert && !CanRemove(Slot, Item, Quantity))
            {
              Log(Level.Warning, $"ExecuteSlotOperations: Cannot remove {Quantity} of {Item.ID} from slot {slotKey}", Category.Tasks);
              return false;
            }

            opList.Add(new SlotOperation
            {
              EntityGuid = EntityGuid,
              SlotKey = slotKey,
              Slot = Slot,
              Item = Item,
              Quantity = Quantity,
              IsInsert = IsInsert,
              Locker = Locker,
              LockReason = LockReason
            });
          }

          // Reserve slots
          foreach (var op in opList)
          {
            if (!ReserveSlot(op.EntityGuid, op.Slot, op.Locker, op.LockReason, op.Item, op.Quantity))
            {
              // Rollback reservations
              foreach (var reservedOp in opList.Take(opList.IndexOf(op)))
                ReleaseSlot(reservedOp.Slot);
              return false;
            }
          }

          // Execute operations
          foreach (var op in opList)
          {
            if (op.IsInsert)
            {
              op.Slot.InsertItem(op.Item.GetCopy(op.Quantity));
              Log(Level.Verbose, $"Inserted {op.Quantity} of {op.Item.ID} into slot {op.SlotKey}", Category.Tasks);
            }
            else
            {
              op.Slot.ChangeQuantity(-op.Quantity);
              Log(Level.Verbose, $"Removed {op.Quantity} of {op.Item.ID} from slot {op.SlotKey}", Category.Tasks);
            }
          }

          // Release slots
          foreach (var op in opList)
            ReleaseSlot(op.Slot);

          await AwaitNextFishNetTickAsync();
          return true;
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"ExecuteSlotOperations: Error - {ex}", Category.Tasks);
          // Rollback reservations
          foreach (var op in opList)
            ReleaseSlot(op.Slot);
          return false;
        }
        finally
        {
          _operationPool.Release(opList);
        }
      }, itemCount: operations.Count);
    }

    private static bool CanInsert(ItemSlot slot, ItemInstance item, int quantity)
    {
      if (slot.ItemInstance == null)
        return slot.DoesItemMatchFilters(item) && quantity <= item.StackLimit;
      return slot.ItemInstance.AdvCanStackWith(item, checkQuantities: true);
    }

    private static bool CanRemove(ItemSlot slot, ItemInstance item, int quantity)
    {
      return slot.ItemInstance != null &&
             slot.ItemInstance.AdvCanStackWith(item) &&
             slot.Quantity >= quantity;
    }

    public static void Cleanup()
    {
      if (Reservations.IsCreated)
        Reservations.Dispose();
      _operationPool.Clear();
      Log(Level.Info, "SlotManager cleaned up", Category.Tasks);
    }

    [HarmonyPatch(typeof(ItemSlot))]
    public class ItemSlotPatch
    {
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

      [HarmonyPrefix]
      [HarmonyPatch("ApplyLock", new Type[] { typeof(NetworkObject), typeof(string), typeof(bool) })]
      static bool ApplyLockPrefix(ItemSlot __instance, NetworkObject lockOwner, string lockReason, bool _internal, ref ItemSlotLock ___ActiveLock)
      {
        if (_internal || __instance.SlotOwner == null)
        {
          Log(Level.Verbose, $"ApplyLock: Internal slot={__instance.SlotIndex}, owner={lockOwner}, reason={lockReason}", Category.Storage);
          ___ActiveLock = new ItemSlotLock(__instance, lockOwner, lockReason);
          __instance.onLocked?.Invoke();
        }
        else
        {
          // Immediate lock for non-internal calls
          Log(Level.Verbose, $"ApplyLock: Initial slot={__instance.SlotIndex}, owner={lockOwner}, reason={lockReason}", Category.Storage);
          ___ActiveLock = new ItemSlotLock(__instance, lockOwner, lockReason);
          __instance.onLocked?.Invoke();
          __instance.SlotOwner.SetSlotLocked(null, __instance.SlotIndex, true, lockOwner, lockReason);
        }
        return false;
      }
    }

    [HarmonyPatch(typeof(ItemSlotUI))]
    public class ItemSlotUIPatch
    {
      [HarmonyPostfix]
      [HarmonyPatch("AssignSlot")]
      static void AssignSlotPostfix(ItemSlotUI __instance, ItemSlot s)
      {
        Log(Level.Verbose, $"AssignSlot: UI assigned to slot {s?.SlotIndex ?? -1}, locked={s?.IsLocked ?? false}", Category.Storage);
      }
    }
  }
}