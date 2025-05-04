
using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.Chemists.ChemistBehaviour;
using static NoLazyWorkers.Handlers.StorageUtilities;
using static NoLazyWorkers.NoLazyUtilities;
namespace NoLazyWorkers.Chemists
{
  [HarmonyPatch(typeof(Chemist))]
  public class MixingStationChemistPatch
  {
    [HarmonyPatch("GetMixingStationsReadyToStart")]
    [HarmonyPrefix]
    static bool GetMixingStationsReadyToStartPrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      try
      {
        List<MixingStation> list = [];
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Checking stations for {__instance?.name ?? "null"}, total stations={__instance.configuration.MixStations.Count}");

        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (!((IUsable)station).IsInUse && station.CurrentMixOperation == null)
          {
            if (!MixingStationExtensions.Config.TryGetValue(station.GUID, out var config))
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Warning($"ChemistPatch.GetMixingStationsReadyToStart: MixerConfig missing for station {station?.GUID}");
              config = station.Configuration as MixingStationConfiguration;
              MixingStationExtensions.Config[station.GUID] = config;
            }

            ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
            if (mixerItem?.SelectedItem == null) continue;
            float threshold = config.StartThrehold.Value;
            int mixQuantity = station.GetMixQuantity();

            bool canStartMix = mixQuantity >= threshold && station.ProductSlot.Quantity >= threshold && station.OutputSlot.Quantity == 0;
            bool canRestock = false;
            bool hasSufficientItems = false;

            if (!canStartMix)
            {
              ItemInstance targetItem = mixerItem.SelectedItem.GetDefaultInstance();
              // Check if a shelf has the required item
              PlaceableStorageEntity shelf = FindShelfWithItem(__instance, targetItem, (int)threshold - mixQuantity);
              if (shelf != null)
              {
                hasSufficientItems = HasSufficientItems(__instance, threshold - mixQuantity, targetItem);
                canRestock = station.OutputSlot.Quantity == 0 &&
                             station.ProductSlot.Quantity >= threshold &&
                             hasSufficientItems;
              }
            }

            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID}, Supply={(ConfigurationExtensions.NPCSupply.TryGetValue(__instance.GUID, out var supply) ? supply.SelectedObject?.GUID : "null")}, IsInUse={station.GetComponent<IUsable>().IsInUse}, CurrentMixOperation={station.CurrentMixOperation != null}, canStartMix={canStartMix}, canRestock={canRestock}, mixQuantity={mixQuantity}, productQuantity={station.ProductSlot.Quantity}, outputQuantity={station.OutputSlot.Quantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}{(canRestock ? $", hasSufficientItems={hasSufficientItems}" : "")}");
            if (canStartMix || canRestock)
              list.Add(station);
          }
        }

        __result = list;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Found {list.Count} stations ready for {__instance?.name ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.GetMixingStationsReadyToStart: Failed for chemist: {__instance?.name ?? "null"}, error: {e}");
        __result = [];
        return false;
      }
    }

    // Patch GetMixStationsReadyToMove to use FindShelfForDelivery
    [HarmonyPatch("GetMixStationsReadyToMove")]
    [HarmonyPrefix]
    static bool GetMixStationsReadyToMovePrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      try
      {
        List<MixingStation> list = new();
        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot.Quantity > 0 && FindShelfForDelivery(__instance, outputSlot.ItemInstance) != null)
            list.Add(station);
        }

        __result = list;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.GetMixStationsReadyToMove: Found {list.Count} stations for {__instance?.fullName ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.GetMixStationsReadyToMove: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}");
        __result = new List<MixingStation>();
        return false;
      }
    }

    public static bool HasSufficientItems(Chemist chemist, float threshold, ItemInstance item)
    {
      return GetAmountInInventoryAndSupply(chemist, item) >= threshold;
    }

    // Patch StartMixingStation to handle mixer item fetching
    [HarmonyPatch("StartMixingStation")]
    [HarmonyPrefix]
    static bool StartMixingStationPrefix(Chemist __instance, MixingStation station)
    {
      if (!InstanceFinder.IsServer)
        return false;

      try
      {
        var behaviour = __instance.StartMixingStationBehaviour;
        if (!states.TryGetValue(behaviour, out var state))
        {
          state = new StateData { CurrentState = EState.Idle };
          states[behaviour] = state;
        }

        if (state.CurrentState != EState.Idle)
          return false;

        if (!cachedConfigs.TryGetValue(__instance, out var config) || config == null || (config as MixingStationConfiguration).station.GUID != station.GUID)
        {
          config = MixingStationExtensions.Config.TryGetValue(station.GUID, out var c) ? c : station.Configuration as MixingStationConfiguration;
          cachedConfigs[__instance] = config;
        }

        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        if (mixerItem?.SelectedItem == null)
        {
          __instance.SubmitNoWorkReason($"No mixer item for station {station.GUID}.", string.Empty, 0);
          return false;
        }

        float threshold = (config as MixingStationConfiguration).StartThrehold.Value;
        int mixQuantity = station.GetMixQuantity();
        state.TargetItem = mixerItem.SelectedItem.GetDefaultInstance();
        state.QuantityToFetch = (int)threshold - mixQuantity;
        state.ClearStationSlot = station.MixerSlot.ItemInstance != null && station.MixerSlot.ItemInstance.ID != state.TargetItem.ID;

        if (mixQuantity >= threshold && station.ProductSlot.Quantity >= threshold && station.OutputSlot.Quantity == 0)
        {
          state.CurrentState = EState.Cooking;
          state.CookPending = true;
          behaviour.StartCook();
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"ChemistPatch.StartMixingStation: Starting cook for {__instance?.fullName ?? "null"}, station={station.GUID}, mixQuantity={mixQuantity}");
          return false;
        }

        if (state.QuantityToFetch > 0)
        {
          PrepareToFetchItems(behaviour, state);
        }
        else
        {
          InsertItemsFromInventory(behaviour, state);
          state.CurrentState = EState.Idle;
        }
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.StartMixingStation: Failed for chemist: {__instance?.fullName ?? "null"}, station: {station?.GUID}, error: {e}");
        Disable(__instance.StartMixingStationBehaviour);
        return false;
      }
    }
  }

  [HarmonyPatch(typeof(StartMixingStationBehaviour))]
  public class StartMixingStationBehaviourPatch
  {
    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(StartMixingStationBehaviour __instance)
    {
      try
      {
        // Initialize state if not already present
        states[__instance] = new StateData
        {
          CurrentState = EState.Idle,
          WalkToSuppliesRoutine = null,
          GrabRoutine = null,
          TargetItem = null,
          ClearStationSlot = false,
          QuantityToFetch = 0,
          LastSupply = null,
          CookPending = false
        };
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.Awake: Initialized state for {__instance.chemist?.fullName ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.Awake: Failed for chemist: {__instance.chemist?.fullName ?? "null"}, error: {e}");
      }
    }

    [HarmonyPatch("ActiveMinPass")]
    [HarmonyPrefix]
    static bool Prefix(StartMixingStationBehaviour __instance)
    {
      try
      {
        if (!InstanceFinder.IsServer)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Skipping, not server");
          return false;
        }
        __instance.onEnd.RemoveAllListeners();
        __instance.onEnd.AddListener(() => Disable(__instance));
        Chemist chemist = __instance.chemist;
        MixingStation station = __instance.targetStation;

        if (station == null || chemist == null)
        {
          Disable(__instance);
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Station, Chemist, or ChemistConfig null for chemist: {chemist?.fullName ?? "null"}");
          return false;
        }

        // Initialize state if missing
        if (!states.TryGetValue(__instance, out var state))
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: State missing for {chemist?.fullName}, initializing");
          states[__instance] = new StateData { CurrentState = EState.Idle };
          state = states[__instance];
        }

        MixingStationConfiguration config = MixingStationExtensions.Config[station.GUID];
        if (config == null)
        {
          Disable(__instance);
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: MixerConfig not found for station: {station?.GUID}");
          return false;
        }

        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        if (mixerItem == null)
        {
          return false;
        }

        float threshold = config.StartThrehold.Value;

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: State={state.CurrentState}, station={station.GUID}, chemist={chemist.fullName}");

        switch (state.CurrentState)
        {
          case EState.Idle:
            int productQuantity = station.ProductSlot.Quantity;
            int outputQuantity = station.OutputSlot.Quantity;
            int mixQuantity = station.GetMixQuantity();

            if (outputQuantity > 0 || productQuantity < threshold)
            {
              Disable(__instance);
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Disabling for {chemist.fullName}, outputQuantity={outputQuantity}, productQuantity={productQuantity}, threshold={threshold}");
              return false;
            }

            bool hasSufficientItems = MixingStationChemistPatch.HasSufficientItems(chemist, threshold - mixQuantity, mixerItem.SelectedItem?.GetDefaultInstance());
            if (!hasSufficientItems && mixQuantity < threshold)
            {
              Disable(__instance);
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Disabling for {chemist.fullName}, hasSufficientItems={hasSufficientItems}, mixQuantity={mixQuantity}, threshold={threshold}");
              return false;
            }

            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - mixQuantity={mixQuantity}, productQuantity={productQuantity}, outputQuantity={outputQuantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}, cookPending={state.CookPending}, mixOperation={(station.CurrentMixOperation != null)}");

            if (!state.CookPending && station.CurrentMixOperation == null && mixQuantity >= threshold && station.ProductSlot.Quantity >= threshold && station.OutputSlot.Quantity == 0)
            {
              bool needsMoreMixer = mixQuantity < productQuantity;
              if (needsMoreMixer && mixerItem.SelectedItem != null)
              {
                int additionalMixerNeeded = productQuantity - mixQuantity;
                int supplyCount = GetAmountInInventoryAndSupply(chemist, mixerItem.SelectedItem.GetDefaultInstance());
                bool canFetchMore = supplyCount >= 0;

                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Cooking conditions met, checking mixer: mixQuantity={mixQuantity}, productQuantity={productQuantity}, additionalMixerNeeded={additionalMixerNeeded}, supplyCount={supplyCount}, canFetchMore={canFetchMore}");

                if (canFetchMore)
                {
                  PrepareToFetchItems(__instance, state);
                  return false;
                }
              }

              if (IsAtStation(__instance))
              {
                state.CurrentState = EState.Cooking;
                state.CookPending = true;
                __instance.StartCook();
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Starting cook for {chemist.fullName}");
              }
              else
              {
                state.CurrentState = EState.WalkingToStation;
                __instance.SetDestination(GetStationAccessPoint(__instance), true);
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Walking to station for {chemist.fullName}");
              }
            }
            else
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Preparing to fetch for {chemist.fullName}, reason={(state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : mixQuantity < threshold ? "low mixQuantity" : "other")}");
              PrepareToFetchItems(__instance, state);
            }
            break;

          case EState.WalkingToSupplies:
            // Handled by WalkRoutine
            break;

          case EState.GrabbingSupplies:
            // Handled in GrabRoutine
            break;

          case EState.WalkingToStation:
            if (IsAtStation(__instance))
            {
              int insertedQuantity = InsertItemsFromInventory(__instance, state);
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Inserted {insertedQuantity} items to station for {chemist.fullName}");

              // Recalculate mixQuantity after insertion
              int updatedMixQuantity = station.GetMixQuantity();
              if (insertedQuantity > 0 && !state.CookPending && station.CurrentMixOperation == null && updatedMixQuantity >= threshold)
              {
                state.CurrentState = EState.Cooking;
                state.CookPending = true;
                __instance.StartCook();
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Arrived at station, starting cook for {chemist.fullName}, updatedMixQuantity={updatedMixQuantity}");
              }
              else
              {
                state.CurrentState = EState.Idle;
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Arrived at station, returning to Idle for {chemist.fullName}, reason={(insertedQuantity == 0 ? "no items inserted" : state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : updatedMixQuantity < threshold ? "low mixQuantity" : "other")}, updatedMixQuantity={updatedMixQuantity}");
              }
            }
            else
            {
              state.CurrentState = EState.Idle;
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Not at station after walking, returning to Idle for {chemist.fullName}");
            }
            break;

          case EState.Cooking:
            // Handled by MonitorMixOperation coroutine
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Cooking - Waiting for mix operation for {chemist.fullName}, station={station.GUID}");
            break;
        }
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.ActiveMinPass: Failed for chemist: {__instance.chemist?.fullName ?? "null"}, error: {e}");
        Disable(__instance);
        return false;
      }
    }
  }
}