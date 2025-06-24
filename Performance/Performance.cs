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
using UnityEngine.Pool;
using HarmonyLib;
using ScheduleOne.UI.MainMenu;
using UnityEngine.Jobs;

namespace NoLazyWorkers.Performance
{
  /// <summary>
  /// Defines types of jobs supported by the job wrapper system.
  /// </summary>
  internal enum JobType
  {
    IJob,
    IJobParallelFor,
    IJobFor,
    IJobParallelForTransform
  }

  /// <summary>
  /// Interface for wrapping Unity Job System jobs.
  /// </summary>
  internal interface IJobWrapper
  {
    /// <summary>
    /// Schedules the job with optional count and batch size.
    /// </summary>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Batch size for parallel processing.</param>
    /// <returns>JobHandle for the scheduled job.</returns>
    JobHandle Schedule(int count = 0, int batchSize = 64);

    /// <summary>
    /// Completes the job and cleans up resources.
    /// </summary>
    void Complete();

    /// <summary>
    /// Gets whether the job is parallel.
    /// </summary>
    bool IsParallel { get; }

    /// <summary>
    /// Gets the type of job.
    /// </summary>
    JobType JobType { get; }
  }

  /// <summary>
  /// Wraps a non-parallel IJob.
  /// </summary>
  /// <typeparam name="T">Job type implementing IJob.</typeparam>
  internal struct JobWrapper<T> : IJobWrapper where T : struct, IJob
  {
    public T Job;
    public bool IsParallel => false;
    public JobType JobType => JobType.IJob;

    public JobWrapper(T job) => Job = job;

    public JobHandle Schedule(int count = 0, int batchSize = 64) => Job.Schedule();

    public void Complete() { }
  }

  /// <summary>
  /// Wraps a parallel IJobParallelFor.
  /// </summary>
  /// <typeparam name="T">Job type implementing IJobParallelFor.</typeparam>
  internal struct JobParallelForWrapper<T> : IJobWrapper where T : struct, IJobParallelFor
  {
    public T Job;
    private readonly int _count;
    private readonly int _batchSize;
    public bool IsParallel => true;
    public JobType JobType => JobType.IJobParallelFor;

    public JobParallelForWrapper(T job, int count, int batchSize)
    {
      Job = job;
      _count = count;
      _batchSize = batchSize;
    }

    public JobHandle Schedule(int count = 0, int batchSize = 64) => Job.Schedule(_count, _batchSize);

    public void Complete() { }
  }

  /// <summary>
  /// Executes a delegate-based IJob with Burst compilation.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
  [BurstCompile]
  internal struct DelegateJob<TInput, TOutput> : IJob
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    public FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>>> Delegate;
    public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public int StartIndex;
    public int Count;

    public void Execute()
    {
      var stopwatch = Stopwatch.StartNew();
      Delegate.Invoke(StartIndex, StartIndex + Count, Inputs, Outputs);
#if DEBUG
      if (stopwatch.ElapsedMilliseconds > SmartExecution.MAX_FRAME_TIME_MS)
      {
        Log(Level.Verbose, $"DelegateJob exceeded frame time ({stopwatch.ElapsedMilliseconds:F3}ms)", Category.Performance);
      }
#endif
    }
  }

  /// <summary>
  /// Executes a delegate-based IJobParallelFor with Burst compilation.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
  [BurstCompile]
  internal struct DelegateParallelJob<TInput, TOutput> : IJobParallelFor
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    public FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>>> Delegate;
    public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public int StartIndex;
    public int BatchSize;

    public void Execute(int index)
    {
      int batchStart = StartIndex + index * BatchSize;
      int batchEnd = Math.Min(batchStart + BatchSize, Inputs.Length);
      Delegate.Invoke(batchStart, batchEnd, Inputs, Outputs);
    }
  }

  /// <summary>
  /// Executes a delegate-based IJobParallelFor with single-item processing.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
  [BurstCompile]
  internal struct DelegateForJob<TInput, TOutput> : IJobParallelFor
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    [ReadOnly] public FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>>> Delegate;
    [ReadOnly] public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public int StartIndex;

    public void Execute(int index)
    {
      Delegate.Invoke(index + StartIndex, Inputs, Outputs);
    }
  }

  /// <summary>
  /// Executes a delegate-based IJobParallelForTransform with Burst compilation.
  /// </summary>
  [BurstCompile]
  internal struct DelegateParallelForTransformJob : IJobParallelForTransform
  {
    [ReadOnly] public FunctionPointer<Action<int, TransformAccess>> Delegate;
    public int StartIndex;
    public int BatchSize;

    public void Execute(int index, TransformAccess transform)
    {
      Delegate.Invoke(index + StartIndex, transform);
    }
  }

  /// <summary>
  /// Wraps delegate-based jobs for various job types.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
  [BurstCompile]
  internal struct DelegateJobWrapper<TInput, TOutput> : IJobWrapper
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    private readonly FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>>> _delegateIJob;
    private readonly FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>>> _delegateFor;
    private readonly FunctionPointer<Action<int, TransformAccess>> _delegateTransform;
    private readonly NativeArray<TInput> _inputs;
    private readonly NativeList<TOutput> _outputs;
    private readonly TransformAccessArray _transforms;
    private readonly int _startIndex;
    private readonly int _count;
    private readonly int _batchSize;
    private readonly JobType _jobType;

    public DelegateJobWrapper(
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
    {
      _delegateIJob = burstDelegateIJob != null ? BurstCompiler.CompileFunctionPointer(burstDelegateIJob) : default;
      _delegateFor = burstDelegateFor != null ? BurstCompiler.CompileFunctionPointer(burstDelegateFor) : default;
      _delegateTransform = transformDelegate != null ? BurstCompiler.CompileFunctionPointer(transformDelegate) : default;
      _inputs = inputs;
      _outputs = outputs;
      _transforms = transforms;
      _startIndex = startIndex;
      _count = count;
      _batchSize = batchSize;
      _jobType = jobType;

#if DEBUG
      Log(Level.Verbose, $"Created DelegateJobWrapper for jobType={jobType}, count={count}, batchSize={batchSize}", Category.Performance);
#endif
    }

    public bool IsParallel => _jobType == JobType.IJobParallelFor || _jobType == JobType.IJobParallelForTransform;
    public JobType JobType => _jobType;

    public JobHandle Schedule(int count = 0, int batchSize = 64)
    {
      int effectiveCount = count > 0 ? count : _count;
      int effectiveBatchSize = batchSize > 0 ? batchSize : _batchSize;

#if DEBUG
      Log(Level.Verbose, $"Scheduling job for jobType={_jobType}, count={effectiveCount}, batchSize={effectiveBatchSize}", Category.Performance);
#endif

      switch (_jobType)
      {
        case JobType.IJob:
          var job = new DelegateJob<TInput, TOutput>
          {
            Delegate = _delegateIJob,
            Inputs = _inputs,
            Outputs = _outputs,
            StartIndex = _startIndex,
            Count = effectiveCount
          };
          return job.Schedule();
        case JobType.IJobParallelFor:
          var jobParallelFor = new DelegateForJob<TInput, TOutput>
          {
            Delegate = _delegateFor,
            Inputs = _inputs,
            Outputs = _outputs,
            StartIndex = _startIndex
          };
          return jobParallelFor.Schedule(effectiveCount, effectiveBatchSize);
        case JobType.IJobParallelForTransform:
          var transformJob = new DelegateParallelForTransformJob
          {
            Delegate = _delegateTransform,
            StartIndex = _startIndex,
            BatchSize = effectiveBatchSize
          };
          return transformJob.Schedule(_transforms);
        default:
          throw new ArgumentException($"Invalid job type: {_jobType}");
      }
    }

    public void Complete()
    {
      if (_jobType == JobType.IJobParallelForTransform && _transforms.isCreated)
      {
        _transforms.Dispose();
#if DEBUG
        Log(Level.Verbose, "Disposed TransformAccessArray", Category.Performance);
#endif
      }
    }
  }

  /// <summary>
  /// Tracks performance metrics for execution methods and cache access.
  /// </summary>
  internal static class Metrics
  {
    internal static readonly ConcurrentDictionary<string, Data> _metrics = new();
    private static readonly Stopwatch _stopwatch = new();
    private static bool _isInitialized;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ConcurrentDictionary<string, ProfilerMarker> _profilerMarkers = new();
#endif

    /// <summary>
    /// Gets the average item processing time in milliseconds.
    /// </summary>
    /// <param name="methodName">Name of the method to query.</param>
    /// <returns>Average time per item in milliseconds.</returns>
    public static float GetAvgItemTimeMs(string methodName) => MainThreadImpactTracker.GetAverageItemImpact(methodName);

    /// <summary>
    /// Initializes the metrics system and subscribes to tick events.
    /// </summary>
    public static void Initialize()
    {
      if (_isInitialized || !IsServer) return;
      _isInitialized = true;
      TimeManagerInstance.OnTick += UpdateMetrics;
      Log(Level.Info, "PerformanceMetrics initialized", Category.Performance);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Log(Level.Info, "Profiler API integration enabled for development build", Category.Performance);
#endif
    }

    /// <summary>
    /// Tracks metrics for a single entity job.
    /// </summary>
    [BurstCompile]
    public struct SingleEntityMetricsJob : IJob
    {
      public FixedString64Bytes TaskType;
      public float Timestamp;
      public NativeArray<Metric> Metrics;
      public NativeArray<CacheMetric> CacheMetrics;
      public int CacheIndex;

      public void Execute()
      {
        TrackExecutionBurst(TaskType, Timestamp, 1, Metrics, 0);
        if (CacheIndex >= 0)
        {
          CacheMetrics[CacheIndex] = new CacheMetric { CacheName = TaskType, IsHit = false };
        }
      }
    }

    /// <summary>
    /// Stores performance metrics for a method.
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
    /// Stores cache access metrics.
    /// </summary>
    [BurstCompile]
    public struct CacheMetric
    {
      public FixedString64Bytes CacheName;
      public bool IsHit;
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

    /// <summary>
    /// Tracks execution time and main thread impact of an action.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="action">Action to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    [BurstCompile]
    internal static void TrackExecution(string methodName, Action action, int itemCount = 1)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var marker = _profilerMarkers.GetOrAdd(methodName, new ProfilerMarker(methodName));
            marker.Begin();
#endif

      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            marker.End();
#endif
    }

    /// <summary>
    /// Tracks execution time and main thread impact of a function with return value.
    /// </summary>
    /// <typeparam name="T">Return type of the function.</typeparam>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="action">Function to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>Result of the function.</returns>
    [BurstCompile]
    internal static T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 1)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var marker = _profilerMarkers.GetOrAdd(methodName, new ProfilerMarker(methodName));
            marker.Begin();
