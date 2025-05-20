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
using NoLazyWorkers.General;
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
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerBehaviour.Planning: Starting for NPC {_packager.fullName}, Property {Employee.AssignedProperty}, Routes={state.ActiveRoutes.Count}", DebugLogger.Category.Packager);

      // Check if routes already exist
      if (state.ActiveRoutes.Count >= behaviour.Npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.Planning: Using {state.ActiveRoutes.Count} existing routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
        TransitionState(behaviour, state, EState.Grabbing, "Using existing routes");
        return true;
      }

      var requests = FindItemsNeedingMovement(_packager, state);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.Planning: Found {requests.Count} routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      if (requests.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.Planning: No routes found. Stations={PropertyStations.GetValueOrDefault(Employee.AssignedProperty)?.Count ?? 0}, Shelves={StorageExtensions.AnyShelves.Count}, Docks={Employee.AssignedProperty?.LoadingDocks.Length ?? 0}", DebugLogger.Category.Packager);
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
      var request = new TransferRequest(behaviour.Npc, item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, deliverySlots);
      state.ActiveRoutes.Add(new PrioritizedRoute(request, PRIORITY_SHELF_RESTOCK));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.HandleInventoryItem: Added route for {quantity} of {item.ID}", DebugLogger.Category.Packager);
      TransitionState(behaviour, state, EState.Delivery, "Inventory route added");
      return true;
    }

    public bool Grabbing(Behaviour behaviour, StateData state)
    {
      var route = state.ActiveRoutes.FirstOrDefault();
      if (route.PickupLocation == null) // Inventory route
      {
        TransitionState(behaviour, state, EState.Delivery, "Inventory route, skip pickup");
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
      TransitionState(behaviour, state, EState.Delivery, "Items picked up");
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
      int maxRoutes = Mathf.Min(MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null) - state.ActiveRoutes.Count);
      var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();
      var processedShelves = new List<Guid>();

      // Station Refill
      if (PropertyStations.TryGetValue(property, out var stations))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Checking {stations.Count} stations for NPC {npc.fullName}", DebugLogger.Category.Packager);
        foreach (var station in stations)
        {
          if (maxRoutes <= 0) break;
          if (station.IsInUse || station.HasActiveOperation) continue;
          var items = station.RefillList();
          foreach (var item in items)
          {
            if (IsItemTimedOut(property, item))
              continue;
            var shelves = StorageUtilities.FindShelvesWithItem(npc, item, 1);
            var source = shelves.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
            var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(source, item);
            if (sourceSlots.Count == 0) continue;
            sourceSlots.ApplyLocks(npc, "Route planning lock");
            var destination = station.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }
            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }
            if (!npc.Movement.CanGetTo(station.GetAccessPoint(npc)))
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
            var request = new TransferRequest(npc, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            if (!pickupGroups.ContainsKey(source)) pickupGroups[source] = new List<TransferRequest>();
            pickupGroups[source].Add(request);
            maxRoutes--;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: StationRefill request for {quantity} of {item.ID}", DebugLogger.Category.Packager);
            break;
          }
        }
      }

      // Loading Dock
      foreach (var dock in npc.AssignedProperty.LoadingDocks ?? Enumerable.Empty<LoadingDock>())
      {
        if (maxRoutes <= 0) break;
        if (!dock.IsInUse) continue;
        foreach (var slot in dock.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
          var dockItem = slot.ItemInstance;
          if (IsItemTimedOut(npc.AssignedProperty, dockItem))
            continue;
          var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(dock, dockItem);
          if (sourceSlots.Count == 0) continue;
          sourceSlots.ApplyLocks(npc, "Route planning lock");
          var destination = StorageUtilities.FindShelfForDelivery(npc, dockItem);
          if (destination == null)
          {
            sourceSlots.RemoveLock();
            AddItemTimeout(npc.AssignedProperty, dockItem);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetTransitRouteReadyPrefix: Added item {dockItem.ID} to NoDestinationCache for property {npc.AssignedProperty}", DebugLogger.Category.Packager);
            continue;
          }
          var transitEntity = destination as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(dockItem, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            sourceSlots.RemoveLock();
            if (transitEntity.GetInputCapacityForItem(dockItem, npc) <= 0)
              AddItemTimeout(npc.AssignedProperty, dockItem);
            continue;
          }
          int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), transitEntity.GetInputCapacityForItem(dockItem, npc));
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
          var request = new TransferRequest(npc, dockItem, quantity, inventorySlot, dock, sourceSlots, destination, deliverySlots);
          if (!pickupGroups.ContainsKey(dock)) pickupGroups[dock] = new List<TransferRequest>();
          pickupGroups[dock].Add(request);
          maxRoutes--;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Dock request for {quantity} of {dockItem.ID}", DebugLogger.Category.Packager);
        }
      }

      // Shelf Restock (Any to Specific)
      foreach (var shelf in StorageExtensions.AnyShelves)
      {
        if (maxRoutes <= 0) break;
        if (shelf?.OutputSlots == null || processedShelves.Contains(shelf.GUID)) continue;
        processedShelves.Add(shelf.GUID);
        foreach (var slot in shelf.OutputSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
          var item = slot.ItemInstance;
          if (IsItemTimedOut(property, item))
            continue;
          if (NoDestinationCache.TryGetValue(property, out var cache) &&
              cache.Any(i => i.CanStackWith(item, false)))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: Skipping item {item.ID} in NoDestinationCache", DebugLogger.Category.Packager);
            continue;
          }
          slot.ApplyLocks(npc, "Route planning lock");
          var assignedShelf = StorageUtilities.FindShelfForDelivery(npc, item, false);
          if (assignedShelf == null || assignedShelf == shelf)
          {
            slot.RemoveLock();
            if (assignedShelf == null)
            {
              cache.AddUnique(item);
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindItemsNeedingMovement: Added item {item.ID} to NoDestinationCache for property {property}", DebugLogger.Category.Packager);
            }
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
          var request = new TransferRequest(npc, item, quantity, inventorySlot, shelf, new List<ItemSlot> { slot }, assignedShelf, deliverySlots);
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
    public static Dictionary<Guid, List<PrioritizedRoute>> PrestateRoutes = new();

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
        if (!PrestateRoutes.ContainsKey(__instance.GUID))
          PrestateRoutes[__instance.GUID] = new();
        int maxRoutes = Mathf.Min(EmployeeBehaviour.MAX_ROUTES_PER_CYCLE, __instance.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        foreach (PackagingStation station in __instance.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
        {
          if (station == null)
            continue;
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            stationAdapter = new PackagingStationAdapter(station);
            StationAdapters[station.GUID] = stationAdapter;
          }
          if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
            continue;
          var items = stationAdapter.RefillList();
          foreach (var item in items)
          {
            if (IsItemTimedOut(__instance.AssignedProperty, item))
              continue;
            var shelves = StorageUtilities.FindShelvesWithItem(__instance, item, stationAdapter.StartThreshold);
            var source = shelves.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
            var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(source, item);
            if (sourceSlots.Count == 0) continue;
            sourceSlots.ApplyLocks(__instance, "Route planning lock");
            var destination = stationAdapter.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, __instance.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }
            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), stationAdapter.MaxProductQuantity - stationAdapter.GetInputQuantity());
            if (quantity <= 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }
            if (!__instance.Movement.CanGetTo(stationAdapter.GetAccessPoint(__instance)))
            {
              sourceSlots.RemoveLock();
              continue;
            }
            var inventorySlot = __instance.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
            {
              sourceSlots.RemoveLock();
              continue;
            }
            var request = new TransferRequest(__instance, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            var route = new PrioritizedRoute(request, EmployeeBehaviour.PRIORITY_STATION_REFILL)
            {
              TransitRoute = new AdvancedTransitRoute(source, destination)
            };
            PrestateRoutes[__instance.GUID].Add(route);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationMoveItemsPrefix: Created route for {quantity} of {item.ID} to station {station.GUID}", DebugLogger.Category.Packager);
            __result = null;
            return false;
          }
          if (PrestateRoutes[__instance.GUID].Count >= maxRoutes)
          {
            __result = null;
            return false;
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetTransitRouteReadyPrefix: Failed to cast adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
        if (!PrestateRoutes.ContainsKey(__instance.GUID))
          PrestateRoutes[__instance.GUID] = new();

        int maxRoutes = Mathf.Min(EmployeeBehaviour.MAX_ROUTES_PER_CYCLE, __instance.Inventory.ItemSlots.Count(s => s.ItemInstance == null) - PrestateRoutes[__instance.GUID].Count);
        if (PrestateRoutes[__instance.GUID].Count < maxRoutes)
        {
          var state = new StateData() { CurrentState = EState.Planning, ActiveRoutes = PrestateRoutes[__instance.GUID] };
          if (packagerAdapter.HandlePlanning(__instance.MoveItemBehaviour, state))
          {
            var route = state.ActiveRoutes.OrderByDescending(r => r.Priority).FirstOrDefault(r => r.TransitRoute != null);
            if (route.TransitRoute != null)
            {
              item = route.Item;
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetTransitRouteReadyPrefix: Planned route for item {item?.ID} for NPC {__instance.fullName}, priority={route.Priority}", DebugLogger.Category.Packager);
            }
          }
        }
        foreach (var route in PrestateRoutes[__instance.GUID])
        {
          __instance.MoveItemBehaviour.Initialize(route.TransitRoute, route.Item, route.Quantity);
          __instance.MoveItemBehaviour.Enable_Networked(null);
        }
        PrestateRoutes[__instance.GUID].Clear();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetTransitRouteReadyPrefix: No routes ready for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        return true;
      }
      catch (Exception e)
      {
        PrestateRoutes[__instance.GUID].Clear();
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetTransitRouteReadyPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
        return true;
      }
    }
  }
}