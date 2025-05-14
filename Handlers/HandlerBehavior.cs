// Summary: Defines core logic for NPC item movement and storage management in a Unity game mod.
//          Handles route planning, item pickup/delivery, and storage configuration using MelonLoader and Harmony patches.
// Role: Extends NPC behavior to manage item transfers between shelves, stations, and docks, and customizes storage rack behavior.
// Related Files: DebugLogger.cs, NavMeshUtility.cs, CoroutineRunner.cs, StorageConfigurableProxy.cs
// Dependencies: Unity, MelonLoader, HarmonyLib, Newtonsoft.Json
// Assumptions: All game fields are publicized at compile time; server-side logic runs on InstanceFinder.IsServer.

using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.General;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.General.GeneralExtensions;
using static NoLazyWorkers.Handlers.HandlerExtensions;
using static NoLazyWorkers.Handlers.HandlerBehaviourUtilities;
using static NoLazyWorkers.Handlers.HandlerBehaviourExtensions;
using static NoLazyWorkers.NoLazyUtilities;
using GameKit.Utilities;
using Beautify.Demos;
using FishNet.Object;
using Pathfinding.Examples;
using ScheduleOne.NPCs;
using UnityEngine.InputSystem;

namespace NoLazyWorkers.Handlers
{
  // Class: MoveItemBehaviourExtensions
  // Purpose: Manages slot reservations for MoveItemBehaviour to prevent NPC conflicts during item transfers.
  // Dependencies: DebugLogger, ItemSlot, MoveItemBehaviour
  // Assumptions: MoveItemBehaviour instances are tied to NPCs; ItemSlot supports locking.
  public static class HandlerBehaviourExtensions
  {
    private static readonly Dictionary<MoveItemBehaviour, ItemSlot> _reservedSlots = new();
    public static Dictionary<NPC, MoveItemBehaviour> ActiveBehaviours = new();

    // Method: GetOutputSlotsContainingTemplateItem
    // Purpose: Retrieves all output slots from an ITransitEntity that contain the specified item template and match the slot type.
    // Parameters:
    //   - entity: The ITransitEntity to search.
    //   - item: The ItemInstance template to match.
    //   - slotType: The ESlotType to filter (e.g., Output).
    // Returns: A List<ItemSlot> containing all matching slots that are not null, have the item, and are not locked.
    public static List<ItemSlot> GetOutputSlotsContainingTemplateItem(ITransitEntity entity, ItemInstance item)
    {
      if (entity == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"GetOutputSlotsContainingTemplateItem: Invalid input (entity={entity?.GUID}, item={item?.ID})",
            DebugLogger.Category.Handler);
        return new List<ItemSlot>();
      }

      var matchingSlots = entity.OutputSlots
          .Where(slot => slot != null &&
                         slot.ItemInstance != null &&
                         slot.ItemInstance.CanStackWith(item, false) &&
                         slot.Quantity > 0 &&
                         !slot.IsLocked)
          .ToList();

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetOutputSlotsContainingTemplateItem: Found {matchingSlots.Count} slots for item {item.ID} in {entity.GUID}",
          DebugLogger.Category.Handler);

