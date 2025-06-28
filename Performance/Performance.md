# NoLazyWorkers.Performance API Documentation

Date: June 28, 2025

## Overview

The `NoLazyWorkers.Performance` namespace provides a high-performance job scheduling and execution system optimized for Unity, leveraging Unity's Job System, Burst compilation, and dynamic profiling to minimize main thread impact. It includes job wrappers, execution time tracking, dynamic batch size load spreading, and smart execution strategies for single items, multiple items, and transform operations. The updated API introduces support for both Burst-compiled and non-Burst delegates, enhancing flexibility for non-Burst scenarios while maintaining performance optimizations.

Delegates must be Burst-compiled for Burst jobs, but non-Burst delegates are supported for main thread or coroutine execution.The system dynamically chooses the optimal execution path(main thread, coroutine, or job) based on performance metrics.Heavy methods should use smart execution for load spreading.

---

## Structs

### SmartExecutionOptions

Configuration options for smart execution.

- **Fields**:
  - `IsPlayerVisible`: Indicates if execution is networked, affecting yield behavior.

- **Static Properties**:
  - `Default`: Returns default options with `IsPlayerVisible = false`.

---

## Classes

### SmartExecution

Manages optimized execution of jobs, coroutines, or main thread tasks with dynamic batch sizing and metrics-driven results processing.

- **Static Methods**:
  - `void Initialize()`: Initializes the smart execution system, setting up metrics and baseline data.
  - `IEnumerator Execute<TInput, TOutput>(string uniqueId, int itemCount, Action<int, int, TInput[], List<TOutput>> nonBurstDelegate, Action<List<TOutput>> nonBurstResultsDelegate, TInput[] inputs, List<TOutput> outputs, Action<List<TOutput>> burstResultsDelegate = null, SmartExecutionOptions options = default)`: Executes a non-Burst loop for `itemCount` items with dynamic batch sizing and optional results processing using either Burst or non-Burst delegates.Yields to spread load across frames.
  - `IEnumerator ExecuteBurst<TInput, TOutput>(string uniqueId, Action<int, int, NativeArray<TInput>, NativeList<TOutput>> burstDelegate, Action<NativeList<TOutput>> burstResultsDelegate, NativeArray<TInput> inputs = default, NativeList<TOutput> outputs = default, Action<List<TOutput>> nonBurstResultsDelegate = null, SmartExecutionOptions options = default)`: Executes a single item using a Burst-compiled job with metrics-driven results processing, supporting both Burst and non-Burst results delegates.
  - `IEnumerator ExecuteBurstFor<TInput, TOutput>(string uniqueId, int itemCount, Action<int, NativeArray<TInput>, NativeList<TOutput>> burstForDelegate, Action<NativeList<TOutput>> burstResultsDelegate, NativeArray<TInput> inputs = default, NativeList<TOutput> outputs = default, Action<List<TOutput>> nonBurstResultsDelegate = null, SmartExecutionOptions options = default)`: Executes multiple items using a Burst-compiled job(IJobFor or IJobParallelFor) with dynamic batch sizing and results processing.
  - `IEnumerator ExecuteTransforms<TInput>(string uniqueId, TransformAccessArray transforms, Action<int, TransformAccess> burstTransformDelegate, Action<int, Transform> burstMainThreadTransformDelegate, NativeArray<TInput> inputs = default, SmartExecutionOptions options = default)`: Executes transform operations using Burst-compiled jobs or main thread processing, with dynamic batch sizing.
  - `void SaveBaselineData()`: Saves performance baseline data to a JSON file.
  - `void LoadBaselineData()`: Loads performance baseline data from a JSON file.
  - `void ResetBaselineData()`: Resets baseline performance data and clears associated files.

- **Example 1: Execute (Non-Burst, Single Item)**

```csharp
public class NonBurstSingleItemExample
{
  private static void ProcessNonBurst(int start, int count, int[] inputs, List<int> outputs)
  {
    for (int i = start; i < start + count; i++)
    {
      outputs.Add(inputs[i] * 2);
    }
  }

  private static void ProcessNonBurstResults(List<int> outputs)
  {
    if (outputs.Count > 0)
    {
      Debug.Log($"Processed output: {outputs[0]}");
    }
  }

public static IEnumerator Start()
  {
    int[] inputs = new int[] { 5 };
    List<int> outputs = new List<int>();
    SmartExecutionOptions options = SmartExecutionOptions.Default;
    yield return SmartExecution.Execute(
        uniqueId: nameof(Start),
        itemCount: 1,
        nonBurstDelegate: ProcessNonBurst,
        nonBurstResultsDelegate: ProcessNonBurstResults,
        inputs: inputs,
        outputs: outputs,
        options: options
    );
    Debug.Log($"Final outputs: {string.Join(", ", outputs)}");
  }
}
```

