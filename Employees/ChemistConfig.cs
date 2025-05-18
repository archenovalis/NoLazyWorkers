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
using NoLazyWorkers.Structures;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;

namespace NoLazyWorkers.Employees
{
  public static class ChemistExtensions
  {
    public class IChemistAdapter(Chemist chemist) : IEmployeeAdapter
    {
      private readonly Chemist _chemist = chemist;

      public Property AssignedProperty => _chemist.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Chemist;

      public bool CustomizePlanning(Behaviour behaviour, StateData state)
      {
        if (state.Station.OutputSlot.Quantity > 0)
        {
          var item = state.Station.OutputSlot.ItemInstance;
          var destination = FindShelfForDelivery(behaviour.Npc, item, false) ??
                            FindPackagingStation(behaviour.Npc, item);
          if (destination == null)
            return false;

          var slots = destination.ReserveInputSlotsForItem(item, behaviour.Npc.NetworkObject);
          var request = new TransferRequest(item, state.Station.OutputSlot.Quantity,
              behaviour.Npc.Inventory.ItemSlots.Find(s => s.ItemInstance == null),
              (ITransitEntity)state.Station, new List<ItemSlot> { state.Station.OutputSlot }, destination, slots);
          state.ActiveRoutes.Add(new PrioritizedRoute(request, EmployeeBehaviour.PRIORITY_STATION_REFILL));
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistAdapter.CustomizePlanning: Planned output delivery for {item.ID}",
              DebugLogger.Category.AllEmployees);
          return true;
        }

        var inputItem = state.Station.GetInputItemForProduct()?.FirstOrDefault()?.SelectedItem;
        if (inputItem == null) return false;

        state.TargetItem = inputItem.GetDefaultInstance();
        state.QuantityNeeded = state.Station.StartThreshold - state.Station.GetInputQuantity();
        state.QuantityInventory = behaviour.Npc.Inventory._GetItemAmount(state.TargetItem.ID);
        return true;
      }

      public bool HandleGrabbing(Behaviour behaviour, StateData state)
      {
        var route = state.ActiveRoutes.FirstOrDefault();
        if (route.PickupLocation == state.Station && state.Station.OutputSlot.Quantity > 0)
        {
          var slot = state.Station.OutputSlot;
          slot.ApplyLocks(behaviour.Npc, "Chemist output grab");
          state.QuantityInventory = slot.Quantity;
          slot.ChangeQuantity(-state.QuantityInventory);
          slot.RemoveLock();
          behaviour.Npc.Inventory.InsertItem(state.TargetItem.GetCopy(state.QuantityInventory));
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistAdapter.HandleGrabbing: Grabbed {state.QuantityInventory} of {state.TargetItem.ID}",
              DebugLogger.Category.AllEmployees);
          return true;
        }
        return false;
      }
    }
  }
}