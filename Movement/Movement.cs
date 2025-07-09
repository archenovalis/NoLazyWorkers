using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using static NoLazyWorkers.Employees.Extensions;
using ScheduleOne.DevUtilities;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using NoLazyWorkers.Extensions;
using UnityEngine.Pool;

namespace NoLazyWorkers.Movement
{
  public static class Utilities
  {
    /// <summary>
    /// Asynchronously executes a sequence of transfer requests for an employee, handling item movement.
    /// </summary>
    /// <returns>A tuple indicating success and an error message if applicable.</returns>
    public static async Task<(bool success, string error)> TransitAsync(Employee employee, EmployeeData state, TaskDescriptor task, List<TransferRequest> requests)
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
        var routes = requests.Select(r => new PrioritizedRoute(r, task.Priority)).ToList();

        state.AdvBehaviour.StartMovement(routes, task.Type, (emp, s, status) =>
        {
          var result = status == Status.Success
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

    /// <summary>
    /// Asynchronously moves an employee to a transit entity's access point using FishNet's TimeManager.
    /// </summary>
    /// <param name="employee">The employee to move.</param>
    /// <param name="transitEntity">The target transit entity.</param>
    /// <returns>True if movement succeeds, false if it fails or times out.</returns>
    /// 
    public static async Task<bool> MoveToAsync(Employee employee, ITransitEntity transitEntity)
    {
      if (employee == null || transitEntity == null)
      {
        Log(Level.Error,
            $"MoveTo: Invalid employee={employee?.fullName ?? "null"} or transitEntity={transitEntity.GUID.ToString() ?? "null"}",
            Category.Movement);
        return false;
      }

      var accessPoint = NavMeshUtility.GetAccessPoint(transitEntity, employee);
      if (accessPoint == null)
      {
        Log(Level.Warning,
            $"MoveTo: No access point for transitEntity={transitEntity.GUID} for {employee.fullName}",
            Category.Movement);
        return false;
      }

      try
      {
        employee.Movement.SetDestination(accessPoint.position);
        double startTick = TimeManagerInstance.Tick;
        double timeoutTick = startTick + TimeManagerInstance.TimeToTicks(30.0f);

        while (TimeManagerInstance.Tick < timeoutTick)
        {
          if (!employee.Movement.IsMoving)
          {
            if (NavMeshUtility.IsAtTransitEntity(transitEntity, employee))
            {
              Log(Level.Info,
                  $"MoveTo: {employee.fullName} reached {transitEntity.GUID}",
                  Category.Movement);
              return true;
            }
            Log(Level.Warning,
                $"MoveTo: {employee.fullName} stopped moving but not at {transitEntity.GUID}",
                Category.Movement);
            return false;
          }
          await AwaitNextFishNetTickAsync();
        }

        Log(Level.Warning,
            $"MoveTo: Timeout for {employee.fullName} to reach {transitEntity.GUID}",
            Category.Movement);
        return false;
      }
      catch (Exception ex)
      {
        Log(Level.Error,
            $"MoveTo: Error for {employee.fullName} - {ex.Message}",
            Category.Movement);
        return false;
      }
    }
  }

  public static class Extensions
  {
    public class TransferRequest
    {
      public Employee Employee { get; private set; }
      public ItemInstance Item { get; private set; }
      public int Quantity { get; private set; }
      public ItemSlot InventorySlot { get; private set; }
      public ITransitEntity PickUp { get; private set; }
      public List<ItemSlot> PickupSlots { get; private set; }
      public ITransitEntity DropOff { get; private set; }
      public List<ItemSlot> DropOffSlots { get; private set; }

      private static readonly ObjectPool<TransferRequest> _pool = PoolUtility.InitializeObjectPool<TransferRequest>(
          createFunc: () => new TransferRequest(),
          actionOnGet: null,
          actionOnRelease: request =>
          {
            request.Employee = null;
            request.Item = null;
            request.Quantity = 0;
            request.InventorySlot = null;
            request.PickUp = null;
            request.PickupSlots?.Clear();
            request.DropOff = null;
            request.DropOffSlots?.Clear();
          },
          actionOnDestroy: null,
          defaultCapacity: 16,
          maxSize: 32,
          poolName: "TransferRequest_Pool"
      );

      private static bool _isInitialized;

      /// <summary>
      /// Initializes the TransferRequest pool.
      /// </summary>
      public static void Initialize()
      {
        if (_isInitialized) return;
        _isInitialized = true;
        Log(Level.Info, "TransferRequest pool initialized", Category.Movement);
      }

      /// <summary>
      /// Retrieves or creates a transfer request.
      /// </summary>
      /// <param name="employee">The employee handling the request.</param>
      /// <param name="item">The item to transfer.</param>
      /// <param name="quantity">The quantity to transfer.</param>
      /// <param name="inventorySlot">The employee's inventory slot.</param>
      /// <param name="pickup">The pickup entity.</param>
      /// <param name="pickupSlots">The pickup slots.</param>
      /// <param name="dropOff">The drop-off entity.</param>
      /// <param name="dropOffSlots">The drop-off slots.</param>
      /// <returns>A configured TransferRequest.</returns>
      public static TransferRequest Get(Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot,
          ITransitEntity pickup, List<ItemSlot> pickupSlots, ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        var request = _pool.Get();
        request.Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        request.Item = item ?? throw new ArgumentNullException(nameof(item));
        request.Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        request.InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        request.PickUp = pickup;
        request.PickupSlots = pickupSlots ?? new List<ItemSlot>();
        request.DropOff = dropOff ?? throw new ArgumentNullException(nameof(dropOff));
        request.DropOffSlots = dropOffSlots ?? new List<ItemSlot>();

        if (request.PickUp == null && request.PickupSlots.Count == 0)
          throw new ArgumentException("No valid pickup slots");

        foreach (var slot in request.PickupSlots.Where(s => s != null))
          SlotService.ReserveSlot(request.PickUp.GUID, slot, employee.NetworkObject, "pickup", item, quantity);
        foreach (var slot in request.DropOffSlots.Where(s => s != null))
          SlotService.ReserveSlot(request.DropOff.GUID, slot, employee.NetworkObject, "dropoff", item, quantity);
        SlotService.ReserveSlot(employee.GUID, request.InventorySlot, employee.NetworkObject, "inventory", request.Item, request.Quantity);
#if DEBUG
        Log(Level.Verbose, $"Retrieved TransferRequest for employee {employee.GUID}, item {item.ID}, quantity {quantity}", Category.Movement);
#endif
        return request;
      }

      /// <summary>
      /// Releases a TransferRequest back to the pool.
      /// </summary>
      /// <param name="request">The request to release.</param>
      public static void Release(TransferRequest request)
      {
        if (request == null) return;
        _pool.Release(request);
#if DEBUG
        Log(Level.Verbose, "Released TransferRequest to pool", Category.Movement);
#endif
      }

      /// <summary>
      /// Cleans up the TransferRequest pool.
      /// </summary>
      public static void Cleanup()
      {
        PoolUtility.DisposeObjectPool(_pool, "TransferRequest_Pool");
        _isInitialized = false;
        Log(Level.Info, "TransferRequest pool cleaned up", Category.Movement);
      }
    }

    /// <summary>
    /// Represents a prioritized movement route for item transfer, including transit details and priority.
    /// </summary>
    public struct PrioritizedRoute
    {
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public ITransitEntity PickUp;
      public List<ItemSlot> PickupSlots;
      public ITransitEntity DropOff;
      public List<ItemSlot> DropoffSlots;
      public int Priority;
      public AdvancedTransitRoute TransitRoute;

      public PrioritizedRoute(TransferRequest request, int priority)
      {
        Item = request.Item ?? throw new ArgumentNullException(nameof(request.Item));
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot ?? throw new ArgumentNullException(nameof(request.InventorySlot));
        PickUp = request.PickUp;
        PickupSlots = request.PickupSlots;
        DropOff = request.DropOff ?? throw new ArgumentNullException(nameof(request.DropOff));
        DropoffSlots = request.DropOffSlots ?? throw new ArgumentNullException(nameof(request.DropOffSlots));
        Priority = priority;
        TransitRoute = new AdvancedTransitRoute(request.PickUp, request.DropOff);
      }
    }
  }
}