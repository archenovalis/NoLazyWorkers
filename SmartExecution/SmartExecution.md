# NoLazyWorkers.SmartExecution API Documentation

**Date**: July 31, 2025

## Overview

The `NoLazyWorkers.SmartExecution` namespace provides a high-performance job scheduling and execution system optimized for Unity, leveraging Unity's Job System, Burst compilation, and dynamic profiling to minimize main thread impact. It supports job wrappers, execution time tracking, dynamic batch sizing, and smart execution strategies for single items, multiple items, and transform operations. The system dynamically selects the optimal execution path (main thread, coroutine, or job) based on performance metrics and supports both Burst-compiled and non-Burst delegates for flexibility.

Delegates for Burst jobs must be Burst-compiled, use non-nullable fields, and avoid managed objects or interfaces. Non-Burst delegates are supported for main thread or coroutine execution. Heavy methods should use `SmartExecution` for load spreading across frames. Logging in Burst jobs uses `Deferred.LogEntry`, with logs processed via `yield return Deferred.ProcessLogs`. Non-Burst execution uses `Log(Level.Verbose/Info/Warning/Error/StackTrace, "message", Category.*)`.

## Classes and Structs

### SmartExecutionOptions

Configuration options for smart execution.

- **Fields:**
  - `IsPlayerVisible`: `bool` - Indicates if the operation affects player-visible elements, such as UI, GameObjects, or Transforms in Unity's frame-based rendering pipeline, or networked objects synchronized via FishNet ticks. Set to `true` for operations impacting visuals (e.g., UI updates, transform movements, or networked object states) to prioritize main thread or synchronized execution for consistency in rendering or network updates.
  - `IsTaskSafe`: `bool` - Indicates if the operation is safe for off-main-thread execution. Operations accessing Unity APIs (e.g., GameObject, Transform, MonoBehaviour) are not task-safe and must run on the main thread. Task-safe operations involve pure data processing or Burst-compiled jobs using native collections.Set to `false` for Unity API-dependent operations.
  - `Default`: `SmartExecutionOptions` (static) - Gets the default options(`IsPlayerVisible = false`, `IsTaskSafe = true`).

### SmartExecution

Manages optimized execution of jobs, coroutines, or main thread tasks with dynamic batch sizing and metrics-driven results processing.

#### Methods

- **Initialize()**
  - Initializes the smart execution system, setting up metrics and baseline data.

  - **Example:**

    ```csharp
    SmartExecution.Initialize()
    ```

- **Execute<TInput, TOutput>(string uniqueId, int itemCount, Action<int, int, TInput[], List<TOutput>> nonBurstDelegate, TInput[] inputs = default, List<TOutput> outputs = default, Action<List<TOutput>> nonBurstResultsDelegate = null, Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsDelegate = null, SmartExecutionOptions options = default)**
  - Executes a non-Burst job with dynamic scheduling based on performance metrics.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking metrics.
    - `itemCount`: `int` - Number of items to process.
    - `nonBurstDelegate`: `Action<int, int, TInput[], List<TOutput>>` - Delegate for processing a batch of items.
    - `inputs`: `TInput[]` - Input data array (optional).
    - `outputs`: `List<TOutput>` - Output data list (optional).
    - `nonBurstResultsDelegate`: `Action<List<TOutput>>` - Delegate for non-Burst results processing (optional).
    - `burstResultsDelegate`: `Action<NativeList<TOutput>, NativeList<LogEntry>>` - Delegate for Burst results processing (optional).
    - `options`: `SmartExecutionOptions` - Execution options (optional, defaults to `SmartExecutionOptions.Default`).
  - **Returns:** `IEnumerator` - Enumerator for the execution coroutine.
  - **Constraints:** `TInput` and `TOutput` must be unmanaged.

  - **Example:**

    ```csharp
    var inputs = new int[] { 1, 2, 3, 4, 5 };
    var outputs = new List<int>();
    Action<int, int, int[], List<int>> processBatch = (start, count, inArray, outList) =>
    {
        for (int i = start; i < start + count; i++)
            outList.Add(inArray[i] * 2);
        Log(Level.Info, $"Processed {count} items", Category.Tasks);
    };
    Action<List<int>> resultsDelegate = (results) => Log(Level.Info, $"Processed {results.Count} items: {string.Join(", ", results)}", Category.Tasks);
    yield return SmartExecution.Execute("ProcessData", inputs.Length, processBatch, inputs, outputs, resultsDelegate)
    ```

