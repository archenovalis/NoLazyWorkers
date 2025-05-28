using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using System.Collections;
using UnityEngine;
using ScheduleOne.DevUtilities;
using MelonLoader;
using FishNet.Managing.Timing;

namespace NoLazyWorkers.General
{
  public static class TransitDistanceCache
  {
    // Cache of distances between transit entity pairs, using sorted GUIDs as keys
    private static readonly Dictionary<(Guid, Guid), float> _distanceCache = new();
    // Tracks known transit entities for validation
    private static readonly Dictionary<Guid, ITransitEntity> _knownEntities = new();
    // Synchronization object for thread safety
    private static readonly object _lock = new object();

    /// <summary>
    /// Gets the distance between two transit entities, using cached value if available.
    /// </summary>
    /// <param name="entityA">First transit entity.</param>
    /// <param name="entityB">Second transit entity.</param>
    /// <param name="employee">Employee for NavMesh access point calculation.</param>
    /// <returns>Distance between entities, or float.MaxValue if invalid.</returns>
    public static float GetDistance(ITransitEntity entityA, ITransitEntity entityB, Employee employee)
    {
      if (entityA == null || entityB == null || employee == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.GetDistance: Invalid input: entityA={entityA?.GUID}, entityB={entityB?.GUID}, employee={employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        return float.MaxValue;
      }

      // Create symmetric key by sorting GUIDs
      var key = entityA.GUID.CompareTo(entityB.GUID) < 0
          ? (entityA.GUID, entityB.GUID)
          : (entityB.GUID, entityA.GUID);

      lock (_lock)
      {
        // Check cache
        if (_distanceCache.TryGetValue(key, out float distance))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvMoveItemBeh.GetDistance: Cache hit for {key.Item1} to {key.Item2}: {distance}",
              DebugLogger.Category.EmployeeCore);
          return distance;
        }

        // Calculate distance
        var pointA = NavMeshUtility.GetAccessPoint(entityA, employee);
        var pointB = NavMeshUtility.GetAccessPoint(entityB, employee);
        if (pointA == null || pointB == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.GetDistance: No access points for {entityA.GUID} or {entityB.GUID}",
              DebugLogger.Category.EmployeeCore);
          return float.MaxValue;
        }

        distance = Vector3.Distance(pointA.position, pointB.position);
        _distanceCache.TryAdd(key, distance);