- **Example 2: ExecuteBurst(Single Item)**

```csharp
public class BurstSingleItemExample
{
  [BurstCompile]
  private static void ProcessSingleItem(int start, int end, NativeArray<int> inputs, NativeList<int> outputs)
  {
    for (int i = start; i < end; i++)
    {
      outputs.Add(inputs[i] * 2);
    }
  }

  [BurstCompile]
  private static void ProcessBurstResults(NativeList<int> outputs)
  {
    if (outputs.Length > 0)
    {
      Debug.Log($"Processed output: {outputs[0]}");
    }
  }

public static IEnumerator Start()
  {
    NativeArray<int> inputs = new NativeArray<int>(1, Allocator.TempJob);
    NativeList<int> outputs = new NativeList<int>(1, Allocator.TempJob);
    try
    {
      inputs[0] = 5;
      SmartExecutionOptions options = SmartExecutionOptions.Default;
      yield return SmartExecution.ExecuteBurst(
          uniqueId: nameof(Start),
          burstDelegate: ProcessSingleItem,
          burstResultsDelegate: ProcessBurstResults,
          inputs: inputs,
          outputs: outputs,
          options: options
      );
    }
    finally
    {
      if (inputs.IsCreated) inputs.Dispose();
      if (outputs.IsCreated) outputs.Dispose();
    }
  }
}
```

- **Example 3: ExecuteBurstFor(Multiple Items)**

```csharp
public class BurstMultipleItemsExample
{
  [BurstCompile]
  private static void ProcessMultipleItems(int index, NativeArray<int> inputs, NativeList<int> outputs)
  {
    outputs.Add(inputs[index] + 10);
  }

  private static void ProcessNonBurstResults(List<int> outputs)
  {
    Debug.Log($"Processed {outputs.Count} items, first few: {string.Join(", ", outputs.Take(5))}");
  }

public static IEnumerator Start()
  {
    NativeArray<int> inputs = new NativeArray<int>(100, Allocator.TempJob);
    NativeList<int> outputs = new NativeList<int>(100, Allocator.TempJob);
    try
    {
      for (int i = 0; i < 100; i++)
      {
        inputs[i] = i;
      }
      SmartExecutionOptions options = new SmartExecutionOptions
      {
        BatchSizeOverride = 32,
        PreferredJobType = JobType.IJobParallelFor
      };
      yield return SmartExecution.ExecuteBurstFor(
          uniqueId: nameof(Start),
          itemCount: 100,
          burstForDelegate: ProcessMultipleItems,
          burstResultsDelegate: null,
          nonBurstResultsDelegate: ProcessNonBurstResults,
          inputs: inputs,
          outputs: outputs,
          options: options
      );
    }
    finally
    {
      if (inputs.IsCreated) inputs.Dispose();
      if (outputs.IsCreated) outputs.Dispose();
    }
  }
}
```

- **Example 4: ExecuteTransforms(Transform Operations)**

```csharp
public class TransformExample
{
  [BurstCompile]
  private static void ProcessTransform(int index, TransformAccess transform)
  {
    transform.position += Vector3.up * 0.1f;
  }

  [BurstCompile]
  private static void ProcessMainThreadTransform(int index, Transform transform)
  {
    transform.position += Vector3.up * 0.1f;
  }

public static IEnumerator Start()
  {
    TransformAccessArray transforms = default;
    GameObject[] gameObjects = null;
    try
    {
      int transformCount = 10;
      transforms = new TransformAccessArray(transformCount);
      gameObjects = new GameObject[transformCount];
      for (int i = 0; i < transformCount; i++)
      {
        gameObjects[i] = new GameObject($"TestObject_{i}");
        transforms.Add(gameObjects[i].transform);
      }
      SmartExecutionOptions options = SmartExecutionOptions.Default;
      yield return SmartExecution.ExecuteTransforms<int>(
          uniqueId: nameof(Start),
          transforms: transforms,
          burstTransformDelegate: ProcessTransform,
          burstMainThreadTransformDelegate: ProcessMainThreadTransform,
          inputs: default,
          options: options
      );
    }
    finally
    {
      if (transforms.isCreated) transforms.Dispose();
      if (gameObjects != null)
      {
        foreach (var go in gameObjects)
        {
          if (go != null) Object.Destroy(go);
        }
      }
    }
  }
}
```
