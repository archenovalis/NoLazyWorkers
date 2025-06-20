using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using static NoLazyWorkers.TimeManagerExtensions;
using static NoLazyWorkers.Debug;
using Unity.Jobs;

namespace NoLazyWorkers.Metrics
{
  public static class Performance
  {
    [BurstCompile]
    public struct SingleEntityMetricsJob : IJob
    {
      public FixedString64Bytes TaskType;
      public float Timestamp;
      public NativeArray<PerformanceMetric> Metrics;

      public void Execute()
      {
        Performance.TrackExecutionBurst(TaskType, Timestamp, 1, Metrics, 0);
        var task = Performance.ProcessMetricsAsync(Metrics);
        task.GetAwaiter().GetResult(); // Synchronous for fire-and-forget
      }
    }

    /// <summary>
    /// Burst-compatible performance metric structure.
    /// </summary>
    [BurstCompile]
    public struct PerformanceMetric
    {
      public FixedString64Bytes MethodName;
      public float ExecutionTimeMs;
      public int ItemCount;
    }

    public struct PerformanceData
    {
      public long CallCount; // Total calls
      public double TotalTimeMs; // Total execution time
      public double MaxTimeMs; // Max execution time
      public long CacheHits; // Cache hits
      public long CacheMisses; // Cache misses
      public long ItemCount; // Total items processed (e.g., shelves, slots)
      public double AvgItemTimeMs; // Average time per item (updated periodically)
    }
    private static readonly ConcurrentDictionary<string, float> _executionTimes = new();
    private static readonly ConcurrentDictionary<string, PerformanceData> _metrics = new();
    private static readonly Stopwatch _stopwatch = new();
    private static bool _isInitialized;

    public static void Initialize()
    {
      if (_isInitialized) return;
      _isInitialized = true;
      TimeManagerInstance.OnTick += UpdateMetrics;
      Log(Level.Info, "PerformanceMetrics initialized", Category.Performance);
    }

    public static void LogExecutionTime(string taskId, long milliseconds)
    {
      _executionTimes[taskId] = milliseconds;
    }

    public static float GetAverageTaskExecutionTime()
    {
      return _executionTimes.Values.DefaultIfEmpty(100f).Average();
    }

    public static void TrackExecution(string methodName, Action action, int itemCount = 1)
    {
      _stopwatch.Restart();
      action();
      _stopwatch.Stop();
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, itemCount);
    }

