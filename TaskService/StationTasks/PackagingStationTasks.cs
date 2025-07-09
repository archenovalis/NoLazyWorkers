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
using System.Threading.Tasks;

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
      public TaskName Type => TaskName.PackagingStationState;
      public int Priority => 5;
      public TaskEmployeeType EmployeeType => TaskEmployeeType.Handler;
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
      public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<Guid>(allocator);
        if (Stations.Extensions.IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values.OfType<PackagingStationAdapter>().Where(s => s.TransitEntity is PackagingStation))
            entities.Add(station.GUID);
        }
        Log(Level.Verbose, $"Selected {entities.Length} packaging stations for property {property.name}", Category.PackagingStation);
        return entities;
      }
    }

    [BurstCompile]
    private class StateValidationValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation)
        {
          Log(Level.Verbose, $"StateValidationValidator: No valid station for GUID {guid}", Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            guid,
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            ItemData.Empty,
            0,
            definition.PickupType,
            guid,
            Array.Empty<int>(),
            definition.DropoffType,
            guid,
            Array.Empty<int>(),
            context.CurrentTime
        );

        validTasks.Add(task);
        Log(Level.Info, $"StateValidationValidator: Created task {task.TaskId} for station {guid}", Category.PackagingStation);
      }
    }

    public class StateValidationExecutor : ITaskExecutor
    {
      private Guid _entityGuid;
      public async Task ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"StateValidationExecutor: Employee {employee.fullName} is not a Packager", Category.PackagingStation);
          return;
        }
        _entityGuid = task.EntityGuid;
        Log(Level.Info, $"StateValidationExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"StateValidationExecutor: Task {task.TaskId} failed revalidation", Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station)
          {
            Log(Level.Error, $"StateValidationExecutor: Invalid station {task.PickupGuid}", Category.PackagingStation);
            return;
          }

          if (((IUsable)station).IsInUse)
          {
            Log(Level.Verbose, $"StateValidationExecutor: Station {station.GUID} is in use", Category.PackagingStation);
            return;
          }

          state.State.TaskContext = new TaskContext { Station = stationAdapter };
          var (isReady, nextStep, packagingId, productItem) = await Utilities.IsStationReady(station, packager, state);
          TaskName? followUpTask = null;

          if (!isReady)
          {
            if (nextStep == "Unpackage")
              followUpTask = TaskName.PackagingStationUnpackage;
            else if (nextStep == "Fetch" || nextStep == "Refill")
              followUpTask = TaskName.PackagingStationFetchPackaging;
          }
          else if (station.ProductSlot?.ItemInstance != null && station.ProductSlot.Quantity > 0)
          {
            followUpTask = TaskName.PackagingStationOperate;
          }
          else if (station.OutputSlot?.ItemInstance != null && station.OutputSlot.Quantity > 0)
          {
            followUpTask = TaskName.PackagingStationDeliver;
          }

          if (followUpTask.HasValue)
          {
            await TaskRegistry.Get(followUpTask.Value).EnqueueFollowUpTasksAsync(packager, task);
            Log(Level.Info, $"StateValidationExecutor: Enqueued follow-up task {followUpTask.Value} for station {station.GUID}", Category.PackagingStation);
          }
          else
          {
            Log(Level.Verbose, $"StateValidationExecutor: No follow-up tasks for station {station.GUID}", Category.PackagingStation);
          }
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"StateValidationExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", Category.PackagingStation);
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          await state.AdvBehaviour.Disable();
          Log(Level.Info, $"StateValidationExecutor: Completed task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        }
      }
    }

    // UnpackageBaggies Task
    public class UnpackageBaggiesTaskDef : ITaskDefinition
    {
      public TaskName Type => TaskName.PackagingStationUnpackage;
      public int Priority => 4;
      public TaskEmployeeType EmployeeType => TaskEmployeeType.Handler;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new UnpackageBaggiesValidator();
      public ITaskExecutor Executor { get; } = new UnpackageBaggiesExecutor();
      public TaskName FollowUpTask => TaskName.PackagingStationState;
    }

    [BurstCompile]
    public class UnpackageBaggiesValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          Log(Level.Verbose, $"UnpackageBaggiesValidator: No valid station for GUID {guid}", Category.PackagingStation);
          return;
        }

        if (((IUsable)station).IsInUse)
        {
          Log(Level.Verbose, $"UnpackageBaggiesValidator: Station {station.GUID} is in use", Category.PackagingStation);
          return;
        }

        var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
        if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
        {
          Log(Level.Verbose, $"UnpackageBaggiesValidator: No product in station {station.GUID}", Category.PackagingStation);
          return;
        }

        if (!Utilities.CheckBaggieUnpackaging(station, null, productSlot.ItemInstance))
        {
          Log(Level.Verbose, $"UnpackageBaggiesValidator: No baggies to unpackage for station {station.GUID}", Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            guid,
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(productSlot.ItemInstance),
            productSlot.Quantity,
            definition.PickupType,
            guid,
            new[] { productSlot.SlotIndex },
            definition.DropoffType,
            guid,
            new[] { productSlot.SlotIndex },
            context.CurrentTime
        );

        validTasks.Add(task);
        Log(Level.Info, $"UnpackageBaggiesValidator: Created task {task.TaskId} for station {guid}", Category.PackagingStation);
      }
    }

    public class UnpackageBaggiesExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"UnpackageBaggiesExecutor: Employee {employee.fullName} is not a Packager", Category.PackagingStation);
          return;
        }

        Log(Level.Info, $"UnpackageBaggiesExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"UnpackageBaggiesExecutor: Task {task.TaskId} failed revalidation", Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station)
          {
            Log(Level.Error, $"UnpackageBaggiesExecutor: Invalid station {task.PickupGuid}", Category.PackagingStation);
            return;
          }

          if (!Utilities.CheckBaggieUnpackaging(station, packager, CreateItemInstance(task.Item)))
          {
            Log(Level.Warning, $"UnpackageBaggiesExecutor: No baggies to unpackage for station {station.GUID}", Category.PackagingStation);
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
            await AwaitNextTickAsync();
          }

          packager.Avatar.Anim.SetBool("UsePackagingStation", false);
          if (FishNetExtensions.IsServer)
            station.Unpack();

          packager.PackagingBehaviour.PackagingInProgress = false;
          Log(Level.Info, $"UnpackageBaggiesExecutor: Completed unpackaging for station {station.GUID}", Category.PackagingStation);
          await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"UnpackageBaggiesExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", Category.PackagingStation);
          packager.PackagingBehaviour.PackagingInProgress = false;
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          await state.AdvBehaviour.Disable();
          Log(Level.Info, $"UnpackageBaggiesExecutor: Completed task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        }
      }
    }

    // FetchPackaging Task
    public class FetchPackagingTaskDef : ITaskDefinition
    {
      public TaskName Type => TaskName.PackagingStationFetchPackaging;
      public int Priority => 3;
      public TaskEmployeeType EmployeeType => TaskEmployeeType.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PlaceableStorageEntity;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new FetchPackagingValidator();
      public ITaskExecutor Executor { get; } = new FetchPackagingExecutor();
      public TaskName FollowUpTask => TaskName.PackagingStationState;
    }

    [BurstCompile]
    public class FetchPackagingValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          Log(Level.Verbose, $"FetchPackagingValidator: No valid station for GUID {guid}", Category.PackagingStation);
          return;
        }

        if (((IUsable)station).IsInUse)
        {
          Log(Level.Verbose, $"FetchPackagingValidator: Station {station.GUID} is in use", Category.PackagingStation);
          return;
        }

        var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
        if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
        {
          Log(Level.Verbose, $"FetchPackagingValidator: No product in station {station.GUID}", Category.PackagingStation);
          return;
        }

        var (canPackage, packagingId, needsUnpack, needsBaggieSwap) = Utilities.CheckPackagingAvailability(station, null, productSlot.Quantity, productSlot.ItemInstance).Result;
        if (canPackage || needsUnpack)
        {
          Log(Level.Verbose, $"FetchPackagingValidator: Station {station.GUID} can package or needs unpack", Category.PackagingStation);
          return;
        }

        var item = Registry.GetItem(packagingId).GetDefaultInstance();
        var shelf = FindStorageWithItemAsync(null, item, 1);
        if (shelf.Key == null)
        {
          Log(Level.Verbose, $"FetchPackagingValidator: No shelf for packaging {packagingId}", Category.PackagingStation);
          return;
        }

        var sourceSlots = GetOutputSlotsContainingItem(shelf.Key, item);
        if (sourceSlots.Count == 0)
        {
          Log(Level.Verbose, $"FetchPackagingValidator: No source slots for {packagingId} on shelf {shelf.Key.GUID}", Category.PackagingStation);
          return;
        }

        var slotKey = new SlotKey(shelf.Key.GUID, sourceSlots[0].SlotIndex);
        if (context.ReservedSlots.ContainsKey(slotKey))
        {
          Log(Level.Verbose, $"FetchPackagingValidator: Slot {slotKey} already reserved", Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            guid,
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemData(item),
            1,
            definition.PickupType,
            shelf.Key.GUID,
            sourceSlots.Select(s => s.SlotIndex).ToArray(),
            definition.DropoffType,
            guid,
            new[] { station.PackagingSlot.SlotIndex },
            context.CurrentTime
        );

        validTasks.Add(task);
        context.ReservedSlots.Add(slotKey, new SlotReservation { EntityGuid = task.TaskId, Timestamp = context.CurrentTime });
        Log(Level.Info, $"FetchPackagingValidator: Created task {task.TaskId} for {packagingId} to station {guid}", Category.PackagingStation);
      }
    }

    public class FetchPackagingExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"FetchPackagingExecutor: Employee {employee.fullName} is not a Packager", Category.PackagingStation);
          return;
        }

        Log(Level.Info, $"FetchPackagingExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"FetchPackagingExecutor: Task {task.TaskId} failed revalidation", Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.DropoffGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station ||
              !Storages[packager.AssignedProperty].TryGetValue(task.PickupGuid, out var shelf))
          {
            Log(Level.Error, $"FetchPackagingExecutor: Invalid entities for task {task.TaskId}", Category.PackagingStation);
            return;
          }

          var item = CreateItemInstance(task.Item);
          var sourceSlots = GetOutputSlotsContainingItem(shelf, item);
          if (sourceSlots.Count == 0)
          {
            Log(Level.Warning, $"FetchPackagingExecutor: No source slots for {task.Item.Id} on shelf {shelf.GUID}", Category.PackagingStation);
            return;
          }

          var deliverySlots = new List<ItemSlot> { station.PackagingSlot };
          if (deliverySlots[0].ItemInstance != null && !item.AdvCanStackWith(deliverySlots[0].ItemInstance))
          {
            Log(Level.Warning, $"FetchPackagingExecutor: Incompatible item in packaging slot for station {station.GUID}", Category.PackagingStation);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || item.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            Log(Level.Warning, $"FetchPackagingExecutor: No inventory slot for {packager.fullName}", Category.PackagingStation);
            return;
          }

          foreach (var pickupSlot in sourceSlots)
          {
            var slotKey = new SlotKey(shelf.GUID, pickupSlot.SlotIndex);
            if (!SlotService.ReserveSlot(slotKey, task.TaskId, Time.time))
            {
              Log(Level.Warning, $"FetchPackagingExecutor: Failed to reserve slot {slotKey} for task {task.TaskId}", Category.PackagingStation);
              continue;
            }
            pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          }

          deliverySlots[0].ApplyLock(packager.NetworkObject, "dropoff");
          var request = TransferRequest.Get(packager, item, task.Quantity, inventorySlot, shelf, sourceSlots, station, deliverySlots);
          state.State.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            Log(Level.Info, $"FetchPackagingExecutor: Successfully fetched {task.Quantity} {task.Item.Id} for station {station.GUID}", Category.PackagingStation);
            await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            Log(Level.Error, $"FetchPackagingExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", Category.PackagingStation);
          }

          await state.AdvBehaviour.ExecuteTask();
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"FetchPackagingExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", Category.PackagingStation);
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          await state.AdvBehaviour.Disable();
          Log(Level.Info, $"FetchPackagingExecutor: Completed task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        }
      }
    }

    // StartPackaging Task
    public class StartPackagingTaskDef : ITaskDefinition
    {
      public TaskName Type => TaskName.PackagingStationOperate;
      public int Priority => 3;
      public TaskEmployeeType EmployeeType => TaskEmployeeType.Handler;
      public bool RequiresPickup => false;
      public bool RequiresDropoff => false;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PackagingStation;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new StartPackagingValidator();
      public ITaskExecutor Executor { get; } = new StartPackagingExecutor();
      public TaskName FollowUpTask => TaskName.PackagingStationState;
    }

    [BurstCompile]
    public class StartPackagingValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          Log(Level.Verbose, $"StartPackagingValidator: No valid station for GUID {guid}", Category.PackagingStation);
          return;
        }

        if (((IUsable)station).IsInUse)
        {
          Log(Level.Verbose, $"StartPackagingValidator: Station {station.GUID} is in use", Category.PackagingStation);
          return;
        }

        var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
        if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
        {
          Log(Level.Verbose, $"StartPackagingValidator: No product in station {station.GUID}", Category.PackagingStation);
          return;
        }

        var (canPackage, _, _, _) = Utilities.CheckPackagingAvailability(station, null, productSlot.Quantity, productSlot.ItemInstance).Result;
        if (!canPackage)
        {
          Log(Level.Verbose, $"StartPackagingValidator: Station {station.GUID} not ready for packaging", Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            guid,
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemKey(productSlot.ItemInstance),
            productSlot.Quantity,
            definition.PickupType,
            guid,
            new[] { productSlot.SlotIndex },
            definition.DropoffType,
            guid,
            new[] { station.OutputSlot.SlotIndex },
            context.CurrentTime
        );

        validTasks.Add(task);
        Log(Level.Info, $"StartPackagingValidator: Created task {task.TaskId} for station {guid}", Category.PackagingStation);
      }
    }

    public class StartPackagingExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"StartPackagingExecutor: Employee {employee.fullName} is not a Packager", Category.PackagingStation);
          return;
        }

        Log(Level.Info, $"StartPackagingExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"StartPackagingExecutor: Task {task.TaskId} failed revalidation", Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station)
          {
            Log(Level.Error, $"StartPackagingExecutor: Invalid station {task.PickupGuid}", Category.PackagingStation);
            return;
          }

          var productSlot = stationAdapter.ProductSlots.FirstOrDefault();
          if (productSlot == null || productSlot.Quantity == 0 || productSlot.ItemInstance == null)
          {
            Log(Level.Warning, $"StartPackagingExecutor: No product in station {station.GUID}", Category.PackagingStation);
            return;
          }

          var (canPackage, packagingId, _, _) = await Utilities.CheckPackagingAvailability(station, packager, productSlot.Quantity, productSlot.ItemInstance);
          if (!canPackage)
          {
            Log(Level.Warning, $"StartPackagingExecutor: Station {station.GUID} not ready for packaging", Category.PackagingStation);
            return;
          }

          packager.PackagingBehaviour.BeginPackaging();
          float timeout = 0f;
          while (packager.PackagingBehaviour.PackagingInProgress && timeout < Constants.OPERATION_TIMEOUT_SECONDS)
          {
            await AwaitNextTickAsync();
            timeout += Time.deltaTime;
          }

          if (packager.PackagingBehaviour.PackagingInProgress)
          {
            packager.PackagingBehaviour.StopPackaging();
            Log(Level.Error, $"StartPackagingExecutor: Timed out for station {station.GUID}", Category.PackagingStation);
          }
          else
          {
            Log(Level.Info, $"StartPackagingExecutor: Completed packaging for station {station.GUID}", Category.PackagingStation);
            await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }

          await state.AdvBehaviour.ExecuteTask();
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"StartPackagingExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", Category.PackagingStation);
          packager.PackagingBehaviour.PackagingInProgress = false;
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          await state.AdvBehaviour.Disable();
          Log(Level.Info, $"StartPackagingExecutor: Completed task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        }
      }
    }

    // DeliverOutput Task
    public class DeliverOutputTaskDef : ITaskDefinition
    {
      public TaskName Type => TaskName.PackagingStationDeliver;
      public int Priority => 2;
      public TaskEmployeeType EmployeeType => TaskEmployeeType.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PackagingStation;
      public TransitTypes DropoffType => TransitTypes.PlaceableStorageEntity;
      public IEntitySelector EntitySelector { get; } = new PackagingStationEntitySelector();
      public ITaskValidator Validator { get; } = new DeliverOutputValidator();
      public ITaskExecutor Executor { get; } = new DeliverOutputExecutor();
      public TaskName FollowUpTask => TaskName.PackagingStationState;
    }

    [BurstCompile]
    public class DeliverOutputValidator : ITaskValidator
    {
      public async Task Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Stations.Extensions.IStations.TryGetValue(property, out var stations) ||
            !stations.TryGetValue(guid, out var stationAdapter) ||
            stationAdapter.TransitEntity is not PackagingStation station)
        {
          Log(Level.Verbose, $"DeliverOutputValidator: No valid station for GUID {guid}", Category.PackagingStation);
          return;
        }

        var outputSlot = station.OutputSlot;
        if (outputSlot == null || outputSlot.Quantity <= 0 || outputSlot.ItemInstance == null)
        {
          Log(Level.Verbose, $"DeliverOutputValidator: No output in station {station.GUID}", Category.PackagingStation);
          return;
        }

        var shelf = FindStorageWithItemAsync(null, outputSlot.ItemInstance, outputSlot.Quantity);
        if (shelf.Key == null)
        {
          Log(Level.Verbose, $"DeliverOutputValidator: No shelf for item {outputSlot.ItemInstance.ID}", Category.PackagingStation);
          return;
        }

        var deliverySlots = await shelf.Result.Key.InputSlots.AdvReserveInputSlotsForItemAsync(outputSlot.ItemInstance, null);
        if (deliverySlots == null || deliverySlots.Count == 0)
        {
          Log(Level.Verbose, $"DeliverOutputValidator: No delivery slots at {shelf.Key.GUID}", Category.PackagingStation);
          return;
        }

        var slotKey = new SlotKey(shelf.Key.GUID, deliverySlots[0].SlotIndex);
        if (context.ReservedSlots.ContainsKey(slotKey))
        {
          Log(Level.Verbose, $"DeliverOutputValidator: Slot {slotKey} already reserved", Category.PackagingStation);
          return;
        }

        var task = TaskDescriptor.Create(
            guid,
            definition.Type,
            definition.EmployeeType,
            definition.Priority,
            context.AssignedPropertyName.ToString(),
            new ItemData(outputSlot.ItemInstance),
            outputSlot.Quantity,
            definition.PickupType,
            guid,
            new[] { outputSlot.SlotIndex },
            definition.DropoffType,
            shelf.Key.GUID,
            deliverySlots.Select(s => s.SlotIndex).ToArray(),
            context.CurrentTime
        );

        validTasks.Add(task);
        context.ReservedSlots.Add(slotKey, new SlotReservation { EntityGuid = task.TaskId, Timestamp = context.CurrentTime });
        Log(Level.Info, $"DeliverOutputValidator: Created task {task.TaskId} for item {outputSlot.ItemInstance.ID} to shelf {shelf.Key.GUID}", Category.PackagingStation);
      }
    }

    public class DeliverOutputExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"DeliverOutputExecutor: Employee {employee.fullName} is not a Packager", Category.PackagingStation);
          return;
        }

        Log(Level.Info, $"DeliverOutputExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"DeliverOutputExecutor: Task {task.TaskId} failed revalidation", Category.PackagingStation);
            return;
          }

          if (!Stations.Extensions.IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.PickupGuid, out var stationAdapter) ||
              stationAdapter.TransitEntity is not PackagingStation station ||
              !Storages[packager.AssignedProperty].TryGetValue(task.DropoffGuid, out var shelf))
          {
            Log(Level.Error, $"DeliverOutputExecutor: Invalid entities for task {task.TaskId}", Category.PackagingStation);
            return;
          }

          var outputSlot = station.OutputSlot;
          if (outputSlot == null || outputSlot.Quantity <= 0 || outputSlot.ItemInstance == null)
          {
            Log(Level.Warning, $"DeliverOutputExecutor: No output in station {station.GUID}", Category.PackagingStation);
            return;
          }

          int[] indexes = [task.DropoffSlotIndex1, task.DropoffSlotIndex2, task.DropoffSlotIndex3];
          var deliverySlots = shelf.InputSlots.Where(s => indexes.Contains(s.SlotIndex)).ToList();
          if (deliverySlots.Count == 0)
          {
            Log(Level.Warning, $"DeliverOutputExecutor: No delivery slots at {shelf.GUID}", Category.PackagingStation);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || outputSlot.ItemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            Log(Level.Warning, $"DeliverOutputExecutor: No inventory slot for {packager.fullName}", Category.PackagingStation);
            return;
          }

          outputSlot.ApplyLock(packager.NetworkObject, "pickup");
          foreach (var deliverySlot in deliverySlots)
          {
            var slotKey = new SlotKey(shelf.GUID, deliverySlot.SlotIndex);
            if (!SlotService.ReserveSlot(slotKey, task.TaskId, Time.time))
            {
              Log(Level.Warning, $"DeliverOutputExecutor: Failed to reserve slot {slotKey} for task {task.TaskId}", Category.PackagingStation);
              continue;
            }
            deliverySlot.ApplyLock(packager.NetworkObject, "dropoff");
          }

          var request = TransferRequest.Get(packager, outputSlot.ItemInstance, outputSlot.Quantity, inventorySlot, station, new List<ItemSlot> { outputSlot }, shelf, deliverySlots);
          state.State.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            Log(Level.Info, $"DeliverOutputExecutor: Successfully delivered {outputSlot.Quantity} {outputSlot.ItemInstance.ID} to shelf {shelf.GUID}", Category.PackagingStation);
            await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            Log(Level.Error, $"DeliverOutputExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", Category.PackagingStation);
          }

          await state.AdvBehaviour.ExecuteTask();
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"DeliverOutputExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", Category.PackagingStation);
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          await state.AdvBehaviour.Disable();
          Log(Level.Info, $"DeliverOutputExecutor: Completed task {task.TaskId} for {packager.fullName}", Category.PackagingStation);
        }
      }
    }

    public static class Utilities
    {
      // Checks if station is ready for packaging or needs action
      public static async Task<(bool isReady, string nextStep, string packagingId, ItemInstance productItem)> IsStationReady(PackagingStation station, Packager packager, EmployeeData state)
      {
        Log(Level.Verbose,
        $"IsStationReady: Checking station={station?.GUID.ToString() ?? "null"} for packager={packager?.fullName ?? "null"}",
        Category.Handler);
        if (station == null || packager == null)
        {
          Log(Level.Error, "IsStationReady: Invalid station or packager", Category.Handler);
          return (false, null, null, null);
        }
        int productCount = station.ProductSlot?.Quantity ?? 0;
        ItemInstance productItem = station.ProductSlot?.ItemInstance;
        if (productCount == 0 || productItem == null)
        {
          Log(Level.Info,
          $"IsStationReady: No product in station={station.GUID}",
          Category.Handler);
          return (false, null, null, null);
        }
        // Check packaging availability and determine action
        var packagingResult = await CheckPackagingAvailability(station, packager, productCount, productItem);
        if (packagingResult.canPackage)
        {
          Log(Level.Info,
          $"IsStationReady: Station={station.GUID} ready for packaging with {packagingResult.packagingId}",
          Category.Handler);
          return (true, "Success", packagingResult.packagingId, productItem);
        }
        if (packagingResult.needsUnpack)
        {
          Log(Level.Info,
          $"IsStationReady: Station={station.GUID} needs baggie unpackaging",
          Category.Handler);
          return (false, "Unpackage", JAR_ITEM_ID, productItem);
        }
        if (packagingResult.needsBaggieSwap)
        {
          Log(Level.Info,
          $"IsStationReady: Station={station.GUID} needs baggie swap",
          Category.Handler);
          return (false, "Fetch", BAGGIE_ITEM_ID, productItem);
        }
        if (productCount < productItem.StackLimit)
        {
          Log(Level.Info,
          $"IsStationReady: Station={station.GUID} needs product refill for {productItem.ID}",
          Category.Handler);
          return (false, "Refill", null, productItem);
        }
        Log(Level.Info,
        $"IsStationReady: Station={station.GUID} needs packaging {packagingResult.packagingId}",
        Category.Handler);
        return (false, "Fetch", packagingResult.packagingId, productItem);
      }

      public static async Task<(bool canPackage, string packagingId, bool needsUnpack, bool needsBaggieSwap)> CheckPackagingAvailability(PackagingStation station, Packager packager, int productCount, ItemInstance productItem)
      {
        Log(Level.Verbose, $"CheckPackagingAvailability: Checking for station={station?.GUID.ToString() ?? "null"}, productCount={productCount}", Category.PackagingStation);
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
            Log(Level.Info, $"CheckPackagingAvailability: Baggie unpackaging needed for station={station.GUID}", Category.PackagingStation);
            return (false, Constants.JAR_ITEM_ID, true, false);
          }

          int neededForJars = Constants.JAR_QUANTITY_THRESHOLD - productCount;
          var shelf = FindStorageWithItemAsync(packager, productItem, neededForJars);
          if (shelf.Key != null)
          {
            Log(Level.Info, $"CheckPackagingAvailability: Found shelf={shelf.Key.GUID} with {shelf.Value} of {productItem.ID}", Category.PackagingStation);
            return (false, Constants.JAR_ITEM_ID, false, false);
          }

          if (hasJars)
          {
            needsBaggieSwap = true;
            requiredPackagingId = Constants.BAGGIE_ITEM_ID;
            Log(Level.Info, $"CheckPackagingAvailability: Swapping jars for baggies in station={station.GUID}", Category.PackagingStation);
          }
          preferJars = false;
        }

        if (preferJars && hasJars)
        {
          Log(Level.Verbose, $"CheckPackagingAvailability: Using jars for station={station.GUID}", Category.PackagingStation);
          return (true, Constants.JAR_ITEM_ID, false, false);
        }

        if (!preferJars && hasBaggies)
        {
          Log(Level.Verbose, $"CheckPackagingAvailability: Using baggies for station={station.GUID}", Category.PackagingStation);
          return (true, Constants.BAGGIE_ITEM_ID, false, false);
        }

        var packagingItem = Registry.GetItem(requiredPackagingId).GetDefaultInstance();
        var packagingShelf = FindStorageWithItemAsync(packager, packagingItem, 1);
        if (packagingShelf.Key != null)
        {
          Log(Level.Info, $"CheckPackagingAvailability: Found shelf={packagingShelf.Key.GUID} with {requiredPackagingId}", Category.PackagingStation);
          return (false, requiredPackagingId, false, needsBaggieSwap);
        }

        Log(Level.Warning, $"CheckPackagingAvailability: No packaging available for {requiredPackagingId} in station={station.GUID}", Category.PackagingStation);
        return (false, requiredPackagingId, false, needsBaggieSwap);
      }

      public static bool CheckBaggieUnpackaging(PackagingStation station, Packager packager, ItemInstance targetProduct)
      {
        Log(Level.Verbose, $"CheckBaggieUnpackaging: Checking for station={station?.GUID.ToString() ?? "null"}", Category.PackagingStation);
        var packagingSlot = station.PackagingSlot;
        if (packagingSlot == null || packagingSlot.Quantity < 1 || (packagingSlot.ItemInstance as ProductItemInstance)?.AppliedPackaging?.ID != Constants.BAGGIE_ITEM_ID)
        {
          Log(Level.Verbose, $"CheckBaggieUnpackaging: No valid baggies in station={station.GUID}", Category.PackagingStation);
          return false;
        }

        int currentQuantity = station.ProductSlot.Quantity;
        int stackLimit = targetProduct?.StackLimit ?? 20;
        int targetQuantity = Math.Min(stackLimit, ((currentQuantity / Constants.JAR_QUANTITY_THRESHOLD) + 1) * Constants.JAR_QUANTITY_THRESHOLD);
        int neededQuantity = targetQuantity - currentQuantity;
        int availableBaggies = packagingSlot.Quantity;

        if (neededQuantity <= 0 || availableBaggies < 1)
        {
          Log(Level.Verbose, $"CheckBaggieUnpackaging: No unpack needed, neededQuantity={neededQuantity}, availableBaggies={availableBaggies}", Category.PackagingStation);
          return false;
        }

        int unpackCount = Math.Min(availableBaggies, (neededQuantity + Constants.JAR_QUANTITY_THRESHOLD - 1) / Constants.JAR_QUANTITY_THRESHOLD);
        Log(Level.Info, $"CheckBaggieUnpackaging: Can unpackage {unpackCount} baggies for station={station.GUID}, adding {unpackCount * Constants.JAR_QUANTITY_THRESHOLD} product", Category.PackagingStation);
        return true;
      }

      public static async Task<bool> InitiatePackagingRetrieval(PackagingStation station, Packager packager, string itemId, EmployeeData state, ItemInstance productItem = null)
      {
        Log(Level.Verbose, $"InitiatePackagingRetrieval: Retrieving itemId={itemId} for station={station?.GUID.ToString() ?? "null"}", Category.PackagingStation);
        ItemInstance item = productItem ?? Registry.GetItem(itemId).GetDefaultInstance();
        int quantityNeeded = productItem != null ? Math.Max(1, item.StackLimit - (station.ProductSlot?.Quantity ?? 0)) : 1;
        var shelf = FindStorageWithItemAsync(packager, item, quantityNeeded);
        if (shelf.Key == null)
        {
          Log(Level.Warning, $"InitiatePackagingRetrieval: No shelf for item={item.ID}", Category.PackagingStation);
          return false;
        }

        var sourceSlots = GetOutputSlotsContainingItem(shelf.Key, item);
        if (sourceSlots.Count == 0)
        {
          Log(Level.Warning, $"InitiatePackagingRetrieval: No source slots for item={item.ID} on shelf={shelf.Key.GUID}", Category.PackagingStation);
          return false;
        }

        var deliverySlots = new List<ItemSlot> { station.PackagingSlot };
        if (deliverySlots[0].ItemInstance != null && !item.AdvCanStackWith(deliverySlots[0].ItemInstance))
        {
          Log(Level.Warning, $"InitiatePackagingRetrieval: Incompatible item in packaging slot for station={station.GUID}", Category.PackagingStation);
          return false;
        }

        int quantity = Math.Min(shelf.Value, quantityNeeded);
        if (quantity <= 0 || packager.Inventory.HowManyCanFit(item) < quantity)
        {
          Log(Level.Warning, $"InitiatePackagingRetrieval: Invalid quantity={quantity} or insufficient inventory for item={item.ID}", Category.PackagingStation);
          return false;
        }

        var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
        if (inventorySlot == null)
        {
          Log(Level.Warning, $"InitiatePackagingRetrieval: No inventory slot for packager={packager.fullName}", Category.PackagingStation);
          return false;
        }

        foreach (var pickupSlot in sourceSlots)
        {
          var slotKey = new SlotKey(shelf.Key.GUID, pickupSlot.SlotIndex);
          if (!SlotService.ReserveSlot(slotKey, state.State.TaskContext?.Task.TaskId ?? Guid.NewGuid(), Time.time))
          {
            Log(Level.Warning, $"InitiatePackagingRetrieval: Failed to reserve slot {slotKey}", Category.PackagingStation);
            continue;
          }
          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
        }

        deliverySlots[0].ApplyLock(packager.NetworkObject, "dropoff");
        var request = TransferRequest.Get(packager, item, quantity, inventorySlot, shelf.Key, sourceSlots, station, deliverySlots);
        state.State.TaskContext = state.State.TaskContext ?? new TaskContext();
        state.State.TaskContext.Requests = new List<TransferRequest> { request };
        state.State.TaskContext.Item = item;

        Log(Level.Info, $"InitiatePackagingRetrieval: Initiated transfer of {quantity} {item.ID} to station={station.GUID}", Category.PackagingStation);
        return true;
      }
    }
  }
}