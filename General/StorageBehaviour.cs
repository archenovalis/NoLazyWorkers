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

namespace NoLazyWorkers.General
{
  public class AdvancedMoveItemBehaviour : MoveItemBehaviour
  {
    public Employee employee;
    private PrioritizedRoute? _prioritizedRoute;
    private ItemSlot _reservedSlot;
    public static readonly HashSet<TransitRoute> InventoryRoutes = new HashSet<TransitRoute>();

    public virtual void Initialize(PrioritizedRoute route)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Initialize: Entering with route={route}, NPC={employee?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.Initialize: Skipping client-side for NPC={employee?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        return;
      }
      if (route.Destination == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour.Initialize: Invalid destination for NPC={employee?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      if (route.Item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour.Initialize: Invalid item for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Initialize: {route} \n {route.Destination} \n {route.DestinationSlots} \n {route.InventorySlot} \n {route.Item} \n {route.PickupSlots} \n {route.Priority} \n {route.Quantity} \n {route.Source}", DebugLogger.Category.AllEmployees);
      _prioritizedRoute = route;
      itemToRetrieveTemplate = route.Item;
      maxMoveAmount = route.Quantity;
      skipPickup = route.Source == null;
      assignedRoute = new TransitRoute(route.Source, route.Destination);
      _reservedSlot = route.InventorySlot;
      EmployeeUtilities.SetReservedSlot(employee, _reservedSlot);

      if (skipPickup)
      {
        lock (InventoryRoutes)
        {
          InventoryRoutes.Add(assignedRoute);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Initialize: Added route with Destination={route.Destination.GUID} to InventoryRoutes", DebugLogger.Category.AllEmployees);
        }
      }

      base.Initialize(assignedRoute, route.Item, route.Quantity, skipPickup);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour.Initialize: Initialized for NPC={employee?.fullName ?? "null"}, item={route.Item.ID}, qty={route.Quantity}, pickup={route.Source?.GUID.ToString() ?? "inventory"}, dest={route.Destination.GUID}", DebugLogger.Category.AllEmployees);

      if (skipPickup)
      {
        currentState = EState.WalkingToDestination;
        MoveToDestination();
      }
    }

    public new void Initialize(TransitRoute route, ItemInstance itemTemplate, int maxAmount, bool skipPickupFlag)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Initialize: Entering with route={route}, item={itemTemplate?.ID ?? "null"}, maxAmount={maxAmount}, skipPickup={skipPickupFlag}, NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      base.Initialize(route, itemTemplate, maxAmount, skipPickupFlag);
      _prioritizedRoute = null;
      _reservedSlot = Npc.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (_reservedSlot != null)
      {
        EmployeeUtilities.SetReservedSlot(Npc as Employee, _reservedSlot);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Initialize: Fallback to base for NPC={Npc?.fullName ?? "null"}, item={itemTemplate?.ID ?? "null"}", DebugLogger.Category.AllEmployees);

      if (skipPickupFlag)
      {
        currentState = EState.WalkingToDestination;
        MoveToDestination();
      }
    }

    public override void ActiveMinPass()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      base.ActiveMinPass();
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: Skipped, not server for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        return;
      }

      if (!assignedRoute.AreEntitiesNonNull())
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.ActiveMinPass: Transit route entities are null for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }

      if (currentState == EState.Idle)
      {
        int inventoryAmount = Npc.Inventory.GetIdenticalItemAmount(itemToRetrieveTemplate);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: Idle, InventoryAmount={inventoryAmount}, GrabbedAmount={grabbedAmount}, SkipPickup={skipPickup} for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);

        if (inventoryAmount > 0 && grabbedAmount > 0)
        {
          if (IsAtDestination())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: At destination, calling PlaceItem for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
            PlaceItem();
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: Not at destination, calling WalkToDestination for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
            WalkToDestination();
          }
        }
        else if (skipPickup)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: SkipPickup, calling TakeItem for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
          TakeItem();
          skipPickup = false;
        }
        else if (IsAtSource())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: At source, calling GrabItem for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
          GrabItem();
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.ActiveMinPass: Not at source, calling WalkToSource for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
          WalkToSource();
        }
      }
    }

    private void MoveToDestination()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.MoveToDestination: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      var destination = assignedRoute?.Destination ?? _prioritizedRoute?.Destination;
      var accessPoint = destination != null ? NavMeshUtility.GetAccessPoint(destination, Npc) : null;
      if (accessPoint != null)
      {
        Npc.Movement.SetDestination(accessPoint.position);
        StartCoroutine(CheckArrival(destination, () =>
        {
          currentState = EState.Placing;
          PlaceItem();
        }));
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.MoveToDestination: No access point for destination={destination?.GUID}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
      }
    }

    private IEnumerator CheckArrival(ITransitEntity location, Action onArrival)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.CheckArrival: Entering for NPC={Npc?.fullName ?? "null"}, location={location?.GUID}", DebugLogger.Category.AllEmployees);
      while (location != null && !NavMeshUtility.IsAtTransitEntity(location, Npc))
      {
        yield return null;
      }
      onArrival?.Invoke();
    }

    protected new void TakeItem()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.TakeItem: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Skipping client-side for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      if (_reservedSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: No reserved slot for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      if (skipPickup)
      {
        if (_reservedSlot.ItemInstance == null || !_reservedSlot.ItemInstance.CanStackWith(itemToRetrieveTemplate, false) || _reservedSlot.Quantity < maxMoveAmount)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Invalid inventory slot for item={itemToRetrieveTemplate.ID}, qty={_reservedSlot.Quantity}, needed={maxMoveAmount}", DebugLogger.Category.AllEmployees);
          Disable_Networked(null);
          return;
        }
        grabbedAmount = maxMoveAmount;
        var destination = assignedRoute?.Destination ?? _prioritizedRoute?.Destination;
        destination?.ReserveInputSlotsForItem(_reservedSlot.ItemInstance.GetCopy(grabbedAmount), Npc.NetworkObject);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour.TakeItem: Skipped pickup for {grabbedAmount} of {itemToRetrieveTemplate.ID} from inventory", DebugLogger.Category.AllEmployees);
        MoveToDestination();
        return;
      }
      if (_reservedSlot.ItemInstance != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Reserved slot not empty for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      int amountToGrab = GetAmountToGrab();
      if (amountToGrab == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Amount to grab is 0 for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      ItemSlot sourceSlot = _prioritizedRoute?.PickupSlots.FirstOrDefault(s => s.Quantity >= amountToGrab && s.ItemInstance.CanStackWith(itemToRetrieveTemplate, false)) ?? assignedRoute.Source.GetFirstSlotContainingTemplateItem(itemToRetrieveTemplate, ITransitEntity.ESlotType.Output);
      if (sourceSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: No source slot for item={itemToRetrieveTemplate.ID}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      ItemInstance copy = sourceSlot.ItemInstance.GetCopy(amountToGrab);
      grabbedAmount = amountToGrab;
      sourceSlot.ChangeQuantity(-amountToGrab);
      _reservedSlot.InsertItem(copy);
      assignedRoute.Destination.ReserveInputSlotsForItem(copy, Npc.NetworkObject);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour.TakeItem: Grabbed {amountToGrab} of {itemToRetrieveTemplate.ID} into slot {_reservedSlot.GetHashCode()}", DebugLogger.Category.AllEmployees);
    }

    protected new void PlaceItem()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.PlaceItem: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.PlaceItem: Skipping client-side for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      if (_reservedSlot == null || _reservedSlot.ItemInstance == null || _reservedSlot.Quantity < grabbedAmount || !_reservedSlot.ItemInstance.CanStackWith(itemToRetrieveTemplate, false))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour.PlaceItem: Invalid reserved slot for NPC={Npc?.fullName ?? "null"}, qty={_reservedSlot?.Quantity}, item={_reservedSlot?.ItemInstance?.ID}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      currentState = EState.Placing;
      placingRoutine = StartCoroutine(PlaceRoutine());

      IEnumerator PlaceRoutine()
      {
        var destination = assignedRoute?.Destination ?? _prioritizedRoute?.Destination;
        if (destination == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour.PlaceItem: Destination is null for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
          Disable_Networked(null);
          yield break;
        }
        var accessPoint = NavMeshUtility.GetAccessPoint(destination, Npc);
        if (accessPoint != null)
        {
          Npc.Movement.FaceDirection(accessPoint.forward);
        }
        Npc.SetAnimationTrigger_Networked(null, "GrabItem");
        yield return new WaitForSeconds(0.5f);
        destination.RemoveSlotLocks(Npc.NetworkObject);
        ItemInstance copy = _reservedSlot.ItemInstance.GetCopy(grabbedAmount);
        var destinationSlots = _prioritizedRoute?.DestinationSlots;
        int remaining = grabbedAmount;
        foreach (var slot in destinationSlots.Where(s => s != null && !s.IsLocked))
        {
          int capacity = slot.GetCapacityForItem(copy);
          int amount = Mathf.Min(remaining, capacity);
          if (amount <= 0) continue;
          slot.InsertItem(copy.GetCopy(amount));
          remaining -= amount;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour.PlaceItem: Delivered {amount} of {itemToRetrieveTemplate.ID} to slot {slot.GetHashCode()}", DebugLogger.Category.AllEmployees);
          if (remaining <= 0) break;
        }
        _reservedSlot.ChangeQuantity(-grabbedAmount);
        if (remaining > 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"AdvancedMoveItemBehaviour.PlaceItem: Could not deliver {remaining} of {itemToRetrieveTemplate.ID}", DebugLogger.Category.AllEmployees);
          Disable_Networked(null);
        }
        yield return new WaitForSeconds(0.5f);
        placingRoutine = null;
        currentState = EState.Idle;

        if (skipPickup && assignedRoute != null)
        {
          lock (InventoryRoutes)
          {
            InventoryRoutes.Remove(assignedRoute);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.PlaceItem: Removed route with Destination={destination.GUID} from InventoryRoutes", DebugLogger.Category.AllEmployees);
          }
        }

        Disable_Networked(null);
      }
    }

    public override void Disable()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Disable: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      if (skipPickup && assignedRoute != null)
      {
        lock (InventoryRoutes)
        {
          InventoryRoutes.Remove(assignedRoute);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Disable: Removed route with Destination={assignedRoute.Destination?.GUID} from InventoryRoutes", DebugLogger.Category.AllEmployees);
        }
      }

      EmployeeUtilities.ReleaseReservations(Npc as Employee);
      base.Disable();
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Disable: Disabled for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
    }

    protected new void Awake()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Awake: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      base.Awake();
      AdvancedMoveItemBehaviours[Npc.GUID] = this;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Awake: Registered for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
    }

    protected new void End()
    {
      //TODO: access employee's state, check for activeroutes > 0, transition to transfer, else transition to idle
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.End: Entering for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
      EmployeeUtilities.ReleaseReservations(Npc as Employee);
      base.End();
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.End: Released for NPC={Npc?.fullName ?? "null"}", DebugLogger.Category.AllEmployees);
    }
  }

  [HarmonyPatch(typeof(TransitRoute))]
  public class TransitRoutePatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("AreEntitiesNonNull")]
    public static bool AreEntitiesNonNullPrefix(TransitRoute __instance, ref bool __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"TransitRoutePatch.AreEntitiesNonNullPrefix: Entering for TransitRoute with Destination={__instance.Destination?.GUID}", DebugLogger.Category.AllEmployees);
      try
      {
        if (__instance.Source == null && __instance.Destination != null && AdvancedMoveItemBehaviour.InventoryRoutes.Contains(__instance))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"TransitRoutePatch.AreEntitiesNonNullPrefix: Allowing inventory route with null Source and Destination={__instance.Destination.GUID}", DebugLogger.Category.AllEmployees);
          __result = true;
          return false; // Skip original method
        }
        return true; // Proceed with original method
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"TransitRoutePatch.AreEntitiesNonNullPrefix: Failed, error: {e}", DebugLogger.Category.AllEmployees);
        __result = false;
        return false;
      }
    }
  }
}