using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Storage.ManagedDictionaries;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using NoLazyWorkers.Storage;
using Unity.Burst;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using NoLazyWorkers.Performance;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.Stations;
using NoLazyWorkers.Extensions;
using FishNet.Object;

namespace NoLazyWorkers.TaskService.StationTasks
{
  [EntityTask]
  public class MixingStationTask : BaseTask<Empty, Empty>
  {
    private readonly TaskService _taskService;
    private readonly Property _property;
    private readonly Action<int, int, int[], List<Empty>> _setupDelegate;
    private readonly Action<NativeList<Guid>> _selectEntitiesDelegate;
    private readonly Action<int, int, Guid[], List<Empty>> _validationSetupDelegate;
    private readonly Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> _validateEntityDelegate;
    private readonly Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> _createTaskDelegate;

    public override TaskName Type => TaskName.MixingStation;
    public override StorageType[] SupportedEntityTypes => new[] { StorageType.Station };

    public MixingStationTask()
    {
      _taskService = TaskServiceManager.GetOrCreateService(ManagedDictionaries.IdToProperty[0]);
      _property = ManagedDictionaries.IdToProperty[0];
      var builder = new TaskBuilder<Empty, Empty>(Type)
          .WithSetup((start, count, inputs, outputs) => { })
          .WithSelectEntities(output => TaskUtilities.SelectEntitiesByType<IStationAdapter>(_property, output))
          .WithValidationSetup((start, count, inputs, outputs) => { })
          .WithValidateEntity((index, guids, outputs) => MixingStationLogic.ValidateEntityState(_property, guids[index], outputs))
          .WithCreateTask(CreateTaskDelegateImpl);
      _setupDelegate = builder.SetupDelegate;
      _selectEntitiesDelegate = builder.SelectEntitiesDelegate;
      _validationSetupDelegate = builder.ValidationSetupDelegate;
      _validateEntityDelegate = builder.ValidateEntityDelegate;
      _createTaskDelegate = builder.CreateTaskDelegate;
    }

    public override Action<int, int, int[], List<Empty>> SetupDelegate => _setupDelegate;
    public override Action<NativeList<Guid>> SelectEntitiesDelegate => _selectEntitiesDelegate;
    public override Action<int, int, Guid[], List<Empty>> ValidationSetupDelegate => _validationSetupDelegate;
    public override Action<int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate => _validateEntityDelegate;
    public override Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate => _createTaskDelegate;

    public override bool IsValidState(int state)
    {
      return state >= 0 && state <= (int)States.NeedsDelivery;
    }

    private void CreateTaskDelegateImpl(int index, NativeArray<ValidationResultData> results, NativeList<TaskResult> outputs)
    {
      var result = results[index];
      if (!result.IsValid || result.State == (int)States.Invalid || result.State == (int)States.Idle)
      {
#if DEBUG
        DeferredLog(Level.Verbose, $"Invalid or idle state for station {result.EntityGuid}", Category.MixingStation);
#endif
        return;
      }

      var station = ManagedDictionaries.IStations[_property][result.EntityGuid];
      if (station == null || station.TransitEntity == null)
      {
#if DEBUG
        DeferredLog(Level.Error, $"Invalid station {result.EntityGuid} or no TransitEntity", Category.MixingStation);
#endif
        return;
      }

      var logs = GetLogPool().Get();
      try
      {
        var taskData = CreateTaskData(station, result);
        if (taskData == null)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"Failed to create task for station {station.GUID}, state {result.State}", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          return;
        }

        var task = TaskDescriptor.Create(
            station.GUID, Type, result.State - 1, TaskEmployeeType.Chemist, 100,
            _property.name, result.Item, result.Quantity,
            taskData.SourceGuid, taskData.SourceSlotIndices,
            taskData.DestGuid, taskData.DestSlotIndices,
            Time.time, logs
        );

        if (task.TaskId != Guid.Empty)
          outputs.Add(new TaskResult(task, true));
#if DEBUG
        logs.Add(new LogEntry { Message = $"Created task {task.TaskId} for station {station.GUID}, state {result.State}", Level = Level.Info, Category = Category.MixingStation });
#endif
      }
      finally
      {
        ProcessLogsAsync(logs).GetAwaiter().GetResult();
        logs.Dispose();
      }
    }

