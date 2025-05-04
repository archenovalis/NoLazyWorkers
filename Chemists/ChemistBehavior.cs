using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Handlers.StorageUtilities;

using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
namespace NoLazyWorkers.Chemists
{
  public static class ChemistBehaviour
  {
    public static readonly Dictionary<Chemist, EntityConfiguration> cachedConfigs = [];
    public static readonly Dictionary<Behaviour, StateData> states = [];

    public class StateData
    {

      public EState CurrentState { get; set; }
      public ItemInstance TargetItem { get; set; } // For cauldron (gasoline, cocaleaf) or mixing station (mixer item)
      public int QuantityToFetch { get; set; }
      public bool IsFetchingPrimary { get; set; } // Gasoline for cauldron, mixer item for mixing station
      public bool IsFetchingSecondary { get; set; } // Coca leaf for cauldron
      public bool ClearStationSlot { get; set; } // For mixing station MixerSlot clearing
      public Coroutine WalkToSuppliesRoutine { get; set; }
      public Coroutine GrabRoutine { get; set; }
      public Coroutine InsertRoutine { get; set; }
      public ITransitEntity LastSupply { get; set; }
      public bool CookPending { get; set; }
    }

    public enum EState
    {
      Idle,
      WalkingToSupplies,
      GrabbingSupplies,
      WalkingToStation,
      InsertingItems,
      StartingOperation,
      Cooking
    }

    public static void PrepareToFetchItems(Behaviour __instance, StateData state)
    {
      Chemist chemist = (Chemist)__instance.Npc;
      if (__instance is StartMixingStationBehaviour mixingBehaviour)
      {
        MixingStation station = mixingBehaviour.targetStation;
        ItemInstance targetMixer = station.MixerSlot.ItemInstance ?? Registry.Instance._GetItem(state.TargetItem.ID).GetDefaultInstance();

        int quantityNeeded = station.MixerSlot.GetCapacityForItem(targetMixer) - station.MixerSlot.Quantity;
        int inventoryCount = chemist.Inventory._GetItemAmount(targetMixer.ID);
        state.IsFetchingPrimary = quantityNeeded > inventoryCount;
        state.QuantityToFetch = quantityNeeded - inventoryCount;
        state.TargetItem = targetMixer;
        state.ClearStationSlot = station.MixerSlot.ItemInstance != null && station.MixerSlot.ItemInstance.ID != targetMixer.ID;

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"BehaviourPatch.PrepareToFetchItems: MixingStation, target={targetMixer.ID}, needed={quantityNeeded}, inventory={inventoryCount}, fetch={state.IsFetchingPrimary}, clearSlot={state.ClearStationSlot}");

        if (!state.IsFetchingPrimary)
        {
          HandleInventorySufficient(__instance, state, inventoryCount);
          return;
        }
      }
      else if (__instance is StartCauldronBehaviour cauldronBehaviour)
      {
        Cauldron cauldron = cauldronBehaviour.Station;
        int gasolineNeeded = cauldron.LiquidSlot.Quantity < 1 ? 1 : 0;
        int gasolineInInventory = chemist.Inventory._GetItemAmount("gasoline");
        state.IsFetchingPrimary = gasolineNeeded > gasolineInInventory;
        state.QuantityToFetch = gasolineNeeded - gasolineInInventory;

        int cocaLeafNeeded = Cauldron.COCA_LEAF_REQUIRED - cauldron.IngredientSlots.Sum(slot => slot.Quantity);
        int cocaLeafInInventory = chemist.Inventory._GetItemAmount("cocaleaf");
        state.IsFetchingSecondary = cocaLeafNeeded > cocaLeafInInventory;
        if (state.IsFetchingSecondary && !state.IsFetchingPrimary)
          state.QuantityToFetch = cocaLeafNeeded - cocaLeafInInventory;

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"BehaviourPatch.PrepareToFetchItems: Cauldron, gasolineNeeded={gasolineNeeded}, gasolineInInventory={gasolineInInventory}, cocaLeafNeeded={cocaLeafNeeded}, cocaLeafInInventory={cocaLeafInInventory}, fetchPrimary={state.IsFetchingPrimary}, fetchSecondary={state.IsFetchingSecondary}");

