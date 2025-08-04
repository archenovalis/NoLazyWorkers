/// <summary>
/// Manages slot operations and reservations in a networked environment.
/// </summary>
internal class SlotService
{
    /// <summary>
    /// Initializes the StorageManager, setting up necessary services if running on the server.
    /// </summary>
    public static void Initialize ()

    /// <summary>
    /// Cleans up resources and resets the StorageManager state.
    /// </summary>
    public static void Cleanup ()

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
    internal static bool ReserveSlot (Guid entityGuid, ItemSlot slot, NetworkObject locker, string lockReason, ItemInstance item = null, int quantity = 0)

    /// <summary>
    /// Releases a previously reserved slot.
    /// </summary>
    /// <param name="slot">The slot to release.</param>
    internal static void ReleaseSlot (ItemSlot slot)

    /// <summary>
    /// Utility class for processing slot operations with Burst compilation.
    /// </summary>
    internal static class SlotProcessingUtility
    {
        /// <summary>
        /// Determines the capacity of a slot for a given item.
        /// </summary>
        /// <param name="slot">The slot data to check.</param>
        /// <param name="item">The item data to store.</param>
        /// <returns>The available capacity for the item in the slot.</returns>
        [BurstCompile]
        public static int GetCapacityForItem (SlotData slot, ItemData item)

        /// <summary>
        /// Checks if an item can be inserted into a slot.
        /// </summary>
        /// <param name="slot">The slot data to check.</param>
        /// <param name="item">The item data to insert.</param>
        /// <param name="quantity">The quantity to insert.</param>
        /// <returns>True if the item can be inserted, false otherwise.</returns>
        [BurstCompile]
        public static bool CanInsert (SlotData slot, ItemData item, int quantity)

        /// <summary>
        /// Checks if an item can be removed from a slot.
        /// </summary>
        /// <param name="slot">The slot data to check.</param>
        /// <param name="item">The item data to remove.</param>
        /// <param name="quantity">The quantity to remove.</param>
        /// <returns>True if the item can be removed, false otherwise.</returns>
        [BurstCompile]
        public static bool CanRemove (SlotData slot, ItemData item, int quantity)

    }

    [HarmonyPatch(typeof(ItemSlot))]
    internal class ItemSlotPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("ApplyLock", new Type[] { typeof(NetworkObject), typeof(string), typeof(bool) })]
        static bool ApplyLockPrefix (ItemSlot __instance, NetworkObject lockOwner, string lockReason, bool _internal, ref ItemSlotLock ___ActiveLock)

    }

}
