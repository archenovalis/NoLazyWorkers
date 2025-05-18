using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.Employees;
using ScheduleOne;
using ScheduleOne.Delivery;
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
using static NoLazyWorkers.Structures.StorageUtilities;
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.DevUtilities;
using NoLazyWorkers.Structures;
using ScheduleOne.NPCs;

namespace NoLazyWorkers.Employees
{
  public static class PackagerExtensions
  {
    public class PackagerAdapter : IEmployeeAdapter
    {
      private readonly Packager _packager;
      public Property AssignedProperty => _packager.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Packager;

      public PackagerAdapter(Packager packager)
      {
        _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      }

      public EmployeeBehaviour GetEmployeeBehaviour(Behaviour behaviour) => ActiveBehaviours[behaviour.Npc.GUID];
      public EmployeeBehaviour GetEmployeeBehaviour(NPC npc) => ActiveBehaviours[npc.GUID];

      public bool HandlePlanning(Behaviour behaviour, StateData state)
      {
        PackagerBehaviour packagerBehaviour = (PackagerBehaviour)GetEmployeeBehaviour(behaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerAdapter.HandlePlanning: Starting for NPC {_packager.fullName}, Property {AssignedProperty}", DebugLogger.Category.Packager);
        var requests = packagerBehaviour.FindItemsNeedingMovement(_packager, state);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.HandlePlanning: Found {requests.Count} routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
        if (requests.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandlePlanning: No routes found. Stations={PropertyStations.GetValueOrDefault(AssignedProperty)?.Count ?? 0}, Shelves={StorageExtensions.AnyShelves.Count}, Docks={AssignedProperty?.LoadingDocks.Length ?? 0}", DebugLogger.Category.Packager);
          return false;
        }
        packagerBehaviour.AddRoutes(behaviour, state, requests);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.HandlePlanning: Planned {requests.Count} routes for NPC {_packager.fullName}", DebugLogger.Category.Packager);
        return true;
      }

      public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerAdapter.HandleInventoryItem: Processing item {item.ID} for NPC {_packager.fullName}", DebugLogger.Category.Packager);
        var destination = FindShelfForDelivery(_packager, item);
        if (destination == null)
        {
          NoDestinationCache[AssignedProperty].Add(item);
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandleInventoryItem: No destination for item {item.ID}", DebugLogger.Category.Packager);
          return false;
        }
        var inventorySlot = _packager.Inventory.ItemSlots
            .FirstOrDefault(s => s?.ItemInstance != null && s.ItemInstance.CanStackWith(item) && s.Quantity > 0);
        if (inventorySlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandleInventoryItem: No valid inventory slot for item {item.ID}", DebugLogger.Category.Packager);
          return false;
        }
        var transitEntity = destination as ITransitEntity;
        var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, _packager.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandleInventoryItem: No valid delivery slots for item {item.ID}", DebugLogger.Category.Packager);
          return false;
        }
        int quantity = Math.Min(inventorySlot.Quantity, transitEntity.GetInputCapacityForItem(item, _packager));
        if (quantity <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerAdapter.HandleInventoryItem: Invalid quantity {quantity} for item {item.ID}", DebugLogger.Category.Packager);
          return false;
        }
        var request = new TransferRequest(item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, deliverySlots);
        state.ActiveRoutes.Add(new PrioritizedRoute(request, EmployeeBehaviour.PRIORITY_SHELF_RESTOCK));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.HandleInventoryItem: Added route for {quantity} of {item.ID}", DebugLogger.Category.Packager);
        return true;
      }

      public bool HandleIdle(Behaviour behaviour, StateData state) => false;
      public bool HandleMoving(Behaviour behaviour, StateData state) => false;
      public bool HandleGrabbing(Behaviour behaviour, StateData state) => false;
      public bool HandleInserting(Behaviour behaviour, StateData state) => false;
      public bool HandleOperating(Behaviour behaviour, StateData state) => false;
      public bool HandleCompleted(Behaviour behaviour, StateData state) => false;
    }
  }
}