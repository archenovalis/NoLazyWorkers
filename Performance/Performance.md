# NoLazyWorkers.Performance API Documentation

**Date**: July 9, 2025

## Overview

The `NoLazyWorkers.Performance` namespace provides a high-performance job scheduling and execution system optimized for Unity, leveraging Unity's Job System, Burst compilation, and dynamic profiling to minimize main thread impact. It includes job wrappers, execution time tracking, dynamic batch size load spreading, and smart execution strategies for single items, multiple items, and transform operations. The system supports both Burst-compiled and non-Burst delegates, offering flexibility for non-Burst scenarios while maintaining performance optimizations.

Delegates for Burst jobs must be Burst-compiled and use non-nullable fields (e.g., no interfaces like `ITask` or managed objects like `TaskDispatcher`). Non-Burst delegates are supported for main thread or coroutine execution. The system dynamically selects the optimal execution path (main thread, coroutine, or job) based on performance metrics. Heavy methods should use smart execution for load spreading. Non-Burst execution handles potentially heavy iterations when Burst compilation is not feasible.

Delegates must use `Deferred.LogEntry` for Job compatibility, with `yield return Deferred.ProcessLogs` for log processing. Non-Burst execution uses `Log(Level.Verbose/Info/Warning/Error/StackTrace, "message", Category.*)`.

```csharp
public static class Deferred
{
  /// <summary>
  /// Processes a list of log entries, spreading load across frames using SmartExecution.
  /// </summary>
  public static IEnumerator ProcessLogs(NativeList<LogEntry> logs)
  {...}

/// <summary>
  /// Represents a log entry for deferred logging in Burst jobs.
  /// </summary>
  [BurstCompile]
  public struct LogEntry
  {
    public FixedString128Bytes Message;
    public Level Level;
    public Category Category;
  }
}
```

## Choosing Between Structs and Direct Methods

When implementing Burst-compiled delegates for `SmartExecution.ExecuteBurst` or `ExecuteBurstFor`, developers must decide whether to encapsulate logic in a struct or use a direct method. This choice impacts performance, modularity, and maintainability.

### When to Use a Struct

- **Use Case**: Complex operations requiring multiple fields, reusable logic across multiple calls, or operations that manage native collections (e.g., `NativeList<T>`, `NativeArray<T>`).
- **Advantages**:
  - Encapsulates state and logic, improving modularity.
  - Allows passing multiple parameters (e.g., collections, configuration data) to the delegate.
  - Supports complex disposal logic for native collections.
- **Constraints**:
  - Structs must use only non-nullable, Burst-compatible fields (e.g., `int`, `Guid`, `NativeArray<T>`, `FixedString128Bytes`). Avoid interfaces (e.g., `ITask`), managed objects (e.g., `TaskDispatcher`), or reference types.
  - The `Execute` method must match the delegate signature: `Action<NativeArray<TInput>, NativeList<TOutput>>` for `ExecuteBurst` or `Action<int, NativeArray<TInput>, NativeList<TOutput>>` for `ExecuteBurstFor`.
  - Native collections (e.g., `NativeList<T>`) must be disposed either by the struct (for temporary collections) or by an owning system (for persistent collections). Document disposal responsibilities clearly.
- **Example** (Struct for Complex Job):

