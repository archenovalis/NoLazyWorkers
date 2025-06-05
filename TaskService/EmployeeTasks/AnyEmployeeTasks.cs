using ScheduleOne.Employees;
using static NoLazyWorkers.Employees.Extensions;
using ScheduleOne.Property;
using NoLazyWorkers.Employees;
using Unity.Burst;
using static NoLazyWorkers.TaskService.Extensions;
using Unity.Collections;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Movement.Utilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using FishNet;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.TaskService.TaskRegistry;
using UnityEngine;

namespace NoLazyWorkers.TaskService.EmployeeTasks
{
  public static class AnyEmployeeTasks
  {
    public static List<ITaskDefinition> Register()
    {
      return new List<ITaskDefinition>
            {
                new DeliverInventoryTaskDef()
            };
    }

    /// <summary>
    /// Defines the DeliverInventory task, which is employee-initiated and delivers items from inventory to storage.
    /// </summary>
    public class DeliverInventoryTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.DeliverInventory;
      public int Priority => 40;
      public EmployeeTypes EmployeeType => EmployeeTypes.Any;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.Inventory;
      public TransitTypes DropoffType => TransitTypes.PlaceableStorageEntity;
      public IEntitySelector EntitySelector { get; } = new EmployeeEntitySelector();
      public ITaskValidator Validator { get; } = new DeliverInventoryValidator();
      public ITaskExecutor Executor { get; } = new DeliverInventoryExecutor();
    }

