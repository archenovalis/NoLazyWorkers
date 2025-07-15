using ScheduleOne.ItemFramework;
using ScheduleOne.Property;
using static NoLazyWorkers.Storage.Extensions;
using Unity.Collections;
using NoLazyWorkers.Performance;
using Unity.Burst;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.Extensions;
using NoLazyWorkers.Storage;

namespace NoLazyWorkers.TaskService
{
  public class EntityStateService
  {
    private readonly Property _property;
    private readonly CacheService _cacheService;
    private readonly Dictionary<EntityType, ValidateEmployeeDelegate> _validateEmployeeDelegates;
    private readonly Dictionary<EntityType, ValidateStationDelegate> _validateStationDelegates;
    private bool _isInitialized;

    public EntityStateService(Property property)
    {
      _property = property;
      _cacheService = CacheService.GetOrCreateCacheService(property);
      _validateEmployeeDelegates = new Dictionary<EntityType, ValidateEmployeeDelegate>
            {
                { EntityType.Chemist, new ValidateEmployeeDelegate(ValidateChemistStateBurst, ValidateChemistStateResults) }
            };
      _validateStationDelegates = new Dictionary<EntityType, ValidateStationDelegate>
            {
                { EntityType.MixingStation, new ValidateStationDelegate(ValidateMixingStationStateBurst, ValidateMixingStationStateResults) }
            };
      StorageManager._slotListeners.Add(OnSlotUpdate);
      _isInitialized = true;
#if DEBUG
      Log(Level.Info, $"EntityStateService initialized for property {_property.name}", Category.EntityState);
#endif
    }

    public void Dispose()
    {
      StorageManager._slotListeners.Remove(OnSlotUpdate);
      _isInitialized = false;
#if DEBUG
      Log(Level.Info, $"EntityStateService disposed for property {_property.name}", Category.EntityState);
#endif
    }

    public static EntityStateService GetOrCreateService(Property property) => new EntityStateService(property);

    private void OnSlotUpdate(ItemSlot slot, ItemInstance instance, int change)
    {
      if (!_isInitialized) return;
      var slotKey = slot.GetSlotKey();
      if (!_cacheService._guidToType.TryGetValue(slotKey.EntityGuid, out var typeInfo)) return;
      if (typeInfo.StorageType == StorageType.Station || typeInfo.StorageType == StorageType.Employee)
      {
        CoroutineRunner.Instance.RunCoroutine(ValidateEntityState(slotKey.EntityGuid));
      }
    }

    private IEnumerator ValidateEntityState(Guid guid)
    {
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      var outputs = new NativeList<int>(1, Allocator.TempJob);
      try
      {
        if (!_cacheService._guidToType.TryGetValue(guid, out var typeInfo))
        {
          Log(Level.Error, $"No type info for entity {guid}", Category.EntityState);
          yield break;
        }
        var entityType = typeInfo.EntityType;
        if (!_validateEmployeeDelegates.TryGetValue(entityType, out var validateDelegate))
        {
          Log(Level.Error, $"No validation delegate for entity type {entityType}", Category.EntityState);
          yield break;
        }
        var input = guid;
        yield return SmartExecution.ExecuteBurst<Guid, int>(
            uniqueId: $"ValidateState_{entityType}_{guid}",
            burstDelegate: validateDelegate.ValidateBurst,
            input: input,
            outputs: outputs,
            burstResultsDelegate: (results, logs) => validateDelegate.ResultsDelegate(results, logs, _cacheService._entityStates, guid)
        );
        yield return ProcessLogs(logs);
      }
      finally
      {
        if (logs.IsCreated) logs.Dispose();
        if (outputs.IsCreated) outputs.Dispose();
      }
    }

    [BurstCompile]
    private struct ValidateEmployeeDelegate
    {
      public readonly Action<Guid, NativeList<int>, NativeList<LogEntry>> ValidateBurst;
      public readonly Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> ResultsDelegate;
      public ValidateEmployeeDelegate(
          Action<Guid, NativeList<int>, NativeList<LogEntry>> validateBurst,
          Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> resultsDelegate)
      {
        ValidateBurst = validateBurst;
        ResultsDelegate = resultsDelegate;
      }
    }

    [BurstCompile]
    private struct ValidateStationDelegate
    {
      public readonly Action<Guid, NativeList<int>, NativeList<LogEntry>, StationData> ValidateBurst;
      public readonly Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> ResultsDelegate;
      public ValidateStationDelegate(
          Action<Guid, NativeList<int>, NativeList<LogEntry>, StationData> validateBurst,
          Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> resultsDelegate)
      {
        ValidateBurst = validateBurst;
        ResultsDelegate = resultsDelegate;
      }
    }

    [BurstCompile]
    private void ValidateMixingStationStateBurst(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs, StationData station)
    {
      int state = station.IsInUse ? 1 : 0; // Idle = 0, ReadyToMix = 1
      outputs.Add(state);
#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Validated MixingStation {input} state: {state}",
        Level = Level.Verbose,
        Category = Category.EntityState
      });
#endif
    }

    private void ValidateMixingStationStateResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
    {
      if (results.Length > 0)
      {
        entityStates[guid] = results[0];
        logs.Add(new LogEntry
        {
          Message = $"Updated MixingStation {guid} state to {results[0]}",
          Level = Level.Info,
          Category = Category.EntityState
        });
      }
    }

    [BurstCompile]
    private void ValidateChemistStateBurst(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs)
    {
      int state = 0; // Placeholder
      outputs.Add(state);
#if DEBUG
      logs.Add(new LogEntry
      {
        Message = $"Validated Chemist {input} state: {state}",
        Level = Level.Verbose,
        Category = Category.EntityState
      });
#endif
    }

    private void ValidateChemistStateResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
    {
      if (results.Length > 0)
      {
        entityStates[guid] = results[0];
#if DEBUG
        logs.Add(new LogEntry
        {
          Message = $"Updated Chemist {guid} state to {results[0]}",
          Level = Level.Info,
          Category = Category.EntityState
        });
#endif
      }
    }
  }
}