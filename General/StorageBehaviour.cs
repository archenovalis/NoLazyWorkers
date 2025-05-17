using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Employees.EmployeeBehaviour;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.ChemistExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using NoLazyWorkers.General;
using System.Collections;
using UnityEngine;
using ScheduleOne.DevUtilities;
using NoLazyWorkers.Stations;
using Steamworks;
using Unity.Mathematics;
using FishNet.Object;
using FishNet.Managing.Object;

namespace NoLazyWorkers.General
{
  public static class AdvancedMoveItemBehaviourUtilities
  {
    public static bool TakeItem(this ItemSlot slot, int amount, out ItemInstance item)
    {
      if (slot == null || amount <= 0)
      {
        item = null;
        return false;
      }

      item = slot.ItemInstance;
      int initialQuantity = slot.Quantity;
      slot.ChangeQuantity(-amount);
      return slot.Quantity != initialQuantity;
    }

    public static bool InsertItem(this ItemSlot slot, ItemInstance item, int amount)
    {
      if (slot == null || item == null || amount <= 0)
        return false;

      int initialQuantity = slot.Quantity;
      slot.InsertItem(item.GetCopy(amount));
      return slot.Quantity != initialQuantity;
    }
  }

  public class AdvancedMoveItemBehaviour : Behaviour
  {
    public void Setup(Employee employee)
    {
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
      WalkingToSource,
      Grabbing,
      WalkingToDropOff,
      Placing
    }

    public Employee Employee { get; private set; }
    private readonly Queue<PrioritizedRoute> _routeQueue = new Queue<PrioritizedRoute>();
    private PrioritizedRoute? _currentRoute;
    private ItemSlot _reservedSlot;
    private Action<Employee, StateData> _callback;
    private bool _success;
    private StateData _stateData;
    private Coroutine _currentCoroutine;
    private static readonly HashSet<TransitRoute> InventoryRoutes = new HashSet<TransitRoute>();

