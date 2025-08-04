using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Property;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.Extensions;
using Unity.Burst;
using static NoLazyWorkers.Movement.Extensions;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using static NoLazyWorkers.CacheManager.Extensions;
using static NoLazyWorkers.Storage.SlotService;
using ScheduleOne.DevUtilities;
using static NoLazyWorkers.Employees.Extensions;
using NoLazyWorkers.Extensions;

namespace NoLazyWorkers.TaskService.StationTasks
{
  [EntityTask]
  public class MixingStationTask : BaseTask
  {
    private readonly Property _property;
    private readonly TaskService _taskService;
    public MixingStationTask(Property property)
    {
      _property = property ?? throw new ArgumentNullException(nameof(property));
      _taskService = TaskServiceManager.GetOrCreateService(property);
    }

    public override TaskName Type => TaskName.MixingStation;
    public override EntityType[] SupportedEntityTypes => [EntityType.MixingStation];
    public override Action<int, NativeArray<Guid>, NativeList<TaskResult>, NativeList<LogEntry>> CreateTaskDelegate =>
      (index, inputs, outputs, logs) =>
      {
        var guid = inputs[index];
        if (!_taskService.CacheService.StationDataCache.TryGetValue(guid, out var stationData))
        {
          logs.Add(new LogEntry { Message = $"Station {guid} not found", Level = Level.Warning, Category = Category.Tasks });
          return;
        }

        if (!_taskService.EntityStateService.EntityStates.TryGetValue(guid, out int state))
        {
          logs.Add(new LogEntry { Message = $"No state for station {guid}", Level = Level.Warning, Category = Category.EntityState });
          state = (int)EntityStates.MixingStation.Invalid;
        }

        ItemData item = default;
        int quantity = 0;
        Guid pickupGuid = default;
        NativeArray<int> pickupSlotIndices = default;
        Guid dropoffGuid = default;
        NativeArray<int> dropoffSlotIndices = default;
        bool isValid = false;

        if (state == (int)EntityStates.MixingStation.NeedsRestock)
        {
          item = stationData.InsertSlots.Length > 0 ? stationData.InsertSlots[0].Item : default;
          quantity = stationData.InsertSlots.Length > 0 ? stationData.StartThreshold - stationData.InsertSlots[0].Quantity : 0;
          var shelf = FindShelfWithItem(item.ItemKey);
          if (shelf.Guid != default)
          {
            pickupGuid = shelf.Guid;
            pickupSlotIndices = new NativeArray<int>(
                shelf.Slots
                    .Select((slot, idx) => SlotProcessingUtility.CanRemove(slot, item, quantity) ? idx : -1)
                    .Where(idx => idx >= 0)
                    .ToArray(),
                Allocator.TempJob
            );
            isValid = pickupSlotIndices.Length > 0;
          }
        }
        else if (state == (int)EntityStates.MixingStation.ReadyToOperate)
        {
          isValid = true;
        }
        else if (state == (int)EntityStates.MixingStation.HasOutput)
        {
          item = stationData.OutputSlot.Item;
          quantity = stationData.OutputSlot.Quantity;
          if (stationData.ProductSlots.Length > 0 &&
              (stationData.ProductSlots[0].Item.ID == "" ||
                stationData.ProductSlots[0].Item.AdvCanStackWithBurst(item)) &&
              stationData.ProductSlots[0].Quantity < stationData.ProductSlots[0].StackLimit)
          {
            state = 3; // Loop action
            isValid = true;
          }
          else
          {
            state = 5; // Deliver action
            var destination = FindDestination(item);
            if (destination.Guid != default)
            {
              dropoffGuid = destination.Guid;
              dropoffSlotIndices = new NativeArray<int>(
                  destination.Slots
                      .Select((slot, idx) => SlotProcessingUtility.CanInsert(slot, item, quantity) ? idx : -1)
                      .Where(idx => idx >= 0)
                      .ToArray(),
                  Allocator.TempJob
              );
              isValid = dropoffSlotIndices.Length > 0;
            }
          }
        }

        if (isValid)
        {
          var task = TaskDescriptor.Create(
              entityGuid: guid,
              type: TaskName.MixingStation,
              actionId: state,
              employeeType: TaskEmployeeType.Chemist,
              priority: 50,
              propertyName: _property.name,
              item: item,
              quantity: quantity,
              pickupGuid: pickupGuid,
              pickupSlotIndices: pickupSlotIndices,
              dropoffGuid: dropoffGuid,
              dropoffSlotIndices: dropoffSlotIndices,
              creationTime: Time.time,
              logs: logs
          );
          outputs.Add(new TaskResult(task, true));
        }
        else
        {
          if (pickupSlotIndices.IsCreated) pickupSlotIndices.Dispose();
          if (dropoffSlotIndices.IsCreated) dropoffSlotIndices.Dispose();
          outputs.Add(new TaskResult(default, false, "Invalid state or no valid slots"));
        }
      };

