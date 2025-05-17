// Summary: Defines core logic for NPC item movement and storage management in a Unity game mod.
//          Handles route planning, item pickup/delivery, and storage configuration using MelonLoader and Harmony patches.
// Role: Extends NPC behavior to manage item transfers between shelves, stations, and docks, and customizes storage rack behavior.
// Related Files: DebugLogger.cs, NavMeshUtility.cs, CoroutineRunner.cs, StorageConfigurableProxy.cs
// Dependencies: Unity, MelonLoader, HarmonyLib, Newtonsoft.Json
// Assumptions: All game fields are publicized at compile time; server-side logic runs on InstanceFinder.IsServer.

using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.Employees;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.NoLazyUtilities;
using GameKit.Utilities;
using Beautify.Demos;
using FishNet.Object;
using Pathfinding.Examples;
using ScheduleOne.NPCs;
using UnityEngine.InputSystem;
using NoLazyWorkers.General;
using System;
using UnityEngine.InputSystem.EnhancedTouch;
using System.Data.Common;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using Object = UnityEngine.Object;
using NoLazyWorkers.Stations;
using FishNet.Managing.Object;
using Newtonsoft.Json.Linq;
using ScheduleOne.EntityFramework;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.UI.Management;
using static NoLazyWorkers.General.StorageUtilities;
using System.Threading.Tasks;

namespace NoLazyWorkers.Employees
{
  public class PackagerBehaviour : EmployeeBehaviour
  {
    private readonly Packager _packager;

    public PackagerBehaviour(Packager packager, IEmployeeAdapter adapter)
        : base(packager, adapter, new List<IEmployeeTask>
        {
                new RefillStationTask(100, 0),
                new EmptyLoadingDockTask(80, 1),
                new RestockSpecificShelfTask(60, 2),
                new PackagingStationBeh.PackagingStation_Work(120, 3)
        })
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerBehaviour: Initialized for NPC {_packager.fullName}", DebugLogger.Category.Packager);
    }

    public class RefillStationTask : IEmployeeTask
    {
      private readonly int _priority;
      private readonly int _scanIndex;
      public int Priority => _priority;
      public int ScanIndex => _scanIndex;

      public RefillStationTask(int priority, int scanIndex)
      {
        _priority = priority;
        _scanIndex = scanIndex;
      }

      private enum RefillStep
      {
        FindRoutes,
        FindShelf,
        MovingToShelf,
        MovingToStation
      }

