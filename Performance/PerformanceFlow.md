Execution Paths and Decision-Making Process

Each entry point (Execute, ExecuteMultiple, ExecuteTransforms) has three possible execution paths: Job Execution, Coroutine Execution, and Main Thread Execution. The choice is driven by minimizing main thread impact, with additional considerations for item count, batch size, and system metrics. Below, I describe the execution paths and how decisions are made for each entry point.

1. Execute Entry Point

    Purpose: Processes a single item using burstDelegate (signature: Action<int, int, NativeArray<int>, NativeList<int>>).
    Execution Paths:
        IJob Execution:
            Description: The burstDelegate is wrapped in a DelegateJobWrapper and scheduled as an IJob using Unity’s job system. The job runs on a worker thread, minimizing main thread impact.
            Execution: The delegate is invoked with startIndex=0, endIndex=1, processing the single item.
            Yield: Yields until the job completes (MainThreadImpactTracker.TrackJobWithStopwatch).
        Coroutine Execution:
            Description: The burstDelegate is executed via RunCoroutine, which invokes the delegate directly on the main thread but spreads the work across frames if it exceeds MAX_FRAME_TIME_MS (1ms).
            Execution: The delegate is called with startIndex=0, endIndex=1, wrapped in Metrics.TrackExecutionCoroutine.
            Yield: Yields null (Unity frame) or AwaitNextFishNetTickAsync(0f) (if IsNetworked) to spread execution.
        Main Thread Execution:
            Description: The burstDelegate is invoked synchronously on the main thread using Metrics.TrackExecution.
            Execution: The delegate is called with startIndex=0, endIndex=1, processing the item immediately.
            Yield: No yield, as execution is synchronous.
    Decision-Making Process:
        Metrics Compared:
            jobOverhead: Main thread impact of scheduling and completing the IJob (MainThreadImpactTracker.GetJobOverhead(jobKey)).
            coroutineImpact: Main thread impact of coroutine execution (MainThreadImpactTracker.GetCoroutineCost(coroutineKey)).
            mainThreadImpact: Main thread impact of synchronous execution (MainThreadImpactTracker.GetAverageItemImpact(mainThreadKey)).
        Logic:
            Baseline Check: If no baseline exists or it’s the first run (_firstRuns.Add(uniqueId)), EstablishBaseline is called to measure impacts.
            IJob Selection: Chosen if jobOverhead is the lowest among jobOverhead, coroutineImpact, and mainThreadImpact. This prioritizes offloading work to a worker thread.
            Coroutine Selection: Chosen if coroutineImpact is lower than mainThreadImpact and jobOverhead is not the lowest. This spreads work to avoid frame spikes.
            Main Thread Selection: Chosen if isHighFps (frame time < HIGH_FPS_THRESHOLD) and mainThreadImpact is acceptable (i.e., neither job nor coroutine is better). This is a fallback for low-impact operations.
            Fallback: If no optimal path is found, yields null with a warning log.
        Example:
            If jobOverhead=0.01ms, coroutineImpact=0.05ms, mainThreadImpact=0.1ms, and isHighFps=true, IJob is chosen due to the lowest impact.
            If jobOverhead=0.2ms, coroutineImpact=0.05ms, mainThreadImpact=0.1ms, coroutine is chosen.
            If jobOverhead=0.2ms, coroutineImpact=0.15ms, mainThreadImpact=0.05ms, and isHighFps=true, main thread is chosen.

