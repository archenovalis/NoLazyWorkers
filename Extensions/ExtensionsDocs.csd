/// <summary>
/// Provides extension methods for FishNet integration, including tick waiting and batch size calculation.
/// </summary>
public static class FishNetExtensions
{
    /// <summary>
    /// Asynchronously waits for the next FishNet TimeManager tick.
    /// </summary>
    /// <returns>A Task that completes on the next tick.</returns>
    private static Task AwaitNextTickAsyncInternal()

    /// <summary>
    /// Asynchronously waits for the specified duration in seconds, aligned with FishNet TimeManager ticks.
    /// </summary>
    /// <param name="seconds">The duration to wait in seconds (default is 0).</param>
    /// <returns>A Task that completes after the specified delay.</returns>
    public static async Task AwaitNextFishNetTickAsync(float seconds = 0)

    /// <summary>
    /// Waits for the next FishNet TimeManager tick in a coroutine.
    /// </summary>
    /// <returns>An enumerator that yields until the next tick.</returns>
    public static IEnumerator WaitForNextTick()

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
    public static TaskYieldInstruction AsCoroutine(this Task task)

    /// <summary>
    /// Converts a Task with a result to a coroutine.
    /// </summary>
    /// <typeparam name="T">The result type of the Task.</typeparam>
    /// <param name="task">The Task to convert.</param>
    /// <returns>A TaskYieldInstruction for the coroutine.</returns>
    public static TaskYieldInstruction<T> AsCoroutine<T>(this Task<T> task)

    /// <summary>
    /// A custom yield instruction for awaiting a Task in a Unity coroutine.
    /// </summary>
    public class TaskYieldInstruction : CustomYieldInstruction
    {
        /// <summary>
        /// Initializes a new instance of the TaskYieldInstruction class.
        /// </summary>
        /// <param name="task">The Task to await.</param>
        /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
        public TaskYieldInstruction(Task task)

    }

    /// <summary>
    /// A custom yield instruction for awaiting a Task with a result in a Unity coroutine.
    /// </summary>
    /// <typeparam name="T">The result type of the Task.</typeparam>
    public class TaskYieldInstruction : CustomYieldInstruction
    {
        /// <summary>
        /// Initializes a new instance of the TaskYieldInstruction class.
        /// </summary>
        /// <param name="task">The Task to await.</param>
        /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
        public TaskYieldInstruction(Task<T> task)

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

}

public static class ItemSlotExtensions
{
    public static SlotKey GetSlotKey(this ItemSlot itemSlot)

    public static Property GetProperty(this ItemSlot itemSlot)

    public static StorageType OwnerType(this ItemSlot itemSlot)

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

}

/// <summary>
/// Manages coroutine execution in Unity with singleton behavior.
/// </summary>
public class CoroutineRunner : MonoBehaviour
{
    /// <summary>
    /// Runs a coroutine on the CoroutineRunner instance.
    /// </summary>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <returns>The started Coroutine object.</returns>
    public Coroutine RunCoroutine(IEnumerator coroutine)

    /// <summary>
    /// Runs a coroutine internally, handling exceptions.
    /// </summary>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private IEnumerator RunCoroutineInternal(IEnumerator coroutine)

    /// <summary>
    /// Runs a coroutine with a result callback.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <param name="callback">The callback to invoke with the result.</param>
    /// <returns>The started Coroutine object.</returns>
    public Coroutine RunCoroutineWithResult<T>(IEnumerator coroutine, Action<T> callback)

    /// <summary>
    /// Runs a coroutine internally with a result callback, handling exceptions.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="coroutine">The coroutine to run.</param>
    /// <param name="callback">The callback to invoke with the result.</param>
    /// <returns>An enumerator for the coroutine.</returns>
    private IEnumerator RunCoroutineInternal<T>(IEnumerator coroutine, Action<T> callback)

}
