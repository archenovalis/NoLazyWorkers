using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using System.Collections;
using UnityEngine;

using static NoLazyWorkers.NoLazyUtilities;
namespace NoLazyWorkers.Chemists
{
  public static class ChemistExtensions
  {
    public static ItemField GetMixerItemForProductSlot(MixingStation station)
    {
      if (station == null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Warning($"GetMixerItemForProductSlot: Product slot item is not a ProductDefinition for station={station?.GUID}");
        return null;
      }

      // Get the product from the product slot
      var productInSlot = station.ProductSlot.ItemInstance?.Definition as ProductDefinition;
      if (productInSlot == null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Warning($"GetMixerItemForProductSlot: Product slot {station.ProductSlot.ItemInstance?.Definition} item is not a ProductDefinition for station={station?.GUID}");
        return null;
      }

      // Get the routes for the station
      if (!MixingStationExtensions.MixingRoutes.TryGetValue(station.GUID, out var routes) || routes == null || routes.Count == 0)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"GetMixerItemForProductSlot: No routes defined for station={station.GUID}");
        return null;
      }
      // Find the first route where the product matches
      var matchingRoute = routes.FirstOrDefault(route =>
          route.Product?.SelectedItem != null &&
          route.Product.SelectedItem == productInSlot);
      if (matchingRoute == null)
      {
        if (DebugLogs.All || DebugLogs.MixingStation)
          MelonLogger.Msg($"GetMixerItemForProductSlot: No route matches product={productInSlot.Name} for station={station.GUID}");
        return null;
      }
      // Return the mixerItem from the matching route
      if (DebugLogs.All || DebugLogs.MixingStation)
        MelonLogger.Msg($"GetMixerItemForProductSlot: Found mixerItem={matchingRoute.MixerItem.SelectedItem?.Name ?? "null"} for product={productInSlot.Name} in station={station.GUID}");
      return matchingRoute.MixerItem;
    }

    public static ItemInstance GetItemInSupply(this Chemist chemist, MixingStation station, string id)
    {
      ObjectField supply = MixingStationExtensions.Supply[station.GUID];

      List<ItemSlot> list = [];
      BuildableItem supplyEntity = supply.SelectedObject;
      if (supplyEntity != null && chemist.behaviour.Npc.Movement.CanGetTo(supplyEntity as ITransitEntity, 1f))
      {
        list.AddRange((supplyEntity as ITransitEntity).OutputSlots);
        for (int i = 0; i < list.Count; i++)
        {
          if (list[i].Quantity > 0 && list[i].ItemInstance.ID.ToLower() == id.ToLower())
          {
            return list[i].ItemInstance;
          }
        }
      }
      return null;
    }
  }

  // Chemist
  [HarmonyPatch(typeof(Chemist))]
  public class ChemistPatch
  {
    [HarmonyPatch("GetMixingStationsReadyToStart")]
    [HarmonyPrefix]
    static bool Prefix(Chemist __instance, ref List<MixingStation> __result)
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
            ObjectField supply = MixingStationExtensions.Supply[station.GUID];
            if (!canStartMix && supply?.SelectedObject != null)
            {
              ConfigurationExtensions.NPCSupply[__instance.GUID] = supply;
              ItemInstance targetItem = mixerItem?.SelectedItem?.GetDefaultInstance();
              if (mixerItem.SelectedItem != null)
              {
                targetItem = mixerItem.SelectedItem.GetDefaultInstance();
              }
              hasSufficientItems = HasSufficientItems(__instance, threshold, targetItem);
              canRestock = station.OutputSlot.Quantity == 0 &&
                           station.ProductSlot.Quantity >= threshold &&
                           hasSufficientItems;
            }
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID}, Supply={supply?.SelectedObject?.GUID}, IsInUse={station.GetComponent<IUsable>().IsInUse}, CurrentMixOperation={station.CurrentMixOperation != null}, canStartMix={canStartMix}, canRestock={canRestock}, mixQuantity={mixQuantity}, productQuantity={station.ProductSlot.Quantity}, outputQuantity={station.OutputSlot.Quantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}{(canRestock ? $", hasSufficientItems={hasSufficientItems}" : "")}");
            if (canStartMix || canRestock)
            {
              list.Add(station);
            }
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

    public static ItemDefinition GetAnyMixer(ITransitEntity supply, float threshold, string preferredId = null)
    {
      if (supply == null || supply.OutputSlots == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning("GetAnyMixer: Supply or OutputSlots is null");
        return null;
      }

      var validItems = NetworkSingleton<ProductManager>.Instance.ValidMixIngredients;
      var slots = supply.OutputSlots
          .Where(s => s?.ItemInstance != null &&
                      validItems.Any(v => v.ID.ToLower() == s.ItemInstance.ID.ToLower()) &&
                      s.Quantity >= threshold);

      if (preferredId != null)
      {
        var preferredSlot = slots.FirstOrDefault(s => s.ItemInstance.ID.ToLower() == preferredId.ToLower());
        if (preferredSlot != null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"GetAnyMixer: Selected preferred item {preferredId} with quantity={preferredSlot.Quantity}");
          return preferredSlot.ItemInstance.definition;
        }
      }

      var firstSlot = slots.FirstOrDefault();
      if (firstSlot != null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"GetAnyMixer: Selected first available item {firstSlot.ItemInstance.ID} with quantity={firstSlot.Quantity}");
      }
      return firstSlot?.ItemInstance?.definition;
    }

    public static bool HasSufficientItems(Chemist chemist, float threshold, ItemInstance item)
    {
      return GetAmountInInventoryAndSupply(chemist, item?.definition) >= threshold;
    }
  }

  [HarmonyPatch(typeof(ChemistConfiguration))]
  public class ChemistConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, [typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(Chemist)])]
    static void Postfix(ChemistConfiguration __instance)
    {
      try
      {
        Chemist chemist = __instance.chemist;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistConfigurationPatch: Failed, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(StartMixingStationBehaviour))]
  public class StartMixingStationBehaviourPatch
  {
    private static readonly Dictionary<StartMixingStationBehaviour, StateData> states = [];

    private class StateData
    {
      public Coroutine WalkToSuppliesRoutine { get; set; }
      public Coroutine GrabRoutine { get; set; }
      public ItemDefinition TargetMixer { get; set; }
      public bool ClearMixerSlot { get; set; }
      public int QuantityToFetch { get; set; }
      public EState CurrentState { get; set; }
      public ObjectField LastSupply { get; set; }
      public bool CookPending { get; set; }
      public bool Any { get; set; }
      public float LastStateChangeTime { get; set; }
    }

    public enum EState
    {
      Idle,
      WalkingToSupplies,
      GrabbingSupplies,
      WalkingToStation,
      Cooking
    }

    private static bool IsStationValid(StartMixingStationBehaviour __instance, MixingStation station, ItemField mixerItem, ObjectField mixerSupply, Chemist chemist, out string reason)
    {
      reason = "";
      try
      {
        if (station == null || chemist == null || mixerItem == null || mixerSupply == null)
        {
          reason = $"Invalid components: station={station != null}, chemist={chemist != null}, mixerItem={mixerItem != null}, mixerSupply={mixerSupply != null}";
          return false;
        }

        if (station.ProductSlot == null || station.MixerSlot == null || station.OutputSlot == null)
        {
          reason = $"Invalid station slots: ProductSlot={station.ProductSlot != null}, MixerSlot={station.MixerSlot != null}, OutputSlot={station.OutputSlot != null}";
          return false;
        }

        float threshold = MixingStationExtensions.Config[station.GUID]?.StartThrehold.Value ?? 0f;
        int mixQuantity = station.MixerSlot.Quantity;
        int productQuantity = station.ProductSlot.Quantity;
        int outputQuantity = station.OutputSlot.Quantity;
        bool productValid = station.ProductSlot.ItemInstance != null && station.ProductSlot.ItemInstance.Definition is ProductDefinition;

        // Check canStartMix
        bool canStartMix = mixQuantity >= threshold && productQuantity >= threshold && outputQuantity == 0 && productValid;

        // Check canRestock
        bool canRestock = false;
        if (!canStartMix && productValid && outputQuantity == 0 && productQuantity >= threshold)
        {
          if (mixerItem.SelectedItem != null)
          {
            ItemInstance targetItem = mixerItem.SelectedItem.GetDefaultInstance();
            int inventoryCount = chemist.Inventory._GetItemAmount(mixerItem.SelectedItem.ID);
            int supplyCount = mixerSupply.SelectedObject != null ? GetAmountInSupply(chemist, targetItem) : 0;
            bool hasSufficientItems = targetItem != null && (inventoryCount + supplyCount) >= threshold;
            canRestock = hasSufficientItems;
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsStationValid: canRestock check: inventoryCount={inventoryCount}, supplyCount={supplyCount}, hasSufficientItems={hasSufficientItems}");
          }
        }

        bool isValid = canStartMix || canRestock;
        reason = isValid ? "Valid" : $"canStartMix={canStartMix} (mixQuantity={mixQuantity}, productQuantity={productQuantity}, outputQuantity={outputQuantity}, threshold={threshold}, productValid={productValid}), canRestock={canRestock}";

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsStationValid: Station={station.GUID}, chemist={chemist.fullName}, isValid={isValid}, reason={reason}");
        return isValid;
      }
      catch (Exception e)
      {
        reason = $"Exception: {e.Message}";
        MelonLogger.Error($"StartMixingStationBehaviourPatch.IsStationValid: Failed for chemist: {chemist?.fullName ?? "null"}, station: {station?.GUID}, error: {e}");
        return false;
      }
    }

    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(StartMixingStationBehaviour __instance)
    {
      try
      {
        // Initialize state if not already present
        /*         if (!states.ContainsKey(__instance))
                { */
        states[__instance] = new StateData
        {
          CurrentState = EState.Idle,
          WalkToSuppliesRoutine = null,
          GrabRoutine = null,
          TargetMixer = null,
          ClearMixerSlot = false,
          QuantityToFetch = 0,
          LastSupply = null,
          CookPending = false,
          Any = false
        };
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.Awake: Initialized state for {__instance.chemist?.fullName ?? "null"}");
        /* } */
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

        Chemist chemist = __instance.chemist;
        MixingStation station = __instance.targetStation;

        if (station == null || chemist == null)
        {
          Disable(__instance);
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Station or Chemist null for chemist: {chemist?.fullName ?? "null"}");
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

        // Early validation
        MixingStationConfiguration config = MixingStationExtensions.Config[station.GUID];
        ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];
        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        if (config == null || mixerSupply == null || mixerItem == null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Invalid config (config={config != null}, mixerSupply={mixerSupply != null}, mixerItem={mixerItem != null}) for station: {station?.GUID}");
          Disable(__instance);
          return false;
        }

        if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
        {
          if (state.CurrentState != EState.Idle)
          {
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Station invalid (reason={reason}), resetting to Idle");
            Disable(__instance);
          }
          return false;
        }

        // Debounce state changes
        float timeSinceLastChange = Time.time - state.LastStateChangeTime;
        if (timeSinceLastChange < 0.5f)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Debouncing state change, timeSinceLastChange={timeSinceLastChange}");
          return false;
        }

        // Update last state change time when changing state
        void UpdateState(EState newState)
        {
          state.CurrentState = newState;
          state.LastStateChangeTime = Time.time;
        }

        __instance.onEnd.RemoveAllListeners();
        __instance.onEnd.AddListener(() => Disable(__instance));

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: State={state.CurrentState}, station={station.GUID}, chemist={chemist.fullName}, productItem={(station.ProductSlot.ItemInstance != null ? station.ProductSlot.ItemInstance.ID : "null")}");

        switch (state.CurrentState)
        {
          case EState.Idle:
            int productQuantity = station.ProductSlot.Quantity;
            int outputQuantity = station.OutputSlot.Quantity;
            int mixQuantity = station.MixerSlot.Quantity;
            float threshold = config.StartThrehold.Value;

            if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out reason))
            {
              Disable(__instance);
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Station invalid (reason={reason}), disabling");
              return false;
            }

            ConfigurationExtensions.NPCSupply[chemist.GUID] = mixerSupply;
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Copied station supply {mixerSupply.SelectedObject?.name} to chemist");

            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - mixQuantity={mixQuantity}, productQuantity={productQuantity}, outputQuantity={outputQuantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}, cookPending={state.CookPending}, mixOperation={(station.CurrentMixOperation != null)}");

            if (!state.CookPending && station.CurrentMixOperation == null && mixQuantity >= threshold && productQuantity >= threshold && outputQuantity == 0)
            {
              // Check inventory for additional items
              int inventoryCount = mixerItem.SelectedItem != null ? chemist.Inventory._GetItemAmount(mixerItem.SelectedItem.ID) : 0;
              if (inventoryCount > 0 && IsAtStation(__instance))
              {
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Found {inventoryCount} inventory items, attempting to insert for {chemist.fullName}");
                state.TargetMixer = mixerItem.SelectedItem;
                int inserted = InsertItemsFromInventory(__instance, state, station);
                if (inserted > 0)
                {
                  mixQuantity = station.GetMixQuantity();
                  if (mixQuantity >= threshold)
                  {
                    UpdateState(EState.Cooking);
                    state.CookPending = true;
                    __instance.StartCook();
                    if (DebugLogs.All || DebugLogs.Chemist)
                      MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Inserted {inserted} items, starting cook with mixQuantity={mixQuantity} for {chemist.fullName}");
                    return false;
                  }
                }
                else
                {
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Failed to insert {inventoryCount} inventory items, disabling for {chemist.fullName}");
                  Disable(__instance);
                  return false;
                }
              }

              if (mixQuantity >= productQuantity && mixQuantity >= threshold)
              {
                if (IsAtStation(__instance))
                {
                  UpdateState(EState.Cooking);
                  state.CookPending = true;
                  __instance.StartCook();
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Starting cook for {chemist.fullName}");
                }
                else
                {
                  UpdateState(EState.WalkingToStation);
                  __instance.SetDestination(GetStationAccessPoint(__instance), true);
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Walking to station for {chemist.fullName}");
                }
              }
              else
              {
                bool needsMoreMixer = mixQuantity < productQuantity;
                if (needsMoreMixer && mixerItem.SelectedItem != null)
                {
                  state.TargetMixer = mixerItem.SelectedItem;
                  int additionalMixerNeeded = productQuantity - mixQuantity;
                  int inventoryCountCheck = chemist.Inventory._GetItemAmount(mixerItem.SelectedItem.ID);
                  int supplyCount = GetAmountInSupply(chemist, mixerItem.SelectedItem.GetDefaultInstance());
                  int totalAvailableMixer = mixQuantity + inventoryCountCheck + supplyCount;
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Checking mixer: mixQuantity={mixQuantity}, productQuantity={productQuantity}, additionalMixerNeeded={additionalMixerNeeded}, inventoryCount={inventoryCountCheck}, supplyCount={supplyCount}, totalAvailableMixer={totalAvailableMixer}");

                  if (inventoryCountCheck >= additionalMixerNeeded)
                  {
                    if (DebugLogs.All || DebugLogs.Chemist)
                      MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Sufficient inventory items ({inventoryCountCheck} >= {additionalMixerNeeded}), proceeding to station");
                    HandleInventorySufficient(__instance, state, station, inventoryCountCheck);
                    return false;
                  }
                  else if (totalAvailableMixer > mixQuantity)
                  {
                    PrepareToFetchItem(__instance, state, mixerItem, productQuantity, mixQuantity, threshold, mixerSupply, station);
                    return false;
                  }
                  else if (mixQuantity >= threshold)
                  {
                    if (DebugLogs.All || DebugLogs.Chemist)
                      MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Insufficient mixer items ({totalAvailableMixer} < {productQuantity}), cooking with available mixQuantity={mixQuantity}");
                    if (IsAtStation(__instance))
                    {
                      UpdateState(EState.Cooking);
                      state.CookPending = true;
                      __instance.StartCook();
                      if (DebugLogs.All || DebugLogs.Chemist)
                        MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Starting cook for {chemist.fullName}");
                    }
                    else
                    {
                      UpdateState(EState.WalkingToStation);
                      __instance.SetDestination(GetStationAccessPoint(__instance), true);
                      if (DebugLogs.All || DebugLogs.Chemist)
                        MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Walking to station for {chemist.fullName}");
                    }
                  }
                  else
                  {
                    if (DebugLogs.All || DebugLogs.Chemist)
                      MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Insufficient mixer items (mixQuantity={mixQuantity} < threshold={threshold}), disabling");
                    Disable(__instance);
                  }
                }
                else
                {
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - No valid mixer item or sufficient mixQuantity, disabling for {chemist.fullName}");
                  Disable(__instance);
                }
              }
            }
            else
            {
              bool needsMoreMixer = mixQuantity < productQuantity;
              if (needsMoreMixer && mixerItem.SelectedItem != null)
              {
                state.TargetMixer = mixerItem.SelectedItem;
                int additionalMixerNeeded = productQuantity - mixQuantity;
                int inventoryCountCheck = chemist.Inventory._GetItemAmount(mixerItem.SelectedItem.ID);
                int supplyCount = GetAmountInSupply(chemist, mixerItem.SelectedItem.GetDefaultInstance());
                int totalAvailableMixer = mixQuantity + inventoryCountCheck + supplyCount;
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Preparing to fetch: mixQuantity={mixQuantity}, productQuantity={productQuantity}, additionalMixerNeeded={additionalMixerNeeded}, inventoryCount={inventoryCountCheck}, supplyCount={supplyCount}, totalAvailableMixer={totalAvailableMixer}");

                if (inventoryCountCheck >= additionalMixerNeeded)
                {
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Sufficient inventory items ({inventoryCountCheck} >= {additionalMixerNeeded}), proceeding to station");
                  HandleInventorySufficient(__instance, state, station, inventoryCountCheck);
                }
                else if (totalAvailableMixer > mixQuantity)
                {
                  PrepareToFetchItem(__instance, state, mixerItem, productQuantity, mixQuantity, threshold, mixerSupply, station);
                }
                else if (mixQuantity >= threshold)
                {
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Insufficient mixer items ({totalAvailableMixer} < {productQuantity}), cooking with available mixQuantity={mixQuantity}");
                  if (IsAtStation(__instance))
                  {
                    UpdateState(EState.Cooking);
                    state.CookPending = true;
                    __instance.StartCook();
                  }
                  else
                  {
                    UpdateState(EState.WalkingToStation);
                    __instance.SetDestination(GetStationAccessPoint(__instance), true);
                  }
                }
                else
                {
                  if (DebugLogs.All || DebugLogs.Chemist)
                    MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Insufficient mixer items (mixQuantity={mixQuantity} < threshold={threshold}), disabling");
                  Disable(__instance);
                }
              }
              else
              {
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - No action needed, reason={(state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : mixQuantity >= productQuantity ? "sufficient mixQuantity" : "no mixer item")}");
                Disable(__instance);
              }
            }
            break;

          case EState.WalkingToSupplies:
            if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out reason))
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Station invalid in WalkingToSupplies (reason={reason}), resetting to Idle");
              Disable(__instance);
              return false;
            }
            // Handled by WalkRoutine
            break;

          case EState.GrabbingSupplies:
            if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out reason))
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Station invalid in GrabbingSupplies (reason={reason}), resetting to Idle");
              Disable(__instance);
              return false;
            }
            // Handled in GrabRoutine
            break;

          case EState.WalkingToStation:
            if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out reason))
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Station invalid in WalkingToStation (reason={reason}), resetting to Idle");
              Disable(__instance);
              return false;
            }
            if (IsAtStation(__instance))
            {
              int inventoryCount = state.TargetMixer != null ? chemist.Inventory._GetItemAmount(state.TargetMixer.ID) : 0;
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - At station, inventory has {inventoryCount} of {state.TargetMixer?.ID ?? "null"} for {chemist.fullName}");

              int insertedQuantity = InsertItemsFromInventory(__instance, state, station);
              if (insertedQuantity > 0)
              {
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Inserted {insertedQuantity} items to station for {chemist.fullName}");
              }
              else if (inventoryCount > 0)
              {
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Failed to insert {inventoryCount} inventory items, disabling for {chemist.fullName}");
                Disable(__instance);
                return false;
              }

              // Recalculate mixQuantity after insertion
              int updatedMixQuantity = station.GetMixQuantity();
              threshold = config.StartThrehold.Value;
              if (!state.CookPending && station.CurrentMixOperation == null && updatedMixQuantity >= threshold && station.ProductSlot.Quantity >= threshold && station.OutputSlot.Quantity == 0)
              {
                UpdateState(EState.Cooking);
                state.CookPending = true;
                __instance.StartCook();
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Arrived at station, starting cook for {chemist.fullName}, updatedMixQuantity={updatedMixQuantity}");
              }
              else
              {
                UpdateState(EState.Idle);
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Arrived at station, returning to Idle for {chemist.fullName}, reason={(insertedQuantity == 0 ? "no items inserted" : state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : updatedMixQuantity < threshold ? "low mixQuantity" : "other")}, updatedMixQuantity={updatedMixQuantity}, productQuantity={station.ProductSlot.Quantity}, outputQuantity={station.OutputSlot.Quantity}, threshold={threshold}");
              }
            }
            else
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Still walking to station for {chemist.fullName}");
            }
            break;

          case EState.Cooking:
            if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out reason))
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Station invalid in Cooking (reason={reason}), resetting to Idle");
              Disable(__instance);
              return false;
            }
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

    private static void PrepareToFetchItem(StartMixingStationBehaviour __instance, StateData state, ItemField mixerItem, int productQuantity, int mixQuantity, float threshold, ObjectField mixerSupply, MixingStation station)
    {
      Chemist chemist = __instance.chemist;
      if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Station invalid (reason={reason}), disabling");
        Disable(__instance);
        return;
      }

      state.ClearMixerSlot = false;
      state.Any = mixerItem.SelectedItem == null;

      int currentMixerQuantity = station.MixerSlot.Quantity;
      int maxCookQuantity = Mathf.Min(productQuantity, currentMixerQuantity);
      if (currentMixerQuantity >= maxCookQuantity && currentMixerQuantity >= threshold)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Mixer slot sufficient for cooking (current={currentMixerQuantity}, product={productQuantity}, threshold={threshold}), proceeding to station");
        state.CurrentState = EState.WalkingToStation;
        __instance.SetDestination(GetStationAccessPoint(__instance), true);
        return;
      }

      int additionalMixerNeeded = productQuantity - currentMixerQuantity;
      state.QuantityToFetch = additionalMixerNeeded;
      state.TargetMixer = mixerItem.SelectedItem;

      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: mixQuantity={mixQuantity}, currentMixerQuantity={currentMixerQuantity}, productQuantity={productQuantity}, threshold={threshold}, targetMixerId={state.TargetMixer?.ID ?? "null"}, quantityToFetch={state.QuantityToFetch}, any={state.Any}");

      // Check inventory for mixer items
      int inventoryCount = 0;
      if (state.TargetMixer != null)
      {
        inventoryCount = chemist.Inventory._GetItemAmount(state.TargetMixer.ID);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Found {inventoryCount} of {state.TargetMixer.ID} in inventory for {chemist?.fullName}");

        if (inventoryCount >= additionalMixerNeeded)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Sufficient items in inventory ({inventoryCount} >= {additionalMixerNeeded}), proceeding to station");
          HandleInventorySufficient(__instance, state, station, inventoryCount);
          return;
        }
        else if (inventoryCount > 0)
        {
          state.QuantityToFetch -= inventoryCount;
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Using {inventoryCount} from inventory, still need {state.QuantityToFetch} from supply");
        }
      }
      else
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: TargetMixer is null, cannot fetch items for {chemist?.fullName}");
        Disable(__instance);
        return;
      }

      // Proceed to fetch from supply if needed
      ITransitEntity supplyEntity = mixerSupply?.SelectedObject as ITransitEntity;
      if (supplyEntity == null || supplyEntity.OutputSlots == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Invalid supply entity for {chemist?.fullName}");
        if (currentMixerQuantity + inventoryCount >= threshold)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: No supply, but sufficient mixer+inventory ({currentMixerQuantity + inventoryCount} >= {threshold}), proceeding to station");
          HandleInventorySufficient(__instance, state, station, inventoryCount);
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Insufficient items (mixer={currentMixerQuantity}, inventory={inventoryCount}, threshold={threshold}), disabling");
          Disable(__instance);
        }
        return;
      }

      int fetchable = 0;
      int supplyCount = GetAmountInSupply(chemist, state.TargetMixer.GetDefaultInstance());
      fetchable = supplyCount < state.QuantityToFetch ? supplyCount : state.QuantityToFetch;
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Supply check: supplyCount={supplyCount}, fetchable={fetchable}");

      if (fetchable <= 0)
      {
        if (currentMixerQuantity + inventoryCount >= threshold)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: No fetchable mixer, but sufficient mixer+inventory ({currentMixerQuantity + inventoryCount} >= {threshold}), proceeding to station");
          HandleInventorySufficient(__instance, state, station, inventoryCount);
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: No fetchable mixer and insufficient items (mixer={currentMixerQuantity}, inventory={inventoryCount}, threshold={threshold}) for {chemist?.fullName}");
          Disable(__instance);
        }
        return;
      }

      state.QuantityToFetch = fetchable;
      if (IsAtSupplies(__instance))
      {
        state.CurrentState = EState.GrabbingSupplies;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: At supplies, grabbing {state.TargetMixer.ID}");
        GrabItem(__instance, state);
      }
      else
      {
        state.CurrentState = EState.WalkingToSupplies;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Walking to supplies for {state.TargetMixer.ID}");
        WalkToSupplies(__instance, state);
      }
    }

    private static void HandleInventorySufficient(StartMixingStationBehaviour __instance, StateData state, MixingStation station, int inventoryCount)
    {
      Chemist chemist = __instance.chemist;
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.HandleInventorySufficient: Processing {inventoryCount} inventory items for {chemist?.fullName}, targetMixer={state.TargetMixer?.ID ?? "null"}");

      if (state.TargetMixer == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.HandleInventorySufficient: TargetMixer is null, disabling for {chemist?.fullName}");
        Disable(__instance);
        return;
      }

      if (IsAtStation(__instance))
      {
        int inserted = InsertItemsFromInventory(__instance, state, station);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.HandleInventorySufficient: Inserted {inserted} items from inventory for {chemist?.fullName}");

        // Recheck station validity after insertion
        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];
        if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.HandleInventorySufficient: Station invalid after insertion (reason={reason}), disabling");
          Disable(__instance);
          return;
        }

        state.CurrentState = EState.Idle;
      }
      else
      {
        state.CurrentState = EState.WalkingToStation;
        __instance.SetDestination(GetStationAccessPoint(__instance), true);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.HandleInventorySufficient: Walking to station with {inventoryCount} inventory items for {chemist?.fullName}");
      }
    }

    [HarmonyPatch("StartCook")]
    [HarmonyPrefix]
    static bool StartCookPrefix(StartMixingStationBehaviour __instance)
    {
      try
      {
        // Initialize state if missing
        if (!states.TryGetValue(__instance, out var state))
        {
          states[__instance] = new StateData { CurrentState = EState.Idle };
          state = states[__instance];
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.StartCook: State missing, initialized for {__instance.chemist?.fullName ?? "null"}");
        }

        Chemist chemist = __instance.chemist;
        MixingStation station = __instance.targetStation;
        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];

        if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.StartCook: Station invalid (reason={reason}), disabling");
          Disable(__instance);
          return false;
        }

        if (state.CurrentState != EState.Cooking || !state.CookPending || station.CurrentMixOperation != null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.StartCook: Invalid state {state.CurrentState}, cookPending={state.CookPending}, mixOperation={(station.CurrentMixOperation != null)} for {chemist?.fullName ?? "null"}");
          Disable(__instance);
          return false;
        }

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.StartCook: Starting cook for {chemist?.fullName ?? "null"}");
        state.CurrentState = EState.Cooking;
        return true;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.StartCook: Failed for chemist: {__instance.chemist?.fullName ?? "null"}, error: {e}");
        Disable(__instance);
        return false;
      }
    }

    private static bool IsAtSupplies(StartMixingStationBehaviour __instance)
    {
      Chemist chemist = __instance.chemist;
      ChemistConfiguration config = chemist.configuration;
      ObjectField supply = ConfigurationExtensions.NPCSupply[chemist.GUID];
      bool atSupplies = config != null && supply != null && supply.SelectedObject != null &&
           NavMeshUtility.IsAtTransitEntity(supply.SelectedObject as ITransitEntity, chemist, 0.4f);
      if (DebugLogs.All || DebugLogs.Chemist)
      {
        Vector3 chemistPos = chemist.transform.position;
        Vector3 supplyPos = supply?.SelectedObject != null ? supply.SelectedObject.transform.position : Vector3.zero;
        float distance = Vector3.Distance(chemistPos, supplyPos);
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsAtSupplies: Result={atSupplies}, chemist={chemist?.fullName ?? "null"}, ChemistPos={chemistPos}, SupplyPos={supplyPos}, Distance={distance}");
      }
      return atSupplies;
    }

    private static void WalkToSupplies(StartMixingStationBehaviour __instance, StateData state)
    {
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Started");
      Chemist chemist = __instance.chemist;
      if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
      {
        Disable(__instance);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Supply not found for {chemist?.fullName ?? "null"}");
        return;
      }

      ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;
      if (!chemist.Movement.CanGetTo(supplyEntity, 1f))
      {
        Disable(__instance);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Cannot reach supply for {chemist?.fullName ?? "null"}");
        return;
      }

      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkToSupplies: {__instance} Walking to supply {supplyEntity.Name} for {chemist?.fullName} {state.CurrentState}");

      state.WalkToSuppliesRoutine = (Coroutine)MelonCoroutines.Start(WalkRoutine(__instance, supplyEntity, state));
    }

    private static void GrabItem(StartMixingStationBehaviour __instance, StateData state)
    {
      Chemist chemist = __instance.chemist;
      MixingStation station = __instance.targetStation;
      try
      {
        if (station.OutputSlot.Quantity > 0 || state.TargetMixer == null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Output quantity {station.OutputSlot.Quantity} > 0, disabling for {chemist?.fullName}");
          Disable(__instance);
          return;
        }

        if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
        {
          Disable(__instance);
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Supply not found for {chemist?.fullName ?? "null"}");
          return;
        }

        ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;

        // Only clear MixerSlot if the current item doesn't match the target
        if (state.ClearMixerSlot && station.MixerSlot.ItemInstance != null &&
            station.MixerSlot.ItemInstance.ID != state.TargetMixer.ID)
        {
          int currentQuantity = station.MixerSlot.Quantity;
          chemist.Inventory.InsertItem(station.MixerSlot.ItemInstance.GetCopy(currentQuantity));
          station.MixerSlot.SetQuantity(0);
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Cleared MixerSlot ({currentQuantity} items returned to inventory) for {chemist?.fullName}");
        }

        // Fetch new items
        List<ItemSlot> slots = (supplyEntity.OutputSlots ?? Enumerable.Empty<ItemSlot>())
            .Concat(supplyEntity.InputSlots ?? Enumerable.Empty<ItemSlot>())
            .Where(s => s?.ItemInstance != null && s.Quantity > 0 && s.ItemInstance.ID.ToLower() == state.TargetMixer.ID.ToLower())
            .Distinct()
            .ToList();

        if (!slots.Any())
        {
          Disable(__instance);
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: No slots containing {state.TargetMixer.ID} in supply for {chemist?.fullName}");
          return;
        }

        int totalAvailable = slots.Sum(s => s.Quantity);
        int quantityToFetch = Mathf.Min(state.QuantityToFetch, totalAvailable);

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Availability: {totalAvailable}/{state.QuantityToFetch} for {state.TargetMixer.ID}");
        if (quantityToFetch <= 0)
        {
          Disable(__instance);
          return;
        }

        // Deduct quantity across slots
        int remainingToFetch = quantityToFetch;
        foreach (ItemSlot slot in slots)
        {
          if (slot == null) continue;
          int amountToTake = Mathf.Min(slot.Quantity, remainingToFetch);
          if (amountToTake > 0)
          {
            // Insert into inventory
            chemist.Inventory.InsertItem(slot.ItemInstance.GetCopy(amountToTake));
            slot.ChangeQuantity(-amountToTake, false);
            remainingToFetch -= amountToTake;

            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Took {amountToTake} of {state.TargetMixer.ID} from slot {slot.GetHashCode()}, remainingToFetch={remainingToFetch} for {chemist?.fullName}");
          }
          if (remainingToFetch <= 0)
            break;
        }

        if (remainingToFetch > 0 && (DebugLogs.All || DebugLogs.Chemist))
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Could not fetch full quantity, still need {remainingToFetch} of {state.TargetMixer.ID} for {chemist?.fullName}");
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Grabbed {quantityToFetch - remainingToFetch} of {state.TargetMixer.ID} into inventory for {chemist?.fullName}");

        state.GrabRoutine = (Coroutine)MelonCoroutines.Start(GrabRoutine(__instance, state));
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.GrabItem: Failed for {chemist?.fullName}, error: {e}");
        Disable(__instance);
      }
    }

    private static IEnumerator WalkRoutine(StartMixingStationBehaviour __instance, ITransitEntity supply, StateData state)
    {
      Chemist chemist = __instance.chemist;
      MixingStation station = __instance.targetStation;
      Vector3 startPos = chemist.transform.position;
      __instance.SetDestination(supply, true);
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkRoutine: Set destination for {chemist?.fullName}, IsMoving={chemist.Movement.IsMoving}");

      yield return new WaitForSeconds(0.1f);

      float timeout = 10f;
      float elapsed = 0f;
      while (chemist.Movement.IsMoving && elapsed < timeout)
      {
        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];
        if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkRoutine: Station invalid during walk (reason={reason}), disabling");
          Disable(__instance);
          yield break;
        }

        yield return null;
        elapsed += Time.deltaTime;
      }

      if (elapsed >= timeout)
      {
        Disable(__instance);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkRoutine: Timeout walking to supply for {chemist?.fullName}");
      }

      state.WalkToSuppliesRoutine = null;
      state.CurrentState = EState.Idle;
      if (DebugLogs.All || DebugLogs.Chemist)
      {
        bool atSupplies = IsAtSupplies(__instance);
        Vector3 chemistPos = chemist.transform.position;
        Vector3 supplyPos = supply != null ? ((BuildableItem)supply).transform.position : Vector3.zero;
        float distanceMoved = Vector3.Distance(startPos, chemistPos);
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkRoutine: Completed walk to supply, reverting to Idle for {chemist?.fullName}. AtSupplies={atSupplies}, ChemistPos={chemistPos}, SupplyPos={supplyPos}, DistanceMoved={distanceMoved}, Elapsed={elapsed}, Timeout={elapsed >= timeout}, IsMoving={chemist.Movement.IsMoving}");
      }
    }

    private static IEnumerator GrabRoutine(StartMixingStationBehaviour __instance, StateData state)
    {
      Chemist chemist = __instance.chemist;
      MixingStation station = __instance.targetStation;
      ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
      ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];

      // Initial validation
      if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Station invalid before grab (reason={reason}), disabling");
        Disable(__instance);
        yield break;
      }

      yield return new WaitForSeconds(0.1f);

      try
      {
        if (chemist.Avatar?.Anim != null)
        {
          chemist.Avatar.Anim.ResetTrigger("GrabItem");
          chemist.Avatar.Anim.SetTrigger("GrabItem");
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Triggered GrabItem animation for {chemist?.fullName}");
        }
        else if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabRoutine: Animator missing for {chemist?.fullName}, skipping animation");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.GrabRoutine: Failed for {chemist?.fullName}, error: {e}");
        Disable(__instance);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabRoutine: Reverted to Idle due to error for {chemist?.fullName}");
        yield break;
      }

      yield return new WaitForSeconds(0.1f);

      // Re-check after grab
      if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out reason))
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Station invalid after grab (reason={reason}), disabling");
        Disable(__instance);
        yield break;
      }

      state.GrabRoutine = null;
      state.CurrentState = EState.WalkingToStation;
      __instance.SetDestination(GetStationAccessPoint(__instance), true);
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Grab complete, walking to station for {chemist?.fullName}, productQuantity={station.ProductSlot.Quantity}");
    }

    private static int InsertItemsFromInventory(StartMixingStationBehaviour __instance, StateData state, MixingStation station)
    {
      Chemist chemist = __instance.chemist;
      ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
      ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];

      if (!IsStationValid(__instance, station, mixerItem, mixerSupply, chemist, out string reason))
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Station invalid (reason={reason}), disabling");
        Disable(__instance);
        return 0;
      }

      int quantity = 0;
      try
      {
        if (state.TargetMixer == null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: TargetMixer is null for {chemist?.fullName}");
          Disable(__instance);
          return 0;
        }

        if (station.MixerSlot == null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: MixerSlot is null for station {station?.ObjectId}");
          Disable(__instance);
          return 0;
        }

        quantity = chemist.Inventory._GetItemAmount(state.TargetMixer.ID);
        if (quantity <= 0)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: No items of {state.TargetMixer.ID} in inventory for {chemist?.fullName}, expected at least 1");
          Disable(__instance);
          return 0;
        }

        ItemInstance item = state.TargetMixer.GetDefaultInstance();
        if (item == null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to get default instance for {state.TargetMixer.ID}");
          Disable(__instance);
          return 0;
        }

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Attempting to insert {quantity} of {state.TargetMixer.ID} for {chemist?.fullName}, current MixerSlot quantity={station.MixerSlot.Quantity}");

        // Insert into MixerSlot
        int currentQuantity = station.MixerSlot.Quantity;
        station.MixerSlot.InsertItem(item.GetCopy(quantity));
        int newQuantity = station.MixerSlot.Quantity;

        if (newQuantity == currentQuantity)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to insert {quantity} of {state.TargetMixer.ID}, MixerSlot quantity unchanged for {chemist?.fullName}");
          Disable(__instance);
          return 0;
        }

        // Remove from inventory
        int quantityToRemove = quantity;
        List<(ItemSlot slot, int amount)> toRemove = [];
        foreach (ItemSlot slot in chemist.Inventory.ItemSlots)
        {
          if (slot?.ItemInstance != null && slot.ItemInstance.ID.ToLower() == state.TargetMixer.ID.ToLower() && slot.Quantity > 0)
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
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Removed {amount} of {state.TargetMixer.ID} from inventory slot for {chemist?.fullName}");
        }

        if (quantityToRemove > 0)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to remove {quantityToRemove} of {state.TargetMixer.ID}, reverting insertion for {chemist?.fullName}");
          station.MixerSlot.SetQuantity(currentQuantity); // Revert insertion
          Disable(__instance);
          return 0;
        }

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Successfully inserted {quantity} of {state.TargetMixer.ID}, MixerSlot quantity changed from {currentQuantity} to {newQuantity} for {chemist?.fullName}");

        return quantity;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed for {chemist?.fullName}, item={state.TargetMixer?.ID ?? "null"}, quantity={quantity}, error: {e}");
        Disable(__instance);
        return 0;
      }
    }

    private static void Disable(StartMixingStationBehaviour __instance)
    {
      if (states.TryGetValue(__instance, out var state))
      {
        if (state.WalkToSuppliesRoutine != null)
        {
          MelonCoroutines.Stop(state.WalkToSuppliesRoutine);
          state.WalkToSuppliesRoutine = null;
        }
        if (state.GrabRoutine != null)
        {
          MelonCoroutines.Stop(state.GrabRoutine);
          state.GrabRoutine = null;
        }

        state.TargetMixer = null;
        state.ClearMixerSlot = false;
        state.QuantityToFetch = 0;
        state.CookPending = false;
        state.CurrentState = EState.Idle;
        state.LastSupply = null;
        state.Any = false;
        state.LastStateChangeTime = 0f;
      }
      states.Remove(__instance);

      Chemist chemist = __instance.chemist;
      if (chemist != null)
      {
        chemist.Movement.Stop();
        if (chemist.Avatar?.Anim != null)
        {
          chemist.Avatar.Anim.ResetTrigger("GrabItem");
          chemist.Avatar.Anim.ResetTrigger("StartCook");
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.Disable: Reset animations for {chemist.fullName}");
        }
        chemist.Movement.SetDestination(chemist.transform.position);
      }

      __instance.Disable();
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.Disable: Disabled behaviour for {chemist?.fullName ?? "null"}");
    }

    private static bool IsAtStation(StartMixingStationBehaviour __instance)
    {
      bool atStation = __instance.targetStation != null &&
                         Vector3.Distance(__instance.chemist.transform.position, GetStationAccessPoint(__instance)) < 1f;
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsAtStation: Result={atStation}, chemist={__instance.chemist?.fullName ?? "null"}");
      return atStation;
    }

    private static Vector3 GetStationAccessPoint(StartMixingStationBehaviour __instance)
    {
      return __instance.targetStation ? (__instance.targetStation as ITransitEntity).AccessPoints[0].position : __instance.chemist.transform.position;
    }
  }

  [HarmonyPatch(typeof(Chemist), "GetMixStationsReadyToMove")]
  public class ChemistGetMixStationsReadyToMovePatch // full override
  {
    [HarmonyPrefix]
    static bool Prefix(Chemist __instance, ref List<MixingStation> __result)
    {
      try
      {
        List<MixingStation> list = [];
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Checking stations for {__instance?.name ?? "null"}, total stations={__instance.configuration.MixStations.Count}");

        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot.Quantity > 0)
          {
            ProductDefinition outputProduct = outputSlot.ItemInstance.Definition as ProductDefinition;
            MixingRoute matchingRoute = MixingStationExtensions.MixingRoutes[station.GUID].FirstOrDefault(route =>
                route.Product.SelectedItem == outputProduct);
            if (matchingRoute != null)
            {
              // Create a route for the matching product
              TransitRoute route = new(station, station);
              if (__instance.MoveItemBehaviour.IsTransitRouteValid(route, station.OutputSlot.ItemInstance.ID))
              {
                __instance.MoveItemBehaviour.Initialize(route, station.OutputSlot.ItemInstance);
                __instance.MoveItemBehaviour.Enable_Networked(null);
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Initialized MoveItemBehaviour for station={station.GUID}, product={outputProduct.Name}");
                return false;
              }
            }
            else if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Output to destination check.");

            if (__instance.MoveItemBehaviour.IsTransitRouteValid(
                (station.Configuration as MixingStationConfiguration).DestinationRoute,
                outputSlot.ItemInstance.ID))
            {
              // Fallback to existing destination route
              if (IsDestinationRouteValid(__instance, station, outputSlot))
                list.Add(station);
            }
          }
        }

        __result = list;
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Found {list.Count} stations ready to move for {__instance?.name ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistGetMixStationsReadyToMovePatch: Failed for chemist: {__instance?.name ?? "null"}, error: {e}");
        return false;
      }
    }

    private static bool IsDestinationRouteValid(Chemist chemist, MixingStation station, ItemSlot outputSlot)
    {
      var config = station.Configuration as MixingStationConfiguration;
      if (config?.DestinationRoute == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"ChemistGetMixStationsReadyToMovePatch: No valid destination route for station={station.GUID}");
        return false;
      }
      bool isValid = chemist.MoveItemBehaviour.IsTransitRouteValid(config.DestinationRoute, outputSlot.ItemInstance.ID);
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Destination route valid={isValid} for station={station.GUID}");
      return isValid;
    }
  }
}