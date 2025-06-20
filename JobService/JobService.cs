using System.Collections;
using NoLazyWorkers.Metrics;
using UnityEngine;
using static NoLazyWorkers.TimeManagerExtensions;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Debug.Deferred;

namespace NoLazyWorkers.JobService
{
  /// <summary>
  /// Provides utilities for Burst-compatible job scheduling, logging, and resource management.
  /// </summary>
  public static class JobScheduler
  {
    /// <summary>
    /// Executes an action in batches, spreading load across frames using AwaitNextTickAsync.
    /// </summary>
    public static async Task ExecuteInBatchesAsync(int totalItems, Action<int> action, string methodName, float defaultAvgProcessingTimeMs = 0.15f)
    {
      if (totalItems <= 0 || action == null) return;

      int batchSize = GetDynamicBatchSize(totalItems, defaultAvgProcessingTimeMs, methodName);
      int processedCount = 0;
      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      for (int i = 0; i < totalItems; i++)
      {
        action(i);
        processedCount++;

        if (processedCount % batchSize == 0)
        {
          if (processedCount > 0)
          {
            double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (System.Diagnostics.Stopwatch.Frequency * processedCount);
            DynamicProfiler.AddSample(methodName, avgItemTimeMs);
            stopwatch.Restart();
          }
          await AwaitNextFishNetTickAsync();
          processedCount = 0;
        }
      }

      if (processedCount > 0)
      {
        double avgItemTimeMs = stopwatch.ElapsedTicks * 1000.0 / (System.Diagnostics.Stopwatch.Frequency * processedCount);
        DynamicProfiler.AddSample(methodName, avgItemTimeMs);
      }
    }

    /// <summary>
    /// Calculates dynamic batch size based on total items and processing time.
    /// </summary>
    public static int GetDynamicBatchSize(int totalItems, float defaultAvgProcessingTimeMs, string methodName)
    {
      float targetFrameTimeMs = 16.67f; // 60 FPS target
      int batchSize = Mathf.CeilToInt(targetFrameTimeMs / defaultAvgProcessingTimeMs);
      return Mathf.Clamp(batchSize, 1, totalItems);
    }

    /// <summary>
    /// Utility coroutine to enforce disposal within 4 frames, logging errors if exceeded.
    /// </summary>
    public static IEnumerator FrameLimitCoroutine(Action disposeAction, int maxFrames = 4, string methodName = null)
    {
      int frameCount = 0;
      while (frameCount < maxFrames)
      {
        yield return null;
        frameCount++;
        try
        {
          disposeAction();
          yield break;
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"{methodName ?? "FrameLimitCoroutine"}: Disposal failed at frame {frameCount}: {ex}", Category.Tasks);
        }
      }
      Log(Level.Error, $"{methodName ?? "FrameLimitCoroutine"}: Exceeded {maxFrames}-frame limit, potential memory leak", Category.Tasks);
    }
  }
}