#endif

      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      T result = action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            marker.End();
#endif

      return result;
    }

    /// <summary>
    /// Tracks execution time and main thread impact of an async action.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="action">Async action to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    internal static async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var marker = _profilerMarkers.GetOrAdd(methodName, new ProfilerMarker(methodName));
            marker.Begin();
#endif

      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      await action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            marker.End();
#endif
    }

    /// <summary>
    /// Tracks execution time and main thread impact of an async function with return value.
    /// </summary>
    /// <typeparam name="T">Return type of the function.</typeparam>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="action">Async function to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>Result of the function.</returns>
    internal static async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var marker = _profilerMarkers.GetOrAdd(methodName, new ProfilerMarker(methodName));
            marker.Begin();
#endif

      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      T result = await action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            marker.End();
#endif

      return result;
    }

    /// <summary>
    /// Tracks execution time and main thread impact of a coroutine.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="coroutine">Coroutine to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>Coroutine enumerator.</returns>
    internal static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var marker = _profilerMarkers.GetOrAdd(methodName, new ProfilerMarker(methodName));
            marker.Begin();
#endif

      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      while (coroutine.MoveNext())
        yield return coroutine.Current;
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            marker.End();
#endif
    }

    /// <summary>
    /// Tracks execution metrics for a Burst-compiled job.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="executionTimeMs">Execution time in milliseconds.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <param name="metrics">Array to store metrics.</param>
    /// <param name="index">Index in the metrics array.</param>
    [BurstCompile]
    internal static void TrackExecutionBurst(
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

#if DEBUG
      Log(Level.Verbose, $"Burst job tracked for {methodName}: time={executionTimeMs:F3}ms, items={itemCount}", Category.Performance);
#endif
    }

    /// <summary>
    /// Tracks cache access for a job.
    /// </summary>
    /// <param name="cacheName">Name of the cache.</param>
    /// <param name="isHit">Whether the cache access was a hit.</param>
    [BurstCompile]
    public static void TrackJobCacheAccess(string cacheName, bool isHit)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var marker = _profilerMarkers.GetOrAdd($"Cache_{cacheName}", new ProfilerMarker($"Cache_{cacheName}"));
            marker.Begin();
#endif

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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            marker.End();
#endif

#if DEBUG
      Log(Level.Verbose, $"Cache access tracked for {cacheName}: isHit={isHit}", Category.Performance);
#endif
    }

    /// <summary>
    /// Tracks cache access for non-job operations.
    /// </summary>
    /// <param name="cacheName">Name of the cache.</param>
    /// <param name="isHit">Whether the cache access was a hit.</param>
    public static void TrackCacheAccess(string cacheName, bool isHit)
    {
      TrackJobCacheAccess(cacheName, isHit);
    }

    /// <summary>
    /// Updates performance metrics for a method.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="timeMs">Execution time in milliseconds.</param>
    /// <param name="mainThreadImpactMs">Main thread impact in milliseconds.</param>
    /// <param name="itemCount">Number of items processed.</param>
    [BurstCompile]
    internal static void UpdateMetric(string methodName, double timeMs, float mainThreadImpactMs, int itemCount)
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

#if DEBUG
      Log(Level.Verbose, $"Updated metric for {methodName}: time={timeMs:F3}ms, mainThreadImpact={mainThreadImpactMs:F3}ms, items={itemCount}", Category.Performance);
