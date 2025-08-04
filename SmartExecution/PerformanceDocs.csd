/// <summary>
/// Defines types of jobs for scheduling in the performance system.
/// </summary>
internal enum JobType

/// <summary>
/// Interface for wrapping job scheduling and completion.
/// </summary>
internal interface IJobWrapper

/// <summary>
/// Wraps a single item job for scheduling.
/// </summary>
/// <typeparam name="T">The type of job, implementing IJob.</typeparam>
internal struct JobWrapper : IJobWrapper
{
    public JobWrapper(T job)

    /// <summary>
    /// Schedules a single item job.
    /// </summary>
    /// <param name="count">Ignored for single item jobs.</param>
    /// <param name="batchSize">Ignored for single item jobs.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    public JobHandle Schedule(int count = 0, int batchSize = 64)

    /// <summary>
    /// Completes the job. No-op for single item jobs.
    /// </summary>
    public void Complete()

}

/// <summary>
/// Wraps a parallel job for multiple items.
/// </summary>
/// <typeparam name="T">The type of job, implementing IJobParallelFor.</typeparam>
internal struct JobParallelForWrapper : IJobWrapper
{
    public JobParallelForWrapper(T job, int count, int batchSize)

    /// <summary>
    /// Schedules a parallel job for multiple items.
    /// </summary>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Size of each batch for parallel processing.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    public JobHandle Schedule(int count = 0, int batchSize = 64)

    /// <summary>
    /// Completes the job. No-op for parallel jobs.
    /// </summary>
    public void Complete()

}

/// <summary>
/// Executes a delegate-based job for processing input to output.
/// </summary>
/// <typeparam name="TInput">Unmanaged input type.</typeparam>
/// <typeparam name="TOutput">Unmanaged output type.</typeparam>
[BurstCompile]
internal struct DelegateJob : IJob
{
    /// <summary>
    /// Executes the job, invoking the delegate with input and output arrays.
    /// </summary>
    public void Execute()

}

/// <summary>
/// Executes a delegate-based parallel job for processing input to output.
/// </summary>
/// <typeparam name="TInput">Unmanaged input type.</typeparam>
/// <typeparam name="TOutput">Unmanaged output type.</typeparam>
[BurstCompile]
internal struct DelegateParallelJob : IJobParallelFor
{
    /// <summary>
    /// Executes the parallel job for a specific index range.
    /// </summary>
    /// <param name="index">The index of the batch to process.</param>
    public void Execute(int index)

}

/// <summary>
/// Executes a delegate-based job for processing individual items in parallel.
/// </summary>
/// <typeparam name="TInput">Unmanaged input type.</typeparam>
/// <typeparam name="TOutput">Unmanaged output type.</typeparam>
[BurstCompile]
internal struct DelegateForJob : IJobParallelFor
{
    /// <summary>
    /// Executes the job for a specific index.
    /// </summary>
    /// <param name="index">The index of the item to process.</param>
    public void Execute(int index)

}

/// <summary>
/// Executes a delegate-based parallel job for transform processing.
/// </summary>
[BurstCompile]
internal struct DelegateParallelForTransformJob : IJobParallelForTransform
{
    /// <summary>
    /// Executes the transform job for a specific index.
    /// </summary>
    /// <param name="index">The index of the transform to process.</param>
    /// <param name="transform">The transform to process.</param>
    public void Execute(int index, TransformAccess transform)

}

/// <summary>
/// Wraps delegate-based jobs for flexible scheduling across job types.
/// </summary>
/// <typeparam name="TInput">Unmanaged input type.</typeparam>
/// <typeparam name="TOutput">Unmanaged output type.</typeparam>
[BurstCompile]
internal struct DelegateJobWrapper : IJobWrapper
{
    public DelegateJobWrapper(
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegateIJob,
        Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegateFor,
        Action<int, TransformAccess, NativeList<LogEntry>> transformDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> outputs,
        TransformAccessArray transforms,
        int startIndex,
        int count,
        int batchSize,
        JobType jobType)

