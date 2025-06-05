using System.Collections;
using FishNet;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Property;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using static NoLazyWorkers.TaskService.Extensions;
using ScheduleOne;
using ScheduleOne.Product;
using static NoLazyWorkers.TaskService.TaskRegistry;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Movement.Utilities;
using static NoLazyWorkers.TaskService.StationTasks.PackagingStationTasks.Constants;

namespace NoLazyWorkers.TaskService.StationTasks
{
  public static class PackagingStationTasks
  {
    public static List<ITaskDefinition> Register()
    {
      return new List<ITaskDefinition>
      {
        new StateValidationTaskDef(),
        new UnpackageBaggiesTaskDef(),
        new FetchPackagingTaskDef(),
        new StartPackagingTaskDef(),
        new DeliverOutputTaskDef()
      };
    }

    public static class Constants
    {
      public static readonly string JAR_ITEM_ID = "jar";
      public static readonly string BAGGIE_ITEM_ID = "baggie";
      public const int JAR_QUANTITY_THRESHOLD = 5;
      public const float UNPACKAGING_TIME = 3f;
      public const float OPERATION_TIMEOUT_SECONDS = 30f;
    }

    // StateValidation Task
    public class StateValidationTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagingStationState;
      public int Priority => 5;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new StateValidationValidator();
      public ITaskExecutor Executor { get; } = new StateValidationExecutor();
    }

