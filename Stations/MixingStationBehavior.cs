using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Employees.EmployeeBehaviour;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.ChemistExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using NoLazyWorkers.General;

namespace NoLazyWorkers.Stations
{
  public class MixingStationBehaviour : EmployeeBehaviour
  {

    public MixingStationBehaviour(NPC npc, ChemistAdapter adapter = null) : base(npc, adapter)
    {
      if (npc == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "MixingStationBehaviour: NPC is null", DebugLogger.Category.Chemist);
        throw new ArgumentNullException(nameof(npc));
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBehaviour: Initialized for NPC {npc.fullName}", DebugLogger.Category.Chemist);
    }

    public virtual bool ValidateState(Chemist chemist, Behaviour behaviour, StateData state, out bool canStart, out bool canRestock)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateState: Entered for {chemist?.fullName}, state={state.CurrentState}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      canStart = false;
      canRestock = false;
      bool hasSufficient = false;
      IStationAdapter station = StationUtilities.GetStation(behaviour);
      if (station == null || chemist == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ValidateState: Invalid station or chemist for {chemist?.fullName}, station={station?.GUID}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        return false;
      }
      if (station.IsInUse || station.HasActiveOperation || station.OutputSlot.Quantity > 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ValidateState: Station in use, active, or has output for {chemist?.fullName}, station={station.GUID}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        return false;
      }
      ItemField inputItem = station.GetInputItemForProduct()[0];
      if (inputItem?.SelectedItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ValidateState: Input item null for station {station.GUID}, chemist={chemist?.fullName}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        return false;
      }
      ItemInstance targetItem = inputItem.SelectedItem.GetDefaultInstance();
      if (targetItem == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ValidateState: Target item null for station {station.GUID}, chemist={chemist?.fullName}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
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
      if (inputQty >= threshold && desiredQty >= threshold)
      {
        if (inputQty >= desiredQty)
          canStart = true;
        else
        {
          var shelves = FindShelvesWithItem(chemist, targetItem, state.QuantityNeeded - invQty, state.QuantityWanted - invQty);
          hasSufficient = shelves?.Values?.Sum() > 0;
          if (!hasSufficient)
            canStart = true;
          else
            canRestock = true;
          return true;
        }
      }
      if (desiredQty < threshold)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ValidateState: Below threshold and cannot restock for {chemist?.fullName}, inputQty={inputQty}, threshold={threshold}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        Disable(behaviour);
        return false;
      }
      if (!canStart && !canRestock)
      {
        var shelves = FindShelvesWithItem(chemist, targetItem, state.QuantityNeeded - invQty, state.QuantityWanted - invQty);
        canRestock = shelves?.Values?.Sum() > 0;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateState: Completed for {chemist?.fullName}, canStart={canStart}, canRestock={canRestock}, invQty={invQty}, inputQty={inputQty}, desiredQty={desiredQty}, threshold={threshold}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      return canRestock;
    }
  }