    /// <summary>
    /// Schedules the job based on the specified job type.
    /// </summary>
    /// <param name="count">Number of items to process.</param>
    /// <param name="batchSize">Size of each batch for parallel processing.</param>
    /// <returns>A JobHandle representing the scheduled job.</returns>
    public JobHandle Schedule(int count = 0, int batchSize = 64)

    /// <summary>
    /// Completes the job, disposing of transform resources if applicable.
    /// </summary>
    public void Complete()

}

/// <summary>
/// Tracks performance metrics for job execution and cache access.
/// </summary>
internal static class Metrics
{
    /// <summary>
    /// Gets the average processing time per item for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The average time per item in milliseconds.</returns>
    public static float GetAvgItemTimeMs(string methodName)

    /// <summary>
    /// Initializes the metrics system and subscribes to tick updates.
    /// </summary>
    public static void Initialize()

    /// <summary>
    /// Job for tracking metrics of a single entity.
    /// </summary>
    [BurstCompile]
    public struct SingleEntityMetricsJob : IJob
    {
        /// <summary>
        /// Executes the metrics tracking job.
        /// </summary>
        public void Execute()

    }

    /// <summary>
    /// Stores performance metrics for a method.
    /// </summary>
    [BurstCompile]
    public struct Metric

    /// <summary>
    /// Stores cache access metrics.
    /// </summary>
    [BurstCompile]
    public struct CacheMetric

    /// <summary>
    /// Aggregated performance data for a method.
    /// </summary>
    public struct Data

    internal static void TrackNonBurstIteration(string methodName, Action action, int itemCount = 1)

    /// <summary>
    /// Tracks execution time of an action and updates metrics.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The action to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    [BurstCompile]
    internal static void TrackExecution(string methodName, Action action, int itemCount = 1)

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

    /// <summary>
    /// Tracks execution time of an async action and updates metrics.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The async action to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    internal static async Task TrackExecutionAsync(string methodName, Func<Task> action, int itemCount = 1)

    /// <summary>
    /// Tracks execution time of an async function and updates metrics.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="action">The async function to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>The result of the function.</returns>
    internal static async Task<T> TrackExecutionAsync<T>(string methodName, Func<Task<T>> action, int itemCount = 1)

    /// <summary>
    /// Tracks execution time of a coroutine and updates metrics.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="coroutine">The coroutine to execute.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    internal static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1)

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

    /// <summary>
    /// Tracks cache access for a job and updates metrics.
    /// </summary>
    /// <param name="cacheName">The name of the cache.</param>
    /// <param name="isHit">Whether the cache access was a hit.</param>
    [BurstCompile]
    public static void TrackJobCacheAccess(string cacheName, bool isHit)

    /// <summary>
    /// Tracks cache access and updates metrics.
    /// </summary>
    /// <param name="cacheName">The name of the cache.</param>
    /// <param name="isHit">Whether the cache access was a hit.</param>
    public static void TrackCacheAccess(string cacheName, bool isHit)

    /// <summary>
    /// Updates performance metrics for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="timeMs">Execution time in milliseconds.</param>
    /// <param name="mainThreadImpactMs">Main thread impact in milliseconds.</param>
    /// <param name="itemCount">Number of items processed.</param>
    [BurstCompile]
    internal static void UpdateMetric(string methodName, double timeMs, float mainThreadImpactMs, int itemCount)

    /// <summary>
    /// Updates and logs metrics for all tracked methods.
    /// </summary>
    private static void UpdateMetrics()

    /// <summary>
    /// Cleans up metrics data and unsubscribes from tick updates.
    /// </summary>
    public static void Cleanup()

}

/// <summary>
/// Tracks rolling averages for performance profiling.
/// </summary>
internal static class DynamicProfiler
{
    /// <summary>
    /// Maintains a rolling average of performance samples.
    /// </summary>
    internal class RollingAverage
    {
        public RollingAverage(int maxCount)

