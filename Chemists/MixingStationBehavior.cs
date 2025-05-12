
using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Chemists.ChemistBehaviour;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.General.GeneralExtensions;
using static NoLazyWorkers.Chemists.MixingStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;

namespace NoLazyWorkers.Chemists
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

  public class MixingStationBehaviour : ChemistBehaviour
  {
    public override IStationAdapter<TStation> GetStation<TStation>(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetStation: Entered for behaviour={behaviour?.Npc?.fullName}, type={behaviour?.GetType().Name}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      if (behaviour is StartMixingStationBehaviour stationBehaviour && stationBehaviour.targetStation != null)
      {
        if (typeof(TStation) == typeof(MixingStation))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"GetStation: Returning MixingStationAdapter for station={stationBehaviour.targetStation.GUID}, chemist={behaviour.Npc?.fullName}",
              DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
          return new MixingStationAdapter(stationBehaviour.targetStation) as IStationAdapter<TStation>;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetStation: Type mismatch for {behaviour?.Npc?.fullName}, expected TStation=MixingStation, got TStation={typeof(TStation).Name}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
        return null;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error,
          $"GetStation: Invalid behaviour or null target station for {behaviour?.Npc?.fullName}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
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
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Entered for chemist={__instance?.name}, total stations={__instance.configuration.MixStations.Count}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      try
      {
        List<MixingStation> list = new();
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Checking {__instance.configuration.MixStations.Count} stations for {__instance?.name}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);

        MixingStationBehaviour behaviourInstance = new MixingStationBehaviour();
        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Null station in MixStations for {__instance?.name}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
            continue;
          }

          IStationAdapter prodStation = new MixingStationAdapter(station);
          if (((IUsable)station).IsInUse || prodStation.HasActiveOperation)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose,
                $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID} in use or active, skipping",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            continue;
          }

          if (!Config.TryGetValue(station.GUID, out var config))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Config missing for station {station?.GUID}, using default",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
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
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"MixingStationChemistPatch.GetMixingStationsReadyToStart: State validation failed for station {station.GUID}, canStart={canStart}, canRestock={canRestock}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            continue;
          }

          states.Remove(behaviour);
          if (canStart || canRestock)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID} ready, canStart={canStart}, canRestock={canRestock}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            list.Add(station);
          }
        }

        __result = list;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Found {list.Count} stations ready for {__instance?.name}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationChemistPatch.GetMixingStationsReadyToStart: Failed for chemist: {__instance?.name}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
        __result = new List<MixingStation>();
        return false;
      }
    }

    [HarmonyPatch("GetMixStationsReadyToMove")]
    [HarmonyPrefix]
    static bool GetMixStationsReadyToMovePrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"MixingStationChemistPatch.GetMixStationsReadyToMove: Entered for chemist={__instance?.fullName}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      try
      {
        List<MixingStation> list = new();
        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error,
                $"MixingStationChemistPatch.GetMixStationsReadyToMove: Null station in MixStations for {__instance?.fullName}",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
            continue;
          }
          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot.Quantity > 0 && FindShelfForDelivery(__instance, outputSlot.ItemInstance) != null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"MixingStationChemistPatch.GetMixStationsReadyToMove: Station {station.GUID} has output {outputSlot.ItemInstance?.ID}, shelf available",
                DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
            list.Add(station);
          }
        }
        __result = list;
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"MixingStationChemistPatch.GetMixStationsReadyToMove: Found {list.Count} stations for {__instance?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"MixingStationChemistPatch.GetMixStationsReadyToMove: Failed for chemist: {__instance?.fullName}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
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
    }

    [HarmonyPatch("StartCook")]
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

        IStationAdapter station = mixingBehaviour.GetStation<MixingStation>(__instance);
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

    [HarmonyPatch("Update")]
    [HarmonyPrefix]
    static bool UpdatePrefix(StartMixingStationBehaviour __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"StartMixingStationBehaviourPatch.Update: Entered for {__instance.chemist?.fullName}",
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
              $"StartMixingStationBehaviourPatch.Update: Initialized new state for {__instance.chemist?.fullName}",
              DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
        }

        mixingBehaviour.UpdateMovement(__instance, state);
        mixingBehaviour.StateHandlers[state.CurrentState](__instance, state);
        return false;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StartMixingStationBehaviourPatch.Update: Failed for chemist: {__instance.chemist?.fullName}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
        mixingBehaviour.Disable(__instance);
        return false;
      }
    }

    [HarmonyPatch("OnDisable")]
    [HarmonyPostfix]
    static void OnDisablePostfix(StartMixingStationBehaviour __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"StartMixingStationBehaviourPatch.OnDisable: Entered for {__instance.chemist?.fullName}",
          DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      try
      {
        Cleanup(__instance);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StartMixingStationBehaviourPatch.OnDisable: Cleaned up state for {__instance.chemist?.fullName}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StartMixingStationBehaviourPatch.OnDisable: Failed for chemist: {__instance.chemist?.fullName}, error: {e}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation, DebugLogger.Category.Stacktrace);
      }
    }
  }
}