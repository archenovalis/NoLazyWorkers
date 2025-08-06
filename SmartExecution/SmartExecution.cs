using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using NoLazyWorkers.Extensions;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using static NoLazyWorkers.SmartExecution.SmartMetrics;
using static NoLazyWorkers.Debug;
using Unity.Jobs;
using UnityEngine;
using ScheduleOne.Persistence;
using MelonLoader.Utils;
using HarmonyLib;
using ScheduleOne.UI.MainMenu;
using UnityEngine.Jobs;
using Object = UnityEngine.Object;
using static NoLazyWorkers.Debug.Deferred;

namespace NoLazyWorkers.SmartExecution
{
  /// <summary>
  /// Defines types of jobs for unified job execution.
  /// </summary>
  internal enum JobType
  {
    Baseline,
    IJob,
    IJobFor,
    IJobParallelFor,
    IJobParallelForTransform
  }

  /// <summary>
  /// Base wrapper for managing job handles and disposal in a Burst-compatible manner.
  /// </summary>
  [BurstCompile]
  public struct UnifiedJobWrapperBase
  {
    public JobHandle JobHandle;
    private FunctionPointer<Action> _disposeAction;
    public bool IsCreated;

    /// <summary>
    /// Initializes a new UnifiedJobWrapperBase with a job handle and disposal delegate.
    /// </summary>
    /// <param name="handle">The job handle to manage.</param>
    /// <param name="disposeAction">Delegate for disposing resources.</param>
    public UnifiedJobWrapperBase(JobHandle handle, FunctionPointer<Action> disposeAction)
    {
      JobHandle = handle;
      _disposeAction = disposeAction;
      IsCreated = true;
    }

    /// <summary>
    /// Disposes the job wrapper, completing the job if necessary.
    /// </summary>
    [BurstCompile]
    public void Dispose()
    {
      if (!IsCreated) return;
      if (!JobHandle.IsCompleted) JobHandle.Complete();
      _disposeAction.Invoke();
      IsCreated = false;
    }
  }

  /// <summary>
  /// Generic wrapper for scheduling and managing Burst-compiled jobs with various execution types.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
  [BurstCompile]
  internal struct UnifiedJobWrapper<TInput, TOutput>
        where TInput : unmanaged
        where TOutput : unmanaged
  {
    private readonly JobType _jobType;
    private readonly FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> _actionIJob;
    private readonly FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> _actionFor;
    private readonly FunctionPointer<Action<int, TransformAccess, NativeList<LogEntry>>> _actionTransform;
    private readonly NativeArray<TInput> _inputs;
    private readonly NativeList<TOutput> _outputs;
    private readonly NativeList<LogEntry> _logs;
    private readonly TransformAccessArray _transforms;
    private readonly int _startIndex;
    private readonly int _count;
    private readonly int _batchSize;

    /// <summary>
    /// Initializes a new UnifiedJobWrapper with job execution parameters.
    /// </summary>
    /// <param name="actionIJob">Delegate for IJob execution.</param>
    /// <param name="actionFor">Delegate for IJobFor execution.</param>
    /// <param name="actionTransform">Delegate for transform job execution.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="logs">Log entries list.</param>
    /// <param name="transforms">Transform array.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Batch size for parallel jobs.</param>
    /// <param name="jobType">Type of job to execute.</param>
    public UnifiedJobWrapper(
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> actionIJob,
        Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> actionFor,
        Action<int, TransformAccess, NativeList<LogEntry>> actionTransform,
        NativeArray<TInput> inputs,
        NativeList<TOutput> outputs,
        NativeList<LogEntry> logs,
        TransformAccessArray transforms,
        int startIndex,
        int count,
        int batchSize,
        JobType jobType)
    {
      _jobType = jobType;
      _actionIJob = actionIJob != null ? BurstCompiler.CompileFunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>>(actionIJob) : default;
      _actionFor = actionFor != null ? BurstCompiler.CompileFunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>>(actionFor) : default;
      _actionTransform = actionTransform != null ? BurstCompiler.CompileFunctionPointer<Action<int, TransformAccess, NativeList<LogEntry>>>(actionTransform) : default;
      _inputs = inputs;
      _outputs = outputs;
      _logs = logs;
      _transforms = transforms;
      _startIndex = startIndex;
      _count = count;
      _batchSize = batchSize;
      _logs.Add(new(Level.Info, $"Created UnifiedJobWrapper for jobType={jobType}, count={count}, batchSize={batchSize}", Category.Performance));
    }

    /// <summary>
    /// Schedules the job based on the specified job type.
    /// </summary>
    /// <param name="count">Number of items to process. Default is 0 (uses instance count).</param>
    /// <param name="batchSize">Batch size for parallel jobs. Default is 64.</param>
    /// <returns>The scheduled job handle.</returns>
    [BurstCompile]
    public JobHandle Schedule(int count = 0, int batchSize = 64)
    {
      int effectiveCount = count > 0 ? count : _count;
      int effectiveBatchSize = batchSize > 0 && effectiveCount % batchSize == 0 ? batchSize : _batchSize;
#if DEBUG
      _logs.Add(new(Level.Verbose, $"Scheduling job for jobType={_jobType}, count={effectiveCount}, batchSize={effectiveBatchSize}", Category.Performance));
#endif
      switch (_jobType)
      {
        case JobType.IJob:
          var job = new ActionJob<TInput, TOutput>
          {
            Action = _actionIJob,
            Inputs = _inputs,
            Outputs = _outputs,
            Logs = _logs,
            StartIndex = _startIndex,
            Count = effectiveCount
          };
          return job.Schedule();
        case JobType.IJobFor:
          var jobFor = new ActionForJob<TInput, TOutput>
          {
            Action = _actionFor,
            Inputs = _inputs,
            Outputs = _outputs,
            Logs = _logs,
            StartIndex = _startIndex
          };
          return jobFor.Schedule(effectiveCount, 1);
        case JobType.IJobParallelFor:
          var jobParallelFor = new ActionParallelJob<TInput, TOutput>
          {
            Action = _actionIJob,
            Inputs = _inputs,
            Outputs = _outputs,
            Logs = _logs,
            StartIndex = _startIndex,
            BatchSize = effectiveBatchSize
          };
          return jobParallelFor.Schedule(effectiveCount, effectiveBatchSize);
        case JobType.IJobParallelForTransform:
          var transformJob = new ActionParallelForTransformJob
          {
            Action = _actionTransform,
            StartIndex = _startIndex,
            BatchSize = effectiveBatchSize,
            Logs = _logs
          };
          return transformJob.Schedule(_transforms);
        default:
          throw new ArgumentException($"Invalid job type: {_jobType}");
      }
    }

    /// <summary>
    /// Disposes of native resources used by the job wrapper.
    /// </summary>
    [BurstCompile]
    public void Dispose()
    {
      if (_inputs.IsCreated)
      {
        _inputs.Dispose();
      }
      if (_outputs.IsCreated)
      {
        _outputs.Dispose();
      }
      if (_transforms.isCreated)
      {
        _transforms.Dispose();
      }
      if (_logs.IsCreated)
      {
        _logs.Add(new(Level.Info, $"Disposed UnifiedJobWrapper resources for jobType={_jobType}", Category.Performance));
        ProcessLogs(_logs);
        _logs.Dispose();
      }
    }
  }

  /// <summary>
  /// Executes a delegate-based job for processing input to output.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
  [BurstCompile]
  internal struct ActionJob<TInput, TOutput> : IJob
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    [ReadOnly] public FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> Action;
    [ReadOnly] public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public NativeList<LogEntry> Logs;
    public int StartIndex;
    public int Count;