        /// <summary>
        /// Adds a performance sample to the rolling average.
        /// </summary>
        /// <param name="value">The sample value in milliseconds.</param>
        public void AddSample(double value)

        /// <summary>
        /// Gets the current rolling average.
        /// </summary>
        /// <returns>The average time in milliseconds.</returns>
        public double GetAverage()

    }

    /// <summary>
    /// Adds a performance sample for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
    /// <param name="isNonBurst">Burst flag. Default=false</param>
    internal static void AddSample(string methodName, double avgItemTimeMs, bool isNonBurst = false)

    /// <summary>
    /// Gets the dynamic average processing time for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="defaultTimeMs">Default time if no data exists.</param>
    /// <param name="isNonBurst">Burst flag. Default=false</param>
    /// <returns>The average processing time in milliseconds.</returns>
    internal static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f, bool isNonBurst = false)

    /// <summary>
    /// Adds a batch size to the history for a method.
    /// </summary>
    /// <param name="uniqueId">The unique identifier for the method.</param>
    /// <param name="batchSize">The batch size used.</param>
    internal static void AddBatchSize(string uniqueId, int batchSize)

    /// <summary>
    /// Gets the average batch size for a method.
    /// </summary>
    /// <param name="uniqueId">The unique identifier for the method.</param>
    /// <returns>The average batch size.</returns>
    internal static int GetAverageBatchSize(string uniqueId)

    /// <summary>
    /// Cleans up profiler data.
    /// </summary>
    internal static void Cleanup()

}

/// <summary>
/// Tracks main thread impact for performance optimization.
/// </summary>
internal static class MainThreadImpactTracker
{
    private struct ImpactSample

    /// <summary>
    /// Initializes the main thread impact tracker.
    /// </summary>
    public static void Initialize()

    /// <summary>
    /// Adds an impact sample for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="impactTimeMs">The impact time in milliseconds.</param>
    public static void AddImpactSample(string methodName, double impactTimeMs)

    /// <summary>
    /// Gets the average main thread impact per item for a method.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The average impact time in milliseconds.</returns>
    public static float GetAverageItemImpact(string methodName)

    /// <summary>
    /// Gets the coroutine execution cost for a method.
    /// </summary>
    /// <param name="coroutineKey">The coroutine identifier.</param>
    /// <returns>The coroutine cost in milliseconds.</returns>
    public static float GetCoroutineCost(string coroutineKey)

    /// <summary>
    /// Gets the job scheduling overhead for a job.
    /// </summary>
    /// <param name="jobKey">The job identifier.</param>
    /// <returns>The job overhead in milliseconds.</returns>
    public static float GetJobOverhead(string jobKey)

    /// <summary>
    /// Tracks a job's execution time using a stopwatch.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="scheduleJob">The function to schedule the job.</param>
    /// <param name="itemCount">Number of items processed.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    public static IEnumerator TrackJobWithStopwatch(string methodName, Func<JobHandle> scheduleJob, int itemCount = 1)

    /// <summary>
    /// Begins tracking a main thread sample.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The sample ID.</returns>
    public static int BeginSample(string methodName)

    /// <summary>
    /// Ends tracking a main thread sample and returns the impact time.
    /// </summary>
    /// <param name="sampleId">The sample ID.</param>
    /// <returns>The impact time in milliseconds.</returns>
    public static float EndSample(int sampleId)

    /// <summary>
    /// Updates performance thresholds based on metrics.
    /// </summary>
    private static void UpdateThresholds()

    /// <summary>
    /// Cleans up main thread impact tracking data.
    /// </summary>
    public static void Cleanup()

}

/// <summary>
/// Caches performance and impact metrics.
/// </summary>
internal static class MetricsCache
{
    /// <summary>
    /// Adds a performance sample to the cache.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
    public static void AddPerformanceSample(string methodName, double avgItemTimeMs)

