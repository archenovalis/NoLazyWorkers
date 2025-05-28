using FishNet;
using HarmonyLib;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Employees.PackagerExtensions;
using NoLazyWorkers.General;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using NoLazyWorkers.Stations;
using static NoLazyWorkers.General.StorageUtilities;
using ScheduleOne.Product;
using ScheduleOne.ObjectScripts;

namespace NoLazyWorkers.Employees.Tasks.Packagers
{
  public static class RefillStationTask
  {
    private static class Utilities
    {
      /// <summary>
      /// Finds routes from a source to stations needing refill items.
      /// Limits to one route per station with a single ProductSlot, enforcing exact quality matching for non-null ProductSlot.ItemInstance.
      /// </summary>
      public static List<TransferRequest> FindRoutesFromSource(Packager packager, ITransitEntity source, List<IStationAdapter> stations, int maxRoutes)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"FindRoutesFromSource: Start for packager={packager?.fullName ?? "null"}, source={source?.GUID.ToString() ?? "null"}, stations={stations?.Count ?? 0}, maxRoutes={maxRoutes}",
            DebugLogger.Category.Packager);

        var requests = new List<TransferRequest>(maxRoutes);
        if (packager == null || source == null || stations == null || maxRoutes <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"FindRoutesFromSource: Invalid input (packager={packager != null}, source={source != null}, stations={stations != null}, maxRoutes={maxRoutes})",
              DebugLogger.Category.Packager);
          return requests;
        }

