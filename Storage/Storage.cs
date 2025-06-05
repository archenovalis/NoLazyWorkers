
using FishNet.Connection;
using FishNet.Object;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Property;
using Grid = ScheduleOne.Tiles.Grid;
using ScheduleOne.UI.Management;
using System.Collections;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.UI;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Stations.Extensions;
using FishNet.Managing;
using FishNet.Managing.Object;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Persistence;
using static NoLazyWorkers.Employees.Constants;
using ScheduleOne.Employees;
using System.Collections.Concurrent;
using Unity.Collections;
using GameKit.Utilities;
using FishNet;
using System.Diagnostics;
using static NoLazyWorkers.Storage.ShelfExtensions;
using static NoLazyWorkers.TaskService.Extensions;
using NoLazyWorkers.TaskService;

namespace NoLazyWorkers.Storage
{
  public static class Extensions
  {
    public static Dictionary<Guid, JObject> PendingConfigData { get; } = new();
    public static Dictionary<Property, Dictionary<Guid, StorageConfiguration>> Configs { get; } = new();
    public static Dictionary<Property, Dictionary<Guid, PlaceableStorageEntity>> Storages { get; } = new();
    public static Dictionary<Property, List<PlaceableStorageEntity>> AnyShelves { get; } = new();
    public static Dictionary<Property, Dictionary<ItemInstance, List<PlaceableStorageEntity>>> SpecificShelves { get; } = new();
    public static ConcurrentDictionary<ItemInstance, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>> ShelfCache { get; } = new();

    public readonly struct CacheKey : IEquatable<CacheKey>
    {
      public readonly ItemInstance Item;
      public readonly string ID;
      public readonly string PackagingId;
      public readonly EQuality? Quality;
      public readonly Property Property;

      public CacheKey(string id, string packagingId, EQuality? quality, Property property, ItemInstance item = null)
      {
        ID = id ?? throw new ArgumentNullException(nameof(id));
        PackagingId = packagingId;
        Quality = quality;
        Property = property ?? throw new ArgumentNullException(nameof(property));

        // Safely handle item creation when quality is null
        if (item == null)
        {
          if (quality.HasValue)
            Item = new ProductItemInstance { ID = id, Quality = quality.Value, PackagingID = packagingId };
          else
            Item = Registry.GetItem(id)?.GetDefaultInstance() ?? throw new ArgumentException($"No default item instance found for ID: {id}");
        }
        else
        {
          Item = item;
        }
      }

      public bool Equals(CacheKey other) =>
          ID == other.ID && PackagingId == other.PackagingId && Quality == other.Quality && ReferenceEquals(Property, other.Property);

      public override bool Equals(object obj) => obj is CacheKey other && Equals(other);

      public override int GetHashCode() => HashCode.Combine(ID, PackagingId, Quality, Property);
    }
  }

  public static class Utilities
  {
    // Removes items from a slot
    public static bool AdvRemoveItem(this ItemSlot slot, int amount, out ItemInstance item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.AdvRemoveItem: Attempting to remove {amount} from slot", DebugLogger.Category.EmployeeCore);
      if (slot == null || amount <= 0)
      {
        item = null;
        return false;
      }
      item = slot.ItemInstance;
      int initialQuantity = slot.Quantity;
      slot.RemoveLock();
      slot.ChangeQuantity(-amount);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.AdvRemoveItem: Removed {amount}, new quantity={slot.Quantity}", DebugLogger.Category.EmployeeCore);
      return slot.Quantity != initialQuantity;
    }

    // Inserts items into a slot
    public static bool AdvInsertItem(this ItemSlot slot, ItemInstance item, int amount)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.AdvInsertItem: Attempting to insert {amount} of {item?.ID}", DebugLogger.Category.EmployeeCore);
      if (slot == null || item == null || amount <= 0)
        return false;
      int initialQuantity = slot.Quantity;
      slot.RemoveLock();
      slot.InsertItem(item.GetCopy(amount));
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.AdvInsertItem: Inserted {amount}, new quantity={slot.Quantity}", DebugLogger.Category.EmployeeCore);
      return slot.Quantity != initialQuantity;
    }

