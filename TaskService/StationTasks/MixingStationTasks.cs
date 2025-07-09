using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Storage.ManagedDictionaries;
using static NoLazyWorkers.Employees.Utilities;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using NoLazyWorkers.Storage;
using Unity.Burst;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using NoLazyWorkers.Performance;
using static NoLazyWorkers.NoLazyUtilities;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using Unity.Jobs;
using NoLazyWorkers.JobService;
using NoLazyWorkers.Stations;
using NoLazyWorkers.Extensions;

namespace NoLazyWorkers.TaskService.StationTasks
{


  public class MixingStationTask : ITask<SetupOutput, ValidationSetupOutput>
  {
    public TaskName Type => TaskName.MixingStation;

    public Action<int, int, NativeArray<int>, NativeList<Guid>> SelectEntitiesDelegate => MixingStationEntitySelector.SelectEntitiesBurst;

    public Action<int, int, NativeArray<Guid>, NativeList<ValidationResultData>> ValidateEntityDelegate => new ValidateMixingStationEntitiesBurst
    {
      CacheService = CacheService.GetOrCreateCacheService(_property),
      ActiveTasksByType = new TaskDispatcher(1).activeTasksByType // Set in ProcessTasksCoroutine
    }.Execute;

    public Action<int, int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate => new CreateMixingStationTaskBurst
    {
      CacheService = CacheService.GetOrCreateCacheService(_property),
      PropertyName = _property.name
    }.Execute;

    private readonly Property _property;

    public MixingStationTask(Property property)
    {
      _property = property;
    }

    public Action<int, int, NativeArray<int>, List<SetupOutput>> SetupDelegate => (start, count, inputs, outputs) =>
    {
      var stationData = new NativeParallelHashMap<Guid, StationData>(100, Allocator.TempJob);
      try
      {
        if (ManagedDictionaries.IStations.TryGetValue(_property, out var stations))
        {
          foreach (var station in stations.Values)
          {
            if (station.TypeOf == typeof(MixingStation) || station.TypeOf == typeof(MixingStationMk2))
            {
              var productSlots = new NativeArray<int>(station.ProductSlots.Length, Allocator.TempJob);
              var insertSlots = new NativeArray<int>(station.InsertSlots.Length, Allocator.TempJob);
              try
              {
                for (int i = 0; i < station.ProductSlots.Length; i++)
                  productSlots[i] = station.ProductSlots[i].SlotIndex;
                for (int i = 0; i < station.InsertSlots.Length; i++)
                  insertSlots[i] = station.InsertSlots[i].SlotIndex;

                stationData.Add(station.GUID, new StationData
                {
                  GUID = station.GUID,
                  IsInUse = station.IsInUse,
                  OutputSlotIndex = station.OutputSlot.SlotIndex,
                  ProductSlotIndices = productSlots,
                  InsertSlotIndices = insertSlots
                });
              }
              catch
              {
                if (productSlots.IsCreated) productSlots.Dispose();
                if (insertSlots.IsCreated) insertSlots.Dispose();
                throw;
              }
            }
          }
        }
        outputs.Add(new SetupOutput { Data = stationData });
#if DEBUG
        outputs.Add(new LogEntry
        {
          Message = $"Setup produced {stationData.Count()} station data entries",
          Level = Level.Verbose,
          Category = Category.MixingStation
        });
#endif
      }
      catch
      {
        if (stationData.IsCreated) stationData.Dispose();
        throw;
      }
    };

    public Action<int, int, NativeArray<Guid>, List<ValidationSetupOutput>> ValidationSetupDelegate => (start, count, inputs, outputs) =>
    {
      var validationData = new NativeParallelHashMap<Guid, ValidationData>(inputs.Length, Allocator.TempJob);
      try
      {
        for (int i = start; i < start + count && i < inputs.Length; i++)
        {
          var guid = inputs[i];
          if (ManagedDictionaries.IStations.TryGetValue(_property, out var stations) &&
                  stations.TryGetValue(guid, out var station))
          {
            var refillSlots = new NativeArray<int>(station.RefillList().Count, Allocator.TempJob);
            try
            {
              int j = 0;
              foreach (var refill in station.RefillList())
              {
                if (refill != null)
                  refillSlots[j++] = refill.SlotIndex;
              }
              validationData.Add(guid, new ValidationData
              {
                GUID = guid,
                RefillSlotIndices = refillSlots
              });
            }
            catch
            {
              if (refillSlots.IsCreated) refillSlots.Dispose();
              throw;
            }
          }
        }
        outputs.Add(new ValidationSetupOutput { Data = validationData });
#if DEBUG
        outputs.Add(new LogEntry
        {
          Message = $"Validation setup produced {validationData.Count()} entries",
          Level = Level.Verbose,
          Category = Category.MixingStation
        });
#endif
      }
      catch
      {
        if (validationData.IsCreated) validationData.Dispose();
        throw;
      }
    };