        // Pre-check available inventory slots
        int availableSlots = packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
        maxRoutes = Math.Min(maxRoutes, availableSlots);
        if (maxRoutes == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"FindRoutesFromSource: No available inventory slots for packager={packager.fullName}",
              DebugLogger.Category.Packager);
          return requests;
        }

        // Pre-filter valid stations
        var validStations = stations
            .Where(s => !s.IsInUse && !s.HasActiveOperation && (s.OutputSlot?.Quantity ?? 0) == 0)
            .ToList();
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"FindRoutesFromSource: Filtered to {validStations.Count} valid stations",
            DebugLogger.Category.Packager);

        foreach (var station in validStations)
        {
          if (maxRoutes <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: Max routes reached",
                DebugLogger.Category.Packager);
            break;
          }

          // Get refill items, skipping timed-out items
          var items = station.RefillList()?.Where(i => !IsItemTimedOut(packager.AssignedProperty, i)).ToList();
          if (items == null || !items.Any())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: No refill items for station {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // For single ProductSlot stations, create only one route
          var productSlots = station.ProductSlots;
          ItemInstance targetItem = null;
          int totalQuantity = 0;
          ProductItemInstance prodItem;
          if (productSlots.Count == 1 && productSlots[0].ItemInstance != null)
          {
            // Single slot with non-null ItemInstance: match exact quality
            targetItem = productSlots[0].ItemInstance;
            totalQuantity = Math.Min(
                station.MaxProductQuantity - productSlots[0].Quantity,
                targetItem.StackLimit - productSlots[0].Quantity
            );
            prodItem = targetItem as ProductItemInstance;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: Single slot station {station.GUID}, target item={targetItem.Name}{(prodItem != null ? ", Quality=" + prodItem.Quality : "")}, needed qty={totalQuantity}",
                DebugLogger.Category.Packager);
          }
          else
          {
            // Multiple slots or empty slot: select first valid item from RefillList
            foreach (var item in items)
            {
              if (productSlots.Any(s => s.ItemInstance == null || s.ItemInstance.AdvCanStackWith(item, allowHigherQuality: false)))
              {
                targetItem = item;
                totalQuantity = station.MaxProductQuantity - productSlots.Sum(s => s.Quantity);
                DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                    $"FindRoutesFromSource: Multi-slot/empty slot station {station.GUID}, selected item={targetItem.ID}, needed qty={totalQuantity}",
                    DebugLogger.Category.Packager);
                break;
              }
            }
          }

          if (targetItem == null || totalQuantity <= 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: No valid target item or quantity for station {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }
          prodItem = targetItem as ProductItemInstance;
          // Find source slots with exact quality match for non-null ProductSlot
          bool minQuality = productSlots.Any(s => s.ItemInstance == null);
          var sourceSlots = GetOutputSlotsContainingTemplateItem(source, targetItem, allowHigherQuality: minQuality);
          if (sourceSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: No source slots for item {targetItem.Name}{(prodItem != null ? ", Quality=" + prodItem.Quality : "")}",
                DebugLogger.Category.Packager);
            continue;
          }

          var destination = station.TransitEntity;
          if (destination == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"FindRoutesFromSource: Null TransitEntity for station {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Reserve delivery slots with exact quality match
          var deliverySlots = productSlots.AdvReserveInputSlotsForItem(targetItem, packager.NetworkObject, allowHigherQuality: minQuality);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: No delivery slots for item {targetItem.ID} at station {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Calculate quantity based on source and destination capacity
          int quantity = Math.Min(sourceSlots.Sum(s => s.Quantity), totalQuantity);
          if (quantity <= 0 || !packager.Movement.CanGetTo(station.GetAccessPoint(packager)))
          {
            destination.RemoveSlotLocks(packager.NetworkObject);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromSource: Invalid quantity={quantity} or unreachable station {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Reserve inventory slot
          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            destination.RemoveSlotLocks(packager.NetworkObject);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"FindRoutesFromSource: No inventory slot for packager={packager.fullName}",
                DebugLogger.Category.Packager);
            break;
          }

          // Lock source slots
          foreach (var pickupSlot in sourceSlots)
            pickupSlot.ApplyLock(packager.NetworkObject, "pickup for refill");

          // Create single transfer request
          var request = TransferRequest.Get(packager, targetItem, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
          requests.Add(request);
          SetReservedSlot(packager, inventorySlot);
          maxRoutes--;

          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"FindRoutesFromSource: Added route for {quantity} of {targetItem.ID}{targetItem.Name}{(prodItem != null ? " (Quality=" + prodItem.Quality + ")" : "")} to station {station.GUID}",
              DebugLogger.Category.Packager);

          // For single ProductSlot, break after one route
          if (productSlots.Count == 1)
            break;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"FindRoutesFromSource: Completed with {requests.Count} routes for packager={packager.fullName}",
            DebugLogger.Category.Packager);
        return requests;
      }
    }

    public enum RefillStationSteps
    {
      CheckRefill, // Validate station and source
      Refill,     // Execute refill
      End         // Cleanup
    }

    public static IEmployeeTask Create(Packager packager, int priority)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateRefillStationTask: Entered for packager={packager?.fullName}, priority={priority}", DebugLogger.Category.Packager);
      var workSteps = new List<WorkStep<RefillStationSteps>>
                {
                    new WorkStep<RefillStationSteps>
                    {
                        Step = RefillStationSteps.CheckRefill,
                        Validate = Logic.ValidateRefillStation,
                        Execute = async (emp, state) => state.EmployeeState.CurrentWorkStep = RefillStationSteps.Refill,
                        Transitions = { { "Success", RefillStationSteps.Refill } }
                    },
                    new WorkStep<RefillStationSteps>
                    {
                        Step = RefillStationSteps.Refill,
                        Validate = Logic.ValidateRefillStation,
                        Execute = Logic.ExecuteRefillStation,
                        Transitions = { { "Success", RefillStationSteps.End } }
                    },
                    new WorkStep<RefillStationSteps>
                    {
                        Step = RefillStationSteps.End,
                        Validate = async (emp, state) => true,
                        Execute = Logic.ExecuteEnd,
                        Transitions = { }
                    }
                };
      var task = new EmployeeTask<RefillStationSteps>(packager, "RefillStation", priority, workSteps);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreateRefillStationTask: Created task with {workSteps.Count} steps for packager={packager.fullName}", DebugLogger.Category.Packager);
      return task;
    }

    public static class Logic
    {
      public static async Task<bool> ValidateRefillStation(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ValidateRefillStation: Start for employee={employee?.fullName ?? "null"}",
            DebugLogger.Category.Packager);

        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              "ValidateRefillStation: Employee is not a Packager",
              DebugLogger.Category.Packager);
          return false;
        }

        state.EmployeeState.TaskContext = new TaskContext();
        if (!PropertyStations.TryGetValue(packager.AssignedProperty, out var stations) || stations == null || !stations.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ValidateRefillStation: No stations for property {packager.AssignedProperty?.name ?? "null"}",
              DebugLogger.Category.Packager);
          return false;
        }

        foreach (var station in stations)
        {
          if (station.IsInUse || station.HasActiveOperation || (station.TypeOf == typeof(MixingStation) && station.OutputSlot?.Quantity > 0))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"ValidateRefillStation: Skipping {station.TypeOf.Name} {station.GUID} (inUse={station.IsInUse}, active={station.HasActiveOperation}, output={station.OutputSlot?.Quantity})",
                DebugLogger.Category.Packager);
            continue;
          }

          var items = station.RefillList()?.Where(i => i != null && !IsItemTimedOut(packager.AssignedProperty, i)).ToList();
          if (items == null || !items.Any())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"ValidateRefillStation: No valid refill items for {station.TypeOf.Name} {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          var productSlots = station.ProductSlots;
          if (productSlots.Count == 1 && productSlots[0].ItemInstance != null)
          {
            // Non-null ProductSlot: match exact item in RefillList
            var slotItem = productSlots[0].ItemInstance;
            var matchingItem = items.FirstOrDefault(i => i.ID == slotItem.ID && (i as ProductItemInstance)?.Quality == (slotItem as ProductItemInstance)?.Quality);
            if (matchingItem == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"ValidateRefillStation: Slot item {slotItem.ID} (quality={(slotItem as ProductItemInstance)?.Quality}) not in RefillList for {station.TypeOf.Name} {station.GUID}",
                  DebugLogger.Category.Packager);
              continue;
            }

            var shelf = FindStorageWithItem(packager, matchingItem, 1);
            if (shelf.Key != null)
            {
              state.EmployeeState.TaskContext.Station = station;
              state.EmployeeState.TaskContext.Pickup = shelf.Key;
              state.EmployeeState.TaskContext.Item = matchingItem;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"ValidateRefillStation: Found shelf {shelf.Key.GUID} for item {matchingItem.ID} (quality={(matchingItem as ProductItemInstance)?.Quality}) for {station.TypeOf.Name} {station.GUID}",
                  DebugLogger.Category.Packager);
              return true;
            }
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"ValidateRefillStation: No shelf found for item {matchingItem.ID} (quality={(matchingItem as ProductItemInstance)?.Quality}) for {station.TypeOf.Name} {station.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Null ProductSlot or multiple slots: find first item in RefillList on a shelf
          foreach (var item in items)
          {
            KeyValuePair<PlaceableStorageEntity, int> shelf;
            if (item.ID == "Any")
            {
              shelf = FindStorageWithItem(packager, item, 1);
              if (shelf.Key != null)
              {
                state.EmployeeState.TaskContext.Station = station;
                state.EmployeeState.TaskContext.Pickup = shelf.Key;
                state.EmployeeState.TaskContext.Item = item;
                DebugLogger.Log(DebugLogger.LogLevel.Info,
                    $"ValidateRefillStation: Found shelf {shelf.Key.GUID} for 'Any' (quality={(item as ProductItemInstance)?.Quality}) for {station.TypeOf.Name} {station.GUID}",
                    DebugLogger.Category.Packager);
                return true;
              }
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"ValidateRefillStation: No shelf found for 'Any' (quality={(item as ProductItemInstance)?.Quality}) for {station.TypeOf.Name} {station.GUID}",
                  DebugLogger.Category.Packager);
              continue;
            }

            shelf = FindStorageWithItem(packager, item, 1);
            if (shelf.Key != null)
            {
              state.EmployeeState.TaskContext.Station = station;
              state.EmployeeState.TaskContext.Pickup = shelf.Key;
              state.EmployeeState.TaskContext.Item = item;
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"ValidateRefillStation: Found shelf {shelf.Key.GUID} for item {item.ID} (quality={(item as ProductItemInstance)?.Quality}) for {station.TypeOf.Name} {station.GUID}",
                  DebugLogger.Category.Packager);
              return true;
            }
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"ValidateRefillStation: No shelf found for item {item.ID} (quality={(item as ProductItemInstance)?.Quality}) for {station.TypeOf.Name} {station.GUID}",
                DebugLogger.Category.Packager);
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ValidateRefillStation: No valid stations need refilling for packager={packager.fullName}",
            DebugLogger.Category.Packager);
        return false;
      }

      public static async Task ExecuteRefillStation(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ExecuteRefillStation: Start for employee={employee?.fullName ?? "null"}",
            DebugLogger.Category.Packager);

        if (!(employee is Packager packager) ||
            state.EmployeeState.TaskContext?.Station == null ||
            state.EmployeeState.TaskContext.Pickup == null ||
            state.EmployeeState.TaskContext.Item == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"ExecuteRefillStation: Invalid packager or context (station={state.EmployeeState.TaskContext?.Station?.GUID}, pickup={state.EmployeeState.TaskContext?.Pickup?.GUID}, item={state.EmployeeState.TaskContext?.Item?.ID})",
              DebugLogger.Category.Packager);
          state.EmployeeState.CurrentWorkStep = RefillStationSteps.End;
          return;
        }

        var station = state.EmployeeState.TaskContext.Station;
        var source = state.EmployeeState.TaskContext.Pickup;
        var item = state.EmployeeState.TaskContext.Item;
        int maxRoutes = Math.Min(5, packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        var requests = Utilities.FindRoutesFromSource(packager, source, new List<IStationAdapter> { station }, maxRoutes);

        if (!requests.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ExecuteRefillStation: No valid routes for station {station.GUID}",
              DebugLogger.Category.Packager);
          state.EmployeeState.CurrentWorkStep = RefillStationSteps.End;
          return;
        }

        state.EmployeeState.TaskContext.Requests = requests;
        state.EmployeeBeh.StartMovement(CreatePrioritizedRoutes(requests, 100), RefillStationSteps.End);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ExecuteRefillStation: Started movement with {requests.Count} routes for {station.TypeOf.Name} {station.GUID}",
            DebugLogger.Category.Packager);
      }

      public static async Task ExecuteEnd(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ExecuteEnd: Cleaning up for employee={employee?.fullName ?? "null"}",
            DebugLogger.Category.Packager);
        state.EmployeeState.TaskContext?.Cleanup(employee);
        await state.EmployeeBeh.Disable();
      }
    }
  }

  public static class EmptyLoadingDockTask
  {
    private static class Utilities
    {
      /// <summary>
      /// Finds all valid transfer routes from a loading dock to storage, combining stackable items
      /// and respecting inventory slot limits. Processes all dock slots in one pass to prevent freezes.
      /// </summary>
      /// <param name="packager">The packager performing the task.</param>
      /// <param name="state">The employee's state data.</param>
      /// <param name="dock">The loading dock to empty.</param>
      /// <param name="maxRoutes">Maximum number of routes to find (limited by inventory slots).</param>
      /// <returns>A list of TransferRequest objects representing valid routes.</returns>
      public static async Task<List<TransferRequest>> FindRoutesFromDock(Packager packager, StateData state, LoadingDock dock, int maxRoutes)
      {
        // Log entry for debugging
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"FindRoutesFromDock: Entered for packager={packager?.fullName}, dock={dock?.GUID}, maxRoutes={maxRoutes}",
            DebugLogger.Category.Packager);

        var requests = new List<TransferRequest>();

        // Validate inputs to prevent null reference errors
        if (packager == null || state == null || dock == null || maxRoutes <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"FindRoutesFromDock: Invalid input: packager={packager != null}, state={state != null}, dock={dock != null}, maxRoutes={maxRoutes}",
              DebugLogger.Category.Packager);
          return requests;
        }

        // Get available inventory slots
        var availableSlots = packager.Inventory.ItemSlots.Where(s => s.ItemInstance == null).ToList();
        maxRoutes = Math.Min(maxRoutes, availableSlots.Count);
        if (maxRoutes == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"FindRoutesFromDock: No available inventory slots for packager={packager.fullName}",
              DebugLogger.Category.Packager);
          return requests;
        }

        // Group slots by item to combine stackable items
        var itemGroups = new Dictionary<ItemInstance, List<ItemSlot>>();
        foreach (var slot in dock.OutputSlots)
        {
          // Skip invalid, empty, or locked slots
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromDock: Skipping slot: item={slot?.ItemInstance?.ID}, qty={slot?.Quantity}, locked={slot?.IsLocked}",
                DebugLogger.Category.Packager);
            continue;
          }

          var item = slot.ItemInstance;
          // Check if item is timed out
          if (IsItemTimedOut(packager.AssignedProperty, item))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromDock: Item {item.ID} timed out for dock {dock.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Group slots by item instance
          if (!itemGroups.TryGetValue(item, out var slots))
          {
            slots = new List<ItemSlot>();
            itemGroups[item] = slots;
          }
          slots.Add(slot);
        }

        int slotIndex = 0; // Track inventory slot assignment
        foreach (var group in itemGroups)
        {
          // Stop if max routes or inventory slots are reached
          if (maxRoutes <= 0 || slotIndex >= availableSlots.Count)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"FindRoutesFromDock: Reached max routes ({maxRoutes}) or inventory slots ({slotIndex}/{availableSlots.Count})",
                DebugLogger.Category.Packager);
            break;
          }

          var item = group.Key;
          var pickupSlots = group.Value;

          // Find destination storage for the item
          var destination = FindStorageForDelivery(packager, item);
          if (destination == null)
          {
            AddItemTimeout(packager.AssignedProperty, item);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromDock: No destination for item {item.ID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Reserve delivery slots at destination
          var destTransit = destination as ITransitEntity;
          var deliverySlots = destination.InputSlots.AdvReserveInputSlotsForItem(item, packager.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            AddItemTimeout(packager.AssignedProperty, item);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromDock: No delivery slots for item {item.ID} at {destination.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Calculate total quantity available from pickup slots
          int totalAvailable = pickupSlots.Sum(s => s.Quantity);
          // Calculate max quantity that can be delivered
          int maxDeliverable = deliverySlots.Sum(s => s.GetCapacityForItem(item));
          // Limit by inventory slot's StackLimit
          int stackLimit = item.StackLimit;
          // Determine quantity to transfer
          int quantity = Math.Min(Math.Min(totalAvailable, maxDeliverable), stackLimit);

          // Validate quantity and dock accessibility
          if (quantity <= 0 || !packager.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, packager).position))
          {
            destTransit.RemoveSlotLocks(packager.NetworkObject);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"FindRoutesFromDock: Invalid quantity={quantity} or unreachable dock {dock.GUID}",
                DebugLogger.Category.Packager);
            continue;
          }

          // Assign inventory slot
          var inventorySlot = availableSlots[slotIndex];
          slotIndex++;

          // Lock slots
          foreach (var slot in pickupSlots)
            slot.ApplyLock(packager.NetworkObject, "pickup");

          // Create transfer request
          var request = TransferRequest.Get(
              packager, item, quantity, inventorySlot,
              dock, pickupSlots, destination, deliverySlots);
          requests.Add(request);

          // Reserve inventory slot
          SetReservedSlot(packager, inventorySlot);
          maxRoutes--;

          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"FindRoutesFromDock: Added route for {quantity} of {item.ID} to {destination.GUID}",
              DebugLogger.Category.Packager);
        }

        // Log completion
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"FindRoutesFromDock: Found {requests.Count} routes for packager={packager.fullName}",
            DebugLogger.Category.Packager);
        return requests;
      }
    }

    public enum EmptyLoadingDockSteps
    {
      CheckDock, // Validate dock items
      Empty,     // Execute emptying
      End        // Cleanup
    }

    public static IEmployeeTask Create(Packager packager, int priority)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateEmptyLoadingDockTask: Entered for packager={packager?.fullName}, priority={priority}", DebugLogger.Category.Packager);
      var workSteps = new List<WorkStep<EmptyLoadingDockSteps>>
                {
                    new WorkStep<EmptyLoadingDockSteps>
                    {
                        Step = EmptyLoadingDockSteps.CheckDock,
                        Validate = Logic.ValidateEmptyLoadingDock,
                        Execute = async (emp, state) => state.EmployeeState.CurrentWorkStep = EmptyLoadingDockSteps.Empty,
                        Transitions = { { "Success", EmptyLoadingDockSteps.Empty } }
                    },
                    new WorkStep<EmptyLoadingDockSteps>
                    {
                        Step = EmptyLoadingDockSteps.Empty,
                        Validate = Logic.ValidateEmptyLoadingDock,
                        Execute = Logic.ExecuteEmptyLoadingDock,
                        Transitions = { { "Success", EmptyLoadingDockSteps.CheckDock }, { "Failure", EmptyLoadingDockSteps.End } }
                    },
                    new WorkStep<EmptyLoadingDockSteps>
                    {
                        Step = EmptyLoadingDockSteps.End,
                        Validate = async (emp, state) => true,
                        Execute = Logic.ExecuteEnd,
                        Transitions = { }
                    }
                };
      var task = new EmployeeTask<EmptyLoadingDockSteps>(packager, "EmptyLoadingDock", priority, workSteps);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreateEmptyLoadingDockTask: Created task with {workSteps.Count} steps for packager={packager.fullName}", DebugLogger.Category.Packager);
      return task;
    }

    public static class Logic
    {
      public static async Task<bool> ValidateEmptyLoadingDock(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateEmptyLoadingDock: Entered for employee={employee?.fullName}", DebugLogger.Category.Packager);
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "ValidateEmptyLoadingDock: Employee is not a Packager", DebugLogger.Category.Packager);
          return false;
        }

        state.EmployeeState.TaskContext ??= new TaskContext();
        LoadingDock[] docks = state.EmployeeState.TaskContext.Pickup is LoadingDock loadingDock
            ? new[] { loadingDock }
            : packager.AssignedProperty.LoadingDocks ?? Array.Empty<LoadingDock>();

        foreach (var dock in docks)
        {
          if (!dock.IsInUse)
            continue;

          if (dock.OutputSlots.Any(slot => slot?.ItemInstance != null && slot.Quantity > 0 && !slot.IsLocked && !IsItemTimedOut(packager.AssignedProperty, slot.ItemInstance)))
          {
            state.EmployeeState.TaskContext.Pickup = dock;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateEmptyLoadingDock: Found items in dock {dock.GUID}", DebugLogger.Category.Packager);
            return true;
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateEmptyLoadingDock: No docks with items for packager={packager.fullName}", DebugLogger.Category.Packager);
        return false;
      }

      public static async Task ExecuteEmptyLoadingDock(Employee employee, StateData state)
      {
        // Log entry for debugging
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ExecuteEmptyLoadingDock: Entered for employee={employee?.fullName}",
            DebugLogger.Category.Packager);

        // Validate packager and dock
        if (!(employee is Packager packager) || state.EmployeeState.TaskContext?.Pickup is not LoadingDock dock)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"ExecuteEmptyLoadingDock: Invalid packager or dock: pickup={state.EmployeeState.TaskContext?.Pickup?.GUID}",
              DebugLogger.Category.Packager);
          state.EmployeeState.CurrentWorkStep = EmptyLoadingDockSteps.End;
          return;
        }

        // Determine max routes based on inventory slots
        int maxRoutes = Math.Min(5, packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null));

        // Find routes from dock
        var requests = await Utilities.FindRoutesFromDock(packager, state, dock, maxRoutes);
        if (!requests.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ExecuteEmptyLoadingDock: No valid routes for dock {dock.GUID}",
              DebugLogger.Category.Packager);
          state.EmployeeState.CurrentWorkStep = EmptyLoadingDockSteps.End;
          return;
        }

        // Store requests in context
        state.EmployeeState.TaskContext.Requests = requests;

        // Start movement with appropriate next step
        state.EmployeeBeh.StartMovement(CreatePrioritizedRoutes(requests, 80), nextStep: EmptyLoadingDockSteps.End);

        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ExecuteEmptyLoadingDock: Started movement with {requests.Count} routes for dock {dock.GUID}",
            DebugLogger.Category.Packager);
      }

      public static async Task ExecuteEnd(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteEnd: Cleaning up for employee={employee.fullName}", DebugLogger.Category.Packager);
        state.EmployeeState.TaskContext?.Cleanup(employee);
        await state.EmployeeBeh.Disable();
      }
    }
  }

  public static class RestockSpecificShelfTask
  {
    private static class Utilities
    {
      // Finds routes from a shelf to specific shelves
      public static async Task<List<TransferRequest>> FindRoutesFromShelf(Packager packager, StateData state, ITransitEntity source, int maxRoutes, List<Guid> processedShelves)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindRoutesFromShelf: Entered for packager={packager?.fullName}, source={source?.GUID}, maxRoutes={maxRoutes}, processedShelves={processedShelves?.Count ?? 0}", DebugLogger.Category.Packager);
        var requests = new List<TransferRequest>();
        if (packager == null || state == null || source == null || maxRoutes <= 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"FindRoutesFromShelf: Invalid input: packager={packager != null}, state={state != null}, source={source != null}, maxRoutes={maxRoutes}", DebugLogger.Category.Packager);
          return requests;
        }

        // Pre-check available inventory slots
        int availableSlots = packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null);
        maxRoutes = Math.Min(maxRoutes, availableSlots);
        if (maxRoutes == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindRoutesFromShelf: No available inventory slots for packager={packager.fullName}", DebugLogger.Category.Packager);
          return requests;
        }

        foreach (var slot in source.OutputSlots)
        {
          if (maxRoutes <= 0) break;
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked)
            continue;

          var item = slot.ItemInstance;
          if (IsItemTimedOut(packager.AssignedProperty, item) ||
              (NoDropOffCache.TryGetValue(packager.AssignedProperty, out var cache) && cache.Any(i => item.AdvCanStackWith(i))))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindRoutesFromShelf: Item {item.ID} timed out or in no-drop-off cache", DebugLogger.Category.Packager);
            continue;
          }
          var destination = FindStorageForDelivery(packager, item, allowAnyShelves: false);
          var destTransit = destination as ITransitEntity;
          if (destination == null || destTransit == source)
          {
            AddItemTimeout(packager.AssignedProperty, item);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindRoutesFromShelf: No valid destination for item {item.ID}", DebugLogger.Category.Packager);
            continue;
          }

          var deliverySlots = destination.InputSlots.AdvReserveInputSlotsForItem(item, packager.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            AddItemTimeout(packager.AssignedProperty, item);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindRoutesFromShelf: No delivery slots for item {item.ID} at {destination.GUID}", DebugLogger.Category.Packager);
            continue;
          }

          int quantity = Math.Min(slot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(item)));
          if (quantity <= 0)
          {
            destTransit.RemoveSlotLocks(packager.NetworkObject);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindRoutesFromShelf: Invalid quantity={quantity}", DebugLogger.Category.Packager);
            continue;
          }

          var inventorySlot = packager.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            destTransit.RemoveSlotLocks(packager.NetworkObject);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindRoutesFromShelf: No inventory slot for packager={packager.fullName}", DebugLogger.Category.Packager);
            break;
          }

          slot.ApplyLock(packager.NetworkObject, "pickup");

          var request = TransferRequest.Get(packager, item, quantity, inventorySlot, source, new List<ItemSlot> { slot }, destination, deliverySlots);
          requests.Add(request);
          SetReservedSlot(packager, inventorySlot);
          maxRoutes--;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindRoutesFromShelf: Added route for {quantity} of {item.ID} to {destination.GUID}", DebugLogger.Category.Packager);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"FindRoutesFromShelf: Found {requests.Count} routes for packager={packager.fullName}", DebugLogger.Category.Packager);
        await Task.Yield();
        return requests;
      }
    }

    public enum RestockSpecificShelfSteps
    {
      CheckRestock, // Validate shelf items
      Restock,      // Execute restock
      End           // Cleanup
    }

    public static IEmployeeTask Create(Packager packager, int priority)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CreateRestockSpecificShelfTask: Entered for packager={packager?.fullName}, priority={priority}", DebugLogger.Category.Packager);
      var workSteps = new List<WorkStep<RestockSpecificShelfSteps>>
                {
                    new WorkStep<RestockSpecificShelfSteps>
                    {
                        Step = RestockSpecificShelfSteps.CheckRestock,
                        Validate = Logic.ValidateRestockSpecificShelf,
                        Execute = async (emp, state) => state.EmployeeState.CurrentWorkStep = RestockSpecificShelfSteps.Restock,
                        Transitions = { { "Success", RestockSpecificShelfSteps.Restock } }
                    },
                    new WorkStep<RestockSpecificShelfSteps>
                    {
                        Step = RestockSpecificShelfSteps.Restock,
                        Validate = Logic.ValidateRestockSpecificShelf,
                        Execute = Logic.ExecuteRestockSpecificShelf,
                        Transitions = { { "Success", RestockSpecificShelfSteps.End } }
                    },
                    new WorkStep<RestockSpecificShelfSteps>
                    {
                        Step = RestockSpecificShelfSteps.End,
                        Validate = async (emp, state) => true,
                        Execute = Logic.ExecuteEnd,
                        Transitions = { }
                    }
                };
      var task = new EmployeeTask<RestockSpecificShelfSteps>(packager, "RestockSpecificShelf", priority, workSteps);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"CreateRestockSpecificShelfTask: Created task with {workSteps.Count} steps for packager={packager.fullName}", DebugLogger.Category.Packager);
      return task;
    }

    public static class Logic
    {
      public static async Task<bool> ValidateRestockSpecificShelf(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateRestockSpecificShelf: Entered for employee={employee?.fullName}", DebugLogger.Category.Packager);
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "ValidateRestockSpecificShelf: Employee is not a Packager", DebugLogger.Category.Packager);
          return false;
        }

        state.EmployeeState.TaskContext = new TaskContext();
        if (!StorageExtensions.SpecificShelves.TryGetValue(packager.AssignedProperty, out var specificShelves))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateRestockSpecificShelf: No specific shelves for property {packager.AssignedProperty.name}", DebugLogger.Category.Packager);
          return false;
        }

        foreach (var kvp in specificShelves)
        {
          var configuredItem = kvp.Key;
          foreach (var shelf in StorageExtensions.Storages.Values.Where(s => s.ParentProperty == packager.AssignedProperty))
          {
            foreach (var slot in shelf.OutputSlots)
            {
              if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked || IsItemTimedOut(packager.AssignedProperty, slot.ItemInstance))
                continue;

              var item = slot.ItemInstance;
              if (!item.AdvCanStackWith(configuredItem, allowHigherQuality: true))
                continue;

              var specificShelf = FindStorageForDelivery(packager, item, allowAnyShelves: false);
              if (specificShelf != null && specificShelf != shelf)
              {
                state.EmployeeState.TaskContext.Pickup = shelf;
                state.EmployeeState.TaskContext.Item = item;
                state.EmployeeState.TaskContext.Dropoff = specificShelf;
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateRestockSpecificShelf: Found item {item.ID} on shelf {shelf.GUID} for specific shelf {specificShelf.GUID}", DebugLogger.Category.Packager);
                return true;
              }
            }
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateRestockSpecificShelf: No items to restock for packager={packager.fullName}", DebugLogger.Category.Packager);
        return false;
      }

      public static async Task ExecuteRestockSpecificShelf(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ExecuteRestockSpecificShelf: Entered for employee={employee?.fullName}", DebugLogger.Category.Packager);
        if (!(employee is Packager packager) || state.EmployeeState.TaskContext?.Pickup == null || state.EmployeeState.TaskContext.Dropoff == null || state.EmployeeState.TaskContext.Item == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ExecuteRestockSpecificShelf: Invalid packager or context: pickup={state.EmployeeState.TaskContext?.Pickup?.GUID}, dropoff={state.EmployeeState.TaskContext?.Dropoff?.GUID}, item={state.EmployeeState.TaskContext?.Item?.ID}", DebugLogger.Category.Packager);
          state.EmployeeState.CurrentWorkStep = RestockSpecificShelfSteps.End;
          return;
        }

        var source = state.EmployeeState.TaskContext.Pickup;
        int maxRoutes = Math.Min(1, packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        var requests = await Utilities.FindRoutesFromShelf(packager, state, source, maxRoutes, new List<Guid>());

        if (!requests.Any())
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteRestockSpecificShelf: No valid routes for shelf {source.GUID}", DebugLogger.Category.Packager);
          state.EmployeeState.CurrentWorkStep = RestockSpecificShelfSteps.End;
          return;
        }

        state.EmployeeState.TaskContext.Requests = requests;
        state.EmployeeBeh.StartMovement(CreatePrioritizedRoutes(requests, 60), RestockSpecificShelfSteps.End);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteRestockSpecificShelf: Started movement with {requests.Count} routes for shelf {source.GUID}", DebugLogger.Category.Packager);
      }

      public static async Task ExecuteEnd(Employee employee, StateData state)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ExecuteEnd: Cleaning up for employee={employee.fullName}", DebugLogger.Category.Packager);
        state.EmployeeState.TaskContext?.Cleanup(employee);
        await state.EmployeeBeh.Disable();
      }
    }
  }
}