#endif
    }

    /// <summary>
    /// Logs aggregated metrics periodically.
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
    /// Cleans up metrics and unsubscribes from tick events.
    /// </summary>
    public static void Cleanup()
    {
      TimeManagerInstance.OnTick -= UpdateMetrics;
      _metrics.Clear();
      _isInitialized = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _profilerMarkers.Clear();
#endif

      Log(Level.Info, "PerformanceMetrics cleaned up", Category.Performance);
    }
  }

  /// <summary>
  /// Manages dynamic profiling data with rolling averages and batch size history.
  /// </summary>
  internal static class DynamicProfiler
  {
    internal static readonly ConcurrentDictionary<string, RollingAverage> _rollingAverages = new();
    internal static readonly ConcurrentDictionary<string, List<int>> _batchSizeHistory = new();
    private const int WINDOW_SIZE = 100;
    private const int BATCH_HISTORY_SIZE = 5;
    private const float MIN_AVG_TIME_MS = 0.05f;
    private const float MAX_AVG_TIME_MS = 0.5f;

    /// <summary>
    /// Maintains a rolling average of performance samples.
    /// </summary>
    internal class RollingAverage
    {
      internal readonly double[] _samples;
      private int _count;
      internal double _sum;
      private readonly int _maxCount;

      public int Count => _count;

      public RollingAverage(int maxCount)
      {
        _samples = new double[maxCount];
        _count = 0;
        _sum = 0;
        _maxCount = maxCount;
      }

      /// <summary>
      /// Adds a performance sample to the rolling average.
      /// </summary>
      /// <param name="value">Sample value in milliseconds.</param>
      public void AddSample(double value)
      {
        if (_count < _maxCount)
        {
          _samples[_count] = value;
          _sum += value;
          _count++;
        }
        else
        {
          _sum -= _samples[_count % _maxCount];
          _samples[_count % _maxCount] = value;
          _sum += value;
        }
        MetricsCache.AddPerformanceSample(Thread.CurrentThread.ManagedThreadId == 1 ? $"MainThread_{Environment.StackTrace}" : Environment.StackTrace, value);
      }

      /// <summary>
      /// Gets the current average of samples.
      /// </summary>
      /// <returns>Average time in milliseconds, clamped between MIN_AVG_TIME_MS and MAX_AVG_TIME_MS.</returns>
      public double GetAverage()
      {
        return _count == 0 ? 0 : Math.Clamp(_sum / _count, MIN_AVG_TIME_MS, MAX_AVG_TIME_MS);
      }
    }

    /// <summary>
    /// Adds a performance sample for a method.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
    internal static void AddSample(string methodName, double avgItemTimeMs)
    {
      if (avgItemTimeMs <= 0) return;
      var avg = _rollingAverages.GetOrAdd(methodName, _ => new RollingAverage(WINDOW_SIZE));
      MetricsCache.AddPerformanceSample(methodName, avgItemTimeMs);
      avg.AddSample(avgItemTimeMs);
    }

    /// <summary>
    /// Adds a batch size sample for a method.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the method.</param>
    /// <param name="batchSize">Batch size used.</param>
    internal static void AddBatchSize(string uniqueId, int batchSize)
    {
      var history = _batchSizeHistory.GetOrAdd(uniqueId, _ => new List<int>(BATCH_HISTORY_SIZE));
      if (history.Count >= BATCH_HISTORY_SIZE)
        history.RemoveAt(0);
      history.Add(batchSize);
    }

    /// <summary>
    /// Gets the dynamic average processing time for a method.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="defaultTimeMs">Default time if no data exists.</param>
    /// <returns>Average processing time in milliseconds.</returns>
    internal static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f)
    {
      RollingAverage rollingAvg = null;
      if (MetricsCache.TryGetAverage(methodName, out var avg) || _rollingAverages.TryGetValue(methodName, out rollingAvg))
      {
        float result = (float)(rollingAvg?.GetAverage() ?? avg);
#if DEBUG
        Log(Level.Verbose, $"DynamicProfiler: {methodName} avgProcessingTimeMs={result:F3}ms (samples={(rollingAvg?.Count ?? 0)})", Category.Performance);
#endif
        return result;
      }
      return defaultTimeMs;
    }

    /// <summary>
    /// Gets the average batch size for a method.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the method.</param>
    /// <returns>Average batch size.</returns>
    internal static int GetAverageBatchSize(string uniqueId)
    {
      var history = _batchSizeHistory.GetOrAdd(uniqueId, _ => new List<int>(BATCH_HISTORY_SIZE));
      if (history.Count == 0) return 0;
      double sum = 0;
      foreach (var size in history) sum += size;
      return Mathf.RoundToInt((float)(sum / history.Count));
    }

    /// <summary>
    /// Cleans up profiling data.
    /// </summary>
    internal static void Cleanup()
    {
      _rollingAverages.Clear();
      _batchSizeHistory.Clear();
      MetricsCache.Cleanup();
      Log(Level.Info, "DynamicProfiler cleaned up", Category.Performance);
    }
  }

  /// <summary>
  /// Tracks main thread impact for performance optimization.
  /// </summary>
  internal static class MainThreadImpactTracker
  {
    private struct ImpactSample
    {
      public string MethodName;
      public long StartTicks;
    }
    internal static readonly ConcurrentDictionary<string, DynamicProfiler.RollingAverage> _impactAverages = new();
    private static double _stopwatchOverheadMs;
    private const int WINDOW_SIZE = 100;
    private const double OUTLIER_THRESHOLD_MS = 10.0;
    private const float VARIABILITY_THRESHOLD = 2.0f;
    private static readonly ConcurrentDictionary<string, DynamicProfiler.RollingAverage> _mainThreadImpacts = new();
    private static readonly ConcurrentDictionary<int, ImpactSample> _samples = new();
    private static readonly Stopwatch _stopwatch = new();
    private static int _sampleId;

    /// <summary>
    /// Initializes the tracker with overhead calibration and test job.
    /// </summary>
    public static void Initialize()
    {
      var stopwatch = Stopwatch.StartNew();
      stopwatch.Stop();
      _stopwatchOverheadMs = stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
      var testData = new NativeArray<int>(10, Allocator.TempJob);
      var testOutputs = new NativeList<int>(10, Allocator.TempJob);
      try
      {
        Action<int, int, NativeArray<int>, NativeList<int>> testDelegate = (start, end, inputs, outputs) =>
        {
          for (int i = start; i < end; i++) outputs.Add(inputs[i] + 1);
        };
        var wrapper = new DelegateJobWrapper<int, int>(testDelegate, null, null, testData, testOutputs, default, 0, 10, 0, JobType.IJob);
        var sw = Stopwatch.StartNew();
        var handle = wrapper.Schedule();
        handle.Complete();
        sw.Stop();
        double testOverheadMs = sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency - _stopwatchOverheadMs;
        AddImpactSample("Test_IJob", testOverheadMs / 10);
      }
      finally
      {
        testData.Dispose();
        testOutputs.Dispose();
      }
    }

    /// <summary>
    /// Adds a main thread impact sample.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="impactTimeMs">Impact time in milliseconds.</param>
    public static void AddImpactSample(string methodName, double impactTimeMs)
    {
      if (impactTimeMs <= 0) return;
      impactTimeMs = Math.Max(0, impactTimeMs - _stopwatchOverheadMs);
      var avg = _impactAverages.GetOrAdd(methodName, _ => new DynamicProfiler.RollingAverage(WINDOW_SIZE));
      if (impactTimeMs > OUTLIER_THRESHOLD_MS)
        Log(Level.Warning, $"Outlier detected for {methodName}: {impactTimeMs:F3}ms", Category.Performance);
      else if (avg.Count > 0 && impactTimeMs > avg.GetAverage() * VARIABILITY_THRESHOLD)
      {
#if DEBUG
        Log(Level.Verbose, $"High variability for {methodName}: {impactTimeMs:F3}ms vs avg {avg.GetAverage():F3}ms", Category.Performance);
#endif
      }
      MetricsCache.AddImpactSample(methodName, impactTimeMs);
      avg.AddSample(impactTimeMs);
    }

    /// <summary>
    /// Gets the average item impact for a method.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <returns>Average impact per item in milliseconds.</returns>
    public static float GetAverageItemImpact(string methodName)
    {
      DynamicProfiler.RollingAverage avg = null;
      if (MetricsCache.TryGetImpact(methodName, out var impact) || _impactAverages.TryGetValue(methodName, out avg))
      {
        float result = (float)(avg?.GetAverage() ?? impact);
#if DEBUG
        Log(Level.Verbose, $"MainThreadImpactTracker: {methodName} avgImpactTimeMs={result:F3}ms (samples={(avg?.Count ?? 0)})", Category.Performance);
#endif
        return result;
      }
      return float.MaxValue;
    }

    /// <summary>
    /// Gets the cost of a coroutine.
    /// </summary>
    /// <param name="coroutineKey">Coroutine identifier.</param>
    /// <returns>Coroutine cost in milliseconds.</returns>
    public static float GetCoroutineCost(string coroutineKey)
    {
      float cost = GetAverageItemImpact(coroutineKey);
      return cost == float.MaxValue ? 0.2f : cost;
    }

    /// <summary>
    /// Gets the overhead of a job.
    /// </summary>
    /// <param name="jobKey">Job identifier.</param>
    /// <returns>Job overhead in milliseconds.</returns>
    public static float GetJobOverhead(string jobKey)
    {
      float overhead = GetAverageItemImpact(jobKey);
      return overhead == float.MaxValue ? 2f : overhead;
    }

    /// <summary>
    /// Tracks a job's execution with stopwatch timing.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="scheduleJob">Function to schedule the job.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>Coroutine enumerator.</returns>
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
    /// Begins tracking a main thread sample.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <returns>Sample ID.</returns>
    public static int BeginSample(string methodName)
    {
      int id = Interlocked.Increment(ref _sampleId);
      _samples[id] = new ImpactSample { MethodName = methodName, StartTicks = _stopwatch.ElapsedTicks };
      return id;
    }

    /// <summary>
    /// Ends tracking a main thread sample and returns impact time.
    /// </summary>
    /// <param name="sampleId">Sample ID.</param>
    /// <returns>Main thread impact in milliseconds.</returns>
    public static float EndSample(int sampleId)
    {
      if (!_samples.TryRemove(sampleId, out var sample)) return 0;
      float impactMs = (_stopwatch.ElapsedTicks - sample.StartTicks) * 1000f / Stopwatch.Frequency;
      var avg = _mainThreadImpacts.GetOrAdd(sample.MethodName, _ => new DynamicProfiler.RollingAverage(100));
      avg.AddSample(impactMs);
      return impactMs;
    }

    /// <summary>
    /// Updates performance thresholds periodically.
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
          float coroutineCostMs = (float)coroutineAvg.GetAverage();
          int threshold = Mathf.Max(100, Mathf.FloorToInt(1000f * SmartExecution.DEFAULT_THRESHOLD / Math.Max(1f, coroutineCostMs - mainThreadImpact)));
          SmartExecution.MetricsThresholds[uniqueId] = threshold;
        }
      }
    }

    /// <summary>
    /// Cleans up tracking data.
    /// </summary>
    public static void Cleanup()
    {
      TimeManagerInstance.OnTick -= UpdateThresholds;
      _mainThreadImpacts.Clear();
      _samples.Clear();
      _impactAverages.Clear();
      Log(Level.Info, "MainThreadImpactTracker cleaned up", Category.Performance);
    }
  }

  /// <summary>
  /// Caches performance and impact metrics.
  /// </summary>
  internal static class MetricsCache
  {
    private static readonly ConcurrentDictionary<string, double> _averageCache = new();
    private static readonly ConcurrentDictionary<string, double> _impactCache = new();
    private const int MAX_CACHE_SIZE = 1000;

    /// <summary>
    /// Adds a performance sample to the cache.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="avgItemTimeMs">Average item time in milliseconds.</param>
    public static void AddPerformanceSample(string methodName, double avgItemTimeMs)
    {
      if (avgItemTimeMs <= 0) return;
      if (_averageCache.Count >= MAX_CACHE_SIZE)
      {
        var oldestKey = _averageCache.Keys.FirstOrDefault();
        if (oldestKey != null)
          _averageCache.TryRemove(oldestKey, out _);
      }
      _averageCache.AddOrUpdate(methodName, avgItemTimeMs, (_, _) => avgItemTimeMs);
    }

    /// <summary>
    /// Adds an impact sample to the cache.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="impactTimeMs">Impact time in milliseconds.</param>
    public static void AddImpactSample(string methodName, double impactTimeMs)
    {
      if (impactTimeMs <= 0) return;
      if (_impactCache.Count >= MAX_CACHE_SIZE)
      {
        var oldestKey = _impactCache.Keys.FirstOrDefault();
        if (oldestKey != null)
          _impactCache.TryRemove(oldestKey, out _);
      }
      _impactCache.AddOrUpdate(methodName, impactTimeMs, (_, _) => impactTimeMs);
    }

    /// <summary>
    /// Tries to get the average performance metric.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="avg">Output average time in milliseconds.</param>
    /// <returns>True if found, false otherwise.</returns>
    public static bool TryGetAverage(string methodName, out double avg)
    {
      return _averageCache.TryGetValue(methodName, out avg);
    }

    /// <summary>
    /// Tries to get the impact metric.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <param name="impact">Output impact time in milliseconds.</param>
    /// <returns>True if found, false otherwise.</returns>
    public static bool TryGetImpact(string methodName, out double impact)
    {
      return _impactCache.TryGetValue(methodName, out impact);
    }

    /// <summary>
    /// Clears all cached metrics.
    /// </summary>
    public static void Cleanup()
    {
      _averageCache.Clear();
      _impactCache.Clear();
      Log(Level.Info, "CacheManager cleared", Category.Performance);
    }
  }

  /// <summary>
  /// Options for configuring SmartExecution behavior.
  /// </summary>
  public struct SmartExecutionOptions
  {
    public bool SpreadSingleItem;
    public float SingleItemThresholdMs;
    public bool IsNetworked;
    public int? BatchSizeOverride;
    internal JobType? PreferredJobType;

    public static SmartExecutionOptions Default => new SmartExecutionOptions
    {
      SpreadSingleItem = false,
      SingleItemThresholdMs = 0.1f,
      IsNetworked = false,
      BatchSizeOverride = null,
      PreferredJobType = null
    };
  }

  /// <summary>
  /// Manages intelligent execution of tasks using jobs, coroutines, or main thread.
  /// </summary>
  public static partial class SmartExecution
  {
    private static readonly NativeParallelHashSet<FixedString64Bytes> _firstRuns = new(100, Allocator.Persistent);
    private static long _lastGCMemory;
    public static readonly float TARGET_FRAME_TIME;
    internal static readonly ConcurrentDictionary<string, int> MetricsThresholds = new();
    internal static int DEFAULT_THRESHOLD = 100;
    internal static float MAX_FRAME_TIME_MS = 1f;
    private static float HIGH_FPS_THRESHOLD = 0.01f;
    private static float FPS_CHANGE_THRESHOLD = 0.2f;
    private static int MIN_TEST_EXECUTIONS = 3;
    private static int PARALLEL_MIN_ITEMS = 100;
    private const int REVALIDATION_INTERVAL = 50;
    private static readonly ConcurrentDictionary<string, bool> _baselineEstablished = new();
    private static readonly ConcurrentDictionary<string, int> _executionCount = new();
    private static float _lastFps = 60f;
    private const float METRIC_STABILITY_THRESHOLD = 0.05f;
    private const int STABILITY_WINDOW = 100;
    private const float STABILITY_VARIANCE_THRESHOLD = 0.001f;
    private const string fileName = "NoLazyWorkers_OptimizationData_";
    private static string _lastSavedDataHash;
    private static readonly ObjectPool<List<int>> _intOutputPool = new ObjectPool<List<int>>(
        createFunc: () => new List<int>(10),
        actionOnGet: null,
        actionOnRelease: list => list.Clear(),
        actionOnDestroy: null,
        collectionCheck: false,
        defaultCapacity: 10,
        maxSize: 100);

    static SmartExecution()
    {
      int targetFrameRate = Mathf.Clamp(Application.targetFrameRate, 30, 120);
      TARGET_FRAME_TIME = targetFrameRate > 0 ? 1f / targetFrameRate : 1f / 60f;
      CoroutineRunner.Instance.RunCoroutine(TrackGCAllocationsCoroutine());
    }

    /// <summary>
    /// Initializes the SmartExecution system.
    /// </summary>
    public static void Initialize()
    {
      Metrics.Initialize();
      MainThreadImpactTracker.Initialize();
      CoroutineRunner.Instance.RunCoroutine(MonitorCpuStability());
      LoadBaselineData();
    }

    /// <summary>
    /// Executes a task with dynamic selection of execution method (job, coroutine, or main thread).
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <param name="jobCallback">Job callback for non-Burst execution.</param>
    /// <param name="coroutineCallback">Coroutine callback.</param>
    /// <param name="mainThreadCallback">Main thread callback.</param>
    /// <param name="burstDelegate">Burst-compiled job delegate.</param>
    /// <param name="burstForDelegate">Burst-compiled parallel-for delegate.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>Coroutine enumerator.</returns>
    public static IEnumerator Execute(
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
    {
      if (itemCount == 0 || (jobCallback == null && coroutineCallback == null && mainThreadCallback == null && burstDelegate == null && burstForDelegate == null))
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
      float jobOverhead = MainThreadImpactTracker.GetJobOverhead(jobKey);
      bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;
      int batchSize = options.BatchSizeOverride ?? GetDynamicBatchSize(itemCount, 0.15f, uniqueId, options.PreferredJobType == JobType.IJobParallelFor);
      JobType jobType = options.PreferredJobType ?? DetermineJobType(uniqueId, itemCount, false, mainThreadImpact);

      bool useJob = itemCount == 1 ? jobType == JobType.IJob : (jobType == JobType.IJobFor || jobType == JobType.IJobParallelFor);

      NativeList<int> nativeOutputs = default;
      if (outputs != null)
      {
        nativeOutputs = new NativeList<int>(Allocator.TempJob);
        for (int i = 0; i < outputs.Count; i++) nativeOutputs.Add(outputs[i]);
      }
      if (itemCount == 1 && useJob && burstDelegate != null)
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via IJob (single item)", Category.Performance);
#endif
        var jobWrapper = new DelegateJobWrapper<int, int>(burstDelegate, null, null, inputs, nativeOutputs, default, 0, 1, 1, JobType.IJob);
        yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(), 1);
      }
      else if (itemCount > 1 && useJob && (burstDelegate != null || burstForDelegate != null))
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via {jobType} ({itemCount} items, batchSize={batchSize})", Category.Performance);
#endif
        var jobWrapper = new DelegateJobWrapper<int, int>(burstDelegate, burstForDelegate, null, inputs, nativeOutputs, default, 0, itemCount, batchSize, jobType);
        yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(itemCount, batchSize), itemCount);
      }
      else if (coroutineCallback != null && (mainThreadCallback == null || coroutineImpact <= mainThreadImpact))
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via Coroutine ({itemCount} items)", Category.Performance);
#endif
        yield return Metrics.TrackExecutionCoroutine(coroutineKey, coroutineCallback(), itemCount);
      }
      else if (mainThreadCallback != null && isHighFps)
      {
#if DEBUG
        Log(Level.Verbose, $"Executing {uniqueId} via MainThread ({itemCount} items, highFps={isHighFps})", Category.Performance);
#endif
        Metrics.TrackExecution(mainThreadKey, mainThreadCallback, itemCount);
      }
      else
      {
        Log(Level.Warning, $"No valid execution path for {uniqueId}, yielding", Category.Performance);
        yield return null;
      }
    }

    /// <summary>
    /// Executes a transform-based task with dynamic execution method selection.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="transforms">Transform access array.</param>
    /// <param name="transformDelegate">Delegate for transform processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="options">Execution options.</param>
    public static void ExecuteTransforms<TInput, TOutput>(
        string uniqueId,
        TransformAccessArray transforms,
        Action<int, Transform, NativeArray<TInput>, List<TOutput>> transformDelegate,
        NativeArray<TInput> inputs = default,
        List<TOutput> outputs = null,
        SmartExecutionOptions options = default)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      if (!transforms.isCreated || transforms.length == 0 || transformDelegate == null)
      {
        Log(Level.Warning, $"SmartExecution.ExecuteTransforms: Invalid input for {uniqueId}, transformCount={transforms.length}", Category.Performance);
        return;
      }

      var jobOutputs = outputs != null ? new NativeList<TOutput>(transforms.length, Allocator.TempJob) : default;
      Action<int, TransformAccess> burstTransformDelegate = (index, transform) =>
      {
        var tempList = outputs != null ? new List<TOutput>() : null;
        transformDelegate(index, null, inputs, tempList);
        if (tempList != null)
          for (int i = 0; i < tempList.Count; i++)
            jobOutputs.Add(tempList[i]);
      };
      Action<int, int, NativeArray<TInput>, List<TOutput>> nonBurstDelegate = (start, end, inArray, outList) =>
      {
        for (int i = start; i < end; i++)
        {
          Transform transform = transforms[i];
          transformDelegate(i, transform, inArray, outList);
        }
      };

      var coroutine = ExecuteCore(uniqueId, null, nonBurstDelegate, inputs, jobOutputs, outputs, options, transforms, burstTransformDelegate);
      CoroutineRunner.Instance.RunCoroutine(coroutine);

