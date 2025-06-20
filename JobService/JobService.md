# API Documentation for NoLazyWorkers.JobService

This document describes the public API of the `NoLazyWorkers.JobService` namespace, which provides utilities for Burst-compatible job scheduling, logging, and resource management in a game or simulation environment. The system integrates with Unity's Burst compiler for performance optimization and FishNet for networked operations. It includes deferred logging, dynamic batch processing, and type-safe disposal of native containers.

**Namespace**: `NoLazyWorkers.JobService`

**Current Date**: June 15, 2025

## Classes and Structs

### DeferredLogger

Processes deferred logs from Burst jobs.

#### `static class DeferredLogger`

- **Methods**:
  - `void ProcessLogs(NativeList<LogEntry> logs)`
    - Processes and logs deferred log entries from Burst jobs, then disposes the log list.
    - **Parameters**:
      - `logs`: Native list of `LogEntry` structs to process.

#### `struct LogEntry` [BurstCompile]

Represents a log entry for deferred logging in Burst jobs.

- **Fields**:
  - `FixedString128Bytes Message`: Log message.
  - `DebugLogger.LogLevel Level`: Log severity level.
  - `DebugLogger.Category Category`: Log category.

### JobScheduler

Provides utilities for Burst-compatible job scheduling and batch processing.

#### `static class JobScheduler`

- **Methods**:
  - `async Task ExecuteInBatchesAsync(int totalItems, Action<int> action, string methodName, float defaultAvgProcessingTimeMs = 0.15f)`
    - Executes an action in batches across frames to spread processing load, using `AwaitNextTickAsync`.
    - **Parameters**:
      - `totalItems`: Total number of items to process.
      - `action`: Action to execute for each item, taking the item index.
      - `methodName`: Name of the method for profiling.
      - `defaultAvgProcessingTimeMs`: Default average processing time per item in milliseconds (default: 0.15f).
    - **Returns**: Task representing the asynchronous operation.
  - `int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string methodName)`
    - Calculates a dynamic batch size based on total items and processing time, targeting 60 FPS (16.67ms frame time).
    - **Parameters**:
      - `totalItems`: Total number of items.
      - `defaultAvgProcessingTimeMs`: Default average processing time per item in milliseconds.
      - `methodName`: Method name for profiling.
    - **Returns**: Batch size, clamped between 1 and `totalItems`.

### JobResourceManager

Manages disposal of native containers in Burst jobs, ensuring type safety.

#### `struct JobResourceManager : IDisposable` [BurstCompile]

- **Fields**:
  - `NativeList<ArrayWrapper<byte>> _arrays`: Stores `NativeArray<T>` wrappers.
  - `NativeList<ListWrapper<byte>> _lists`: Stores `NativeList<T>` wrappers.
  - `NativeList<HashSetWrapper<byte>> _hashSets`: Stores `NativeParallelHashSet<T>` wrappers.
  - `NativeList<HashMapWrapper<byte, byte>> _hashMaps`: Stores `NativeParallelHashMap<K,V>` wrappers.
  - `NativeList<QueueWrapper<byte>> _queues`: Stores `NativeQueue<T>` wrappers.

- **Constructor**: `JobResourceManager(Allocator allocator)`
  - Initializes lists for native container wrappers with the specified allocator.

- **Methods**:
  - `void Register<T>(NativeArray<T> container) where T : struct`
    - Registers a `NativeArray<T>` for disposal.
    - **Parameters**:
      - `container`: Native array to register.
  - `void Register<T>(NativeList<T> container) where T : unmanaged`
    - Registers a `NativeList<T>` for disposal.
    - **Parameters**:
      - `container`: Native list to register.
  - `void Register<T>(NativeParallelHashSet<T> container) where T : unmanaged, IEquatable<T>`
    - Registers a `NativeParallelHashSet<T>` for disposal.
    - **Parameters**:
      - `container`: Native hash set to register.
  - `void Register<K, V>(NativeParallelHashMap<K, V> container) where K : unmanaged, IEquatable<K>, V : unmanaged`
    - Registers a `NativeParallelHashMap<K,V>` for disposal.
    - **Parameters**:
      - `container`: Native hash map to register.
  - `void Register<T>(NativeQueue<T> container) where T : unmanaged`
    - Registers a `NativeQueue<T>` for disposal.
    - **Parameters**:
      - `container`: Native queue to register.
  - `void Dispose()`
    - Disposes all registered native containers without logging.
  - `void Dispose(NativeList<DeferredLogger.LogEntry> logs)`
    - Disposes all registered native containers and logs disposal events.
    - **Parameters**:
      - `logs`: Native list to store disposal log entries.