    /// <summary>
    /// Executes the job using the provided delegate.
    /// </summary>
    public void Execute()
    {
      Action.Invoke(StartIndex, StartIndex + Count, Inputs, Outputs, Logs);
    }
  }

  /// <summary>
  /// Executes a delegate-based parallel job for processing input to output.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
  [BurstCompile]
  internal struct ActionParallelJob<TInput, TOutput> : IJobParallelFor
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    [ReadOnly] public FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> Action;
    [ReadOnly] public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public NativeList<LogEntry> Logs;
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
      Action.Invoke(batchStart, batchEnd, Inputs, Outputs, Logs);
    }
  }

  /// <summary>
  /// Executes a delegate-based job for processing individual items sequentially.
  /// </summary>
  /// <typeparam name="TInput">Unmanaged input type.</typeparam>
  /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
  [BurstCompile]
  internal struct ActionForJob<TInput, TOutput> : IJobParallelFor
      where TInput : unmanaged
      where TOutput : unmanaged
  {
    [ReadOnly] public FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> Action;
    [ReadOnly] public NativeArray<TInput> Inputs;
    public NativeList<TOutput> Outputs;
    public NativeList<LogEntry> Logs;
    public int StartIndex;

    /// <summary>
    /// Executes the job for a specific index.
    /// </summary>
    /// <param name="index">The index of the item to process.</param>
    public void Execute(int index)
    {
      Action.Invoke(index + StartIndex, Inputs, Outputs, Logs);
    }
  }

  /// <summary>
  /// Executes a delegate-based parallel job for transform processing.
  /// </summary>
  [BurstCompile]
  internal struct ActionParallelForTransformJob : IJobParallelForTransform
  {
    [ReadOnly] public FunctionPointer<Action<int, TransformAccess, NativeList<LogEntry>>> Action;
    public int StartIndex;
    public int BatchSize;
    public NativeList<LogEntry> Logs;

    /// <summary>
    /// Executes the transform job for a specific index.
    /// </summary>
    /// <param name="index">The index of the transform to process.</param>
    /// <param name="transform">The transform to process.</param>
    public void Execute(int index, TransformAccess transform)
    {
      Action.Invoke(index + StartIndex, transform, Logs);
    }
  }

  /// <summary>
  /// Configuration options for smart execution.
  /// </summary>
  public struct SmartOptions
  {
    public bool IsPlayerVisible;
    public bool IsTaskSafe;

    /// <summary>
    /// Gets the default smart execution options.
    /// </summary>
    public static SmartOptions Default => new SmartOptions
    {
      IsPlayerVisible = false,
      IsTaskSafe = false
    };
  }

  /// <summary>
  /// Manages optimized execution of jobs, coroutines, or main thread tasks.
  /// </summary>
  public static partial class Smart
  {
    private static readonly ConcurrentDictionary<string, bool> _baselineEstablished = new();
    private static readonly ConcurrentDictionary<string, int> _executionCount = new();
    private static readonly HashSet<string> _firstRuns = new();
    private static readonly ConcurrentDictionary<string, int> _burstExecutionCount = new();
    private static readonly ConcurrentDictionary<string, bool> _burstBaselineEstablished = new();
    private static readonly NativeParallelHashSet<FixedString64Bytes> _burstFirstRuns = new(100, Allocator.Persistent);
    private static readonly NativeParallelHashMap<JobHandle, UnifiedJobWrapperBase> _pendingJobs = new(100, Allocator.Persistent);

    private static long _lastGCMemory;
    public static readonly float TARGET_FRAME_TIME;
    private const string fileName = "NoLazyWorkers_OptimizationData_";
    private static string _lastSavedDataHash;
    private static int _activeThreads;

    /// <summary>
    /// Initializes the SmartExecution system and starts monitoring coroutines.
    /// </summary>
    static Smart()
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
      InitializeMetrics();
      CoroutineRunner.Instance.RunCoroutine(MonitorCpuStability());
      CoroutineRunner.Instance.RunCoroutine(MonitorThreadUsage());
      LoadBaselineData();
    }

    /// <summary>
    /// Cleans up resources and pending jobs in the SmartExecution system.
    /// </summary>
    public static void Cleanup()
    {
      foreach (var kvp in _pendingJobs)
      {
        if (!kvp.Value.IsCreated) continue;
        if (!kvp.Key.IsCompleted)
        {
          kvp.Key.Complete();
          Log(Level.Warning, $"Forced completion of pending job: {kvp.Key}", Category.Performance);
        }
        kvp.Value.Dispose();
      }
      _pendingJobs.Clear();
      SmartMetrics.Cleanup();
      _baselineEstablished.Clear();
      _executionCount.Clear();
      _firstRuns.Clear();
      _burstExecutionCount.Clear();
      _burstBaselineEstablished.Clear();
      if (_burstFirstRuns.IsCreated) _burstFirstRuns.Dispose();
      Log(Level.Info, "SmartExecution cleaned up", Category.Performance);
    }

    /// <summary>
    /// Monitors thread usage to detect oversubscription.
    /// </summary>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator MonitorThreadUsage()
    {
      while (true)
      {
        ThreadPool.GetAvailableThreads(out int workerThreads, out int _);
        _activeThreads = SystemInfo.processorCount - workerThreads + _pendingJobs.Count();
        if (_activeThreads > SystemInfo.processorCount)
        {
          Log(Level.Warning, $"Thread oversubscription detected: activeThreads={_activeThreads}, cores={SystemInfo.processorCount}", Category.Performance);
        }
        yield return new WaitForSeconds(1f);
      }
    }

    /// <summary>
    /// Checks if an action is task-safe by testing Unity object access.
    /// </summary>
    /// <param name="key">The identifier for the action.</param>
    /// <param name="action">The action to test.</param>
    /// <param name="uniqueId">Unique identifier for logging.</param>
    /// <param name="logs">List for logging errors.</param>
    /// <returns>True if the action is task-safe, false otherwise.</returns>
    private static bool CheckTaskSafety(string key, Action action, string uniqueId, List<LogEntry> logs)
    {
      try
      {
        var testGameObject = new GameObject("TestTaskSafetyObject");
        try
        {
          var task = Task.Run(() =>
          {
            try { testGameObject.transform.position = Vector3.zero; }
            catch { }
            action();
          });
          task.Wait();
          return true;
        }
        finally
        {
          Object.DestroyImmediate(testGameObject);
        }
      }
      catch (Exception ex)
      {
        logs.Add(new LogEntry
        {
          Level = Level.Error,
          Message = $"Thread safety violation detected for {uniqueId} ({key}): {ex.Message}. Consider setting IsTaskSafe = false.",
          Category = Category.Performance
        });
        return false;
      }
    }

    /// <summary>
    /// Processes results using coroutine or main thread based on metrics.
    /// </summary>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="resultsAction">Delegate to process the results.</param>
    /// <param name="managedOutputs">List of output items.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="mainThreadCost">Main thread execution cost.</param>
    /// <param name="coroutineCost">Coroutine execution cost.</param>
    /// <param name="isHighFps">Whether the frame rate is high.</param>
    /// <returns>An enumerator for the results processing coroutine.</returns>
    private static IEnumerator ProcessResults<TOutput>(
            string uniqueId,
            Action<List<TOutput>> resultsAction,
            List<TOutput> managedOutputs,
            SmartOptions options,
            float mainThreadCost,
            float coroutineCost,
            bool isHighFps)
    {
      string resultsKey = $"{uniqueId}_Results";
      string taskKey = $"{uniqueId}_ResultsTask";
      float resultsCost = GetAvgItemTimeMs(resultsKey) * managedOutputs.Count;
      float taskOverhead = GetTaskOverhead(taskKey);
      if (resultsAction == null)
      {
        Log(Level.Warning, $"No results delegate provided for {uniqueId}", Category.Performance);
        yield break;
      }
      bool isTaskSafe = options.IsTaskSafe;
#if DEBUG
      if (TryGetThreadSafety(taskKey, out var cachedTaskSafe) && !cachedTaskSafe && options.IsTaskSafe)
      {
        Log(Level.Warning, $"Thread safety warning for {uniqueId} results: Delegate may not be task-safe.", Category.Performance);
        isTaskSafe = false;
      }
#endif
      if (isTaskSafe && taskOverhead < mainThreadCost * managedOutputs.Count && _activeThreads < SystemInfo.processorCount)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results via Task (cost={resultsCost:F3}ms, taskOverhead={taskOverhead:F3}ms)", Category.Performance);
#endif
        yield return TrackExecutionTaskAsync(taskKey, async () =>
        {
          if (Thread.CurrentThread.ManagedThreadId == 1)
            Log(Level.Warning, $"Task for {taskKey} executed on main thread", Category.Performance);
          resultsAction(managedOutputs);
        }, managedOutputs.Count).ConfigureAwait(false);
      }
      else if (resultsCost <= MAX_FRAME_TIME_MS && isHighFps)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results on main thread (cost={resultsCost:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
        TrackExecutionMetrics(resultsKey, () => resultsAction(managedOutputs), managedOutputs.Count);
      }
      else
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results via coroutine (cost={resultsCost:F3}ms, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
        yield return TrackExecutionCoroutine(resultsKey, RunResultsCoroutine(resultsAction, managedOutputs, options.IsPlayerVisible), managedOutputs.Count);
      }
    }

    /// <summary>
    /// Executes a non-Burst job with dynamic batching and results processing.
    /// </summary>
    /// <typeparam name="TInput">Input data type.</typeparam>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="action">Delegate for processing a batch of items.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="resultsAction">Delegate for processing results.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    public static IEnumerator Execute<TInput, TOutput>(
            string uniqueId,
            int itemCount,
            Action<int, int, TInput[], List<TOutput>> action,
            TInput[] inputs = null,
            List<TOutput> outputs = null,
            Action<List<TOutput>> resultsAction = null,
            SmartOptions options = default)
    {
      if (itemCount <= 0 || action == null || inputs == null || inputs.Length < itemCount)
      {
        Log(Level.Warning, $"ExecuteNonNullable: Invalid input for {uniqueId}, itemCount={itemCount}, inputs={(inputs?.Length ?? 0)}", Category.Performance);
        yield break;
      }

      string coroutineKey = $"{uniqueId}_NonBurstCoroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
      string taskKey = $"{uniqueId}_Task";
      var tempOutputs = outputs ?? new List<TOutput>(itemCount);
      int batchSize = CalculateBatchSize(itemCount, 0.15f, uniqueId);

      yield return RunExecutionLoop(
          uniqueId,
          itemCount,
          action,
          inputs,
          tempOutputs,
          resultsAction,
          options,
          batchSize,
          (batchStart, currentBatchSize, mainThreadCost, coroutineCost, taskCost, isHighFps, isTaskSafe) =>
          {
            float batchImpact = mainThreadCost * currentBatchSize;
            float taskOverhead = GetTaskOverhead(taskKey);
            // Dynamic switch: Task if safe, low overhead, and threads free; main if low impact/high FPS; else coroutine with yielding.
            if (isTaskSafe && taskOverhead < batchImpact && _activeThreads < SystemInfo.processorCount)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} batch {batchStart / currentBatchSize + 1} via Task (batchSize={currentBatchSize}, taskOverhead={taskOverhead:F3}ms, activeThreads={_activeThreads})", Category.Performance);
#endif
              Interlocked.Increment(ref _activeThreads);
              return TaskToCoroutine(TrackExecutionTaskAsync(taskKey, () => Task.Run(() =>
                  {
                    try
                    {
                      action(batchStart, currentBatchSize, inputs, tempOutputs);
                    }
                    finally
                    {
                      Interlocked.Decrement(ref _activeThreads);
                    }
                  }), currentBatchSize));
            }
            else if (batchImpact <= MAX_FRAME_TIME_MS && isHighFps)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} batch {batchStart / currentBatchSize + 1} on main thread (batchSize={currentBatchSize}, cost={batchImpact:F3}ms)", Category.Performance);
