using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.General.GeneralExtensions;
using static NoLazyWorkers.Chemists.ChemistUtilities;
using static NoLazyWorkers.General.StorageUtilities;

using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;

namespace NoLazyWorkers.Chemists
{
  public static class ChemistExtensions
  {

  }

  // TODO implement slot locking
  public static class ChemistUtilities
  {
    public static bool HasSufficientItems(NPC npc, ItemInstance targetItem, int needed)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HasSufficientItems: Entered for {npc?.fullName}, targetItem={targetItem?.ID}, needed={needed}",
          DebugLogger.Category.Chemist);

      if (targetItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HasSufficientItems: Target item is null for {npc?.fullName}",
            DebugLogger.Category.Chemist);
        return false;
      }
      var shelves = FindShelvesWithItem(npc, targetItem, needed);
      if (shelves == null || shelves.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HasSufficientItems: No shelves found for {targetItem.ID}, needed={needed}, hasSufficient=false for {npc?.fullName}",
            DebugLogger.Category.Chemist);
        return false;
      }
      int shelfQty = shelves.Values.Sum();
      bool hasSufficient = shelfQty >= needed;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"HasSufficientItems: Completed for {targetItem.ID}, shelfQty={shelfQty}, needed={needed}, hasSufficient={hasSufficient} for {npc?.fullName}",
          DebugLogger.Category.Chemist);
      return hasSufficient;
    }
  }

  public abstract class ChemistBehaviour
  {
    public static readonly Dictionary<Chemist, EntityConfiguration> cachedConfigs = new();
    public static readonly Dictionary<Behaviour, StateData> states = new();
    public delegate void StateHandler(Behaviour behaviour, StateData state);
    public enum EState
    {
      Idle,
      Grabbing,
      Inserting,
      StartingOperation,
      Completed
    }

    public readonly Dictionary<EState, StateHandler> StateHandlers;

    public ChemistBehaviour()
    {
      StateHandlers = new Dictionary<EState, StateHandler>
            {
                { EState.Idle, HandleIdle },
                { EState.Grabbing, HandleGrabbing },
                { EState.Inserting, HandleInserting },
                { EState.StartingOperation, HandleStartingOperation },
                { EState.Completed, HandleCompleted }
            };
    }

    public class StateData
    {
      public EState CurrentState { get; set; } = EState.Idle;
      public ItemInstance TargetItem { get; set; }
      public int QuantityInventory { get; set; }
      public int QuantityNeeded { get; set; }
      public int QuantityWanted { get; set; }
      public Coroutine WalkToSuppliesRoutine { get; set; }
      public Coroutine GrabRoutine { get; set; }
      public Coroutine InsertRoutine { get; set; }
      public ITransitEntity LastSupply { get; set; }
      public bool OperationPending { get; set; }
      public ITransitEntity Destination { get; set; }
      public float MoveTimeout { get; set; }
      public float MoveElapsed { get; set; }
      public bool IsMoving { get; set; }
      public string ExpectedInputItemID { get; set; }
      public Dictionary<PlaceableStorageEntity, int> Fetching { get; set; } = new();
      public object CachedConfig { get; set; }
      public int MovementStartFrames { get; set; }
      public bool FailedToFetch { get; set; }
    }

    protected void TransitionState(Behaviour behaviour, StateData state, EState newState, string reason)
    {
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (station == null || chemist == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"TransitionState: Invalid station or chemist for {chemist?.fullName}, station={station?.GUID}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"TransitionState: {chemist?.fullName} from {state.CurrentState} to {newState}, reason={reason}, invQty={state.QuantityInventory}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}, fetchingCount={state.Fetching.Count}, station={station?.GUID}",
          DebugLogger.Category.Chemist);
      state.CurrentState = newState;
    }

    public virtual bool ValidateState(Chemist chemist, Behaviour behaviour, StateData state, out bool canStart, out bool canRestock)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ValidateState: Entered for {chemist?.fullName}, state={state.CurrentState}",
          DebugLogger.Category.Chemist);
      canStart = false;
      canRestock = false;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (station == null || chemist == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ValidateState: Invalid station or chemist for {chemist?.fullName}, station={station?.GUID}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      if (station.IsInUse || station.HasActiveOperation || station.OutputSlot.Quantity > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ValidateState: Station in use, active, or has output for {chemist?.fullName}, station={station.GUID}",
            DebugLogger.Category.Chemist);
        return false;
      }
      ItemField inputItem = station.GetInputItemForProduct()[0];
      if (inputItem?.SelectedItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ValidateState: Input item null for station {station.GUID}, chemist={chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      ItemInstance targetItem = inputItem.SelectedItem.GetDefaultInstance();
      if (targetItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ValidateState: Target item null for station {station.GUID}, chemist={chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      state.TargetItem = targetItem;
      int threshold = station.StartThreshold;
      int desiredQty = Math.Min(station.MaxProductQuantity, station.ProductSlots.Sum(s => s.Quantity));
      int invQty = chemist.Inventory._GetItemAmount(targetItem.ID);
      int inputQty = station.GetInputQuantity();
      state.QuantityInventory = invQty;
      state.QuantityNeeded = Math.Max(0, threshold - inputQty);
      state.QuantityWanted = Math.Max(0, desiredQty - inputQty);
      canStart = inputQty >= threshold && desiredQty >= threshold;
      canRestock = invQty > 0 || HasSufficientItems(chemist, targetItem, state.QuantityNeeded - invQty);
      if (inputQty < threshold && !canRestock)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ValidateState: Below threshold and cannot restock for {chemist?.fullName}, inputQty={inputQty}, threshold={threshold}",
            DebugLogger.Category.Chemist);
        Disable(behaviour);
      }
      if (canRestock)
      {
        var shelves = FindShelvesWithItem(chemist, targetItem, state.QuantityNeeded - invQty, state.QuantityWanted - invQty);
        if (shelves != null && shelves.Count > 0)
        {
          state.Fetching.Clear();
          foreach (var shelf in shelves)
          {
            state.Fetching[shelf.Key] = Math.Min(shelf.Value, state.QuantityWanted - invQty);
          }
        }
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ValidateState: Completed for {chemist?.fullName}, canStart={canStart}, canRestock={canRestock}, invQty={invQty}, inputQty={inputQty}, desiredQty={desiredQty}, threshold={threshold}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}, fetchingCount={state.Fetching.Count}",
          DebugLogger.Category.Chemist);
      return true;
    }

    public virtual bool ValidateFetchState(Chemist chemist, Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ValidateFetchState: Entered for {chemist?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}, qtyWanted={state.QuantityWanted}",
          DebugLogger.Category.Chemist);
      if (state.QuantityInventory >= state.QuantityWanted || state.Fetching.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ValidateFetchState: No fetching needed for {chemist?.fullName}, invQty={state.QuantityInventory}, qtyWanted={state.QuantityWanted}, fetchingCount={state.Fetching.Count}",
            DebugLogger.Category.Chemist);
        return false;
      }
      PlaceableStorageEntity shelf = state.Fetching.First().Key;
      int available = GetItemQuantityInShelf(shelf, state.TargetItem);
      bool isValid = available >= Math.Min(state.QuantityWanted - state.QuantityInventory, state.Fetching[shelf]);
      if (!isValid)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ValidateFetchState: Insufficient items in shelf {shelf.GUID}, available={available}, needed={state.QuantityWanted - state.QuantityInventory} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ValidateFetchState: Completed for {chemist?.fullName}, isValid={isValid}, shelfAvailable={available}, qtyWanted={state.QuantityWanted}, invQty={state.QuantityInventory}, fetchingCount={state.Fetching.Count}",
          DebugLogger.Category.Chemist);
      return isValid;
    }

    public virtual bool ValidateInsertState(Chemist chemist, Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ValidateInsertState: Entered for {chemist?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}",
          DebugLogger.Category.Chemist);
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ValidateInsertState: Station is null for {chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      int invQty = chemist.Inventory._GetItemAmount(state.TargetItem?.ID);
      state.QuantityInventory = invQty;
      bool isValid = invQty > 0 && (station.InsertSlot.ItemInstance == null || station.InsertSlot.ItemInstance.ID == state.TargetItem.ID);
      if (!isValid)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ValidateInsertState: Invalid conditions for {chemist?.fullName}, invQty={invQty}, slotItem={station.InsertSlot.ItemInstance?.ID ?? "null"}",
            DebugLogger.Category.Chemist);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ValidateInsertState: Completed for {chemist?.fullName}, isValid={isValid}, invQty={invQty}, slotItem={station.InsertSlot.ItemInstance?.ID ?? "null"}",
          DebugLogger.Category.Chemist);
      return isValid;
    }

    public virtual bool ValidateOperationState(Chemist chemist, Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ValidateOperationState: Entered for {chemist?.fullName}, state={state.CurrentState}, operationPending={state.OperationPending}",
          DebugLogger.Category.Chemist);
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (station == null || state.OperationPending || station.HasActiveOperation || station.IsInUse)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ValidateOperationState: Invalid station, pending, or active operation for {chemist?.fullName}, station={station?.GUID}, pending={state.OperationPending}, active={station?.HasActiveOperation}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      if (!IsAtStation(behaviour))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ValidateOperationState: Not at station for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        return false;
      }
      int inputQty = station.GetInputQuantity();
      int productQty = station.ProductSlots.Sum(s => s.Quantity);
      int threshold = station.StartThreshold;
      bool isValid = inputQty >= threshold && productQty >= threshold;

      if (!isValid)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ValidateOperationState: Insufficient input for {chemist?.fullName}, inputQty={inputQty}, threshold={threshold}",
            DebugLogger.Category.Chemist);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ValidateOperationState: Completed for {chemist?.fullName}, isValid={isValid}, inputQty={inputQty}, threshold={threshold}, activeOperation={station.HasActiveOperation}",
          DebugLogger.Category.Chemist);
      return isValid;
    }

    public virtual bool ValidateCompletedState(Chemist chemist, Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ValidateCompletedState: Entered for {chemist?.fullName}, state={state.CurrentState}, operationPending={state.OperationPending}",
          DebugLogger.Category.Chemist);
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ValidateCompletedState: Station is null for {chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        Disable(behaviour);
      }
      bool isValid = IsAtStation(behaviour) && station.OutputSlot.Quantity > 0;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ValidateCompletedState: Completed for {chemist?.fullName}, isValid={isValid}, pending={state.OperationPending}, activeOperation={station.HasActiveOperation}, outputQty={station.OutputSlot.Quantity}",
          DebugLogger.Category.Chemist);
      return isValid;
    }

    protected virtual void HandleIdle(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HandleIdle: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (!ValidateState(chemist, behaviour, state, out bool canStart, out bool canRestock))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleIdle: State validation failed for {chemist?.fullName}, disabling behaviour",
            DebugLogger.Category.Chemist);
        Disable(behaviour);
        return;
      }
      if (canRestock)
      {
        // TODO duplicate HasSufficient check from canrestock
        if (state.QuantityInventory > 0 && (state.QuantityInventory >= state.QuantityWanted || !HasSufficientItems(chemist, state.TargetItem, state.QuantityWanted - state.QuantityInventory)))
        {
          TransitionState(behaviour, state, EState.Inserting, "Have enough items to insert");
          if (!IsAtStation(behaviour))
            WalkToDestination(behaviour, state, (ITransitEntity)station);
          else
            HandleInserting(behaviour, state);
        }
        else
        {
          TransitionState(behaviour, state, EState.Grabbing, "Need to fetch items");
          PrepareToFetchItems(behaviour, state);
        }
      }
      else if (canStart)
      {
        TransitionState(behaviour, state, EState.StartingOperation, "Can start operation");
        if (!IsAtStation(behaviour))
          WalkToDestination(behaviour, state, (ITransitEntity)station);
        else
          HandleStartingOperation(behaviour, state);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleIdle: No actions possible for {chemist?.fullName}, disabling behaviour",
            DebugLogger.Category.Chemist);
        Disable(behaviour);
      }
    }

    protected virtual void HandleGrabbing(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HandleGrabbing: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}, qtyWanted={state.QuantityWanted}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      var curInv = chemist.Inventory._GetItemAmount(state.TargetItem?.ID);
      if (curInv < state.QuantityInventory)
      {
        state.QuantityInventory = curInv;
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleGrabbing: Inventory loss detected for {chemist?.fullName}, expected={state.QuantityInventory}, actual={chemist.Inventory._GetItemAmount(state.TargetItem?.ID)}, returning to Idle",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Inventory loss detected");
        return;
      }
      state.QuantityInventory = curInv;
      if (state.QuantityInventory >= state.QuantityWanted)
      {
        TransitionState(behaviour, state, EState.Inserting, "Sufficient inventory");
        IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
        if (!IsAtStation(behaviour))
          WalkToDestination(behaviour, state, (ITransitEntity)station);
        else
          HandleInserting(behaviour, state);
        return;
      }
      if (!ValidateFetchState(chemist, behaviour, state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleGrabbing: Fetch state invalid for {chemist?.fullName}, returning to Idle",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Invalid fetch state");
        return;
      }
      GrabItem(behaviour, state);
    }

    protected virtual void HandleInserting(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HandleInserting: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      var curInv = chemist.Inventory._GetItemAmount(state.TargetItem?.ID); //TODO replace with onchanged listener cache
      if (curInv < state.QuantityInventory)
      {
        state.QuantityInventory = curInv;
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleInserting: Inventory loss detected for {chemist?.fullName}, expected={state.QuantityInventory}, actual={chemist.Inventory._GetItemAmount(state.TargetItem?.ID)}, returning to Idle",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Inventory loss detected");
        return;
      }
      state.QuantityInventory = curInv;
      if (!IsAtStation(behaviour))
      {
        WalkToDestination(behaviour, state, (ITransitEntity)station);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleInserting: Moving to station for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        return;
      }
      if (!ValidateInsertState(chemist, behaviour, state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleInserting: Insert state invalid for {chemist?.fullName}, atStation={IsAtStation(behaviour)}, returning to Idle",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Insert state invalid");
        return;
      }

      if (!InsertItemsFromInventory(behaviour, state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleInserting: Failed to insert {state.QuantityInventory} for {chemist?.fullName}, returning to Idle",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Failed to insert items");
        return;
      }

      if (ValidateOperationState(chemist, behaviour, state))
      {
        TransitionState(behaviour, state, EState.StartingOperation, $"Can start operation after inserting {state.QuantityInventory} items");
        HandleStartingOperation(behaviour, state);
      }
      else
      {
        TransitionState(behaviour, state, EState.Idle, $"Inserted {curInv - state.QuantityInventory} items, but failed validate operation state");
      }
    }

    protected virtual void HandleStartingOperation(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HandleStartingOperation: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, operationPending={state.OperationPending}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);

      if (!IsAtStation(behaviour))
      {
        WalkToDestination(behaviour, state, (ITransitEntity)station);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleStartingOperation: Moving to station for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        return;
      }
      if (!ValidateOperationState(chemist, behaviour, state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"HandleStartingOperation: Operation state invalid for {chemist?.fullName}, returning to Idle",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Invalid operation state");
        state.OperationPending = false;
        return;
      }
      state.OperationPending = true;
      station.StartOperation(behaviour);
      TransitionState(behaviour, state, EState.Completed, "Operation started");
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"HandleStartingOperation: Operation started for {chemist?.fullName}",
          DebugLogger.Category.Chemist);
    }

    protected virtual void HandleCompleted(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"HandleCompleted: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, operationPending={state.OperationPending}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (state.OperationPending)
      {
        if (ValidateCompletedState(chemist, behaviour, state))
        {
          state.OperationPending = false;
          DebugLogger.Log(DebugLogger.LogLevel.Warning,
              $"HandleCompleted: Operation complete but validation failed for {chemist?.fullName}, returning to Idle",
              DebugLogger.Category.Chemist);
          TransitionState(behaviour, state, EState.Idle, "Invalid completed state");
          return;
        }
        if (!station.HasActiveOperation)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"HandleCompleted: Operation pending but no active operation for {chemist?.fullName}, waiting",
              DebugLogger.Category.Chemist);
          return;
        }
        state.OperationPending = false;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleCompleted: Operation started, clearing pending for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
      else if (!station.HasActiveOperation)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"HandleCompleted: Operation complete, disabling for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        Disable(behaviour);
      }
    }

    protected virtual void WalkToDestination(Behaviour behaviour, StateData state, ITransitEntity destination)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"WalkToDestination: Entered for {behaviour.Npc?.fullName}, destination={destination?.Name}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      if (destination == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"WalkToDestination: Destination null for {chemist?.fullName}, disabling behaviour",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        Disable(behaviour);
        return;
      }
      if (!chemist.Movement.CanGetTo(destination, 1f))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"WalkToDestination: Cannot reach {destination.Name} for {chemist?.fullName}, disabling behaviour",
            DebugLogger.Category.Chemist);
        Disable(behaviour); //TODO how to exclude blocked shelves?
        return;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"WalkToDestination: Initiating movement to {destination.Name} for {chemist?.fullName}",
          DebugLogger.Category.Chemist);
      Vector3 initialPosition = chemist.transform.position;
      state.MovementStartFrames = 0;
      behaviour.SetDestination(destination, true);
      state.Destination = destination;
      state.MoveTimeout = 5f;
      state.MoveElapsed = 0f;
      state.IsMoving = true;
      if (state.WalkToSuppliesRoutine != null)
        MelonCoroutines.Stop(state.WalkToSuppliesRoutine);
      state.WalkToSuppliesRoutine = (Coroutine)MelonCoroutines.Start(MonitorMovementStart(chemist, initialPosition, state, behaviour));
    }

    private IEnumerator MonitorMovementStart(Chemist chemist, Vector3 initialPosition, StateData state, Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"MonitorMovementStart: Entered for {chemist?.fullName}, initialPos={initialPosition}",
          DebugLogger.Category.Chemist);
      int maxFrames = 20;
      while (state.MovementStartFrames < maxFrames)
      {
        state.MovementStartFrames++;
        if (chemist.Movement.IsMoving || Vector3.Distance(chemist.transform.position, initialPosition) > 0.01f)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MonitorMovementStart: Movement started for {chemist?.fullName}",
              DebugLogger.Category.Chemist);
          state.WalkToSuppliesRoutine = null;
          yield break;
        }
        yield return new WaitForSeconds(0.2f);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Warning,
          $"MonitorMovementStart: Movement failed to start after {maxFrames} frames for {chemist?.fullName}, returning to Idle",
          DebugLogger.Category.Chemist);
      state.IsMoving = false;
      state.MovementStartFrames = 0;
      TransitionState(behaviour, state, EState.Idle, "Movement failed to start");
    }

    public virtual void UpdateMovement(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"UpdateMovement: Entered for {behaviour.Npc?.fullName}, isMoving={state.IsMoving}, destination={state.Destination?.Name}",
          DebugLogger.Category.Chemist);
      if (!state.IsMoving) return;
      Chemist chemist = behaviour.Npc as Chemist;
      state.MoveElapsed += Time.deltaTime;
      if (state.MoveElapsed >= state.MoveTimeout)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"UpdateMovement: Timeout moving to {state.Destination?.Name} for {chemist?.fullName}, returning to Idle",
            DebugLogger.Category.Chemist);
        state.IsMoving = false;
        state.MovementStartFrames = 0;
        TransitionState(behaviour, state, EState.Idle, "Movement timeout");
        return;
      }
      if (!chemist.Movement.IsMoving)
      {
        state.IsMoving = false;
        state.MovementStartFrames = 0;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"UpdateMovement: Movement complete to {state.Destination?.Name} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        if (state.Destination == state.LastSupply)
          HandleGrabbing(behaviour, state);
        else if (state.Destination == StationHandlerFactory.GetStation(behaviour))
          HandleInserting(behaviour, state);
      }
    }

    public virtual void PrepareToFetchItems(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"PrepareToFetchItems: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      if (state.Fetching.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"PrepareToFetchItems: No shelves available for {chemist?.fullName}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}, disabling behaviour",
            DebugLogger.Category.Chemist);
        state.FailedToFetch = true;
        Disable(behaviour);
        return;
      }
      var shelf = state.Fetching.First().Key;
      ConfigurationExtensions.NPCSupply[chemist.GUID] = new ObjectField(chemist.configuration) { SelectedObject = shelf };
      state.LastSupply = shelf;
      state.FailedToFetch = false;
      if (!IsAtShelf(behaviour, state))
      {
        WalkToDestination(behaviour, state, shelf);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"PrepareToFetchItems: Moving to shelf {shelf.GUID} for {state.TargetItem?.ID}, invQty={state.QuantityInventory}, qtyWanted={state.QuantityWanted}",
            DebugLogger.Category.Chemist);
      }
      else
      {
        HandleGrabbing(behaviour, state);
      }
    }

    public virtual bool InsertItemsFromInventory(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"InsertItemsFromInventory: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (!ValidateInsertState(chemist, behaviour, state) || station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"InsertItemsFromInventory: Invalid state or station for {chemist?.fullName}, returning 0",
            DebugLogger.Category.Chemist);
        return false;
      }
      if (station.InsertSlot.ItemInstance != null && station.InsertSlot.ItemInstance.ID != state.TargetItem.ID)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"InsertItemsFromInventory: Insert slot contains wrong item {station.InsertSlot.ItemInstance.ID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      int toInsert = Math.Min(state.QuantityInventory, state.QuantityWanted);
      int currentQuantity = station.InsertSlot.Quantity;
      station.InsertSlot.InsertItem(state.TargetItem.GetCopy(toInsert));
      int newQuantity = station.InsertSlot.Quantity;
      int inserted = newQuantity - currentQuantity;
      if (inserted > 0)
      {
        RemoveItem(chemist, inserted, state);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"InsertItemsFromInventory: Inserted {inserted} of {state.TargetItem?.ID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"InsertItemsFromInventory: Failed to insert {state.TargetItem?.ID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
      return true;
    }

    public virtual void GrabItem(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GrabItem: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}, qtyWanted={state.QuantityWanted}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);

      if (!IsAtShelf(behaviour, state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GrabItem: Not at supplies for {chemist.fullName}",
            DebugLogger.Category.Chemist);
        WalkToDestination(behaviour, state, state.LastSupply);
        return;
      }
      PlaceableStorageEntity shelf = state.Fetching.First().Key;
      int available = GetItemQuantityInShelf(shelf, state.TargetItem);
      int quantityToFetch = Math.Min(state.Fetching[shelf], state.QuantityWanted - state.QuantityInventory);
      quantityToFetch = Math.Min(quantityToFetch, available);
      if (quantityToFetch <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GrabItem: quantityToFetch <= 0 {shelf.GUID}, available={available}, wanted={quantityToFetch} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "Failed to fetch");
        return;
      }
      int fetched = 0;
      foreach (ItemSlot slot in (shelf.OutputSlots ?? Enumerable.Empty<ItemSlot>()).Concat(shelf.InputSlots ?? Enumerable.Empty<ItemSlot>()))
      {
        if (slot?.ItemInstance == null || slot.Quantity <= 0 || slot.ItemInstance.ID.ToLower() != state.TargetItem.ID.ToLower())
          continue;
        int amountToTake = Math.Min(slot.Quantity, quantityToFetch - fetched);
        if (amountToTake <= 0) continue;
        ItemInstance itemCopy = slot.ItemInstance.GetCopy(amountToTake);
        if (itemCopy == null) continue;
        chemist.Inventory.InsertItem(itemCopy);
        slot.ChangeQuantity(-amountToTake, false);
        fetched += amountToTake;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"GrabItem: Took {amountToTake} of {state.TargetItem.ID} from {shelf.GUID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        if (fetched >= quantityToFetch) break;
      }
      state.QuantityInventory += fetched;
      state.Fetching.Remove(shelf);
      if (fetched == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GrabItem: No items fetched from {shelf.GUID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "No items fetched from");
        return;
      }
      if (state.QuantityInventory >= state.QuantityWanted)
      {
        TransitionState(behaviour, state, EState.Inserting, "Sufficient inventory after grabbing");
        if (!IsAtStation(behaviour))
          WalkToDestination(behaviour, state, (ITransitEntity)station);
        else
          HandleInserting(behaviour, state);
      }
      else if (state.Fetching.Count > 0)
      {
        state.LastSupply = state.Fetching.First().Key;
        TransitionState(behaviour, state, EState.Grabbing, "Need more items, moving to next shelf");
        WalkToDestination(behaviour, state, state.LastSupply);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GrabItem: No more shelves available for {chemist?.fullName}, qtyWanted={state.QuantityWanted}, invQty={state.QuantityInventory}",
            DebugLogger.Category.Chemist);
        TransitionState(behaviour, state, EState.Idle, "No more shelves available for");
        return;
      }
      if (state.GrabRoutine != null)
        MelonCoroutines.Stop(state.GrabRoutine);
      state.GrabRoutine = (Coroutine)MelonCoroutines.Start(GrabRoutine(behaviour, state));
    }

    public abstract IStationAdapter<TStation> GetStation<TStation>(Behaviour behaviour)
        where TStation : class;

    public virtual bool IsAtShelf(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"IsAtShelf: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}",
          DebugLogger.Category.Chemist);
      if (state.Fetching.Count == 0) return false;
      Chemist chemist = behaviour.Npc as Chemist;
      var shelf = state.Fetching.First().Key;
      bool atShelf = NavMeshUtility.IsAtTransitEntity(shelf, chemist, 0.4f);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsAtShelf: Result={atShelf}, chemist={chemist?.fullName}, ChemistPos={chemist.transform.position}, SupplyPos={shelf.transform.position}, Distance={Vector3.Distance(chemist.transform.position, shelf.transform.position)}",
          DebugLogger.Category.Chemist);
      return atShelf;
    }

    public virtual IEnumerator GrabRoutine(Behaviour behaviour, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GrabRoutine: Entered for {behaviour.Npc?.fullName}, state={state.CurrentState}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      if (chemist.Avatar?.Anim != null)
      {
        chemist.Avatar.Anim.ResetTrigger("GrabItem");
        chemist.Avatar.Anim.SetTrigger("GrabItem");
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"GrabRoutine: Triggered GrabItem animation for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        yield return new WaitForSeconds(0.2f);
      }
      state.GrabRoutine = null;
    }

    protected virtual void RemoveItem(NPC npc, int quantity, StateData state, string id = "")
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"RemoveItem: Entered for {npc?.fullName}, quantity={quantity}, targetItem={state.TargetItem?.ID}",
          DebugLogger.Category.Chemist);
      string targetItemId = string.IsNullOrEmpty(id) ? state.TargetItem.ID : id;
      int quantityToRemove = quantity;
      List<(ItemSlot slot, int amount)> toRemove = new();
      foreach (ItemSlot slot in npc.Inventory.ItemSlots)
      {
        if (slot?.ItemInstance != null && slot.ItemInstance.ID == targetItemId && slot.Quantity > 0)
        {
          int amount = Mathf.Min(slot.Quantity, quantityToRemove);
          toRemove.Add((slot, amount));
          quantityToRemove -= amount;
          if (quantityToRemove <= 0)
            break;
        }
      }
      foreach (var (slot, amount) in toRemove)
      {
        slot.SetQuantity(slot.Quantity - amount);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"RemoveItem: Removed {amount} of {targetItemId} from {npc?.fullName}",
            DebugLogger.Category.Chemist);
      }
      state.QuantityInventory = npc.Inventory._GetItemAmount(state.TargetItem.ID);
    }

    public virtual void Disable(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"Disable: Entered for {behaviour.Npc?.fullName}",
          DebugLogger.Category.Chemist);
      Chemist chemist = behaviour.Npc as Chemist;
      if (states.TryGetValue(behaviour, out var state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"Disable: Disabling behaviour for {chemist?.fullName}, state={state.CurrentState}, invQty={state.QuantityInventory}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}, fetchingCount={state.Fetching.Count}",
            DebugLogger.Category.Chemist);
        if (state.WalkToSuppliesRoutine != null)
          MelonCoroutines.Stop(state.WalkToSuppliesRoutine);
        if (state.GrabRoutine != null)
          MelonCoroutines.Stop(state.GrabRoutine);
        if (state.InsertRoutine != null)
          MelonCoroutines.Stop(state.InsertRoutine);
        state.WalkToSuppliesRoutine = null;
        state.GrabRoutine = null;
        state.InsertRoutine = null;
        state.Fetching.Clear();
        state.OperationPending = false;
        state.IsMoving = false;
        states.Remove(behaviour);
      }
      behaviour.Disable();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"Disable: Behaviour disabled for {chemist?.fullName}",
          DebugLogger.Category.Chemist);
    }

    public virtual bool IsAtStation(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"IsAtStation: Entered for {behaviour.Npc?.fullName}",
          DebugLogger.Category.Chemist);
      IStationAdapter station = StationHandlerFactory.GetStation(behaviour);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"IsAtStation: Station is null for {behaviour.Npc?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        return false;
      }
      float distance = Vector3.Distance(behaviour.Npc.transform.position, station.GetAccessPoint());
      bool atStation = distance < 1.5f;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"IsAtStation: Result={atStation}, Distance={distance:F2}, chemist={behaviour.Npc?.fullName}, station={station.GUID}",
          DebugLogger.Category.Chemist);
      return atStation;
    }

    public static void Cleanup(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"Cleanup: Entered for {behaviour.Npc?.fullName}",
          DebugLogger.Category.Chemist);
      if (states.ContainsKey(behaviour))
      {
        var state = states[behaviour];
        if (state.WalkToSuppliesRoutine != null) MelonCoroutines.Stop(state.WalkToSuppliesRoutine);
        if (state.GrabRoutine != null) MelonCoroutines.Stop(state.GrabRoutine);
        if (state.InsertRoutine != null) MelonCoroutines.Stop(state.InsertRoutine);
        states.Remove(behaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"Cleanup: Removed state for {behaviour.Npc?.fullName}",
            DebugLogger.Category.Chemist);
      }
    }
  }

  [HarmonyPatch(typeof(Chemist))]
  public class ChemistPatch
  {
    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(Chemist __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ChemistPatch.Awake: Entered for {__instance?.fullName}",
          DebugLogger.Category.Chemist);
      try
      {
        ChemistBehaviour.Cleanup(__instance.StartCauldronBehaviour);
        ChemistBehaviour.Cleanup(__instance.StartMixingStationBehaviour);
        ChemistBehaviour.states[__instance.StartCauldronBehaviour] = new ChemistBehaviour.StateData { CurrentState = ChemistBehaviour.EState.Idle };
        ChemistBehaviour.states[__instance.StartMixingStationBehaviour] = new ChemistBehaviour.StateData { CurrentState = ChemistBehaviour.EState.Idle };
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ChemistPatch.Awake: Reinitialized states for {__instance?.fullName}",
            DebugLogger.Category.Chemist);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ChemistPatch.Awake: Failed for chemist: {__instance?.fullName}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
      }
    }

    [HarmonyPatch("TryStartNewTask")]
    [HarmonyPrefix]
    static bool TryStartNewTaskPrefix(Chemist __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ChemistPatch.TryStartNewTask: Entered for {__instance?.fullName}",
          DebugLogger.Category.Chemist);
      if (!InstanceFinder.IsServer)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ChemistPatch.TryStartNewTask: Skipping, not server for {__instance?.fullName}",
            DebugLogger.Category.Chemist);
        return false;
      }
      try
      {
        List<LabOven> labOvensReadyToFinish = __instance.GetLabOvensReadyToFinish();
        if (labOvensReadyToFinish.Count > 0)
        {
          __instance.FinishLabOven(labOvensReadyToFinish[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Finishing lab oven for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<LabOven> labOvensReadyToStart = __instance.GetLabOvensReadyToStart();
        if (labOvensReadyToStart.Count > 0)
        {
          __instance.StartLabOven(labOvensReadyToStart[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Starting lab oven for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<ChemistryStation> chemistryStationsReadyToStart = __instance.GetChemistryStationsReadyToStart();
        if (chemistryStationsReadyToStart.Count > 0)
        {
          __instance.StartChemistryStation(chemistryStationsReadyToStart[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Starting chemistry station for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<Cauldron> cauldronsReadyToStart = __instance.GetCauldronsReadyToStart();
        if (cauldronsReadyToStart.Count > 0)
        {
          __instance.StartCauldron(cauldronsReadyToStart[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Starting cauldron for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<MixingStation> mixingStationsReadyToStart = __instance.GetMixingStationsReadyToStart();
        if (mixingStationsReadyToStart.Count > 0)
        {
          __instance.StartMixingStation(mixingStationsReadyToStart[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Starting mixing station for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<LabOven> labOvensReadyToMove = __instance.GetLabOvensReadyToMove();
        if (labOvensReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, labOvensReadyToMove[0].OutputSlot.ItemInstance, labOvensReadyToMove[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Moving lab oven output for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<ChemistryStation> chemStationsReadyToMove = __instance.GetChemStationsReadyToMove();
        if (chemStationsReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, chemStationsReadyToMove[0].OutputSlot.ItemInstance, chemStationsReadyToMove[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Moving chemistry station output for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<Cauldron> cauldronsReadyToMove = __instance.GetCauldronsReadyToMove();
        if (cauldronsReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, cauldronsReadyToMove[0].OutputSlot.ItemInstance, cauldronsReadyToMove[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Moving cauldron output for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        List<MixingStation> mixStationsReadyToMove = __instance.GetMixStationsReadyToMove();
        if (mixStationsReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, mixStationsReadyToMove[0].OutputSlot.ItemInstance, mixStationsReadyToMove[0]);
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"ChemistPatch.TryStartNewTask: Moving mixing station output for {__instance?.fullName}",
              DebugLogger.Category.Chemist);
          return false;
        }
        __instance.SubmitNoWorkReason("No tasks available.", string.Empty, 0);
        __instance.SetIdle(true);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ChemistPatch.TryStartNewTask: No tasks available, setting idle for {__instance?.fullName}",
            DebugLogger.Category.Chemist);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ChemistPatch.TryStartNewTask: Failed for chemist: {__instance?.fullName}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.Stacktrace);
        __instance.SubmitNoWorkReason("Task assignment error.", string.Empty, 0);
        __instance.SetIdle(true);
        return false;
      }
    }

    private static void MoveOutputToShelf(Chemist chemist, ItemInstance outputItem, ITransitEntity source)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"MoveOutputToShelf: Entered for {chemist?.fullName}, item={outputItem?.ID}",
          DebugLogger.Category.Chemist);
      PlaceableStorageEntity shelf = FindShelfForDelivery(chemist, outputItem);
      if (shelf == null)
      {
        chemist.SubmitNoWorkReason($"No shelf for {outputItem.ID}.", string.Empty, 0);
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MoveOutputToShelf: No shelf found for {outputItem.ID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
        return;
      }
      TransitRoute route = new TransitRoute(source, shelf);
      if (chemist.MoveItemBehaviour.IsTransitRouteValid(route, outputItem.ID))
      {
        chemist.MoveItemBehaviour.Initialize(route, outputItem);
        chemist.MoveItemBehaviour.Enable_Networked(null);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MoveOutputToShelf: Moving {outputItem.ID} to shelf {shelf.GUID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
      else
      {
        chemist.SubmitNoWorkReason($"Invalid route to shelf for {outputItem.ID}.", string.Empty, 0);
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"MoveOutputToShelf: Invalid route to shelf {shelf.GUID} for {outputItem.ID} for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
    }
  }

  [HarmonyPatch(typeof(Employee))]
  public class EmployeePatch
  {
    [HarmonyPatch("OnDestroy")]
    [HarmonyPostfix]
    static void OnDestroyPostfix(Employee __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"EmployeePatch.OnDestroy: Entered for {__instance?.fullName}",
          DebugLogger.Category.Chemist);
      if (__instance is Chemist chemist)
      {
        ChemistBehaviour.Cleanup(chemist.StartCauldronBehaviour);
        ChemistBehaviour.Cleanup(chemist.StartMixingStationBehaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"EmployeePatch.OnDestroy: Cleaned up states for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
    }
  }
}