    /// <summary>
    /// Selects employee entities for task validation.
    /// </summary>
    public class EmployeeEntitySelector : IEntitySelector
    {
      public NativeList<EntityKey> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<EntityKey>(allocator);
        foreach (var employee in property.Employees)
          entities.Add(new EntityKey { Guid = employee.GUID, Type = TransitTypes.Inventory });
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Selected {entities.Length} employee entities for property {property.name}", DebugLogger.Category.AnyEmployee);
        return entities;
      }
    }

    /// <summary>
    /// Validates DeliverInventory tasks using Burst compilation.
    /// </summary>
    [BurstCompile]
    public class DeliverInventoryValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        var employee = property.Employees.FirstOrDefault(e => e.GUID == entityKey.Guid);
        if (employee == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverInventoryValidator: No employee found for GUID {entityKey.Guid}", DebugLogger.Category.AnyEmployee);
          return;
        }

        // Process inventory slots
        foreach (var slot in employee.Inventory.ItemSlots)
        {
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked || Utilities.IsItemTimedOut(property, slot.ItemInstance))
            continue;

          var destination = FindStorageForDelivery(employee, slot.ItemInstance, true);
          if (destination == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverInventoryValidator: No destination for item {slot.ItemInstance.ID} for employee {employee.fullName}", DebugLogger.Category.AnyEmployee);
            continue;
          }

          var deliverySlots = destination.InputSlots
              .Where(s => s.ItemInstance == null || slot.ItemInstance.AdvCanStackWith(s.ItemInstance))
              .Take(3)
              .ToList();
          if (deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverInventoryValidator: No valid delivery slots for item {slot.ItemInstance.ID}", DebugLogger.Category.AnyEmployee);
            continue;
          }

          var slotKey = new SlotKey(destination.GUID, deliverySlots[0].SlotIndex);
          if (context.ReservedSlots.ContainsKey(slotKey))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverInventoryValidator: Slot {slotKey} already reserved", DebugLogger.Category.AnyEmployee);
            continue;
          }

          var task = TaskDescriptor.Create(
              definition.Type,
              definition.EmployeeType,
              definition.Priority,
              context.AssignedPropertyName.ToString(),
              new ItemKey(slot.ItemInstance),
              slot.Quantity,
              definition.PickupType,
              Guid.Empty,
              new[] { slot.SlotIndex },
              definition.DropoffType,
              destination.GUID,
              deliverySlots.Select(s => s.SlotIndex).ToArray(),
              context.CurrentTime,
              employee.GUID,
              isFollowUp: true
          );

          validTasks.Add(task);
          context.ReservedSlots.Add(slotKey, new SlotReservation { TaskId = task.TaskId, Timestamp = context.CurrentTime });
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryValidator: Created task {task.TaskId} for item {slot.ItemInstance.ID} to {destination.GUID}", DebugLogger.Category.AnyEmployee);
        }
      }
    }

    /// <summary>
    /// Executes DeliverInventory tasks asynchronously.
    /// </summary>
    public class DeliverInventoryExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryExecutor: Starting task {task.TaskId} for {employee.fullName}", DebugLogger.Category.AnyEmployee);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(employee, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverInventoryExecutor: Task {task.TaskId} failed revalidation for {employee.fullName}", DebugLogger.Category.AnyEmployee);
            return;
          }

          state.EmployeeState.TaskContext = new TaskContext { Task = task };
          var routes = new List<PrioritizedRoute>();
          const int batchSize = 2; // Process up to 5 slots per frame

          // Batch process inventory slots
          var validSlots = employee.Inventory.ItemSlots
              .Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked && !Utilities.IsItemTimedOut(employee.AssignedProperty, s.ItemInstance))
              .ToList();

          for (int i = 0; i < validSlots.Count; i += batchSize)
          {
            var batch = validSlots.GetRange(i, Math.Min(batchSize, validSlots.Count - i));
            foreach (var slot in batch)
            {
              var destination = FindStorageForDelivery(employee, slot.ItemInstance, true);
              if (destination == null)
              {
                Utilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverInventoryExecutor: No destination for item {slot.ItemInstance.ID}", DebugLogger.Category.AnyEmployee);
                continue;
              }

              var deliverySlots = destination.InputSlots
                  .Where(s => s.ItemInstance == null || slot.ItemInstance.AdvCanStackWith(s.ItemInstance))
                  .Take(3)
                  .ToList();
              if (deliverySlots.Count == 0)
              {
                Utilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverInventoryExecutor: No valid delivery slots for item {slot.ItemInstance.ID}", DebugLogger.Category.AnyEmployee);
                continue;
              }

              int quantity = Math.Min(slot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(slot.ItemInstance)));
              if (quantity <= 0)
              {
                (destination as ITransitEntity).RemoveSlotLocks(employee.NetworkObject);
                Utilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
                DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverInventoryExecutor: Invalid quantity {quantity} for item {slot.ItemInstance.ID}", DebugLogger.Category.AnyEmployee);
                continue;
              }

              slot.ApplyLock(employee.NetworkObject, "pickup");
              foreach (var deliverySlot in deliverySlots)
              {
                var slotKey = new SlotKey(destination.GUID, deliverySlot.SlotIndex);
                if (!SlotManager.ReserveSlot(slotKey, task.TaskId, Time.time))
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverInventoryExecutor: Failed to reserve slot {slotKey} for task {task.TaskId}", DebugLogger.Category.AnyEmployee);
                  continue;
                }
                deliverySlot.ApplyLock(employee.NetworkObject, "dropoff");
                Utilities.SetReservedSlot(employee, deliverySlot);
              }

              var request = TransferRequest.Get(employee, slot.ItemInstance, quantity, slot, null, new List<ItemSlot> { slot }, destination, deliverySlots);
              routes.Add(Utilities.CreatePrioritizedRoute(request, task.Priority));
              Utilities.SetReservedSlot(employee, slot);
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverInventoryExecutor: Added route for {quantity} of {slot.ItemInstance.ID} to {destination.GUID}", DebugLogger.Category.AnyEmployee);
            }

            await InstanceFinder.TimeManager.AwaitNextTickAsync();
          }

          if (routes.Any())
          {
            state.EmployeeState.TaskContext.Requests = routes.Select(r => TransferRequest.Get(employee, r.Item, r.Quantity, r.InventorySlot, r.PickUp, r.PickupSlots, r.DropOff, r.DropoffSlots)).ToList();
            var movementResult = await TransitAsync(employee, state, task, state.EmployeeState.TaskContext.Requests);
            if (movementResult.Success)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryExecutor: Successfully executed task {task.TaskId} for {employee.fullName}", DebugLogger.Category.AnyEmployee);
            }
            else
            {
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverInventoryExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.AnyEmployee);
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryExecutor: No valid routes for task {task.TaskId}", DebugLogger.Category.AnyEmployee);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverInventoryExecutor: Exception for task {task.TaskId}, employee {employee.fullName} - {ex}", DebugLogger.Category.AnyEmployee);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(employee);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryExecutor: Completed task {task.TaskId} for {employee.fullName}", DebugLogger.Category.AnyEmployee);
        }
      }
    }
  }
}