    /// <summary>
    /// Reserves input slots for an item from the provided slots list.
    /// When allowHigherQuality is true, the slot's item quality can be ≥ the target item's quality.
    /// </summary>
    public static List<ItemSlot> AdvReserveInputSlotsForItem(this List<ItemSlot> slots, ItemInstance item, NetworkObject locker, bool allowHigherQuality = false)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvReserveInputSlotsForItem: Start for item={item?.ID ?? "null"}, slots={slots?.Count ?? 0}, locker={locker?.GetInstanceID() ?? -1}, allowHigherQuality={allowHigherQuality}",
          DebugLogger.Category.Storage);

      var reservedSlots = new List<ItemSlot>(slots?.Count ?? 0);
      if (!ValidateReserveInputs(slots, item, locker, out string error))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvReserveInputSlotsForItem: {error}", DebugLogger.Category.Storage);
        return reservedSlots;
      }

      int remainingQuantity = item.Quantity;
      foreach (var slot in slots)
      {
        if (!CanReserveSlot(slot, item, allowHigherQuality, out int capacity))
          continue;

        int amountToReserve = Mathf.Min(capacity, remainingQuantity);
        if (amountToReserve <= 0)
          continue;

        slot.ApplyLock(locker, "Employee reserving slot for refill");
        reservedSlots.Add(slot);
        remainingQuantity -= amountToReserve;

        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvReserveInputSlotsForItem: Reserved slot {slot.SlotIndex} (capacity={capacity}, amount={amountToReserve}, remaining={remainingQuantity}, quality={(item as ProductItemInstance)?.Quality})",
            DebugLogger.Category.Storage);

        if (remainingQuantity <= 0)
          break;
      }

      DebugLogger.Log(reservedSlots.Count > 0 ? DebugLogger.LogLevel.Info : DebugLogger.LogLevel.Warning,
          $"AdvReserveInputSlotsForItem: Reserved {reservedSlots.Count} slots for item={item.ID}, remaining={remainingQuantity}",
          DebugLogger.Category.Storage);
      return reservedSlots;
    }

    public static ItemInstance CreateItemInstance(ItemKey key)
    {
      string itemId = key.Id.ToString();
      if (key.Quality != NEQuality.None)
      {
        return new ProductItemInstance
        {
          ID = itemId,
          Quality = Enum.Parse<EQuality>(key.Quality.ToString()),
          PackagingID = key.PackagingId.ToString()
        };
      }
      return Registry.GetItem(itemId)?.GetDefaultInstance() ??
             throw new ArgumentException($"No default item instance for ID: {itemId}");
    }

    /// <summary>
    /// Retrieves output slots containing items matching the template item.
    /// When allowHigherQuality is true, the slot's item quality can be ≥ the target item's quality.
    /// </summary>
    public static List<ItemSlot> GetOutputSlotsContainingItem(ITransitEntity entity, ItemInstance item, bool allowHigherQuality = false)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetOutputSlotsContainingItem: Start for item={item?.ID ?? "null"}, entity={entity?.GUID.ToString() ?? "null"}, allowHigherQuality={allowHigherQuality}",
          DebugLogger.Category.Storage);

      if (entity == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GetOutputSlotsContainingItem: Invalid input (entity={entity != null}, item={item != null})",
            DebugLogger.Category.Storage);
        return new List<ItemSlot>();
      }

      var result = new List<ItemSlot>(entity.OutputSlots.Count);
      foreach (var slot in entity.OutputSlots)
      {
        if (slot?.ItemInstance != null && !slot.IsLocked && slot.Quantity > 0 &&
            slot.ItemInstance.AdvCanStackWith(item, allowHigherQuality: allowHigherQuality))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"GetOutputSlotsContainingItem: Found slot with item={slot.ItemInstance.ID}, quality={(slot.ItemInstance as ProductItemInstance)?.Quality}, qty={slot.Quantity}",
              DebugLogger.Category.Storage);
          result.Add(slot);
        }
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"GetOutputSlotsContainingItem: Found {result.Count} slots for item={item.ID}, quality={(item as ProductItemInstance)?.Quality}",
          DebugLogger.Category.Storage);
      return result;
    }

    private static readonly object _lock = new();

    /// <summary>
    /// Checks if two items can stack based on ID, packaging, quality, and quantity.
    /// Supports "Any" ID for matching any product with quality > target quality.
    /// </summary>
    /// <param name="item">The source item.</param>
    /// <param name="targetItem">The reference item.</param>
    /// <param name="allowHigherQuality">If true, item.Quality >= targetItem.Quality; otherwise, exact match for non-"Any".</param>
    /// <param name="checkQuantities">If true, checks if stacking exceeds StackLimit.</param>
    public static bool AdvCanStackWith(this ItemInstance item, ItemInstance targetItem, bool allowHigherQuality = false, bool checkQuantities = false)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvCanStackWith: Checking item={item?.ID ?? "null"} (quality={(item as ProductItemInstance)?.Quality}) against targetItem={targetItem?.ID ?? "null"} (quality={(targetItem as ProductItemInstance)?.Quality}, allowHigherQuality={allowHigherQuality}, checkQuantities={checkQuantities})",
          DebugLogger.Category.Storage);

      if (!ValidateStackingInputs(item, targetItem, out string error))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvCanStackWith: {error}", DebugLogger.Category.Storage);
        return false;
      }

      // Handle "Any" ID case
      if (targetItem.ID == "Any")
      {
        if (item is ProductItemInstance itemProd && targetItem is ProductItemInstance targetProd)
        {
          bool qualityMatch = itemProd.Quality >= targetProd.Quality;
          if (qualityMatch)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvCanStackWith: Matched 'Any' (itemQuality={itemProd.Quality}, targetQuality={targetProd.Quality})",
            DebugLogger.Category.Storage);
            return true;
          }
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvCanStackWith: Quality mismatch for 'Any' (itemQuality={itemProd.Quality}, targetQuality={targetProd.Quality})",
              DebugLogger.Category.Storage);
          return false;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvCanStackWith: Invalid types for 'Any' (item={item.GetType().Name}, target={targetItem.GetType().Name})",
            DebugLogger.Category.Storage);
        return false;
      }

      if (item.ID != targetItem.ID)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvCanStackWith: ID mismatch", DebugLogger.Category.Storage);
        return false;
      }

      if (checkQuantities && item.StackLimit < targetItem.Quantity + item.Quantity)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvCanStackWith: Stack limit exceeded", DebugLogger.Category.Storage);
        return false;
      }

      if (item is ProductItemInstance prodA && targetItem is ProductItemInstance prodB)
      {
        if (!ArePackagingsCompatible(prodA, prodB))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvCanStackWith: Packaging mismatch", DebugLogger.Category.Storage);
          return false;
        }

        bool qualityMatch = AreQualitiesCompatible(prodA, prodB, allowHigherQuality);
        if (qualityMatch)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvCanStackWith: Quality match (allowHigherQuality={allowHigherQuality})",
              DebugLogger.Category.Storage);
        }
        return qualityMatch;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"AdvCanStackWith: Matched non-product item (id={item.ID})",
          DebugLogger.Category.Storage);
      return true;
    }


    /// <summary>
    /// Schedules an update to the storage cache for a shelf's parent property using FishNet's TimeManager.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    public static void UpdateStorageCache(PlaceableStorageEntity shelf)
    {
      if (shelf == null || shelf.ParentProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            "UpdateStorageCache: Shelf or ParentProperty is null",
            DebugLogger.Category.Storage);
        return;
      }

      CacheManager.QueueStorageUpdate(shelf);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateStorageCache: Queued for shelf={shelf.GUID}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Schedules an update to the station cache for a station's parent property using FishNet's TimeManager.
    /// </summary>
    /// <param name="station">The station adapter to update.</param>
    public static void UpdateStationCache(IStationAdapter station)
    {
      if (station == null || station.Buildable?.ParentProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            "UpdateStationCache: Station or ParentProperty is null",
            DebugLogger.Category.Storage);
        return;
      }

      CacheManager.QueueStationUpdate(station);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateStationCache: Queued for station={station.GUID}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Finds a storage shelf with the specified item and quantity.
    /// When allowHigherQuality is true, the shelf's item quality can be ≥ the target item's quality.
    /// </summary>
    public static KeyValuePair<PlaceableStorageEntity, int> FindStorageWithItem(NPC npc, ItemInstance targetItem, int needed, int wanted = 0, bool allowHigherQuality = false)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"FindStorageWithItem: Start for item={targetItem?.ID ?? "null"}, needed={needed}, wanted={wanted}, allowHigherQuality={allowHigherQuality}, npc={npc?.fullName ?? "null"}",
          DebugLogger.Category.Storage);

      var defaultResult = new KeyValuePair<PlaceableStorageEntity, int>(null, 0);
      if (!ValidateFindStorageInputs(npc, targetItem, out Employee employee, out Property property, out string error))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindStorageWithItem: {error}", DebugLogger.Category.Storage);
        return defaultResult;
      }

      var cacheKey = CreateCacheKey(targetItem, property);
      if (CacheManager.IsItemNotFound(cacheKey))
        return defaultResult;

      if (TryFindCachedShelf(employee, cacheKey, needed, out var result))
        return result;

      return SearchShelvesForItem(employee, targetItem, needed, wanted, allowHigherQuality, cacheKey);
    }

    /// <summary>
    /// Finds a storage shelf for delivering an item.
    /// </summary>
    public static PlaceableStorageEntity FindStorageForDelivery(NPC npc, ItemInstance item, bool allowAnyShelves = true)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"FindStorageForDelivery: Start for item={item?.ID ?? "null"}, npc={npc?.fullName ?? "null"}, allowAnyShelves={allowAnyShelves}",
          DebugLogger.Category.Storage);

      if (!ValidateDeliveryInputs(npc, item, out Employee employee, out Property property, out int qtyToDeliver, out string error))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindStorageForDelivery: {error}", DebugLogger.Category.Storage);
        return null;
      }

      if (TryFindSpecificShelf(employee, property, item, qtyToDeliver, allowAnyShelves, out var shelf))
        return shelf;

      if (allowAnyShelves && TryFindAnyShelf(employee, property, item, qtyToDeliver, out shelf))
        return shelf;

      DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindStorageForDelivery: No suitable shelf found for {item.ID}", DebugLogger.Category.Storage);
      return null;
    }

    /// <summary>
    /// Gets the total quantity of an item in a shelf's output slots.
    /// </summary>
    public static int GetItemQuantityInStorage(PlaceableStorageEntity shelf, ItemInstance targetItem)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetItemQuantityInStorage: Start for shelf={shelf?.GUID.ToString() ?? "null"}, item={targetItem?.ID ?? "null"}",
          DebugLogger.Category.Storage);

      if (shelf == null || targetItem == null)
        return 0;

      int qty = 0;
      foreach (var slot in shelf.OutputSlots)
      {
        if (slot?.ItemInstance != null && slot.ItemInstance.ID.Equals(targetItem.ID, StringComparison.OrdinalIgnoreCase))
          qty += slot.Quantity;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetItemQuantityInStorage: Found {qty} of {targetItem.ID}", DebugLogger.Category.Storage);
      return qty;
    }

    /// <summary>
    /// Updates the storage configuration for a shelf.
    /// </summary>
    public static void UpdateStorageConfiguration(PlaceableStorageEntity shelf, ItemInstance assignedItem)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateStorageConfiguration: Start for shelf={shelf?.GUID.ToString() ?? "null"}, item={assignedItem?.ID ?? "null"}",
          DebugLogger.Category.Storage);

      if (!ValidateConfigurationInputs(shelf, out StorageConfiguration config, out string error))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UpdateStorageConfiguration: {error}", DebugLogger.Category.Storage);
        return;
      }

      UpdateStorageConfigurationInternal(shelf, assignedItem, config);
    }

    #region Private Helper Methods

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

    private static bool CanReserveSlot(ItemSlot slot, ItemInstance item, bool allowHigherQuality, out int capacity)
    {
      capacity = 0;
      if (slot == null || slot.IsLocked)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"CanReserveSlot: Skipping slot (null={slot == null}, locked={slot?.IsLocked})",
            DebugLogger.Category.Storage);
        return false;
      }

      if (slot.ItemInstance != null && slot.ItemInstance.AdvCanStackWith(item, allowHigherQuality))
      {
        capacity = slot.ItemInstance.StackLimit - slot.Quantity;
        if (capacity <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"CanReserveSlot: Slot {slot.SlotIndex} full (item={slot.ItemInstance.ID}, qty={slot.Quantity})",
              DebugLogger.Category.Storage);
          return false;
        }
      }
      else if (slot.ItemInstance == null)
      {
        capacity = item.StackLimit;
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"CanReserveSlot: Slot {slot.SlotIndex} has non-matching item {slot.ItemInstance.ID}",
            DebugLogger.Category.Storage);
        return false;
      }

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

    private static bool ArePackagingsCompatible(ProductItemInstance prodA, ProductItemInstance prodB)
    {
      return (prodA.AppliedPackaging == null && prodB.AppliedPackaging == null) ||
             (prodA.AppliedPackaging != null && prodB.AppliedPackaging != null &&
              prodA.AppliedPackaging.ID == prodB.AppliedPackaging.ID);
    }

    private static bool AreQualitiesCompatible(ProductItemInstance prodA, ProductItemInstance prodB, bool allowHigherQuality)
    {
      if (prodA is QualityItemInstance qualA && prodB is QualityItemInstance qualB)
        return allowHigherQuality ? qualA.Quality >= qualB.Quality : qualA.Quality == qualB.Quality;
      return !(prodA is QualityItemInstance) && !(prodB is QualityItemInstance);
    }

    /// <summary>
    /// Updates the shelf cache with current item quantities and configuration.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="shelfGuid">The GUID of the shelf.</param>
    /// <param name="itemQuantities">The current item quantities on the shelf.</param>
    private static void UpdateShelfCache(PlaceableStorageEntity shelf, Guid shelfGuid, Dictionary<ItemInstance, int> itemQuantities)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateShelfCache: Updating for shelf={shelfGuid}, items={itemQuantities.Count}",
          DebugLogger.Category.Storage);

      lock (_lock)
      {
        // Remove entries where shelf exists but item is no longer present or quantity is zero
        var keysToRemove = ShelfCache
            .Where(kvp => kvp.Value.ContainsKey(shelf) &&
                         (!itemQuantities.ContainsKey(kvp.Key) || itemQuantities[kvp.Key] <= 0))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
          ShelfCache[key].TryRemove(shelf, out _);
          if (ShelfCache[key].Count == 0)
            ShelfCache.TryRemove(key, out _);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateShelfCache: Removed item={key.ID} from shelf={shelfGuid}",
              DebugLogger.Category.Storage);
        }

        // Update or add entries for current items
        foreach (var kvp in itemQuantities)
        {
          var key = kvp.Key;
          var qty = kvp.Value;

          // Skip zero or negative quantities
          if (qty <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateShelfCache: Skipping item={key.ID} with qty={qty} for shelf={shelfGuid}",
                DebugLogger.Category.Storage);
            continue;
          }

          if (!ShelfCache.TryGetValue(key, out var shelfDict))
          {
            shelfDict = new ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>();
            ShelfCache[key] = shelfDict;
          }

          bool isConfigured = Configs.TryGetValue(shelf.ParentProperty, out var configs) &&
                             configs.TryGetValue(shelfGuid, out var config) &&
                             config.Mode == StorageMode.Specific &&
                             config.AssignedItem?.AdvCanStackWith(key) == true;

          shelfDict[shelf] = new ShelfInfo(qty, isConfigured);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateShelfCache: Updated item={key.ID}, qty={qty}, configured={isConfigured} for shelf={shelfGuid}",
              DebugLogger.Category.Storage);
        }
      }
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

    private static bool ValidateFindStorageInputs(NPC npc, ItemInstance targetItem, out Employee employee, out Property property, out string error)
    {
      employee = null;
      property = null;
      if (targetItem == null)
      {
        error = "Invalid input: targetItem is null";
        return false;
      }
      if (npc == null)
      {
        error = "Invalid input: npc is null";
        return false;
      }
      employee = npc as Employee;
      if (employee == null)
      {
        error = "NPC is not an Employee";
        return false;
      }
      property = employee.AssignedProperty;
      if (property == null)
      {
        error = "No property assigned to employee";
        return false;
      }
      if (npc.Movement == null)
      {
        error = "NPC movement is null";
        return false;
      }
      error = string.Empty;
      return true;
    }

    private static bool TryFindCachedShelf(Employee employee, CacheKey cacheKey, int needed, out KeyValuePair<PlaceableStorageEntity, int> result)
    {
      result = new KeyValuePair<PlaceableStorageEntity, int>(null, 0);
      if (!CacheManager.TryGetCachedShelves(cacheKey, out var cachedShelves))
        return false;

      foreach (var cachedResult in cachedShelves)
      {
        if (cachedResult.Value >= needed &&
            Storages[employee.AssignedProperty].TryGetValue(cachedResult.Key.GUID, out var shelf) &&
            employee.Movement.CanGetTo(shelf))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"TryFindCachedShelf: Cache hit for shelf={shelf.GUID}, qty={cachedResult.Value}",
              DebugLogger.Category.Storage);
          result = cachedResult;
          return true;
        }
      }
      return false;
    }

    private static KeyValuePair<PlaceableStorageEntity, int> SearchShelvesForItem(Employee employee, ItemInstance targetItem, int needed, int wanted, bool allowHigherQuality, CacheKey cacheKey)
    {
      PlaceableStorageEntity selectedShelf = null;
      int assignedQty = 0;
      bool itemFound = false;

      foreach (var key in ShelfCache.Keys)
      {
        if (key?.AdvCanStackWith(targetItem, allowHigherQuality) != true)
          continue;

        itemFound = true;
        var shelfDict = ShelfCache[key];
        if (TrySelectShelf(employee, shelfDict, needed, wanted, out selectedShelf, out assignedQty))
          break;
      }

      if (selectedShelf != null)
      {
        var result = new KeyValuePair<PlaceableStorageEntity, int>(selectedShelf, assignedQty);
        CacheManager.UpdateShelfSearchCache(cacheKey, selectedShelf, assignedQty);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"SearchShelvesForItem: Found shelf={selectedShelf.GUID}, qty={assignedQty}",
            DebugLogger.Category.Storage);
        return result;
      }

      if (!itemFound)
        CacheManager.AddItemNotFound(cacheKey);

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"SearchShelvesForItem: {(itemFound ? "Found item but insufficient qty" : "No items found")} (needed={needed})",
          DebugLogger.Category.Storage);
      return new KeyValuePair<PlaceableStorageEntity, int>(null, 0);
    }

    private static bool TrySelectShelf(Employee employee, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo> shelfDict,
        int needed, int wanted, out PlaceableStorageEntity selectedShelf, out int assignedQty)
    {
      selectedShelf = null;
      assignedQty = 0;
      var neededShelves = new Dictionary<PlaceableStorageEntity, int>(shelfDict.Count);

      foreach (var kvp in shelfDict)
      {
        var shelf = kvp.Key;
        var shelfInfo = kvp.Value;

        if (shelfInfo.Quantity < needed)
          continue;

        if (shelfInfo.Quantity >= wanted && employee.Movement.CanGetTo(shelf))
        {
          selectedShelf = shelf;
          assignedQty = shelfInfo.Quantity;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"TrySelectShelf: Selected shelf={shelf.GUID}, qty={assignedQty} (wanted={wanted})",
              DebugLogger.Category.Storage);
          return true;
        }

        if (shelfInfo.Quantity >= needed)
          neededShelves[shelf] = shelfInfo.Quantity;
      }

      if (neededShelves.Count > 0)
      {
        var sortedShelves = neededShelves.OrderByDescending(q => q.Value).ToList();
        foreach (var shelfKvp in sortedShelves)
        {
          if (employee.Movement.CanGetTo(shelfKvp.Key))
          {
            selectedShelf = shelfKvp.Key;
            assignedQty = shelfKvp.Value;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"TrySelectShelf: Selected shelf={shelfKvp.Key.GUID}, qty={assignedQty} (sorted)",
                DebugLogger.Category.Storage);
            return true;
          }
        }
      }

      return false;
    }

    private static bool ValidateDeliveryInputs(NPC npc, ItemInstance item, out Employee employee, out Property property, out int qtyToDeliver, out string error)
    {
      employee = null;
      property = null;
      qtyToDeliver = 0;
      if (item == null)
      {
        error = "Invalid input: item is null";
        return false;
      }
      if (npc == null)
      {
        error = "Invalid input: npc is null";
        return false;
      }
      employee = npc as Employee;
      if (employee == null)
      {
        error = "NPC is not an Employee";
        return false;
      }
      property = employee.AssignedProperty;
      if (property == null)
      {
        error = "No property assigned";
        return false;
      }
      if (npc.Movement == null)
      {
        error = "NPC movement is null";
        return false;
      }
      qtyToDeliver = item.Quantity;
      error = string.Empty;
      return true;
    }

    private static bool TryFindSpecificShelf(Employee employee, Property property, ItemInstance item, int qtyToDeliver, bool allowAnyShelves, out PlaceableStorageEntity shelf)
    {
      shelf = null;
      if (NoDropOffCache.TryGetValue(property, out var noDropOffCache) && noDropOffCache.Contains(item))
      {
        if (!allowAnyShelves)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"TryFindSpecificShelf: Item {item.ID} in NoDropOffCache, allowAnyShelves=false",
              DebugLogger.Category.Storage);
          return true;
        }
        return false;
      }

      if (SpecificShelves.TryGetValue(property, out var specificShelves) &&
          specificShelves.TryGetValue(item, out var shelves))
      {
        foreach (var candidate in shelves)
        {
          if (!Configs[property].TryGetValue(candidate.GUID, out var config) ||
              config.Mode != StorageMode.Specific)
            continue;

          if (candidate.StorageEntity?.CanItemFit(item, qtyToDeliver) == true &&
              employee.Movement.CanGetTo(candidate))
          {
            shelf = candidate;
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"TryFindSpecificShelf: Found specific shelf={candidate.GUID}",
                DebugLogger.Category.Storage);
            return true;
          }
        }
        return false;
      }

      if (!NoDropOffCache.TryGetValue(property, out noDropOffCache))
      {
        noDropOffCache = new List<ItemInstance>();
        NoDropOffCache.TryAdd(property, noDropOffCache);
      }
      if (!noDropOffCache.Contains(item))
      {
        noDropOffCache.Add(item);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TryFindSpecificShelf: Added {item.ID} to NoDropOffCache",
            DebugLogger.Category.Storage);
      }
      return true;
    }

    private static bool TryFindAnyShelf(Employee employee, Property property, ItemInstance item, int qtyToDeliver, out PlaceableStorageEntity shelf)
    {
      shelf = null;
      if (!AnyShelves.TryGetValue(property, out var anyShelves))
        return false;

      foreach (var candidate in anyShelves)
      {
        if (candidate.StorageEntity?.CanItemFit(item, qtyToDeliver) == true &&
            employee.Movement.CanGetTo(candidate))
        {
          shelf = candidate;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"TryFindAnyShelf: Found any shelf={candidate.GUID}",
              DebugLogger.Category.Storage);
          return true;
        }
      }
      return false;
    }

    private static int HowManyCanFit(ItemInstance item, IEnumerable<ItemSlot> slots)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HowManyCanFit: Start for item={item?.ID ?? "null"}",
          DebugLogger.Category.Storage);

      if (slots == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HowManyCanFit: Invalid input (slots={slots != null}, item={item != null})",
            DebugLogger.Category.Storage);
        return 0;
      }

      int totalCapacity = 0;
      foreach (var slot in slots)
      {
        if (slot == null || slot.IsLocked || slot.IsAddLocked)
          continue;

        int slotCapacity = item.StackLimit;
        if (slot.ItemInstance == null)
        {
          totalCapacity += slotCapacity;
        }
        else if (slot.ItemInstance.AdvCanStackWith(item, allowHigherQuality: true))
        {
          int available = slot.ItemInstance.StackLimit - slot.Quantity;
          if (available > 0)
          {
            totalCapacity += available;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"HowManyCanFit: Slot has {slot.SlotIndex} available capacity for {item.ID}, {available}",
            DebugLogger.Category.Storage);
          }
        }
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"HowManyCanFit: Total capacity={totalCapacity} for {item.ID}",
          DebugLogger.Category.Storage);
      return totalCapacity;
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
      if (!Configs[shelf.ParentProperty].TryGetValue(shelf.GUID, out config))
      {
        error = $"No configuration for shelf {shelf.GUID}";
        return false;
      }
      error = string.Empty;
      return true;
    }

    private static void UpdateStorageConfigurationInternal(PlaceableStorageEntity shelf, ItemInstance assignedItem, StorageConfiguration config)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateStorageConfigurationInternal: Start for shelf={shelf.GUID}, item={assignedItem?.ID ?? "null"}",
          DebugLogger.Category.Storage);

      var property = shelf.ParentProperty;
      var oldAssignedItem = config.AssignedItem;

      // Ensure NoDropOffCache exists
      if (!NoDropOffCache.TryGetValue(property, out var cache))
      {
        cache = new List<ItemInstance>();
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

      CacheManager.RemoveShelfSearchCacheEntries(cacheKeysToRemove);
      UpdateStorageCache(shelf);

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"UpdateStorageConfigurationInternal: Completed for shelf={shelf.GUID}",
          DebugLogger.Category.Storage);
    }

    private static void AddShelfToAnyShelves(PlaceableStorageEntity shelf, Property property)
    {
      if (!AnyShelves.TryGetValue(property, out var anyShelves))
        AnyShelves[property] = new List<PlaceableStorageEntity> { shelf };

      else if (!anyShelves.Contains(shelf))
      {
        anyShelves.Add(shelf);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AddShelfToAnyShelves: Added shelf={shelf.GUID}",
            DebugLogger.Category.Storage);
      }
    }

    private static void AddShelfToSpecificShelves(PlaceableStorageEntity shelf, ItemInstance assignedItem, Property property, List<ItemInstance> cache)
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
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"AddShelfToSpecificShelves: Added shelf={shelf.GUID} for {assignedItem.ID}",
            DebugLogger.Category.Storage);
      }

      for (int i = cache.Count - 1; i >= 0; i--)
      {
        if (cache[i].ID == assignedItem.ID)
        {
          cache.RemoveAt(i);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AddShelfToSpecificShelves: Removed {assignedItem.ID} from NoDropOffCache",
              DebugLogger.Category.Storage);
        }
      }

      int qty = GetItemQuantityInStorage(shelf, assignedItem);
      shelfDict[shelf] = new ShelfInfo(qty, true);
    }

    public static void RemoveStorageFromLists(PlaceableStorageEntity shelf)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"RemoveStorageFromLists: Start for shelf={shelf?.GUID.ToString() ?? "null"}",
          DebugLogger.Category.Storage);

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
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"RemoveStorageFromLists: Removed shelf={shelf.GUID} from SpecificShelves",
            DebugLogger.Category.Storage);
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"RemoveStorageFromLists: Completed for shelf={shelf.GUID}",
          DebugLogger.Category.Storage);
    }

    #endregion
  }

  /// <summary>
  /// Manages storage and station-related caches with thread-safe access for efficient task validation and item lookup.
  /// </summary>
  public static class CacheManager
  {
    private static readonly ConcurrentDictionary<CacheKey, bool> _itemNotFoundCache = new();
    private static readonly ConcurrentDictionary<CacheKey, List<KeyValuePair<PlaceableStorageEntity, int>>> _shelfSearchCache = new();
    private static readonly ConcurrentDictionary<ItemInstance, ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>> _shelfCache = new();
    private static readonly ConcurrentDictionary<(Property, EQuality?), List<KeyValuePair<PlaceableStorageEntity, ShelfInfo>>> _anyQualityCache = new();
    private static readonly ConcurrentQueue<PlaceableStorageEntity> _pendingUpdates = new();
    private static readonly ConcurrentQueue<IStationAdapter> _pendingStationUpdates = new(); // New: Queue for station updates
    private static bool _isProcessingUpdates;
    private static readonly ConcurrentDictionary<Property, NativeParallelHashMap<ItemKey, NativeList<StorageKey>>> _specificShelvesCache = new();
    private static readonly ConcurrentDictionary<Property, (List<IStationAdapter> Stations, List<PlaceableStorageEntity> Storages)> _propertyDataCache = new();

    /// <summary>
    /// Initializes the cache manager by subscribing to FishNet's TimeManager ticks and populating initial property data.
    /// </summary>
    public static void Initialize()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, "CacheManager.Initialize: Subscribing to TimeManager.OnTick", DebugLogger.Category.Storage);
      InstanceFinder.TimeManager.OnTick += ProcessPendingUpdates;
      foreach (var property in Property.Properties)
      {
        UpdatePropertyDataCache(property);
      }
    }

    /// <summary>
    /// Retrieves the specific shelves cache for a given property.
    /// </summary>
    /// <param name="property">The property to query.</param>
    /// <param name="specificShelves">The dictionary of item keys to storage keys if found; otherwise, null.</param>
    /// <returns>True if the cache exists; otherwise, false.</returns>
    public static bool TryGetSpecificShelves(Property property, out NativeParallelHashMap<ItemKey, NativeList<StorageKey>> specificShelves)
    {
      return _specificShelvesCache.TryGetValue(property, out specificShelves);
    }

    /// <summary>
    /// Asynchronously retrieves property data.
    /// </summary>
    public static async Task<(bool, List<IStationAdapter>, List<PlaceableStorageEntity>)> TryGetPropertyDataAsync(Property property)
    {
      await InstanceFinder.TimeManager.AwaitNextTickAsync();
      try
      {
        return TryGetPropertyData(property);
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"TryGetPropertyDataAsync: Failed for {property.name} - {ex}", DebugLogger.Category.TaskManager);
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
    public static (bool, List<IStationAdapter>, List<PlaceableStorageEntity>) TryGetPropertyData(Property property)
    {
      List<IStationAdapter> stations;
      List<PlaceableStorageEntity> storages;
      if (_propertyDataCache.TryGetValue(property, out var data))
      {
        stations = data.Stations;
        storages = data.Storages;
        return (true, stations, storages);
      }
      stations = null;
      storages = null;
      return (false, stations, storages);
    }

    /// <summary>
    /// Updates the property data cache with current stations and storages, and refreshes the specific shelves cache.
    /// </summary>
    /// <param name="property">The property to update.</param>
    public static void UpdatePropertyDataCache(Property property)
    {
      if (property == null) return;

      var stations = Stations.Extensions.IStations.TryGetValue(property, out var stationList) ? stationList.Values.ToList() : new List<IStationAdapter>();
      var storages = Storages.TryGetValue(property, out var storageDict) ? storageDict.Values.ToList() : new List<PlaceableStorageEntity>();
      _propertyDataCache[property] = (stations, storages);

      // Update specific shelves cache
      var specificShelves = new NativeParallelHashMap<ItemKey, NativeList<StorageKey>>();
      foreach (var storage in storages)
      {
        if (Configs.TryGetValue(property, out var configs) &&
            configs.TryGetValue(storage.GUID, out var config) &&
            config.Mode == StorageMode.Specific &&
            config.AssignedItem != null)
        {
          var itemKey = new ItemKey(config.AssignedItem);
          if (!specificShelves.ContainsKey(itemKey))
            specificShelves[itemKey] = new NativeList<StorageKey>();
          specificShelves[itemKey].Add(new StorageKey(storage.GUID));
        }
      }
      _specificShelvesCache[property] = specificShelves;

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"UpdatePropertyDataCache: Updated for property {property.name}, stations={stations.Count}, storages={storages.Count}, specificShelves={specificShelves.Count()}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Queues a station for cache update, triggering property data refresh and task revalidation.
    /// </summary>
    /// <param name="station">The station adapter to update.</param>
    public static void QueueStationUpdate(IStationAdapter station)
    {
      if (station == null || station.Buildable?.ParentProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            "QueueStationUpdate: Invalid station or no parent property",
            DebugLogger.Category.Storage);
        return;
      }

      _pendingStationUpdates.Enqueue(station);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"QueueStationUpdate: Queued station {station.GUID} for property {station.Buildable.ParentProperty.name}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Queues a shelf for cache update, processed once per tick.
    /// </summary>
    /// <param name="shelf">The storage entity to update.</param>
    public static void QueueStorageUpdate(PlaceableStorageEntity shelf)
    {
      if (shelf == null) return;
      _pendingUpdates.Enqueue(shelf);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"QueueUpdate: Queued shelf {shelf.GUID}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Clears item not found cache entries for a specific item and property.
    /// </summary>
    /// <param name="item">The item to clear from the cache.</param>
    /// <param name="property">The property associated with the cache.</param>
    public static void ClearItemNotFoundCache(ItemInstance item, Property property)
    {
      var cacheKey = Utilities.CreateCacheKey(item, property);
      if (_itemNotFoundCache.TryRemove(cacheKey, out _))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ClearItemNotFoundCache: Removed key={cacheKey.ID}{(cacheKey.Quality.HasValue ? $" Quality={cacheKey.Quality}" : "")}",
            DebugLogger.Category.Storage);
      }
    }

    /// <summary>
    /// Processes pending shelf and station updates in a single tick, with deduplication and performance limits.
    /// </summary>
    private static void ProcessPendingUpdates()
    {
      if (_isProcessingUpdates || (_pendingUpdates.IsEmpty && _pendingStationUpdates.IsEmpty)) return;
      _isProcessingUpdates = true;
      try
      {
        var processedShelves = new HashSet<Guid>();
        var processedProperties = new HashSet<string>();
        var stopwatch = Stopwatch.StartNew();
        const int MAX_ITEMS_PER_TICK = 5;
        const long MAX_TIME_MS = 1;
        int processedCount = 0;

        // Process shelf updates
        while (processedCount < MAX_ITEMS_PER_TICK && stopwatch.ElapsedMilliseconds < MAX_TIME_MS &&
               _pendingUpdates.TryDequeue(out var shelf))
        {
          if (shelf == null || !processedShelves.Add(shelf.GUID)) continue;
          PerformStorageCacheUpdate(shelf);
          processedCount++;
        }

        // Process station updates
        while (processedCount < MAX_ITEMS_PER_TICK && stopwatch.ElapsedMilliseconds < MAX_TIME_MS &&
               _pendingStationUpdates.TryDequeue(out var station))
        {
          if (station == null || station.Buildable?.ParentProperty == null ||
              !processedProperties.Add(station.Buildable.ParentProperty.name)) continue;

          var property = station.Buildable.ParentProperty;
          if (!IStations.TryGetValue(property, out var stations))
            stations = IStations[property] = new();

          // Track station types before update
          var typeCountsBefore = stations.Values.GroupBy(s => s.TransitEntity.GetType())
            .ToDictionary(g => g.Key, g => g.Count());

          UpdatePropertyDataCache(property);

          // Update station in cache
          if (stations.ContainsKey(station.GUID))
            stations[station.GUID] = station;
          else
            stations.Add(station.GUID, station);

          // Track station types after update
          var typeCountsAfter = stations.Values.GroupBy(s => s.TransitEntity.GetType())
              .ToDictionary(g => g.Key, g => g.Count());

          // Detect station type addition or removal
          bool needsRevalidation = false;
          foreach (var type in typeCountsBefore.Keys.Concat(typeCountsAfter.Keys).Distinct())
          {
            int beforeCount = typeCountsBefore.GetValueOrDefault(type, 0);
            int afterCount = typeCountsAfter.GetValueOrDefault(type, 0);
            if ((beforeCount == 0 && afterCount == 1) || (beforeCount == 1 && afterCount == 0))
            {
              needsRevalidation = true;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"ProcessPendingUpdates: Station type {type.Name} {(afterCount == 1 ? "added" : "removed")} for property {property.name}",
                  DebugLogger.Category.Storage);
              break;
            }
          }

          if (needsRevalidation)
          {
            TaskServiceManager.GetOrCreateService(property).EnqueueValidation(property, 100);
          }
          else
          {
            TaskServiceManager.GetOrCreateService(property).EnqueueValidation(property);
          }

          processedCount++;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ProcessPendingUpdates: Processed station {station.GUID} for property {property.name}",
              DebugLogger.Category.Storage);
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ProcessPendingUpdates: Error - {ex}", DebugLogger.Category.Storage);
      }
      finally
      {
        _isProcessingUpdates = false;
      }
    }

    /// <summary>
    /// Performs the storage cache update for a single shelf.
    /// </summary>
    /// <param name="shelf">The storage entity to update.</param>
    private static void PerformStorageCacheUpdate(PlaceableStorageEntity shelf)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PerformStorageCacheUpdate: Start for shelf={shelf.GUID}", DebugLogger.Category.Storage);

      var outputSlots = shelf.OutputSlots.ToArray();
      var property = shelf.ParentProperty;
      var itemQuantities = new Dictionary<ItemInstance, int>(outputSlots.Length);

      // Collect current items
      foreach (var slot in outputSlots)
      {
        if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;
        if (!itemQuantities.TryGetValue(slot.ItemInstance, out int currentQty))
          itemQuantities[slot.ItemInstance] = 0;
        itemQuantities[slot.ItemInstance] += slot.Quantity;
        ClearItemNotFoundCache(slot.ItemInstance, property);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"PerformStorageCacheUpdate: Found {slot.Quantity} of {slot.ItemInstance.ID}{(slot.ItemInstance is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}",
            DebugLogger.Category.Storage);
      }

      // Update caches
      UpdateShelfCache(shelf, itemQuantities, property);
      UpdateAnyQualityCache(shelf, itemQuantities, property);

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PerformStorageCacheUpdate: Completed for shelf={shelf.GUID}, items={itemQuantities.Count}",
          DebugLogger.Category.Storage);

      // Notify clients if server
      if (InstanceFinder.IsServer)
        RpcUpdateClientCache(property, shelf.GUID, itemQuantities);
    }

    /// <summary>
    /// Updates client caches via RPC for a specific shelf.
    /// </summary>
    /// <param name="property">The property containing the shelf.</param>
    /// <param name="shelfGuid">The GUID of the shelf.</param>
    /// <param name="itemQuantities">The current item quantities on the shelf.</param>
    [ServerRpc]
    private static void RpcUpdateClientCache(Property property, Guid shelfGuid, Dictionary<ItemInstance, int> itemQuantities)
    {
      if (!Storages.TryGetValue(property, out var storageDict) ||
          !storageDict.TryGetValue(shelfGuid, out var shelf) ||
          InstanceFinder.IsServer) return;
      foreach (var kvp in itemQuantities)
        ClearItemNotFoundCache(kvp.Key, shelf.ParentProperty);
      UpdateShelfCache(shelf, itemQuantities, shelf.ParentProperty);
      UpdateAnyQualityCache(shelf, itemQuantities, shelf.ParentProperty);
    }

    /// <summary>
    /// Clears zero-quantity entries for a specific shelf, optionally for specific items.
    /// </summary>
    /// <param name="shelf">The shelf to clear entries for.</param>
    /// <param name="itemsToRemove">Optional list of items to remove; if null, clears all zero-quantity entries.</param>
    public static void ClearZeroQuantityEntries(PlaceableStorageEntity shelf, IEnumerable<ItemInstance> itemsToRemove = null)
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
            _ => new List<KeyValuePair<PlaceableStorageEntity, int>>(),
            (_, list) =>
            {
              list.RemoveAll(r => r.Key == shelf);
              return list;
            });

        if (!_shelfSearchCache[key].Any())
          _shelfSearchCache.TryRemove(key, out _);

        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ClearZeroQuantityEntries: Removed shelf={shelf.GUID} for key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}",
            DebugLogger.Category.Storage);
      }
    }

    /// <summary>
    /// Checks if an item is marked as not found in the cache.
    /// </summary>
    /// <param name="key">The cache key to check.</param>
    /// <returns>True if the item is not found; otherwise, false.</returns>
    public static bool IsItemNotFound(CacheKey key)
    {
      bool found = _itemNotFoundCache.ContainsKey(key);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"IsItemNotFound: {(found ? "Hit" : "Miss")}",
          DebugLogger.Category.Storage);
      return found;
    }

    /// <summary>
    /// Marks an item as not found in the cache.
    /// </summary>
    /// <param name="key">The cache key to mark.</param>
    public static void AddItemNotFound(CacheKey key)
    {
      _itemNotFoundCache.TryAdd(key, true);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AddItemNotFound: Added key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Retrieves cached shelves for an item.
    /// </summary>
    /// <param name="key">The cache key to query.</param>
    /// <param name="shelves">The list of shelves and quantities if found; otherwise, null.</param>
    /// <returns>True if the cache exists; otherwise, false.</returns>
    public static bool TryGetCachedShelves(CacheKey key, out List<KeyValuePair<PlaceableStorageEntity, int>> shelves)
    {
      bool found = _shelfSearchCache.TryGetValue(key, out shelves);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"TryGetCachedShelves: {(found ? "Hit" : "Miss")} for key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}, shelves={shelves?.Count ?? 0}",
          DebugLogger.Category.Storage);
      return found;
    }

    /// <summary>
    /// Updates the shelf search cache with a new quantity.
    /// </summary>
    /// <param name="key">The cache key to update.</param>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="quantity">The new quantity.</param>
    public static void UpdateShelfSearchCache(CacheKey key, PlaceableStorageEntity shelf, int quantity)
    {
      if (quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"UpdateShelfSearchCache: Skipping update for key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}, shelf={shelf.GUID}, qty={quantity}",
            DebugLogger.Category.Storage);
        return;
      }

      _shelfSearchCache.AddOrUpdate(
          key,
          _ => new List<KeyValuePair<PlaceableStorageEntity, int>> { new(shelf, quantity) },
          (_, list) =>
          {
            list.RemoveAll(kvp => kvp.Key == shelf);
            list.Add(new(shelf, quantity));
            return list;
          });

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateShelfSearchCache: Updated key={key.ID}{(key.Quality.HasValue ? $" Quality={key.Quality}" : "")}, shelf={shelf.GUID}, qty={quantity}",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Removes specified cache entries from the shelf search cache.
    /// </summary>
    /// <param name="keys">The cache keys to remove.</param>
    public static void RemoveShelfSearchCacheEntries(IEnumerable<CacheKey> keys)
    {
      int count = 0;
      foreach (var key in keys)
      {
        if (_shelfSearchCache.TryRemove(key, out _))
          count++;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"RemoveShelfSearchCacheEntries: Removed {count} entries",
          DebugLogger.Category.Storage);
    }

    /// <summary>
    /// Updates shelf cache with current quantities and removes obsolete search cache entries.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="itemQuantities">The current item quantities on the shelf.</param>
    /// <param name="property">The property containing the shelf.</param>
    internal static void UpdateShelfCache(PlaceableStorageEntity shelf, Dictionary<ItemInstance, int> itemQuantities, Property property)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateShelfCache: Updating for shelf={shelf.GUID}, items={itemQuantities.Count}",
          DebugLogger.Category.Storage);

      // Collect keys to remove from search cache
      var cacheKeysToRemove = new List<CacheKey>();
      foreach (var kvp in _shelfCache)
      {
        if (!kvp.Value.TryGetValue(shelf, out var shelfInfo)) continue;
        if (!itemQuantities.ContainsKey(kvp.Key) || itemQuantities[kvp.Key] <= 0)
        {
          kvp.Value.TryRemove(shelf, out _);
          if (kvp.Value.IsEmpty)
            _shelfCache.TryRemove(kvp.Key, out _);
          cacheKeysToRemove.Add(Utilities.CreateCacheKey(kvp.Key, property));
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateShelfCache: Removed item={kvp.Key.ID}{(kvp.Key is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")} from shelf={shelf.GUID}",
              DebugLogger.Category.Storage);
        }
      }

      // Update or add items
      foreach (var kvp in itemQuantities)
      {
        var item = kvp.Key;
        var qty = kvp.Value;
        if (qty <= 0) continue;

        var shelfDict = _shelfCache.GetOrAdd(item, _ => new ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>());
        bool isConfigured = Configs.TryGetValue(shelf.ParentProperty, out var configs) &&
                           configs.TryGetValue(shelf.GUID, out var config) &&
                           config.Mode == StorageMode.Specific &&
                           config.AssignedItem?.AdvCanStackWith(item) == true;
        shelfDict[shelf] = new ShelfInfo(qty, isConfigured);

        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"UpdateShelfCache: Updated item={item.ID}{(item is QualityItemInstance qual ? $" Quality={qual.Quality}" : "")}, qty={qty}, configured={isConfigured} for shelf={shelf.GUID}",
            DebugLogger.Category.Storage);
      }

      // Remove obsolete search cache entries
      if (cacheKeysToRemove.Any())
      {
        RemoveShelfSearchCacheEntries(cacheKeysToRemove);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"UpdateShelfCache: Removed {cacheKeysToRemove.Count} obsolete cache entries for shelf={shelf.GUID}",
            DebugLogger.Category.Storage);
      }

      // Sync ShelfCache
      foreach (var kvp in _shelfCache)
      {
        if (!kvp.Value.TryGetValue(shelf, out var shelfInfo)) continue;
        if (!itemQuantities.ContainsKey(kvp.Key) || itemQuantities[kvp.Key] <= 0)
        {
          kvp.Value.TryRemove(shelf, out _);
          if (kvp.Value.IsEmpty)
            _shelfCache.TryRemove(kvp.Key, out _);
        }
      }

      foreach (var kvp in itemQuantities)
      {
        var item = kvp.Key;
        var qty = kvp.Value;
        if (qty <= 0) continue;

        var shelfDict = _shelfCache.GetOrAdd(item, _ => new ConcurrentDictionary<PlaceableStorageEntity, ShelfInfo>());
        bool isConfigured = Configs.TryGetValue(shelf.ParentProperty, out var configs) &&
                           configs.TryGetValue(shelf.GUID, out var config) &&
                           config.Mode == StorageMode.Specific &&
                           config.AssignedItem?.AdvCanStackWith(item) == true;
        shelfDict[shelf] = new ShelfInfo(qty, isConfigured);
      }
    }

    /// <summary>
    /// Updates AnyQualityCache for a shelf.
    /// </summary>
    /// <param name="shelf">The shelf to update.</param>
    /// <param name="itemQuantities">The current item quantities on the shelf.</param>
    /// <param name="property">The property containing the shelf.</param>
    internal static void UpdateAnyQualityCache(PlaceableStorageEntity shelf, Dictionary<ItemInstance, int> itemQuantities, Property property)
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
            .Where(k => k.Item1 == property && k.Item2.HasValue && item.Quality > k.Item2)
            .ToList();
        foreach (var threshold in qualityThresholds)
        {
          var shelfInfo = new ShelfInfo(itemQuantities[item], _shelfCache[item][shelf].IsConfigured);
          _anyQualityCache[threshold].Add(new KeyValuePair<PlaceableStorageEntity, ShelfInfo>(shelf, shelfInfo));
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateAnyQualityCache: Added shelf={shelf.GUID} for quality>{threshold.Item2}",
              DebugLogger.Category.Storage);
        }
      }
    }

    /// <summary>
    /// Cleans up all caches and unsubscribes from TimeManager ticks.
    /// </summary>
    public static void Cleanup()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, "CacheManager.Cleanup: Unsubscribing and clearing caches", DebugLogger.Category.Storage);
      InstanceFinder.TimeManager.OnTick -= ProcessPendingUpdates;
      _itemNotFoundCache.Clear();
      _shelfSearchCache.Clear();
      _shelfCache.Clear();
      _anyQualityCache.Clear();
      _specificShelvesCache.Clear();
      _propertyDataCache.Clear();
      _pendingUpdates.Clear();
      _pendingStationUpdates.Clear();
    }
  }
}