- **ExecuteBurst<TInput, TOutput, TStruct>(string uniqueId, Action<TInput, NativeList<TOutput>, NativeList<LogEntry>> burstDelegate, TInput input, NativeList<TOutput> outputs, NativeList<LogEntry> logs, Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsDelegate = null, Action<List<TOutput>> nonBurstResultsDelegate = null, SmartExecutionOptions options = default)**
  - Executes a Burst-compiled job for a single item with metrics-driven results processing.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking metrics.
    - `burstDelegate`: `Action<TInput, NativeList<TOutput>, NativeList<LogEntry>>` - Delegate for Burst-compiled job execution.
    - `input`: `TInput` - Single input item.
    - `outputs`: `NativeList<TOutput>` - Output data list.
    - `logs`: `NativeList<LogEntry>` - Optional NativeList for logs.
    - `burstResultsDelegate`: `Action<NativeList<TOutput>, NativeList<LogEntry>>` - Delegate for Burst-compiled results processing (optional).
    - `nonBurstResultsDelegate`: `Action<List<TOutput>>` - Delegate for non-Burst results processing (optional).
    - `options`: `SmartExecutionOptions` - Execution options (optional, defaults to `SmartExecutionOptions.Default`).
  - **Returns:** `IEnumerator` - Enumerator for the execution coroutine.
  - **Constraints:** `TInput`, `TOutput`, and `TStruct` must be unmanaged; `TStruct` must be a struct.

  - **Example:**

    ```csharp
    [BurstCompile]
    struct SingleItemProcessor
    {
      [ReadOnly] public NativeArray<float> Coefficients; // Native collection for Burst
      public float Scale;

      public void Execute(int input, NativeList<int> outputs, NativeList<LogEntry> logs)
      {
        float result = input *Coefficients[0]* Scale;
        outputs.Add((int)result);
        logs.Add(new LogEntry { Message = $"Processed input {input} to {result}", Level = Level.Info, Category = Category.Tasks });
      }
    }

    [BurstCompile]
    struct ResultProcessor
    {
      public void Execute(NativeList<int> outputs, NativeList<LogEntry> logs)
      {
        logs.Add(new LogEntry { Message = $"Processed {outputs.Length} items", Level = Level.Info, Category = Category.Tasks });
      }
    }

    var coefficients = new NativeArray<float>(new float[] { 2.5f }, Allocator.TempJob);
    var outputs = new NativeList<int>(Allocator.TempJob);
    var logs = new NativeList<LogEntry>(Allocator.TempJob);
    try
    {
        yield return SmartExecution.ExecuteBurst<int, int, SingleItemProcessor>(
            uniqueId: "SingleItemScale",
            burstDelegate: new SingleItemProcessor { Coefficients = coefficients, Scale = 1.0f }.Execute,
            input: 42,
            outputs: outputs,
            logs: logs,
            burstResultsDelegate: new ResultProcessor().Execute,
            options: new SmartExecutionOptions { IsPlayerVisible = false, IsTaskSafe = true }
        );
        yield return Deferred.ProcessLogs(logs);
    }
    finally
    {
        coefficients.Dispose();
        outputs.Dispose();
        logs.Dispose();
    }
    ```

- **ExecuteBurstFor<TInput, TOutput, TStruct>(string uniqueId, int itemCount, Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstForDelegate, NativeArray<TInput> inputs = default, NativeList<TOutput> outputs = default, NativeList<LogEntry> logs = default, Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsDelegate = null, Action<List<TOutput>> nonBurstResultsDelegate = null, SmartExecutionOptions options = default)**
  - Executes a Burst-compiled job for multiple items with metrics-driven results processing.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking metrics.
    - `itemCount`: `int` - Total number of items to process.
    - `burstForDelegate`: `Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>` - Delegate for Burst-compiled job execution per item.
    - `inputs`: `NativeArray<TInput>` - Input data array (optional).
    - `outputs`: `NativeList<TOutput>` - Output data list (optional).
    - `logs`: `NativeList<LogEntry>` - Optional NativeList for logs.
    - `burstResultsDelegate`: `Action<NativeList<TOutput>, NativeList<LogEntry>>` - Delegate for Burst-compiled results processing (optional).
    - `nonBurstResultsDelegate`: `Action<List<TOutput>>` - Delegate for non-Burst results processing (optional).
    - `options`: `SmartExecutionOptions` - Execution options (optional, defaults to `SmartExecutionOptions.Default`).
  - **Returns:** `IEnumerator` - Enumerator for the execution coroutine.
  - **Constraints:** `TInput`, `TOutput`, and `TStruct` must be unmanaged; `TStruct` must be a struct.

  - **Example:**

    ```csharp
    [BurstCompile]
    struct MultiItemProcessor
    {
      [ReadOnly] public NativeArray<float> Weights; // Native collection for Burst
      public float Threshold;

      public void ExecuteFor(int index, NativeArray<int> inputs, NativeList<int> outputs, NativeList<LogEntry> logs)
      {
        float weighted = inputs[index] * Weights[index % Weights.Length];
        if (weighted > Threshold)
        {
          outputs.Add((int)weighted);
          logs.Add(new LogEntry { Message = $"Processed item {index}: {weighted}", Level = Level.Verbose, Category = Category.Tasks });
        }
      }
    }

    [BurstCompile]
    struct ResultProcessor
    {
      public void Execute(NativeList<int> outputs, NativeList<LogEntry> logs)
      {
        logs.Add(new LogEntry { Message = $"Filtered {outputs.Length} items above threshold", Level = Level.Info, Category = Category.Tasks });
      }
    }
    var inputs = new NativeArray<int>(10, Allocator.TempJob);
    for (int i = 0; i < inputs.Length; i++) inputs[i] = i + 1;
    var weights = new NativeArray<float>(new float[] { 1.5f, 2.0f }, Allocator.TempJob);
    var outputs = new NativeList<int>(Allocator.TempJob);
    var logs = new NativeList<LogEntry>(Allocator.TempJob);
    try
    {
        yield return SmartExecution.ExecuteBurstFor<int, int, MultiItemProcessor>(
            uniqueId: "MultiItem",
            itemCount: inputs.Length,
            burstForDelegate: new MultiItemProcessor { Weights = weights, Threshold = 10.0f }.ExecuteFor,
            inputs: inputs,
            outputs: outputs,
            logs: logs,
            nonBurstResultsDelegate: new ResultProcessor().Execute,
            options: new SmartExecutionOptions { IsTaskSafe = true }
        );
        yield return Deferred.ProcessLogs(logs);
    }
    finally
    {
        inputs.Dispose();
        weights.Dispose();
        outputs.Dispose();
        logs.Dispose();
    }
    ```

