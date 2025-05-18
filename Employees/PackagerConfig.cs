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
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;

namespace NoLazyWorkers.Employees
{
  public static class PackagerExtensions
  {
    public class PackagerAdapter(Packager packager) : IEmployeeAdapter
    {
      private readonly Packager _packager = packager;

      public Property AssignedProperty => _packager.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Packager;

      public bool CustomizePlanning(Behaviour behaviour, StateData state)
      {
        var requests = PackagerBehaviour.FindItemsNeedingMovement(behaviour.Npc as Packager, PackagerPatch.ScanType.None);
        if (requests.Count == 0) return false;

        EmployeeBehaviour.AddRoutes(behaviour, state, requests);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandlerAdapter.CustomizePlanning: Planned {requests.Count} routes",
            DebugLogger.Category.AllEmployees);
        return true;
      }

      public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item)
      {
        IEmployeeAdapter employee = GetEmployee(behaviour.Npc);
        var destination = FindShelfForDelivery(behaviour.Npc, item);
        if (destination == null)
        {
          RouteQueueManager.NoDestinationCache[employee.AssignedProperty].Add(item);
          return false;
        }

        var inventorySlot = behaviour.Npc.Inventory.ItemSlots
            .FirstOrDefault(s => s?.ItemInstance != null && s.ItemInstance.CanStackWith(item) && s.Quantity > 0);
        if (inventorySlot == null) return false;

        ITransitEntity transitEntity = destination;
        var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, behaviour.Npc.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0) return false;

        int quantity = Math.Min(inventorySlot.Quantity, transitEntity.GetInputCapacityForItem(item, behaviour.Npc));
        if (quantity <= 0) return false;

        var request = new TransferRequest(item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, deliverySlots);
        state.ActiveRoutes.Add(new PrioritizedRoute(request, EmployeeBehaviour.PRIORITY_SHELF_RESTOCK));
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandlerAdapter.HandleInventoryItem: Added route for {quantity} of {item.ID}",
            DebugLogger.Category.AllEmployees);
        return true;
      }
    }
  }
}