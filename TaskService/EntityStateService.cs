using ScheduleOne.ItemFramework;
using ScheduleOne.Property;
using static NoLazyWorkers.CacheManager.Extensions;
using Unity.Collections;
using NoLazyWorkers.SmartExecution;
using Unity.Burst;
using System.Collections;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.Extensions;
using NoLazyWorkers.CacheManager;
using static NoLazyWorkers.TaskService.EntityStateService;
using static NoLazyWorkers.TaskService.TaskService;
using static NoLazyWorkers.TaskService.Delegates;
using NoLazyWorkers.Storage;

namespace NoLazyWorkers.TaskService
{
  public class EntityStates
  {
    public enum Chemist
    {
      Invalid = 0,
    }

    public enum MixingStation
    {
      Invalid = 0,
      NeedsRestock = 1,
      ReadyToOperate = 2,
      HasOutput = 3
    }
  }

  public static class Delegates
  {
    internal static class Employee
    {
      internal static Dictionary<EntityType, IValidateEntity> ValidateEmployeeStructs = new()
        {
            { EntityType.Chemist, new ValidateChemistStruct() }
        };
    }
    internal static class Stations
    {
      internal static Dictionary<EntityType, IValidateEntity> ValidateStationStructs = new()
        {
            { EntityType.MixingStation, new ValidateMixingStationStruct() }
        };
    }

    [BurstCompile]
    internal struct ValidateChemistStruct : IValidateEntity
    {
      public void Validate(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs, object data = null)
      {
        int state = 0; // Placeholder
        outputs.Add(state);
        logs.Add(new LogEntry
        {
          Message = $"Validated Chemist {input} state: {state}",
          Level = Level.Info,
          Category = Category.EntityState
        });
      }

      public void ProcessResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
      {
        if (results.Length > 0)
        {
          entityStates[guid] = results[0];
          logs.Add(new LogEntry
          {
            Message = $"Updated Chemist {guid} state to {results[0]}",
            Level = Level.Info,
            Category = Category.EntityState
          });
        }
      }
    }

    [BurstCompile]
    internal struct ValidateMixingStationStruct : IValidateEntity
    {
      public void Validate(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs, object data = null)
      {
        StationData station = (StationData)data; // Cast passed stationData
        int state;
        if (station.IsInUse)
        {
          state = (int)EntityStates.MixingStation.Invalid;
        }
        else if (station.OutputSlot.Quantity > 0)
        {
          state = (int)EntityStates.MixingStation.HasOutput;
        }
        else if (station.InsertSlots[0].Quantity < station.StartThreshold)
        {
          state = (int)EntityStates.MixingStation.NeedsRestock;
        }
        else if (station.InsertSlots.Length > 0 && station.InsertSlots[0].Quantity >= station.StartThreshold && station.ProductSlots.Length > 0 && station.ProductSlots[0].Quantity >= station.StartThreshold)
        {
          state = (int)EntityStates.MixingStation.ReadyToOperate;
        }
        else
        {
          state = (int)EntityStates.MixingStation.Invalid;
        }
        outputs.Add(state);
        logs.Add(new LogEntry
        {
          Message = $"Validated MixingStation {input} state: {state}",
          Level = Level.Info,
          Category = Category.EntityState
        });
      }

      public void ProcessResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
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
    }

    [BurstCompile]
    internal struct ValidateEntityExampleStruct : IValidateEntity
    {
      public void Validate(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs, object data = null)
      {
        int state = 0; // Placeholder
        outputs.Add(state);
        logs.Add(new LogEntry
        {
          Message = $"Validated Chemist {input} state: {state}",
          Level = Level.Info,
          Category = Category.EntityState
        });
      }

      public void ProcessResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
      {
        if (results.Length > 0)
        {
          entityStates[guid] = results[0];
          logs.Add(new LogEntry
          {
            Message = $"Updated Chemist {guid} state to {results[0]}",
            Level = Level.Info,
            Category = Category.EntityState
          });
        }
      }
    }
  }