      public async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "RefillStationTask.CanExecute: Invalid packager or state", DebugLogger.Category.Packager);
          return false;
        }

        if (!PropertyStations.TryGetValue(packager.AssignedProperty, out var stations))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationTask.CanExecute: No stations for property {packager.AssignedProperty}", DebugLogger.Category.Packager);
          return false;
        }

        foreach (var station in stations)
        {
          if (station.IsInUse || station.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationTask.CanExecute: Station {station.GUID} skipped (InUse={station.IsInUse}, HasActiveOperation={station.HasActiveOperation})", DebugLogger.Category.Packager);
            continue;
          }

          if (station.OutputSlot?.ItemInstance != null && station.OutputSlot.Quantity > 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationTask.CanExecute: Station {station.GUID} skipped (OutputSlot has {station.OutputSlot.Quantity} {station.OutputSlot.ItemInstance.ID})", DebugLogger.Category.Packager);
            continue;
          }

          var items = station.RefillList();
          foreach (var item in items)
          {
            if (IsItemTimedOut(packager.AssignedProperty, item))
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationTask.CanExecute: Item {item.ID} timed out for station {station.GUID}", DebugLogger.Category.Packager);
              continue;
            }
            var shelf = await FindShelfWithItemAsync(packager, item, 1);
            if (shelf.Key != null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"RefillStationTask.CanExecute: Station {station.GUID} can be refilled with {item.ID} (shelf found: {shelf.Key.GUID})", DebugLogger.Category.Packager);
              var state = GetState(employee);
              state.SetValue("station", station);
              state.SetValue("Source", shelf);
              state.SetValue("Item", item);
              return true;
            }
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationTask.CanExecute: No stations need refilling for {packager.fullName}", DebugLogger.Category.Packager);
        return false;
      }

      public void Execute(Employee employee, StateData state)
      {
        if (!(employee is Packager packager) || state == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RefillStationTask.Execute: Invalid packager or state", DebugLogger.Category.Packager);
          state.CurrentState = EState.Idle;
          return;
        }

        if (!state.TryGetValue<RefillStep>("RefillStep", out var currentStep))
        {
          currentStep = RefillStep.FindRoutes;
          state.SetValue("RefillStep", currentStep);
        }

        switch (currentStep)
        {
          case RefillStep.FindRoutes:
            HandleFindRoutes(packager, state);
            break;
        }
      }

      private void HandleFindRoutes(Packager packager, StateData state)
      {
        if (!PropertyStations.TryGetValue(packager.AssignedProperty, out var stations))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RefillStationTask.FindStation: No stations for {packager.fullName}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var requests = new List<TransferRequest>();
        int maxRoutes = Math.Min(5, packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        ITransitEntity source = state.TryGetValue<ITransitEntity>("Source", out var s) ? s : null;

        if (source != null)
        {
          requests = FindRoutesFromSource(packager, source, stations, maxRoutes);
          if (requests.Count > 0)
          {
            state.SetValue("Requests", requests);
            state.SetValue("RefillStep", RefillStep.MovingToShelf);
            StartRefillMovement(packager, state, requests, RefillStep.FindShelf);
            return;
          }
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RefillStationTask.FindStation: No valid routes for {packager.fullName}", DebugLogger.Category.Packager);
        ResetTask(state);
      }

      private List<TransferRequest> FindRoutesFromSource(Packager packager, ITransitEntity source, List<IStationAdapter> stations, int maxRoutes)
      {
        var requests = new List<TransferRequest>();
        foreach (var station in stations)
        {
          if (maxRoutes <= 0)
            break;
          if (station.IsInUse || station.HasActiveOperation || (station.OutputSlot?.ItemInstance != null && station.OutputSlot.Quantity > 0))
            continue;

          var items = station.RefillList();
          foreach (var item in items)
          {
            if (IsItemTimedOut(packager.AssignedProperty, item))
              continue;

            var sourceSlots = GetOutputSlotsContainingTemplateItem(source, item)
                .Where(s => s.Quantity > 0 && (!s.IsLocked || s.ActiveLock?.LockOwner == packager.NetworkObject)).ToList();
            if (sourceSlots.Count == 0)
              continue;

            var destination = station.TransitEntity;
            var deliverySlots = destination.ReserveInputSlotsForItem(item, packager.NetworkObject);
            if (deliverySlots == null || deliverySlots.Count == 0)
              continue;

            int quantity = Math.Min(sourceSlots.Sum(s => s.Quantity), station.MaxProductQuantity - station.GetInputQuantity());
            if (quantity <= 0)
              continue;

            if (!packager.Movement.CanGetTo(station.GetAccessPoint(packager)))
              continue;

            var inventorySlot = packager.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
            if (inventorySlot == null)
              continue;

            var request = new TransferRequest(packager, item, quantity, inventorySlot, source, sourceSlots, destination, deliverySlots);
            requests.Add(request);
            maxRoutes--;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RefillStationTask.FindRoutesFromSource: Added route for {quantity} of {item.ID} to station {station.GUID}", DebugLogger.Category.Packager);
          }
        }
        return requests;
      }

      private void StartRefillMovement(Packager packager, StateData state, List<TransferRequest> requests, RefillStep nextStep)
      {
        state.EmployeeBeh.StartMovement(CreatePrioritizedRoutes(requests, Priority), (emp, s) =>
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartRefillMovement: Refillroutes completed for {emp.fullName}", DebugLogger.Category.Packager);
          state.EmployeeBeh.Disable();
        });
      }

      private void ResetTask(StateData state)
      {
        state.CurrentState = EState.Idle;
        state.RemoveValue<RefillStep>("RefillStep");
        state.RemoveValue<ITransitEntity>("Source");
        state.RemoveValue<List<TransferRequest>>("Requests");
        state.RemoveValue<TransferRequest>("CurrentRequest");
        DebugLogger.Log(DebugLogger.LogLevel.Info, "RefillStationTask.ResetTask: Task reset", DebugLogger.Category.Packager);
      }
    }

    public class EmptyLoadingDockTask : IEmployeeTask
    {
      private readonly int _priority;
      private readonly int _scanIndex;
      public int Priority => _priority;
      public int ScanIndex => _scanIndex;

      public EmptyLoadingDockTask(int priority, int scanIndex)
      {
        _priority = priority;
        _scanIndex = scanIndex;
      }

      private enum DockStep
      {
        FindDock,
        FindShelf,
        MovingToDock,
        MovingToShelf
      }

      public bool CanExecute(Employee employee, ITransitEntity recheck = null)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "EmptyLoadingDockTask.CanExecute: Invalid packager or state", DebugLogger.Category.Packager);
          return false;
        }

        foreach (var dock in packager.AssignedProperty.LoadingDocks ?? Enumerable.Empty<LoadingDock>())
        {
          if (!dock.IsInUse)
            continue;
          foreach (var slot in dock.OutputSlots)
          {
            if (slot?.ItemInstance != null && slot.Quantity > 0 && !slot.IsLocked)
              return true;
          }
        }
        return false;
      }

      public async Task Execute(Employee employee, StateData state)
      {
        if (!(employee is Packager packager) || state == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"EmptyLoadingDockTask.Execute: Invalid packager or state", DebugLogger.Category.Packager);
          state.CurrentState = EState.Idle;
          return;
        }

        if (!state.TryGetValue<DockStep>("DockStep", out var currentStep))
        {
          currentStep = DockStep.FindDock;
          state.SetValue("DockStep", currentStep);
        }

        switch (currentStep)
        {
          case DockStep.FindDock:
            await HandleFindDock(packager, state);
            break;
          case DockStep.FindShelf:
            HandleFindShelf(packager, state);
            break;
          case DockStep.MovingToDock:
          case DockStep.MovingToShelf:
            // Movement handled by EmployeeBehaviour
            break;
        }
      }

      private async Task HandleFindDock(Packager packager, StateData state)
      {
        var requests = new List<TransferRequest>();
        int maxRoutes = Math.Min(5, packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        ITransitEntity source = state.TryGetValue<ITransitEntity>("Source", out var s) ? s : null;

        if (source != null)
        {
          requests = await FindRoutesFromDock(packager, state, source as LoadingDock, maxRoutes);
          if (requests.Count > 0)
          {
            state.SetValue("Requests", requests);
            state.SetValue("DockStep", DockStep.MovingToDock);
            StartDockMovement(packager, state, requests, DockStep.FindShelf);
            return;
          }
          state.RemoveValue<ITransitEntity>("Source");
        }

        foreach (var dock in packager.AssignedProperty.LoadingDocks ?? Enumerable.Empty<LoadingDock>())
        {
          if (!dock.IsInUse)
            continue;
          requests = await FindRoutesFromDock(packager, state, dock, maxRoutes);
          if (requests.Count > 0)
          {
            state.SetValue("Source", dock);
            state.SetValue("Requests", requests);
            state.SetValue("DockStep", DockStep.MovingToDock);
            StartDockMovement(packager, state, requests, DockStep.FindShelf);
            return;
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmptyLoadingDockTask.FindDock: No valid routes for {packager.fullName}", DebugLogger.Category.Packager);
        ResetTask(state);
      }

      private async Task<List<TransferRequest>> FindRoutesFromDock(Packager packager, StateData state, LoadingDock dock, int maxRoutes)
      {
        var requests = new List<TransferRequest>();
        foreach (var slot in dock.OutputSlots)
        {
          if (maxRoutes <= 0)
            break;
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked)
            continue;

          var item = slot.ItemInstance;
          if (IsItemTimedOut(packager.AssignedProperty, item))
            continue;

          var destination = await FindShelfForDeliveryAsync(packager, item);
          if (destination == null)
            continue;

          var deliverySlots = (destination as ITransitEntity).ReserveInputSlotsForItem(item, packager.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
            continue;

          int quantity = Math.Min(slot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(item)));
          if (quantity <= 0)
            continue;

          if (!packager.Movement.CanGetTo(NavMeshUtility.GetAccessPoint(dock, packager).position))
            continue;

          var inventorySlot = packager.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
            continue;

          var request = new TransferRequest(packager, item, quantity, inventorySlot, dock, new List<ItemSlot> { slot }, destination, deliverySlots);
          requests.Add(request);
          maxRoutes--;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmptyLoadingDockTask.FindRoutesFromDock: Added route for {quantity} of {item.ID} to shelf {destination.GUID}", DebugLogger.Category.Packager);
        }
        return requests;
      }

      private void HandleFindShelf(Packager packager, StateData state)
      {
        if (!state.TryGetValue<List<TransferRequest>>("Requests", out var requests) || requests.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmptyLoadingDockTask.FindShelf: No requests for {packager.fullName}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var request = requests[0];
        state.SetValue("CurrentRequest", request);
        state.SetValue("Requests", requests.Skip(1).ToList());
        state.SetValue("DockStep", DockStep.MovingToShelf);
        StartDockMovement(packager, state, new List<TransferRequest> { request }, DockStep.FindDock);
      }

      private void StartDockMovement(Packager packager, StateData state, List<TransferRequest> requests, DockStep nextStep)
      {
        state.EmployeeBeh.StartMovement(CreatePrioritizedRoutes(requests, Priority), async (emp, s) =>
        {
          s.SetValue("RefillStep", nextStep);
          await Execute(emp, s);
        });
      }

      private void ResetTask(StateData state)
      {
        state.CurrentState = EState.Idle;
        state.RemoveValue<DockStep>("DockStep");
        state.RemoveValue<ITransitEntity>("Source");
        state.RemoveValue<List<TransferRequest>>("Requests");
        state.RemoveValue<TransferRequest>("CurrentRequest");
        DebugLogger.Log(DebugLogger.LogLevel.Info, "EmptyLoadingDockTask.ResetTask: Task reset", DebugLogger.Category.Packager);
      }
    }
    public class RestockSpecificShelfTask : IEmployeeTask
    {
      private readonly int _priority;
      private readonly int _scanIndex;
      public int Priority => _priority;
      public int ScanIndex => _scanIndex;

      public RestockSpecificShelfTask(int priority, int scanIndex)
      {
        _priority = priority;
        _scanIndex = scanIndex;
      }

      private enum RestockStep
      {
        FindAnyShelf,
        FindSpecificShelf,
        MovingToAnyShelf,
        MovingToSpecificShelf
      }

      public async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "RestockSpecificShelfTask.CanExecute: Invalid packager or state", DebugLogger.Category.Packager);
          return false;
        }

        foreach (var shelf in StorageExtensions.AnyShelves)
        {
          foreach (var slot in shelf.OutputSlots)
          {
            if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked)
              continue;

            var item = slot.ItemInstance;
            if (IsItemTimedOut(packager.AssignedProperty, item))
              continue;

            var specificShelf = await FindShelfForDeliveryAsync(packager, item, allowAnyShelves: false);
            if (specificShelf != null && specificShelf != shelf)
              return true;
          }
        }
        return false;
      }

      public async Task Execute(Employee employee, StateData state)
      {
        if (!(employee is Packager packager) || state == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestockSpecificShelfTask.Execute: Invalid packager or state", DebugLogger.Category.Packager);
          state.CurrentState = EState.Idle;
          return;
        }

        if (!state.TryGetValue<RestockStep>("RestockStep", out var currentStep))
        {
          currentStep = RestockStep.FindAnyShelf;
          state.SetValue("RestockStep", currentStep);
        }

        switch (currentStep)
        {
          case RestockStep.FindAnyShelf:
            await HandleFindAnyShelf(packager, state);
            break;
          case RestockStep.FindSpecificShelf:
            HandleFindSpecificShelf(packager, state);
            break;
          case RestockStep.MovingToAnyShelf:
          case RestockStep.MovingToSpecificShelf:
            // Movement handled by EmployeeBehaviour
            break;
        }
      }

      private async Task HandleFindAnyShelf(Packager packager, StateData state)
      {
        var requests = new List<TransferRequest>();
        int maxRoutes = Math.Min(5, packager.Inventory.ItemSlots.Count(s => s.ItemInstance == null));
        ITransitEntity source = state.TryGetValue<ITransitEntity>("Source", out var s) ? s : null;
        var processedShelves = new List<Guid>();

        if (source != null)
        {
          requests = await FindRoutesFromShelf(packager, state, source, maxRoutes, processedShelves);
          if (requests.Count > 0)
          {
            state.SetValue("Requests", requests);
            state.SetValue("RestockStep", RestockStep.MovingToAnyShelf);
            StartRestockMovement(packager, state, requests, source, RestockStep.FindSpecificShelf);
            return;
          }
          state.RemoveValue<ITransitEntity>("Source");
        }

        foreach (var shelf in StorageExtensions.AnyShelves)
        {
          if (processedShelves.Contains(shelf.GUID))
            continue;
          processedShelves.Add(shelf.GUID);
          requests = await FindRoutesFromShelf(packager, state, shelf, maxRoutes, processedShelves);
          if (requests.Count > 0)
          {
            state.SetValue("Source", shelf);
            state.SetValue("Requests", requests);
            state.SetValue("RestockStep", RestockStep.MovingToAnyShelf);
            StartRestockMovement(packager, state, requests, shelf, RestockStep.FindSpecificShelf);
            return;
          }
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestockSpecificShelfTask.FindAnyShelf: No valid routes for {packager.fullName}", DebugLogger.Category.Packager);
        ResetTask(state);
      }

      private async Task<List<TransferRequest>> FindRoutesFromShelf(Packager packager, StateData state, ITransitEntity source, int maxRoutes, List<Guid> processedShelves)
      {
        var requests = new List<TransferRequest>();
        foreach (var slot in source.OutputSlots)
        {
          if (maxRoutes <= 0)
            break;
          if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.IsLocked)
            continue;

          var item = slot.ItemInstance;
          if (IsItemTimedOut(packager.AssignedProperty, item))
            continue;

          if (NoDropOffCache.TryGetValue(packager.AssignedProperty, out var cache) &&
              cache.Any(i => i.CanStackWith(item, false)))
            continue;

          var destination = await FindShelfForDeliveryAsync(packager, item, allowAnyShelves: false);
          if (destination == null || (destination as ITransitEntity) == source)
            continue;

          var deliverySlots = (destination as ITransitEntity).ReserveInputSlotsForItem(item, packager.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
            continue;

          int quantity = Math.Min(slot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(item)));
          if (quantity <= 0)
            continue;

          var inventorySlot = packager.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
          if (inventorySlot == null)
            continue;

          var request = new TransferRequest(packager, item, quantity, inventorySlot, source, new List<ItemSlot> { slot }, destination, deliverySlots);
          requests.Add(request);
          maxRoutes--;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RestockSpecificShelfTask.FindRoutesFromShelf: Added route for {quantity} of {item.ID} to shelf {destination.GUID}", DebugLogger.Category.Packager);
        }
        return requests;
      }

      private void HandleFindSpecificShelf(Packager packager, StateData state)
      {
        if (!state.TryGetValue<List<TransferRequest>>("Requests", out var requests) || requests.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestockSpecificShelfTask.FindSpecificShelf: No requests for {packager.fullName}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var request = requests[0];
        state.SetValue("CurrentRequest", request);
        state.SetValue("Requests", requests.Skip(1).ToList());
        state.SetValue("RestockStep", RestockStep.MovingToSpecificShelf);
        StartRestockMovement(packager, state, new List<TransferRequest> { request }, request.DropOff, RestockStep.FindAnyShelf);
      }

      private void StartRestockMovement(Packager packager, StateData state, List<TransferRequest> requests, ITransitEntity destination, RestockStep nextStep)
      {
        state.EmployeeBeh.StartMovement(CreatePrioritizedRoutes(requests, Priority), async (emp, s) =>
        {
          s.SetValue("RestockStep", nextStep);
          await Execute(emp, s);
        });
      }

      private void ResetTask(StateData state)
      {
        state.CurrentState = EState.Idle;
        state.RemoveValue<RestockStep>("RestockStep");
        state.RemoveValue<ITransitEntity>("Source");
        state.RemoveValue<List<TransferRequest>>("Requests");
        state.RemoveValue<TransferRequest>("CurrentRequest");
        DebugLogger.Log(DebugLogger.LogLevel.Info, "RestockSpecificShelfTask.ResetTask: Task reset", DebugLogger.Category.Packager);
      }
    }

    [HarmonyPatch(typeof(Packager))]
    public static class PackagerPatch
    {
      [HarmonyPrefix]
      [HarmonyPatch("UpdateBehaviour")]
      public static bool UpdateBehaviourPrefix(Packager __instance)
      {
        try
        {
          if (__instance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, "UpdateBehaviourPrefix: Packager instance is null", DebugLogger.Category.Packager);
            return false;
          }

          if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employeeAdapter))
          {
            employeeAdapter = new PackagerAdapter(__instance);
            EmployeeAdapters[__instance.GUID] = employeeAdapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Registered PackagerAdapter for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          }
          var state = GetState(__instance);

          if (__instance.Fired || (__instance.behaviour.activeBehaviour != null && __instance.behaviour.activeBehaviour != __instance.WaitOutside))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Fired={__instance.Fired} or activeBehaviour={__instance.behaviour.activeBehaviour?.Name ?? "null"} for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            return false;
          }

          bool noWork = false;
          bool needsPay = false;
          if (__instance.GetBed() == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: No bed assigned for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            noWork = true;
            __instance.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
          }
          else if (NetworkSingleton<ScheduleOne.GameTime.TimeManager>.Instance.IsEndOfDay)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: End of day for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            noWork = true;
            __instance.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
          }
          else if (!__instance.PaidForToday)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Not paid for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            if (__instance.IsPayAvailable())
            {
              needsPay = true;
            }
            else
            {
              noWork = true;
              __instance.SubmitNoWorkReason("I haven't been paid yet", "You can place cash in my briefcase on my bed.");
            }
          }

          if (noWork)
          {
            __instance.SetWaitOutside(true);
            state.CurrentState = EState.Idle;
            return false;
          }

          if (InstanceFinder.IsServer && needsPay && __instance.IsPayAvailable())
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Processing payment for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            __instance.RemoveDailyWage();
            __instance.SetIsPaid();
          }

          if (!InstanceFinder.IsServer)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Client-side, skipping for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            return false;
          }

          if (!__instance.CanWork())
          {
            __instance.SubmitNoWorkReason("I am unable to work right now", "Check my status to see why I can't work.");
            __instance.SetIdle(true);
            state.CurrentState = EState.Idle;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"UpdateBehaviourPrefix: Cannot work for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            return false;
          }

          state.EmployeeBeh.Update();
          if (state.CurrentState == EState.Working)
          {
            __instance.MarkIsWorking();
          }
          else
          {
            PackagingStation stationToAttend = __instance.GetStationToAttend();
            if (stationToAttend != null && StationAdapters.TryGetValue(stationToAttend.GUID, out var stationAdapter))
            {
              state.Station = stationAdapter;
              state.CurrentState = EState.Idle; // Trigger task check
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"UpdateBehaviourPrefix: Set station {stationToAttend.GUID} for NPC={__instance.fullName}", DebugLogger.Category.Packager);
            }
            else
            {
              __instance.SetIdle(true);
            }
          }

          return false;
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"UpdateBehaviourPrefix: Failed for packager {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Packager);
          if (__instance != null)
            __instance.SetIdle(true);
          return false;
        }
      }

      [HarmonyPostfix]
      [HarmonyPatch("Fire")]
      public static void FirePostfix(Packager __instance)
      {
        try
        {
          GetState(__instance).EmployeeBeh.Disable();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerFirePatch: Disabled PackagerBehaviour for NPC={__instance.fullName}", DebugLogger.Category.Packager);

          if (EmployeeAdapters.ContainsKey(__instance.GUID))
          {
            EmployeeAdapters.Remove(__instance.GUID);
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerFirePatch: Removed PackagerAdapter for NPC={__instance.fullName}", DebugLogger.Category.Packager);
          }
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerFirePatch: Failed for Packager {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Packager);
        }
      }

      [HarmonyPrefix]
      [HarmonyPatch("GetStationToAttend")]
      public static bool GetStationToAttendPrefix(Packager __instance, ref PackagingStation __result)
      {
        try
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationToAttendPrefix: Checking stations for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          if (__instance.AssignedProperty == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationToAttendPrefix: AssignedProperty is null for NPC {__instance.fullName}", DebugLogger.Category.Packager);
            __result = null;
            return false;
          }

          if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var adapter))
          {
            adapter = new PackagerAdapter(__instance);
            EmployeeAdapters[__instance.GUID] = adapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationToAttendPrefix: Registered adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          }

          foreach (PackagingStation station in __instance.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
          {
            if (station == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetStationToAttendPrefix: Null station in AssignedStations", DebugLogger.Category.Packager);
              continue;
            }

            if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
            {
              stationAdapter = new PackagingStationAdapter(station);
              StationAdapters[station.GUID] = stationAdapter;
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationToAttendPrefix: Created station adapter for station {station.GUID}", DebugLogger.Category.Packager);
            }

            if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetStationToAttendPrefix: Station {station.GUID} in use or active, skipping", DebugLogger.Category.Packager);
              continue;
            }

            bool hasProducts = stationAdapter.ProductSlots.Any(s => s.ItemInstance != null && s.Quantity > 0 &&
                s.ItemInstance.ID != Stations.PackagingStationBeh.PackagingStation_Work.JAR_ITEM_ID && s.ItemInstance.ID != Stations.PackagingStationBeh.PackagingStation_Work.BAGGIE_ITEM_ID);
            if (!hasProducts)
              continue;

            bool hasPackaging = stationAdapter.InsertSlot != null && stationAdapter.InsertSlot.Quantity > 0 &&
                (stationAdapter.InsertSlot.ItemInstance.ID == Stations.PackagingStationBeh.PackagingStation_Work.JAR_ITEM_ID ||
                 stationAdapter.InsertSlot.ItemInstance.ID == Stations.PackagingStationBeh.PackagingStation_Work.BAGGIE_ITEM_ID);
            if (hasPackaging)
            {
              __result = station;
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationToAttendPrefix: Selected station {station.GUID} for NPC {__instance.fullName}", DebugLogger.Category.Packager);
              return false;
            }
          }

          __result = null;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetStationToAttendPrefix: No station ready for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          return false;
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetStationToAttendPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
      }

      /*       [HarmonyPrefix]
            [HarmonyPatch("GetStationMoveItems")]
            public static async Task<bool> GetStationMoveItemsPrefix(Packager __instance, PackagingStation __result)
            {
              try
              {
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetStationMoveItemsPrefix: Checking stations for NPC {__instance.fullName}", DebugLogger.Category.Packager);
                if (__instance.AssignedProperty == null)
                {
                  DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetStationMoveItemsPrefix: AssignedProperty is null for NPC {__instance.fullName}", DebugLogger.Category.Packager);
                  __result = null;
                  return false;
                }

                if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var adapter))
                {
                  adapter = new PackagerAdapter(__instance);
                  EmployeeAdapters[__instance.GUID] = adapter;
                  DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationMoveItemsPrefix: Registered adapter for NPC {__instance.fullName}", DebugLogger.Category.Packager);
                }

                var state = GetState(__instance);

                foreach (PackagingStation station in __instance.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
                {
                  if (station == null)
                    continue;

                  if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
                  {
                    stationAdapter = new PackagingStationAdapter(station);
                    StationAdapters[station.GUID] = stationAdapter;
                  }

                  if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
                    continue;

                  var items = stationAdapter.RefillList();
                  foreach (var item in items)
                  {
                    if (IsItemTimedOut(__instance.AssignedProperty, item))
                      continue;

                    var shelf = await FindShelfWithItemAsync(__instance, item, stationAdapter.StartThreshold);
                    if (shelf.Key == null)
                      continue;
                    state.SetValue("source", shelf);
                    state.Station = stationAdapter;
                    __result = station;
                    DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStationMoveItemsPrefix: Set station {station.GUID} for item {item.ID}", DebugLogger.Category.Packager);
                    return false;
                  }
                }

                __result = null;
                DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetStationMoveItemsPrefix: No station with items to move for NPC {__instance.fullName}", DebugLogger.Category.Packager);
                return false;
              }
              catch (Exception e)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetStationMoveItemsPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
                __result = null;
                return false;
              }
            }
       */
      [HarmonyPrefix]
      [HarmonyPatch("GetTransitRouteReady")]
      public static bool GetTransitRouteReadyPrefix(Packager __instance, ref AdvancedTransitRoute __result, out ItemInstance item)
      {
        item = null;
        try
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagerPatch.GetTransitRouteReadyPrefix: Checking routes for NPC {__instance.fullName}", DebugLogger.Category.Packager);
          if (__instance.AssignedProperty == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagerPatch.GetTransitRouteReadyPrefix: AssignedProperty is null for NFC {__instance.fullName}", DebugLogger.Category.Packager);
            __result = null;
            return false;
          }

          var state = GetState(__instance);

          if (state.CurrentState == EState.Idle)
          {
            state.EmployeeBeh?.Update();
          }

          __result = null;
          return false;
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagerPatch.GetTransitRouteReadyPrefix: Failed for NPC {__instance.fullName}, error: {e}", DebugLogger.Category.Packager);
          __result = null;
          return false;
        }
      }
    }
  }
}