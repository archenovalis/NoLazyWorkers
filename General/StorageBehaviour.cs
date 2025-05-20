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
    private PrioritizedRoute? _prioritizedRoute;
    private ItemSlot _reservedSlot;

    public AdvancedMoveItemBehaviour(NPC npc) : base()
    {
      ActiveMoveItemBehaviours[npc.GUID] = this;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour: Initialized for NPC={npc.fullName}", DebugLogger.Category.AllEmployees);
    }

    public virtual void Initialize(PrioritizedRoute route)
    {
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.Initialize: Skipping client-side for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        return;
      }
      _prioritizedRoute = route;
      itemToRetrieveTemplate = route.Item;
      maxMoveAmount = route.Quantity;
      skipPickup = route.PickupLocation == null;
      assignedRoute = route.PickupLocation != null ? new TransitRoute(route.PickupLocation, route.Destination) : null;
      _reservedSlot = route.InventorySlot;
      EmployeeUtilities.SetReservedSlot(this, _reservedSlot);

      base.Initialize(assignedRoute, route.Item, route.Quantity, skipPickup);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour.Initialize: Initialized for NPC={Npc.fullName}, item={route.Item.ID}, qty={route.Quantity}, pickup={route.PickupLocation?.GUID.ToString() ?? "inventory"}, dest={route.Destination.GUID}", DebugLogger.Category.AllEmployees);

      if (skipPickup)
      {
        // Inventory route: Manually start delivery
        currentState = EState.WalkingToDestination;
        MoveToDestination();
      }
    }

    public new void Initialize(TransitRoute route, ItemInstance itemTemplate, int maxAmount, bool skipPickupFlag)
    {
      base.Initialize(route, itemTemplate, maxAmount, skipPickupFlag);
      _prioritizedRoute = null;
      _reservedSlot = Npc.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (_reservedSlot != null)
      {
        EmployeeUtilities.SetReservedSlot(this, _reservedSlot);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Initialize: Fallback to base for NPC={Npc.fullName}, item={itemTemplate.ID}", DebugLogger.Category.AllEmployees);

      if (skipPickupFlag)
      {
        currentState = EState.WalkingToDestination;
        MoveToDestination();
      }
    }

    private void MoveToDestination()
    {
      var accessPoint = NavMeshUtility.GetAccessPoint(assignedRoute?.Destination ?? _prioritizedRoute?.Destination, Npc);
      if (accessPoint != null)
      {
        Npc.Movement.SetDestination(accessPoint.position);
        StartCoroutine(CheckArrival(assignedRoute?.Destination ?? _prioritizedRoute?.Destination, () =>
        {
          currentState = EState.Placing;
          PlaceItem();
        }));
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.MoveToDestination: No access point for destination={_prioritizedRoute?.Destination.GUID}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
      }
    }

    private IEnumerator CheckArrival(ITransitEntity location, Action onArrival)
    {
      while (location != null && !NavMeshUtility.IsAtTransitEntity(location, Npc))
      {
        yield return null;
      }
      onArrival?.Invoke();
    }

    public new void TakeItem()
    {
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Skipping client-side for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      if (_reservedSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: No reserved slot for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
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
        return;
      }
      if (_reservedSlot.ItemInstance != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Reserved slot not empty for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      int amountToGrab = GetAmountToGrab();
      if (amountToGrab == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.TakeItem: Amount to grab is 0 for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      ItemSlot sourceSlot = assignedRoute.Source.GetFirstSlotContainingTemplateItem(itemToRetrieveTemplate, ITransitEntity.ESlotType.Output);
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

    public new void PlaceItem()
    {
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.PlaceItem: Skipping client-side for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      if (_reservedSlot == null || _reservedSlot.ItemInstance == null || _reservedSlot.Quantity < grabbedAmount || !_reservedSlot.ItemInstance.CanStackWith(itemToRetrieveTemplate, false))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.PlaceItem: Invalid reserved slot for NPC={Npc.fullName}, qty={_reservedSlot?.Quantity}, item={_reservedSlot?.ItemInstance?.ID}", DebugLogger.Category.AllEmployees);
        Disable_Networked(null);
        return;
      }
      currentState = EState.Placing;
      placingRoutine = StartCoroutine(PlaceRoutine());

      IEnumerator PlaceRoutine()
      {
        var destination = assignedRoute?.Destination ?? _prioritizedRoute?.Destination;
        var accessPoint = destination != null ? NavMeshUtility.GetAccessPoint(destination, Npc) : null;
        if (accessPoint != null)
        {
          Npc.Movement.FaceDirection(accessPoint.forward);
        }
        Npc.SetAnimationTrigger_Networked(null, "GrabItem");
        yield return new WaitForSeconds(0.5f);
        destination?.RemoveSlotLocks(Npc.NetworkObject);
        ItemInstance copy = _reservedSlot.ItemInstance.GetCopy(grabbedAmount);
        if (destination != null && destination.GetInputCapacityForItem(copy, Npc) >= grabbedAmount)
        {
          destination.InsertItemIntoInput(copy, Npc);
          _reservedSlot.ChangeQuantity(-grabbedAmount);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"AdvancedMoveItemBehaviour.PlaceItem: Delivered {grabbedAmount} of {itemToRetrieveTemplate.ID} to {destination.GUID}", DebugLogger.Category.AllEmployees);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AdvancedMoveItemBehaviour.PlaceItem: Destination lacks capacity for {grabbedAmount} of {itemToRetrieveTemplate.ID}", DebugLogger.Category.AllEmployees);
          Disable_Networked(null);
        }
        yield return new WaitForSeconds(0.5f);
        placingRoutine = null;
        currentState = EState.Idle;
        Disable_Networked(null);
      }
    }

    public override void Disable()
    {
      EmployeeUtilities.ReleaseReservations(this);
      base.Disable();
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Disable: Disabled for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
    }

    protected new void Awake()
    {
      base.Awake();
      ActiveMoveItemBehaviours[Npc.GUID] = this;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.Awake: Registered for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
    }

    protected new void End()
    {
      EmployeeUtilities.ReleaseReservations(this);
      ActiveMoveItemBehaviours.Remove(Npc.GUID);
      base.End();
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AdvancedMoveItemBehaviour.End: Released for NPC={Npc.fullName}", DebugLogger.Category.AllEmployees);
    }
  }
}