    private (Guid SourceGuid, int[] SourceSlotIndices, Guid DestGuid, int[] DestSlotIndices)? CreateTaskData(IStationAdapter station, ValidationResultData result)
    {
      Guid sourceGuid = Guid.Empty;
      int[] sourceSlotIndices = null;
      Guid destGuid = Guid.Empty;
      int[] destSlotIndices = null;

      if (result.State == (int)States.NeedsRestock)
      {
        var requiredItems = PoolUtility.GetPooled<NativeList<ItemData>>(Allocator.TempJob);
        try
        {
          var mixerItemField = MixingStationUtilities.GetInputItemForProductSlot(station);
          ItemData mixerItem = mixerItemField?.SelectedItem != null ? new ItemData(mixerItemField.SelectedItem.GetDefaultInstance()) : ItemData.Empty;

          if (mixerItem.Id != "")
            requiredItems.Add(mixerItem);
          else
          {
            var refills = station.RefillList();
            foreach (var refill in refills)
              if (refill != null)
                requiredItems.Add(new ItemData(refill));
          }

          bool success = false;
          foreach (var itemKey in requiredItems)
          {
            var itemInstance = itemKey.CreateItemInstance();
            int threshold = station.StartThreshold;
            int desiredQty = Math.Min(station.MaxProductQuantity, station.ProductSlots[0].Quantity);
            int invQty = ManagedDictionaries.IEmployees[_property][station.GUID]?.Inventory.GetIdenticalItemAmount(itemInstance) ?? 0;
            int inputQty = station.InsertSlots[0].Quantity;
            int quantityNeeded = Math.Max(0, threshold - inputQty - invQty);
            int quantityWanted = Math.Max(0, desiredQty - inputQty - invQty);
            int finalQuantity = Math.Max(quantityNeeded, quantityWanted);

            var runner = CoroutineRunner.Instance;
            var routine = runner.RunCoroutineWithResult(
                StorageManager.FindStorageWithItem(_property, itemInstance, finalQuantity, true),
                result =>
                {
                  if (result != null && result.Shelf != null)
                  {
                    sourceGuid = result.Shelf.GUID;
                    sourceSlotIndices = result.ItemSlots.Select(s => s.SlotIndex).ToArray();
                    destGuid = station.GUID;
                    result.Quantity = Math.Min(result.AvailableQuantity, finalQuantity);
                    success = true;
                  }
                }
            );
            routine.MoveNext();
            if (success) break;
          }

          if (!success)
          {
            var runner = CoroutineRunner.Instance;
            runner.RunCoroutine(_taskService.AddDisabledEntity<Empty, Empty>(
                station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.MissingItem,
                requiredItems, true, _property, Type
            ));
            return null;
          }
        }
        finally
        {
          requiredItems.Dispose();
        }
      }
      else if (result.State == (int)States.NeedsDelivery)
      {
        var runner = CoroutineRunner.Instance;
        var destinations = PoolUtility.GetPooled<NativeList<(Guid, NativeList<int>, int)>>(Allocator.TempJob);
        try
        {
          var routine = runner.RunCoroutineWithResult(
              StorageManager.FindDeliveryDestination(_property, result.Item.CreateItemInstance(), result.Quantity, station.GUID),
              result =>
              {
                foreach (var dest in result)
                {
                  var slotIndices = new NativeList<int>(Allocator.Temp);
                  foreach (var slot in dest.ItemSlots)
                    slotIndices.Add(slot.SlotIndex);
                  destinations.Add((dest.Entity.GUID, slotIndices, dest.Capacity));
                }
              }
          );
          routine.MoveNext();

          if (destinations.Length > 0)
          {
            var (dest, destSlots, capacity) = destinations[0];
            sourceGuid = station.GUID;
            sourceSlotIndices = new[] { station.OutputSlot.SlotIndex };
            destGuid = dest;
            destSlotIndices = destSlots.ToArray();
            result.DestinationCapacity = capacity;
            destSlots.Dispose();
          }
          else
          {
            var requiredItems = PoolUtility.GetPooled<NativeList<ItemData>>(Allocator.TempJob);
            try
            {
              requiredItems.Add(result.Item);
              runner.RunCoroutine(_taskService.AddDisabledEntity<Empty, Empty>(
                  station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.NoDestination,
                  requiredItems, true, _property, Type
              ));
            }
            finally
            {
              requiredItems.Dispose();
            }
            return null;
          }
        }
        finally
        {
          destinations.Dispose();
        }
      }
      else if (result.State == (int)States.HasOutput)
      {
        sourceGuid = station.GUID;
        sourceSlotIndices = new[] { station.OutputSlot.SlotIndex };
        destGuid = station.GUID;
        var loopSlots = PoolUtility.GetPooled<NativeList<int>>(Allocator.TempJob);
        try
        {
          var runner = CoroutineRunner.Instance;
          var routine = runner.RunCoroutineWithResult(
              StorageManager.FindAvailableSlots(_property, station.GUID, station.ProductSlots.ToList(), result.Item.CreateItemInstance(), result.Quantity),
              result =>
              {
                foreach (var (slot, _) in result)
                  if (!slot.IsLocked)
                    loopSlots.Add(slot.SlotIndex);
              }
          );
          routine.MoveNext();
          destSlotIndices = loopSlots.Length > 0 ? loopSlots.ToArray().Take(3).ToArray() : null;
        }
        finally
        {
          loopSlots.Dispose();
        }
      }

      if (sourceSlotIndices == null || destSlotIndices == null)
        return null;

      return (sourceGuid, sourceSlotIndices, destGuid, destSlotIndices);
    }

