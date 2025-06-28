using System.Collections;
using FishNet.Managing.Timing;
using NoLazyWorkers.Storage;
using ScheduleOne.Delivery;
using ScheduleOne.ItemFramework;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Storage.ShelfExtensions;

namespace NoLazyWorkers.Extensions
{
  /// <summary>
  /// Provides extension methods for FishNet integration, including tick waiting and batch size calculation.
  /// </summary>
  public static class FishNetExtensions
  {
    /// <summary>
    /// Indicates whether the current context is a server.
    /// </summary>
    public static bool IsServer;

    /// <summary>
    /// Reference to the FishNet TimeManager instance.
    /// </summary>
    public static TimeManager TimeManagerInstance;

    private static readonly Dictionary<TimeManager, Dictionary<double, TaskCompletionSource<bool>>> _tickAwaiters = new();

    /// <summary>
    /// Asynchronously waits for the next FishNet TimeManager tick.
    /// </summary>
    /// <returns>A Task that completes on the next tick.</returns>
    private static Task AwaitNextTickAsyncInternal()
    {
      var tick = TimeManagerInstance.Tick;
      var awaiters = _tickAwaiters.GetOrAdd(TimeManagerInstance, _ => new Dictionary<double, TaskCompletionSource<bool>>());
      if (awaiters.TryGetValue(tick + 1, out var tcs))
        return tcs.Task;

      tcs = new TaskCompletionSource<bool>();
      if (!awaiters.TryAdd(tick + 1, tcs))
        return awaiters[tick + 1].Task;

      void OnTick()
      {
        if (TimeManagerInstance.Tick <= tick) return;
        TimeManagerInstance.OnTick -= OnTick;
        awaiters.Remove(tick + 1);
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
#if DEBUG
        Log(Level.Verbose, $"AwaitNextTickAsync: Waiting for {seconds}s ({ticksToWait} ticks) at tick rate {TimeManagerInstance.TickRate}", Category.Tasks);
#endif

        for (int i = 0; i < ticksToWait; i++)
        {
          await AwaitNextTickAsyncInternal();
        }
      }
      else
      {
        await AwaitNextTickAsyncInternal();
      }
    }

    /// <summary>
    /// Waits for the next FishNet TimeManager tick in a coroutine.
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
  /// Provides extension methods for converting Tasks to Unity coroutines.
  /// </summary>
  public static class TaskExtensions
  {
    /// <summary>
    /// Converts a Task to a coroutine.
    /// </summary>
    /// <param name="task">The Task to convert.</param>
    /// <returns>A TaskYieldInstruction for the coroutine.</returns>
    public static TaskYieldInstruction AsCoroutine(this Task task) => new TaskYieldInstruction(task);

    /// <summary>
    /// Converts a Task with a result to a coroutine.
    /// </summary>
    /// <typeparam name="T">The result type of the Task.</typeparam>
    /// <param name="task">The Task to convert.</param>
    /// <returns>A TaskYieldInstruction for the coroutine.</returns>
    public static TaskYieldInstruction<T> AsCoroutine<T>(this Task<T> task) => new TaskYieldInstruction<T>(task);

    /// <summary>
    /// A custom yield instruction for awaiting a Task in a Unity coroutine.
    /// </summary>
    public class TaskYieldInstruction : CustomYieldInstruction
    {
      private readonly Task _task;

      /// <summary>
      /// Initializes a new instance of the TaskYieldInstruction class.
      /// </summary>
      /// <param name="task">The Task to await.</param>
      /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
      public TaskYieldInstruction(Task task)
      {
        _task = task ?? throw new ArgumentNullException(nameof(task));
      }

      /// <summary>
      /// Gets whether the coroutine should continue waiting.
      /// </summary>
      public override bool keepWaiting => !_task.IsCompleted;
    }

    /// <summary>
    /// A custom yield instruction for awaiting a Task with a result in a Unity coroutine.
    /// </summary>
    /// <typeparam name="T">The result type of the Task.</typeparam>
    public class TaskYieldInstruction<T> : CustomYieldInstruction
    {
      private readonly Task<T> _task;

