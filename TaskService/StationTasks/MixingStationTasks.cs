using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.Constants;
using static NoLazyWorkers.Storage.Jobs;
using static NoLazyWorkers.Employees.Utilities;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using NoLazyWorkers.Storage;
using Unity.Burst;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.TimeManagerExtensions;
using NoLazyWorkers.Metrics;
using static NoLazyWorkers.NoLazyUtilities;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using Unity.Jobs;
using NoLazyWorkers.JobService;
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.TaskService.StationTasks
{
  public class MixingStationTask : ITask
  {
    private TaskService _taskService;
    private CacheManager _cacheManager;
    private Property _property;

    public enum States
    {
      Invalid,
      NeedsRestock,
      ReadyToOperate,
      HasOutput,
      NeedsDelivery,
      Idle
    }

    [BurstCompile]
    public struct MixingStationValidationJob : IJobParallelFor
    {
      [ReadOnly] public NativeList<Guid> EntityGuids;
      public NativeArray<ValidationResultData> Results;
      public NativeList<LogEntry> Logs;
      [ReadOnly] public NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntities;
      [ReadOnly] public CacheManager CacheManager;
      [ReadOnly] public TaskService TaskService;

      public void Execute(int index)
      {
        var entityGuid = EntityGuids[index];
        var entity = TaskService.ResolveEntity(entityGuid) as IStationAdapter;
        if (entity == null)
        {
          Logs.Add(new LogEntry { Message = $"Entity {entityGuid} not found", Level = Level.Verbose, Category = Category.MixingStation });
          Results[index] = new ValidationResultData { IsValid = false, State = (int)States.Invalid };
          return;
        }

        var storageKey = new StorageKey(entityGuid, StorageTypes.Station);
        if (!CacheManager.TryGetStationSlots(storageKey, out var slots))
        {
          Logs.Add(new LogEntry { Message = $"No slot data for entity {entityGuid}", Level = Level.Verbose, Category = Category.MixingStation });
          Results[index] = new ValidationResultData { IsValid = false, State = (int)States.Invalid };
          return;
        }

        var result = MixingStationLogic.ValidateEntityState(entity, slots, Logs);
        if (result.IsValid && DisabledEntities.ContainsKey(entityGuid))
        {
          if (DisabledEntities.TryGetValue(entityGuid, out var disabledData) && disabledData.ActionId == result.State - 1)
          {
            result.IsValid = false;
            Logs.Add(new LogEntry { Message = $"Entity {entityGuid} disabled for action {result.State - 1}", Level = Level.Verbose, Category = Category.MixingStation });
          }
        }
        Results[index] = result;
        slots.Dispose();
      }
    }

    public TaskTypes Type => TaskTypes.MixingStation;

    public IEntitySelector EntitySelector => new MixingStationEntitySelector();

    public class MixingStationEntitySelector : IEntitySelector
    {
      [BurstCompile]
      public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<Guid>(allocator);
        if (IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values)
            if (station.TypeOf == typeof(MixingStation) || station.TypeOf == typeof(MixingStationMk2))
              entities.Add(station.GUID);
        }
        Log(Level.Verbose, $"Selected {entities.Length} mixing stations for {property.name}", Category.MixingStation);
        return entities;
      }
    }

    public JobHandle ScheduleValidationJob(
      NativeList<Guid> entityGuids,
      NativeArray<ValidationResultData> results,
      NativeList<LogEntry> logs,
      Property property,
      TaskService taskService,
      CacheManager cacheManager,
      DisabledEntityService disabledService)
    {
      _taskService = taskService;
      _cacheManager = cacheManager;
      _property = property;
      var job = new MixingStationValidationJob
      {
        EntityGuids = entityGuids,
        Results = results,
        Logs = logs,
        CacheManager = cacheManager,
        DisabledEntities = disabledService.disabledEntities,
        TaskService = taskService
      };
      return job.Schedule(entityGuids.Length, JobScheduler.GetDynamicBatchSize(entityGuids.Length, 0.15f, nameof(MixingStationValidationJob)));
    }

    public async Task ValidateEntityStateAsync(object entity)
    {
      await Performance.TrackExecutionAsync(nameof(ValidateEntityStateAsync), async () =>
      {
        if (entity is not IStationAdapter station)
        {
          Log(Level.Error, "Invalid station entity", Category.MixingStation);
          return;
        }

        var storageKey = new StorageKey(station.GUID, StorageTypes.Station);
        if (!_cacheManager.TryGetStationSlots(storageKey, out var slots))
        {
          Log(Level.Error, $"No slot data for station {station.GUID}", Category.MixingStation);
          return;
        }

        var logs = new NativeList<LogEntry>(Allocator.TempJob);
        try
        {
          var result = MixingStationLogic.ValidateEntityState(station, slots, logs);
          ProcessLogs(logs);
        }
        finally
        {
          logs.Dispose();
          slots.Dispose();
        }
      });
    }

    public IEnumerator CreateTaskForState(object entity, Property property, ValidationResultData result, TaskDispatcher dispatcher, DisabledEntityService disabledService, NativeList<LogEntry> logs)
    {
      if (entity is not IStationAdapter station)
      {
        logs.Add(new LogEntry { Message = $"Invalid entity {entity?.GetType()}", Level = Level.Error, Category = Category.MixingStation });
        yield break;
      }

      if (!result.IsValid || result.State == (int)States.Invalid || result.State == (int)States.Idle)
      {
        logs.Add(new LogEntry { Message = $"Invalid or idle state for station {station.GUID}", Level = Level.Verbose, Category = Category.MixingStation });
        yield break;
      }

      TaskDescriptor task = default;
      Guid sourceGuid = Guid.Empty;
      int[] sourceSlotIndices = null;
      Guid destGuid = Guid.Empty;
      int[] destSlotIndices = null;

      if (result.State == (int)States.NeedsRestock)
      {
        var requiredItems = new NativeList<ItemKey>(Allocator.TempJob);
        var mixerItemField = MixingStationUtilities.GetInputItemForProductSlot(station);
        ItemKey mixerItem = mixerItemField?.SelectedItem != null ? new ItemKey(mixerItemField.SelectedItem.GetDefaultInstance()) : ItemKey.Empty;
        if (mixerItem.Id != "")
        {
          requiredItems.Add(mixerItem);
        }
        else
        {
          var refills = station.RefillList();
          foreach (var refill in refills)
            if (refill != null)
              requiredItems.Add(new ItemKey(refill));
        }

        bool success = false;
        foreach (var itemKey in requiredItems)
        {
          var routine = Performance.TrackExecutionCoroutine(nameof(CreateTaskForState) + "_NeedsRestock", FindStorageWithItemAsync(property, itemKey, result.Quantity));
          yield return routine;
          (success, var kvp, var slotIndices) = routine.result;
          if (success)
          {
            sourceGuid = kvp.Shelf.GUID;
            sourceSlotIndices = slotIndices.ToArray();
            destGuid = station.GUID;
            result.Quantity = kvp.Quantity;
            result.Item = requiredItems[0];
            slotIndices.Dispose();
            break;
          }
        }
        if (!success)
        {
          disabledService.AddDisabledEntity(station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.MissingItem, requiredItems);
          logs.Add(new LogEntry { Message = $"No shelf found for items [{string.Join(", ", requiredItems.Select(i => i.Id))}] for station {station.GUID}", Level = Level.Verbose, Category = Category.MixingStation });
          requiredItems.Dispose();
          yield break;
        }
        requiredItems.Dispose();
      }
      else if (result.State == (int)States.NeedsDelivery)
      {
        var destinations = new NativeList<(Guid, NativeList<int>, int)>(Allocator.TempJob);
        if (_cacheManager.TryGetDeliveryDestinations(property, result.Item, result.Quantity, station.GUID, out destinations))
        {
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
            var requiredItems = new NativeList<ItemKey>(1, Allocator.TempJob);
            requiredItems.Add(result.Item);
            disabledService.AddDisabledEntity(station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.NoDestination, requiredItems);
            logs.Add(new LogEntry { Message = $"No destination found for item {result.Item.Id} for station {station.GUID}", Level = Level.Verbose, Category = Category.MixingStation });
            requiredItems.Dispose();
            destinations.Dispose();
            yield break;
          }
        }
        else
        {
          var requiredItems = new NativeList<ItemKey>(1, Allocator.TempJob);
          requiredItems.Add(result.Item);
          disabledService.AddDisabledEntity(station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.NoDestination, requiredItems);
          logs.Add(new LogEntry { Message = $"No destination found for item {result.Item.Id} for station {station.GUID}", Level = Level.Verbose, Category = Category.MixingStation });
          requiredItems.Dispose();
          destinations.Dispose();
          yield break;
        }
        destinations.Dispose();
      }
      else if (result.State == (int)States.HasOutput)
      {
        sourceGuid = station.GUID;
        sourceSlotIndices = new[] { station.OutputSlot.SlotIndex };
        destGuid = station.GUID;
        var loopSlots = new List<int>();
        foreach (var slot in station.ProductSlots)
          if (!slot.IsLocked && (slot.ItemInstance == null || slot.ItemInstance.AdvCanStackWith(result.Item.CreateItemInstance())))
            loopSlots.Add(slot.SlotIndex);
        destSlotIndices = loopSlots.Take(3).ToArray();
      }

      task = MixingStationLogic.CreateTaskDescriptor(
          station.GUID, TaskTypes.MixingStation, result.State - 1, EmployeeTypes.Chemist, 100,
          property.name, result.Item, result.Quantity,
          sourceGuid, sourceSlotIndices,
          destGuid, destSlotIndices,
          Time.time
      );

      if (task.TaskId != Guid.Empty)
      {
        dispatcher.Enqueue(task);
        logs.Add(new LogEntry { Message = $"Created and enqueued task {task.TaskId} for station {station.GUID}, state {result.State}", Level = Level.Info, Category = Category.MixingStation });
      }
    }

    public async Task ExecuteAsync(Employee employee, TaskDescriptor task)
    {
      await Performance.TrackExecutionAsync(nameof(ExecuteAsync), async () =>
      {
        if (employee.Type != Enum.Parse<EEmployeeType>(EmployeeTypes.Chemist.ToString()))
        {
          Log(Level.Error, $"Employee {employee.fullName} is not a Chemist", Category.MixingStation);
          return;
        }

        var station = _taskService.ResolveEntity(task.EntityGuid) as IStationAdapter;
        if (station == null)
        {
          Log(Level.Error, $"Invalid station {task.EntityGuid}", Category.MixingStation);
          return;
        }

        switch (task.ActionId)
        {
          case 0: // NeedsRestock
            await ExecuteActionAsync(station, employee, task, RestockSlots);
            var storageKey = new StorageKey(station.GUID, StorageTypes.Station);
            if (_cacheManager.TryGetStationSlots(storageKey, out var slots))
            {
              var logs = new NativeList<LogEntry>(Allocator.TempJob);
              try
              {
                var result = MixingStationLogic.ValidateEntityState(station, slots, logs);
                if (result.State == (int)States.ReadyToOperate)
                {
                  await OperateAsync(station, employee, task);
                }
              }
              finally
              {
                logs.Dispose();
                slots.Dispose();
              }
            }
            break;
          case 1: // ReadyToOperate
            await OperateAsync(station, employee, task);
            break;
          case 2: // HasOutput (Loop)
            await ExecuteActionAsync(station, employee, task, LoopSlots);
            await FollowUpAsync(employee, task);
            break;
          case 3: // NeedsDelivery
            await ExecuteActionAsync(station, employee, task, DeliverSlots);
            break;
        }
      });
    }

    public async Task FollowUpAsync(Employee employee, TaskDescriptor task)
    {
      var station = _taskService.ResolveEntity(task.EntityGuid) as IStationAdapter;
      if (station == null)
      {
        Log(Level.Error, $"Invalid station {task.EntityGuid} for follow-up", Category.MixingStation);
        return;
      }

      var taskService = TaskServiceManager.GetOrCreateService(station.Buildable.ParentProperty);
      Performance.TrackExecutionCoroutine(
          nameof(FollowUpAsync) + "_" + task.Type + ":" + task.ActionId,
          taskService.CreateEmployeeSpecificTaskAsync(employee.GUID, task.EntityGuid, station.Buildable.ParentProperty, TaskTypes.MixingStation),
          1
      );

      var newTask = taskService.GetEmployeeSpecificTask(employee.GUID);
      if (newTask.TaskId != Guid.Empty)
      {
        await ExecuteAsync(employee, newTask);
        newTask.Dispose();
      }
    }

    public static class MixingStationLogic
    {
      [BurstCompile]
      public static ValidationResultData ValidateEntityState(IStationAdapter station, NativeList<SlotData> slots, NativeList<LogEntry> logs)
      {
        var result = new ValidationResultData { IsValid = false, State = (int)States.Invalid };
        if (station.IsInUse)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"Station {station.GUID} is in use", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          return result;
        }

        ItemKey outputItem = ItemKey.Empty;
        int outputQuantity = 0;
        for (int i = 0; i < slots.Length; i++)
        {
          var slot = slots[i];
          if (slot.SlotIndex == station.OutputSlot.SlotIndex && slot.Quantity > 0 && !slot.IsLocked)
          {
            outputItem = slot.Item;
            outputQuantity = slot.Quantity;
            break;
          }
        }

        if (outputItem.Id != "")
        {
          int loopCount = 0;
          for (int i = 0; i < slots.Length; i++)
          {
            var slot = slots[i];
            if (station.ProductSlots.Any(ps => ps.SlotIndex == slot.SlotIndex) && !slot.IsLocked)
            {
              int capacity = slot.StackLimit - slot.Quantity;
              if (capacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(outputItem)))
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

        ItemKey restockItem = ItemKey.Empty;
        int restockQuantity = 0;
        for (int i = 0; i < slots.Length; i++)
        {
          var slot = slots[i];
          if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex))
          {
            int capacity = slot.StackLimit - slot.Quantity;
            if (capacity > 0)
            {
              restockItem = slot.Item.Id == "" ? new ItemKey(station.InsertSlots.First(isl => isl.SlotIndex == slot.SlotIndex).ItemInstance) : slot.Item;
              restockQuantity = capacity;
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
        for (int i = 0; i < slots.Length; i++)
        {
          var slot = slots[i];
          if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex) && slot.Quantity < slot.StackLimit)
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
            Item = ItemKey.Empty,
            Quantity = 0
          };
          return result;
        }

        result = new ValidationResultData { IsValid = true, State = (int)States.Idle };
        return result;
      }

      [BurstCompile]
      public static TaskDescriptor CreateTaskDescriptor(
          Guid entityGuid, TaskTypes type, int actionId, EmployeeTypes employeeType, int priority,
          string propertyName, ItemKey item, int quantity,
          Guid pickupGuid, int[] pickupSlotIndices,
          Guid dropoffGuid, int[] dropoffSlotIndices,
          float creationTime)
      {
        return TaskDescriptor.Create(
            entityGuid, type, actionId, employeeType, priority,
            propertyName, item, quantity,
            pickupGuid, pickupSlotIndices,
            dropoffGuid, dropoffSlotIndices,
            creationTime
        );
      }
    }

    private async Task ExecuteActionAsync(IStationAdapter station, Employee employee, TaskDescriptor task,
        Func<IStationAdapter, ITransitEntity, TaskDescriptor, List<ItemSlot>> slotResolver)
    {
      var pickupEntity = _taskService.ResolveEntity(task.PickupGuid) as ITransitEntity;
      if (pickupEntity == null)
      {
        Log(Level.Error, $"Invalid pickup entity {task.PickupGuid}", Category.MixingStation);
        return;
      }

      var deliveryEntity = _taskService.ResolveEntity(task.DropoffGuid) as ITransitEntity;
      if (deliveryEntity == null)
      {
        Log(Level.Error, $"Invalid delivery entity {task.DropoffGuid}", Category.MixingStation);
        return;
      }

      var pickupSlots = slotResolver(station, pickupEntity, task);
      var deliverySlots = slotResolver(station, deliveryEntity, task);
      if (!pickupSlots.Any() || !deliverySlots.Any())
      {
        Log(Level.Warning, $"Invalid slots for station {station.GUID}", Category.MixingStation);
        return;
      }

      var request = TransferRequest.Get(employee, task.Item.CreateItemInstance(), task.Quantity,
          employee.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || s.ItemInstance.AdvCanStackWith(task.Item.CreateItemInstance())),
          pickupEntity, pickupSlots, deliveryEntity, deliverySlots);
      IEmployees[employee.AssignedProperty][employee.GUID].AdvBehaviour.StartMovement([new PrioritizedRoute(request, priority: 1)], TaskTypes.MixingStation, async (emp, _, status) =>
        {
          if (status == Status.Success)
          {
            _cacheManager.QueueStorageUpdate(pickupEntity as PlaceableStorageEntity);
            _cacheManager.QueueStorageUpdate(deliveryEntity as PlaceableStorageEntity);
            Log(Level.Info, $"Completed action for task {task.TaskId} on station {station.GUID}", Category.MixingStation);
          }
        });
    }

    private List<ItemSlot> RestockSlots(IStationAdapter station, ITransitEntity entity, TaskDescriptor task)
    {
      var slots = new List<ItemSlot>();
      for (int i = 0; i < task.PickupSlotIndices.Length; i++)
      {
        var slot = entity.OutputSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndices[i]);
        if (slot != null) slots.Add(slot);
      }
      if (entity == station.TransitEntity)
        slots.AddRange(station.InsertSlots.Where(s => s.ItemInstance == null || s.ItemInstance.AdvCanStackWith(task.Item.CreateItemInstance())));
      return slots;
    }

    private List<ItemSlot> LoopSlots(IStationAdapter station, ITransitEntity entity, TaskDescriptor task)
    {
      var slots = new List<ItemSlot>();
      if (entity == station.TransitEntity)
      {
        if (task.PickupSlotIndices.Contains(station.OutputSlot.SlotIndex))
          slots.Add(station.OutputSlot);
        for (int i = 0; i < task.DropoffSlotIndices.Length; i++)
        {
          var slot = station.ProductSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndices[i]);
          if (slot != null) slots.Add(slot);
        }
      }
      return slots;
    }

    private List<ItemSlot> DeliverSlots(IStationAdapter station, ITransitEntity entity, TaskDescriptor task)
    {
      var slots = new List<ItemSlot>();
      if (entity == station.TransitEntity)
      {
        if (task.PickupSlotIndices.Contains(station.OutputSlot.SlotIndex))
          slots.Add(station.OutputSlot);
      }
      else
      {
        for (int i = 0; i < task.DropoffSlotIndices.Length; i++)
        {
          var slot = entity.InputSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndices[i]) ??
                     entity.OutputSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndices[i]);
          if (slot != null) slots.Add(slot);
        }
      }
      return slots;
    }

    private async Task OperateAsync(IStationAdapter station, Employee employee, TaskDescriptor task)
    {
      var movementTask = await Performance.TrackExecutionAsync(nameof(NoLazyWorkers.Movement.Utilities.MoveToAsync),
          () => NoLazyWorkers.Movement.Utilities.MoveToAsync(employee, station.TransitEntity));
      if (!movementTask)
      {
        Log(Level.Error, $"Movement failed for {employee.fullName}", Category.MixingStation);
        return;
      }

      station.StartOperation(employee);
      float elapsed = 0f;
      const float timeout = 30f;
      while (station.IsInUse && elapsed < timeout)
      {
        elapsed += Time.deltaTime;
        await AwaitNextFishNetTickAsync();
      }

      if (station.IsInUse)
        Log(Level.Error, $"Operation timed out on station {station.GUID}", Category.MixingStation);
      else
      {
        _cacheManager.QueueStorageUpdate(station.TransitEntity as PlaceableStorageEntity);
        Log(Level.Info, $"Operation completed on station {station.GUID}", Category.MixingStation);
      }
    }
  }
}