    public override IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
    {
      if (employee.Type != Enum.Parse<EEmployeeType>(TaskEmployeeType.Chemist.ToString()))
      {
#if DEBUG
        Log(Level.Error, $"Employee {employee.fullName} is not a Chemist", Category.MixingStation);
#endif
        yield break;
      }

      var station = ManagedDictionaries.IStations[_property][task.EntityGuid];
      if (station == null || station.TransitEntity == null)
      {
#if DEBUG
        Log(Level.Error, $"Invalid station {task.EntityGuid} or no TransitEntity", Category.MixingStation);
#endif
        yield break;
      }

      switch (task.ActionId)
      {
        case 0: // NeedsRestock
          yield return HandleRestockAsync(station, employee, task);
          yield return ValidateAndOperateAsync(station, employee, task);
          break;
        case 1: // ReadyToOperate
          yield return HandleOperateAsync(station, employee, task);
          break;
        case 2: // HasOutput
          yield return HandleLoopAsync(station, employee, task);
          break;
        case 3: // NeedsDelivery
          yield return HandleDeliveryAsync(station, employee, task);
          break;
        default:
#if DEBUG
          Log(Level.Error, $"Invalid action ID {task.ActionId} for task {task.TaskId}", Category.MixingStation);
#endif
          break;
      }
#if DEBUG
      Log(Level.Info, $"Transitioned to action {task.ActionId} for task {task.TaskId}, employee {employee.fullName}", Category.MixingStation);
#endif
    }

    private IEnumerator HandleRestockAsync(IStationAdapter station, Employee employee, TaskDescriptor task)
    {
      yield return ExecuteActionAsync(station, employee, task, RestockSlots, maxRetries: 3);
    }

    private IEnumerator HandleOperateAsync(IStationAdapter station, Employee employee, TaskDescriptor task)
    {
      yield return OperateAsync(station, employee, task);
    }

    private IEnumerator HandleLoopAsync(IStationAdapter station, Employee employee, TaskDescriptor task)
    {
      yield return ExecuteActionAsync(station, employee, task, LoopSlots, maxRetries: 3);
      yield return FollowUpCoroutine(employee, task, SmartExecutionOptions.Default);
    }