      /// <summary>
      /// Initializes a new instance of the TaskYieldInstruction class.
      /// </summary>
      /// <param name="task">The Task to await.</param>
      /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
      public TaskYieldInstruction(Task<T> task)
      {
        _task = task ?? throw new ArgumentNullException(nameof(task));
      }

      /// <summary>
      /// Gets whether the coroutine should continue waiting.
      /// </summary>
      public override bool keepWaiting => !_task.IsCompleted;

      /// <summary>
      /// Gets the result of the Task if completed; otherwise, returns default(T).
      /// </summary>
      public T Result => _task.IsCompleted ? _task.Result : default;
    }
  }

  public static class NativeListExtensions
  {
    /// <summary>
    /// Removes the first occurrence of the specified item from the NativeList.
    /// </summary>
    /// <param name="list">The NativeList to modify.</param>
    /// <param name="item">The item to remove.</param>
    /// <returns>True if the item was found and removed; otherwise, false.</returns>
    [BurstCompile]
    public static bool Remove<T>(this NativeList<T> list, T item) where T : unmanaged, IEquatable<T>
    {
      for (int i = 0; i < list.Length; i++)
      {
        if (list[i].Equals(item))
        {
          list.RemoveAt(i);
          return true;
        }
      }
      return false;
    }
  }

  public static class ItemSlotExtensions
  {
    public static SlotKey GetSlotKey(this ItemSlot itemSlot)
    {
      Guid guid;
      switch (itemSlot.SlotOwner)
      {
        case PlaceableStorageEntity storage:
          guid = storage.GUID;
          break;
        case IStationAdapter station:
          guid = station.GUID;
          break;
        case LoadingDock dock:
          guid = dock.GUID;
          break;
        case IEmployeeAdapter employee:
          guid = employee.Guid;
          break;
        default:
          return default;
      }
      return new SlotKey(guid, itemSlot.SlotIndex);
    }

    public static Property GetProperty(this ItemSlot itemSlot)
    {
      switch (itemSlot.SlotOwner)
      {
        case PlaceableStorageEntity storage:
          return storage.ParentProperty;
        case IStationAdapter station:
          return station.ParentProperty;
        case LoadingDock dock:
          return dock.ParentProperty;
        case IEmployeeAdapter employee:
          return employee.AssignedProperty;
        default:
          return null;
      }
    }

    public static StorageType OwnerType(this ItemSlot itemSlot)
    {
      switch (itemSlot.SlotOwner)
      {
        case PlaceableStorageEntity storage:
          var property = storage.ParentProperty;
          return ManagedDictionaries.StorageConfigs.TryGetValue(property, out var configs) &&
                 configs.TryGetValue(storage.GUID, out var config) && config.Mode == StorageMode.Specific
                 ? StorageType.SpecificShelf
                 : StorageType.AnyShelf;
        case IStationAdapter:
          return StorageType.Station;
        case LoadingDock:
          return StorageType.LoadingDock;
        case IEmployeeAdapter:
          return StorageType.Employee;
        default:
          return default;
      }
    }
  }

  public static class DictionaryExtensions
  {
    /// <summary>
    /// Gets the value associated with the specified key, or adds a new value using the provided factory if the key doesn't exist.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    /// <param name="dictionary">The dictionary to operate on.</param>
    /// <param name="key">The key to look up or add.</param>
    /// <param name="valueFactory">The function to create a new value if the key is not found.</param>
    /// <returns>The existing or newly added value.</returns>
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> valueFactory)
    {
      if (dictionary == null)
        throw new ArgumentNullException(nameof(dictionary));
      if (key == null)
        throw new ArgumentNullException(nameof(key));
      if (valueFactory == null)
        throw new ArgumentNullException(nameof(valueFactory));

      if (dictionary.TryGetValue(key, out var value))
        return value;

      value = valueFactory(key);
      dictionary[key] = value;
      return value;
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
    /// Runs a coroutine on the CoroutineRunner instance.
    /// </summary>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <returns>The started Coroutine object.</returns>
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
    /// <returns>The started Coroutine object.</returns>
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
          {
            callback?.Invoke(default);
            yield break;
          }
          current = coroutine.Current;
        }
        catch (Exception e)
        {
          Log(Level.Warning, $"CoroutineRunner: Exception in coroutine: {e.Message}", Category.Core);
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