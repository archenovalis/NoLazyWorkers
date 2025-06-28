# API Documentation

**Date:** June 2025-06-25

## FishNetExtensions

Extension methods for FishNet integration, focusing on tick-based timing.

### Fields

- **IsServer** (`bool`): Indicates if the current context is a server.
- **TimeManagerInstance** (`TimeManager`): Reference to the FishNet TimeManager instance.

### Methods

#### `Task AwaitNextFishNetTickAsync(float seconds = 0)`

- **Description**: Asynchronously waits for a specified duration in seconds, aligned with FishNet TimeManager ticks.
- **Parameters**:
  - `seconds`: Duration to wait in seconds (`default: 0). Negative values are invalid and log a warning.
- **Returns**: A `Task` that completes after the specified delay.

- **Example**:

  ```csharp
  async Task Example() {
      await FishNetExtensions.WaitForNextFishNetTickAsync(1.5f); // Wait for 1.5 seconds
  }
  ```

#### `IEnumerator WaitForNextTick()`)

- **Description**: Waits for the next FishNet TimeManager tick in a coroutine.
- **Returns**: An `IEnumerator` that yields until the next tick.

- **Example**:

  ```csharp
  IEnumerator Example() {
      yield return FishNetExtensions.WaitForNextTick();
      Debug.Log("Next tick reached");
  }
  ```

---

## TaskExtensions

Extension methods for converting `Task` to Unity coroutines.

### Methods

#### `TaskYieldInstruction AsCoroutine(this Task task)`

- **Description**: Converts a `Task` to a coroutine.
- **Parameters**:
  - `task`: The `Task` to convert.
- **Returns**: A `TaskYieldInstruction` for use in coroutines.

- **Example**:

  ```csharp
  IEnumerator Example() {
      var task = Task.Delay(1000);
      yield return task.AsCoroutine();
      Debug.Log("Task completed");
  }
  ```

#### `TaskYieldInstruction<T> AsCoroutine<T>(this Task<T> task)`

- **Description**: Converts a `Task` with a result to a coroutine.
- **Generic Type**:
  - `T`: The result type of the task.
- **Parameters**:
  - `task`: The Task<T> to convert.
- **Returns**: A `TaskYieldInstruction<T>` for use in coroutines.

- **Example**:

  ```csharp
  async Task<int> GetValueAsync() => { 42;
  IEnumerator Example() {
      var task = GetTaskValueAsync();
      yield return task.AsCoroutine();
      Debug.Log($"Result: {task.AsCoroutine().Result}");
  }
  ```

### Classes

#### `TaskYieldInstruction`

Custom yield instruction for awaiting a `Task` in a coroutine.

- **Constructor**: `TaskYieldInstruction(Task task)`
  - **Parameters**: `task`: The Task to await. Throws `ArgumentNullException` if null.
- **Properties**:
  - `keepWaiting` (`bool`): Indicates if the coroutine should continue waiting (returns `true` if task is not completed).

- **Example**:

  ```csharp
  IEnumerator Example() {
      var task = Task.Delay(1000);
      yield return new TaskYieldInstruction(task);
      Debug.Log("Task completed");
  }
  ```

#### `TaskYieldInstruction<T>`

Custom yield instruction for awaiting a `Task` with a result in a coroutine.

- **Generic Type**:
  - `T`: The result type of the task.
- **Constructor**: `TaskYieldInstruction<T>(T task(Task<T>)`
  - **Parameters**: Initializes with the task.
    - `task`: The `Task<T>` to await. Throws `ArgumentNullException` if null.
- **Properties**:
  - `keepWaiting` (`bool`): Indicates if the coroutine should continue waiting (returns `true` if task is not completed).
  - `Result` (`T`): Returns the task’s result if completed; otherwise, `default(T)`.

- **Example**:

  ```csharp
  async Task<int> GetValueAsync() => { 42; };
  IEnumerator Example() => {
      var task = GetValueAsync();
      var yieldInstruction = new TaskYieldInstruction<int>(task);
      yield return yieldInstruction;
      Debug.Log($"Result: {yieldInstruction.Result}");
  }
  ```

---

## CoroutineRunner

Singleton class for managing coroutines in Unity.

### Properties

- **Instance** (`CoroutineRunner`): Gets the singleton instance, creating it if it doesn't exist doesn't.

### Methods

#### `Coroutine RunCoroutine(IEnumerator coroutine)`

- **Description**: Runs a coroutine on the CoroutineRunner instance.
- **Parameters**:
  - `coroutine`: The coroutine to run.
- **Returns**: The started `Coroutine` object.

- **Example**:

  ```csharp
  IEnumerator Example() {
      yield return null;
      Debug.Log("Coroutine ran");
  }
  CoroutineRunner.Instance.RunCoroutine(Example())
  ```

#### `Coroutine RunCoroutineWithResult<T>(IEnumerator<T> coroutine, Action<T> callback)`

- **Description**: Runs a coroutine with a result callback.
- **Generic Type**:
  - `T`: The type of the result.
- **Parameters**:
  - `coroutine`: The coroutine to run.
  - `callback`: The callback to invoke with the result.
- **Returns**: The started `Coroutine` object.

- **Example**:

  ```csharp
  IEnumerator Example() {
      yield return null;
      yield return 42;
  }
  CoroutineRunner.Instance.RunCoroutineWithResult<int>(Example, result => Debug.Log($"Result: {result}"))
  ```
