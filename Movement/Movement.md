# NoLazyWorkers.Metrics API Documentation

## Overview

The `NoLazyWorkers.Metrics` namespace provides performance monitoring and profiling tools for tracking execution times, cache access, and item processing metrics in a thread-safe and Burst-compatible manner. The `Performance` class manages performance metrics, while the `DynamicProfiler` class calculates rolling averages for dynamic profiling.

---

## Performance Class

The `Performance` class provides methods to track execution times, cache hits/misses, and item counts for various tasks, including synchronous, asynchronous, coroutine-based, and Burst-compiled operations.

### Structures

#### PerformanceMetric

Burst-compatible structure for storing performance metrics.

- **Fields**:
  - `MethodName` (`FixedString64Bytes`): Name of the method or task.
  - `ExecutionTimeMs` (`float`): Execution time in milliseconds.
  - `ItemCount` (`int`): Number of items processed.

#### PerformanceData

Structure for aggregating performance data.

- **Fields**:
  - `CallCount` (`long`): Total number of calls.
  - `TotalTimeMs` (`double`): Total execution time in milliseconds.
  - `MaxTimeMs` (`double`): Maximum execution time in milliseconds.
  - `CacheHits` (`long`): Number of cache hits.
  - `CacheMisses` (`long`): Number of cache misses.
  - `ItemCount` (`long`): Total items processed.
  - `AvgItemTimeMs` (`double`): Average time per item in milliseconds.

### Methods

#### Initialize

Initializes the performance metrics system.

```csharp
public static void Initialize()
{
    Performance.Initialize();
}
```

- **Description**: Subscribes to `TimeManagerInstance.OnTick` for periodic metric updates and logs initialization.
- **Thread-Safe**: Yes.

#### LogExecutionTime

Logs the execution time for a task.

```csharp
public static void LogExecutionTime(string taskId, long milliseconds)
{
    Performance.LogExecutionTime("MyTask", 50);
}
```

- **Parameters**:
  - `taskId` (`string`): Unique identifier for the task.
  - `milliseconds` (`long`): Execution time in milliseconds.
- **Thread-Safe**: Yes.

#### GetAverageTaskExecutionTime

Calculates the average execution time across all tracked tasks.

```csharp
public static float GetAverageTaskExecutionTime()
{
    float avgTime = Performance.GetAverageTaskExecutionTime();
}
```

- **Returns**: Average execution time in milliseconds (defaults to 100ms if no data).
- **Thread-Safe**: Yes.

#### TrackExecution (Action)

Tracks execution time for a synchronous action.

