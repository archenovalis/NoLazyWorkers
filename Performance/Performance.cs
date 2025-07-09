using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using NoLazyWorkers.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using static NoLazyWorkers.Debug;
using Unity.Jobs;
using UnityEngine;
using ScheduleOne.Persistence;
using MelonLoader.Utils;
using Newtonsoft.Json;
using HarmonyLib;
using ScheduleOne.UI.MainMenu;
using UnityEngine.Jobs;
using Object = UnityEngine.Object;

namespace NoLazyWorkers.Performance
{
  internal class SmartExecuteAttribute : Attribute
  {
  }

  /// <summary>
  /// Defines types of jobs for scheduling in the performance system.
  /// </summary>
  internal enum JobType
  {
    Baseline,
    IJob, // single item
    IJobFor, // multiple items
    IJobParallelFor, // multiple items processed in parallel
    IJobParallelForTransform // multiple transforms processed in parallel
  }

  /// <summary>
  /// Interface for wrapping job scheduling and completion.
  /// </summary>
  internal interface IJobWrapper
  {
    /// <summary>
    /// Schedules the job with optional count and batch size.
    /// </summary>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Size of each batch for parallel processing.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    JobHandle Schedule(int count = 0, int batchSize = 64);

    /// <summary>
    /// Completes the job, disposing of resources if necessary.
    /// </summary>
    void Complete();

    /// <summary>
    /// Gets whether the job is processed in parallel.
    /// </summary>
    bool IsParallel { get; }

    /// <summary>
    /// Gets the type of job.
    /// </summary>
    JobType JobType { get; }
  }

  /// <summary>
  /// Wraps a single item job for scheduling.
  /// </summary>
  /// <typeparam name="T">The type of job, implementing IJob.</typeparam>
  internal struct JobWrapper<T> : IJobWrapper where T : struct, IJob
  {
    public T Job;
    public bool IsParallel => false;
    public JobType JobType => JobType.IJob;

    public JobWrapper(T job) => Job = job;

    /// <summary>
    /// Schedules a single item job.
    /// </summary>
    /// <param name="count">Ignored for single item jobs.</param>
    /// <param name="batchSize">Ignored for single item jobs.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    public JobHandle Schedule(int count = 0, int batchSize = 64) => Job.Schedule();

    /// <summary>
    /// Completes the job. No-op for single item jobs.
    /// </summary>
    public void Complete() { }
  }

  /// <summary>
  /// Wraps a parallel job for multiple items.
  /// </summary>
  /// <typeparam name="T">The type of job, implementing IJobParallelFor.</typeparam>
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

    /// <summary>
    /// Schedules a parallel job for multiple items.
    /// </summary>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Size of each batch for parallel processing.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    public JobHandle Schedule(int count = 0, int batchSize = 64) => Job.Schedule(_count, _batchSize);

