
using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Movement.Utilities;
using System.Collections;
using UnityEngine;
using ScheduleOne.DevUtilities;
using MelonLoader;
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.TimeManagerExtensions;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;

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
      private static readonly Stack<TransferRequest> Pool = new(16);
      public Employee Employee { get; private set; }
      public ItemInstance Item { get; private set; }
      public int Quantity { get; private set; }
      public ItemSlot InventorySlot { get; private set; }
      public ITransitEntity PickUp { get; private set; }
      public List<ItemSlot> PickupSlots { get; private set; }
      public ITransitEntity DropOff { get; private set; }
      public List<ItemSlot> DropOffSlots { get; private set; }

      private static bool _isInitialized;
      public static void Initialize()
      {
        if (_isInitialized) return;
        for (int i = 0; i < 32; i++)
          Pool.Push(new TransferRequest());
        _isInitialized = true;
        Log(Level.Info, "TransferRequest pool initialized", Category.Movement);
      }

      private TransferRequest() { }

      /// <summary>
      /// Retrieves or creates a transfer request
      /// </summary>
      public static TransferRequest Get(Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot,
              ITransitEntity pickup, List<ItemSlot> pickupSlots, ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        var request = Pool.Count > 0 ? Pool.Pop() : new TransferRequest();
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

        // Reserve slots using SlotManager
        foreach (var slot in request.PickupSlots.Where(s => s != null))
          SlotService.ReserveSlot(request.PickUp.GUID, slot, employee.NetworkObject, "pickup", item, quantity);
        foreach (var slot in request.DropOffSlots.Where(s => s != null))
          SlotService.ReserveSlot(request.DropOff.GUID, slot, employee.NetworkObject, "dropoff", item, quantity);
        SlotService.ReserveSlot(employee.GUID, request.InventorySlot, employee.NetworkObject, "inventory", request.Item, request.Quantity);
        return request;
      }

      public static void Release(TransferRequest request)
      {
        if (request == null) return;
        request.Employee = null;
        request.Item = null;
        request.Quantity = 0;
        request.InventorySlot = null;
        request.PickUp = null;
        request.PickupSlots?.Clear();
        request.DropOff = null;
        request.DropOffSlots?.Clear();
        Pool.Push(request);
      }

      public static void Cleanup()
      {
        Pool.Clear();
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