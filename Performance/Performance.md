# NoLazyWorkers.Performance API Documentation

**Date**: June 24, 2025

## Overview

The `NoLazyWorkers.Performance` namespace provides a robust system for managing and optimizing task execution in Unity using the Job System, coroutines, or main thread execution. It includes job wrappers, performance metrics tracking, dynamic profiling, and intelligent task scheduling with support for Burst compilation and FishNet integration.

---

## Enums

### JobType

Defines the types of jobs supported by the job wrapper system.

- **IJob**: Non-parallel job.
- **IJobParallelFor**: Parallel job processing items in batches.
- **IJobFor**: Parallel job processing single items.
- **IJobParallelForTransform**: Parallel job for transform operations.

---

## Interfaces

### IJobWrapper

Interface for wrapping Unity Job System jobs.

- **Properties**:
  - `IsParallel`: Indicates if the job is parallel.
  - `JobType`: The type of job (see `JobType`).

- **Methods**:

  ```csharp
    JobHandle Schedule(int count = 0, int batchSize = 64)
    ```

  Schedules the job.
  - **count**: Number of items to process (default: 0).
  - **batchSize**: Batch size for parallel processing (default: 64).
  - **Returns**: `JobHandle` for the scheduled job.

  ```csharp
    void Complete()
    ```

    Completes the job and cleans up resources.

---

## Structs

### JobWrapper<T> where T : struct, IJob

Wraps a non-parallel `IJob`.

- **Fields**:
  - `Job`: The job to be executed.

- **Properties**:
  - `IsParallel`: Always `false`.
  - `JobType`: Always `JobType.IJob`.

- **Methods**:

  ```csharp
    JobWrapper(T job)
    ```

    Constructor initializing the job.

  ```csharp
    JobHandle Schedule(int count = 0, int batchSize = 64)
    ```

    Schedules the job using `Job.Schedule()`.

  ```csharp
    void Complete()
    ```

    Empty implementation (no cleanup required).

### JobParallelForWrapper<T> where T : struct, IJobParallelFor

Wraps a parallel `IJobParallelFor`.

- **Fields**:
  - `Job`: The job to be executed in parallel.

- **Properties**:
  - `IsParallel`: Always `true`.
  - `JobType`: Always `JobType.IJobParallelFor`.

- **Methods**:

  ```csharp
    JobParallelForWrapper(T job, int count, int batchSize)
    ```

    Constructor initializing the job, count, and batch size.

  ```csharp
    JobHandle Schedule(int count = 0, int batchSize = 64)
    ```

    Schedules the job using `Job.Schedule(_count, _batchSize)`.

  ```csharp
    void Complete()
    ```

    Empty implementation (no cleanup required).

### DelegateJob<TInput, TOutput> where TInput : unmanaged, TOutput : unmanaged

Executes a delegate-based `IJob` with Burst compilation.

- **Fields**:
  - `Delegate`: Function pointer to the job delegate.
  - `Inputs`: Input data array.
  - `Outputs`: Output data list.
  - `StartIndex`: Starting index for processing.
  - `Count`: Number of items to process.

- **Methods**:

  ```csharp
    void Execute()
    ```

    Invokes the delegate and logs performance if exceeding frame time in debug mode.

### DelegateParallelJob<TInput, TOutput> where TInput : unmanaged, TOutput : unmanaged

Executes a delegate-based `IJobParallelFor` with Burst compilation.

- **Fields**:
  - `Delegate`: Function pointer to the parallel job delegate.
  - `Inputs`: Input data array.
  - `Outputs`: Output data list.
  - `StartIndex`: Starting index for processing.
  - `BatchSize`: Size of each batch.

- **Methods**:

  ```csharp
    void Execute(int index)
    ```

    Processes a batch of items using the delegate.

### DelegateForJob<TInput, TOutput> where TInput : unmanaged, TOutput : unmanaged