#if DEBUG
      Log(Level.Verbose, $"Started ExecuteTransforms for {uniqueId}, transformCount={transforms.length}", Category.Performance);
#endif
    }

    /// <summary>
    /// Core execution logic for tasks with generic input/output types.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="burstDelegate">Burst-compiled job delegate.</param>
    /// <param name="nonBurstDelegate">Non-Burst delegate.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="jobOutputs">Job output list.</param>
    /// <param name="coroutineOutputs">Coroutine output list.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="transforms">Transform access array.</param>
    /// <param name="transformDelegate">Transform delegate.</param>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator ExecuteCore<TInput, TOutput>(
        string uniqueId,
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate,
        Action<int, int, NativeArray<TInput>, List<TOutput>> nonBurstDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> jobOutputs,
        List<TOutput> coroutineOutputs,
        SmartExecutionOptions options,
        TransformAccessArray transforms = default,
        Action<int, TransformAccess> transformDelegate = null)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      string jobKey = $"{uniqueId}_IJob";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
      int itemCount = transforms.isCreated ? transforms.length : inputs.Length;
      int batchSize = options.BatchSizeOverride ?? GetDynamicBatchSize(itemCount, 0.15f, uniqueId, options.PreferredJobType == JobType.IJobParallelFor || options.PreferredJobType == JobType.IJobParallelForTransform);
      DynamicProfiler.AddBatchSize(uniqueId, batchSize);
      bool isDroppedFrame = Time.unscaledDeltaTime > TARGET_FRAME_TIME * 1.1f;

      if (isDroppedFrame)
      {
        batchSize = Mathf.Max(1, batchSize / 2);
#if DEBUG
        Log(Level.Verbose, $"Dropped frame detected for {uniqueId}: frameTime={Time.unscaledDeltaTime * 1000f:F3}ms, newBatchSize={batchSize}", Category.Performance);
#endif
      }

      bool isFirstRun = _firstRuns.Add(uniqueId);
      float currentFps = 1f / Time.unscaledDeltaTime;
      bool fpsChanged = Mathf.Abs(currentFps - _lastFps) / _lastFps > FPS_CHANGE_THRESHOLD;
      _lastFps = currentFps;

      if (!_baselineEstablished.ContainsKey(uniqueId) || fpsChanged || _executionCount.GetOrAdd(uniqueId, 0) >= REVALIDATION_INTERVAL)
      {
        _baselineEstablished.TryRemove(uniqueId, out _);
        yield return EstablishBaseline(uniqueId, burstDelegate, nonBurstDelegate, inputs, jobOutputs, coroutineOutputs, batchSize, options.IsNetworked);
      }

      float mainThreadCost = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
      float coroutineCost = MainThreadImpactTracker.GetCoroutineCost(coroutineKey);
      float jobOverhead = MainThreadImpactTracker.GetJobOverhead(jobKey);
      bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;

      for (int i = 0; i < itemCount; i += batchSize)
      {
        int currentBatchSize = Math.Min(batchSize, itemCount - i);
        JobType jobType = options.PreferredJobType ?? DetermineJobType(uniqueId, currentBatchSize, transforms.isCreated, mainThreadCost);
        bool useParallel = ShouldUseParallel(uniqueId, currentBatchSize);
        float mainThreadBatchCost = mainThreadCost * currentBatchSize;

        if (currentBatchSize == 1 && options.SpreadSingleItem && (burstDelegate != null || transformDelegate != null))
        {
          var stopwatch = Stopwatch.StartNew();
          var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(
              burstDelegate,
              jobType == JobType.IJobParallelFor ? (index, inArray, outList) => burstDelegate(index, index + 1, inArray, outList) : null,
              transformDelegate,
              inputs,
              jobOutputs,
              transforms,
              i,
              1,
              1,
              jobType
          );
          yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(), 1);
          stopwatch.Stop();

          if (stopwatch.ElapsedMilliseconds > options.SingleItemThresholdMs)
          {
#if DEBUG
            Log(Level.Verbose, $"Single item for {uniqueId} exceeded threshold ({stopwatch.ElapsedMilliseconds:F3}ms), yielding", Category.Performance);
#endif
            yield return options.IsNetworked ? AwaitNextFishNetTickAsync(0f) : null;
          }

          if (coroutineOutputs != null)
            CopyOutputs(jobOutputs, coroutineOutputs);
          continue;
        }

        bool isStale = !_baselineEstablished.ContainsKey(uniqueId);
        if (isStale && nonBurstDelegate != null)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (stale metrics, {currentBatchSize} items)", Category.Performance);