    public static T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 1)
    {
      _stopwatch.Restart();
      T result = action();
      _stopwatch.Stop();
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, itemCount);
      return result;
    }

    public static async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)
    {
      _stopwatch.Restart();
      await action();
      _stopwatch.Stop();
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, itemCount);
      return;
    }

    public static async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)
    {
      _stopwatch.Restart();
      var result = await action();
      _stopwatch.Stop();
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, itemCount);
      return result;
    }

    public static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)
    {
      _stopwatch.Restart();
      while (true)
      {
        if (!coroutine.MoveNext()) break;
        yield return coroutine.Current;
      }
      _stopwatch.Stop();
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, itemCount);
    }

    /// <summary>
    /// Collects a performance metric within a Burst job.
    /// </summary>
    [BurstCompile]
    public static void TrackExecutionBurst(
        FixedString64Bytes methodName,
        float executionTimeMs,
        int itemCount,
        NativeArray<PerformanceMetric> metrics,
        int index)
    {
      metrics[index] = new PerformanceMetric
      {
        MethodName = methodName,
        ExecutionTimeMs = executionTimeMs,
        ItemCount = itemCount
      };
    }

    public static void TrackCacheAccess(string cacheName, bool isHit)
    {
      _metrics.AddOrUpdate(cacheName, _ => new PerformanceData { CacheHits = isHit ? 1 : 0, CacheMisses = isHit ? 0 : 1 },
          (_, m) => new PerformanceData
          {
            CallCount = m.CallCount,
            TotalTimeMs = m.TotalTimeMs,
            MaxTimeMs = m.MaxTimeMs,
            CacheHits = m.CacheHits + (isHit ? 1 : 0),
            CacheMisses = m.CacheMisses + (isHit ? 0 : 1),
            ItemCount = m.ItemCount,
            AvgItemTimeMs = m.AvgItemTimeMs
          });
    }

    /// <summary>
    /// Processes performance metrics asynchronously, updating DynamicProfiler.
    /// </summary>
    public static async Task ProcessMetricsAsync(NativeArray<PerformanceMetric> metrics)
    {
      await JobService.JobScheduler.ExecuteInBatchesAsync(metrics.Length, i =>
      {
        var metric = metrics[i];
        if (metric.ItemCount > 0)
        {
          float avgTimeMs = metric.ExecutionTimeMs / metric.ItemCount;
          DynamicProfiler.AddSample(metric.MethodName.ToString(), avgTimeMs);
          Log(Level.Verbose,
                    $"Performance: {metric.MethodName} processed {metric.ItemCount} items in {metric.ExecutionTimeMs:F2}ms (avg {avgTimeMs:F2}ms/item)",
                    Category.Tasks);
        }
      }, nameof(ProcessMetricsAsync), 0.1f);
    }

    private static void UpdateMetric(string methodName, double timeMs, int itemCount)
    {
      _metrics.AddOrUpdate(methodName, _ => new PerformanceData
      {
        CallCount = 1,
        TotalTimeMs = timeMs,
        MaxTimeMs = timeMs,
        ItemCount = itemCount,
        AvgItemTimeMs = itemCount > 0 ? timeMs / itemCount : 0
      },
          (_, m) => new PerformanceData
          {
            CallCount = m.CallCount + 1,
            TotalTimeMs = m.TotalTimeMs + timeMs,
            MaxTimeMs = Math.Max(m.MaxTimeMs, timeMs),
            CacheHits = m.CacheHits,
            CacheMisses = m.CacheMisses,
            ItemCount = m.ItemCount + itemCount,
            AvgItemTimeMs = m.ItemCount + itemCount > 0 ? (m.TotalTimeMs + timeMs) / (m.ItemCount + itemCount) : m.AvgItemTimeMs
          });
    }

    private static void UpdateMetrics()
    {
      if (!_metrics.Any()) return;
      foreach (var kvp in _metrics)
      {
        var m = kvp.Value;
        if (m.CallCount == 0) continue;
        Log(Level.Info,
            $"Metric {kvp.Key}: Calls={m.CallCount}, AvgTime={(m.TotalTimeMs / m.CallCount):F2}ms, MaxTime={m.MaxTimeMs:F2}ms, " +
            $"CacheHitRate={(m.CacheHits / (double)(m.CacheHits + m.CacheMisses) * 100):F1}%, " +
            $"Items={m.ItemCount}, AvgItemTime={(m.AvgItemTimeMs):F3}ms",
            Category.Performance);
      }
    }

    public static double GetAvgItemTimeMs(string methodName)
    {
      return _metrics.TryGetValue(methodName, out var data) && data.ItemCount > 0 ? data.AvgItemTimeMs : 0;
    }

    public static void Cleanup()
    {
      TimeManagerInstance.OnTick -= UpdateMetrics;
      _metrics.Clear();
      _isInitialized = false;
      Log(Level.Info, "PerformanceMetrics cleaned up", Category.Performance);
    }
  }

  public static class DynamicProfiler
  {
    private static readonly ConcurrentDictionary<string, RollingAverage> _rollingAverages = new();
    private const int WINDOW_SIZE = 100; // Store last 100 samples
    private const float MIN_AVG_TIME_MS = 0.05f; // Minimum avgProcessingTimeMs
    private const float MAX_AVG_TIME_MS = 0.5f; // Maximum avgProcessingTimeMs

    private class RollingAverage
    {
      private readonly double[] _samples;
      private int _index;
      public int Count;
      private double _sum;

      public RollingAverage(int size)
      {
        _samples = new double[size];
        _index = 0;
        Count = 0;
        _sum = 0;
      }

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
          return Count > 0 ? Math.Clamp((float)(_sum / Count), MIN_AVG_TIME_MS, MAX_AVG_TIME_MS) : MIN_AVG_TIME_MS;
        }
      }
    }

    public static void AddSample(string methodName, double avgItemTimeMs)
    {
      if (avgItemTimeMs <= 0) return;
      var avg = _rollingAverages.GetOrAdd(methodName, _ => new RollingAverage(WINDOW_SIZE));
      avg.AddSample(avgItemTimeMs);
    }

    public static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)
    {
      if (_rollingAverages.TryGetValue(methodName, out var avg))
      {
        float result = (float)avg.GetAverage();
        Log(Level.Verbose,
            $"DynamicProfiler: {methodName} avgProcessingTimeMs={result:F3}ms (samples={avg.Count})",
            Category.Performance);
        return result;
      }
      return defaultTimeMs;
    }

    public static void Cleanup()
    {
      _rollingAverages.Clear();
      Log(Level.Info, "DynamicProfiler cleaned up", Category.Performance);
    }
  }
}