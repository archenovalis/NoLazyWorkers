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
using NoLazyWorkers.Employees;
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
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.NoLazyUtilities;
using GameKit.Utilities;
using Beautify.Demos;
using FishNet.Object;
using Pathfinding.Examples;
using ScheduleOne.NPCs;
using UnityEngine.InputSystem;
using NoLazyWorkers.Structures;
using System;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using UnityEngine.InputSystem.EnhancedTouch;
using static NoLazyWorkers.Employees.PackagerPatch;
using System.Data.Common;

namespace NoLazyWorkers.Employees
{
  // Class: MoveItemBehaviourExtensions
  // Purpose: Manages slot reservations for MoveItemBehaviour to prevent NPC conflicts during item transfers.
  // Dependencies: DebugLogger, ItemSlot, MoveItemBehaviour
  // Assumptions: MoveItemBehaviour instances are tied to NPCs; ItemSlot supports locking.
  public class PackagerBehaviour : EmployeeBehaviour
  {
    public PackagerBehaviour(NPC npc, PackagerAdapter adapter) : base(npc, adapter)
    {
    }

    // Method: FindItemsNeedingMovement
    // Purpose: Identifies items that need to be moved for a given NPC based on the scan type, creating TransferRequests for valid routes.
    // Parameters:
    //   - npc: The Packager NPC to process.
    //   - lastScanType: The last scan type used to prioritize scan order.
    // Returns: A list of TransferRequests representing items to move.
    // Remarks: Uses GetOutputSlotsContainingTemplateItem to include multiple pickup slots for each TransferRequest, supporting combined quantities from multiple slots.
    public static List<TransferRequest> FindItemsNeedingMovement(Packager npc, ScanType lastScanType)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"FindItemsNeedingMovement: Starting for NPC={npc?.fullName}, LastScanType={lastScanType}",
          DebugLogger.Category.Packager);
      if (npc == null || npc.AssignedProperty == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"FindItemsNeedingMovement: NPC or AssignedProperty is null for NPC={npc?.fullName}",
            DebugLogger.Category.Packager);
        return new List<TransferRequest>();
      }
      var property = npc.AssignedProperty;
      var requests = new List<TransferRequest>();
      var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();
      int maxRoutes = Mathf.Min(EmployeeBehaviour.MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.FindAll(s => s.ItemInstance == null).Count);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Max routes limited to {maxRoutes}, inventory slots={npc.Inventory.ItemSlots.FindAll(s => s.ItemInstance == null).Count} for NPC={npc.fullName}",
          DebugLogger.Category.Packager);
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
              DebugLogger.Category.Packager);
          if (PropertyStations.TryGetValue(property, out var stations))
          {
            foreach (var station in stations)
            {
              if (requests.Count >= maxRoutes) break;
              if (station.IsInUse || station.HasActiveOperation)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: Skipping station {station.GUID} (in use or active operation) for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
                continue;
              }
              var canGetToChecked = false;
              var items = station.RefillList();
              foreach (var item in items)
              {
                var shelves = StorageUtilities.FindShelvesWithItem(npc, item, needed: 1);
                var source = shelves.Keys.FirstOrDefault(s => EmployeeUtilities.GetOutputSlotsContainingTemplateItem(s, item).Count > 0);
                if (source == null)
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No accessible shelf with item {item.ID} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                  continue;
                }
                var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(source, item);
                if (sourceSlots.Count == 0)
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No available output slots for item {item.ID} on shelf {source.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                  continue;
                }
                sourceSlots.ApplyLocks(npc, "Route planning lock");
                var destination = station.TransitEntity;
                if (destination == null)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Warning,
                      $"FindItemsNeedingMovement: Station transit entity null for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                  continue;
                }
                var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
                if (deliverySlots == null || deliverySlots.Count == 0)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No delivery slots available for item {item.ID} on station for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                  continue;
                }
                var totalAvailable = sourceSlots.Sum(slot => slot.Quantity);
                var quantity = Mathf.Min(totalAvailable, station.MaxProductQuantity - station.GetInputQuantity());
                if (quantity <= 0)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: No quantity to transfer for item {item.ID} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
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
                          DebugLogger.Category.Packager);
                      break;
                    }
                  }
                  var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), source, sourceSlots, destination, deliverySlots);
                  if (!pickupGroups.ContainsKey(source)) pickupGroups[source] = new List<TransferRequest>();
                  pickupGroups[source].Add(request);
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: Created StationRefill request for {quantity} of {item.ID} from {source.GUID} ({sourceSlots.Count} slots) to station {station.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                }
                catch (ArgumentNullException ex)
                {
                  sourceSlots.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Error,
                      $"FindItemsNeedingMovement: Failed to create StationRefill TransferRequest: {ex.Message} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
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
              DebugLogger.Category.Packager);
          for (int i = 0; i < property.LoadingDocks.Length && requests.Count < maxRoutes; i++)
          {
            var dock = property.LoadingDocks[i];
            if (!dock.IsInUse)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"FindItemsNeedingMovement: Skipping loading dock {dock.GUID} (not in use) for NPC={npc.fullName}",
                  DebugLogger.Category.Packager);
              continue;
            }
            var canGetToChecked = false;
            foreach (var slots in dock.OutputSlots)
            {
              if (slots?.ItemInstance == null || slots.Quantity <= 0 || slots.IsLocked)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: Skipping slot (null, empty, or locked) on loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
                continue;
              }
              var item = slots.ItemInstance;
              var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(dock, item);
              if (sourceSlots.Count == 0)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No available output slots for item {item.ID} on loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
                continue;
              }
              sourceSlots.ApplyLocks(npc, "Route planning lock");
              var destination = StorageUtilities.FindShelfForDelivery(npc, item);
              if (destination == null)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No accessible shelf for item {item.ID} from loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
                continue;
              }
              var deliverySlots = (destination as ITransitEntity).ReserveInputSlotsForItem(item, npc.NetworkObject);
              if (deliverySlots == null || deliverySlots.Count == 0)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No delivery slots available for item {item.ID} on shelf {destination.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
                continue;
              }
              var totalAvailable = sourceSlots.Sum(slot => slot.Quantity);
              var quantity = Mathf.Min(totalAvailable, (destination as ITransitEntity).GetInputCapacityForItem(item, npc));
              if (quantity <= 0)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: No quantity to transfer for item {item.ID} from loading dock {dock.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
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
                        DebugLogger.Category.Packager);
                    break;
                  }
                }
                var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), dock, sourceSlots, destination, deliverySlots);
                if (!pickupGroups.ContainsKey(dock)) pickupGroups[dock] = new List<TransferRequest>();
                pickupGroups[dock].Add(request);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindItemsNeedingMovement: Created LoadingDock request for {quantity} of {item.ID} from {dock.GUID} ({sourceSlots.Count} slots) to shelf {destination.GUID} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
              }
              catch (ArgumentNullException ex)
              {
                sourceSlots.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Error,
                    $"FindItemsNeedingMovement: Failed to create LoadingDock TransferRequest: {ex.Message} for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
              }
              if (pickupGroups[dock].Count >= maxRoutes) break;
            }
          }
        }
        else if (scanType == PackagerPatch.ScanType.ShelfRestock)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"FindItemsNeedingMovement: Checking shelf restocks for NPC={npc.fullName}",
              DebugLogger.Category.Packager);
          try
          {
            foreach (var anyShelf in StorageExtensions.AnyShelves)
            {
              if (requests.Count >= maxRoutes) break;
              if (anyShelf == null || anyShelf.OutputSlots == null)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Warning,
                    $"FindItemsNeedingMovement: Skipping null or invalid shelf for NPC={npc.fullName}",
                    DebugLogger.Category.Packager);
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
                        DebugLogger.Category.Packager);
                    continue;
                  }
                  var item = slot.ItemInstance;

                  if (anyShelf.ParentProperty != null && RouteQueueManager.NoDestinationCache.TryGetValue(anyShelf.ParentProperty, out var cache) && cache.Any(i => i.CanStackWith(item, false)))
                  {
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: Skipping cached item {item.ID} for property {anyShelf.ParentProperty.name} in shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Packager);
                    continue;
                  }
                  slot.ApplyLocks(npc, "Route planning lock");
                  var assignedShelf = StorageUtilities.FindShelfForDelivery(npc, item, allowAnyShelves: false);
                  if (assignedShelf == null || assignedShelf == anyShelf)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No valid assigned shelf or cannot navigate for item {item.ID} from shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Packager);
                    continue;
                  }
                  var deliverySlots = (assignedShelf as ITransitEntity)?.ReserveInputSlotsForItem(item, npc.NetworkObject);
                  if (deliverySlots == null || deliverySlots.Count == 0)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No delivery slots available for item {item.ID} on shelf {assignedShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Packager);
                    continue;
                  }
                  var quantity = Mathf.Min(slot.Quantity, (assignedShelf as ITransitEntity).GetInputCapacityForItem(item, npc));
                  if (quantity <= 0)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No quantity to transfer for item {item.ID} from shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Packager);
                    continue;
                  }
                  var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
                  if (inventorySlot == null)
                  {
                    slot.RemoveLock();
                    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                        $"FindItemsNeedingMovement: No available inventory slot for item {item.ID} from shelf {anyShelf.GUID} for NPC={npc.fullName}",
                        DebugLogger.Category.Packager);
                    continue;
                  }
                  var request = new TransferRequest(item, quantity, inventorySlot, anyShelf, new List<ItemSlot> { slot }, assignedShelf, deliverySlots);
                  if (!pickupGroups.ContainsKey(anyShelf)) pickupGroups[anyShelf] = new List<TransferRequest>();
                  pickupGroups[anyShelf].Add(request);
                  DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                      $"FindItemsNeedingMovement: Created ShelfRestock request for {quantity} of {item.ID} from {anyShelf.GUID} to {assignedShelf.GUID} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                }
                catch (Exception ex)
                {
                  if (slot != null) slot.RemoveLock();
                  DebugLogger.Log(DebugLogger.LogLevel.Error,
                      $"FindItemsNeedingMovement: Error processing slot in shelf {anyShelf.GUID}: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={npc.fullName}",
                      DebugLogger.Category.Packager);
                }
                if (pickupGroups.ContainsKey(anyShelf) && pickupGroups[anyShelf].Count >= maxRoutes) break;
              }
            }
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindItemsNeedingMovement: Finished checking shelf restocks for NPC={npc.fullName}",
                DebugLogger.Category.Packager);
          }
          catch (Exception ex)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"FindItemsNeedingMovement: Failed to process shelf restocks: {ex.Message}\nStackTrace: {ex.StackTrace} for NPC={npc.fullName}",
                DebugLogger.Category.Packager);
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
              DebugLogger.Category.Packager);
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"FindItemsNeedingMovement: Completed with {requests.Count} transfer requests for NPC={npc.fullName}",
          DebugLogger.Category.Packager);
      return requests;
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
      if (__instance.Npc is Packager)
        EmployeeBehaviour.ActiveBehaviours[__instance.Npc.GUID] = __instance;
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
      if (__instance.Npc is Packager)
      {
        if (__instance.skipPickup)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"GrabItem: Skipping for inventory route for NPC={__instance.Npc.fullName}",
              DebugLogger.Category.Packager);
          return true;
        }
        var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(__instance.assignedRoute.Source,
            __instance.itemToRetrieveTemplate);
        var availableSlot = sourceSlots.FirstOrDefault(s => !s.IsLocked || s.ActiveLock.LockOwner == __instance.Npc.NetworkObject);
        if (availableSlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"GrabItem: No available source slot for item {__instance.itemToRetrieveTemplate?.ID} for NPC={__instance.Npc.fullName}",
              DebugLogger.Category.Packager);
          if (PackagerPatch._routeQueues.TryGetValue(__instance.Npc.GUID, out var routeQueueManager))
            routeQueueManager.HandleFailedDelivery(__instance.itemToRetrieveTemplate);
          return false;
        }
        EmployeeUtilities.SetReservedSlot(__instance, availableSlot);
      }
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
      if (__instance.Npc is Packager)
      {
        if (__instance.skipPickup)
        {
          EmployeeUtilities.ReleaseReservations(__instance);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"TakeItem: Skipping for inventory route, released reservations for NPC={__instance.Npc.fullName}",
              DebugLogger.Category.Packager);
          return true;
        }

        int amountToGrab = __instance.GetAmountToGrab();
        if (amountToGrab == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"TakeItem: Amount to grab is 0 for item {__instance.itemToRetrieveTemplate?.ID} for NPC={__instance.Npc.fullName}",
              DebugLogger.Category.Packager);
          EmployeeUtilities.ReleaseReservations(__instance);
          if (PackagerPatch._routeQueues.TryGetValue(__instance.Npc.GUID, out var routeQueueManager))
            routeQueueManager.HandleFailedDelivery(__instance.itemToRetrieveTemplate);
          return false;
        }

        ItemSlot reservedSlot = EmployeeUtilities.GetReservedSlot(__instance);
        if (reservedSlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"TakeItem: No reserved slot for item {__instance.itemToRetrieveTemplate?.ID} for NPC={__instance.Npc.fullName}",
              DebugLogger.Category.Packager);
          EmployeeUtilities.ReleaseReservations(__instance);
          if (PackagerPatch._routeQueues.TryGetValue(__instance.Npc.GUID, out var routeQueueManager))
            routeQueueManager.HandleFailedDelivery(__instance.itemToRetrieveTemplate);
          return false;
        }
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
      if (__instance.Npc is Packager)
      {
        EmployeeBehaviour.ActiveBehaviours.Remove(__instance.Npc.GUID);
        EmployeeUtilities.ReleaseReservations(__instance);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"End: Released reservations for MoveItemBehaviour for NPC={__instance.Npc.fullName}",
            DebugLogger.Category.Packager);
      }
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
    private static readonly Dictionary<Guid, float> _checkIntervals = new();
    private static readonly Dictionary<Guid, float> _lastCheckTimes = new();
    public static readonly Dictionary<Guid, RouteQueueManager> _routeQueues = new();
    public static readonly Dictionary<Guid, PackagerState> _states = new();
    private static readonly Dictionary<Guid, ScanType> _lastScanType = new();
    public static Dictionary<Guid, float> _stateStartTimes = new();

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
      _routeQueues[__instance.GUID] = new RouteQueueManager(__instance);
      _checkIntervals[__instance.GUID] = UnityEngine.Random.Range(MIN_CHECK_INTERVAL, MAX_CHECK_INTERVAL);
      _lastScanType[__instance.GUID] = ScanType.None;
      _lastCheckTimes[__instance.GUID] = Time.time;
      _stateStartTimes[__instance.GUID] = 0f;
      _states[__instance.GUID] = PackagerState.Idle;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"PackagerPatch: Initialized handler {__instance.fullName} with scan interval {_checkIntervals[__instance.GUID]}s",
          DebugLogger.Category.Packager);
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
      if (!_routeQueues.TryGetValue(__instance.GUID, out var routeQueueManager))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"UpdateBehaviourPrefix: No route queue for NPC={__instance.fullName}",
            DebugLogger.Category.Packager);
        return true;
      }

      var currentState = _states.GetValueOrDefault(__instance.GUID, PackagerState.Idle);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateBehaviourPrefix: Current state={currentState}, routes={routeQueueManager.RouteCount} for NPC={__instance.fullName}",
          DebugLogger.Category.Packager);

      switch (currentState)
      {
        case PackagerState.Idle:
          // Check for player-inserted items
          var inventorySlots = __instance.Inventory.ItemSlots
              .Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked)
              .ToList();
          if (inventorySlots.Count > 0)
          {
            _states[__instance.GUID] = PackagerState.ProcessingRoutes;
            _stateStartTimes[__instance.GUID] = Time.time;
            foreach (var slot in inventorySlots)
            {
              slot.ApplyLocks(__instance, "Inventory route planning lock");
              if (routeQueueManager.HandleInventoryItem(slot.ItemInstance))
              {
                DebugLogger.Log(DebugLogger.LogLevel.Info,
                    $"UpdateBehaviourPrefix: Added inventory route for item {slot.ItemInstance.ID} (qty={slot.Quantity}) for NPC={__instance.fullName}",
                    DebugLogger.Category.Packager);
              }
              else
              {
                slot.RemoveLock();
                DebugLogger.Log(DebugLogger.LogLevel.Warning,
                    $"UpdateBehaviourPrefix: Failed to add route for item {slot.ItemInstance.ID}, unlocked slot for NPC={__instance.fullName}",
                    DebugLogger.Category.Packager);
              }
            }
          }
          else
          {
            // Other route processing (e.g., FindItemsNeedingMovement)
            var requests = PackagerBehaviour.FindItemsNeedingMovement(__instance, _lastScanType[__instance.GUID]);
            if (requests.Count > 0)
            {
              _states[__instance.GUID] = PackagerState.ProcessingRoutes;
              _stateStartTimes[__instance.GUID] = Time.time;
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
              $"UpdateBehaviourPrefix: In state ProcessingRoutes (elapsed: {Time.time - _stateStartTimes[__instance.GUID]}s) for NPC={__instance.fullName}",
              DebugLogger.Category.Packager);
          if (routeQueueManager.RouteCount == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateBehaviourPrefix: No routes, transitioning to Idle for NPC={__instance.fullName}",
                DebugLogger.Category.Packager);
            _states[__instance.GUID] = PackagerState.Idle;
            _stateStartTimes[__instance.GUID] = Time.time;
          }
          else if (routeQueueManager.HasInventoryRoute())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateBehaviourPrefix: Processing inventory route for NPC={__instance.fullName}",
                DebugLogger.Category.Packager);
            routeQueueManager.ProcessInventoryRoute();
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"UpdateBehaviourPrefix: Processing regular routes for NPC={__instance.fullName}",
                DebugLogger.Category.Packager);
            routeQueueManager.ProcessRoutes();
          }
          break;

        case PackagerState.Moving:
          // Check if movement is complete or stuck
          if (!__instance.Movement.IsMoving)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"UpdateBehaviourPrefix: NPC stuck in Moving state, transitioning to Idle for NPC={__instance.fullName}",
                DebugLogger.Category.Packager);
            _states[__instance.GUID] = PackagerState.Idle;
            _stateStartTimes[__instance.GUID] = Time.time;
          }
          break;

        case PackagerState.PickingUp:
        case PackagerState.Delivering:
          // These states are typically handled by coroutines (WaitForMultiSlotPickup, WaitForDelivery, WaitForInventoryDelivery)
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"UpdateBehaviourPrefix: In state {currentState}, waiting for coroutine completion for NPC={__instance.fullName}",
              DebugLogger.Category.Packager);
          break;

        default:
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"UpdateBehaviourPrefix: Unknown state {currentState} for NPC={__instance.fullName}",
              DebugLogger.Category.Packager);
          _states[__instance.GUID] = PackagerState.Idle;
          _stateStartTimes[__instance.GUID] = Time.time;
          break;
      }

      return false; // Skip original method
    }
  }
}