    /// <summary>
    /// Completes the job. No-op for parallel jobs.
    /// </summary>
    public void Complete() { }
  }

  /// <summary>
  /// Executes a delegate-based job for processing input to output.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
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

    /// <summary>
    /// Executes the job, invoking the delegate with input and output arrays.
    /// </summary>
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
  /// Executes a delegate-based parallel job for processing input to output.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
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

    /// <summary>
    /// Executes the parallel job for a specific index range.
    /// </summary>
    /// <param name="index">The index of the batch to process.</param>
    public void Execute(int index)
    {
      int batchStart = StartIndex + index * BatchSize;
      int batchEnd = Math.Min(batchStart + BatchSize, Inputs.Length);
      Delegate.Invoke(batchStart, batchEnd, Inputs, Outputs);
    }
  }

  /// <summary>
  /// Executes a delegate-based job for processing individual items in parallel.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
  [BurstCompile]
  internal struct DelegateForJob<TInput, TOutput> : IJobParallelFor
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    [ReadOnly] public FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>>> Delegate;
    [ReadOnly] public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public int StartIndex;

    /// <summary>
    /// Executes the job for a specific index.
    /// </summary>
    /// <param name="index">The index of the item to process.</param>
    public void Execute(int index)
    {
      Delegate.Invoke(index + StartIndex, Inputs, Outputs);
    }
  }

  /// <summary>
  /// Executes a delegate-based parallel job for transform processing.
  /// </summary>
  [BurstCompile]
  internal struct DelegateParallelForTransformJob : IJobParallelForTransform
  {
    [ReadOnly] public FunctionPointer<Action<int, TransformAccess>> Delegate;
    public int StartIndex;
    public int BatchSize;

    /// <summary>
    /// Executes the transform job for a specific index.
    /// </summary>
    /// <param name="index">The index of the transform to process.</param>
    /// <param name="transform">The transform to process.</param>
    public void Execute(int index, TransformAccess transform)
    {
      Delegate.Invoke(index + StartIndex, transform);
    }
  }

  /// <summary>
  /// Wraps delegate-based jobs for flexible scheduling across job types.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
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

    /// <summary>
    /// Schedules the job based on the specified job type.
    /// </summary>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Size of each batch for parallel processing.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    public JobHandle Schedule(int count = 0, int batchSize = 64)
    {
      int effectiveCount = count > 0 ? count : _count;
      int effectiveBatchSize = batchSize > 0 ? batchSize : _batchSize;
#if DEBUG
      Log(Level.Verbose, $"Scheduling job for jobType={_jobType}, count={effectiveCount}, batchSize={effectiveBatchSize}", Category.Performance);
#endif
      switch (_jobType)
      {
        case JobType.Baseline:
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
        case JobType.IJobFor:
          var jobFor = new DelegateForJob<TInput, TOutput>
          {
            Delegate = _delegateFor,
            Inputs = _inputs,
            Outputs = _outputs,
            StartIndex = _startIndex
          };
          return jobFor.Schedule(effectiveCount, 1);
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

    /// <summary>
    /// Completes the job, disposing of transform resources if applicable.
    /// </summary>
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
  /// Tracks performance metrics for job execution and cache access.
  /// </summary>
  internal static class Metrics
  {
    internal static readonly ConcurrentDictionary<string, Data> _metrics = new();
    private static readonly Stopwatch _stopwatch = new();
    private static bool _isInitialized;

    /// <summary>
    /// Gets the average processing time per item for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The average time per item in milliseconds.</returns>
    public static float GetAvgItemTimeMs(string methodName) => MainThreadImpactTracker.GetAverageItemImpact(methodName);

    /// <summary>
    /// Initializes the metrics system and subscribes to tick updates.
    /// </summary>
    public static void Initialize()
    {
      if (_isInitialized || !IsServer) return;
      _isInitialized = true;
      TimeManagerInstance.OnTick += UpdateMetrics;
      Log(Level.Info, "PerformanceMetrics initialized", Category.Performance);
    }

    /// <summary>
    /// Job for tracking metrics of a single entity.
    /// </summary>
    [BurstCompile]
    public struct SingleEntityMetricsJob : IJob
    {
      public FixedString64Bytes TaskType;
      public float Timestamp;
      public NativeArray<Metric> Metrics;
      public NativeArray<CacheMetric> CacheMetrics;
      public int CacheIndex;

      /// <summary>
      /// Executes the metrics tracking job.
      /// </summary>
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

    internal static void TrackNonBurstIteration(string methodName, Action action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      double timeMs = _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
      DynamicProfiler.AddSample(methodName, timeMs / itemCount, isNonBurst: true);
      UpdateMetric(methodName, timeMs, mainThreadImpactMs, itemCount);
    }

    /// <summary>
    /// Tracks execution time of an action and updates metrics.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The action to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    [BurstCompile]
    internal static void TrackExecution(string methodName, Action action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
    }

    /// <summary>
    /// Tracks execution time of a function and updates metrics.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The function to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>The result of the function.</returns>
    [BurstCompile]
    internal static T TrackExecution<T>(string methodName, Func<T> action, int itemCount = 1)
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
    /// Tracks execution time of an async action and updates metrics.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The async action to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    internal static async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      await action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
    }

    /// <summary>
    /// Tracks execution time of an async function and updates metrics.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The async function to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>The result of the function.</returns>
    internal static async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      T result = await action();
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
      return result;
    }

    /// <summary>
    /// Tracks execution time of a coroutine and updates metrics.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="coroutine">The coroutine to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    internal static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)
    {
      var impact = MainThreadImpactTracker.BeginSample(methodName);
      _stopwatch.Restart();
      while (coroutine.MoveNext())
        yield return coroutine.Current;
      _stopwatch.Stop();
      float mainThreadImpactMs = MainThreadImpactTracker.EndSample(impact);
      UpdateMetric(methodName, _stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency, mainThreadImpactMs, itemCount);
    }

    /// <summary>
    /// Tracks execution time of a burst-compiled job.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
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
    /// Tracks cache access for a job and updates metrics.
    /// </summary>
    /// <param name="cacheName">The name of the cache.</param>
    /// <param name="isHit">Whether the cache access was a hit.</param>
    [BurstCompile]
    public static void TrackJobCacheAccess(string cacheName, bool isHit)
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
#if DEBUG
      Log(Level.Verbose, $"Cache access tracked for {cacheName}: isHit={isHit}", Category.Performance);
#endif
    }

    /// <summary>
    /// Tracks cache access and updates metrics.
    /// </summary>
    /// <param name="cacheName">The name of the cache.</param>
    /// <param name="isHit">Whether the cache access was a hit.</param>
    public static void TrackCacheAccess(string cacheName, bool isHit)
    {
      TrackJobCacheAccess(cacheName, isHit);
    }

    /// <summary>
    /// Updates performance metrics for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
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
    /// Updates and logs metrics for all tracked methods.
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
    /// Cleans up metrics data and unsubscribes from tick updates.
    /// </summary>
    public static void Cleanup()
    {
      TimeManagerInstance.OnTick -= UpdateMetrics;
      _metrics.Clear();
      _isInitialized = false;
      Log(Level.Info, "PerformanceMetrics cleaned up", Category.Performance);
    }
  }

  /// <summary>
  /// Tracks rolling averages for performance profiling.
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
      /// <param name="value">The sample value in milliseconds.</param>
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
      /// Gets the current rolling average.
      /// </summary>
      /// <returns>The average time in milliseconds.</returns>
      public double GetAverage()
      {
        return _count == 0 ? 0 : Math.Clamp(_sum / _count, MIN_AVG_TIME_MS, MAX_AVG_TIME_MS);
      }
    }

    /// <summary>
    /// Adds a performance sample for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
    /// <param name="isNonBurst">Burst flag. Default=false</param>
    internal static void AddSample(string methodName, double avgItemTimeMs, bool isNonBurst = false)
    {
      if (avgItemTimeMs <= 0) return;
      var avg = _rollingAverages.GetOrAdd(methodName, _ => new RollingAverage(WINDOW_SIZE));
      MetricsCache.AddPerformanceSample(methodName, avgItemTimeMs);
      avg.AddSample(avgItemTimeMs);
#if DEBUG
      Log(Level.Verbose, $"DynamicProfiler: {methodName} avgProcessingTimeMs={avgItemTimeMs:F3}ms (samples={avg.Count}, isNonBurst={isNonBurst})", Category.Performance);
#endif
    }

    /// <summary>
    /// Gets the dynamic average processing time for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="defaultTimeMs">Default time if no data exists.</param>
    /// <param name="isNonBurst">Burst flag. Default=false</param>
    /// <returns>The average processing time in milliseconds.</returns>
    internal static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f, bool isNonBurst = false)
    {
      RollingAverage rollingAvg = null;
      if (MetricsCache.TryGetAverage(methodName, out var avg) || _rollingAverages.TryGetValue(methodName, out rollingAvg))
      {
        float result = (float)(rollingAvg?.GetAverage() ?? avg);
#if DEBUG
        Log(Level.Verbose, $"DynamicProfiler: {methodName} avgProcessingTimeMs={result:F3}ms (samples={(rollingAvg?.Count ?? 0)}, isNonBurst={isNonBurst})", Category.Performance);
#endif
        return result;
      }
      return defaultTimeMs;
    }

    /// <summary>
    /// Adds a batch size to the history for a method.
    /// </summary>
    /// <param name="uniqueId">The unique identifier for the method.</param>
    /// <param name="batchSize">The batch size used.</param>
    internal static void AddBatchSize(string uniqueId, int batchSize)
    {
      var history = _batchSizeHistory.GetOrAdd(uniqueId, _ => new List<int>(BATCH_HISTORY_SIZE));
      if (history.Count >= BATCH_HISTORY_SIZE)
        history.RemoveAt(0);
      history.Add(batchSize);
    }

    /// <summary>
    /// Gets the average batch size for a method.
    /// </summary>
    /// <param name="uniqueId">The unique identifier for the method.</param>
    /// <returns>The average batch size.</returns>
    internal static int GetAverageBatchSize(string uniqueId)
    {
      var history = _batchSizeHistory.GetOrAdd(uniqueId, _ => new List<int>(BATCH_HISTORY_SIZE));
      if (history.Count == 0) return 0;
      double sum = 0;
      foreach (var size in history) sum += size;
      return Mathf.RoundToInt((float)(sum / history.Count));
    }

    /// <summary>
    /// Cleans up profiler data.
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
    /// Initializes the main thread impact tracker.
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
    /// Adds an impact sample for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="impactTimeMs">The impact time in milliseconds.</param>
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
    /// Gets the average main thread impact per item for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The average impact time in milliseconds.</returns>
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
    /// Gets the coroutine execution cost for a method.
    /// </summary>
    /// <param name="coroutineKey">The coroutine identifier.</param>
    /// <returns>The coroutine cost in milliseconds.</returns>
    public static float GetCoroutineCost(string coroutineKey)
    {
      float cost = GetAverageItemImpact(coroutineKey);
      return cost == float.MaxValue ? 0.2f : cost;
    }

    /// <summary>
    /// Gets the job scheduling overhead for a job.
    /// </summary>
    /// <param name="jobKey">The job identifier.</param>
    /// <returns>The job overhead in milliseconds.</returns>
    public static float GetJobOverhead(string jobKey)
    {
      float overhead = GetAverageItemImpact(jobKey);
      return overhead == float.MaxValue ? 2f : overhead;
    }

    /// <summary>
    /// Tracks a job's execution time using a stopwatch.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="scheduleJob">The function to schedule the job.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>An enumerator for the coroutine.</returns>
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
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The sample ID.</returns>
    public static int BeginSample(string methodName)
    {
      int id = Interlocked.Increment(ref _sampleId);
      _samples[id] = new ImpactSample { MethodName = methodName, StartTicks = _stopwatch.ElapsedTicks };
      return id;
    }

    /// <summary>
    /// Ends tracking a main thread sample and returns the impact time.
    /// </summary>
    /// <param name="sampleId">The sample ID.</param>
    /// <returns>The impact time in milliseconds.</returns>
    public static float EndSample(int sampleId)
    {
      if (!_samples.TryRemove(sampleId, out var sample)) return 0;
      float impactMs = (_stopwatch.ElapsedTicks - sample.StartTicks) * 1000f / Stopwatch.Frequency;
      var avg = _mainThreadImpacts.GetOrAdd(sample.MethodName, _ => new DynamicProfiler.RollingAverage(100));
      avg.AddSample(impactMs);
      return impactMs;
    }

    /// <summary>
    /// Updates performance thresholds based on metrics.
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
    /// Cleans up main thread impact tracking data.
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
    /// <param name="methodName">The name of the method.</param>
    /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
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
    /// <param name="methodName">The name of the method.</param>
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
    /// Tries to get the average performance time from the cache.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="avg">The average time in milliseconds.</param>
    /// <returns>True if the average was found, false otherwise.</returns>
    public static bool TryGetAverage(string methodName, out double avg)
    {
      return _averageCache.TryGetValue(methodName, out avg);
    }

    /// <summary>
    /// Tries to get the impact time from the cache.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="impact">The impact time in milliseconds.</param>
    /// <returns>True if the impact was found, false otherwise.</returns>
    public static bool TryGetImpact(string methodName, out double impact)
    {
      return _impactCache.TryGetValue(methodName, out impact);
    }

    /// <summary>
    /// Clears the performance and impact caches.
    /// </summary>
    public static void Cleanup()
    {
      _averageCache.Clear();
      _impactCache.Clear();
      Log(Level.Info, "CacheManager cleared", Category.Performance);
    }
  }

  /// <summary>
  /// Configuration options for smart execution.
  /// </summary>
  public struct SmartExecutionOptions
  {
    public bool IsPlayerVisible;

    /// <summary>
    /// Gets the default smart execution options.
    /// </summary>
    public static SmartExecutionOptions Default => new SmartExecutionOptions
    {
      IsPlayerVisible = false
    };
  }

  /// <summary>
  /// Manages optimized execution of jobs, coroutines, or main thread tasks.
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
    private static readonly ConcurrentDictionary<string, bool> _baselineEstablished = new();
    private static readonly ConcurrentDictionary<string, int> _executionCount = new();
    private const float METRIC_STABILITY_THRESHOLD = 0.05f;
    private const int STABILITY_WINDOW = 100;
    private const float STABILITY_VARIANCE_THRESHOLD = 0.001f;
    private const string fileName = "NoLazyWorkers_OptimizationData_";
    private static string _lastSavedDataHash;

    static SmartExecution()
    {
      int targetFrameRate = Mathf.Clamp(Application.targetFrameRate, 30, 120);
      TARGET_FRAME_TIME = targetFrameRate > 0 ? 1f / targetFrameRate : 1f / 60f;
      CoroutineRunner.Instance.RunCoroutine(TrackGCAllocationsCoroutine());
    }

    /// <summary>
    /// Initializes the smart execution system.
    /// </summary>
    public static void Initialize()
    {
      Metrics.Initialize();
      MainThreadImpactTracker.Initialize();
      CoroutineRunner.Instance.RunCoroutine(MonitorCpuStability());
      LoadBaselineData();
    }

    /// <summary>
    /// Executes a non-Burst loop with dynamic batch sizing and metrics-driven results processing.
    /// </summary>
    /// <typeparam name="TInput">Input data type (can be managed or unmanaged).</typeparam>
    /// <typeparam name="TOutput">Output data type (unmanaged for jobs).</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="nonBurstDelegate">Delegate for processing a batch of items.</param>
    /// <param name="burstResultsDelegate">Optional Burst-compatible delegate for results processing.</param>
    /// <param name="nonBurstResultsDelegate">Optional non-Burst delegate for results processing.</param>
    /// <param name="inputs">Input data collection.</param>
    /// <param name="outputs">Output data collection.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>Coroutine that yields during execution to spread load.</returns>
    public static IEnumerator Execute<TInput, TOutput>(
        string uniqueId,
        int itemCount,
        Action<int, int, TInput[], List<TOutput>> nonBurstDelegate,
        TInput[] inputs = default,
        List<TOutput> outputs = default,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        Action<List<TOutput>> burstResultsDelegate = null,
        SmartExecutionOptions options = default)
        where TOutput : unmanaged
    {
      if (itemCount <= 0 || nonBurstDelegate == null || inputs == null || inputs.Length < itemCount)
      {
        Log(Level.Warning, $"ExecuteNonBurst: Invalid input for {uniqueId}, itemCount={itemCount}, inputs={(inputs?.Length ?? 0)}", Category.Performance);
        yield break;
      }

      string jobKey = $"{uniqueId}_ResultsJob";
      string coroutineKey = $"{uniqueId}_NonBurstCoroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
      var stopwatch = Stopwatch.StartNew();
      var tempOutputs = new List<TOutput>(itemCount);
      int batchSize = GetDynamicBatchSize(itemCount, 0.15f, uniqueId, false);
      DynamicProfiler.AddBatchSize(uniqueId, batchSize);

      bool isFirstRun = _firstRuns.Add(uniqueId);
      if (!_baselineEstablished.ContainsKey(uniqueId) || isFirstRun)
      {
        yield return EstablishBaseline(uniqueId, nonBurstDelegate, inputs, tempOutputs, batchSize, options.IsPlayerVisible);
      }

      float mainThreadCost = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
      float coroutineCost = MainThreadImpactTracker.GetCoroutineCost(coroutineKey);
      bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;

      for (int i = 0; i < itemCount; i += batchSize)
      {
        int currentBatchSize = Math.Min(batchSize, itemCount - i);
        float batchCost = mainThreadCost * currentBatchSize;

        if (batchCost <= MAX_FRAME_TIME_MS && isHighFps)
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} batch {i / batchSize + 1} on main thread (batchSize={currentBatchSize}, cost={batchCost:F3}ms)", Category.Performance);
#endif
          Metrics.TrackExecution(mainThreadKey, () => nonBurstDelegate(i, currentBatchSize, inputs, tempOutputs), currentBatchSize);
        }
        else
        {
#if DEBUG
          Log(Level.Verbose, $"Executing {uniqueId} batch {i / batchSize + 1} via coroutine (batchSize={currentBatchSize}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
          yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(nonBurstDelegate, inputs, tempOutputs, i, currentBatchSize, options.IsPlayerVisible), currentBatchSize);
        }

        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return options.IsPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }

        _executionCount.AddOrUpdate(uniqueId, 1, (_, count) => count + 1);
      }

      // Process results based on metrics
      if (burstResultsDelegate != null || nonBurstResultsDelegate != null)
      {
        float jobOverhead = MainThreadImpactTracker.GetJobOverhead(jobKey);
        float resultsCost = Metrics.GetAvgItemTimeMs($"{uniqueId}_Results") * tempOutputs.Count;
        if (burstResultsDelegate != null && jobOverhead <= Math.Min(mainThreadCost, coroutineCost) && resultsCost > 0.05f)
        {
#if DEBUG
          Log(Level.Verbose, $"Processing {uniqueId} results via IJob (jobOverhead={jobOverhead:F3}ms, resultsCost={resultsCost:F3}ms)", Category.Performance);
#endif
          NativeList<TOutput> nativeOutputs = new NativeList<TOutput>(tempOutputs.Count, Allocator.TempJob);
          try
          {
            for (int i = 0; i < tempOutputs.Count; i++) nativeOutputs.Add(tempOutputs[i]);
            var jobWrapper = new DelegateJobWrapper<TOutput, int>(
                (start, end, inArray, outList) => burstResultsDelegate(tempOutputs),
                null, null, nativeOutputs, default, default, 0, 1, 1, JobType.IJob);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(), tempOutputs.Count);
          }
          finally
          {
            if (nativeOutputs.IsCreated) nativeOutputs.Dispose();
#if DEBUG
            Log(Level.Verbose, $"Cleaned up nativeOutputs for {uniqueId} results", Category.Performance);
#endif
          }
        }
        else
        {
          yield return ProcessResultsNonJob(uniqueId, burstResultsDelegate, nonBurstResultsDelegate, tempOutputs, options, mainThreadCost, coroutineCost, isHighFps);
        }
      }

      outputs?.AddRange(tempOutputs);
      Log(Level.Info, $"ExecuteNonBurst completed for {uniqueId}: items={itemCount}, batches={Mathf.CeilToInt((float)itemCount / batchSize)}", Category.Performance);
    }

    /// <summary>
    /// Runs a non-Burst coroutine with fine-grained yielding for load spreading.
    /// </summary>
    /// <typeparam name="TInput">Input data type.</typeparam>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="nonBurstDelegate">Delegate for processing a batch of items.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator RunCoroutine<TInput, TOutput>(
        Action<int, int, TInput[], List<TOutput>> nonBurstDelegate,
        TInput[] inputs,
        List<TOutput> outputs,
        int startIndex,
        int count,
        bool isPlayerVisible)
    {
      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count);

      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
        nonBurstDelegate(i, currentSubBatchSize, inputs, outputs);

        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Establishes baseline metrics for execution and results processing.
    /// </summary>
    /// <typeparam name="TInput">Input data type.</typeparam>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="nonBurstDelegate">Delegate for processing a batch of items.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements.</param>
    /// <returns>An enumerator for the baseline coroutine.</returns>
    private static IEnumerator EstablishBaseline<TInput, TOutput>(
        string uniqueId,
        Action<int, int, TInput[], List<TOutput>> nonBurstDelegate,
        TInput[] inputs,
        List<TOutput> outputs,
        int batchSize,
        bool isPlayerVisible)
    {
      string mainThreadKey = $"{uniqueId}_MainThread";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string resultsKey = $"{uniqueId}_Results";

      // Measure main-thread execution
      Metrics.TrackExecution(mainThreadKey, () => nonBurstDelegate(0, Math.Max(1, inputs.Length), inputs, outputs), 1);

      // Measure coroutine execution
      yield return Metrics.TrackExecutionCoroutine(coroutineKey, RunCoroutine(nonBurstDelegate, inputs, outputs, 0, Math.Max(1, inputs.Length), isPlayerVisible), 1);

      // Measure results processing (if applicable)
      if (outputs.Count > 0)
      {
        Action<List<TOutput>> dummyResultsDelegate = (_) => { };
        Metrics.TrackExecution(resultsKey, () => dummyResultsDelegate(outputs), outputs.Count);
      }

      _baselineEstablished.AddOrUpdate(uniqueId, true, (_, _) => true);
#if DEBUG
      Log(Level.Verbose, $"Established baseline for {uniqueId}: mainThread={MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey):F3}ms, coroutine={MainThreadImpactTracker.GetCoroutineCost(coroutineKey):F3}ms, results={Metrics.GetAvgItemTimeMs(resultsKey):F3}ms", Category.Performance);
#endif
    }

    /// <summary>
    /// Processes results using coroutine or main thread based on metrics, supporting both Burst and non-Burst delegates.
    /// </summary>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="burstResultsDelegate">Burst-compatible delegate for results processing.</param>
    /// <param name="nonBurstResultsDelegate">Non-Burst delegate for results processing.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="mainThreadCost">Main thread execution cost.</param>
    /// <param name="coroutineCost">Coroutine execution cost.</param>
    /// <param name="isHighFps">Whether the frame rate is high.</param>
    /// <returns>An enumerator for the results processing coroutine.</returns>
    private static IEnumerator ProcessResultsNonJob<TOutput>(
        string uniqueId,
        Action<List<TOutput>> burstResultsDelegate,
        Action<List<TOutput>> nonBurstResultsDelegate,
        List<TOutput> outputs,
        SmartExecutionOptions options,
        float mainThreadCost,
        float coroutineCost,
        bool isHighFps)
    {
      string resultsKey = $"{uniqueId}_Results";
      float resultsCost = Metrics.GetAvgItemTimeMs(resultsKey) * outputs.Count;
      var resultsDelegate = nonBurstResultsDelegate ?? burstResultsDelegate;

      if (resultsDelegate == null)
      {
        Log(Level.Warning, $"No results delegate provided for {uniqueId}", Category.Performance);
        yield break;
      }

      if (resultsCost <= MAX_FRAME_TIME_MS && isHighFps)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results on main thread (cost={resultsCost:F3}ms, highFps={isHighFps}, using={(nonBurstResultsDelegate != null ? "non-Burst" : "Burst")} delegate)", Category.Performance);
#endif
        Metrics.TrackExecution(resultsKey, () => resultsDelegate(outputs), outputs.Count);
      }
      else
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results via coroutine (cost={resultsCost:F3}ms, coroutineCost={coroutineCost:F3}ms, using={(nonBurstResultsDelegate != null ? "non-Burst" : "Burst")} delegate)", Category.Performance);
#endif
        yield return Metrics.TrackExecutionCoroutine(resultsKey, RunResultsCoroutine(resultsDelegate, outputs, options.IsPlayerVisible), outputs.Count);
      }
    }

    /// <summary>
    /// Runs a coroutine for processing results with fine-grained yielding across sub-batches.
    /// </summary>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="resultsDelegate">Delegate to process the results.</param>
    /// <param name="outputs">List of output items to process.</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator RunResultsCoroutine<TOutput>(
        Action<List<TOutput>> resultsDelegate,
        List<TOutput> outputs,
        bool isPlayerVisible)
    {
      if (resultsDelegate == null || outputs == null || outputs.Count == 0)
      {
        Log(Level.Warning, $"RunResultsCoroutine: Invalid input, delegate={resultsDelegate != null}, outputs={(outputs?.Count ?? 0)}", Category.Performance);
        yield break;
      }

      string resultsKey = $"{typeof(TOutput).Name}_Results";
      float resultsCost = Metrics.GetAvgItemTimeMs(resultsKey) * outputs.Count;
      var stopwatch = Stopwatch.StartNew();
      int batchSize = GetDynamicBatchSize(outputs.Count, 0.15f, resultsKey, false);
      DynamicProfiler.AddBatchSize(resultsKey, batchSize);

      // Execute in a single frame if cost is low
      if (resultsCost <= MAX_FRAME_TIME_MS && Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing results for {resultsKey} in single frame (cost={resultsCost:F3}ms, highFps={Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD})", Category.Performance);
#endif
        Metrics.TrackExecution(resultsKey, () => resultsDelegate(outputs), outputs.Count);
        yield break;
      }

      // Process in sub-batches to spread load
      for (int i = 0; i < outputs.Count; i += batchSize)
      {
        int currentBatchSize = Math.Min(batchSize, outputs.Count - i);
        List<TOutput> subBatch = outputs.GetRange(i, currentBatchSize);
#if DEBUG
        Log(Level.Verbose, $"Processing results batch {i / batchSize + 1} for {resultsKey} (batchSize={currentBatchSize})", Category.Performance);
#endif
        Metrics.TrackExecution(resultsKey, () => resultsDelegate(subBatch), currentBatchSize);

        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
#if DEBUG
          Log(Level.Verbose, $"Yielding for {resultsKey} after batch {i / batchSize + 1} (elapsed={stopwatch.ElapsedMilliseconds:F3}ms, isPlayerVisible={isPlayerVisible})", Category.Performance);
#endif
          yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Executes a single-item Burst-compiled job with simplified input handling.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the job, used for metrics and profiling.</param>
    /// <param name="burstDelegate">Delegate to process the single input, outputting to a NativeList.</param>
    /// <param name="input">Single input item to process.</param>
    /// <param name="outputs">Optional NativeList for outputs; created if not provided.</param>
    /// <param name="burstResultsDelegate">Optional delegate for processing results in Burst.</param>
    /// <param name="nonBurstResultsDelegate">Optional delegate for processing results without Burst.</param>
    /// <param name="options">Execution options, e.g., visibility settings.</param>
    /// <returns>An IEnumerator for coroutine execution, allowing frame distribution.</returns>
    [BurstCompile]
    public static IEnumerator ExecuteBurst<TInput, TOutput>(
        string uniqueId,
        Action<TInput, NativeList<TOutput>> burstDelegate,
        TInput input = default,
        NativeList<TOutput> outputs = default,
        Action<NativeList<TOutput>> burstResultsDelegate = null,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        SmartExecutionOptions options = default)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      if (burstDelegate == null)
      {
        Log(Level.Warning, $"SmartExecution.ExecuteBurst: No valid delegate for {uniqueId}", Category.Performance);
        yield break;
      }

      // Convert single input to NativeArray for job wrapper compatibility
      NativeArray<TInput> inputs = new NativeArray<TInput>(1, Allocator.TempJob);
      try
      {
        inputs[0] = input;
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> wrappedDelegate = (start, end, inArray, outList) =>
        {
          burstDelegate(inArray[0], outList);
          Log(Level.Verbose, $"Executed single-item delegate for {uniqueId}", Category.Performance);
        };
        yield return ExecuteBurstInternal(uniqueId, wrappedDelegate, burstResultsDelegate, inputs, outputs, nonBurstResultsDelegate, options);
      }
      finally
      {
        if (inputs.IsCreated)
        {
          inputs.Dispose();
#if DEBUG
          Log(Level.Verbose, $"Cleaned up input NativeArray for {uniqueId}", Category.Performance);
#endif
        }
      }
    }

    /// <summary>
    /// Executes a Burst-compiled job for a single item with metrics-driven results processing.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="burstDelegate">Delegate for Burst-compiled job execution.</param>
    /// <param name="burstResultsDelegate">Delegate for Burst-compiled results processing.</param>
    /// <param name="nonBurstResultsDelegate">Delegate for non-Burst results processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    private static IEnumerator ExecuteBurstInternal<TInput, TOutput>(
        string uniqueId,
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate,
        Action<NativeList<TOutput>> burstResultsDelegate,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        SmartExecutionOptions options = default)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      if (burstDelegate == null)
      {
        Log(Level.Warning, $"SmartExecution.Execute: No valid delegate for {uniqueId}", Category.Performance);
        yield break;
      }

      string jobKey = $"{uniqueId}_IJob";
      string resultsJobKey = $"{uniqueId}_ResultsJob";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
      NativeList<TOutput> nativeOutputs = new NativeList<TOutput>(1, Allocator.TempJob);

      try
      {
        if (outputs.IsCreated && outputs.Length > 0)
        {
          for (int i = 0; i < outputs.Length; i++) nativeOutputs.Add(outputs[i]);
        }

        yield return RunExecutionLoop(
            uniqueId,
            itemCount: 1,
            batchSize: 1,
            burstDelegateIJob: burstDelegate,
            inputs: inputs,
            outputs: nativeOutputs,
            options: options,
            executeAction: (batchStart, batchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps) =>
            {
              if (jobOverhead <= Math.Min(mainThreadCost, coroutineCost))
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via IJob (single item, jobOverhead={jobOverhead:F3}ms)", Category.Performance);
#endif
                var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(burstDelegate, null, null, inputs, nativeOutputs, default, batchStart, batchSize, batchSize, JobType.IJob);
                return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(), batchSize);
              }
              else if (coroutineCost <= mainThreadCost)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (single item, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
                return Metrics.TrackExecutionCoroutine(coroutineKey, RunBurstCoroutine(burstDelegate, inputs, nativeOutputs, batchStart, batchSize, options.IsPlayerVisible), batchSize);
              }
              else if (isHighFps)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via MainThread (single item, highFps={isHighFps})", Category.Performance);
#endif
                Metrics.TrackExecution(mainThreadKey, () => burstDelegate(0, 1, inputs, nativeOutputs), batchSize);
                return null;
              }
              else
              {
                Log(Level.Warning, $"No optimal execution path for {uniqueId}, yielding", Category.Performance);
                return options.IsPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
              }
            });

        if (burstResultsDelegate != null || nonBurstResultsDelegate != null)
        {
          float jobOverhead = MainThreadImpactTracker.GetJobOverhead(resultsJobKey);
          float mainThreadCost = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
          float coroutineCost = MainThreadImpactTracker.GetCoroutineCost(coroutineKey);
          float resultsCost = Metrics.GetAvgItemTimeMs($"{uniqueId}_Results") * nativeOutputs.Length;
          bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;

          List<TOutput> managedOutputs = new List<TOutput>(nativeOutputs.Length);
          for (int i = 0; i < nativeOutputs.Length; i++) managedOutputs.Add(nativeOutputs[i]);

          if (burstResultsDelegate != null && jobOverhead <= Math.Min(mainThreadCost, coroutineCost) && resultsCost > 0.05f)
          {
#if DEBUG
            Log(Level.Verbose, $"Processing {uniqueId} results via IJob (jobOverhead={jobOverhead:F3}ms, resultsCost={resultsCost:F3}ms)", Category.Performance);
#endif
            var jobWrapper = new DelegateJobWrapper<TOutput, int>(
                (start, end, inArray, outList) => burstResultsDelegate(nativeOutputs),
                null, null, nativeOutputs, default, default, 0, 1, 1, JobType.IJob);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch(resultsJobKey, () => jobWrapper.Schedule(), nativeOutputs.Length);
          }
          else
          {
            Action<List<TOutput>> resultsDelegate;
            if (nonBurstResultsDelegate != null)
            {
              resultsDelegate = nonBurstResultsDelegate;
            }
            else
            {
              NativeList<TOutput> tempNativeOutputs = new NativeList<TOutput>(managedOutputs.Count, Allocator.Temp);
              try
              {
                for (int i = 0; i < managedOutputs.Count; i++) tempNativeOutputs.Add(managedOutputs[i]);
#if DEBUG
                Log(Level.Verbose, $"Converted List<TOutput> to NativeList<TOutput> for {uniqueId} results (count={managedOutputs.Count})", Category.Performance);
#endif
                resultsDelegate = (_) => burstResultsDelegate(tempNativeOutputs);
              }
              finally
              {
                if (tempNativeOutputs.IsCreated) tempNativeOutputs.Dispose();
#if DEBUG
                Log(Level.Verbose, $"Cleaned up tempNativeOutputs for {uniqueId} results in non-job path", Category.Performance);
#endif
              }
            }
            yield return ProcessResultsNonJob(uniqueId, null, resultsDelegate, managedOutputs, options, mainThreadCost, coroutineCost, isHighFps);
          }
        }
      }
      finally
      {
        if (nativeOutputs.IsCreated) nativeOutputs.Dispose();
#if DEBUG
        Log(Level.Verbose, $"Cleaned up nativeOutputs for {uniqueId}", Category.Performance);
#endif
      }
    }

    /// <summary>
    /// Executes a Burst-compiled job for multiple items with metrics-driven results processing.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="burstForDelegate">Delegate for Burst-compiled job execution per item.</param>
    /// <param name="burstResultsDelegate">Delegate for Burst-compiled results processing.</param>
    /// <param name="nonBurstResultsDelegate">Delegate for non-Burst results processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    public static IEnumerator ExecuteBurstFor<TInput, TOutput>(
        string uniqueId,
        int itemCount,
        Action<int, NativeArray<TInput>, NativeList<TOutput>> burstForDelegate,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        Action<NativeList<TOutput>> burstResultsDelegate = null,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        SmartExecutionOptions options = default)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      if (itemCount <= 0 || burstForDelegate == null)
      {
        Log(Level.Warning, $"SmartExecution.ExecuteMultiple: Invalid input for {uniqueId}, itemCount={itemCount}", Category.Performance);
        yield break;
      }

      NativeList<TOutput> nativeOutputs = new NativeList<TOutput>(itemCount, Allocator.TempJob);

      try
      {
        if (outputs.IsCreated && outputs.Length > 0)
        {
          for (int i = 0; i < outputs.Length; i++) nativeOutputs.Add(outputs[i]);
        }

        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate = (start, end, inArray, outList) =>
        {
          for (int i = start; i < end; i++) burstForDelegate(i, inArray, outList);
        };

        string jobKey = $"{uniqueId}_IJob";
        string coroutineKey = $"{uniqueId}_Coroutine";
        string mainThreadKey = $"{uniqueId}_MainThread";

        yield return RunExecutionLoop(
            uniqueId,
            itemCount,
            GetDynamicBatchSize(itemCount, 0.15f, uniqueId),
            burstDelegateIJob: burstDelegate,
            burstDelegateFor: burstForDelegate,
            inputs: inputs,
            outputs: nativeOutputs,
            options: options,
            executeAction: (batchStart, batchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps) =>
            {
              float mainThreadBatchCost = mainThreadCost * batchSize;
              if (ShouldUseParallel(uniqueId, batchSize) && jobOverhead <= mainThreadBatchCost)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via IJobParallelFor (batchSize={batchSize}, jobOverhead={jobOverhead:F3}ms)", Category.Performance);
#endif
                var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(null, burstForDelegate, null, inputs, nativeOutputs, default, batchStart, batchSize, batchSize, JobType.IJobParallelFor);
                return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(batchSize, batchSize), batchSize);
              }
              else if (jobOverhead <= mainThreadBatchCost)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via IJobFor (batchSize={batchSize}, jobOverhead={jobOverhead:F3}ms)", Category.Performance);
#endif
                var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(null, burstForDelegate, null, inputs, nativeOutputs, default, batchStart, batchSize, batchSize, JobType.IJobFor);
                return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(batchSize, batchSize), batchSize);
              }
              else if (coroutineCost <= mainThreadBatchCost)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (batchSize={batchSize}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
                return Metrics.TrackExecutionCoroutine(coroutineKey, RunBurstCoroutine(burstDelegate, inputs, nativeOutputs, batchStart, batchSize, options.IsPlayerVisible), batchSize);
              }
              else if (mainThreadBatchCost <= MAX_FRAME_TIME_MS && isHighFps)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via MainThread (batchSize={batchSize}, cost={mainThreadBatchCost:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
                Metrics.TrackExecution(mainThreadKey, () =>
                        {
                          for (int j = batchStart; j < batchStart + batchSize; j++) burstForDelegate(j, inputs, nativeOutputs);
                        }, batchSize);
                return null;
              }
              else
              {
                Log(Level.Warning, $"No optimal execution path for {uniqueId}, yielding", Category.Performance);
                return options.IsPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
              }
            });

        if (burstResultsDelegate != null || nonBurstResultsDelegate != null)
        {
          float jobOverhead = MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_ResultsJob");
          float mainThreadCost = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
          float coroutineCost = MainThreadImpactTracker.GetCoroutineCost(coroutineKey);
          float resultsCost = Metrics.GetAvgItemTimeMs($"{uniqueId}_Results") * nativeOutputs.Length;
          bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;

          List<TOutput> managedOutputs = new List<TOutput>(nativeOutputs.Length);
          for (int i = 0; i < nativeOutputs.Length; i++) managedOutputs.Add(nativeOutputs[i]);

          if (burstResultsDelegate != null && jobOverhead <= Math.Min(mainThreadCost, coroutineCost) && resultsCost > 0.05f)
          {
#if DEBUG
            Log(Level.Verbose, $"Processing {uniqueId} results via IJob (jobOverhead={jobOverhead:F3}ms, resultsCost={resultsCost:F3}ms)", Category.Performance);
#endif
            var jobWrapper = new DelegateJobWrapper<TOutput, int>(
                (start, end, inArray, outList) => burstResultsDelegate(nativeOutputs),
                null, null, nativeOutputs, default, default, 0, 1, 1, JobType.IJob);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{uniqueId}_ResultsJob", () => jobWrapper.Schedule(), nativeOutputs.Length);
          }
          else
          {
            Action<List<TOutput>> resultsDelegate;
            if (nonBurstResultsDelegate != null)
            {
              resultsDelegate = nonBurstResultsDelegate;
            }
            else
            {
              NativeList<TOutput> tempNativeOutputs = new NativeList<TOutput>(managedOutputs.Count, Allocator.Temp);
              try
              {
                for (int i = 0; i < managedOutputs.Count; i++) tempNativeOutputs.Add(managedOutputs[i]);
#if DEBUG
                Log(Level.Verbose, $"Converted List<TOutput> to NativeList<TOutput> for {uniqueId} results (count={managedOutputs.Count})", Category.Performance);
#endif
                resultsDelegate = (_) => burstResultsDelegate(tempNativeOutputs);
              }
              finally
              {
                if (tempNativeOutputs.IsCreated) tempNativeOutputs.Dispose();
#if DEBUG
                Log(Level.Verbose, $"Cleaned up tempNativeOutputs for {uniqueId} results in non-job path", Category.Performance);
#endif
              }
            }
            yield return ProcessResultsNonJob(uniqueId, null, resultsDelegate, managedOutputs, options, mainThreadCost, coroutineCost, isHighFps);
          }
        }
      }
      finally
      {
        if (nativeOutputs.IsCreated) nativeOutputs.Dispose();
#if DEBUG
        Log(Level.Verbose, $"Cleaned up nativeOutputs for {uniqueId}", Category.Performance);
#endif
      }
    }

    /// <summary>
    /// Executes transform operations using the optimal execution path.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="transforms">Array of transforms to process.</param>
    /// <param name="burstTransformDelegate">Delegate for burst-compiled transform job.</param>
    /// <param name="burstMainThreadTransformDelegate">Delegate for main thread transform processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    public static IEnumerator ExecuteTransforms<TInput>(
        string uniqueId,
        TransformAccessArray transforms,
        Action<int, TransformAccess> burstTransformDelegate,
        Action<int, Transform> burstMainThreadTransformDelegate,
        NativeArray<TInput> inputs = default,
        SmartExecutionOptions options = default)
        where TInput : unmanaged
    {
      if (!transforms.isCreated || transforms.length == 0 || burstTransformDelegate == null || burstMainThreadTransformDelegate == null)
      {
        Log(Level.Warning, $"SmartExecution.ExecuteTransforms: Invalid input for {uniqueId}, transformCount={transforms.length}", Category.Performance);
        yield break;
      }
      yield return RunExecutionLoop<TInput, int>(
          uniqueId,
          itemCount: transforms.length,
          batchSize: GetDynamicBatchSize(transforms.length, 0.15f, uniqueId, true),
          transforms: transforms,
          burstTransformDelegate: burstTransformDelegate,
          burstMainThreadTransformDelegate: burstMainThreadTransformDelegate,
          inputs: inputs,
          outputs: default,
          options: options,
          executeAction: (batchStart, batchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps) =>
          {
            string jobKey = $"{uniqueId}_IJobParallelForTransform";
            string coroutineKey = $"{uniqueId}_Coroutine";
            string mainThreadKey = $"{uniqueId}_MainThread";
            float mainThreadBatchCost = mainThreadCost * batchSize;
            if (jobOverhead <= mainThreadBatchCost)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} via IJobParallelForTransform (batchSize={batchSize}, jobOverhead={jobOverhead:F3}ms)", Category.Performance);
#endif
              var jobWrapper = new DelegateJobWrapper<TInput, int>(null, null, burstTransformDelegate, inputs, default, transforms, batchStart, batchSize, batchSize, JobType.IJobParallelForTransform);
              return MainThreadImpactTracker.TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(batchSize, batchSize), batchSize);
            }
            else if (coroutineCost <= mainThreadBatchCost)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (batchSize={batchSize}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
              return Metrics.TrackExecutionCoroutine(coroutineKey, RunBurstTransformCoroutine(burstMainThreadTransformDelegate, transforms, batchStart, batchSize, options.IsPlayerVisible), batchSize);
            }
            else if (mainThreadBatchCost <= MAX_FRAME_TIME_MS && isHighFps)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} via MainThread (batchSize={batchSize}, cost={mainThreadBatchCost:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
              Metrics.TrackExecution(mainThreadKey, () =>
                    {
                      for (int j = batchStart; j < batchStart + batchSize; j++)
                        burstMainThreadTransformDelegate(j, transforms[j].transform);
                    }, batchSize);
              return null;
            }
            else
            {
              Log(Level.Warning, $"No optimal execution path for {uniqueId}, yielding", Category.Performance);
              return options.IsPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
            }
          });
      if (transforms.isCreated) transforms.Dispose();
    }

    /// <summary>
    /// Runs the execution loop for processing items.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="burstDelegateIJob">Delegate for IJob execution.</param>
    /// <param name="burstDelegateFor">Delegate for IJobFor execution.</param>
    /// <param name="burstTransformDelegate">Delegate for transform job execution.</param>
    /// <param name="burstMainThreadTransformDelegate">Delegate for main thread transform execution.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="transforms">Array of transforms.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="executeAction">Action to execute for each batch.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    private static IEnumerator RunExecutionLoop<TInput, TOutput>(
        string uniqueId,
        int itemCount,
        int batchSize,
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegateIJob = null,
        Action<int, NativeArray<TInput>, NativeList<TOutput>> burstDelegateFor = null,
        Action<int, TransformAccess> burstTransformDelegate = null,
        Action<int, Transform> burstMainThreadTransformDelegate = null,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        TransformAccessArray transforms = default,
        SmartExecutionOptions options = default,
        Func<int, int, JobType, float, float, float, bool, IEnumerator> executeAction = null)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      string jobKey = transforms.isCreated ? $"{uniqueId}_IJobParallelForTransform" : $"{uniqueId}_IJob";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
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
      if (!_baselineEstablished.ContainsKey(uniqueId) || isFirstRun)
      {
        if (transforms.isCreated)
          yield return EstablishBaselineTransform(uniqueId, transforms, burstTransformDelegate, burstMainThreadTransformDelegate, inputs, batchSize, options.IsPlayerVisible);
        else
          yield return EstablishBaselineBurst(uniqueId, burstDelegateIJob, burstDelegateFor, inputs, outputs, null, batchSize, options.IsPlayerVisible);
      }
      float mainThreadCost = MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey);
      float coroutineCost = MainThreadImpactTracker.GetCoroutineCost(coroutineKey);
      float jobOverhead = MainThreadImpactTracker.GetJobOverhead(jobKey);
      bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;
      for (int i = 0; i < itemCount; i += batchSize)
      {
        int currentBatchSize = Math.Min(batchSize, itemCount - i);
        JobType jobType = transforms.isCreated ? DetermineJobTypeExecutescope(uniqueId, currentBatchSize, true, mainThreadCost)
            : itemCount == 1 ? JobType.IJob
            : DetermineJobTypeExecutescope(uniqueId, currentBatchSize, false, mainThreadCost);
        var yieldInstruction = executeAction(i, currentBatchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps);
        if (yieldInstruction != null)
          yield return yieldInstruction;
        _executionCount.AddOrUpdate(uniqueId, 1, (_, count) => count + 1);
        yield return options.IsPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
      }
      if (outputs.IsCreated)
        outputs.Dispose();
    }

    /// <summary>
    /// Determines the job type for execution scope.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <param name="hasTransforms">Whether transforms are involved.</param>
    /// <param name="mainThreadCost">Main thread execution cost.</param>
    /// <returns>The selected job type.</returns>
    private static JobType DetermineJobTypeExecutescope(string uniqueId, int itemCount, bool hasTransforms, float mainThreadCost)
    {
      float avgItemTimeMs = Metrics.GetAvgItemTimeMs($"{uniqueId}_IJob");
      float parallelCost = MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_IJobParallelFor");
      int parallelThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_ParallelThreshold", PARALLEL_MIN_ITEMS);
      int transformThreshold = MetricsThresholds.GetOrAdd($"{uniqueId}_TransformThreshold", PARALLEL_MIN_ITEMS * 2);
      if (hasTransforms && itemCount > transformThreshold && avgItemTimeMs > 0.05f && parallelCost <= mainThreadCost)
      {
#if DEBUG
        Log(Level.Verbose, $"Selected IJobParallelForTransform for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
        return JobType.IJobParallelForTransform;
      }
      if (itemCount <= parallelThreshold && avgItemTimeMs > 0.02f)
      {
#if DEBUG
        Log(Level.Verbose, $"Selected IJobFor for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
        return JobType.IJobFor;
      }
      if (itemCount > parallelThreshold && avgItemTimeMs > 0.05f && SystemInfo.processorCount > 1 && parallelCost <= mainThreadCost)
      {
#if DEBUG
        Log(Level.Verbose, $"Selected IJobParallelFor for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}, cores={SystemInfo.processorCount}", Category.Performance);
#endif
        return JobType.IJobParallelFor;
      }
#if DEBUG
      Log(Level.Verbose, $"Selected IJob for {uniqueId}: itemCount={itemCount}, avgItemTimeMs={avgItemTimeMs:F3}", Category.Performance);
#endif
      return JobType.IJob;
    }

    /// <summary>
    /// Runs a coroutine for processing Burst-compiled items with metrics-driven yielding.
    /// </summary>
    /// <typeparam name="TInput">Input data type (unmanaged).</typeparam>
    /// <typeparam name="TOutput">Output data type (unmanaged).</typeparam>
    /// <param name="burstDelegate">Burst-compatible delegate for processing items.</param>
    /// <param name="inputs">Input data collection.</param>
    /// <param name="outputs">Output data collection to store results.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    /// <param name="isNetworked">Whether the operation is networked (affects metrics).</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements (determines yield type).</param>
    /// <returns>Coroutine that yields to spread load across frames.</returns>
    private static IEnumerator RunBurstCoroutine<TInput, TOutput>(
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> outputs,
        int startIndex,
        int count,
        bool isPlayerVisible)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      if (burstDelegate == null || !inputs.IsCreated || !outputs.IsCreated || count <= 0)
      {
        Log(Level.Warning, $"RunBurstCoroutine: Invalid input, delegate={burstDelegate != null}, inputs={inputs.IsCreated}, outputs={outputs.IsCreated}, count={count}", Category.Performance);
        yield break;
      }

#if DEBUG
      Log(Level.Verbose, $"RunBurstCoroutine: Using outputs (capacity={outputs.Capacity}, count={count})", Category.Performance);
#endif

      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count);
      string key = $"{typeof(TInput).Name}_{typeof(TOutput).Name}_BurstCoroutine";
      DynamicProfiler.AddBatchSize(key, subBatchSize);

      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
#if DEBUG
        Log(Level.Verbose, $"Processing batch {i / subBatchSize + 1} for {key} (startIndex={i}, batchSize={currentSubBatchSize})", Category.Performance);
#endif
        burstDelegate(i, i + currentSubBatchSize, inputs, outputs);

        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
#if DEBUG
          Log(Level.Verbose, $"Yielding for {key} after batch {i / subBatchSize + 1} (elapsed={stopwatch.ElapsedMilliseconds:F3}ms, isPlayerVisible={isPlayerVisible})", Category.Performance);
#endif
          yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Runs a coroutine for processing transforms.
    /// </summary>
    /// <param name="burstMainThreadTransformDelegate">Delegate for main thread transform processing.</param>
    /// <param name="transforms">Array of transforms.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of transforms to process.</param>
    /// <param name="isPlayerVisible">Whether the execution is networked.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator RunBurstTransformCoroutine(
        Action<int, Transform> burstMainThreadTransformDelegate,
        TransformAccessArray transforms,
        int startIndex,
        int count,
        bool isPlayerVisible)
    {
      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count);
      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
        for (int j = i; j < i + currentSubBatchSize; j++)
          burstMainThreadTransformDelegate(j, transforms[j].transform);
        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f) : null;
          stopwatch.Restart();
        }
      }
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
      yield return EstablishInitialBaselineBurst();
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

    /// <summary>
    /// Establishes initial performance baseline for execution types.
    /// </summary>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator EstablishInitialBaselineBurst()
    {
      string testId = "InitialBaselineTest";
      var testInputs = new NativeArray<int>(10, Allocator.TempJob);
      var testJobOutputs = new NativeList<int>(10, Allocator.TempJob);
      var testTransforms = new TransformAccessArray(10);
      var tempGameObjects = new GameObject[10];

      try
      {
        for (int i = 0; i < testInputs.Length; i++)
          testInputs[i] = i;

        for (int i = 0; i < 10; i++)
        {
          tempGameObjects[i] = new GameObject($"TestTransform_{i}");
          testTransforms.Add(tempGameObjects[i].transform);
        }

        Action<int, int, NativeArray<int>, NativeList<int>> burstDelegate = (start, end, inputs, outputs) =>
        {
          for (int i = start; i < end; i++)
            outputs.Add(inputs[i] * 2);
        };

        Action<int, TransformAccess> burstTransformDelegate = (index, transform) =>
        {
          transform.position += Vector3.one * 0.01f;
        };

        Action<int, Transform> burstMainThreadTransformDelegate = (index, transform) =>
        {
          transform.position += Vector3.one * 0.01f;
        };

        float mainThreadTime = 0.0f;
        float coroutineCostMs = 0.0f;
        float jobTime = 0.0f;
        float transformMainThreadTime = 0.0f;
        float transformCoroutineCostMs = 0.0f;
        float transformJobTime = 0.0f;

        // Test generic data processing
        Metrics.TrackExecution($"{testId}_MainThread", () => burstDelegate(0, testInputs.Length, testInputs, testJobOutputs), testInputs.Length);
        mainThreadTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_MainThread");

        yield return Metrics.TrackExecutionCoroutine($"{testId}_Coroutine", RunBurstCoroutine(burstDelegate, testInputs, default, 0, testInputs.Length, false), testInputs.Length);
        coroutineCostMs = (float)Metrics.GetAvgItemTimeMs($"{testId}_Coroutine");

        var jobWrapper = new DelegateJobWrapper<int, int>(burstDelegate, null, null, testInputs, testJobOutputs, default, 0, testInputs.Length, testInputs.Length, JobType.IJob);
        yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJob", () => jobWrapper.Schedule(), testInputs.Length);
        jobTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJob");

        // Test transform processing
        Metrics.TrackExecution($"{testId}_TransformMainThread", () =>
        {
          for (int i = 0; i < testTransforms.length; i++)
            burstMainThreadTransformDelegate(i, testTransforms[i].transform);
        }, testTransforms.length);
        transformMainThreadTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_TransformMainThread");

        yield return Metrics.TrackExecutionCoroutine($"{testId}_TransformCoroutine", RunBurstTransformCoroutine(burstMainThreadTransformDelegate, testTransforms, 0, testTransforms.length, false), testTransforms.length);
        transformCoroutineCostMs = (float)Metrics.GetAvgItemTimeMs($"{testId}_TransformCoroutine");

        var transformJobWrapper = new DelegateJobWrapper<int, int>(null, null, burstTransformDelegate, testInputs, default, testTransforms, 0, testTransforms.length, testTransforms.length, JobType.IJobParallelForTransform);
        yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJobParallelForTransform", () => transformJobWrapper.Schedule(testTransforms.length, testTransforms.length), testTransforms.length);
        transformJobTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJobParallelForTransform");

        // Use the maximum times to ensure conservative thresholds
        float maxMainThreadTime = Mathf.Max(mainThreadTime, transformMainThreadTime);
        float maxJobTime = Mathf.Max(jobTime, transformJobTime);

        DEFAULT_THRESHOLD = Mathf.Max(50, Mathf.FloorToInt(1000f / Math.Max(1f, maxMainThreadTime)));
        MAX_FRAME_TIME_MS = Mathf.Clamp(maxMainThreadTime * 10, 0.5f, 2f);
        HIGH_FPS_THRESHOLD = Mathf.Clamp(1f / (60f + maxMainThreadTime * 1000f), 0.005f, 0.02f);
        FPS_CHANGE_THRESHOLD = Mathf.Clamp(maxMainThreadTime / 1000f, 0.1f, 0.3f);
        MIN_TEST_EXECUTIONS = Math.Max(3, Mathf.FloorToInt(maxMainThreadTime / 0.1f));
        PARALLEL_MIN_ITEMS = Math.Max(100, Mathf.FloorToInt(1000f / (SystemInfo.processorCount * 0.5f)));
        Log(Level.Info, $"Initial baseline set: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, " +
            $"MAX_FRAME_TIME_MS={MAX_FRAME_TIME_MS:F3}, HIGH_FPS_THRESHOLD={HIGH_FPS_THRESHOLD:F3}, " +
            $"FPS_CHANGE_THRESHOLD={FPS_CHANGE_THRESHOLD:F3}, MIN_TEST_EXECUTIONS={MIN_TEST_EXECUTIONS}, " +
            $"PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS} (cores={SystemInfo.processorCount})",
            Category.Performance);
      }
      finally
      {
        testInputs.Dispose();
        testJobOutputs.Dispose();
        if (testTransforms.isCreated) testTransforms.Dispose();
        for (int i = 0; i < tempGameObjects.Length; i++)
          if (tempGameObjects[i] != null)
            UnityEngine.Object.DestroyImmediate(tempGameObjects[i]);
      }
    }

    /// <summary>
    /// Establishes baseline performance for generic data processing.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="burstDelegate">Delegate for IJob execution.</param>
    /// <param name="burstForDelegate">Delegate for IJobFor execution.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="jobOutputs">Output data list for jobs.</param>
    /// <param name="coroutineOutputs">Output data list for coroutines.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="isPlayerVisible">Whether the execution is networked.</param>
    /// <returns>An enumerator for the baseline coroutine.</returns>
    private static IEnumerator EstablishBaselineBurst<TInput, TOutput>(
        string uniqueId,
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate,
        Action<int, NativeArray<TInput>, NativeList<TOutput>> burstForDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> jobOutputs,
        List<TOutput> coroutineOutputs,
        int batchSize,
        bool isPlayerVisible)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      string testId = $"{uniqueId}_BaselineTest";
      int testItemCount = Math.Min(batchSize, GetDynamicBatchSize(10, 0.15f, testId));
      var testInputs = new NativeArray<TInput>(testItemCount, Allocator.TempJob);
      var testJobOutputs = new NativeList<TOutput>(testItemCount, Allocator.TempJob);
      _executionCount.TryAdd(testId, 0);

      try
      {
        if (typeof(TInput) == typeof(int))
        {
          for (int i = 0; i < testInputs.Length; i++)
            testInputs[i] = (TInput)(object)i;
        }

        Action<int, int, NativeArray<TInput>, NativeList<TOutput>> testBurstDelegate = burstDelegate ?? ((start, end, inputs, _) =>
        {
          for (int i = start; i < end; i++) { }
        });

        Action<int, NativeArray<TInput>, NativeList<TOutput>> testBurstForDelegate = burstForDelegate ?? ((index, inputs, _) => { });

        float mainThreadTime = 0.0f;
        float coroutineCostMs = 0.0f;
        float jobTime = 0.0f;
        float jobForTime = 0.0f;
        int iterations = 0;
        int maxIterations = MIN_TEST_EXECUTIONS * 4;

        while (iterations < maxIterations)
        {
          bool isLowLoad = !isPlayerVisible || TimeManagerInstance.Tick % 2 == 0;
          if (!isLowLoad)
          {
            yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f) : null;
            continue;
          }

          int executionCount = _executionCount.AddOrUpdate(testId, 1, (_, count) => count + 1);
          if (executionCount % 4 == 0)
          {
            Metrics.TrackExecution($"{testId}_MainThread", () => testBurstDelegate(0, testInputs.Length, testInputs, testJobOutputs), testInputs.Length);
            mainThreadTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_MainThread");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: MainThread iteration {executionCount}, time={mainThreadTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 4 == 1)
          {
            var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(testBurstDelegate, null, null, testInputs, testJobOutputs, default, 0, testItemCount, testItemCount, JobType.Baseline);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJob", () => jobWrapper.Schedule(), testItemCount);
            jobTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJob");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJob iteration {executionCount}, time={jobTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 4 == 2)
          {
            var jobWrapper = new DelegateJobWrapper<TInput, TOutput>(null, testBurstForDelegate, null, testInputs, testJobOutputs, default, 0, testItemCount, testItemCount, JobType.IJobFor);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJobFor", () => jobWrapper.Schedule(testItemCount, testItemCount), testItemCount);
            jobForTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJobFor");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJobFor iteration {executionCount}, time={jobForTime:F3}ms", Category.Performance);
#endif
          }
          else
          {
            yield return Metrics.TrackExecutionCoroutine($"{testId}_Coroutine", RunBurstCoroutine(testBurstDelegate, testInputs, default, 0, testInputs.Length, isPlayerVisible), testInputs.Length);
            coroutineCostMs = (float)Metrics.GetAvgItemTimeMs($"{testId}_Coroutine");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: Coroutine iteration {executionCount}, time={coroutineCostMs:F3}ms", Category.Performance);
#endif
          }

          iterations++;
          if (iterations >= MIN_TEST_EXECUTIONS)
          {
            double jobVariance = CalculateMetricVariance($"{testId}_IJob");
            double jobForVariance = CalculateMetricVariance($"{testId}_IJobFor");
            double coroutineVariance = CalculateMetricVariance($"{testId}_Coroutine");
            double mainThreadVariance = CalculateMetricVariance($"{testId}_MainThread");
            if (jobVariance < METRIC_STABILITY_THRESHOLD &&
                jobForVariance < METRIC_STABILITY_THRESHOLD &&
                coroutineVariance < METRIC_STABILITY_THRESHOLD &&
                mainThreadVariance < METRIC_STABILITY_THRESHOLD)
            {
              _baselineEstablished.TryAdd(uniqueId, true);
              CalculateThresholds(uniqueId, testId, mainThreadTime, Math.Max(jobTime, jobForTime));
              Log(Level.Info, $"Baseline set for {uniqueId}: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, " +
                  $"MAX_FRAME_TIME_MS={MAX_FRAME_TIME_MS:F3}, HIGH_FPS_THRESHOLD={HIGH_FPS_THRESHOLD:F3}, " +
                  $"FPS_CHANGE_THRESHOLD={FPS_CHANGE_THRESHOLD:F3}, MIN_TEST_EXECUTIONS={MIN_TEST_EXECUTIONS}, " +
                  $"PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS} (cores={SystemInfo.processorCount})",
                  Category.Performance);
              break;
            }
          }
          yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f) : null;
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
    /// Establishes baseline performance for transform operations.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="transforms">Array of transforms to process.</param>
    /// <param name="burstTransformDelegate">Delegate for burst-compiled transform job.</param>
    /// <param name="burstMainThreadTransformDelegate">Delegate for main thread transform processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="isPlayerVisible">Whether the execution is networked.</param>
    /// <returns>An enumerator for the baseline coroutine.</returns>
    private static IEnumerator EstablishBaselineTransform<TInput>(
        string uniqueId,
        TransformAccessArray transforms,
        Action<int, TransformAccess> burstTransformDelegate,
        Action<int, Transform> burstMainThreadTransformDelegate,
        NativeArray<TInput> inputs,
        int batchSize,
        bool isPlayerVisible)
        where TInput : unmanaged
    {
      string testId = $"{uniqueId}_BaselineTest";
      int testItemCount = Math.Min(batchSize, GetDynamicBatchSize(10, 0.15f, testId));
      var testTransforms = new TransformAccessArray(testItemCount);
      try
      {
        var tempGameObjects = new GameObject[testItemCount];
        for (int i = 0; i < testItemCount; i++)
        {
          tempGameObjects[i] = new GameObject($"TestTransform_{i}");
          testTransforms.Add(tempGameObjects[i].transform);
        }

        float mainThreadTime = 0.0f;
        float coroutineCostMs = 0.0f;
        float jobTime = 0.0f;
        int iterations = 0;
        int maxIterations = MIN_TEST_EXECUTIONS * 3;

        while (iterations < maxIterations)
        {
          bool isLowLoad = !isPlayerVisible || TimeManagerInstance.Tick % 2 == 0;
          if (!isLowLoad)
          {
            yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f) : null;
            continue;
          }

          int executionCount = _executionCount.AddOrUpdate(testId, 1, (_, count) => count + 1);
          if (executionCount % 3 == 0)
          {
            Metrics.TrackExecution($"{testId}_MainThread", () =>
            {
              for (int i = 0; i < testItemCount; i++)
                burstMainThreadTransformDelegate(i, testTransforms[i].transform);
            }, testItemCount);
            mainThreadTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_MainThread");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: MainThread iteration {executionCount}, time={mainThreadTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 3 == 1)
          {
            var jobWrapper = new DelegateJobWrapper<TInput, int>(null, null, burstTransformDelegate, inputs, default, testTransforms, 0, testItemCount, testItemCount, JobType.IJobParallelForTransform);
            yield return MainThreadImpactTracker.TrackJobWithStopwatch($"{testId}_IJobParallelForTransform", () => jobWrapper.Schedule(testItemCount, testItemCount), testItemCount);
            jobTime = (float)Metrics.GetAvgItemTimeMs($"{testId}_IJobParallelForTransform");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJobParallelForTransform iteration {executionCount}, time={jobTime:F3}ms", Category.Performance);
#endif
          }
          else
          {
            yield return Metrics.TrackExecutionCoroutine($"{testId}_Coroutine", RunBurstTransformCoroutine(burstMainThreadTransformDelegate, testTransforms, 0, testItemCount, isPlayerVisible), testItemCount);
            coroutineCostMs = (float)Metrics.GetAvgItemTimeMs($"{testId}_Coroutine");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: Coroutine iteration {executionCount}, time={coroutineCostMs:F3}ms", Category.Performance);
#endif
          }

          iterations++;
          if (iterations >= MIN_TEST_EXECUTIONS)
          {
            double jobVariance = CalculateMetricVariance($"{testId}_IJobParallelForTransform");
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
          yield return isPlayerVisible ? AwaitNextFishNetTickAsync(0f) : null;
        }

        for (int i = 0; i < testItemCount; i++)
          Object.DestroyImmediate(tempGameObjects[i]);
      }
      finally
      {
        if (testTransforms.isCreated) testTransforms.Dispose();
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
  /// Applies Harmony patches for performance-related functionality.
  /// </summary>
  [HarmonyPatch]
  public static class PerformanceHarmonyPatches
  {
    /// <summary>
    /// Postfix patch for SetupScreen.ClearFolderContents to reset baseline data.
    /// </summary>
    /// <param name="folderPath">Path to the folder being cleared.</param>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SetupScreen), "ClearFolderContents", new Type[] { typeof(string) })]
    public static void SetupScreen_ClearFolderContents(string folderPath)
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
    public static void ImportScreen_Confirm()
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