    public bool IsValidState(int state) => state is (int)States.NeedsRestock or (int)States.NeedsDelivery or (int)States.HasOutput;

    public IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
    {
      yield return null;
    }

    public IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
    {
      yield return null;
    }

    private enum States
    {
      Invalid = 0,
      Idle = 1,
      NeedsRestock = 2,
      NeedsDelivery = 3,
      HasOutput = 4,
      ReadyToOperate = 5
    }

    [BurstCompile]
    public static class MixingStationEntitySelector
    {
      public static void SelectEntitiesBurst(int start, int count, NativeArray<int> inputs, NativeList<Guid> outputs)
      {
        // Note: ManagedDictionaries.IStations is not Burst-compatible; assume native collections in production
        var entities = outputs;
        entities.Add(Guid.NewGuid()); // Placeholder
#if DEBUG
        entities.Add(new LogEntry
        {
          Message = $"Selected {entities.Length} mixing stations",
          Level = Level.Verbose,
          Category = Category.MixingStation
        });
#endif
      }
    }

    [BurstCompile]
    public struct ValidateMixingStationEntitiesBurst
    {
      public CacheService CacheService;
      public NativeParallelHashMap<TaskTypeEntityKey, bool> ActiveTasksByType;
      public FixedString32Bytes PropertyName;
      public NativeParallelHashMap<Guid, StationData> StationData;
      public NativeParallelHashMap<Guid, ValidationData> ValidationData;

      /// <summary>
      /// Validates mixing station entities, checking task existence and slot data.
      /// CacheService, ActiveTasksByType, StationData, ValidationData, and outputs are disposed by the caller.
      /// </summary>
      public void Execute(int start, int count, NativeArray<Guid> entityGuids, NativeList<ValidationResultData> outputs)
      {
        for (int i = start; i < start + count && i < entityGuids.Length; i++)
        {
          var entityGuid = entityGuids[i];
          var key = new TaskTypeEntityKey { Type = TaskName.MixingStation, EntityGuid = entityGuid };
          if (ActiveTasksByType.ContainsKey(key))
          {
#if DEBUG
            outputs.Add(new LogEntry
            {
              Message = $"Task of type {TaskName.MixingStation} already exists for entity {entityGuid}, skipping",
              Level = Level.Verbose,
              Category = Category.MixingStation
            });
#endif
            continue;
          }

          if (!StationData.TryGetValue(entityGuid, out var station) ||
              !ValidationData.TryGetValue(entityGuid, out var validationData))
          {
#if DEBUG
            outputs.Add(new LogEntry
            {
              Message = $"No station or validation data for entity {entityGuid}",
              Level = Level.Warning,
              Category = Category.MixingStation
            });
#endif
            continue;
          }

          var slotData = new NativeArray<SlotData>(1, Allocator.TempJob);
          try
          {
            if (CacheService._ownerToStorageType.TryGetValue(entityGuid, out var type) &&
                CacheService._storageSlotsCache.TryGetValue(new StorageKey(entityGuid, type), out var slots))
            {
              slotData[0] = slots[0];
              var result = ValidateEntityState(station, validationData, slotData, outputs);
              if (result.IsValid)
              {
                outputs.Add(result);
#if DEBUG
                outputs.Add(new LogEntry
                {
                  Message = $"Validated entity {entityGuid} with state {result.State}",
                  Level = Level.Verbose,
                  Category = Category.MixingStation
                });
#endif
              }
            }
            else
            {
#if DEBUG
              outputs.Add(new LogEntry
              {
                Message = $"No slot data found for entity {entityGuid}",
                Level = Level.Warning,
                Category = Category.MixingStation
              });
#endif
            }
          }
          finally
          {
            if (slotData.IsCreated) slotData.Dispose();
          }
        }
      }
    }

    [BurstCompile]
    public struct CreateMixingStationTaskBurst
    {
      public CacheService CacheService;
      public FixedString32Bytes PropertyName;

