
using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Chemists.ChemistBehaviour;
using static NoLazyWorkers.Handlers.StorageUtilities;
using static NoLazyWorkers.Chemists.ChemistExtensions;
using static NoLazyWorkers.Chemists.MixingStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;

namespace NoLazyWorkers.Chemists
{
  public static class MixingStationUtilities
  {
    public static ItemField GetInputItemForProductSlot(IStationAdapter station)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetInputItemForProductSlot: Entered for station={station?.GUID}", isStation: true);
      if (station == null || !(station is MixingStationAdapter mixingAdapter))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetInputItemForProductSlot: Invalid or null station, GUID={station?.GUID}", isStation: true);
        return null;
      }
      MixingStation mixingStation = mixingAdapter?._station;
      var productInSlot = mixingStation.ProductSlot.ItemInstance?.Definition as ProductDefinition;
      if (productInSlot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetInputItemForProductSlot: Product slot item is not a ProductDefinition for station={mixingStation.GUID}", isStation: true);
        return null;
      }
      if (!MixingRoutes.TryGetValue(mixingStation.GUID, out var routes) || routes == null || routes.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetInputItemForProductSlot: No routes defined for station={mixingStation.GUID}", isStation: true);
        return null;
      }
      var matchingRoute = routes.FirstOrDefault(route =>
          route.Product?.SelectedItem != null && route.Product.SelectedItem == productInSlot);
      if (matchingRoute == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetInputItemForProductSlot: No route matches product={productInSlot.Name} for station={mixingStation.GUID}", isStation: true);
        return null;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetInputItemForProductSlot: Found mixerItem={matchingRoute.MixerItem.SelectedItem?.Name ?? "null"} for product={productInSlot.Name} in station={mixingStation.GUID}", isStation: true);
      return matchingRoute.MixerItem;
    }
  }

  public class MixingStationBehaviour : ChemistBehaviour
  {
    public override IStationAdapter GetStation(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetStation: Entered for behaviour={behaviour?.Npc?.fullName}, type={behaviour?.GetType().Name}", isChemist: true);
      if (behaviour is StartMixingStationBehaviour mixingBehaviour && mixingBehaviour.targetStation != null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GetStation: Returning MixingStationAdapter for station={mixingBehaviour.targetStation.GUID}, chemist={behaviour.Npc?.fullName}", isChemist: true);
        return new MixingStationAdapter(mixingBehaviour.targetStation);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetStation: Invalid behaviour or null target station for {behaviour?.Npc?.fullName}", isChemist: true);
      return null;
    }
  }

  [HarmonyPatch(typeof(Chemist))]
  public class MixingStationChemistPatch
  {
    [HarmonyPatch("GetMixingStationsReadyToStart")]
    [HarmonyPrefix]
    static bool GetMixingStationsReadyToStartPrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Entered for chemist={__instance?.name}, total stations={__instance.configuration.MixStations.Count}", isChemist: true);
      try
      {
        List<MixingStation> list = new();
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Checking {__instance.configuration.MixStations.Count} stations for {__instance?.name}", isChemist: true);

        MixingStationBehaviour behaviourInstance = new MixingStationBehaviour();
        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Null station in MixStations for {__instance?.name}", isChemist: true);
            continue;
          }

          IStationAdapter prodStation = new MixingStationAdapter(station);
          if (((IUsable)station).IsInUse || prodStation.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID} in use or active, skipping", isChemist: true);
            continue;
          }

          if (!Config.TryGetValue(station.GUID, out var config))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Config missing for station {station?.GUID}, using default", isChemist: true);
            config = station.Configuration as MixingStationConfiguration;
            Config[station.GUID] = config;
          }

          // Create a temporary behaviour and state for validation
          var behaviour = __instance.StartMixingStationBehaviour;
          behaviour.targetStation = station;
          if (!states.TryGetValue(behaviour, out var state))
          {
            state = new StateData
            {
              CurrentState = EState.Idle,
              Fetching = new Dictionary<PlaceableStorageEntity, int>(),
            };
            states[behaviour] = state;
          }

          // Use ValidateState to determine if the station is ready
          if (!behaviourInstance.ValidateState(__instance, behaviour, state, out bool canStart, out bool canRestock))
          {
            states.Remove(behaviour);
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: State validation failed for station {station.GUID}, canStart={canStart}, canRestock={canRestock}", isChemist: true);
            continue;
          }

          states.Remove(behaviour);
          if (canStart || canRestock)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID} ready, canStart={canStart}, canRestock={canRestock}", isChemist: true);
            list.Add(station);
          }
        }

        __result = list;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Found {list.Count} stations ready for {__instance?.name}", isChemist: true);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Failed for chemist: {__instance?.name}, error: {e}", isChemist: true);
        __result = new List<MixingStation>();
        return false;
      }
    }

    [HarmonyPatch("GetMixStationsReadyToMove")]
    [HarmonyPrefix]
    static bool GetMixStationsReadyToMovePrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationChemistPatch.GetMixStationsReadyToMove: Entered for chemist={__instance?.fullName}", isChemist: true);
      try
      {
        List<MixingStation> list = new();
        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationChemistPatch.GetMixStationsReadyToMove: Null station in MixStations for {__instance?.fullName}", isChemist: true);
            continue;
          }
          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot.Quantity > 0 && FindShelfForDelivery(__instance, outputSlot.ItemInstance) != null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationChemistPatch.GetMixStationsReadyToMove: Station {station.GUID} has output {outputSlot.ItemInstance?.ID}, shelf available", isChemist: true);
            list.Add(station);
          }
        }
        __result = list;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationChemistPatch.GetMixStationsReadyToMove: Found {list.Count} stations for {__instance?.fullName}", isChemist: true);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationChemistPatch.GetMixStationsReadyToMove: Failed for chemist: {__instance?.fullName}, error: {e}", isChemist: true);
        __result = new List<MixingStation>();
        return false;
      }
    }
  }

  [HarmonyPatch(typeof(StartMixingStationBehaviour))]
  public class StartMixingStationBehaviourPatch
  {
    private static readonly MixingStationBehaviour mixingBehaviour = new MixingStationBehaviour();

    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(StartMixingStationBehaviour __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StartMixingStationBehaviourPatch.Awake: Entered for {__instance.chemist?.fullName}", isChemist: true);
      try
      {
        Cleanup(__instance);
        states[__instance] = new StateData
        {
          CurrentState = EState.Idle,
          Fetching = new Dictionary<PlaceableStorageEntity, int>()
        };
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartMixingStationBehaviourPatch.Awake: Initialized state for {__instance.chemist?.fullName}", isChemist: true);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartMixingStationBehaviourPatch.Awake: Failed for chemist: {__instance.chemist?.fullName}, error: {e}", isChemist: true);
      }
    }

    [HarmonyPatch("StartCook")]
    [HarmonyPrefix]
    static bool StartCookPrefix(StartMixingStationBehaviour __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"StartCook: Entered for {__instance.chemist?.fullName}", isChemist: true);
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
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StartCook: State missing, initialized for {__instance.chemist?.fullName}", isChemist: true);
        }
        IStationAdapter station = mixingBehaviour.GetStation(__instance);
        if (state.CurrentState != EState.StartingOperation || !state.OperationPending || station.HasActiveOperation)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"StartCook: Invalid state {state.CurrentState}, OperationPending={state.OperationPending}, mixOperation={station.HasActiveOperation} for {__instance.chemist?.fullName}", isChemist: true);
          return false;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"StartCook: Starting cook for {__instance.chemist?.fullName}", isChemist: true);
        return true;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"StartCook: Failed for chemist: {__instance.chemist?.fullName}, error: {e}", isChemist: true);
        return false;
      }
    }

    [HarmonyPatch("ActiveMinPass")]
    [HarmonyPrefix]
    static bool ActiveMinPassPrefix(StartMixingStationBehaviour __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ActiveMinPass: Entered for {__instance.chemist?.fullName}", isChemist: true);
      try
      {
        if (!InstanceFinder.IsServer)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ActiveMinPass: Skipping, not server for {__instance.chemist?.fullName}", isChemist: true);
          return false;
        }
        __instance.onEnd.RemoveAllListeners();
        __instance.onEnd.AddListener(() => mixingBehaviour.Disable(__instance));
        Chemist chemist = __instance.chemist;
        IStationAdapter station = mixingBehaviour.GetStation(__instance);
        if (station == null || chemist == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ActiveMinPass: Station or Chemist null for {chemist?.fullName}, station={station?.GUID}, disabling behaviour", isChemist: true);
          mixingBehaviour.Disable(__instance);
          return false;
        }
        if (!states.TryGetValue(__instance, out var state))
        {
          state = new StateData
          {
            CurrentState = EState.Idle,
            Fetching = new Dictionary<PlaceableStorageEntity, int>()
          };
          states[__instance] = state;
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ActiveMinPass: State missing, initialized for {chemist?.fullName}", isChemist: true);
        }
        mixingBehaviour.UpdateMovement(__instance, state);
        if (mixingBehaviour.StateHandlers.TryGetValue(state.CurrentState, out var handler))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ActiveMinPass: Executing handler for state={state.CurrentState}, chemist={chemist.fullName}, station={station.GUID}", isChemist: true);
          handler(__instance, state);
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ActiveMinPass: Processed state={state.CurrentState}, chemist={chemist.fullName}, station={station.GUID}, inputQty={station.GetInputQuantity()}, productQty={station.ProductSlots.Sum(s => s.Quantity)}, outputQty={station.OutputSlot.Quantity}, threshold={station.StartThreshold}",
            isChemist: true);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ActiveMinPass: Failed for chemist: {__instance.chemist?.fullName}, error: {e}", isChemist: true);
        mixingBehaviour.Disable(__instance);
        return false;
      }
    }
  }
}