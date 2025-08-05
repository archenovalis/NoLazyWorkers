namespace NoLazyWorkers.SmartExecution
{
    /// <summary>
    /// Provides performance tracking and metrics collection for methods and jobs, optimized for Burst compilation.
    /// </summary>
    [BurstCompile]
    internal static class SmartMetrics
    {
        /// <summary>
        /// Represents a single performance sample for tracking method execution start time.
        /// </summary>
        private struct ImpactSample
        {
            public FixedString64Bytes MethodName; // Name of the method being tracked
            public long StartTicks; // Start time in ticks
            public int Id; // Unique sample identifier
        }

        /// <summary>
        /// Stores performance metrics for a method or job.
        /// </summary>
        [BurstCompile]
        public struct MetricData
        {
            public long CallCount; // Number of calls to the method
            public double TotalTimeMs; // Total execution time in milliseconds
            public double MaxTimeMs; // Maximum execution time in milliseconds
            public double TotalMainThreadImpactMs; // Total main thread impact time in milliseconds
            public double MaxMainThreadImpactMs; // Maximum main thread impact time in milliseconds
            public long CacheHits; // Number of cache hits
            public long CacheMisses; // Number of cache misses
            public long ItemCount; // Total number of items processed
            public double AvgItemTimeMs; // Average time per item in milliseconds
            public double AvgMainThreadImpactMs; // Average main thread impact per item in milliseconds
        }

        /// <summary>
        /// Stores performance metrics for a method.
        /// </summary>
        [BurstCompile]
        public struct Metric
        {
            public FixedString64Bytes MethodName; // Name of the method
            public float ExecutionTimeMs; // Execution time in milliseconds
            public float MainThreadImpactMs; // Main thread impact time in milliseconds
            public int ItemCount; // Number of items processed
        }

        /// <summary>
        /// Initializes the performance metrics system, configuring thread pool settings.
        /// </summary>
        public static void InitializeMetrics();

        /// <summary>
        /// Tracks execution time of a non-Burst method and updates metrics.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="action">The action to execute and measure.</param>
        /// <param name="itemCount">Number of items processed. Default is 1.</param>
        public static void TrackExecution(string methodName, Action action, int itemCount = 1);

        /// <summary>
        /// Begins a performance sample for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <returns>The sample ID.</returns>
        public static int BeginSample(string methodName);

        /// <summary>
        /// Ends a performance sample and calculates impact time.
        /// </summary>
        /// <param name="sampleId">The sample ID.</param>
        /// <returns>The main thread impact time in milliseconds.</returns>
        public static float EndSample(int sampleId);

        /// <summary>
        /// Updates performance metrics for a method in a Burst-compatible context.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="timeMs">Execution time in milliseconds.</param>
        /// <param name="mainThreadImpactMs">Main thread impact time in milliseconds.</param>
        /// <param name="itemCount">Number of items processed.</param>
        /// <param name="logs">Optional native list for logging performance data.</param>
        [BurstCompile]
        private static void UpdateMetric(string methodName, double timeMs, float mainThreadImpactMs, int itemCount, NativeList<LogEntry> logs = default);

        /// <summary>
        /// Updates performance metrics for a method in a non-Burst context.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="timeMs">Execution time in milliseconds.</param>
        /// <param name="mainThreadImpactMs">Main thread impact time in milliseconds.</param>
        /// <param name="itemCount">Number of items processed.</param>
        private static void UpdateMetricNonBurst(string methodName, double timeMs, float mainThreadImpactMs, int itemCount);

        /// <summary>
        /// Adds a performance sample for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
        /// <param name="isNonBurst">Indicates if the context is non-Burst. Default is false.</param>
        internal static void AddSample(string methodName, double avgItemTimeMs, bool isNonBurst = false);

        /// <summary>
        /// Gets the dynamic average processing time for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="defaultTimeMs">Default time if no data exists.</param>
        /// <param name="isNonBurst">Indicates if the context is non-Burst. Default is false.</param>
        /// <returns>The average processing time in milliseconds.</returns>
        internal static float GetDynamicAvgProcessingTimeMs(string methodName, float defaultTimeMs = 0.15f, bool isNonBurst = false);

        /// <summary>
        /// Tries to get the average performance time from the cache.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="avg">The average time in milliseconds.</param>
        /// <returns>True if the average was found, false otherwise.</returns>
        public static bool TryGetAverage(string methodName, out double avg);

        /// <summary>
        /// Adds a batch size to the history for a method.
        /// </summary>
        /// <param name="uniqueId">The unique identifier for the method.</param>
        /// <param name="batchSize">The batch size used.</param>
        internal static void AddBatchSize(string uniqueId, int batchSize);

        /// <summary>
        /// Gets the average batch size for a method.
        /// </summary>
        /// <param name="uniqueId">The unique identifier for the method.</param>
        /// <returns>The average batch size.</returns>
        internal static int GetAverageBatchSize(string uniqueId);

        /// <summary>
        /// Adds an impact sample for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="impactTimeMs">The impact time in milliseconds.</param>
        public static void AddImpactSample(string methodName, double impactTimeMs);

        /// <summary>
        /// Gets the coroutine execution cost for a method.
        /// </summary>
        /// <param name="coroutineKey">The coroutine identifier.</param>
        /// <returns>The coroutine cost in milliseconds.</returns>
        public static float GetCoroutineCost(string coroutineKey);

        /// <summary>
        /// Gets the job scheduling overhead for a job.
        /// </summary>
        /// <param name="jobKey">The job identifier.</param>
        /// <returns>The job overhead in milliseconds.</returns>
        public static float GetJobOverhead(string jobKey);

        /// <summary>
        /// Adds a task overhead sample for a method.
        /// </summary>
        /// <param name="key">The method identifier.</param>
        /// <param name="overheadMs">The task overhead in milliseconds.</param>
        public static void AddTaskOverheadSample(string key, double overheadMs);

        /// <summary>
        /// Gets the average task overhead for a method.
        /// </summary>
        /// <param name="key">The method identifier.</param>
        /// <returns>The average task overhead in milliseconds.</returns>
        public static float GetTaskOverhead(string key);

        /// <summary>
        /// Adds a scheduling impact sample for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="schedulingTimeMs">The scheduling time in milliseconds.</param>
        public static void AddSchedulingImpactSample(string methodName, double schedulingTimeMs);

        /// <summary>
        /// Gets the scheduling overhead for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <returns>The scheduling overhead in milliseconds.</returns>
        public static float GetSchedulingOverhead(string methodName);

        /// <summary>
        /// Updates performance thresholds based on main thread and coroutine impacts.
        /// </summary>
        [BurstCompile]
        private static void UpdateThresholds();

        /// <summary>
        /// Calculates variance for a given method's performance metrics.
        /// </summary>
        /// <param name="methodName">Name of the method.</param>
        /// <returns>Variance of performance metrics, or float.MaxValue if insufficient data.</returns>
        internal static float CalculateMetricVariance(string methodName);

        /// <summary>
        /// Calculates performance thresholds based on execution times.
        /// </summary>
        /// <param name="uniqueId">Unique identifier for the task.</param>
        /// <param name="testId">Test identifier.</param>
        /// <param name="mainThreadTime">Main thread execution time per item in milliseconds.</param>
        /// <param name="jobTime">Job execution time per item in milliseconds.</param>
        internal static void CalculateThresholds(string uniqueId, string testId, float mainThreadTime, float jobTime);

        /// <summary>
        /// Gets the average main thread impact per item for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <returns>The average impact time in milliseconds.</returns>
        public static float GetAverageItemImpact(string methodName);

        /// <summary>
        /// Tries to get the impact time from the cache.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="impact">The impact time in milliseconds.</param>
        /// <returns>True if the impact was found, false otherwise.</returns>
        public static bool TryGetImpact(string methodName, out double impact);

        /// <summary>
        /// Gets the average processing time per item for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <returns>The average time per item in milliseconds.</returns>
        public static float GetAvgItemTimeMs(string methodName);

        /// <summary>
        /// Gets the task execution cost for a method.
        /// </summary>
        /// <param name="taskKey">The task identifier.</param>
        /// <returns>The task cost in milliseconds.</returns>
        public static float GetTaskCost(string taskKey);

        /// <summary>
        /// Adds a thread safety sample for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="isTaskSafe">Whether the method is task-safe.</param>
        public static void AddThreadSafetySample(string methodName, bool isTaskSafe);

        /// <summary>
        /// Tries to get the thread safety status for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="isTaskSafe">The thread safety status.</param>
        /// <returns>True if the status was found, false otherwise.</returns>
        public static bool TryGetThreadSafety(string methodName, out bool isTaskSafe);

        /// <summary>
        /// Maintains a rolling average of performance samples.
        /// </summary>
        internal class RollingAverage
        {
            /// <summary>
            /// Initializes a new RollingAverage with a maximum sample count.
            /// </summary>
            /// <param name="maxCount">Maximum number of samples to store.</param>
            public RollingAverage(int maxCount);

            /// <summary>
            /// Adds a performance sample to the rolling average.
            /// </summary>
            /// <param name="value">The sample value in milliseconds.</param>
            public void AddSample(double value);

            /// <summary>
            /// Gets the current rolling average.
            /// </summary>
            /// <returns>The average time in milliseconds.</returns>
            public double GetAverage();
        }

        /// <summary>
        /// Adds a performance sample to the cache.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="avgItemTimeMs">Average item processing time in milliseconds.</param>
        public static void AddPerformanceSample(string methodName, double avgItemTimeMs);

        /// <summary>
        /// Tracks execution time of an async task and updates metrics.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="action">The async function to execute.</param>
        /// <param name="itemCount">Number of items processed. Default is 1.</param>
        /// <returns>A task representing the async operation.</returns>
        public static async Task TrackExecutionTaskAsync(string methodName, Func<Task> action, int itemCount = 1);

        /// <summary>
        /// Executes a function within Unity's synchronization context and tracks performance.
        /// </summary>
        /// <typeparam name="T">The return type of the function.</typeparam>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="action">The function to execute.</param>
        /// <param name="itemCount">Number of items processed. Default is 1.</param>
        /// <returns>The result of the function.</returns>
        public static async Task<T> ExecuteWithUnitySyncContext<T>(string methodName, Func<T> action, int itemCount = 1);

        /// <summary>
        /// Gets the average task creation overhead for a method.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <returns>The average task creation overhead in milliseconds.</returns>
        public static float GetTaskCreationOverhead(string methodName);

        /// <summary>
        /// Tracks execution time of a coroutine and updates metrics.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="coroutine">The coroutine to execute.</param>
        /// <param name="itemCount">Number of items processed. Default is 1.</param>
        /// <returns>An enumerator for the coroutine.</returns>
        internal static IEnumerator TrackExecutionCoroutine(string methodName, IEnumerator coroutine, int itemCount = 1);

        /// <summary>
        /// Tracks cache access for a Burst-compiled job and updates metrics.
        /// </summary>
        /// <param name="cacheName">The name of the cache.</param>
        /// <param name="isHit">Whether the cache access was a hit.</param>
        /// <param name="logs">Native list for logging performance data.</param>
        [BurstCompile]
        public static void TrackCacheAccess(FixedString64Bytes cacheName, bool isHit, NativeList<LogEntry> logs);

        /// <summary>
        /// Updates all performance metrics and logs results.
        /// </summary>
        private static void UpdateMetrics();

        /// <summary>
        /// Cleans up performance metrics resources and clears caches.
        /// </summary>
        [BurstCompile]
        public static void Cleanup();

        /// <summary>
        /// Creates a new MetricData instance for a cache access.
        /// </summary>
        /// <param name="isHit">Whether the cache access was a hit.</param>
        /// <returns>The created MetricData instance.</returns>
        [BurstCompile]
        private static MetricData CreateCacheData(bool isHit);

        /// <summary>
        /// Updates an existing MetricData instance with cache access data.
        /// </summary>
        /// <param name="existing">The existing MetricData instance.</param>
        /// <param name="isHit">Whether the cache access was a hit.</param>
        /// <returns>The updated MetricData instance.</returns>
        [BurstCompile]
        private static MetricData UpdateCacheData(MetricData existing, bool isHit);

        /// <summary>
        /// Creates a new MetricData instance for a method execution.
        /// </summary>
        /// <param name="timeMs">Execution time in milliseconds.</param>
        /// <param name="mainThreadImpactMs">Main thread impact time in milliseconds.</param>
        /// <param name="itemCount">Number of items processed.</param>
        /// <returns>The created MetricData instance.</returns>
        [BurstCompile]
        private static MetricData CreateMetricData(double timeMs, float mainThreadImpactMs, int itemCount);

        /// <summary>
        /// Updates an existing MetricData instance with new execution data.
        /// </summary>
        /// <param name="existing">The existing MetricData instance.</param>
        /// <param name="timeMs">Execution time in milliseconds.</param>
        /// <param name="mainThreadImpactMs">Main thread impact time in milliseconds.</param>
        /// <param name="itemCount">Number of items processed.</param>
        /// <returns>The updated MetricData instance.</returns>
        [BurstCompile]
        private static MetricData UpdateMetricData(MetricData existing, double timeMs, float mainThreadImpactMs, int itemCount);
    }

    /// <summary>
    /// Defines types of jobs for unified job execution.
    /// </summary>
    internal enum JobType
    {
        Baseline, // Baseline job type
        IJob, // Standard job
        IJobFor, // Job for sequential processing
        IJobParallelFor, // Parallel job for multiple items
        IJobParallelForTransform // Parallel job for transform operations
    }

    /// <summary>
    /// Base wrapper for managing job handles and disposal in a Burst-compatible manner.
    /// </summary>
    [BurstCompile]
    public struct UnifiedJobWrapperBase
    {
        public JobHandle JobHandle; // The job handle to manage
        private FunctionPointer<Action> _disposeAction; // Delegate for disposing resources
        public bool IsCreated; // Indicates if the wrapper is created

        /// <summary>
        /// Initializes a new UnifiedJobWrapperBase with a job handle and disposal delegate.
        /// </summary>
        /// <param name="handle">The job handle to manage.</param>
        /// <param name="disposeAction">Delegate for disposing resources.</param>
        public UnifiedJobWrapperBase(JobHandle handle, FunctionPointer<Action> disposeAction);

        /// <summary>
        /// Disposes the job wrapper, completing the job if necessary.
        /// </summary>
        [BurstCompile]
        public void Dispose();
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
        private readonly JobType _jobType; // Type of job to execute
        private readonly FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> _actionIJob; // Delegate for IJob execution
        private readonly FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> _actionFor; // Delegate for IJobFor execution
        private readonly FunctionPointer<Action<int, TransformAccess, NativeList<LogEntry>>> _actionTransform; // Delegate for transform job execution
        private readonly NativeArray<TInput> _inputs; // Input data
        private readonly NativeList<TOutput> _outputs; // Output data
        private readonly NativeList<LogEntry> _logs; // Log entries
        private readonly TransformAccessArray _transforms; // Transform array
        private readonly int _startIndex; // Starting index for processing
        private readonly int _count; // Number of items to process
        private readonly int _batchSize; // Batch size for parallel jobs

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
            JobType jobType);

        /// <summary>
        /// Schedules the job based on the specified job type.
        /// </summary>
        /// <param name="count">Number of items to process. Default is 0 (uses instance count).</param>
        /// <param name="batchSize">Batch size for parallel jobs. Default is 64.</param>
        /// <returns>The scheduled job handle.</returns>
        [BurstCompile]
        public JobHandle Schedule(int count = 0, int batchSize = 64);

        /// <summary>
        /// Disposes of native resources used by the job wrapper.
        /// </summary>
        [BurstCompile]
        public void Dispose();
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
        [ReadOnly] public FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> Action; // Delegate for job execution
        [ReadOnly] public NativeArray<TInput> Inputs; // Input data
        public NativeList<TOutput> Outputs; // Output data
        public NativeList<LogEntry> Logs; // Log entries
        public int StartIndex; // Starting index for processing
        public int Count; // Number of items to process

        /// <summary>
        /// Executes the job using the provided delegate.
        /// </summary>
        public void Execute();
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
        [ReadOnly] public FunctionPointer<Action<int, int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> Action; // Delegate for parallel job execution
        [ReadOnly] public NativeArray<TInput> Inputs; // Input data
        public NativeList<TOutput> Outputs; // Output data
        public NativeList<LogEntry> Logs; // Log entries
        public int StartIndex; // Starting index for processing
        public int BatchSize; // Batch size for parallel processing

        /// <summary>
        /// Executes the parallel job for a specific index range.
        /// </summary>
        /// <param name="index">The index of the batch to process.</param>
        public void Execute(int index);
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
        [ReadOnly] public FunctionPointer<Action<int, NativeArray<TInput>, NativeList<TOutput>, NativeList<LogEntry>>> Action; // Delegate for sequential job execution
        [ReadOnly] public NativeArray<TInput> Inputs; // Input data
        public NativeList<TOutput> Outputs; // Output data
        public NativeList<LogEntry> Logs; // Log entries
        public int StartIndex; // Starting index for processing

        /// <summary>
        /// Executes the job for a specific index.
        /// </summary>
        /// <param name="index">The index of the item to process.</param>
        public void Execute(int index);
    }

    /// <summary>
    /// Executes a delegate-based parallel job for transform processing.
    /// </summary>
    [BurstCompile]
    internal struct ActionParallelForTransformJob : IJobParallelForTransform
    {
        [ReadOnly] public FunctionPointer<Action<int, TransformAccess, NativeList<LogEntry>>> Action; // Delegate for transform job execution
        public int StartIndex; // Starting index for processing
        public int BatchSize; // Batch size for parallel processing
        public NativeList<LogEntry> Logs; // Log entries

        /// <summary>
        /// Executes the transform job for a specific index.
        /// </summary>
        /// <param name="index">The index of the transform to process.</param>
        /// <param name="transform">The transform to process.</param>
        public void Execute(int index, TransformAccess transform);
    }

    /// <summary>
    /// Configuration options for smart execution.
    /// </summary>
    public struct SmartOptions
    {
        public bool IsPlayerVisible; // Indicates if the operation affects player-visible elements
        public bool IsTaskSafe; // Indicates if the operation is task-safe

        /// <summary>
        /// Gets the default smart execution options.
        /// </summary>
        public static SmartOptions Default { get; }
    }

    /// <summary>
    /// Manages optimized execution of jobs, coroutines, or main thread tasks.
    /// </summary>
    public static partial class Smart
    {
        /// <summary>
        /// Initializes the SmartExecution system and starts monitoring coroutines.
        /// </summary>
        static Smart();

        /// <summary>
        /// Initializes the SmartExecution system.
        /// </summary>
        public static void Initialize();

        /// <summary>
        /// Cleans up resources and pending jobs in the SmartExecution system.
        /// </summary>
        public static void Cleanup();

        /// <summary>
        /// Monitors thread usage to detect oversubscription.
        /// </summary>
        /// <returns>An enumerator for the coroutine.</returns>
        private static IEnumerator MonitorThreadUsage();

        /// <summary>
        /// Checks if an action is task-safe by testing Unity object access.
        /// </summary>
        /// <param name="key">The identifier for the action.</param>
        /// <param name="action">The action to test.</param>
        /// <param name="uniqueId">Unique identifier for logging.</param>
        /// <param name="logs">List for logging errors.</param>
        /// <returns>True if the action is task-safe, false otherwise.</returns>
        private static bool CheckTaskSafety(string key, Action action, string uniqueId, List<LogEntry> logs);

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
            bool isHighFps);

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
            SmartOptions options = default);

        /// <summary>
        /// Converts a Task to a coroutine for Unity integration.
        /// </summary>
        /// <param name="task">The task to convert.</param>
        /// <returns>An enumerator for the coroutine.</returns>
        private static IEnumerator TaskToCoroutine(Task task);

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
            bool isPlayerVisible);

        /// <summary>
        /// Tracks execution metrics for a non-Burst action.
        /// </summary>
        /// <param name="key">The identifier for the action.</param>
        /// <param name="action">The action to measure.</param>
        /// <param name="itemCount">Number of items processed.</param>
        private static void TrackExecutionMetrics(string key, Action action, int itemCount);

        /// <summary>
        /// Calculates the optimal batch size based on performance metrics.
        /// </summary>
        /// <param name="totalItems">Total number of items to process.</param>
        /// <param name="defaultAvgProcessingTimeMs">Default average processing time in milliseconds.</param>
        /// <param name="uniqueId">Unique identifier for the task.</param>
        /// <returns>The calculated batch size.</returns>
        private static int CalculateBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId);

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
            Func<int, int, float, float, float, bool, bool, IEnumerator> executeAction);

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
            SmartOptions options);

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
            where TOutput : unmanaged;

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
            bool isPlayerVisible);

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
            where TStruct : struct;

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
            where TOutput : unmanaged;

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
            where TStruct : struct;

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
            where TInput : unmanaged;

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
            where TOutput : unmanaged;

        /// <summary>
        /// Determines the job type for execution scope.
        /// </summary>
        /// <param name="uniqueId">Unique identifier for the execution.</param>
        /// <param name="itemCount">Number of items to process.</param>
        /// <param name="hasTransforms">Whether transforms are involved.</param>
        /// <param name="mainThreadCost">Main thread execution cost.</param>
        /// <returns>The selected job type.</returns>
        [BurstCompile]
        private static JobType DetermineJobTypeExecutescope(string uniqueId, int itemCount, bool hasTransforms, float mainThreadCost);

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
            where TOutput : unmanaged;

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
            bool isPlayerVisible);

        /// <summary>
        /// Tracks GC allocations periodically.
        /// </summary>
        /// <returns>Coroutine enumerator.</returns>
        private static IEnumerator TrackGCAllocationsCoroutine();

        /// <summary>
        /// Resets baseline performance data for a new game session.
        /// </summary>
        internal static void ResetBaselineData();

        /// <summary>
        /// Monitors CPU stability to trigger baseline establishment.
        /// </summary>
        /// <returns>Coroutine enumerator.</returns>
        private static IEnumerator MonitorCpuStability();

        /// <summary>
        /// Calculates variance of frame times.
        /// </summary>
        /// <param name="values">List of frame times.</param>
        /// <returns>Variance of frame times.</returns>
        private static float CalculateVariance(List<float> values);

        /// <summary>
        /// Tracks a job's execution time using a stopwatch.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <param name="scheduleJob">The function to schedule the job.</param>
        /// <param name="itemCount">Number of items processed.</param>
        /// <returns>An enumerator for the coroutine.</returns>
        public static IEnumerator TrackJobWithStopwatch(string methodName, Func<JobHandle> scheduleJob, int itemCount = 1);

        /// <summary>
        /// Establishes initial performance baseline for execution types.
        /// </summary>
        /// <returns>Coroutine enumerator.</returns>
        private static IEnumerator EstablishInitialBaselineBurst();

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
            where TOutput : unmanaged;

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
            where TInput : unmanaged;

        /// <summary>
        /// Determines if parallel job execution should be used based on item count and costs.
        /// </summary>
        /// <param name="uniqueId">Unique identifier for the task.</param>
        /// <param name="itemCount">Number of items to process.</param>
        /// <returns>True if parallel execution is preferred, false otherwise.</returns>
        private static bool ShouldUseParallel(string uniqueId, int itemCount);

        /// <summary>
        /// Saves baseline performance data to a file.
        /// </summary>
        internal static void SaveBaselineData();

        /// <summary>
        /// Loads baseline performance data from a file.
        /// </summary>
        private static void LoadBaselineData();

        /// <summary>
        /// Calculates the optimal batch size for Burst operations.
        /// </summary>
        /// <param name="totalItems">Total number of items to process.</param>
        /// <param name="defaultAvgProcessingTimeMs">Default average processing time in milliseconds.</param>
        /// <param name="uniqueId">Unique identifier for the task.</param>
        /// <param name="isTransform">Whether the operation involves transforms.</param>
        /// <returns>The calculated batch size.</returns>
        private static int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string uniqueId, bool isTransform = false);

        /// <summary>
        /// Validates baseline data by checking for significant deviations.
        /// </summary>
        /// <param name="uniqueId">Unique identifier for the task.</param>
        private static void ValidateBaseline(string uniqueId);
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
        public static void SetupScreen_ClearFolderContents(string folderPath);

        /// <summary>
        /// Postfix patch for ImportScreen to reset baseline data on confirm.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ImportScreen), "Confirm")]
        public static void ImportScreen_Confirm();
    }

    /// <summary>
    /// Stores optimization data for serialization.
    /// </summary>
    [Serializable]
    private class OptimizationData
    {
        public Dictionary<string, MetricData> Metrics; // Performance metrics
        public Dictionary<string, List<int>> BatchSizeHistory; // Batch size history
        public Dictionary<string, int> MetricsThresholds; // Performance thresholds
        public Dictionary<string, RollingAverageData> RollingAveragesData; // Rolling average data
        public Dictionary<string, RollingAverageData> ImpactAveragesData; // Impact average data
    }

    /// <summary>
    /// Stores rolling average data for serialization.
    /// </summary>
    [Serializable]
    private class RollingAverageData
    {
        public double[] Samples; // Array of performance samples
        public int Count; // Number of samples
        public double Sum; // Sum of sample values
    }
}