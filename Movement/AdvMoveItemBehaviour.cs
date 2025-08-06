using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Employees.Extensions;
using System.Collections;
using UnityEngine;
using ScheduleOne.DevUtilities;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Movement.Utilities;
using NoLazyWorkers.CacheManager;
using NoLazyWorkers.SmartExecution;
using static NoLazyWorkers.Debug;
using NoLazyWorkers.Extensions;
using static NoLazyWorkers.Storage.SlotService;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Storage.SlotExtensions;

namespace NoLazyWorkers.Movement
{
  public class AdvMoveItemBehaviour : Behaviour
  {
    public Employee Employee { get; private set; }
    public TransitRoute activeRoute;
    public ItemInstance itemToRetrieveTemplate;
    public int grabbedAmount;
    public int maxMoveAmount = -1;
    public EState currentState;
    public bool skipPickup;

    private TravelTimeCacheService _travelTimeCacheService;
    private CacheService _cacheService;
    private readonly Queue<TransferRequest> _routeQueue = new();
    private TransferRequest _currentRoute;
    private Action<Employee, EmployeeInfo, MovementStatus> _callback;
    private EmployeeInfo _stateData;
    private Coroutine _currentCoroutine;
    private static readonly Dictionary<Guid, List<TransitRoute>> InventoryRoutes = new();
    private PauseState _pauseState = new();
    private bool _isPaused;
    private bool _resumed;
    private bool _anySuccess = false;
    private List<TransferRequest> _sameSourceRoutes = new();
    private int _processedCount;