- **ExecuteTransforms<TInput>(string uniqueId, TransformAccessArray transforms, Action<int, TransformAccess, NativeList<LogEntry>> burstTransformDelegate, Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformDelegate, NativeArray<TInput> inputs = default, NativeList<LogEntry> logs = default, SmartExecutionOptions options = default)**
  - Executes transform operations using the optimal execution path.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for the execution.
    - `transforms`: `TransformAccessArray` - Array of transforms to process.
    - `burstTransformDelegate`: `Action<int, TransformAccess, NativeList<LogEntry>>` - Delegate for Burst-compiled transform job.
    - `burstMainThreadTransformDelegate`: `Action<int, Transform, NativeList<LogEntry>>` - Delegate for main thread transform processing.
    - `inputs`: `NativeArray<TInput>` - Input data array (optional).
    - `logs`: `NativeList<LogEntry>` - Optional NativeList for logs.
    - `options`: `SmartExecutionOptions` - Execution options (optional, defaults to `SmartExecutionOptions.Default`).
  - **Returns:** `IEnumerator` - Enumerator for the execution coroutine.
  - **Constraints:** `TInput` must be unmanaged.

  - **Example:**

    ```csharp
    [BurstCompile]
    struct TransformProcessor
    {
        [ReadOnly] public NativeArray<float> Offsets; // Native collection for Burst
        public float DeltaTime;
        public void Execute(int index, TransformAccess transform, NativeList<LogEntry> logs)
        {
            transform.position += Vector3.up * Offsets[index % Offsets.Length] * DeltaTime;
            logs.Add(new LogEntry { Message = $"Moved transform {index}", Level = Level.Info, Category = Category.Transforms });
        }
    }
    [BurstCompile]
    struct MainThreadTransformProcessor
    {
        public float DeltaTime;

        public void Execute(int index, Transform transform, NativeList<LogEntry> logs)
        {
            transform.position += Vector3.up * DeltaTime;
            logs.Add(new LogEntry { Message = $"Moved transform {index} on main thread", Level = Level.Info, Category = Category.Transforms });
        }
    }
    var transforms = new TransformAccessArray(5);
    var gameObjects = new GameObject[5];
    for (int i = 0; i < 5; i++)
    {
      gameObjects[i] = new GameObject($"TestObject_{i}");
      transforms.Add(gameObjects[i].transform);
    }
    var offsets = new NativeArray<float>(new float[] { 0.1f, 0.2f }, Allocator.TempJob);
    var logs = new NativeList<LogEntry>(Allocator.TempJob);
    try
    {
        yield return SmartExecution.ExecuteTransforms<int>(
            uniqueId: "TransformJob",
            transforms: transforms,
            burstTransformDelegate: new TransformProcessor { Offsets = offsets, DeltaTime = 0.016f }.Execute,
            burstMainThreadTransformDelegate: new MainThreadTransformProcessor { DeltaTime = 0.016f }.Execute,
            logs: logs,
            options: new SmartExecutionOptions { IsPlayerVisible = true }
        );
        yield return Deferred.ProcessLogs(logs);
    }
    finally
    {
        transforms.Dispose();
        offsets.Dispose();
        logs.Dispose();
        foreach (var go in gameObjects)
          if (go != null) UnityEngine.Object.Destroy(go);
    }
    ```

- **SaveBaselineData()**
  - Saves performance baseline data to a JSON file.

  - **Example:**

    ```csharp
    SmartExecution.SaveBaselineData()
    ```

- **LoadBaselineData()**
  - Loads performance baseline data from a JSON file.

  - **Example:**

    ```csharp
    SmartExecution.LoadBaselineData()
    ```

- **ResetBaselineData()**
  - Resets baseline performance data and clears associated files.

  - **Example:**

    ```csharp
    SmartExecution.ResetBaselineData()
    ```
