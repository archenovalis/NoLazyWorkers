using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
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
using static NoLazyWorkers.Handlers.HandlerUtilities;
using static NoLazyWorkers.NoLazyUtilities;

namespace NoLazyWorkers.Handlers
{
  public class RouteQueueManager
  {
    private readonly Packager _packager;
    private readonly List<PrioritizedRoute> _routeQueue = new();
    private readonly Dictionary<ItemInstance, List<ItemSlot>> _lockedDestinationSlots = new();

    public RouteQueueManager(Packager packager)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
    }

    public void AddRoutes(List<TransferRequest> requests)
    {
      _routeQueue.Clear();
      _lockedDestinationSlots.Clear();
      foreach (var request in requests)
      {
        var route = new TransitRoute(request.PickupLocation, request.DeliveryLocation);
        int priority = request.DeliveryLocation is IStationAdapter
            ? PRIORITY_STATION_REFILL
            : request.PickupLocation is LoadingDock
                ? PRIORITY_LOADING_DOCK
                : PRIORITY_SHELF_RESTOCK;

        _routeQueue.Add(new PrioritizedRoute
        {
          Route = route,
          Item = request.PickupItem,
          Quantity = request.Quantity,
          DestinationSlots = request.DeliverySlots,
          Priority = priority
        });
        _lockedDestinationSlots[request.PickupItem] = request.DeliverySlots;
      }
      _routeQueue.Sort((a, b) => b.Priority.CompareTo(a.Priority));
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"RouteQueueManager.AddRoutes: Added {_routeQueue.Count} routes", DebugLogger.Category.Handler);
    }

    public bool TryGetNextRoute(out TransitRoute route, out ItemInstance item, out int quantity, out List<ItemSlot> destinationSlots)
    {
      route = null;
      item = null;
      quantity = 0;
      destinationSlots = null;
      if (_routeQueue.Count == 0) return false;

      var nextRoute = _routeQueue[0];
      route = nextRoute.Route;
      item = nextRoute.Item;
      quantity = nextRoute.Quantity;
      destinationSlots = nextRoute.DestinationSlots;
      return true;
    }

    public void RemoveCompletedRoute()
    {
      if (_routeQueue.Count > 0)
      {
        var route = _routeQueue[0];
        if (_lockedDestinationSlots.ContainsKey(route.Item))
        {
          route.Route.Destination.RemoveSlotLocks(_packager.NetworkObject);
          _lockedDestinationSlots.Remove(route.Item);
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"RouteQueueManager.RemoveCompletedRoute: Removed route for {route.Item.ID} from {route.Route.Source.Name} to {route.Route.Destination.Name}", DebugLogger.Category.Handler);
        _routeQueue.RemoveAt(0);
      }
    }

    public void PickupAllItems()
    {
      if (_routeQueue.Count == 0 || _packager == null) return;

      var pickupLocation = _routeQueue[0].Route.Source;
      if (!_packager.movement.CanGetTo(pickupLocation))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PickupAllItems: Cannot reach pickup location {pickupLocation.Name}", DebugLogger.Category.Handler);
        _routeQueue.Clear();
        _lockedDestinationSlots.Clear();
        return;
      }

      var accessPoint = NavMeshUtility.GetAccessPoint(pickupLocation, _packager);
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PickupAllItems: No access point for {pickupLocation.Name}", DebugLogger.Category.Handler);
        _routeQueue.Clear();
        _lockedDestinationSlots.Clear();
        return;
      }

      _packager.Movement.SetDestination(accessPoint.position);
      CoroutineRunner.Instance.RunCoroutine(WaitForPickup(pickupLocation));
    }

    private IEnumerator WaitForPickup(ITransitEntity pickupLocation)
    {
      yield return new WaitUntil(() => NavMeshUtility.IsAtTransitEntity(pickupLocation, _packager, 0.4f));

      var failedRoutes = new List<PrioritizedRoute>();
      foreach (var route in _routeQueue)
      {
        var sourceSlot = route.Route.Source.GetFirstSlotContainingTemplateItem(route.Item, ITransitEntity.ESlotType.Output);
        if (sourceSlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PickupAllItems: Failed to lock source slot for {route.Item.ID} at {route.Route.Source.Name}", DebugLogger.Category.Handler);
          failedRoutes.Add(route);
          continue;
        }

        int amountToGrab = Mathf.Min(route.Quantity, sourceSlot.Quantity);
        if (amountToGrab <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"PickupAllItems: No items to grab for {route.Item.ID} at {route.Route.Source.Name}", DebugLogger.Category.Handler);
          failedRoutes.Add(route);
          continue;
        }

        var itemCopy = route.Item.GetCopy(amountToGrab);
        sourceSlot.ChangeQuantity(-amountToGrab, false);
        _packager.Inventory.InsertItem(itemCopy, true);
        _packager.SetAnimationTrigger_Networked(null, "GrabItem");

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PickupAllItems: Picked up {amountToGrab} of {route.Item.ID} from {route.Route.Source.Name}", DebugLogger.Category.Handler);
      }

      foreach (var failedRoute in failedRoutes)
      {
        if (_lockedDestinationSlots.ContainsKey(failedRoute.Item))
        {
          failedRoute.Route.Destination.RemoveSlotLocks(_packager.NetworkObject);
          _lockedDestinationSlots.Remove(failedRoute.Item);
        }
        _routeQueue.Remove(failedRoute);
      }
    }

    public void Clear()
    {
      foreach (var kvp in _lockedDestinationSlots)
        kvp.Value.ForEach(slot => slot.RemoveLock());
      _routeQueue.Clear();
      _lockedDestinationSlots.Clear();
    }

    private struct PrioritizedRoute
    {
      public TransitRoute Route;
      public ItemInstance Item;
      public int Quantity;
      public List<ItemSlot> DestinationSlots;
      public int Priority;
    }
  }

  [HarmonyPatch(typeof(Packager))]
  public class PackagerPatch
  {
    private static readonly float CHECK_INTERVAL = 2f;
    private static readonly float COOLDOWN_DURATION = 5f;
    private static readonly Dictionary<Packager, float> _lastCheckTimes = new();
    private static readonly Dictionary<Packager, RouteQueueManager> _routeQueues = new();
    private static readonly Dictionary<Packager, bool> _eventTriggers = new();
    private static readonly Dictionary<Packager, bool> _isPickingUp = new();

    [HarmonyPrefix]
    [HarmonyPatch("UpdateBehaviour")]
    static bool UpdateBehaviourPrefix(Packager __instance)
    {
      if (__instance.Fired || !__instance.CanWork() || !InstanceFinder.IsServer)
        return true;

      if (__instance.PackagingBehaviour.Active || __instance.MoveItemBehaviour.Active)
      {
        if (__instance.MoveItemBehaviour.Active && !__instance.MoveItemBehaviour.Initialized)
        {
          if (_routeQueues.ContainsKey(__instance))
            _routeQueues[__instance].RemoveCompletedRoute();
        }
        __instance.MarkIsWorking();
        return true;
      }

      RouteQueueManager routeQueue;
      if (!_routeQueues.ContainsKey(__instance))
      {
        routeQueue = new RouteQueueManager(__instance);
        _routeQueues.Add(__instance, routeQueue);
      }
      else
      {
        routeQueue = _routeQueues[__instance];
      }

      bool isPickingUp = _isPickingUp.ContainsKey(__instance) && _isPickingUp[__instance];

      float currentTime = Time.time;
      bool eventTriggered = _eventTriggers.ContainsKey(__instance) && _eventTriggers[__instance];

      if (!isPickingUp && (!_lastCheckTimes.ContainsKey(__instance) || currentTime - _lastCheckTimes[__instance] >= CHECK_INTERVAL || eventTriggered))
      {
        var requests = FindItemsNeedingMovement(__instance);
        if (requests.Count > 0)
        {
          routeQueue.AddRoutes(requests);
          routeQueue.PickupAllItems();
          _lastCheckTimes[__instance] = currentTime;
          if (_eventTriggers.ContainsKey(__instance))
            _eventTriggers[__instance] = false;
          if (!_isPickingUp.ContainsKey(__instance))
            _isPickingUp.Add(__instance, true);
          else
            _isPickingUp[__instance] = true;
          __instance.MarkIsWorking();
          return true;
        }
        else
        {
          _lastCheckTimes[__instance] = currentTime + COOLDOWN_DURATION;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, "UpdateBehaviourPrefix: No tasks found, entering cooldown", DebugLogger.Category.Handler);
        }
      }

      if (_isPickingUp.ContainsKey(__instance) && _isPickingUp[__instance])
      {
        if (__instance.Inventory.ItemSlots.Any(s => s?.ItemInstance != null))
        {
          _isPickingUp[__instance] = false;
        }
        else
        {
          return true;
        }
      }

      if (routeQueue.TryGetNextRoute(out var route, out var itemInstance, out var quantity, out var destinationSlots))
      {
        var itemInInventory = __instance.Inventory.GetFirstIdenticalItem(itemInstance, null);
        if (itemInInventory == null || itemInInventory.Quantity < quantity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"UpdateBehaviourPrefix: Item {itemInstance.ID} not found in inventory or insufficient quantity", DebugLogger.Category.Handler);
          routeQueue.RemoveCompletedRoute();
          return true;
        }

        __instance.MoveItemBehaviour.Initialize(route, itemInstance, quantity, true);
        __instance.MoveItemBehaviour.Enable_Networked(null);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"UpdateBehaviourPrefix: Initialized MoveItemBehaviour for {itemInstance.ID} from inventory to {route.Destination.Name}, quantity={quantity}", DebugLogger.Category.Handler);
        __instance.MarkIsWorking();
        return true;
      }

      if (!__instance.PackagingBehaviour.Active)
      {
        var stationToAttend = __instance.GetStationToAttend();
        if (stationToAttend != null)
        {
          __instance.StartPackaging(stationToAttend);
          __instance.MarkIsWorking();
          return true;
        }
      }

      __instance.SubmitNoWorkReason("Nothing to do.", "Assign me to stations or ensure items need moving.", 0);
      __instance.SetIdle(true);
      return true;
    }

    public static void TriggerEvent(Packager packager)
    {
      if (!_eventTriggers.ContainsKey(packager))
        _eventTriggers.Add(packager, true);
      else
        _eventTriggers[packager] = true;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"TriggerEvent: Event triggered for Packager {packager.GUID}", DebugLogger.Category.Handler);
    }
  }
}