2. ExecuteMultiple Entry Point

    Purpose: Processes multiple items using burstForDelegate (signature: Action<int, NativeArray<int>, NativeList<int>>).
    Execution Paths:
        IJobParallelFor Execution:
            Description: The burstForDelegate is wrapped in a DelegateJobWrapper and scheduled as an IJobParallelFor, processing items in parallel across worker threads.
            Execution: The delegate is invoked for each index in the batch, with batch size determined by GetDynamicBatchSize.
            Yield: Yields until the job completes (MainThreadImpactTracker.TrackJobWithStopwatch).
        IJobFor Execution:
            Description: The burstForDelegate is scheduled as an IJobFor, processing items sequentially on a worker thread.
            Execution: Similar to IJobParallelFor but without parallel batching.
            Yield: Yields until the job completes.
        Coroutine Execution:
            Description: The burstForDelegate is wrapped in a temporary burstDelegate and executed via RunCoroutine, spreading work across frames.
            Execution: The delegate is called for each index in the batch, with sub-batches of up to 10 items to avoid frame spikes.
            Yield: Yields null or AwaitNextFishNetTickAsync(0f) if execution exceeds MAX_FRAME_TIME_MS.
        Main Thread Execution:
            Description: The burstForDelegate is invoked synchronously for each item in the batch.
            Execution: Wrapped in Metrics.TrackExecution, calling the delegate for each index.
            Yield: No yield, as execution is synchronous.
    Decision-Making Process:
        Metrics Compared:
            jobOverhead, coroutineImpact, mainThreadImpact (as above).
            mainThreadBatchCost: mainThreadImpact *currentBatchSize.
        Additional Metrics:
            useParallel: Determined by ShouldUseParallel, based on item count and parallel cost (MainThreadImpactTracker.GetJobOverhead($"{uniqueId}_IJobParallelFor")).
            Logic:
                Baseline Check: If needed, EstablishBaseline is called with a temporary burstDelegate wrapping burstForDelegate.
                Batch Size: Determined by GetDynamicBatchSize, adjusted if a dropped frame is detected (Time.unscaledDeltaTime > TARGET_FRAME_TIME* 1.1f).
                IJobParallelFor Selection: Chosen if useParallel=true (item count > parallelThreshold and jobOverhead <= mainThreadBatchCost). Requires multiple cores (SystemInfo.processorCount > 1).
                IJobFor Selection: Chosen if jobType == IJobFor (item count <= parallelThreshold but > 10) and jobOverhead <= mainThreadBatchCost.
                Coroutine Selection: Chosen if coroutineImpact <= mainThreadBatchCost and job execution is not optimal.
                Main Thread Selection: Chosen if mainThreadBatchCost <= MAX_FRAME_TIME_MS (1ms) and isHighFps=true, as a fallback for low-impact batches.
                Fallback: Yields null with a warning log.
            Job Type: Determined by DetermineJobType, which considers item count, avgItemTimeMs, and parallelCost.
        Example:
            For 500 items, jobOverhead=0.05ms, coroutineImpact=0.1ms, mainThreadImpact=0.2ms, useParallel=true, IJobParallelFor is chosen with batch size=50.
            For 50 items, jobOverhead=0.1ms, coroutineImpact=0.05ms, mainThreadImpact=0.15ms, coroutine is chosen.
            For 5 items, mainThreadBatchCost=0.25ms, isHighFps=true, main thread is chosen.

3. ExecuteTransforms Entry Point

    Purpose: Processes multiple transforms using burstTransformDelegate (signature: Action<int, TransformAccess>).
    Execution Paths:
        IJobParallelForTransform Execution:
            Description: The burstTransformDelegate is scheduled as an IJobParallelForTransform, processing transforms in parallel.
            Execution: The delegate is invoked for each transform index in the batch.
            Yield: Yields until the job completes.
        Coroutine Execution:
            Description: The burstTransformDelegate is executed via RunCoroutine, spreading transform processing across frames.
            Execution: The delegate is called for each transform index, with sub-batches of up to 10.
            Yield: Yields null or AwaitNextFishNetTickAsync(0f) if needed.
        Main Thread Execution:
            Description: The burstTransformDelegate is invoked synchronously for each transform.
            Execution: Wrapped in Metrics.TrackExecution, calling the delegate for each index.
            Yield: No yield.
    Decision-Making Process:
        Metrics Compared: jobOverhead, coroutineImpact, mainThreadImpact, mainThreadBatchCost.
        Logic:
            Baseline Check: If needed, EstablishBaseline is called (minimal implementation due to no outputs).
            Batch Size: Determined by GetDynamicBatchSize with parallel preference.
            IJobParallelForTransform Selection: Chosen if jobOverhead <= mainThreadBatchCost and item count > transformThreshold.
            Coroutine Selection: Chosen if coroutineImpact <= mainThreadBatchCost.
            Main Thread Selection: Chosen if mainThreadBatchCost <= MAX_FRAME_TIME_MS and isHighFps=true.
            Fallback: Yields null with a warning log.
        Example:
            For 200 transforms, jobOverhead=0.03ms, coroutineImpact=0.1ms, mainThreadImpact=0.15ms, IJobParallelForTransform is chosen.
            For 20 transforms, coroutineImpact=0.05ms, mainThreadImpact=0.1ms, coroutine is chosen.
            For 5 transforms, mainThreadBatchCost=0.25ms, isHighFps=true, main thread is chosen.