    public override IEnumerator Execute(Employee employee, TaskDescriptor task)
    {
      if (!(employee is Chemist chemist))
      {
        Log(Level.Error, "MixingStationTask requires a Chemist", Category.Tasks);
        yield break;
      }

      if (!_taskService.CacheService.IStations.TryGetValue(task.EntityGuid, out var station))
      {
        Log(Level.Error, $"Station {task.EntityGuid} not found", Category.Tasks);
        yield break;
      }

      switch (task.ActionId)
      {
        case (int)EntityStates.MixingStation.NeedsRestock:
          yield return RestockIngredients(chemist, task, station);
          break;
        case (int)EntityStates.MixingStation.ReadyToOperate:
          yield return OperateStation(chemist, task, station);
          break;
        case 3: // Loop
          yield return LoopOutput(chemist, task, station);
          break;
        case 4: // Deliver
          yield return DeliverOutput(chemist, task, station);
          break;
        default:
          Log(Level.Warning, $"Unknown action {task.ActionId}", Category.Tasks);
          yield break;
      }
    }

    private IEnumerator RestockIngredients(Chemist chemist, TaskDescriptor task, IStationAdapter station)
    {
      var slot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (slot == null || !_taskService.CacheService.IStations.TryGetValue(task.PickupGuid, out var pickupStation))
      {
        Log(Level.Warning, $"Restock failed: No slot or pickup station {task.PickupGuid}", Category.Tasks);
        yield break;
      }

      var transferRequest = TransferRequest.Get(
          chemist, task.Item.CreateItemInstance(), task.Quantity, slot,
          pickupStation.TransitEntity, new List<ItemSlot> { pickupStation.OutputSlot },
          station.TransitEntity, new List<ItemSlot> { station.InsertSlots[0] }
      );
      bool completed = false;
      yield return _taskService.CacheService.IEmployees[chemist.GUID].AdvBehaviour.StartMovement(
        transferRequest,
        (request, _, status) =>
          {
            completed = status == MovementStatus.Success;
            TransferRequest.Release(transferRequest);
          }
      );

      if (completed && _taskService.EntityStateService.EntityStates.TryGetValue(task.EntityGuid, out var state) &&
          state == (int)EntityStates.MixingStation.ReadyToOperate)
      {
        yield return OperateStation(chemist, task, station);
      }

      _taskService.CompleteTask(task);
    }

    private IEnumerator OperateStation(Chemist chemist, TaskDescriptor task, IStationAdapter station)
    {
      chemist.SetDestination(station.TransitEntity);
      while (!NavMeshUtility.IsAtTransitEntity(station.TransitEntity, chemist))
      {
        yield return FishNetExtensions.AwaitNextTick();
      }
      station.StartOperation(chemist);
      float elapsed = 0f;
      const float TIMEOUT = 30f;
      while (!station.IsInUse && elapsed < TIMEOUT)
      {
        yield return new WaitForSeconds(0.1f);
        elapsed += 0.1f;
      }

      if (!station.IsInUse)
      {
        Log(Level.Error, $"Station {station.GUID} failed to start", Category.Tasks);
        yield break;
      }

      elapsed = 0f;
      while (station.IsInUse && elapsed < TIMEOUT)
      {
        yield return new WaitForSeconds(0.1f);
        elapsed += 0.1f;
      }

      if (station.IsInUse)
      {
        Log(Level.Error, $"Operation timed out for station {station.GUID}", Category.Tasks);
        yield break;
      }

      _taskService.EntityStateService.OnConfigUpdate(station.GUID);
      _taskService.CompleteTask(task);
    }

    private IEnumerator LoopOutput(Chemist chemist, TaskDescriptor task, IStationAdapter station)
    {
      var slot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (slot == null)
      {
        Log(Level.Warning, $"No inventory slot for chemist {chemist.fullName}", Category.Tasks);
        yield break;
      }

      var transferRequest = TransferRequest.Get(
          chemist, station.OutputSlot.ItemInstance, station.OutputSlot.Quantity, slot,
          station.TransitEntity, new List<ItemSlot> { station.OutputSlot },
          station.TransitEntity, new List<ItemSlot> { station.ProductSlots[0] }
      );
      bool completed = false;
      yield return _taskService.CacheService.IEmployees[chemist.GUID].AdvBehaviour.StartMovement(
        transferRequest,
        (_, _, status) =>
        {
          completed = status == MovementStatus.Success;
          TransferRequest.Release(transferRequest);
        }
      );

      if (completed && _taskService.EntityStateService.EntityStates.TryGetValue(task.EntityGuid, out var state) &&
          (state == (int)EntityStates.MixingStation.NeedsRestock || state == (int)EntityStates.MixingStation.ReadyToOperate))
      {
        yield return _taskService.CreateFollowUpTask(chemist.GUID, task.EntityGuid, task.Type);
      }

      _taskService.CompleteTask(task);
    }

