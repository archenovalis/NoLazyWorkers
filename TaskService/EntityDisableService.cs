using ScheduleOne.Employees;
using UnityEngine;
using static NoLazyWorkers.Storage.Extensions;
using Unity.Collections;
using ScheduleOne.Property;
using Unity.Burst;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.TaskService.Extensions;
using System.Collections;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;
using NoLazyWorkers.TaskService.StationTasks;
using NoLazyWorkers.Extensions;
using NoLazyWorkers.Performance;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.TaskService.TaskService;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using static NoLazyWorkers.Extensions.PoolUtility;

namespace NoLazyWorkers.TaskService
{
  [SmartExecute]
  [BurstCompile]
  public struct DisableEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        where TSetupOutput : struct, IDisposable
        where TValidationSetupOutput : struct, IDisposable
  {
    private readonly ITask<TSetupOutput, TValidationSetupOutput> _task;
    public NativeParallelHashMap<Guid, DisabledEntityData> DisabledEntities;

    public DisableEntitiesBurst(ITask<TSetupOutput, TValidationSetupOutput> task)
    {
      _task = task;
      DisabledEntities = default;
    }

    public void Execute(int index, NativeArray<ValidationResultData> results, NativeList<DisabledEntityData> outputs, NativeList<LogEntry> logs)
    {
      var result = results[index];
      if (!result.IsValid)
      {
        var requiredItems = new NativeList<ItemData>(1, Allocator.Persistent);
        requiredItems.Add(result.Item);
        var data = new DisabledEntityData
        {
          ActionId = result.State - 1,
          ReasonType = DisabledEntityData.DisabledReasonType.MissingItem,
          RequiredItems = requiredItems,
          AnyItem = true,
          State = result.State
        };
        DisabledEntities[result.EntityGuid] = data;
        outputs.Add(data);
        logs.Add(new LogEntry
        {
          Message = $"Disabled entity {result.EntityGuid} for action {data.ActionId}, reason: {data.ReasonType}",
          Level = Level.Info,
          Category = Category.Tasks
        });
      }
    }
  }

  /// <summary>
  /// Manages disabled entities and their associated data.
  /// </summary>
  public class EntityDisableService
  {
    internal NativeParallelHashMap<Guid, DisabledEntityData> _disabledEntities;
    private readonly NativeListPool<DisabledEntityData> _outputPool;
    private readonly Property _property;

    public EntityDisableService(Property property)
    {
      _property = property;
      _disabledEntities = new NativeParallelHashMap<Guid, DisabledEntityData>(100, Allocator.Persistent);
      _outputPool = new NativeListPool<DisabledEntityData>(() => new NativeList<DisabledEntityData>(100, Allocator.TempJob));
#if DEBUG
      Log(Level.Info, $"Initialized EntityDisableService for property {_property.name}", Category.Tasks);
#endif
    }

    public static EntityDisableService GetOrCreateService(Property property)
    {
      if (!ManagedDictionaries.DisabledEntityServices.TryGetValue(property, out var service) || service == null)
      {
        service = new EntityDisableService(property);
        ManagedDictionaries.DisabledEntityServices[property] = service;
      }
      return service;
    }

    public IEnumerator AddDisabledEntity<TSetupOutput, TValidationSetupOutput>(
        Guid guid, int actionId, DisabledEntityData.DisabledReasonType reasonType,
        NativeList<ItemData> requiredItems, bool anyItem, Property property, TaskName taskType)
        where TSetupOutput : unmanaged, IDisposable
        where TValidationSetupOutput : unmanaged, IDisposable
    {
      var logs = new NativeList<LogEntry>(Allocator.TempJob);
      var outputs = new NativeList<DisabledEntityData>(Allocator.TempJob);
      var entity = new NativeList<Guid>(1, Allocator.TempJob) { [0] = guid };
      var validationResults = new NativeList<ValidationResultData>(1, Allocator.TempJob);
      var results = new NativeArray<ValidationResultData>(1, Allocator.TempJob);
      try
      {
        var taskRegistry = TaskServiceManager.GetRegistry(property);
        var task = taskRegistry.GetTask(taskType) as ITask<TSetupOutput, TValidationSetupOutput>;
        if (task == null)
        {
          Log(Level.Error, $"Task type {taskType} not registered", Category.Tasks);
          yield return ProcessLogs(logs);
          yield break;
        }
        var processor = new TaskProcessor<TSetupOutput, TValidationSetupOutput>(task);
        var setupOutputs = new List<TSetupOutput>();
        var setupInputs = new[] { 0 };
#if DEBUG
        Log(Level.Info, $"Executing setup for disabling entity {guid}, task {taskType}", Category.Tasks);
#endif
        yield return SmartExecution.Execute<int, TSetupOutput>(
            uniqueId: $"{property.name}_{taskType}_Disable_Setup",
            itemCount: 1,
            nonBurstDelegate: processor.SetupDelegate,
            inputs: setupInputs,
            outputs: setupOutputs
        );
        var selectJob = new SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        {
          SelectEntitiesDelegate = processor.SelectEntitiesDelegate
        };
        yield return SmartExecution.ExecuteBurst<int, Guid>(
            uniqueId: $"{property.name}_{taskType}_Disable_SelectEntities",
            burstDelegate: selectJob.Execute,
            outputs: entity
        );
        var validationSetupOutputs = new List<TValidationSetupOutput>();
        var validationSetupInputs = entity;
        if (processor.ValidationSetupDelegate != null && entity.Length > 0)
        {
#if DEBUG
          Log(Level.Info, $"Executing validation setup for disabling entity {guid}, task {taskType}", Category.Tasks);
#endif
          yield return SmartExecution.Execute<Guid, TValidationSetupOutput>(
              uniqueId: $"{property.name}_{taskType}_Disable_ValidationSetup",
              itemCount: entity.Length,
              nonBurstDelegate: processor.ValidationSetupDelegate,
              inputs: validationSetupInputs.ToArray(),
              outputs: validationSetupOutputs
          );
        }
        var validateJob = new ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        {
          ValidateEntityDelegate = processor.ValidateEntityDelegate
        };
        yield return SmartExecution.ExecuteBurstFor<Guid, ValidationResultData>(
            uniqueId: $"{property.name}_{taskType}_Disable_ValidateEntities",
            itemCount: entity.Length,
            burstForDelegate: validateJob.ExecuteFor,
            inputs: entity,
            outputs: validationResults
        );
        yield return SmartExecution.ExecuteBurstFor<ValidationResultData, DisabledEntityData>(
            uniqueId: $"{property.name}_{taskType}_Disable_Entities",
            itemCount: validationResults.Length,
            burstForDelegate: processor.DisableEntityDelegate,
            inputs: validationResults.AsArray(),
            outputs: outputs
        );
        yield return ProcessLogs(logs);
      }
      finally
      {
        if (logs.IsCreated) logs.Dispose();
        if (outputs.IsCreated) outputs.Dispose();
        if (entity.IsCreated) entity.Dispose();
        if (validationResults.IsCreated) validationResults.Dispose();
        if (results.IsCreated) results.Dispose();
      }
    }

    public void Dispose()
    {
      foreach (var data in _disabledEntities.GetValueArray(Allocator.Temp))
        if (data.RequiredItems.IsCreated) data.RequiredItems.Dispose();
      if (_disabledEntities.IsCreated) _disabledEntities.Dispose();
      _outputPool.Dispose();
#if DEBUG
      Log(Level.Info, $"Disposed EntityDisableService for property {_property.name}", Category.Tasks);
#endif
    }
  }
}