```csharp
[BurstCompile]
public struct CreateTasksBurst<TSetupOutput, TValidationSetupOutput>
    where TSetupOutput : struct, IDisposable
    where TValidationSetupOutput : struct, IDisposable
{
    public Action<int, NativeArray<ValidationResultData>, NativeList<TaskResult>> CreateTaskDelegate;
    public NativeList<LogEntry> Logs;

    public void Execute(int index, NativeArray<ValidationResultData> inputs, NativeList<TaskResult> outputs)
    {
        CreateTaskDelegate(index, inputs, outputs);
        Logs.Add(new LogEntry
        {
            Message = $"Created {outputs.Length} tasks",
            Level = Level.Info,
            Category = Category.Tasks
        });
    }
}

private IEnumerator CreateTaskGeneric<TSetupOutput, TValidationSetupOutput>(
    Guid employeeGuid, Guid entityGuid, Property property, ITask task,
    NativeList<LogEntry> logs, NativeList<TaskResult> taskResults, NativeList<Guid> entityGuids,
    NativeList<ValidationResultData> validationResults)
    where TSetupOutput : unmanaged, IDisposable
    where TValidationSetupOutput : unmanaged, IDisposable
{
    if (!(task is ITask<TSetupOutput, TValidationSetupOutput> genericTask))
    {
        Log(Level.Error, $"Task {task.Type} does not support generic type {typeof(TSetupOutput)}", Category.Tasks);
        yield break;
    }

    using (var scope = new DisposableScope())
    {
        var processor = new TaskProcessor<TSetupOutput, TValidationSetupOutput>(task);
        var setupOutputs = new List<TSetupOutput>();
        var setupInputs = new[] { 0 };
        Log(Level.Info, $"Executing setup for task {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
        yield return SmartExecution.Execute<int, TSetupOutput>(
            uniqueId: $"{property.name}_{task.Type}_Setup",
            itemCount: 1,
            nonBurstDelegate: processor.SetupDelegate
        );

        var selectJob = new SelectEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        {
            Logs = logs,
            SelectEntitiesDelegate = processor.SelectEntitiesDelegate
        };
        yield return SmartExecution.ExecuteBurst<int, Guid>(
            uniqueId: $"{property.name}_{task.Type}_SelectEntities",
            burstDelegate: selectJob.Execute
        );

        var validationSetupOutputs = new List<TValidationSetupOutput>();
        var validationSetupInputs = entityGuids.ToArray();
        if (processor.ValidationSetupDelegate != null && entityGuids.Length > 0)
        {
            Log(Level.Info, $"Executing validation setup for task {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
            yield return SmartExecution.Execute<Guid, TValidationSetupOutput>(
                uniqueId: $"{property.name}_{task.Type}_ValidationSetup",
                itemCount: entityGuids.Length,
                nonBurstDelegate: processor.ValidationSetupDelegate
            );
        }

        var results = scope.Add(new NativeArray<ValidationResultData>(entityGuids.Length, Allocator.TempJob));
        var validateJob = new ValidateEntitiesBurst<TSetupOutput, TValidationSetupOutput>
        {
            Logs = logs,
            ValidateEntityDelegate = processor.ValidateEntityDelegate
        };
        yield return SmartExecution.ExecuteBurstFor<Guid, ValidationResultData>(
            uniqueId: $"{property.name}_{task.Type}_ValidateEntities",
            itemCount: entityGuids.Length,
            burstForDelegate: validateJob.Execute
        );

        var createJob = new CreateTasksBurst<TSetupOutput, TValidationSetupOutput>
        {
            Logs = logs,
            CreateTaskDelegate = processor.CreateTaskDelegate
        };
        yield return SmartExecution.ExecuteBurstFor<ValidationResultData, TaskResult>(
            uniqueId: $"{property.name}_{task.Type}_CreateTasks",
            itemCount: validationResults.Length,
            burstForDelegate: createJob.Execute,
            nonBurstResultsDelegate: outputs =>
            {
                Log(Level.Info, $"Enqueuing {outputs.Count} tasks for type {task.Type}{(employeeGuid != Guid.Empty ? $" for employee {employeeGuid}" : "")}", Category.Tasks);
                for (int i = 0; i < outputs.Count; i++)
                {
                    if (employeeGuid != Guid.Empty)
                    {
                        if (!_employeeSpecificTasks.ContainsKey(employeeGuid))
                            _employeeSpecificTasks.Add(employeeGuid, new NativeList<TaskDescriptor>(Allocator.Persistent));
                        _employeeSpecificTasks[employeeGuid].Add(outputs[i].Task);
                    }
                    _taskQueue.Enqueue(outputs[i].Task);
                }
            }
        );
    }
}

```

### When to Use a Direct Method

