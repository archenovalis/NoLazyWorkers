using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using static NoLazyWorkers.Performance.FishNetExtensions;
using static NoLazyWorkers.Debug;
using Unity.Jobs;
using UnityEngine;
using FishNet.Managing.Timing;
using ScheduleOne.Persistence;
using MelonLoader.Utils;
using Newtonsoft.Json;
using System.IO.Compression;

namespace NoLazyWorkers.Performance
{
  public enum JobType
  {
    IJob,
    IJobParallelFor
  }

  public interface IExecutableTask<T> where T : struct
  {
    [BurstCompile]
    void ExecuteItem(int index, ref NativeArray<T> data);

    void ExecuteItemNonBurst(int index, ref NativeArray<T> data) { } // Optional fallback
  }

  public interface IJobWrapper
  {
    JobHandle Schedule(int count = 0, int batchSize = 64);
    void Complete();
    bool IsParallel { get; }
  }

  public struct JobWrapper<T>(T job) : IJobWrapper where T : struct, IJob
  {
    public T Job = job;
    public readonly bool IsParallel => false;

    public readonly JobHandle Schedule(int count = 0, int batchSize = 64)
    {
      return Job.Schedule();
    }

    public readonly void Complete()
    {
      // No-op, handled by caller
    }
  }

  public struct JobParallelForWrapper<T>(T job, int count, int batchSize) : IJobWrapper where T : struct, IJobParallelFor
  {
    public T Job = job;
    private readonly int _count = count;
    private readonly int _batchSize = batchSize;
    public readonly bool IsParallel => true;

    public readonly JobHandle Schedule(int count = 0, int batchSize = 64)
    {
      return Job.Schedule(_count, _batchSize);
    }

    public readonly void Complete()
    {
      // No-op, handled by caller
    }
  }

  [BurstCompile]
  public struct GenericParallelJob<T> : IJobParallelFor where T : struct
  {
    public NativeArray<T> Data;
    [ReadOnly] public IExecutableTask<T> Task;

    public void Execute(int index)
    {
      Task.ExecuteItem(index, ref Data);
    }
  }

  [BurstCompile]
  public struct GenericJob<T> : IJob where T : struct
  {
    public NativeArray<T> Data;
    [ReadOnly] public IExecutableTask<T> Task;
    public int ItemCount;

    public void Execute()
    {
      for (int i = 0; i < ItemCount; i++)
      {
        Task.ExecuteItem(i, ref Data);
      }
    }
  }

  /// <summary>
  /// Provides performance metrics tracking for execution times, main thread impact, and cache access.
  /// </summary>
  public static class Metrics
  {
    public static float GetAvgItemTimeMs(string methodName)
    {
      return MainThreadImpactTracker.GetAverageItemImpact(methodName);
    }

    /// <summary>
    /// A Burst-compiled job for tracking metrics of a single entity.
    /// </summary>
    [BurstCompile]
    public struct SingleEntityMetricsJob : IJob
    {
      public FixedString64Bytes TaskType;
      public float Timestamp;
      public NativeArray<Metric> Metrics;

      public void Execute()
      {
        TrackExecutionBurst(TaskType, Timestamp, 1, Metrics, 0);
      }
    }

    /// <summary>
    /// Represents a performance metric for a method.
    /// </summary>
    [BurstCompile]
    public struct Metric
    {
      public FixedString64Bytes MethodName;
      public float ExecutionTimeMs;
      public float MainThreadImpactMs;
      public int ItemCount;
    }

    /// <summary>
    /// Aggregated performance data for a method.
    /// </summary>
    public struct Data
    {
      public long CallCount;
      public double TotalTimeMs;
      public double MaxTimeMs;
      public double TotalMainThreadImpactMs;
      public double MaxMainThreadImpactMs;
      public long CacheHits;
      public long CacheMisses;
      public long ItemCount;
      public double AvgItemTimeMs;
      public double AvgMainThreadImpactMs;
    }

    private static readonly ConcurrentDictionary<string, float> _executionTimes = new();
    private static readonly ConcurrentDictionary<string, Data> _metrics = new();
    private static readonly Stopwatch _stopwatch = new();
    private static bool _isInitialized;

    /// <summary>
    /// Initializes the performance metrics system (server-side only).
    /// </summary>
    public static void Initialize()
    {
      if (_isInitialized || !FishNetExtensions.IsServer) return;
      _isInitialized = true;
      TimeManagerInstance.OnTick += UpdateMetrics;
      MainThreadImpactTracker.Initialize();
      Log(Level.Info, "PerformanceMetrics initialized (server-side only)", Category.Performance);
    }

    /// <summary>
    /// Logs the execution time for a task.
    /// </summary>
    /// <param name="taskId">The unique identifier for the task.</param>
    /// <param name="milliseconds">The execution time in milliseconds.</param>
    public static void LogExecutionTime(string taskId, long milliseconds)
    {
      _executionTimes[taskId] = milliseconds;
    }

    /// <summary>
    /// Calculates the average execution time across all tracked tasks.
    /// </summary>
    /// <returns>The average execution time in milliseconds, or 100f if no tasks are tracked.</returns>
    public static float GetAverageTaskExecutionTime()
    {
      return _executionTimes.Values.DefaultIfEmpty(100f).Average();
    }

    /// <summary>
    /// Tracks the execution time and main thread impact of an action.
    /// </summary>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <param name="action">The action to execute and track.</param>
    /// <param name="itemCount">The number of items processed (default is 1).</param>
    public static void TrackExecution(string methodName, Action action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
    }

    /// <summary>
    /// Tracks the execution time and main thread impact of a function with a return value.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <param name="action">The function to execute and track.</param>
    /// <param name="itemCount">The number of items processed (default is 1).</param>
    /// <returns>The result of the function.</returns>
    public static T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      T result = action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
      return result;
    }

