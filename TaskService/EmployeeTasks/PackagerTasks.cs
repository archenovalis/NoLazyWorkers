using ScheduleOne.Employees;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Utilities;
using static NoLazyWorkers.Storage.Extensions;
using ScheduleOne.Property;
using NoLazyWorkers.Employees;
using Unity.Burst;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.TaskService.Extensions;
using static NoLazyWorkers.Storage.Utilities;
using NoLazyWorkers.TaskService;
using Unity.Collections;
using NoLazyWorkers.Storage;
using ScheduleOne.ItemFramework;
using FishNet;
using static NoLazyWorkers.Movement.Extensions;
using static NoLazyWorkers.Movement.Utilities;
using static NoLazyWorkers.TaskService.TaskRegistry;
using System.Diagnostics;
using static NoLazyWorkers.Debug;

namespace NoLazyWorkers.TaskService.EmployeeTasks
{
  public static class PackagerTasks
  {
    public static List<ITaskDefinition> Register()
    {
      return new List<ITaskDefinition>
            {
                new RefillStationTaskDef(),
                new EmptyLoadingDockTaskDef(),
                new RestockSpecificShelfTaskDef()
            };
    }

    /// <summary>
    /// Refills a station's input slots from storage.
    /// </summary>
    public class RefillStationTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagerRefillStation;
      public int Priority => 3;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PlaceableStorageEntity;
      public TransitTypes DropoffType => TransitTypes.AnyStation;
      public IEntitySelector EntitySelector { get; } = new StationEntitySelector();
      public ITaskValidator Validator { get; } = new RefillStationValidator();
      public ITaskExecution Execution { get; } = new RefillStationExecutor();
    }