- **Use Case**: Simple, single-purpose operations with minimal state (e.g., updating a single collection or performing a straightforward calculation). Ideal for delegates with few parameters or no need for reuse.
- **Advantages**:
  - Reduces code complexity by avoiding struct boilerplate.
  - Minimizes overhead for trivial operations, as `SmartExecution.ExecuteBurst` optimizes for small datasets.
- **Constraints**:
  - Limited to simple logic due to lack of state encapsulation.
  - Parameters must be passed directly to the delegate, which must be Burst-compatible and match the signature: `Action<NativeArray<TInput>, NativeList<TOutput>>` for `ExecuteBurst` or `Action<int, NativeArray<TInput>, NativeList<TOutput>>` for `ExecuteBurstFor`.
  - Native collections passed as parameters must be disposed by the caller.
- **Example** (Direct Method for Simple Update):

```csharp
[BurstCompile]
public static void UpdateStationRefillListBurst(NativeArray<(Guid, NativeList<ItemKey>)> inputs, NativeList<int> outputs, ItemKey item, NativeList<Deferred.LogEntry> logs)
{
  var (stationGuid, refillList) = inputs[0];
  if (!refillList.Contains(item))
  {
    refillList.Add(item);

# if DEBUG
    logs.Add(new Deferred.LogEntry
    {
      Message = $"Added item {item.CacheId} to station {stationGuid} refill list",
      Level = Level.Verbose,
      Category = Category.Storage
    });
# endif
  }
}

public static IEnumerator UpdateStationRefillList(Guid stationGuid, NativeList<ItemKey> refillList, ItemKey item)
{
  var inputs = new NativeArray<(Guid, NativeList<ItemKey>)>(1, Allocator.TempJob);
  var outputs = new NativeList<int>(1, Allocator.TempJob);
  var logs = new NativeList<Deferred.LogEntry>(1, Allocator.TempJob);
  try
  {
    yield return SmartExecution.ExecuteBurst(
      uniqueId: nameof(UpdateStationRefillList)
      burstDelegate: (inputs, outputs) => UpdateStationRefillListBurst(inputs, outputs, item, logs),
      inputs: inputs
    );
    yield return Deferred.ProcessLogs(logs);
  }
  finally
  {
    if (inputs.IsCreated) inputs.Dispose();
    if (outputs.IsCreated) outputs.Dispose();
    if (logs.IsCreated) logs.Dispose();
  }
}
```

### Guidelines

- **Use Structs When**:
  - The operation involves multiple native collections or complex state.
  - The logic is reusable across multiple calls or coroutines.
  - Disposal of temporary collections needs to be managed within the job.
- **Use Direct Methods When**:
  - The operation is simple, with minimal state (e.g., updating a single collection).
  - The delegate has few parameters and does not require reuse.
- **Disposal Best Practices**:
  - Document disposal responsibilities in XML comments for structs (e.g., which collections are owned by the caller or external systems).
  - Use try-finally blocks in coroutines to dispose temporary collections (e.g., `Allocator.TempJob`).
  - Transfer ownership of persistent collections (e.g., `Allocator.Persistent`) to services like `TaskService` or `DisabledEntityService`, and document this transfer.

## Structs

### SmartExecutionOptions

Configuration options for smart execution.

- **Fields**:
  - `IsPlayerVisible`: Indicates if execution is networked, affecting yield behavior.

- **Static Properties**:
  - `Default`: Returns default options with `IsPlayerVisible = false`.

## Classes

### SmartExecution

Manages optimized execution of jobs, coroutines, or main thread tasks with dynamic batch sizing and metrics-driven results processing.