    // Fields from MoveItemBehaviour
    public TransitRoute assignedRoute;
    public ItemInstance itemToRetrieveTemplate;
    public int grabbedAmount;
    public int maxMoveAmount = -1;
    public EState currentState;
    public bool skipPickup;
    private bool _firstRoute;
    public void Initialize(List<PrioritizedRoute> routes, StateData stateData = null, Action<Employee, StateData> callback = null)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Initialize", DebugLogger.Category.EmployeeCore);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour: {Employee.fullName} Skipping client-side for NPC={Employee?.fullName ?? "null"}", DebugLogger.Category.EmployeeCore);
        return;
      }
      _firstRoute = true;
      _success = false;
      grabbedAmount = 0;
      foreach (var route in routes)
        _routeQueue.Enqueue(route);
      _callback = callback;
      _stateData = stateData;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Queuing route for NPC={Employee?.fullName ?? "null"}, item={routes.Count}", DebugLogger.Category.EmployeeCore);
      ProcessNextRoute();
    }

    private void ProcessNextRoute()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} ProcessNextRoute", DebugLogger.Category.EmployeeCore);
      if (_routeQueue.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: All routes complete for NPC={Employee.fullName ?? "null"}", DebugLogger.Category.EmployeeCore);
        _success = true;
        Disable_Networked(null);
        return;
      }

      _currentRoute = _routeQueue.Dequeue();
      var route = _currentRoute.Value;
      if (route.Item == null || route.DropOff == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Invalid route: Item={route.Item?.ID ?? "null"}, Dropoff={route.DropOff?.GUID}", DebugLogger.Category.EmployeeCore);

        FailAndEnd($"Invalid route: Item={route.Item?.ID ?? "null"}, Dropoff={route.DropOff?.GUID}");
        return;
      }
      itemToRetrieveTemplate = route.Item;
      maxMoveAmount = route.Quantity;
      skipPickup = route.PickUp == null;
      assignedRoute = new TransitRoute(route.PickUp, route.DropOff);
      _reservedSlot = route.InventorySlot;

      if (_reservedSlot != null)
        EmployeeUtilities.SetReservedSlot(Employee, _reservedSlot);

      if (skipPickup)
        lock (InventoryRoutes)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Locking", DebugLogger.Category.EmployeeCore);
          InventoryRoutes.Add(assignedRoute);
        }

      if (!IsTransitRouteValid(assignedRoute, itemToRetrieveTemplate, out var invalidReason))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Transit route invalid: {invalidReason}", DebugLogger.Category.EmployeeCore);

        FailAndEnd($"Transit route invalid: {invalidReason}");
        return;
      }
      if (_firstRoute)
      {
        _firstRoute = false;
        Enable_Networked(null);
        Begin();
      }

      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Processing route: item={route.Item?.ID ?? "null"}, qty={route.Quantity}, skipPickup={skipPickup}", DebugLogger.Category.EmployeeCore);

      if (skipPickup)
      {
        grabbedAmount = _reservedSlot.Quantity;
        currentState = EState.WalkingToDropOff;
        if (IsAtDropOff())
          PlaceItem();
        else
          WalkToDropOff();
      }
      else
        currentState = EState.Idle;
    }

    public override void Begin()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Begin", DebugLogger.Category.EmployeeCore);
      base.Begin();
      StartTransit();
    }

    public void StartTransit()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} StartTransit", DebugLogger.Category.EmployeeCore);
      if (!InstanceFinder.IsServer)
      {
        return;
      }
      currentState = EState.Idle;
    }

    public override void Pause()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Pause", DebugLogger.Category.EmployeeCore);
      base.Pause();
      StopCurrentActivity();
    }

    public void StopCurrentActivity()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} StopCurrentActivity", DebugLogger.Category.EmployeeCore);
      if (_currentCoroutine != null)
      {
        StopCoroutine(_currentCoroutine);
      }
      currentState = EState.Idle;
    }

    public override void Resume()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Resume", DebugLogger.Category.EmployeeCore);
      base.Resume();
      StartTransit();
    }

    public override void Disable()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Disable", DebugLogger.Category.EmployeeCore);
      base.Disable();
      if (Active)
      {
        End();
      }
    }

    public override void ActiveMinPass()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} ActiveMinPass", DebugLogger.Category.EmployeeCore);
      if (_currentRoute == null)
        Disable_Networked(null);
      if (!InstanceFinder.IsServer || !Active || currentState != EState.Idle)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Exiting ActiveMinPass: IsServer={InstanceFinder.IsServer}, Active={Active}, State={currentState}", DebugLogger.Category.EmployeeCore);
        return;
      }

      int inventoryAmount = Employee.Inventory.GetIdenticalItemAmount(itemToRetrieveTemplate);
      if (inventoryAmount > 0 && grabbedAmount > 0)
      {
        if (IsAtDropOff())
          PlaceItem();
        else
          WalkToDropOff();
      }
      else if (IsAtPickUp())
      {
        if (currentState != EState.Grabbing)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} At pickup, initiating GrabItem", DebugLogger.Category.EmployeeCore);
          GrabItem();
        }
      }
      else
      {
        WalkToPickUp();
      }
    }

    private void TakeItem()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} TakeItem", DebugLogger.Category.EmployeeCore);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour: {Employee.fullName} Skipping client-side TakeItem for NPC={Employee?.fullName ?? "null"}", DebugLogger.Category.EmployeeCore);
        return;
      }

      var route = _currentRoute.Value;
      if (route.PickupSlots?.Count > 0)
      {
        int quantityToTake = Math.Min(maxMoveAmount, route.PickupSlots.Sum(s => s.Quantity));
        foreach (var slot in route.PickupSlots)
        {
          if (quantityToTake <= 0) break;
          int takeAmount = Math.Min(slot.Quantity, quantityToTake);
          if (slot.TakeItem(takeAmount, out var takenItem))
          {
            if (_reservedSlot.InsertItem(takenItem, takeAmount))
            {
              grabbedAmount += takeAmount;
              quantityToTake -= takeAmount;
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Took {takeAmount} of {takenItem.ID}", DebugLogger.Category.EmployeeCore);
            }
            else
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed to insert {takeAmount} of {takenItem.ID} into reserved slot", DebugLogger.Category.EmployeeCore);
              if (slot.InsertItem(takenItem, takeAmount))
              {
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Returned {takeAmount} of {takenItem.ID} to pickup slot", DebugLogger.Category.EmployeeCore);
              }
              else
              {
                DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed to return {takeAmount} of {takenItem.ID} to pickup slot", DebugLogger.Category.EmployeeCore);
              }
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed to take {takeAmount} of item from slot", DebugLogger.Category.EmployeeCore);
          }
        }

        if (grabbedAmount > 0)
        {
          assignedRoute.Destination.ReserveInputSlotsForItem(_reservedSlot.ItemInstance.GetCopy(grabbedAmount), Employee.NetworkObject);
          currentState = EState.WalkingToDropOff;
          {
            if (IsAtDropOff())
              PlaceItem();
            else
              WalkToDropOff();
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed to take any items from pickup slots", DebugLogger.Category.EmployeeCore);
          FailAndEnd("Failed to take any items from pickup slots");
        }
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} No pickup slots available", DebugLogger.Category.EmployeeCore);
        FailAndEnd("No pickup slots available");
      }
    }

    private void WalkToPickUp()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} WalkToPickUp", DebugLogger.Category.EmployeeCore);
      var pickup = assignedRoute?.Source;
      var accessPoint = pickup != null ? NavMeshUtility.GetAccessPoint(pickup, Employee) : null;
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} No access point for pickup={pickup?.GUID}", DebugLogger.Category.EmployeeCore);

        FailAndEnd("No access point for pickup");
        return;
      }

      currentState = EState.WalkingToSource;

      StartCoroutineSafely(WalkToPickupRoutine(accessPoint.position));

      IEnumerator WalkToPickupRoutine(Vector3 pickupPos)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Walking to pickup position={pickupPos} from NPCPos={Employee.transform.position}, IsMoving={Employee.Movement.IsMoving}", DebugLogger.Category.EmployeeCore);
        SetDestination(pickupPos);

        float startTime = Time.time;
        float timeoutSeconds = 10.0f;
        bool startedMoving = false;

        while (Time.time - startTime < timeoutSeconds)
        {
          if (Employee.Movement.IsMoving)
          {
            startedMoving = true;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Started moving to pickup at NPCPos={Employee.transform.position}, elapsed={Time.time - startTime:F2}s", DebugLogger.Category.EmployeeCore);
            break;
          }
          yield return null;
        }

        if (!startedMoving)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Timeout: Failed to start moving to pickup={assignedRoute.Source.GUID} after {timeoutSeconds}s, NPCPos={Employee.transform.position}, TargetPos={pickupPos}", DebugLogger.Category.EmployeeCore);
          FailAndEnd("Timeout: Failed to start moving to pickup");
          yield break;
        }

        yield return new WaitUntil(() => !Employee.Movement.IsMoving);
        currentState = EState.Idle;
        _currentCoroutine = null;
      }
    }

    private void WalkToDropOff()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} WalkToDropOff", DebugLogger.Category.EmployeeCore);
      var dropoff = assignedRoute?.Destination;
      var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
      currentState = EState.WalkingToDropOff;
      StartCoroutineSafely(WalkToDropoffRoutine(accessPoint.position));

      IEnumerator WalkToDropoffRoutine(Vector3 dropoffPos)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Walking to dropoff position={dropoffPos} from NPCPos={Employee.transform.position}, IsMoving={Employee.Movement.IsMoving}", DebugLogger.Category.EmployeeCore);
        SetDestination(dropoffPos);

        float startTime = Time.time;
        float timeoutSeconds = 10.0f;
        bool startedMoving = false;

        while (Time.time - startTime < timeoutSeconds)
        {
          if (Employee.Movement.IsMoving)
          {
            startedMoving = true;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} Started moving to dropoff at NPCPos={Employee.transform.position}, elapsed={Time.time - startTime:F2}s", DebugLogger.Category.EmployeeCore);
            break;
          }
          yield return null;
        }

        if (!startedMoving)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Timeout: Failed to start moving to dropoff={assignedRoute.Destination.GUID} after {timeoutSeconds}s, NPCPos={Employee.transform.position}, TargetPos={dropoffPos}", DebugLogger.Category.EmployeeCore);
          FailAndEnd("Timeout: Failed to start moving to dropoff");
          yield break;
        }

        yield return new WaitUntil(() => !Employee.Movement.IsMoving);
        currentState = EState.Idle;
        _currentCoroutine = null;
      }
    }

    private void StartCoroutineSafely(IEnumerator routine)
    {
      if (_currentCoroutine != null)
        StopCoroutine(_currentCoroutine);
      _currentCoroutine = StartCoroutine(routine);
    }

    private void GrabItem()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} GrabItem", DebugLogger.Category.EmployeeCore);
      var sourceAccessPoint = NavMeshUtility.GetAccessPoint(assignedRoute.Source, Employee);
      if (sourceAccessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} No pickup access point", DebugLogger.Category.EmployeeCore);

        FailAndEnd("No pickup access point");
        return;
      }
      currentState = EState.Grabbing;

      StartCoroutineSafely(GrabRoutine());

      IEnumerator GrabRoutine()
      {
        Employee.Movement.FaceDirection(sourceAccessPoint.forward);
        Employee.SetAnimationTrigger_Networked(null, "GrabItem");
        yield return new WaitForSeconds(0.5f);
        TakeItem();
        yield return new WaitForSeconds(0.2f);
        currentState = EState.Idle;
        _currentCoroutine = null;
      }
    }

    private void PlaceItem()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} PlaceItem", DebugLogger.Category.EmployeeCore);
      currentState = EState.Placing;
      StartCoroutineSafely(PlaceRoutine());

      IEnumerator PlaceRoutine()
      {
        var dropoff = assignedRoute?.Destination;
        var accessPoint = NavMeshUtility.GetAccessPoint(dropoff, Employee);
        Employee.Movement.FaceDirection(accessPoint.forward);

        Employee.SetAnimationTrigger_Networked(null, "GrabItem");
        yield return new WaitForSeconds(0.5f);

        dropoff?.RemoveSlotLocks(Employee.NetworkObject);

        ItemInstance itemToPlace = _reservedSlot.ItemInstance;
        if (itemToPlace == null || grabbedAmount <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Could not find item to place: Item={itemToRetrieveTemplate?.ID ?? "null"}, grabbedAmount={grabbedAmount}", DebugLogger.Category.EmployeeCore);
          FailAndEnd("Could not find item to place");
          yield break;
        }

        bool placedSuccessfully = false;
        if (dropoff != null)
        {
          var placedAmount = 0;
          foreach (var slot in _currentRoute.Value.DropoffSlots)
          {
            if (grabbedAmount <= placedAmount) break;
            var amount = math.min(_reservedSlot.Quantity, itemToPlace.StackLimit - (slot.ItemInstance != null ? slot.ItemInstance.Quantity : 0));
            if (!slot.InsertItem(itemToPlace, amount))
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed to place {itemToPlace.ID} at dropoff={dropoff.GUID}", DebugLogger.Category.EmployeeCore);
            else
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Placed {amount} of {itemToPlace.ID} at dropoff={dropoff.GUID}", DebugLogger.Category.EmployeeCore);
              placedAmount += amount;
              placedSuccessfully = true;
            }
          }

          if (placedSuccessfully)
          {
            EmployeeUtilities.ReleaseReservations(Employee);
            _reservedSlot.ChangeQuantity(-placedAmount);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: {Employee.fullName} Route complete for NPC={Employee?.fullName ?? "null"}", DebugLogger.Category.EmployeeCore);
            ProcessNextRoute();
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed to place {itemToPlace.ID}", DebugLogger.Category.EmployeeCore);
            FailAndEnd("Failed to place item");
          }

          yield return new WaitForSeconds(0.2f);
          currentState = EState.Idle;
          _currentCoroutine = null;
        }
      }
    }

    private void FailAndEnd(string reason)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: {Employee.fullName} Failed: {reason}", DebugLogger.Category.EmployeeCore);
      GetState(Employee).CurrentState = EmployeeExtensions.EState.Idle;
      Disable_Networked(null);
    }

    public override void End()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} End", DebugLogger.Category.EmployeeCore);
      base.End();
      if (_currentCoroutine != null)
      {
        StopCoroutine(_currentCoroutine);
        _currentCoroutine = null;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} End 1", DebugLogger.Category.EmployeeCore);
      _currentRoute = null;
      EmployeeUtilities.ReleaseReservations(Employee);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} End 2", DebugLogger.Category.EmployeeCore);
      _routeQueue.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} End 3", DebugLogger.Category.EmployeeCore);
      lock (InventoryRoutes)
      {
        InventoryRoutes.Clear();
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} End 4 {_success} | {_callback} | {Employee} | {_stateData}", DebugLogger.Category.EmployeeCore);
      if (_success)
        _callback?.Invoke(Employee, _stateData);
      else
        GetState(Employee).CurrentState = EmployeeExtensions.EState.Idle;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: {Employee.fullName} End 5", DebugLogger.Category.EmployeeCore);
      currentState = EState.Idle;
    }

    // Reimplemented validation methods from MoveItemBehaviour
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
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour: {Employee.fullName} Destination has no capacity for item!", DebugLogger.Category.EmployeeCore);
        return false;
      }
      if (!CanGetToDropoff(route))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour: {Employee.fullName} Cannot get to pickup!", DebugLogger.Category.EmployeeCore);
        return false;
      }
      if (!skipPickup && !CanGetToPickup(route))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour: {Employee.fullName} Cannot get to pickup!", DebugLogger.Category.EmployeeCore);
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

    private bool IsAtPickUp()
    {
      return NavMeshUtility.IsAtTransitEntity(assignedRoute.Source, Employee);
    }

    private bool IsAtDropOff()
    {
      return NavMeshUtility.IsAtTransitEntity(assignedRoute.Destination, Employee);
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
          if (__instance.Source == null && __instance.Destination != null && InventoryRoutes.Contains(__instance))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour: TransitRoutePatch.AreEntitiesNonNullPrefix: Allowing inventory route with null Source and Destination={__instance.Destination.GUID}", DebugLogger.Category.EmployeeCore);
            __result = true;
            return false;
          }
          return true;
        }
        catch (System.Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour: TransitRoutePatch.AreEntitiesNonNullPrefix: Failed, error={e}", DebugLogger.Category.EmployeeCore);
          __result = false;
          return false;
        }
      }
    }
  }
}
