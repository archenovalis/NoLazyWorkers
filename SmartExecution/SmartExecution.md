# NoLazyWorkers.SmartExecution API Documentation

**Date**: August 4, 2025

## Overview

The `NoLazyWorkers.SmartExecution` namespace provides a high-performance system for scheduling and executing jobs in Unity, leveraging Unity's Job System and Burst compilation. It dynamically optimizes execution by selecting the best path (main thread, coroutine, or job) based on real-time performance metrics, ensuring minimal main thread impact and efficient resource usage. The system supports dynamic batch sizing, performance tracking, and both Burst-compiled and non-Burst delegates for flexibility in handling single items, multiple items, and transform operations.

### Key Features

- **Dynamic Execution Path Selection**: Chooses between main thread, coroutines, or jobs based on metrics like execution time, main thread impact, and CPU load.
- **Burst Compilation**: Optimizes performance for computationally intensive tasks using unmanaged types and native collections.
- **Performance Tracking**: Monitors execution times, cache hits/misses, and thread safety to inform scheduling decisions.
- **Flexible Delegates**: Supports both Burst-compiled (unmanaged) and non-Burst (managed) delegates for processing and results handling.
- **Logging**: Uses `Deferred.LogEntry` for Burst jobs and `Log(Level, message, Category)` for non-Burst operations. Process Burst logs with `yield return Deferred.ProcessLogs`.

### Usage Guidelines

- **Burst Delegates**: Must be Burst-compiled, use unmanaged types, and avoid managed objects or interfaces. Use `NativeArray` and `NativeList` for data.
- **Non-Burst Delegates**: Suitable for main thread or coroutine execution, especially for operations involving Unity APIs (e.g., `GameObject`, `Transform`).
- **Heavy Workloads**: Use `Smart` methods to spread load across frames, especially for player-visible operations.
- **Thread Safety**: Set `IsTaskSafe` to `false` for operations accessing Unity APIs to avoid threading issues.
- **Resource Management**: Always dispose of `NativeArray`, `NativeList`, and `TransformAccessArray` in a `finally` block to prevent memory leaks.

## Classes and Structs

### SmartOptions

Configuration options for smart execution.

- **Fields:**
  - `IsPlayerVisible`: `bool` - Set to `true` for operations affecting player-visible elements (e.g., UI updates, transform movements, or networked objects synchronized via FishNet ticks). Ensures synchronized execution for rendering or network consistency.
  - `IsTaskSafe`: `bool` - Set to `true` for operations safe to run off the main thread (e.g., pure data processing or Burst jobs with native collections). Set to `false` for operations using Unity APIs (e.g., `GameObject`, `Transform`).
  - `Default`: `SmartOptions` (static) - Returns default options (`IsPlayerVisible = false`, `IsTaskSafe = false`).

### Smart

Manages optimized execution of jobs, coroutines, or main thread tasks with dynamic batch sizing and metrics-driven decisions.

#### Methods

- **Initialize()**
  - Initializes the system, setting up performance metrics and starting coroutine monitoring for CPU stability and thread usage.

  - **Example:**

    ```csharp
    Smart.Initialize()
    ```

- **Execute<TInput, TOutput>(string uniqueId, int itemCount, Action<int, int, TInput[], List<TOutput>> action, TInput[] inputs = null, List<TOutput> outputs = null, Action<List<TOutput>> resultsAction = null, SmartOptions options = default)**
  - Executes a non-Burst job with dynamic batching and results processing, choosing the optimal execution path (task, main thread, or coroutine) based on metrics.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking performance metrics.
    - `itemCount`: `int` - Number of items to process.
    - `action`: `Action<int, int, TInput[], List<TOutput>>` - Delegate to process a batch of items (start index, count, inputs, outputs).
    - `inputs`: `TInput[]` - Input data array (optional, must match `itemCount` if provided).
    - `outputs`: `List<TOutput>` - Output data list (optional, created if null).
    - `resultsAction`: `Action<List<TOutput>>` - Delegate for processing results (optional).
    - `options`: `SmartOptions` - Execution options (optional, defaults to `SmartOptions.Default`).
  - **Returns:** `IEnumerator` - Coroutine enumerator for yielding during execution.

  - **Example:**

    ```csharp
    var inputs = new int[] { 1, 2, 3, 4, 5 };
    var outputs = new List<int>();
    Action<int, int, int[], List<int>> processBatch = (start, count, inArray, outList) => {
        for (int i = start; i < start + count; i++)
            outList.Add(inArray[i] * 2);
        Log(Level.Info, $"Processed {count} items", Category.Performance);
    };
    Action<List<int>> results = (results) => Log(Level.Info, $"Final results: {string.Join(", ", results)}", Category.Performance);
    yield return Smart.Execute("ProcessData", inputs.Length, processBatch, inputs, outputs, results, new SmartOptions { IsTaskSafe = true })
    ```