#endif
              TrackExecution(mainThreadKey, () => action(batchStart, currentBatchSize, inputs, tempOutputs), currentBatchSize);
              return null;
            }
            else
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} batch {batchStart / currentBatchSize + 1} via coroutine (batchSize={currentBatchSize}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
              return TrackExecutionCoroutine(coroutineKey, RunCoroutine(action, inputs, tempOutputs, batchStart, currentBatchSize, options.IsPlayerVisible), currentBatchSize);
            }
          });

      if (outputs != tempOutputs)
        outputs?.AddRange(tempOutputs);

      Log(Level.Info, $"ExecuteNonNullable completed for {uniqueId}: items={itemCount}, batches={Mathf.CeilToInt((float)itemCount / batchSize)}", Category.Performance);
    }

    /// <summary>
    /// Converts a Task to a coroutine for Unity integration.
    /// </summary>
    /// <param name="task">The task to convert.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator TaskToCoroutine(Task task)
    {
      while (!task.IsCompleted)
        yield return null;
      if (task.IsFaulted)
        throw task.Exception;
    }

    /// <summary>
    /// Runs a non-Burst coroutine with fine-grained yielding for load spreading.
    /// </summary>
    /// <typeparam name="TInput">Input data type.</typeparam>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="action">Delegate for processing a batch of items.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator RunCoroutine<TInput, TOutput>(
            Action<int, int, TInput[], List<TOutput>> action,
            TInput[] inputs,
            List<TOutput> outputs,
            int startIndex,
            int count,
            bool isPlayerVisible)
    {
      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count); // Dynamic sub-batching for yielding within coroutine.
      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
        action(i, currentSubBatchSize, inputs, outputs);

        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return isPlayerVisible ? AwaitNextTick(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Tracks execution metrics for a non-Burst action.
    /// </summary>
    /// <param name="key">The identifier for the action.</param>
    /// <param name="action">The action to measure.</param>
    /// <param name="itemCount">Number of items processed.</param>
    private static void TrackExecutionMetrics(string key, Action action, int itemCount)
    {
      var stopwatch = Stopwatch.StartNew();
      action();
      stopwatch.Stop();
      double impactTimeMs = stopwatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
      AddImpactSample(key, itemCount > 0 ? impactTimeMs / itemCount : impactTimeMs);
    }

    /// <summary>
    /// Calculates the optimal batch size based on performance metrics.
    /// </summary>
    /// <param name="totalItems">Total number of items to process.</param>
    /// <param name="defaultAvgProcessingTimeMs">Default average processing time in milliseconds.</param>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <returns>The calculated batch size.</returns>
    private static int CalculateBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId)
    {
      float avgProcessingTimeMs = GetDynamicAvgProcessingTimeMs(uniqueId, defaultAvgProcessingTimeMs);
      int avgBatchSize = GetAverageBatchSize(uniqueId);
      float targetFrameTimeMs = Mathf.Min(16.666f, Time.deltaTime * 1000f);
      int calculatedBatchSize = Mathf.Max(1, Mathf.RoundToInt(targetFrameTimeMs / avgProcessingTimeMs));
      if (avgBatchSize > 0)
      {
        calculatedBatchSize = Mathf.RoundToInt((calculatedBatchSize + avgBatchSize) / 2f);
      }
      return Mathf.Min(totalItems, calculatedBatchSize);
    }

    /// <summary>
    /// Runs the execution loop with dynamic batching and execution path selection.
    /// </summary>
    /// <typeparam name="TInput">Input data type.</typeparam>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="action">Delegate for processing a batch of items.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="resultsAction">Delegate for processing results.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="executeAction">Delegate to execute a batch.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    private static IEnumerator RunExecutionLoop<TInput, TOutput>(
            string uniqueId,
            int itemCount,
            Action<int, int, TInput[], List<TOutput>> action,
            TInput[] inputs,
            List<TOutput> outputs,
            Action<List<TOutput>> resultsAction,
            SmartOptions options,
            int batchSize,
            Func<int, int, float, float, float, bool, bool, IEnumerator> executeAction)
    {
      string coroutineKey = $"{uniqueId}_NonBurstCoroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
      string taskKey = $"{uniqueId}_Task";

      AddBatchSize(uniqueId, batchSize);
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
        yield return EstablishBaseline(uniqueId, action, resultsAction, inputs, outputs, batchSize, options);
      }
      float mainThreadCost = GetAverageItemImpact(mainThreadKey);
      float coroutineCost = GetCoroutineCost(coroutineKey);
      float taskCost = GetTaskCost(taskKey); // Execution cost, not overhead
      bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;
      bool isTaskSafe = options.IsTaskSafe;

#if DEBUG
      if (TryGetThreadSafety(taskKey, out var cachedTaskSafe) && !cachedTaskSafe && options.IsTaskSafe)
      {
        Log(Level.Warning, $"Thread safety warning for {uniqueId}: Delegate may not be task-safe. Consider setting IsTaskSafe = false.", Category.Performance);
        isTaskSafe = false;
      }