      /// <summary>
      /// Creates tasks for validated mixing station entities. Outputs and CacheService are disposed by the caller.
      /// </summary>
      public void Execute(int start, int count, NativeArray<ValidationResultData> results, NativeList<TaskResult> outputs)
      {
        for (int i = start; i < start + count && i < results.Length; i++)
        {
          var result = results[i];
          if (!result.IsValid || !IsValidState(result.State)) continue;

          Guid sourceGuid = Guid.Empty;
          int[] sourceSlotIndices = null;
          Guid destGuid = Guid.Empty;
          int[] destSlotIndices = null;

          if (result.State == (int)States.NeedsRestock)
          {
            var requiredItems = new NativeList<ItemData>(1, Allocator.TempJob);
            requiredItems.Add(result.Item);
            try
            {
              if (CacheService._itemToStorageCache.TryGetValue(new ItemKey(result.Item), out var storageKeys))
              {
                foreach (var storageKey in storageKeys)
                {
                  if (CacheService._storageSlotsCache.TryGetValue(storageKey, out var slots))
                  {
                    int totalQuantity = 0;
                    var slotIndices = new NativeList<int>(Allocator.TempJob);
                    try
                    {
                      foreach (var slot in slots)
                      {
                        if (slot.Item.Equals(result.Item) && slot.Quantity > 0)
                        {
                          totalQuantity += slot.Quantity;
                          slotIndices.Add(slot.SlotIndex);
                        }
                      }
                      if (totalQuantity >= result.Quantity)
                      {
                        sourceGuid = storageKey.Guid;
                        sourceSlotIndices = slotIndices.ToArray();
                        destGuid = storageKey.Guid;
                        break;
                      }
                    }
                    finally
                    {
                      if (slotIndices.IsCreated) slotIndices.Dispose();
                    }
                  }
                }
              }
            }
            finally
            {
              if (requiredItems.IsCreated) requiredItems.Dispose();
            }
          }
          else if (result.State == (int)States.NeedsDelivery)
          {
            var destinations = new NativeList<(Guid, NativeList<int>, int)>(Allocator.TempJob);
            try
            {
              if (CacheService._itemToStorageCache.TryGetValue(new ItemKey(result.Item), out var storageKeys))
              {
                foreach (var storageKey in storageKeys)
                {
                  if (CacheService._storageSlotsCache.TryGetValue(storageKey, out var slots))
                  {
                    var destSlots = new NativeList<int>(Allocator.TempJob);
                    int capacity = 0;
                    try
                    {
                      foreach (var slot in slots)
                      {
                        if (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(result.Item))
                        {
                          capacity += slot.StackLimit - slot.Quantity;
                          destSlots.Add(slot.SlotIndex);
                        }
                      }
                      if (capacity >= result.Quantity)
                      {
                        destinations.Add((storageKey.Guid, destSlots, capacity));
                        break;
                      }
                    }
                    finally
                    {
                      if (destSlots.IsCreated) destSlots.Dispose();
                    }
                  }
                }
              }
              if (destinations.Length > 0)
              {
                var (dest, destSlots, capacity) = destinations[0];
                sourceGuid = storageKey.Guid;
                sourceSlotIndices = new[] { 0 }; // Placeholder
                destGuid = dest;
                destSlotIndices = destSlots.ToArray();
                result.DestinationCapacity = capacity;
              }
            }
            finally
            {
              if (destinations.IsCreated) destinations.Dispose();
            }
          }
          else if (result.State == (int)States.HasOutput)
          {
            sourceGuid = storageKey.Guid;
            sourceSlotIndices = new[] { 0 }; // Placeholder
            destGuid = storageKey.Guid;
            var loopSlots = new NativeList<int>(Allocator.TempJob);
            try
            {
              loopSlots.Add(0); // Simplified
              destSlotIndices = loopSlots.ToArray();
            }
            finally
            {
              if (loopSlots.IsCreated) loopSlots.Dispose();
            }
          }

          var task = TaskDescriptor.Create(
              storageKey.Guid, TaskName.MixingStation, result.State - 1,
              TaskEmployeeType.Chemist, 100, PropertyName.ToString(),
              result.Item, result.Quantity,
              sourceGuid, sourceSlotIndices,
              destGuid, destSlotIndices,
              Time.time
          );
          outputs.Add(new TaskResult(task, true));
#if DEBUG
          outputs.Add(new LogEntry
          {
            Message = $"Created task {task.TaskId} for entity {storageKey.Guid}",
            Level = Level.Verbose,
            Category = Category.MixingStation
          });
#endif
        }
      }

      private bool IsValidState(int state) => state is (int)States.NeedsRestock or (int)States.NeedsDelivery or (int)States.HasOutput;
    }

    [BurstCompile]
    public static ValidationResultData ValidateEntityState(StationData station, ValidationData validationData, NativeArray<SlotData> slots, NativeList<LogEntry> logs)
    {
      var result = new ValidationResultData { IsValid = false, State = (int)States.Invalid };
      if (station.IsInUse)
      {
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Station {station.GUID} is in use",
          Level = Level.Verbose,
          Category = Category.MixingStation
        });
#endif
        return result;
      }