#endif
          yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(nonBurstDelegate, inputs, coroutineOutputs, i, currentBatchSize, options.IsNetworked), currentBatchSize);
        }
        else if (mainThreadBatchCost <= MAX_FRAME_TIME_MS && nonBurstDelegate != null && isHighFps)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via MainThread ({currentBatchSize} items, cost={mainThreadBatchCost:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
          Metrics.TrackExecution(mainThreadKey, () => RunMainThread(nonBurstDelegate, inputs, coroutineOutputs, i, currentBatchSize), currentBatchSize);
        }
        else if (coroutineCost <= jobOverhead && nonBurstDelegate != null)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via Coroutine ({currentBatchSize} items, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
          yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(nonBurstDelegate, inputs, coroutineOutputs, i, currentBatchSize, options.IsNetworked), currentBatchSize);
        }
        else if ((burstDelegate != null || transformDelegate != null) && jobOverhead <= mainThreadBatchCost)
        {
          var testJob = jobType == JobType.IJob
              ? new DelegateJob<TInput, TOutput>
              {
                Delegate = burstDelegate != null ? BurstCompiler.CompileFunctionPointer(burstDelegate) : default,
                Inputs = inputs,
                Outputs = jobOutputs,
                StartIndex = i,
                Count = currentBatchSize
              }
              : default;
          var handle = testJob.Schedule();
          bool highQueuePressure = !handle.IsCompleted;
          handle.Complete();

          if (highQueuePressure && nonBurstDelegate != null)
          {
#if DEBUG
            Log(Level.Verbose, $"High job queue pressure for {uniqueId}, falling back to coroutine", Category.Performance);
#endif
            yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(nonBurstDelegate, inputs, coroutineOutputs, i, currentBatchSize, options.IsNetworked), currentBatchSize);
          }
          else
          {
#if DEBUG
            Log(Level.Verbose, $"Executing {uniqueId} via Job ({currentBatchSize} items, jobType={jobType}, jobOverhead={jobOverhead:F3}ms)", Category.Performance);
#endif
            var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(
                burstDelegate,
                jobType == JobType.IJobParallelFor ? (index, inArray, outList) => burstDelegate(index, index + 1, inArray, outList) : null,
                transformDelegate,
                inputs,
                jobOutputs,
                transforms,
                i,
                currentBatchSize,
                currentBatchSize,
                jobType
            );
            yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(), currentBatchSize);
            Metrics.TrackJobCacheAccess($"{uniqueId}_JobCache", true);
          }
        }
        else if (nonBurstDelegate != null)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} via Async ({currentBatchSize} items)", Category.Performance);
#endif
          yield return Metrics.TrackExecutionAsync(coroutineKey, () => Task.Run(() => nonBurstDelegate(i, i + currentBatchSize, inputs, coroutineOutputs)), currentBatchSize).AsCoroutine();
        }
        else
        {
          Log(Level.Warning, $"No valid execution path for {uniqueId}, yielding", Category.Performance);
          yield return null;
        }

        _executionCount.AddOrUpdate(uniqueId, 1, (_, count) => count + 1);
        if (Time.unscaledDeltaTime > TARGET_FRAME_TIME * 1.5f)
        {
          batchSize = Mathf.Max(1, batchSize / 2);
          jobType = DetermineJobType(uniqueId, currentBatchSize, transforms.isCreated, mainThreadCost);
#if DEBUG
          Log(Level.Verbose, $"Performance degradation for {uniqueId}, adjusting batchSize={batchSize}, jobType={jobType}", Category.Performance);
#endif
        }

        yield return options.IsNetworked ? AwaitNextFishNetTickAsync(0f) : null;
      }

      if (coroutineOutputs != null && jobOutputs.IsCreated)
        CopyOutputs(jobOutputs, coroutineOutputs);
      if (jobOutputs.IsCreated)
        jobOutputs.Dispose();

#if DEBUG
      Log(Level.Verbose, $"Completed ExecuteCore for {uniqueId}, itemCount={itemCount}", Category.Performance);