#### `private interface IDisposalWrapper` [BurstCompile]

Internal interface for wrapping native containers.

- **Methods**:
  - `void Dispose()`: Disposes the wrapped container.

#### `private struct ArrayWrapper<T> : IDisposalWrapper where T : struct` [BurstCompile]

Wraps a `NativeArray<T>` for disposal.

- **Fields**:
  - `NativeArray<T> Container`: Wrapped native array.

- **Constructor**: `ArrayWrapper(NativeArray<T> container)`
- **Methods**:
  - `void Dispose()`: Disposes the native array if created.

#### `private struct ListWrapper<T> : IDisposalWrapper where T : unmanaged` [BurstCompile]

Wraps a `NativeList<T>` for disposal.

- **Fields**:
  - `NativeList<T> Container`: Wrapped native list.

- **Constructor**: `ListWrapper(NativeList<T> container)`
- **Methods**:
  - `void Dispose()`: Disposes the native list if created.

#### `private struct HashSetWrapper<T> : IDisposalWrapper where T : unmanaged, IEquatable<T>` [BurstCompile]

Wraps a `NativeParallelHashSet<T>` for disposal.

- **Fields**:
  - `NativeParallelHashSet<T> Container`: Wrapped native hash set.

- **Constructor**: `HashSetWrapper(NativeParallelHashSet<T> container)`
- **Methods**:
  - `void Dispose()`: Disposes the native hash set if created.

#### `private struct HashMapWrapper<K, V> : IDisposalWrapper where K : unmanaged, IEquatable<K>, V : unmanaged` [BurstCompile]

Wraps a `NativeParallelHashMap<K,V>` for disposal.

- **Fields**:
  - `NativeParallelHashMap<K, V> Container`: Wrapped native hash map.

- **Constructor**: `HashMapWrapper(NativeParallelHashMap<K, V> container)`
- **Methods**:
  - `void Dispose()`: Disposes the native hash map if created.

#### `private struct QueueWrapper<T> : IDisposalWrapper where T : unmanaged` [BurstCompile]

Wraps a `NativeQueue<T>` for disposal.

- **Fields**:
  - `NativeQueue<T> Container`: Wrapped native queue.

- **Constructor**: `QueueWrapper(NativeQueue<T> container)`
- **Methods**:
  - `void Dispose()`: Disposes the native queue if created.

## Usage Notes

- **Burst Compilation**: Structs like `JobResourceManager` and `LogEntry` are Burst-compiled for performance. Use Burst-compatible types (e.g., `NativeList`, `FixedString128Bytes`).
- **Deferred Logging**: Use `DeferredLogger.ProcessLogs` to handle logs from Burst jobs, ensuring thread-safe logging.
- **Batch Processing**: `JobScheduler.ExecuteInBatchesAsync` spreads processing across frames to maintain performance, ideal for large datasets.
- **Resource Management**: `JobResourceManager` ensures safe disposal of native containers, preventing memory leaks in Burst jobs.
- **FishNet Integration**: Methods like `ExecuteInBatchesAsync` align with FishNet’s `TimeManager` for networked tick-based operations.
- **Logging**: Logs are categorized under `Jobs` using `DebugLogger`, with levels like `Info` and `Verbose`.

## Example Usage

### Processing Deferred Logs

```csharp
void ProcessJobLogs(NativeList<DeferredLogger.LogEntry> logs)
{
    DeferredLogger.ProcessLogs(logs);
    Log("Processed deferred logs from Burst job");
}
```

### Executing Batched Actions

```csharp
async Task ProcessItems(int count)
{
    await JobScheduler.ExecuteInBatchesAsync(count, i => {
        // Process item at index i
        Log($"Processing item {i}");
    }, nameof(ProcessItems), 0.2f);
    Log("Batch processing completed");
}
```

### Managing Native Containers

```csharp
void RunJobWithResources()
{
    var allocator = Allocator.TempJob;
    var resources = new JobResourceManager(allocator);
    var array = new NativeArray<int>(10, allocator);
    resources.Register(array);
    // Run Burst job using array
    resources.Dispose(); // Clean up
}
```
