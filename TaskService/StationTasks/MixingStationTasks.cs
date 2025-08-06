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
    public override ITaskBurstFor CreateTaskStruct => new MixingStationTaskStruct
    {
      StationCacheMap = _taskService.CacheService.StationDataCache,
      EntityStatesMap = _taskService.EntityStateService.EntityStates,
      DisabledEntitiesMap = _taskService.EntityStateService.DisabledEntities,
      StorageDataCacheMap = _taskService.CacheService.StorageDataCache,
      StationDataCacheMap = _taskService.CacheService.StationDataCache,
      PropertyName = _property.name
    };

    [BurstCompile]
    private struct MixingStationTaskStruct : ITaskBurstFor
    {
      public NativeParallelHashMap<Guid, StationData> StationCacheMap;
      public NativeParallelHashMap<Guid, int> EntityStatesMap;
      public NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntitiesMap;
      public NativeParallelHashMap<Guid, StorageData> StorageDataCacheMap;
      public NativeParallelHashMap<Guid, StationData> StationDataCacheMap;
      public FixedString32Bytes PropertyName;

      public void ExecuteFor(int index, NativeArray<Guid> inputs, NativeList<TaskResult> outputs, NativeList<LogEntry> logs)
      {
        var guid = inputs[index];
        var stationCacheKVP = StationCacheMap.GetKeyValueArrays(Allocator.Temp);
        NativeList<ItemData> requiredItems = default;
        try
        {
          bool found = false;
          for (int i = 0; i < stationCacheKVP.Keys.Length; i++)
          {
            if (stationCacheKVP.Keys[i] == guid)
            {
              found = true;
              break;
            }
          }
          if (!found)
          {
            logs.Add(new LogEntry { Message = $"Station {guid} not found", Level = Level.Warning, Category = Category.Tasks });
            EntityStatesMap[guid] = 0;
            DisabledEntitiesMap.Add(guid, new DisabledEntityData { ReasonType = DisabledEntityData.DisabledReasonType.NoDestination });
            return;
          }

          StationData stationData = default;
          for (int i = 0; i < stationCacheKVP.Keys.Length; i++)
          {
            if (stationCacheKVP.Keys[i] == guid)
            {
              stationData = stationCacheKVP.Values[i];
              break;
            }
          }

          if (!EntityStatesMap.TryGetValue(guid, out int state))
          {
            logs.Add(new LogEntry { Message = $"No state for station {guid}", Level = Level.Warning, Category = Category.Tasks });
            EntityStatesMap[guid] = 0;
            DisabledEntitiesMap.Add(guid, new DisabledEntityData { ReasonType = DisabledEntityData.DisabledReasonType.NoDestination });
            return;
          }
          if (state == 0)
          {
            logs.Add(new LogEntry { Message = $"Station {guid} is invalid", Level = Level.Verbose, Category = Category.Tasks });
            return;
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
              var validIndices = new NativeList<int>(Allocator.Temp);
              for (int j = 0; j < shelf.Slots.Length; j++)
              {
                if (SlotProcessingUtility.CanRemove(shelf.Slots[j], item, quantity))
                {
                  validIndices.Add(j);
                }
              }
              pickupSlotIndices = validIndices.ToArray(Allocator.TempJob);
              validIndices.Dispose();
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
              isValid = true;
            }
            else
            {
              state = 4; // Deliver action
              var packagingStation = FindPackagingStation(item);
              if (packagingStation.Guid != default)
              {
                dropoffGuid = packagingStation.Guid;
                var validIndices = new NativeList<int>(Allocator.Temp);
                for (int j = 0; j < packagingStation.InsertSlots.Length; j++)
                {
                  if (SlotProcessingUtility.CanInsert(packagingStation.InsertSlots[j], item, quantity))
                  {
                    validIndices.Add(j);
                  }
                }
                dropoffSlotIndices = validIndices.ToArray(Allocator.TempJob);
                validIndices.Dispose();
                isValid = dropoffSlotIndices.Length > 0;
              }
              else
              {
                var storage = FindStorage(item);
                if (storage.Guid != default)
                {
                  dropoffGuid = storage.Guid;
                  var validIndices = new NativeList<int>(Allocator.Temp);
                  for (int j = 0; j < storage.Slots.Length; j++)
                  {
                    if (SlotProcessingUtility.CanInsert(storage.Slots[j], item, quantity))
                    {
                      validIndices.Add(j);
                    }
                  }
                  dropoffSlotIndices = validIndices.ToArray(Allocator.TempJob);
                  validIndices.Dispose();
                  isValid = dropoffSlotIndices.Length > 0;
                }
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
                propertyName: PropertyName,
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
            EntityStatesMap[guid] = (int)EntityStates.MixingStation.Invalid;
            requiredItems = item.ID != default ? new NativeList<ItemData>(1, Allocator.TempJob) { item } : default;
            DisabledEntitiesMap.Add(guid, new DisabledEntityData
            {
              ReasonType = state == (int)EntityStates.MixingStation.NeedsRestock ? DisabledEntityData.DisabledReasonType.MissingItem : DisabledEntityData.DisabledReasonType.NoDestination,
              RequiredItems = requiredItems
            });
            outputs.Add(new TaskResult(default, false, $"Invalid state or no valid slots for {guid}"));
          }
        }
        finally
        {
          if (requiredItems.IsCreated) requiredItems.Dispose();
          stationCacheKVP.Dispose();
        }
      }

      [BurstCompile]
      private StorageData FindShelfWithItem(NativeList<ItemKey> items, int desiredQuantity = 0, bool allowTargetHigherQuality = false)
      {
        StorageData returnData = default;
        if (StorageDataCacheMap.Count() > 0)
        {
          var dataCache = StorageDataCacheMap.GetValueArray(Allocator.Temp);
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
          returnData = FindShelfWithItem(list, desiredQuantity, allowTargetHigherQuality);
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
        if (StationCacheMap.Count() > 0)
        {
          var dataCache = StationCacheMap.GetValueArray(Allocator.Temp);
          try
          {
            foreach (var station in dataCache)
              if (station.EntityType == EntityType.PackagingStation && (station.ProductSlots[0].Item.ID.IsEmpty || (station.ProductSlots[0].Item.ItemKey.Equals(item.ItemKey) && station.ProductSlots[0].StackLimit >= station.ProductSlots[0].Quantity + item.Quantity)))
                returnData = station;
          }
          finally
          {
            dataCache.Dispose();
          }
        }
        return returnData;
      }

      [BurstCompile]
      private StorageData FindStorage(ItemData item)
      {
        StorageData returnData = default;
        if (StorageDataCacheMap.Count() > 0)
        {
          var dataCache = StorageDataCacheMap.GetValueArray(Allocator.Temp);
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

    public override IEnumerator Execute(Employee employee, TaskDescriptor task)
    {
      if (!(employee is Chemist chemist))
      {
        Log(Level.Error, "MixingStationTask requires a Chemist", Category.Tasks);
        _taskService.CompleteTask(task);
        yield break;
      }

      if (!_taskService.CacheService.IStations.TryGetValue(task.EntityGuid, out var station))
      {
        Log(Level.Error, $"Station {task.EntityGuid} not found", Category.Tasks);
        _taskService.CompleteTask(task);
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
          _taskService.CompleteTask(task);
          yield break;
      }
    }

    private IEnumerator RestockIngredients(Chemist chemist, TaskDescriptor task, IStationAdapter station)
    {
      var slot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (slot == null || !_taskService.CacheService.IStations.TryGetValue(task.PickupGuid, out var pickupStation))
      {
        Log(Level.Warning, $"Restock failed: No slot or pickup station {task.PickupGuid}", Category.Tasks);
        _taskService.EntityStateService.EntityStates[task.EntityGuid] = (int)EntityStates.MixingStation.Invalid;
        _taskService.EntityStateService.OnConfigUpdate(task.EntityGuid);
        _taskService.CompleteTask(task);
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
          task,
          (request, _, status) =>
          {
            try
            {
              completed = status == MovementStatus.Success;
              TransferRequest.Release(transferRequest);
            }
            catch (Exception ex)
            {
              Log(Level.Error, $"Restock movement callback failed: {ex.Message}", Category.Tasks);
              completed = false;
            }
          }
      );
      if (!completed)
      {
        _taskService.EntityStateService.EntityStates[task.EntityGuid] = (int)EntityStates.MixingStation.Invalid;
        _taskService.EntityStateService.OnConfigUpdate(task.EntityGuid);
        _taskService.CompleteTask(task);
      }
      else if (_taskService.EntityStateService.EntityStates.TryGetValue(task.EntityGuid, out var state) &&
          state == (int)EntityStates.MixingStation.ReadyToOperate)
      {
        yield return OperateStation(chemist, task, station);
      }
      else _taskService.CompleteTask(task);
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
      const float TIMEOUT = 10f;
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
      }
      _taskService.CompleteTask(task);
    }

    private IEnumerator LoopOutput(Chemist chemist, TaskDescriptor task, IStationAdapter station)
    {
      var slot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
      if (slot == null)
      {
        Log(Level.Warning, $"No inventory slot for chemist {chemist.fullName}", Category.Tasks);
        _taskService.CompleteTask(task);
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
          task,
          (_, _, status) =>
          {
            try
            {
              completed = status == MovementStatus.Success;
              TransferRequest.Release(transferRequest);
            }
            catch (Exception ex)
            {
              Log(Level.Error, $"LoopOutput movement callback failed: {ex.Message}", Category.Tasks);
              completed = false;
            }
          }
      );

      if (!completed)
      {
        _taskService.EntityStateService.EntityStates[task.EntityGuid] = (int)EntityStates.MixingStation.Invalid;
        _taskService.EntityStateService.OnConfigUpdate(task.EntityGuid);
      }
      else if (_taskService.EntityStateService.EntityStates.TryGetValue(task.EntityGuid, out var state) &&
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
        _taskService.EntityStateService.EntityStates[task.EntityGuid] = (int)EntityStates.MixingStation.Invalid;
        _taskService.EntityStateService.OnConfigUpdate(task.EntityGuid);
        _taskService.CompleteTask(task);
        yield break;
      }

      var transferRequest = TransferRequest.Get(
          chemist, station.OutputSlot.ItemInstance, station.OutputSlot.Quantity, slot,
          station.TransitEntity, new List<ItemSlot> { station.OutputSlot },
          dropoffStation.TransitEntity, new List<ItemSlot> { dropoffStation.ProductSlots[0] }
      );

      yield return _taskService.CacheService.IEmployees[chemist.GUID].AdvBehaviour.StartMovement(
          transferRequest,
          task,
          (_, _, _) =>
          {
            try
            {
              TransferRequest.Release(transferRequest);
            }
            catch (Exception ex)
            {
              Log(Level.Error, $"DeliverOutput movement callback failed: {ex.Message}", Category.Tasks);
            }
          }
      );
      _taskService.CompleteTask(task);
    }
  }
}