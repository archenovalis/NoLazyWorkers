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
      public ITaskExecutor Executor { get; } = new RefillStationExecutor();
      public TaskTypes FollowUpTask => TaskTypes.MixingStationState; // Added for state revalidation
    }

    public class StationEntitySelector : IEntitySelector
    {
      public NativeList<EntityKey> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<EntityKey>(allocator);
        if (IStations.TryGetValue(property, out var stations))
        {
          foreach (var station in stations.Values)
            entities.Add(new EntityKey { Guid = station.GUID, Type = TransitTypes.AnyStation });
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Selected {entities.Length} station entities for property {property.name}", DebugLogger.Category.Handler);
        return entities;
      }
    }

    [BurstCompile]
    public class RefillStationValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!context.StationInputSlots.TryGetValue(new StorageKey(entityKey.Guid), out var inputSlots) || inputSlots.Length != 1)
          return;

        var productSlot = inputSlots[0];
        if (productSlot.Quantity >= 10)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationValidator: Station {entityKey.Guid} slot {productSlot.SlotIndex} already full ({productSlot.Quantity}/10)", DebugLogger.Category.Handler);
          return;
        }

        ItemKey targetItemKey = productSlot.ItemKey;
        int neededQuantity = 10 - productSlot.Quantity;
        var cacheKey = new CacheKey(targetItemKey.Id.ToString(), targetItemKey.PackagingId.ToString(), targetItemKey.Quality != NEQuality.None ? Enum.Parse<EQuality>(targetItemKey.Quality.ToString()) : null, property);

        if (!CacheManager.TryGetCachedShelves(cacheKey, out var shelves))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationValidator: No shelves found for item {targetItemKey.Id}", DebugLogger.Category.Handler);
          return;
        }

        foreach (var shelf in shelves)
        {
          var shelfGuid = shelf.Key.GUID;
          var slotIndex = shelf.Value;
          var slotKey = new SlotKey(shelfGuid, slotIndex);
          if (context.ReservedSlots.ContainsKey(slotKey))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationValidator: Slot {slotKey} already reserved", DebugLogger.Category.Handler);
            continue;
          }

          var task = TaskDescriptor.Create(
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
              entityKey.Guid,
              new[] { productSlot.SlotIndex },
              context.CurrentTime
          );

          validTasks.Add(task);
          context.ReservedSlots.Add(slotKey, new SlotReservation { TaskId = task.TaskId, Timestamp = context.CurrentTime });
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"RefillStationValidator: Created task {task.TaskId} for item {targetItemKey.Id} to station {entityKey.Guid}", DebugLogger.Category.Handler);
        }
      }
    }

    public class RefillStationExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RefillStationExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.Handler);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RefillStationExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RefillStationExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.Handler);
            return;
          }

          if (!Storages[packager.AssignedProperty].TryGetValue(task.PickupGuid, out var shelf) ||
              !IStations.TryGetValue(packager.AssignedProperty, out var stations) ||
              !stations.TryGetValue(task.DropoffGuid, out var stationAdapter))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RefillStationExecutor: Invalid entities for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var pickupSlot = shelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndex1);
          var dropoffSlot = stationAdapter.ProductSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (pickupSlot == null || dropoffSlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RefillStationExecutor: Invalid slots for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var itemInstance = CreateItemInstance(task.Item);
          int quantity = Math.Min(task.Quantity, dropoffSlot.GetCapacityForItem(itemInstance));
          if (quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RefillStationExecutor: Invalid quantity {quantity} for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || itemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RefillStationExecutor: No inventory slot for {packager.fullName}", DebugLogger.Category.Handler);
            return;
          }

          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          dropoffSlot.ApplyLock(packager.NetworkObject, "dropoff");
          var request = TransferRequest.Get(packager, itemInstance, quantity, inventorySlot, shelf, new List<ItemSlot> { pickupSlot }, stationAdapter.TransitEntity, new List<ItemSlot> { dropoffSlot });
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await Movement.Utilities.TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"RefillStationExecutor: Successfully executed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RefillStationExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.Handler);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RefillStationExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.Handler);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"RefillStationExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
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
      public ITaskExecutor Executor { get; } = new EmptyLoadingDockExecutor();
      public TaskTypes FollowUpTask => TaskTypes.MixingStationState;
    }

    public class DockEntitySelector : IEntitySelector
    {
      public NativeList<EntityKey> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<EntityKey>(allocator);
        foreach (var dock in property.LoadingDocks)
          entities.Add(new EntityKey { Guid = dock.GUID, Type = TransitTypes.LoadingDock });
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Selected {entities.Length} dock entities for property {property.name}", DebugLogger.Category.Handler);
        return entities;
      }
    }

    [BurstCompile]
    public class EmptyLoadingDockValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        var dock = property.LoadingDocks.FirstOrDefault(d => d.GUID == entityKey.Guid);
        if (dock == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmptyLoadingDockValidator: No dock found for GUID {entityKey.Guid}", DebugLogger.Category.Handler);
          return;
        }

        if (!context.StationInputSlots.TryGetValue(new StorageKey(entityKey.Guid), out var outputSlots))
          return;

        foreach (var slot in outputSlots)
        {
          if (slot.Quantity <= 0 || slot.IsLocked)
            continue;

          var cacheKey = new CacheKey(slot.ItemKey.Id.ToString(), slot.ItemKey.PackagingId.ToString(), slot.ItemKey.Quality != NEQuality.None ? Enum.Parse<EQuality>(slot.ItemKey.Quality.ToString()) : null, property);
          if (!CacheManager.TryGetCachedShelves(cacheKey, out var shelves))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmptyLoadingDockValidator: No shelves for item {slot.ItemKey.Id}", DebugLogger.Category.Handler);
            continue;
          }

          foreach (var shelf in shelves)
          {
            var shelfGuid = shelf.Key.GUID;
            var dropoffIndex = shelf.Value;
            var slotKey = new SlotKey(shelfGuid, dropoffIndex);
            if (context.ReservedSlots.ContainsKey(slotKey))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmptyLoadingDockValidator: Slot {slotKey} already reserved", DebugLogger.Category.Handler);
              continue;
            }

            var task = TaskDescriptor.Create(
                definition.Type,
                definition.EmployeeType,
                definition.Priority,
                context.AssignedPropertyName.ToString(),
                slot.ItemKey,
                slot.Quantity,
                definition.PickupType,
                entityKey.Guid,
                new[] { slot.SlotIndex },
                definition.DropoffType,
                shelfGuid,
                new[] { dropoffIndex },
                context.CurrentTime
            );

            validTasks.Add(task);
            context.ReservedSlots.Add(slotKey, new SlotReservation { TaskId = task.TaskId, Timestamp = context.CurrentTime });
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmptyLoadingDockValidator: Created task {task.TaskId} for item {slot.ItemKey.Id} to shelf {shelfGuid}", DebugLogger.Category.Handler);
          }
        }
      }
    }

    public class EmptyLoadingDockExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmptyLoadingDockExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.Handler);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmptyLoadingDockExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmptyLoadingDockExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.Handler);
            return;
          }

          var dock = packager.AssignedProperty.LoadingDocks.FirstOrDefault(d => d.GUID == task.PickupGuid);
          if (dock == null || !Storages[packager.AssignedProperty].TryGetValue(task.DropoffGuid, out var shelf))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmptyLoadingDockExecutor: Invalid entities for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var pickupSlot = dock.OutputSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndex1);
          var dropoffSlot = shelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (pickupSlot == null || dropoffSlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmptyLoadingDockExecutor: Invalid slots for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var itemInstance = CreateItemInstance(task.Item);
          int quantity = Math.Min(task.Quantity, dropoffSlot.GetCapacityForItem(itemInstance));
          if (quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmptyLoadingDockExecutor: Invalid quantity {quantity} for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || itemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmptyLoadingDockExecutor: No inventory slot for {packager.fullName}", DebugLogger.Category.Handler);
            return;
          }

          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          dropoffSlot.ApplyLock(packager.NetworkObject, "dropoff");
          var request = TransferRequest.Get(packager, itemInstance, quantity, inventorySlot, dock, new List<ItemSlot> { pickupSlot }, shelf, new List<ItemSlot> { dropoffSlot });
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmptyLoadingDockExecutor: Successfully executed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmptyLoadingDockExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.Handler);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmptyLoadingDockExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.Handler);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmptyLoadingDockExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
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
      public ITaskExecutor Executor { get; } = new RestockSpecificShelfExecutor();
      public TaskTypes FollowUpTask => TaskTypes.MixingStationState;
    }

    public class ShelfEntitySelector : IEntitySelector
    {
      public NativeList<EntityKey> SelectEntities(Property property, Allocator allocator)
      {
        var entities = new NativeList<EntityKey>(allocator);
        if (Storages.TryGetValue(property, out var storages))
        {
          foreach (var storage in storages.Values)
            entities.Add(new EntityKey { Guid = storage.GUID, Type = TransitTypes.PlaceableStorageEntity });
        }
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Selected {entities.Length} storage entities for property {property.name}", DebugLogger.Category.Handler);
        return entities;
      }
    }

    [BurstCompile]
    public class RestockSpecificShelfValidator : ITaskValidator
    {
      public void Validate(ITaskDefinition definition, EntityKey entityKey, TaskValidatorContext context, Property property, NativeList<TaskDescriptor> validTasks)
      {
        if (!Storages.TryGetValue(property, out var storages) || !storages.TryGetValue(entityKey.Guid, out var sourceShelf))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RestockSpecificShelfValidator: No storage found for GUID {entityKey.Guid}", DebugLogger.Category.Handler);
          return;
        }

        if (!context.StationInputSlots.TryGetValue(new StorageKey(entityKey.Guid), out var outputSlots))
          return;

        foreach (var slot in outputSlots)
        {
          if (slot.Quantity <= 0 || slot.IsLocked)
            continue;

          var cacheKey = new CacheKey(slot.ItemKey.Id.ToString(), slot.ItemKey.PackagingId.ToString(), slot.ItemKey.Quality != NEQuality.None ? Enum.Parse<EQuality>(slot.ItemKey.Quality.ToString()) : null, property);
          if (!CacheManager.TryGetCachedShelves(cacheKey, out var shelves))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RestockSpecificShelfValidator: No destination shelves for item {slot.ItemKey.Id}", DebugLogger.Category.Handler);
            continue;
          }

          foreach (var shelf in shelves)
          {
            var shelfGuid = shelf.Key.GUID;
            if (shelfGuid == entityKey.Guid)
              continue;

            var dropoffIndex = shelf.Value;
            var slotKey = new SlotKey(shelfGuid, dropoffIndex);
            if (context.ReservedSlots.ContainsKey(slotKey))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RestockSpecificShelfValidator: Slot {slotKey} already reserved", DebugLogger.Category.Handler);
              continue;
            }

            var task = TaskDescriptor.Create(
                definition.Type,
                definition.EmployeeType,
                definition.Priority,
                context.AssignedPropertyName.ToString(),
                slot.ItemKey,
                slot.Quantity,
                definition.PickupType,
                entityKey.Guid,
                new[] { slot.SlotIndex },
                definition.DropoffType,
                shelfGuid,
                new[] { dropoffIndex },
                context.CurrentTime
            );

            validTasks.Add(task);
            context.ReservedSlots.Add(slotKey, new SlotReservation { TaskId = task.TaskId, Timestamp = context.CurrentTime });
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestockSpecificShelfValidator: Created task {task.TaskId} for item {slot.ItemKey.Id} to shelf {shelfGuid}", DebugLogger.Category.Handler);
          }
        }
      }
    }

    public class RestockSpecificShelfExecutor : ITaskExecutor
    {
      public async Task ExecuteAsync(Employee employee, EmployeeStateData state, TaskDescriptor task)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockSpecificShelfExecutor: Employee {employee.fullName} is not a Packager", DebugLogger.Category.Handler);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestockSpecificShelfExecutor: Starting task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
        try
        {
          if (!await TaskValidationService.ReValidateTaskAsync(packager, state, task))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockSpecificShelfExecutor: Task {task.TaskId} failed revalidation", DebugLogger.Category.Handler);
            return;
          }

          if (!Storages[packager.AssignedProperty].TryGetValue(task.PickupGuid, out var sourceShelf) ||
              !Storages[packager.AssignedProperty].TryGetValue(task.DropoffGuid, out var destShelf))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockSpecificShelfExecutor: Invalid entities for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var pickupSlot = sourceShelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.PickupSlotIndex1);
          var dropoffSlot = destShelf.StorageEntity.ItemSlots.FirstOrDefault(s => s.SlotIndex == task.DropoffSlotIndex1);
          if (pickupSlot == null || dropoffSlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockSpecificShelfExecutor: Invalid slots for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var itemInstance = CreateItemInstance(task.Item);
          int quantity = Math.Min(task.Quantity, dropoffSlot.GetCapacityForItem(itemInstance));
          if (quantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockSpecificShelfExecutor: Invalid quantity {quantity} for task {task.TaskId}", DebugLogger.Category.Handler);
            return;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || itemInstance.AdvCanStackWith(s.ItemInstance));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockSpecificShelfExecutor: No inventory slot for {packager.fullName}", DebugLogger.Category.Handler);
            return;
          }

          pickupSlot.ApplyLock(packager.NetworkObject, "pickup");
          dropoffSlot.ApplyLock(packager.NetworkObject, "dropoff");
          var request = TransferRequest.Get(packager, itemInstance, quantity, inventorySlot, sourceShelf, new List<ItemSlot> { pickupSlot }, destShelf, new List<ItemSlot> { dropoffSlot });
          state.EmployeeState.TaskContext = new TaskContext { Task = task, Requests = new List<TransferRequest> { request } };

          var movementResult = await TransitAsync(packager, state, task, new List<TransferRequest> { request });
          if (movementResult.Success)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestockSpecificShelfExecutor: Successfully executed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
            await TaskDefinitionRegistry.Get(task.Type).EnqueueFollowUpTasksAsync(packager, task); // Enqueue StateValidation
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockSpecificShelfExecutor: Movement failed for task {task.TaskId}: {movementResult.Error}", DebugLogger.Category.Handler);
          }

          await state.EmployeeBeh.ExecuteTask();
        }
        catch (Exception ex)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockSpecificShelfExecutor: Exception for task {task.TaskId}, employee {packager.fullName} - {ex}", DebugLogger.Category.Handler);
        }
        finally
        {
          state.EmployeeState.TaskContext?.Cleanup(packager);
          await state.EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestockSpecificShelfExecutor: Completed task {task.TaskId} for {packager.fullName}", DebugLogger.Category.Handler);
        }
      }
    }
  }
}