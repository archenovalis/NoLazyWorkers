using FishNet;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Movement.Utilities;
using static NoLazyWorkers.Employees.Utilities;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.TaskService.TaskRegistry;
using static NoLazyWorkers.Movement.Extensions;

namespace NoLazyWorkers.TaskService.StationTasks
{
  public static class MixingStationTasks
  {
    public static List<ITaskDefinition> Register()
    {
      return new List<ITaskDefinition>
            {
                new StateValidationTaskDef(),
                new OperateTaskDef(),
                new RestockIngredientsTaskDef(),
                new HandleOutputTaskDef(),
                new DeliverProductTaskDef()
            };
    }

    public static class Constants
    {
      public const float OPERATION_TIMEOUT_SECONDS = 30f;
    }

    public class StateValidationTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.MixingStationState;
      public int Priority => 10;
      public EmployeeTypes EmployeeType => EmployeeTypes.Chemist;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.MixingStation;
      public TransitTypes DropoffType => TransitTypes.MixingStation;
      public IEntitySelector EntitySelector { get; } = new MixingStationEntitySelector();
      public ITaskValidator Validator { get; } = new StateValidator();
      public ITaskExecutor Executor { get; } = new StateExecutor();
    }

    public class StateValidator : ITaskValidator
    {
      [BurstCompile]
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not (MixingStation or MixingStationMk2))
          return;

        var state = (StationState<MixingStationStates>)(stationAdapter.StationState ?? new StationState<MixingStationStates> { State = MixingStationStates.Idle });
        if (state.IsValid(context.CurrentTime))
          return;

        var inputItems = stationAdapter.GetInputItemForProduct();
        if (inputItems == null || inputItems.Count == 0 || inputItems[0]?.SelectedItem == null)
        {
          state.State = MixingStationStates.Idle;
          stationAdapter.StationState = state;
          return;
        }

        ItemInstance targetItem = inputItems[0].SelectedItem.GetDefaultInstance();
        int threshold = stationAdapter.StartThreshold;
        int desiredQty = Math.Min(stationAdapter.MaxProductQuantity, stationAdapter.ProductSlots[0].Quantity);
        int inputQty = stationAdapter.InsertSlots[0].Quantity;

        bool hasOutput = stationAdapter.OutputSlot.Quantity > 0;
        bool canStart = inputQty >= threshold && desiredQty >= threshold;
        bool needsRestock = inputQty < desiredQty;

        if (hasOutput)
        {
          state.State = MixingStationStates.HasOutput;
        }
        else if (needsRestock)
        {
          var shelf = FindStorageWithItem(null, targetItem, threshold, desiredQty);
          if (shelf.Key != null && shelf.Value >= threshold)
          {
            state.State = MixingStationStates.NeedsRestock;
            state.SetData("RestockItem", targetItem);
            state.SetData("RestockShelfGuid", shelf.Key.GUID);
            state.SetData("RestockQuantity", Math.Min(shelf.Value, desiredQty - inputQty));
            state.SetData("RestockPickupSlotIndices", shelf.Key.StorageEntity.ItemSlots
                .FindAll(s => s.ItemInstance != null && targetItem.AdvCanStackWith(s.ItemInstance, allowHigherQuality: true))
                .Select(s => s.SlotIndex)
                .Take(3)
                .ToList());
          }
          else
          {
            state.State = MixingStationStates.Idle;
          }
        }
        else if (canStart)
        {
          state.State = MixingStationStates.CanStart;
        }
        else
        {
          state.State = MixingStationStates.Idle;
        }

        state.LastValidatedTime = context.CurrentTime;
        stationAdapter.StationState = state;