      ItemData outputItem = ItemData.Empty;
      int outputQuantity = 0;
      for (int i = 0; i < slots.Length; i++)
      {
        var slot = slots[i];
        if (slot.SlotIndex == station.OutputSlotIndex && slot.Quantity > 0 && !slot.IsLocked)
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
          for (int j = 0; j < station.ProductSlotIndices.Length; j++)
          {
            if (station.ProductSlotIndices[j] == slot.SlotIndex && !slot.IsLocked)
            {
              int capacity = slot.StackLimit - slot.Quantity;
              if (capacity > 0 && (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(outputItem)))
                loopCount++;
            }
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
      for (int i = 0; i < slots.Length; i++)
      {
        var slot = slots[i];
        for (int j = 0; j < validationData.RefillSlotIndices.Length; j++)
        {
          if (validationData.RefillSlotIndices[j] == slot.SlotIndex)
          {
            int capacity = slot.StackLimit - slot.Quantity;
            if (capacity > 0)
            {
              restockItem = slot.Item.Id == "" ? new ItemData { Id = "default" } : slot.Item;
              restockQuantity = capacity;
              break;
            }
          }
        }
        if (restockItem.Id != "") break;
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
        for (int j = 0; j < station.InsertSlotIndices.Length; j++)
        {
          if (station.InsertSlotIndices[j] == slot.SlotIndex && slot.Quantity < slot.StackLimit)
          {
            canOperate = false;
            break;
          }
        }
        if (!canOperate) break;
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
      }

      return result;
    }

    private enum States
    {
      Invalid = 0,
      Idle = 1,
      NeedsRestock = 2,
      NeedsDelivery = 3,
      HasOutput = 4,
      ReadyToOperate = 5
    }

    public class MixingStationTask : ITask
    {
      private const int StateInvalid = 0;
      private const int StateIdle = 1;
      private const int StateNeedsRestock = 2;
      private const int StateNeedsDelivery = 3;
      private const int StateHasOutput = 4;
      private const int StateReadyToOperate = 5;

      public TaskName Type => TaskName.MixingStation;
      public IEntitySelector EntitySelector => new MixingStationEntitySelector();

      public class MixingStationEntitySelector : IEntitySelector
      {
        [BurstCompile]
        public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
        {
          var entities = new NativeList<Guid>(allocator);
          if (ManagedDictionaries.IStations.TryGetValue(property, out var stations))
          {
            foreach (var station in stations.Values)
            {
              if (station.TypeOf == typeof(MixingStation) || station.TypeOf == typeof(MixingStationMk2))
                entities.Add(station.GUID);
            }
          }
          Log(Level.Info, $"Selected {entities.Length} mixing stations for {property.name}", Category.MixingStation);
          return entities;
        }
      }

      public bool IsValidState(int state) => state != StateInvalid && state != StateIdle;

      [BurstCompile]
      public void ProcessEntity(int index, NativeArray<Guid> entityGuids, NativeArray<ValidationResultData> results, NativeList<LogEntry> logs, Property property, TaskService taskService, NativeArray<SlotData> slotData, DisabledEntityService disabledService, TaskDispatcher dispatcher)
      {
        if (index >= entityGuids.Length) return;
        var entityGuid = entityGuids[index];
        var station = taskService.ResolveEntity(entityGuid) as IStationAdapter;
        if (station == null)
        {
          logs.Add(new LogEntry
          {
            Message = $"Invalid entity for GUID {entityGuid}",
            Level = Level.Error,
            Category = Category.MixingStation
          });
          return;
        }

        var result = ValidateEntityState(station, slotData, logs);
        results[index] = result;

        if (!result.IsValid || !IsValidState(result.State))
        {
#if DEBUG
          logs.Add(new LogEntry
          {
            Message = $"Invalid or idle state for station {station.GUID}",
            Level = Level.Verbose,
            Category = Category.MixingStation
          });
#endif
          return;
        }

        Guid sourceGuid = Guid.Empty;
        int[] sourceSlotIndices = null;
        Guid destGuid = Guid.Empty;
        int[] destSlotIndices = null;

        if (result.State == StateNeedsRestock)
        {
          var requiredItems = new NativeList<ItemData>(Allocator.TempJob);
          var mixerItemField = MixingStationUtilities.GetInputItemForProductSlot(station);
          ItemData mixerItem = mixerItemField?.SelectedItem != null ? new ItemData(mixerItemField.SelectedItem.GetDefaultInstance()) : ItemData.Empty;
          if (mixerItem.Id != "")
          {
            requiredItems.Add(mixerItem);
          }
          else
          {
            var refills = station.RefillList();
            foreach (var refill in refills)
              if (refill != null)
                requiredItems.Add(new ItemData(refill));
          }

          bool success = false;
          if (ManagedDictionaries.CacheServices.TryGetValue(property, out var cacheService))
          {
            foreach (var itemKey in requiredItems)
            {
              if (cacheService._itemToStorageCache.TryGetValue(new ItemKey(itemKey), out var storageKeys))
              {
                foreach (var storageKey in storageKeys)
                {
                  if (cacheService._storageSlotsCache.TryGetValue(storageKey, out var slots))
                  {
                    int totalQuantity = 0;
                    var slotIndices = new NativeList<int>(Allocator.Temp);
                    foreach (var slot in slots)
                    {
                      if (slot.Item.Equals(itemKey) && slot.Quantity > 0)
                      {
                        totalQuantity += slot.Quantity;
                        slotIndices.Add(slot.SlotIndex);
                      }
                    }
                    if (totalQuantity >= result.Quantity)
                    {
                      success = true;
                      sourceGuid = storageKey.Guid;
                      sourceSlotIndices = slotIndices.ToArray();
                      destGuid = station.GUID;
                      result.Quantity = totalQuantity;
                      result.Item = itemKey;
                      slotIndices.Dispose();
                      break;
                    }
                    slotIndices.Dispose();
                  }
                }
              }
              if (success) break;
            }
          }

          if (!success)
          {
            CoroutineRunner.Instance.RunCoroutine(disabledService.AddDisabledEntity(station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.MissingItem, requiredItems));
            requiredItems.Dispose();
            return;
          }
          requiredItems.Dispose();
        }
        else if (result.State == StateNeedsDelivery)
        {
          var destinations = new NativeList<(Guid, NativeList<int>, int)>(Allocator.TempJob);
          if (ManagedDictionaries.CacheServices.TryGetValue(property, out var cacheService) &&
              cacheService._itemToStorageCache.TryGetValue(new ItemKey(result.Item), out var storageKeys))
          {
            foreach (var storageKey in storageKeys)
            {
              if (cacheService._storageSlotsCache.TryGetValue(storageKey, out var slots))
              {
                var destSlots = new NativeList<int>(Allocator.Temp);
                int capacity = 0;
                foreach (var slot in slots)
                {
                  if (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(result.Item))
                  {
                    capacity += slot.StackLimit - slot.Quantity;
                    destSlots.Add(slot.SlotIndex);
                  }
                }
                if (capacity >= result.Quantity)
                {
                  destinations.Add((storageKey.Guid, destSlots, capacity));
                  break;
                }
                destSlots.Dispose();
              }
            }
          }

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
            var requiredItems = new NativeList<ItemData>(1, Allocator.TempJob);
            requiredItems.Add(result.Item);
            CoroutineRunner.Instance.RunCoroutine(disabledService.AddDisabledEntity(station.GUID, result.State - 1, DisabledEntityData.DisabledReasonType.NoDestination, requiredItems));
            requiredItems.Dispose();
            destinations.Dispose();
            return;
          }
          destinations.Dispose();
        }
        else if (result.State == StateHasOutput)
        {
          sourceGuid = station.GUID;
          sourceSlotIndices = new[] { station.OutputSlot.SlotIndex };
          destGuid = station.GUID;
          var loopSlots = new NativeList<int>(Allocator.Temp);
          foreach (var slot in station.ProductSlots)
            if (!slot.IsLocked && (slot.ItemInstance == null || slot.ItemInstance.AdvCanStackWith(result.Item.CreateItemInstance())))
              loopSlots.Add(slot.SlotIndex);
          destSlotIndices = loopSlots.Length > 0 ? loopSlots.ToArray().Take(3).ToArray() : null;
          loopSlots.Dispose();
        }

        var task = TaskDescriptor.Create(
            station.GUID, TaskName.MixingStation, result.State - 1, TaskEmployeeType.Chemist, 100,
            property.name, result.Item, result.Quantity,
            sourceGuid, sourceSlotIndices,
            destGuid, destSlotIndices,
            Time.time
        );

        if (task.TaskId != Guid.Empty)
        {
          dispatcher.Enqueue(task);
#if DEBUG
          logs.Add(new LogEntry
          {
            Message = $"Created and enqueued task {task.TaskId} for station {station.GUID}, state {result.State}",
            Level = Level.Info,
            Category = Category.MixingStation
          });
#endif
        }
      }

      public IEnumerator ExecuteCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
      {
        var logs = new NativeList<LogEntry>(Allocator.TempJob);
        try
        {
          List<TaskResult> results = new();
          yield return SmartExecution.Execute(
              uniqueId: $"Execute_{task.TaskId}",
              itemCount: 1,
              nonBurstDelegate: (start, count, inputs, outputs) =>
              {
                outputs.Add(new TaskResult(task, true));
              },
              nonBurstResultsDelegate: (List<TaskResult> r) => results.AddRange(r),
              inputs: new object[] { task },
              outputs: results,
              options: options
          );
          yield return ProcessLogs(logs);
        }
        finally
        {
          logs.Dispose();
        }
      }

      public IEnumerator FollowUpCoroutine(Employee employee, TaskDescriptor task, SmartExecutionOptions options)
      {
        var logs = new NativeList<LogEntry>(Allocator.TempJob);
        try
        {
          NativeList<TaskResult> results = new NativeList<TaskResult>(Allocator.TempJob);
          yield return SmartExecution.ExecuteBurst(
              uniqueId: $"FollowUp_{task.TaskId}",
              burstDelegate: (start, count, inputs, outputs) =>
              {
                outputs.Add(new TaskResult(task, true));
              },
              burstResultsDelegate: (NativeList<TaskResult> r) => results.AddRange(r),
              inputs: new NativeArray<object>(1, Allocator.TempJob) { [0] = task },
              outputs: results,
              options: options
          );
          yield return ProcessLogs(logs);
        }
        finally
        {
          logs.Dispose();
          results.Dispose();
        }
      }

      [BurstCompile]
      public static ValidationResultData ValidateEntityState(IStationAdapter station, NativeArray<SlotData> slots, NativeList<LogEntry> logs)
      {
        var result = new ValidationResultData { IsValid = false, State = StateInvalid };
        if (station.IsInUse)
        {
#if DEBUG
          logs.Add(new LogEntry
          {
            Message = $"Station {station.GUID} is in use",
            Level = Level.Verbose,
            Category = Category.MixingStation
          });
#endif
          return result;
        }

        ItemData outputItem = ItemData.Empty;
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
              State = StateHasOutput,
              Item = outputItem,
              Quantity = outputQuantity
            };
            return result;
          }

