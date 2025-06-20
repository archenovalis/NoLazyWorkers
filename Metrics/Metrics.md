# API Documentation for NoLazyWorkers.Metrics

This document describes the public API of the `NoLazyWorkers.Metrics` namespace, which provides performance tracking and profiling utilities for a game or simulation environment. The system integrates with Unity's Burst compiler and FishNet for networked operations, offering performance metrics collection, cache tracking, and dynamic profiling.

**Namespace**: `NoLazyWorkers.Metrics`

**Current Date**: June 15, 2025

## Classes and Structs

### Performance

Manages performance metrics collection and cache access tracking.

#### `static class Performance`

- **Fields**:
  - `ConcurrentDictionary<string, PerformanceData> _metrics`: Stores performance data by method name.
  - `Stopwatch _stopwatch`: Tracks execution time.

- **Structs**:
  - `PerformanceMetric` [BurstCompile]
    - Represents a Burst-compatible performance metric.
    - **Fields**:
      - `FixedString64Bytes MethodName`: Method name.
      - `float ExecutionTimeMs`: Execution time in milliseconds.
      - `int ItemCount`: Number of items processed.
  - `PerformanceData`
    - Stores aggregated performance data.
    - **Fields**:
      - `long CallCount`: Total calls.
      - `double TotalTimeMs`: Total execution time.
      - `double MaxTimeMs`: Maximum execution time.
      - `long CacheHits`: Cache hits.
      - `long CacheMisses`: Cache misses.
      - `long ItemCount`: Total items processed.
      - `double AvgItemTimeMs`: Average time per item.

- **Methods**:
  - `void Initialize()`
    - Initializes the performance system, subscribing to FishNet’s `TimeManager.OnTick`.
  - `void TrackExecution(string methodName, Action action, int itemCount = 0)`
    - Tracks the execution time of an action.
    - **Parameters**:
      - `methodName`: Method name for metrics.
      - `action`: Action to execute.
      - `itemCount`: Number of items processed (default: 0).
  - `T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 0)`
    - Tracks the execution time of a function returning a value.
    - **Parameters**:
      - `methodName`: Method name for metrics.
      - `action`: Function to execute.
      - `itemCount`: Number of items processed (default: 0).
    - **Returns**: Result of the function.
  - `async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 0)`
    - Tracks the execution time of an asynchronous action.
    - **Parameters**:
      - `methodName`: Method name for metrics.
      - `action`: Asynchronous action to execute.
      - `itemCount`: Number of items processed (default: 0).
  - `async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 0)`
    - Tracks the execution time of an asynchronous function returning a value.
    - **Parameters**:
      - `methodName`: Method name for metrics.
      - `action`: Asynchronous function to execute.
      - `itemCount`: Number of items processed (default: 0).
    - **Returns**: Result of the function.
  - `IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 0)`
    - Tracks the execution time of a coroutine.
    - **Parameters**:
      - `methodName`: Method name for metrics.
      - `coroutine`: Coroutine to execute.
      - `itemCount`: Number of items processed (default: 0).
    - **Returns**: Coroutine enumerator.
  - `void TrackExecutionBurst(FixedString64Bytes methodName, float executionTimeMs, int itemCount, NativeArray<PerformanceMetric> metrics, int index)` [BurstCompile]
    - Collects a performance metric within a Burst job.
    - **Parameters**:
      - `methodName`: Method name.
      - `executionTimeMs`: Execution time in milliseconds.
      - `itemCount`: Number of items processed.
      - `metrics`: Native array to store the metric.
      - `index`: Index in the metrics array.
  - `void TrackCacheAccess(string cacheName, bool isHit)`
    - Tracks cache access, recording hits or misses.
    - **Parameters**:
      - `cacheName`: Cache name.
      - `isHit`: True if cache hit, false if miss.
  - `async Task ProcessMetricsAsync(NativeArray<PerformanceMetric> metrics)`
    - Processes performance metrics asynchronously, updating `DynamicProfiler`.
    - **Parameters**:
      - `metrics`: Native array of performance metrics.
  - `double GetAvgItemTimeMs(string methodName)`
    - Gets the average processing time per item for a method.
    - **Parameters**:
      - `methodName`: Method name.
    - **Returns**: Average time per item in milliseconds, or 0 if no data.
  - `void Cleanup()`
    - Cleans up metrics, unsubscribing from `TimeManager.OnTick`.

