using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Employees.EmployeeExtensions;

namespace NoLazyWorkers.Employees.Tasks
{
  public static class DeliverInventoryTask
  {
    // Enum for DeliverInventory steps
    public enum DeliverInventorySteps
    {
      CheckDelivery, // Validate inventory slots
      Deliver,      // Execute delivery
      End           // Cleanup and disable
    }

    // Creates a type-safe DeliverInventory task
    public static IEmployeeTask Create(Employee employee, int priority)
    {
      var workSteps = new List<WorkStep<DeliverInventorySteps>>
            {
                new() {
                    Step = DeliverInventorySteps.CheckDelivery,
                    Validate = Logic.ValidateDelivery,
                    Execute = async (emp, state) => state.EmployeeState.CurrentWorkStep = DeliverInventorySteps.Deliver,
                    Transitions = { { "Success", DeliverInventorySteps.Deliver } }
                },
                new() {
                    Step = DeliverInventorySteps.Deliver,
                    Validate = Logic.ValidateDelivery,
                    Execute = Logic.ExecuteDelivery,
                    Transitions = { { "Success", DeliverInventorySteps.End } }
                },
                new() {
                    Step = DeliverInventorySteps.End,
                    Validate = async (emp, state) => true,
                    Execute = Logic.ExecuteEnd,
                    Transitions = { }
                }
            };
      return new EmployeeTask<DeliverInventorySteps>(employee, "DeliverInventory", priority, workSteps);
    }

    public static class Logic
    {
      // Validates if there are items to deliver
      public static async Task<bool> ValidateDelivery(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateDelivery: Entered for employee={employee?.fullName}", DebugLogger.Category.AnyEmployee);
        if (employee == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "ValidateDelivery: Employee is null", DebugLogger.Category.AnyEmployee);
          return false;
        }
        // Initialize task context
        state.EmployeeState.TaskContext = new TaskContext();
        // Check for valid inventory slots
        bool hasValidSlot = employee.Inventory.ItemSlots.Any(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked && !EmployeeUtilities.IsItemTimedOut(employee.AssignedProperty, s.ItemInstance));
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateDelivery: Has valid slot={hasValidSlot} for {employee.fullName}", DebugLogger.Category.AnyEmployee);
        return hasValidSlot;
      }

      // Executes delivery for all valid inventory slots
      public static async Task ExecuteDelivery(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ExecuteDelivery: Entered for employee={employee?.fullName}", DebugLogger.Category.AnyEmployee);
        if (employee == null || state.EmployeeState.TaskContext == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ExecuteDelivery: Invalid employee or context", DebugLogger.Category.AnyEmployee);
          state.EmployeeState.CurrentWorkStep = DeliverInventorySteps.End;
          return;
        }

        var routes = new List<PrioritizedRoute>();
        var context = state.EmployeeState.TaskContext;

        foreach (var slot in employee.Inventory.ItemSlots.Where(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked))
        {
          if (EmployeeUtilities.IsItemTimedOut(employee.AssignedProperty, slot.ItemInstance))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ExecuteDelivery: Skipping timed-out item {slot.ItemInstance.ID}", DebugLogger.Category.AnyEmployee);
            continue;
          }

          var destination = FindStorageForDelivery(employee, slot.ItemInstance, true);
          if (destination == null)
          {
            EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteDelivery: No destination for {slot.ItemInstance.ID}, timed out", DebugLogger.Category.AnyEmployee);
            continue;
          }

          var destTransit = destination as ITransitEntity;
          var deliverySlots = destination.InputSlots.AdvReserveInputSlotsForItem(slot.ItemInstance, employee.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ExecuteDelivery: No slots at {destination.GUID}", DebugLogger.Category.AnyEmployee);
            continue;
          }

          int quantity = Math.Min(slot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(slot.ItemInstance)));
          if (quantity <= 0)
          {
            destTransit.RemoveSlotLocks(employee.NetworkObject);
            EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ExecuteDelivery: Invalid quantity {quantity}", DebugLogger.Category.AnyEmployee);
            continue;
          }

          slot.ApplyLock(employee.NetworkObject, "pickup");

          var request = TransferRequest.Get(employee, slot.ItemInstance, quantity, slot, null, new List<ItemSlot> { slot }, destination, deliverySlots);
          routes.Add(EmployeeUtilities.CreatePrioritizedRoute(request, 40));
          EmployeeUtilities.SetReservedSlot(employee, slot);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteDelivery: Route for {quantity} of {slot.ItemInstance.ID} to {destination.GUID}", DebugLogger.Category.AnyEmployee);
        }

        if (!routes.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteDelivery: No routes, ending", DebugLogger.Category.AnyEmployee);
          state.EmployeeState.CurrentWorkStep = DeliverInventorySteps.End;
          return;
        }

        context.Requests = routes.Select(r => TransferRequest.Get(employee, r.Item, r.Quantity, r.InventorySlot, r.PickUp, r.PickupSlots, r.DropOff, r.DropoffSlots)).ToList();
        state.EmployeeBeh.StartMovement(routes, DeliverInventorySteps.End);
      }

      // Cleans up and disables the behavior
      public static async Task ExecuteEnd(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteEnd: Cleaning up for {employee.fullName}", DebugLogger.Category.AnyEmployee);
        if (state.EmployeeState.TaskContext?.MovementStatus == Status.Failure)
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ExecuteEnd: Movement failed for {employee.fullName}", DebugLogger.Category.AnyEmployee);
        state.EmployeeState.TaskContext?.Cleanup(employee);
        await state.EmployeeBeh.Disable();
      }
    }
  }
}