- **Static Methods**:
  - `void Initialize()`: Initializes the smart execution system, setting up metrics and baseline data.
  - `IEnumerator Execute<TInput, TOutput>(string uniqueId, int itemCount, Action<int, int, TInput[], List<TOutput>> nonBurstDelegate, Action<List<TOutput>> nonBurstResultsDelegate, TInput[] inputs, List<TOutput> outputs, Action<List<TOutput>> burstResultsDelegate = null, SmartExecutionOptions options = default)`: Executes a non-Burst loop for `itemCount` items with dynamic batch sizing and optional results processing using either Burst or non-Burst delegates. Yields to spread load across frames.
  - `IEnumerator ExecuteBurst<TInput, TOutput>(string uniqueId, Action<NativeArray<TInput>, NativeList<TOutput>> burstDelegate, NativeArray<TInput> inputs, NativeList<TOutput> outputs, Action<NativeList<TOutput>> burstResultsDelegate = null, Action<List<TOutput>> nonBurstResultsDelegate = null, SmartExecutionOptions options = default)`: Executes a single item using a Burst-compiled job with metrics-driven results processing, supporting both Burst and non-Burst results delegates. Inputs and outputs are required.
  - `IEnumerator ExecuteBurstFor<TInput, TOutput>(string uniqueId, int itemCount, Action<int, NativeArray<TInput>, NativeList<TOutput>> burstForDelegate, NativeArray<TInput> inputs, NativeList<TOutput> outputs, Action<NativeList<TOutput>> burstResultsDelegate = null, Action<List<TOutput>> nonBurstResultsDelegate = null, SmartExecutionOptions options = default)`: Executes multiple items using a Burst-compiled job (`IJobFor` or `IJobParallelFor`) with dynamic batch sizing and results processing. Inputs, outputs, and itemCount are required.
  - `IEnumerator ExecuteTransforms<TInput>(string uniqueId, TransformAccessArray transforms, Action<int, TransformAccess> burstTransformDelegate, Action<int, Transform> burstMainThreadTransformDelegate, NativeArray<TInput> inputs = default, SmartExecutionOptions options = default)`: Executes transform operations using Burst-compiled jobs or main thread processing, with dynamic batch sizing.
  - `void SaveBaselineData()`: Saves performance baseline data to a JSON file.
  - `void LoadBaselineData()`: Loads performance baseline data from a JSON file.
  - `void ResetBaselineData()`: Resets baseline performance data and clears associated files.

- **Example 1: Execute (Non-Burst, Single Item)**

```csharp
public class NonBurstSingleItemExample
{
  private static void ProcessNonBurst(int start, int count, int[] inputs, List<int> outputs)
  {
    for (int i = start; i < start + count; i++)
    {
      outputs.Add(inputs[i] * 2);
    }
  }

  private static void ProcessNonBurstResults(List<int> outputs)
  {
    if (outputs.Count > 0)
    {
      Debug.Log(Level.Info, $"Processed output: {outputs[0]}", Category.Tasks);
    }
  }

  public static IEnumerator Start()
  {
    int[] inputs = new int[] { 5 };
    List<int> outputs = new List<int>();
    yield return SmartExecution.Execute(
      uniqueId: nameof(NonBurstSingleItemExample),
      itemCount: 1,
      nonBurstDelegate: ProcessNonBurst,
      nonBurstResultsDelegate: ProcessNonBurstResults,
      inputs: inputs,
      outputs: outputs
    );
    Debug.Log(Level.Info, $"Final outputs: {string.Join(", ", outputs)}", Category.Tasks);
  }
}
```

- **Example 2: ExecuteBurst (Single Item)**

```csharp
public class BurstSingleItemExample
{
  [BurstCompile]
  private static void ProcessSingleItem(NativeArray<int> inputs, NativeList<int> outputs, NativeList<LogEntry> logs)
  {
    outputs.Add(inputs[0] * 2);
    logs.Add(new LogEntry
    {
      Message = $"Processed {inputs[0]}",
      Level = Level.Info,
      Category = Category.Tasks
    });
  }

  [BurstCompile]
  private static void ProcessBurstResults(NativeList<int> outputs, NativeList<LogEntry> logs)
  {
    if (outputs.Length > 0)
    {
      logs.Add(new LogEntry
      {
        Message = $"Processed {outputs[0]}",
        Level = Level.Info,
        Category = Category.Tasks
      });
    }
  }

  public static IEnumerator Start()
  {
    var inputs = new NativeArray<int>(1, Allocator.TempJob);
    var outputs = new NativeList<int>(1, Allocator.TempJob);
    var logs = new NativeList<Deferred.LogEntry>(1, Allocator.TempJob);
    try
    {
      inputs[0] = 5;
      yield return SmartExecution.ExecuteBurst(
        uniqueId: nameof(BurstSingleItemExample),
        burstDelegate: (inputs, outputs) => ProcessSingleItem(inputs, outputs, logs),
        inputs: inputs,
        outputs: outputs,
        burstResultsDelegate: (outputs) => ProcessBurstResults(outputs, logs)
      );
      yield return ProcessLogs(logs);
    }
    finally
    {
      if (inputs.IsCreated) inputs.Dispose();
      if (outputs.IsCreated) outputs.Dispose();
      if (logs.IsCreated) logs.Dispose();
    }
  }
}
```