#endif

      var stopwatch = Stopwatch.StartNew();
      for (int i = 0; i < itemCount; i += batchSize)
      {
        int currentBatchSize = Math.Min(batchSize, itemCount - i);
        var yieldInstruction = executeAction(i, currentBatchSize, mainThreadCost, coroutineCost, taskCost, isHighFps, isTaskSafe);
        if (yieldInstruction != null)
          yield return yieldInstruction;

        _executionCount.AddOrUpdate(uniqueId, 1, (_, count) => count + 1);
        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return options.IsPlayerVisible ? AwaitNextTick(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }

      if (resultsAction != null)
      {
        yield return ProcessResults(uniqueId, resultsAction, outputs, options, mainThreadCost, coroutineCost, isHighFps);
      }
    }

    /// <summary>
    /// Establishes baseline performance for non-Burst execution.
    /// </summary>
    /// <typeparam name="TInput">Input data type.</typeparam>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="action">Delegate for processing a batch of items.</param>
    /// <param name="resultsAction">Delegate for processing results.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the baseline coroutine.</returns>
    private static IEnumerator EstablishBaseline<TInput, TOutput>(
        string uniqueId,
        Action<int, int, TInput[], List<TOutput>> action,
        Action<List<TOutput>> resultsAction,
        TInput[] inputs,
        List<TOutput> outputs,
        int batchSize,
        SmartOptions options)
    {
      ValidateBaseline(uniqueId);
      string mainThreadKey = $"{uniqueId}_MainThread";
      string coroutineKey = $"{uniqueId}_NonBurstCoroutine";
      string resultsKey = $"{uniqueId}_Results";
      string taskKey = $"{uniqueId}_Task";
      List<LogEntry> logs = new();
      TrackExecution(mainThreadKey, () => action(0, Math.Max(1, inputs.Length), inputs, outputs), 1);
      yield return TrackExecutionCoroutine(coroutineKey, RunCoroutine(action, inputs, outputs, 0, Math.Max(1, inputs.Length), options.IsPlayerVisible), 1);
      if (options.IsTaskSafe)
      {
        var taskOverheadStopwatch = Stopwatch.StartNew();
        var task = Task.Run(() => { });
        taskOverheadStopwatch.Stop();
        double taskOverheadMs = taskOverheadStopwatch.Elapsed.TotalMilliseconds;
        AddTaskOverheadSample(taskKey, taskOverheadMs);

#if DEBUG
        bool isTaskSafe = CheckTaskSafety(taskKey, () =>
        {
          if (Thread.CurrentThread.ManagedThreadId == 1)
            logs.Add(new LogEntry
            {
              Level = Level.Warning,
              Message = $"Task for {taskKey} executed on main thread",
              Category = Category.Performance
            });
          action(0, Math.Min(1, inputs.Length), inputs, outputs);
        }, uniqueId, logs);
        AddThreadSafetySample(taskKey, isTaskSafe);
        if (isTaskSafe)
        {
          yield return TrackExecutionTaskAsync(taskKey, async () =>
          {
            await Task.Run(() =>
                    {
                      if (Thread.CurrentThread.ManagedThreadId == 1)
                        logs.Add(new LogEntry
                        {
                          Level = Level.Warning,
                          Message = $"Task for {taskKey} executed on main thread",
                          Category = Category.Performance
                        });
                      action(0, Math.Min(1, inputs.Length), inputs, outputs);
                    }).ConfigureAwait(false);
          }, 1);
        }
#endif
      }
      if (outputs.Count > 0 && resultsAction != null)
      {
        TrackExecutionMetrics(resultsKey, () => resultsAction(outputs), outputs.Count);
#if DEBUG
        bool isResultsTaskSafe = CheckTaskSafety(resultsKey, () => resultsAction(outputs), uniqueId, logs);
        AddThreadSafetySample(resultsKey, isResultsTaskSafe);
#endif
      }
      _baselineEstablished.AddOrUpdate(uniqueId, true, (_, _) => true);
#if DEBUG
      Log(Level.Verbose, $"Established baseline for {uniqueId}: mainThread={GetAverageItemImpact(mainThreadKey):F3}ms, coroutine={GetCoroutineCost(coroutineKey):F3}ms, results={GetAvgItemTimeMs(resultsKey):F3}ms, taskSafe={(TryGetThreadSafety(taskKey, out var safe) ? safe : false)}", Category.Performance);
      foreach (var log in logs)
        Log(log.Level, log.Message.ToString(), log.Category);
#endif
    }

    /// <summary>
    /// Processes results using coroutine or main thread based on metrics, supporting both Burst and non-Burst delegates.
    /// </summary>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="burstResultsAction">Burst-compatible delegate for results processing.</param>
    /// <param name="nonBurstResultsAction">Non-Burst delegate for results processing.</param>
    /// <param name="managedOutputs">Output data list.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="mainThreadCost">Main thread execution cost.</param>
    /// <param name="coroutineCost">Coroutine execution cost.</param>
    /// <param name="isHighFps">Whether the frame rate is high.</param>
    /// <returns>An enumerator for the results processing coroutine.</returns>
    private static IEnumerator ProcessBurstResultsNonJob<TOutput>(
        string uniqueId,
        Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsAction,
        Action<List<TOutput>> nonBurstResultsAction,
        List<TOutput> managedOutputs,
        SmartOptions options,
        float mainThreadCost,
        float coroutineCost,
        bool isHighFps)
        where TOutput : unmanaged
    {
      string resultsKey = $"{uniqueId}_Results";
      string taskKey = $"{uniqueId}_ResultsTask";
      float resultsCost = GetAvgItemTimeMs(resultsKey) * managedOutputs.Count;
      float taskOverhead = GetTaskOverhead(taskKey);
      var resultsAction = nonBurstResultsAction ?? (burstResultsAction != null ? (_) => { } : null);

      if (resultsAction == null && burstResultsAction == null)
      {
        Log(Level.Warning, $"No results delegate provided for {uniqueId}", Category.Performance);
        yield break;
      }

      bool isTaskSafe = options.IsTaskSafe;
#if DEBUG
      if (TryGetThreadSafety(taskKey, out var cachedTaskSafe) && !cachedTaskSafe && options.IsTaskSafe)
      {
        Log(Level.Warning, $"Thread safety warning for {uniqueId} results: Delegate may not be task-safe.", Category.Performance);
        isTaskSafe = false;
      }
#endif
      if (burstResultsAction != null && GetSchedulingOverhead($"{uniqueId}_ResultsJob") < mainThreadCost * managedOutputs.Count && resultsCost > 0.05f)
      {
        NativeList<TOutput> nativeOutputs = new NativeList<TOutput>(managedOutputs.Count, Allocator.TempJob);
        NativeList<LogEntry> logs = new NativeList<LogEntry>(10, Allocator.TempJob);
        try
        {
          for (int i = 0; i < managedOutputs.Count; i++) nativeOutputs.Add(managedOutputs[i]);
#if DEBUG
          Log(Level.Verbose, $"Processing {uniqueId} results via IJob (schedulingOverhead={GetSchedulingOverhead($"{uniqueId}_ResultsJob"):F3}ms, resultsCost={resultsCost:F3}ms)", Category.Performance);
#endif
          var jobWrapper = new UnifiedJobWrapper<TOutput, int>(
              (start, end, inArray, outList, logs) => burstResultsAction(nativeOutputs, logs),
              null, null, nativeOutputs, default, logs, default, 0, 1, 1, JobType.IJob);
          yield return TrackJobWithStopwatch($"{uniqueId}_ResultsJob", () => jobWrapper.Schedule(), nativeOutputs.Length);
        }
        finally
        {
          if (nativeOutputs.IsCreated) nativeOutputs.Dispose();
          if (logs.IsCreated) logs.Dispose();
        }
      }
      else if (isTaskSafe && taskOverhead < mainThreadCost * managedOutputs.Count && _activeThreads < SystemInfo.processorCount)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results via Task (cost={resultsCost:F3}ms, taskOverhead={taskOverhead:F3}ms)", Category.Performance);
#endif
        yield return TrackExecutionTaskAsync(taskKey, async () =>
        {
          if (Thread.CurrentThread.ManagedThreadId == 1)
            Log(Level.Warning, $"Task for {taskKey} executed on main thread", Category.Performance);
          resultsAction(managedOutputs);
        }, managedOutputs.Count).ConfigureAwait(false);
      }
      else if (resultsCost <= MAX_FRAME_TIME_MS && isHighFps)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results on main thread (cost={resultsCost:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
        TrackExecution(resultsKey, () => resultsAction(managedOutputs), managedOutputs.Count);
      }
      else
      {
#if DEBUG
        Log(Level.Verbose, $"Processing {uniqueId} results via coroutine (cost={resultsCost:F3}ms, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
        yield return TrackExecutionCoroutine(resultsKey, RunResultsCoroutine(resultsAction, managedOutputs, options.IsPlayerVisible), managedOutputs.Count);
      }
    }

    /// <summary>
    /// Runs a coroutine for processing results with fine-grained yielding across sub-batches.
    /// </summary>
    /// <typeparam name="TOutput">Output data type.</typeparam>
    /// <param name="resultsAction">Delegate to process the results.</param>
    /// <param name="outputs">List of output items to process.</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator RunResultsCoroutine<TOutput>(
            Action<List<TOutput>> resultsAction,
            List<TOutput> outputs,
            bool isPlayerVisible)
    {
      if (resultsAction == null || outputs == null || outputs.Count == 0)
      {
        Log(Level.Warning, $"RunResultsCoroutine: Invalid input, delegate={resultsAction != null}, outputs={(outputs?.Count ?? 0)}", Category.Performance);
        yield break;
      }

      string resultsKey = $"{typeof(TOutput).Name}_Results";
      float resultsCost = GetAvgItemTimeMs(resultsKey) * outputs.Count;
      var stopwatch = Stopwatch.StartNew();
      int batchSize = GetDynamicBatchSize(outputs.Count, 0.15f, resultsKey, false);
      AddBatchSize(resultsKey, batchSize);
      if (resultsCost <= MAX_FRAME_TIME_MS && Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD)
      {
#if DEBUG
        Log(Level.Verbose, $"Processing results for {resultsKey} in single frame (cost={resultsCost:F3}ms, highFps={Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD})", Category.Performance);
#endif
        TrackExecution(resultsKey, () => resultsAction(outputs), outputs.Count);
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
        TrackExecution(resultsKey, () => resultsAction(subBatch), currentBatchSize);
        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
#if DEBUG
          Log(Level.Verbose, $"Yielding for {resultsKey} after batch {i / batchSize + 1} (elapsed={stopwatch.ElapsedMilliseconds:F3}ms, isPlayerVisible={isPlayerVisible})", Category.Performance);
#endif
          yield return isPlayerVisible ? AwaitNextTick(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Executes a Burst-compiled job for a single item with metrics-driven results processing.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <typeparam name="TStruct">Struct type for job execution.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="burstAction">Delegate for Burst-compiled job execution.</param>
    /// <param name="input">Single input item.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="burstResultsAction">Delegate for Burst-compiled results processing.</param>
    /// <param name="nonBurstResultsAction">Delegate for non-Burst results processing.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    [BurstCompile]
    public static IEnumerator ExecuteBurst<TInput, TOutput, TStruct>(
            string uniqueId,
            Action<TInput, NativeList<TOutput>, NativeList<LogEntry>> burstAction,
            TInput input,
            NativeList<TOutput> outputs,
            NativeList<LogEntry> logs,
            Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsAction = null,
            Action<List<TOutput>> nonBurstResultsAction = null,
            SmartOptions options = default)
            where TInput : unmanaged
            where TOutput : unmanaged
            where TStruct : struct
    {
      if (burstAction == null)
      {
        Log(Level.Warning, $"SmartExecution.ExecuteBurst: No valid delegate for {uniqueId}", Category.Performance);
        yield break;
      }
      NativeArray<TInput> inputs = new NativeArray<TInput>(1, Allocator.TempJob);
      try
      {
        inputs[0] = input;
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> wrappedAction = (start, end, inArray, outList, logs) =>
        {
          burstAction(inArray[0], outList, logs);
          Log(Level.Verbose, $"Executed single-item delegate for {uniqueId}", Category.Performance);
        };
        yield return ExecuteBurstInternal(uniqueId, wrappedAction, burstResultsAction, inputs, outputs, logs, nonBurstResultsAction, options);
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
    /// Internal method for executing Burst-compiled jobs with dynamic execution path selection.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="burstAction">Delegate for Burst-compiled job execution.</param>
    /// <param name="burstResultsAction">Delegate for Burst-compiled results processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="nonBurstResultsAction">Delegate for non-Burst results processing.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    [BurstCompile]
    private static IEnumerator ExecuteBurstInternal<TInput, TOutput>(
        string uniqueId,
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstAction,
        Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsAction,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        NativeList<LogEntry> logs = default,
        Action<List<TOutput>> nonBurstResultsAction = null,
        SmartOptions options = default)
        where TInput : unmanaged
        where TOutput : unmanaged
    {
      if (burstAction == null)
      {
        logs.Add(new(Level.Warning, $"SmartExecution.Execute: No valid delegate for {uniqueId}", Category.Performance));
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
        yield return RunBurstExecutionLoop(
            uniqueId,
            itemCount: 1,
            batchSize: 1,
            burstActionIJob: burstAction,
            inputs: inputs,
            outputs: nativeOutputs,
            logs: logs,
            options: options,
            executeAction: (batchStart, batchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps) =>
            {
              float batchImpact = mainThreadCost * batchSize;
              float schedulingOverhead = GetSchedulingOverhead(jobKey);
              // Dynamic switch for single-item: IJob if overhead < impact, main if low/high FPS, else coroutine.
              if (schedulingOverhead < batchImpact)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via IJob (single item, schedulingOverhead={schedulingOverhead:F3}ms)", Category.Performance);
#endif
                var jobWrapper = new UnifiedJobWrapper<TInput, TOutput>(burstAction, null, null, inputs, nativeOutputs, logs, default, batchStart, batchSize, batchSize, JobType.IJob);
                return TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(), batchSize);
              }
              else if (batchImpact <= MAX_FRAME_TIME_MS && isHighFps)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via MainThread (single item, highFps={isHighFps})", Category.Performance);
#endif
                TrackExecution(mainThreadKey, () => burstAction(0, 1, inputs, nativeOutputs, logs), batchSize);
                return null;
              }
              else
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (single item, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
                return TrackExecutionCoroutine(coroutineKey, RunBurstCoroutine(burstAction, inputs, nativeOutputs, logs, batchStart, batchSize, options.IsPlayerVisible), batchSize);
              }
            });
        if (burstResultsAction != null || nonBurstResultsAction != null)
        {
          float mainThreadCost = GetAverageItemImpact(mainThreadKey);
          float coroutineCost = GetCoroutineCost(coroutineKey);
          float resultsCost = GetAvgItemTimeMs($"{uniqueId}_Results") * nativeOutputs.Length;
          bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;
          List<TOutput> managedOutputs = new List<TOutput>(nativeOutputs.Length);
          for (int i = 0; i < nativeOutputs.Length; i++) managedOutputs.Add(nativeOutputs[i]);
          yield return ProcessBurstResultsNonJob(uniqueId, burstResultsAction, nonBurstResultsAction, managedOutputs, options, mainThreadCost, coroutineCost, isHighFps);
        }
      }
      finally
      {
        if (nativeOutputs.IsCreated) nativeOutputs.Dispose();
#if DEBUG
        logs.Add(new LogEntry
        {
          Level = Level.Verbose,
          Message = $"Cleaned up nativeOutputs and logs for {uniqueId}",
          Category = Category.Performance
        });
#endif
      }
    }

    /// <summary>
    /// Executes a Burst-compiled job for multiple items with metrics-driven results processing.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <typeparam name="TStruct">Struct type for job execution.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="burstForAction">Delegate for Burst-compiled job execution per item.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="burstResultsAction">Delegate for Burst-compiled results processing.</param>
    /// <param name="nonBurstResultsAction">Delegate for non-Burst results processing.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    [BurstCompile]
    public static IEnumerator ExecuteBurstFor<TInput, TOutput, TStruct>(
            string uniqueId,
            int itemCount,
            Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstForAction,
            NativeArray<TInput> inputs = default,
            NativeList<TOutput> outputs = default,
            NativeList<LogEntry> logs = default,
            Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsAction = null,
            Action<List<TOutput>> nonBurstResultsAction = null,
            SmartOptions options = default)
            where TInput : unmanaged
            where TOutput : unmanaged
            where TStruct : struct
    {
      if (itemCount <= 0 || burstForAction == null)
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

        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstAction = (start, end, inArray, outList, Logs) =>
        {
          for (int i = start; i < end; i++) burstForAction(i, inArray, outList, Logs);
        };

        string jobKey = $"{uniqueId}_IJob";
        string coroutineKey = $"{uniqueId}_Coroutine";
        string mainThreadKey = $"{uniqueId}_MainThread";

        yield return RunBurstExecutionLoop(
            uniqueId,
            itemCount,
            GetDynamicBatchSize(itemCount, 0.15f, uniqueId),
            burstActionIJob: burstAction,
            burstActionFor: burstForAction,
            inputs: inputs,
            outputs: nativeOutputs,
            logs: logs,
            options: options,
            executeAction: (batchStart, batchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps) =>
            {
              float batchImpact = mainThreadCost * batchSize;
              float schedulingOverhead = GetSchedulingOverhead(jobKey);
              // Dynamic switch: IJobParallelFor if multi-core beneficial, IJobFor if sequential better, main if low, coroutine else.
              if (schedulingOverhead < batchImpact)
              {
                JobType selectedJobType = itemCount == 1 ? JobType.IJob
                            : ShouldUseParallel(uniqueId, batchSize) ? JobType.IJobParallelFor : JobType.IJobFor;
                if (selectedJobType == JobType.IJobParallelFor)
                {
#if DEBUG
                  Log(Level.Verbose, $"Executing {uniqueId} via IJobParallelFor (batchSize={batchSize}, schedulingOverhead={schedulingOverhead:F3}ms)", Category.Performance);
#endif
                  var jobWrapper = new UnifiedJobWrapper<TInput, TOutput>(null, burstForAction, null, inputs, nativeOutputs, logs, default, batchStart, batchSize, batchSize, JobType.IJobParallelFor);
                  return TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(batchSize, batchSize), batchSize);
                }
                else
                {
#if DEBUG
                  Log(Level.Verbose, $"Executing {uniqueId} via IJobFor (batchSize={batchSize}, jobOverhead={jobOverhead:F3}ms)", Category.Performance);
#endif
                  var jobWrapper = new UnifiedJobWrapper<TInput, TOutput>(null, burstForAction, null, inputs, nativeOutputs, logs, default, batchStart, batchSize, batchSize, JobType.IJobFor);
                  return TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(batchSize, batchSize), batchSize);
                }
              }
              else if (batchImpact <= MAX_FRAME_TIME_MS && isHighFps)
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via MainThread (batchSize={batchSize}, cost={batchImpact:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
                TrackExecution(mainThreadKey, () =>
                        {
                          for (int j = batchStart; j < batchStart + batchSize; j++) burstForAction(j, inputs, nativeOutputs, logs);
                        }, batchSize);
                return null;
              }
              else
              {
#if DEBUG
                Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (batchSize={batchSize}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
                return TrackExecutionCoroutine(coroutineKey, RunBurstCoroutine(burstAction, inputs, nativeOutputs, logs, batchStart, batchSize, options.IsPlayerVisible), batchSize);
              }
            });

        if (burstResultsAction != null || nonBurstResultsAction != null)
        {
          float mainThreadCost = GetAverageItemImpact(mainThreadKey);
          float coroutineCost = GetCoroutineCost(coroutineKey);
          float resultsCost = GetAvgItemTimeMs($"{uniqueId}_Results") * nativeOutputs.Length;
          bool isHighFps = Time.unscaledDeltaTime < HIGH_FPS_THRESHOLD;
          List<TOutput> managedOutputs = new List<TOutput>(nativeOutputs.Length);
          for (int i = 0; i < nativeOutputs.Length; i++) managedOutputs.Add(nativeOutputs[i]);
          yield return ProcessBurstResultsNonJob(uniqueId, burstResultsAction, nonBurstResultsAction, managedOutputs, options, mainThreadCost, coroutineCost, isHighFps);
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
    /// <param name="burstTransformAction">Delegate for burst-compiled transform job.</param>
    /// <param name="burstMainThreadTransformAction">Delegate for main thread transform processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="options">Execution options.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    [BurstCompile]
    public static IEnumerator ExecuteTransforms<TInput>(
            string uniqueId,
            TransformAccessArray transforms,
            Action<int, TransformAccess, NativeList<LogEntry>> burstTransformAction,
            Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformAction,
            NativeArray<TInput> inputs = default,
            NativeList<LogEntry> logs = default,
            SmartOptions options = default)
            where TInput : unmanaged
    {
      if (!transforms.isCreated || transforms.length == 0 || burstTransformAction == null || burstMainThreadTransformAction == null)
      {
        Log(Level.Warning, $"SmartExecution.ExecuteTransforms: Invalid input for {uniqueId}, transformCount={transforms.length}", Category.Performance);
        yield break;
      }
      yield return RunBurstExecutionLoop<TInput, int>(
          uniqueId,
          itemCount: transforms.length,
          batchSize: GetDynamicBatchSize(transforms.length, 0.15f, uniqueId, true),
          transforms: transforms,
          burstTransformAction: burstTransformAction,
          burstMainThreadTransformAction: burstMainThreadTransformAction,
          inputs: inputs,
          outputs: default,
          logs: logs,
          options: options,
          executeAction: (batchStart, batchSize, jobType, mainThreadCost, coroutineCost, jobOverhead, isHighFps) =>
          {
            string jobKey = $"{uniqueId}_IJobParallelForTransform";
            string coroutineKey = $"{uniqueId}_Coroutine";
            string mainThreadKey = $"{uniqueId}_MainThread";
            float batchImpact = mainThreadCost * batchSize;
            float schedulingOverhead = GetSchedulingOverhead(jobKey);
            if (schedulingOverhead < batchImpact)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} via IJobParallelForTransform (batchSize={batchSize}, schedulingOverhead={schedulingOverhead:F3}ms)", Category.Performance);
#endif
              var jobWrapper = new UnifiedJobWrapper<TInput, int>(null, null, burstTransformAction, inputs, default, logs, transforms, batchStart, batchSize, batchSize, JobType.IJobParallelForTransform);
              return TrackJobWithStopwatch(jobKey, () => jobWrapper.Schedule(batchSize, batchSize), batchSize);
            }
            else if (batchImpact <= MAX_FRAME_TIME_MS && isHighFps)
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} via MainThread (batchSize={batchSize}, cost={batchImpact:F3}ms, highFps={isHighFps})", Category.Performance);
#endif
              TrackExecution(mainThreadKey, () =>
                    {
                      for (int j = batchStart; j < batchStart + batchSize; j++)
                        burstMainThreadTransformAction(j, transforms[j].transform, logs);
                    }, batchSize);
              return null;
            }
            else
            {
#if DEBUG
              Log(Level.Verbose, $"Executing {uniqueId} via Coroutine (batchSize={batchSize}, coroutineCost={coroutineCost:F3}ms)", Category.Performance);
#endif
              return TrackExecutionCoroutine(coroutineKey, RunBurstTransformCoroutine(burstMainThreadTransformAction, transforms, logs, batchStart, batchSize, options.IsPlayerVisible), batchSize);
            }
          });
      if (transforms.isCreated) transforms.Dispose();
    }

    /// <summary>
    /// Runs a Burst-compiled execution loop with dynamic batching and execution path selection.
    /// </summary>
    /// <typeparam name="TInput">Unmanaged input type.</typeparam>
    /// <typeparam name="TOutput">Unmanaged output type.</typeparam>
    /// <param name="uniqueId">Unique identifier for tracking metrics.</param>
    /// <param name="itemCount">Total number of items to process.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="burstActionIJob">Delegate for IJob execution.</param>
    /// <param name="burstActionFor">Delegate for IJobFor execution.</param>
    /// <param name="burstTransformAction">Delegate for transform job execution.</param>
    /// <param name="burstMainThreadTransformAction">Delegate for main thread transform processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="outputs">Output data list.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="transforms">Transform array.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="executeAction">Delegate to execute a batch.</param>
    /// <returns>An enumerator for the execution coroutine.</returns>
    [BurstCompile]
    private static IEnumerator RunBurstExecutionLoop<TInput, TOutput>(
            string uniqueId,
            int itemCount,
            int batchSize,
            Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstActionIJob = null,
            Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstActionFor = null,
            Action<int, TransformAccess, NativeList<LogEntry>> burstTransformAction = null,
            Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformAction = null,
            NativeArray<TInput> inputs = default,
            NativeList<TOutput> outputs = default,
            NativeList<LogEntry> logs = default,
            TransformAccessArray transforms = default,
            SmartOptions options = default,
            Func<int, int, JobType, float, float, float, bool, IEnumerator> executeAction = null)
            where TInput : unmanaged
            where TOutput : unmanaged
    {
      string jobKey = transforms.isCreated ? $"{uniqueId}_IJobParallelForTransform" : $"{uniqueId}_IJob";
      string coroutineKey = $"{uniqueId}_Coroutine";
      string mainThreadKey = $"{uniqueId}_MainThread";
      AddBatchSize(uniqueId, batchSize);
      bool isDroppedFrame = Time.unscaledDeltaTime > TARGET_FRAME_TIME * 1.1f;
      if (isDroppedFrame)
      {
        batchSize = Mathf.Max(1, batchSize / 2);
#if DEBUG
        logs.Add(new LogEntry
        {
          Level = Level.Verbose,
          Message = $"Dropped frame detected for {uniqueId}: frameTime={Time.unscaledDeltaTime * 1000f:F3}ms, newBatchSize={batchSize}",
          Category = Category.Performance
        });
#endif
      }
      bool isFirstRun = _burstFirstRuns.Add(uniqueId);
      if (!_burstBaselineEstablished.ContainsKey(uniqueId) || isFirstRun)
      {
        if (transforms.isCreated)
          yield return EstablishBaselineTransform(uniqueId, transforms, burstTransformAction, burstMainThreadTransformAction, inputs, logs, batchSize, options.IsPlayerVisible);
        else
          yield return EstablishBaselineBurst(uniqueId, burstActionIJob, burstActionFor, inputs, outputs, logs, null, batchSize, options.IsPlayerVisible);
      }
      float mainThreadCost = GetAverageItemImpact(mainThreadKey);
      float coroutineCost = GetCoroutineCost(coroutineKey);
      float jobOverhead = GetJobOverhead(jobKey); // Execution cost, not scheduling overhead
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
        _burstExecutionCount.AddOrUpdate(uniqueId, 1, (_, count) => count + 1);
        yield return options.IsPlayerVisible ? AwaitNextTick(0f).AsCoroutine() : null;
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
    [BurstCompile]
    private static JobType DetermineJobTypeExecutescope(string uniqueId, int itemCount, bool hasTransforms, float mainThreadCost)
    {
      float avgItemTimeMs = GetAvgItemTimeMs($"{uniqueId}_IJob");
      float parallelCost = GetJobOverhead($"{uniqueId}_IJobParallelFor");
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
    /// <param name="burstAction">Burst-compatible delegate for processing items.</param>
    /// <param name="inputs">Input data collection.</param>
    /// <param name="outputs">Output data collection to store results.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of items to process.</param>
    /// <param name="isPlayerVisible">Whether the operation affects player-visible elements.</param>
    /// <returns>Coroutine that yields to spread load across frames.</returns>
    private static IEnumerator RunBurstCoroutine<TInput, TOutput>(
            Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstAction,
            NativeArray<TInput> inputs,
            NativeList<TOutput> outputs,
            NativeList<LogEntry> logs,
            int startIndex,
            int count,
            bool isPlayerVisible)
            where TInput : unmanaged
            where TOutput : unmanaged
    {
      if (burstAction == null || !inputs.IsCreated || !outputs.IsCreated || count <= 0)
      {
        Log(Level.Warning, $"RunBurstCoroutine: Invalid input, delegate={burstAction != null}, inputs={inputs.IsCreated}, outputs={outputs.IsCreated}, count={count}", Category.Performance);
        yield break;
      }

#if DEBUG
      Log(Level.Verbose, $"RunBurstCoroutine: Using outputs (capacity={outputs.Capacity}, count={count})", Category.Performance);
#endif

      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count); // Dynamic sub-batching for yielding within coroutine.
      string key = $"{typeof(TInput).Name}_{typeof(TOutput).Name}_BurstCoroutine";
      AddBatchSize(key, subBatchSize);

      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
#if DEBUG
        Log(Level.Verbose, $"Processing batch {i / subBatchSize + 1} for {key} (startIndex={i}, batchSize={currentSubBatchSize})", Category.Performance);
#endif
        burstAction(i, i + currentSubBatchSize, inputs, outputs, logs);

        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
#if DEBUG
          Log(Level.Verbose, $"Yielding for {key} after batch {i / subBatchSize + 1} (elapsed={stopwatch.ElapsedMilliseconds:F3}ms, isPlayerVisible={isPlayerVisible})", Category.Performance);
#endif
          yield return isPlayerVisible ? AwaitNextTick(0f).AsCoroutine() : null;
          stopwatch.Restart();
        }
      }
    }

    /// <summary>
    /// Runs a coroutine for processing transforms.
    /// </summary>
    /// <param name="burstMainThreadTransformAction">Delegate for main thread transform processing.</param>
    /// <param name="transforms">Array of transforms.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="startIndex">Starting index for processing.</param>
    /// <param name="count">Number of transforms to process.</param>
    /// <param name="isPlayerVisible">Whether the execution is networked.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private static IEnumerator RunBurstTransformCoroutine(
            Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformAction,
            TransformAccessArray transforms,
            NativeList<LogEntry> logs,
            int startIndex,
            int count,
            bool isPlayerVisible)
    {
      var stopwatch = Stopwatch.StartNew();
      int endIndex = startIndex + count;
      int subBatchSize = Math.Min(10, count); // Dynamic sub-batching for yielding.
      for (int i = startIndex; i < endIndex; i += subBatchSize)
      {
        int currentSubBatchSize = Math.Min(subBatchSize, endIndex - i);
        for (int j = i; j < i + currentSubBatchSize; j++)
          burstMainThreadTransformAction(j, transforms[j].transform, logs);
        if (stopwatch.ElapsedMilliseconds >= MAX_FRAME_TIME_MS)
        {
          yield return isPlayerVisible ? AwaitNextTick(0f) : null;
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
    /// Resets baseline performance data for a new game session.
    /// </summary>
    internal static void ResetBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, fileName + "_" + $"{saveId}.json");
      Cleanup();
      _burstBaselineEstablished.Clear();
      _burstExecutionCount.Clear();
      _burstFirstRuns.Clear();
      _lastSavedDataHash = null;
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
    /// Tracks a job's execution time using a stopwatch.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="scheduleJob">The function to schedule the job.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    public static IEnumerator TrackJobWithStopwatch(string methodName, Func<JobHandle> scheduleJob, int itemCount = 1)
    {
      var scheduleStart = Stopwatch.GetTimestamp();
      var handle = scheduleJob();
      var scheduleEnd = Stopwatch.GetTimestamp();
      double schedulingTimeMs = (scheduleEnd - scheduleStart) * 1000.0 / Stopwatch.Frequency;
      AddSchedulingImpactSample(methodName, schedulingTimeMs);
      yield return new WaitUntil(() => handle.IsCompleted);
      handle.Complete();
      if (_pendingJobs.TryGetValue(handle, out var wrapper))
      {
        wrapper.Dispose();
        _pendingJobs.Remove(handle);
        Log(Level.Verbose, $"Disposed job wrapper for {methodName} and removed from pending jobs", Category.Performance);
      }
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
      var logs = new NativeList<LogEntry>(10, Allocator.TempJob);
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

        Action<int, int, NativeArray<int>, NativeList<int>, NativeList<LogEntry>> burstAction = (start, end, inputs, outputs, logs) =>
        {
          for (int i = start; i < end; i++)
            outputs.Add(inputs[i] * 2);
        };

        Action<int, TransformAccess, NativeList<LogEntry>> burstTransformAction = (index, transform, logs) =>
        {
          transform.position += Vector3.one * 0.01f;
        };

        Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformAction = (index, transform, logs) =>
        {
          transform.position += Vector3.one * 0.01f;
        };

        float mainThreadTime = 0.0f;
        float coroutineCostMs = 0.0f;
        float jobTime = 0.0f;
        float transformMainThreadTime = 0.0f;
        float transformCoroutineCostMs = 0.0f;
        float transformJobTime = 0.0f;
        TrackExecution($"{testId}_MainThread", () => burstAction(0, testInputs.Length, testInputs, testJobOutputs, logs), testInputs.Length);
        mainThreadTime = (float)GetAvgItemTimeMs($"{testId}_MainThread");
        yield return TrackExecutionCoroutine($"{testId}_Coroutine", RunBurstCoroutine(burstAction, testInputs, default, logs, 0, testInputs.Length, false), testInputs.Length);
        coroutineCostMs = (float)GetAvgItemTimeMs($"{testId}_Coroutine");
        var jobWrapper = new UnifiedJobWrapper<int, int>(burstAction, null, null, testInputs, testJobOutputs, logs, default, 0, testInputs.Length, testInputs.Length, JobType.IJob);
        yield return TrackJobWithStopwatch($"{testId}_IJob", () => jobWrapper.Schedule(), testInputs.Length);
        jobTime = (float)GetAvgItemTimeMs($"{testId}_IJob");

        // Test transform processing
        TrackExecution($"{testId}_TransformMainThread", () =>
        {
          for (int i = 0; i < testTransforms.length; i++)
            burstMainThreadTransformAction(i, testTransforms[i].transform, logs);
        }, testTransforms.length);
        transformMainThreadTime = (float)GetAvgItemTimeMs($"{testId}_TransformMainThread");
        yield return TrackExecutionCoroutine($"{testId}_TransformCoroutine", RunBurstTransformCoroutine(burstMainThreadTransformAction, testTransforms, logs, 0, testTransforms.length, false), testTransforms.length);
        transformCoroutineCostMs = (float)GetAvgItemTimeMs($"{testId}_TransformCoroutine");
        var transformJobWrapper = new UnifiedJobWrapper<int, int>(null, null, burstTransformAction, testInputs, default, logs, testTransforms, 0, testTransforms.length, testTransforms.length, JobType.IJobParallelForTransform);
        yield return TrackJobWithStopwatch($"{testId}_IJobParallelForTransform", () => transformJobWrapper.Schedule(testTransforms.length, testTransforms.length), testTransforms.length);
        transformJobTime = (float)GetAvgItemTimeMs($"{testId}_IJobParallelForTransform");
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
    /// <param name="burstAction">Delegate for IJob execution.</param>
    /// <param name="burstForAction">Delegate for IJobFor execution.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="jobOutputs">Output data list for jobs.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="coroutineOutputs">Output data list for coroutines.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="isPlayerVisible">Whether the execution is networked.</param>
    /// <returns>An enumerator for the baseline coroutine.</returns>
    [BurstCompile]
    private static IEnumerator EstablishBaselineBurst<TInput, TOutput>(
            string uniqueId,
            Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstAction,
            Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstForAction,
            NativeArray<TInput> inputs,
            NativeList<TOutput> jobOutputs,
            NativeList<LogEntry> logs,
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
      _burstExecutionCount.TryAdd(testId, 0);

      try
      {
        if (typeof(TInput) == typeof(int))
        {
          for (int i = 0; i < testInputs.Length; i++)
            testInputs[i] = (TInput)(object)i;
        }

        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> testBurstAction = burstAction ?? ((start, end, inputs, _, logs) =>
        {
          for (int i = start; i < end; i++) { }
        });

        Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> testBurstForAction = burstForAction ?? ((index, inputs, _, logs) => { });

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
            yield return isPlayerVisible ? AwaitNextTick(0f) : null;
            continue;
          }

          int executionCount = _burstExecutionCount.AddOrUpdate(testId, 1, (_, count) => count + 1);
          if (executionCount % 4 == 0)
          {
            TrackExecution($"{testId}_MainThread", () => testBurstAction(0, testInputs.Length, testInputs, testJobOutputs, logs), testInputs.Length);
            mainThreadTime = (float)GetAvgItemTimeMs($"{testId}_MainThread");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: MainThread iteration {executionCount}, time={mainThreadTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 4 == 1)
          {
            var jobWrapper = new UnifiedJobWrapper<TInput, TOutput>(testBurstAction, null, null, testInputs, testJobOutputs, logs, default, 0, testItemCount, testItemCount, JobType.Baseline);
            yield return TrackJobWithStopwatch($"{testId}_IJob", () => jobWrapper.Schedule(), testItemCount);
            jobTime = (float)GetAvgItemTimeMs($"{testId}_IJob");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJob iteration {executionCount}, time={jobTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 4 == 2)
          {
            var jobWrapper = new UnifiedJobWrapper<TInput, TOutput>(null, testBurstForAction, null, testInputs, testJobOutputs, logs, default, 0, testItemCount, testItemCount, JobType.IJobFor);
            yield return TrackJobWithStopwatch($"{testId}_IJobFor", () => jobWrapper.Schedule(testItemCount, testItemCount), testItemCount);
            jobForTime = (float)GetAvgItemTimeMs($"{testId}_IJobFor");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJobFor iteration {executionCount}, time={jobForTime:F3}ms", Category.Performance);
#endif
          }
          else
          {
            yield return TrackExecutionCoroutine($"{testId}_Coroutine", RunBurstCoroutine(testBurstAction, testInputs, default, logs, 0, testInputs.Length, isPlayerVisible), testInputs.Length);
            coroutineCostMs = (float)GetAvgItemTimeMs($"{testId}_Coroutine");
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
              _burstBaselineEstablished.TryAdd(uniqueId, true);
              CalculateThresholds(uniqueId, testId, mainThreadTime, Math.Max(jobTime, jobForTime));
              Log(Level.Info, $"Baseline set for {uniqueId}: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, " +
                  $"MAX_FRAME_TIME_MS={MAX_FRAME_TIME_MS:F3}, HIGH_FPS_THRESHOLD={HIGH_FPS_THRESHOLD:F3}, " +
                  $"FPS_CHANGE_THRESHOLD={FPS_CHANGE_THRESHOLD:F3}, MIN_TEST_EXECUTIONS={MIN_TEST_EXECUTIONS}, " +
                  $"PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS} (cores={SystemInfo.processorCount})",
                  Category.Performance);
              break;
            }
          }
          yield return isPlayerVisible ? AwaitNextTick(0f) : null;
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
    /// <param name="burstTransformAction">Delegate for burst-compiled transform job.</param>
    /// <param name="burstMainThreadTransformAction">Delegate for main thread transform processing.</param>
    /// <param name="inputs">Input data array.</param>
    /// <param name="logs">Optional NativeList for logs.</param>
    /// <param name="batchSize">Size of each batch.</param>
    /// <param name="isPlayerVisible">Whether the execution is networked.</param>
    /// <returns>An enumerator for the baseline coroutine.</returns>
    [BurstCompile]
    private static IEnumerator EstablishBaselineTransform<TInput>(
            string uniqueId,
            TransformAccessArray transforms,
            Action<int, TransformAccess, NativeList<LogEntry>> burstTransformAction,
            Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformAction,
            NativeArray<TInput> inputs,
            NativeList<LogEntry> logs,
            int batchSize,
            bool isPlayerVisible)
            where TInput : unmanaged
    {
      string testId = $"{uniqueId}_BaselineTest";
      int testItemCount = Math.Min(batchSize, GetDynamicBatchSize(10, 0.15f, testId));
      var testTransforms = new TransformAccessArray(testItemCount);
      var tempGameObjects = new GameObject[testItemCount];
      try
      {
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
            yield return isPlayerVisible ? AwaitNextTick(0f) : null;
            continue;
          }

          int executionCount = _burstExecutionCount.AddOrUpdate(testId, 1, (_, count) => count + 1);
          if (executionCount % 3 == 0)
          {
            TrackExecution($"{testId}_MainThread", () =>
            {
              for (int i = 0; i < testItemCount; i++)
                burstMainThreadTransformAction(i, testTransforms[i].transform, logs);
            }, testItemCount);
            mainThreadTime = (float)GetAvgItemTimeMs($"{testId}_MainThread");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: MainThread iteration {executionCount}, time={mainThreadTime:F3}ms", Category.Performance);
#endif
          }
          else if (executionCount % 3 == 1)
          {
            var jobWrapper = new UnifiedJobWrapper<TInput, int>(null, null, burstTransformAction, inputs, default, logs, testTransforms, 0, testItemCount, testItemCount, JobType.IJobParallelForTransform);
            yield return TrackJobWithStopwatch($"{testId}_IJobParallelForTransform", () => jobWrapper.Schedule(testItemCount, testItemCount), testItemCount);
            jobTime = (float)GetAvgItemTimeMs($"{testId}_IJobParallelForTransform");
#if DEBUG
            Log(Level.Verbose, $"Baseline test {testId}: IJobParallelForTransform iteration {executionCount}, time={jobTime:F3}ms", Category.Performance);
#endif
          }
          else
          {
            yield return TrackExecutionCoroutine($"{testId}_Coroutine", RunBurstTransformCoroutine(burstMainThreadTransformAction, testTransforms, logs, 0, testItemCount, isPlayerVisible), testItemCount);
            coroutineCostMs = (float)GetAvgItemTimeMs($"{testId}_Coroutine");
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
              _burstBaselineEstablished.TryAdd(uniqueId, true);
              CalculateThresholds(uniqueId, testId, mainThreadTime, jobTime);
              Log(Level.Info, $"Baseline set for {uniqueId}: DEFAULT_THRESHOLD={DEFAULT_THRESHOLD}, " +
                  $"MAX_FRAME_TIME_MS={MAX_FRAME_TIME_MS:F3}, HIGH_FPS_THRESHOLD={HIGH_FPS_THRESHOLD:F3}, " +
                  $"FPS_CHANGE_THRESHOLD={FPS_CHANGE_THRESHOLD:F3}, MIN_TEST_EXECUTIONS={MIN_TEST_EXECUTIONS}, " +
                  $"PARALLEL_MIN_ITEMS={PARALLEL_MIN_ITEMS} (cores={SystemInfo.processorCount})",
                  Category.Performance);
              break;
            }
          }
          yield return isPlayerVisible ? AwaitNextTick(0f) : null;
        }
      }
      finally
      {
        if (testTransforms.isCreated) testTransforms.Dispose();
        for (int i = 0; i < tempGameObjects.Length; i++)
          if (tempGameObjects[i] != null)
            UnityEngine.Object.DestroyImmediate(tempGameObjects[i]);
#if DEBUG
        Log(Level.Verbose, $"Cleaned up transform test resources for {testId}", Category.Performance);
#endif
      }
    }

    /// <summary>
    /// Determines if parallel job execution should be used based on item count and costs.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <returns>True if parallel execution is preferred, false otherwise.</returns>
    private static bool ShouldUseParallel(string uniqueId, int itemCount)
    {
      return itemCount > PARALLEL_MIN_ITEMS && GetAvgItemTimeMs($"{uniqueId}_IJobParallelFor") > 0.05f;
    }

    /// <summary>
    /// Saves baseline performance data to a file.
    /// </summary>
    internal static void SaveBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, $"{fileName}_{saveId}.json");
      try
      {
        var data = new OptimizationData // Use qualified name
        {
          Metrics = NonBurstMetrics.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
          BatchSizeHistory = BatchSizeHistory.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
          MetricsThresholds = MetricsThresholds.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), // Added
          RollingAveragesData = RollingAverages.ToDictionary(kvp => kvp.Key, kvp => new RollingAverageData { Samples = kvp.Value._samples, Count = kvp.Value.Count, Sum = kvp.Value._sum }), // Added
          ImpactAveragesData = ImpactAverages.ToDictionary(kvp => kvp.Key, kvp => new RollingAverageData { Samples = kvp.Value._samples, Count = kvp.Value.Count, Sum = kvp.Value._sum }) // Added
        };
        string json = JsonUtility.ToJson(data);
        if (json == _lastSavedDataHash) return;
        File.WriteAllText(filePath, json);
        _lastSavedDataHash = json;
        Log(Level.Info, $"Saved baseline data to {filePath}", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to save baseline data: {ex.Message}", Category.Performance);
      }
    }

    /// <summary>
    /// Loads baseline performance data from a file.
    /// </summary>
    private static void LoadBaselineData()
    {
      if (LoadManager.Instance.ActiveSaveInfo == null) return;
      string saveId = $"save_{LoadManager.Instance.ActiveSaveInfo.SaveSlotNumber}";
      string filePath = Path.Combine(MelonEnvironment.UserDataDirectory, $"{fileName}_{saveId}.json");
      if (!File.Exists(filePath)) return;
      try
      {
        string json = File.ReadAllText(filePath);
        if (json == _lastSavedDataHash) return;
        var data = JsonUtility.FromJson<OptimizationData>(json);
        foreach (var entry in data.Metrics)
        {
          SmartMetrics.AddSample(entry.Key, entry.Value.AvgItemTimeMs, true);
          SmartMetrics.AddImpactSample(entry.Key, entry.Value.AvgMainThreadImpactMs);
        }
        foreach (var entry in data.BatchSizeHistory)
        {
          foreach (var size in entry.Value)
            SmartMetrics.AddBatchSize(entry.Key, size);
        }
        foreach (var entry in data.MetricsThresholds) // Added load
          SmartMetrics.MetricsThresholds[entry.Key] = entry.Value;
        foreach (var entry in data.RollingAveragesData) // Added reconstruct
        {
          var avg = new RollingAverage(entry.Value.Samples.Length) { _samples = entry.Value.Samples, _count = entry.Value.Count, _sum = entry.Value.Sum };
          SmartMetrics.RollingAverages[entry.Key] = avg;
        }
        foreach (var entry in data.ImpactAveragesData) // Added reconstruct
        {
          var avg = new RollingAverage(entry.Value.Samples.Length) { _samples = entry.Value.Samples, _count = entry.Value.Count, _sum = entry.Value.Sum };
          SmartMetrics.ImpactAverages[entry.Key] = avg;
        }
        _lastSavedDataHash = json;
        Log(Level.Info, $"Loaded baseline data from {filePath}", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to load baseline data: {ex.Message}", Category.Performance);
      }
    }

    /// <summary>
    /// Calculates the optimal batch size for Burst operations.
    /// </summary>
    /// <param name="totalItems">Total number of items to process.</param>
    /// <param name="defaultAvgProcessingTimeMs">Default average processing time in milliseconds.</param>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="isTransform">Whether the operation involves transforms.</param>
    /// <returns>The calculated batch size.</returns>
    private static int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId, bool isTransform = false)
    {
      float avgProcessingTimeMs = GetDynamicAvgProcessingTimeMs(uniqueId, defaultAvgProcessingTimeMs, isTransform);
      int avgBatchSize = GetAverageBatchSize(uniqueId);
      float targetFrameTimeMs = Mathf.Min(16.666f, Time.deltaTime * 1000f);
      int calculatedBatchSize = Mathf.Max(1, Mathf.RoundToInt(targetFrameTimeMs / Math.Max(avgProcessingTimeMs, 0.001f)));
      if (avgBatchSize > 0)
      {
        calculatedBatchSize = Mathf.RoundToInt((calculatedBatchSize + avgBatchSize) / 2f);
      }
      return Mathf.Min(totalItems, calculatedBatchSize);
    }

    /// <summary>
    /// Validates baseline data by checking for significant deviations.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    private static void ValidateBaseline(string uniqueId)
    {
      string mainThreadKey = $"{uniqueId}_MainThread";
      double savedAvg = GetAvgItemTimeMs(mainThreadKey);
      double testAvg = 0;
      int testCount = 1;
      for (int i = 0; i < testCount; i++)
      {
        TrackExecution(mainThreadKey, () => { }, 1);
        testAvg += GetAvgItemTimeMs(mainThreadKey);
      }
      testAvg /= testCount;
      if (savedAvg > 0 && Math.Abs(savedAvg - testAvg) / savedAvg > 0.2)
      {
        MetricsThresholds.TryRemove(uniqueId, out _);
        _burstBaselineEstablished.TryRemove(uniqueId, out _);
        _burstExecutionCount.TryRemove(uniqueId, out _);
        RollingAverages.TryRemove($"{uniqueId}_IJob", out _);
        RollingAverages.TryRemove($"{uniqueId}_Coroutine", out _);
        RollingAverages.TryRemove(mainThreadKey, out _);
        ImpactAverages.TryRemove($"{uniqueId}_IJob", out _);
        ImpactAverages.TryRemove($"{uniqueId}_Coroutine", out _);
        ImpactAverages.TryRemove(mainThreadKey, out _);
        BatchSizeHistory.TryRemove(uniqueId, out _);
        Log(Level.Warning, $"Reset baseline for {uniqueId} due to significant deviation (saved={savedAvg:F3}ms, test={testAvg:F3}ms)", Category.Performance);
      }
    }

    [Serializable]
    private class OptimizationData
    {
      public Dictionary<string, MetricData> Metrics = new();
      public Dictionary<string, List<int>> BatchSizeHistory = new();
      public Dictionary<string, int> MetricsThresholds = new(); // Added for full baseline resume
      public Dictionary<string, RollingAverageData> RollingAveragesData = new(); // Added serializable for averages
      public Dictionary<string, RollingAverageData> ImpactAveragesData = new(); // Added serializable for impacts
    }

    [Serializable]
    private class RollingAverageData
    {
      public double[] Samples;
      public int Count;
      public double Sum;
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
        Smart.ResetBaselineData();
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
        Smart.ResetBaselineData();
        Log(Level.Info, "Triggered baseline reset from ImportScreen.Confirm", Category.Performance);
      }
      catch (Exception ex)
      {
        Log(Level.Error, $"Failed to reset baseline in ImportScreen.Confirm: {ex.Message}", Category.Performance);
      }
    }
  }
}