#endif
    }

    /// <summary>
    /// Determines the appropriate job type based on performance metrics.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <param name="hasTransforms">Whether transforms are involved.</param>
    /// <param name="mainThreadCost">Main thread cost per item.</param>
    /// <returns>Selected job type.</returns>
    private static JobType DetermineJobType(string uniqueId, int itemCount, bool hasTransforms, float mainThreadCost)
    {
      float avgItemTimeMs = Metrics.GetAvgItemTimeMs($"{uniqueId}_IJob");
      int smallThreshold = 50;
      int mediumThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_MediumThreshold", 500);
      int parallelThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_ParallelThreshold", PARALLEL_MIN_ITEMS);
      int transformThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_TransformThreshold", PARALLEL_MIN_ITEMS * 2);

      if (hasTransforms)
      {
        if (itemCount > transformThreshold && avgItemTimeMs > 0.05f)
        {
#if DEBUG
          Log(Level.Verbose, $"Selected IJobParallelForTransform for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
          return JobType.IJobParallelForTransform;
        }
        if (itemCount > mediumThreshold)
        {
#if DEBUG
          Log(Level.Verbose, $"Selected IJobFor for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
          return JobType.IJobFor;
        }
        return JobType.IJob;
      }

      if (itemCount < smallThreshold || mainThreadCost < 0.1f)
      {
#if DEBUG
        Log(Level.Verbose, $"Selected IJob for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
        return JobType.IJob;
      }
      if (itemCount <= mediumThreshold && avgItemTimeMs > 0.02f)
      {
#if DEBUG
        Log(Level.Verbose, $"Selected IJobFor for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
        return JobType.IJobFor;
      }
      if (itemCount > parallelThreshold && avgItemTimeMs > 0.05f && SystemInfo.processorCount > 2)
      {
#if DEBUG
        Log(Level.Verbose, $"Selected IJobParallelFor for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}, cores={SystemInfo.processorCount}", Category.Performance);
#endif
        return JobType.IJobParallelFor;
      }
      return JobType.IJob;
    }

    /// <summary>
    /// Copies outputs from a NativeList to a List.
    /// </summary>
    /// <typeparam name="T">Unmanaged data type.</typeparam>
    /// <param name="source">Source NativeList.</param>
    /// <param name="destination">Destination List.</param>
    private static void CopyOutputs<T>(NativeList<T> source, List<T> destination) where T : unmanaged
    {
      destination.Clear();
      for (int i = 0; i < source.Length; i++)
        destination.Add(source[i]);
    }

    /// <summary>
    /// Runs a coroutine for processing data.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
    /// <param name="nonBurstDelegate">Non-Burst delegate.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="startIndex">Start index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    /// <param name="isNetworked">Whether execution is networked.</param>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator RunCoroutine<TInput, TOutput>(
        Action<int, int, NativeArray<TInput>, List<TOutput>> nonBurstDelegate,
        NativeArray<TInput> inputs,
        List<TOutput> outputs,
        int startIndex,
        int count,
        bool isNetworked)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count);
      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
        nonBurstDelegate(i, i + currentSubBatchSize, inputs, outputs);
        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return isNetworked ? AwaitNextFishNetTickAsync(0f) : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Runs a main thread task for processing data.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
    /// <param name="nonBurstDelegate">Non-Burst delegate.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="startIndex">Start index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    private static void RunMainThread<TInput, TOutput>(
        Action<int, int, NativeArray<TInput>, List<TOutput>> nonBurstDelegate,
        NativeArray<TInput> inputs,
        List<TOutput> outputs,
        int startIndex,
        int count)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      nonBurstDelegate(startIndex, startIndex + count, inputs, outputs);
    }

    /// <summary>
    /// Tracks GC allocations periodically.
    /// </summary>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator TrackGCAllocationsCoroutine()
    {
      while (true)
      {
        long currentMemory = GC.GetTotalMemory(false);
        if (_lastGCMemory > 0)
        {
          long deltaTime = currentMemory - _lastGCMemory;
          if (deltaTime > 50 * 1024) // >50KB
            Log(Level.Warning, $"High GC allocation detected: {(deltaTime / 1024f):F3}KB", Category.Performance);
#if DEBUG
          Log(Level.Verbose, $"GC allocation: {(deltaTime / 1024f):F3}KB", Category.Performance);
#endif
        }
        _lastGCMemory = currentMemory;
        yield return new WaitForSeconds(5f);
      }
    }

    /// <summary>
    /// Resets baseline performance data.
    /// </summary>
    internal static void ResetBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, fileName + "_" + $"{saveId}.json");
      DynamicProfiler.Cleanup();
      MainThreadImpactTracker.Cleanup();
      MetricsCache.Cleanup();
      _baselineEstablished.Clear();
      _executionCount.Clear();
      _firstRuns.Clear();
      _lastSavedDataHash = null;
      MetricsThresholds.Clear();
      if (File.Exists(filePath))
        File.Delete(filePath);
      Log(Level.Info, $"Reset baseline data for new game at {filePath}", Category.Performance);
    }

    /// <summary>
    /// Monitors CPU stability to trigger baseline establishment.
    /// </summary>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator MonitorCpuStability()
    {
      var frameTimes = new List<float>(STABILITY_WINDOW);
      while (frameTimes.Count < STABILITY_WINDOW || CalculateVariance(frameTimes) > STABILITY_VARIANCE_THRESHOLD)
      {
        frameTimes.Add(Time.deltaTime);
        if (frameTimes.Count > STABILITY_WINDOW)
          frameTimes.RemoveAt(0);
        yield return null;
      }
      Log(Level.Info, "CPU load stable, triggering initial baseline creation", Category.Performance);
      yield return EstablishInitialBaseline();
    }

    /// <summary>
    /// Calculates variance of frame times.
    /// </summary>
    /// <param name="values">List of frame times.</param>
    /// <returns>Variance of frame times.</returns>
    private static float CalculateVariance(List<float> values)
    {
      if (values.Count == 0) return float.MaxValue;
      float mean = values.Average();
      float variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
      return variance;
    }

    private static IEnumerator EstablishInitialBaseline()
    {
      string testId = "InitialBaselineTest";
      var testInputs = new NativeArray<int>(10, Allocator.TempJob);
      var testJobOutputs = new NativeList<int>(10, Allocator.TempJob);
      var testCoroutineOutputs = _intOutputPool.Get();
      try
      {
        for (int i = 0; i < testInputs.Length; i++)
          testInputs[i] = i;
        Action<int, int, NativeArray<int>, NativeList<int>> burstDelegate = (start, end, inputs, outputs) =>
        {
          for (int i = start; i < end; i++)
            outputs.Add(inputs[i] * 2);
        };
        Action<int, int, NativeArray<int>, List<int>> nonBurstDelegate = (start, end, inputs, outputs) =>
        {
          for (int i = start; i < end; i++)
            outputs.Add(inputs[i] * 2);
        };
        float mainThreadTime = 0;
        Metrics.TrackExecution($"{testId}_MainThread", () => RunMainThread(nonBurstDelegate, testInputs, testCoroutineOutputs, 0, testInputs.Length), testInputs.Length);
        mainThreadTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_MainThread");
        yield return Metrics.TrackExecutionCoroutine($"{testId}_Coroutine", RunCoroutine(nonBurstDelegate, testInputs, testCoroutineOutputs, 0, testInputs.Length, false), testInputs.Length);
        float coroutineCostMs = (float)Metrics.GetAvgItemTimeMs($"{testId}_Coroutine");
        var jobWrapper = new DelegateJobWrapper<int, int>(burstDelegate, null, null, testInputs, testJobOutputs, default, 0, testInputs.Length, testInputs.Length, JobType.IJob);
        yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJob", () => jobWrapper.Schedule(testInputs.Length), testInputs.Length);
        float jobTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJob");
        DEFAULT_THRESHOLD = Mathf.Max(50, Mathf.FloorToInt(1000f / Math.Max(1f, mainThreadTime)));
        MAX_FRAME_TIME_MS = Mathf.Clamp(mainThreadTime * 10, 0.5f, 2f);
        HIGH_FPS_THRESHOLD = Mathf.Clamp(1f / (60f + mainThreadTime * 1000f), 0.005f, 0.02f);
        FPS_CHANGE_THRESHOLD = Mathf.Clamp(mainThreadTime / 1000f, 0.1f, 0.3f);
        MIN_TEST_EXECUTIONS = Math.Max(3, Mathf.FloorToInt(mainThreadTime / 0.1f));
        PARALLEL_MIN_ITEMS = Math.Max(100, Mathf.FloorToInt(1000f / (SystemInfo.processorCount * 0.5f)));
        Log(Level.Info, $"Initial baseline set: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, MAX_FRAME_TIME_MS={MAX_FRAME_TIME_MS:F3}, " +
            $"HIGH_FPS_THRESHOLD={HIGH_FPS_THRESHOLD:F3}, FPS_CHANGE_THRESHOLD={FPS_CHANGE_THRESHOLD:F3}, " +
            $"MIN_TEST_EXECUTIONS={MIN_TEST_EXECUTIONS}, PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS} (cores={SystemInfo.processorCount})",
            Category.Performance);
      }
      finally
      {
        testInputs.Dispose();
        testJobOutputs.Dispose();
        _intOutputPool.Release(testCoroutineOutputs);
      }
    }

    /// <summary>
    /// Establishes a performance baseline for a task by testing different execution methods.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input data type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="burstDelegate">Burst-compiled job delegate for batch processing.</param>
    /// <param name="nonBurstDelegate">Non-Burst delegate for batch processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="jobOutputs">Output data list for jobs.</param>
    /// <param name="coroutineOutputs">Output data list for coroutines.</param>
    /// <param name="batchSize">Batch size for processing.</param>
    /// <param name="isNetworked">Whether execution is networked.</param>
    /// <returns>Coroutine enumerator for asynchronous execution.</returns>
    private static IEnumerator EstablishBaseline<TInput, TOutput>(
        string uniqueId,
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate,
        Action<int, int, NativeArray<TInput>, List<TOutput>> nonBurstDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> jobOutputs,
        List<TOutput> coroutineOutputs,
        int batchSize,
        bool isNetworked)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      string testId = $"{uniqueId}_BaselineTest";
      int testItemCount = Math.Min(batchSize, GetDynamicBatchSize(10, 0.15f, testId));
      var testInputs = new NativeArray<TInput>(testItemCount, Allocator.TempJob);
      var testJobOutputs = new NativeList<TOutput>(testItemCount, Allocator.TempJob);
      var testCoroutineOutputs = new List<TOutput>();
      _executionCount.TryAdd(testId, 0);

      try
      {
        if (typeof(TInput) == typeof(int))
        {
          for (int i = 0; i < testInputs.Length; i++)
            testInputs[i] = (TInput)(object)i;
        }

        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> testBurstDelegate = burstDelegate ?? ((start, end, inputs, outputs) =>
        {
          for (int i = start; i < end; i++)
            outputs.Add(default);
        });
        Action<int, NativeArray<TInput>, NativeList<TOutput>> testBurstForDelegate = (index, inputs, outputs) =>
        {
          outputs.Add(default);
        };
        Action<int, int, NativeArray<TInput>, List<TOutput>> testNonBurstDelegate = nonBurstDelegate ?? ((start, end, inputs, outputs) =>
        {
          for (int i = start; i < end; i++)
            outputs.Add(default);
        });

        float mainThreadTime = 0f;
        float coroutineCostMs = 0f;
        float jobTime = 0f;
        int iterations = 0;
        int maxIterations = MIN_TEST_EXECUTIONS * 3;

        while (iterations < maxIterations)
        {
          bool isLowLoad = !isNetworked || TimeManagerInstance.Tick % 10 == 0;
          if (!isLowLoad)
          {
            yield return isNetworked ? AwaitNextFishNetTickAsync(0f) : null;
            continue;
          }

          int executionCount = _executionCount.AddOrUpdate(testId, 0, (_, count) => count + 1);

          if (executionCount % 3 == 0)
          {
            Metrics.TrackExecution($"{testId}_MainThread", () => RunMainThread(testNonBurstDelegate, testInputs, testCoroutineOutputs, 0, testInputs.Length), testInputs.Length);
            mainThreadTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_MainThread");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: MainThread iteration {executionCount}, time={mainThreadTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 3 == 1)
          {
            var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(testBurstDelegate, testBurstForDelegate, null, testInputs, testJobOutputs, default, 0, testInputs.Length, testInputs.Length, JobType.IJob);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJob", () => jobWrapper.Schedule(testInputs.Length), testInputs.Length);
            jobTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJob");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJob iteration {executionCount}, time={jobTime:F3}ms", Category.Performance);
#endif
          }
          else
          {
            yield return Metrics.TrackExecutionCoroutine($"{testId}_Coroutine", RunCoroutine(testNonBurstDelegate, testInputs, testCoroutineOutputs, 0, testInputs.Length, isNetworked), testInputs.Length);
            coroutineCostMs = (float)Metrics.GetAvgItemTimeMs($"{testId}_Coroutine");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: Coroutine iteration {executionCount}, time={coroutineCostMs:F3}ms", Category.Performance);
#endif
          }

          iterations++;
          if (iterations >= MIN_TEST_EXECUTIONS)
          {
            double jobVariance = CalculateMetricVariance($"{testId}_IJob");
            double coroutineVariance = CalculateMetricVariance($"{testId}_Coroutine");
            double mainThreadVariance = CalculateMetricVariance($"{testId}_MainThread");

            if (jobVariance < METRIC_STABILITY_THRESHOLD &&
                coroutineVariance < METRIC_STABILITY_THRESHOLD &&
                mainThreadVariance < METRIC_STABILITY_THRESHOLD)
            {
              _baselineEstablished.TryAdd(uniqueId, true);
              CalculateThresholds(uniqueId, testId, mainThreadTime, jobTime);
              Log(Level.Info, $"Baseline set for {uniqueId}: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, " +
                  $"MAX_FRAME_TIME_MS={MAX_FRAME_TIME_MS:F3}, HIGH_FPS_THRESHOLD={HIGH_FPS_THRESHOLD:F3}, " +
                  $"FPS_CHANGE_THRESHOLD={FPS_CHANGE_THRESHOLD:F3}, MIN_TEST_EXECUTIONS={MIN_TEST_EXECUTIONS}, " +
                  $"PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS} (cores={SystemInfo.processorCount})",
                  Category.Performance);
              break;
            }
          }

          yield return isNetworked ? AwaitNextFishNetTickAsync(0f) : null;
        }
      }
      finally
      {
        testInputs.Dispose();
        testJobOutputs.Dispose();
#if DEBUG
        Log(Level.Verbose, $"Cleaned up test resources for {testId}", Category.Performance);
#endif
      }
    }

    /// <summary>
    /// Calculates variance for a given method's performance metrics.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <returns>Variance of performance metrics, or double.MaxValue if insufficient data.</returns>
    private static double CalculateMetricVariance(string methodName)
    {
      if (!DynamicProfiler._batchSizeHistory.TryGetValue(methodName, out var history))
        return double.MaxValue;
      if (history.Count < MIN_TEST_EXECUTIONS)
        return double.MaxValue;
      double mean = history.Average();
      double variance = history.Sum(v => (v - mean) * (v - mean)) / history.Count;
#if DEBUG
      Log(Level.Verbose, $"Variance for {methodName}: {variance:F3}, samples={history.Count}", Category.Performance);
#endif
      return variance;
    }

    /// <summary>
    /// Calculates performance thresholds based on execution times.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="testId">Test identifier.</param>
    /// <param name="mainThreadTime">Main thread execution time per item in milliseconds.</param>
    /// <param name="jobTime">Job execution time per item in milliseconds.</param>
    private static void CalculateThresholds(string uniqueId, string testId, float mainThreadTime, float jobTime)
    {
      float iJobCost = MainThreadImpactTracker.GetJobOverhead($"{testId}_IJob");
      DEFAULT_THRESHOLD = Mathf.Max(50, Mathf.FloorToInt(1000f / Math.Max(1f, mainThreadTime)));
      MAX_FRAME_TIME_MS = Mathf.Clamp(mainThreadTime * 10, 0.5f, 2f);
      HIGH_FPS_THRESHOLD = Mathf.Clamp(1f / (60f + mainThreadTime * 1000f), 0.005f, 0.02f);
      FPS_CHANGE_THRESHOLD = Mathf.Clamp(mainThreadTime / 1000f, 0.1f, 0.3f);
      MIN_TEST_EXECUTIONS = Math.Max(3, Mathf.FloorToInt(mainThreadTime / 0.1f));
      PARALLEL_MIN_ITEMS = Math.Max(100, Mathf.FloorToInt(1000f / (SystemInfo.processorCount * 0.5f)));
      MetricsThresholds[uniqueId] = DEFAULT_THRESHOLD;
      MetricsThresholds[$"{uniqueId}_ParallelThreshold"] = PARALLEL_MIN_ITEMS;
#if DEBUG
      Log(Level.Verbose, $"Calculated thresholds for {uniqueId}: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS}, Cores={SystemInfo.processorCount}", Category.Performance);
#endif
    }

    /// <summary>
    /// Determines if parallel job execution should be used based on item count and costs.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <returns>True if parallel execution is preferred, false otherwise.</returns>
    private static bool ShouldUseParallel(string uniqueId, int itemCount)
    {
      int parallelThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_ParallelThreshold", PARALLEL_MIN_ITEMS);
      int largeDatasetThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_LargeDatasetThreshold", PARALLEL_MIN_ITEMS * 10);
      if (itemCount > largeDatasetThreshold)
      {
        Log(Level.Warning, $"Large dataset for {uniqueId}: itemCount={itemCount}. Using IJobParallelFor.", Category.Performance);
        return true;
      }
      if (itemCount < parallelThreshold)
        return false;
      float iJobCost = MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_IJob");
      float parallelCost = MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_IJobParallelFor");
      bool useParallel = parallelCost < iJobCost || parallelCost == 2f;
#if DEBUG
      Log(Level.Verbose, $"ShouldUseParallel: {uniqueId}, itemCount={itemCount}, parallelThreshold={parallelThreshold}, useParallel={useParallel} (iJobCost={iJobCost:F3}ms, parallelCost={parallelCost:F3}ms)", Category.Performance);
#endif
      return useParallel;
    }

    /// <summary>
    /// Saves performance baseline data to a JSON file.
    /// </summary>
    public static void SaveBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, fileName + $"{saveId}.json");
      var data = new BaselineData
      {
        MetricsThresholds = MetricsThresholds.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        DynamicProfilerAverages = DynamicProfiler._rollingAverages.ToDictionary(kvp => kvp.Key, kvp => new RollingAverageData
        {
          Samples = kvp.Value._samples.Take(kvp.Value.Count).ToArray(),
          Count = kvp.Value.Count,
          Sum = kvp.Value._sum
        }),
        BaselineEstablished = _baselineEstablished.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        MainThreadImpactAverages = MainThreadImpactTracker._impactAverages.ToDictionary(kvp => kvp.Key, kvp => new RollingAverageData
        {
          Samples = kvp.Value._samples.Take(kvp.Value.Count).ToArray(),
          Count = kvp.Value.Count,
          Sum = kvp.Value._sum
        }),
        BatchSizeHistory = DynamicProfiler._batchSizeHistory.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
        MetricsCache = Metrics._metrics.ToDictionary(kvp => kvp.Key, kvp => new CacheMetricData
        {
          CacheHits = kvp.Value.CacheHits,
          CacheMisses = kvp.Value.CacheMisses,
          CallCount = kvp.Value.CallCount,
          TotalTimeMs = kvp.Value.TotalTimeMs,
          MaxTimeMs = kvp.Value.MaxTimeMs,
          TotalMainThreadImpactMs = kvp.Value.TotalMainThreadImpactMs,
          MaxMainThreadImpactMs = kvp.Value.MaxMainThreadImpactMs,
          ItemCount = kvp.Value.ItemCount,
          AvgItemTimeMs = kvp.Value.AvgItemTimeMs,
          AvgMainThreadImpactMs = kvp.Value.AvgMainThreadImpactMs
        }),
        ExecutionCount = _executionCount.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        FirstRuns = _firstRuns.ToArray().Select(fr => fr.ToString()).ToList()
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
      File.WriteAllText(filePath, json);
      Log(Level.Info, $"Saved baseline data to {filePath}", Category.Performance);
    }

    /// <summary>
    /// Loads performance baseline data from a JSON file.
    /// </summary>
    public static void LoadBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, fileName + $"{saveId}.json");
      if (!File.Exists(filePath)) return;
      try
      {
        string json = File.ReadAllText(filePath);
        var data = JsonConvert.DeserializeObject<BaselineData>(json);
        _lastSavedDataHash = json.GetHashCode().ToString("X8");
        MetricsThresholds.Clear();
        foreach (var kvp in data.MetricsThresholds)
          MetricsThresholds.TryAdd(kvp.Key, kvp.Value);
        DynamicProfiler._rollingAverages.Clear();
        foreach (var kvp in data.DynamicProfilerAverages)
        {
          var avg = new DynamicProfiler.RollingAverage(100);
          for (int i = 0; i < kvp.Value.Count; i++)
            avg.AddSample(kvp.Value.Samples[i]);
          DynamicProfiler._rollingAverages.TryAdd(kvp.Key, avg);
          MetricsCache.AddPerformanceSample(kvp.Key, avg.GetAverage());
        }
        _baselineEstablished.Clear();
        foreach (var kvp in data.BaselineEstablished)
          _baselineEstablished.TryAdd(kvp.Key, kvp.Value);
        MainThreadImpactTracker._impactAverages.Clear();
        foreach (var kvp in data.MainThreadImpactAverages)
        {
          var avg = new DynamicProfiler.RollingAverage(100);
          for (int i = 0; i < kvp.Value.Count; i++)
            avg.AddSample(kvp.Value.Samples[i]);
          MainThreadImpactTracker._impactAverages.TryAdd(kvp.Key, avg);
          MetricsCache.AddImpactSample(kvp.Key, avg.GetAverage());
        }
        DynamicProfiler._batchSizeHistory.Clear();
        foreach (var kvp in data.BatchSizeHistory)
          DynamicProfiler._batchSizeHistory.TryAdd(kvp.Key, new List<int>(kvp.Value));
        Metrics._metrics.Clear();
        foreach (var kvp in data.MetricsCache)
        {
          Metrics._metrics.AddOrUpdate(kvp.Key, _ => new Metrics.Data
          {
            CacheHits = kvp.Value.CacheHits,
            CacheMisses = kvp.Value.CacheMisses,
            CallCount = kvp.Value.CallCount,
            TotalTimeMs = kvp.Value.TotalTimeMs,
            MaxTimeMs = kvp.Value.MaxTimeMs,
            TotalMainThreadImpactMs = kvp.Value.TotalMainThreadImpactMs,
            MaxMainThreadImpactMs = kvp.Value.MaxMainThreadImpactMs,
            ItemCount = kvp.Value.ItemCount,
            AvgItemTimeMs = kvp.Value.AvgItemTimeMs,
            AvgMainThreadImpactMs = kvp.Value.AvgMainThreadImpactMs
          }, (_, m) => new Metrics.Data
          {
            CacheHits = kvp.Value.CacheHits,
            CacheMisses = kvp.Value.CacheMisses,
            CallCount = kvp.Value.CallCount,
            TotalTimeMs = kvp.Value.TotalTimeMs,
            MaxTimeMs = kvp.Value.MaxTimeMs,
            TotalMainThreadImpactMs = kvp.Value.TotalMainThreadImpactMs,
            MaxMainThreadImpactMs = kvp.Value.MaxMainThreadImpactMs,
            ItemCount = kvp.Value.ItemCount,
            AvgItemTimeMs = kvp.Value.AvgItemTimeMs,
            AvgMainThreadImpactMs = kvp.Value.AvgMainThreadImpactMs
          });
        }
        _executionCount.Clear();
        foreach (var kvp in data.ExecutionCount)
          _executionCount.TryAdd(kvp.Key, kvp.Value);
        _firstRuns.Clear();
        foreach (var fr in data.FirstRuns)
          _firstRuns.Add(fr.ToString());
        foreach (var uniqueId in MetricsThresholds.Keys)
          ValidateBaseline(uniqueId);
        Log(Level.Info, $"Loaded baseline data from {filePath}", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to load baseline data from {filePath}: {ex.Message}", Category.Performance);
      }
    }

    /// <summary>
    /// Validates baseline data by checking for significant deviations.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    private static void ValidateBaseline(string uniqueId)
    {
      string mainThreadKey = $"{uniqueId}_MainThread";
      double savedAvg = Metrics.GetAvgItemTimeMs(mainThreadKey);
      double testAvg = 0;
      int testCount = 1;
      for (int i = 0; i < testCount; i++)
      {
        Metrics.TrackExecution(mainThreadKey, () => { }, 1);
        testAvg += Metrics.GetAvgItemTimeMs(mainThreadKey);
      }
      testAvg /= testCount;
      if (savedAvg > 0 && Math.Abs(savedAvg - testAvg) / savedAvg > 0.2)
      {
        MetricsThresholds.TryRemove(uniqueId, out _);
        _baselineEstablished.TryRemove(uniqueId, out _);
        _executionCount.TryRemove(uniqueId, out _);
        DynamicProfiler._rollingAverages.TryRemove($"{uniqueId}_IJob", out _);
        DynamicProfiler._rollingAverages.TryRemove($"{uniqueId}_Coroutine", out _);
        DynamicProfiler._rollingAverages.TryRemove(mainThreadKey, out _);
        MainThreadImpactTracker._impactAverages.TryRemove($"{uniqueId}_IJob", out _);
        MainThreadImpactTracker._impactAverages.TryRemove($"{uniqueId}_Coroutine", out _);
        MainThreadImpactTracker._impactAverages.TryRemove(mainThreadKey, out _);
        DynamicProfiler._batchSizeHistory.TryRemove(uniqueId, out _);
        Log(Level.Warning, $"Reset baseline for {uniqueId} due to significant deviation (saved={savedAvg:F3}ms, test={testAvg:F3}ms)", Category.Performance);
      }
    }

    /// <summary>
    /// Executes a task with dynamic selection of execution method (job, coroutine, or main thread).
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <param name="jobCallback">Job callback for non-Burst execution.</param>
    /// <param name="coroutineCallback">Coroutine callback.</param>
    /// <param name="mainThreadCallback">Main thread callback.</param>
    /// <returns>Coroutine enumerator for asynchronous execution.</returns>
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

      if (itemCount >= jobThreshold && jobCallback != null && MainThreadImpactTracker.GetJobOverhead(jobKey) <= Math.Min(mainThreadImpact, coroutineImpact))
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
    /// <param name="jobCallback">Job callback to execute.</param>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator RunJob(Action jobCallback)
    {
      jobCallback();
      yield return null;
    }

    /// <summary>
    /// Calculates the dynamic batch size for job execution.
    /// </summary>
    /// <param name="totalItems">Total number of items to process.</param>
    /// <param name="defaultAvgProcessingTimeMs">Default average processing time per item in milliseconds.</param>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="isParallel">Whether parallel execution is preferred.</param>
    /// <returns>Calculated batch size.</returns>
    internal static int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId, bool isParallel = false)
    {
      if (!isParallel) return totalItems;
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

    /// <summary>
    /// Serializes baseline data for storage.
    /// </summary>
    [Serializable]
    private class BaselineData
    {
      public Dictionary<string, int> MetricsThresholds;
      public Dictionary<string, RollingAverageData> DynamicProfilerAverages;
      public Dictionary<string, bool> BaselineEstablished;
      public Dictionary<string, RollingAverageData> MainThreadImpactAverages;
      public Dictionary<string, int[]> BatchSizeHistory;
      public Dictionary<string, CacheMetricData> MetricsCache;
      public Dictionary<string, int> ExecutionCount;
      public List<string> FirstRuns;
    }

    /// <summary>
    /// Serializes rolling average data for storage.
    /// </summary>
    [Serializable]
    private class RollingAverageData
    {
      public double[] Samples;
      public int Count;
      public double Sum;
    }

    /// <summary>
    /// Serializes cache metric data for storage.
    /// </summary>
    [Serializable]
    private class CacheMetricData
    {
      public long CacheHits;
      public long CacheMisses;
      public long CallCount;
      public double TotalTimeMs;
      public double MaxTimeMs;
      public double TotalMainThreadImpactMs;
      public double MaxMainThreadImpactMs;
      public long ItemCount;
      public double AvgItemTimeMs;
      public double AvgMainThreadImpactMs;
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

  /// <summary>
  /// Applies Harmony patches for performance-related functionality.
  /// </summary>
  public static class PerformanceHarmonyPatches
  {
    /// <summary>
    /// Postfix patch for SetupScreen.ClearFolderContents to reset baseline data.
    /// </summary>
    /// <param name="folderPath">Path to the folder being cleared.</param>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SetupScreen), "ClearFolderContents", new Type[] { typeof(string) })]
    private static void SetupScreen_ClearFolderContents(string folderPath)
    {
      try
      {
        SmartExecution.ResetBaselineData();
        Log(Level.Info, $"Triggered baseline reset from SetupScreen.ClearFolderContents for path {folderPath}", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to reset baseline in SetupScreen.ClearFolderContents: {ex.Message}", Category.Performance);
      }
    }

    /// <summary>
    /// Postfix patch for ImportScreen to reset baseline data on confirm.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ImportScreen), "Confirm")]
    private static void ImportScreen_Confirm()
    {
      try
      {
        SmartExecution.ResetBaselineData();
        Log(Level.Info, "Triggered baseline reset from ImportScreen.Confirm", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to reset baseline in ImportScreen.Confirm: {ex.Message}", Category.Performance);
      }
    }
  }
}