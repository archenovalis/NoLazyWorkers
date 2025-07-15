using ScheduleOne.ItemFramework;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using Unity.Burst;
using HarmonyLib;
using static NoLazyWorkers.Debug;
using UnityEngine.Pool;
using FishNet.Object;
using ScheduleOne.UI;

namespace NoLazyWorkers.Storage
{
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
    /// Initializes the StorageManager, setting up necessary services if running on the server.
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
    /// Cleans up resources and resets the StorageManager state.
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