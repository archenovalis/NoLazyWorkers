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

namespace NoLazyWorkers.TaskService
{
  public class EntityStates
  {
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
      internal static Dictionary<EntityType, ValidateEmployeeBurst> ValidateEmployeeDelegates = new()
      {
          { EntityType.Chemist, new ValidateEmployeeBurst(ValidateChemistStateBurst, ValidateChemistStateResults) }
      };

      /// <summary>
      /// Validates the state of a chemist entity in a burst-compiled context.
      /// </summary>
      /// <param name="input">The unique identifier of the chemist entity.</param>
      /// <param name="outputs">List to store state results.</param>
      /// <param name="logs">List to store log entries.</param>
      [BurstCompile]
      internal static void ValidateChemistStateBurst(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs)
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

      /// <summary>
      /// Processes validation results for a chemist entity.
      /// </summary>
      /// <param name="results">List of validation results.</param>
      /// <param name="logs">List to store log entries.</param>
      /// <param name="entityStates">Hash map to store entity states.</param>
      /// <param name="guid">The unique identifier of the entity.</param>
      internal static void ValidateChemistStateResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
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

    internal static class Stations
    {
      internal static Dictionary<EntityType, ValidateStationBurst> ValidateStationDelegates = new()
      {
          { EntityType.MixingStation, new ValidateStationBurst(ValidateMixingStationStateBurst, ValidateMixingStationStateResults) }
      };


      /// <summary>
      /// Validates the state of a mixing station in a burst-compiled context.
      /// </summary>
      /// <param name="input">The unique identifier of the station.</param>
      /// <param name="outputs">List to store state results.</param>
      /// <param name="logs">List to store log entries.</param>
      /// <param name="station">The station data for validation.</param>
      [BurstCompile]
      internal static void ValidateMixingStationStateBurst(Guid input, NativeList<int> outputs, NativeList<LogEntry> logs, StationData station)
      {
        int state;
        if (station.IsInUse)//TODO: add IsInUse listener?
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
          Message = $"Validated MixingStation {input} state: {Enum.GetName(typeof(EntityStates.MixingStation), state)}",
          Level = Level.Info,
          Category = Category.EntityState
        });
      }

      /// <summary>
      /// Processes validation results for a mixing station.
      /// </summary>
      /// <param name="results">List of validation results.</param>
      /// <param name="logs">List to store log entries.</param>
      /// <param name="entityStates">Hash map to store entity states.</param>
      /// <param name="guid">The unique identifier of the station.</param>
      [BurstCompile]
      internal static void ValidateMixingStationStateResults(NativeList<int> results, NativeList<LogEntry> logs, NativeParallelHashMap<Guid, int> entityStates, Guid guid)
      {
        if (results.Length > 0)
        {
          entityStates[guid] = results[0];
          logs.Add(new LogEntry
          {
            Message = $"Updated MixingStation {guid} state to {Enum.GetName(typeof(EntityStates.MixingStation), results[0])}",
            Level = Level.Info,
            Category = Category.EntityState
          });
        }
      }
    }
  }

  /// <summary>
  /// Manages entity state validation and updates.
  /// </summary>
  public class EntityStateService
  {
    private readonly Property _property;
    private readonly TaskService _taskService;
    private readonly CacheService _cacheService;
    private readonly Dictionary<EntityType, ValidateEmployeeBurst> _validateEmployeeDelegates;
    private readonly Dictionary<EntityType, ValidateStationBurst> _validateStationDelegates;
    internal NativeParallelHashMap<Guid, int> EntityStates = new(20, Allocator.Persistent);
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
      _validateEmployeeDelegates = Delegates.Employee.ValidateEmployeeDelegates;
      _validateStationDelegates = Delegates.Stations.ValidateStationDelegates;
      _isInitialized = true;
      Log(Level.Info, $"EntityStateService initialized for property {_property.name}", Category.EntityState);
    }

    /// <summary>
    /// Disposes of the service and clears validation delegates.
    /// </summary>
    public void Dispose()
    {
      if (EntityStates.IsCreated) EntityStates.Dispose();
      _validateEmployeeDelegates.Clear();
      _validateStationDelegates.Clear();
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
        if (_validateEmployeeDelegates.TryGetValue(entityType, out var employeeDelegate))
        {
          yield return SmartExecution.Smart.ExecuteBurst<Guid, int, ValidateEmployeeBurst>(
              uniqueId: $"ValidateState_{entityType}_{guid}",
              burstAction: employeeDelegate.ValidateEmployeeDelegate,
              input: guid,
              outputs: outputs,
              logs: logs,
              burstResultsAction: (results, logs) => employeeDelegate.ResultsDelegate(results, logs, EntityStates, guid)
          );
        }
        else if (_validateStationDelegates.TryGetValue(entityType, out var stationDelegate))
        {
          StationData stationData = _cacheService.StationDataCache.ContainsKey(guid) ? _cacheService.StationDataCache[guid] : default;
          yield return SmartExecution.Smart.ExecuteBurst<Guid, int, ValidateStationBurst>(
              uniqueId: $"ValidateState_{entityType}_{guid}",
              burstAction: (input, outputs, logs) => stationDelegate.ValidateStationDelegate(input, outputs, logs, stationData),
              input: guid,
              outputs: outputs,
              logs: logs,
              burstResultsAction: (results, logs) => stationDelegate.ResultsDelegate(results, logs, EntityStates, guid)
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

    /// <summary>
    /// Defines delegates for employee state validation in a burst-compiled context.
    /// </summary>
    [BurstCompile]
    internal struct ValidateEmployeeBurst
    {
      public readonly Action<Guid, NativeList<int>, NativeList<LogEntry>> ValidateEmployeeDelegate;
      public readonly Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> ResultsDelegate;
      /// <summary>
      /// Initializes a new instance of the ValidateEmployeeBurst struct.
      /// </summary>
      /// <param name="validateBurst">Delegate for validating employee state.</param>
      /// <param name="resultsDelegate">Delegate for processing validation results.</param>
      public ValidateEmployeeBurst(
          Action<Guid, NativeList<int>, NativeList<LogEntry>> validateBurst,
          Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> resultsDelegate)
      {
        ValidateEmployeeDelegate = validateBurst;
        ResultsDelegate = resultsDelegate;
      }
    }

    /// <summary>
    /// Defines delegates for station state validation in a burst-compiled context.
    /// </summary>
    [BurstCompile]
    internal struct ValidateStationBurst
    {
      public readonly Action<Guid, NativeList<int>, NativeList<LogEntry>, StationData> ValidateStationDelegate;
      public readonly Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> ResultsDelegate;
      /// <summary>
      /// Initializes a new instance of the ValidateStationBurst struct.
      /// </summary>
      /// <param name="validateBurst">Delegate for validating station state.</param>
      /// <param name="resultsDelegate">Delegate for processing validation results.</param>
      public ValidateStationBurst(
          Action<Guid, NativeList<int>, NativeList<LogEntry>, StationData> validateBurst,
          Action<NativeList<int>, NativeList<LogEntry>, NativeParallelHashMap<Guid, int>, Guid> resultsDelegate)
      {
        ValidateStationDelegate = validateBurst;
        ResultsDelegate = resultsDelegate;
      }
    }
  }
}