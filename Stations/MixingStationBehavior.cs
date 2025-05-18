
using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Employees.EmployeeBehaviour;
using static NoLazyWorkers.Structures.StorageUtilities;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.ChemistExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;

namespace NoLazyWorkers.Stations
{
  public static class MixingStationUtilities
  {
    public static ItemField GetInputItemForProductSlot(IStationAdapter station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetInputItemForProductSlot: Entered for station={station?.GUID}",
          DebugLogger.Category.MixingStation);
      if (station == null || !(station is MixingStationAdapter mixingAdapter))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetInputItemForProductSlot: Invalid or null station, GUID={station?.GUID}",
            DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
        return null;
      }
      MixingStation mixingStation = mixingAdapter?.Station;
      var productInSlot = mixingStation.ProductSlot.ItemInstance?.Definition as ProductDefinition;
      if (productInSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"GetInputItemForProductSlot: Product slot item is not a ProductDefinition for station={mixingStation.GUID}",
            DebugLogger.Category.MixingStation);
        return null;
      }
      if (!MixingRoutes.TryGetValue(mixingStation.GUID, out var routes) || routes == null || routes.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"GetInputItemForProductSlot: No routes defined for station={mixingStation.GUID}",
            DebugLogger.Category.MixingStation);
        return null;
      }
      var matchingRoute = routes.FirstOrDefault(route =>
          route.Product?.SelectedItem != null && route.Product.SelectedItem == productInSlot);
      if (matchingRoute == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"GetInputItemForProductSlot: No route matches product={productInSlot.Name} for station={mixingStation.GUID}",
            DebugLogger.Category.MixingStation);
        return null;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"GetInputItemForProductSlot: Found mixerItem={matchingRoute.MixerItem.SelectedItem?.Name ?? "null"} for product={productInSlot.Name} in station={mixingStation.GUID}",
          DebugLogger.Category.MixingStation);
      return matchingRoute.MixerItem;
    }
  }

  public class MixingStationBehaviour : EmployeeBehaviour
  {
    public MixingStationBehaviour(NPC npc, ChemistAdapter adapter = null) : base(npc, adapter)
    {
      if (npc == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "MixingStationBehaviour: NPC is null", DebugLogger.Category.Chemist);
        throw new ArgumentNullException(nameof(npc));
      }
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBehaviour: Initialized for NPC {npc.fullName}", DebugLogger.Category.Chemist);
    }

    public virtual bool ValidateState(Chemist chemist, Behaviour behaviour, StateData state, out bool canStart, out bool canRestock)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateState: Entered for {chemist?.fullName}, state={state.CurrentState}", DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      canStart = false;
      canRestock = false;
      bool hasSufficient = false;
      IStationAdapter station = EmployeeUtilities.GetStation(behaviour);
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
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Checking {__instance.configuration.MixStations?.Count ?? 0} stations for {__instance.fullName}", DebugLogger.Category.Chemist);

        if (!EmployeeAdapters.TryGetValue(__instance.GUID, out var employee))
        {
          employee = new ChemistAdapter(__instance);
          EmployeeAdapters[__instance.GUID] = employee;
        }
        MixingStationBehaviour behaviourInstance = new MixingStationBehaviour(__instance, (ChemistAdapter)employee);

        foreach (MixingStation station in __instance.configuration.MixStations ?? Enumerable.Empty<MixingStation>())
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, "GetMixingStationsReadyToStart: Null station in MixStations", DebugLogger.Category.Chemist);
            continue;
          }
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetMixingStationsReadyToStart: No adapter for station {station.GUID}, creating new", DebugLogger.Category.Chemist);
            stationAdapter = new MixingStationAdapter(station);
          }
          if (((IUsable)station).IsInUse || stationAdapter.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixingStationsReadyToStart: Station {station.GUID} in use or active, skipping", DebugLogger.Category.Chemist);
            continue;
          }
          if (!Config.TryGetValue(station.GUID, out var config))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetMixingStationsReadyToStart: Config missing for station {station.GUID}, using default", DebugLogger.Category.Chemist);
            config = station.Configuration as MixingStationConfiguration;
            Config[station.GUID] = config;
          }

          var behaviour = __instance.StartMixingStationBehaviour;
          behaviour.targetStation = station;
          var state = new StateData { CurrentState = EState.Idle };

          if (!behaviourInstance.ValidateState(__instance, behaviour, state, out bool canStart, out bool canRestock))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetMixingStationsReadyToStart: State validation failed for station {station.GUID}, canStart={canStart}, canRestock={canRestock}", DebugLogger.Category.Chemist);
            continue;
          }
          if (canStart || canRestock)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetMixingStationsReadyToStart: Station {station.GUID} ready, canStart={canStart}, canRestock={canRestock}", DebugLogger.Category.Chemist);
            list.Add(station);
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
  }

  /* [HarmonyPatch(typeof(StartMixingStationBehaviour))]
  public class StartMixingStationBehaviourPatch
  {
    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(StartMixingStationBehaviour __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"StartMixingStationBehaviourPatch.Awake: Entered for {__instance.chemist?.fullName}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      try
      {
        Cleanup(__instance);
        states[__instance] = new StateData
        {
          CurrentState = EState.Idle,
          Fetching = new Dictionary<PlaceableStorageEntity, int>()
        };
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StartMixingStationBehaviourPatch.Awake: Initialized state for {__instance.chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StartMixingStationBehaviourPatch.Awake: Failed for chemist: {__instance.chemist?.fullName}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
      }
    } */

  /* [HarmonyPatch("StartCook")]
  [HarmonyPrefix]
  static bool StartCookPrefix(StartMixingStationBehaviour __instance)
  {
    DebugLogger.Log(DebugLogger.LogLevel.Verbose,
        $"StartCook: Entered for {__instance.chemist?.fullName}",
        DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
    try
    {
      if (!states.TryGetValue(__instance, out var state))
      {
        state = new StateData
        {
          CurrentState = EState.Idle,
          Fetching = new Dictionary<PlaceableStorageEntity, int>()
        };
        states[__instance] = state;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StartCook: Initialized new state for {__instance.chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      }

      IStationAdapter station = mixingBehaviour.GetStation(__instance);
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StartCook: Station is null for {__instance.chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
        mixingBehaviour.Disable(__instance);
        return false;
      }

      if (!mixingBehaviour.ValidateOperationState(__instance.chemist, __instance, state))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"StartCook: Operation state invalid for {__instance.chemist?.fullName}, station={station.GUID}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        mixingBehaviour.Disable(__instance);
        return false;
      }

      station.StartOperation(__instance);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StartCook: Started operation for {__instance.chemist?.fullName}, station={station.GUID}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      return false;
    }
    catch (Exception e)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Error,
          $"StartCook: Failed for chemist: {__instance.chemist?.fullName}, error: {e}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
      mixingBehaviour.Disable(__instance);
      return false;
    }
  }
} */

  [HarmonyPatch(typeof(Chemist))]
  public class ChemistMixingStationBehaviourPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("GetMixStationsReadyToMove")]
    static bool GetMixStationsReadyToMovePrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetMixStationsReadyToMove: Checking stations for chemist={__instance?.fullName ?? "null"}, total stations={__instance?.configuration.MixStations.Count ?? 0}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      try
      {
        List<MixingStation> list = new();
        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"GetMixStationsReadyToMove: Null station in MixStations for chemist={__instance?.fullName ?? "null"}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
            continue;
          }

          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot?.Quantity <= 0 || outputSlot.ItemInstance == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"GetMixStationsReadyToMove: Skipping station={station.GUID}, output slot empty or invalid",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            continue;
          }

          var outputProduct = outputSlot.ItemInstance as ProductItemInstance;

          TransitRoute route = null;
          // Check for looping
          if (MixingRoutes.TryGetValue(station.GUID, out var routes) && routes != null && routes.Any())
          {
            MixingRoute matchingRoute = routes.FirstOrDefault(route =>
                route.Product?.SelectedItem != null && route.Product.SelectedItem == outputProduct.definition);
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"GetMixStationsReadyToMove: product={outputProduct?.Name} in station={station?.GUID} | {matchingRoute?.Product?.SelectedItem?.name} | {outputSlot?.ItemInstance?.Name}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            if (matchingRoute != null)
            {
              route = new TransitRoute(station, station);
              __instance.MoveItemBehaviour.beh.Npc = __instance;
              __instance.MoveItemBehaviour.Initialize(route, outputSlot.ItemInstance);
              __instance.MoveItemBehaviour.Enable_Networked(null);
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"GetMixStationsReadyToMove: Initialized MoveItemBehaviour for looping product={outputProduct.Name} in station={station.GUID}",
                  DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
              continue;
            }
            else
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info,
                  $"GetMixStationsReadyToMove: No matching route for output={outputProduct.Name} in station={station.GUID}, checking shelf",
                  DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            }
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"GetMixStationsReadyToMove: No routes defined for station={station.GUID}, checking shelf",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
          }

          // Fallback to shelf delivery
          PlaceableStorageEntity shelf = FindShelfForDelivery(__instance, outputSlot.ItemInstance);
          if (shelf != null)
          {
            if (shelf.InputSlots == null || !shelf.InputSlots.Any())
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning,
                  $"FindShelfForDelivery: Shelf {shelf.GUID} has no valid input slots for product={outputProduct.Name}",
                  DebugLogger.Category.Chemist, DebugLogger.Category.Storage);
              return false;
            }
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"GetMixStationsReadyToMove: Initialized MoveItemBehaviour for shelf={shelf.GUID}, product={outputProduct.Name}, station={station.GUID}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            route = new TransitRoute(station, shelf);
            if (outputSlot?.Quantity <= 0 || outputSlot.ItemInstance == null || outputSlot.ItemInstance.Definition == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                  $"GetMixStationsReadyToMove: Skipping station={station.GUID}, output slot empty or invalid {outputSlot?.Quantity} | {outputSlot.ItemInstance == null} | {outputSlot.ItemInstance.Definition}",
                  DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
              continue;
            }
            __instance.MoveItemBehaviour.Initialize(route, outputSlot.ItemInstance);
            __instance.MoveItemBehaviour.Enable_Networked(null);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"GetMixStationsReadyToMove: No shelf found for product={outputProduct.Name} in station={station.GUID}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
          }
        }
        __result = list;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"GetMixStationsReadyToMove: Found {list.Count} stations ready to move for chemist={__instance?.fullName ?? "null"}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetMixStationsReadyToMove: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
        __result = new List<MixingStation>();
        return false;
      }
    }
  }
}