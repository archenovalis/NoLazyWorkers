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

namespace NoLazyWorkers.Chemists
{
  public static class ChemistExtensions
  {
    public static ItemField GetMixerItemForProductSlot(MixingStation station)
    {
      if (station == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Warning($"GetMixerItemForProductSlot: Product slot item is not a ProductDefinition for station={station?.ObjectId.ToString() ?? "null"}");
        return null;
      }

      // Get the product from the product slot
      var productInSlot = station.ProductSlot.ItemInstance?.Definition as ProductDefinition;
      if (productInSlot == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Warning($"GetMixerItemForProductSlot: Product slot {station.ProductSlot.ItemInstance?.Definition} item is not a ProductDefinition for station={station?.ObjectId.ToString() ?? "null"}");
        return null;
      }

      // Get the routes for the station
      if (!MixingStationExtensions.MixingRoutes.TryGetValue(station.GUID, out var routes) || routes == null || routes.Count == 0)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"GetMixerItemForProductSlot: No routes defined for station={station.GUID}");
        return null;
      }
      // Find the first route where the product matches
      var matchingRoute = routes.FirstOrDefault(route =>
          route.Product?.SelectedItem != null &&
          route.Product.SelectedItem == productInSlot);
      if (matchingRoute == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
          MelonLogger.Msg($"GetMixerItemForProductSlot: No route matches product={productInSlot.Name} for station={station.GUID}");
        return null;
      }
      // Return the mixerItem from the matching route
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingStation)
        MelonLogger.Msg($"GetMixerItemForProductSlot: Found mixerItem={matchingRoute.MixerItem.SelectedItem?.Name ?? "null"} for product={productInSlot.Name} in station={station.GUID}");
      return matchingRoute.MixerItem;
    }

    public static ItemInstance GetItemInSupply(this Chemist chemist, MixingStation station, string id)
    {
      MixingStationConfiguration config = MixingStationExtensions.MixingConfig[station.GUID];
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Checking stations for {__instance?.name ?? "null"}, total stations={__instance.configuration.MixStations.Count}");

        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          if (!((IUsable)station).IsInUse && station.CurrentMixOperation == null)
          {
            if (!MixingStationExtensions.MixingConfig.TryGetValue(station.GUID, out var config))
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                MelonLogger.Warning($"ChemistPatch.GetMixingStationsReadyToStart: MixerConfig missing for station {station?.ObjectId.ToString() ?? "null"}");
              continue;
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
              /* else
              {
                string preferredId = mixerItem?.SelectedItem?.ID?.ToLower();
                ItemDefinition mixerDef = GetAnyMixer(supply.SelectedObject as ITransitEntity, threshold, preferredId);
                if (mixerDef == null)
                {
                  if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehaviorLogs)
                    MelonLogger.Warning($"No suitable mixer found for station {station.GUID}");
                  continue;
                }
                targetItem = mixerDef.GetDefaultInstance();
              } */
              hasSufficientItems = HasSufficientItems(__instance, threshold, targetItem);
              canRestock = station.OutputSlot.Quantity == 0 &&
                           station.ProductSlot.Quantity >= threshold &&
                           hasSufficientItems;
            }
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
              MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Station {station.GUID}, Supply={supply?.SelectedObject?.GUID}, IsInUse={station.GetComponent<IUsable>().IsInUse}, CurrentMixOperation={station.CurrentMixOperation != null}, canStartMix={canStartMix}, canRestock={canRestock}, mixQuantity={mixQuantity}, productQuantity={station.ProductSlot.Quantity}, outputQuantity={station.OutputSlot.Quantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}{(canRestock ? $", hasSufficientItems={hasSufficientItems}" : "")}");
            if (canStartMix || canRestock)
            {
              list.Add(station);
            }
          }
        }

        __result = list;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Msg($"GetAnyMixer: Selected preferred item {preferredId} with quantity={preferredSlot.Quantity}");
          return preferredSlot.ItemInstance.definition;
        }
      }

      var firstSlot = slots.FirstOrDefault();
      if (firstSlot != null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"GetAnyMixer: Selected first available item {firstSlot.ItemInstance.ID} with quantity={firstSlot.Quantity}");
      }
      return firstSlot?.ItemInstance?.definition;
    }

    public static bool HasSufficientItems(Chemist chemist, float threshold, ItemInstance item)
    {
      return NoLazyUtilities.GetAmountInInventoryAndSupply(chemist, item?.definition) >= threshold;
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
    }

    public enum EState
    {
      Idle,
      WalkingToSupplies,
      GrabbingSupplies,
      WalkingToStation,
      Cooking
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Station, Chemist, or ChemistConfig null for chemist: {chemist?.fullName ?? "null"}");
          return false;
        }

        // Initialize state if missing
        if (!states.TryGetValue(__instance, out var state))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: State missing for {chemist?.fullName}, initializing");
          states[__instance] = new StateData { CurrentState = EState.Idle };
          state = states[__instance];
        }

        MixingStationConfiguration config = MixingStationExtensions.MixingConfig[station.GUID];
        ObjectField mixerSupply = MixingStationExtensions.Supply[station.GUID];
        if (config == null || mixerSupply == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: MixerConfig or MixerSupply not found for station: {station?.GUID}");
          return false;
        }

        ItemField mixerItem = ChemistExtensions.GetMixerItemForProductSlot(station);
        if (mixerItem == null)
        {
          return false;
        }

        float threshold = config.StartThrehold.Value;

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: State={state.CurrentState}, station={station.ObjectId}, chemist={chemist.fullName}");

        switch (state.CurrentState)
        {
          case EState.Idle:
            int productQuantity = station.ProductSlot.Quantity;
            int outputQuantity = station.OutputSlot.Quantity;
            int mixQuantity = station.GetMixQuantity();

            if (outputQuantity > 0 || productQuantity < threshold)
            {
              Disable(__instance);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Disabling for {chemist.fullName}, outputQuantity={outputQuantity}, productQuantity={productQuantity}, threshold={threshold}");
              return false;
            }

            ConfigurationExtensions.NPCSupply[chemist.GUID] = mixerSupply;
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Copied station supply {mixerSupply.SelectedObject?.name} to chemist");

            bool hasSufficientItems = ChemistPatch.HasSufficientItems(chemist, threshold - mixQuantity, mixerItem.SelectedItem?.GetDefaultInstance());
            if (!hasSufficientItems && mixQuantity < threshold)
            {
              Disable(__instance);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Disabling for {chemist.fullName}, hasSufficientItems={hasSufficientItems}, mixQuantity={mixQuantity}, threshold={threshold}");
              return false;
            }

            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - mixQuantity={mixQuantity}, productQuantity={productQuantity}, outputQuantity={outputQuantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}, cookPending={state.CookPending}, mixOperation={(station.CurrentMixOperation != null)}");

            if (!state.CookPending && station.CurrentMixOperation == null && mixQuantity >= threshold && station.ProductSlot.Quantity >= threshold && station.OutputSlot.Quantity == 0)
            {
              bool needsMoreMixer = mixQuantity < productQuantity;
              if (needsMoreMixer && mixerItem.SelectedItem != null)
              {
                int additionalMixerNeeded = productQuantity - mixQuantity;
                int supplyCount = NoLazyUtilities.GetAmountInInventoryAndSupply(chemist, mixerItem.SelectedItem);
                bool canFetchMore = supplyCount >= 0;

                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Cooking conditions met, checking mixer: mixQuantity={mixQuantity}, productQuantity={productQuantity}, additionalMixerNeeded={additionalMixerNeeded}, supplyCount={supplyCount}, canFetchMore={canFetchMore}");

                if (canFetchMore)
                {
                  PrepareToFetchItem(__instance, state, mixerItem, productQuantity, mixQuantity, threshold, mixerSupply, station);
                  return false;
                }
              }

              if (IsAtStation(__instance))
              {
                state.CurrentState = EState.Cooking;
                state.CookPending = true;
                __instance.StartCook();
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Starting cook for {chemist.fullName}");
              }
              else
              {
                state.CurrentState = EState.WalkingToStation;
                __instance.SetDestination(GetStationAccessPoint(__instance), true);
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Walking to station for {chemist.fullName}");
              }
            }
            else
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Idle - Preparing to fetch for {chemist.fullName}, reason={(state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : mixQuantity < threshold ? "low mixQuantity" : "other")}");
              PrepareToFetchItem(__instance, state, mixerItem, productQuantity, mixQuantity, threshold, mixerSupply, station);
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
              int insertedQuantity = InsertItemsFromInventory(__instance, state, station);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Inserted {insertedQuantity} items to station for {chemist.fullName}");

              // Recalculate mixQuantity after insertion
              int updatedMixQuantity = station.GetMixQuantity();
              if (insertedQuantity > 0 && !state.CookPending && station.CurrentMixOperation == null && updatedMixQuantity >= threshold)
              {
                state.CurrentState = EState.Cooking;
                state.CookPending = true;
                __instance.StartCook();
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Arrived at station, starting cook for {chemist.fullName}, updatedMixQuantity={updatedMixQuantity}");
              }
              else
              {
                state.CurrentState = EState.Idle;
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Arrived at station, returning to Idle for {chemist.fullName}, reason={(insertedQuantity == 0 ? "no items inserted" : state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : updatedMixQuantity < threshold ? "low mixQuantity" : "other")}, updatedMixQuantity={updatedMixQuantity}");
              }
            }
            else
            {
              state.CurrentState = EState.Idle;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: WalkingToStation - Not at station after walking, returning to Idle for {chemist.fullName}");
            }
            break;

          case EState.Cooking:
            // Handled by MonitorMixOperation coroutine
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Cooking - Waiting for mix operation for {chemist.fullName}, station={station.ObjectId}");
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
      state.ClearMixerSlot = false;
      state.QuantityToFetch = Mathf.Max(productQuantity - mixQuantity, 0);
      state.TargetMixer = mixerItem.SelectedItem;
      state.Any = state.TargetMixer == null;

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: mixQuantity={mixQuantity}, threshold={threshold}, targetMixerId={state.TargetMixer?.ID ?? "null"}, quantityToFetch={state.QuantityToFetch}, any={state.Any}");

      // Check inventory for sufficient items
      if (state.TargetMixer != null)
      {
        int inventoryCount = chemist.Inventory._GetItemAmount(state.TargetMixer.ID);
        if (inventoryCount >= state.QuantityToFetch)
        {
          HandleInventorySufficient(__instance, state, station, inventoryCount);
          return;
        }
        else if (inventoryCount > 0)
        {
          state.QuantityToFetch -= inventoryCount;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Using {inventoryCount} from inventory, still need {state.QuantityToFetch} for {chemist?.fullName}");
        }
      }

      // Validate supply entity
      ITransitEntity supplyEntity = mixerSupply?.SelectedObject as ITransitEntity;
      if (supplyEntity == null || supplyEntity.OutputSlots == null)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Invalid supply entity for {chemist?.fullName}");
        return;
      }

      int fetchable = 0;
      // Check specific mixer in supply
      if (state.TargetMixer != null)
      {
        int supplyCount = NoLazyUtilities.GetAmountInSupply(chemist, state.TargetMixer.GetDefaultInstance());
        fetchable = supplyCount < state.QuantityToFetch ? supplyCount : state.QuantityToFetch;
      }

      // Select a mixer if any is allowed and station is empty or not enough available
      if (state.Any && (state.TargetMixer == null))
      {
        var productManager = NetworkSingleton<ProductManager>.Instance;
        if (productManager == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: ProductManager is null");
          return;
        }

        var validMixIngredientIds = productManager.ValidMixIngredients.Select(i => i.ID).ToHashSet();
        foreach (ItemSlot slot in supplyEntity.OutputSlots)
        {
          if (slot?.ItemInstance == null || !validMixIngredientIds.Contains(slot.ItemInstance.ID))
            continue;

          int invQuantity = chemist.Inventory._GetItemAmount(slot.ItemInstance.ID);
          int supplyQuantity = 0;
          if (invQuantity < state.QuantityToFetch)
            supplyQuantity = NoLazyUtilities.GetAmountInSupply(chemist, slot.ItemInstance);
          if (supplyQuantity + invQuantity >= threshold)
          {
            state.TargetMixer = slot.ItemInstance.definition;
            fetchable = Mathf.Min(productQuantity - invQuantity, supplyQuantity);
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Selected mixer {state.TargetMixer.ID}, quantityToFetch={state.QuantityToFetch}");
            break;
          }
        }
      }

      // Final validation
      if (state.TargetMixer == null)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: No suitable mixer available for {chemist?.fullName}");
        return;
      }

      // Proceed to fetch
      state.QuantityToFetch = fetchable;
      if (IsAtSupplies(__instance))
      {
        state.CurrentState = EState.GrabbingSupplies;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: At supplies, grabbing {state.TargetMixer.ID}");
        GrabItem(__instance, state);
      }
      else
      {
        state.CurrentState = EState.WalkingToSupplies;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Walking to supplies for {state.TargetMixer.ID}");
        WalkToSupplies(__instance, state);
      }
    }
    private static void HandleInventorySufficient(StartMixingStationBehaviour __instance, StateData state, MixingStation station, int inventoryCount)
    {
      Chemist chemist = __instance.chemist;
      if (IsAtStation(__instance))
      {
        int inserted = InsertItemsFromInventory(__instance, state, station);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Inserted {inserted} items from inventory for {chemist?.fullName}");
        state.CurrentState = EState.Idle;
      }
      else
      {
        state.CurrentState = EState.WalkingToStation;
        __instance.SetDestination(GetStationAccessPoint(__instance), true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Walking to station with {inventoryCount} inventory items for {chemist?.fullName}");
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.StartCook: State missing, initialized for {__instance.chemist?.fullName ?? "null"}");
        }

        if (state.CurrentState != EState.Cooking || !state.CookPending || __instance.targetStation.CurrentMixOperation != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.StartCook: Invalid state {state.CurrentState}, cookPending={state.CookPending}, mixOperation={(__instance.targetStation.CurrentMixOperation != null)} for {__instance.chemist?.fullName ?? "null"}");
          return false;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.StartCook: Starting cook for {__instance.chemist?.fullName ?? "null"}");
        state.CurrentState = EState.Cooking;
        return true;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.StartCook: Failed for chemist: {__instance.chemist?.fullName ?? "null"}, error: {e}");
        return false;
      }
    }

    [HarmonyPatch("StartCook")]
    [HarmonyPostfix]
    static void StartCookPostfix(StartMixingStationBehaviour __instance)
    {
      try
      {
        if (states.TryGetValue(__instance, out var state))
        {
          __instance.StartCoroutine(MonitorMixOperation(__instance, state));
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.StartCookPostfix: Failed for chemist: {__instance.chemist?.fullName ?? "null"}, error: {e}");
      }
    }

    private static IEnumerator MonitorMixOperation(StartMixingStationBehaviour __instance, StateData state)
    {
      yield return new MonitorMixOperationCoroutine(__instance, state);
    }

    private class MonitorMixOperationCoroutine
    {
      private readonly StartMixingStationBehaviour _instance;
      private readonly StateData _state;

      public MonitorMixOperationCoroutine(StartMixingStationBehaviour instance, StateData state)
      {
        _instance = instance;
        _state = state;
      }
    }

    private static bool IsAtSupplies(StartMixingStationBehaviour __instance)
    {
      Chemist chemist = __instance.chemist;
      ChemistConfiguration config = chemist.configuration;
      ObjectField supply = ConfigurationExtensions.NPCSupply[chemist.GUID];
      bool atSupplies = config != null && supply != null && supply.SelectedObject != null &&
           NavMeshUtility.IsAtTransitEntity(supply.SelectedObject as ITransitEntity, chemist, 0.4f);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Started");
      Chemist chemist = __instance.chemist;
      if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Supply not found for {chemist?.fullName ?? "null"}");
        return;
      }

      ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;
      if (!chemist.Movement.CanGetTo(supplyEntity, 1f))
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Cannot reach supply for {chemist?.fullName ?? "null"}");
        return;
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkToSupplies: {__instance} Walking to supply {supplyEntity.Name} for {chemist?.fullName} {state.CurrentState}");

      state.WalkToSuppliesRoutine = __instance.StartCoroutine(WalkRoutine(__instance, supplyEntity, state));
    }

    private static void GrabItem(StartMixingStationBehaviour __instance, StateData state)
    {
      Chemist chemist = __instance.chemist;
      MixingStation station = __instance.targetStation;

      try
      {
        if (station.OutputSlot.Quantity > 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Output quantity {station.OutputSlot.Quantity} > 0, disabling for {chemist?.fullName}");
          Disable(__instance);
          return;
        }

        if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Supply not found for {chemist?.fullName ?? "null"}");
          return;
        }

        ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;

        // Clear MixerSlot if needed
        if (state.ClearMixerSlot && station.MixerSlot.ItemInstance != null)
        {
          int currentQuantity = station.MixerSlot.Quantity;
          chemist.Inventory.InsertItem(station.MixerSlot.ItemInstance.GetCopy(currentQuantity));
          station.MixerSlot.SetQuantity(0);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: No slots containing {state.TargetMixer.ID} in supply for {chemist?.fullName}");
          return;
        }

        int totalAvailable = slots.Sum(s => s.Quantity);
        int quantityToFetch = Mathf.Min(state.QuantityToFetch, totalAvailable);

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
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

            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Took {amountToTake} of {state.TargetMixer.ID} from slot {slot.GetHashCode()}, remainingToFetch={remainingToFetch} for {chemist?.fullName}");
          }
          if (remainingToFetch <= 0)
            break;
        }

        if (remainingToFetch > 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Could not fetch full quantity, still need {remainingToFetch} of {state.TargetMixer.ID} for {chemist?.fullName}");
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Grabbed {quantityToFetch - remainingToFetch} of {state.TargetMixer.ID} into inventory for {chemist?.fullName}");

        state.GrabRoutine = __instance.StartCoroutine(GrabRoutine(__instance, state));
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
      Vector3 startPos = chemist.transform.position;
      __instance.SetDestination(supply, true);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkRoutine: Set destination for {chemist?.fullName}, IsMoving={chemist.Movement.IsMoving}");

      yield return new WaitForSeconds(0.2f); // Allow pathfinding to start
      float timeout = 10f;
      float elapsed = 0f;
      while (chemist.Movement.IsMoving && elapsed < timeout)
      {
        yield return null;
        elapsed += Time.deltaTime;
      }
      if (elapsed >= timeout)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkRoutine: Timeout walking to supply for {chemist?.fullName}");
      }

      state.WalkToSuppliesRoutine = null;
      state.CurrentState = EState.Idle;
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
      {
        bool atSupplies = IsAtSupplies(__instance);
        Vector3 chemistPos = chemist.transform.position;
        Vector3 supplyPos = supply != null ? ((MonoBehaviour)supply).transform.position : Vector3.zero;
        float distanceMoved = Vector3.Distance(startPos, chemistPos);
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkRoutine: Completed walk to supply, reverting to Idle for {chemist?.fullName}. AtSupplies={atSupplies}, ChemistPos={chemistPos}, SupplyPos={supplyPos}, DistanceMoved={distanceMoved}, Elapsed={elapsed}, Timeout={elapsed >= timeout}, IsMoving={chemist.Movement.IsMoving}");
      }
    }

    private static IEnumerator GrabRoutine(StartMixingStationBehaviour __instance, StateData state)
    {
      Chemist chemist = __instance.chemist;
      yield return new WaitForSeconds(0.2f);
      try
      {
        if (chemist.Avatar?.Anim != null)
        {
          chemist.Avatar.Anim.ResetTrigger("GrabItem");
          chemist.Avatar.Anim.SetTrigger("GrabItem");
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Triggered GrabItem animation for {chemist?.fullName}");
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabRoutine: Animator missing for {chemist?.fullName}, skipping animation");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.GrabRoutine: Failed for {chemist?.fullName}, error: {e}");
        state.GrabRoutine = null;
        state.CurrentState = EState.Idle;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabRoutine: Reverted to Idle due to error for {chemist?.fullName}");
      }
      yield return new WaitForSeconds(0.2f);
      state.GrabRoutine = null;
      state.CurrentState = EState.WalkingToStation;
      __instance.SetDestination(GetStationAccessPoint(__instance), true);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Grab complete, walking to station for {chemist?.fullName}");
    }

    private static int InsertItemsFromInventory(StartMixingStationBehaviour __instance, StateData state, MixingStation station)
    {
      Chemist chemist = __instance.chemist;
      int quantity = 0;
      try
      {
        if (state.TargetMixer == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: TargetMixer is null for {chemist?.fullName}");
          return 0;
        }

        if (station.MixerSlot == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: MixerSlot is null for station {station?.ObjectId}");
          return 0;
        }

        quantity = chemist.Inventory._GetItemAmount(state.TargetMixer.ID);
        if (quantity <= 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: No items of {state.TargetMixer.ID} in inventory for {chemist?.fullName}");
          return 0;
        }

        ItemInstance item = state.TargetMixer.GetDefaultInstance();
        if (item == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to get default instance for {state.TargetMixer.ID}");
          return 0;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Attempting to insert {quantity} of {state.TargetMixer.ID} for {chemist?.fullName}");

        // Insert into MixerSlot
        int currentQuantity = station.MixerSlot.Quantity;
        station.MixerSlot.InsertItem(item.GetCopy(quantity));
        int newQuantity = station.MixerSlot.Quantity;

        // Remove from inventory
        int quantityToRemove = quantity;
        List<(ItemSlot slot, int amount)> toRemove = [];
        foreach (ItemSlot slot in chemist.Inventory.ItemSlots)
        {
          if (slot?.ItemInstance != null && slot.ItemInstance.ID == state.TargetMixer.ID && slot.Quantity > 0)
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
        }

        if (quantityToRemove > 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to remove {quantityToRemove} of {state.TargetMixer.ID}, inventory may be inconsistent for {chemist?.fullName}");
          station.MixerSlot.SetQuantity(currentQuantity); // Revert insertion
          return 0;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Successfully inserted {quantity} of {state.TargetMixer.ID}, MixerSlot quantity changed from {currentQuantity} to {newQuantity} for {chemist?.fullName}");

        return quantity;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed for {chemist?.fullName}, item={state.TargetMixer?.ID ?? "null"}, quantity={quantity}, error: {e}");
        return 0;
      }
    }
    private static void Disable(StartMixingStationBehaviour __instance)
    {
      if (states.TryGetValue(__instance, out var state))
      {
        if (state.WalkToSuppliesRoutine != null)
        {
          __instance.StopCoroutine(state.WalkToSuppliesRoutine);
          state.WalkToSuppliesRoutine = null;
        }
        if (state.GrabRoutine != null)
        {
          __instance.StopCoroutine(state.GrabRoutine);
          state.GrabRoutine = null;
        }
        state.TargetMixer = null;
        state.ClearMixerSlot = false;
        state.QuantityToFetch = 0;
        state.CookPending = false;
        state.CurrentState = EState.Idle;
        state.LastSupply = null;
        state.Any = false;
      }
      states.Remove(__instance);
      __instance.Disable();
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.Disable: Disabled behaviour for {__instance.chemist?.fullName ?? "null"}");
    }

    private static bool IsAtStation(StartMixingStationBehaviour __instance)
    {
      bool atStation = __instance.targetStation != null &&
                         Vector3.Distance(__instance.chemist.transform.position, GetStationAccessPoint(__instance)) < 1f;
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsAtStation: Result={atStation}, chemist={__instance.chemist?.fullName ?? "null"}");
      return atStation;
    }

    private static Vector3 GetStationAccessPoint(StartMixingStationBehaviour __instance)
    {
      return __instance.targetStation ? ((ITransitEntity)__instance.targetStation).AccessPoints[0].position : __instance.chemist.transform.position;
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Checking stations for {__instance?.name ?? "null"}, total stations={__instance.configuration.MixStations.Count}");

        foreach (MixingStation station in __instance.configuration.MixStations)
        {
          ItemSlot outputSlot = station.OutputSlot;
          if (outputSlot.Quantity > 0)
          {
            ProductDefinition outputProduct = outputSlot.ItemInstance.Definition as ProductDefinition;
            MixingRoute matchingRoute = MixingStationExtensions.MixingRoutes[station.GUID].FirstOrDefault(r =>
                r.Product.SelectedItem == outputProduct);
            if (matchingRoute != null)
            {
              // Create a route for the matching product
              TransitRoute route = new(station, station);
              if (__instance.MoveItemBehaviour.IsTransitRouteValid(route, station.OutputSlot.ItemInstance.ID))
              {
                __instance.MoveItemBehaviour.Initialize(route, station.OutputSlot.ItemInstance);
                __instance.MoveItemBehaviour.Enable_Networked(null);
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
                  MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Initialized MoveItemBehaviour for station={station.ObjectId}, product={outputProduct.Name}");
                return false;
              }
            }
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Found {list.Count} stations ready to move for {__instance?.name ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistGetMixStationsReadyToMovePatch: Failed for chemist: {__instance?.name ?? "null"}, error: {e}");
        __result = [];
        return false;
      }
    }

    private static bool IsDestinationRouteValid(Chemist chemist, MixingStation station, ItemSlot outputSlot)
    {
      var config = station.Configuration as MixingStationConfiguration;
      if (config?.DestinationRoute == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
          MelonLogger.Warning($"ChemistGetMixStationsReadyToMovePatch: No valid destination route for station={station.ObjectId}");
        return false;
      }

      bool isValid = chemist.MoveItemBehaviour.IsTransitRouteValid(config.DestinationRoute, outputSlot.ItemInstance.ID);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugChemistBehavior)
        MelonLogger.Msg($"ChemistGetMixStationsReadyToMovePatch: Destination route valid={isValid} for station={station.ObjectId}");
      return isValid;
    }
  }
}