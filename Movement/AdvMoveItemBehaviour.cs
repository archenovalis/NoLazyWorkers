using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Movement.Utilities;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.ManagedDictionaries;
using static NoLazyWorkers.Storage.CacheService;
using System.Collections;
using UnityEngine;
using ScheduleOne.DevUtilities;
using MelonLoader;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.TimeManagerExtensions;
using System.Diagnostics;
using NoLazyWorkers.Storage;
using System.Threading.Tasks;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.Performance;
using static NoLazyWorkers.TaskExtensions;
using static NoLazyWorkers.Debug;

namespace NoLazyWorkers.Movement
{
  public class AdvMoveItemBehaviour : Behaviour
  {
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
      public List<PrioritizedRoute> CurrentRouteGroup { get; set; }
      public float ProgressTime { get; set; }
    }

    public Employee Employee { get; private set; }
    public TransitRoute assignedRoute;
    public ItemInstance itemToRetrieveTemplate;
    public int grabbedAmount;
    public int maxMoveAmount = -1;
    public EState currentState;
    public bool skipPickup;

    private CacheService _cacheManager;
    private readonly Queue<PrioritizedRoute> _routeQueue = new();
    private PrioritizedRoute? _currentRoute;
    private Action<Employee, EmployeeData, Status> _callback;
    private Stopwatch _travelStopwatch;
    private EmployeeData _stateData;
    private Coroutine _currentCoroutine;
    private static readonly Dictionary<Guid, List<TransitRoute>> InventoryRoutes = new();
    private PauseState _pauseState = new();
    private bool _isPaused;
    private bool _resumed;
    private bool _anySuccess = false;
    private List<PrioritizedRoute> _sameSourceRoutes = new();
    private Task<bool> _currentTask;
    private int _processedCount;

