using BepInEx.AssemblyPublicizer;
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
using Unity.Mathematics;
using UnityEngine;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{
  public static class ChemistExtensions
  {
    public static ItemInstance GetItemInSupply(this Chemist chemist, MixingStation station, string id)
    {
      MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
      ObjectField supply = ConfigurationExtensions.MixerSupply[config];

      List<ItemSlot> list = new();
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
    static bool GetMixingStationsReadyToStartPrefix(Chemist __instance, ref List<MixingStation> __result)
    {
      try
      {
        List<MixingStation> list = new();
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Checking stations for {__instance?.name ?? "null"}, total stations={__instance.configuration.MixStations.Count}");

        foreach (MixingStation mixingStation in __instance.configuration.MixStations)
        {
          if (!((IUsable)mixingStation).IsInUse && mixingStation.CurrentMixOperation == null)
          {
            if (!ConfigurationExtensions.MixerConfig.TryGetValue(mixingStation, out var config))
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                MelonLogger.Warning($"ChemistPatch.GetMixingStationsReadyToStart: MixerConfig missing for station {mixingStation?.ObjectId.ToString() ?? "null"}");
              continue;
            }

            ItemField mixerItem = ConfigurationExtensions.MixerItem[config];
            float threshold = config.StartThrehold.Value;
            int mixQuantity = mixingStation.GetMixQuantity();

            bool canStartMix = mixQuantity >= threshold && mixingStation.ProductSlot.Quantity >= threshold && mixingStation.OutputSlot.Quantity == 0;
            bool canRestock = false;
            ObjectField supply = ConfigurationExtensions.MixerSupply[config];
            if (!canStartMix && supply?.SelectedObject != null)
            {
              ConfigurationExtensions.NPCSupply[__instance.configuration] = supply;
              ItemInstance targetItem;
              if (mixerItem.SelectedItem != null)
              {
                targetItem = mixerItem.SelectedItem.GetDefaultInstance();
              }
              else
              {
                string preferredId = mixerItem?.SelectedItem?.ID?.ToLower(); // Pass the station's intended item ID
                var mixerDef = GetAnyMixer(supply.SelectedObject as ITransitEntity, threshold, preferredId);
                if (mixerDef == null)
                {
                  if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"No suitable mixer found for station {mixingStation.ObjectId}");
                  continue;
                }
                targetItem = mixerDef.GetDefaultInstance();
              }
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Station {mixingStation.ObjectId}, IsInUse={((IUsable)mixingStation).IsInUse}, CurrentMixOperation={mixingStation.CurrentMixOperation != null}, mixQuantity={mixQuantity}, productQuantity={mixingStation.ProductSlot.Quantity}, outputQuantity={mixingStation.OutputSlot.Quantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}, supplyId={supply?.SelectedObject?.ObjectId}, canStartMix={canStartMix}, canRestock={canRestock}, hasSufficientItems={HasSufficientItems(__instance, threshold, targetItem)}");
              canRestock = mixingStation.OutputSlot.Quantity == 0 &&
                           mixingStation.ProductSlot.Quantity >= threshold &&
                           HasSufficientItems(__instance, threshold, targetItem);
            }

            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
              MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Station {mixingStation.ObjectId}, Supply={supply?.SelectedObject?.ObjectId.ToString() ?? "null"}, canStartMix={canStartMix}, canRestock={canRestock}, mixQuantity={mixQuantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}");

            if (canStartMix || canRestock)
            {
              list.Add(mixingStation);
            }
          }
        }

        __result = list;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"ChemistPatch.GetMixingStationsReadyToStart: Found {list.Count} stations ready for {__instance?.name ?? "null"}");
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.GetMixingStationsReadyToStart: Failed for chemist: {__instance?.name ?? "null"}, error: {e}");
        __result = new List<MixingStation>();
        return false;
      }
    }

    public static ItemDefinition GetAnyMixer(ITransitEntity supply, float threshold, string preferredId = null)
    {
      if (supply == null || supply.OutputSlots == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"GetAnyMixer: Selected preferred item {preferredId} with quantity={preferredSlot.Quantity}");
          return preferredSlot.ItemInstance.definition;
        }
      }

      var firstSlot = slots.FirstOrDefault();
      if (firstSlot != null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"GetAnyMixer: Selected first available item {firstSlot.ItemInstance.ID} with quantity={firstSlot.Quantity}");
      }
      return firstSlot?.ItemInstance?.definition;
    }

    public static bool HasSufficientItems(Chemist chemist, float threshold, ItemInstance item)
    {
      return NoLazyUtilities.GetAmountInInventoryAndSupply(chemist, item.definition) > threshold;
    }
  }

  [HarmonyPatch(typeof(ChemistConfiguration))]
  public class ChemistConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(Chemist) })]
    static void Postfix(ChemistConfiguration __instance)
    {
      try
      {
        Chemist chemist = __instance.chemist;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"ChemistConfigurationPatch: Initializing for chemist: {chemist?.fullName ?? "null"}"); }
        ObjectField supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = false
        };
        supply.onObjectChanged.AddListener(delegate { ConfigurationExtensions.InvokeChanged(__instance); });
        ConfigurationExtensions.NPCSupply[__instance] = supply;
        ConfigurationExtensions.NPCConfig[chemist] = __instance;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg("ChemistConfigurationPatch: Supply initialized"); }
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
    private static readonly Dictionary<StartMixingStationBehaviour, StateData> states = new();

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
        if (!states.ContainsKey(__instance))
        {
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.Awake: Initialized state for {__instance.chemist?.fullName ?? "null"}");
        }
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Skipping, not server");
          return false;
        }

        Chemist chemist = __instance.chemist;
        MixingStation station = __instance.targetStation;

        if (station == null || chemist == null || !ConfigurationExtensions.NPCConfig.TryGetValue(chemist, out var chemistConfig))
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Station, Chemist, or ChemistConfig null for chemist: {chemist?.fullName ?? "null"}");
          return false;
        }

        // Initialize state if missing
        if (!states.TryGetValue(__instance, out var state))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: State missing for {chemist?.fullName}, initializing");
          states[__instance] = new StateData { CurrentState = EState.Idle };
          state = states[__instance];
        }
        MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
        ObjectField mixerSupply = ConfigurationExtensions.MixerSupply[config];
        if (config == null || mixerSupply == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: MixerConfig not found for station: {station?.ObjectId.ToString() ?? "null"}");
          return false;
        }

        ItemField mixerItem = ConfigurationExtensions.MixerItem[config];
        float threshold = config.StartThrehold.Value;
        int productQuantity = station.ProductSlot.Quantity;
        int outputQuantity = station.OutputSlot.Quantity;
        if (outputQuantity > 0 || productQuantity < threshold)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Disabling for {chemist?.fullName}, outputQuantity={outputQuantity}, productQuantity={productQuantity}, threshold={threshold}");
          Disable(__instance);
          return false;
        }

        ObjectField chemistSupply = ConfigurationExtensions.NPCSupply[chemistConfig];
        // Copy supply only if changed
        if (state.LastSupply != mixerSupply)
        {
          chemistSupply.SelectedObject = mixerSupply.SelectedObject;
          state.LastSupply = mixerSupply;
          ConfigurationExtensions.NPCSupply[chemistConfig] = mixerSupply;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Copied station supply {mixerSupply.SelectedObject?.name} to chemist");
        }

        // Check if station is valid for operation
        int mixQuantity = station.GetMixQuantity();
        bool hasSufficientItems = ChemistPatch.HasSufficientItems(chemist, threshold - mixQuantity, mixerItem.SelectedItem.GetDefaultInstance());
        if (!hasSufficientItems)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Disabling for {chemist?.fullName}, outputQuantity={outputQuantity}, productQuantity={productQuantity}, hasSufficientItems={hasSufficientItems}, threshold={threshold}");
          Disable(__instance);
          return false;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: State={state.CurrentState}, mixQuantity={mixQuantity}, productQuantity={productQuantity}, outputQuantity={outputQuantity}, threshold={threshold}, mixerItem={mixerItem.SelectedItem?.Name ?? "null"}, cookPending={state.CookPending}, mixOperation={(station.CurrentMixOperation != null)}");

        switch (state.CurrentState)
        {
          case EState.Idle:
            if (!state.CookPending && station.CurrentMixOperation == null && mixQuantity >= threshold && station.ProductSlot.Quantity >= threshold && station.OutputSlot.Quantity == 0)
            {
              if (IsAtStation(__instance))
              {
                state.CurrentState = EState.Cooking;
                state.CookPending = true;
                __instance.StartCook();
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Starting cook for {chemist?.fullName}");
              }
              else
              {
                state.CurrentState = EState.WalkingToStation;
                __instance.SetDestination(GetStationAccessPoint(__instance), true);
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Walking to station for {chemist?.fullName}");
              }
            }
            else
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Preparing to fetch for {chemist?.fullName}, reason={(state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : mixQuantity < threshold ? "low mixQuantity" : "other")}");
              PrepareToFetchItem(__instance, state, mixerItem, mixQuantity, threshold, mixerSupply, station);
            }
            break;

          case EState.WalkingToSupplies:
            // Handled by WalkRoutine
            break;

          case EState.GrabbingSupplies:
            // Handled in GrabRoutine
            break;

          case EState.WalkingToStation:
            if (!chemist.Movement.IsMoving)
            {
              if (IsAtStation(__instance))
              {
                int insertedQuantity = InsertItemsFromInventory(__instance, state, station);
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                  MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Inserted {insertedQuantity} items to station for {chemist?.fullName}");

                if (insertedQuantity > 0 && !state.CookPending && station.CurrentMixOperation == null && station.GetMixQuantity() >= threshold)
                {
                  state.CurrentState = EState.Cooking;
                  state.CookPending = true;
                  __instance.StartCook();
                  if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Arrived at station, starting cook for {chemist?.fullName}");
                }
                else
                {
                  state.CurrentState = EState.Idle;
                  if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Msg($"StartMixingStationBehaviourPatch.ActiveMinPass: Arrived at station, returning to Idle for {chemist?.fullName}, reason={(insertedQuantity == 0 ? "no items inserted" : state.CookPending ? "cook pending" : station.CurrentMixOperation != null ? "mix operation active" : station.GetMixQuantity() < threshold ? "low mixQuantity" : "other")}");
                }
              }
              else
              {
                state.CurrentState = EState.Idle;
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                  MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Not at station after walking, returning to Idle for {chemist?.fullName}");
              }
            }
            break;

          case EState.Cooking:
            if (station.CurrentMixOperation != null)
            {
              Disable(__instance);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                MelonLogger.Warning($"StartMixingStationBehaviourPatch.ActiveMinPass: Cooking Complete for {chemist?.fullName}");
              return false;
            }
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

    private static void PrepareToFetchItem(StartMixingStationBehaviour __instance, StateData state, ItemField mixerItem, int mixQuantity, float threshold, ObjectField mixerSupply, MixingStation station)
    {
      Chemist chemist = __instance.chemist;
      state.ClearMixerSlot = false;
      state.QuantityToFetch = Mathf.Max(20 - mixQuantity, 0); // Use threshold-based quantity
      state.TargetMixer = mixerItem.SelectedItem;
      state.Any = state.TargetMixer == null; // Any mixer allowed if no specific item selected

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Using {inventoryCount} from inventory, still need {state.QuantityToFetch} for {chemist?.fullName}");
        }
      }

      // Validate supply entity
      ITransitEntity supplyEntity = mixerSupply?.SelectedObject as ITransitEntity;
      if (supplyEntity == null || supplyEntity.OutputSlots == null)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Invalid supply entity for {chemist?.fullName}");
        return;
      }

      // Check specific mixer in supply
      if (state.TargetMixer != null)
      {
        int supplyCount = NoLazyUtilities.GetAmountInInventoryAndSupply(chemist, state.TargetMixer);
        if (supplyCount < state.QuantityToFetch)
        {
          state.ClearMixerSlot = true;
          state.TargetMixer = null;
          state.Any = true; // Fall back to any mixer
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Insufficient {state.TargetMixer.ID} in supply ({supplyCount}/{state.QuantityToFetch}), clearing MixerSlot");
        }
      }

      // Select a mixer if any is allowed
      if (state.Any)
      {
        var productManager = NetworkSingleton<ProductManager>.Instance;
        if (productManager == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: ProductManager is null");
          return;
        }

        var validMixIngredientIds = productManager.ValidMixIngredients.Select(i => i.ID).ToHashSet();
        foreach (ItemSlot slot in supplyEntity.OutputSlots)
        {
          if (slot?.ItemInstance == null || !validMixIngredientIds.Contains(slot.ItemInstance.ID))
            continue;

          int totalQuantity = NoLazyUtilities.GetAmountInInventoryAndSupply(chemist, slot.ItemInstance.definition);
          if (totalQuantity >= threshold)
          {
            state.TargetMixer = slot.ItemInstance.definition;
            state.QuantityToFetch = Mathf.Min((int)threshold, totalQuantity);
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
              MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Selected mixer {state.TargetMixer.ID}, quantityToFetch={state.QuantityToFetch}");
            break;
          }
        }
      }

      // Final validation
      if (state.TargetMixer == null)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.PrepareToFetchItem: No suitable mixer available for {chemist?.fullName}");
        return;
      }

      // Proceed to fetch
      if (IsAtSupplies(__instance))
      {
        state.CurrentState = EState.GrabbingSupplies;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: At supplies, grabbing {state.TargetMixer.ID}");
        GrabItem(__instance, state);
      }
      else
      {
        state.CurrentState = EState.WalkingToSupplies;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.PrepareToFetchItem: Inserted {inserted} items from inventory for {chemist?.fullName}");
        state.CurrentState = EState.Idle;
      }
      else
      {
        state.CurrentState = EState.WalkingToStation;
        __instance.SetDestination(GetStationAccessPoint(__instance), true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.StartCook: State missing, initialized for {__instance.chemist?.fullName ?? "null"}");
        }

        if (state.CurrentState != EState.Cooking || !state.CookPending || __instance.targetStation.CurrentMixOperation != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.StartCook: Invalid state {state.CurrentState}, cookPending={state.CookPending}, mixOperation={(__instance.targetStation.CurrentMixOperation != null)} for {__instance.chemist?.fullName ?? "null"}");
          return false;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.StartCook: Starting cook for {__instance.chemist?.fullName ?? "null"}");

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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.StartCookPostfix: Cook started for {__instance.chemist?.fullName ?? "null"}");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.StartCookPostfix: Failed for chemist: {__instance.chemist?.fullName ?? "null"}, error: {e}");
      }
    }

    private static bool IsAtSupplies(StartMixingStationBehaviour __instance)
    {
      Chemist chemist = __instance.chemist;
      ChemistConfiguration config = ConfigurationExtensions.NPCConfig[chemist] as ChemistConfiguration;
      ObjectField supply = ConfigurationExtensions.NPCSupply[config];
      bool atSupplies = config != null && supply != null && supply.SelectedObject != null &&
           NavMeshUtility.IsAtTransitEntity(supply.SelectedObject as ITransitEntity, chemist, 0.4f);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
      {
        Vector3 chemistPos = chemist.transform.position;
        Vector3 supplyPos = supply?.SelectedObject != null ? ((MonoBehaviour)supply.SelectedObject).transform.position : Vector3.zero;
        float distance = Vector3.Distance(chemistPos, supplyPos);
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsAtSupplies: Result={atSupplies}, chemist={chemist?.fullName ?? "null"}, ChemistPos={chemistPos}, SupplyPos={supplyPos}, Distance={distance}");
      }
      return atSupplies;
    }

    private static void WalkToSupplies(StartMixingStationBehaviour __instance, StateData state)
    {
      Chemist chemist = __instance.chemist;
      if (!ConfigurationExtensions.NPCConfig.TryGetValue(chemist, out var config) ||
          !ConfigurationExtensions.NPCSupply.TryGetValue(config, out var supply) || supply.SelectedObject == null)
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Supply not found for {chemist?.fullName ?? "null"}");
        return;
      }

      ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;
      if (!chemist.Movement.CanGetTo(supplyEntity, 1f))
      {
        Disable(__instance);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkToSupplies: Cannot reach supply for {chemist?.fullName ?? "null"}");
        return;
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.WalkToSupplies: Walking to supply {supplyEntity.Name} for {chemist?.fullName}");

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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Output quantity {station.OutputSlot.Quantity} > 0, disabling for {chemist?.fullName}");
          Disable(__instance);
          return;
        }

        if (!ConfigurationExtensions.NPCConfig.TryGetValue(chemist, out var config) ||
            !ConfigurationExtensions.NPCSupply.TryGetValue(config, out var supply) || supply.SelectedObject == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Cleared MixerSlot ({currentQuantity} items returned to inventory) for {chemist?.fullName}");
        }

        // Fetch new items
        ItemSlot targetSlot = supplyEntity.GetFirstSlotContainingItem(state.TargetMixer.ID, ITransitEntity.ESlotType.Both);
        if (targetSlot?.ItemInstance == null)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Item {state.TargetMixer.ID} not found in supply for {chemist?.fullName}");
          return;
        }

        int quantity = Mathf.Min(20, NoLazyUtilities.GetAmountInInventoryAndSupply(chemist, state.TargetMixer));
        if (quantity < state.QuantityToFetch)
        {
          Disable(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabItem: Insufficient quantity {quantity}/{state.QuantityToFetch} for {state.TargetMixer.ID}");
          return;
        }

        chemist.Inventory.InsertItem(targetSlot.ItemInstance.GetCopy(quantity));
        targetSlot.ChangeQuantity(-quantity, false); //todo: loop through slots and change/set quantity to ensure full amount is reduced since some slots might have less than quantity.

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabItem: Grabbed {quantity} of {state.TargetMixer} into inventory for {chemist?.fullName}");

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
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.WalkRoutine: Timeout walking to supply for {chemist?.fullName}");
      }

      state.WalkToSuppliesRoutine = null;
      state.CurrentState = EState.Idle;
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
      yield return new WaitForSeconds(0.5f);
      try
      {
        if (chemist.Avatar?.Anim != null)
        {
          chemist.Avatar.Anim.ResetTrigger("GrabItem");
          chemist.Avatar.Anim.SetTrigger("GrabItem");
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Msg($"StartMixingStationBehaviourPatch.GrabRoutine: Triggered GrabItem animation for {chemist?.fullName}");
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabRoutine: Animator missing for {chemist?.fullName}, skipping animation");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.GrabRoutine: Failed for {chemist?.fullName}, error: {e}");
        state.GrabRoutine = null;
        state.CurrentState = EState.Idle;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Warning($"StartMixingStationBehaviourPatch.GrabRoutine: Reverted to Idle due to error for {chemist?.fullName}");
      }
      yield return new WaitForSeconds(0.5f);
      state.GrabRoutine = null;
      state.CurrentState = EState.WalkingToStation;
      __instance.SetDestination(GetStationAccessPoint(__instance), true);
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: TargetMixer is null for {chemist?.fullName}");
          return 0;
        }

        if (station.MixerSlot == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: MixerSlot is null for station {station?.name}");
          return 0;
        }

        quantity = chemist.Inventory._GetItemAmount(state.TargetMixer.ID);
        if (quantity <= 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: No items of {state.TargetMixer.ID} in inventory for {chemist?.fullName}");
          return 0;
        }

        ItemInstance item = state.TargetMixer.GetDefaultInstance();
        if (item == null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to get default instance for {state.TargetMixer.ID}");
          return 0;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Attempting to insert {quantity} of {state.TargetMixer.ID} for {chemist?.fullName}");

        // Insert into MixerSlot
        station.MixerSlot.InsertItem(item.GetCopy(quantity));

        // Remove from inventory by reducing slot quantities
        int quantityToRemove = quantity;
        List<(ItemSlot slot, int amount)> toRemove = new();
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

        if (quantityToRemove > 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Insufficient inventory items, needed {quantity}, found {quantity - quantityToRemove}");
          return 0;
        }

        // Apply changes
        station.MixerSlot.InsertItem(item.GetCopy(quantity));
        foreach (var (slot, amount) in toRemove)
        {
          slot.SetQuantity(slot.Quantity - amount);
        }

        if (quantityToRemove > 0)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
            MelonLogger.Warning($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Failed to remove {quantityToRemove} of {state.TargetMixer.ID}, inventory may be inconsistent for {chemist?.fullName}");
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
          MelonLogger.Msg($"StartMixingStationBehaviourPatch.InsertItemsFromInventory: Successfully inserted {quantity} of {state.TargetMixer.ID} for {chemist?.fullName}");

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
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.Disable: Disabled behaviour for {__instance.chemist?.fullName ?? "null"}");
    }

    private static bool IsAtStation(StartMixingStationBehaviour __instance)
    {
      bool atStation = __instance.targetStation != null &&
                         Vector3.Distance(__instance.chemist.transform.position, GetStationAccessPoint(__instance)) < 1f;
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
        MelonLogger.Msg($"StartMixingStationBehaviourPatch.IsAtStation: Result={atStation}, chemist={__instance.chemist?.fullName ?? "null"}");
      return atStation;
    }

    private static Vector3 GetStationAccessPoint(StartMixingStationBehaviour __instance)
    {
      return __instance.targetStation ? ((ITransitEntity)__instance.targetStation).AccessPoints[0].position : __instance.chemist.transform.position;
    }
  }
}