        // Register entities
        _knownEntities.TryAdd(entityA.GUID, entityA);
        _knownEntities.TryAdd(entityB.GUID, entityB);

        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.GetDistance: Cached new distance {distance} for {key.Item1} to {key.Item2}",
            DebugLogger.Category.EmployeeCore);
        return distance;
      }
    }

    /// <summary>
    /// Clears cache entries involving a destroyed transit entity.
    /// </summary>
    /// <param name="entityGuid">GUID of the destroyed entity.</param>
    public static void ClearCacheForEntity(Guid entityGuid)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.ClearCacheForEntity: Clearing cache for entity {entityGuid}",
          DebugLogger.Category.EmployeeCore);

      lock (_lock)
      {
        // Remove from known entities
        if (_knownEntities.ContainsKey(entityGuid))
          _knownEntities.Remove(entityGuid);

        // Collect keys to remove
        var keysToRemove = _distanceCache.Keys
            .Where(k => k.Item1 == entityGuid || k.Item2 == entityGuid)
            .ToList();

        // Remove cache entries
        foreach (var key in keysToRemove)
        {
          if (_distanceCache.ContainsKey(key))
            _distanceCache.Remove(key);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvMoveItemBeh.ClearCacheForEntity: Removed cache entry {key.Item1} to {key.Item2}",
              DebugLogger.Category.EmployeeCore);
        }
      }
    }
  }

  public static class AdvancedMoveItemBehaviourUtilities
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
  }

  public class AdvancedMoveItemBehaviour : Behaviour
  {
    public void Setup(Employee employee)
    {
      // Initialize behavior with employee
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.Setup: Initializing for {employee.fullName}", DebugLogger.Category.EmployeeCore);
      Employee = employee;
      Name = "Advanced Move items";
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
      Grabbing,
      Placing
    }

    [Serializable]
    public class PauseState
    {
      public EState CurrentState { get; set; }
      public int CurrentRouteIndex { get; set; }
      public List<PrioritizedRoute> CurrentRouteGroup { get; set; }
      public string CurrentCoroutineType { get; set; } // e.g., "WalkToPickup", "Deliver", "Place"
      public float ProgressTime { get; set; } // Time spent in current coroutine
    }

    public Employee Employee { get; private set; }
    private readonly Queue<PrioritizedRoute> _routeQueue = new();
    private PrioritizedRoute? _currentRoute;
    private Action<Employee, StateData, Status> _callback;
    private bool _success;
    private StateData _stateData;
    private object _currentCoroutine;
    private static readonly Dictionary<Guid, List<TransitRoute>> InventoryRoutes = new();
    public TransitRoute assignedRoute;
    public ItemInstance itemToRetrieveTemplate;
    public int grabbedAmount;
    public int maxMoveAmount = -1;
    public EState currentState;
    public bool skipPickup;
    private PauseState _pauseState = new();
    private bool _isPaused;
    private bool _resumed;
    private bool _anySuccess = false;
    private List<PrioritizedRoute> _sameSourceRoutes = new();

    /// <summary>
    /// Initializes the behavior with routes, grouping by pickup source and sorting by destination proximity.
    /// Routes are processed in groups from the same pickup source, with items picked up once and delivered
    /// to destinations sorted by distance using TransitDistanceCache.
    /// </summary>
    public void Initialize(List<PrioritizedRoute> routes, StateData stateData = null, Action<Employee, StateData, Status> callback = null)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.Initialize: {Employee.fullName} Initialize", DebugLogger.Category.EmployeeCore);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvMoveItemBeh.Initialize: Skipping client-side for NPC={Employee?.fullName}", DebugLogger.Category.EmployeeCore);
        return;
      }
      _success = true;
      _anySuccess = false;
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
                  ? TransitDistanceCache.GetDistance(r.PickUp ?? r.DropOff, r.DropOff, Employee)
                  : float.MaxValue;
        }).ToList();
        foreach (var route in sortedRoutes)
        {
          _routeQueue.Enqueue(route);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvMoveItemBeh.Initialize: Enqueued route: item={route.Item?.ID}, qty={route.Quantity}, source={route.PickUp?.GUID}, dest={route.DropOff?.GUID} for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvMoveItemBeh.Initialize: Queued {routes.Count} routes for NPC={Employee?.fullName}", DebugLogger.Category.EmployeeCore);
      Enable_Networked(null);
      AdvBegin();
    }

    /// <summary>
    /// Processes the next group of routes from the same pickup source, sorted by destination proximity.
    /// Picks up all items once, then delivers to destinations in order.
    /// Skips to the next group on failure, tracking success.
    /// </summary>
    private void ProcessNextRoute()
    {
      if (!_resumed)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.ProcessNextRoute: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
        if (_routeQueue.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"AdvMoveItemBeh.ProcessNextRoute: All routes processed for NPC={Employee.fullName}, anySuccess={_anySuccess}",
              DebugLogger.Category.EmployeeCore);
          _success = _anySuccess; // Set success based on any route succeeding
          Disable_Networked(null);
          return;
        }
        var currentSource = _routeQueue.Peek().PickUp?.GUID ?? Guid.Empty;
        _sameSourceRoutes = new List<PrioritizedRoute>();
        while (_routeQueue.Count > 0 && (_routeQueue.Peek().PickUp?.GUID ?? Guid.Empty) == currentSource)
          _sameSourceRoutes.Add(_routeQueue.Dequeue());
        if (!_sameSourceRoutes.Any() || _sameSourceRoutes.Any(r => r.Item == null || r.DropOff == null))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"AdvMoveItemBeh.ProcessNextRoute: Invalid routes in group: count={_sameSourceRoutes.Count} for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          ProcessNextRoute(); // Skip to next group
          return;
        }
        _currentRoute = _sameSourceRoutes[0];
        var firstRoute = _currentRoute.Value;
        itemToRetrieveTemplate = firstRoute.Item;
        maxMoveAmount = _sameSourceRoutes.Sum(r => r.Quantity);
        skipPickup = firstRoute.PickUp == null;
        assignedRoute = firstRoute.TransitRoute;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"AdvMoveItemBeh.ProcessNextRoute: Processing {_sameSourceRoutes.Count} routes from source={currentSource}, total qty={maxMoveAmount} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        if (skipPickup)
        {
          lock (InventoryRoutes)
          {
            if (!InventoryRoutes.ContainsKey(Employee.GUID))
              InventoryRoutes[Employee.GUID] = new();
            InventoryRoutes[Employee.GUID].Add(assignedRoute);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"AdvMoveItemBeh.ProcessNextRoute: Added inventory route for NPC={Employee?.fullName}",
                DebugLogger.Category.EmployeeCore);
          }
        }
        if (!IsTransitRouteValid(assignedRoute, itemToRetrieveTemplate, out var invalidReason))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.ProcessNextRoute: Skipping invalid transit route: {invalidReason} for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          ProcessNextRoute(); // Skip to next group
          return;
        }
      }
      if (skipPickup)
      {
        grabbedAmount = _sameSourceRoutes.Sum(r => r.InventorySlot.Quantity);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.ProcessNextRoute: SkipPickup enabled, grabbedAmount={grabbedAmount} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        DeliverToSortedDestinations(_sameSourceRoutes, skipPickup: true);
      }
      else
      {
        WalkToPickUpGroup(_sameSourceRoutes);
      }
    }

    public override void Begin()
    {
      return;
    }

    private void AdvBegin()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.Begin: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      base.Begin();
      StartTransit();
    }

    public void StartTransit()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.StartTransit: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      if (!InstanceFinder.IsServer)
        return;
      currentState = EState.Idle;
      ProcessNextRoute();
    }

    public void StopCurrentActivity()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.StopCurrentActivity: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      if (_currentCoroutine != null)
      {
        MelonCoroutines.Stop(_currentCoroutine);
        _currentCoroutine = null;
      }
      currentState = EState.Idle;
    }

    public override void Pause()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.Pause: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      if (_isPaused) return;

      _isPaused = true;
      base.Pause();
      SavePauseState();
      StopCurrentActivity();
    }

    private void SavePauseState()
    {
      _pauseState.CurrentState = currentState;
      _pauseState.CurrentRouteIndex = 0; // Track index in DeliverRoutine
      _pauseState.CurrentRouteGroup = _routeQueue.ToList(); // Snapshot of remaining routes
      _pauseState.ProgressTime = _currentCoroutine != null ? Time.time - _coroutineStartTime : 0;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.SavePauseState: Saved state for {Employee.fullName},", DebugLogger.Category.EmployeeCore);
    }

    public override void Resume()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.Resume: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      if (!_isPaused) return;

      _isPaused = false;
      base.Resume();
      RestorePauseState();
    }

    private void RestorePauseState()
    {
      currentState = _pauseState.CurrentState;

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.RestorePauseState: Restoring {Employee.fullName}, coroutine={_pauseState.CurrentCoroutineType}", DebugLogger.Category.EmployeeCore);

      switch (currentState)
      {
        case EState.Grabbing:
          WalkToPickUpGroup(_pauseState.CurrentRouteGroup);
          break;
        case EState.Placing:
          DeliverToSortedDestinations(_pauseState.CurrentRouteGroup, skipPickup);
          break;
        default:
          _resumed = true;
          ProcessNextRoute();
          break;
      }
    }

    public override void Disable()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.Disable: {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      base.Disable();
    }

    public override void ActiveMinPass()
    {
      return;
    }

    private float _coroutineStartTime;

    private void StartCoroutineSafely(IEnumerator routine)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.StartCoroutineSafely: Starting coroutine={routine.GetType().Name} for NPC={Employee?.fullName}", DebugLogger.Category.EmployeeCore);
      if (_currentCoroutine != null)
        MelonCoroutines.Stop(_currentCoroutine);
      _coroutineStartTime = Time.time;
      _currentCoroutine = MelonCoroutines.Start(routine);
    }

    /// <summary>
    /// Ends the behavior, invoking callback with Success if any route succeeded, Failure otherwise.
    /// Leaves inventory items for another task to handle.
    /// </summary>
    public override void End()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.End: {Employee.fullName}, anySuccess={_anySuccess}",
          DebugLogger.Category.EmployeeCore);
      base.End();
      if (_currentCoroutine != null)
      {
        MelonCoroutines.Stop(_currentCoroutine);
        _currentCoroutine = null;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.End: Stopped coroutine for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
      }
      _currentRoute = null;
      _routeQueue.Clear();
      if (InventoryRoutes.ContainsKey(Employee.GUID))
        lock (InventoryRoutes)
        {
          InventoryRoutes.Remove(Employee.GUID);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvMoveItemBeh.End: Cleared inventory routes for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
        }
      if (_callback != null)
      {
        _callback.Invoke(Employee, _stateData, _anySuccess ? Status.Success : Status.Failure);
        _callback = null;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.End: Invoked callback with status={(_anySuccess ? "Success" : "Failure")} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
      }
      currentState = EState.Idle;
      // Note: Inventory items are left as-is for another task to handle
    }

    private bool IsTransitRouteValid(TransitRoute route, ItemInstance templateItem, out string invalidReason)
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
        ItemInstance itemInstance = route.Source.GetFirstSlotContainingTemplateItem(templateItem, ITransitEntity.ESlotType.Output)?.ItemInstance;
        if (itemInstance == null || itemInstance.Quantity <= 0)
        {
          invalidReason = "Item is null or quantity is 0!";
          return false;
        }
      }
      if (!IsDestinationValid(route, templateItem))
      {
        invalidReason = "Can't access pickup, pickup, or pickup is full!";
        return false;
      }
      return true;
    }

    private bool IsDestinationValid(TransitRoute route, ItemInstance item)
    {
      if (route.Destination.GetInputCapacityForItem(item, Employee) == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvMoveItemBeh.IsDestinationValid: Destination has no capacity for item! for NPC={Employee?.fullName}", DebugLogger.Category.EmployeeCore);
        return false;
      }
      if (!CanGetToDropoff(route))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvMoveItemBeh.IsDestinationValid: Cannot get to dropoff! for NPC={Employee?.fullName}", DebugLogger.Category.EmployeeCore);
        return false;
      }
      if (!skipPickup && !CanGetToPickup(route))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvMoveItemBeh.IsDestinationValid: Cannot get to pickup! for NPC={Employee?.fullName}", DebugLogger.Category.EmployeeCore);
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

    /// <summary>
    /// Walks to the pickup location for a group of routes and grabs all items.
    /// Skips to next group on failure.
    /// </summary>
    /// <param name="routes">Routes with the same pickup source.</param>
    private void WalkToPickUpGroup(List<PrioritizedRoute> routes)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.WalkToPickUpGroup: {Employee.fullName}, routes={routes.Count}",
          DebugLogger.Category.EmployeeCore);
      var pickup = routes[0].PickUp;
      var accessPoint = NavMeshUtility.GetAccessPoint(pickup, Employee);
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.WalkToPickUpGroup: No access point for pickup={pickup?.GUID} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        ProcessNextRoute(); // Skip to next group
        return;
      }
      StartCoroutineSafely(WalkToPickupRoutine(accessPoint.position, routes));

      IEnumerator WalkToPickupRoutine(Vector3 pickupPos, List<PrioritizedRoute> routes)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.WalkToPickupRoutine: Walking to {pickupPos} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        SetDestination(pickupPos);
        float startTime = Time.time;
        float timeoutSeconds = 10.0f;
        bool startedMoving = false;
        while (Time.time - startTime < timeoutSeconds)
        {
          if (Employee.Movement.IsMoving)
          {
            startedMoving = true;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"AdvMoveItemBeh.WalkToPickupRoutine: Started moving for NPC={Employee?.fullName}",
                DebugLogger.Category.EmployeeCore);
            break;
          }
          yield return null;
        }
        if (!startedMoving)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.WalkToPickupRoutine: Timeout: Failed to start moving for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          ProcessNextRoute(); // Skip to next group
          yield break;
        }
        float moveTimeout = 30.0f;
        startTime = Time.time;
        while (Employee.Movement.IsMoving && Time.time - startTime < moveTimeout)
          yield return null;
        if (Employee.Movement.IsMoving)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.WalkToPickupRoutine: Timeout: Movement did not complete for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          ProcessNextRoute(); // Skip to next group
          yield break;
        }
        currentState = EState.Grabbing;
        GrabItemGroup(routes);
        _currentCoroutine = null;
      }
    }

    /// <summary>
    /// Grabs items from all pickup slots for a group of routes.
    /// Skips to next group if grabbing fails.
    /// </summary>
    /// <param name="routes">Routes with the same pickup source.</param>
    private void GrabItemGroup(List<PrioritizedRoute> routes)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.GrabItemGroup: {Employee.fullName}, routes={routes.Count} for NPC={Employee?.fullName}",
          DebugLogger.Category.EmployeeCore);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.GrabItemGroup: Skipping client-side execution for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        ProcessNextRoute(); // Skip to next group
        return;
      }
      var sourceAccessPoint = NavMeshUtility.GetAccessPoint(routes[0].PickUp, Employee);
      if (sourceAccessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.GrabItemGroup: No pickup access point for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        ProcessNextRoute(); // Skip to next group
        return;
      }
      StartCoroutineSafely(GrabRoutine());

      IEnumerator GrabRoutine()
      {
        Employee.Movement.FaceDirection(sourceAccessPoint.forward);
        Employee.SetAnimationTrigger_Networked(null, "GrabItem");
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.GrabRoutine: Triggered GrabItem animation for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        yield return new WaitForSeconds(0.5f);
        bool anyGrabSuccess = false;
        grabbedAmount = 0;
        foreach (var route in routes)
        {
          if (route.PickupSlots?.Count > 0)
          {
            var totalAvailable = route.PickupSlots.Sum(s => s.Quantity);
            int quantityToTake = Math.Min(route.Quantity, totalAvailable);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"AdvMoveItemBeh.GrabRoutine: quantityToTake={quantityToTake}, totalAvailable={totalAvailable}, item={route.Item.ID} for NPC={Employee?.fullName}",
                DebugLogger.Category.EmployeeCore);
            if (quantityToTake <= 0)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"AdvMoveItemBeh.GrabRoutine: No valid quantity to take for item={route.Item.ID} for NPC={Employee?.fullName}",
                  DebugLogger.Category.EmployeeCore);
              continue;
            }
            foreach (var slot in route.PickupSlots)
            {
              if (quantityToTake <= 0)
                break;
              int takeAmount = Math.Min(slot.Quantity, quantityToTake);
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"AdvMoveItemBeh.GrabRoutine: Taking {takeAmount} from slot for NPC={Employee?.fullName}",
                  DebugLogger.Category.EmployeeCore);
              if (route.InventorySlot.AdvInsertItem(slot.ItemInstance, takeAmount))
              {
                if (slot.AdvRemoveItem(takeAmount, out var takenItem))
                {
                  grabbedAmount += takeAmount;
                  quantityToTake -= takeAmount;
                  anyGrabSuccess = true;
                  DebugLogger.Log(DebugLogger.LogLevel.Info,
                      $"AdvMoveItemBeh.GrabRoutine: Took {takeAmount} {takenItem.ID}, grabbedQty={grabbedAmount} for NPC={Employee?.fullName}",
                      DebugLogger.Category.EmployeeCore);
                }
                else
                {
                  route.InventorySlot?.AdvRemoveItem(takeAmount, out _);
                  DebugLogger.Log(DebugLogger.LogLevel.Warning,
                      $"AdvMoveItemBeh.GrabRoutine: Failed to take {takeAmount} from slot for NPC={Employee?.fullName}",
                      DebugLogger.Category.EmployeeCore);
                }
              }
              else
              {
                DebugLogger.Log(DebugLogger.LogLevel.Warning,
                    $"AdvMoveItemBeh.GrabRoutine: Failed to insert {takeAmount} into inventory for NPC={Employee?.fullName}",
                    DebugLogger.Category.EmployeeCore);
              }
            }
          }
        }
        yield return new WaitForSeconds(0.2f);
        if (anyGrabSuccess)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"AdvMoveItemBeh.GrabRoutine: Grabbed {grabbedAmount} items, proceeding to deliver for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          DeliverToSortedDestinations(routes, skipPickup: false);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.GrabRoutine: Failed to grab any items, skipping to next group for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          ProcessNextRoute(); // Skip to next group
        }
        _currentCoroutine = null;
      }
    }

    /// <summary>
    /// Delivers items to sorted destinations sequentially, using skipPickup for efficiency.
    /// Skips to next group if delivery fails.
    /// </summary>
    /// <param name="routes">Routes to process, sorted by destination.</param>
    /// <param name="skipPickup">Whether pickup was already handled.</param>
    private void DeliverToSortedDestinations(List<PrioritizedRoute> routes, bool skipPickup)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.DeliverToSortedDestinations {Employee.fullName}, routes={routes.Count}, skipPickup={skipPickup} for NPC={Employee?.fullName}",
          DebugLogger.Category.EmployeeCore);
      StartCoroutineSafely(DeliverRoutine(routes, 0));
    }

    IEnumerator DeliverRoutine(List<PrioritizedRoute> routes, int routeIndex)
    {
      _pauseState.CurrentRouteIndex = routeIndex;
      if (routeIndex >= routes.Count)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"AdvMoveItemBeh.DeliverRoutine: All dropoffs completed for group, moving to next group for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        ProcessNextRoute(); // Move to next group
        _currentCoroutine = null;
        yield break;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.DeliverRoutine: Processing route {routeIndex + 1}/{routes.Count} for NPC={Employee?.fullName}",
          DebugLogger.Category.EmployeeCore);
      var route = routes[routeIndex];
      var dropoff = route.DropOff;
      var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.DeliverRoutine: No access point for dropoff={dropoff.GUID}, skipping route for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        StartCoroutineSafely(DeliverRoutine(routes, routeIndex + 1)); // Try next route
        _currentCoroutine = null;
        yield break;
      }
      if (!IsDestinationValid(route.TransitRoute, route.Item))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.DeliverRoutine: Invalid destination for item={route.Item.ID}, skipping route for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        StartCoroutineSafely(DeliverRoutine(routes, routeIndex + 1)); // Try next route
        _currentCoroutine = null;
        yield break;
      }
      if (NavMeshUtility.IsAtTransitEntity(dropoff, Employee))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.DeliverRoutine: Already at dropoff {dropoff.GUID} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        currentState = EState.Placing;
        PlaceItemToDropoff(route, routes, routeIndex);
        _currentCoroutine = null;
        yield break;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.WalkToDropoffRoutine: Walking to dropoff at {accessPoint.position} for item={route.Item.ID} for NPC={Employee?.fullName}",
          DebugLogger.Category.EmployeeCore);
      SetDestination(accessPoint.position);
      float startTime = Time.time;
      float timeoutSeconds = 10.0f;
      bool startedMoving = false;
      while (Time.time - startTime < timeoutSeconds)
      {
        if (Employee.Movement.IsMoving)
        {
          startedMoving = true;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvMoveItemBeh.WalkToDropoffRoutine: Started moving for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          break;
        }
        yield return null;
      }
      if (!startedMoving)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"AdvMoveItemBeh.WalkToDropoffRoutine: Timeout: Failed to start moving, skipping route for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        StartCoroutineSafely(DeliverRoutine(routes, routeIndex + 1)); // Try next route
        _currentCoroutine = null;
        yield break;
      }
      yield return new WaitUntil(() => !Employee.Movement.IsMoving);
      currentState = EState.Placing;
      PlaceItemToDropoff(route, routes, routeIndex);
      _currentCoroutine = null;
    }

    /// <summary>
    /// Places items at the dropoff location for a single route, then proceeds to the next.
    /// Marks success if placement occurs, skips to next route on failure.
    /// </summary>
    /// <param name="route">Current route to process.</param>
    /// <param name="routes">All routes in the group for continuation.</param>
    /// <param name="routeIndex">Index of the current route.</param>
    private void PlaceItemToDropoff(PrioritizedRoute route, List<PrioritizedRoute> routes, int routeIndex)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"AdvMoveItemBeh.PlaceItemToDropoff: {Employee.fullName}, item={route.Item.ID}, dest={route.DropOff.GUID} for NPC={Employee?.fullName}",
          DebugLogger.Category.EmployeeCore);
      StartCoroutineSafely(PlaceRoutine());

      IEnumerator PlaceRoutine()
      {
        var dropoff = route.DropOff;
        var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
        Employee.Movement.FaceDirection(accessPoint.forward);
        Employee.SetAnimationTrigger_Networked(null, "GrabItem");
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"AdvMoveItemBeh.PlaceRoutine: Triggered GrabItem animation for item={route.Item.ID} for NPC={Employee?.fullName}",
            DebugLogger.Category.EmployeeCore);
        yield return new WaitForSeconds(0.5f);
        ItemInstance itemToPlace = route.InventorySlot.ItemInstance;
        if (itemToPlace == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.PlaceRoutine: No item to place for item={route.Item.ID}, skipping route for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          StartCoroutineSafely(DeliverRoutine(routes, routeIndex + 1)); // Try next route
          yield break;
        }
        bool placedSuccessfully = false;
        int placedAmount = 0;
        foreach (var slot in route.DropoffSlots)
        {
          int capacity = slot.GetCapacityForItem(itemToPlace);
          int amount = Math.Min(route.InventorySlot.Quantity, capacity);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"AdvMoveItemBeh.PlaceRoutine: Slot capacity={capacity}, placing {amount} for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          if (amount <= 0)
            continue;
          if (slot.AdvInsertItem(itemToPlace, amount))
          {
            if (route.InventorySlot.AdvRemoveItem(amount, out _))
            {
              placedSuccessfully = true;
              placedAmount += amount;
              grabbedAmount -= amount;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"AdvMoveItemBeh.PlaceRoutine: Placed {amount} at {dropoff.GUID} for NPC={Employee?.fullName}",
                  DebugLogger.Category.EmployeeCore);
            }
            else
            {
              slot.AdvRemoveItem(amount, out _);
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"AdvMoveItemBeh.PlaceRoutine: Failed to remove {amount} from inventory slot for NPC={Employee?.fullName}",
                  DebugLogger.Category.EmployeeCore);
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"AdvMoveItemBeh.PlaceRoutine: Failed to insert {amount} into dropoff slot for NPC={Employee?.fullName}",
                DebugLogger.Category.EmployeeCore);
          }
        }
        yield return new WaitForSeconds(0.2f);
        if (placedSuccessfully)
        {
          _anySuccess = true; // Mark that at least one route succeeded
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"AdvMoveItemBeh.PlaceRoutine: Placed {placedAmount}, remaining grabbedAmount={grabbedAmount}, anySuccess={_anySuccess} for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          StartCoroutineSafely(DeliverRoutine(routes, routeIndex + 1)); // Move to next route
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"AdvMoveItemBeh.PlaceRoutine: Failed to place any items, skipping route for NPC={Employee?.fullName}",
              DebugLogger.Category.EmployeeCore);
          StartCoroutineSafely(DeliverRoutine(routes, routeIndex + 1)); // Try next route
        }
        _currentCoroutine = null;
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
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvMoveItemBeh.TransitRoutePatch: Allowing inventory route with null Source", DebugLogger.Category.EmployeeCore);
            __result = true;
            return false;
          }
          return true;
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvMoveItemBeh.TransitRoutePatch: Failed, error={e}", DebugLogger.Category.EmployeeCore);
          __result = false;
          return false;
        }
      }
    }
  }
}