        if (!state.IsFetchingPrimary && !state.IsFetchingSecondary)
        {
          HandleInventorySufficient(__instance, state, 0); // Inventory check handled in InsertItems
          return;
        }

        state.TargetItem = state.IsFetchingPrimary ? Registry.Instance._GetItem("gasoline").GetDefaultInstance() : Registry.Instance._GetItem("cocaleaf").GetDefaultInstance();
      }
      else
      {
        __instance.Disable();
        return;
      }

      if (state.TargetItem == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"BehaviourPatch.PrepareToFetchItems: Target item not found for {chemist.fullName}, type={__instance.GetType().Name}");
        __instance.Disable();
        return;
      }

      PlaceableStorageEntity shelf = FindShelfWithItem(chemist, state.TargetItem, state.QuantityToFetch);
      if (shelf == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"BehaviourPatch.PrepareToFetchItems: No shelf found for {state.TargetItem.ID} for {chemist.fullName}");
        __instance.Disable();
        return;
      }

      ConfigurationExtensions.NPCSupply[chemist.GUID] = new ObjectField(null) { SelectedObject = shelf };
      state.LastSupply = shelf;

      if (IsAtSupplies(__instance))
      {
        state.CurrentState = EState.GrabbingSupplies;
        GrabItem(__instance, state);
      }
      else
      {
        state.CurrentState = EState.WalkingToSupplies;
        WalkToSupplies(__instance, state);
      }
    }

    private static void HandleInventorySufficient(Behaviour __instance, StateData state, int inventoryCount)
    {
      if (IsAtStation(__instance))
      {
        int inserted = InsertItemsFromInventory(__instance, state);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"BehaviourPatch.HandleInventorySufficient: Inserted {inserted} items for {__instance.Npc?.fullName}, type={__instance.GetType().Name}");
        state.CurrentState = EState.StartingOperation;
      }
      else
      {
        state.CurrentState = EState.WalkingToStation;
        __instance.SetDestination(GetStationAccessPoint(__instance), true);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"BehaviourPatch.HandleInventorySufficient: Walking to station with {inventoryCount} inventory items for {__instance.Npc?.fullName}, type={__instance.GetType().Name}");
      }
    }

    private static bool IsAtSupplies(Behaviour __instance)
    {
      Chemist chemist = (Chemist)__instance.Npc;
      if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
        return false;

      bool atSupplies = NavMeshUtility.IsAtTransitEntity(supply.SelectedObject as ITransitEntity, chemist, 0.4f);
      if (DebugLogs.All || DebugLogs.Chemist)
      {
        Vector3 chemistPos = chemist.transform.position;
        Vector3 supplyPos = supply.SelectedObject?.transform.position ?? Vector3.zero;
        float distance = Vector3.Distance(chemistPos, supplyPos);
        MelonLogger.Msg($"BehaviourPatch.IsAtSupplies: Result={atSupplies}, chemist={chemist?.fullName ?? "null"}, ChemistPos={chemistPos}, SupplyPos={supplyPos}, Distance={distance}, type={__instance.GetType().Name}");
      }
      return atSupplies;
    }

    private static void WalkToSupplies(Behaviour __instance, StateData state)
    {
      Chemist chemist = (Chemist)__instance.Npc;
      if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"BehaviourPatch.WalkToSupplies: Supply not found for {chemist?.fullName ?? "null"}, type={__instance.GetType().Name}");
        Disable(__instance);
        return;
      }

      ITransitEntity supplyEntity = supply.SelectedObject as ITransitEntity;
      if (!chemist.Movement.CanGetTo(supplyEntity, 1f))
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"BehaviourPatch.WalkToSupplies: Cannot reach supply for {chemist?.fullName ?? "null"}, type={__instance.GetType().Name}");
        Disable(__instance);
        return;
      }

      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"BehaviourPatch.WalkToSupplies: Walking to supply {supplyEntity.Name} for {chemist?.fullName}, type={__instance.GetType().Name}, state={state.CurrentState}");

      state.WalkToSuppliesRoutine = (Coroutine)MelonCoroutines.Start(WalkRoutine(__instance, supplyEntity, state));
    }

    private static IEnumerator WalkRoutine(Behaviour __instance, ITransitEntity supply, StateData state)
    {
      Chemist chemist = (Chemist)__instance.Npc;
      Vector3 startPos = chemist.transform.position;
      __instance.SetDestination(supply, true);
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"BehaviourPatch.WalkRoutine: Set destination for {chemist?.fullName}, IsMoving={chemist.Movement.IsMoving}, type={__instance.GetType().Name}");

      yield return new WaitForSeconds(0.2f);
      float timeout = 10f;
      float elapsed = 0f;
      while (chemist.Movement.IsMoving && elapsed < timeout)
      {
        yield return null;
        elapsed += Time.deltaTime;
      }
      if (elapsed >= timeout)
      {
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"BehaviourPatch.WalkRoutine: Timeout walking to supply for {chemist?.fullName}, type={__instance.GetType().Name}");
        Disable(__instance);
      }

      state.WalkToSuppliesRoutine = null;
      state.CurrentState = EState.GrabbingSupplies;
      GrabItem(__instance, state);

      if (DebugLogs.All || DebugLogs.Chemist)
      {
        bool atSupplies = IsAtSupplies(__instance);
        Vector3 chemistPos = chemist.transform.position;
        Vector3 supplyPos = supply != null ? ((BuildableItem)supply).transform.position : Vector3.zero;
        float distanceMoved = Vector3.Distance(startPos, chemistPos);
        MelonLogger.Msg($"BehaviourPatch.WalkRoutine: Completed walk to supply for {chemist?.fullName}, AtSupplies={atSupplies}, ChemistPos={chemistPos}, SupplyPos={supplyPos}, DistanceMoved={distanceMoved}, Elapsed={elapsed}, type={__instance.GetType().Name}");
      }
    }

    private static void GrabItem(Behaviour __instance, StateData state)
    {
      Chemist chemist = (Chemist)__instance.Npc;

      ITransitEntity station = (ITransitEntity)((__instance as StartMixingStationBehaviour)?.targetStation) ?? ((__instance as StartCauldronBehaviour)?.Station);
      try
      {
        if (!ConfigurationExtensions.NPCSupply.TryGetValue(chemist.GUID, out var supply) || supply.SelectedObject == null)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"BehaviourPatch.GrabItem: Shelf not found for {chemist?.fullName}, type={__instance.GetType().Name}");
          Disable(__instance);
          return;
        }

        ITransitEntity shelf = supply.SelectedObject as ITransitEntity;
        var slots = (shelf.OutputSlots ?? Enumerable.Empty<ItemSlot>())
            .Concat(shelf.InputSlots ?? Enumerable.Empty<ItemSlot>())
            .Where(s => s?.ItemInstance != null && s.Quantity > 0 && s.ItemInstance.ID.ToLower() == state.TargetItem.ID.ToLower())
            .ToList();

        if (!slots.Any())
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"BehaviourPatch.GrabItem: No slots with {state.TargetItem.ID} in shelf {shelf.GUID}, type={__instance.GetType().Name}");
          Disable(__instance);
          return;
        }

        // Station-specific output check
        if (__instance is StartMixingStationBehaviour mixingBehaviour && mixingBehaviour.targetStation.OutputSlot.Quantity > 0)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"BehaviourPatch.GrabItem: Output quantity {mixingBehaviour.targetStation.OutputSlot.Quantity} > 0, disabling for {chemist?.fullName}");
          Disable(__instance);
          return;
        }

        // Clear station slot if needed (mixing station only)
        if (__instance is StartMixingStationBehaviour mixing && state.ClearStationSlot && mixing.targetStation.MixerSlot.ItemInstance != null)
        {
          int currentQuantity = mixing.targetStation.MixerSlot.Quantity;
          chemist.Inventory.InsertItem(mixing.targetStation.MixerSlot.ItemInstance.GetCopy(currentQuantity));
          mixing.targetStation.MixerSlot.SetQuantity(0);
          state.ClearStationSlot = false;
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"BehaviourPatch.GrabItem: Cleared MixerSlot ({currentQuantity} items returned to inventory) for {chemist?.fullName}");
        }

        int totalAvailable = slots.Sum(s => s.Quantity);
        int quantityToFetch = Mathf.Min(state.QuantityToFetch, totalAvailable);

        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"BehaviourPatch.GrabItem: Available {totalAvailable}/{state.QuantityToFetch} for {state.TargetItem.ID}, type={__instance.GetType().Name}");

        if (quantityToFetch <= 0)
        {
          Disable(__instance);
          return;
        }

        int remainingToFetch = quantityToFetch;
        foreach (var slot in slots)
        {
          int amountToTake = Mathf.Min(slot.Quantity, remainingToFetch);
          if (amountToTake > 0)
          {
            chemist.Inventory.InsertItem(slot.ItemInstance.GetCopy(amountToTake));
            slot.ChangeQuantity(-amountToTake, false);
            remainingToFetch -= amountToTake;
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Msg($"BehaviourPatch.GrabItem: Took {amountToTake} of {slot.ItemInstance.ID} from slot, type={__instance.GetType().Name}");
          }
          if (remainingToFetch <= 0)
            break;
        }

        if (remainingToFetch > 0)
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"BehaviourPatch.GrabItem: Could not fetch full quantity, still need {remainingToFetch} of {state.TargetItem.ID}, type={__instance.GetType().Name}");
        }

        state.GrabRoutine = (Coroutine)MelonCoroutines.Start(GrabRoutine(__instance, state));
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"BehaviourPatch.GrabItem: Failed for {chemist?.fullName}, type={__instance.GetType().Name}, error: {e}");
        Disable(__instance);
      }
    }

    private static IEnumerator GrabRoutine(Behaviour __instance, StateData state)
    {
      Chemist chemist = (Chemist)__instance.Npc;
      yield return new WaitForSeconds(0.2f);
      try
      {
        if (chemist.Avatar?.Anim != null)
        {
          chemist.Avatar.Anim.ResetTrigger("GrabItem");
          chemist.Avatar.Anim.SetTrigger("GrabItem");
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"BehaviourPatch.GrabRoutine: Triggered GrabItem animation for {chemist?.fullName}, type={__instance.GetType().Name}");
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Warning($"BehaviourPatch.GrabRoutine: Animator missing for {chemist?.fullName}, skipping animation, type={__instance.GetType().Name}");
        }
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"BehaviourPatch.GrabRoutine: Failed for {chemist?.fullName}, type={__instance.GetType().Name}, error: {e}");
        Disable(__instance);
      }
      yield return new WaitForSeconds(0.2f);

      state.GrabRoutine = null;

      // Update fetching flags and fetch next item if needed (cauldron only)
      if (__instance is StartCauldronBehaviour cauldronBehaviour)
      {
        if (state.IsFetchingPrimary)
          state.IsFetchingPrimary = false;
        else if (state.IsFetchingSecondary)
          state.IsFetchingSecondary = false;

        if (state.IsFetchingPrimary || state.IsFetchingSecondary)
        {
          PrepareToFetchItems(__instance, state);
          yield break;
        }
      }
      else
      {
        state.IsFetchingPrimary = false; // Mixing station only fetches one item
      }

      state.CurrentState = EState.WalkingToStation;
      __instance.SetDestination(GetStationAccessPoint(__instance), true);
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"BehaviourPatch.GrabRoutine: Grab complete, walking to station for {chemist?.fullName}, type={__instance.GetType().Name}");
    }

    public static int InsertItemsFromInventory(Behaviour __instance, StateData state)
    {
      Chemist chemist = (Chemist)__instance.Npc;
      try
      {
        if (__instance is StartMixingStationBehaviour mixingBehaviour)
        {
          MixingStation station = mixingBehaviour.targetStation;
          if (state.TargetItem == null || station.MixerSlot == null)
          {
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Warning($"BehaviourPatch.InsertItemsFromInventory: TargetItem or MixerSlot is null for {chemist?.fullName}");
            return 0;
          }

          int quantity = chemist.Inventory._GetItemAmount(state.TargetItem.ID);
          if (quantity <= 0)
          {
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Warning($"BehaviourPatch.InsertItemsFromInventory: No items of {state.TargetItem.ID} in inventory for {chemist?.fullName}");
            return 0;
          }

          ItemInstance item = state.TargetItem;
          int currentQuantity = station.MixerSlot.Quantity;
          station.MixerSlot.InsertItem(item.GetCopy(quantity));
          int newQuantity = station.MixerSlot.Quantity;

          int quantityToRemove = quantity;
          List<(ItemSlot slot, int amount)> toRemove = [];
          foreach (ItemSlot slot in chemist.Inventory.ItemSlots)
          {
            if (slot?.ItemInstance != null && slot.ItemInstance.ID == state.TargetItem.ID && slot.Quantity > 0)
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
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Warning($"BehaviourPatch.InsertItemsFromInventory: Failed to remove {quantityToRemove} of {state.TargetItem.ID}, reverting MixerSlot for {chemist?.fullName}");
            station.MixerSlot.SetQuantity(currentQuantity);
            return 0;
          }

          if (DebugLogs.All || DebugLogs.Chemist)
            MelonLogger.Msg($"BehaviourPatch.InsertItemsFromInventory: Inserted {quantity} of {state.TargetItem.ID} into MixerSlot, quantity changed from {currentQuantity} to {newQuantity} for {chemist?.fullName}");
          return quantity;
        }
        else if (__instance is StartCauldronBehaviour cauldronBehaviour)
        {
          Cauldron cauldron = cauldronBehaviour.Station;
          int inserted = 0;

          // Insert gasoline
          if (cauldron.LiquidSlot.Quantity < 1)
          {
            int gasolineInInventory = chemist.Inventory._GetItemAmount("gasoline");
            if (gasolineInInventory >= 1)
            {
              ItemInstance gasoline = Registry.Instance._GetItem("gasoline").GetDefaultInstance(1);
              cauldron.LiquidSlot.InsertItem(gasoline.GetCopy(1));
              RemoveItem(chemist, 1, state, gasoline.ID);
              inserted += 1;
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Msg($"BehaviourPatch.InsertItemsFromInventory: Inserted 1 gasoline into LiquidSlot for {chemist?.fullName}");
            }
            else
            {
              if (DebugLogs.All || DebugLogs.Chemist)
                MelonLogger.Warning($"BehaviourPatch.InsertItemsFromInventory: Insufficient gasoline in inventory for {chemist?.fullName}");
              return 0;
            }
          }

          // Insert coca leaves
          int cocaLeafNeeded = Cauldron.COCA_LEAF_REQUIRED - cauldron.IngredientSlots.Sum(slot => slot.Quantity);
          int cocaLeafInInventory = chemist.Inventory._GetItemAmount("cocaleaf");
          if (cocaLeafInInventory >= cocaLeafNeeded)
          {
            ItemInstance cocaLeaf = Registry.Instance._GetItem("cocaleaf").GetDefaultInstance(cocaLeafNeeded);
            foreach (var slot in cauldron.IngredientSlots)
            {
              int space = slot.GetCapacityForItem(cocaLeaf) - slot.Quantity;
              if (space > 0)
              {
                int amountToInsert = Mathf.Min(space, cocaLeafNeeded);
                slot.InsertItem(cocaLeaf.GetCopy(amountToInsert));
                cocaLeafNeeded -= amountToInsert;
                inserted += amountToInsert;
                if (DebugLogs.All || DebugLogs.Chemist)
                  MelonLogger.Msg($"BehaviourPatch.InsertItemsFromInventory: Inserted {amountToInsert} coca leaves into IngredientSlot for {chemist?.fullName}");
              }
              if (cocaLeafNeeded <= 0)
                break;
            }
            RemoveItem(chemist, Cauldron.COCA_LEAF_REQUIRED - cauldron.IngredientSlots.Sum(slot => slot.Quantity), state, "cocaleaf");
          }
          else
          {
            if (DebugLogs.All || DebugLogs.Chemist)
              MelonLogger.Warning($"BehaviourPatch.InsertItemsFromInventory: Insufficient coca leaves in inventory for {chemist?.fullName}");
            return 0;
          }

          return inserted;
        }

        return 0;
      }
      catch (System.Exception e)
      {
        MelonLogger.Error($"BehaviourPatch.InsertItemsFromInventory: Failed for {chemist?.fullName}, item={state.TargetItem?.ID ?? "null"}, type={__instance.GetType().Name}, error: {e}");
        return 0;
      }
    }

    private static void RemoveItem(NPC npc, int quantity, StateData state, string id = "")
    {
      string targetItem = id != "" ? id : state.TargetItem.ID;
      // Remove from inventory
      int quantityToRemove = quantity;
      List<(ItemSlot slot, int amount)> toRemove = [];
      foreach (ItemSlot slot in npc.Inventory.ItemSlots)
      {
        if (slot?.ItemInstance != null && slot.ItemInstance.ID == targetItem && slot.Quantity > 0)
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
    }

    public static void Disable(Behaviour __instance)
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
        if (state.InsertRoutine != null)
        {
          MelonCoroutines.Stop(state.InsertRoutine);
          state.InsertRoutine = null;
        }
        state.TargetItem = null;
        state.ClearStationSlot = false;
        state.QuantityToFetch = 0;
        state.IsFetchingPrimary = false;
        state.IsFetchingSecondary = false;
        state.CurrentState = EState.Idle;
        state.LastSupply = null;
        states.Remove(__instance);
      }
      __instance.Disable();
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"BehaviourPatch.Disable: Disabled behaviour for {__instance.Npc?.fullName ?? "null"}, type={__instance.GetType().Name}");
    }

    public static bool IsAtStation(Behaviour __instance)
    {
      ITransitEntity station = (ITransitEntity)(__instance as StartMixingStationBehaviour)?.targetStation ?? (__instance as StartCauldronBehaviour)?.Station;
      bool atStation = station != null && Vector3.Distance(__instance.Npc.transform.position, GetStationAccessPoint(__instance)) < 1f;
      if (DebugLogs.All || DebugLogs.Chemist)
        MelonLogger.Msg($"BehaviourPatch.IsAtStation: Result={atStation}, chemist={__instance.Npc?.fullName ?? "null"}, type={__instance.GetType().Name}");
      return atStation;
    }

    public static Vector3 GetStationAccessPoint(Behaviour __instance)
    {
      ITransitEntity station = (ITransitEntity)(__instance as StartMixingStationBehaviour)?.targetStation ?? (__instance as StartCauldronBehaviour)?.Station;
      return station?.AccessPoints.FirstOrDefault()?.position ?? __instance.Npc.transform.position;
    }

    // Initialize state for Chemist behaviors
    [HarmonyPatch("Awake")]
    [HarmonyPostfix]
    static void AwakePostfix(Chemist __instance)
    {
      try
      {
        if (!states.ContainsKey(__instance.StartCauldronBehaviour))
        {
          states[__instance.StartCauldronBehaviour] = new StateData { CurrentState = EState.Idle };
        }
        if (!states.ContainsKey(__instance.StartMixingStationBehaviour))
        {
          states[__instance.StartMixingStationBehaviour] = new StateData { CurrentState = EState.Idle };
        }
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.Awake: Initialized states for {__instance?.fullName ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.Awake: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}");
      }
    }

    // Patch TryStartNewTask to orchestrate tasks
    [HarmonyPatch("TryStartNewTask")]
    [HarmonyPrefix]
    static bool TryStartNewTaskPrefix(Chemist __instance)
    {
      if (!InstanceFinder.IsServer)
        return false;

      try
      {
        // Check for ovens ready to finish
        List<LabOven> labOvensReadyToFinish = __instance.GetLabOvensReadyToFinish();
        if (labOvensReadyToFinish.Count > 0)
        {
          __instance.FinishLabOven(labOvensReadyToFinish[0]);
          return false;
        }

        // Check for stations ready to start
        List<LabOven> labOvensReadyToStart = __instance.GetLabOvensReadyToStart();
        if (labOvensReadyToStart.Count > 0)
        {
          __instance.StartLabOven(labOvensReadyToStart[0]);
          return false;
        }

        List<ChemistryStation> chemistryStationsReadyToStart = __instance.GetChemistryStationsReadyToStart();
        if (chemistryStationsReadyToStart.Count > 0)
        {
          __instance.StartChemistryStation(chemistryStationsReadyToStart[0]);
          return false;
        }

        List<Cauldron> cauldronsReadyToStart = __instance.GetCauldronsReadyToStart();
        if (cauldronsReadyToStart.Count > 0)
        {
          __instance.StartCauldron(cauldronsReadyToStart[0]);
          return false;
        }

        List<MixingStation> mixingStationsReadyToStart = __instance.GetMixingStationsReadyToStart();
        if (mixingStationsReadyToStart.Count > 0)
        {
          __instance.StartMixingStation(mixingStationsReadyToStart[0]);
          return false;
        }

        // Check for stations with outputs to move
        List<LabOven> labOvensReadyToMove = __instance.GetLabOvensReadyToMove();
        if (labOvensReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, labOvensReadyToMove[0].OutputSlot.ItemInstance);
          return false;
        }

        List<ChemistryStation> chemStationsReadyToMove = __instance.GetChemStationsReadyToMove();
        if (chemStationsReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, chemStationsReadyToMove[0].OutputSlot.ItemInstance);
          return false;
        }

        List<Cauldron> cauldronsReadyToMove = __instance.GetCauldronsReadyToMove();
        if (cauldronsReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, cauldronsReadyToMove[0].OutputSlot.ItemInstance);
          return false;
        }

        List<MixingStation> mixStationsReadyToMove = __instance.GetMixStationsReadyToMove();
        if (mixStationsReadyToMove.Count > 0)
        {
          MoveOutputToShelf(__instance, mixStationsReadyToMove[0].OutputSlot.ItemInstance);
          return false;
        }

        __instance.SubmitNoWorkReason("No tasks available.", string.Empty, 0);
        __instance.SetIdle(true);
        return false;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ChemistPatch.TryStartNewTask: Failed for chemist: {__instance?.fullName ?? "null"}, error: {e}");
        __instance.SubmitNoWorkReason("Task assignment error.", string.Empty, 0);
        __instance.SetIdle(true);
        return false;
      }
    }
    private static void MoveOutputToShelf(Chemist chemist, ItemInstance outputItem)
    {
      PlaceableStorageEntity shelf = FindShelfForDelivery(chemist, outputItem);
      if (shelf == null)
      {
        chemist.SubmitNoWorkReason($"No shelf for {outputItem.ID}.", string.Empty, 0);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"ChemistPatch.MoveOutputToShelf: No shelf found for {outputItem.ID} for {chemist?.fullName ?? "null"}");
        return;
      }

      TransitRoute route = new TransitRoute(null, shelf);
      if (chemist.MoveItemBehaviour.IsTransitRouteValid(route, outputItem.ID))
      {
        chemist.MoveItemBehaviour.Initialize(route, outputItem);
        chemist.MoveItemBehaviour.Enable_Networked(null);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Msg($"ChemistPatch.MoveOutputToShelf: Moving {outputItem.ID} to shelf {shelf.GUID} for {chemist?.fullName ?? "null"}");
      }
      else
      {
        chemist.SubmitNoWorkReason($"Invalid route to shelf for {outputItem.ID}.", string.Empty, 0);
        if (DebugLogs.All || DebugLogs.Chemist)
          MelonLogger.Warning($"ChemistPatch.MoveOutputToShelf: Invalid route to shelf {shelf.GUID} for {outputItem.ID} for {chemist?.fullName ?? "null"}");
      }
    }
  }

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
  }
}