  public interface IValidateEntity
  {
    void Validate(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs, object data = null);
    void ProcessResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid);
  }

  /// <summary>
  /// Manages entity state validation and updates.
  /// </summary>
  public class EntityStateService
  {
    private readonly Property _property;
    private readonly TaskService _taskService;
    private readonly CacheService _cacheService;
    private readonly Dictionary<EntityType, IValidateEntity> _validateEmployeeStructs;
    private readonly Dictionary<EntityType, IValidateEntity> _validateStationStructs;
    internal NativeParallelHashMap<Guid, int> EntityStates = new(20, Allocator.Persistent);
    internal NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntities = new(20, Allocator.Persistent);

    private bool _isInitialized;

    /// <summary>
    /// Initializes a new instance of the EntityStateService class.
    /// </summary>
    /// <param name="property">The property associated with this service.</param>
    public EntityStateService(Property property)
    {
      _property = property ?? throw new ArgumentNullException(nameof(property));
      _taskService = TaskServiceManager.GetOrCreateService(property);
      _cacheService = CacheService.GetOrCreateService(property);
      _validateEmployeeStructs = Delegates.Employee.ValidateEmployeeStructs;
      _validateStationStructs = Delegates.Stations.ValidateStationStructs;
      _isInitialized = true;
      Log(Level.Info, $"EntityStateService initialized for property {_property.name}", Category.EntityState);
    }

    /// <summary>
    /// Disposes of the service and clears validation delegates.
    /// </summary>
    public void Dispose()
    {
      if (EntityStates.IsCreated) EntityStates.Dispose();
      if (DisabledEntities.IsCreated)
      {
        foreach (var kvp in DisabledEntities)
          kvp.Value.RequiredItems.Dispose();
        DisabledEntities.Dispose();
      }
      _validateEmployeeStructs.Clear();
      _validateStationStructs.Clear();
      _isInitialized = false;
      Log(Level.Info, $"EntityStateService disposed for property {_property.name}", Category.EntityState);
    }

    /// <summary>
    /// Handles slot updates and triggers entity state validation if applicable.
    /// </summary>
    /// <param name="slot">The updated item slot.</param>
    public void OnSlotUpdate(ItemSlot slot)
    {
      if (!_isInitialized) return;
      var slotKey = slot.GetSlotKey();
      if (!_cacheService.GuidToType.TryGetValue(slotKey.EntityGuid, out var typeInfo)) return;
      if (typeInfo.StorageType == StorageType.Station || typeInfo.StorageType == StorageType.Employee)
      {
        CoroutineRunner.Instance.RunCoroutine(ValidateEntityState(slotKey.EntityGuid));
      }
    }

    /// <summary>
    /// Handles configuration updates for an entity.
    /// </summary>
    /// <param name="guid">The unique identifier of the entity.</param>
    public void OnConfigUpdate(Guid guid)
    {
      if (!_isInitialized) return;
      CoroutineRunner.Instance.RunCoroutine(ValidateEntityState(guid));
    }

    /// <summary>
    /// Validates the state of a specified entity.
    /// </summary>
    /// <param name="guid">The unique identifier of the entity.</param>
    /// <returns>An IEnumerator for coroutine execution.</returns>
    private IEnumerator ValidateEntityState(Guid guid)
    {
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      var outputs = new NativeList<int>(1, Allocator.TempJob);
      var scope = new DisposableScope(_taskService);

      scope.Add(logs);
      scope.Add(outputs);

      try
      {
        if (!_cacheService.GuidToType.TryGetValue(guid, out var typeInfo))
        {
          Log(Level.Error, $"No type info for entity {guid}", Category.EntityState);
          yield break;
        }
        var entityType = typeInfo.EntityType;
        if (_validateEmployeeStructs.TryGetValue(entityType, out var employeeStruct))
        {
          yield return SmartExecution.Smart.ExecuteBurst<Guid, int, ValidateEntityExampleStruct>(
              uniqueId: $"ValidateState_{entityType}_{guid}",
              burstAction: (input, outList, logList) => employeeStruct.Validate(input, outList, logList),
              input: guid,
              outputs: outputs,
              logs: logs,
              burstResultsAction: (results, logList) => employeeStruct.ProcessResults(results, logList, EntityStates, guid)
          );
        }
        else if (_validateStationStructs.TryGetValue(entityType, out var stationStruct))
        {
          StationData stationData = _cacheService.StationDataCache.ContainsKey(guid) ? _cacheService.StationDataCache[guid] : default;
          yield return SmartExecution.Smart.ExecuteBurst<Guid, int, ValidateEntityExampleStruct>(
              uniqueId: $"ValidateState_{entityType}_{guid}",
              burstAction: (input, outList, logList) => stationStruct.Validate(input, outList, logList, stationData),
              input: guid,
              outputs: outputs,
              logs: logs,
              burstResultsAction: (results, logList) => stationStruct.ProcessResults(results, logList, EntityStates, guid)
          );
        }
        else
        {
          Log(Level.Error, $"No validation delegate for entity type {entityType}", Category.EntityState);
          yield break;
        }
        yield return ProcessLogs(logs);
      }
      finally
      {
        scope.Dispose();
      }
    }
  }
}