        var task = TaskDescriptor.Create(
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(targetItem),
            0,
            definition.PickupType,
            stationAdapter.GUID,
            new int[] { },
            definition.DropoffType,
            stationAdapter.GUID,
            new int[] { },
            context.CurrentTime
        );
        context.StationAdapter = stationAdapter;
        validTasks.Add(task);
      }
    }

    public class StateExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Chemist chemist))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StateValidationExecutor: Employee {employee?.fullName ?? "null"} is not a Chemist", DebugLogger.Category.MixingStation);
          return;
        }

        if (!IStations.TryGetValue(chemist.AssignedProperty, out var stations) ||
            !stations.TryGetValue(task.PickupGuid, out var stationAdapter))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StateValidationExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.MixingStation);
          return;
        }

        var stationState = (StationState<MixingStationStates>)stationAdapter.StationState;
        if (stationState == null || !stationState.IsValid(Time.time))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StateValidationExecutor: Invalid or stale station state for {stationAdapter.GUID}", DebugLogger.Category.MixingStation);
          TaskServiceManager.GetOrCreateService(chemist.AssignedProperty).EnqueueValidation(chemist.AssignedProperty, 100);
          return;
        }

        state.EmployeeState.TaskContext = new TaskContext { Station = stationAdapter };

        TaskDescriptor? subtask = null;
        if (stationState.State.Equals(MixingStationStates.HasOutput))
        {
          subtask = CreateActionTask(TaskTypes.MixingStationLoop, stationAdapter, chemist);
        }
        else if (stationState.State.Equals(MixingStationStates.NeedsRestock))
        {
          subtask = CreateActionTask(TaskTypes.MixingStationRestock, stationAdapter, chemist);
        }
        else if (stationState.State.Equals(MixingStationStates.CanStart))
        {
          subtask = CreateActionTask(TaskTypes.MixingStationOperate, stationAdapter, chemist);
        }

        if (subtask.HasValue)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"StateValidationExecutor: Enqueuing subtask {subtask.Value.Type} for station {stationAdapter.GUID}", DebugLogger.Category.MixingStation);
          await TaskServiceManager.GetOrCreateService(chemist.AssignedProperty).EnqueueTaskAsync(subtask.Value, subtask.Value.Priority);
        }

        state.EmployeeState.TaskContext?.Cleanup(employee);
        await state.EmployeeBeh.Disable();
        await InstanceFinder.TimeManager.AwaitNextTickAsync();
      }

      private TaskDescriptor? CreateActionTask(TaskTypes type, IStationAdapter station, Chemist chemist)
      {
        var state = (StationState<MixingStationStates>)station.StationState;
        if (type == TaskTypes.MixingStationRestock && state.State.Equals(MixingStationStates.NeedsRestock))
        {
          return TaskDescriptor.Create(
              TaskTypes.MixingStationRestock,
              EmployeeTypes.Chemist,
              10,
              station.ParentProperty.name,
              new ItemKey(state.GetData<ItemInstance>("RestockItem")),
              state.GetData<int>("RestockQuantity"),
              TransitTypes.PlaceableStorageEntity,
              state.GetData<Guid>("RestockShelfGuid"),
              state.GetData<List<int>>("RestockPickupSlotIndices").ToArray(),
              TransitTypes.MixingStation,
              station.GUID,
              new[] { station.InsertSlots[0].SlotIndex },
              Time.time,
              chemist.GUID,
              true
          );
        }
        if (type == TaskTypes.MixingStationLoop && state.State.Equals(MixingStationStates.HasOutput) && station.OutputSlot?.ItemInstance != null)
        {
          var deliverySlot = station.ProductSlots.FirstOrDefault();
          if (deliverySlot != null && (deliverySlot.ItemInstance == null || station.CanRefill(station.OutputSlot.ItemInstance)))
          {
            return TaskDescriptor.Create(
                TaskTypes.MixingStationLoop,
                EmployeeTypes.Chemist,
                8,
                station.ParentProperty.name,
                new ItemKey(station.OutputSlot.ItemInstance),
                station.OutputSlot.Quantity,
                TransitTypes.MixingStation,
                station.GUID,
                new[] { station.OutputSlot.SlotIndex },
                TransitTypes.MixingStation,
                station.GUID,
                new[] { deliverySlot.SlotIndex },
                Time.time,
                chemist.GUID,
                true
            );
          }
        }
        if (type == TaskTypes.MixingStationOperate && state.State.Equals(MixingStationStates.CanStart))
        {
          return TaskDescriptor.Create(
              TaskTypes.MixingStationOperate,
              EmployeeTypes.Chemist,
              3,
              station.ParentProperty.name,
              new ItemKey(station.GetInputItemForProduct()[0].SelectedItem.GetDefaultInstance()),
              0,
              TransitTypes.MixingStation,
              station.GUID,
              new int[] { },
              TransitTypes.MixingStation,
              station.GUID,
              new int[] { },
              Time.time,
              chemist.GUID,
              true
          );
        }
        return null;
      }
    }

    public class OperateTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.MixingStationOperate;
      public int Priority => 3;
      public EmployeeTypes EmployeeType => EmployeeTypes.Chemist;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.MixingStation;
      public TransitTypes DropoffType => TransitTypes.MixingStation;
      public IEntitySelector EntitySelector { get; } = new MixingStationEntitySelector();
      public ITaskValidator Validator { get; } = new OperateValidator();
      public ITaskExecutor Executor { get; } = new OperateExecutor();
    }

    public class OperateValidator : ITaskValidator
    {
      [BurstCompile]
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not (MixingStation or MixingStationMk2))
          return;

        var state = (StationState<MixingStationStates>)stationAdapter.StationState;
        if (!state.State.Equals(MixingStationStates.CanStart) || stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
          return;

        var inputItems = stationAdapter.GetInputItemForProduct();
        if (inputItems == null || inputItems.Count == 0 || inputItems[0]?.SelectedItem == null)
          return;

        if (!inputItems[0].SelectedItem.GetDefaultInstance().Equals(CreateItemInstance(context.Task.Item)))
          return;

        context.StationAdapter = stationAdapter;
        validTasks.Add(context.Task);
      }
    }

    public class OperateExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Chemist chemist))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"OperateExecutor: Employee {employee?.fullName ?? "null"} is not a Chemist", DebugLogger.Category.MixingStation);
          return;
        }

        if (!IStations.TryGetValue(chemist.AssignedProperty, out var stations) ||
            !stations.TryGetValue(task.PickupGuid, out var stationAdapter))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"OperateExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.MixingStation);
          return;
        }

        try
        {
          bool movementResult = await MoveToAsync(chemist, stationAdapter.TransitEntity);
          if (!movementResult)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"OperateExecutor: Movement failed for {chemist.fullName}", DebugLogger.Category.MixingStation);
            return;
          }

          stationAdapter.StartOperation(chemist);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"OperateExecutor: Started operation for station {stationAdapter.GUID}", DebugLogger.Category.MixingStation);
          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"OperateExecutor: Exception for {chemist.fullName} - {ex}", DebugLogger.Category.MixingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(employee);
          await state.EmployeeBeh.Disable();
        }
      }
    }

    public class RestockIngredientsTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.MixingStationRestock;
      public int Priority => 8;
      public EmployeeTypes EmployeeType => EmployeeTypes.Chemist;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PlaceableStorageEntity;
      public TransitTypes DropoffType => TransitTypes.MixingStation;
      public IEntitySelector EntitySelector { get; } = new MixingStationEntitySelector();
      public ITaskValidator Validator { get; } = new RestockIngredientsValidator();
      public ITaskExecutor Executor { get; } = new RestockIngredientsExecutor();
      public TaskTypes FollowUpTask => TaskTypes.MixingStationState;
    }

    public class RestockIngredientsValidator : ITaskValidator
    {
      [BurstCompile]
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not (MixingStation or MixingStationMk2))
          return;

        var state = (StationState<MixingStationStates>)stationAdapter.StationState;
        if (!state.State.Equals(MixingStationStates.NeedsRestock))
          return;

        if (!Storages[property].TryGetValue(context.Task.PickupGuid, out var shelf))
          return;

        var pickupSlots = shelf.StorageEntity.ItemSlots
            .Where(s => s.SlotIndex == context.Task.PickupSlotIndex1 ||
                        s.SlotIndex == context.Task.PickupSlotIndex2 ||
                        s.SlotIndex == context.Task.PickupSlotIndex3)
            .ToList();
        if (pickupSlots.Count == 0 || !pickupSlots.All(s => s.ItemInstance != null && CreateItemInstance(context.Task.Item).AdvCanStackWith(s.ItemInstance, allowHigherQuality: true)))
          return;

        if (stationAdapter.InsertSlots[0].GetCapacityForItem(CreateItemInstance(context.Task.Item)) <= 0)
          return;

        context.StationAdapter = stationAdapter;
        validTasks.Add(context.Task);
      }
    }

    public class RestockIngredientsExecutor : ITaskExecutor
    {
      private Chemist _chemist;

      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Chemist chemist))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockIngredientsExecutor: Employee {employee?.fullName ?? "null"} is not a Chemist", DebugLogger.Category.MixingStation);
          return;
        }

        if (!Storages[chemist.AssignedProperty].TryGetValue(task.PickupGuid, out var shelf) ||
            !IStations.TryGetValue(chemist.AssignedProperty, out var stations) ||
            !stations.TryGetValue(task.DropoffGuid, out var stationAdapter))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockIngredientsExecutor: Invalid entities for task {task.TaskId}", DebugLogger.Category.MixingStation);
          return;
        }

        try
        {
          var inventorySlot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockIngredientsExecutor: No inventory slot for {chemist.fullName}", DebugLogger.Category.MixingStation);
            return;
          }

          if (stationAdapter.InsertSlots[0].GetCapacityForItem(CreateItemInstance(task.Item)) <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockIngredientsExecutor: No capacity in station slot for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          var deliverySlots = new List<ItemSlot> { stationAdapter.InsertSlots[0] };
          var pickupSlots = shelf.StorageEntity.ItemSlots
              .Where(s => s.SlotIndex == task.PickupSlotIndex1 ||
                          s.SlotIndex == task.PickupSlotIndex2 ||
                          s.SlotIndex == task.PickupSlotIndex3)
              .ToList();

          if (pickupSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockIngredientsExecutor: No valid pickup slots for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          await ProcessSlotValidationAsync(task, CreateItemInstance(task.Item), pickupSlots);
          foreach (var slot in pickupSlots)
            slot.ApplyLock(chemist.NetworkObject, "pickup");

          var request = TransferRequest.Get(chemist, CreateItemInstance(task.Item), task.Quantity, inventorySlot, shelf, pickupSlots, stationAdapter.TransitEntity, deliverySlots);
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = [request] };

          var movementResult = await TransitAsync(chemist, state, task, [request]);
          if (movementResult.Success)
          {
            var stationState = (StationState<MixingStationStates>)stationAdapter.StationState;
            stationState.State = MixingStationStates.CanStart;
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(chemist, task); // Use automated follow-ups (TaskTypes.MixingStation)
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockIngredientsExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.MixingStation);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockIngredientsExecutor: Exception for {chemist.fullName} - {ex}", DebugLogger.Category.MixingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(employee);
          await state.EmployeeBeh.Disable();
        }
      }

      private async Task ProcessSlotValidationAsync(TaskDescriptor task, ItemInstance item, List<ItemSlot> slots)
      {
        int batchSize = 10;
        for (int i = 0; i < slots.Count; i += batchSize)
        {
          var batch = slots.GetRange(i, Math.Min(batchSize, slots.Count - i));
          foreach (var slot in batch)
          {
            if (slot.GetCapacityForItem(item) <= 0)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"No capacity in slot {slot.SlotIndex} for {task.TaskId} for {_chemist.fullName}", DebugLogger.Category.MixingStation);
              return;
            }
          }
          await InstanceFinder.TimeManager.AwaitNextTickAsync();
        }
      }
    }

    public class HandleOutputTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.MixingStationLoop;
      public int Priority => 6;
      public EmployeeTypes EmployeeType => EmployeeTypes.Chemist;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.MixingStation;
      public TransitTypes DropoffType => TransitTypes.MixingStation;
      public IEntitySelector EntitySelector { get; } = new MixingStationEntitySelector();
      public ITaskValidator Validator { get; } = new HandleOutputValidator();
      public ITaskExecutor Executor => new HandleOutputExecutor();
      public TaskTypes FollowUpTask => TaskTypes.MixingStationState;
    }

    public class HandleOutputValidator : ITaskValidator
    {
      [BurstCompile]
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not (MixingStation or MixingStationMk2))
          return;

        var state = (StationState<MixingStationStates>)stationAdapter.StationState;
        if (!state.State.Equals(MixingStationStates.HasOutput))
          return;

        var outputItem = stationAdapter.OutputSlot?.ItemInstance;
        if (outputItem == null || stationAdapter.OutputSlot.Quantity <= 0)
          return;

        var deliverySlot = stationAdapter.ProductSlots.FirstOrDefault(s => s.SlotIndex == context.Task.DropoffSlotIndex1);
        if (deliverySlot == null || (deliverySlot.ItemInstance != null && !stationAdapter.CanRefill(outputItem)))
          return;

        if (!outputItem.Equals(CreateItemInstance(context.Task.Item)))
          return;

        context.StationAdapter = stationAdapter;
        validTasks.Add(context.Task);
      }
    }

    public class HandleOutputExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Chemist chemist))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleOutputExecutor: Employee {employee?.fullName ?? "null"} is not a Chemist", DebugLogger.Category.MixingStation);
          return;
        }

        if (!IStations.TryGetValue(chemist.AssignedProperty, out var stations) ||
            !stations.TryGetValue(task.PickupGuid, out var stationAdapter))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleOutputExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.MixingStation);
          return;
        }

        try
        {
          var outputItem = stationAdapter.OutputSlot?.ItemInstance;
          if (outputItem == null || stationAdapter.OutputSlot.Quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleOutputExecutor: No output item for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          var deliverySlot = stationAdapter.ProductSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (deliverySlot == null || (deliverySlot.ItemInstance != null && !stationAdapter.CanRefill(outputItem)))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleOutputExecutor: Invalid delivery slot for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          var inventorySlot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || outputItem.AdvCanStackWith(s.ItemInstance, true));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleOutputExecutor: No inventory slot for {chemist.fullName}", DebugLogger.Category.MixingStation);
            return;
          }

          int quantity = Math.Min(stationAdapter.OutputSlot.Quantity, deliverySlot.GetCapacityForItem(outputItem));
          stationAdapter.OutputSlot.ApplyLock(chemist.NetworkObject, "pickup");

          var request = TransferRequest.Get(chemist, outputItem, quantity, inventorySlot, stationAdapter.TransitEntity, [stationAdapter.OutputSlot], stationAdapter.TransitEntity, [deliverySlot]);
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = [request] };

          var movementResult = await TransitAsync(chemist, state, task, [request]);
          if (movementResult.Success)
          {
            var stationState = (StationState<MixingStationStates>)stationAdapter.StationState;
            stationState.State = stationAdapter.OutputSlot.Quantity > 0 ? MixingStationStates.HasOutput : MixingStationStates.Idle;
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(chemist, task); // Use automated follow-ups (TaskTypes.StateValidation)
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleOutputExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.MixingStation);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandleOutputExecutor: Exception for {chemist.fullName} - {ex}", DebugLogger.Category.MixingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(employee);
          await state.EmployeeBeh.Disable();
        }
      }
    }

    public class DeliverProductTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.MixingStationDeliver;
      public int Priority => 4;
      public EmployeeTypes EmployeeType => EmployeeTypes.Chemist;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.MixingStation;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new MixingStationEntitySelector();
      public ITaskValidator Validator { get; } = new DeliverProductValidator();
      public ITaskExecutor Executor { get; } = new DeliverProductExecutor();
    }

    public class DeliverProductValidator : ITaskValidator
    {
      [BurstCompile]
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not (MixingStation or MixingStationMk2))
          return;

        var state = (StationState<MixingStationStates>)stationAdapter.StationState;
        if (!state.State.Equals(MixingStationStates.HasOutput))
          return;

        var outputItem = stationAdapter.OutputSlot?.ItemInstance;
        if (outputItem == null || stationAdapter.OutputSlot.Quantity <= 0)
          return;

        var destination = EntityProvider.ResolveEntity(property, context.Task.DropoffGuid, context.Task.DropoffType) as ITransitEntity;
        if (destination == null)
          return;

        var deliverySlots = destination.InputSlots
            .Where(s => s.SlotIndex == context.Task.DropoffSlotIndex1 ||
                        s.SlotIndex == context.Task.DropoffSlotIndex2 ||
                        s.SlotIndex == context.Task.DropoffSlotIndex3)
            .ToList();
        if (deliverySlots.Count == 0 || !deliverySlots.Any(s => s.ItemInstance == null || outputItem.AdvCanStackWith(s.ItemInstance)))
          return;

        if (!outputItem.Equals(CreateItemInstance(context.Task.Item)))
          return;

        context.StationAdapter = stationAdapter;
        validTasks.Add(context.Task);
      }
    }

    public class DeliverProductExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Chemist chemist))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverProductExecutor: Employee {employee?.fullName ?? "null"} is not a Chemist", DebugLogger.Category.MixingStation);
          return;
        }

        if (!IStations.TryGetValue(chemist.AssignedProperty, out var stations) ||
            !stations.TryGetValue(task.PickupGuid, out var stationAdapter))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverProductExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.MixingStation);
          return;
        }

        try
        {
          var outputItem = stationAdapter.OutputSlot?.ItemInstance;
          if (outputItem == null || stationAdapter.OutputSlot.Quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverProductExecutor: No output item for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          var destination = EntityProvider.ResolveEntity(chemist.AssignedProperty, task.DropoffGuid, task.DropoffType) as ITransitEntity;
          if (destination == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverProductExecutor: Invalid destination for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          var deliverySlots = destination.InputSlots
              .Where(s => s.SlotIndex == task.DropoffSlotIndex1 ||
                          s.SlotIndex == task.DropoffSlotIndex2 ||
                          s.SlotIndex == task.DropoffSlotIndex3)
              .ToList();

          if (deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverProductExecutor: No valid delivery slots for task {task.TaskId}", DebugLogger.Category.MixingStation);
            return;
          }

          var inventorySlot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverProductExecutor: No inventory slot for {chemist.fullName}", DebugLogger.Category.MixingStation);
            return;
          }

          int quantity = Math.Min(stationAdapter.OutputSlot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(outputItem)));
          stationAdapter.OutputSlot.ApplyLock(chemist.NetworkObject, "pickup");

          var request = TransferRequest.Get(chemist, outputItem, quantity, inventorySlot, stationAdapter.TransitEntity, [stationAdapter.OutputSlot], destination, deliverySlots);
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = [request] };

          var movementResult = await TransitAsync(chemist, state, task, [request]);
          if (movementResult.Success)
          {
            var stationState = (StationState<MixingStationStates>)stationAdapter.StationState;
            stationState.State = stationAdapter.OutputSlot.Quantity > 0 ? MixingStationStates.HasOutput : MixingStationStates.Idle;
            // No follow-up tasks (aligns with prior version)
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverProductExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.MixingStation);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverProductExecutor: Exception for {chemist.fullName} - {ex}", DebugLogger.Category.MixingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(employee);
          await state.EmployeeBeh.Disable();
        }
      }
    }
  }

  /// <summary>
  /// Selects MixingStation entities for task validation.
  /// </summary>
  public class MixingStationEntitySelector : IEntitySelector
  {
    public NativeList<EntityKey> SelectEntities(Property property, Allocator allocator)
    {
      var entities = new NativeList<EntityKey>(allocator);
      if (IStations.TryGetValue(property, out var stations))
      {
        foreach (var station in stations.Values.Where(s => s.TransitEntity is MixingStation or MixingStationMk2))
          entities.Add(new EntityKey { Guid = station.GUID, Type = TransitTypes.MixingStation });
      }
      return entities;
    }
  }
}