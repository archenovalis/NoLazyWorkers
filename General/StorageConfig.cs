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
using static NoLazyWorkers.General.StorageExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.General.StorageConfigUtilities;
using static NoLazyWorkers.General.StorageConstants;
using FishNet.Managing;
using FishNet.Managing.Object;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Persistence;
using NoLazyWorkers.Employees;
using ScheduleOne.Employees;
using System.Collections.Concurrent;
using Unity.Collections;
using GameKit.Utilities;

namespace NoLazyWorkers.General
{
  public static class StorageExtensions
  {
    public static ConfigPanel ConfigPanelTemplate { get; set; }
    public static Sprite CrossSprite { get; set; }
    public static Dictionary<Guid, StorageConfiguration> Configs { get; } = new();
    public static Dictionary<Guid, PlaceableStorageEntity> Storages { get; } = new();
    public static Dictionary<Guid, JObject> PendingConfigData { get; } = new();
    public static Dictionary<Property, List<PlaceableStorageEntity>> AnyShelves { get; } = new();
    public static Dictionary<Property, Dictionary<ItemInstance, List<PlaceableStorageEntity>>> SpecificShelves { get; } = new();
    public static Dictionary<ItemInstance, Dictionary<PlaceableStorageEntity, ShelfInfo>> ShelfCache { get; } = new();
    public static Dictionary<(Property, EQuality?), List<KeyValuePair<PlaceableStorageEntity, ShelfInfo>>> AnyQualityCache { get; } = new();

    public enum StorageMode
    {
      None,    // No items allowed (red cross)
      Any,     // Any item allowed (blank)
      Specific // Specific item allowed (item's icon or packaged icon)
    }

    public class StorageItemOption : ItemSelector.Option
    {
      public ItemDefinition PackagingDefinition { get; }

      public StorageItemOption(string title, ItemDefinition item, ItemDefinition packagingDefinition)
          : base(title, item)
      {
        PackagingDefinition = packagingDefinition;
      }
    }

    public readonly struct CacheKey : IEquatable<CacheKey>
    {
      public readonly string ID;
      public readonly string PackagingId;
      public readonly EQuality? Quality;
      public readonly Property Property;

      public CacheKey(string id, string packagingId, EQuality? quality, Property property)
      {
        ID = id;
        PackagingId = packagingId;
        Quality = quality;
        Property = property;
      }

      public bool Equals(CacheKey other) =>
          ID == other.ID && PackagingId == other.PackagingId && Quality == other.Quality && ReferenceEquals(Property, other.Property);

      public override bool Equals(object obj) => obj is CacheKey other && Equals(other);

      public override int GetHashCode() => HashCode.Combine(ID, PackagingId, Quality, Property);
    }

    public struct ShelfInfo
    {
      public int Quantity { get; set; }
      public bool IsConfigured { get; set; }

      public ShelfInfo(int quantity, bool isConfigured)
      {
        Quantity = quantity;
        IsConfigured = isConfigured;
      }
    }
  }

  public static class StorageUtilities
  {
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

    /// <summary>
    /// Retrieves output slots containing items matching the template item.
    /// When allowHigherQuality is true, the slot's item quality can be ≥ the target item's quality.
    /// </summary>
    public static List<ItemSlot> GetOutputSlotsContainingTemplateItem(ITransitEntity entity, ItemInstance item, bool allowHigherQuality = false)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetOutputSlotsContainingTemplateItem: Start for item={item?.ID ?? "null"}, entity={entity?.GUID.ToString() ?? "null"}, allowHigherQuality={allowHigherQuality}",
          DebugLogger.Category.Storage);