    /// <summary>
    /// Tracks the execution time and main thread impact of an asynchronous action.
    /// </summary>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <param name="action">The asynchronous action to execute and track.</param>
    /// <param name="itemCount">The number of items processed (default is 1).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      await action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
    }

    /// <summary>
    /// Tracks the execution time and main thread impact of an asynchronous function with a return value.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <param name="action">The asynchronous function to execute and track.</param>
    /// <param name="itemCount">The number of items processed (default is 1).</param>
    /// <returns>A task representing the asynchronous operation with the result.</returns>
    public static async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      var result = await action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
      return result;
    }

    /// <summary>
    /// Tracks the execution time and main thread impact of a coroutine.
    /// </summary>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <param name="coroutine">The coroutine to execute and track.</param>
    /// <param name="itemCount">The number of items processed (default is 1).</param>
    /// <returns>An enumerator for the coroutine.</returns>
    public static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      while (coroutine.MoveNext())
        yield return coroutine.Current;
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
    }

    public static IEnumerator TrackExecutionCoroutine<T>(string methodName, IEnumerator coroutine, int itemCount)
    {
      var stopwatch = Stopwatch.StartNew();
      while (coroutine.MoveNext())
      {
        yield return coroutine.Current;
      }
      stopwatch.Stop();
      double impactTimeMs = stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency - MainThreadImpactTracker.StopwatchOverheadMs;
      MainThreadImpactTracker.AddImpactSample(methodName, itemCount > 0 ? impactTimeMs / itemCount : impactTimeMs);
    }

    /// <summary>
    /// Tracks execution metrics in a Burst-compiled job.
    /// </summary>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <param name="executionTimeMs">The execution time in milliseconds.</param>
    /// <param name="itemCount">The number of items processed.</param>
    /// <param name="metrics">The array to store the metric data.</param>
    /// <param name="index">The index in the metrics array to store the data.</param>
    [BurstCompile]
    public static void TrackExecutionBurst(
        FixedString64Bytes methodName,
        float executionTimeMs,
        int itemCount,
        NativeArray<Metric> metrics,
        int index)
    {
      metrics[index] = new Metric
      {
        MethodName = methodName,
        ExecutionTimeMs = executionTimeMs,
        MainThreadImpactMs = 0, // Jobs have no main-thread impact
        ItemCount = itemCount
      };
    }

    /// <summary>
    /// Tracks cache access (hit or miss) for a given cache.
    /// </summary>
    /// <param name="cacheName">The name of the cache.</param>
    /// <param name="isHit">True if the access was a cache hit, false if a miss.</param>
    public static void TrackCacheAccess(string cacheName, bool isHit)
    {
      _metrics.AddOrUpdate(cacheName, _ => new Data { CacheHits = isHit ? 1 : 0, CacheMisses = isHit ? 0 : 1 },
          (_, m) => new Data
          {
            CallCount = m.CallCount,
            TotalTimeMs = m.TotalTimeMs,
            MaxTimeMs = m.MaxTimeMs,
            TotalMainThreadImpactMs = m.TotalMainThreadImpactMs,
            MaxMainThreadImpactMs = m.MaxMainThreadImpactMs,
            CacheHits = m.CacheHits + (isHit ? 1 : 0),
            CacheMisses = m.CacheMisses + (isHit ? 0 : 1),
            ItemCount = m.ItemCount,
            AvgItemTimeMs = m.AvgItemTimeMs,
            AvgMainThreadImpactMs = m.AvgMainThreadImpactMs
          });
    }

    /// <summary>
    /// Processes an array of metrics asynchronously, logging performance data.
    /// </summary>
    /// <param name="metrics">The array of metrics to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task ProcessMetricsAsync(NativeArray<Metric> metrics)
    {
      await JobService.JobScheduler.ExecuteInBatchesAsync(metrics.Length, i =>
      {
        var metric = metrics[i];
        if (metric.ItemCount > 0)
        {
          float avgTimeMs = metric.ExecutionTimeMs / metric.ItemCount;
          DynamicProfiler.AddSample(metric.MethodName.ToString(), avgTimeMs);
#if DEBUG
          Log(Level.Verbose,
                    $"Performance: {metric.MethodName} processed {metric.ItemCount} items in {metric.ExecutionTimeMs:F2}ms (avg {avgTimeMs:F2}ms/item, mainThreadImpact={metric.MainThreadImpactMs:F2}ms)",
                    Category.Tasks);
#endif
        }
      }, nameof(ProcessMetricsAsync), 0.1f);
    }

    /// <summary>
    /// Updates the performance metrics for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="timeMs">The execution time in milliseconds.</param>
    /// <param name="mainThreadImpactMs">The main thread impact time in milliseconds.</param>
    /// <param name="itemCount">The number of items processed.</param>
    private static void UpdateMetric(string methodName, double timeMs, float mainThreadImpactMs, int itemCount)
    {
      _metrics.AddOrUpdate(methodName, _ => new Data
      {
        CallCount = 1,
        TotalTimeMs = timeMs,
        MaxTimeMs = timeMs,
        TotalMainThreadImpactMs = mainThreadImpactMs,
        MaxMainThreadImpactMs = mainThreadImpactMs,
        ItemCount = itemCount,
        AvgItemTimeMs = itemCount > 0 ? timeMs / itemCount : 0,
        AvgMainThreadImpactMs = itemCount > 0 ? mainThreadImpactMs / itemCount : 0
      },
      (_, m) => new Data
      {
        CallCount = m.CallCount + 1,
        TotalTimeMs = m.TotalTimeMs + timeMs,
        MaxTimeMs = Math.Max(m.MaxTimeMs, timeMs),
        TotalMainThreadImpactMs = m.TotalMainThreadImpactMs + mainThreadImpactMs,
        MaxMainThreadImpactMs = Math.Max(m.MaxMainThreadImpactMs, mainThreadImpactMs),
        CacheHits = m.CacheHits,
        CacheMisses = m.CacheMisses,
        ItemCount = m.ItemCount + itemCount,
        AvgItemTimeMs = m.ItemCount + itemCount > 0 ? (m.TotalTimeMs + timeMs) / (m.ItemCount + itemCount) : m.AvgItemTimeMs,
        AvgMainThreadImpactMs = m.ItemCount + itemCount > 0 ? (m.TotalMainThreadImpactMs + mainThreadImpactMs) / (m.ItemCount + itemCount) : m.AvgMainThreadImpactMs
      });
    }

    /// <summary>
    /// Updates and logs performance metrics for all tracked methods.
    /// </summary>
    private static void UpdateMetrics()
    {
      if (!_metrics.Any()) return;
      foreach (var kvp in _metrics)
      {
        var m = kvp.Value;
        if (m.CallCount == 0) continue;
        Log(Level.Info,
            $"Metric {kvp.Key}: Calls={m.CallCount}, AvgTime={(m.TotalTimeMs / m.CallCount):F2}ms, MaxTime={m.MaxTimeMs:F2}ms, " +
            $"AvgMainThreadImpact={(m.TotalMainThreadImpactMs / m.CallCount):F2}ms, MaxMainThreadImpact={m.MaxMainThreadImpactMs:F2}ms, " +
            $"CacheHitRate={(m.CacheHits / (double)(m.CacheHits + m.CacheMisses) * 100):F1}%, " +
            $"Items={m.ItemCount}, AvgItemTime={(m.AvgItemTimeMs):F3}ms, AvgItemMainThreadImpact={(m.AvgMainThreadImpactMs):F3}ms",
            Category.Performance);
      }
    }

    /// <summary>
    /// Gets the average main thread impact per item for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The average main thread impact per item in milliseconds, or 0 if not found.</returns>
    public static double GetAvgMainThreadImpactMs(string methodName)
    {
      return _metrics.TryGetValue(methodName, out var data) && data.ItemCount > 0 ? data.AvgMainThreadImpactMs : 0;
    }

    /// <summary>
    /// Cleans up the performance metrics system, clearing all data and unsubscribing from events.
    /// </summary>
    public static void Cleanup()
    {
      TimeManagerInstance.OnTick -= UpdateMetrics;
      _metrics.Clear();
      _isInitialized = false;
      MainThreadImpactTracker.Cleanup();
      Log(Level.Info, "PerformanceMetrics cleaned up", Category.Performance);
    }
  }

  public class LRUCache<TKey, TValue>(int capacity)
  {
    private readonly int _capacity = capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey key, TValue value)>> _cache = new Dictionary<TKey, LinkedListNode<(TKey key, TValue value)>>();
    private readonly LinkedList<(TKey key, TValue value)> _lruList = new LinkedList<(TKey key, TValue value)>();

    public bool Get(TKey key, out TValue value)
    {
      if (_cache.TryGetValue(key, out var node))
      {
        _lruList.Remove(node);
        _lruList.AddFirst(node);
        value = node.Value.value;
        return true;
      }
      value = default;
      return false;
    }

    public void Put(TKey key, TValue value)
    {
      if (_cache.TryGetValue(key, out var node))
      {
        _lruList.Remove(node);
        _cache.Remove(key);
      }
      if (_cache.Count >= _capacity)
      {
        var last = _lruList.Last;
        _cache.Remove(last.Value.key);
        _lruList.RemoveLast();
      }
      var newNode = new LinkedListNode<(TKey key, TValue value)>((key, value));
      _lruList.AddFirst(newNode);
      _cache.Add(key, newNode);
    }

    public void Clear()
    {
      _cache.Clear();
      _lruList.Clear();
    }
  }

  /// <summary>
  /// Manages dynamic profiling of method execution times using rolling averages.
  /// </summary>
  public static class DynamicProfiler
  {
    public static readonly ConcurrentDictionary<string, RollingAverage> RollingAverages = new();
    public static readonly LRUCache<string, RollingAverage> AverageCache = new(200);
    public static readonly ConcurrentDictionary<string, List<int>> BatchSizeHistory = new();
    private const int WINDOW_SIZE = 100;
    private const int BATCH_HISTORY_SIZE = 5;
    private const float MIN_AVG_TIME_MS = 0.05f;
    private const float MAX_AVG_TIME_MS = 0.5f;

    /// <summary>
    /// Represents a rolling average for tracking execution times.
    /// </summary>
    public class RollingAverage(int size)
    {
      public readonly double[] _samples = new double[size];
      public int _index = 0;
      public int Count = 0;
      public double _sum = 0;

      public void AddSample(double value)
      {
        lock (_samples)
        {
          if (Count >= _samples.Length)
          {
            _sum -= _samples[_index];
            Count--;
          }
          _samples[_index] = value;
          _sum += value;
          Count++;
          _index = (_index + 1) % _samples.Length;
        }
      }

      public double GetAverage()
      {
        lock (_samples)
        {
          return Count > 0 ? Math.Clamp(_sum / Count, MIN_AVG_TIME_MS, MAX_AVG_TIME_MS) : MIN_AVG_TIME_MS;
        }
      }
    }

    public static void AddSample(string methodName, double avgItemTimeMs)
    {
      if (avgItemTimeMs <= 0) return;
      var avg = RollingAverages.GetOrAdd(methodName, _ => new RollingAverage(WINDOW_SIZE));
      AverageCache.Put(methodName, avg);
      avg.AddSample(avgItemTimeMs);
    }

    public static void AddBatchSize(string uniqueId, int batchSize)
    {
      var history = BatchSizeHistory.GetOrAdd(uniqueId, _ => new List<int>(BATCH_HISTORY_SIZE));
      lock (history)
      {
        if (history.Count >= BATCH_HISTORY_SIZE)
        {
          history.RemoveAt(0);
        }
        history.Add(batchSize);
      }
    }

    public static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)
    {
      if (AverageCache.Get(methodName, out var avg) || RollingAverages.TryGetValue(methodName, out avg))
      {
        float result = (float)avg.GetAverage();
#if DEBUG
        Log(Level.Verbose, $"DynamicProfiler: {methodName} avgProcessingTimeMs={result:F3}ms (samples={avg.Count})", Category.Performance);
#endif
        return result;
      }
      return defaultTimeMs;
    }

    public static int GetAverageBatchSize(string uniqueId)
    {
      var history = BatchSizeHistory.GetOrAdd(uniqueId, _ => new List<int>(BATCH_HISTORY_SIZE));
      lock (history)
      {
        if (history.Count == 0) return 0;
        double sum = 0;
        foreach (var size in history)
        {
          sum += size;
        }
        return Mathf.RoundToInt((float)(sum / history.Count));
      }
    }

    /// <summary>
    /// Clears all rolling average data.
    /// </summary>
    public static void Cleanup()
    {
      RollingAverages.Clear();
      AverageCache.Clear();
      BatchSizeHistory.Clear();
      Log(Level.Info, "DynamicProfiler cleaned up", Category.Performance);
    }
  }

  /// <summary>
  /// Tracks the main thread impact of method executions.
  /// </summary>
  public static class MainThreadImpactTracker
  {
    /// <summary>
    /// Represents a sample of main thread impact.
    /// </summary>
    private struct ImpactSample
    {
      public string MethodName;
      public long StartTicks;
    }
    public static readonly ConcurrentDictionary<string, RollingAverage> ImpactAverages = new();
    public static readonly LRUCache<string, RollingAverage> ImpactCache = new(200);
    private static double _stopwatchOverheadMs = 0;
    private const int WINDOW_SIZE = 100;
    private const double OUTLIER_THRESHOLD_MS = 10.0;
    private const float VARIABILITY_THRESHOLD = 2.0f;
    private static readonly ConcurrentDictionary<string, RollingAverage> _mainThreadImpacts = new();
    private static readonly ConcurrentDictionary<int, ImpactSample> _samples = new();
    private static readonly Stopwatch _stopwatch = new();
    private static bool _isInitialized;
    private static int _sampleId;

    public static double StopwatchOverheadMs => _stopwatchOverheadMs;

    /// <summary>
    /// Initializes the main thread impact tracker.
    /// </summary>
    public static void Initialize()
    {
      var stopwatch = Stopwatch.StartNew();
      stopwatch.Stop();
      _stopwatchOverheadMs = stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;

      var testData = new NativeArray<float>(10, Allocator.TempJob);
      var testTask = new TestTask();
      var wrapper = new JobWrapper<GenericJob<float>>(new GenericJob<float> { Data = testData, Task = testTask, ItemCount = 10 });
      var sw = Stopwatch.StartNew();
      var handle = wrapper.Schedule();
      handle.Complete();
      sw.Stop();
      double testOverheadMs = sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency - _stopwatchOverheadMs;
      AddImpactSample("Test_IJob", testOverheadMs / 10);
      testData.Dispose();
    }

    [BurstCompile]
    private struct TestTask : IExecutableTask<float>
    {
      [BurstCompile]
      public void ExecuteItem(int index, ref NativeArray<float> data)
      {
        data[index] += 1f;
      }
    }

    public static void AddImpactSample(string methodName, double impactTimeMs)
    {
      if (impactTimeMs <= 0) return;
      impactTimeMs = Math.Max(0, impactTimeMs - _stopwatchOverheadMs);
      var avg = ImpactAverages.GetOrAdd(methodName, _ => new RollingAverage(WINDOW_SIZE));
      if (impactTimeMs > OUTLIER_THRESHOLD_MS)
      {
        Log(Level.Warning, $"Outlier detected for {methodName}: {impactTimeMs:F3}ms", Category.Performance);
      }
      else if (avg.Count > 0 && impactTimeMs > avg.GetAverage() * VARIABILITY_THRESHOLD)
      {
#if DEBUG
        Log(Level.Verbose, $"High variability for {methodName}: {impactTimeMs:F3}ms vs avg {avg.GetAverage():F3}ms", Category.Performance);
#endif
      }
      ImpactCache.Put(methodName, avg);
      avg.AddSample(impactTimeMs);
    }

    public static float GetAverageItemImpact(string methodName)
    {
      if (ImpactCache.Get(methodName, out var avg) || ImpactAverages.TryGetValue(methodName, out avg))
      {
        float result = (float)avg.GetAverage();
#if DEBUG
        Log(Level.Verbose, $"MainThreadImpactTracker: {methodName} avgImpactTimeMs={result:F3}ms (samples={avg.Count})", Category.Performance);
#endif
        return result;
      }
      return float.MaxValue;
    }

    public static float GetCoroutineCost(string coroutineKey)
    {
      float cost = GetAverageItemImpact(coroutineKey);
      return cost == float.MaxValue ? 0.2f : cost;
    }

    public static float GetJobOverhead(string jobKey)
    {
      float overhead = GetAverageItemImpact(jobKey);
      return overhead == float.MaxValue ? 2f : overhead;
    }

    public static void TrackWithStopwatch(string methodName, Action action, int itemCount = 1)
    {
      var stopwatch = Stopwatch.StartNew();
      action();
      stopwatch.Stop();
      double impactTimeMs = stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
      AddImpactSample(methodName, itemCount > 0 ? impactTimeMs / itemCount : impactTimeMs);
    }

    public static IEnumerator TrackJobWithStopwatch(string methodName, Func<JobHandle> scheduleJob, int itemCount = 1)
    {
      var stopwatch = Stopwatch.StartNew();
      var handle = scheduleJob();
      stopwatch.Stop();
      yield return new WaitUntil(() => handle.IsCompleted);
      stopwatch.Start();
      handle.Complete();
      stopwatch.Stop();
      double impactTimeMs = stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
      AddImpactSample(methodName, itemCount > 0 ? impactTimeMs / itemCount : impactTimeMs);
    }

    /// <summary>
    /// Begins a new main thread impact sample.
    /// </summary>
    /// <param name="methodName">The name of the method being tracked.</param>
    /// <returns>The sample ID.</returns>
    public static int BeginSample(string methodName)
    {
      int id = Interlocked.Increment(ref _sampleId);
      _samples[id] = new ImpactSample
      {
        MethodName = methodName,
        StartTicks = _stopwatch.ElapsedTicks
      };
      return id;
    }

    /// <summary>
    /// Ends a main thread impact sample and records the impact time.
    /// </summary>
    /// <param name="sampleId">The sample ID returned by BeginSample.</param>
    /// <returns>The main thread impact time in milliseconds.</returns>
    public static float EndSample(int sampleId)
    {
      if (!_samples.TryRemove(sampleId, out var sample)) return 0;
      float impactMs = (_stopwatch.ElapsedTicks - sample.StartTicks) * 1000f / Stopwatch.Frequency;
      var avg = _mainThreadImpacts.GetOrAdd(sample.MethodName, _ => new RollingAverage(100));
      avg.AddSample(impactMs);
      return impactMs;
    }

    /// <summary>
    /// Updates execution thresholds based on main thread and coroutine impacts.
    /// </summary>
    private static void UpdateThresholds()
    {
      if (TimeManagerInstance.Tick % 1000 != 0) return;
      foreach (var kvp in SmartExecution.MetricsThresholds)
      {
        string uniqueId = kvp.Key;
        if (_mainThreadImpacts.TryGetValue($"{uniqueId}_MainThread", out var mainThreadAvg) &&
            _mainThreadImpacts.TryGetValue($"{uniqueId}_Coroutine", out var coroutineAvg))
        {
          float mainThreadImpact = (float)mainThreadAvg.GetAverage();
          float coroutineImpact = (float)coroutineAvg.GetAverage();
          int threshold = Mathf.Max(100, Mathf.FloorToInt(1000f * SmartExecution.DEFAULT_THRESHOLD / Math.Max(1f, coroutineImpact - mainThreadImpact)));
          SmartExecution.MetricsThresholds[uniqueId] = threshold;
#if DEBUG
          Log(Level.Verbose,
              $"Updated threshold for {uniqueId}: {threshold} items (mainThreadImpact={mainThreadImpact:F3}ms, coroutineImpact={coroutineImpact:F3}ms)",
              Category.Performance);
#endif
        }
      }
    }

    /// <summary>
    /// Clears all main thread impact data and unsubscribes from events.
    /// </summary>
    public static void Cleanup()
    {
      TimeManagerInstance.OnTick -= UpdateThresholds;
      _mainThreadImpacts.Clear();
      _samples.Clear();
      _isInitialized = false;
      Log(Level.Info, "MainThreadImpactTracker cleaned up", Category.Performance);
      ImpactAverages.Clear();
      ImpactCache.Clear();
    }

    public class RollingAverage(int size)
    {
      public readonly double[] _samples = new double[size];
      public int _index = 0;
      public int Count = 0;
      public double _sum = 0;

      public void AddSample(double value)
      {
        lock (_samples)
        {
          if (Count >= _samples.Length)
          {
            _sum -= _samples[_index];
            Count--;
          }
          _samples[_index] = value;
          _sum += value;
          Count++;
          _index = (_index + 1) % _samples.Length;
        }
      }

      public double GetAverage()
      {
        lock (_samples)
        {
          return Count > 0 ? _sum / Count : float.MaxValue;
        }
      }
    }
  }

  /// <summary>
  /// Manages smart execution of tasks using jobs, coroutines, or main thread based on performance metrics.
  /// </summary>
  public static class SmartExecution
  {
    public static readonly ConcurrentDictionary<string, int> MetricsThresholds = new();
    public const int DEFAULT_THRESHOLD = 100;
    private static readonly ConcurrentDictionary<string, bool> _baselineEstablished = new();
    private static readonly ConcurrentDictionary<string, int> _executionCount = new();
    private static string _lastSavedDataHash;
    private static float _lastFps = 60f;
    private const float METRIC_STABILITY_THRESHOLD = 0.01f;
    private const int MIN_TEST_EXECUTIONS = 3;
    private const float NEW_GAME_THRESHOLD_SECONDS = 60;
    private const int REVALIDATION_INTERVAL = 50;
    private const float MAX_FRAME_TIME_MS = 0.5f;
    private const float HIGH_FPS_THRESHOLD = 0.01f;
    private const float FPS_CHANGE_THRESHOLD = 0.2f;
    private const int PARALLEL_THRESHOLD = 100;
    private const int LARGE_DATASET_WARNING = 1000;

    public static IEnumerator Execute<T>(
                string uniqueId,
                IExecutableTask<T> task,
                NativeArray<T> data,
                int itemCount,
                int? overrideBatchSize = null) where T : struct
    {
      if (task == null || itemCount == 0 || itemCount > data.Length)
      {
        yield break;
      }
      bool useParallel = ShouldUseParallel(uniqueId, itemCount);
      IJobWrapper wrapper = useParallel
          ? new JobParallelForWrapper<GenericParallelJob<T>>(new GenericParallelJob<T> { Data = data, Task = task }, itemCount, overrideBatchSize ?? 64)
          : new JobWrapper<GenericJob<T>>(new GenericJob<T> { Data = data, Task = task, ItemCount = itemCount });
      yield return ExecuteCore(uniqueId, task, data, wrapper, itemCount, overrideBatchSize, useParallel);
    }

    public static IEnumerator Execute<TJob>(
        string uniqueId,
        TJob job,
        int itemCount,
        JobType jobType,
        int? overrideBatchSize = null) where TJob : struct, IJob
    {
      yield return ExecuteCore<TJob>(uniqueId, null, default, new JobWrapper<TJob> { Job = job }, itemCount, overrideBatchSize, false);
    }

    private static bool ShouldUseParallel(string uniqueId, int itemCount)
    {
      if (itemCount > LARGE_DATASET_WARNING)
      {
        Log(Level.Warning, $"Large dataset for {uniqueId}: itemCount={itemCount}. Consider IJobParallelFor.", Category.Performance);
      }
      if (itemCount < PARALLEL_THRESHOLD)
        return false;
      float iJobCost = MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_IJob");
      float parallelCost = MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_IJobParallelFor");
      return parallelCost < iJobCost || parallelCost == 2f; // Default parallel if no IJob data
    }

    private static IEnumerator ExecuteCore<T>(
        string uniqueId,
        IExecutableTask<T> task,
        NativeArray<T> data,
        IJobWrapper jobWrapper,
        int itemCount,
        int? overrideBatchSize,
        bool isParallel) where T : struct
    {
      if (itemCount == 0 || (task == null && jobWrapper == null))
      {
        Log(Level.Warning, $"SmartExecution.Execute: Invalid input for {uniqueId}, itemCount={itemCount}", Category.Performance);
        yield break;
      }

      string jobKey = $"{uniqueId}_IJob"; // Always use IJob key for consistency
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";

      int batchSize = overrideBatchSize ?? FishNetExtensions.GetDynamicBatchSize(itemCount, 0.15f, uniqueId, isParallel);
      DynamicProfiler.AddBatchSize(uniqueId, batchSize);
#if DEBUG
      Log(Level.Verbose, $"SmartExecution.Execute: Calculated batch size {batchSize} for {uniqueId} (itemCount={itemCount}, isParallel={isParallel})", Category.Performance);
#endif

      float currentFps = 1f / Time.deltaTime;
      bool fpsChanged = Mathf.Abs(currentFps - _lastFps) / _lastFps > FPS_CHANGE_THRESHOLD;
      _lastFps = currentFps;

      if (!_baselineEstablished.ContainsKey(uniqueId) || fpsChanged || _executionCount.GetOrAdd(uniqueId, 0) >= REVALIDATION_INTERVAL)
      {
        _baselineEstablished.TryRemove(uniqueId, out _);
        yield return EstablishBaseline(uniqueId, task, data, jobWrapper, itemCount, batchSize, isParallel);
      }

      float mainThreadCost = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
      float coroutineCost = MainThreadImpactTracker.GetCoroutineCost(coroutineKey);
      float jobOverhead = MainThreadImpactTracker.GetJobOverhead(jobKey);
      bool isHighFps = Time.deltaTime < HIGH_FPS_THRESHOLD;

      for (int i = 0; i < itemCount; i += batchSize)
      {
        int currentBatchSize = Math.Min(batchSize, itemCount - i);
        float mainThreadBatchCost = mainThreadCost * currentBatchSize;
        bool isStale = !_baselineEstablished.ContainsKey(uniqueId);

        if (isStale && task != null)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (stale metrics, {currentBatchSize} items)", Category.Performance);
#endif
          yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(task, data, i, currentBatchSize), currentBatchSize);
        }
        else if (jobWrapper != null && (mainThreadBatchCost > jobOverhead || coroutineCost > jobOverhead))
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via Job ({currentBatchSize} items, mainThreadCost={mainThreadBatchCost:F3}ms > jobOverhead={jobOverhead:F3}ms, isParallel={isParallel})", Category.Performance);
#endif
          yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(currentBatchSize), currentBatchSize);
        }
        else if (task != null && (isHighFps || coroutineCost <= mainThreadBatchCost))
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via Coroutine ({currentBatchSize} items, isHighFps={isHighFps}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
          yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(task, data, i, currentBatchSize), currentBatchSize);
        }
        else if (task != null && mainThreadBatchCost <= MAX_FRAME_TIME_MS)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via MainThread ({currentBatchSize} items, mainThreadCost={mainThreadBatchCost:F3}ms)", Category.Performance);