    /// <summary>
    /// Adds an impact sample to the cache.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="impactTimeMs">Impact time in milliseconds.</param>
    public static void AddImpactSample(string methodName, double impactTimeMs)

    /// <summary>
    /// Tries to get the average performance time from the cache.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="avg">The average time in milliseconds.</param>
    /// <returns>True if the average was found, false otherwise.</returns>
    public static bool TryGetAverage(string methodName, out double avg)

    /// <summary>
    /// Tries to get the impact time from the cache.
    /// </summary>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="impact">The impact time in milliseconds.</param>
    /// <returns>True if the impact was found, false otherwise.</returns>
    public static bool TryGetImpact(string methodName, out double impact)

    /// <summary>
    /// Clears the performance and impact caches.
    /// </summary>
    public static void Cleanup()

}

/// <summary>
/// Configuration options for smart execution.
/// </summary>
public struct SmartExecutionOptions

/// <summary>
/// Manages optimized execution of jobs, coroutines, or main thread tasks.
/// </summary>
public static partial class SmartExecution
{
    static SmartExecution()

    /// <summary>
    /// Initializes the smart execution system.
    /// </summary>
    public static void Initialize()

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
        SmartExecutionOptions options = default) where TOutput : unmanaged

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
    public static IEnumerator ExecuteBurst<TInput, TOutput, TStruct>(
        string uniqueId,
        Action<TInput, NativeList<TOutput>, NativeList<LogEntry>> burstDelegate,
        TInput input = default,
        NativeList<TOutput> outputs = default,
        NativeList<LogEntry> logs = default,
        Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsDelegate = null,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        SmartExecutionOptions options = default) where TInput : unmanaged where TOutput : unmanaged where TStruct : struct

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
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegate,
        Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsDelegate,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        NativeList<LogEntry> logs = default,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        SmartExecutionOptions options = default) where TInput : unmanaged where TOutput : unmanaged

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
    public static IEnumerator ExecuteBurstFor<TInput, TOutput, TStruct>(
        string uniqueId,
        int itemCount,
        Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstForDelegate,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        NativeList<LogEntry> logs = default,
        Action<NativeList<TOutput>, NativeList<LogEntry>> burstResultsDelegate = null,
        Action<List<TOutput>> nonBurstResultsDelegate = null,
        SmartExecutionOptions options = default) where TInput : unmanaged where TOutput : unmanaged where TStruct : struct

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
        Action<int, TransformAccess, NativeList<LogEntry>> burstTransformDelegate,
        Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformDelegate,
        NativeArray<TInput> inputs = default,
        NativeList<LogEntry> logs = default,
        SmartExecutionOptions options = default) where TInput : unmanaged

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
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegateIJob = null,
        Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegateFor = null,
        Action<int, TransformAccess, NativeList<LogEntry>> burstTransformDelegate = null,
        Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformDelegate = null,
        NativeArray<TInput> inputs = default,
        NativeList<TOutput> outputs = default,
        NativeList<LogEntry> logs = default,
        TransformAccessArray transforms = default,
        SmartExecutionOptions options = default,
        Func<int, int, JobType, float, float, float, bool, IEnumerator> executeAction = null) where TInput : unmanaged where TOutput : unmanaged

    /// <summary>
    /// Determines the job type for execution scope.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the execution.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <param name="hasTransforms">Whether transforms are involved.</param>
    /// <param name="mainThreadCost">Main thread execution cost.</param>
    /// <returns>The selected job type.</returns>
    private static JobType DetermineJobTypeExecutescope(string uniqueId, int itemCount, bool hasTransforms, float mainThreadCost)

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
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> outputs,
        NativeList<LogEntry> logs,
        int startIndex,
        int count,
        bool isPlayerVisible) where TInput : unmanaged where TOutput : unmanaged

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
        Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformDelegate,
        TransformAccessArray transforms,
        NativeList<LogEntry> logs,
        int startIndex,
        int count,
        bool isPlayerVisible)

    /// <summary>
    /// Tracks GC allocations periodically.
    /// </summary>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator TrackGCAllocationsCoroutine()

    /// <summary>
    /// Resets baseline performance data.
    /// </summary>
    internal static void ResetBaselineData()

    /// <summary>
    /// Monitors CPU stability to trigger baseline establishment.
    /// </summary>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator MonitorCpuStability()

    /// <summary>
    /// Calculates variance of frame times.
    /// </summary>
    /// <param name="values">List of frame times.</param>
    /// <returns>Variance of frame times.</returns>
    private static float CalculateVariance(List<float> values)

    /// <summary>
    /// Establishes initial performance baseline for execution types.
    /// </summary>
    /// <returns>Coroutine enumerator.</returns>
    private static IEnumerator EstablishInitialBaselineBurst()

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
        Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstDelegate,
        Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>> burstForDelegate,
        NativeArray<TInput> inputs,
        NativeList<TOutput> jobOutputs,
        NativeList<LogEntry> logs,
        List<TOutput> coroutineOutputs,
        int batchSize,
        bool isPlayerVisible) where TInput : unmanaged where TOutput : unmanaged

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
        Action<int, TransformAccess, NativeList<LogEntry>> burstTransformDelegate,
        Action<int, Transform, NativeList<LogEntry>> burstMainThreadTransformDelegate,
        NativeArray<TInput> inputs,
        NativeList<LogEntry> logs,
        int batchSize,
        bool isPlayerVisible) where TInput : unmanaged

    /// <summary>
    /// Calculates variance for a given method's performance metrics.
    /// </summary>
    /// <param name="methodName">Name of the method.</param>
    /// <returns>Variance of performance metrics, or double.MaxValue if insufficient data.</returns>
    private static double CalculateMetricVariance(string methodName)

    /// <summary>
    /// Calculates performance thresholds based on execution times.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="testId">Test identifier.</param>
    /// <param name="mainThreadTime">Main thread execution time per item in milliseconds.</param>
    /// <param name="jobTime">Job execution time per item in milliseconds.</param>
    private static void CalculateThresholds(string uniqueId, string testId, float mainThreadTime, float jobTime)

    /// <summary>
    /// Determines if parallel job execution should be used based on item count and costs.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="itemCount">Number of items to process.</param>
    /// <returns>True if parallel execution is preferred, false otherwise.</returns>
    private static bool ShouldUseParallel(string uniqueId, int itemCount)

    /// <summary>
    /// Saves performance baseline data to a JSON file.
    /// </summary>
    public static void SaveBaselineData()

    /// <summary>
    /// Loads performance baseline data from a JSON file.
    /// </summary>
    public static void LoadBaselineData()

    /// <summary>
    /// Validates baseline data by checking for significant deviations.
    /// </summary>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    private static void ValidateBaseline(string uniqueId)

    /// <summary>
    /// Calculates the dynamic batch size for job execution.
    /// </summary>
    /// <param name="totalItems">Total number of items to process.</param>
    /// <param name="defaultAvgProcessingTimeMs">Default average processing time per item in milliseconds.</param>
    /// <param name="uniqueId">Unique identifier for the task.</param>
    /// <param name="isParallel">Whether parallel execution is preferred.</param>
    /// <returns>Calculated batch size.</returns>
    internal static int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId, bool isParallel = false)

    /// <summary>
    /// Serializes baseline data for storage.
    /// </summary>
    [Serializable]
    private class BaselineData
    {
    }

    /// <summary>
    /// Serializes rolling average data for storage.
    /// </summary>
    [Serializable]
    private class RollingAverageData
    {
    }

    /// <summary>
    /// Serializes cache metric data for storage.
    /// </summary>
    [Serializable]
    private class CacheMetricData
    {
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

    /// <summary>
    /// Postfix patch for ImportScreen to reset baseline data on confirm.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ImportScreen), "Confirm")]
    public static void ImportScreen_Confirm()

}