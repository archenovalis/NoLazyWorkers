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
using UnityEngine.InputSystem.EnhancedTouch;
using System.Data.Common;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using Object = UnityEngine.Object;

namespace NoLazyWorkers.Employees
{
  public class PackagerBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    public PackagerBehaviour(Packager packager, IStationAdapter station, IEmployeeAdapter employee) : base(packager, employee)
    {
      Employee = employee;
      RegisterStationBehaviour(packager.MoveItemBehaviour, station);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour: Initialized for NPC {packager.fullName}", DebugLogger.Category.Packager);
    }

    public List<TransferRequest> FindItemsNeedingMovement(Packager npc, StateData state)
    {
      var requests = new List<TransferRequest>();
      var property = npc.AssignedProperty;
      if (property == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"FindItemsNeedingMovement: Property is null for NPC {npc.fullName}", DebugLogger.Category.Packager);
        return requests;
      }
      int maxRoutes = Mathf.Min(MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
      var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();

      // Station Refill
      if (PropertyStations.TryGetValue(property, out var stations))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Checking {stations.Count} stations for NPC {npc.fullName}", DebugLogger.Category.Packager);
        foreach (var station in stations)
        {
          if (requests.Count >= maxRoutes) break;
          if (station.IsInUse || station.HasActiveOperation) continue;
          var items = station.RefillList();
          foreach (var item in items)
          {
            var shelves = StorageUtilities.FindShelvesWithItem(npc, item, 1);
            var source = shelves.Keys.FirstOrDefault(s => GetOutputSlotsContainingTemplateItem(s, item).Count > 0);
            if (source == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: No source shelf for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            var sourceSlots = GetOutputSlotsContainingTemplateItem(source, item);
            if (sourceSlots.Count == 0)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: No valid source slots for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            sourceSlots.ApplyLocks(npc, "Route planning lock");
            var destination = station.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              sourceSlots.RemoveLock();
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: No valid delivery slots for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
            {
              sourceSlots.RemoveLock();
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Invalid quantity {quantity} for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            if (!npc.Movement.CanGetTo(station.GetAccessPoint(npc)))
            {
              sourceSlots.RemoveLock();
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Cannot reach station for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
            {
              sourceSlots.RemoveLock();
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: No free inventory slot for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            var request = new TransferRequest(item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            if (!pickupGroups.ContainsKey(source)) pickupGroups[source] = new List<TransferRequest>();
            pickupGroups[source].Add(request);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: StationRefill request for {quantity} of {item.ID}", DebugLogger.Category.Packager);
            break;
          }
        }
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindItemsNeedingMovement: No stations found for property {property}", DebugLogger.Category.Packager);
      }

      // Loading Dock
      foreach (var dock in property.LoadingDocks ?? Enumerable.Empty<LoadingDock>())
      {
        if (requests.Count >= maxRoutes) break;
        if (!dock.IsInUse) continue;
        foreach (var slot in dock.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
          var item = slot.ItemInstance;
          var sourceSlots = GetOutputSlotsContainingTemplateItem(dock, item);
          if (sourceSlots.Count == 0) continue;
          sourceSlots.ApplyLocks(npc, "Route planning lock");
          var destination = StorageUtilities.FindShelfForDelivery(npc, item);
          if (destination == null)
          {
            sourceSlots.RemoveLock();
            continue;
          }
          var transitEntity = destination as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            sourceSlots.RemoveLock();
            continue;
          }
          int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), transitEntity.GetInputCapacityForItem(item, npc));
          if (quantity <= 0)
          {
            sourceSlots.RemoveLock();
            continue;
          }
          if (!npc.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, npc).position))
          {
            sourceSlots.RemoveLock();
            continue;
          }
          var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            sourceSlots.RemoveLock();
            continue;
          }
          var request = new TransferRequest(item, quantity, inventorySlot, dock, sourceSlots, destination, deliverySlots);
          if (!pickupGroups.ContainsKey(dock)) pickupGroups[dock] = new List<TransferRequest>();
          pickupGroups[dock].Add(request);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: LoadingDock request for {quantity} of {item.ID}", DebugLogger.Category.Packager);
        }
      }

      // Shelf Restock
      foreach (var shelf in StorageExtensions.AnyShelves)
      {
        if (requests.Count >= maxRoutes) break;
        if (shelf?.OutputSlots == null) continue;
        foreach (var slot in shelf.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
          var item = slot.ItemInstance;
          if (NoDestinationCache.TryGetValue(shelf.ParentProperty, out var cache) && cache.Any(i => i.CanStackWith(item, false)))
            continue;
          slot.ApplyLocks(npc, "Route planning lock");
          var assignedShelf = StorageUtilities.FindShelfForDelivery(npc, item, false);
          if (assignedShelf == null || assignedShelf == shelf)
          {
            slot.RemoveLock();
            continue;
          }
          var transitEntity = assignedShelf as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            slot.RemoveLock();
            continue;
          }
          int quantity = Mathf.Min(slot.Quantity, transitEntity.GetInputCapacityForItem(item, npc));
          if (quantity <= 0)
          {
            slot.RemoveLock();
            continue;
          }
          var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            slot.RemoveLock();
            continue;
          }
          var request = new TransferRequest(item, quantity, inventorySlot, shelf, new List<ItemSlot> { slot }, assignedShelf, deliverySlots);
          if (!pickupGroups.ContainsKey(shelf)) pickupGroups[shelf] = new List<TransferRequest>();
          pickupGroups[shelf].Add(request);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: ShelfRestock request for {quantity} of {item.ID}", DebugLogger.Category.Packager);
        }
      }

      foreach (var group in pickupGroups.OrderByDescending(g => g.Value.Max(r => GetPriority(r))))
      {
        requests.AddRange(group.Value.OrderByDescending(r => GetPriority(r)).Take(maxRoutes));
        if (requests.Count >= maxRoutes) break;
      }

      foreach (var slot in pickupGroups.SelectMany(g => g.Value).SelectMany(r => r.PickupSlots).Distinct())
      {
        if (!requests.Any(r => r.PickupSlots.Contains(slot)))
          slot.RemoveLock();
      }

      return requests;
    }
  }

  [HarmonyPatch(typeof(Packager))]
  public static class PackagerPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetStationToAttend")]
    public static bool GetStationToAttendPrefix(Packager __instance, ref PackagingStation __result)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationToAttendPrefix: Checking stations for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        if (__instance.AssignedProperty == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationToAttendPrefix: AssignedProperty is null for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var adapter))
        {
          adapter = new PackagerAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = adapter;
        }
        var packagerAdapter = adapter as PackagerAdapter;
        foreach (PackagingStation station in __instance.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationToAttendPrefix: Null station in AssignedStations", DebugLogger.Category.Packager);
            continue;
          }
          var stationAdapter = new PackagingStationAdapter(station);
          if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
            continue;
          if (__instance.PackagingBehaviour.IsStationReady(station))
          {
            __result = station;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetStationToAttendPrefix: Selected station {station.GUID} for NPC {__instance.fullName}", DebugLogger.Category.Packager);
            return false;
          }
          // Start if available
          if (station.GetState(PackagingStation.EMode.Package) == PackagingStation.EState.CanBegin)
          {
            //TODO: prefer packaging with jars switch packagingslot to jars when available. when productslot.quantity >0 && <5 then package with baggies. when more than 5 baggies are in a shelf then unpackage them then package them into jars.            __result = station;
            return false;
          }
          // Check for refill needs
          var items = stationAdapter.RefillList();
          foreach (var item in items)
          {
            var shelves = StorageUtilities.FindShelvesWithItem(__instance, item, stationAdapter.StartThreshold);
            if (shelves.Values.ToList()[0] > 0)
            {
              var state = new StateData
              {
                CurrentState = EState.Grabbing
              };
              var shelf = shelves.Keys.ToList()[0];
              if (packagerAdapter.HandlePlanning(__instance.PackagingBehaviour, state))
              {
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetStationToAttendPrefix: Planned refill routes for station {station.GUID}", DebugLogger.Category.Packager);
                __result = null;
                return false;
              }
            }
          }
        }
        __result = null;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationToAttendPrefix: No station ready for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetStationToAttendPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
        __result = null;
        return false;
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("GetStationMoveItems")]
    public static bool GetStationMoveItemsPrefix(Packager __instance, ref PackagingStation __result)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationMoveItemsPrefix: Checking stations for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        if (__instance.AssignedProperty == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationMoveItemsPrefix: AssignedProperty is null for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var adapter))
        {
          adapter = new PackagerAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = adapter;
        }
        var packagerAdapter = adapter as PackagerAdapter;
        if (PropertyStations.TryGetValue(__instance.AssignedProperty, out var stationAdapters))
          foreach (var stationAdapter in stationAdapters)
          {
            if (stationAdapter == null)
              continue;
            if (stationAdapter is PackagerAdapter)
            {
              // deliver outputslot to shelf
            }
            foreach (var productslot in stationAdapter.ProductSlots)
            {
              if (productslot?.ItemInstance != null && productslot.Quantity < productslot.ItemInstance.StackLimit && stationAdapter.RefillList().Any(p => p.CanStackWith(productslot.ItemInstance)))
              {
                var shelves = StorageUtilities.FindShelvesWithItem(__instance, productslot.ItemInstance, stationAdapter.StartThreshold, productslot.ItemInstance.StackLimit - productslot.Quantity);
                if (shelves.Values.ToList()[0] > 0)
                {
                  var state = new StateData
                  {
                    PickupLocation = shelves.Keys.ToList()[0],
                    Destination = stationAdapter.TransitEntity,
                    TargetItem = productslot.ItemInstance,
                    QuantityNeeded = stationAdapter.StartThreshold - productslot.Quantity,
                    QuantityWanted = productslot.ItemInstance.StackLimit - productslot.Quantity,
                    Station = stationAdapter,
                    QuantityInventory = __instance.Inventory.GetIdenticalItemAmount(productslot.ItemInstance),
                    CurrentState = EState.Grabbing
                  };
                  if (packagerAdapter.HandleGrabbing(__instance.MoveItemBehaviour, state))
                  {
                    DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetStationMoveItemsPrefix: Planned move for item {productslot.ItemInstance.ID} from station {stationAdapter.GUID}", DebugLogger.Category.Packager);
                    __result = null;
                    return false;
                  }
                }
              }
            }
          }
        __result = null;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationMoveItemsPrefix: No station with items to move for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetStationMoveItemsPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
        __result = null;
        return false;
      }
    }
  }
}