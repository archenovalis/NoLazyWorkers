# Performance Metrics API Documentation

**Date**: June 20, 2025

This API provides tools for tracking performance metrics, managing coroutines, and optimizing execution in a Unity environment with FishNet integration. It includes classes and methods for profiling execution times, main thread impact, cache access, and dynamic task execution.

## Namespace: NoLazyWorkers.Performance

### Class: Metrics

Tracks performance metrics for execution times, main thread impact, and cache access.

#### Struct: SingleEntityMetricsJob

A Burst-compiled job for tracking metrics of a single entity.

- **Fields**:
  - `TaskType` (FixedString64Bytes): The type of task being tracked.
  - `Timestamp` (float): The timestamp of the job execution.
  - `Metrics` (NativeArray<Metric>): The array to store metric data.

- **Methods**:
  - `Execute()`: Executes the job, storing metrics in the `Metrics` array.

```csharp
// Example: Scheduling a SingleEntityMetricsJob
var metricsArray = new NativeArray<Metric>(1, Allocator.TempJob);
var job = new SingleEntityMetricsJob
{
    TaskType = "ProcessEntity",
    Timestamp = Time.time,
    Metrics = metricsArray
};
```

#### Struct: Metric

Represents a performance metric for a method.

- **Fields**:
  - `MethodName` (FixedString64Bytes): The name of the method.
  - `ExecutionTimeMs` (float): Total execution time in milliseconds.
  - `MainThreadImpactMs` (float): Main thread impact time in milliseconds.
  - `ItemCount` (int): Number of items processed.

#### Struct: Data

Aggregated performance data for a method.

- **Fields**:
  - `CallCount` (long): Total number of calls.
  - `TotalTimeMs` (double): Total execution time in milliseconds.
  - `MaxTimeMs` (double): Maximum execution time in milliseconds.
  - `TotalMainThreadImpactMs` (double): Total main thread impact time in milliseconds.
  - `MaxMainThreadImpactMs` (double): Maximum main thread impact time in milliseconds.
  - `CacheHits` (long): Number of cache hits.
  - `CacheMisses` (long): Number of cache misses.
  - `ItemCount` (long): Total number of items processed.
  - `AvgItemTimeMs` (double): Average execution time per item in milliseconds.
  - `AvgMainThreadImpactMs` (double): Average main thread impact per item in milliseconds.

#### Methods

- `Initialize()`: Initializes the metrics system (server-side only).
- `LogExecutionTime(string taskId, long milliseconds)`: Logs execution time for a task.
- `GetAverageTaskExecutionTime()`: Returns the average execution time across all tasks.
  - **Returns**: float (average time in milliseconds, default 100f if empty).
- `TrackExecution(string methodName, Action action, int itemCount = 1)`: Tracks execution time and main thread impact of an action.
- `TrackExecution<T>(string methodName, Func<T> action, int itemCount = 1)`: Tracks execution time and main thread impact of a function with a return value.
  - **Returns**: T (result of the function).
- `TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)`: Tracks execution time and main thread impact of an asynchronous action.
  - **Returns**: Task.
- `TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)`: Tracks execution time and main thread impact of an asynchronous function.
  - **Returns**: Task<T> (result of the function).
- `TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)`: Tracks execution time and main thread impact of a coroutine.
  - **Returns**: IEnumerator.
- `TrackExecutionBurst(FixedString64Bytes methodName, float executionTimeMs, int itemCount, NativeArray<Metric> metrics, int index)`: Tracks execution metrics in a Burst-compiled job.
- `TrackCacheAccess(string cacheName, bool isHit)`: Tracks cache hit or miss for a given cache.
- `ProcessMetricsAsync(NativeArray<Metric> metrics)`: Processes an array of metrics asynchronously.
  - **Returns**: Task.
- `GetAvgItemTimeMs(string methodName)`: Returns the average execution time per item for a method.
  - **Returns**: double (average time in milliseconds, or 0 if not found).
- `GetAvgMainThreadImpactMs(string methodName)`: Returns the average main thread impact per item for a method.
  - **Returns**: double (average impact in milliseconds, or 0 if not found).
- `Cleanup()`: Clears all metrics data and unsubscribes from events.

```csharp
// Example: Tracking an action
Metrics.TrackExecution("ProcessData", () => ProcessDataMethod(), itemCount: 10);

// Example: Tracking an async function
var result = await Metrics.TrackExecutionAsync("FetchData", async () => await FetchDataAsync(), itemCount: 5)
```

### Class: DynamicProfiler

Manages dynamic profiling using rolling averages.

#### Methods