- **Jobs**:
- `SingleEntityMetricsJob` : `IJob` [BurstCompile]

Processes performance metrics for a single entity in a Burst-compiled job.

- **Description**: Schedules a fire-and-forget Job to record and process metrics for a single entity, updating the `DynamicProfiler` synchronously within the Job.
- **Parameters**:
  - `TaskType` (`FixedString64Bytes`): Name of the task or method.
  - `Timestamp` (`float`): Execution time in milliseconds (typically Time.realtimeSinceStartup * 1000f).
  - `Metrics` (`NativeArray<PerformanceMetric>`): Array to store the metric (size 1, Allocator.TempJob).
- **Thread-Safe**: Yes (Burst-compatible).
- **Usage**:

```csharp
var metrics = new NativeArray<PerformanceMetric>(1, Allocator.TempJob);
var job = new SingleEntityMetricsJob
{
    TaskType = new FixedString64Bytes("MixingStation"),
    Timestamp = Time.realtimeSinceStartup * 1000f,
    Metrics = metrics
};
job.Schedule(); // Fire-and-forget
```

### DynamicProfiler

Manages dynamic profiling with rolling averages for processing times.

#### `static class DynamicProfiler`

- **Fields**:
  - `ConcurrentDictionary<string, RollingAverage> _rollingAverages`: Stores rolling averages by method name.
  - `const int WINDOW_SIZE = 100`: Number of samples to store.
  - `const float MIN_AVG_TIME_MS = 0.05f`: Minimum average processing time.
  - `const float MAX_AVG_TIME_MS = 0.5f`: Maximum average processing time.

- **Methods**:
  - `void AddSample(string methodName, double avgItemTimeMs)`
    - Adds a sample to the rolling average for a method.
    - **Parameters**:
      - `methodName`: Method name.
      - `avgItemTimeMs`: Average time per item in milliseconds.
  - `float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)`
    - Gets the dynamic average processing time for a method.
    - **Parameters**:
      - `methodName`: Method name.
      - `defaultTimeMs`: Default time if no data (default: 0.15f).
    - **Returns**: Average processing time in milliseconds.
  - `void Cleanup()`
    - Clears all rolling averages.

#### `private class RollingAverage`

Internal class for calculating rolling averages.

- **Fields**:
  - `double[] _samples`: Array of samples.
  - `int _index`: Current sample index.
  - `int Count`: Number of samples.
  - `double _sum`: Sum of samples.

- **Constructor**: `RollingAverage(int size)`
- **Methods**:
  - `void AddSample(double value)`: Adds a sample, updating the rolling average.
  - `double GetAverage()`: Gets the clamped average of samples.

## Usage Notes

- **Burst Compilation**: `PerformanceMetric` and `TrackExecutionBurst` are Burst-compiled for use in Burst jobs.
- **Performance Tracking**: Use `TrackExecution` variants to measure execution time for actions, functions, async tasks, or coroutines.
- **Cache Tracking**: `TrackCacheAccess` monitors cache performance, useful for optimizing storage lookups.
- **Dynamic Profiling**: `DynamicProfiler` maintains rolling averages to adapt batch sizes in `JobScheduler`.
- **FishNet Integration**: Metrics are updated on FishNet’s `TimeManager.OnTick` for networked consistency.
- **Logging**: Logs are categorized under `Performance` or `Tasks` using `DebugLogger`.

## Example Usage

### Tracking Execution Time

```csharp
void ProcessAction()
{
    Performance.TrackExecution("ProcessAction", () => {
        // Perform action
        Log("Action executed");
    }, itemCount: 10);
}
```

### Tracking Async Execution

```csharp
async Task ProcessAsync()
{
    await Performance.TrackExecutionAsync("ProcessAsync", async () => {
        await Task.Delay(100);
        Log("Async task completed");
    }, itemCount: 5);
}
```

### Adding a Profiling Sample

```csharp
void AddProfileSample()
{
    DynamicProfiler.AddSample("MyMethod", 0.2);
    float avgTime = DynamicProfiler.GetDynamicAvgProcessingTimeMs("MyMethod");
    Log($"Average processing time: {avgTime:F3}ms");
}
```