    public class StationEntitySelector : IEntitySelector
    {
      public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<Guid>(allocator);
        if (IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values)
            entities.Add(station.GUID);
        }
        Log(Level.Verbose, $"Selected {entities.Length} station entities for property {property.name}", Category.Handler);
        return entities;
      }
    }

    [BurstCompile]
    public class RefillStationValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!context.StationInputSlots.TryGetValue(new StorageKey(guid), out var inputSlots) || inputSlots.Length != 1)
          return;

        const int BATCH_SIZE = 10;
        var productSlot = inputSlots[0];
        if (productSlot.Quantity >= 10 || productSlot.IsLocked || TaskUtilities.IsItemTimedOut(property, productSlot.ItemInstance))
        {
          Log(Level.Verbose, $"RefillStationValidator: Station {guid} slot {productSlot.SlotIndex} invalid (Qty: {productSlot.Quantity}/10, Locked: {productSlot.IsLocked})", Category.Handler);
          return;
        }

        ItemData targetItemKey = productSlot.Item;
        int neededQuantity = 10 - productSlot.Quantity;
        var cacheKey = new CacheKey(targetItemKey.Id.ToString(), targetItemKey.PackagingId.ToString(), targetItemKey.Quality != EQualityBurst.None ? Enum.Parse<EQuality>(targetItemKey.Quality.ToString()) : null, property);

        if (!CacheService.TryGetCachedShelves(cacheKey, out var shelves))
        {
          Log(Level.Verbose, $"RefillStationValidator: No shelves found for item {targetItemKey.Id}", Category.Handler);
          return;
        }

        var shelfBatch = shelves.Take(BATCH_SIZE).ToList();
        foreach (var shelf in shelfBatch)
        {
          var shelfGuid = shelf.Key.GUID;
          var slotIndex = shelf.Value;
          var slotKey = new SlotKey(shelfGuid, slotIndex);

          if (context.ReservedSlots.ContainsKey(slotKey) || !SlotService.ReserveSlot(slotKey, Guid.NewGuid(), context.CurrentTime))
          {
            Log(Level.Verbose, $"RefillStationValidator: Slot {slotKey} already reserved", Category.Handler);
            continue;
          }

          var task = TaskDescriptor.Create(
              guid,
              definition.Type,
              definition.EmployeeType,
              definition.Priority,
              context.AssignedPropertyName.ToString(),
              targetItemKey,
              neededQuantity,
              definition.PickupType,
              shelfGuid,
              new[] { slotIndex },
              definition.DropoffType,
              guid,
              new[] { productSlot.SlotIndex },
              context.CurrentTime,
              Guid.Empty,
              true);

          validTasks.Add(task);
          context.ReservedSlots.Add(slotKey, new SlotReservation { EntityGuid = task.TaskId, Timestamp = context.CurrentTime });
          Log(Level.Info, $"RefillStationValidator: Created task {task.TaskId} for item {targetItemKey.Id} to station {guid}", Category.Handler);
        }
      }
    }

    public class RefillStationExecutor : ITaskExecution
    {
      public async Task<bool> ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"RefillStationExecutor: Employee {employee.fullName} is not a Packager", Category.Handler);
          return false;
        }

        var stopwatch = Stopwatch.StartNew();
        Log(Level.Info, $"RefillStationExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.Handler);
        var pickupSlotKey = new SlotKey(task.PickupGuid, task.PickupSlotIndex1);
        var dropoffSlotKey = new SlotKey(task.DropoffGuid, task.DropoffSlotIndex1);

        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"RefillStationExecutor: Task {task.TaskId} failed revalidation", Category.Handler);
            return false;
          }

          var shelf = TaskUtilities.FindStorageForDelivery(packager, CreateItemInstance(task.Item), false);
          if (shelf == null || !IStations.TryGetValue(packager.AssignedProperty, out var stations) || !stations.TryGetValue(task.DropoffGuid, out var stationAdapter))
          {
            Log(Level.Error, $"RefillStationExecutor: Invalid entities for task {task.TaskId}", Category.Handler);
            return false;
          }

          var pickupSlot = shelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndex1);
          var dropoffSlot = stationAdapter.ProductSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (pickupSlot == null || dropoffSlot == null)
          {
            Log(Level.Error, $"RefillStationExecutor: Invalid slots for task {task.TaskId}", Category.Handler);
            return false;
          }

          var itemInstance = CreateItemInstance(task.Item);
          int quantity = Math.Min(task.Quantity, dropoffSlot.GetCapacityForItem(itemInstance));
          if (quantity <= 0)
          {
            Log(Level.Warning, $"RefillStationExecutor: Invalid quantity {quantity} for task {task.TaskId}", Category.Handler);
            return false;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || itemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            Log(Level.Warning, $"RefillStationExecutor: No inventory slot for {packager.fullName}", Category.Handler);
            return false;
          }

          var routes = await TaskUtilities.BuildTransferRoutesAsync(packager, task, batchSize: 2);
          if (!routes.Any())
          {
            Log(Level.Warning, $"RefillStationExecutor: No valid routes for task {task.TaskId}", Category.Handler);
            return false;
          }

          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          dropoffSlot.ApplyLock(packager.NetworkObject, "dropoff");
          state.State.TaskContext = new TaskContext { Task = task, Requests = routes.Select(r => r.Request).ToList() };

          bool success = await MoveToRetryAsync(async () =>
          {
            var result = await TransitAsync(packager, state, task, routes.Select(r => r.Request).ToList());
            return result.Success;
          }, maxRetries: 3, delaySeconds: 1f);

          if (success)
          {
            await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task);
            Log(Level.Info, $"RefillStationExecutor: Successfully executed task {task.TaskId} in {stopwatch.ElapsedMilliseconds}ms", Category.Handler);
            return true;
          }
          else
          {
            Log(Level.Error, $"RefillStationExecutor: Failed task {task.TaskId} after retries", Category.Handler);
            return false;
          }
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"RefillStationExecutor: Exception for task {task.TaskId}: {ex}", Category.Handler);
          return false;
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          SlotService.ReleaseSlot(pickupSlotKey);
          SlotService.ReleaseSlot(dropoffSlotKey);
          await state.AdvBehaviour.Disable();
          TaskUtilities.LogExecutionTime(task.TaskId.ToString(), stopwatch.ElapsedMilliseconds);
          Log(Level.Info, $"RefillStationExecutor: Completed task {task.TaskId} in {stopwatch.ElapsedMilliseconds}ms", Category.Handler);
        }
      }
    }

    /// <summary>
    /// Empties items from a loading dock to storage.
    /// </summary>
    public class EmptyLoadingDockTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagerEmptyLoadingDock;
      public int Priority => 2;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.LoadingDock;
      public TransitTypes DropoffType => TransitTypes.PlaceableStorageEntity;
      public IEntitySelector EntitySelector { get; } = new DockEntitySelector();
      public ITaskValidator Validator { get; } = new EmptyLoadingDockValidator();
      public ITaskExecution Execution { get; } = new EmptyLoadingDockExecutor();
    }

    public class DockEntitySelector : IEntitySelector
    {
      public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<Guid>(allocator);
        foreach (var dock in property.LoadingDocks)
          entities.Add(dock.GUID);
        Log(Level.Verbose, $"Selected {entities.Length} dock entities for property {property.name}", Category.Handler);
        return entities;
      }
    }

    [BurstCompile]
    public class EmptyLoadingDockValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        var dock = property.LoadingDocks.FirstOrDefault(d => d.GUID == guid);
        if (dock == null)
        {
          Log(Level.Verbose, $"EmptyLoadingDockValidator: No dock found for GUID {guid}", Category.Handler);
          return;
        }

        if (!context.StationInputSlots.TryGetValue(new StorageKey(guid), out var outputSlots))
          return;

        const int BATCH_SIZE = 10;
        var slotBatch = outputSlots.Take(BATCH_SIZE).ToList();
        foreach (var slot in slotBatch)
        {
          if (slot.Quantity <= 0 || slot.IsLocked || TaskUtilities.IsItemTimedOut(property, slot.ItemInstance))
          {
            Log(Level.Verbose, $"EmptyLoadingDockValidator: Slot {slot.SlotIndex} invalid (Qty: {slot.Quantity}, Locked: {slot.IsLocked})", Category.Handler);
            continue;
          }

          var cacheKey = new CacheKey(slot.Item.Id.ToString(), slot.Item.PackagingId.ToString(), slot.Item.Quality != EQualityBurst.None ? Enum.Parse<EQuality>(slot.Item.Quality.ToString()) : null, property);
          if (!CacheService.TryGetCachedShelves(cacheKey, out var shelves))
          {
            Log(Level.Verbose, $"EmptyLoadingDockValidator: No shelves for item {slot.Item.Id}", Category.Handler);
            continue;
          }

          foreach (var shelf in shelves.Take(BATCH_SIZE))
          {
            var shelfGuid = shelf.Key.GUID;
            var dropoffIndex = shelf.Value;
            var slotKey = new SlotKey(shelfGuid, dropoffIndex);
            if (context.ReservedSlots.ContainsKey(slotKey) || !SlotService.ReserveSlot(slotKey, Guid.NewGuid(), context.CurrentTime))
            {
              Log(Level.Verbose, $"EmptyLoadingDockValidator: Slot {slotKey} already reserved", Category.Handler);
              continue;
            }

            var task = TaskDescriptor.Create(
                guid,
                definition.Type,
                definition.EmployeeType,
                definition.Priority,
                context.AssignedPropertyName.ToString(),
                slot.Item,
                slot.Quantity,
                definition.PickupType,
                guid,
                new[] { slot.SlotIndex },
                definition.DropoffType,
                shelfGuid,
                new[] { dropoffIndex },
                context.CurrentTime,
                Guid.Empty,
                true);

            validTasks.Add(task);
            context.ReservedSlots.Add(slotKey, new SlotReservation { EntityGuid = task.TaskId, Timestamp = context.CurrentTime });
            Log(Level.Info, $"EmptyLoadingDockValidator: Created task {task.TaskId} for item {slot.Item.Id} to shelf {shelfGuid}", Category.Handler);
          }
        }
      }
    }

    public class EmptyLoadingDockExecutor : ITaskExecution
    {
      public async Task<bool> ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"EmptyLoadingDockExecutor: Employee {employee.fullName} is not a Packager", Category.Handler);
          return false;
        }

        var stopwatch = Stopwatch.StartNew();
        Log(Level.Info, $"EmptyLoadingDockExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.Handler);
        var pickupSlotKey = new SlotKey(task.PickupGuid, task.PickupSlotIndex1);
        var dropoffSlotKey = new SlotKey(task.DropoffGuid, task.DropoffSlotIndex1);

        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"EmptyLoadingDockExecutor: Task {task.TaskId} failed revalidation", Category.Handler);
            return false;
          }

          var dock = packager.AssignedProperty.LoadingDocks.FirstOrDefault(d => d.GUID == task.PickupGuid);
          if (dock == null || !Storages[packager.AssignedProperty].TryGetValue(task.DropoffGuid, out var shelf))
          {
            Log(Level.Error, $"EmptyLoadingDockExecutor: Invalid entities for task {task.TaskId}", Category.Handler);
            return false;
          }

          var pickupSlot = dock.OutputSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndex1);
          var dropoffSlot = shelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (pickupSlot == null || dropoffSlot == null)
          {
            Log(Level.Error, $"EmptyLoadingDockExecutor: Invalid slots for task {task.TaskId}", Category.Handler);
            return false;
          }

          var itemInstance = CreateItemInstance(task.Item);
          int quantity = Math.Min(task.Quantity, dropoffSlot.GetCapacityForItem(itemInstance));
          if (quantity <= 0)
          {
            Log(Level.Warning, $"EmptyLoadingDockExecutor: Invalid quantity {quantity} for task {task.TaskId}", Category.Handler);
            return false;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || itemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            Log(Level.Warning, $"EmptyLoadingDockExecutor: No inventory slot for {packager.fullName}", Category.Handler);
            return false;
          }

          var routes = await TaskUtilities.BuildTransferRoutesAsync(packager, task, batchSize: 2);
          if (!routes.Any())
          {
            Log(Level.Warning, $"EmptyLoadingDockExecutor: No valid routes for task {task.TaskId}", Category.Handler);
            return false;
          }

          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          dropoffSlot.ApplyLock(packager.NetworkObject, "dropoff");
          state.State.TaskContext = new TaskContext { Task = task, Requests = routes.Select(r => r.Request).ToList() };

          bool success = await MoveToRetryAsync(async () =>
          {
            var result = await TransitAsync(packager, state, task, routes.Select(r => r.Request).ToList());
            return result.Success;
          }, maxRetries: 3, delaySeconds: 1f);

          if (success)
          {
            await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task);
            Log(Level.Info, $"EmptyLoadingDockExecutor: Successfully executed task {task.TaskId} in {stopwatch.ElapsedMilliseconds}ms", Category.Handler);
            return true;
          }
          else
          {
            Log(Level.Error, $"EmptyLoadingDockExecutor: Failed task {task.TaskId} after retries", Category.Handler);
            return false;
          }
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"EmptyLoadingDockExecutor: Exception for task {task.TaskId}: {ex}", Category.Handler);
          return false;
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          SlotService.ReleaseSlot(pickupSlotKey);
          SlotService.ReleaseSlot(dropoffSlotKey);
          await state.AdvBehaviour.Disable();
          TaskUtilities.LogExecutionTime(task.TaskId.ToString(), stopwatch.ElapsedMilliseconds);
          Log(Level.Info, $"EmptyLoadingDockExecutor: Completed task {task.TaskId} in {stopwatch.ElapsedMilliseconds}ms", Category.Handler);
        }
      }
    }

    /// <summary>
    /// Restocks items between storage entities to optimize stock placement.
    /// </summary>
    public class RestockSpecificShelfTaskDef : ITaskDefinition
    {
      public TaskTypes Type => TaskTypes.PackagerRestock;
      public int Priority => 2;
      public EmployeeTypes EmployeeType => EmployeeTypes.Handler;
      public bool RequiresPickup => true;
      public bool RequiresDropoff => true;
      public TransitTypes PickupType => TransitTypes.PlaceableStorageEntity;
      public TransitTypes DropoffType => TransitTypes.PlaceableStorageEntity;
      public IEntitySelector EntitySelector { get; } = new ShelfEntitySelector();
      public ITaskValidator Validator { get; } = new RestockSpecificShelfValidator();
      public ITaskExecution Execution { get; } = new RestockSpecificShelfExecutor();
    }

    public class ShelfEntitySelector : IEntitySelector
    {
      public NativeList<Guid> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<Guid>(allocator);
        if (Storages.TryGetValue(property, out var storages))
        {
          foreach (var storage in storages.Values)
            entities.Add(storage.GUID);
        }
        Log(Level.Verbose, $"Selected {entities.Length} storage entities for property {property.name}", Category.Handler);
        return entities;
      }
    }

    [BurstCompile]
    public class RestockSpecificShelfValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, Guid guid, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Storages.TryGetValue(property, out var storages) || !storages.TryGetValue(guid, out var sourceShelf))
        {
          Log(Level.Verbose, $"RestockSpecificShelfValidator: No storage found for GUID {guid}", Category.Handler);
          return;
        }

        if (!context.StationInputSlots.TryGetValue(new StorageKey(guid), out var outputSlots))
          return;

        const int BATCH_SIZE = 10;
        var slotBatch = outputSlots.Take(BATCH_SIZE).ToList();
        foreach (var slot in slotBatch)
        {
          if (slot.Quantity <= 0 || slot.IsLocked || TaskUtilities.IsItemTimedOut(property, slot.ItemInstance))
          {
            Log(Level.Verbose, $"RestockSpecificShelfValidator: Slot {slot.SlotIndex} invalid (Qty: {slot.Quantity}, Locked: {slot.IsLocked})", Category.Handler);
            continue;
          }

          var cacheKey = new CacheKey(slot.Item.Id.ToString(), slot.Item.PackagingId.ToString(), slot.Item.Quality != EQualityBurst.None ? Enum.Parse<EQuality>(slot.Item.Quality.ToString()) : null, property);
          if (!CacheService.TryGetCachedShelves(cacheKey, out var shelves))
          {
            Log(Level.Verbose, $"RestockSpecificShelfValidator: No destination shelves for item {slot.Item.Id}", Category.Handler);
            continue;
          }

          foreach (var shelf in shelves.Take(BATCH_SIZE))
          {
            var shelfGuid = shelf.Key.GUID;
            if (shelfGuid == guid)
              continue;

            var dropoffIndex = shelf.Value;
            var slotKey = new SlotKey(shelfGuid, dropoffIndex);
            if (context.ReservedSlots.ContainsKey(slotKey) || !SlotService.ReserveSlot(slotKey, Guid.NewGuid(), context.CurrentTime))
            {
              Log(Level.Verbose, $"RestockSpecificShelfValidator: Slot {slotKey} already reserved", Category.Handler);
              continue;
            }

            var task = TaskDescriptor.Create(
                guid,
                definition.Type,
                definition.EmployeeType,
                definition.Priority,
                context.AssignedPropertyName.ToString(),
                slot.Item,
                slot.Quantity,
                definition.PickupType,
                guid,
                new[] { slot.SlotIndex },
                definition.DropoffType,
                shelfGuid,
                new[] { dropoffIndex },
                context.CurrentTime,
                Guid.Empty,
                true);

            validTasks.Add(task);
            context.ReservedSlots.Add(slotKey, new SlotReservation { EntityGuid = task.TaskId, Timestamp = context.CurrentTime });
            Log(Level.Info, $"RestockSpecificShelfValidator: Created task {task.TaskId} for item {slot.Item.Id} to shelf {shelfGuid}", Category.Handler);
          }
        }
      }
    }

    public class RestockSpecificShelfExecutor : ITaskExecution
    {
      public async Task<bool> ExecuteAsync(Employee employee, EmployeeData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          Log(Level.Error, $"RestockSpecificShelfExecutor: Employee {employee.fullName} is not a Packager", Category.Handler);
          return false;
        }

        var stopwatch = Stopwatch.StartNew();
        Log(Level.Info, $"RestockSpecificShelfExecutor: Starting task {task.TaskId} for {packager.fullName}", Category.Handler);
        var pickupSlotKey = new SlotKey(task.PickupGuid, task.PickupSlotIndex1);
        var dropoffSlotKey = new SlotKey(task.DropoffGuid, task.DropoffSlotIndex1);

        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            Log(Level.Warning, $"RestockSpecificShelfExecutor: Task {task.TaskId} failed revalidation", Category.Handler);
            return false;
          }

          if (!Storages[packager.AssignedProperty].TryGetValue(task.PickupGuid, out var sourceShelf) ||
              !Storages[packager.AssignedProperty].TryGetValue(task.DropoffGuid, out var destShelf))
          {
            Log(Level.Error, $"RestockSpecificShelfExecutor: Invalid entities for task {task.TaskId}", Category.Handler);
            return false;
          }

          var pickupSlot = sourceShelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndex1);
          var dropoffSlot = destShelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (pickupSlot == null || dropoffSlot == null)
          {
            Log(Level.Error, $"RestockSpecificShelfExecutor: Invalid slots for task {task.TaskId}", Category.Handler);
            return false;
          }

          var itemInstance = CreateItemInstance(task.Item);
          int quantity = Math.Min(task.Quantity, dropoffSlot.GetCapacityForItem(itemInstance));
          if (quantity <= 0)
          {
            Log(Level.Warning, $"RestockSpecificShelfExecutor: Invalid quantity {quantity} for task {task.TaskId}", Category.Handler);
            return false;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || itemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            Log(Level.Warning, $"RestockSpecificShelfExecutor: No inventory slot for {packager.fullName}", Category.Handler);
            return false;
          }

          var routes = await TaskUtilities.BuildTransferRoutesAsync(packager, task, batchSize: 2);
          if (!routes.Any())
          {
            Log(Level.Warning, $"RestockSpecificShelfExecutor: No valid routes for task {task.TaskId}", Category.Handler);
            return false;
          }

          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          dropoffSlot.ApplyLock(packager.NetworkObject, "dropoff");
          state.State.TaskContext = new TaskContext { Task = task, Requests = routes.Select(r => r.Request).ToList() };

          bool success = await MoveToRetryAsync(async () =>
          {
            var result = await TransitAsync(packager, state, task, routes.Select(r => r.Request).ToList());
            return result.Success;
          }, maxRetries: 3, delaySeconds: 1f);

          if (success)
          {
            await TaskRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task);
            Log(Level.Info, $"RestockSpecificShelfExecutor: Successfully executed task {task.TaskId} in {stopwatch.ElapsedMilliseconds}ms", Category.Handler);
            return true;
          }
          else
          {
            Log(Level.Error, $"RestockSpecificShelfExecutor: Failed task {task.TaskId} after retries", Category.Handler);
            return false;
          }
        }
        catch (Exception ex)
        {
          Log(Level.Error, $"RestockSpecificShelfExecutor: Exception for task {task.TaskId}: {ex}", Category.Handler);
          return false;
        }
        finally
        {
          state.State.TaskContext?.Cleanup(packager);
          SlotService.ReleaseSlot(pickupSlotKey);
          SlotService.ReleaseSlot(dropoffSlotKey);
          await state.AdvBehaviour.Disable();
          TaskUtilities.LogExecutionTime(task.TaskId.ToString(), stopwatch.ElapsedMilliseconds);
          Log(Level.Info, $"RestockSpecificShelfExecutor: Completed task {task.TaskId} in {stopwatch.ElapsedMilliseconds}ms", Category.Handler);
        }
      }
    }
  }
}