      if (entity == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GetOutputSlotsContainingTemplateItem: Invalid input (entity={entity != null}, item={item != null})",
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
              $"GetOutputSlotsContainingTemplateItem: Found slot with item={slot.ItemInstance.ID}, quality={(slot.ItemInstance as ProductItemInstance)?.Quality}, qty={slot.Quantity}",
              DebugLogger.Category.Storage);
          result.Add(slot);
        }
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"GetOutputSlotsContainingTemplateItem: Found {result.Count} slots for item={item.ID}, quality={(item as ProductItemInstance)?.Quality}",
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
    /// Updates the storage cache with current shelf contents.
    /// </summary>
    public static void UpdateStorageCache(PlaceableStorageEntity shelf)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateStorageCache: Start for shelf={shelf?.GUID.ToString() ?? "null"}",
          DebugLogger.Category.Storage);

      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "UpdateStorageCache: Shelf is null", DebugLogger.Category.Storage);
        return;
      }

      lock (_lock)
      {
        var outputSlots = shelf.OutputSlots.ToArray();
        var shelfGuid = shelf.GUID;
        var property = shelf.ParentProperty;
        var itemQuantities = new Dictionary<ItemInstance, int>(outputSlots.Length);

        // Collect current items
        foreach (var slot in outputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0)
            continue;

          if (!itemQuantities.TryGetValue(slot.ItemInstance, out int currentQty))
            itemQuantities[slot.ItemInstance] = 0;
          itemQuantities[slot.ItemInstance] += slot.Quantity;

          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateStorageCache: Found {slot.Quantity} of {slot.ItemInstance.ID}, quality={(slot.ItemInstance as ProductItemInstance)?.Quality}",
              DebugLogger.Category.Storage);
        }

        // Update ShelfCache
        UpdateShelfCache(shelf, shelfGuid, itemQuantities);

        // Clean up AnyQualityCache for this shelf
        var anyCacheKeysToRemove = AnyQualityCache
            .Where(kvp => kvp.Value.Any(r => r.Key == shelf))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in anyCacheKeysToRemove)
        {
          AnyQualityCache[key].RemoveAll(r => r.Key == shelf);
          if (!AnyQualityCache[key].Any())
            AnyQualityCache.Remove(key);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateStorageCache: Removed shelf={shelfGuid} from AnyQualityCache for key={key}",
              DebugLogger.Category.Storage);
        }

        // Rebuild AnyQualityCache for affected qualities
        foreach (var item in itemQuantities.Keys.OfType<ProductItemInstance>())
        {
          var qualityThresholds = AnyQualityCache.Keys
              .Where(k => k.Item1 == property && k.Item2.HasValue && item.Quality > k.Item2)
              .ToList();

          foreach (var threshold in qualityThresholds)
          {
            var shelfInfo = new ShelfInfo(itemQuantities[item], ShelfCache[item][shelf].IsConfigured);
            AnyQualityCache[threshold].Add(new KeyValuePair<PlaceableStorageEntity, ShelfInfo>(shelf, shelfInfo));
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateStorageCache: Added shelf={shelfGuid} to AnyQualityCache for quality>{threshold.Item2}",
                DebugLogger.Category.Storage);
          }
        }

        // Clean up obsolete cache entries
        if (property != null)
        {
          var previousItems = ShelfCache
              .Where(kvp => kvp.Value.ContainsKey(shelf))
              .Select(kvp => kvp.Key)
              .ToList();

          var cacheKeysToRemove = previousItems
              .Where(prevItem => !itemQuantities.ContainsKey(prevItem) || itemQuantities[prevItem] <= 0)
              .Select(prevItem => StorageUtilities.CreateCacheKey(prevItem, property))
              .ToList();

          if (cacheKeysToRemove.Any())
          {
            CacheManager.RemoveShelfSearchCacheEntries(cacheKeysToRemove);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"UpdateStorageCache: Removed {cacheKeysToRemove.Count} obsolete cache entries for shelf={shelfGuid}",
                DebugLogger.Category.Storage);
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"UpdateStorageCache: Completed for shelf={shelf.GUID}, items={itemQuantities.Count}",
            DebugLogger.Category.Storage);
      }
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

      if (TryFindCachedShelf(employee, cacheKey, targetItem, needed, out var result))
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
          ShelfCache[key].Remove(shelf);
          if (ShelfCache[key].Count == 0)
            ShelfCache.Remove(key);
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
            shelfDict = new Dictionary<PlaceableStorageEntity, ShelfInfo>();
            ShelfCache[key] = shelfDict;
          }

          bool isConfigured = Configs.TryGetValue(shelfGuid, out var config) &&
                             config.Mode == StorageMode.Specific &&
                             config.AssignedItem?.AdvCanStackWith(key) == true;

          shelfDict[shelf] = new ShelfInfo(qty, isConfigured);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateShelfCache: Updated item={key.ID}, qty={qty}, configured={isConfigured} for shelf={shelfGuid}",
              DebugLogger.Category.Storage);
        }
      }
    }

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

    private static bool TryFindCachedShelf(Employee employee, CacheKey cacheKey, ItemInstance targetItem, int needed, out KeyValuePair<PlaceableStorageEntity, int> result)
    {
      result = new KeyValuePair<PlaceableStorageEntity, int>(null, 0);
      if (!CacheManager.TryGetCachedShelves(cacheKey, out var cachedShelves))
        return false;

      foreach (var cachedResult in cachedShelves)
      {
        if (cachedResult.Value >= needed &&
            Storages.TryGetValue(cachedResult.Key.GUID, out var shelf) &&
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

    private static bool TrySelectShelf(Employee employee, Dictionary<PlaceableStorageEntity, ShelfInfo> shelfDict,
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
      if (EmployeeExtensions.NoDropOffCache.TryGetValue(property, out var noDropOffCache) && noDropOffCache.Contains(item))
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
          if (!Configs.TryGetValue(candidate.GUID, out var config) ||
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

      if (!EmployeeExtensions.NoDropOffCache.TryGetValue(property, out noDropOffCache))
      {
        noDropOffCache = new List<ItemInstance>();
        EmployeeExtensions.NoDropOffCache.TryAdd(property, noDropOffCache);
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
      if (!Storages.ContainsKey(shelf.GUID))
      {
        error = $"Shelf {shelf.GUID} not found";
        return false;
      }
      if (!Configs.TryGetValue(shelf.GUID, out config))
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
      if (!EmployeeExtensions.NoDropOffCache.TryGetValue(property, out var cache))
      {
        cache = new List<ItemInstance>();
        EmployeeExtensions.NoDropOffCache.TryAdd(property, cache);
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
        shelfDict = new Dictionary<PlaceableStorageEntity, ShelfInfo>();
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
        itemCache.Remove(shelf);

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

  public static class StorageConstants
  {
    public const int StorageEnum = 345543;
    public static class StorageInstanceIDs
    {
      public const string SmallStorageRack = "smallstoragerack";
      public const string MediumStorageRack = "mediumstoragerack";
      public const string LargeStorageRack = "largestoragerack";
      public const string Safe = "safe";
      public static readonly string[] All = [SmallStorageRack, MediumStorageRack, LargeStorageRack, Safe];
    };
  }

  public static class StorageConfigUtilities
  {
    public static void InitializeStorageModule()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, "InitializeStorageModule: Starting", DebugLogger.Category.Storage);
      GetStorageConfigPanelTemplate();
      CacheCrossSprite();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeStorageModule: ConfigPanelTemplate {(ConfigPanelTemplate != null ? "initialized" : "null")}", DebugLogger.Category.Storage);
    }

    public static bool InitializeStorage(Guid guid, PlaceableStorageEntity storage)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeStorage: Called for GUID: {guid}, storage: {(storage != null ? storage.name : "null")}", DebugLogger.Category.Storage);

        if (storage == null || guid == Guid.Empty)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeStorage: storage is null or guid is empty ({guid})", DebugLogger.Category.Storage);
          return false;
        }

        if (!Storages.ContainsKey(guid))
        {
          Storages[guid] = storage;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitializeStorage: Added storage to dictionary for GUID: {guid}, Storage count: {Storages.Count}", DebugLogger.Category.Storage);
        }

        var proxy = StorageConfigurableProxy.AttachToStorage(storage);
        if (proxy == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeStorage: Failed to attach proxy for GUID: {guid}", DebugLogger.Category.Storage);
          return false;
        }

        bool initialized = false;
        CoroutineRunner.Instance.RunCoroutineWithResult<bool>(WaitForProxyInitialization(guid, storage, proxy), success => initialized = success);

        return initialized;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InitializeStorage: Failed for GUID: {guid}, error: {e.Message}", DebugLogger.Category.Storage);
        return false;
      }
    }

    private static IEnumerator WaitForProxyInitialization(Guid guid, PlaceableStorageEntity storage, StorageConfigurableProxy proxy)
    {
      int retries = 30;
      float delay = 0.05f;
      for (int i = 0; i < retries; i++)
      {
        if (proxy != null && proxy.configuration != null)
        {
          Property property = storage.GetProperty(storage.transform);
          if (property != null)
          {
            storage.ParentProperty = property;
            proxy.CreateWorldspaceUI();
            property.AddConfigurable(proxy);

            DebugLogger.Log(DebugLogger.LogLevel.Info, $"WaitForProxyInitialization: Completed for GUID: {guid}", DebugLogger.Category.Storage);
            yield return true;
            yield break;
          }
        }
        yield return new WaitForSeconds(delay);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"WaitForProxyInitialization: Failed for GUID: {guid} after {retries} retries", DebugLogger.Category.Storage);
      yield return false;
    }

    public static void CacheCrossSprite()
    {
      CrossSprite = GetCrossSprite();
      if (CrossSprite == null)
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "CacheCrossSprite: Failed to load cross sprite", DebugLogger.Category.Storage);
      else
        DebugLogger.Log(DebugLogger.LogLevel.Info, "CacheCrossSprite: Successfully cached cross sprite", DebugLogger.Category.Storage);
    }

    public static Sprite GetCrossSprite()
    {
      if (CrossSprite != null)
        return CrossSprite;

      var prefab = GetPrefabGameObject("Supplier Stash");
      if (prefab == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetCrossSprite: Supplier Stash prefab not found", DebugLogger.Category.Storage);
        return null;
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"GetCrossSprite: Prefab hierarchy:\n{DebugTransformHierarchy(prefab.transform)}", DebugLogger.Category.Storage);
      }

      var iconObj = prefab.transform.Find("Dead drop hopper/Icon");
      if (iconObj == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetCrossSprite: Icon GameObject not found in Supplier Stash prefab", DebugLogger.Category.Storage);
        return null;
      }

      var renderer = iconObj.GetComponent<MeshRenderer>();
      if (renderer == null || renderer.material == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetCrossSprite: No MeshRenderer or material found on Icon GameObject", DebugLogger.Category.Storage);
        return null;
      }

      var texture = renderer.material.mainTexture as Texture2D;
      if (texture == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetCrossSprite: No Texture2D found in material", DebugLogger.Category.Storage);
        return null;
      }
      // Create Sprite from Texture2D
      var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
      if (sprite != null)
      {
        sprite.name = "Cross";
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetCrossSprite: Created sprite '{sprite.name}' from Texture2D '{texture.name}'", DebugLogger.Category.Storage);
      }
      CrossSprite = sprite;
      return sprite;
    }

    public static Sprite GetPackagedSprite(ItemDefinition product, ItemDefinition packaging)
    {
      if (product == null || packaging == null)
        return product?.Icon;

      var productIconManager = ProductIconManager.Instance;
      if (productIconManager == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetPackagedSprite: ProductIconManager instance is null", DebugLogger.Category.Storage);
        return product?.Icon;
      }

      var sprite = productIconManager.GetIcon(product.ID, packaging.ID);
      if (sprite == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "GetPackagedSprite: Sprite is null", DebugLogger.Category.Storage);
        return product?.Icon;
      }
      sprite.name = $"{product.Name} ({packaging.Name})";
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetPackagedSprite: Created sprite '{sprite.name}' for product {product.ID}, packaging {packaging.ID}", DebugLogger.Category.Storage);
      return sprite;
    }

    public static ConfigPanel GetStorageConfigPanelTemplate()
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "GetStorageConfigPanelTemplate: Starting",
            DebugLogger.Category.Storage);

        if (ConfigPanelTemplate != null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Returning cached template",
              DebugLogger.Category.Storage);
          return ConfigPanelTemplate;
        }

        // Get template from PotConfigPanel
        Transform storageTransform = GetTransformTemplateFromConfigPanel(EConfigurableType.Pot, "");
        if (storageTransform == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "GetStorageConfigPanelTemplate: storageTransform is null",
              DebugLogger.Category.Storage);
          return null;
        }

        GameObject storageObj = Object.Instantiate(storageTransform.gameObject);
        storageObj.name = "StorageConfigPanel";
        if (storageObj.GetComponent<CanvasRenderer>() == null)
        {
          storageObj.AddComponent<CanvasRenderer>();
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "GetStorageConfigPanelTemplate: Instantiated StorageConfigPanel",
            DebugLogger.Category.Storage);

        // Remove default script
        var defaultScript = storageObj.GetComponent<PotConfigPanel>();
        if (defaultScript != null)
        {
          Object.Destroy(defaultScript);
        }

        // Add StorageConfigPanel
        StorageConfigPanel configPanel = storageObj.GetComponent<StorageConfigPanel>();
        if (configPanel == null)
        {
          configPanel = storageObj.AddComponent<StorageConfigPanel>();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Added StorageConfigPanel",
              DebugLogger.Category.Storage);
        }

        // Remove unnecessary components
        foreach (string partName in new[] { "Additive2", "Additive3", "Botanist", "ObjectFieldUI", "SupplyUI", "Seed" })
        {
          var part = storageObj.transform.Find(partName);
          if (part != null)
          {
            Object.Destroy(part.gameObject);
          }
        }

        // Setup StorageItemUI
        Transform itemFieldUITransform = storageObj.transform.Find("Additive1");
        if (itemFieldUITransform == null)
        {
          // Fallback: Instantiate from PotConfigPanel prefab
          var potPanel = GetPrefabGameObject("PotConfigPanel");
          if (potPanel == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                "GetStorageConfigPanelTemplate: PotConfigPanel prefab not found",
                DebugLogger.Category.Storage);
            return null;
          }
          var templateUI = potPanel.GetComponentInChildren<ItemFieldUI>();
          if (templateUI == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                "GetStorageConfigPanelTemplate: ItemFieldUI not found in PotConfigPanel",
                DebugLogger.Category.Storage);
            return null;
          }
          itemFieldUITransform = Object.Instantiate(templateUI.gameObject, storageObj.transform, false).transform;
          itemFieldUITransform.name = "StorageItemUI";
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Instantiated StorageItemUI from prefab",
              DebugLogger.Category.Storage);
        }

        GameObject itemFieldUIObj = itemFieldUITransform.gameObject;
        itemFieldUIObj.SetActive(true);
        var itemFieldUI = itemFieldUIObj.GetComponent<ItemFieldUI>();
        if (itemFieldUI == null)
        {
          itemFieldUI = itemFieldUIObj.AddComponent<ItemFieldUI>();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Added ItemFieldUI to StorageItemUI",
              DebugLogger.Category.Storage);
        }
        if (itemFieldUIObj.GetComponent<CanvasRenderer>() == null)
        {
          itemFieldUIObj.AddComponent<CanvasRenderer>();
        }

        configPanel.StorageItemUI = itemFieldUI;
        if (configPanel.StorageItemUI == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "GetStorageConfigPanelTemplate: Failed to assign StorageItemUI",
              DebugLogger.Category.Storage);
          return null;
        }

        // Setup Title and Description
        TextMeshProUGUI titleText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>()
            .FirstOrDefault(t => t.gameObject.name == "Title");
        if (titleText != null)
        {
          titleText.text = "Assign Item";
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Set Title to 'Assign Item'",
              DebugLogger.Category.Storage);
        }
        TextMeshProUGUI descText = itemFieldUIObj.GetComponentsInChildren<TextMeshProUGUI>()
            .FirstOrDefault(t => t.gameObject.name == "Description");
        if (descText != null)
        {
          descText.text = "Select the item to assign to this shelf";
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Set Description",
              DebugLogger.Category.Storage);
        }

        // Setup QualityFieldUI
        var dryingRackPanelObj = GetPrefabGameObject("DryingRackPanel");
        if (dryingRackPanelObj == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "GetStorageConfigPanelTemplate: DryingRackPanel prefab not found",
              DebugLogger.Category.Storage);
          return null;
        }
        var qualityFieldUIObj = dryingRackPanelObj.GetComponentInChildren<QualityFieldUI>();
        if (qualityFieldUIObj == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "GetStorageConfigPanelTemplate: QualityFieldUI not found in DryingRackPanel",
              DebugLogger.Category.Storage);
          return null;
        }
        var qualityUIObj = Object.Instantiate(qualityFieldUIObj.gameObject, storageObj.transform, false);
        qualityUIObj.name = "QualityFieldUI";
        qualityUIObj.transform.Find("Description").gameObject.SetActive(false);
        qualityUIObj.transform.Find("Title").GetComponent<TextMeshProUGUI>().text = "Quality";
        qualityUIObj.SetActive(false);

        var qualityRect = qualityUIObj.GetComponent<RectTransform>();
        var itemFieldRect = itemFieldUIObj.GetComponent<RectTransform>();
        if (qualityRect != null && itemFieldRect != null)
        {
          qualityRect.anchoredPosition = new Vector2(itemFieldRect.anchoredPosition.x, itemFieldRect.anchoredPosition.y - 75f);
          itemFieldRect.anchoredPosition = new Vector2(itemFieldRect.anchoredPosition.x, itemFieldRect.anchoredPosition.y + 10f);
        }

        configPanel.QualityUI = qualityUIObj.GetComponent<QualityFieldUI>();
        if (configPanel.QualityUI == null)
        {
          configPanel.QualityUI = qualityUIObj.AddComponent<QualityFieldUI>();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              "GetStorageConfigPanelTemplate: Added QualityFieldUI to QualityFieldUI",
              DebugLogger.Category.Storage);
        }
        if (qualityUIObj.GetComponent<CanvasRenderer>() == null)
        {
          qualityUIObj.AddComponent<CanvasRenderer>();
        }

        ConfigPanelTemplate = configPanel;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "GetStorageConfigPanelTemplate: Template initialized successfully",
            DebugLogger.Category.Storage);
        return configPanel;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetStorageConfigPanelTemplate: Failed, error: {e.Message}\nStackTrace: {e.StackTrace}",
            DebugLogger.Category.Storage);
        return null;
      }
    }
  }

  /// <summary>
  /// Manages storage-related caches for thread-safe access and consistency.
  /// </summary>
  public static class CacheManager
  {
    private static readonly ConcurrentDictionary<CacheKey, bool> _itemNotFoundCache = new();
    private static readonly ConcurrentDictionary<CacheKey, List<KeyValuePair<PlaceableStorageEntity, int>>> _shelfSearchCache = new();

    /// <summary>
    /// Clears cache entries with zero quantities for a specific shelf.
    /// </summary>
    public static void ClearZeroQuantityEntries(PlaceableStorageEntity shelf)
    {
      if (shelf == null) return;

      var keysToRemove = _shelfSearchCache
          .Where(kvp => kvp.Value.Any(r => r.Key == shelf && r.Value <= 0))
          .Select(kvp => kvp.Key)
          .ToList();

      foreach (var key in keysToRemove)
      {
        _shelfSearchCache.AddOrUpdate(
            key,
            _ => new List<KeyValuePair<PlaceableStorageEntity, int>>(),
            (_, list) =>
            {
              list.RemoveAll(r => r.Key == shelf && r.Value <= 0);
              return list;
            });

        if (!_shelfSearchCache[key].Any())
          _shelfSearchCache.TryRemove(key, out _);

        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"CacheManager.ClearZeroQuantityEntries: Removed zero-quantity entry for shelf={shelf.GUID}, key={key.ID}",
            DebugLogger.Category.Storage);
      }
    }

    // Existing methods with enhanced logging
    public static bool IsItemNotFound(CacheKey key)
    {
      bool found = _itemNotFoundCache.ContainsKey(key);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"CacheManager.IsItemNotFound: {(found ? "Hit" : "Miss")} for key={key.ID}",
          DebugLogger.Category.Storage);
      return found;
    }

    public static void AddItemNotFound(CacheKey key)
    {
      _itemNotFoundCache.TryAdd(key, true);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"CacheManager.AddItemNotFound: Added key={key.ID}",
          DebugLogger.Category.Storage);
    }

    public static bool TryGetCachedShelves(CacheKey key, out List<KeyValuePair<PlaceableStorageEntity, int>> shelves)
    {
      bool found = _shelfSearchCache.TryGetValue(key, out shelves);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"CacheManager.TryGetCachedShelves: {(found ? "Hit" : "Miss")} for key={key.ID}, shelves={shelves?.Count ?? 0}",
          DebugLogger.Category.Storage);
      return found;
    }

    public static void UpdateShelfSearchCache(CacheKey key, PlaceableStorageEntity shelf, int quantity)
    {
      if (quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"CacheManager.UpdateShelfSearchCache: Skipping update for key={key.ID}, shelf={shelf.GUID}, qty={quantity}",
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
          $"CacheManager.UpdateShelfSearchCache: Updated key={key.ID}, shelf={shelf.GUID}, qty={quantity}",
          DebugLogger.Category.Storage);
    }

    public static void RemoveShelfSearchCacheEntries(IEnumerable<CacheKey> keys)
    {
      int count = 0;
      foreach (var key in keys)
      {
        if (_shelfSearchCache.TryRemove(key, out _))
          count++;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"CacheManager.RemoveShelfSearchCacheEntries: Removed {count} entries",
          DebugLogger.Category.Storage);
    }
  }

  public class StorageConfigPanel : ConfigPanel
  {
    [SerializeField]
    public ItemFieldUI StorageItemUI;
    [SerializeField]
    public QualityFieldUI QualityUI;
    private Button Button;

    private void InitializeUIComponents()
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            "StorageConfigPanel.InitializeUIComponents: Starting",
            DebugLogger.Category.Storage);

        // Initialize StorageItemUI
        if (StorageItemUI == null)
        {
          var storageItemUITransform = transform.Find("StorageItemUI");
          if (storageItemUITransform == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                "StorageConfigPanel.InitializeUIComponents: StorageItemUI transform not found",
                DebugLogger.Category.Storage);
            return;
          }
          StorageItemUI = storageItemUITransform.GetComponent<ItemFieldUI>();
          if (StorageItemUI == null)
          {
            StorageItemUI = storageItemUITransform.gameObject.AddComponent<ItemFieldUI>();
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                "StorageConfigPanel.InitializeUIComponents: Added ItemFieldUI",
                DebugLogger.Category.Storage);
          }
          if (StorageItemUI.gameObject.GetComponent<CanvasRenderer>() == null)
            StorageItemUI.gameObject.AddComponent<CanvasRenderer>();
        }
        StorageItemUI.gameObject.SetActive(true);

        // Initialize QualityUI
        if (QualityUI == null)
        {
          var qualityUITransform = transform.Find("QualityFieldUI");
          if (qualityUITransform == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                "StorageConfigPanel.InitializeUIComponents: QualityFieldUI transform not found",
                DebugLogger.Category.Storage);
            return;
          }
          QualityUI = qualityUITransform.GetComponent<QualityFieldUI>();
          if (QualityUI == null)
          {
            QualityUI = qualityUITransform.gameObject.AddComponent<QualityFieldUI>();
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                "StorageConfigPanel.InitializeUIComponents: Added QualityFieldUI",
                DebugLogger.Category.Storage);
          }
          if (QualityUI.gameObject.GetComponent<CanvasRenderer>() == null)
            QualityUI.gameObject.AddComponent<CanvasRenderer>();
        }
        QualityUI.gameObject.SetActive(false);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            "StorageConfigPanel.InitializeUIComponents: Completed",
            DebugLogger.Category.Storage);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StorageConfigPanel.InitializeUIComponents: Failed, error={e.Message}",
            DebugLogger.Category.Storage);
      }
    }

    public override void Bind(List<EntityConfiguration> configs)
    {
      try
      {
        transform.gameObject.SetActive(true);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"StorageConfigPanel.Bind: Binding {configs.Count} configs",
            DebugLogger.Category.Storage);

        // Initialize UI components (unchanged)
        InitializeUIComponents();

        List<ItemField> itemFieldList = new();
        List<QualityField> qualityList = new();
        bool hasNone = false, hasAny = false, hasSpecific = false;
        ItemInstance item = null;
        bool isConsistent = true;
        List<ItemSlot> itemSlots = new();

        foreach (var config in configs.OfType<StorageConfiguration>())
        {
          itemFieldList.Add(config.StorageItem);
          itemSlots.AddRange(config.Storage.StorageEntity.ItemSlots);
          qualityList.Add(config.Quality);

          switch (config.Mode)
          {
            case StorageMode.None:
              hasNone = true;
              break;
            case StorageMode.Any:
              hasAny = true;
              break;
            case StorageMode.Specific:
              hasSpecific = true;
              if (item == null)
              {
                item = config.AssignedItem;
              }
              else if (!item.AdvCanStackWith(config.AssignedItem, allowHigherQuality: true))
              {
                isConsistent = false;
                DebugLogger.Log(DebugLogger.LogLevel.Warning,
                    $"StorageConfigPanel.Bind: Inconsistent items detected",
                    DebugLogger.Category.Storage);
              }
              break;
          }
        }

        StorageItemUI.Bind(itemFieldList);
        QualityUI.Bind(qualityList);

        UpdateUIState(hasNone, hasAny, hasSpecific, isConsistent, item);

        Button = StorageItemUI.transform.Find("Selection")?.GetComponent<Button>();
        Button.onClick.RemoveAllListeners();
        Button.onClick.AddListener(() => OpenItemSelectorScreen(itemFieldList, "Assign Item", itemSlots));

        if (false) //DebugLogger.IsDebugMode
          AddCacheDebugUI();

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StorageConfigPanel.Bind: Bound {itemFieldList.Count} ItemFields",
            DebugLogger.Category.Storage);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StorageConfigPanel.Bind: Failed, error: {e.Message}",
            DebugLogger.Category.Storage);
      }
    }

    private void UpdateUIState(bool hasNone, bool hasAny, bool hasSpecific, bool isConsistent, ItemInstance item)
    {
      if (!isConsistent)
        StorageItemUI.SelectionLabel.text = "Mixed";
      else
      {
        if (hasNone)
          StorageItemUI.SelectionLabel.text = "None";
        else if (hasAny)
          StorageItemUI.SelectionLabel.text = "Any";
        else if (hasSpecific && item != null)
        {
          if (item is ProductItemInstance prodItem && prodItem.AppliedPackaging != null)
            StorageItemUI.IconImg.sprite = GetPackagedSprite(item.Definition, prodItem.AppliedPackaging);
          StorageItemUI.SelectionLabel.text = item.Definition.Name;
          QualityUI.gameObject.SetActive(item is ProductItemInstance);
        }
      }
    }

    private void AddCacheDebugUI()
    {
      var debugText = gameObject.AddComponent<TextMeshProUGUI>();
      debugText.text = "Cache Debug: Enable verbose logging for details";
      debugText.fontSize = 12;
      debugText.color = Color.gray;
      debugText.rectTransform.anchoredPosition = new Vector2(0, -100);
    }

    public void OpenItemSelectorScreen(List<ItemField> itemFields, string fieldName, List<ItemSlot> itemSlots)
    {
      if (itemFields == null || itemFields.Count == 0 || itemFields.Any(f => f == null))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "StorageConfigPanel: OpenItemSelectorScreen called with null or empty itemFields", DebugLogger.Category.Storage);
        return;
      }

      if (Singleton<ManagementInterface>.Instance?.ItemSelectorScreen == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "StorageConfigPanel: ItemSelectorScreen is null or ManagementInterface is not initialized", DebugLogger.Category.Storage);
        return;
      }

      ItemField primaryField = itemFields[0];
      var primaryConfig = (StorageConfiguration)primaryField.ParentConfig;
      List<ItemSelector.Option> list = [];
      ItemSelector.Option selectedOption = null;

      // Add None and Any options
      list.Add(new StorageItemOption("None", new ItemDefinition { Name = "None", ID = "None", Icon = GetCrossSprite() }, null));
      list.Add(new StorageItemOption("Any", new ItemDefinition { Name = "Any", ID = "Any" }, null));

      // Set selected option based on mode
      if (primaryConfig.Mode == StorageMode.None)
        selectedOption = list[0];
      else if (primaryConfig.Mode == StorageMode.Any)
        selectedOption = list[1];

      itemSlots.AddRange([.. Player.Local.Inventory]);
      Dictionary<string, List<(ItemDefinition product, ItemDefinition packaging)>> productOptions = [];
      foreach (var slot in itemSlots)
      {
        if (slot.ItemInstance == null)
          continue;

        var instance = slot.ItemInstance;
        ItemDefinition product = instance.Definition;
        ItemDefinition packaging = null;

        if (instance is ProductItemInstance productInstance && productInstance.AppliedPackaging != null)
          packaging = productInstance.AppliedPackaging;

        string key = packaging != null ? $"{product.ID}_{packaging.ID}" : product.ID;
        if (!productOptions.ContainsKey(key))
          productOptions[key] = [];
        productOptions[key].Add((product, packaging));
      }

      foreach (var pair in productOptions)
      {
        var (product, packaging) = pair.Value[0];
        if (product == null)
          continue;

        string title = packaging != null ? $"{product.Name} ({packaging.Name})" : product.Name;
        var opt = new StorageItemOption(title, product, packaging);
        list.Add(opt);

        if (primaryConfig.Mode == StorageMode.Specific &&
            primaryField.SelectedItem == product &&
            primaryConfig.AssignedItem is ProductItemInstance prodItem && prodItem.AdvCanStackWith(
                Registry.GetItem(product.ID).GetDefaultInstance(), allowHigherQuality: true))
        {
          selectedOption = opt;
        }
      }

      try
      {
        Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Initialize(fieldName, list, selectedOption, new Action<ItemSelector.Option>(selected =>
        {
          StorageMode newMode;
          ItemDefinition selectedItem = selected.Item;
          PackagingDefinition selectedPackaging = (selected is StorageItemOption storageOption ? storageOption?.PackagingDefinition : null) as PackagingDefinition;

          if (selected.Title == "None")
            newMode = StorageMode.None;
          else if (selected.Title == "Any")
            newMode = StorageMode.Any;
          else
            newMode = StorageMode.Specific;

          foreach (var itemField in itemFields)
          {
            ItemInstance item = null;
            var config = (StorageConfiguration)itemField.ParentConfig;
            if (newMode == StorageMode.Specific)
            {
              item = Registry.GetItem(selectedItem.ID).GetDefaultInstance();
              if (selectedPackaging != null)
                (item as ProductItemInstance).SetPackaging(selectedPackaging);
            }
            config.SetModeAndItem(newMode, item);
          }

          StorageItemUI.SelectionLabel.text = newMode == StorageMode.Any ? "Any" :
                                              newMode == StorageMode.None ? "None" :
                                              selectedItem?.Name ?? "None";
          StorageItemUI.gameObject.SetActive(true);

          DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigPanel: Selected mode: {newMode}, item: {selectedItem?.Name ?? "null"}, packaging: {selectedPackaging?.Name ?? "null"} for {fieldName}", DebugLogger.Category.Storage);
        }));

        Singleton<ManagementInterface>.Instance.ItemSelectorScreen.Open();
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfigPanel: Failed to open ItemSelectorScreen for {fieldName}, error: {e.Message}", DebugLogger.Category.Storage);
      }
    }
  }

  public class StorageConfiguration : EntityConfiguration
  {
    public ItemField StorageItem { get; private set; }
    public PlaceableStorageEntity Storage { get; private set; }
    public StorageConfigurableProxy Proxy { get; private set; }
    public StorageMode Mode { get; set; }
    public ItemInstance AssignedItem { get; set; }
    public QualityField Quality { get; private set; }

    public StorageConfiguration(ConfigurationReplicator replicator, IConfigurable configurable, PlaceableStorageEntity storage)
        : base(replicator, configurable)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfiguration: Initializing for GUID: {storage?.GUID}", DebugLogger.Category.Storage);

      if (replicator == null || configurable == null || storage == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfiguration: Null parameters - replicator: {replicator == null}, configurable: {configurable == null}, storage: {storage == null}, GUID: {storage?.GUID}", DebugLogger.Category.Storage);
        throw new ArgumentNullException("StorageConfiguration: One or more parameters are null");
      }

      replicator.Configuration = this;
      Proxy = configurable as StorageConfigurableProxy;
      Storage = storage;

      // Add event listener for content changes
      Storage.StorageEntity.onContentsChanged.AddListener(OnContentsChanged);

      try
      {
        Quality = new QualityField(this);
        Quality.onValueChanged.AddListener(quality =>
        {
          if (AssignedItem is ProductItemInstance prodItem)
            prodItem.SetQuality(quality);
          InvokeChanged();
        });
        Quality.SetValue(EQuality.Premium, network: false);

        StorageItem = new ItemField(this)
        {
          CanSelectNone = false,
          Options = []
        };
        StorageItem.onItemChanged.AddListener(_ =>
        {
          UpdateStorageConfiguration(Storage, AssignedItem);
          RefreshChanged();
        });
        Configs[storage.GUID] = this;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfiguration: Initialized for GUID: {storage.GUID}", DebugLogger.Category.Storage);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfiguration: Failed to initialize for GUID: {storage.GUID}, error: {e.Message}", DebugLogger.Category.Storage);
        throw;
      }
    }

    private void OnContentsChanged()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"StorageConfiguration.OnContentsChanged: GUID={Storage.GUID}",
          DebugLogger.Category.Storage);

      var outputSlots = Storage.OutputSlots.ToArray();
      var property = Storage.ParentProperty;
      if (property != null)
      {
        var cacheKeysToRemove = new List<CacheKey>();
        foreach (var slot in outputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0)
            continue;
          var item = slot.ItemInstance;
          var cacheKey = CreateCacheKey(item, property);
          if (CacheManager.IsItemNotFound(cacheKey))
          {
            cacheKeysToRemove.Add(cacheKey);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"StorageConfiguration.OnContentsChanged: Clearing not-found cache for {item.ID}",
                DebugLogger.Category.Storage);
          }
        }
        CacheManager.RemoveShelfSearchCacheEntries(cacheKeysToRemove);
      }

      UpdateStorageCache(Storage);
    }

    private void RefreshChanged()
    {
      var worldSpaceUI = (StorageUIElement)Proxy.WorldspaceUI;
      worldSpaceUI.RefreshUI();
      InvokeChanged();
    }

    public void Load(JObject jsonObject)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StorageConfiguration.Load: Loading for GUID={Storage.GUID}", DebugLogger.Category.Storage);

        if (jsonObject["StorageMode"] != null)
        {
          var str = jsonObject["StorageMode"].ToString();
          try
          {
            Mode = (StorageMode)Enum.Parse(typeof(StorageMode), str);
            if (Mode == StorageMode.Any)
            {
              if (!AnyShelves.TryGetValue(Storage.ParentProperty, out var anyShelves))
              {
                AnyShelves[Storage.ParentProperty] = new();
              }
              AnyShelves[Storage.ParentProperty].Add(Storage);
            }
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageConfiguration.Load: Invalid StorageMode '{str}' for GUID={Storage.GUID}, error: {e.Message}", DebugLogger.Category.Storage);
            Mode = StorageMode.None;
          }
        }
        else
        {
          Mode = StorageMode.None;
        }

        if (jsonObject["StorageItem"] != null)
        {
          string itemInstanceJson = jsonObject["StorageItem"].ToString();
          ItemInstance itemInstance = ItemDeserializer.LoadItem(itemInstanceJson);
          if (itemInstance != null)
          {
            StorageItem.SelectedItem = itemInstance.Definition;
            AssignedItem = itemInstance;
            if (itemInstance is ProductItemInstance && jsonObject["Quality"] != null)
              Quality.SetValue(Enum.Parse<EQuality>(jsonObject["Quality"].ToString()), false);
          }
          else
          {
            StorageItem.SelectedItem = null;
            AssignedItem = null;
          }
        }
        else
        {
          StorageItem.SelectedItem = null;
          AssignedItem = null;
        }

        UpdateStorageConfiguration(Storage, AssignedItem);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfiguration.Load: Failed for GUID={Storage.GUID}, error: {e.Message}", DebugLogger.Category.Storage);
      }
    }

    public void SetModeAndItem(StorageMode mode, ItemInstance item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"StorageConfiguration.SetModeAndItem: GUID={Storage.GUID}, mode={mode}, item={item?.ID ?? "null"}",
          DebugLogger.Category.Storage);

      Mode = mode;
      ItemInstance previousItem = AssignedItem;
      StorageItem.SelectedItem = mode == StorageMode.Specific ? item?.Definition : null;
      AssignedItem = mode == StorageMode.Specific ? item : null;

      if (mode == StorageMode.Specific && item != null && previousItem != null)
      {
        // Validate quality compatibility
        if (!item.AdvCanStackWith(previousItem, allowHigherQuality: true))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"StorageConfiguration.SetModeAndItem: Quality mismatch for {item.ID}, resetting to default",
              DebugLogger.Category.Storage);
          if (item is ProductItemInstance prodItem)
            prodItem.SetQuality(EQuality.Premium);
        }
      }

      StorageItem.onItemChanged?.Invoke(StorageItem.SelectedItem);
      UpdateStorageConfiguration(Storage, AssignedItem);
      RefreshChanged();
    }

    // Cleanup to prevent memory leaks
    public void Dispose()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StorageConfiguration.Dispose: GUID={Storage.GUID}",
          DebugLogger.Category.Storage);

      Storage.StorageEntity.onContentsChanged.RemoveListener(OnContentsChanged);
      Configs.Remove(Storage.GUID);

      var property = Storage.ParentProperty;
      if (property != null)
      {
        // Clear all related cache entries
        var cacheKeysToRemove = ShelfCache
            .Where(kvp => kvp.Value.ContainsKey(Storage))
            .Select(kvp => CreateCacheKey(kvp.Key, property))
            .ToList();
        CacheManager.RemoveShelfSearchCacheEntries(cacheKeysToRemove);
      }

      RemoveStorageFromLists(Storage);
    }
  }

  [HarmonyPatch(typeof(PlaceableStorageEntity))]
  public class PlaceableStorageEntityPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("Awake")]
    static bool AwakePrefix(PlaceableStorageEntity __instance)
    {
      if (!__instance.gameObject.GetComponent<ConfigurationReplicator>())
      {
        __instance.gameObject.AddComponent<ConfigurationReplicator>();
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PlaceableStorageEntityPatch.Awake: Added ConfigurationReplicator to {__instance.gameObject.name}", DebugLogger.Category.Storage);
      }
      return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(PlaceableStorageEntity __instance, ref string __result)
    {
      if (!Configs.TryGetValue(__instance.GUID, out var config))
        return;

      JObject jsonObject = string.IsNullOrEmpty(__result) ? new JObject() : JObject.Parse(__result);
      jsonObject["StorageMode"] = config.Mode.ToString();

      if (config.StorageItem.SelectedItem != null)
      {
        var item = config.AssignedItem;
        jsonObject["StorageItem"] = item.GetItemData().GetJson(true);
        if (item is ProductItemInstance prodItem)
        {
          jsonObject["Quality"] = prodItem.Quality.ToString();
        }
      }

      __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
    }
  }

  [HarmonyPatch(typeof(StorageRackLoader))]
  public class StorageRackLoaderPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Load")]
    static void LoadPostfix(StorageRackLoader __instance, string mainPath)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StorageRackLoaderPatch: Processing for {mainPath}", DebugLogger.Category.Storage);
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageRackLoaderPatch: No GridItem found for mainPath: {mainPath}", DebugLogger.Category.Storage);
          return;
        }
        if (gridItem is not PlaceableStorageEntity storage)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageRackLoaderPatch: GridItem is not a PlaceableStorageEntity for mainPath: {mainPath}", DebugLogger.Category.Storage);
          return;
        }
        string dataPath = Path.Combine(mainPath, "Data.json");
        if (!File.Exists(dataPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageRackLoaderPatch: No Data.json found at: {dataPath}", DebugLogger.Category.Storage);
          return;
        }
        string json = File.ReadAllText(dataPath);
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"StorageRackLoaderPatch: Read JSON for GUID: {gridItem.GUID}, JSON: {json}", DebugLogger.Category.Storage);
        JObject jsonObject = JObject.Parse(json);
        PendingConfigData[storage.GUID] = jsonObject;
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"StorageRackLoaderPatch: Stored pending config data for GUID: {gridItem.GUID}, StorageMode={jsonObject["StorageMode"]}, StorageItem={jsonObject["StorageItem"]}", DebugLogger.Category.Storage);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageRackLoaderPatch: Failed for {mainPath}, error: {e}", DebugLogger.Category.Storage);
      }
    }
  }

  public class StorageConfigurableProxy : NetworkBehaviour, IConfigurable
  {
    public PlaceableStorageEntity Storage { get; private set; }
    public StorageConfiguration configuration { get; private set; }
    public WorldspaceUIElement WorldspaceUI { get; set; }
    [SerializeField]
    public ConfigurationReplicator configReplicator { get; private set; }

    public static StorageConfigurableProxy AttachToStorage(PlaceableStorageEntity storage)
    {
      if (storage == null || storage.gameObject == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "StorageConfigurableProxy: storage or storage.gameObject is null", DebugLogger.Category.Storage);
        throw new ArgumentNullException(nameof(storage));
      }

      var existingProxy = storage.gameObject.GetComponent<StorageConfigurableProxy>();
      if (existingProxy != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: Proxy already exists for {storage.gameObject.name}, GUID: {storage.GUID}", DebugLogger.Category.Storage);
        return existingProxy;
      }

      var proxy = storage.gameObject.AddComponent<StorageConfigurableProxy>();
      proxy.Storage = storage;
      proxy.Initialize();
      return proxy;
    }

    private void Initialize()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: Initializing for {Storage.gameObject.name}, GUID: {Storage.GUID}", DebugLogger.Category.Storage);

      // Verify NetworkObject
      var networkObject = gameObject.GetComponent<NetworkObject>();
      if (networkObject == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfigurableProxy: No NetworkObject on {Storage.gameObject.name}, GUID: {Storage.GUID}", DebugLogger.Category.Storage);
        throw new InvalidOperationException("NetworkObject is required");
      }

      // Attach ConfigurationReplicator
      configReplicator = gameObject.GetComponent<ConfigurationReplicator>();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: Replicator: {configReplicator.name}, NetworkObject: {networkObject != null}, IsSpawned: {networkObject.IsSpawned}, GUID: {Storage.GUID}", DebugLogger.Category.Storage);

      configReplicator.NetworkInitializeIfDisabled();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: NetworkInitializeIfDisabled for ConfigurationReplicator on {Storage.GUID}", DebugLogger.Category.Storage);
      MelonCoroutines.Start(InitializeConfiguration());
    }

    private IEnumerator InitializeConfiguration()
    {
      yield return null;
      try
      {
        if (configReplicator == null || Storage == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfigurableProxy: configReplicator or storage is null for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
          yield break;
        }
        configuration = new StorageConfiguration(configReplicator, this, Storage);
        configReplicator.Configuration = configuration;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: Configuration created for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StorageConfigurableProxy: Failed to create StorageConfiguration for GUID: {Storage.GUID}, error: {e.Message}", DebugLogger.Category.Storage);
      }
      yield return MelonCoroutines.Start(LoadConfigurationWhenReady());
    }

    private IEnumerator LoadConfigurationWhenReady()
    {
      var networkObject = gameObject.GetComponent<NetworkObject>();
      if (networkObject == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadConfigurationWhenReady: No NetworkObject for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
        yield break;
      }
      int retries = 10;
      while (!networkObject.IsSpawned && retries > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"LoadConfigurationWhenReady: Waiting for NetworkObject to spawn for GUID: {Storage.GUID}, retries left: {retries}", DebugLogger.Category.Storage);
        yield return new WaitForSeconds(1f);
        retries--;
      }
      if (!networkObject.IsSpawned)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadConfigurationWhenReady: NetworkObject failed to spawn after retries for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
        yield break;
      }
      ManagedObjects.InitializePrefab(networkObject, -1);
      configReplicator._transportManagerCache = NetworkManager.TransportManager;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StorageConfigurableProxy: InitializePrefab for ConfigurationReplicator on {Storage.GUID}", DebugLogger.Category.Storage);
      yield return new WaitForSeconds(1f);
      if (PendingConfigData.TryGetValue(Storage.GUID, out var data))
      {
        try
        {
          configuration.Load(data);
          if (WorldspaceUI != null)
            ((StorageUIElement)WorldspaceUI).RefreshUI();
          UpdateStorageConfiguration(Storage, configuration.AssignedItem);

          // Network cache update
          if (IsServer)
            RpcUpdateClientCache(Storage.GUID, configuration.AssignedItem);

          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"LoadConfigurationWhenReady: Loaded for GUID={Storage.GUID}",
              DebugLogger.Category.Storage);
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"LoadConfigurationWhenReady: Failed for GUID={Storage.GUID}, error={e.Message}",
              DebugLogger.Category.Storage);
        }
        PendingConfigData.Remove(Storage.GUID);
      }

      yield return null;
    }

    [ServerRpc]
    private void RpcUpdateClientCache(Guid shelfGuid, ItemInstance item)
    {
      if (Storages.TryGetValue(shelfGuid, out var shelf))
      {
        UpdateStorageCache(shelf);
        TargetUpdateCache();
      }
    }

    [TargetRpc]
    private void TargetUpdateCache()
    {
      UpdateStorageCache(Storage);
    }

    private void OnEnable()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: OnEnable for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
    }

    private void OnDisable()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: OnDisable for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
    }

    private void OnDestroy()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageConfigurableProxy: OnDestroy for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
    }

    // IConfigurable implementation (unchanged)
    public EntityConfiguration Configuration => configuration;
    public ConfigurationReplicator ConfigReplicator => configReplicator;
    public EConfigurableType ConfigurableType => (EConfigurableType)StorageEnum;
    public NetworkObject CurrentPlayerConfigurer { get; set; }
    public Transform Transform => Storage.transform;
    public Transform UIPoint => Storage.transform;
    public bool CanBeSelected => true;
    public Sprite TypeIcon => typeIcon;
    public bool IsBeingConfiguredByOtherPlayer => CurrentPlayerConfigurer != null && !CurrentPlayerConfigurer.IsOwner;
    public Sprite typeIcon;
    public Property ParentProperty { get => Storage.ParentProperty; set => Storage.ParentProperty = value; }
    public bool IsDestroyed => Storage.IsDestroyed;
    public void ShowOutline(Color color)
    {
      Storage.ShowOutline(color);
    }
    public void HideOutline()
    {
      Storage.HideOutline();
    }
    public override void OnSpawnServer(NetworkConnection connection)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"OnSpawnServer: Called for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
      Storage.OnSpawnServer(connection);
      ((IItemSlotOwner)this).SendItemsToClient(connection);
      SendConfigurationToClient(connection);
    }

    public void SendConfigurationToClient(NetworkConnection conn)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"SendConfigurationToClient: Called for GUID: {Storage.GUID}, conn: {(conn != null ? conn.ClientId : -1)}", DebugLogger.Category.Storage);

      if (!conn.IsHost)
      {
        StartCoroutine(WaitForConfig());
      }

      IEnumerator WaitForConfig()
      {
        yield return new WaitUntil(() => configuration != null);
        configuration.ReplicateAllFields(conn);
      }
    }

    public WorldspaceUIElement CreateWorldspaceUI()
    {
      if (ParentProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateWorldspaceUI: ParentProperty is null for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
        return null;
      }
      GameObject uiPrefab = GetPrefabGameObject("PackagingStationUI");
      if (uiPrefab == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateWorldspaceUI: Failed to load PackagingStationUI prefab for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
        return null;
      }
      var uiInstance = Instantiate(uiPrefab, ParentProperty.WorldspaceUIContainer);
      if (uiInstance == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"CreateWorldspaceUI: uiInstance is null for GUID: {Storage.GUID}", DebugLogger.Category.Storage);
        return null;
      }
      uiInstance.name = "StorageUI";
      var component = uiInstance.AddComponent<StorageUIElement>();
      var iconImg = uiInstance.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.gameObject.name.Contains("Icon"));
      iconImg.sprite = null;
      var old = uiInstance.GetComponent<PackagingStationUIElement>();
      if (old != null)
      {
        var oldRectTransform = old.GetComponent<RectTransform>();
        var rectTransform = component.GetComponent<RectTransform>();
        rectTransform.anchoredPosition = oldRectTransform.anchoredPosition;
        rectTransform.anchorMax = oldRectTransform.anchorMax;
        rectTransform.anchorMin = oldRectTransform.anchorMin;
        rectTransform.offsetMax = oldRectTransform.offsetMax;
        rectTransform.offsetMin = oldRectTransform.offsetMin;
        rectTransform.position = oldRectTransform.position;
        Destroy(old);
      }
      component.Initialize(this);
      WorldspaceUI = component;
      return component;
    }

    public void DestroyWorldspaceUI()
    {
      if (WorldspaceUI != null)
      {
        WorldspaceUI.Destroy();
        WorldspaceUI = null;
      }
    }

    public void Selected() => Configuration?.Selected();
    public void Deselected() => Configuration?.Deselected();
    public void SetConfigurer(NetworkObject player) => CurrentPlayerConfigurer = player;
  }

  public class StorageUIElement : WorldspaceUIElement
  {
    public StorageConfigurableProxy Proxy { get; set; }
    public Image Icon { get; set; }

    public void Initialize(StorageConfigurableProxy proxy)
    {
      RectTransform = (RectTransform)transform;
      RectTransform containerRectTransform = (RectTransform)transform.Find("Outline");
      if (containerRectTransform != null)
        Container = containerRectTransform;
      else
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageUIElement.Initialize: Container (Outline) not found for GUID: {proxy.Storage.GUID}", DebugLogger.Category.Storage);

      var titleLabel = GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name.Contains("Title"));
      if (titleLabel != null)
        titleLabel.text = proxy.Storage.Name;
      else
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageUIElement.Initialize: No Title TextMeshProUGUI found for GUID: {proxy.Storage.GUID}", DebugLogger.Category.Storage);

      var iconImg = GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.gameObject.name.Contains("Icon"));
      if (iconImg != null)
        Icon = iconImg;
      else
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StorageUIElement.Initialize: No Icon Image found for GUID: {proxy.Storage.GUID}", DebugLogger.Category.Storage);
      if (proxy.configuration.Mode == default)
      {
        proxy.configuration.Mode = StorageMode.None;
        Icon.sprite = GetCrossSprite();
        Icon.color = Color.red;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StorageUIElement.Initialize: Sprite: {Icon.sprite?.name}", DebugLogger.Category.Storage);
      Proxy = proxy;
      Proxy.Configuration.onChanged.AddListener(RefreshUI);
      RefreshUI();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StorageUIElement.Initialize: Completed for GUID: {proxy.Storage.GUID}", DebugLogger.Category.Storage);
    }

    public void RefreshUI()
    {
      UpdateWorldspaceUIIcon();
    }

    public void UpdateWorldspaceUIIcon()
    {
      if (Proxy.WorldspaceUI == null || Icon == null)
        return;

      var config = Proxy.configuration;
      Sprite newSprite = null;
      Color iconColor = Color.white;

      if (config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"StorageUIElement.UpdateWorldspaceUIIcon: Config null for GUID={Proxy.Storage.GUID}",
            DebugLogger.Category.Storage);
      }
      else if (config.Mode == StorageMode.None)
      {
        newSprite = GetCrossSprite();
        iconColor = Color.red;
      }
      else if (config.Mode == StorageMode.Any)
      {
        newSprite = null;
        iconColor = Color.white;
      }
      else if (config.Mode == StorageMode.Specific && config.AssignedItem != null)
      {
        var defaultItem = Registry.GetItem(config.AssignedItem.ID).GetDefaultInstance();
        if (config.AssignedItem.AdvCanStackWith(defaultItem, allowHigherQuality: true))
          newSprite = GetPackagedSprite(config.AssignedItem.Definition,
              (config.AssignedItem as ProductItemInstance)?.AppliedPackaging);
        iconColor = Color.white;
      }

      Icon.sprite = newSprite;
      Icon.color = iconColor;
      Icon.enabled = newSprite != null;
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
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GridItemPatch: Initializing storage for GUID: {__instance.GUID}", DebugLogger.Category.Storage);
            InitializeStorage(__instance.GUID, entity);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GridItemPatch: {__instance.GUID} is not a PlaceableStorageEntity", DebugLogger.Category.Storage);
          }
        }
        return true;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GridItemPatch: Failed for GUID: {GUID}, error: {e.Message}", DebugLogger.Category.Storage);
        return true;
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("DestroyItem")]
    static void DestroyItemPostfix(GridItem __instance, bool callOnServer = true)
    {
      if (__instance is PlaceableStorageEntity storage)
      {
        if (Configs.TryGetValue(__instance.GUID, out var config))
          config.Dispose();
        var cacheKeys = ShelfCache
            .Where(kvp => kvp.Value.ContainsKey(storage))
            .Select(kvp => CreateCacheKey(kvp.Key, storage.ParentProperty))
            .ToList();
        CacheManager.RemoveShelfSearchCacheEntries(cacheKeys);
      }
      if (__instance is ITransitEntity)
        TransitDistanceCache.ClearCacheForEntity(__instance.GUID);
    }
  }

  [HarmonyPatch(typeof(ConfigurableType))]
  public class ConfigurableTypePatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetTypeName")]
    static bool GetTypeNamePrefix(EConfigurableType type, ref string __result)
    {
      if (type == (EConfigurableType)StorageEnum)
      {
        __result = "Storage Rack";
        return false;
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(ManagementInterface))]
  public class ManagementInterfacePatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetConfigPanelPrefab")]
    static bool GetConfigPanelPrefabPrefix(ManagementInterface __instance, EConfigurableType type, ref ConfigPanel __result)
    {
      if (type == (EConfigurableType)StorageEnum)
      {
        __result = GetStorageConfigPanelTemplate();
        return false;
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(ItemSelector))]
  public class ItemSelectorPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("CreateOptions")]
    static void CreateOptionsPostfix(ItemSelector __instance, List<ItemSelector.Option> options)
    {
      for (int i = 0; i < __instance.optionButtons.Count; i++)
      {
        var button = __instance.optionButtons[i];
        var btnComponent = button.GetComponent<Button>();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ItemSelectorPatch.CreateOptionsPostfix: Button {i}, Title={options[i].Title}, interactable={btnComponent?.interactable}, active={button.gameObject.activeSelf}", DebugLogger.Category.Storage);
        var option = options[i];
        if (option is StorageItemOption storageOption && storageOption.PackagingDefinition != null && storageOption.Item != null)
        {
          var icon = button.transform.Find("Icon").gameObject.GetComponent<Image>();
          icon.sprite = GetPackagedSprite(storageOption.Item, storageOption.PackagingDefinition);
          icon.gameObject.SetActive(icon.sprite != null);
        }
        if (option.Title == "None")
          button.transform.Find("Icon").gameObject.GetComponent<Image>().color = Color.red;
        if (option.Title == "Any")
          button.transform.Find("Icon").gameObject.GetComponent<Image>().gameObject.SetActive(false);
      }
    }
  }

  [HarmonyPatch(typeof(ItemSlot))]
  public class ItemSlotPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("ChangeQuantity")]
    public static void ChangeQuantityPostfix(ItemSlot __instance, int change, bool _internal)
    {
      // Skip if no change or slot is empty
      if (change == 0 || __instance.ItemInstance == null) return;

      var owner = __instance.SlotOwner as PlaceableStorageEntity;
      if (owner == null) return;

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ItemSlotPatch.ChangeQuantityPostfix: Slot={__instance.SlotIndex}, item={__instance.ItemInstance.ID}, change={change}, newQty={__instance.Quantity}, shelf={owner.GUID}",
          DebugLogger.Category.Storage);

      // Trigger cache update
      StorageUtilities.UpdateStorageCache(owner);
    }

    [HarmonyPostfix]
    [HarmonyPatch("SetStoredItem")]
    public static void SetStoredItemPostfix(ItemSlot __instance, ItemInstance instance, bool _internal)
    {
      var owner = __instance.SlotOwner as PlaceableStorageEntity;
      if (owner == null) return;

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ItemSlotPatch.SetStoredItemPostfix: Slot={__instance.SlotIndex}, item={(instance?.ID ?? "null")}, shelf={owner.GUID}",
          DebugLogger.Category.Storage);

      // Trigger cache update
      StorageUtilities.UpdateStorageCache(owner);
    }

    [HarmonyPostfix]
    [HarmonyPatch("ClearStoredInstance")]
    public static void ClearStoredInstancePostfix(ItemSlot __instance, bool _internal)
    {
      var owner = __instance.SlotOwner as PlaceableStorageEntity;
      if (owner == null) return;

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ItemSlotPatch.ClearStoredInstancePostfix: Slot={__instance.SlotIndex}, shelf={owner.GUID}",
          DebugLogger.Category.Storage);

      // Trigger cache update
      StorageUtilities.UpdateStorageCache(owner);
    }
  }
}