- **Example 3: ExecuteBurstFor (Multiple Items)**

```csharp
public class BurstMultipleItemsExample
{
  [BurstCompile]
  private static void ProcessMultipleItems(int index, NativeArray<int> inputs, NativeList<int> outputs, NativeList<LogEntry> logs)
  {
    outputs.Add(inputs[index] + 10);
    logs.Add(new LogEntry
    {
      Message = $"Processed index={index}, first few: {string.Join(", ", outputs.Take(5))}",
      Level = Level.Info,
      Category = Category.Tasks
    });
  }

  private static void ProcessNonBurstResults(List<int> outputs)
  {
    Log(Level.Info, $"Processed {outputs.Count} items, first few: {string.Join(", ", outputs.Take(5))}", Category.Tasks);
  }

  public static IEnumerator Start()
  {
    var inputs = new NativeArray<int>(100, Allocator.TempJob);
    var outputs = new NativeList<int>(100, Allocator.TempJob);
    var logs = new NativeList<Deferred.LogEntry>(100, Allocator.TempJob);
    try
    {
      for (int i = 0; i < 100; i++)
      {
        inputs[i] = i;
      }
      yield return SmartExecution.ExecuteBurstFor(
        uniqueId: nameof(BurstMultipleItemsExample),
        itemCount: 100,
        burstForDelegate: (index, inputs, outputs) => ProcessMultipleItems(index, inputs, outputs, logs),
        inputs: inputs,
        outputs: outputs,
        nonBurstResultsDelegate: ProcessNonBurstResults
      );
      yield return ProcessLogs(logs);
    }
    finally
    {
      if (inputs.IsCreated) inputs.Dispose();
      if (outputs.IsCreated) outputs.Dispose();
      if (logs.IsCreated) logs.Dispose();
    }
  }
}
```

- **Example 4: ExecuteTransforms (Transform Operations)**

```csharp
public class TransformExample
{
  [BurstCompile]
  private static void ProcessTransform(int index, TransformAccess transform)
  {
    transform.position += Vector3.up * 0.1f;
  }

  [BurstCompile]
  private static void ProcessMainThreadTransform(int index, Transform transform)
  {
    transform.position += Vector3.up * 0.1f;
  }

public static IEnumerator Start()
  {
    TransformAccessArray transforms = default;
    GameObject[] gameObjects = null;
    try
    {
      int transformCount = 10;
      transforms = new TransformAccessArray(transformCount);
      gameObjects = new GameObject[transformCount];
      for (int i = 0; i < transformCount; i++)
      {
        gameObjects[i] = new GameObject($"TestObject_{i}");
        transforms.Add(gameObjects[i].transform);
      }
      yield return SmartExecution.ExecuteTransforms<int>(
        uniqueId: nameof(TransformExample),
        transforms: transforms,
        burstTransformDelegate: ProcessTransform,
        burstMainThreadTransformDelegate: ProcessMainThreadTransform
      );
    }
    finally
    {
      if (transforms.isCreated) transforms.Dispose();
      if (gameObjects != null)
      {
        foreach (var go in gameObjects)
        {
          if (go != null) UnityEngine.Object.Destroy(go);
        }
      }
    }
  }
}
```
