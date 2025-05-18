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
      public EmployeeBehaviour EmployeeBehaviour(Behaviour behaviour) => ActiveBehaviours[behaviour.Npc.GUID];
      public EmployeeBehaviour EmployeeBehaviour(NPC npc) => ActiveBehaviours[npc.GUID];

      public PackagerAdapter(Packager packager)
      {
        _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      }

      public bool HandlePlanning(Behaviour behaviour, StateData state)
      {
        var requests = FindItemsNeedingMovement(_packager, state);
        if (requests.Count == 0) return false;
        EmployeeBehaviour(behaviour).AddRoutes(behaviour, state, requests);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.CustomizePlanning: Planned {requests.Count} routes", DebugLogger.Category.AllEmployees);
        return true;
      }

      public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item)
      {
        var destination = FindShelfForDelivery(behaviour.Npc, item);
        if (destination == null)
        {
          NoDestinationCache[AssignedProperty].Add(item);
          return false;
        }

        var inventorySlot = behaviour.Npc.Inventory.ItemSlots
            .FirstOrDefault(s => s?.ItemInstance != null && s.ItemInstance.CanStackWith(item) && s.Quantity > 0);
        if (inventorySlot == null) return false;

        var transitEntity = destination as ITransitEntity;
        var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, behaviour.Npc.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0) return false;

        int quantity = Math.Min(inventorySlot.Quantity, transitEntity.GetInputCapacityForItem(item, behaviour.Npc));
        if (quantity <= 0) return false;

        var request = new TransferRequest(item, quantity, inventorySlot, null, new List<ItemSlot> { inventorySlot }, destination, deliverySlots);
        state.ActiveRoutes.Add(new PrioritizedRoute(request, Employees.EmployeeBehaviour.PRIORITY_SHELF_RESTOCK));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter.HandleInventoryItem: Added route for {quantity} of {item.ID}", DebugLogger.Category.AllEmployees);
        return true;
      }

      private List<TransferRequest> FindItemsNeedingMovement(Packager npc, StateData state)
      {
        var requests = new List<TransferRequest>();
        var property = AssignedProperty;
        if (property == null) return requests;

        int maxRoutes = Mathf.Min(Employees.EmployeeBehaviour.MAX_ROUTES_PER_CYCLE, npc.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        var pickupGroups = new Dictionary<ITransitEntity, List<TransferRequest>>();

        // Station Refill
        if (PropertyStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations)
          {
            if (requests.Count >= maxRoutes) break;
            if (station.IsInUse || station.HasActiveOperation) continue;

            var items = station.RefillList();
            foreach (var item in items)
            {
              var shelves = FindShelvesWithItem(npc, item, 1);
              var source = shelves.Keys.FirstOrDefault(s => EmployeeUtilities.GetOutputSlotsContainingTemplateItem(s, item).Count > 0);
              if (source == null) continue;

              var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(source, item);
              if (sourceSlots.Count == 0) continue;

              sourceSlots.ApplyLocks(npc, "Route planning lock");
              var destination = station.TransitEntity;
              var deliverySlots = destination.ReserveInputSlotsForItem(item, npc.NetworkObject);
              if (deliverySlots == null || deliverySlots.Count == 0)
              {
                sourceSlots.RemoveLock();
                continue;
              }

              int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
              if (quantity <= 0)
              {
                sourceSlots.RemoveLock();
                continue;
              }

              if (!npc.Movement.CanGetTo(station.GetAccessPoint(npc)))
              {
                sourceSlots.RemoveLock();
                continue;
              }

              var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), source, sourceSlots, destination, deliverySlots);
              if (!pickupGroups.ContainsKey(source)) pickupGroups[source] = new List<TransferRequest>();
              pickupGroups[source].Add(request);
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: StationRefill request for {quantity} of {item.ID}", DebugLogger.Category.AllEmployees);
              break;
            }
          }
        }

        // Loading Dock
        foreach (var dock in property.LoadingDocks)
        {
          if (requests.Count >= maxRoutes) break;
          if (!dock.IsInUse) continue;

          foreach (var slot in dock.OutputSlots)
          {
            if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
            var item = slot.ItemInstance;
            var sourceSlots = EmployeeUtilities.GetOutputSlotsContainingTemplateItem(dock, item);
            if (sourceSlots.Count == 0) continue;

            sourceSlots.ApplyLocks(npc, "Route planning lock");
            var destination = FindShelfForDelivery(npc, item);
            if (destination == null)
            {
              sourceSlots.RemoveLock();
              continue;
            }

            var transitEntity = destination as ITransitEntity;
            var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }

            int quantity = Mathf.Min(sourceSlots.Sum(s => s.Quantity), transitEntity.GetInputCapacityForItem(item, npc));
            if (quantity <= 0)
            {
              sourceSlots.RemoveLock();
              continue;
            }

            if (!npc.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, npc).position))
            {
              sourceSlots.RemoveLock();
              continue;
            }

            var request = new TransferRequest(item, quantity, npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null), dock, sourceSlots, destination, deliverySlots);
            if (!pickupGroups.ContainsKey(dock)) pickupGroups[dock] = new List<TransferRequest>();
            pickupGroups[dock].Add(request);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: LoadingDock request for {quantity} of {item.ID}", DebugLogger.Category.AllEmployees);
          }
        }

        // Shelf Restock
        foreach (var shelf in StorageExtensions.AnyShelves)
        {
          if (requests.Count >= maxRoutes) break;
          if (shelf?.OutputSlots == null) continue;

          foreach (var slot in shelf.OutputSlots)
          {
            if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked) continue;
            var item = slot.ItemInstance;
            if (NoDestinationCache.TryGetValue(shelf.ParentProperty, out var cache) && cache.Any(i => i.CanStackWith(item, false)))
              continue;

            slot.ApplyLocks(npc, "Route planning lock");
            var assignedShelf = FindShelfForDelivery(npc, item, false);
            if (assignedShelf == null || assignedShelf == shelf)
            {
              slot.RemoveLock();
              continue;
            }
            var transitEntity = assignedShelf as ITransitEntity;
            var deliverySlots = transitEntity.ReserveInputSlotsForItem(item, npc.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
            {
              slot.RemoveLock();
              continue;
            }

            int quantity = Mathf.Min(slot.Quantity, transitEntity.GetInputCapacityForItem(item, npc));
            if (quantity <= 0)
            {
              slot.RemoveLock();
              continue;
            }

            var inventorySlot = npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
            {
              slot.RemoveLock();
              continue;
            }

            var request = new TransferRequest(item, quantity, inventorySlot, shelf, new List<ItemSlot> { slot }, assignedShelf, deliverySlots);
            if (!pickupGroups.ContainsKey(shelf)) pickupGroups[shelf] = new List<TransferRequest>();
            pickupGroups[shelf].Add(request);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindItemsNeedingMovement: ShelfRestock request for {quantity} of {item.ID}", DebugLogger.Category.AllEmployees);
          }
        }

        var employeeBehaviour = EmployeeBehaviour(npc);
        foreach (var group in pickupGroups.OrderByDescending(g => g.Value.Max(r => employeeBehaviour.GetPriority(r))))
        {
          requests.AddRange(group.Value.OrderByDescending(r => employeeBehaviour.GetPriority(r)).Take(maxRoutes));
          break;
        }

        foreach (var slot in pickupGroups.SelectMany(g => g.Value).SelectMany(r => r.PickupSlots).Distinct())
        {
          if (!requests.Any(r => r.PickupSlots.Contains(slot)))
            slot.RemoveLock();
        }

        return requests;
      }
    }
  }
}