```csharp
public static void TrackExecution(string methodName, Action action, int itemCount = 0)
{
    Performance.TrackExecution("ProcessData", () => ProcessData(), 100);
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `action` (`Action`): The action to execute and measure.
  - `itemCount` (`int`): Number of items processed (optional, default: 0).
- **Thread-Safe**: Yes.

#### TrackExecution (Func<T>)

Tracks execution time for a synchronous function with a return value.

```csharp
public static T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 0)
{
    int result = Performance.TrackExecution("Calculate", () => CalculateValue(), 50);
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `action` (`Func<T>`): The function to execute and measure.
  - `itemCount` (`int`): Number of items processed (optional, default: 0).
- **Returns**: Result of the function.
- **Thread-Safe**: Yes.

#### TrackExecutionAsync (Task)

Tracks execution time for an asynchronous action.

```csharp
public static async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 0)
{
    await Performance.TrackExecutionAsync("FetchData", async () => await FetchDataAsync(), 10);
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `action` (`Func<Task>`): The asynchronous action to execute and measure.
  - `itemCount` (`int`): Number of items processed (optional, default: 0).
- **Thread-Safe**: Yes.

#### TrackExecutionAsync (Task<T>)

Tracks execution time for an asynchronous function with a return value.

```csharp
public static async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 0)
{
    var result = await Performance.TrackExecutionAsync("GetData", async () => await GetDataAsync(), 20);
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `action` (`Func<Task<T>>`): The asynchronous function to execute and measure.
  - `itemCount` (`int`): Number of items processed (optional, default: 0).
- **Returns**: Result of the asynchronous function.
- **Thread-Safe**: Yes.

#### TrackExecutionCoroutine

Tracks execution time for a Unity coroutine.

```csharp
public static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 0)
{
    yield return StartCoroutine(Performance.TrackExecutionCoroutine("ProcessCoroutine", MyCoroutine(), 30));
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `coroutine` (`IEnumerator`): The coroutine to execute and measure.
  - `itemCount` (`int`): Number of items processed (optional, default: 0).
- **Returns**: IEnumerator for coroutine execution.
- **Thread-Safe**: No (Unity coroutines are main-thread only).

#### TrackExecutionBurst

Collects performance metrics in a Burst-compiled job.

```csharp
public static void TrackExecutionBurst(FixedString64Bytes methodName, float executionTimeMs, int itemCount, NativeArray<PerformanceMetric> metrics, int index)
{
    var metrics = new NativeArray<PerformanceMetric>(1, Allocator.TempJob);
    Performance.TrackExecutionBurst("BurstJob".ToFixedString(), 10.5f, 100, metrics, 0);
}
```

- **Parameters**:
  - `methodName` (`FixedString64Bytes`): Name of the method or task.
  - `executionTimeMs` (`float`): Execution time in milliseconds.
  - `itemCount` (`int`): Number of items processed.
  - `metrics` (`NativeArray<PerformanceMetric>`): Array to store the metric.
  - `index` (`int`): Index in the metrics array.
- **Thread-Safe**: Yes (Burst-compatible).

#### TrackCacheAccess

Tracks cache hits or misses for a cache.

```csharp
public static void TrackCacheAccess(string cacheName, bool isHit)
{
    Performance.TrackCacheAccess("MyCache", true);
}
```

- **Parameters**:
  - `cacheName` (`string`): Name of the cache.
  - `isHit` (`bool`): True if cache hit, false if miss.
- **Thread-Safe**: Yes.

#### ProcessMetricsAsync

Processes performance metrics asynchronously and updates the `DynamicProfiler`.

```csharp
public static async Task ProcessMetricsAsync(NativeArray<PerformanceMetric> metrics)
{
    var metrics = new NativeArray<PerformanceMetric>(1, Allocator.TempJob);
    metrics[0] = new PerformanceMetric { MethodName = "Test".ToFixedString(), ExecutionTimeMs = 10, ItemCount = 5 };
    await Performance.ProcessMetricsAsync(metrics);
}
```

- **Parameters**:
  - `metrics` (`NativeArray<PerformanceMetric>`): Array of metrics to process.
- **Thread-Safe**: Yes.

#### GetAvgItemTimeMs

Retrieves the average time per item for a method.

```csharp
public static double GetAvgItemTimeMs(string methodName)
{
    double avgTime = Performance.GetAvgItemTimeMs("ProcessData");
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
- **Returns**: Average time per item in milliseconds (0 if no data or no items).
- **Thread-Safe**: Yes.

#### Cleanup

Cleans up the performance metrics system.

```csharp
public static void Cleanup()
{
    Performance.Cleanup();
}
```

- **Description**: Unsubscribes from `TimeManagerInstance.OnTick`, clears metrics, and logs cleanup.
- **Thread-Safe**: Yes.

---

## DynamicProfiler Class

The `DynamicProfiler` class calculates rolling averages of processing times for methods, with a fixed window size of 100 samples.

### Fields

- `WINDOW_SIZE` (`int`): Constant set to 100, defining the number of samples stored.
- `MIN_AVG_TIME_MS` (`float`): Minimum average processing time (0.05ms).
- `MAX_AVG_TIME_MS` (`float`): Maximum average processing time (0.5ms).

### Methods

#### AddSample

Adds a sample to the rolling average for a method.

```csharp
public static void AddSample(string methodName, double avgItemTimeMs)
{
    DynamicProfiler.AddSample("ProcessData", 0.1);
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `avgItemTimeMs` (`double`): Average time per item in milliseconds.
- **Thread-Safe**: Yes.

#### GetDynamicAvgProcessingTimeMs

Retrieves the dynamic average processing time for a method.

```csharp
public static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)
{
    float avgTime = DynamicProfiler.GetDynamicAvgProcessingTimeMs("ProcessData", 0.2f);
}
```

- **Parameters**:
  - `methodName` (`string`): Name of the method or task.
  - `defaultTimeMs` (`float`): Default time if no data (optional, default: 0.15ms).
- **Returns**: Average processing time in milliseconds, clamped between `MIN_AVG_TIME_MS` and `MAX_AVG_TIME_MS`.
- **Thread-Safe**: Yes.

#### Cleanup

Cleans up the dynamic profiler.

```csharp
public static void Cleanup()
{
    DynamicProfiler.Cleanup();
}
```

- **Description**: Clears all rolling averages and logs cleanup.
- **Thread-Safe**: Yes.

---

## Notes

- The `Performance` class uses `ConcurrentDictionary` for thread-safe metric storage.
- `TrackExecutionBurst` and `PerformanceMetric` are optimized for Unity's Burst compiler.
- `DynamicProfiler` uses a rolling average with a fixed window size to provide stable profiling data.
- Logging is performed using a custom `Log` method from the `NoLazyWorkers.Debug` namespace.
- External dependencies include `TimeManagerInstance`, `JobService.JobScheduler`, and `Stopwatch`.