    private IEnumerator HandleDeliveryAsync(IStationAdapter station, Employee employee, TaskDescriptor task)
    {
      yield return ExecuteActionAsync(station, employee, task, DeliverSlots, maxRetries: 3);
    }

    private IEnumerator ValidateAndOperateAsync(IStationAdapter station, Employee employee, TaskDescriptor task)
    {
      var logs = GetLogPool().Get();
      List<(ItemSlot, int)>? cachedSlots = null;
      try
      {
        var runner = CoroutineRunner.Instance;
        var routine = runner.RunCoroutineWithResult(
            StorageManager.FindAvailableSlots(_property, station.GUID, station.AllSlots.ToList(), ItemData.Empty.CreateItemInstance(), 0),
            result => cachedSlots = result
        );
        routine.MoveNext();

        if (cachedSlots == null)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"No slot data for station {station.GUID} during validation", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          yield break;
        }

        var result = MixingStationLogic.ValidateEntityState(_property, station.GUID, logs, employee, cachedSlots);
        if (result.State == (int)States.ReadyToOperate)
          yield return OperateAsync(station, employee, task);

        runner.RunCoroutine(StorageManager.UpdateStorageCache(_property, station.GUID, cachedSlots.Select(s => s.Item1).ToList(), StorageType.Station));
      }
      finally
      {
        ProcessLogsAsync(logs).GetAwaiter().GetResult();
        logs.Dispose();
      }
    }

    private IEnumerator ExecuteActionAsync(
        IStationAdapter station,
        Employee employee,
        TaskDescriptor task,
        Func<IStationAdapter, ITransitEntity, TaskDescriptor, List<ItemSlot>> slotResolver,
        int maxRetries)
    {
      var pickupEntity = ManagedDictionaries.IStations[_property].ContainsKey(task.PickupGuid)
          ? ManagedDictionaries.IStations[_property][task.PickupGuid]
          : ManagedDictionaries.Storages[_property][task.PickupGuid];
      if (pickupEntity == null)
      {
#if DEBUG
        Log(Level.Error, $"Invalid pickup entity {task.PickupGuid}", Category.MixingStation);
#endif
        yield break;
      }

      var deliveryEntity = ManagedDictionaries.IStations[_property].ContainsKey(task.DropoffGuid)
          ? ManagedDictionaries.IStations[_property][task.DropoffGuid]
          : ManagedDictionaries.Storages[_property][task.DropoffGuid];
      if (deliveryEntity == null)
      {
#if DEBUG
        Log(Level.Error, $"Invalid delivery entity {task.DropoffGuid}", Category.MixingStation);
#endif
        yield break;
      }

      var pickupSlots = slotResolver(station, pickupEntity, task);
      var deliverySlots = slotResolver(station, deliveryEntity, task);
      if (!pickupSlots.Any() || !deliverySlots.Any())
      {
#if DEBUG
        Log(Level.Warning, $"Invalid slots for station {station.GUID}", Category.MixingStation);
#endif
        yield break;
      }

      var inventorySlot = employee.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || s.ItemInstance.AdvCanStackWith(task.Item.CreateItemInstance()));
      if (inventorySlot == null)
      {
#if DEBUG
        Log(Level.Warning, $"No inventory slot for employee {employee.fullName}", Category.MixingStation);
#endif
        yield break;
      }

      bool success = false;
      int retries = 0;
      var operations = new List<(Guid, ItemSlot, ItemInstance, int, bool, NetworkObject, string)>();
      while (!success && retries < maxRetries)
      {
        operations.Clear();
        foreach (var slot in pickupSlots)
          if (StorageManager.ReserveSlot(pickupEntity.GUID, slot, employee.NetworkObject, "pickup"))
            operations.Add((pickupEntity.GUID, slot, task.Item.CreateItemInstance(), task.Quantity, false, employee.NetworkObject, "pickup"));
        foreach (var slot in deliverySlots)
          if (StorageManager.ReserveSlot(deliveryEntity.GUID, slot, employee.NetworkObject, "delivery"))
            operations.Add((deliveryEntity.GUID, slot, task.Item.CreateItemInstance(), task.Quantity, true, employee.NetworkObject, "delivery"));

        if (operations.Count != pickupSlots.Count + deliverySlots.Count)
        {
          foreach (var op in operations)
            StorageManager.ReleaseSlot(op.Item2);
          retries++;
          if (retries < maxRetries)
          {
#if DEBUG
            Log(Level.Warning, $"Retry {retries} for task {task.TaskId} on station {station.GUID}", Category.MixingStation);
#endif
            yield return new WaitForSeconds(1f);
          }
          continue;
        }

        var runner = CoroutineRunner.Instance;
        var routine = runner.RunCoroutineWithResult(
            StorageManager.ExecuteSlotOperations(_property, operations),
            results =>
            {
              success = results.All(r => r);
              if (success)
              {
                runner.RunCoroutine(StorageManager.UpdateStorageCache(_property, pickupEntity.GUID, pickupSlots, pickupEntity is IStationAdapter ? StorageType.Station : StorageType.AnyShelf));
                runner.RunCoroutine(StorageManager.UpdateStorageCache(_property, deliveryEntity.GUID, deliverySlots, deliveryEntity is IStationAdapter ? StorageType.Station : StorageType.AnyShelf));
#if DEBUG
                Log(Level.Info, $"Completed action for task {task.TaskId} on station {station.GUID}", Category.MixingStation);
#endif
              }
            }
        );
        routine.MoveNext();

        if (!success)
        {
          foreach (var op in operations)
            StorageManager.ReleaseSlot(op.Item2);
          retries++;
          if (retries < maxRetries)
          {
#if DEBUG
            Log(Level.Warning, $"Retry {retries} for task {task.TaskId} on station {station.GUID}", Category.MixingStation);
#endif
            yield return new WaitForSeconds(1f);
          }
        }
      }

      if (!success)
      {
#if DEBUG
        Log(Level.Error, $"Failed action for task {task.TaskId} after {maxRetries} retries", Category.MixingStation);
#endif
      }
      else
      {
        _taskService.CompleteTask(task);
      }
    }

    public override IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
    {
      if (!employee.IsAvailable())
      {
#if DEBUG
        Log(Level.Verbose, $"Employee {employee.fullName} unavailable for follow-up task {task.TaskId}", Category.MixingStation);
#endif
        yield break;
      }

      var station = ManagedDictionaries.IStations[_property][task.EntityGuid];
      if (station == null || station.TransitEntity == null)
      {
#if DEBUG
        Log(Level.Error, $"Invalid station {task.EntityGuid} for follow-up", Category.MixingStation);
#endif
        yield break;
      }

      var logs = GetLogPool().Get();
      List<(ItemSlot, int)>? cachedSlots = null;
      try
      {
        var runner = CoroutineRunner.Instance;
        var routine = runner.RunCoroutineWithResult(
            StorageManager.FindAvailableSlots(_property, station.GUID, station.AllSlots.ToList(), ItemData.Empty.CreateItemInstance(), 0),
            result => cachedSlots = result
        );
        routine.MoveNext();

        if (cachedSlots == null)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"No slot data for station {station.GUID}", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          yield break;
        }

        var result = MixingStationLogic.ValidateEntityState(_property, station.GUID, logs, employee, cachedSlots);
        if (result.State != (int)States.NeedsRestock && result.State != (int)States.ReadyToOperate)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"Station {station.GUID} not in restock or operate state after looping", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          yield break;
        }

        var createTaskRoutine = _taskService.CreateTaskAsync(_property, Type, employee.GUID, station.GUID);
        yield return createTaskRoutine;

        var newTask = _taskService.GetEmployeeSpecificTask(employee.GUID);
        if (newTask.TaskId != Guid.Empty)
        {
          yield return ExecuteCoroutine(employee, newTask, options);
          newTask.Dispose();
        }
