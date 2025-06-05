using FishNet;
using ScheduleOne.Employees;
using UnityEngine;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using System.Collections.Concurrent;
using Unity.Jobs;
using Unity.Collections;
using ScheduleOne.Property;
using Unity.Burst;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.TaskService.Extensions;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Utilities;
using ScheduleOne.Management;
using ScheduleOne.Delivery;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.TaskService.TaskRegistry;

namespace NoLazyWorkers.TaskService
{
  /// <summary>
  /// Manages task processing for a property, integrating with FishNet's TimeManager.
  /// </summary>
  public class TaskService
  {
    private readonly Property _property;
    private bool _isActive;
    private bool _isProcessing;
    private readonly ConcurrentQueue<(TaskDescriptor, int)> _pendingTasks = new();
    private readonly ConcurrentDictionary<string, TaskService> _activeTasks = new();
    private static readonly ConcurrentQueue<(Property, int)> _pendingValidations = new();
    private static readonly ConcurrentDictionary<string, float> _lastValidationTimes = new();
    private static readonly SemaphoreSlim _validationSemaphore = new(4); // Limit concurrent validations
    private const float VALIDATION_INTERVAL = 5f;
    private int _currentPriority;

    public TaskService(Property property)
    {
      _property = property ?? throw new ArgumentNullException(nameof(property));
      _isActive = false;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"TaskService initialized for property {_property.name}", DebugLogger.Category.TaskManager);
    }

