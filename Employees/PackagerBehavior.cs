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
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public class PackagerBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    private readonly Packager _packager;
    public PackagerBehaviour(Packager packager, IStationAdapter station, IEmployeeAdapter employee) : base(packager, employee)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      Employee = employee;
      if (station != null)
      {
        RegisterStationBehaviour(GetInstancedBehaviour(packager, station), station);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour: Initialized for NPC {packager.fullName}", DebugLogger.Category.Packager);
    }

    public bool Planning(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerBehaviour.HandlePlanning: Starting for NPC {_packager.fullName}, Property {Employee.AssignedProperty}", DebugLogger.Category.Packager);
      var requests = FindItemsNeedingMovement(_packager, state);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.HandlePlanning: Found {requests.Count} routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      if (requests.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.HandlePlanning: No routes found. Stations={PropertyStations.GetValueOrDefault(Employee.AssignedProperty)?.Count ?? 0}, Shelves={StorageExtensions.AnyShelves.Count}, Docks={Employee.AssignedProperty?.LoadingDocks.Length ?? 0}", DebugLogger.Category.Packager);
        state.ActiveRoutes.Clear();
        TransitionState(behaviour, state, EState.Idle, "No routes planned");
        return false;
      }
      AddRoutes(behaviour, state, requests);
      TransitionState(behaviour, state, EState.Grabbing, "Routes planned");
      return true;
    }

    public bool InventoryItem(Behaviour behaviour, StateData state, ItemInstance item)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerBehaviour.HandleInventoryItem: Processing item {item.ID} for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      var destination = StorageUtilities.FindShelfForDelivery(_packager, item);
      if (destination == null)
      {
        NoDestinationCache[Employee.AssignedProperty].Add(item);
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.HandleInventoryItem: No destination for item {item.ID}", DebugLogger.Category.Packager);
        return false;
      }
      var inventorySlot = _packager.Inventory.ItemSlots
          .FirstOrDefault(s => s?.ItemInstance != null && s.ItemInstance.CanStackWith(item) && s.Quantity > 0);
      if (inventorySlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.HandleInventoryItem: No valid inventory slot for item {item.ID}", DebugLogger.Category.Packager);
        return false;
      }
      var transitEntity = destination as ITransitEntity;
      var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, _packager.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.HandleInventoryItem: No valid delivery slots for item {item.ID}", DebugLogger.Category.Packager);
        return false;
      }
      int quantity = Math.Min(inventorySlot.Quantity, transitEntity.GetInputCapacityForItem(item, _packager));
      if (quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.HandleInventoryItem: Invalid quantity {quantity} for item {item.ID}", DebugLogger.Category.Packager);
        return false;
      }
      var request = new TransferRequest(item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, deliverySlots);
      state.ActiveRoutes.Add(new PrioritizedRoute(request, PRIORITY_SHELF_RESTOCK));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.HandleInventoryItem: Added route for {quantity} of {item.ID}", DebugLogger.Category.Packager);
      TransitionState(behaviour, state, EState.Inserting, "Inventory route added");
      return true;
    }

    public bool Grabbing(Behaviour behaviour, StateData state)
    {
      var route = state.ActiveRoutes.FirstOrDefault();
      if (route.PickupLocation == null) // Inventory route
      {
        TransitionState(behaviour, state, EState.Inserting, "Inventory route, skip pickup");
        MoveTo(behaviour, state, route.Destination);
        return true;
      }
      if (!IsAtLocation(behaviour, route.PickupLocation))
      {
        MoveTo(behaviour, state, route.PickupLocation);
        return true;
      }
      state.PickupSlots = route.PickupSlots;
      state.PickupSlots.ApplyLocks(_packager, "Pickup lock");
      int remaining = route.PickupSlots.Count;
      foreach (var slot in state.PickupSlots)
      {
        if (slot.Quantity <= 0 || (slot.IsLocked && slot.ActiveLock.LockOwner != _packager.NetworkObject))
          continue;
        int amount = Mathf.Min(slot.Quantity, remaining);
        if (amount <= 0) continue;
        slot.ChangeQuantity(-amount);
        route.InventorySlot.InsertItem(route.Item.GetCopy(amount));
        remaining -= amount;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleGrabbing: Picked up {amount} of {route.Item.ID} from slot {slot.GetHashCode()}", DebugLogger.Category.Packager);
        if (remaining <= 0) break;
      }
      state.PickupSlots.RemoveLock();
      state.QuantityInventory += route.Quantity - remaining;
      if (remaining > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleGrabbing: Insufficient items, {remaining} remaining", DebugLogger.Category.Packager);
        HandleFailedRoute(behaviour, state, route, "Insufficient items");
        return false;
      }
      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, EState.Inserting, "Items picked up");
      MoveTo(behaviour, state, route.Destination);
      return true;
    }

    public bool Inserting(Behaviour behaviour, StateData state)
    {
      var route = state.ActiveRoutes.FirstOrDefault();
      if (route.Destination == null)
      {
        TransitionState(behaviour, state, EState.Idle, "No destination");
        return false;
      }
      if (!IsAtLocation(behaviour, route.Destination))
      {
        MoveTo(behaviour, state, route.Destination);
        return true;
      }
      state.DeliverySlots = route.DeliverySlots;
      state.DeliverySlots.ApplyLocks(_packager, "Delivery lock");
      int remaining = state.QuantityInventory;
      foreach (var slot in state.DeliverySlots)
      {
        int capacity = slot.GetCapacityForItem(route.Item);
        int amount = Mathf.Min(remaining, capacity);
        if (amount <= 0) continue;
        slot.InsertItem(route.Item.GetCopy(amount));
        route.InventorySlot.ChangeQuantity(-amount);
        remaining -= amount;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleInserting: Delivered {amount} of {route.Item.ID} to slot {slot.GetHashCode()}", DebugLogger.Category.Packager);
        if (remaining <= 0) break;
      }
      state.DeliverySlots.RemoveLock();
      state.QuantityInventory = remaining;
      if (remaining > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleInserting: Could not deliver {remaining} items", DebugLogger.Category.Packager);
        HandleFailedRoute(behaviour, state, route, "Insufficient slot capacity");
        return false;
      }
      state.ActiveRoutes.Remove(route);
      TransitionState(behaviour, state, state.ActiveRoutes.Count > 0 ? EState.Grabbing : EState.Idle, "Delivery complete");
      return true;
    }

    public bool Operating(Behaviour behaviour, StateData state)
    {
      if (state.Station == null)
      {
        state.Station = StationUtilities.GetStation(behaviour);
        if (state.Station == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleOperating: No station for {_packager.fullName}", DebugLogger.Category.Packager);
          TransitionState(behaviour, state, EState.Idle, "No station");
          return false;
        }
      }
      if (!IsAtLocation(behaviour, state.Station.TransitEntity))
      {
        MoveTo(behaviour, state, state.Station.TransitEntity);
        return true;
      }
      if (state.Station.HasActiveOperation || state.Station.GetInputQuantity() < state.Station.StartThreshold)
      {
        TransitionState(behaviour, state, EState.Planning, "Cannot start operation");
        return false;
      }
      state.Station.StartOperation(behaviour);
      TransitionState(behaviour, state, EState.Completed, "Operation started");
      return true;
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
            var source = shelves.Keys.FirstOrDefault(s => EmployeeUtilities.GetOutputSlotsContainingTemplateItem(s, item).Count > 0);
            if (source == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: No source shelf for item {item.ID}", DebugLogger.Category.Packager);
              continue;
            }
            var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(source, item);
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

      // Loading Dock
      foreach (var dock in property.LoadingDocks ?? Enumerable.Empty<LoadingDock>())
      {
        if (requests.Count >= maxRoutes) break;
        if (!dock.IsInUse) continue;
        foreach (var slot in dock.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
          var item = slot.ItemInstance;
          var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(dock, item);
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

      // Shelf Restock (Any to Specific)
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
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"Registered adapter for NPC {__instance.fullName}, type=Packager", DebugLogger.Category.Packager);
        }
        var packagerAdapter = adapter as PackagerAdapter;
        if (packagerAdapter == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetStationToAttendPrefix: Failed to cast adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
        foreach (PackagingStation station in __instance.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationToAttendPrefix: Null station in AssignedStations", DebugLogger.Category.Packager);
            continue;
          }
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            stationAdapter = new PackagingStationAdapter(station);
            StationAdapters[station.GUID] = stationAdapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"Created station adapter for station {station.GUID}", DebugLogger.Category.Packager);
          }
          if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationToAttendPrefix: Station {station.GUID} in use or active, skipping", DebugLogger.Category.Packager);
            continue;
          }
          if (packagerAdapter.GetEmployeeBehaviour(__instance, station, out var employeeBehaviour))
          {
            var packagingBehaviour = employeeBehaviour as PackagingBehaviour;
            if (packagingBehaviour != null && packagingBehaviour.IsStationReady(station))
            {
              __result = station;
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetStationToAttendPrefix: Selected station {station.GUID} for NPC {__instance.fullName}", DebugLogger.Category.Packager);
              return false;
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
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"Registered adapter for NPC {__instance.fullName}, type=Packager", DebugLogger.Category.Packager);
        }
        var packagerAdapter = adapter as PackagerAdapter;
        if (packagerAdapter == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetStationMoveItemsPrefix: Failed to cast adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
        foreach (PackagingStation station in __instance.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationMoveItemsPrefix: Null station in AssignedStations", DebugLogger.Category.Packager);
            continue;
          }
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            stationAdapter = new PackagingStationAdapter(station);
            StationAdapters[station.GUID] = stationAdapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"Created station adapter for station {station.GUID}", DebugLogger.Category.Packager);
          }
          if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationMoveItemsPrefix: Station {station.GUID} in use or active, skipping", DebugLogger.Category.Packager);
            continue;
          }
          var items = stationAdapter.RefillList();
          foreach (var item in items)
          {
            var shelves = StorageUtilities.FindShelvesWithItem(__instance, item, stationAdapter.StartThreshold);
            if (shelves.Any() && shelves.Values.Sum() > 0)
            {
              var state = new StateData { CurrentState = EState.Planning, Station = stationAdapter };
              if (packagerAdapter.HandlePlanning(__instance.MoveItemBehaviour, state))
              {
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetStationMoveItemsPrefix: Planned refill routes for station {station.GUID}", DebugLogger.Category.Packager);
                __result = null;
                return false;
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

    [HarmonyPrefix]
    [HarmonyPatch("GetTransitRouteReady")]
    public static bool GetTransitRouteReadyPrefix(Packager __instance, ref AdvancedTransitRoute __result, out ItemInstance item)
    {
      item = null;
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetTransitRouteReadyPrefix: Checking routes for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        if (__instance.AssignedProperty == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetTransitRouteReadyPrefix: AssignedProperty is null for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          return true;
        }
        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var adapter))
        {
          adapter = new PackagerAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = adapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"Registered adapter for NPC {__instance.fullName}, type=Packager", DebugLogger.Category.Packager);
        }
        var packagerAdapter = adapter as PackagerAdapter;
        if (packagerAdapter == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetTransitRouteReadyPrefix: Failed to cast adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          return true;
        }
        var state = new StateData { CurrentState = EState.Planning };
        if (packagerAdapter.HandlePlanning(__instance.MoveItemBehaviour, state))
        {
          var route = state.ActiveRoutes.FirstOrDefault();
          if (route.TransitRoute != null)
          {
            __result = route.TransitRoute as AdvancedTransitRoute;
            item = route.Item;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetTransitRouteReadyPrefix: Planned route for item {item?.ID} for NPC {__instance.fullName}", DebugLogger.Category.Packager);
            return false;
          }
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetTransitRouteReadyPrefix: No routes ready for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        return true;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetTransitRouteReadyPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
        __result = null;
        return true;
      }
    }
  }
}