    public void Initialize(List<TransferRequest> routes, EmployeeInfo stateData = null, Action<Employee, EmployeeInfo, MovementStatus> callback = null)
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.Initialize: {Employee.fullName} Initialize", Category.Movement);
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Warning, $"AdvMoveItemBeh.Initialize: Skipping client-side for NPC={Employee?.fullName}", Category.Movement);
        return;
      }
      _anySuccess = false;
      _cacheService = CacheService.GetOrCreateService(Employee.AssignedProperty);
      _travelTimeCacheService = TravelTimeCacheService.GetOrCreateService(Employee.AssignedProperty); // Initialize new service
      grabbedAmount = 0;
      _routeQueue.Clear();
      _callback = callback;
      _stateData = stateData;
      var routesBySource = routes.GroupBy(r => r.PickUp?.GUID ?? Guid.Empty)
          .ToDictionary(g => g.Key, g => g.ToList());
      foreach (var sourceGroup in routesBySource)
      {
        var sortedRoutes = sourceGroup.Value.OrderBy(r =>
        {
          var reference = sourceGroup.Key != Guid.Empty
                      ? NavMeshUtility.GetAccessPoint(r.PickUp, Employee)?.position ?? Employee.transform.position
                      : Employee.transform.position;
          var dropoffPoint = NavMeshUtility.GetAccessPoint(r.DropOff, Employee);
          return dropoffPoint != null
                      ? _travelTimeCacheService.GetTravelTime(r.PickUp ?? r.DropOff, r.DropOff) // Use new service
                      : float.MaxValue;
        }).ToList();
        foreach (var route in sortedRoutes)
        {
          _routeQueue.Enqueue(route);
          Log(Level.Verbose,
              $"AdvMoveItemBeh.Initialize: Enqueued route: item={route.Item?.ID}, qty={route.Quantity}, source={route.PickUp?.GUID}, dest={route.DropOff?.GUID} for NPC={Employee?.fullName}",
              Category.Movement);
        }
      }
      Log(Level.Info, $"AdvMoveItemBeh.Initialize: Queued {routes.Count} routes for NPC={Employee?.fullName}", Category.Movement);
      Enable_Networked(null);
      AdvBegin();
    }

    public void Setup(Employee employee)
    {
      // Initialize behavior with employee
      Log(Level.Verbose, $"AdvMoveItemBeh.Setup: Initializing for {employee.fullName}", Category.Movement);
      Employee = employee;
      Name = "Adv Move items";
      Priority = 4;
      EnabledOnAwake = false;
      beh = Employee.behaviour;
      beh.Npc = employee;
      onEnable.AddListener(() => Employee.behaviour.AddEnabledBehaviour(this));
      onDisable.AddListener(() => Employee.behaviour.RemoveEnabledBehaviour(this));
    }

    public enum EState
    {
      Idle,
      MovingToPickup,
      Grabbing,
      MovingToDropoff,
      Placing
    }

    [Serializable]
    public class PauseState
    {
      public EState CurrentState { get; set; }
      public int CurrentRouteIndex { get; set; }
      public List<TransferRequest> CurrentRouteGroup { get; set; }
      public float ProgressTime { get; set; }
    }

    public override void Begin()
    {
      return;
    }

    private void AdvBegin()
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.Begin: {Employee.fullName}", Category.Movement);
      base.Begin();
      StartTransit();
    }

    public void StartTransit()
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.StartTransit: {Employee.fullName}", Category.Movement);
      if (!FishNetExtensions.IsServer)
        return;
      currentState = EState.Idle;
      ProcessNextRoute();
    }

    /// <summary>
    /// Stops the current coroutine.
    /// </summary>
    public void StopCurrentActivity()
    {
      Log(Level.Verbose, $"StopCurrentActivity: {Employee.fullName}", Category.Movement);
      if (_currentCoroutine != null)
      {
        CoroutineRunner.Instance.StopCoroutine(_currentCoroutine);
        _currentCoroutine = null;
      }
      currentState = EState.Idle;
    }

    public override void Pause()
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.Pause: {Employee.fullName}", Category.Movement);
      if (_isPaused) return;

      _isPaused = true;
      base.Pause();
      SavePauseState();
      StopCurrentActivity();
    }

    public override void Resume()
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.Resume: {Employee.fullName}", Category.Movement);
      if (!_isPaused) return;

      _isPaused = false;
      base.Resume();
      RestorePauseState();
    }

    public override void Disable()
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.Disable: {Employee.fullName}", Category.Movement);
      base.Disable();
    }

    public override void ActiveMinPass()
    {
      return;
    }

    /// <summary>
    /// Ends the behavior, invoking callback with Success if any route succeeded, Failure otherwise.
    /// Leaves inventory items for another task to handle.
    /// </summary>
    public override void End()
    {
      Log(Level.Verbose, $"End: {Employee.fullName}, anySuccess={_anySuccess}", Category.Movement);
      base.End();
      if (_currentCoroutine != null)
      {
        CoroutineRunner.Instance.StopCoroutine(_currentCoroutine);
        _currentCoroutine = null;
      }
      _currentRoute = null;
      _routeQueue.Clear();
      InventoryRoutes.Remove(Employee.GUID);
      if (_callback != null)
      {
        _callback.Invoke(Employee, _stateData, _anySuccess ? MovementStatus.Success : MovementStatus.Failure);
        _callback = null;
      }
      currentState = EState.Idle;
    }

    private struct GrabPlaceInput
    {
      public TransferRequest Route;
      public ItemSlot Slot;
      public int Quantity;
    }

    private struct GrabPlaceOutput
    {
      public int Quantity;
      public string ItemId;
    }

    private bool IsTransitRouteValid(TransitRoute route, ItemInstance templateItem, int quantity, out string invalidReason)
    {
      invalidReason = string.Empty;
      if (route == null)
      {
        invalidReason = "Route is null!";
        return false;
      }
      if (!route.AreEntitiesNonNull())
      {
        invalidReason = "Entities are null!";
        return false;
      }
      if (!skipPickup && route.Source != null)
      {
        var slot = route.Source.GetFirstSlotContainingTemplateItem(templateItem, ITransitEntity.ESlotType.Output);
        if (slot == null || !SlotProcessingUtility.CanRemove(slot.ToSlotData(), templateItem.ToItemData(), quantity))
        {
          invalidReason = "Item is null or quantity is 0!";
          return false;
        }
      }
      if (!IsDestinationValid(route, templateItem))
      {
        invalidReason = "Can't access dropoff or dropoff is full!";
        return false;
      }
      return true;
    }

    private bool IsDestinationValid(TransitRoute route, ItemInstance item)
    {
      var slot = route.Destination.GetFirstSlotContainingTemplateItem(item, ITransitEntity.ESlotType.Input);
      var inputs = new[] { new SlotCheckInput { Slot = slot.ToSlotData(), Item = item.ToItemData(), Quantity = 1 } };
      var outputs = new List<bool>();
      Smart.Execute(
          uniqueId: "SlotCheck_Destination",
          itemCount: inputs.Length,
          action: (start, count, inArray, outList) =>
          {
            for (int i = start; i < start + count; i++)
            {
              outList.Add(SlotProcessingUtility.CanInsert(inArray[i].Slot, inArray[i].Item, inArray[i].Quantity));
            }
          },
          inputs: inputs,
          outputs: outputs,
          options: new SmartOptions { IsTaskSafe = true, IsPlayerVisible = false }
      );
      if (!outputs.Any() || !outputs[0])
      {
        Log(Level.Warning, $"AdvMoveItemBeh.IsDestinationValid: Destination has no capacity for item! for NPC={Employee?.fullName}", Category.Movement);
        return false;
      }
      if (!CanGetToDropoff(route))
      {
        Log(Level.Warning, $"AdvMoveItemBeh.IsDestinationValid: Cannot get to dropoff! for NPC={Employee?.fullName}", Category.Movement);
        return false;
      }
      if (!skipPickup && !CanGetToPickup(route))
      {
        Log(Level.Warning, $"AdvMoveItemBeh.IsDestinationValid: Cannot get to pickup! for NPC={Employee?.fullName}", Category.Movement);
        return false;
      }
      return true;
    }

    private bool CanGetToPickup(TransitRoute route)
    {
      return NavMeshUtility.GetAccessPoint(route.Source, Employee) != null;
    }

    private bool CanGetToDropoff(TransitRoute route)
    {
      return NavMeshUtility.GetAccessPoint(route.Destination, Employee) != null;
    }

    private IEnumerator GrabItemGroupCoroutine(List<TransferRequest> routes)
    {
      Log(Level.Verbose, $"GrabItemGroupCoroutine: {Employee.fullName}, routes={routes.Count}", Category.Movement);
      if (!FishNetExtensions.IsServer) { ProcessNextRoute(); yield break; }
      var sourceAccessPoint = NavMeshUtility.GetAccessPoint(routes[0].PickUp, Employee);
      if (sourceAccessPoint == null) { ProcessNextRoute(); yield break; }
      Employee.Movement.FaceDirection(sourceAccessPoint.forward);
      Employee.SetAnimationTrigger_Networked(null, "GrabItem");
      yield return new WaitForSeconds(0.5f);
      var inputs = routes.SelectMany(r => r.PickupSlots.Select(s => new GrabPlaceInput
      {
        Route = r,
        Slot = s,
        Quantity = Math.Min(r.Quantity, s.Quantity)
      })).ToArray();
      var outputs = new List<GrabPlaceOutput>();
      yield return Smart.Execute(
          uniqueId: "GrabItemGroup",
          itemCount: inputs.Length,
          action: ProcessGrabBatch,
          inputs: inputs,
          outputs: outputs,
          resultsAction: (results) =>
          {
            if (results.Any())
            {
              _travelTimeCacheService.StartTiming(routes[0].PickUp.GUID);
              Log(Level.Info, $"GrabItemGroupCoroutine: Grabbed {grabbedAmount} items, starting travel timer", Category.Movement);
              _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(DeliverToSortedDestinationsCoroutine(routes, skipPickup: false));
            }
            else
            {
              Log(Level.Warning, $"GrabItemGroupCoroutine: Failed to grab any items, skipping to next group", Category.Movement);
              ProcessNextRoute();
            }
            _currentCoroutine = null;
            currentState = EState.Idle;
          },
          options: new SmartOptions { IsTaskSafe = false, IsPlayerVisible = true }
      );
    }

    private void ProcessGrabBatch(int start, int count, GrabPlaceInput[] inputs, List<GrabPlaceOutput> outputs)
    {
      for (int i = start; i < start + count; i++)
      {
        var input = inputs[i];
        if (input.Quantity <= 0) continue;
        int inserted = input.Route.InventorySlot.AdvInsertItem(input.Slot.ItemInstance, input.Quantity, Employee.GUID, Employee);
        if (inserted == input.Quantity)
        {
          var (removed, takenItem) = input.Slot.AdvRemoveItem(input.Quantity, input.Route.PickUp.GUID, Employee);
          if (removed == input.Quantity)
          {
            outputs.Add(new GrabPlaceOutput { Quantity = input.Quantity, ItemId = takenItem.ID });
            grabbedAmount += input.Quantity;
            Log(Level.Info, $"GrabItemGroupCoroutine: Took {input.Quantity} {takenItem.ID}", Category.Movement);
          }
          else
          {
            input.Route.InventorySlot.AdvRemoveItem(removed, Employee.GUID, Employee);
            Log(Level.Warning, $"GrabItemGroupCoroutine: Failed to take {input.Quantity} from slot, rolled back", Category.Movement);
          }
        }
        else
        {
          Log(Level.Warning, $"GrabItemGroupCoroutine: Failed to insert {input.Quantity} into inventory", Category.Movement);
        }
      }
    }

    private IEnumerator MoveToPickupGroupCoroutine(List<TransferRequest> routes)
    {
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Warning, $"MoveToPickupGroupCoroutine: Skipping client-side for {Employee?.fullName}", Category.Movement);
        ProcessNextRoute();
        yield break;
      }
      var pickup = routes?[0].PickUp;
      if (pickup == null)
      {
        Log(Level.Warning, $"MoveToPickupGroupCoroutine: Null pickup for {Employee?.fullName}", Category.Movement);
        ProcessNextRoute();
        yield break;
      }
      Log(Level.Verbose, $"MoveToPickupGroupCoroutine: {Employee?.fullName}, routes={routes?.Count ?? 0}", Category.Movement);
      currentState = EState.MovingToPickup;
      _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(MoveToCoroutine(Employee, pickup));
      yield return new WaitUntil(() => _currentCoroutine == null);
      if (_pauseState.CurrentState != EState.MovingToPickup) // Check if paused
      {
        Log(Level.Verbose, $"MoveToPickupGroupCoroutine: Paused or failed for {Employee?.fullName}", Category.Movement);
        yield break;
      }
      Log(Level.Info, $"MoveToPickupGroupCoroutine: Reached pickup {pickup.GUID} for {Employee?.fullName}", Category.Movement);
      currentState = EState.Grabbing;
      _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(GrabItemGroupCoroutine(routes));
    }

    private IEnumerator DeliverToSortedDestinationsCoroutine(List<TransferRequest> routes, bool skipPickup)
    {
      Log(Level.Verbose, $"DeliverToSortedDestinationsCoroutine: {Employee.fullName}, routes={routes.Count}", Category.Movement);
      for (int routeIndex = _pauseState.CurrentRouteIndex; routeIndex < routes.Count; routeIndex++)
      {
        _pauseState.CurrentRouteIndex = routeIndex;
        currentState = EState.MovingToDropoff;
        var route = routes[routeIndex];
        var dropoff = route.DropOff;
        if (!IsDestinationValid(route.TransitRoute, route.Item))
        {
          Log(Level.Warning, $"DeliverToSortedDestinationsCoroutine: Invalid destination for item={route.Item.ID}", Category.Movement);
          continue;
        }
        if (NavMeshUtility.IsAtTransitEntity(dropoff, Employee))
        {
          currentState = EState.Placing;
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(PlaceItemToDropoffCoroutine(route, routes, routeIndex));
          yield return new WaitUntil(() => _currentCoroutine == null);
        }
        else
        {
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(MoveToCoroutine(Employee, dropoff));
          yield return new WaitUntil(() => _currentCoroutine == null);
          if (_pauseState.CurrentState != EState.MovingToDropoff) // Check if paused
          {
            Log(Level.Verbose, $"DeliverToSortedDestinationsCoroutine: Paused or failed for {Employee?.fullName}", Category.Movement);
            yield break;
          }
          if (!NavMeshUtility.IsAtTransitEntity(dropoff, Employee))
          {
            Log(Level.Warning, $"DeliverToSortedDestinationsCoroutine: Movement failed for dropoff={dropoff.GUID}", Category.Movement);
            _travelTimeCacheService.ClearTiming();
            continue;
          }
          if (!skipPickup)
          {
            float travelTime = _travelTimeCacheService.StopTiming(dropoff.GUID);
            _travelTimeCacheService.UpdateTravelTimeCache(routes[0].PickUp.GUID, dropoff.GUID, travelTime);
            Log(Level.Info, $"DeliverToSortedDestinationsCoroutine: Moved from pickup {routes[0].PickUp.GUID} to dropoff {dropoff.GUID} in {travelTime:F2}s", Category.Movement);
          }
          currentState = EState.Placing;
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(PlaceItemToDropoffCoroutine(route, routes, routeIndex));
          yield return new WaitUntil(() => _currentCoroutine == null);
        }
        yield return null;
      }
      ProcessNextRoute();
      currentState = EState.Idle;
      _pauseState.CurrentRouteIndex = 0;
      _currentCoroutine = null;
    }

    private IEnumerator PlaceItemToDropoffCoroutine(TransferRequest route, List<TransferRequest> routes, int routeIndex)
    {
      Log(Level.Verbose, $"PlaceItemToDropoffCoroutine: {Employee.fullName}, item={route.Item.ID}", Category.Movement);
      var dropoff = route.DropOff;
      var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
      if (accessPoint == null) yield break;
      Employee.Movement.FaceDirection(accessPoint.forward);
      Employee.SetAnimationTrigger_Networked(null, "GrabItem");
      yield return new WaitForSeconds(0.5f);
      ItemInstance itemToPlace = route.InventorySlot.ItemInstance;
      if (itemToPlace == null) yield break;
      var inputs = route.DropOffSlots.Select(s => new GrabPlaceInput
      {
        Route = route,
        Slot = s,
        Quantity = Math.Min(route.InventorySlot.Quantity, s.GetCapacityForItem(itemToPlace))
      }).ToArray();
      var outputs = new List<GrabPlaceOutput>();
      Action<int, int, GrabPlaceInput[], List<GrabPlaceOutput>> processBatch = ProcessPlaceBatch;
      Action<List<GrabPlaceOutput>> resultsAction = (results) =>
      {
        Log(Level.Info, $"PlaceItemToDropoffCoroutine: Placed {results.Sum(o => o.Quantity)}, anySuccess={_anySuccess}", Category.Movement);
        _currentCoroutine = null;
      };
      yield return Smart.Execute(
          uniqueId: "PlaceItemToDropoff",
          itemCount: inputs.Length,
          action: processBatch,
          inputs: inputs,
          outputs: outputs,
          resultsAction: resultsAction,
          options: new SmartOptions { IsTaskSafe = false, IsPlayerVisible = true }
      );
      yield return new WaitForSeconds(0.2f);
    }

    private void ProcessPlaceBatch(int start, int count, GrabPlaceInput[] inputs, List<GrabPlaceOutput> outputs)
    {
      for (int i = start; i < start + count; i++)
      {
        var input = inputs[i];
        if (input.Quantity <= 0) continue;
        int inserted = input.Slot.AdvInsertItem(input.Route.InventorySlot.ItemInstance, input.Quantity, input.Route.DropOff.GUID, Employee);
        if (inserted == input.Quantity)
        {
          var (removed, removedItem) = input.Route.InventorySlot.AdvRemoveItem(input.Quantity, Employee.GUID, Employee);
          if (removed == input.Quantity)
          {
            outputs.Add(new GrabPlaceOutput { Quantity = input.Quantity, ItemId = removedItem.ID });
            grabbedAmount -= input.Quantity;
            _anySuccess = true;
            Log(Level.Info, $"PlaceItemToDropoffCoroutine: Placed {input.Quantity} {removedItem.ID} at {input.Route.DropOff.GUID}", Category.Movement);
          }
          else
          {
            input.Slot.AdvRemoveItem(removed, input.Route.DropOff.GUID, Employee);
            Log(Level.Warning, $"PlaceItemToDropoffCoroutine: Failed to remove {input.Quantity} from inventory, rolled back", Category.Movement);
          }
        }
        else
        {
          Log(Level.Warning, $"PlaceItemToDropoffCoroutine: Failed to insert {input.Quantity} into dropoff slot", Category.Movement);
        }
      }
    }

    private void ProcessNextRoute()
    {
      if (!_resumed)
      {
        Log(Level.Verbose, $"ProcessNextRoute: {Employee.fullName}", Category.Movement);
        if (_routeQueue.Count == 0)
        {
          Log(Level.Info, $"ProcessNextRoute: All routes processed, anySuccess={_anySuccess}", Category.Movement);
          Disable_Networked(null);
          return;
        }
        var currentSource = _routeQueue.Peek().PickUp?.GUID ?? Guid.Empty;
        _sameSourceRoutes = new List<TransferRequest>();
        while (_routeQueue.Count > 0 && (_routeQueue.Peek().PickUp?.GUID ?? Guid.Empty) == currentSource)
          _sameSourceRoutes.Add(_routeQueue.Dequeue());
        if (!_sameSourceRoutes.Any() || _sameSourceRoutes.Any(r => r.Item == null || r.DropOff == null))
        {
          Log(Level.Error, $"ProcessNextRoute: Invalid routes in group: count={_sameSourceRoutes.Count}", Category.Movement);
          ProcessNextRoute();
          return;
        }
        _currentRoute = _sameSourceRoutes[0];
        var firstRoute = _currentRoute;
        itemToRetrieveTemplate = firstRoute.Item;
        maxMoveAmount = _sameSourceRoutes.Sum(r => r.Quantity);
        skipPickup = firstRoute.PickUp == null;
        activeRoute = firstRoute.TransitRoute;
        Log(Level.Info, $"ProcessNextRoute: Processing {_sameSourceRoutes.Count} routes from source={currentSource}, total qty={maxMoveAmount}", Category.Movement);
        if (skipPickup)
        {
          if (!InventoryRoutes.ContainsKey(Employee.GUID))
            InventoryRoutes[Employee.GUID] = new();
          InventoryRoutes[Employee.GUID].Add(activeRoute);
        }
        if (!IsTransitRouteValid(activeRoute, itemToRetrieveTemplate, firstRoute.Quantity, out var invalidReason))
        {
          Log(Level.Warning, $"ProcessNextRoute: Skipping invalid transit route: {invalidReason}", Category.Movement);
          ProcessNextRoute();
          return;
        }
      }
      if (skipPickup)
      {
        grabbedAmount = _sameSourceRoutes.Sum(r => r.InventorySlot.Quantity);
        Log(Level.Verbose, $"ProcessNextRoute: SkipPickup enabled, grabbedAmount={grabbedAmount}", Category.Movement);
        _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(DeliverToSortedDestinationsCoroutine(_sameSourceRoutes, skipPickup: true));
      }
      else
      {
        _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(MoveToPickupGroupCoroutine(_sameSourceRoutes));
      }
    }

    private void SavePauseState()
    {
      _pauseState.CurrentState = currentState;
      _pauseState.CurrentRouteGroup = _sameSourceRoutes.ToList();
      Log(Level.Verbose, $"SavePauseState: Saved state, state={currentState}", Category.Movement);
    }

    private void RestorePauseState()
    {
      currentState = _pauseState.CurrentState;
      Log(Level.Verbose, $"RestorePauseState: Restoring state={currentState}", Category.Movement);
      switch (currentState)
      {
        case EState.MovingToPickup:
        case EState.Grabbing:
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(MoveToPickupGroupCoroutine(_pauseState.CurrentRouteGroup));
          break;
        case EState.MovingToDropoff:
        case EState.Placing:
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(DeliverToSortedDestinationsCoroutine(_pauseState.CurrentRouteGroup, skipPickup));
          break;
        default:
          _resumed = true;
          ProcessNextRoute();
          break;
      }
    }

    [HarmonyPatch(typeof(TransitRoute))]
    public class TransitRoutePatch
    {
      [HarmonyPrefix]
      [HarmonyPatch("AreEntitiesNonNull")]
      public static bool AreEntitiesNonNullPrefix(TransitRoute __instance, ref bool __result)
      {
        try
        {
          if (__instance.Source == null && __instance.Destination != null && InventoryRoutes.Values.Any(v => v.Contains(__instance)))
          {
            Log(Level.Verbose, $"AdvMoveItemBeh.TransitRoutePatch: Allowing inventory route with null Source", Category.Movement);
            __result = true;
            return false;
          }
          return true;
        }
        catch (Exception e)
        {
          Log(Level.Error, $"AdvMoveItemBeh.TransitRoutePatch: Failed, error={e}", Category.Movement);
          __result = false;
          return false;
        }
      }
    }
  }
}