      return matchingSlots;
    }

    // Method: SetReservedSlot
    // Purpose: Assigns a reserved slot to a MoveItemBehaviour instance for item pickup.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to associate with the slot.
    //   - slot: The ItemSlot to reserve.
    public static void SetReservedSlot(MoveItemBehaviour behaviour, ItemSlot slot)
    {
      _reservedSlots[behaviour] = slot;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"SetReservedSlot: Set slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}",
          DebugLogger.Category.Handler);
    }

    // Method: GetReservedSlot
    // Purpose: Retrieves the reserved slot for a MoveItemBehaviour instance.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to query.
    // Returns: The reserved ItemSlot, or null if none exists.
    public static ItemSlot GetReservedSlot(MoveItemBehaviour behaviour)
    {
      _reservedSlots.TryGetValue(behaviour, out var slot);
      return slot;
    }

    // Method: ReleaseReservations
    // Purpose: Releases the reserved slot for a MoveItemBehaviour instance, unlocking it.
    // Parameters:
    //   - behaviour: The MoveItemBehaviour instance to release.
    public static void ReleaseReservations(MoveItemBehaviour behaviour)
    {
      if (_reservedSlots.TryGetValue(behaviour, out var slot) && slot != null)
      {
        slot.RemoveLock();
        _reservedSlots.Remove(behaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ReleaseReservations: Released slot {slot.GetHashCode()} for behaviour {behaviour.GetHashCode()} for NPC={behaviour.Npc.fullName}",
            DebugLogger.Category.Handler);
      }
    }

    public static void ApplyLocks(this List<ItemSlot> slots, NPC npc, string reason)
    {
      foreach (var slot in slots.Where(s => s != null))
      {
        ApplyLocks(slot, npc, reason);
      }
    }
    public static void ApplyLocks(this ItemSlot slot, NPC npc, string reason)
    {
      slot.ApplyLock(npc.NetworkObject, reason);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ApplyLock: Locked slot {slot.GetHashCode()} for {reason} by NPC={npc.fullName}",
          DebugLogger.Category.Handler);
    }

    public static void RemoveLock(this List<ItemSlot> slots)
    {
      foreach (var slot in slots.Where(s => s != null))
      {
        slot.RemoveLock();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"RemoveLock: Unlocked slot {slot.GetHashCode()}",
            DebugLogger.Category.Handler);
      }
    }
  }

  // Class: HandlerBehaviourUtilities
  // Purpose: Provides utility methods for scanning game objects and creating transfer requests for NPC tasks.
  // Dependencies: Packager, IStationAdapter, LoadingDock, StorageExtensions, StorageUtilities, DebugLogger
  // Assumptions: Game objects (stations, docks, shelves) are accessible; IStationAdapter uses InsertSlot for input.
  public static class HandlerBehaviourUtilities
  {
    public static readonly int MAX_ROUTES_PER_CYCLE = 5;
    public const int PRIORITY_STATION_REFILL = 100;
    public const int PRIORITY_LOADING_DOCK = 50;
    public const int PRIORITY_SHELF_RESTOCK = 10;
    public static readonly Dictionary<Packager, float> _stateStartTimes = new();

    // Method: FindItemsNeedingMovement
    // Purpose: Identifies items that need to be moved for a given NPC based on the scan type, creating TransferRequests for valid routes.
    // Parameters:
    //   - npc: The Packager NPC to process.
    //   - lastScanType: The last scan type used to prioritize scan order.
    // Returns: A list of TransferRequests representing items to move.
    // Remarks: Uses GetOutputSlotsContainingTemplateItem to include multiple pickup slots for each TransferRequest, supporting combined quantities from multiple slots.
    public static List<TransferRequest> FindItemsNeedingMovement(Packager npc, PackagerPatch.ScanType lastScanType)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"FindItemsNeedingMovement: Starting for NPC={npc?.fullName}, LastScanType={lastScanType}",
          DebugLogger.Category.Handler);
      if (npc == null || npc.AssignedProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"FindItemsNeedingMovement: NPC or AssignedProperty is null for NPC={npc?.fullName}",
            DebugLogger.Category.Handler);
        return new List<TransferRequest>();
      }
      var property = npc.AssignedProperty;
      var requests = new List<TransferRequest>();
      var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();
      int maxRoutes = Mathf.Min(MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.FindAll(s => s.ItemInstance == null).Count);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Max routes limited to {maxRoutes}, inventory slots={npc.Inventory.ItemSlots.FindAll(s => s.ItemInstance == null).Count} for NPC={npc.fullName}",
          DebugLogger.Category.Handler);
      var scanOrder = new List<PackagerPatch.ScanType>
        {
            PackagerPatch.ScanType.StationRefill,
            PackagerPatch.ScanType.LoadingDock,
            PackagerPatch.ScanType.ShelfRestock
        };
      if (lastScanType != PackagerPatch.ScanType.None)
      {
        scanOrder.Remove(lastScanType);
        scanOrder.Add(lastScanType);
      }
      foreach (var scanType in scanOrder)
      {
        if (requests.Count >= maxRoutes) break;
        if (scanType == PackagerPatch.ScanType.StationRefill)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Checking stations for NPC={npc.fullName}",
              DebugLogger.Category.Handler);
          if (PropertyStations.TryGetValue(property, out var stations))
          {
            foreach (var station in stations)
            {
              if (requests.Count >= maxRoutes) break;
              if (station.IsInUse || station.HasActiveOperation)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: Skipping station {station.GUID} (in use or active operation) for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              var canGetToChecked = false;
              var itemFields = station.GetInputItemForProduct();
              foreach (var itemField in itemFields)
              {
                if (itemField?.SelectedItem == null || station.GetInputQuantity() >= station.StartThreshold)
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: Skipping itemField (null or sufficient quantity) for station {station.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                  continue;
                }
                var item = itemField.SelectedItem.GetDefaultInstance();
                var shelves = StorageUtilities.FindShelvesWithItem(npc, item, needed: 1);
                var source = shelves.Keys.FirstOrDefault(s => GetOutputSlotsContainingTemplateItem(s, item).Count > 0);
                if (source == null)
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No accessible shelf with item {item.ID} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                  continue;
                }
                var sourceSlots = GetOutputSlotsContainingTemplateItem(source, item);
                if (sourceSlots.Count == 0)
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No available output slots for item {item.ID} on shelf {source.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                  continue;
                }
                sourceSlots.ApplyLocks(npc, "Route planning lock");
                var destination = station.TransitEntity;
                if (destination == null)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Warning,
                      $"FindItemsNeedingMovement: Station transit entity null for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                  continue;
                }
                var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
                if (deliverySlots == null || deliverySlots.Count == 0)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No delivery slots available for item {item.ID} on station for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                  continue;
                }
                var totalAvailable = sourceSlots.Sum(slot => slot.Quantity);
                var quantity = Mathf.Min(totalAvailable, station.MaxProductQuantity - station.GetInputQuantity());
                if (quantity <= 0)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No quantity to transfer for item {item.ID} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                  continue;
                }
                try
                {
                  if (!canGetToChecked)
                  {
                    canGetToChecked = true;
                    if (!npc.movement.CanGetTo(station.GetAccessPoint(npc)))
                    {
                      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                          $"FindItemsNeedingMovement: Cannot navigate to station {station.GUID} for NPC={npc.fullName}",
                          DebugLogger.Category.Handler);
                      break;
                    }
                  }
                  var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), source, sourceSlots, destination, deliverySlots);
                  if (!pickupGroups.ContainsKey(source)) pickupGroups[source] = new List<TransferRequest>();
                  pickupGroups[source].Add(request);
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: Created StationRefill request for {quantity} of {item.ID} from {source.GUID} ({sourceSlots.Count} slots) to station {station.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                }
                catch (ArgumentNullException ex)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Error,
                      $"FindItemsNeedingMovement: Failed to create StationRefill TransferRequest: {ex.Message} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                }
                break;
              }
            }
          }
        }
        else if (scanType == PackagerPatch.ScanType.LoadingDock)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Checking loading docks for NPC={npc.fullName}",
              DebugLogger.Category.Handler);
          for (int i = 0; i < property.LoadingDocks.Length && requests.Count < maxRoutes; i++)
          {
            var dock = property.LoadingDocks[i];
            if (!dock.IsInUse)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"FindItemsNeedingMovement: Skipping loading dock {dock.GUID} (not in use) for NPC={npc.fullName}",
                  DebugLogger.Category.Handler);
              continue;
            }
            var canGetToChecked = false;
            foreach (var slots in dock.OutputSlots)
            {
              if (slots?.ItemInstance == null || slots.Quantity <= 0 || slots.IsLocked)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: Skipping slot (null, empty, or locked) on loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              var item = slots.ItemInstance;
              var sourceSlots = GetOutputSlotsContainingTemplateItem(dock, item);
              if (sourceSlots.Count == 0)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No available output slots for item {item.ID} on loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              sourceSlots.ApplyLocks(npc, "Route planning lock");
              var destination = StorageUtilities.FindShelfForDelivery(npc, item);
              if (destination == null)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No accessible shelf for item {item.ID} from loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              var deliverySlots = (destination as ITransitEntity).ReserveInputSlotsForItem(item, npc.NetworkObject);
              if (deliverySlots == null || deliverySlots.Count == 0)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No delivery slots available for item {item.ID} on shelf {destination.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              var totalAvailable = sourceSlots.Sum(slot => slot.Quantity);
              var quantity = Mathf.Min(totalAvailable, (destination as ITransitEntity).GetInputCapacityForItem(item, npc));
              if (quantity <= 0)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No quantity to transfer for item {item.ID} from loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              try
              {
                if (!canGetToChecked)
                {
                  canGetToChecked = true;
                  if (!npc.movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, npc).position))
                  {
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: Cannot navigate to loading dock {dock.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    break;
                  }
                }
                var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), dock, sourceSlots, destination, deliverySlots);
                if (!pickupGroups.ContainsKey(dock)) pickupGroups[dock] = new List<TransferRequest>();
                pickupGroups[dock].Add(request);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: Created LoadingDock request for {quantity} of {item.ID} from {dock.GUID} ({sourceSlots.Count} slots) to shelf {destination.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
              }
              catch (ArgumentNullException ex)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Error,
                    $"FindItemsNeedingMovement: Failed to create LoadingDock TransferRequest: {ex.Message} for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
              }
              if (pickupGroups[dock].Count >= maxRoutes) break;
            }
          }
        }
        else if (scanType == PackagerPatch.ScanType.ShelfRestock)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Checking shelf restocks for NPC={npc.fullName}",
              DebugLogger.Category.Handler);
          try
          {
            foreach (var anyShelf in StorageExtensions.AnyShelves)
            {
              if (requests.Count >= maxRoutes) break;
              if (anyShelf == null || anyShelf.OutputSlots == null)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Warning,
                    $"FindItemsNeedingMovement: Skipping null or invalid shelf for NPC={npc.fullName}",
                    DebugLogger.Category.Handler);
                continue;
              }
              foreach (var slot in anyShelf.OutputSlots)
              {
                try
                {
                  if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked)
                  {
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: Skipping invalid slot in shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    continue;
                  }
                  var item = slot.ItemInstance;

                  if (anyShelf.ParentProperty != null && RouteQueueManager.NoDestinationCache.TryGetValue(anyShelf.ParentProperty, out var cache) && cache.Any(i => i.CanStackWith(item)))
                  {
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: Skipping cached item {item.ID} for property {anyShelf.ParentProperty.name} in shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    continue;
                  }
                  slot.ApplyLocks(npc, "Route planning lock");
                  var assignedShelf = StorageUtilities.FindShelfForDelivery(npc, item, allowAnyShelves: false);
                  if (assignedShelf == null || assignedShelf == anyShelf)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No valid assigned shelf or cannot navigate for item {item.ID} from shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    continue;
                  }
                  var deliverySlots = (assignedShelf as ITransitEntity)?.ReserveInputSlotsForItem(item, npc.NetworkObject);
                  if (deliverySlots == null || deliverySlots.Count == 0)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No delivery slots available for item {item.ID} on shelf {assignedShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    continue;
                  }
                  var quantity = Mathf.Min(slot.Quantity, (assignedShelf as ITransitEntity).GetInputCapacityForItem(item, npc));
                  if (quantity <= 0)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No quantity to transfer for item {item.ID} from shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    continue;
                  }
                  var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
                  if (inventorySlot == null)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No available inventory slot for item {item.ID} from shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Handler);
                    continue;
                  }
                  var request = new TransferRequest(item, quantity, inventorySlot, anyShelf, new List<ItemSlot> { slot }, assignedShelf, deliverySlots);
                  if (!pickupGroups.ContainsKey(anyShelf)) pickupGroups[anyShelf] = new List<TransferRequest>();
                  pickupGroups[anyShelf].Add(request);
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: Created ShelfRestock request for {quantity} of {item.ID} from {anyShelf.GUID} to {assignedShelf.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                }
                catch (Exception ex)
                {
                  if (slot != null) slot.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Error,
                      $"FindItemsNeedingMovement: Error processing slot in shelf {anyShelf.GUID}: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={npc.fullName}",
                      DebugLogger.Category.Handler);
                }
                if (pickupGroups.ContainsKey(anyShelf) && pickupGroups[anyShelf].Count >= maxRoutes) break;
              }
            }
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Finished checking shelf restocks for NPC={npc.fullName}",
                DebugLogger.Category.Handler);
          }
          catch (Exception ex)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"FindItemsNeedingMovement: Failed to process shelf restocks: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={npc.fullName}",
                DebugLogger.Category.Handler);
            return requests;
          }
        }
      }
      foreach (var group in pickupGroups.OrderByDescending(g => g.Value.Max(r => GetPriority(r))))
      {
        var groupRequests = group.Value.OrderByDescending(r => GetPriority(r)).Take(maxRoutes).ToList();
        requests.AddRange(groupRequests);
        break;
      }
      foreach (var slot in pickupGroups.SelectMany(g => g.Value).Select(r => r.PickupSlots).SelectMany(s => s).Distinct())
      {
        if (!requests.Any(r => r.PickupSlots.Contains(slot)))
        {
          slot.RemoveLock();
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Released unused slot lock {slot.GetHashCode()} for NPC={npc.fullName}",
              DebugLogger.Category.Handler);
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Completed with {requests.Count} transfer requests for NPC={npc.fullName}",
          DebugLogger.Category.Handler);
      return requests;
    }

    // Method: GetPriority
    // Purpose: Calculates the priority of a transfer request based on its locations.
    // Parameters:
    //   - request: The TransferRequest to evaluate.
    // Returns: The priority value (PRIORITY_STATION_REFILL, PRIORITY_LOADING_DOCK, or PRIORITY_SHELF_RESTOCK).
    public static int GetPriority(TransferRequest request)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetPriority: Calculating priority for TransferRequest with PickupLocation={request.PickupLocation.GUID}, DeliveryLocation={request.DeliveryLocation.GUID}",
          DebugLogger.Category.Handler);
      int priority = request.DeliveryLocation is IStationAdapter
          ? PRIORITY_STATION_REFILL
          : request.PickupLocation is LoadingDock
              ? PRIORITY_LOADING_DOCK
              : PRIORITY_SHELF_RESTOCK;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetPriority: Returning priority={priority} for TransferRequest",
          DebugLogger.Category.Handler);
      return priority;
    }
  }

  // Class: RouteQueueManager
  // Purpose: Manages prioritized item transfer routes for a Packager NPC, handling pickup and delivery tasks.
  // Dependencies: Packager, TransitRoute, ItemInstance, ItemSlot, NavMeshUtility, CoroutineRunner, DebugLogger
  // Assumptions: Packager has valid Inventory and Movement components; routes are processed sequentially.
  public class RouteQueueManager
  {
    public static Dictionary<Property, List<ItemInstance>> NoDestinationCache = new();
    private readonly Packager _packager;
    public readonly List<PrioritizedRoute> _routeQueue = new();
    private readonly Dictionary<PrioritizedRoute, (float Time, string Reason)> _failedRoutes = new();
    public int RouteCount => _routeQueue.Count;
    public bool HasInventoryRoute() => _routeQueue.Any(r => r.TransitRoute == null);

    // Constructor
    // Purpose: Initializes a RouteQueueManager for a specific Packager NPC.
    // Parameters:
    //   - packager: The Packager NPC to manage routes for.
    // Throws: ArgumentNullException if packager is null.
    public RouteQueueManager(Packager packager)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
    }

    // Struct: PrioritizedRoute
    // Purpose: Represents a prioritized route for item transfer, including multiple pickup and delivery slots.
    // Fields:
    //   - TransitRoute: The route from pickup to delivery location.
    //   - Destination: The destination entity.
    //   - Item: The item to transfer.
    //   - Quantity: The number of items to transfer.
    //   - DestinationSlots: The list of destination slots.
    //   - PickupSlots: The list of pickup slots.
    //   - Priority: The priority of the route.
    public struct PrioritizedRoute
    {
      public TransitRoute TransitRoute;
      public ITransitEntity Destination;
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public List<ItemSlot> DestinationSlots;
      public List<ItemSlot> PickupSlots;
      public int Priority;

      // Constructor
      // Purpose: Initializes a PrioritizedRoute from a TransferRequest and priority.
      // Parameters:
      //   - request: The TransferRequest to base the route on.
      //   - priority: The priority of the route.
      public PrioritizedRoute(TransferRequest request, int priority)
      {
        TransitRoute = request.PickupLocation == null ? null : new TransitRoute(request.PickupLocation, request.DeliveryLocation);
        Destination = request.DeliveryLocation;
        Item = request.Item;
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot;
        DestinationSlots = request.DeliverySlots;
        PickupSlots = request.PickupSlots;
        Priority = priority;
      }
    }

    // Method: AddRoutes
    // Purpose: Adds a list of transfer requests to the route queue, prioritizing based on destination type.
    // Parameters:
    //   - requests: The list of TransferRequest objects to add.
    public void AddRoutes(List<TransferRequest> requests)
    {
      _routeQueue.Clear();
      int availableSlots = _packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      var filteredRequests = requests
          .Where(r => !_failedRoutes.Any(t => t.Key.Item.CanStackWith(r.Item) || Time.time > t.Value.Time + PackagerPatch.RETRY_DELAY))
          .OrderByDescending(GetPriority)
          .Take(availableSlots)
          .ToList();
      foreach (var request in filteredRequests)
      {
        int priority = GetPriority(request);
        if (request.PickupSlots.Count > 0)
        {
          request.PickupSlots.ApplyLocks(_packager, "Route pickup lock");
        }
        request.DeliverySlots.ApplyLocks(_packager, "Route destination lock");
        _routeQueue.Add(new PrioritizedRoute(request, priority));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"AddRoutes: Added route for {request.Item.ID} (qty={request.Quantity}) from {request.PickupLocation?.GUID} to {request.DeliveryLocation.GUID}, locked slots for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
      }
    }

    // Method: PickupItemsFromLocation
    // Purpose: Initiates item pickup from a specified location for a list of routes.
    // Parameters:
    //   - pickupLocation: The ITransitEntity to pick up items from.
    //   - routes: The list of PrioritizedRoute objects to process.
    public void PickupItemsFromLocation(ITransitEntity pickupLocation, List<PrioritizedRoute> routes)
    {
      try
      {
        if (routes.Count == 0 || _packager == null || pickupLocation == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"PickupItemsFromLocation: Invalid input (routes={routes.Count}, packager={_packager?.fullName}, location={pickupLocation?.GUID}) for NPC={_packager?.fullName}",
              DebugLogger.Category.Handler);
          PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
          _stateStartTimes[_packager] = Time.time;
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PickupItemsFromLocation: Attempting to move to {pickupLocation.GUID} with {routes.Count} routes (items={string.Join(", ", routes.Select(r => $"{r.Item.ID} (qty={r.Quantity})"))}) for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        var accessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PickupItemsFromLocation: Setting destination to {pickupLocation.GUID} at position {accessPoint.position} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Moving;
        _stateStartTimes[_packager] = Time.time;
        _packager.Movement.SetDestination(accessPoint.position);
        CoroutineRunner.Instance.RunCoroutine(WaitForMultiSlotPickup(pickupLocation, routes));
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"PickupItemsFromLocation: Exception: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        foreach (var route in routes)
        {
          _failedRoutes[route] = (Time.time, $"Exception: {ex.Message}");
          ReleaseRouteLocks(route);
          _routeQueue.Remove(route);
        }
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        _stateStartTimes[_packager] = Time.time;
      }
    }

    // Method: WaitForMultiSlotPickup
    // Purpose: Picks up items from multiple slots at a location to fulfill routes.
    // Parameters:
    //   - pickupLocation: The ITransitEntity to pick up from.
    //   - routes: The list of PrioritizedRoute objects to process.
    // Remarks: Supports picking up from multiple slots to meet the required quantity (e.g., 12 and 8 for a total of 20).

    private IEnumerator WaitForMultiSlotPickup(ITransitEntity pickupLocation, List<PrioritizedRoute> routes)
    {
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.PickingUp;
      yield return new WaitUntil(() => NavMeshUtility.IsAtTransitEntity(pickupLocation, _packager, 0.4f));
      var failedRoutes = new List<PrioritizedRoute>();

      try
      {
        foreach (var route in routes)
        {
          if (route.PickupSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"WaitForMultiSlotPickup: No pickup slots for {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                DebugLogger.Category.Handler);
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"No pickup slots at {pickupLocation.GUID}");
            continue;
          }

          int remainingQuantity = route.Quantity;
          foreach (var sourceSlot in route.PickupSlots)
          {
            if (sourceSlot == null || sourceSlot.ItemInstance == null ||
                !sourceSlot.ItemInstance.CanStackWith(route.Item, false))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"WaitForMultiSlotPickup: Invalid source slot for {route.Item.ID} (slotID={sourceSlot?.GetHashCode()}) at {pickupLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Handler);
              continue;
            }

            if (sourceSlot.IsLocked && sourceSlot.ActiveLock.LockOwner != _packager.NetworkObject)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"WaitForMultiSlotPickup: Slot {sourceSlot.GetHashCode()} locked by another owner for {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Handler);
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Slot locked by another owner");
              break;
            }

            sourceSlot.RemoveLock();
            int amountToGrab = Mathf.Min(remainingQuantity, sourceSlot.Quantity);
            if (amountToGrab <= 0)
            {
              sourceSlot.ApplyLocks(_packager, "Post-pickup lock");
              continue;
            }

            try
            {
              sourceSlot.ChangeQuantity(-amountToGrab, true);
              sourceSlot.ApplyLocks(_packager, "Post-pickup lock");
              route.InventorySlot.RemoveLock();
              route.InventorySlot.InsertItem(route.Item.GetCopy(amountToGrab));
              route.InventorySlot.ApplyLocks(_packager, "Post-pickup lock");
              _packager.SetAnimationTrigger_Networked(null, "GrabItem");
              remainingQuantity -= amountToGrab;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"WaitForMultiSlotPickup: Picked up {amountToGrab} of {route.Item.ID} from slot {sourceSlot.GetHashCode()} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Handler);
              if (remainingQuantity <= 0) break;
            }
            catch (Exception ex)
            {
              sourceSlot.ApplyLocks(_packager, "Post-pickup lock");
              DebugLogger.Log(DebugLogger.LogLevel.Error,
                  $"WaitForMultiSlotPickup: Failed to pick up {amountToGrab} of {route.Item.ID}: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
                  DebugLogger.Category.Handler);
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Pickup exception: {ex.Message}");
              break;
            }
          }

          if (remainingQuantity > 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"WaitForMultiSlotPickup: Could not pick up remaining {remainingQuantity} of {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
                DebugLogger.Category.Handler);
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"Insufficient quantity in slots");
          }
          else
          {
            _routeQueue.Remove(route);
          }
        }

        foreach (var failedRoute in failedRoutes)
        {
          ReleaseRouteLocks(failedRoute);
          _routeQueue.Remove(failedRoute);
        }

        if (routes.Any(r => !failedRoutes.Contains(r)))
        {
          ProcessDelivery();
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"WaitForMultiSlotPickup: No items picked up, transitioning to Idle for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
          _stateStartTimes[_packager] = Time.time;
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WaitForMultiSlotPickup: Coroutine exception: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        foreach (var route in routes)
        {
          _failedRoutes[route] = (Time.time, $"Coroutine exception: {ex.Message}");
          ReleaseRouteLocks(route);
          _routeQueue.Remove(route);
        }
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        _stateStartTimes[_packager] = Time.time;
      }
    }

    // Method: ProcessDelivery
    // Purpose: Initiates delivery of items for the first route in the queue.
    private void ProcessDelivery()
    {
      if (_routeQueue.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ProcessDelivery: No routes to process for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ProcessDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return;
      }

      var route = _routeQueue[0];
      var accessPoint = NavMeshUtility.GetAccessPoint(route.Destination, _packager);
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ProcessDelivery: No access point for {route.Destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        _failedRoutes[route] = (Time.time, $"No access point for {route.Destination.GUID}");
        HandleFailedDelivery(route);
        return;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ProcessDelivery: Moving to {route.Destination.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Moving;
      _packager.Movement.SetDestination(accessPoint.position);
      CoroutineRunner.Instance.RunCoroutine(WaitForDelivery());
    }

    // Method: WaitForDelivery
    // Purpose: Delivers items to multiple destination slots at the target location.
    // Remarks: Distributes items across destination slots based on capacity (e.g., 8 to a slot with 19, 1 to a slot with 0).
    private IEnumerator WaitForDelivery()
    {
      var route = _routeQueue[0];
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Delivering;
      yield return new WaitUntil(() => NavMeshUtility.IsAtTransitEntity(route.Destination, _packager, 0.4f));

      try
      {
        var itemInInventory = route.InventorySlot?.ItemInstance != null && route.InventorySlot.ItemInstance.ID == route.Item.ID ? route.InventorySlot.ItemInstance : null;
        if (itemInInventory == null || itemInInventory.Quantity < route.Quantity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForDelivery: Item {route.Item.ID} not found or insufficient quantity for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          _failedRoutes[route] = (Time.time, "Item not found or insufficient quantity");
          HandleFailedDelivery(route);
          yield break;
        }

        int remainingQuantity = route.Quantity;
        foreach (var destinationSlot in route.DestinationSlots)
        {
          if (destinationSlot.IsLocked && destinationSlot.ActiveLock.LockOwner != _packager.NetworkObject)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"WaitForDelivery: Slot {destinationSlot.GetHashCode()} locked by another owner for {route.Item.ID} at {route.Destination.GUID} for NPC={_packager.fullName}",
                DebugLogger.Category.Handler);
            _failedRoutes[route] = (Time.time, $"Slot locked by another owner");
            HandleFailedDelivery(route);
            yield break;
          }

          destinationSlot.RemoveLock();
          int capacity = destinationSlot.GetCapacityForItem(itemInInventory);
          int depositAmount = Mathf.Min(remainingQuantity, capacity);
          if (depositAmount <= 0)
          {
            destinationSlot.ApplyLocks(_packager, "Post-delivery lock");
            continue;
          }

          destinationSlot.InsertItem(itemInInventory.GetCopy(depositAmount));
          destinationSlot.ApplyLocks(_packager, "Post-delivery lock");
          route.InventorySlot.RemoveLock();
          itemInInventory.ChangeQuantity(-depositAmount);
          route.InventorySlot.ApplyLocks(_packager, "Post-delivery lock");
          remainingQuantity -= depositAmount;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"WaitForDelivery: Delivered {depositAmount} of {route.Item.ID} to slot {destinationSlot.GetHashCode()} at {route.Destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          if (remainingQuantity <= 0) break;
        }

        if (remainingQuantity > 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForDelivery: Could not deliver remaining {remainingQuantity} of {route.Item.ID} to {route.Destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          _failedRoutes[route] = (Time.time, $"Insufficient slot capacity");
          HandleFailedDelivery(route);
          yield break;
        }

        _packager.SetAnimationTrigger_Networked(null, "GrabItem");
        ReleaseRouteLocks(route); // Unlock all slots on successful delivery
        _routeQueue.RemoveAt(0);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"WaitForDelivery: Transitioned to Idle, checking for more routes for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        if (_routeQueue.Count > 0)
        {
          ProcessDelivery();
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WaitForDelivery: Exception: {ex.Message} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        _failedRoutes[route] = (Time.time, $"Exception: {ex.Message}");
        HandleFailedDelivery(route);
      }
    }

    // Method: HandleFailedDelivery
    // Purpose: Handles a failed delivery for a specific item, attempting to return it or reroute to a shelf.
    // Parameters:
    //   - item: The ItemInstance that failed delivery.
    public void HandleFailedDelivery(ItemInstance item)
    {
      var route = _routeQueue.FirstOrDefault(r => r.Item.ID == item.ID);
      if (route.Item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleFailedDelivery: No route found for item {item.ID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return;
      }
      var itemInInventory = route.InventorySlot.ItemInstance;
      if (itemInInventory == null)
      {
        ReleaseRouteLocks(route);
        _routeQueue.Remove(route);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return;
      }
      int returnQuantity = Mathf.Min(itemInInventory.Quantity, route.Quantity);
      foreach (var pickupSlot in route.PickupSlots)
      {
        int capacity = pickupSlot.GetCapacityForItem(itemInInventory);
        int amountToReturn = Mathf.Min(returnQuantity, capacity);
        if (amountToReturn <= 0) continue;
        pickupSlot.ChangeQuantity(amountToReturn);
        itemInInventory.ChangeQuantity(-amountToReturn);
        returnQuantity -= amountToReturn;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleFailedDelivery: Returned {amountToReturn} of {route.Item.ID} to pickup slot {pickupSlot.GetHashCode()} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        if (returnQuantity <= 0) break;
      }
      if (returnQuantity <= 0)
      {
        ReleaseRouteLocks(route);
        _routeQueue.Remove(route);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle after return for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return;
      }
    }


    // Method: HandleFailedDelivery
    // Purpose: Handles a failed delivery for a specific route, attempting to return it or reroute to a shelf.
    // Parameters:
    //   - route: The PrioritizedRoute that failed.
    public void HandleFailedDelivery(PrioritizedRoute route)
    {
      var itemInInventory = route.InventorySlot.ItemInstance;
      if (itemInInventory == null)
      {
        ReleaseRouteLocks(route);
        _routeQueue.Remove(route);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleFailedDelivery: Transitioned to Idle for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return;
      }

      // Attempt to return to pickup slots
      if (route.PickupSlots?.Count > 0)
      {
        int returnQuantity = Mathf.Min(itemInInventory.Quantity, route.Quantity);
        foreach (var pickupSlot in route.PickupSlots)
        {
          pickupSlot.RemoveLock();
          int capacity = pickupSlot.GetCapacityForItem(itemInInventory);
          int amountToReturn = Mathf.Min(returnQuantity, capacity);
          if (amountToReturn <= 0)
          {
            pickupSlot.ApplyLocks(_packager, "Post-return lock");
            continue;
          }

          pickupSlot.ChangeQuantity(amountToReturn);
          pickupSlot.ApplyLocks(_packager, "Post-return lock");
          route.InventorySlot.RemoveLock();
          itemInInventory.ChangeQuantity(-amountToReturn);
          route.InventorySlot.ApplyLocks(_packager, "Post-return lock");
          returnQuantity -= amountToReturn;
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleFailedDelivery: Returned {amountToReturn} of {route.Item.ID} to pickup slot {pickupSlot.GetHashCode()} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          if (returnQuantity <= 0) break;
        }

        if (returnQuantity <= 0)
        {
          ReleaseRouteLocks(route);
          _routeQueue.Remove(route);
          PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"HandleFailedDelivery: Transitioned to Idle after return for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          return;
        }
      }
    }

    // Method: HandlePlayerInsertedItem
    // Purpose: Processes items inserted into the NPC’s inventory by a player, routing them to appropriate shelves.
    // Parameters:
    //   - item: The ItemInstance inserted by the player.
    // Returns: True if a route was successfully added, false otherwise.
    public bool HandleInventoryItem(ItemInstance item)
    {
      if (item == null || item.Quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"HandleInventoryItem: Invalid item or quantity for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return false;
      }
      var destination = StorageUtilities.FindShelfForDelivery(_packager, item);
      if (destination == null)
      {
        NoDestinationCache[_packager.AssignedProperty].Add(item);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
        $"HandleInventoryItem: No accessible shelf for item {item.ID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return false;
      }
      var inventorySlots = _packager.Inventory.ItemSlots
          .Where(s => s != null && s.ItemInstance != null && s.ItemInstance.CanStackWith(item) && s.Quantity > 0)
          .ToList();
      bool success = false;
      foreach (var slot in inventorySlots)
      {
        var destinationSlots = (destination as ITransitEntity).ReserveInputSlotsForItem(item, _packager.NetworkObject);
        if (destinationSlots == null || destinationSlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"HandleInventoryItem: No delivery slots for item {item.ID} on {destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          continue;
        }
        int quantity = slot.Quantity; // Respect stack limit (20)
        if (quantity <= 0)
        {
          destinationSlots.RemoveLock();
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"HandleInventoryItem: No quantity to transfer for item {item.ID} in slot {slot.GetHashCode()} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          continue;
        }
        try
        {
          AddInventoryRoute(slot, item, quantity, destination, destinationSlots);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleInventoryItem: Added route for {quantity} of {item.ID} from slot {slot.GetHashCode()} to {destination.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          success = true;
        }
        catch (Exception ex)
        {
          destinationSlots.RemoveLock();
          DebugLogger.Log(DebugLogger.LogLevel.Error,
          $"HandleInventoryItem: Failed to add route for {item.ID}: {ex.Message} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
        }
      }

      return success;
    }

    // Method: AddInventoryRoute
    // Purpose: Adds an inventory route for an item in the NPC’s inventory, using inventory slots as pickup slots.
    // Parameters:
    //   - item: The ItemInstance to route.
    //   - quantity: The quantity to transfer.
    //   - destination: The target ITransitEntity.
    //   - destinationSlots: The reserved delivery slots.
    // Returns: A PrioritizedRoute if successful; throws ArgumentException on invalid input.
    // Remarks: Uses NPC inventory slots as PickupSlots and sets PickupLocation to null.
    private PrioritizedRoute AddInventoryRoute(ItemSlot inventorySlot, ItemInstance item, int quantity, ITransitEntity destination, List<ItemSlot> destinationSlots)
    {
      if (item == null || destination == null || destinationSlots == null || destinationSlots.Count == 0 || inventorySlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AddInventoryRoute: Invalid input for item={item?.ID}, destination={destination?.GUID}, slots={destinationSlots?.Count}, inventorySlot={inventorySlot?.GetHashCode()} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        throw new ArgumentException("Invalid input parameters for AddInventoryRoute");
      }
      if (quantity <= 0 || quantity > inventorySlot.Quantity || quantity > item.StackLimit)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
        $"AddInventoryRoute: Invalid quantity {quantity} for {item.ID} (slot qty={inventorySlot.Quantity}, stack limit={item.StackLimit}) to {destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        throw new ArgumentException($"Invalid quantity: {quantity}");
      }

      if (quantity > destination.GetInputCapacityForItem(item, _packager))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AddInventoryRoute: Quantity {quantity} exceeds capacity for {item.ID} at {destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        throw new ArgumentException($"Quantity exceeds destination capacity");
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AddInventoryRoute: Found locked inventory slots for item {item.ID} (requested={quantity}) for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AddInventoryRoute: Creating TransferRequest with {destinationSlots.Count} delivery slots for item {item.ID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        var request = new TransferRequest(item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, destinationSlots);
        var route = new PrioritizedRoute(request, 100);
        _routeQueue.Add(route);
        inventorySlot.ApplyLocks(_packager, "Inventory route inventory lock");
        destinationSlots.ApplyLocks(_packager, "Inventory route destination lock");
        DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"AddInventoryRoute: Added inventory route for {quantity} of {item.ID} from slot {inventorySlot.GetHashCode()} to {destination.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        return route;
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"AddInventoryRoute: Failed to add route for {item.ID}: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        throw;
      }
    }

    // Method: ProcessInventoryRoute
    // Purpose: Processes the first inventory route in the queue, initiating delivery.
    public void ProcessInventoryRoute()
    {
      if (_routeQueue.Count == 0 || _routeQueue[0].TransitRoute != null)
      {
        TransitionToIdle("No inventory routes");
        return;
      }
      var route = _routeQueue[0];
      var deliveryLocation = route.Destination;
      if (deliveryLocation == null)
      {
        HandleRouteFailure(route, "Delivery location is null");
        return;
      }
      try
      {
        var accessPoint = NavMeshUtility.GetAccessPoint(deliveryLocation, _packager);
        if (accessPoint == null)
        {
          HandleRouteFailure(route, $"No accessible access point for {deliveryLocation.GUID}");
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ProcessInventoryRoute: Moving to {deliveryLocation.GUID} for {route.Item.ID} (qty={route.Quantity}) for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Moving;
        _stateStartTimes[_packager] = Time.time;
        _packager.Movement.SetDestination(accessPoint.position);

        // Filter inventory routes for the same destination
        var routes = _routeQueue.Where(r => r.TransitRoute == null && r.Destination == deliveryLocation).ToList();
        CoroutineRunner.Instance.RunCoroutine(WaitForInventoryDelivery(deliveryLocation, routes));
      }
      catch (Exception ex)
      {
        HandleRouteFailure(route, $"Exception: {ex.Message}");
      }
    }

    // Helper method to transition to Idle state
    private void TransitionToIdle(string reason)
    {
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
      _stateStartTimes[_packager] = Time.time;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"TransitionToIdle: {reason} for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
    }

    // Helper method to handle route failure
    private void HandleRouteFailure(PrioritizedRoute route, string reason)
    {
      _failedRoutes[route] = (Time.time, reason);
      ReleaseRouteLocks(route);
      _routeQueue.Remove(route);
      TransitionToIdle($"Failed route for {route.Item.ID}: {reason}");
    }

    // Method: WaitForInventoryDelivery
    // Purpose: Delivers items from NPC inventory to multiple destination slots.
    // Parameters:
    //   - deliveryLocation: The ITransitEntity to deliver to.
    //   - routes: The list of inventory routes to process.
    // Remarks: Sources items from multiple inventory slots and distributes to destination slots.
    private IEnumerator WaitForInventoryDelivery(ITransitEntity deliveryLocation, List<PrioritizedRoute> routes)
    {
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Delivering;
      float timeout = Time.time + 30f;
      while (!NavMeshUtility.IsAtTransitEntity(deliveryLocation, _packager, 0.4f) && Time.time < timeout)
        yield return null;
      if (Time.time >= timeout)
      {
        foreach (var route in routes)
          HandleRouteFailure(route, $"Timed out reaching {deliveryLocation.GUID}");
        yield break;
      }
      var failedRoutes = new List<PrioritizedRoute>();
      try
      {
        foreach (var route in routes.ToList())
        {
          if (!ValidateRouteSlots(route, out var sourceSlot))
          {
            HandleRouteFailure(route, $"Validation Failure");
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"Validation Failure");
            continue;
          }
          int remainingQuantity = route.Quantity;
          foreach (var destinationSlot in route.DestinationSlots)
          {
            if (destinationSlot.IsLocked && destinationSlot.ActiveLock.LockOwner != _packager.NetworkObject)
            {
              HandleRouteFailure(route, $"Destination slot {destinationSlot.GetHashCode()} locked by another owner");
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Destination slot {destinationSlot.GetHashCode()} locked by another owner");
              break;
            }
            destinationSlot.RemoveLock();
            int depositAmount = Mathf.Min(remainingQuantity, destinationSlot.GetCapacityForItem(route.Item));
            if (depositAmount <= 0)
              continue;
            try
            {
              destinationSlot.InsertItem(route.Item.GetCopy(depositAmount));
              sourceSlot.RemoveLock();
              sourceSlot.ItemInstance.ChangeQuantity(-depositAmount);
              remainingQuantity -= depositAmount;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"WaitForInventoryDelivery: Deposited {depositAmount} of {route.Item.ID} to slot {destinationSlot.GetHashCode()} at {deliveryLocation.GUID} for NPC={_packager.fullName}",
                  DebugLogger.Category.Handler);
              if (remainingQuantity <= 0) break;
            }
            catch (Exception ex)
            {
              destinationSlot.ApplyLocks(_packager, "Post-delivery lock");
              sourceSlot.ApplyLocks(_packager, "Post-delivery lock");
              HandleRouteFailure(route, $"Deposit exception: {ex.Message}");
              failedRoutes.Add(route);
              _failedRoutes[route] = (Time.time, $"Deposit exception: {ex.Message}");
              break;
            }
          }
          if (remainingQuantity > 0)
          {
            HandleRouteFailure(route, $"Insufficient slot capacity: {remainingQuantity} remaining");
            failedRoutes.Add(route);
            _failedRoutes[route] = (Time.time, $"Insufficient slot capacity: {remainingQuantity} remaining");
          }
          else
          {
            ReleaseRouteLocks(route); // Unlock all slots on successful delivery
            _routeQueue.Remove(route);
          }
        }

        foreach (var failedRoute in failedRoutes)
        {
          ReleaseRouteLocks(failedRoute);
          _routeQueue.Remove(failedRoute);
        }
        TransitionToIdle($"Completed delivery to {deliveryLocation.GUID}");
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WaitForInventoryDelivery: Exception: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        foreach (var route in routes)
          HandleRouteFailure(route, $"Coroutine exception: {ex.Message}");
      }
    }

    // Helper method to validate route slots
    private bool ValidateRouteSlots(PrioritizedRoute route, out ItemSlot sourceSlot)
    {
      sourceSlot = route.InventorySlot;
      if (route.PickupSlots == null || route.PickupSlots.Count == 0 || !route.PickupSlots.Any(s => s?.ItemInstance != null && s.ItemInstance.CanStackWith(route.Item, false)))
      {
        HandleRouteFailure(route, $"No valid inventory slots for {route.Item.ID}");
        return false;
      }
      if (sourceSlot.IsLocked && sourceSlot.ActiveLock.LockOwner != _packager.NetworkObject)
      {
        HandleRouteFailure(route, $"Inventory slot {sourceSlot.GetHashCode()} locked by another owner");
        return false;
      }
      return true;
    }

    public void ProcessRoutes()
    {
      if (_routeQueue.Count == 0 || _routeQueue[0].TransitRoute == null)
      {
        TransitionToIdle("No regular routes");
        return;
      }
      var route = _routeQueue[0];
      var pickupLocation = route.TransitRoute.Source;
      var deliveryLocation = route.TransitRoute.Destination;
      try
      {
        var accessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
        if (accessPoint == null)
        {
          HandleRouteFailure(route, $"No accessible access point for {pickupLocation.GUID}");
          return;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ProcessRoutes: Processing route for {route.Item.ID} (qty={route.Quantity}) from {pickupLocation.GUID} to {deliveryLocation.GUID} for NPC={_packager.fullName}",
            DebugLogger.Category.Handler);
        PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Moving;
        PackagerPatch._stateStartTimes[_packager] = Time.time;
        _packager.Movement.SetDestination(accessPoint.position);
        CoroutineRunner.Instance.RunCoroutine(WaitForRouteProcessing(route, pickupLocation, deliveryLocation));
      }
      catch (Exception ex)
      {
        HandleRouteFailure(route, $"Exception: {ex.Message}");
      }
    }

    private IEnumerator WaitForRouteProcessing(PrioritizedRoute route, ITransitEntity pickupLocation, ITransitEntity deliveryLocation)
    {
      var pickupAccessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
      if (pickupAccessPoint == null)
      {
        HandleRouteFailure(route, $"Invalid or unreachable access point for {pickupLocation.GUID}");
        yield break;
      }
      float moveTimeout = Time.time + 30f;
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Moving;
      PackagerPatch._stateStartTimes[_packager] = Time.time;
      _packager.Movement.SetDestination(pickupAccessPoint.position);
      while (_packager.Movement.IsMoving && Time.time < moveTimeout)
        yield return null;
      if (Time.time >= moveTimeout || Vector3.Distance(_packager.transform.position, pickupAccessPoint.position) > 2f)
      {
        HandleRouteFailure(route, $"Failed to reach pickup location {pickupLocation.GUID} (distance={Vector3.Distance(_packager.transform.position, pickupAccessPoint.position)})");
        yield break;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"WaitForRouteProcessing: Reached pickup location {pickupLocation.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.PickingUp;
      PackagerPatch._stateStartTimes[_packager] = Time.time;
      int remainingQuantity = route.Quantity;
      foreach (var slot in route.PickupSlots)
      {
        if (slot == null || slot.Quantity <= 0 || slot.IsLocked)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForRouteProcessing: Skipping invalid slot for {route.Item.ID} at {pickupLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          continue;
        }
        int quantityToTake = Mathf.Min(slot.Quantity, remainingQuantity);
        if (quantityToTake <= 0) continue;
        try
        {
          slot.RemoveLock();
          var itemInstance = slot.ItemInstance.GetCopy(quantityToTake);

          route.InventorySlot.RemoveLock();
          route.InventorySlot.InsertItem(itemInstance);
          route.InventorySlot.ApplyLocks(_packager, "Route Inventory Slot");
          slot.ChangeQuantity(-quantityToTake);
          slot.ApplyLocks(_packager, "Route Return Slot");
          remainingQuantity -= quantityToTake;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"WaitForRouteProcessing: Picked up {quantityToTake} of {route.Item.ID} from slot at {pickupLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          if (remainingQuantity <= 0) break;
        }
        catch (Exception ex)
        {
          HandleRouteFailure(route, $"Pickup failed: {ex.Message}");
          yield break;
        }
      }
      if (remainingQuantity > 0)
      {
        HandleRouteFailure(route, $"Insufficient items picked up: {remainingQuantity} remaining");
        yield break;
      }
      var deliveryAccessPoint = NavMeshUtility.GetAccessPoint(deliveryLocation, _packager);
      if (deliveryAccessPoint == null)
      {
        HandleRouteFailure(route, $"No accessible access point for {deliveryLocation.GUID}");
        yield break;
      }
      moveTimeout = Time.time + 30f;
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Moving;
      PackagerPatch._stateStartTimes[_packager] = Time.time;
      _packager.Movement.SetDestination(deliveryAccessPoint.position);
      while (_packager.Movement.IsMoving && Time.time < moveTimeout)
        yield return null;
      if (Time.time >= moveTimeout || Vector3.Distance(_packager.transform.position, deliveryAccessPoint.position) > 1f)
      {
        HandleRouteFailure(route, $"Failed to reach delivery location {deliveryLocation.GUID}");
        yield break;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"WaitForRouteProcessing: Reached delivery location {deliveryLocation.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Delivering;
      PackagerPatch._stateStartTimes[_packager] = Time.time;
      foreach (var slot in route.DestinationSlots)
      {
        if (slot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"WaitForRouteProcessing: Skipping invalid delivery slot for {route.Item.ID} at {deliveryLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          continue;
        }
        try
        {
          slot.RemoveLock();
          slot.InsertItem(route.Item.GetCopy(route.Quantity));
          route.InventorySlot.RemoveLock();
          route.InventorySlot.ChangeQuantity(-route.Quantity);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"WaitForRouteProcessing: Delivered {route.Quantity} of {route.Item.ID} to slot at {deliveryLocation.GUID} for NPC={_packager.fullName}",
              DebugLogger.Category.Handler);
          break;
        }
        catch (Exception ex)
        {
          HandleRouteFailure(route, $"Delivery failed: {ex.Message}");
          yield break;
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"WaitForRouteProcessing: Completed route for {route.Item.ID} from {pickupLocation.GUID} to {deliveryLocation.GUID} for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
      ReleaseRouteLocks(route);
      _routeQueue.RemoveAt(0);
      TransitionToIdle("Route completed");
    }

    // Method: ReleaseRouteLocks
    // Purpose: Releases all locks associated with a specific route, clearing destination and pickup slots.
    // Parameters:
    //   - route: The PrioritizedRoute whose locks should be released.
    private void ReleaseRouteLocks(PrioritizedRoute route)
    {
      route.DestinationSlots?.RemoveLock();
      route.PickupSlots?.RemoveLock();
      route.InventorySlot?.RemoveLock();
    }

    // Method: Clear
    // Purpose: Clears all routes, locked slots, and reservations for the NPC.
    public void Clear()
    {
      foreach (var route in _routeQueue)
        ReleaseRouteLocks(route);
      _routeQueue.Clear();
      _failedRoutes.Clear();
      PackagerPatch._states[_packager] = PackagerPatch.PackagerState.Idle;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
      $"RouteQueueManager.Clear: Cleared all routes, locks, and failed routes for NPC={_packager.fullName}",
          DebugLogger.Category.Handler);
      ActiveBehaviours[_packager].End();
    }
  }

  // Class: MoveItemBehaviourPatch
  // Purpose: Harmony patches for MoveItemBehaviour to customize item pickup and delivery logic, ensuring proper slot reservation and failure handling.
  // Dependencies: MoveItemBehaviour, ItemSlot, PackagerPatch, RouteQueueManager, DebugLogger
  [HarmonyPatch(typeof(MoveItemBehaviour))]
  public static class MoveItemBehaviourPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("Awake")]
    public static bool AwakePrefix(MoveItemBehaviour __instance)
    {
      ActiveBehaviours[__instance.Npc] = __instance;
      return true;
    }

    // Method: GrabItemPrefix
    // Purpose: Harmony prefix for MoveItemBehaviour.GrabItem to reserve slots and handle pickup failures.
    // Parameters:
    //   - __instance: The MoveItemBehaviour instance being patched.
    // Returns: True to continue execution, false to skip the original method.
    [HarmonyPrefix]
    [HarmonyPatch("GrabItem")]
    public static bool GrabItemPrefix(MoveItemBehaviour __instance)
    {
      if (__instance.skipPickup)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"GrabItem: Skipping for inventory route for NPC={__instance.Npc.fullName}",
            DebugLogger.Category.Handler);
        return true;
      }
      var sourceSlots = GetOutputSlotsContainingTemplateItem(__instance.assignedRoute.Source,
          __instance.itemToRetrieveTemplate);
      var availableSlot = sourceSlots.FirstOrDefault(s => !s.IsLocked || s.ActiveLock.LockOwner == __instance.Npc.NetworkObject);
      if (availableSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GrabItem: No available source slot for item {__instance.itemToRetrieveTemplate?.ID} for NPC={__instance.Npc.fullName}",
            DebugLogger.Category.Handler);
        if (PackagerPatch._routeQueues.TryGetValue(__instance.Npc as Packager, out var routeQueueManager))
          routeQueueManager.HandleFailedDelivery(__instance.itemToRetrieveTemplate);
        return false;
      }
      SetReservedSlot(__instance, availableSlot);
      return true;
    }

    // Method: TakeItemPrefix
    // Purpose: Harmony prefix for MoveItemBehaviour.TakeItem to validate reserved slots and handle pickup failures.
    // Parameters:
    //   - __instance: The MoveItemBehaviour instance being patched.
    // Returns: True to continue execution, false to skip the original method.
    [HarmonyPrefix]
    [HarmonyPatch("TakeItem")]
    public static bool TakeItemPrefix(MoveItemBehaviour __instance)
    {
      if (__instance.skipPickup)
      {
        ReleaseReservations(__instance);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TakeItem: Skipping for inventory route, released reservations for NPC={__instance.Npc.fullName}",
            DebugLogger.Category.Handler);
        return true;
      }

      int amountToGrab = __instance.GetAmountToGrab();
      if (amountToGrab == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"TakeItem: Amount to grab is 0 for item {__instance.itemToRetrieveTemplate?.ID} for NPC={__instance.Npc.fullName}",
            DebugLogger.Category.Handler);
        ReleaseReservations(__instance);
        if (PackagerPatch._routeQueues.TryGetValue(__instance.Npc as Packager, out var routeQueueManager))
          routeQueueManager.HandleFailedDelivery(__instance.itemToRetrieveTemplate);
        return false;
      }

      ItemSlot reservedSlot = GetReservedSlot(__instance);
      if (reservedSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"TakeItem: No reserved slot for item {__instance.itemToRetrieveTemplate?.ID} for NPC={__instance.Npc.fullName}",
            DebugLogger.Category.Handler);
        ReleaseReservations(__instance);
        if (PackagerPatch._routeQueues.TryGetValue(__instance.Npc as Packager, out var routeQueueManager))
          routeQueueManager.HandleFailedDelivery(__instance.itemToRetrieveTemplate);
        return false;
      }

      return true;
    }

    // Method: Postfix
    // Purpose: Harmony postfix for MoveItemBehaviour.End to release any reserved slots after execution.
    // Parameters:
    //   - __instance: The MoveItemBehaviour instance being patched.
    [HarmonyPostfix]
    [HarmonyPatch("End")]
    public static void Postfix(MoveItemBehaviour __instance)
    {
      ActiveBehaviours.Remove(__instance.Npc);
      ReleaseReservations(__instance);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"End: Released reservations for MoveItemBehaviour for NPC={__instance.Npc.fullName}",
          DebugLogger.Category.Handler);
    }
  }

  // Class: PackagerPatch
  // Purpose: Harmony patches for Packager to manage route scanning, task execution, and player inventory interactions.
  // Dependencies: Packager, RouteQueueManager, DebugLogger, StorageUtilities
  [HarmonyPatch(typeof(Packager))]
  public class PackagerPatch
  {
    private static readonly float MIN_CHECK_INTERVAL = 0f;
    private static readonly float MAX_CHECK_INTERVAL = 1f;
    public static readonly float RETRY_DELAY = 30f;
    private static readonly Dictionary<Packager, float> _checkIntervals = new();
    private static readonly Dictionary<Packager, float> _lastCheckTimes = new();
    public static readonly Dictionary<Packager, RouteQueueManager> _routeQueues = new();
    public static readonly Dictionary<Packager, PackagerState> _states = new();
    private static readonly Dictionary<Packager, ScanType> _lastScanType = new();
    public static Dictionary<Packager, float> _stateStartTimes = new();

    // Enum: ScanType
    // Purpose: Defines the types of scans for NPC tasks (station refill, loading dock, shelf restock).
    public enum ScanType
    {
      None,
      StationRefill,
      LoadingDock,
      ShelfRestock
    }

    public enum PackagerState
    {
      Idle,
      ProcessingRoutes,
      Moving,
      PickingUp,
      Delivering
    }

    // Method: AwakePostfix
    // Purpose: Harmony postfix for Packager.Awake to log initialization.
    // Parameters:
    //   - __instance: The Packager instance being patched.
    [HarmonyPostfix]
    [HarmonyPatch("Awake")]
    static void AwakePostfix(Packager __instance)
    {
      _routeQueues[__instance] = new RouteQueueManager(__instance);
      _checkIntervals[__instance] = UnityEngine.Random.Range(MIN_CHECK_INTERVAL, MAX_CHECK_INTERVAL);
      _lastScanType[__instance] = ScanType.None;
      _lastCheckTimes[__instance] = Time.time;
      _stateStartTimes[__instance] = 0f;
      _states[__instance] = PackagerState.Idle;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagerPatch: Initialized handler {__instance.fullName} with scan interval {_checkIntervals[__instance]}s",
          DebugLogger.Category.Handler);
    }

    // Method: UpdateBehaviourPrefix
    // Purpose: Overrides the Packager's UpdateBehaviour to manage route processing and state transitions.
    // Parameters:
    //   - __instance: The Packager instance.
    // Returns: False to skip the original method.
    // Remarks: Uses a switch statement to handle each PackagerState, ensuring inventory routes are processed and state transitions occur correctly.
    [HarmonyPrefix]
    [HarmonyPatch("UpdateBehaviour")]
    public static bool UpdateBehaviourPrefix(Packager __instance)
    {
      if (!_routeQueues.TryGetValue(__instance, out var routeQueueManager))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"UpdateBehaviourPrefix: No route queue for NPC={__instance.fullName}",
            DebugLogger.Category.Handler);
        return true;
      }

      var currentState = _states.GetValueOrDefault(__instance, PackagerState.Idle);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateBehaviourPrefix: Current state={currentState}, routes={routeQueueManager.RouteCount} for NPC={__instance.fullName}",
          DebugLogger.Category.Handler);

      switch (currentState)
      {
        case PackagerState.Idle:
          // Check for player-inserted items
          var inventorySlots = __instance.Inventory.ItemSlots
              .Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked)
              .ToList();
          if (inventorySlots.Count > 0)
          {
            _states[__instance] = PackagerState.ProcessingRoutes;
            _stateStartTimes[__instance] = Time.time;
            foreach (var slot in inventorySlots)
            {
              slot.ApplyLocks(__instance, "Inventory route planning lock");
              if (routeQueueManager.HandleInventoryItem(slot.ItemInstance))
              {
                DebugLogger.Log(DebugLogger.LogLevel.Info,
                    $"UpdateBehaviourPrefix: Added inventory route for item {slot.ItemInstance.ID} (qty={slot.Quantity}) for NPC={__instance.fullName}",
                    DebugLogger.Category.Handler);
              }
              else
              {
                slot.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Warning,
                    $"UpdateBehaviourPrefix: Failed to add route for item {slot.ItemInstance.ID}, unlocked slot for NPC={__instance.fullName}",
                    DebugLogger.Category.Handler);
              }
            }
          }
          else
          {
            // Other route processing (e.g., FindItemsNeedingMovement)
            var requests = FindItemsNeedingMovement(__instance, _lastScanType[__instance]);
            if (requests.Count > 0)
            {
              _states[__instance] = PackagerState.ProcessingRoutes;
              _stateStartTimes[__instance] = Time.time;
              routeQueueManager.AddRoutes(requests);
            }
            else
            {
              routeQueueManager.Clear();
              return true;
            }
          }
          break;

        case PackagerState.ProcessingRoutes:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateBehaviourPrefix: In state ProcessingRoutes (elapsed: {Time.time - _stateStartTimes[__instance]}s) for NPC={__instance.fullName}",
              DebugLogger.Category.Handler);
          if (routeQueueManager.RouteCount == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateBehaviourPrefix: No routes, transitioning to Idle for NPC={__instance.fullName}",
                DebugLogger.Category.Handler);
            _states[__instance] = PackagerState.Idle;
            _stateStartTimes[__instance] = Time.time;
          }
          else if (routeQueueManager.HasInventoryRoute())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateBehaviourPrefix: Processing inventory route for NPC={__instance.fullName}",
                DebugLogger.Category.Handler);
            routeQueueManager.ProcessInventoryRoute();
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateBehaviourPrefix: Processing regular routes for NPC={__instance.fullName}",
                DebugLogger.Category.Handler);
            routeQueueManager.ProcessRoutes();
          }
          break;

        case PackagerState.Moving:
          // Check if movement is complete or stuck
          if (!__instance.Movement.IsMoving)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"UpdateBehaviourPrefix: NPC stuck in Moving state, transitioning to Idle for NPC={__instance.fullName}",
                DebugLogger.Category.Handler);
            _states[__instance] = PackagerState.Idle;
            _stateStartTimes[__instance] = Time.time;
          }
          break;

        case PackagerState.PickingUp:
        case PackagerState.Delivering:
          // These states are typically handled by coroutines (WaitForMultiSlotPickup, WaitForDelivery, WaitForInventoryDelivery)
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateBehaviourPrefix: In state {currentState}, waiting for coroutine completion for NPC={__instance.fullName}",
              DebugLogger.Category.Handler);
          break;

        default:
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"UpdateBehaviourPrefix: Unknown state {currentState} for NPC={__instance.fullName}",
              DebugLogger.Category.Handler);
          _states[__instance] = PackagerState.Idle;
          _stateStartTimes[__instance] = Time.time;
          break;
      }

      return false; // Skip original method
    }
  }
}