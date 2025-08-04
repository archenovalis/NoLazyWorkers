using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using static NoLazyWorkers.Employees.Extensions;
using ScheduleOne.DevUtilities;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using NoLazyWorkers.Extensions;
using UnityEngine.Pool;
using NoLazyWorkers.Storage;
using System.Collections;

namespace NoLazyWorkers.Movement
{
  public static class Utilities
  {
    /// <summary>
    /// Asynchronously executes a sequence of transfer requests for an employee, handling item movement.
    /// </summary>
    /// <returns>A tuple indicating success and an error message if applicable.</returns>
    public static async Task<(bool success, string error)> TransitAsync(Employee employee, EmployeeInfo state, TaskDescriptor task, List<TransferRequest> requests)
    {
      if (!requests.Any())
      {
        Log(Level.Warning,
            $"Transit: No valid requests for {employee.fullName}",
            Category.Movement);
        return (false, "No valid requests");
      }

      try
      {
        var tcs = new TaskCompletionSource<(bool success, string error)>();

        state.AdvBehaviour.StartMovement(requests, (emp, s, status) =>
        {
          var result = status == MovementStatus.Success
              ? (true, "Success")
              : (false, $"Movement failed: {status}");
          Log(Level.Info,
              $"Transit: Movement completed with status={status} for {emp.fullName}",
              Category.Movement);
          tcs.TrySetResult(result);
        });

        return await tcs.Task;
      }
      catch (Exception ex)
      {
        Log(Level.Error,
            $"Transit: Failed for {employee.fullName} - {ex.Message}",
            Category.Movement);
        return (false, ex.Message);
      }
    }
  }

  public static class Extensions
  {
    public class TransferRequest
    {
      public Employee Employee;
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public ITransitEntity PickUp;
      public List<ItemSlot> PickupSlots;
      public ITransitEntity DropOff;
      public List<ItemSlot> DropOffSlots;
      public int Priority;
      public AdvancedTransitRoute TransitRoute;

      private static readonly ObjectPool<TransferRequest> _pool = PoolUtility.InitializeObjectPool<TransferRequest>(
          createFunc: () => new TransferRequest(),
          actionOnGet: null,
          actionOnRelease: request =>
          {
            request.Employee = null;
            request.Item = default;
            request.Quantity = 0;
            request.InventorySlot = null;
            request.PickUp = null;
            request.DropOff = null;
            request.Priority = 0;
            request.TransitRoute = default;
          },
          actionOnDestroy: null,
          defaultCapacity: 16,
          maxSize: 32,
          poolName: "TransferRequest_Pool"
      );

      public static TransferRequest Get(
          Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot,
          ITransitEntity pickup, List<ItemSlot> pickupSlots, ITransitEntity dropOff, List<ItemSlot> dropOffSlots,
          int priority = 50)
      {
        var request = _pool.Get();
        request.Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        request.Item = item;
        request.Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        request.InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        request.PickUp = pickup;
        request.PickupSlots = pickupSlots ?? [];
        request.DropOff = dropOff ?? throw new ArgumentNullException(nameof(dropOff));
        request.DropOffSlots = dropOffSlots ?? [];
        request.Priority = priority;
        request.TransitRoute = new AdvancedTransitRoute(pickup, dropOff);

        if (request.PickUp == null && request.PickupSlots.Count == 0)
          throw new ArgumentException("No valid pickup slots");

        foreach (var slot in request.PickupSlots)
          SlotService.ReserveSlot(request.PickUp.GUID, slot, employee.NetworkObject, "pickup", item, quantity);
        foreach (var slot in request.DropOffSlots)
          SlotService.ReserveSlot(request.DropOff.GUID, slot, employee.NetworkObject, "dropoff", item, quantity);
        SlotService.ReserveSlot(employee.GUID, request.InventorySlot, employee.NetworkObject, "inventory", item, quantity);

        Log(Level.Verbose, $"Retrieved TransferRequest for employee {employee.GUID}, item {item.ID}, quantity {quantity}", Category.Movement);
        return request;
      }

      public static void Release(TransferRequest request)
      {
        if (!request.IsCreated) return;
        _pool.Release(request);
        Log(Level.Verbose, "Released TransferRequest to pool", Category.Movement);
      }

      public static void Cleanup()
      {
        PoolUtility.DisposeObjectPool(_pool, "TransferRequest_Pool");
        Log(Level.Info, "TransferRequest pool cleaned up", Category.Movement);
      }

      public bool IsCreated => Employee != null;
    }
  }
}