Executes a delegate-based `IJobParallelFor` for single-item processing.

- **Fields**:
  - `Delegate`: Function pointer to the single-item delegate.
  - `Inputs`: Input data array.
  - `Outputs`: Output data list.
  - `StartIndex`: Starting index for processing.

- **Methods**:

  ```csharp
    void Execute(int index)
    ```

    Processes a single item using the delegate.

### DelegateParallelForTransformJob

Executes a delegate-based `IJobParallelForTransform` with Burst compilation.

- **Fields**:
  - `Delegate`: Function pointer to the transform delegate.
  - `StartIndex`: Starting index for processing.
  - `BatchSize`: Size of each batch.

- **Methods**:

  ```csharp
    void Execute(int index, TransformAccess transform)
    ```

    Processes a transform using the delegate.

### DelegateJobWrapper<TInput, TOutput> where TInput : unmanaged, TOutput : unmanaged

Wraps delegate-based jobs for various job types.

- **Fields**:
  - `_inputs`: Input data for the job.
  - `_outputs`: Output data for the job.

- **Properties**:
  - `IsParallel`: True for `IJobParallelFor` or `IJobParallelForTransform`.
  - `JobType`: The type of job.

- **Methods**:

  ```csharp
    DelegateJobWrapper(
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegateIJob,
        Action<int, NativeArray<TInput>, NativeList<TOutput>> burstDelegateFor,
        Action<int, TransformAccess> transformDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> outputs,
        TransformAccessArray transforms,
        int startIndex,
        int count,
        int batchSize,
        JobType jobType)
    ```

    Constructor initializing delegates, data, and job type.

  ```csharp
    JobHandle Schedule(int count = 0, int batchSize = 64)
    ```

    Schedules the appropriate job based on `JobType`.

  ```csharp
    void Complete()
    ```

    Disposes `TransformAccessArray` for transform jobs in debug mode.

### SingleEntityMetricsJob

Tracks metrics for a single entity job.

- **Fields**:
  - `TaskType`: Type of the task.
  - `Timestamp`: Execution timestamp.
  - `Metrics`: Array to store metrics.
  - `CacheMetrics`: Array to store cache metrics.
  - `CacheIndex`: Index in the cache metrics array.

- **Methods**:

  ```csharp
    void Execute()
    ```

    Tracks execution and cache metrics.

### Metric

Stores performance metrics for a method.

- **Fields**:
  - `MethodName`: Name of the method.
  - `ExecutionTimeMs`: Execution time in milliseconds.
  - `MainThreadImpactMs`: Main thread impact in milliseconds.
  - `ItemCount`: Number of items processed.

### CacheMetric

Stores cache access metrics.

- **Fields**:
  - `CacheName`: Name of the cache.
  - `IsHit`: Whether the cache access was a hit.

### SmartExecutionOptions

Options for configuring `SmartExecution` behavior.

- **Fields**:
  - `SpreadSingleItem`: Spread single-item jobs across frames.
  - `SingleItemThresholdMs`: Threshold for single-item processing.
  - `IsNetworked`: Whether execution is networked.
  - `BatchSizeOverride`: Overrides default batch size.
  - `PreferredJobType`: Preferred job type (optional).

- **Static Properties**:
  - `Default`: Default configuration.

---

## Classes

### Metrics

Tracks performance metrics for execution methods and cache access.

- **Static Methods**:

  ```csharp
    float GetAvgItemTimeMs(string methodName)
    ```

    Returns the average item processing time in milliseconds.

  ```csharp
    void Initialize()
    ```

    Initializes the metrics system and subscribes to tick events.

  ```csharp
    void TrackExecution(string methodName, Action action, int itemCount = 1)
    ```

    Tracks execution time and main thread impact of an action.

  ```csharp
    T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 1)
    ```

    Tracks execution time and main thread impact of a function.

  ```csharp
    Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)
    ```

    Tracks execution time and main thread impact of an async action.

  ```csharp
    Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)
    ```

    Tracks execution time and main thread impact of an async function.

  ```csharp
    IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)
    ```

    Tracks execution time and main thread impact of a coroutine.

  ```csharp
    void TrackJobCacheAccess(string cacheName, bool isHit)
    ```

    Tracks cache access for a job.

  ```csharp
    void TrackCacheAccess(string cacheName, bool isHit)
    ```

    Tracks cache access for non-job operations.

  ```csharp
    void Cleanup()
    ```

    Cleans up metrics and unsubscribes from tick events.