          result = new ValidationResultData
          {
            IsValid = true,
            State = StateNeedsDelivery,
            Item = outputItem,
            Quantity = outputQuantity
          };
          return result;
        }

        ItemData restockItem = ItemData.Empty;
        int restockQuantity = 0;
        for (int i = 0; i < slots.Length; i++)
        {
          var slot = slots[i];
          if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex))
          {
            int capacity = slot.StackLimit - slot.Quantity;
            if (capacity > 0)
            {
              restockItem = slot.Item.Id == "" ? new ItemData(station.InsertSlots.First(isl => isl.SlotIndex == slot.SlotIndex).ItemInstance) : slot.Item;
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
            State = StateNeedsRestock,
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
            State = StateReadyToOperate,
            Item = ItemData.Empty,
            Quantity = 0
          };
          return result;
        }

        result = new ValidationResultData { IsValid = true, State = StateIdle };
        return result;
      }
    }


    public class MixingStationTask : ITask
    {
      public Extensions.TaskName Type => Extensions.TaskName.MixingStation;
      public IEntitySelector EntitySelector => new MixingStationEntitySelector();

      public class MixingStationEntitySelector : IEntitySelector
      {
        [BurstCompile]
        public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
        {
          var entities = new NativeList<Guid>(allocator);
          if (ManagedDictionaries.IStations.TryGetValue(property, out var stations))
          {
            foreach (var station in stations.Values)
            {
              if (station.TypeOf == typeof(MixingStation) || station.TypeOf == typeof(MixingStationMk2))
                entities.Add(station.GUID);
            }
          }
#if DEBUG
          Log(Level.Verbose, $"Selected {entities.Length} mixing stations for {property.name}", Category.MixingStation);
#endif
          return entities;
        }
      }

      [BurstCompile]
      public void ProcessEntity(int index, NativeArray<Guid> entityGuids, NativeArray<Extensions.ValidationResultData> results, NativeList<LogEntry> logs, Property property, TaskService taskService, NativeArray<SlotData> slotData, DisabledEntityService disabledService, TaskDispatcher dispatcher)
      {
        if (index >= entityGuids.Length) return;
        var entityGuid = entityGuids[index];
        var station = taskService.ResolveEntity(entityGuid) as IStationAdapter;
        if (station == null)
        {
          logs.Add(new LogEntry { Message = $"Invalid entity for GUID {entityGuid}", Level = Level.Error, Category = Category.MixingStation });
          return;
        }

        var result = ValidateEntityState(station, slotData, logs);
        results[index] = result;

        if (!result.IsValid || result.State == (int)States.Invalid || result.State == (int)States.Idle)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"Invalid or idle state for station {station.GUID}", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          return;
        }

        Guid sourceGuid = Guid.Empty;
        int[] sourceSlotIndices = null;
        Guid destGuid = Guid.Empty;
        int[] destSlotIndices = null;

        if (result.State == (int)States.NeedsRestock)
        {
          var requiredItems = new NativeList<ItemData>(Allocator.TempJob);
          var mixerItemField = MixingStationUtilities.GetInputItemForProductSlot(station);
          ItemData mixerItem = mixerItemField?.SelectedItem != null ? new ItemData(mixerItemField.SelectedItem.GetDefaultInstance()) : ItemData.Empty;
          if (mixerItem.Id != "")
          {
            requiredItems.Add(mixerItem);
          }
          else
          {
            var refills = station.RefillList();
            foreach (var refill in refills)
              if (refill != null)
                requiredItems.Add(new ItemData(refill));
          }

          bool success = false;
          if (ManagedDictionaries.CacheServices.TryGetValue(property, out var cacheService))
          {
            foreach (var itemKey in requiredItems)
            {
              if (cacheService._itemToStorageCache.TryGetValue(new ItemKey(itemKey), out var storageKeys))
              {
                foreach (var storageKey in storageKeys)
                {
                  if (cacheService._storageSlotsCache.TryGetValue(storageKey, out var slots))
                  {
                    int totalQuantity = 0;
                    var slotIndices = new NativeList<int>(Allocator.Temp);
                    foreach (var slot in slots)
                    {
                      if (slot.Item.Equals(itemKey) && slot.Quantity > 0)
                      {
                        totalQuantity += slot.Quantity;
                        slotIndices.Add(slot.SlotIndex);
                      }
                    }
                    if (totalQuantity >= result.Quantity)
                    {
                      success = true;
                      sourceGuid = storageKey.Guid;
                      sourceSlotIndices = slotIndices.ToArray();
                      destGuid = station.GUID;
                      result.Quantity = totalQuantity;
                      result.Item = itemKey;
                      slotIndices.Dispose();
                      break;
                    }
                    slotIndices.Dispose();
                  }
                }
              }
              if (success) break;
            }
          }

          if (!success)
          {
            yield return disabledService.AddDisabledEntity(station.GUID, result.State - 1, Extensions.DisabledEntityData.DisabledReasonType.MissingItem, requiredItems);
            requiredItems.Dispose();
            return;
          }
          requiredItems.Dispose();
        }
        else if (result.State == (int)States.NeedsDelivery)
        {
          var destinations = new NativeList<(Guid, NativeList<int>, int)>(Allocator.TempJob);
          if (ManagedDictionaries.CacheServices.TryGetValue(property, out var cacheService) &&
              cacheService._itemToStorageCache.TryGetValue(new ItemKey(result.Item), out var storageKeys))
          {
            foreach (var storageKey in storageKeys)
            {
              if (cacheService._storageSlotsCache.TryGetValue(storageKey, out var slots))
              {
                var destSlots = new NativeList<int>(Allocator.Temp);
                int capacity = 0;
                foreach (var slot in slots)
                {
                  if (slot.Item.Id == "" || slot.Item.AdvCanStackWithBurst(result.Item))
                  {
                    capacity += slot.StackLimit - slot.Quantity;
                    destSlots.Add(slot.SlotIndex);
                  }
                }
                if (capacity >= result.Quantity)
                {
                  destinations.Add((storageKey.Guid, destSlots, capacity));
                  break;
                }
                destSlots.Dispose();
              }
            }
          }

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
            var requiredItems = new NativeList<ItemData>(1, Allocator.TempJob);
            requiredItems.Add(result.Item);
            yield return disabledService.AddDisabledEntity(station.GUID, result.State - 1, Extensions.DisabledEntityData.DisabledReasonType.NoDestination, requiredItems);
            requiredItems.Dispose();
            destinations.Dispose();
            return;
          }
          destinations.Dispose();
        }
        else if (result.State == (int)States.HasOutput)
        {
          sourceGuid = station.GUID;
          sourceSlotIndices = new[] { station.OutputSlot.SlotIndex };
          destGuid = station.GUID;
          var loopSlots = new NativeList<int>(Allocator.Temp);
          foreach (var slot in station.ProductSlots)
            if (!slot.IsLocked && (slot.ItemInstance == null || slot.ItemInstance.AdvCanStackWith(result.Item.CreateItemInstance())))
              loopSlots.Add(slot.SlotIndex);
          destSlotIndices = loopSlots.Length > 0 ? loopSlots.ToArray().Take(3).ToArray() : null;
          loopSlots.Dispose();
        }

        var task = CreateTaskDescriptor(
            station.GUID, Extensions.TaskName.MixingStation, result.State - 1, Extensions.TaskEmployeeType.Chemist, 100,
            property.name, result.Item, result.Quantity,
            sourceGuid, sourceSlotIndices,
            destGuid, destSlotIndices,
            Time.time
        );

        if (task.TaskId != Guid.Empty)
        {
          dispatcher.Enqueue(task);
#if DEBUG
          logs.Add(new LogEntry { Message = $"Created and enqueued task {task.TaskId} for station {station.GUID}, state {result.State}", Level = Level.Info, Category = Category.MixingStation });
#endif
        }
      }

      public Task ExecuteAsync(Employee employee, Extensions.TaskDescriptor task)
      {
        return Task.CompletedTask; // Implementation-specific
      }

      public Task FollowUpAsync(Employee employee, Extensions.TaskDescriptor task)
      {
        return Task.CompletedTask; // Implementation-specific
      }

      [BurstCompile]
      public static Extensions.ValidationResultData ValidateEntityState(IStationAdapter station, NativeArray<SlotData> slots, NativeList<LogEntry> logs)
      {
        var result = new Extensions.ValidationResultData { IsValid = false, State = (int)States.Invalid };
        if (station.IsInUse)
        {
#if DEBUG
          logs.Add(new LogEntry { Message = $"Station {station.GUID} is in use", Level = Level.Verbose, Category = Category.MixingStation });
#endif
          return result;
        }

        ItemData outputItem = ItemData.Empty;
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
            result = new Extensions.ValidationResultData
            {
              IsValid = true,
              State = (int)States.HasOutput,
              Item = outputItem,
              Quantity = outputQuantity
            };
            return result;
          }

          result = new Extensions.ValidationResultData
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
        for (int i = 0; i < slots.Length; i++)
        {
          var slot = slots[i];
          if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex))
          {
            int capacity = slot.StackLimit - slot.Quantity;
            if (capacity > 0)
            {
              restockItem = slot.Item.Id == "" ? new ItemData(station.InsertSlots.First(isl => isl.SlotIndex == slot.SlotIndex).ItemInstance) : slot.Item;
              restockQuantity = capacity;
              break;
            }
          }
        }

        if (restockItem.Id != "")
        {
          result = new Extensions.ValidationResultData
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
          result = new Extensions.ValidationResultData
          {
            IsValid = true,
            State = (int)States.ReadyToOperate,
            Item = ItemData.Empty,
            Quantity = 0
          };
          return result;
        }

        result = new Extensions.ValidationResultData { IsValid = true, State = (int)States.Idle };
        return result;
      }

      [BurstCompile]
      public static Extensions.TaskDescriptor CreateTaskDescriptor(
          Guid entityGuid, Extensions.TaskName type, int actionId, Extensions.TaskEmployeeType employeeType, int priority,
          string propertyName, ItemData item, int quantity,
          Guid pickupGuid, int[] pickupSlotIndices,
          Guid dropoffGuid, int[] dropoffSlotIndices,
          float creationTime)
      {
        return Extensions.TaskDescriptor.Create(
            entityGuid, type, actionId, employeeType, priority,
            propertyName, item, quantity,
            pickupGuid, pickupSlotIndices,
            dropoffGuid, dropoffSlotIndices,
            creationTime
        );
      }
    }

    public class MixingStationTask2 : ITask
    {
      private TaskService _taskService;
      private CacheService _cacheManager;
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
        [ReadOnly] public CacheService CacheManager;
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

          var storageKey = new StorageKey(entityGuid, StorageType.Station);
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

      public TaskName Type => TaskName.MixingStation;

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
        CacheService cacheManager,
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
        await Performance.Metrics.TrackExecutionAsync(nameof(ValidateEntityStateAsync), async () =>
        {
          if (entity is not IStationAdapter station)
          {
            Log(Level.Error, "Invalid station entity", Category.MixingStation);
            return;
          }

          var storageKey = new StorageKey(station.GUID, StorageType.Station);
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
          var requiredItems = new NativeList<ItemData>(Allocator.TempJob);
          var mixerItemField = MixingStationUtilities.GetInputItemForProductSlot(station);
          ItemData mixerItem = mixerItemField?.SelectedItem != null ? new ItemData(mixerItemField.SelectedItem.GetDefaultInstance()) : ItemData.Empty;
          if (mixerItem.Id != "")
          {
            requiredItems.Add(mixerItem);
          }
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
            var routine = Performance.Metrics.TrackExecutionCoroutine(nameof(CreateTaskForState) + "_NeedsRestock", FindStorageWithItemAsync(property, itemKey, result.Quantity));
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
              var requiredItems = new NativeList<ItemData>(1, Allocator.TempJob);
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
            var requiredItems = new NativeList<ItemData>(1, Allocator.TempJob);
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
            station.GUID, TaskName.MixingStation, result.State - 1, TaskEmployeeType.Chemist, 100,
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
        await Performance.Metrics.TrackExecutionAsync(nameof(ExecuteAsync), async () =>
        {
          if (employee.Type != Enum.Parse<EEmployeeType>(TaskEmployeeType.Chemist.ToString()))
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
              var storageKey = new StorageKey(station.GUID, StorageType.Station);
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
        Performance.Metrics.TrackExecutionCoroutine(
            nameof(FollowUpAsync) + "_" + task.Type + ":" + task.ActionId,
            taskService.CreateEmployeeSpecificTaskAsync(employee.GUID, task.EntityGuid, station.Buildable.ParentProperty, TaskName.MixingStation),
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

          ItemData outputItem = ItemData.Empty;
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

          ItemData restockItem = ItemData.Empty;
          int restockQuantity = 0;
          for (int i = 0; i < slots.Length; i++)
          {
            var slot = slots[i];
            if (station.InsertSlots.Any(isl => isl.SlotIndex == slot.SlotIndex))
            {
              int capacity = slot.StackLimit - slot.Quantity;
              if (capacity > 0)
              {
                restockItem = slot.Item.Id == "" ? new ItemData(station.InsertSlots.First(isl => isl.SlotIndex == slot.SlotIndex).ItemInstance) : slot.Item;
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
              Item = ItemData.Empty,
              Quantity = 0
            };
            return result;
          }

          result = new ValidationResultData { IsValid = true, State = (int)States.Idle };
          return result;
        }

        [BurstCompile]
        public static TaskDescriptor CreateTaskDescriptor(
            Guid entityGuid, TaskName type, int actionId, TaskEmployeeType employeeType, int priority,
            string propertyName, ItemData item, int quantity,
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
        IEmployees[employee.AssignedProperty][employee.GUID].AdvBehaviour.StartMovement([new PrioritizedRoute(request, priority: 1)], TaskName.MixingStation, async (emp, _, status) =>
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
        var movementTask = await Performance.Metrics.TrackExecutionAsync(nameof(NoLazyWorkers.Movement.Utilities.MoveToAsync),
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