    public class PackagingStationEntitySelector : IEntitySelector
    {
      public NativeList<EntityKey> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<EntityKey>(allocator);
        if (Stations.Extensions.IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values.OfType<PackagingStationAdapter>().Where(s => s.TransitEntity is PackagingStation))
            entities.Add(new EntityKey { Guid = station.GUID, Type = TransitTypes.PackagingStation });
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Selected {entities.Length} packaging stations for property {property.name}", DebugLogger.Category.PackagingStation);
        return entities;
      }
    }

    [BurstCompile]
    private class StateValidationValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StateValidationValidator: No valid station for GUID {entityKey.Guid}", DebugLogger.Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            ItemKey.Empty,
            0,
            definition.PickupType,
            entityKey.Guid,
            Array.Empty<int>(),
            definition.DropoffType,
            entityKey.Guid,
            Array.Empty<int>(),
            context.CurrentTime
        );

        validTasks.Add(task);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StateValidationValidator: Created task {task.TaskId} for station {entityKey.Guid}", DebugLogger.Category.PackagingStation);
      }
    }

    public class StateValidationExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StateValidationExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.PackagingStation);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StateValidationExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StateValidationExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"StateValidationExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.PackagingStation);
            return;
          }

          if (((IUsable)station).IsInUse)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StateValidationExecutor: Station {station.GUID} is in use", DebugLogger.Category.PackagingStation);
            return;
          }

          state.EmployeeState.TaskContext = new TaskContext { Station = stationAdapter };
          var (isReady, nextStep, packagingId, productItem) = await Utilities.IsStationReady(station, packager, state);
          TaskTypes? followUpTask = null;

          if (!isReady)
          {
            if (nextStep == "Unpackage")
              followUpTask = TaskTypes.PackagingStationUnpackage;
            else if (nextStep == "Fetch" || nextStep == "Refill")
              followUpTask = TaskTypes.PackagingStationFetchPackaging;
          }
          else if (station.ProductSlot?.ItemInstance != null && station.ProductSlot.Quantity > 0)
          {
            followUpTask = TaskTypes.PackagingStationOperate;
          }
          else if (station.OutputSlot?.ItemInstance != null && station.OutputSlot.Quantity > 0)
          {
            followUpTask = TaskTypes.PackagingStationDeliver;
          }

          if (followUpTask.HasValue)
          {
            await TaskDefinitionRegistry.Get(followUpTask.Value).EnqueueFollowUpTasksAsync(packager, task);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"StateValidationExecutor: Enqueued follow-up task {followUpTask.Value} for station {station.GUID}", DebugLogger.Category.PackagingStation);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StateValidationExecutor: No follow-up tasks for station {station.GUID}", DebugLogger.Category.PackagingStation);
          }
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StateValidationExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.PackagingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"StateValidationExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        }
      }
    }

    // UnpackageBaggies Task
    public class UnpackageBaggiesTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagingStationUnpackage;
      public int Priority => 4;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new UnpackageBaggiesValidator();
      public ITaskExecutor Executor { get; } = new UnpackageBaggiesExecutor();
      public TaskTypes FollowUpTask => TaskTypes.PackagingStationState;
    }

    [BurstCompile]
    public class UnpackageBaggiesValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UnpackageBaggiesValidator: No valid station for GUID {entityKey.Guid}", DebugLogger.Category.PackagingStation);
          return;
        }

        if (((IUsable)station).IsInUse)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UnpackageBaggiesValidator: Station {station.GUID} is in use", DebugLogger.Category.PackagingStation);
          return;
        }

        var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
        if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UnpackageBaggiesValidator: No product in station {station.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        if (!Utilities.CheckBaggieUnpackaging(station, null, productSlot.ItemInstance))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UnpackageBaggiesValidator: No baggies to unpackage for station {station.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(productSlot.ItemInstance),
            productSlot.Quantity,
            definition.PickupType,
            entityKey.Guid,
            new[] { productSlot.SlotIndex },
            definition.DropoffType,
            entityKey.Guid,
            new[] { productSlot.SlotIndex },
            context.CurrentTime
        );

        validTasks.Add(task);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"UnpackageBaggiesValidator: Created task {task.TaskId} for station {entityKey.Guid}", DebugLogger.Category.PackagingStation);
      }
    }

    public class UnpackageBaggiesExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"UnpackageBaggiesExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.PackagingStation);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"UnpackageBaggiesExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UnpackageBaggiesExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"UnpackageBaggiesExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.PackagingStation);
            return;
          }

          if (!Utilities.CheckBaggieUnpackaging(station, packager, CreateItemInstance(task.Item)))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"UnpackageBaggiesExecutor: No baggies to unpackage for station {station.GUID}", DebugLogger.Category.PackagingStation);
            return;
          }

          packager.PackagingBehaviour.PackagingInProgress = true;
          packager.Avatar.Anim.SetBool("UsePackagingStation", true);
          float unpackageTime = Constants.UNPACKAGING_TIME / packager.PackagingSpeedMultiplier * station.PackagerEmployeeSpeedMultiplier;
          float elapsed = 0f;

          while (elapsed < unpackageTime)
          {
            packager.Avatar.LookController.OverrideLookTarget(station.Container.position, 0);
            elapsed += Time.deltaTime;
            await InstanceFinder.TimeManager.AwaitNextTickAsync();
          }

          packager.Avatar.Anim.SetBool("UsePackagingStation", false);
          if (InstanceFinder.IsServer)
            station.Unpack();

          packager.PackagingBehaviour.PackagingInProgress = false;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UnpackageBaggiesExecutor: Completed unpackaging for station {station.GUID}", DebugLogger.Category.PackagingStation);
          await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"UnpackageBaggiesExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.PackagingStation);
          packager.PackagingBehaviour.PackagingInProgress = false;
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"UnpackageBaggiesExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        }
      }
    }

    // FetchPackaging Task
    public class FetchPackagingTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagingStationFetchPackaging;
      public int Priority => 3;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PlaceableStorageEntity;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new FetchPackagingValidator();
      public ITaskExecutor Executor { get; } = new FetchPackagingExecutor();
      public TaskTypes FollowUpTask => TaskTypes.PackagingStationState;
    }

    [BurstCompile]
    public class FetchPackagingValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: No valid station for GUID {entityKey.Guid}", DebugLogger.Category.PackagingStation);
          return;
        }

        if (((IUsable)station).IsInUse)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: Station {station.GUID} is in use", DebugLogger.Category.PackagingStation);
          return;
        }

        var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
        if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: No product in station {station.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var (canPackage, packagingId, needsUnpack, needsBaggieSwap) = Utilities.CheckPackagingAvailability(station, null, productSlot.Quantity, productSlot.ItemInstance).Result;
        if (canPackage || needsUnpack)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: Station {station.GUID} can package or needs unpack", DebugLogger.Category.PackagingStation);
          return;
        }

        var item = Registry.GetItem(packagingId).GetDefaultInstance();
        var shelf = FindStorageWithItem(null, item, 1);
        if (shelf.Key == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: No shelf for packaging {packagingId}", DebugLogger.Category.PackagingStation);
          return;
        }

        var sourceSlots = GetOutputSlotsContainingItem(shelf.Key, item);
        if (sourceSlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: No source slots for {packagingId} on shelf {shelf.Key.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var slotKey = new SlotKey(shelf.Key.GUID, sourceSlots[0].SlotIndex);
        if (context.ReservedSlots.ContainsKey(slotKey))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FetchPackagingValidator: Slot {slotKey} already reserved", DebugLogger.Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(item),
            1,
            definition.PickupType,
            shelf.Key.GUID,
            sourceSlots.Select(s => s.SlotIndex).ToArray(),
            definition.DropoffType,
            entityKey.Guid,
            new[] { station.PackagingSlot.SlotIndex },
            context.CurrentTime
        );

        validTasks.Add(task);
        context.ReservedSlots.Add(slotKey, new SlotReservation { TaskId = task.TaskId, Timestamp = context.CurrentTime });
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FetchPackagingValidator: Created task {task.TaskId} for {packagingId} to station {entityKey.Guid}", DebugLogger.Category.PackagingStation);
      }
    }

    public class FetchPackagingExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"FetchPackagingExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.PackagingStation);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FetchPackagingExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FetchPackagingExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.DropoffGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station ||
              !Storages[packager.AssignedProperty].TryGetValue(task.PickupGuid, out var shelf))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"FetchPackagingExecutor: Invalid entities for task {task.TaskId}", DebugLogger.Category.PackagingStation);
            return;
          }

          var item = CreateItemInstance(task.Item);
          var sourceSlots = GetOutputSlotsContainingItem(shelf, item);
          if (sourceSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FetchPackagingExecutor: No source slots for {task.Item.Id} on shelf {shelf.GUID}", DebugLogger.Category.PackagingStation);
            return;
          }

          var deliverySlots = new List<ItemSlot> { station.PackagingSlot };
          if (deliverySlots[0].ItemInstance != null && !item.AdvCanStackWith(deliverySlots[0].ItemInstance))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FetchPackagingExecutor: Incompatible item in packaging slot for station {station.GUID}", DebugLogger.Category.PackagingStation);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || item.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FetchPackagingExecutor: No inventory slot for {packager.fullName}", DebugLogger.Category.PackagingStation);
            return;
          }

          foreach (var pickupSlot in sourceSlots)
          {
            var slotKey = new SlotKey(shelf.GUID, pickupSlot.SlotIndex);
            if (!SlotManager.ReserveSlot(slotKey, task.TaskId, Time.time))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FetchPackagingExecutor: Failed to reserve slot {slotKey} for task {task.TaskId}", DebugLogger.Category.PackagingStation);
              continue;
            }
            pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          }

          deliverySlots[0].ApplyLock(packager.NetworkObject, "dropoff");
          var request = TransferRequest.Get(packager, item, task.Quantity, inventorySlot, shelf, sourceSlots, station, deliverySlots);
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"FetchPackagingExecutor: Successfully fetched {task.Quantity} {task.Item.Id} for station {station.GUID}", DebugLogger.Category.PackagingStation);
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"FetchPackagingExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.PackagingStation);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"FetchPackagingExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.PackagingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"FetchPackagingExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        }
      }
    }

    // StartPackaging Task
    public class StartPackagingTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagingStationOperate;
      public int Priority => 3;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new StartPackagingValidator();
      public ITaskExecutor Executor { get; } = new StartPackagingExecutor();
      public TaskTypes FollowUpTask => TaskTypes.PackagingStationState;
    }

    [BurstCompile]
    public class StartPackagingValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StartPackagingValidator: No valid station for GUID {entityKey.Guid}", DebugLogger.Category.PackagingStation);
          return;
        }

        if (((IUsable)station).IsInUse)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StartPackagingValidator: Station {station.GUID} is in use", DebugLogger.Category.PackagingStation);
          return;
        }

        var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
        if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StartPackagingValidator: No product in station {station.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var (canPackage, _, _, _) = Utilities.CheckPackagingAvailability(station, null, productSlot.Quantity, productSlot.ItemInstance).Result;
        if (!canPackage)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StartPackagingValidator: Station {station.GUID} not ready for packaging", DebugLogger.Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(productSlot.ItemInstance),
            productSlot.Quantity,
            definition.PickupType,
            entityKey.Guid,
            new[] { productSlot.SlotIndex },
            definition.DropoffType,
            entityKey.Guid,
            new[] { station.OutputSlot.SlotIndex },
            context.CurrentTime
        );

        validTasks.Add(task);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartPackagingValidator: Created task {task.TaskId} for station {entityKey.Guid}", DebugLogger.Category.PackagingStation);
      }
    }

    public class StartPackagingExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartPackagingExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.PackagingStation);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartPackagingExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StartPackagingExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartPackagingExecutor: Invalid station {task.PickupGuid}", DebugLogger.Category.PackagingStation);
            return;
          }

          var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
          if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StartPackagingExecutor: No product in station {station.GUID}", DebugLogger.Category.PackagingStation);
            return;
          }

          var (canPackage, packagingId, _, _) = await Utilities.CheckPackagingAvailability(station, packager, productSlot.Quantity, productSlot.ItemInstance);
          if (!canPackage)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StartPackagingExecutor: Station {station.GUID} not ready for packaging", DebugLogger.Category.PackagingStation);
            return;
          }

          packager.PackagingBehaviour.BeginPackaging();
          float timeout = 0f;
          while (packager.PackagingBehaviour.PackagingInProgress && timeout < Constants.OPERATION_TIMEOUT_SECONDS)
          {
            await InstanceFinder.TimeManager.AwaitNextTickAsync();
            timeout += Time.deltaTime;
          }

          if (packager.PackagingBehaviour.PackagingInProgress)
          {
            packager.PackagingBehaviour.StopPackaging();
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartPackagingExecutor: Timed out for station {station.GUID}", DebugLogger.Category.PackagingStation);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartPackagingExecutor: Completed packaging for station {station.GUID}", DebugLogger.Category.PackagingStation);
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartPackagingExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.PackagingStation);
          packager.PackagingBehaviour.PackagingInProgress = false;
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartPackagingExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        }
      }
    }

    // DeliverOutput Task
    public class DeliverOutputTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagingStationDeliver;
      public int Priority => 2;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PlaceableStorageEntity;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new DeliverOutputValidator();
      public ITaskExecutor Executor { get; } = new DeliverOutputExecutor();
      public TaskTypes FollowUpTask => TaskTypes.PackagingStationState;
    }

    [BurstCompile]
    public class DeliverOutputValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(entityKey.Guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverOutputValidator: No valid station for GUID {entityKey.Guid}", DebugLogger.Category.PackagingStation);
          return;
        }

        var outputSlot = station.OutputSlot;
        if (outputSlot == null || outputSlot.Quantity <= 0 || outputSlot.ItemInstance == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverOutputValidator: No output in station {station.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var shelf = FindStorageWithItem(null, outputSlot.ItemInstance, outputSlot.Quantity);
        if (shelf.Key == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverOutputValidator: No shelf for item {outputSlot.ItemInstance.ID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var deliverySlots = shelf.Key.InputSlots.AdvReserveInputSlotsForItem(outputSlot.ItemInstance, null);
        if (deliverySlots == null || deliverySlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverOutputValidator: No delivery slots at {shelf.Key.GUID}", DebugLogger.Category.PackagingStation);
          return;
        }

        var slotKey = new SlotKey(shelf.Key.GUID, deliverySlots[0].SlotIndex);
        if (context.ReservedSlots.ContainsKey(slotKey))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"DeliverOutputValidator: Slot {slotKey} already reserved", DebugLogger.Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(outputSlot.ItemInstance),
            outputSlot.Quantity,
            definition.PickupType,
            entityKey.Guid,
            new[] { outputSlot.SlotIndex },
            definition.DropoffType,
            shelf.Key.GUID,
            deliverySlots.Select(s => s.SlotIndex).ToArray(),
            context.CurrentTime
        );

        validTasks.Add(task);
        context.ReservedSlots.Add(slotKey, new SlotReservation { TaskId = task.TaskId, Timestamp = context.CurrentTime });
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverOutputValidator: Created task {task.TaskId} for item {outputSlot.ItemInstance.ID} to shelf {shelf.Key.GUID}", DebugLogger.Category.PackagingStation);
      }
    }

    public class DeliverOutputExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverOutputExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.PackagingStation);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverOutputExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverOutputExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station ||
              !Storages[packager.AssignedProperty].TryGetValue(task.DropoffGuid, out var shelf))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverOutputExecutor: Invalid entities for task {task.TaskId}", DebugLogger.Category.PackagingStation);
            return;
          }

          var outputSlot = station.OutputSlot;
          if (outputSlot == null || outputSlot.Quantity <= 0 || outputSlot.ItemInstance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverOutputExecutor: No output in station {station.GUID}", DebugLogger.Category.PackagingStation);
            return;
          }

          int[] indexes = [task.DropoffSlotIndex1, task.DropoffSlotIndex2, task.DropoffSlotIndex3];
          var deliverySlots = shelf.InputSlots.Where(s => indexes.Contains(s.SlotIndex)).ToList();
          if (deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverOutputExecutor: No delivery slots at {shelf.GUID}", DebugLogger.Category.PackagingStation);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || outputSlot.ItemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverOutputExecutor: No inventory slot for {packager.fullName}", DebugLogger.Category.PackagingStation);
            return;
          }

          outputSlot.ApplyLock(packager.NetworkObject, "pickup");
          foreach (var deliverySlot in deliverySlots)
          {
            var slotKey = new SlotKey(shelf.GUID, deliverySlot.SlotIndex);
            if (!SlotManager.ReserveSlot(slotKey, task.TaskId, Time.time))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverOutputExecutor: Failed to reserve slot {slotKey} for task {task.TaskId}", DebugLogger.Category.PackagingStation);
              continue;
            }
            deliverySlot.ApplyLock(packager.NetworkObject, "dropoff");
          }

          var request = TransferRequest.Get(packager, outputSlot.ItemInstance, outputSlot.Quantity, inventorySlot, station, new List<ItemSlot> { outputSlot }, shelf, deliverySlots);
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverOutputExecutor: Successfully delivered {outputSlot.Quantity} {outputSlot.ItemInstance.ID} to shelf {shelf.GUID}", DebugLogger.Category.PackagingStation);
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverOutputExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.PackagingStation);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"DeliverOutputExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.PackagingStation);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverOutputExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.PackagingStation);
        }
      }
    }

    public static class Utilities
    {
      // Checks if station is ready for packaging or needs action
      public static async Task<(bool isReady, string nextStep, string packagingId, ItemInstance productItem)> IsStationReady(PackagingStation station, Packager packager, EmployeeStateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
        $"IsStationReady: Checking station={station?.GUID.ToString() ?? "null"} for packager={packager?.fullName ?? "null"}",
        DebugLogger.Category.Handler);
        if (station == null || packager == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "IsStationReady: Invalid station or packager", DebugLogger.Category.Handler);
          return (false, null, null, null);
        }
        int productCount = station.ProductSlot?.Quantity ?? 0;
        ItemInstance productItem = station.ProductSlot?.ItemInstance;
        if (productCount == 0 || productItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsStationReady: No product in station={station.GUID}",
          DebugLogger.Category.Handler);
          return (false, null, null, null);
        }
        // Check packaging availability and determine action
        var packagingResult = await CheckPackagingAvailability(station, packager, productCount, productItem);
        if (packagingResult.canPackage)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsStationReady: Station={station.GUID} ready for packaging with {packagingResult.packagingId}",
          DebugLogger.Category.Handler);
          return (true, "Success", packagingResult.packagingId, productItem);
        }
        if (packagingResult.needsUnpack)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsStationReady: Station={station.GUID} needs baggie unpackaging",
          DebugLogger.Category.Handler);
          return (false, "Unpackage", JAR_ITEM_ID, productItem);
        }
        if (packagingResult.needsBaggieSwap)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsStationReady: Station={station.GUID} needs baggie swap",
          DebugLogger.Category.Handler);
          return (false, "Fetch", BAGGIE_ITEM_ID, productItem);
        }
        if (productCount < productItem.StackLimit)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsStationReady: Station={station.GUID} needs product refill for {productItem.ID}",
          DebugLogger.Category.Handler);
          return (false, "Refill", null, productItem);
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
        $"IsStationReady: Station={station.GUID} needs packaging {packagingResult.packagingId}",
        DebugLogger.Category.Handler);
        return (false, "Fetch", packagingResult.packagingId, productItem);
      }

      public static async Task<(bool canPackage, string packagingId, bool needsUnpack, bool needsBaggieSwap)> CheckPackagingAvailability(PackagingStation station, Packager packager, int productCount, ItemInstance productItem)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckPackagingAvailability: Checking for station={station?.GUID.ToString() ?? "null"}, productCount={productCount}", DebugLogger.Category.PackagingStation);
        bool preferJars = productCount >= Constants.JAR_QUANTITY_THRESHOLD;
        bool hasJars = false;
        bool hasBaggies = false;
        bool needsBaggieSwap = false;
        string requiredPackagingId = preferJars ? Constants.JAR_ITEM_ID : Constants.BAGGIE_ITEM_ID;

        if (station.PackagingSlot?.Quantity > 0)
        {
          if (station.PackagingSlot.ItemInstance.ID == Constants.JAR_ITEM_ID)
            hasJars = true;
          else if (station.PackagingSlot.ItemInstance.ID == Constants.BAGGIE_ITEM_ID)
            hasBaggies = true;
        }

        if (preferJars && productCount < Constants.JAR_QUANTITY_THRESHOLD)
        {
          if (CheckBaggieUnpackaging(station, packager, productItem))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckPackagingAvailability: Baggie unpackaging needed for station={station.GUID}", DebugLogger.Category.PackagingStation);
            return (false, Constants.JAR_ITEM_ID, true, false);
          }

          int neededForJars = Constants.JAR_QUANTITY_THRESHOLD - productCount;
          var shelf = FindStorageWithItem(packager, productItem, neededForJars);
          if (shelf.Key != null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckPackagingAvailability: Found shelf={shelf.Key.GUID} with {shelf.Value} of {productItem.ID}", DebugLogger.Category.PackagingStation);
            return (false, Constants.JAR_ITEM_ID, false, false);
          }

          if (hasJars)
          {
            needsBaggieSwap = true;
            requiredPackagingId = Constants.BAGGIE_ITEM_ID;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckPackagingAvailability: Swapping jars for baggies in station={station.GUID}", DebugLogger.Category.PackagingStation);
          }
          preferJars = false;
        }

        if (preferJars && hasJars)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckPackagingAvailability: Using jars for station={station.GUID}", DebugLogger.Category.PackagingStation);
          return (true, Constants.JAR_ITEM_ID, false, false);
        }

        if (!preferJars && hasBaggies)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckPackagingAvailability: Using baggies for station={station.GUID}", DebugLogger.Category.PackagingStation);
          return (true, Constants.BAGGIE_ITEM_ID, false, false);
        }

        var packagingItem = Registry.GetItem(requiredPackagingId).GetDefaultInstance();
        var packagingShelf = FindStorageWithItem(packager, packagingItem, 1);
        if (packagingShelf.Key != null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckPackagingAvailability: Found shelf={packagingShelf.Key.GUID} with {requiredPackagingId}", DebugLogger.Category.PackagingStation);
          return (false, requiredPackagingId, false, needsBaggieSwap);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"CheckPackagingAvailability: No packaging available for {requiredPackagingId} in station={station.GUID}", DebugLogger.Category.PackagingStation);
        return (false, requiredPackagingId, false, needsBaggieSwap);
      }

      public static bool CheckBaggieUnpackaging(PackagingStation station, Packager packager, ItemInstance targetProduct)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckBaggieUnpackaging: Checking for station={station?.GUID.ToString() ?? "null"}", DebugLogger.Category.PackagingStation);
        var packagingSlot = station.PackagingSlot;
        if (packagingSlot == null || packagingSlot.Quantity < 1 || (packagingSlot.ItemInstance as ProductItemInstance)?.AppliedPackaging?.ID != Constants.BAGGIE_ITEM_ID)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckBaggieUnpackaging: No valid baggies in station={station.GUID}", DebugLogger.Category.PackagingStation);
          return false;
        }

        int currentQuantity = station.ProductSlot.Quantity;
        int stackLimit = targetProduct?.StackLimit ?? 20;
        int targetQuantity = Math.Min(stackLimit, ((currentQuantity / Constants.JAR_QUANTITY_THRESHOLD) + 1) * Constants.JAR_QUANTITY_THRESHOLD);
        int neededQuantity = targetQuantity - currentQuantity;
        int availableBaggies = packagingSlot.Quantity;

        if (neededQuantity <= 0 || availableBaggies < 1)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckBaggieUnpackaging: No unpack needed, neededQuantity={neededQuantity}, availableBaggies={availableBaggies}", DebugLogger.Category.PackagingStation);
          return false;
        }

        int unpackCount = Math.Min(availableBaggies, (neededQuantity + Constants.JAR_QUANTITY_THRESHOLD - 1) / Constants.JAR_QUANTITY_THRESHOLD);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckBaggieUnpackaging: Can unpackage {unpackCount} baggies for station={station.GUID}, adding {unpackCount * Constants.JAR_QUANTITY_THRESHOLD} product", DebugLogger.Category.PackagingStation);
        return true;
      }

      public static async Task<bool> InitiatePackagingRetrieval(PackagingStation station, Packager packager, string itemId, EmployeeStateData state, ItemInstance productItem = null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InitiatePackagingRetrieval: Retrieving itemId={itemId} for station={station?.GUID.ToString() ?? "null"}", DebugLogger.Category.PackagingStation);
        ItemInstance item = productItem ?? Registry.GetItem(itemId).GetDefaultInstance();
        int quantityNeeded = productItem != null ? Math.Max(1, item.StackLimit - (station.ProductSlot?.Quantity ?? 0)) : 1;
        var shelf = FindStorageWithItem(packager, item, quantityNeeded);
        if (shelf.Key == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"InitiatePackagingRetrieval: No shelf for item={item.ID}", DebugLogger.Category.PackagingStation);
          return false;
        }

        var sourceSlots = GetOutputSlotsContainingItem(shelf.Key, item);
        if (sourceSlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"InitiatePackagingRetrieval: No source slots for item={item.ID} on shelf={shelf.Key.GUID}", DebugLogger.Category.PackagingStation);
          return false;
        }

        var deliverySlots = new List<ItemSlot> { station.PackagingSlot };
        if (deliverySlots[0].ItemInstance != null && !item.AdvCanStackWith(deliverySlots[0].ItemInstance))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"InitiatePackagingRetrieval: Incompatible item in packaging slot for station={station.GUID}", DebugLogger.Category.PackagingStation);
          return false;
        }

        int quantity = Math.Min(shelf.Value, quantityNeeded);
        if (quantity <= 0 || packager.Inventory.HowManyCanFit(item) < quantity)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"InitiatePackagingRetrieval: Invalid quantity={quantity} or insufficient inventory for item={item.ID}", DebugLogger.Category.PackagingStation);
          return false;
        }

        var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
        if (inventorySlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"InitiatePackagingRetrieval: No inventory slot for packager={packager.fullName}", DebugLogger.Category.PackagingStation);
          return false;
        }

        foreach (var pickupSlot in sourceSlots)
        {
          var slotKey = new SlotKey(shelf.Key.GUID, pickupSlot.SlotIndex);
          if (!SlotManager.ReserveSlot(slotKey, state.EmployeeState.TaskContext?.Task.TaskId ?? Guid.NewGuid(), Time.time))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"InitiatePackagingRetrieval: Failed to reserve slot {slotKey}", DebugLogger.Category.PackagingStation);
            continue;
          }
          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
        }

        deliverySlots[0].ApplyLock(packager.NetworkObject, "dropoff");
        var request = TransferRequest.Get(packager, item, quantity, inventorySlot, shelf.Key, sourceSlots, station, deliverySlots);
        state.EmployeeState.TaskContext = state.EmployeeState.TaskContext ?? new TaskContext();
        state.EmployeeState.TaskContext.Requests = new List<TransferRequest> { request };
        state.EmployeeState.TaskContext.Item = item;

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitiatePackagingRetrieval: Initiated transfer of {quantity} {item.ID} to station={station.GUID}", DebugLogger.Category.PackagingStation);
        return true;
      }
    }
  }
}