### DynamicProfiler

Manages dynamic profiling data with rolling averages and batch size history.

- **Static Methods**:

  ```csharp
    void AddSample(string methodName, double avgItemTimeMs)
    ```

    Adds a performance sample for a method.

  ```csharp
    void AddBatchSize(string uniqueId, int batchSize)
    ```

    Adds a batch size sample for a method.

  ```csharp
    float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)
    ```

    Returns the dynamic average processing time.

  ```csharp
    int GetAverageBatchSize(string uniqueId)
    ```

    Returns the average batch size.

  ```csharp
    void Cleanup()
    ```

    Cleans up profiling data.

### MainThreadImpactTracker

Tracks main thread impact for performance optimization.

- **Static Methods**:

  ```csharp
    void Initialize()
    ```

    Initializes the tracker with overhead calibration.

  ```csharp
    void AddImpactSample(string methodName, double impactTimeMs)
    ```

    Adds a main thread impact sample.

  ```csharp
    float GetAverageItemImpact(string methodName)
    ```

    Returns the average item impact.

  ```csharp
    float GetCoroutineCost(string coroutineKey)
    ```

    Returns the cost of a coroutine.

  ```csharp
    float GetJobOverhead(string jobKey)
    ```

    Returns the overhead of a job.

  ```csharp
    IEnumerator TrackJobWithStopwatch(string methodName, Func<JobHandle> scheduleJob, int itemCount = 1)
    ```

    Tracks a job's execution with stopwatch timing.

  ```csharp
    int BeginSample(string methodName)
    ```

    Begins tracking a main thread sample.

  ```csharp
    float EndSample(int sampleId)
    ```

    Ends tracking a main thread sample.

  ```csharp
    void Cleanup()
    ```

    Cleans up tracking data.

### MetricsCache

Caches performance and impact metrics.

- **Static Methods**:

  ```csharp
    void AddPerformanceSample(string methodName, double avgItemTimeMs)
    ```

    Adds a performance sample to the cache.

  ```csharp
    void AddImpactSample(string methodName, double impactTimeMs)
    ```

    Adds an impact sample to the cache.

  ```csharp
    bool TryGetAverage(string methodName, out double avg)
    ```

    Tries to get the average performance metric.

  ```csharp
    bool TryGetImpact(string methodName, out double impact)
    ```

    Tries to get the impact metric.

  ```csharp
    void Cleanup()
    ```

    Clears all cached metrics.

### SmartExecution

Manages intelligent execution of tasks.

- **Static Methods**:

  ```csharp
    void Initialize()
    ```

    Initializes the SmartExecution system.

  ```csharp
    IEnumerator Execute(
        string uniqueId,
        int itemCount,
        Action jobCallback = null,
        Func<IEnumerator> coroutineCallback = null,
        Action mainThreadCallback = null,
        Action<int, int, NativeArray<int>, NativeList<int>> burstDelegate = null,
        Action<int, NativeArray<int>, NativeList<int>> burstForDelegate = null,
        NativeArray<int> inputs = default,
        List<int> outputs = null,
        SmartExecutionOptions options = default)
    ```

    Executes a task with dynamic execution method selection.

  ```csharp
    void ExecuteTransforms<TInput, TOutput>(
        string uniqueId,
        TransformAccessArray transforms,
        Action<int, Transform, NativeArray<TInput>, List<TOutput>> transformDelegate,
        NativeArray<TInput> inputs = default,
        List<TOutput> outputs = null,
        SmartExecutionOptions options = default)
        where TInput : unmanaged
        where TOutput : unmanaged
    ```

    Executes a transform-based task.

  ```csharp
    void SaveBaselineData()
    ```

    Saves performance baseline data to a JSON file.

  ```csharp
    void LoadBaselineData()
    ```

    Loads performance baseline data from a JSON file.

