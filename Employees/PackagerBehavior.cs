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
using FishNet.Managing.Object;

namespace NoLazyWorkers.Employees
{
  public class PackagerBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    public readonly Packager _packager;
    public PackagerBehaviour(Packager packager, IEmployeeAdapter employee) : base(packager, employee)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      Employee = employee ?? throw new ArgumentNullException(nameof(employee));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour: Initialized for NPC {packager.fullName}", DebugLogger.Category.Packager);
      if (Npc == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerBehaviour: Npc is null after base constructor for Packager {packager.fullName}", DebugLogger.Category.Packager);
        Npc = packager;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour: Initialized for NPC {packager.fullName}, Npc={Npc?.fullName ?? "null"}", DebugLogger.Category.Packager);
    }

    public bool Planning(Employee employee, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerBehaviour.Planning: Starting for NPC {_packager.fullName}, Property {_packager.AssignedProperty}, Routes={state.ActiveRoutes.Count}", DebugLogger.Category.Packager);

      // Check if routes already fill inventory capacity
      int availableSlots = _packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      if (state.ActiveRoutes.Count >= availableSlots)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.Planning: Using {state.ActiveRoutes.Count} existing routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
        TransitionState(employee, state, EState.Transfer, "Using existing routes");
        return true;
      }

      ITransitEntity existingSource = null;
      // Check for additional routes from the same source as existing routes
      if (state.ActiveRoutes.Count > 0)
        existingSource = state.ActiveRoutes.FirstOrDefault().Source;
      var requests = new List<TransferRequest>();
      if (existingSource != null)
      {
        requests = FindAdditionalRoutesFromSource(_packager, state, existingSource);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerBehaviour.Planning: Found {requests.Count} additional routes from source {existingSource.GUID} for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      }

      // If no additional routes from existing source, search all sources
      if (requests.Count == 0)
      {
        requests = FindItemsNeedingMovement(_packager, state);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour.Planning: Found {requests.Count} routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      }

      if (requests.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerBehaviour.Planning: No routes found. Stations={PropertyStations.GetValueOrDefault(_packager.AssignedProperty)?.Count ?? 0}, Shelves={StorageExtensions.AnyShelves.Count}, Docks={_packager.AssignedProperty?.LoadingDocks.Length ?? 0}", DebugLogger.Category.Packager);
        if (state.ActiveRoutes.Count == 0)
        {
          TransitionState(employee, state, EState.Idle, "No routes planned");
          return false;
        }
        return true;
      }