- **ExecuteBurst<TInput, TOutput, TStruct>(string uniqueId, Action<TInput, NativeList<TOutput>, NativeList<LogEntry>> burstAction, TInput input, NativeList<TOutput> outputs, NativeList<LogEntry> logs, Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsAction = null, Action<List<TOutput>> nonBurstResultsAction = null, SmartOptions options = default)**
  - Executes a Burst-compiled job for a single item with metrics-driven results processing.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking metrics.
    - `burstAction`: `Action<TInput, NativeList<TOutput>, NativeList<LogEntry>>` - Burst-compiled delegate for processing a single item.
    - `input`: `TInput` - Single input item.
    - `outputs`: `NativeList<TOutput>` - Output data list (must be initialized).
    - `logs`: `NativeList<LogEntry>` - Native list for logging (must be initialized).
    - `burstResultsAction`: `Action<NativeList<TOutput>, NativeList<LogEntry>>` - Burst-compiled delegate for results processing (optional).
    - `nonBurstResultsAction`: `Action<List<TOutput>>` - Non-Burst delegate for results processing (optional).
    - `options`: `SmartOptions` - Execution options (optional, defaults to `SmartOptions.Default`).
  - **Returns:** `IEnumerator` - Coroutine enumerator for yielding during execution.
  - **Constraints:** `TInput`, `TOutput`, and `TStruct` must be unmanaged; `TStruct` must be a struct.

  - **Example:**

    ```csharp
    [BurstCompile]
    struct SingleItemProcessor
    {
        [ReadOnly] public NativeArray<float> Coefficients;
        public float Scale;
        public void Execute(int input, NativeList<int> outputs, NativeList<LogEntry> logs)
        {
            float result = input *Coefficients[0]* Scale;
            outputs.Add((int)result);
            logs.Add(new LogEntry { Message = $"Processed input {input} to {result}", Level = Level.Info, Category = Category.Performance });
        }
    }
    [BurstCompile]
    struct ResultProcessor
    {
        public void Execute(NativeList<int> outputs, NativeList<LogEntry> logs)
        {
            logs.Add(new LogEntry { Message = $"Processed {outputs.Length} items", Level = Level.Info, Category = Category.Performance });
        }
    }
    var coefficients = new NativeArray<float>(new float[] { 2.5f }, Allocator.TempJob);
    var outputs = new NativeList<int>(Allocator.TempJob);
    var logs = new NativeList<LogEntry>(Allocator.TempJob);
    try
    {
        yield return Smart.ExecuteBurst<int, int, SingleItemProcessor>(
            "SingleItemScale",
            new SingleItemProcessor { Coefficients = coefficients, Scale = 1.0f }.Execute,
            42,
            outputs,
            logs,
            new ResultProcessor().Execute,
            null,
            new SmartOptions { IsTaskSafe = true }
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

- **ExecuteBurstFor<TInput, TOutput, TStruct>(string uniqueId, int itemCount, Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstForAction, NativeArray<TInput> inputs = default, NativeList<TOutput> outputs = default, NativeList<LogEntry> logs = default, Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsAction = null, Action<List<TOutput>> nonBurstResultsAction = null, SmartOptions options = default)**
  - Executes a Burst-compiled job for multiple items with dynamic batching and results processing.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking metrics.
    - `itemCount`: `int` - Number of items to process.
    - `burstForAction`: `Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>` - Burst-compiled delegate for per-item processing.
    - `inputs`: `NativeArray<TInput>` - Input data array (optional, must match `itemCount` if provided).
    - `outputs`: `NativeList<TOutput>` - Output data list (optional, created if null).
    - `logs`: `NativeList<LogEntry>` - Native list for logging (optional, created if null).
    - `burstResultsAction`: `Action<NativeList<TOutput>, NativeList<LogEntry>>` - Burst-compiled delegate for results processing (optional).
    - `nonBurstResultsAction`: `Action<List<TOutput>>` - Non-Burst delegate for results processing (optional).
    - `options`: `SmartOptions` - Execution options (optional, defaults to `SmartOptions.Default`).
  - **Returns:** `IEnumerator` - Coroutine enumerator for yielding during execution.
  - **Constraints:** `TInput`, `TOutput`, and `TStruct` must be unmanaged; `TStruct` must be a struct.

  - **Example:**

    ```csharp
    [BurstCompile]
    struct MultiItemProcessor
    {
        [ReadOnly] public NativeArray<float> Weights;
        public float Threshold;
        public void Execute(int index, NativeArray<int> inputs, NativeList<int> outputs, NativeList<LogEntry> logs)
        {
            float weighted = inputs[index] * Weights[index % Weights.Length];
            if (weighted > Threshold)
            {
                outputs.Add((int)weighted);
                logs.Add(new LogEntry { Message = $"Processed item {index}: {weighted}", Level = Level.Verbose, Category = Category.Performance });
            }
        }
    }
    [BurstCompile]
    struct ResultProcessor
    {
        public void Execute(NativeList<int> outputs, NativeList<LogEntry> logs)
        {
            logs.Add(new LogEntry { Message = $"Filtered {outputs.Length} items", Level = Level.Info, Category = Category.Performance });
        }
    }
    var inputs = new NativeArray<int>(10, Allocator.TempJob);
    for (int i = 0; i < inputs.Length; i++) inputs[i] = i + 1;
    var weights = new NativeArray<float>(new float[] { 1.5f, 2.0f }, Allocator.TempJob);
    var outputs = new NativeList<int>(Allocator.TempJob);
    var logs = new NativeList<LogEntry>(Allocator.TempJob);
    try
    {
        yield return Smart.ExecuteBurstFor<int, int, MultiItemProcessor>(
            "MultiItem",
            inputs.Length,
            new MultiItemProcessor { Weights = weights, Threshold = 10.0f }.Execute,
            inputs,
            outputs,
            logs,
            new ResultProcessor().Execute
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

- **ExecuteTransforms<TInput>(string uniqueId, TransformAccessArray transforms, Action<int, TransformAccess, NativeList<LogEntry>> burstTransformAction, Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformAction, NativeArray<TInput> inputs = default, NativeList<LogEntry> logs = default, SmartOptions options = default)**
  - Executes transform operations with dynamic batching, choosing between Burst-compiled jobs or main thread execution.
  - **Parameters:**
    - `uniqueId`: `string` - Unique identifier for tracking metrics.
    - `transforms`: `TransformAccessArray` - Array of transforms to process.
    - `burstTransformAction`: `Action<int, TransformAccess, NativeList<LogEntry>>` - Burst-compiled delegate for transform processing.
    - `burstMainThreadTransformAction`: `Action<int, Transform, NativeList<LogEntry>>` - Delegate for main thread transform processing.
    - `inputs`: `NativeArray<TInput>` - Input data array (optional).
    - `logs`: `NativeList<LogEntry>` - Native list for logging (optional).
    - `options`: `SmartOptions` - Execution options (optional, defaults to `SmartOptions.Default`).
  - **Returns:** `IEnumerator` - Coroutine enumerator for yielding during execution.
  - **Constraints:** `TInput` must be unmanaged.

  - **Example:**

    ```csharp
    [BurstCompile]
    struct TransformProcessor
    {
        [ReadOnly] public NativeArray<float> Offsets;
        public float DeltaTime;
        public void Execute(int index, TransformAccess transform, NativeList<LogEntry> logs)
        {
            transform.position += Vector3.up *Offsets[index % Offsets.Length]* DeltaTime;
            logs.Add(new LogEntry { Message = $"Moved transform {index}", Level = Level.Info, Category = Category.Performance });
        }
    }
    [BurstCompile]
    struct MainThreadTransformProcessor
    {
        public float DeltaTime;
        public void Execute(int index, Transform transform, NativeList<LogEntry> logs)
        {
            transform.position += Vector3.up * DeltaTime;
            logs.Add(new LogEntry { Message = $"Moved transform {index} on main thread", Level = Level.Info, Category = Category.Performance });
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
        yield return Smart.ExecuteTransforms<int>(
            "TransformJob",
            transforms,
            new TransformProcessor { Offsets = offsets, DeltaTime = 0.016f }.Execute,
            new MainThreadTransformProcessor { DeltaTime = 0.016f }.Execute,
            logs: logs,
            options: new SmartOptions { IsPlayerVisible = true }
        );
        yield return Deferred.ProcessLogs(logs);
    }
    finally
    {
        transforms.Dispose();
        offsets.Dispose();
        logs.Dispose();
        foreach (var go in gameObjects)
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
    }
    ```

- **SaveBaselineData()**
  - Saves performance metrics and batch size history to a JSON file for persistence across sessions.

  - **Example:**

    ```csharp
    Smart.SaveBaselineData()
    ```

- **LoadBaselineData()**
  - Loads performance metrics and batch size history from a JSON file to resume previous optimization settings.

  - **Example:**

    ```csharp
    Smart.LoadBaselineData()
    ```

- **ResetBaselineData()**
  - Clears baseline performance data and deletes associated JSON files, resetting optimization metrics.

  - **Example:**

    ```csharp
    Smart.ResetBaselineData()
    ```

- **Cleanup()**
  - Disposes of all resources, completes pending jobs, and clears caches and metrics.

  - **Example:**

    ```csharp
    Smart.Cleanup()
    ```

## Notes

- **Performance Metrics**: The system tracks execution times, main thread impact, cache hit rates, and batch sizes to optimize scheduling. Metrics are stored in `SmartMetrics` and used to adjust batch sizes and execution paths dynamically.
- **Baseline Establishment**: The system establishes performance baselines during initialization and on first runs to determine optimal execution strategies. Baselines are validated and reset if significant deviations are detected.
- **Thread Safety**: Always set `IsTaskSafe` appropriately to prevent Unity API access errors in off-main-thread tasks. Use `CheckTaskSafety` for validation during development.
- **Memory Management**: Ensure proper disposal of native collections (`NativeArray`, `NativeList`, `TransformAccessArray`) to avoid memory leaks, especially in Burst jobs.
- **Logging**: Use `Log` for non-Burst contexts and `Deferred.LogEntry` for Burst contexts. Process Burst logs with `Deferred.ProcessLogs` to ensure thread safety.

This API is designed for Unity developers needing high-performance task execution with minimal main thread impact, suitable for both computationally intensive tasks and operations requiring Unity API access