### FishNetExtensions

Provides extensions for FishNet integration.

- **Static Methods**:

  ```csharp
    int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId, bool isParallel)
    ```

    Calculates dynamic batch size for FishNet tasks.

  ```csharp
    Task AwaitNextFishNetTickAsync(float seconds = 0)
    ```

    Asynchronously waits for the next FishNet tick.

  ```csharp
    IEnumerator WaitForNextTick()
    ```

    Waits for the next FishNet tick.

### TaskExtensions

Provides extensions for converting Tasks to Unity coroutines.

- **Static Methods**:

  ```csharp
    TaskYieldInstruction AsCoroutine(this Task task)
    ```

    Converts a Task to a coroutine.

  ```csharp
    TaskYieldInstruction<T> AsCoroutine<T>(this Task<T> task)
    ```

    Converts a Task with a result to a coroutine.

### CoroutineRunner

Manages coroutine execution in Unity with singleton behavior.

- **Static Properties**:
  - `Instance`: Singleton instance of the CoroutineRunner.

- **Methods**:

  ```csharp
    Coroutine RunCoroutine(IEnumerator coroutine)
    ```

    Runs a coroutine.

  ```csharp
    Coroutine RunCoroutineWithResult<T>(IEnumerator coroutine, Action<T> callback)
    ```

    Runs a coroutine with a result callback.

### PerformanceHarmonyPatches

Applies Harmony patches for performance-related functionality.

- **Static Methods**:

  ```csharp
    void SetupScreen_ClearFolderContents(string folderPath)
    ```

    Postfix patch to reset baseline data.

  ```csharp
    void ImportScreen_Confirm()
    ```

    Postfix patch to reset baseline data on confirm.

---

## Example Usage

### Scheduling a Job

```csharp
var inputs = new NativeArray<int>(100, Allocator.TempJob);
var outputs = new NativeList<int>(100, Allocator.TempJob);
Action<int, int, NativeArray<int>, NativeList<int>> burstDelegate = (start, end, inputs, outputs) =>
{
    for (int i = start; i < end; i++)
        outputs.Add(inputs[i] * 2);
};
var wrapper = new DelegateJobWrapper<int, int>(burstDelegate, null, null, inputs, outputs, default, 0, 100, 10, JobType.IJobParallelFor);
var handle = wrapper.Schedule();
handle.Complete();
wrapper.Complete();
inputs.Dispose();
outputs.Dispose()
```

### Executing a Task with SmartExecution

```csharp
string uniqueId = "MyTask";
int itemCount = 50;
Action<int, int, NativeArray<int>, NativeList<int>> burstDelegate = (start, end, inputs, outputs) =>
{
    for (int i = start; i < end; i++)
        outputs.Add(inputs[i] + 1);
};
var inputs = new NativeArray<int>(itemCount, Allocator.TempJob);
var outputs = new List<int>();
var coroutine = SmartExecution.Execute(uniqueId, itemCount, burstDelegate: burstDelegate, inputs: inputs, outputs: outputs);
CoroutineRunner.Instance.RunCoroutine(coroutine);
inputs.Dispose()
```

### Tracking Performance Metrics

```csharp
Metrics.Initialize();
string methodName = "MyMethod";
Metrics.TrackExecution(methodName, () => { /*Some work*/ }, itemCount: 10);
float avgTime = Metrics.GetAvgItemTimeMs(methodName);
Metrics.Cleanup()
```