      AddRoutes(employee, state, requests);
      return true;
    }

    private List<TransferRequest> FindAdditionalRoutesFromSource(Packager npc, StateData state, ITransitEntity source)
    {
      var requests = new List<TransferRequest>();
      var property = npc.AssignedProperty;
      if (property == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"FindAdditionalRoutesFromSource: Property is null for NPC {npc.fullName}", DebugLogger.Category.Packager);
        return requests;
      }

      int maxRoutes = Mathf.Min(MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null) - state.ActiveRoutes.Count);
      if (maxRoutes <= 0)
        return requests;

      // Check stations that need items from this source
      if (PropertyStations.TryGetValue(property, out var stations))
      {
        foreach (var station in stations)
        {
          if (station.IsInUse || station.HasActiveOperation) continue;
          var items = station.RefillList();
          foreach (var item in items)
          {
            if (IsItemTimedOut(property, item))
              continue;
            var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(source, item)
                .Where(s => s.Quantity > 0 && (!s.IsLocked || s.ActiveLock?.LockOwner == npc.NetworkObject)).ToList();
            if (sourceSlots.Count == 0)
              continue;

            var destination = station.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
              continue;

            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
              continue;

            if (!npc.Movement.CanGetTo(station.GetAccessPoint(npc)))
              continue;

            var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
              continue;

            var request = new TransferRequest(npc, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            requests.Add(request);
            maxRoutes--;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindAdditionalRoutesFromSource: Added route for {quantity} of {item.ID} to station {station.GUID}", DebugLogger.Category.Packager);
            if (maxRoutes <= 0)
              break;
          }
          if (maxRoutes <= 0)
            break;
        }
      }

      return requests;
    }

    public bool Operating(Employee employee, StateData state)
    {
      if (state.Station == null)
      {
        state.Station = StationUtilities.GetStationBehaviour(employee);
        if (state.Station == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleOperating: No station for {_packager.fullName}", DebugLogger.Category.Packager);
          TransitionState(employee, state, EState.Idle, "No station");
          return false;
        }
      }
      if (!IsAtLocation(state.Station.TransitEntity))
      {
        MoveTo(employee, state, state.Station.TransitEntity);
        return true;
      }
      if (state.Station.HasActiveOperation || state.Station.GetInputQuantity() < state.Station.StartThreshold)
      {
        TransitionState(employee, state, EState.Planning, "Cannot start operation");
        return false;
      }
      state.Station.StartOperation(employee);
      TransitionState(employee, state, EState.Completed, "Operation started");
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
            if (shelves.Count == 0)
              continue;
            var source = shelves.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
            var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(source, item)
                .Where(s => s.Quantity > 0 && (!s.IsLocked || s.ActiveLock?.LockOwner == npc.NetworkObject)).ToList();
            if (sourceSlots.Count == 0)
              continue;

            var destination = station.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
              continue;

            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
              continue;

            if (!npc.Movement.CanGetTo(station.GetAccessPoint(npc)))
              continue;

            var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
              continue;

            var request = new TransferRequest(npc, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            if (!pickupGroups.ContainsKey(source))
              pickupGroups[source] = new List<TransferRequest>();
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
          var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(dock, dockItem)
              .Where(s => s.Quantity > 0 && (!s.IsLocked || s.ActiveLock?.LockOwner == npc.NetworkObject)).ToList();
          if (sourceSlots.Count == 0)
            continue;

          var destination = StorageUtilities.FindShelfForDelivery(npc, dockItem);
          if (destination == null)
          {
            continue;
          }

          var transitEntity = destination as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(dockItem, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            if (transitEntity.GetInputCapacityForItem(dockItem, npc) <= 0)
              continue;
          }

          int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), transitEntity.GetInputCapacityForItem(dockItem, npc));
          if (quantity <= 0)
            continue;

          if (!npc.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, npc).position))
            continue;

          var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
            continue;

          var request = new TransferRequest(npc, dockItem, quantity, inventorySlot, dock, sourceSlots, destination, deliverySlots);
          if (!pickupGroups.ContainsKey(dock))
            pickupGroups[dock] = new List<TransferRequest>();
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

          var assignedShelf = StorageUtilities.FindShelfForDelivery(npc, item, allowAnyShelves: false);
          if (assignedShelf == null || assignedShelf == shelf)
          {
            continue;
          }

          var transitEntity = assignedShelf as ITransitEntity;
          var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
            continue;

          int quantity = Mathf.Min(slot.Quantity, transitEntity.GetInputCapacityForItem(item, npc));
          if (quantity <= 0)
            continue;

          var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
            continue;

          var request = new TransferRequest(npc, item, quantity, inventorySlot, shelf, [slot], assignedShelf, deliverySlots);
          if (!pickupGroups.ContainsKey(shelf))
            pickupGroups[shelf] = new List<TransferRequest>();
          pickupGroups[shelf].Add(request);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: ShelfRestock request for {quantity} of {item.ID}", DebugLogger.Category.Packager);
        }
      }

      foreach (var group in pickupGroups.OrderByDescending(g => g.Value.Max(r => GetPriority(r))))
      {
        requests.AddRange(group.Value.OrderByDescending(r => GetPriority(r)).Take(maxRoutes));
        if (requests.Count >= maxRoutes) break;
      }

      return requests;
    }
  }

  [HarmonyPatch(typeof(Packager))]
  public static class PackagerPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("UpdateBehaviour")]
    public static bool UpdateBehaviourPrefix(Packager __instance)
    {
      try
      {
        if (__instance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "UpdateBehaviourPrefix: Packager instance is null", DebugLogger.Category.Packager);
          return false;
        }
        if (__instance.MoveItemBehaviour is not AdvancedMoveItemBehaviour)
        {
          __instance.behaviour.behaviourStack.Remove(__instance.MoveItemBehaviour);
          Object.Destroy(__instance.MoveItemBehaviour);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Removed existing MoveItemBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          var advancedBehaviour = __instance.gameObject.AddComponent<AdvancedMoveItemBehaviour>();
          advancedBehaviour.Priority = 4;
          advancedBehaviour.EnabledOnAwake = false;
          var networkObject = __instance.gameObject.GetComponent<NetworkObject>();
          advancedBehaviour.beh = __instance.behaviour;
          advancedBehaviour.beh.Npc = __instance;
          advancedBehaviour.onEnable.AddListener(() => __instance.behaviour.AddEnabledBehaviour(advancedBehaviour));
          advancedBehaviour.onDisable.AddListener(() => __instance.behaviour.RemoveEnabledBehaviour(advancedBehaviour));
          ManagedObjects.InitializePrefab(networkObject, -1);
          __instance.MoveItemBehaviour = advancedBehaviour;
          (__instance.MoveItemBehaviour as AdvancedMoveItemBehaviour).employee = __instance;
          if (InstanceFinder.IsServer)
            __instance.MoveItemBehaviour.Preinitialize_Internal(networkObject, true);
          else
            __instance.MoveItemBehaviour.Preinitialize_Internal(networkObject, false);
          __instance.MoveItemBehaviour.NetworkInitializeIfDisabled();
          __instance.behaviour.behaviourStack.Add(__instance.MoveItemBehaviour);
          __instance.behaviour.behaviourStack = __instance.behaviour.behaviourStack.OrderByDescending((Behaviour x) => x.Priority).ToList();
          ActiveMoveItemBehaviours[__instance.GUID] = __instance.MoveItemBehaviour;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: Initialized for NPC={__instance.fullName}", DebugLogger.Category.Packager);
        }
        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employeeAdapter))
        {
          employeeAdapter = new PackagerAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = employeeAdapter;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Registered PackagerAdapter for NPC={__instance.fullName}", DebugLogger.Category.Packager);
        }
        if (!ActiveBehaviours.TryGetValue(__instance.GUID, out var employeeBehaviour))
        {
          employeeBehaviour = new PackagerBehaviour(__instance, employeeAdapter);
          ActiveBehaviours[__instance.GUID] = employeeBehaviour;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Initialized PackagerBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Packager);
        }
        //base
        if (__instance.Fired || (!(__instance.behaviour.activeBehaviour == null) && !(__instance.behaviour.activeBehaviour == __instance.WaitOutside)))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.Fired || (!(__instance.behaviour.activeBehaviour == null) && !(__instance.behaviour.activeBehaviour == __instance.WaitOutside)) {__instance.Fired} | {!(__instance.behaviour.activeBehaviour == null)} | {!(__instance.behaviour.activeBehaviour == __instance.WaitOutside)} for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          return false;
        }

        bool flag = false;
        bool flag2 = false;
        if (__instance.GetBed() == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.GetBed() == null for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          flag = true;
          __instance.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
        }
        else if (NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          flag = true;
          __instance.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
        }
        else if (!__instance.PaidForToday)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: !__instance.PaidForToday for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          if (__instance.IsPayAvailable())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.IsPayAvailable() for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            flag2 = true;
          }
          else
          {
            flag = true;
            __instance.SubmitNoWorkReason("I haven't been paid yet", "You can place cash in my briefcase on my bed.");
          }
        }

        if (flag)
        {
          __instance.SetWaitOutside(wait: true);
        }
        else if (InstanceFinder.IsServer && flag2 && __instance.IsPayAvailable())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: InstanceFinder.IsServer && flag2 && __instance.IsPayAvailable() for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          __instance.RemoveDailyWage();
          __instance.SetIsPaid();
        }
        if (!InstanceFinder.IsServer)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: !InstanceFinder.IsServer for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          return false;
        }

        if (__instance.PackagingBehaviour.Active)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.PackagingBehaviour.Active for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          __instance.MarkIsWorking();
        }
        else if (__instance.MoveItemBehaviour.Active)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: __instance.MoveItemBehaviour.Active for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          __instance.MarkIsWorking();
        }
        else if (EmployeeBehaviour.States.TryGetValue(__instance.GUID, out var state) && state.CurrentState != EState.Idle)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: CurrentState {state.CurrentState} != EState.Idle for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          employeeBehaviour.Update(__instance);
          __instance.MarkIsWorking();
        }
        else if (__instance.Fired)
        {
          __instance.LeavePropertyAndDespawn();
        }
        else
        {
          if (!__instance.CanWork())
          {
            __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
            __instance.SetIdle(true);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: !__instance.CanWork() for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            return false;
          }

          PackagingStation stationToAttend = __instance.GetStationToAttend();
          if (stationToAttend != null)
          {
            __instance.StartPackaging(stationToAttend);
            return false;
          }

          BrickPress brickPress = __instance.GetBrickPress();
          if (brickPress != null)
          {
            __instance.StartPress(brickPress);
            return false;
          }

          PackagingStation stationMoveItems = __instance.GetStationMoveItems();
          if (stationMoveItems != null)
          {
            __instance.StartMoveItem(stationMoveItems);
            return false;
          }

          BrickPress brickPressMoveItems = __instance.GetBrickPressMoveItems();
          if (brickPressMoveItems != null)
          {
            __instance.StartMoveItem(brickPressMoveItems);
            return false;
          }

          ItemInstance item;
          AdvancedTransitRoute transitRouteReady = __instance.GetTransitRouteReady(out item);
          if (transitRouteReady != null)
          {
            __instance.MoveItemBehaviour.Initialize(transitRouteReady, item, item.Quantity);
            __instance.MoveItemBehaviour.Enable_Networked(null);
          }
          /* else
          {
            __instance.SubmitNoWorkReason("There's nothing for me to do right now.", "I need one of my assigned stations to have enough product and packaging to get to work.");
            __instance.SetIdle(idle: true);
          } */
        }
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefixPrefixPrefix: Failed for packager {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
        if (__instance != null)
          __instance.SetIdle(true);
        return false;
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Fire")]
    public static void FirePostfix(Packager __instance)
    {
      try
      {
        if (ActiveBehaviours.TryGetValue(__instance.GUID, out var behaviour))
        {
          if (ActiveMoveItemBehaviours.TryGetValue(__instance.GUID, out var moveItemBehaviour))
          {
            behaviour.Disable(__instance);
          }
          ActiveBehaviours.Remove(__instance.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerFirePatch: Disabled PackagerBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Packager);
        }
        if (EmployeeAdapters.ContainsKey(__instance.GUID))
        {
          EmployeeAdapters.Remove(__instance.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerFirePatch: Removed PackagerAdapter for NPC={__instance.fullName}", DebugLogger.Category.Packager);
        }
        if (ActiveMoveItemBehaviours.ContainsKey(__instance.GUID))
        {
          ActiveMoveItemBehaviours.Remove(__instance.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerFirePatch: Removed AdvancedMoveItemBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Packager);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerFirePatch: Failed for Packager {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Packager);
      }
    }

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
          if (packagerAdapter.GetEmployeeBehaviour(__instance, out var employeeBehaviour))
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
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerPatch.GetStationMoveItemsPrefix: Registered adapter for NPC {__instance.fullName}, type=Packager", DebugLogger.Category.Packager);
        }

        var packagerAdapter = adapter as PackagerAdapter;
        if (packagerAdapter == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetStationMoveItemsPrefix: Failed to cast adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }

        // Ensure state exists
        if (!EmployeeBehaviour.States.ContainsKey(__instance.GUID))
        {
          EmployeeBehaviour.States[__instance.GUID] = new StateData();
        }
        var state = EmployeeBehaviour.States[__instance.GUID];

        int maxRoutes = Mathf.Min(EmployeeBehaviour.MAX_ROUTES_PER_CYCLE, __instance.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationMoveItemsPrefix: maxRoutes={maxRoutes} for NPC {__instance.fullName}, type=Packager", DebugLogger.Category.Packager);
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
            if (shelves.Count == 0)
              continue;

            var source = shelves.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
            var sourceSlots = StorageUtilities.GetOutputSlotsContainingTemplateItem(source, item)
                .Where(s => s.Quantity > 0 && (!s.IsLocked || s.ActiveLock?.LockOwner == __instance.NetworkObject)).ToList();
            if (sourceSlots.Count == 0)
              continue;

            var destination = stationAdapter.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, __instance.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
              continue;

            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), stationAdapter.MaxProductQuantity - stationAdapter.GetInputQuantity());
            if (quantity <= 0)
              continue;

            if (!__instance.Movement.CanGetTo(stationAdapter.GetAccessPoint(__instance)))
              continue;

            var inventorySlot = __instance.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
              continue;

            var request = new TransferRequest(__instance, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            var route = new PrioritizedRoute(request, EmployeeBehaviour.PRIORITY_STATION_REFILL)
            {
              TransitRoute = new AdvancedTransitRoute(source, destination)
            };
            state.ActiveRoutes.Add(route);
            EmployeeBehaviour.States[__instance.GUID] = state;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationMoveItemsPrefix: Created route for {quantity} of {item.ID} to station {station.GUID}", DebugLogger.Category.Packager);
            __result = null;
            return false;
          }

          if (state.ActiveRoutes.Count >= maxRoutes)
          {
            EmployeeBehaviour.States[__instance.GUID] = state;
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
        packagerAdapter.GetEmployeeBehaviour(__instance, out var employeeBehaviour);
        if (employeeBehaviour == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetMixStationsReadyToMove: No PackagerBehaviour for {__instance.fullName}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }

        if (!EmployeeBehaviour.States.ContainsKey(__instance.GUID))
        {
          EmployeeBehaviour.States[__instance.GUID] = new StateData();
        }
        var state = EmployeeBehaviour.States[__instance.GUID];

        int maxRoutes = Mathf.Min(EmployeeBehaviour.MAX_ROUTES_PER_CYCLE, __instance.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        if (state.ActiveRoutes.Count < maxRoutes)
        {
          state.CurrentState = EState.Planning;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetTransitRouteReadyPrefix: Entering Planning with Routes={state.ActiveRoutes.Count} for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          packagerAdapter.HandlePlanning(__instance, state);
        }
        if (state.ActiveRoutes.Count <= 0)
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetTransitRouteReadyPrefix: No routes ready for NPC {__instance.fullName}", DebugLogger.Category.Packager);
        else
        {
          employeeBehaviour.TransitionState(__instance, state, EState.Transfer, "Shelf/packaging delivery planned");
        }
        __result = null;
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetTransitRouteReadyPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
        __result = null;
        return false;
      }
    }
  }
}