    public void Activate()
    {
      if (_isActive) return;
      if (InstanceFinder.IsServer)
        InstanceFinder.TimeManager.OnTick += ProcessPendingTasksSync;
      _isActive = true;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{_property.name}] TaskService activated", DebugLogger.Category.TaskManager);
    }

    public void Deactivate()
    {
      if (!_isActive) return;
      _isActive = false;
      Dispose();
      _pendingTasks.Clear();
      _activeTasks.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{_property.name}] TaskService deactivated", DebugLogger.Category.TaskManager);
    }

    public void SubmitPriorityTask(Employee employee, TaskDescriptor task, bool isEmployeeInitiated = false)
    {
      if (!_isActive || employee.AssignedProperty != _property)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"SubmitPriorityTask: Invalid submission by {employee.fullName} for property {_property.name}", DebugLogger.Category.TaskManager);
        return;
      }

      var modifiedTask = TaskDescriptor.Create(
          task.Type,
          task.EmployeeType,
          task.Priority,
          task.PropertyName.ToString(),
          task.Item,
          task.Quantity,
          task.PickupType,
          task.PickupGuid,
          new[] { task.PickupSlotIndex1, task.PickupSlotIndex2, task.PickupSlotIndex3 }.Take(task.PickupSlotCount).ToArray(),
          task.DropoffType,
          task.DropoffGuid,
          new[] { task.DropoffSlotIndex1, task.DropoffSlotIndex2, task.DropoffSlotIndex3 }.Take(task.DropoffSlotCount).ToArray(),
          Time.time,
          isEmployeeInitiated ? employee.GUID : default,
          isEmployeeInitiated
      );

      EnqueueTaskAsync(modifiedTask, modifiedTask.Priority).GetAwaiter().GetResult(); // Synchronous for compatibility
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{_property.name}] Priority task {task.TaskId} submitted by {employee.fullName}, initiated: {isEmployeeInitiated}", DebugLogger.Category.TaskManager);
    }

    public async Task EnqueueTaskAsync(TaskDescriptor task, int priority)
    {
      if (!_isActive)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"[{_property.name}] TaskService is inactive, cannot enqueue task {task.TaskId}", DebugLogger.Category.TaskManager);
        return;
      }

      string uniqueKey = task.UniqueKey.ToString();
      if (_activeTasks.TryGetValue(uniqueKey, out var existingService))
      {
        if (_pendingTasks.Any(t => t.Item1.UniqueKey.ToString() == uniqueKey && t.Item2 < priority))
        {
          _activeTasks.TryRemove(uniqueKey, out _);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{_property.name}] Replaced task {task.TaskId} with higher priority {priority} (key: {uniqueKey})", DebugLogger.Category.TaskManager);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"[{_property.name}] Skipped duplicate task {task.TaskId} (key: {uniqueKey})", DebugLogger.Category.TaskManager);
          return;
        }
      }

      if (!_activeTasks.TryAdd(uniqueKey, this))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"[{_property.name}] Failed to add task {task.TaskId} to active tasks (key: {uniqueKey})", DebugLogger.Category.TaskManager);
        return;
      }

      _pendingTasks.Enqueue((task, priority));
      UpdatePropertyPriority(priority);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"[{_property.name}] Enqueued task {task.TaskId} with priority {priority} (key: {uniqueKey})", DebugLogger.Category.TaskManager);
      await InstanceFinder.TimeManager.AwaitNextTickAsync();
    }

    public void EnqueueValidation(Property property, int priority = 50)
    {
      if (!_isActive)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"[{_property.name}] TaskService is inactive, cannot enqueue validation", DebugLogger.Category.TaskManager);
        return;
      }

      _pendingValidations.Enqueue((property, priority));
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"[{_property.name}] Enqueued validation with priority {priority}", DebugLogger.Category.TaskManager);
    }

    public async Task<(bool Success, TaskDescriptor Task, string Error)> TryGetTaskAsync(Employee employee)
    {
      TaskDescriptor task = default;
      if (!_isActive || employee.AssignedProperty != _property)
        return (false, task, $"Invalid service or property for {employee.fullName}");

      try
      {
        var employeeType = Enum.Parse<EmployeeTypes>(employee.Type.ToString());
        var state = EmployeeBehaviour.GetState(employee);
        var sortedTasks = new List<(TaskDescriptor, int)>();
        while (_pendingTasks.TryDequeue(out var candidate))
          sortedTasks.Add(candidate);

        sortedTasks.Sort((a, b) => b.Item2.CompareTo(a.Item2));

        foreach (var (candidateTask, priority) in sortedTasks)
        {
          if (candidateTask.EmployeeType != employeeType && candidateTask.EmployeeType != EmployeeTypes.Any)
          {
            _pendingTasks.Enqueue((candidateTask, priority));
            continue;
          }

          if (candidateTask.IsFollowUp && candidateTask.FollowUpEmployeeGUID != employee.GUID)
          {
            _pendingTasks.Enqueue((candidateTask, priority));
            continue;
          }

          if (candidateTask.IsValid(employee) && await TaskValidationService.ReValidateTaskAsync(employee, state, candidateTask))
          {
            task = candidateTask;
            _activeTasks.TryRemove(candidateTask.UniqueKey.ToString(), out _);
            UpdatePropertyPriority();
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{_property.name}] Assigned task {task.TaskId} to {employee.fullName} (key: {task.UniqueKey})", DebugLogger.Category.TaskManager);
            return (true, task, null);
          }

          _pendingTasks.Enqueue((candidateTask, priority));
        }

        return (false, task, "No valid task found");
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"TryGetTaskAsync: Failed for {employee.fullName} - {ex}", DebugLogger.Category.TaskManager);
        return (false, task, ex.ToString());
      }
    }

    private void ProcessPendingTasksSync()
    {
      if (!_isActive || !InstanceFinder.IsServer)
        return;
      _ = ProcessPendingValidationsAsync(); // Fire and forget
    }

    private async Task ProcessPendingValidationsAsync()
    {
      if (!_isActive || _isProcessing || !InstanceFinder.IsServer)
        return;
      _isProcessing = true;
      try
      {
        await _validationSemaphore.WaitAsync();
        var sortedValidations = new List<(Property, int)>();
        while (_pendingValidations.TryDequeue(out var validation))
          sortedValidations.Add(validation);
        sortedValidations.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        foreach (var (property, priority) in sortedValidations)
        {
          float dynamicInterval = GetDynamicValidationInterval(property);
          if (_lastValidationTimes.TryGetValue(property.name, out var lastTime) && Time.time < lastTime + dynamicInterval)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"[{property.name}] Skipped validation due to interval, last: {lastTime:F2}, interval: {dynamicInterval:F2}", DebugLogger.Category.TaskManager);
            continue;
          }
          var tasks = await ValidateProperty(property);
          foreach (var t in tasks)
            await EnqueueTaskAsync(t, t.Priority);
          _lastValidationTimes[property.name] = Time.time;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{property.name}] Processed validation, priority: {priority}, tasks: {tasks.Length}", DebugLogger.Category.TaskManager);
          await InstanceFinder.TimeManager.AwaitNextTickAsync();
        }
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ProcessPendingValidations: Failed - {ex}", DebugLogger.Category.TaskManager);
      }
      finally
      {
        _isProcessing = false;
        _validationSemaphore.Release();
      }
    }

    private float GetDynamicValidationInterval(Property property)
    {
      int entityCount = property.Employees.Count + (IStations.TryGetValue(property, out var stations) ? stations.Count : 0);
      float loadFactor = InstanceFinder.TimeManager.TickRate / 60f; // Normalize based on tick rate
      float queueFactor = Mathf.Clamp(_pendingTasks.Count / 50f, 0.5f, 2f); // Scale with queue size
      return Mathf.Clamp(VALIDATION_INTERVAL * (entityCount / 50f) * loadFactor * queueFactor, 2f, 10f);
    }

    private async Task<NativeList<TaskDescriptor>> ValidateProperty(Property property)
    {
      var validTasks = new NativeList<TaskDescriptor>(Allocator.TempJob);
      try
      {
        (bool got, List<IStationAdapter> stations, List<PlaceableStorageEntity> storages) = await CacheManager.TryGetPropertyDataAsync(property);
        if (!got)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"[{property.name}] No property data for validation", DebugLogger.Category.TaskManager);
          return validTasks;
        }
        var entities = new NativeList<EntityKey>(Allocator.TempJob);
        var context = new TaskValidatorContext
        {
          AssignedPropertyName = property.name,
          CurrentTime = Time.time,
          ReservedSlots = new NativeParallelHashMap<SlotKey, SlotReservation>(100, Allocator.TempJob),
          SpecificShelves = new NativeParallelHashMap<ItemKey, NativeList<StorageKey>>(10, Allocator.TempJob),
          StorageInputSlots = new NativeParallelHashMap<StorageKey, NativeList<SlotData>>(10, Allocator.TempJob),
          StationInputSlots = new NativeParallelHashMap<StorageKey, NativeList<SlotData>>(10, Allocator.TempJob),
          Task = default // Initialize with default task
        };
        foreach (var station in stations)
          entities.Add(new EntityKey { Guid = station.GUID, Type = TransitTypes.MixingStation });
        foreach (var storage in storages)
          entities.Add(new EntityKey { Guid = storage.GUID, Type = TransitTypes.PlaceableStorageEntity });
        foreach (var employee in property.Employees)
          entities.Add(new EntityKey { Guid = employee.GUID, Type = TransitTypes.Inventory });
        foreach (var definition in TaskDefinitionRegistry.AllDefinitions)
        {
          var job = new TaskValidationJob
          {
            Definition = definition,
            Entities = entities,
            Context = context,
            ValidTasks = validTasks,
            Property = property
          };
          var jobHandle = job.Schedule(entities.Length, 64);
          await InstanceFinder.TimeManager.AwaitNextTickAsync();
          jobHandle.Complete();
        }
        entities.Dispose();
        context.ReservedSlots.Dispose();
        context.SpecificShelves.Dispose();
        context.StorageInputSlots.Dispose();
        context.StationInputSlots.Dispose();
        return validTasks;
      }
      catch (Exception ex)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ValidateProperty: Failed for {property.name} - {ex}", DebugLogger.Category.TaskManager);
        validTasks.Dispose();
        return new NativeList<TaskDescriptor>(Allocator.TempJob);
      }
    }

    private void UpdatePropertyPriority(int priority = 0)
    {
      _currentPriority = Math.Max(_currentPriority, priority);
      TaskServiceManager.UpdatePropertyPriority(_property, _currentPriority);
    }

    public void Dispose()
    {
      _isActive = false;
      if (InstanceFinder.IsServer)
        InstanceFinder.TimeManager.OnTick -= ProcessPendingTasksSync;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"TaskService disposed for property {_property.name}", DebugLogger.Category.TaskManager);
    }

    public void Cleanup()
    {
      Deactivate();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"[{_property.name}] TaskService cleaned up", DebugLogger.Category.TaskManager);
    }
  }

  public static class TaskServiceManager
  {
    private static readonly ConcurrentDictionary<Property, TaskService> _services = new();

    public static void Initialize()
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, "TaskServiceManager initialized", DebugLogger.Category.TaskManager);
    }

    public static TaskService GetOrCreateService(Property property)
    {
      return _services.GetOrAdd(property, p => new TaskService(p));
    }

    public static void ActivateProperty(Property property)
    {
      var service = GetOrCreateService(property);
      service.Activate();
    }

    public static void DeactivateProperty(Property property)
    {
      if (_services.TryGetValue(property, out var service))
        service.Deactivate();
    }

    public static void UpdatePropertyPriority(Property property, int priority)
    {
      if (_services.TryGetValue(property, out var service))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"TaskServiceManager: Updated priority for property {property.name} to {priority}", DebugLogger.Category.TaskManager);
      }
    }

    public static async Task<(bool Success, TaskDescriptor Task, string Error)> TryGetTask(Employee employee)
    {
      if (_services.TryGetValue(employee.AssignedProperty, out var service))
        return await service.TryGetTaskAsync(employee);
      return (false, default, $"No service found for property {employee.AssignedProperty.name}");
    }

    public static void Cleanup()
    {
      foreach (var service in _services.Values)
        service.Cleanup();
      _services.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, "TaskServiceManager cleaned up", DebugLogger.Category.TaskManager);
    }
  }

  public static class Extensions
  {
    public interface ITaskDefinition
    {
      TaskTypes Type { get; }
      int Priority { get; }
      EmployeeTypes EmployeeType { get; }
      bool RequiresPickup { get; }
      bool RequiresDropoff { get; }
      TransitTypes PickupType { get; }
      TransitTypes DropoffType { get; }
      IEntitySelector EntitySelector { get; }
      ITaskValidator Validator { get; }
      ITaskExecutor Executor { get; }
      public TaskTypes FollowUpTask => TaskTypes.NoFollowUp;
    }

    public static async Task EnqueueFollowUpTasksAsync(this ITaskDefinition definition, Employee employee, TaskDescriptor task)
    {
      var followUpTask = TaskDescriptor.Create(
          definition.FollowUpTask,
          definition.EmployeeType,
          TaskDefinitionRegistry.Get(definition.FollowUpTask).Priority,
          employee.AssignedProperty.name,
          ItemKey.Empty,
          0,
          task.PickupType,
          task.PickupGuid,
          new int[] { },
          task.DropoffType,
          task.DropoffGuid,
          new int[] { },
          Time.time,
          employee.GUID,
          true
      );
      await TaskServiceManager.GetOrCreateService(employee.AssignedProperty).EnqueueTaskAsync(followUpTask, followUpTask.Priority);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Enqueued follow-up task {definition.FollowUpTask} for {employee.fullName}", DebugLogger.Category.TaskManager);
    }

    public interface IEntitySelector
    {
      NativeList<EntityKey> SelectEntities(Property property, Allocator allocator);
    }

    public interface ITaskValidator
    {
      void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks);
    }

    public interface ITaskExecutor
    {
      Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task);
    }

    public enum TaskTypes
    {
      NoFollowUp,

      // Any Employee Tasks
      DeliverInventory,

      // Packager Tasks
      PackagerRefillStation,
      PackagerEmptyLoadingDock,
      PackagerRestock,

      // PackagingStation Tasks
      PackagingStationState,
      PackagingStationFetchPackaging,
      PackagingStationUnpackage,
      PackagingStationOperate,
      PackagingStationDeliver,

      // MixingStation Tasks
      MixingStationState,
      MixingStationOperate,
      MixingStationRestock,
      MixingStationLoop,
      MixingStationDeliver
    }

    public enum EmployeeTypes
    {
      Any,
      Chemist,
      Handler,
      Botanist,
      Driver,
      Cleaner
    }

    public enum NEQuality
    {
      Trash,
      Poor,
      Standard,
      Premium,
      Heavenly,
      None
    }

    public enum TransitTypes
    {
      Inventory, // Only invenotry
      LoadingDock, // Only loadingdock
      PlaceableStorageEntity, // Only storages
      AnyStation, // All Stations and storages
      MixingStation, // MixingStation/MixingStationMk2 and storages
      PackagingStation, // PackagingStation and storages
      BrickPress, // BrickPress and storages
      LabOven, // LabOven and storages
      Pot, // Pot and storages
      DryingRack, // DryingRack and storages
      ChemistryStation, // ChemistryStation and storages
      Cauldron // Cauldron and storages
    }
  }

  [BurstCompile]
  public struct TaskValidationJob : IJobParallelFor
  {
    public ITaskDefinition Definition;
    public TaskValidatorContext Context;
    public NativeList<EntityKey> Entities;
    public Property Property;
    public NativeList<TaskDescriptor> ValidTasks;

    public void Execute(int index)
    {
      var entityKey = Context.Entities[index];
      Definition.Validator.Validate(Definition, entityKey, Context, Property, ValidTasks);
    }
  }

  public struct TaskValidatorContext
  {
    public FixedString32Bytes AssignedPropertyName;
    public NativeParallelHashMap<SlotKey, SlotReservation> ReservedSlots;
    public NativeParallelHashMap<ItemKey, NativeList<StorageKey>> SpecificShelves;
    public NativeParallelHashMap<StorageKey, NativeList<SlotData>> StorageInputSlots;
    public NativeParallelHashMap<StorageKey, NativeList<SlotData>> StationInputSlots;
    public float CurrentTime;
    public NativeArray<EntityKey> Entities;
    public TaskDescriptor Task;
    public IStationAdapter StationAdapter;

  }

  public struct SlotData
  {
    public int SlotIndex;
    public ItemKey ItemKey;
    public int Quantity;
    public bool IsLocked;
  }

  public struct StorageKey : IEquatable<StorageKey>
  {
    public Guid Guid;
    public StorageKey(Guid guid) => Guid = guid != Guid.Empty ? guid : throw new ArgumentException("Invalid GUID");
    public bool Equals(StorageKey other) => Guid.Equals(other.Guid);
    public override bool Equals(object obj) => obj is StorageKey other && Equals(other);
    public override int GetHashCode() => Guid.GetHashCode();
  }

  public struct ItemKey : IEquatable<ItemKey>
  {
    public FixedString32Bytes Id;
    public FixedString32Bytes PackagingId;
    public NEQuality Quality;

    public static ItemKey Empty => new ItemKey("", "", NEQuality.None); // Added Empty property

    public ItemKey(ItemInstance item)
    {
      Id = item.ID ?? throw new ArgumentNullException(nameof(Id));
      PackagingId = (item as ProductItemInstance)?.AppliedPackaging?.ID ?? "";
      Quality = (item is ProductItemInstance prodItem) ? Enum.Parse<NEQuality>(prodItem.Quality.ToString()) : NEQuality.None;
    }

    public ItemKey(string id, string packagingId, NEQuality? quality)
    {
      Id = id ?? "";
      PackagingId = packagingId ?? "";
      Quality = quality ?? NEQuality.None;
    }

    public bool Equals(ItemKey other) =>
        Id == other.Id && PackagingId == other.PackagingId && Quality == other.Quality;

    public override bool Equals(object obj) => obj is ItemKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Id, PackagingId, Quality);
  }

  public struct SlotKey : IEquatable<SlotKey>
  {
    public Guid EntityGuid;
    public int SlotIndex;

    public SlotKey(Guid entityGuid, int slotIndex)
    {
      EntityGuid = entityGuid;
      SlotIndex = slotIndex;
    }

    public bool Equals(SlotKey other) => EntityGuid.Equals(other.EntityGuid) && SlotIndex == other.SlotIndex;
    public override bool Equals(object obj) => obj is SlotKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(EntityGuid, SlotIndex);
  }

  public struct SlotReservation
  {
    public Guid TaskId;
    public float Timestamp;
  }

  public struct EntityKey
  {
    public Guid Guid;
    public TransitTypes Type;

    public EntityKey(Guid guid, TransitTypes type)
    {
      Guid = guid;
      Type = type;
    }
  }

  [BurstCompile]
  /// <summary>
  /// Represents a task descriptor for processing within the TaskService.
  /// </summary>
  public struct TaskDescriptor : IDisposable
  {
    public Guid TaskId;
    public TaskTypes Type;
    public ItemKey Item;
    public TransitTypes PickupType;
    public Guid PickupGuid;
    public int PickupSlotIndex1;
    public int PickupSlotIndex2;
    public int PickupSlotIndex3;
    public int PickupSlotCount;
    public TransitTypes DropoffType;
    public Guid DropoffGuid;
    public int DropoffSlotIndex1;
    public int DropoffSlotIndex2;
    public int DropoffSlotIndex3;
    public int DropoffSlotCount;
    public int Quantity;
    public EmployeeTypes EmployeeType;
    public int Priority;
    public FixedString32Bytes PropertyName;
    public float CreationTime;
    public Guid FollowUpEmployeeGUID;
    public bool IsFollowUp;
    public FixedString128Bytes UniqueKey;
    public Guid EntityGuid;

    /// <summary>
    /// Creates a new task descriptor.
    /// </summary>
    public static TaskDescriptor Create(
    TaskTypes type, EmployeeTypes employeeType, int priority, string propertyName,
    ItemKey item, int quantity,
    TransitTypes pickupType, Guid pickupGUID, int[] pickupSlotIndices,
    TransitTypes dropoffType, Guid dropoffGUID, int[] dropoffSlotIndices,
    float creationTime,
    Guid followUpEmployeeGUID = default, bool isFollowUp = false,
    Guid entityGuid = default)
    {
      var descriptor = new TaskDescriptor
      {
        TaskId = Guid.NewGuid(),
        Type = type,
        EmployeeType = employeeType,
        PropertyName = propertyName,
        Priority = priority,
        Item = item,
        Quantity = quantity,
        PickupType = pickupType,
        PickupGuid = pickupGUID,
        PickupSlotCount = pickupSlotIndices?.Length ?? 0,
        DropoffType = dropoffType,
        DropoffGuid = dropoffGUID,
        DropoffSlotCount = dropoffSlotIndices?.Length ?? 0,
        CreationTime = creationTime,
        FollowUpEmployeeGUID = followUpEmployeeGUID,
        IsFollowUp = isFollowUp,
        EntityGuid = entityGuid
      };

      // Assign pickup slot indices
      if (pickupSlotIndices != null)
      {
        if (pickupSlotIndices.Length > 0) descriptor.PickupSlotIndex1 = pickupSlotIndices[0];
        if (pickupSlotIndices.Length > 1) descriptor.PickupSlotIndex2 = pickupSlotIndices[1];
        if (pickupSlotIndices.Length > 2) descriptor.PickupSlotIndex3 = pickupSlotIndices[2];
      }

      // Assign dropoff slot indices
      if (dropoffSlotIndices != null)
      {
        if (dropoffSlotIndices.Length > 0) descriptor.DropoffSlotIndex1 = dropoffSlotIndices[0];
        if (dropoffSlotIndices.Length > 1) descriptor.DropoffSlotIndex2 = dropoffSlotIndices[1];
        if (dropoffSlotIndices.Length > 2) descriptor.DropoffSlotIndex3 = dropoffSlotIndices[2];
      }

      // Generate UniqueKey
      descriptor.UniqueKey = GenerateUniqueKey(type, pickupGUID, dropoffGUID, isFollowUp, entityGuid);

      return descriptor;
    }

    private static FixedString128Bytes GenerateUniqueKey(TaskTypes type, Guid pickupGuid, Guid dropoffGuid, bool isFollowUp, Guid entityGuid)
    {
      var pickup = pickupGuid == Guid.Empty ? "none" : pickupGuid.ToString();
      var dropoff = dropoffGuid == Guid.Empty ? "none" : dropoffGuid.ToString();
      var entity = entityGuid == Guid.Empty ? "none" : entityGuid.ToString();
      return $"{type}:{pickup}:{dropoff}:{isFollowUp}:{entity}";
    }

    /// <summary>
    /// Checks if the task is valid for an employee.
    /// </summary>
    public bool IsValid(Employee employee)
    {
      if (PropertyName != employee.AssignedProperty.name) return false;
      if (IsFollowUp && employee.GUID != FollowUpEmployeeGUID)
        return false;

      if (employee.AssignedProperty.name != PropertyName ||
          (EmployeeType != EmployeeTypes.Any && Enum.Parse<EmployeeTypes>(employee.Type.ToString()) != EmployeeType))
        return false;

      var storages = Storages[employee.AssignedProperty];
      var stations = Stations.Extensions.IStations[employee.AssignedProperty];

      // Validate pickup entity
      if (PickupType != TransitTypes.Inventory && PickupGuid != Guid.Empty)
      {
        var pickupValid = PickupType == TransitTypes.PlaceableStorageEntity ? storages.ContainsKey(PickupGuid) :
            PickupType == TransitTypes.AnyStation ? (stations.ContainsKey(PickupGuid) || storages.ContainsKey(PickupGuid)) :
            stations.ContainsKey(PickupGuid) && EntityProvider.IsValidTransitType(stations[PickupGuid], PickupType);
        if (!pickupValid) return false;
      }

      // Validate dropoff entity
      if (DropoffGuid != Guid.Empty)
      {
        var dropoffValid = DropoffType == TransitTypes.PlaceableStorageEntity ? storages.ContainsKey(DropoffGuid) :
            DropoffType == TransitTypes.AnyStation ? (stations.ContainsKey(DropoffGuid) || storages.ContainsKey(DropoffGuid)) :
            stations.ContainsKey(DropoffGuid) && EntityProvider.IsValidTransitType(stations[DropoffGuid], DropoffType);
        if (!dropoffValid) return false;
      }

      if (!storages.TryGetValue(PickupGuid, out var pickup) && PickupGuid != Guid.Empty)
        return false;
      if (!storages.TryGetValue(DropoffGuid, out var dropoff) && DropoffGuid != Guid.Empty)
        return false;

      // Validate pickup slots
      if (PickupSlotCount > 0 && pickup != null)
      {
        var itemInstance = CreateItemInstance(Item);
        int totalQuantity = 0;
        for (int i = 0; i < PickupSlotCount; i++)
        {
          int slotIndex = i switch { 0 => PickupSlotIndex1, 1 => PickupSlotIndex2, 2 => PickupSlotIndex3, _ => 0 };
          var pickupSlot = pickup.OutputSlots.FirstOrDefault(s => s.SlotIndex == slotIndex);
          if (pickupSlot == null || pickupSlot.IsLocked || pickupSlot.ItemInstance == null || !pickupSlot.ItemInstance.AdvCanStackWith(itemInstance))
            return false;
          totalQuantity += pickupSlot.Quantity;
        }
        if (totalQuantity < Quantity)
          return false;
      }

      // Validate dropoff slots
      if (DropoffSlotCount > 0 && dropoff != null)
      {
        var itemInstance = CreateItemInstance(Item);
        for (int i = 0; i < DropoffSlotCount; i++)
        {
          int slotIndex = i switch { 0 => DropoffSlotIndex1, 1 => DropoffSlotIndex2, 2 => DropoffSlotIndex3, _ => 0 };
          var dropoffSlot = dropoff.InputSlots.FirstOrDefault(s => s.SlotIndex == slotIndex);
          if (dropoffSlot == null || dropoffSlot.IsLocked || (dropoffSlot.ItemInstance != null && !dropoffSlot.ItemInstance.AdvCanStackWith(itemInstance)))
            return false;
        }
      }

      // Validate employee inventory
      int requiredSlots = PickupSlotCount > 0 ? PickupSlotCount : 1;
      int freeSlots = employee.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      return freeSlots >= requiredSlots;
    }
    public void Dispose()
    {
    }
  }

  public static class TaskValidationService
  {
    public static async Task<bool> ReValidateTaskAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
    {
      if (employee == null || state == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "ReValidateTask: Employee or state is null", DebugLogger.Category.Handler);
        return false;
      }

      var context = state.EmployeeState.TaskContext;
      if (context?.Task.Type != task.Type)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Invalid task for {employee.fullName}, expected {task.Type}, got {context?.Task.Type}", DebugLogger.Category.Handler);
        return false;
      }

      var property = employee.AssignedProperty;

      ITransitEntity pickup = task.PickupType == TransitTypes.Inventory ? null : EntityProvider.ResolveEntity(property, task.PickupGuid, task.PickupType) as ITransitEntity;
      if (pickup == null && task.PickupType != TransitTypes.Inventory)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Task {task.TaskId} ({task.Type}) has invalid pickup entity {task.PickupGuid}", DebugLogger.Category.Handler);
        return false;
      }

      var dropoff = EntityProvider.ResolveEntity(property, task.DropoffGuid, task.DropoffType);
      if (dropoff == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Task {task.TaskId} ({task.Type}) has invalid dropoff entity {task.DropoffGuid}", DebugLogger.Category.Handler);
        return false;
      }

      var itemInstance = CreateItemInstance(task.Item);
      var pickupSlots = new ItemSlot[task.PickupSlotCount];
      int totalPickupQuantity = 0;
      if (pickup != null && task.PickupSlotCount > 0)
      {
        var pickupSlotsSource = pickup.OutputSlots;
        for (int i = 0; i < task.PickupSlotCount; i++)
        {
          int slotIndex = i switch { 0 => task.PickupSlotIndex1, 1 => task.PickupSlotIndex2, 2 => task.PickupSlotIndex3, _ => 0 };
          var pickupSlot = pickupSlotsSource.FirstOrDefault(s => s.SlotIndex == slotIndex);
          if (pickupSlot == null || pickupSlot.IsLocked || pickupSlot.Quantity < 1 || !pickupSlot.ItemInstance.AdvCanStackWith(itemInstance))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Task {task.TaskId} ({task.Type}) has invalid pickup slot {slotIndex}", DebugLogger.Category.Handler);
            return false;
          }
          pickupSlots[i] = pickupSlot;
          totalPickupQuantity += pickupSlot.Quantity;
        }
        if (totalPickupQuantity < task.Quantity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Task {task.TaskId} ({task.Type}) has insufficient pickup quantity {totalPickupQuantity}/{task.Quantity}", DebugLogger.Category.Handler);
          return false;
        }
      }

      var dropoffSlotsSource = dropoff is ITransitEntity t ? t.InputSlots : (dropoff as IStationAdapter)?.ProductSlots ?? [];
      var dropoffSlots = new ItemSlot[task.DropoffSlotCount];
      for (int i = 0; i < task.DropoffSlotCount; i++)
      {
        int slotIndex = i switch { 0 => task.DropoffSlotIndex1, 1 => task.DropoffSlotIndex2, 2 => task.DropoffSlotIndex3, _ => 0 };
        var dropoffSlot = dropoffSlotsSource.FirstOrDefault(s => s.SlotIndex == slotIndex);
        if (dropoffSlot == null || dropoffSlot.IsLocked || (dropoffSlot.ItemInstance != null && !dropoffSlot.ItemInstance.AdvCanStackWith(itemInstance)))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Task {task.TaskId} has invalid dropoff slot {slotIndex}", DebugLogger.Category.Handler);
          return false;
        }
        dropoffSlots[i] = dropoffSlot;
      }

      int requiredSlots = task.PickupSlotCount > 0 ? task.PickupSlotCount : 1;
      int freeSlots = employee.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
      if (freeSlots < requiredSlots)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReValidateTask: Task {task.TaskId} has insufficient inventory slots ({freeSlots}/{requiredSlots})", DebugLogger.Category.Handler);
        return false;
      }

      foreach (var slot in pickupSlots.Where(s => s != null))
      {
        var slotKey = new SlotKey(task.PickupGuid, slot.SlotIndex);
        if (!SlotManager.ReserveSlot(slotKey, task.TaskId, Time.time))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Failed to reserve pickup slot {slot.SlotIndex} for task {task.TaskId}", DebugLogger.Category.Handler);
          return false;
        }
        slot.ApplyLock(employee.NetworkObject, "pickup");
        SetReservedSlot(employee, slot);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Locked pickup slot {slot.SlotIndex} for task {task.TaskId}", DebugLogger.Category.Handler);
      }
      foreach (var slot in dropoffSlots)
      {
        var slotKey = new SlotKey(task.DropoffGuid, slot.SlotIndex);
        if (!SlotManager.ReserveSlot(slotKey, task.TaskId, Time.time))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ReValidateTask: Failed to reserve dropoff slot {slot.SlotIndex} for task {task.TaskId}", DebugLogger.Category.Handler);
          return false;
        }
        slot.ApplyLock(employee.NetworkObject, "dropoff");
        SetReservedSlot(employee, slot);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Locked dropoff slot {slot.SlotIndex} for task {task.TaskId}", DebugLogger.Category.Handler);
      }

      context.Pickup = pickup;
      context.Dropoff = dropoff as ITransitEntity;
      context.Station = dropoff as IStationAdapter ?? (task.DropoffType is TransitTypes.AnyStation or TransitTypes.PackagingStation ? state.Station : null);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ReValidateTask: Task {task.TaskId} ({task.Type}) validated for {employee.fullName}", DebugLogger.Category.Handler);
      await InstanceFinder.TimeManager.AwaitNextTickAsync();
      return true;
    }
  }

  public static class SlotManager
  {
    public static NativeParallelHashMap<SlotKey, SlotReservation> Reservations { get; private set; }

    public static void Initialize()
    {
      Reservations = new NativeParallelHashMap<SlotKey, SlotReservation>(1000, Allocator.Persistent);
      DebugLogger.Log(DebugLogger.LogLevel.Info, "SlotManager initialized", DebugLogger.Category.TaskManager);
    }

    public static bool ReserveSlot(SlotKey slotKey, Guid taskId, float timestamp)
    {
      if (Reservations.ContainsKey(slotKey))
        return false;
      Reservations.Add(slotKey, new SlotReservation { TaskId = taskId, Timestamp = timestamp });
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Reserved slot {slotKey} for task {taskId}", DebugLogger.Category.TaskManager);
      return true;
    }

    public static void ReleaseSlot(SlotKey slotKey)
    {
      if (Reservations.ContainsKey(slotKey))
      {
        Reservations.Remove(slotKey);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Released slot {slotKey}", DebugLogger.Category.TaskManager);
      }
    }

    public static void ReleaseExpiredReservations(float currentTime)
    {
      var expired = Reservations.Where(kvp => kvp.Value.Timestamp + 30f < currentTime).ToList();
      foreach (var kvp in expired)
      {
        Reservations.Remove(kvp.Key);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Released expired slot {kvp.Key} for task {kvp.Value.TaskId}", DebugLogger.Category.TaskManager);
      }
    }

    public static void Cleanup()
    {
      if (Reservations.IsCreated)
        Reservations.Dispose();
      DebugLogger.Log(DebugLogger.LogLevel.Info, "SlotManager cleaned up", DebugLogger.Category.TaskManager);
    }
  }

  public static class EntityProvider
  {
    public static object ResolveEntity(Property property, Guid guid, TransitTypes expectedType)
    {
      if (Storages.TryGetValue(property, out var storages) && storages.TryGetValue(guid, out var storage))
        return IsValidTransitType(storage, expectedType) ? storage : null;

      if (IStations.TryGetValue(property, out var stations) && stations.TryGetValue(guid, out var station))
        return IsValidTransitType(station, expectedType) ? station : null;

      var loadingDock = property.LoadingDocks.FirstOrDefault(d => d.GUID == guid);
      if (loadingDock != null && IsValidTransitType(loadingDock, expectedType))
        return loadingDock;

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ResolveEntity: No entity found for GUID {guid} with type {expectedType}", DebugLogger.Category.Handler);
      return null;
    }

    public static NativeList<EntityKey> GetStations(Property property, Allocator allocator)
    {
      var stations = IStations.TryGetValue(property, out var list) ? list : null;
      var result = new NativeList<EntityKey>(allocator);
      if (stations != null)
        foreach (var station in stations.Values)
          result.Add(new EntityKey(station.GUID, TransitTypes.AnyStation));
      return result;
    }

    public static NativeList<EntityKey> GetDocks(Property property, Allocator allocator)
    {
      var result = new NativeList<EntityKey>(allocator);
      foreach (var dock in property.LoadingDocks)
        result.Add(new EntityKey(dock.GUID, TransitTypes.LoadingDock));
      return result;
    }

    public static NativeList<EntityKey> GetShelves(Property property, Allocator allocator)
    {
      var result = new NativeList<EntityKey>(allocator);
      if (Storages.TryGetValue(property, out var storages))
        foreach (var shelf in storages.Keys)
          result.Add(new EntityKey(shelf, TransitTypes.PlaceableStorageEntity));
      return result;
    }

    public static bool IsValidTransitType(object entity, TransitTypes type)
    {
      if (entity == null)
        return false;

      if (entity is IStationAdapter station)
      {
        return type switch
        {
          TransitTypes.AnyStation => true,
          TransitTypes.PackagingStation => station.TypeOf == typeof(PackagingStation),
          TransitTypes.MixingStation => station.TypeOf == typeof(MixingStation) || station.TypeOf == typeof(MixingStationMk2),
          TransitTypes.BrickPress => station.TypeOf == typeof(BrickPress),
          TransitTypes.LabOven => station.TypeOf == typeof(LabOven),
          TransitTypes.Pot => station.TypeOf == typeof(Pot),
          TransitTypes.DryingRack => station.TypeOf == typeof(DryingRack),
          TransitTypes.ChemistryStation => station.TypeOf == typeof(ChemistryStation),
          TransitTypes.Cauldron => station.TypeOf == typeof(Cauldron),
          _ => false
        };
      }

      var entityType = entity.GetType();
      return type switch
      {
        TransitTypes.PlaceableStorageEntity => entityType == typeof(PlaceableStorageEntity),
        TransitTypes.LoadingDock => entityType == typeof(LoadingDock),
        _ => false
      };
    }
  }
}