    private IEnumerator DeliverOutput(Chemist chemist, TaskDescriptor task, IStationAdapter station)
    {
      var slot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (slot == null || !_taskService.CacheService.IStations.TryGetValue(task.DropoffGuid, out var dropoffStation))
      {
        Log(Level.Warning, $"Deliver failed: No slot or dropoff station {task.DropoffGuid}", Category.Tasks);
        yield break;
      }

      var transferRequest = TransferRequest.Get(
          chemist, station.OutputSlot.ItemInstance, station.OutputSlot.Quantity, slot,
          station.TransitEntity, new List<ItemSlot> { station.OutputSlot },
          dropoffStation.TransitEntity, new List<ItemSlot> { dropoffStation.ProductSlots[0] }
      );
      bool completed = false;
      yield return _taskService.CacheService.IEmployees[chemist.GUID].AdvBehaviour.StartMovement(
        transferRequest,
        (_, _, status) =>
        {
          completed = status == MovementStatus.Success;
          TransferRequest.Release(transferRequest);
        }
      );

      _taskService.CompleteTask(task);
    }

    [BurstCompile]
    private StorageData FindShelfWithItem(NativeList<ItemKey> items, int desiredQuantity = 0, bool allowTargetHigherQuality = false)
    {
      StorageData returnData = default;
      if (_taskService.CacheService.StorageDataCache.Count() > 0)
      {
        var dataCache = _taskService.CacheService.StorageDataCache.GetValueArray(Allocator.Temp);
        foreach (var storageData in dataCache)
          if (storageData.Slots.Any(slot => slot.Item.ItemKey.AdvCanStackWithBurst(items, allowTargetHigherQuality) && slot.Quantity > desiredQuantity))
          {
            returnData = storageData;
            break;
          }
        dataCache.Dispose();
      }
      return returnData;
    }

    [BurstCompile]
    private StorageData FindShelfWithItem(ItemKey item, int desiredQuantity = 0, bool allowTargetHigherQuality = false)
    {
      StorageData returnData = default;
      var list = new NativeList<ItemKey>(Allocator.TempJob) { item };
      try
      {
        returnData = FindShelfWithItem(item, desiredQuantity, allowTargetHigherQuality);
      }
      finally
      {
        list.Dispose();
      }
      return returnData;
    }

    [BurstCompile]
    private StationData FindPackagingStation(ItemData item)
    {
      StationData returnData = default;
      if (_taskService.CacheService.StorageDataCache.Count() > 0)
      {
        var dataCache = _taskService.CacheService.StationDataCache.GetValueArray(Allocator.Temp);
        foreach (var station in dataCache)
          if (station.EntityType == EntityType.PackagingStation && station.ProductSlots[0].Item.ID.IsEmpty || (station.ProductSlots[0].Item.ItemKey.Equals(item.ItemKey) && station.ProductSlots[0].StackLimit >= station.ProductSlots[0].Quantity + item.Quantity))
            returnData = station;
      }
      return returnData;
    }

    [BurstCompile]
    private StorageData FindDestination(ItemData item)
    {
      foreach (var storage in _taskService.CacheService.StorageDataCache)
      {
        if (storage.Value.Slots.Any(slot => slot.Item.ID == "" || (slot.Item.Equals(item) && slot.Quantity < slot.StackLimit)))
        {
          return storage.Value;
        }
      }
      return default;
    }

    [BurstCompile]
    private StorageData FindStorageForDelivery(ItemData item)
    {
      StorageData returnData = default;
      if (_taskService.CacheService.StorageDataCache.Count() > 0)
      {
        var dataCache = _taskService.CacheService.StorageDataCache.GetValueArray(Allocator.Temp);
        foreach (var storageData in dataCache)
          if (storageData.Slots.Any(slot => slot.Item.ID.IsEmpty || slot.Item.AdvCanStackWithBurst(item, checkQuantities: true)))
          {
            returnData = storageData;
            break;
          }
        dataCache.Dispose();
      }
      return returnData;
    }
  }
}