  [HarmonyPatch(typeof(Chemist))]
  public class MixingStationChemistPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetMixingStationsReadyToStart")]
    static bool GetMixingStationsReadyToStartPrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Entered for chemist={__instance?.fullName ?? "null"}", DebugLogger.Category.Chemist);
      try
      {
        if (__instance == null || __instance.configuration == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "GetMixingStationsReadyToStart: Chemist or configuration is null", DebugLogger.Category.Chemist);
          __result = new List<MixingStation>();
          return false;
        }
        List<MixingStation> list = new();
        var chemistBehaviour = ChemistUtilities.GetChemistBehaviour(__instance);
        if (chemistBehaviour == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetMixingStationsReadyToStart: No ChemistBehaviour for {__instance.fullName}", DebugLogger.Category.Chemist);
          __result = new List<MixingStation>();
          return false;
        }
        foreach (MixingStation station in __instance.configuration.MixStations ?? Enumerable.Empty<MixingStation>())
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, "GetMixingStationsReadyToStart: Null station in MixStations", DebugLogger.Category.Chemist);
            continue;
          }
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            stationAdapter = new MixingStationAdapter(station);
            StationAdapters[station.GUID] = stationAdapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixingStationsReadyToStart: Created adapter for station {station.GUID}", DebugLogger.Category.Chemist);
          }
          if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixingStationsReadyToStart: Station {station.GUID} in use or active, skipping", DebugLogger.Category.Chemist);
            continue;
          }
          if (!MixingStationUtilities.ValidateStationState(__instance, stationAdapter, out bool canStart, out bool canRestock, out var restock))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixingStationsReadyToStart: State validation failed for station {station.GUID}", DebugLogger.Category.Chemist);
            continue;
          }
          if (canStart)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixingStationsReadyToStart: Station {station.GUID} ready to start", DebugLogger.Category.Chemist);
            list.Add(station);
          }
          else if (canRestock)
          {
            if (!States.TryGetValue(__instance.GUID, out var state))
            {
              state = new StateData { Station = stationAdapter };
              States[__instance.GUID] = state;
            }
            state.Station = stationAdapter;
            var behaviour = chemistBehaviour.GetInstancedBehaviour(__instance, stationAdapter);
            chemistBehaviour.AddRoutes(behaviour, state, [EmployeeUtilities.CreateTransferRequest(__instance, restock.Item, restock.Quantity, restock.Shelf, restock.PickupSlots, station, [station.MixerSlot], force: true)]);
            chemistBehaviour.TransitionState(behaviour, state, EState.Grabbing, "Looping route planned");
          }
        }
        __result = list;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixingStationsReadyToStart: Found {list.Count} stations ready for {__instance.fullName}", DebugLogger.Category.Chemist);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetMixingStationsReadyToStart: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
        __result = new List<MixingStation>();
        return false;
      }
    }

    [HarmonyPrefix]
    [HarmonyPatch("GetMixStationsReadyToMove")]
    static bool GetMixStationsReadyToMovePrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixStationsReadyToMove: Checking stations for chemist={__instance?.fullName ?? "null"}", DebugLogger.Category.Chemist);
      try
      {
        List<MixingStation> list = new();
        var chemistBehaviour = ChemistUtilities.GetChemistBehaviour(__instance);
        if (chemistBehaviour == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetMixStationsReadyToMove: No ChemistBehaviour for {__instance.fullName}", DebugLogger.Category.Chemist);
          __result = new List<MixingStation>();
          return false;
        }
        foreach (MixingStation station in __instance.configuration.MixStations ?? Enumerable.Empty<MixingStation>())
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetMixStationsReadyToMove: Null station in MixStations", DebugLogger.Category.Chemist);
            continue;
          }
          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot?.Quantity <= 0 || outputSlot.ItemInstance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixStationsReadyToMove: Skipping station={station.GUID}, output slot empty", DebugLogger.Category.Chemist);
            continue;
          }
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            stationAdapter = new MixingStationAdapter(station);
            StationAdapters[station.GUID] = stationAdapter;
          }
          var outputProduct = outputSlot.ItemInstance as ProductItemInstance;
          if (outputProduct == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixStationsReadyToMove: Output item is not a ProductItemInstance for station={station.GUID}", DebugLogger.Category.Chemist);
            continue;
          }
          var behaviour = chemistBehaviour.GetInstancedBehaviour(__instance, stationAdapter);
          if (!States.TryGetValue(__instance.GUID, out var state))
          {
            state = new StateData { Station = stationAdapter };
            States[__instance.GUID] = state;
          }
          state.Station = stationAdapter;
          // Check for looping
          if (MixingRoutes.TryGetValue(station.GUID, out var routes) && routes != null && routes.Any())
          {
            MixingRoute matchingRoute = routes.FirstOrDefault(route =>
                route.Product?.SelectedItem != null && route.Product.SelectedItem == outputProduct.definition);
            if (matchingRoute != null)
            {
              var deliverySlots = stationAdapter.ProductSlots.Where(s => s.GetCapacityForItem(outputSlot.ItemInstance) > 0).ToList();
              if (deliverySlots.Count == 0)
              {
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: No product slots available for station={station.GUID}", DebugLogger.Category.Chemist);
                continue;
              }
              int quantity = Math.Min(outputSlot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(outputSlot.ItemInstance)));
              var request = EmployeeUtilities.CreateTransferRequest(__instance, outputSlot.ItemInstance, quantity, station, [outputSlot], station, deliverySlots);
              if (request != null)
              {
                chemistBehaviour.AddRoutes(behaviour, state, new List<TransferRequest> { request });
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: Planned looping for product={outputProduct.Name} in station={station.GUID}", DebugLogger.Category.Chemist);
                chemistBehaviour.TransitionState(behaviour, state, EState.Grabbing, "Looping route planned");
                continue;
              }
            }
          }
          // Fallback to product delivery
          ITransitEntity destination = EmployeeUtilities.FindPackagingStation(chemistBehaviour.Employee, outputSlot.ItemInstance) ?? FindShelfForDelivery(__instance, outputSlot.ItemInstance);
          if (destination != null)
          {
            var destinationSlots = destination.ReserveInputSlotsForItem(outputSlot.ItemInstance, __instance.NetworkObject);
            if (destinationSlots == null || destinationSlots.Count == 0)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: No delivery slots for destination={destination.GUID}", DebugLogger.Category.Chemist);
              continue;
            }
            int quantity = Math.Min(outputSlot.Quantity, destinationSlots.Sum(s => s.GetCapacityForItem(outputSlot.ItemInstance)));
            var inventorySlot = __instance.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
            if (inventorySlot == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: No inventory slot for product={outputProduct.Name}", DebugLogger.Category.Chemist);
              continue;
            }
            var request = EmployeeUtilities.CreateTransferRequest(__instance, outputSlot.ItemInstance, quantity, station, [outputSlot], destination, destinationSlots);
            if (request != null)
            {
              chemistBehaviour.AddRoutes(behaviour, state, new List<TransferRequest> { request });
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: Planned delivery for product={outputProduct.Name} to {destination.GUID}", DebugLogger.Category.Chemist);
              chemistBehaviour.TransitionState(behaviour, state, EState.Delivery, "Shelf/packaging delivery planned");
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: No destination found for product={outputProduct.Name} in station={station.GUID}", DebugLogger.Category.Chemist);
          }
        }
        __result = new List<MixingStation>();
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixStationsReadyToMove: Found {list.Count} stations ready to move for chemist={__instance.fullName}", DebugLogger.Category.Chemist);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetMixStationsReadyToMove: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}", DebugLogger.Category.Chemist);
        __result = new List<MixingStation>();
        return false;
      }
    }
  }
}