    /// <summary>
    /// Initializes the behavior with routes, grouping by pickup source and sorting by destination proximity.
    /// Routes are processed in groups from the same pickup source, with items picked up once and delivered
    /// to destinations sorted by distance using TransitDistanceCache.
    /// </summary>
    public void Initialize(List<PrioritizedRoute> routes, EmployeeData stateData = null, Action<Employee, EmployeeData, Status> callback = null)
    {
      Log(Level.Verbose, $"AdvMoveItemBeh.Initialize: {Employee.fullName} Initialize", Category.Movement);
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Warning, $"AdvMoveItemBeh.Initialize: Skipping client-side for NPC={Employee?.fullName}", Category.Movement);
        return;
      }
      _anySuccess = false;
      _cacheManager = GetOrCreateCacheManager(Employee.AssignedProperty);
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
                  ? _cacheManager.GetTravelTime(r.PickUp ?? r.DropOff, r.DropOff)
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
    /// Stops the current async task or coroutine.
    /// </summary>
    public void StopCurrentActivity()
    {
      Log(Level.Verbose, $"StopCurrentActivity: {Employee.fullName}", Category.Movement);
      if (_currentCoroutine != null)
      {
        CoroutineRunner.Instance.StopCoroutine(_currentCoroutine);
        _currentCoroutine = null;
      }
      _currentTask = null;
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
        _callback.Invoke(Employee, _stateData, _anySuccess ? Status.Success : Status.Failure);
        _callback = null;
      }
      currentState = EState.Idle;
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

    /// <summary>
    /// Asynchronously moves to the pickup location for a group of routes.
    /// </summary>
    private async Task<bool> MoveToPickupGroupAsync(List<PrioritizedRoute> routes)
    {
      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Warning, $"MoveToPickupGroupAsync: Skipping client-side for {Employee?.fullName}", Category.Movement);
        ProcessNextRoute();
        return false;
      }
      var pickup = routes?[0].PickUp;
      if (pickup == null)
      {
        Log(Level.Warning, $"MoveToPickupGroupAsync: Null pickup for {Employee?.fullName}", Category.Movement);
        ProcessNextRoute();
        return false;
      }
      Log(Level.Verbose, $"MoveToPickupGroupAsync: {Employee?.fullName}, routes={routes?.Count ?? 0}", Category.Movement);
      try
      {
        currentState = EState.MovingToPickup;
        _currentTask = MoveToAsync(Employee, pickup);
        bool success = await Performance.Metrics.TrackExecutionAsync(nameof(MoveToPickupGroupAsync), () => _currentTask, routes.Count);
        if (!success)
        {
          Log(Level.Warning, $"MoveToPickupGroupAsync: Movement failed for {Employee?.fullName} to {pickup.GUID}", Category.Movement);
          ProcessNextRoute();
          return false;
        }
        Log(Level.Info, $"MoveToPickupGroupAsync: Reached pickup {pickup.GUID} for {Employee?.fullName}", Category.Movement);
        currentState = EState.Grabbing;
        _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(Performance.Metrics.TrackExecutionCoroutine(nameof(GrabItemGroupCoroutine), GrabItemGroupCoroutine(routes), routes.Sum(r => r.PickupSlots?.Count ?? 0)));
        return true;
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"MoveToPickupGroupAsync: Exception for {Employee?.fullName} - {ex}", Category.Movement);
        ProcessNextRoute();
        return false;
      }
      finally
      {
        _currentTask = null;
        if (currentState != EState.Grabbing) currentState = EState.Idle;
      }
    }

    /// <summary>
    /// Asynchronously grabs items from all pickup slots for a group of routes and starts travel time tracking.
    /// </summary>
    private async Task GrabItemGroupAsync(List<PrioritizedRoute> routes)
    {
      Log(Level.Verbose,
          $"AdvMoveItemBeh.GrabItemGroupAsync: {Employee.fullName}, routes={routes.Count}",
          Category.Movement);

      if (!FishNetExtensions.IsServer)
      {
        Log(Level.Warning,
            $"AdvMoveItemBeh.GrabItemGroupAsync: Skipping client-side for NPC={Employee?.fullName}",
            Category.Movement);
        ProcessNextRoute();
        return;
      }

      var sourceAccessPoint = NavMeshUtility.GetAccessPoint(routes[0].PickUp, Employee);
      if (sourceAccessPoint == null)
      {
        Log(Level.Warning,
            $"AdvMoveItemBeh.GrabItemGroupAsync: No pickup access point for NPC={Employee?.fullName}",
            Category.Movement);
        ProcessNextRoute();
        return;
      }

      try
      {
        Employee.Movement.FaceDirection(sourceAccessPoint.forward);
        Employee.SetAnimationTrigger_Networked(null, "GrabItem");
        Log(Level.Verbose,
            $"AdvMoveItemBeh.GrabItemGroupAsync: Triggered GrabItem animation for NPC={Employee?.fullName}",
            Category.Movement);

        await Task.Delay(TimeSpan.FromSeconds(0.5f));

        bool anyGrabSuccess = false;
        grabbedAmount = 0;
        foreach (var route in routes)
        {
          if (route.PickupSlots?.Count > 0)
          {
            var totalAvailable = route.PickupSlots.Sum(s => s.Quantity);
            int quantityToTake = Math.Min(route.Quantity, totalAvailable);
            Log(Level.Info,
                $"AdvMoveItemBeh.GrabItemGroupAsync: quantityToTake={quantityToTake}, totalAvailable={totalAvailable}, item={route.Item.ID} for NPC={Employee?.fullName}",
                Category.Movement);

            if (quantityToTake <= 0)
            {
              Log(Level.Warning,
                  $"AdvMoveItemBeh.GrabItemGroupAsync: No valid quantity to take for item={route.Item.ID} for NPC={Employee?.fullName}",
                  Category.Movement);
              continue;
            }

            foreach (var slot in route.PickupSlots)
            {
              if (quantityToTake <= 0)
                break;

              int takeAmount = Math.Min(slot.Quantity, quantityToTake);
              Log(Level.Verbose,
                  $"AdvMoveItemBeh.GrabItemGroupAsync: Taking {takeAmount} from slot for NPC={Employee?.fullName}",
                  Category.Movement);

              if (await route.InventorySlot.AdvInsertItemAsync(slot.ItemInstance, takeAmount, route.PickUp.GUID, Employee))
              {
                (var success, var takenItem) = await slot.AdvRemoveItemAsync(takeAmount, route.PickUp.GUID, Employee);
                if (success)
                {
                  grabbedAmount += takeAmount;
                  quantityToTake -= takeAmount;
                  anyGrabSuccess = true;
                  Log(Level.Info,
                      $"AdvMoveItemBeh.GrabItemGroupAsync: Took {takeAmount} {takenItem.ID}, grabbedQty={grabbedAmount} for NPC={Employee?.fullName}",
                      Category.Movement);
                }
                else
                {
                  await route.InventorySlot?.AdvRemoveItemAsync(takeAmount, route.PickUp.GUID, Employee);
                  Log(Level.Warning,
                      $"AdvMoveItemBeh.GrabItemGroupAsync: Failed to take {takeAmount} from slot for NPC={Employee?.fullName}",
                      Category.Movement);
                }
              }
              else
              {
                Log(Level.Warning,
                    $"AdvMoveItemBeh.GrabItemGroupAsync: Failed to insert {takeAmount} into inventory for NPC={Employee?.fullName}",
                    Category.Movement);
              }
            }
          }
        }

        await Task.Delay(TimeSpan.FromSeconds(0.2f));

        if (anyGrabSuccess)
        {
          Log(Level.Info,
              $"AdvMoveItemBeh.GrabItemGroupAsync: Grabbed {grabbedAmount} items, starting travel timer for NPC={Employee?.fullName}",
              Category.Movement);

          // Start travel time tracking after grabbing items
          _travelStopwatch = Stopwatch.StartNew();
          await DeliverToSortedDestinationsAsync(routes, skipPickup: false);
        }
        else
        {
          Log(Level.Warning,
              $"AdvMoveItemBeh.GrabItemGroupAsync: Failed to grab any items, skipping to next group for NPC={Employee?.fullName}",
              Category.Movement);
          ProcessNextRoute();
        }
      }
      catch (Exception ex)
      {
        Log(Level.Error,
            $"AdvMoveItemBeh.GrabItemGroupAsync: Exception for {Employee?.fullName} - {ex.Message}",
            Category.Movement);
        ProcessNextRoute();
      }
      finally
      {
        _currentTask = null;
        currentState = EState.Idle;
        _travelStopwatch = null; // Clear stopwatch if grabbing fails
      }
    }

    private IEnumerator GrabItemGroupCoroutine(List<PrioritizedRoute> routes)
    {
      Log(Level.Verbose, $"GrabItemGroupCoroutine: {Employee.fullName}, routes={routes.Count}", Category.Movement);
      if (!FishNetExtensions.IsServer) { ProcessNextRoute(); yield break; }
      var sourceAccessPoint = NavMeshUtility.GetAccessPoint(routes[0].PickUp, Employee);
      if (sourceAccessPoint == null) { ProcessNextRoute(); yield break; }
      bool anyGrabSuccess = false;
      grabbedAmount = 0;
      Employee.Movement.FaceDirection(sourceAccessPoint.forward);
      Employee.SetAnimationTrigger_Networked(null, "GrabItem");
      float animationTime = 0.5f;
      while (animationTime > 0)
      {
        animationTime -= Time.deltaTime;
        yield return null;
      }
      int batchSize = GetDynamicBatchSize(routes.Sum(r => r.PickupSlots?.Count ?? 0), 0.1f, nameof(GrabItemGroupCoroutine));
      _processedCount = 0;
      var stopwatch = Stopwatch.StartNew();
      foreach (var route in routes)
      {
        if (route.PickupSlots?.Count > 0)
        {
          var totalAvailable = route.PickupSlots.Sum(s => s.Quantity);
          int quantityToTake = Math.Min(route.Quantity, totalAvailable);
          if (quantityToTake <= 0) continue;
          foreach (var slot in route.PickupSlots)
          {
            if (quantityToTake <= 0) break;
            int takeAmount = Math.Min(slot.Quantity, quantityToTake);

            // Wait for AdvInsertItemAsync and get the bool result
            var insertTask = route.InventorySlot.AdvInsertItemAsync(slot.ItemInstance, takeAmount, Employee.GUID, Employee);
            yield return new TaskYieldInstruction<bool>(insertTask);
            bool insertSuccess = insertTask.Result;

            if (insertSuccess)
            {
              // Wait for AdvRemoveItemAsync and get the (bool, ItemInstance) result
              var removeTask = slot.AdvRemoveItemAsync(takeAmount, route.PickUp.GUID, Employee);
              yield return new TaskYieldInstruction<(bool, ItemInstance)>(removeTask);
              var (success, takenItem) = removeTask.Result;

              if (success)
              {
                grabbedAmount += takeAmount;
                quantityToTake -= takeAmount;
                anyGrabSuccess = true;
                Log(Level.Info, $"GrabItemGroupCoroutine: Took {takeAmount} {takenItem.ID}", Category.Movement);
              }
              else
              {
                removeTask = route.InventorySlot.AdvRemoveItemAsync(takeAmount, Employee.GUID, Employee);
                yield return new TaskYieldInstruction<(bool, ItemInstance)>(removeTask);
                Log(Level.Warning,
                    $"PlaceItemToDropoffCoroutine: Failed to grab {takeAmount} from {route.PickUp.GUID}",
                    Category.Movement);
              }
            }
            _processedCount++;
            if (_processedCount % batchSize == 0)
            {
              if (_processedCount > 0)
              {
                double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * _processedCount);
                DynamicProfiler.AddSample(nameof(GrabItemGroupCoroutine), avgItemTimeMs);
                stopwatch.Restart();
              }
              yield return null;
              _processedCount = 0;
            }
          }
        }
      }
      if (_processedCount > 0)
      {
        double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * _processedCount);
        DynamicProfiler.AddSample(nameof(GrabItemGroupCoroutine), avgItemTimeMs);
      }
      float postGrabDelay = 0.2f;
      while (postGrabDelay > 0)
      {
        postGrabDelay -= Time.deltaTime;
        yield return null;
      }
      if (anyGrabSuccess)
      {
        _travelStopwatch = Stopwatch.StartNew();
        _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(DeliverToSortedDestinationsCoroutine(routes, skipPickup: false));
      }
      else
      {
        ProcessNextRoute();
      }
      _currentCoroutine = null;
      currentState = EState.Idle;
      if (!anyGrabSuccess) _travelStopwatch = null;
    }

    /// <summary>
    /// Asynchronously delivers items to sorted destinations sequentially, tracking travel time from pickup.
    /// </summary>
    private async Task<bool> DeliverToSortedDestinationsAsync(List<PrioritizedRoute> routes, bool skipPickup)
    {
      Log(Level.Verbose,
          $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: {Employee?.fullName}, routes={routes?.Count ?? 0}, skipPickup={skipPickup}",
          Category.Movement);

      try
      {
        for (int routeIndex = _pauseState.CurrentRouteIndex; routeIndex < routes.Count; routeIndex++)
        {
          _pauseState.CurrentRouteIndex = routeIndex;
          currentState = EState.MovingToDropoff;

          Log(Level.Verbose,
              $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Processing route {routeIndex + 1}/{routes.Count} for {Employee?.fullName}",
              Category.Movement);

          var route = routes[routeIndex];
          var dropoff = route.DropOff;

          if (!IsDestinationValid(route.TransitRoute, route.Item))
          {
            Log(Level.Warning,
                $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Invalid destination for item={route.Item?.ID ?? "null"}, skipping route for {Employee?.fullName}",
                Category.Movement);
            continue;
          }

          if (NavMeshUtility.IsAtTransitEntity(dropoff, Employee))
          {
            Log(Level.Verbose,
                $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Already at dropoff {dropoff.GUID} for {Employee?.fullName}",
                Category.Movement);
            currentState = EState.Placing;
            await PlaceItemToDropoffAsync(route, routes, routeIndex);
            continue;
          }

          _currentTask = MoveToAsync(Employee, dropoff);
          bool success = await _currentTask;

          if (success)
          {
            if (!skipPickup && _travelStopwatch != null)
            {
              _travelStopwatch.Stop();
              float travelTime = (float)_travelStopwatch.Elapsed.TotalSeconds;
              _cacheManager.UpdateTravelTimeCache(routes[0].PickUp.GUID, dropoff.GUID, travelTime);
              Log(Level.Info,
                  $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Moved from pickup {routes[0].PickUp.GUID} to dropoff {dropoff.GUID} in {travelTime:F2}s for {Employee?.fullName}",
                  Category.Movement);
              _travelStopwatch = null; // Clear after use
            }
            else if (skipPickup)
            {
              Log(Level.Verbose,
                  $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Skipped travel time cache update (skipPickup=true) for dropoff {dropoff.GUID}",
                  Category.Movement);
            }
          }
          else
          {
            Log(Level.Warning,
                $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Movement failed for dropoff={dropoff.GUID}, skipping route for {Employee?.fullName}",
                Category.Movement);
            _travelStopwatch = null; // Clear on failure
            continue;
          }

          currentState = EState.Placing;
          await PlaceItemToDropoffAsync(route, routes, routeIndex);

          // Yield to next fishnet tick to spread load
          await AwaitNextFishNetTickAsync();
        }

        Log(Level.Info,
            $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: All dropoffs completed for group, moving to next group for {Employee?.fullName}",
            Category.Movement);
        ProcessNextRoute();
        return true;
      }
      catch (Exception ex)
      {
        Log(Level.Error,
            $"AdvMoveItemBeh.DeliverToSortedDestinationsAsync: Exception for {Employee?.fullName} - {ex.Message}",
            Category.Movement);
        ProcessNextRoute();
        return false;
      }
      finally
      {
        _currentTask = null;
        currentState = EState.Idle;
        _pauseState.CurrentRouteIndex = 0;
        _travelStopwatch = null; // Ensure cleanup
      }
    }

    private IEnumerator DeliverToSortedDestinationsCoroutine(List<PrioritizedRoute> routes, bool skipPickup)
    {
      Log(Level.Verbose, $"DeliverToSortedDestinationsCoroutine: {Employee.fullName}, routes={routes.Count}", Category.Movement);
      int batchSize = GetDynamicBatchSize(routes.Count, 0.15f, nameof(DeliverToSortedDestinationsCoroutine));
      int processedCount = 0;
      var stopwatch = Stopwatch.StartNew();
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
        bool success = false;
        if (NavMeshUtility.IsAtTransitEntity(dropoff, Employee))
        {
          currentState = EState.Placing;
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(Performance.Metrics.TrackExecutionCoroutine(nameof(PlaceItemToDropoffCoroutine), PlaceItemToDropoffCoroutine(route, routes, routeIndex), route.DropoffSlots.Count));
          yield return new WaitUntil(() => _currentCoroutine == null);
          processedCount++;
        }
        else
        {
          var moveTask = MoveToAsync(Employee, dropoff);
          yield return new WaitUntil(() => moveTask.IsCompleted);
          success = moveTask.Result;
          if (success)
          {
            if (!skipPickup && _travelStopwatch != null)
            {
              _travelStopwatch.Stop();
              float travelTime = (float)_travelStopwatch.Elapsed.TotalSeconds;
              _cacheManager.UpdateTravelTimeCache(routes[0].PickUp.GUID, dropoff.GUID, travelTime);
              _travelStopwatch = null;
            }
            currentState = EState.Placing;
            _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(Performance.Metrics.TrackExecutionCoroutine(nameof(PlaceItemToDropoffCoroutine), PlaceItemToDropoffCoroutine(route, routes, routeIndex), route.DropoffSlots.Count));
            yield return new WaitUntil(() => _currentCoroutine == null);
            processedCount++;
          }
          else
          {
            _travelStopwatch = null;
            Log(Level.Warning, $"DeliverToSortedDestinationsCoroutine: Movement failed for dropoff={dropoff.GUID}", Category.Movement);
            continue;
          }
        }
        if (processedCount % batchSize == 0)
        {
          if (processedCount > 0)
          {
            double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
            DynamicProfiler.AddSample(nameof(DeliverToSortedDestinationsCoroutine), avgItemTimeMs);
            stopwatch.Restart();
          }
          yield return null;
          processedCount = 0;
        }
      }
      if (processedCount > 0)
      {
        double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
        DynamicProfiler.AddSample(nameof(DeliverToSortedDestinationsCoroutine), avgItemTimeMs);
      }
      ProcessNextRoute();
      _currentTask = null;
      currentState = EState.Idle;
      _pauseState.CurrentRouteIndex = 0;
      _travelStopwatch = null;
      _currentCoroutine = null;
    }

    private async Task<bool> ProcessRoute(int routeIndex, List<PrioritizedRoute> routes, bool skipPickup, int batchSize, Stopwatch stopwatch)
    {
      _pauseState.CurrentRouteIndex = routeIndex;
      currentState = EState.MovingToDropoff;
      var route = routes[routeIndex];
      var dropoff = route.DropOff;
      if (!IsDestinationValid(route.TransitRoute, route.Item))
      {
        Log(Level.Warning, $"ProcessRoute: Invalid destination for item={route.Item?.ID}, skipping", Category.Movement);
        return true; // Continue loop
      }
      bool success;
      if (NavMeshUtility.IsAtTransitEntity(dropoff, Employee))
      {
        currentState = EState.Placing;
        _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(Performance.Metrics.TrackExecutionCoroutine(nameof(PlaceItemToDropoffCoroutine), PlaceItemToDropoffCoroutine(route, routes, routeIndex), route.DropoffSlots.Count));
        while (_currentCoroutine != null) await Task.Delay(1); // Wait for coroutine
        _processedCount++;
      }
      else
      {
        _currentTask = MoveToAsync(Employee, dropoff);
        success = await Performance.Metrics.TrackExecutionAsync(nameof(ProcessRoute), () => _currentTask, 1);
        if (success)
        {
          if (!skipPickup && _travelStopwatch != null)
          {
            _travelStopwatch.Stop();
            float travelTime = (float)_travelStopwatch.Elapsed.TotalSeconds;
            _cacheManager.UpdateTravelTimeCache(routes[0].PickUp.GUID, dropoff.GUID, travelTime);
            _travelStopwatch = null;
          }
          currentState = EState.Placing;
          _currentCoroutine = CoroutineRunner.Instance.RunCoroutine(Performance.Metrics.TrackExecutionCoroutine(nameof(PlaceItemToDropoffCoroutine), PlaceItemToDropoffCoroutine(route, routes, routeIndex), route.DropoffSlots.Count));
          while (_currentCoroutine != null) await Task.Delay(1); // Wait for coroutine
          _processedCount++;
        }
        else
        {
          _travelStopwatch = null;
          Log(Level.Warning, $"ProcessRoute: Movement failed for dropoff={dropoff.GUID}", Category.Movement);
          return true; // Continue loop
        }
      }
      if (_processedCount % batchSize == 0)
      {
        if (_processedCount > 0)
        {
          double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * _processedCount);
          DynamicProfiler.AddSample(nameof(DeliverToSortedDestinationsCoroutine), avgItemTimeMs);
          stopwatch.Restart();
        }
        await Task.Delay(1); // Simulate yield return null
        _processedCount = 0;
      }
      return false; // Proceed to next route
    }

    /// <summary>
    /// Asynchronously places items at the dropoff location for a single route.
    /// </summary>
    /// <param name="route">Current route to process.</param>
    /// <param name="routes">All routes in the group for continuation.</param>
    /// <param name="routeIndex">Index of the current route.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    private async Task PlaceItemToDropoffAsync(PrioritizedRoute route, List<PrioritizedRoute> routes, int routeIndex)
    {
      Log(Level.Verbose,
          $"AdvMoveItemBeh.PlaceItemToDropoffAsync: {Employee.fullName}, item={route.Item.ID}, dest={route.DropOff.GUID}",
          Category.Movement);

      try
      {
        var dropoff = route.DropOff;
        var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
        Employee.Movement.FaceDirection(accessPoint.forward);
        Employee.SetAnimationTrigger_Networked(null, "GrabItem");
        Log(Level.Verbose,
            $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Triggered GrabItem animation for item={route.Item.ID} for NPC={Employee?.fullName}",
            Category.Movement);

        await Task.Delay(TimeSpan.FromSeconds(0.5f));

        ItemInstance itemToPlace = route.InventorySlot.ItemInstance;
        if (itemToPlace == null)
        {
          Log(Level.Warning,
              $"AdvMoveItemBeh.PlaceItemToDropoffAsync: No item to place for item={route.Item.ID}, skipping route for NPC={Employee?.fullName}",
              Category.Movement);
          return;
        }

        bool placedSuccessfully = false;
        int placedAmount = 0;
        foreach (var slot in route.DropoffSlots)
        {
          int capacity = slot.GetCapacityForItem(itemToPlace);
          int amount = Math.Min(route.InventorySlot.Quantity, capacity);
          Log(Level.Verbose,
              $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Slot capacity={capacity}, placing {amount} for NPC={Employee?.fullName}",
              Category.Movement);

          if (amount <= 0)
            continue;
          if (await slot.AdvInsertItemAsync(itemToPlace, amount, route.DropOff.GUID, Employee))
          {
            (var success, _) = await route.InventorySlot.AdvRemoveItemAsync(amount, Employee.GUID, Employee);
            if (success)
            {
              placedSuccessfully = true;
              placedAmount += amount;
              grabbedAmount -= amount;
              Log(Level.Info,
                  $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Placed {amount} at {dropoff.GUID} for NPC={Employee?.fullName}",
                  Category.Movement);
            }
            else
            {
              await slot.AdvRemoveItemAsync(amount, route.DropOff.GUID, Employee);
              Log(Level.Warning,
                  $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Failed to remove {amount} from inventory slot for NPC={Employee?.fullName}",
                  Category.Movement);
            }
          }
          else
          {
            Log(Level.Warning,
                $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Failed to insert {amount} into dropoff slot for NPC={Employee?.fullName}",
                Category.Movement);
          }
        }

        await Task.Delay(TimeSpan.FromSeconds(0.2f));

        if (placedSuccessfully)
        {
          _anySuccess = true;
          Log(Level.Info,
              $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Placed {placedAmount}, remaining grabbedAmount={grabbedAmount}, anySuccess={_anySuccess} for NPC={Employee?.fullName}",
              Category.Movement);
        }
        else
        {
          Log(Level.Warning,
              $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Failed to place any items, skipping route for NPC={Employee?.fullName}",
              Category.Movement);
        }
      }
      catch (Exception ex)
      {
        Log(Level.Error,
            $"AdvMoveItemBeh.PlaceItemToDropoffAsync: Exception for {Employee?.fullName} - {ex.Message}",
            Category.Movement);
      }
    }

    private IEnumerator PlaceItemToDropoffCoroutine(PrioritizedRoute route, List<PrioritizedRoute> routes, int routeIndex)
    {
      Log(Level.Verbose, $"PlaceItemToDropoffCoroutine: {Employee.fullName}, item={route.Item.ID}", Category.Movement);
      var dropoff = route.DropOff;
      var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
      if (accessPoint == null) yield break;

      Employee.Movement.FaceDirection(accessPoint.forward);
      Employee.SetAnimationTrigger_Networked(null, "GrabItem");

      float animationTime = 0.5f;
      while (animationTime > 0)
      {
        animationTime -= Time.deltaTime;
        yield return null;
      }

      ItemInstance itemToPlace = route.InventorySlot.ItemInstance;
      if (itemToPlace == null) yield break;

      bool placedSuccessfully = false;
      int placedAmount = 0;
      int batchSize = GetDynamicBatchSize(route.DropoffSlots.Count, 0.1f, nameof(PlaceItemToDropoffCoroutine));
      int processedCount = 0;
      var stopwatch = Stopwatch.StartNew();

      foreach (var slot in route.DropoffSlots)
      {
        int capacity = slot.GetCapacityForItem(itemToPlace);
        int amount = Math.Min(route.InventorySlot.Quantity, capacity);
        if (amount <= 0) continue;

        // Wait for AdvInsertItemAsync and get the bool result
        var insertTask = slot.AdvInsertItemAsync(itemToPlace, amount, route.DropOff.GUID, Employee);
        yield return new TaskYieldInstruction<bool>(insertTask);
        bool insertSuccess = insertTask.Result;

        if (insertSuccess)
        {
          // Wait for AdvRemoveItemAsync and get the (bool, ItemInstance) result
          var removeTask = route.InventorySlot.AdvRemoveItemAsync(amount, Employee.GUID, Employee);
          yield return new TaskYieldInstruction<(bool, ItemInstance)>(removeTask);
          var (success, removedItem) = removeTask.Result;

          if (success)
          {
            placedSuccessfully = true;
            placedAmount += amount;
            grabbedAmount -= amount;
            Log(Level.Info,
                $"PlaceItemToDropoffCoroutine: Placed {amount} of {removedItem.ID} at {dropoff.GUID}",
                Category.Movement);
          }
          else
          {
            // Rollback the insert if removal fails
            var rollbackTask = slot.AdvRemoveItemAsync(amount, route.DropOff.GUID, Employee);
            yield return new TaskYieldInstruction<(bool, ItemInstance)>(rollbackTask);
            Log(Level.Warning,
                $"PlaceItemToDropoffCoroutine: Failed to remove {amount} from inventory",
                Category.Movement);
          }
        }
        else
        {
          Log(Level.Warning,
              $"PlaceItemToDropoffCoroutine: Failed to insert {amount} into dropoff slot",
              Category.Movement);
        }

        processedCount++;
        if (processedCount % batchSize == 0)
        {
          if (processedCount > 0)
          {
            double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
            DynamicProfiler.AddSample(nameof(PlaceItemToDropoffCoroutine), avgItemTimeMs);
            stopwatch.Restart();
          }
          yield return null;
          processedCount = 0;
        }
      }

      if (processedCount > 0)
      {
        double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (Stopwatch.Frequency * processedCount);
        DynamicProfiler.AddSample(nameof(PlaceItemToDropoffCoroutine), avgItemTimeMs);
      }

      float postPlaceDelay = 0.2f;
      while (postPlaceDelay > 0)
      {
        postPlaceDelay -= Time.deltaTime;
        yield return null;
      }

      if (placedSuccessfully)
      {
        _anySuccess = true;
        Log(Level.Info,
            $"PlaceItemToDropoffCoroutine: Placed {placedAmount}, anySuccess={_anySuccess}",
            Category.Movement);
      }

      _currentCoroutine = null;
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
        _sameSourceRoutes = new List<PrioritizedRoute>();
        while (_routeQueue.Count > 0 && (_routeQueue.Peek().PickUp?.GUID ?? Guid.Empty) == currentSource)
          _sameSourceRoutes.Add(_routeQueue.Dequeue());
        if (!_sameSourceRoutes.Any() || _sameSourceRoutes.Any(r => r.Item == null || r.DropOff == null))
        {
          Log(Level.Error, $"ProcessNextRoute: Invalid routes in group: count={_sameSourceRoutes.Count}", Category.Movement);
          ProcessNextRoute();
          return;
        }
        _currentRoute = _sameSourceRoutes[0];
        var firstRoute = _currentRoute.Value;
        itemToRetrieveTemplate = firstRoute.Item;
        maxMoveAmount = _sameSourceRoutes.Sum(r => r.Quantity);
        skipPickup = firstRoute.PickUp == null;
        assignedRoute = firstRoute.TransitRoute;
        Log(Level.Info, $"ProcessNextRoute: Processing {_sameSourceRoutes.Count} routes from source={currentSource}, total qty={maxMoveAmount}", Category.Movement);
        if (skipPickup)
        {
          if (!InventoryRoutes.ContainsKey(Employee.GUID))
            InventoryRoutes[Employee.GUID] = new();
          InventoryRoutes[Employee.GUID].Add(assignedRoute);
        }
        if (!IsTransitRouteValid(assignedRoute, itemToRetrieveTemplate, out var invalidReason))
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
        _currentTask = MoveToPickupGroupAsync(_sameSourceRoutes);
      }
    }

    private void SavePauseState()
    {
      _pauseState.CurrentState = currentState;
      _pauseState.CurrentRouteIndex = _pauseState.CurrentRouteIndex;
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
          _currentTask = MoveToPickupGroupAsync(_pauseState.CurrentRouteGroup);
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