#if DEBUG
        logs.Add(new LogEntry { Message = $"Completed follow-up for task {task.TaskId}, new task {newTask.TaskId}", Level = Level.Info, Category = Category.MixingStation });
#endif
      }
      finally
      {
        ProcessLogsAsync(logs).GetAwaiter().GetResult();
        logs.Dispose();
      }
    }

    // Unchanged methods: OperateAsync, RestockSlots, LoopSlots, DeliverSlots
    // Note: These methods are unchanged as they align with StorageManager and TaskService requirements.

    public static class MixingStationLogic
    {
      [BurstCompile]
      public static ValidationResultData ValidateEntityState(Property property, Guid stationGuid, NativeList<LogEntry> logs, Employee employee = null, List<(ItemSlot, int)> slots = null)
      {
        var result = new ValidationResultData { IsValid = false, State = (int)States.Invalid };
        var station = ManagedDictionaries.IStations[property][stationGuid];
        if (station.IsInUse)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"Station {station.GUID} is in use", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          return result;
        }

        ItemData outputItem = ItemData.Empty;
        int outputQuantity = 0;
        foreach (var (slot, capacity) in slots)
        {
          if (slot.SlotIndex == station.OutputSlot.SlotIndex && slot.Quantity > 0 && !slot.IsLocked)
          {
            outputItem = new ItemData(slot.ItemInstance);
            outputQuantity = slot.Quantity;
            break;
          }
        }

        if (outputItem.Id != "")
        {
          int loopCount = 0;
          foreach (var (slot, capacity) in slots)
          {
            if (station.ProductSlots.Any(ps => ps.SlotIndex == slot.SlotIndex) && !slot.IsLocked)
            {
              if (capacity > 0 && (slot.ItemInstance == null || slot.ItemInstance.AdvCanStackWith(outputItem.CreateItemInstance())))
                loopCount++;
            }
          }

          if (loopCount > 0)
          {
            result = new ValidationResultData
            {
              IsValid = true,
              State = (int)States.HasOutput,
              Item = outputItem,
              Quantity = outputQuantity
            };
            return result;
          }

          result = new ValidationResultData
          {
            IsValid = true,
            State = (int)States.NeedsDelivery,
            Item = outputItem,
            Quantity = outputQuantity
          };
          return result;
        }

        ItemData restockItem = ItemData.Empty;
        int restockQuantity = 0;
        int threshold = station.StartThreshold;
        int desiredQty = Math.Min(station.MaxProductQuantity, station.ProductSlots[0].Quantity);
        int invQty = employee != null ? employee.Inventory.GetIdenticalItemAmount(restockItem.CreateItemInstance()) : 0;
        foreach (var (slot, capacity) in slots)
        {
          if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex))
          {
            if (capacity > 0)
            {
              restockItem = slot.ItemInstance != null ? new ItemData(slot.ItemInstance) : new ItemData(station.InsertSlots.First(isl => isl.SlotIndex == slot.SlotIndex).ItemInstance);
              restockQuantity = Math.Max(0, Math.Max(threshold, desiredQty) - slot.Quantity - invQty);
              break;
            }
          }
        }

        if (restockItem.Id != "")
        {
          result = new ValidationResultData
          {
            IsValid = true,
            State = (int)States.NeedsRestock,
            Item = restockItem,
            Quantity = restockQuantity
          };
          return result;
        }

        bool canOperate = true;
        foreach (var (slot, _) in slots)
        {
          if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex) && slot.Quantity < threshold)
          {
            canOperate = false;
            break;
          }
        }

        if (canOperate)
        {
          result = new ValidationResultData
          {
            IsValid = true,
            State = (int)States.ReadyToOperate,
            Item = ItemData.Empty,
            Quantity = 0
          };
          return result;
        }

        result = new ValidationResultData { IsValid = true, State = (int)States.Idle };
        return result;
      }
    }

    private enum States
    {
      Invalid = 0,
      NeedsRestock = 1,
      ReadyToOperate = 2,
      HasOutput = 3,
      NeedsDelivery = 4,
      Idle = 5
    }
  }
}