#endif
          MainThreadImpactTracker.TrackWithStopwatch(mainThreadKey, () => RunMainThread(task, data, i, currentBatchSize), currentBatchSize);
        }
        else
        {
          Log(Level.Warning, $"No valid execution path for {uniqueId}, falling back to empty coroutine", Category.Performance);
          yield return null;
        }

        _executionCount.AddOrUpdate(uniqueId, 1, (_, count) => count + 1);
        yield return null;
      }
    }

    private static IEnumerator EstablishBaseline<T>(
        string uniqueId,
        IExecutableTask<T> task,
        NativeArray<T> data,
        IJobWrapper jobWrapper,
        int itemCount,
        int batchSize,
        bool isParallel) where T : struct
    {
      string jobKey = $"{uniqueId}_IJob";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";

      _executionCount.TryAdd(uniqueId, 0);
      int executionCount = _executionCount[uniqueId];

      if (executionCount == 0 && task != null)
      {
        MainThreadImpactTracker.TrackWithStopwatch(mainThreadKey, () => RunMainThread(task, data, 0, Math.Min(batchSize, itemCount)), batchSize);
        _executionCount[uniqueId]++;
        yield return null;
      }

      while (!_baselineEstablished.ContainsKey(uniqueId))
      {
        executionCount = _executionCount[uniqueId];
        bool isLowLoad = TimeManagerInstance.Tick % 10 == 0;

        if (isLowLoad)
        {
          if (executionCount % 3 == 1 && jobWrapper != null)
          {
            yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(Math.Min(batchSize, itemCount)), batchSize);
          }
          else if (executionCount % 3 == 2 && task != null)
          {
            yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(task, data, 0, Math.Min(batchSize, itemCount)), batchSize);
          }
          else if (task != null)
          {
            MainThreadImpactTracker.TrackWithStopwatch(mainThreadKey, () => RunMainThread(task, data, 0, Math.Min(batchSize, itemCount)), batchSize);
          }

          _executionCount[uniqueId]++;

          if (executionCount >= MIN_TEST_EXECUTIONS * 3)
          {
            double jobVariance = CalculateMetricVariance(jobKey);
            double coroutineVariance = CalculateMetricVariance(coroutineKey);
            double mainThreadVariance = CalculateMetricVariance(mainThreadKey);

            if (jobVariance < METRIC_STABILITY_THRESHOLD &&
                coroutineVariance < METRIC_STABILITY_THRESHOLD &&
                mainThreadVariance < METRIC_STABILITY_THRESHOLD)
            {
              _baselineEstablished.TryAdd(uniqueId, true);
              MetricsThresholds.TryAdd(uniqueId, DEFAULT_THRESHOLD);
              Log(Level.Info, $"Baseline established for {uniqueId}: jobThreshold={DEFAULT_THRESHOLD}", Category.Performance);
            }
          }
        }

        yield return null;
      }
    }

    private static double CalculateMetricVariance(string methodName)
    {
      if (!DynamicProfiler.RollingAverages.TryGetValue(methodName, out var avg))
        return double.MaxValue;

      lock (avg._samples)
      {
        if (avg.Count < MIN_TEST_EXECUTIONS) return double.MaxValue;
        double mean = avg._sum / avg.Count;
        double variance = 0;
        for (int i = 0; i < avg.Count; i++)
        {
          variance += Math.Pow(avg._samples[i] - mean, 2);
        }
        return variance / avg.Count;
      }
    }

    private static void RunMainThread<T>(IExecutableTask<T> task, NativeArray<T> data, int startIndex, int count) where T : struct
    {
      for (int i = startIndex; i < startIndex + count; i++)
      {
        if (task.ExecuteItemNonBurst != null)
          task.ExecuteItemNonBurst(i, ref data);
        else
          task.ExecuteItem(i, ref data);
      }
    }

    private static IEnumerator RunCoroutine<T>(IExecutableTask<T> task, NativeArray<T> data, int startIndex, int count) where T : struct
    {
      for (int i = startIndex; i < startIndex + count; i++)
      {
        if (task.ExecuteItemNonBurst != null)
          task.ExecuteItemNonBurst(i, ref data);
        else
          task.ExecuteItem(i, ref data);
        yield return null;
      }
    }

    public static void TrackCustomExecution(string methodName, Action action, int itemCount = 1)
    {
#if DEBUG
      Log(Level.Verbose, $"TrackCustomExecution: Tracking {methodName} with {itemCount} items", Category.Performance);
#endif
      MainThreadImpactTracker.TrackWithStopwatch(methodName, action, itemCount);
    }

    public static void SaveBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, $"NoLazyWorkers_Baselines_{saveId}.json");
      var data = new BaselineData
      {
        MetricsThresholds = MetricsThresholds.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        DynamicProfilerAverages = DynamicProfiler.RollingAverages
              .ToDictionary(kvp => kvp.Key, kvp => new RollingAverageData
              {
                Samples = kvp.Value._samples.Take(kvp.Value.Count).ToArray(),
                Count = kvp.Value.Count,
                Sum = kvp.Value._sum
              }),
        BaselineEstablished = _baselineEstablished.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        MainThreadImpactAverages = MainThreadImpactTracker.ImpactAverages
              .ToDictionary(kvp => kvp.Key, kvp => new RollingAverageData
              {
                Samples = kvp.Value._samples.Take(kvp.Value.Count).ToArray(),
                Count = kvp.Value.Count,
                Sum = kvp.Value._sum
              }),
        BatchSizeHistory = DynamicProfiler.BatchSizeHistory
              .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray())
      };
      string json = JsonConvert.SerializeObject(data);
      string dataHash = json.GetHashCode().ToString("X8");
      if (dataHash == _lastSavedDataHash)
      {
#if DEBUG
        Log(Level.Verbose, $"No changes in baseline data for {saveId}, skipping save", Category.Performance);
#endif
        return;
      }
      _lastSavedDataHash = dataHash;
      Directory.CreateDirectory(Path.GetDirectoryName(filePath));
      using (var stream = new FileStream(filePath, FileMode.Create))
      using (var gzip = new GZipStream(stream, System.IO.Compression.CompressionLevel.Optimal))
      using (var writer = new StreamWriter(gzip))
      {
        writer.Write(json);
      }
      Log(Level.Info, $"Saved baseline data to {filePath}", Category.Performance);
    }

    public static void LoadBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, $"NoLazyWorkers_Baselines_{saveId}.json");
      if (!File.Exists(filePath)) return;

      try
      {
        string json;
        using (var stream = new FileStream(filePath, FileMode.Open))
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
          json = reader.ReadToEnd();
        }
        var data = JsonConvert.DeserializeObject<BaselineData>(json);
        _lastSavedDataHash = json.GetHashCode().ToString("X8");
        foreach (var kvp in data.MetricsThresholds)
        {
          MetricsThresholds.TryAdd(kvp.Key, kvp.Value);
        }
        foreach (var kvp in data.DynamicProfilerAverages)
        {
          var avg = new DynamicProfiler.RollingAverage(100);
          for (int i = 0; i < kvp.Value.Count; i++)
          {
            avg.AddSample(kvp.Value.Samples[i]);
          }
          DynamicProfiler.RollingAverages.TryAdd(kvp.Key, avg);
          DynamicProfiler.AverageCache.Put(kvp.Key, avg);
        }
        foreach (var kvp in data.BaselineEstablished)
        {
          _baselineEstablished.TryAdd(kvp.Key, kvp.Value);
        }
        foreach (var kvp in data.MainThreadImpactAverages)
        {
          string key = kvp.Key;
          if (key.EndsWith("_IJobParallelFor"))
          {
            key = key.Replace("_IJobParallelFor", "_IJob"); // Migrate old keys
          }
          var avg = new MainThreadImpactTracker.RollingAverage(100);
          for (int i = 0; i < kvp.Value.Count; i++)
          {
            avg.AddSample(kvp.Value.Samples[i]);
          }
          MainThreadImpactTracker.ImpactAverages.TryAdd(key, avg);
          MainThreadImpactTracker.ImpactCache.Put(key, avg);
        }
        foreach (var kvp in data.BatchSizeHistory)
        {
          var history = new List<int>(kvp.Value);
          DynamicProfiler.BatchSizeHistory.TryAdd(kvp.Key, history);
        }
        foreach (var uniqueId in MetricsThresholds.Keys)
        {
          ValidateBaseline(uniqueId);
        }
        Log(Level.Info, $"Loaded baseline data from {filePath}", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to load baseline data from {filePath}: {ex.Message}", Category.Performance);
      }
    }

    [Serializable]
    private class BaselineData
    {
      public Dictionary<string, int> MetricsThresholds;
      public Dictionary<string, RollingAverageData> DynamicProfilerAverages;
      public Dictionary<string, bool> BaselineEstablished;
      public Dictionary<string, RollingAverageData> MainThreadImpactAverages;
      public Dictionary<string, int[]> BatchSizeHistory;
    }

    [Serializable]
    private class RollingAverageData
    {
      public double[] Samples;
      public int Count;
      public double Sum;
    }

    public static void Initialize()
    {
      MainThreadImpactTracker.Initialize();
      CheckNewGameReset();
      LoadBaselineData();
    }

    private static void CheckNewGameReset()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, $"NoLazyWorkers_Baselines_{saveId}.json");
      if (File.Exists(filePath))
      {
        var creationTime = File.GetCreationTime(filePath);
        if ((DateTime.Now - creationTime).TotalSeconds <= NEW_GAME_THRESHOLD_SECONDS)
        {
          MetricsThresholds.Clear();
          DynamicProfiler.Cleanup();
          MainThreadImpactTracker.Cleanup();
          _baselineEstablished.Clear();
          _executionCount.Clear();
          File.Delete(filePath);
          _lastSavedDataHash = null;
          Log(Level.Info, $"Reset baseline data for new game at {filePath}", Category.Performance);
        }
      }
    }

    private static void ValidateBaseline(string uniqueId)
    {
      string mainThreadKey = $"{uniqueId}_MainThread";
      double savedAvg = Metrics.GetAvgItemTimeMs(mainThreadKey);
      double testAvg = 0;
      int testCount = 1;

      for (int i = 0; i < testCount; i++)
      {
        MainThreadImpactTracker.TrackWithStopwatch(mainThreadKey, () => { }, 1);
        testAvg += Metrics.GetAvgItemTimeMs(mainThreadKey);
      }
      testAvg /= testCount;

      if (Math.Abs(savedAvg - testAvg) / savedAvg > 0.2)
      {
        MetricsThresholds.TryRemove(uniqueId, out _);
        _baselineEstablished.TryRemove(uniqueId, out _);
        _executionCount.TryRemove(uniqueId, out _);
        DynamicProfiler.RollingAverages.TryRemove($"{uniqueId}_IJob", out _);
        DynamicProfiler.RollingAverages.TryRemove($"{uniqueId}_Coroutine", out _);
        DynamicProfiler.RollingAverages.TryRemove(mainThreadKey, out _);
        MainThreadImpactTracker.ImpactAverages.TryRemove($"{uniqueId}_IJob", out _);
        MainThreadImpactTracker.ImpactAverages.TryRemove($"{uniqueId}_Coroutine", out _);
        MainThreadImpactTracker.ImpactAverages.TryRemove(mainThreadKey, out _);
        DynamicProfiler.BatchSizeHistory.TryRemove(uniqueId, out _);
        Log(Level.Warning, $"Reset baseline for {uniqueId} due to significant deviation (saved={savedAvg:F3}ms, test={testAvg:F3}ms)", Category.Performance);
      }
    }

    /// <summary>
    /// Executes a task using the most efficient method (job, coroutine, or main thread) based on performance metrics.
    /// </summary>
    /// <param name="uniqueId">The unique identifier for the task.</param>
    /// <param name="itemCount">The number of items to process.</param>
    /// <param name="jobCallback">The callback for job execution (optional).</param>
    /// <param name="coroutineCallback">The callback for coroutine execution (optional).</param>
    /// <param name="mainThreadCallback">The callback for main thread execution (optional).</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    public static IEnumerator Execute(
        string uniqueId,
        int itemCount,
        Action jobCallback = null,
        Func<IEnumerator> coroutineCallback = null,
        Action mainThreadCallback = null)
    {
      if (itemCount == 0 || (jobCallback == null && coroutineCallback == null && mainThreadCallback == null))
      {
        Log(Level.Warning, $"SmartExecution.Execute: Invalid input for {uniqueId}, itemCount={itemCount}", Category.Performance);
        yield break;
      }

      string jobKey = $"{uniqueId}_Job";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";

      if (!MetricsThresholds.TryGetValue(uniqueId, out int jobThreshold))
      {
        jobThreshold = DEFAULT_THRESHOLD;
        MetricsThresholds.TryAdd(uniqueId, jobThreshold);
      }

      float mainThreadImpact = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
      float coroutineImpact = MainThreadImpactTracker.GetAverageItemImpact(coroutineKey);

      if (itemCount >= jobThreshold && jobCallback != null && MainThreadImpactTracker.GetAverageItemImpact(jobKey) <= Math.Min(mainThreadImpact, coroutineImpact))
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via Job ({itemCount} items, threshold={jobThreshold})", Category.Performance);
#endif
        yield return Metrics.TrackExecutionCoroutine(jobKey, RunJob(jobCallback));
      }
      else if (coroutineCallback != null && (mainThreadCallback == null || coroutineImpact <= mainThreadImpact))
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via Coroutine ({itemCount} items)", Category.Performance);
#endif
        yield return Metrics.TrackExecutionCoroutine(coroutineKey, coroutineCallback());
      }
      else if (mainThreadCallback != null)
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via MainThread ({itemCount} items)", Category.Performance);
#endif
        Metrics.TrackExecution(mainThreadKey, mainThreadCallback, itemCount);
      }
      else
      {
        Log(Level.Warning, $"No valid callback provided for {uniqueId}, falling back to empty coroutine", Category.Performance);
      }
    }

    /// <summary>
    /// Runs a job callback as a coroutine.
    /// </summary>
    /// <param name="jobCallback">The job callback to execute.</param>
    /// <returns>An enumerator for the job execution.</returns>
    private static IEnumerator RunJob(Action jobCallback)
    {
      jobCallback();
      yield return null;
    }

    public static class JobConversionHelpers
    {
      private static int _parallelThreshold = PARALLEL_THRESHOLD;

      public static void SetParallelThreshold(int threshold)
      {
        _parallelThreshold = Mathf.Max(10, threshold);
        Log(Level.Info, $"Parallel threshold set to {_parallelThreshold}", Category.Performance);
      }
    }
  }

  /// <summary>
  /// Provides extensions for FishNet integration, including batch size calculation and tick waiting.
  /// </summary>
  public static class FishNetExtensions
  {
    public static bool IsServer;
    public static TimeManager TimeManagerInstance;

    public static int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId, bool isParallel)
    {
      if (!isParallel) return totalItems; // No batching for IJob
      float avgProcessingTimeMs = DynamicProfiler.GetDynamicAvgProcessingTimeMs(uniqueId, defaultAvgProcessingTimeMs);
      int avgBatchSize = DynamicProfiler.GetAverageBatchSize(uniqueId);
      float targetFrameTimeMs = Mathf.Min(16.666f, Time.deltaTime * 1000f);
      int calculatedBatchSize = Mathf.Max(1, Mathf.RoundToInt(targetFrameTimeMs / avgProcessingTimeMs));
      if (avgBatchSize > 0)
      {
        calculatedBatchSize = Mathf.RoundToInt((calculatedBatchSize + avgBatchSize) / 2f);
      }
      return Mathf.Min(totalItems, calculatedBatchSize);
    }

    private static readonly ConcurrentDictionary<TimeManager, ConcurrentDictionary<double, TaskCompletionSource<bool>>> _tickAwaiters = new();

    /// <summary>
    /// Asynchronously waits for the next FishNet TimeManager tick.
    /// </summary>
    /// <returns>A Task that completes on the next tick.</returns>
    private static Task AwaitNextTickAsyncInternal()
    {
      var tick = TimeManagerInstance.Tick;
      var awaiters = _tickAwaiters.GetOrAdd(TimeManagerInstance, _ => new ConcurrentDictionary<double, TaskCompletionSource<bool>>());
      if (awaiters.TryGetValue(tick + 1, out var tcs))
        return tcs.Task;

      tcs = new TaskCompletionSource<bool>();
      if (!awaiters.TryAdd(tick + 1, tcs))
        return awaiters[tick + 1].Task;

      void OnTick()
      {
        if (TimeManagerInstance.Tick <= tick) return;
        TimeManagerInstance.OnTick -= OnTick;
        awaiters.TryRemove(tick + 1, out _);
        tcs.TrySetResult(true);
      }
      TimeManagerInstance.OnTick += OnTick;
      return tcs.Task;
    }

    /// <summary>
    /// Asynchronously waits for the specified duration in seconds, aligned with FishNet TimeManager ticks.
    /// </summary>
    /// <param name="seconds">The duration to wait in seconds (default is 0).</param>
    /// <returns>A Task that completes after the specified delay.</returns>
    public static async Task AwaitNextFishNetTickAsync(float seconds = 0)
    {
      if (seconds < 0f)
      {
        Log(Level.Warning, $"AwaitNextTickAsync: Invalid delay {seconds}s, using no delay", Category.Tasks);
        return;
      }

      if (seconds > 0f)
      {
        int ticksToWait = Mathf.CeilToInt(seconds * TimeManagerInstance.TickRate);
        Log(Level.Verbose, $"AwaitNextTickAsync: Waiting for {seconds}s ({ticksToWait} ticks) at tick rate {TimeManagerInstance.TickRate}", Category.Tasks);

        for (int i = 0; i < ticksToWait; i++)
        {
          await AwaitNextTickAsyncInternal();
        }
      }
      else
        await AwaitNextTickAsyncInternal();
    }

    /// <summary>
    /// Waits for the next FishNet tick.
    /// </summary>
    /// <returns>An enumerator that yields until the next tick.</returns>
    public static IEnumerator WaitForNextTick()
    {
      long currentTick = TimeManagerInstance.Tick;
      while (TimeManagerInstance.Tick == currentTick)
        yield return null;
    }
  }

  /// <summary>
  /// Provides extensions for converting Tasks to Unity coroutines.
  /// </summary>
  public static class TaskExtensions
  {
    /// <summary>
    /// Converts a Task to a coroutine.
    /// </summary>
    /// <param name="task">The task to convert.</param>
    /// <returns>A TaskYieldInstruction for the coroutine.</returns>
    public static TaskYieldInstruction AsCoroutine(this Task task) => new TaskYieldInstruction(task);

    /// <summary>
    /// Converts a Task with a result to a coroutine.
    /// </summary>
    /// <typeparam name="T">The result type of the task.</typeparam>
    /// <param name="task">The task to convert.</param>
    /// <returns>A TaskYieldInstruction for the coroutine.</returns>
    public static TaskYieldInstruction<T> AsCoroutine<T>(this Task<T> task) => new TaskYieldInstruction<T>(task);

    /// <summary>
    /// A custom yield instruction for awaiting a Task in a coroutine.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the TaskYieldInstruction class.
    /// </remarks>
    /// <param name="task">The task to await.</param>
    /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
    public class TaskYieldInstruction(Task task) : CustomYieldInstruction
    {
      private readonly Task _task = task ?? throw new ArgumentNullException(nameof(task));

      /// <summary>
      /// Gets whether the coroutine should continue waiting.
      /// </summary>
      public override bool keepWaiting => !_task.IsCompleted;
    }

    /// <summary>
    /// A custom yield instruction for awaiting a Task with a result in a coroutine.
    /// </summary>
    /// <typeparam name="T">The result type of the task.</typeparam>
    /// <remarks>
    /// Initializes a new instance of the TaskYieldInstruction class.
    /// </remarks>
    /// <param name="task">The task to await.</param>
    /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
    public class TaskYieldInstruction<T>(Task<T> task) : CustomYieldInstruction
    {
      private readonly Task<T> _task = task ?? throw new ArgumentNullException(nameof(task));

      /// <summary>
      /// Gets whether the coroutine should continue waiting.
      /// </summary>
      public override bool keepWaiting => !_task.IsCompleted;

      /// <summary>
      /// Gets the result of the task if completed, otherwise default(T).
      /// </summary>
      public T Result => _task.IsCompleted ? _task.Result : default;
    }
  }

  /// <summary>
  /// Manages coroutine execution in Unity with singleton behavior.
  /// </summary>
  public class CoroutineRunner : MonoBehaviour
  {
    private static CoroutineRunner _instance;

    /// <summary>
    /// Gets the singleton instance of the CoroutineRunner.
    /// </summary>
    public static CoroutineRunner Instance
    {
      get
      {
        if (_instance == null)
        {
          var go = new GameObject("CoroutineRunner");
          _instance = go.AddComponent<CoroutineRunner>();
          DontDestroyOnLoad(go);
        }
        return _instance;
      }
    }

    /// <summary>
    /// Runs a coroutine.
    /// </summary>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <returns>The started coroutine.</returns>
    public Coroutine RunCoroutine(IEnumerator coroutine)
    {
      return StartCoroutine(RunCoroutineInternal(coroutine));
    }

    /// <summary>
    /// Runs a coroutine internally, handling exceptions.
    /// </summary>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private IEnumerator RunCoroutineInternal(IEnumerator coroutine)
    {
      while (true)
      {
        object current;
        try
        {
          if (!coroutine.MoveNext())
            yield break;
          current = coroutine.Current;
        }
        catch (Exception e)
        {
          Log(Level.Stacktrace, $"CoroutineRunner: Exception in coroutine: {e.Message}", Category.Core);
          yield break;
        }
        yield return current;
      }
    }

    /// <summary>
    /// Runs a coroutine with a result callback.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <param name="callback">The callback to invoke with the result.</param>
    /// <returns>The started coroutine.</returns>
    public Coroutine RunCoroutineWithResult<T>(IEnumerator coroutine, Action<T> callback)
    {
      return StartCoroutine(RunCoroutineInternal(coroutine, callback));
    }

    /// <summary>
    /// Runs a coroutine internally with a result callback, handling exceptions.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <param name="callback">The callback to invoke with the result.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private IEnumerator RunCoroutineInternal<T>(IEnumerator coroutine, Action<T> callback)
    {
      while (true)
      {
        object current;
        try
        {
          if (!coroutine.MoveNext())
            yield break;
          current = coroutine.Current;
        }
        catch (Exception e)
        {
          Log(Level.Stacktrace, $"CoroutineRunner: Exception in coroutine: {e.Message}", Category.Core);
          callback?.Invoke(default);
          yield break;
        }
        if (current is T result)
        {
          callback?.Invoke(result);
          yield break;
        }
        yield return current;
      }
    }
  }
}