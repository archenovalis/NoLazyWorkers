using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.UI.Management;
using System.Collections;
using UnityEngine;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{

  [HarmonyPatch(typeof(BotanistConfigPanel), "Bind")]
  public class BotanistConfigPanelBindPatch
  {
    static void Postfix(BotanistConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"BotanistConfigPanelBindPatch: Processing configs, count: {configs?.Count ?? 0}"); }
        if (__instance == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: __instance is null");
          return;
        }

        // Verify UI components
        if (__instance.SuppliesUI == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: SuppliesUI is null");
          return;
        }
        if (__instance.PotsUI == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: PotsUI is null");
          return;
        }

        // Hide SuppliesUI
        __instance.SuppliesUI.gameObject.SetActive(false);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg("BotanistConfigPanelBindPatch: Hid SuppliesUI"); }

        // Move PotsUI to SuppliesUI's y-coordinate
        RectTransform suppliesRect = __instance.SuppliesUI.GetComponent<RectTransform>();
        RectTransform potsRect = __instance.PotsUI.GetComponent<RectTransform>();
        if (suppliesRect == null || potsRect == null)
        {
          MelonLogger.Error("BotanistConfigPanelBindPatch: SuppliesUI or PotsUI RectTransform is null");
          return;
        }

        float suppliesY = suppliesRect.anchoredPosition.y;
        potsRect.anchoredPosition = new Vector2(potsRect.anchoredPosition.x, suppliesY);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"BotanistConfigPanelBindPatch: Moved PotsUI to y={suppliesY}"); }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"BotanistConfigPanelBindPatch: Failed, error: {e}");
      }
    }
  }
  [HarmonyPatch(typeof(PotActionBehaviour), "StartAction")]
  public class PotActionBehaviourStartActionPatch
  {
    static void Prefix(PotActionBehaviour __instance)
    {
      try
      {
        if (__instance.AssignedPot == null)
        {
          MelonLogger.Warning("PotActionBehaviourStartActionPatch: AssignedPot is null");
          return;
        }

        Botanist botanist = __instance.Npc as Botanist;
        if (botanist == null || !(botanist.Configuration is BotanistConfiguration botanistConfig))
        {
          MelonLogger.Warning("PotActionBehaviourStartActionPatch: Botanist or BotanistConfiguration is null");
          return;
        }

        if (!ConfigurationExtensions.PotConfig.TryGetValue(__instance.AssignedPot, out var potConfig) ||
            !ConfigurationExtensions.PotSupply.TryGetValue(potConfig, out var potSupply))
        {
          MelonLogger.Warning("PotActionBehaviourStartActionPatch: Pot supply not found");
          return;
        }

        botanistConfig.Supplies.SelectedObject = potSupply.SelectedObject;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"PotActionBehaviourStartActionPatch: Set Botanist.Supplies to {potSupply.SelectedObject?.name ?? "null"} for pot {__instance.AssignedPot}"); }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotActionBehaviourStartActionPatch: Failed, error: {e}");
      }
    }
  }

  // Chemist
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"ChemistConfigurationPatch: Initializing for chemist: {chemist?.name ?? "null"}"); }
        ObjectField supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = false
        };
        supply.onObjectChanged.AddListener(delegate { ConfigurationExtensions.InvokeChanged(__instance); });
        ConfigurationExtensions.ChemistSupply[__instance] = supply;
        ConfigurationExtensions.ChemistConfig[chemist] = __instance;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg("ChemistConfigurationPatch: Supply initialized"); }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistConfigurationPatch: Failed, error: {e}");
      }
    }
  }
  [HarmonyPatch(typeof(StartMixingStationBehaviour))]
  public class StartMixingStationBehaviourPatch : Behaviour
  {
    private static void Disable(StartMixingStationBehaviour __instance)
    {
      // clear state then disable
      states.Remove(__instance);
      __instance.Disable();
    }

    // State tracking
    private static readonly Dictionary<StartMixingStationBehaviour, StateData> states = new();

    private class StateData
    {
      public int CurrentState { get; set; } // 0 = Idle, 1 = WalkingToSupplies, 2 = GrabbingSupplies
      public Coroutine WalkToSuppliesRoutine { get; set; }
      public Coroutine GrabRoutine { get; set; }
      public string TargetMixerId { get; set; }
      public bool ClearMixerSlot { get; set; }
      public int QuantityToFetch { get; set; }
    }

    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(StartMixingStationBehaviour __instance)
    {
      try
      {
        states[__instance] = new StateData
        {
          CurrentState = 0,
          WalkToSuppliesRoutine = null,
          GrabRoutine = null,
          TargetMixerId = null,
          ClearMixerSlot = false,
          QuantityToFetch = 0
        };
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Initialized state for {__instance.chemist?.name ?? "null"}"); }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.Awake: Failed, error: {e}");
      }
    }

    [HarmonyPatch("ActiveMinPass")]
    [HarmonyPrefix]
    static bool Prefix(StartMixingStationBehaviour __instance)
    {
      try
      {
        if (!InstanceFinder.IsServer)
          return false;

        Chemist chemist = __instance.Npc as Chemist;
        MixingStation station = __instance.targetStation;
        if (station == null || chemist == null || !ConfigurationExtensions.ChemistConfig.TryGetValue(chemist, out var chemistConfig))
        {
          Disable(__instance);
          MelonLogger.Warning("StartMixingStationBehaviourPatch: Station, Chemist, or ChemistConfig is null");
          return false;
        }

        if (!states.TryGetValue(__instance, out var state))
        {
          Disable(__instance);
          MelonLogger.Warning("StartMixingStationBehaviourPatch: State not initialized");
          return false;
        }

        MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
        ItemField mixerItem = ConfigurationExtensions.MixerItem[config];
        float threshold = config.StartThrehold.Value;
        int mixQuantity = station.GetMixQuantity();

        // Copy MixerSupply to ChemistSupply
        ObjectField chemistSupply = null;
        if (ConfigurationExtensions.MixerSupply.TryGetValue(config, out var mixerSupply) && mixerSupply.SelectedObject != null)
        {
          if (ConfigurationExtensions.ChemistSupply.TryGetValue(chemistConfig, out chemistSupply))
          {
            chemistSupply.SelectedObject = mixerSupply.SelectedObject;
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Set Chemist.Supplies to {mixerSupply.SelectedObject.name}"); }
          }
        }
        else
        {
          Disable(__instance);
          MelonLogger.Warning("StartMixingStationBehaviourPatch: MixerSupply not found");
          return false;
        }

        // State machine
        if (state.CurrentState == 0) // Idle
        {
          if (mixQuantity >= threshold)
          {
            if (mixerItem.SelectedItem == null)
            {
              if (station.MixerSlot.ItemInstance != null)
              {
                mixerItem.SetItem(station.MixerSlot.ItemInstance.Definition, true);
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Set MixerItem to {station.MixerSlot.ItemInstance.ID}"); }
              }
              else
              {
                Disable(__instance);
                MelonLogger.Warning("StartMixingStationBehaviourPatch: No mixer in MixerSlot or selected");
                return false;
              }
            }

            if (IsAtStation(__instance))
            {
              __instance.StartCook();
              return false;
            }
            __instance.SetDestination(GetStationAccessPoint(__instance), true);
            return false;
          }
          else if (chemistSupply.SelectedObject != null)
          {
            // Determine target mixer and quantity
            state.ClearMixerSlot = false;
            state.QuantityToFetch = 20 - mixQuantity;
            state.TargetMixerId = mixerItem.SelectedItem?.ID;

            if (state.TargetMixerId == null && station.MixerSlot.ItemInstance != null)
            {
              // Try current MixerSlot item
              state.TargetMixerId = station.MixerSlot.ItemInstance.ID;
              ItemSlot slot = (chemistSupply.SelectedObject as ITransitEntity).GetFirstSlotContainingItem(state.TargetMixerId, ITransitEntity.ESlotType.Both);
              int available = slot?.Quantity ?? 0;
              if (available < threshold - mixQuantity)
              {
                // Not enough to reach threshold; clear MixerSlot and pick new item
                state.ClearMixerSlot = true;
                state.TargetMixerId = null;

                // Find a mixer with at least threshold quantity
                foreach (ItemSlot s in (chemistSupply.SelectedObject as ITransitEntity).OutputSlots)
                {
                  if (s.Quantity >= threshold)
                  {
                    state.TargetMixerId = s.ItemInstance.ID;
                    state.QuantityToFetch = Mathf.Min(20, s.Quantity);
                    break;
                  }
                }
              }
            }

            if (state.TargetMixerId == null)
            {
              // No specific mixer selected; pick first with enough quantity
              foreach (ItemSlot s in (chemistSupply.SelectedObject as ITransitEntity).OutputSlots)
              {
                if (s.Quantity >= threshold)
                {
                  state.TargetMixerId = s.ItemInstance.ID;
                  state.QuantityToFetch = Mathf.Min(20, s.Quantity);
                  break;
                }
              }
            }

            if (state.TargetMixerId == null)
            {
              Disable(__instance);
              MelonLogger.Warning("StartMixingStationBehaviourPatch: No suitable mixer available in supply");
              return false;
            }

            ItemInstance item = GetItemInSupplies(chemist, state.TargetMixerId);
            if (item != null)
            {
              if (IsAtSupplies(__instance))
              {
                GrabItem(__instance, state);
                return false;
              }
              WalkToSupplies(__instance, state);
              return false;
            }
            else
            {
              Disable(__instance);
              MelonLogger.Warning($"StartMixingStationBehaviourPatch: Mixer {state.TargetMixerId} not in supply");
              return false;
            }
          }
          else
          {
            Disable(__instance);
            MelonLogger.Warning("StartMixingStationBehaviourPatch: ChemistSupply is null");
            return false;
          }
        }

        return false; // Handle all cases in patch
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.ActiveMinPass: Failed, error: {e}");
        return true;
      }
    }

    [HarmonyPatch("StartCook")]
    [HarmonyPrefix]
    static bool StartCookPrefix(StartMixingStationBehaviour __instance)
    {
      try
      {
        if (!states.TryGetValue(__instance, out var state))
        {
          MelonLogger.Warning("StartMixingStationBehaviourPatch: State not initialized for StartCook");
          return false;
        }

        // Reset state
        state.CurrentState = 0;
        state.WalkToSuppliesRoutine = null;
        state.GrabRoutine = null;
        state.TargetMixerId = null;
        state.ClearMixerSlot = false;
        state.QuantityToFetch = 0;
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Starting cook for {__instance.chemist?.name ?? "null"}"); }
        return true; // Proceed with original StartCook
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.StartCook: Failed, error: {e}");
        return false;
      }
    }

    [HarmonyPatch("CanCookStart")]
    [HarmonyPostfix]
    static void CanCookStartPostfix(StartMixingStationBehaviour __instance, ref bool __result)
    {
      try
      {
        if (!__result)
          return;

        MixingStation station = __instance.targetStation;
        MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
        ItemField mixerItem = ConfigurationExtensions.MixerItem[config];
        if (mixerItem.SelectedItem == null)
        {
          MelonLogger.Warning("StartMixingStationBehaviourPatch: No mixer selected for CanCookStart");
          __result = false;
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"StartMixingStationBehaviourPatch.CanCookStart: Failed, error: {e}");
      }
    }

    private static bool IsAtStation(StartMixingStationBehaviour behaviour)
    {
      return behaviour.targetStation != null &&
             Vector3.Distance(behaviour.Npc.transform.position, GetStationAccessPoint(behaviour)) < 1f;
    }

    private static Vector3 GetStationAccessPoint(StartMixingStationBehaviour behaviour)
    {
      return behaviour.targetStation ? ((ITransitEntity)behaviour.targetStation).AccessPoints[0].position : behaviour.Npc.transform.position;
    }

    private static bool IsAtSupplies(StartMixingStationBehaviour behaviour)
    {
      Chemist chemist = behaviour.Npc as Chemist;
      if (ConfigurationExtensions.ChemistConfig.TryGetValue(chemist, out var config) &&
          ConfigurationExtensions.ChemistSupply.TryGetValue(config, out var supply) && supply.SelectedObject != null)
      {
        return NavMeshUtility.IsAtTransitEntity(supply.SelectedObject as ITransitEntity, behaviour.Npc, 0.4f);
      }
      return false;
    }

    private static void WalkToSupplies(StartMixingStationBehaviour behaviour, StateData state)
    {
      Chemist chemist = behaviour.Npc as Chemist;
      if (!ConfigurationExtensions.ChemistConfig.TryGetValue(chemist, out var config) ||
          !ConfigurationExtensions.ChemistSupply.TryGetValue(config, out var supply) || supply.SelectedObject == null)
      {
        behaviour.Disable();
        MelonLogger.Warning("StartMixingStationBehaviourPatch: Supply not found for WalkToSupplies");
        return;
      }

      ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;
      if (!behaviour.Npc.Movement.CanGetTo(supplyEntity, 1f))
      {
        behaviour.Disable();
        MelonLogger.Warning("StartMixingStationBehaviourPatch: Cannot reach supply");
        return;
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Walking to supply {supplyEntity.Name}"); }
      state.CurrentState = 1; // WalkingToSupplies
      state.WalkToSuppliesRoutine = behaviour.StartCoroutine(WalkRoutine(behaviour, supplyEntity, state));
    }

    private static void GrabItem(StartMixingStationBehaviour behaviour, StateData state)
    {
      Chemist chemist = behaviour.Npc as Chemist;
      MixingStation station = behaviour.targetStation;
      if (!ConfigurationExtensions.ChemistConfig.TryGetValue(chemist, out var config) ||
          !ConfigurationExtensions.ChemistSupply.TryGetValue(config, out var supply) || supply.SelectedObject == null)
      {
        behaviour.Disable();
        MelonLogger.Warning("StartMixingStationBehaviourPatch: Supply not found for GrabItem");
        return;
      }

      ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;

      // Clear MixerSlot if needed
      if (state.ClearMixerSlot && station.MixerSlot.ItemInstance != null)
      {
        int currentQuantity = station.MixerSlot.Quantity;
        chemist.Inventory.InsertItem(station.MixerSlot.ItemInstance.GetCopy(currentQuantity));
        station.MixerSlot.SetQuantity(0);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Cleared MixerSlot ({currentQuantity} items returned)"); }
      }

      // Fetch new items
      ItemSlot targetSlot = supplyEntity.GetFirstSlotContainingItem(state.TargetMixerId, ITransitEntity.ESlotType.Both);
      if (targetSlot?.ItemInstance == null)
      {
        behaviour.Disable();
        MelonLogger.Warning($"StartMixingStationBehaviourPatch: Item {state.TargetMixerId} not found in supply");
        return;
      }

      int quantity = Mathf.Min(state.QuantityToFetch, targetSlot.Quantity);
      if (quantity <= 0)
      {
        behaviour.Disable();
        MelonLogger.Warning($"StartMixingStationBehaviourPatch: No items available for {state.TargetMixerId}");
        return;
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Grabbing {state.TargetMixerId} (quantity: {quantity})"); }
      state.CurrentState = 2; // GrabbingSupplies
      state.GrabRoutine = behaviour.StartCoroutine(GrabRoutine(behaviour, targetSlot, quantity, state, ConfigurationExtensions.MixerItem[ConfigurationExtensions.MixerConfig[station]]));
    }

    private static IEnumerator WalkRoutine(StartMixingStationBehaviour behaviour, ITransitEntity supply, StateData state)
    {
      behaviour.SetDestination(supply, true);
      yield return new WaitForEndOfFrame();
      yield return new WaitUntil(() => !behaviour.Npc.Movement.IsMoving);
      state.CurrentState = 0; // Idle
      state.WalkToSuppliesRoutine = null;
    }

    private static IEnumerator GrabRoutine(StartMixingStationBehaviour behaviour, ItemSlot slot, int quantity, StateData state, ItemField mixerItem)
    {
      MixingStation station = behaviour.targetStation;
      Chemist chemist = behaviour.Npc as Chemist;
      chemist.Movement.FacePoint(station.transform.position, 0.5f);
      chemist.Avatar.Anim.ResetTrigger("GrabItem");
      chemist.Avatar.Anim.SetTrigger("GrabItem");
      yield return new WaitForSeconds(0.5f);

      station.MixerSlot.InsertItem(slot.ItemInstance.GetCopy(quantity));
      chemist.Inventory.GetFirstIdenticalItem(mixerItem.SelectedItem.GetDefaultInstance()).ChangeQuantity(-quantity);
      if (mixerItem.SelectedItem == null || mixerItem.SelectedItem.ID != state.TargetMixerId)
      {
        mixerItem.SetItem(slot.ItemInstance.Definition, true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs) { MelonLogger.Msg($"StartMixingStationBehaviourPatch: Set MixerItem to {state.TargetMixerId}"); }
      }
      yield return new WaitForSeconds(0.5f);

      state.CurrentState = 0; // Idle
      state.GrabRoutine = null;
      state.TargetMixerId = null;
      state.ClearMixerSlot = false;
      state.QuantityToFetch = 0;
    }

    // Reuse Chemist's GetItemInSupplies
    public static ItemInstance GetItemInSupplies(Chemist chemist, string id)
    {
      if (!ConfigurationExtensions.ChemistConfig.TryGetValue(chemist, out var config) ||
          !ConfigurationExtensions.ChemistSupply.TryGetValue(config, out var supply) || supply.SelectedObject == null)
      {
        return null;
      }

      ITransitEntity supplies = supply.SelectedObject as ITransitEntity;
      if (!chemist.Movement.CanGetTo(supplies, 1f))
      {
        return null;
      }

      ItemSlot slot = supplies.GetFirstSlotContainingItem(id, ITransitEntity.ESlotType.Both);
      return slot?.ItemInstance;
    }
  }
}