- `AddSample(string methodName, double avgItemTimeMs)`: Adds a sample to the rolling average for a method.
- `GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)`: Gets the dynamic average processing time for a method.
  - **Returns**: float (average time in milliseconds, or defaultTimeMs if not found).
- `Cleanup()`: Clears all rolling average data.

```csharp
// Example: Adding a sample and retrieving average
DynamicProfiler.AddSample("ProcessData", 0.2);
float avgTime = DynamicProfiler.GetDynamicAvgProcessingTimeMs("ProcessData")
```

### Class: MainThreadImpactTracker

Tracks main thread impact of method executions.

#### Methods

- `Initialize()`: Initializes the tracker.
- `BeginSample(string methodName)`: Begins a new main thread impact sample.
  - **Returns**: int (sample ID).
- `EndSample(int sampleId)`: Ends a sample and records the impact time.
  - **Returns**: float (impact time in milliseconds).
- `GetAverageItemImpact(string methodName)`: Gets the average main thread impact for a method.
  - **Returns**: float (average impact in milliseconds, or 0 if not found).
- `Cleanup()`: Clears all impact data and unsubscribes from events.

```csharp
// Example: Tracking main thread impact
int sampleId = MainThreadImpactTracker.BeginSample("ProcessData");
ProcessDataMethod();
float impactMs = MainThreadImpactTracker.EndSample(sampleId)
```

### Class: SmartExecution

Manages smart execution of tasks using jobs, coroutines, or main thread.

#### Fields

- `MetricsThresholds` (ConcurrentDictionary<string, int>): Stores execution thresholds.
- `DEFAULT_THRESHOLD` (const int): Default threshold for job execution (100).

#### Methods

- `Execute(string uniqueId, int itemCount, Action jobCallback = null, Func<IEnumerator> coroutineCallback = null, Action mainThreadCallback = null)`: Executes a task using the most efficient method based on performance metrics.
  - **Returns**: IEnumerator.

```csharp
// Example: Smart execution with coroutine
yield return SmartExecution.Execute("ProcessItems", 50, coroutineCallback: () => ProcessItemsCoroutine())
```

### Class: FishNetExtensions

Provides extensions for FishNet integration.

#### Fields

- `IsServer` (bool): Indicates if the application is running as a server.
- `TimeManagerInstance` (TimeManager): FishNet TimeManager instance.

#### Methods

- `GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs = 0.15f, string methodName = null)`: Calculates dynamic batch size based on performance metrics.
  - **Returns**: int (batch size).
- `WaitForNextTick()`: Waits for the next FishNet tick.
  - **Returns**: IEnumerator.

```csharp
// Example: Calculating batch size
int batchSize = FishNetExtensions.GetDynamicBatchSize(100, methodName: "ProcessData");

// Example: Waiting for next tick
yield return FishNetExtensions.WaitForNextTick()
```

### Class: TaskExtensions

Provides extensions for converting Tasks to Unity coroutines.

#### Methods

- `AsCoroutine(this Task task)`: Converts a Task to a coroutine.
  - **Returns**: TaskYieldInstruction.
- `AsCoroutine<T>(this Task<T> task)`: Converts a Task with a result to a coroutine.
  - **Returns**: TaskYieldInstruction<T>.

#### Class: TaskYieldInstruction

A custom yield instruction for awaiting a Task in a coroutine.

- **Fields**:
  - `keepWaiting` (bool): Indicates if the coroutine should continue waiting.

#### Class: TaskYieldInstruction<T>

A custom yield instruction for awaiting a Task with a result in a coroutine.

- **Fields**:
  - `keepWaiting` (bool): Indicates if the coroutine should continue waiting.
  - `Result` (T): The result of the task if completed, otherwise default(T).

```csharp
// Example: Converting a Task to a coroutine
Task task = SomeAsyncMethod();
yield return task.AsCoroutine()
```

### Class: CoroutineRunner

Manages coroutine execution with singleton behavior.

#### Properties

- `Instance` (CoroutineRunner): Singleton instance of the CoroutineRunner.

#### Methods

- `RunCoroutine(IEnumerator coroutine)`: Runs a coroutine.
  - **Returns**: Coroutine.
- `RunCoroutineWithResult<T>(IEnumerator coroutine, Action<T> callback)`: Runs a coroutine with a result callback.
  - **Returns**: Coroutine.

```csharp
// Example: Running a coroutine
CoroutineRunner.Instance.RunCoroutine(MyCoroutine());

// Example: Running a coroutine with result
CoroutineRunner.Instance.RunCoroutineWithResult<string>(MyCoroutineWithResult(), result => Debug.Log(result))
```
