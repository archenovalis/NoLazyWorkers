
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
using FishNet.Managing.Timing;
using NoLazyWorkers.TaskService;
using static NoLazyWorkers.Movement.Extensions;

namespace NoLazyWorkers.Movement
{
  public static class Utilities
  {
    public static async Task<(bool Success, string Error)> TransitAsync(Employee employee, EmployeeStateData state, TaskDescriptor task, List<TransferRequest> requests)
    {
      if (!requests.Any())
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MoveAsync: No valid requests for {employee.fullName}", DebugLogger.Category.MixingStation);
        return (false, "No valid requests");
      }

      var tcs = new TaskCompletionSource<(bool Success, string Error)>();
      var routes = requests.Select(r => new PrioritizedRoute(r, task.Priority)).ToList();

      state.EmployeeBeh.StartMovement(routes, task.Type, (emp, s, status) =>
      {
        var result = status == Status.Success
              ? (true, "Success")
              : (false, $"Movement failed with status {status}");
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MoveAsync: Movement completed with status={status} for {emp.fullName}", DebugLogger.Category.MixingStation);
        tcs.SetResult(result);
      });

      return await tcs.Task;
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
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MovementUtilities.MoveToAsync: Invalid employee={employee?.fullName ?? "null"} or transitEntity={transitEntity?.GUID.ToString() ?? "null"}",
            DebugLogger.Category.EmployeeCore);
        return false;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"MovementUtilities.MoveToAsync: Moving {employee.fullName} to transitEntity={transitEntity.GUID}",
          DebugLogger.Category.EmployeeCore);

      var accessPoint = NavMeshUtility.GetAccessPoint(transitEntity, employee);
      if (accessPoint == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MovementUtilities.MoveToAsync: No access point for transitEntity={transitEntity.GUID} for {employee.fullName}",
            DebugLogger.Category.EmployeeCore);
        return false;
      }

      try
      {
        employee.Movement.SetDestination(accessPoint.position);
        var timeManager = InstanceFinder.TimeManager;
        double startTick = timeManager.Tick;
        double startTimeoutTick = startTick + timeManager.TimeToTicks(10.0f);
        bool startedMoving = false;

        while (timeManager.Tick < startTimeoutTick)
        {
          if (employee.Movement.IsMoving)
          {
            startedMoving = true;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"MovementUtilities.MoveToAsync: Started moving for {employee.fullName}",
                DebugLogger.Category.EmployeeCore);
            break;
          }
          await timeManager.AwaitNextTickAsync();
        }

        if (!startedMoving)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MovementUtilities.MoveToAsync: Timeout: Failed to start moving for {employee.fullName}",
              DebugLogger.Category.EmployeeCore);
          return false;
        }

        double moveTimeoutTick = startTick + timeManager.TimeToTicks(30.0f);
        while (employee.Movement.IsMoving && timeManager.Tick < moveTimeoutTick)
        {
          if (NavMeshUtility.IsAtTransitEntity(transitEntity, employee))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MovementUtilities.MoveToAsync: {employee.fullName} reached transitEntity={transitEntity.GUID}",
                DebugLogger.Category.EmployeeCore);
            return true;
          }
          await timeManager.AwaitNextTickAsync();
        }

        if (employee.Movement.IsMoving)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"MovementUtilities.MoveToAsync: Timeout: Movement did not complete for {employee.fullName}",
              DebugLogger.Category.EmployeeCore);
          return false;
        }

        if (NavMeshUtility.IsAtTransitEntity(transitEntity, employee))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MovementUtilities.MoveToAsync: {employee.fullName} successfully reached transitEntity={transitEntity.GUID}",
              DebugLogger.Category.EmployeeCore);
          return true;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MovementUtilities.MoveToAsync: {employee.fullName} did not reach transitEntity={transitEntity.GUID}",
            DebugLogger.Category.EmployeeCore);
        return false;
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MovementUtilities.MoveToAsync: Exception for {employee.fullName} - {ex.Message}",
            DebugLogger.Category.EmployeeCore);
        return false;
      }
    }

    //TODO: not implemented? add to MoveToAsync?
    public static async Task<bool> MoveToRetryAsync(Func<Task<(bool Success, string Error)>> action, int maxRetries = 3, float delaySeconds = 1f)
    {
      var timeManager = InstanceFinder.TimeManager;
      for (int i = 0; i < maxRetries; i++)
      {
        try
        {
          var result = await action();
          if (result.Success) return true;
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"Retry {i + 1}/{maxRetries}: {result.Error}", DebugLogger.Category.TaskManager);
          await timeManager.AwaitNextTickAsync(delaySeconds * Mathf.Pow(2, i));
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"Retry {i + 1}/{maxRetries}: {ex}", DebugLogger.Category.TaskManager);
          await timeManager.AwaitNextTickAsync(delaySeconds * Mathf.Pow(2, i));
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"Failed after {maxRetries} retries", DebugLogger.Category.TaskManager);
      return false;
    }
  }

  public static class Extensions
  {
    public class TransferRequest
    {
      private static readonly Stack<TransferRequest> Pool = new();
      public Employee Employee { get; private set; }
      public ItemInstance Item { get; private set; }
      public int Quantity { get; private set; }
      public ItemSlot InventorySlot { get; private set; }
      public ITransitEntity PickUp { get; private set; }
      public List<ItemSlot> PickupSlots { get; private set; }
      public ITransitEntity DropOff { get; private set; }
      public List<ItemSlot> DropOffSlots { get; private set; }

      private TransferRequest(Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickup, List<ItemSlot> pickupSlots,
          ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        // Initialize transfer request with validation
        Employee = employee ?? throw new ArgumentNullException(nameof(employee));
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        PickUp = pickup;
        DropOff = dropOff ?? throw new ArgumentNullException(nameof(dropOff));
        PickupSlots = pickupSlots?.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList() ?? new List<ItemSlot>();
        DropOffSlots = dropOffSlots?.Where(slot => slot != null).ToList() ?? throw new ArgumentNullException(nameof(dropOffSlots));
        if (PickUp == null && PickupSlots.Count == 0)
          throw new ArgumentException("No valid pickup slots for inventory route");
        if (DropOffSlots.Count == 0)
          throw new ArgumentException("No valid delivery slots");
      }

      // Retrieves or creates a transfer request
      public static TransferRequest Get(Employee employee, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickup, List<ItemSlot> pickupSlots,
          ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        if (Pool.Count > 0)
        {
          var request = Pool.Pop();
          request.Employee = employee;
          request.Item = item;
          request.Quantity = quantity;
          request.InventorySlot = inventorySlot;
          request.PickUp = pickup;
          request.PickupSlots = pickupSlots?.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList() ?? new List<ItemSlot>();
          request.DropOff = dropOff;
          request.DropOffSlots = dropOffSlots?.Where(slot => slot != null).ToList() ?? new List<ItemSlot>();
          return request;
        }
        return new TransferRequest(employee, item, quantity, inventorySlot, pickup, pickupSlots, dropOff, dropOffSlots);
      }

      // Releases a transfer request back to the pool
      public static void Release(TransferRequest request)
      {
        if (request == null)
          return;
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
    }

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