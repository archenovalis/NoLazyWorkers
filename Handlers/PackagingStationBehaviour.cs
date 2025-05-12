using FishNet;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using NoLazyWorkers.General;
using ScheduleOne;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Product;
using ScheduleOne.Product.Packaging;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using static NoLazyWorkers.General.GeneralExtensions;
using static NoLazyWorkers.General.StorageUtilities;

namespace NoLazyWorkers.Handlers
{
  [HarmonyPatch(typeof(PackagingStationBehaviour))]
  public class PackagingStationBehaviourPatch
  {
    private static readonly string JAR_ITEM_ID = "jar";
    private static readonly string BAGGIE_ITEM_ID = "baggie";
    private static readonly int BAGGIE_THRESHOLD = 4;
    private static readonly int BAGGIE_UNPACKAGE_THRESHOLD = 5;
    private static readonly Dictionary<Guid, bool> _isFetchingPackaging = new();

    [HarmonyPrefix]
    [HarmonyPatch("IsStationReady")]
    static bool IsStationReadyPrefix(PackagingStationBehaviour __instance, PackagingStation station, ref bool __result)
    {
      if (station == null || !PackagingStationExtensions.Config.TryGetValue(station.GUID, out var config))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"IsStationReadyPrefix: Invalid station or configuration for {station?.GUID}", DebugLogger.Category.Handler);
        __result = false;
        return false;
      }

      var adapter = new PackagingStationAdapter(station);
      var npc = __instance.Npc as Packager;
      if (npc == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"IsStationReadyPrefix: NPC is not a Packager for station {station.GUID}", DebugLogger.Category.Handler);
        __result = false;
        return false;
      }

      bool hasProducts = adapter.ProductSlots.Any(s => s.ItemInstance != null && s.Quantity > 0 &&
                                                      s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID);
      if (!hasProducts)
      {
        __result = false;
        return false;
      }

      if (_isFetchingPackaging.ContainsKey(station.GUID) && _isFetchingPackaging[station.GUID])
      {
        __result = false;
        return false;
      }

      bool hasPackaging = CheckPackagingAvailability(adapter, npc, out string requiredPackagingId);
      if (hasPackaging)
      {
        __result = true;
        return false;
      }

      bool initiatedRetrieval = InitiatePackagingRetrieval(adapter, npc, requiredPackagingId);
      if (initiatedRetrieval)
      {
        _isFetchingPackaging[station.GUID] = true;
        __result = false;
        return false;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Warning, $"IsStationReadyPrefix: Failed to initiate packaging retrieval for station {station.GUID}", DebugLogger.Category.Handler);
      __result = false;
      return false;
    }

    private static bool CheckPackagingAvailability(IStationAdapter<PackagingStation> adapter, Packager npc, out string requiredPackagingId)
    {
      int productCount = adapter.ProductSlots
          .Where(s => s.ItemInstance != null && s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID)
          .Sum(s => s.Quantity);

      bool preferBaggies = productCount <= BAGGIE_THRESHOLD;
      requiredPackagingId = preferBaggies ? BAGGIE_ITEM_ID : JAR_ITEM_ID;
      var packagingSlot = adapter.InsertSlot;

      if (packagingSlot != null && packagingSlot.Quantity > 0 && packagingSlot.ItemInstance.ID == JAR_ITEM_ID)
        return true;

      if (packagingSlot != null && packagingSlot.Quantity > 0 && packagingSlot.ItemInstance.ID == BAGGIE_ITEM_ID &&
          (preferBaggies || packagingSlot.ItemInstance.ID != JAR_ITEM_ID))
        return true;

      if (!preferBaggies && CheckBaggieUnpackaging(adapter, npc))
      {
        requiredPackagingId = JAR_ITEM_ID;
        return false;
      }

      return false;
    }

    private static bool CheckBaggieUnpackaging(IStationAdapter<PackagingStation> adapter, Packager npc)
    {
      var baggieSlot = adapter.InsertSlot;
      if (baggieSlot == null || baggieSlot.Quantity < BAGGIE_UNPACKAGE_THRESHOLD || baggieSlot.ItemInstance.ID != BAGGIE_ITEM_ID)
        return false;

      int unpackCount = baggieSlot.Quantity / BAGGIE_UNPACKAGE_THRESHOLD;
      for (int i = 0; i < unpackCount; i++)
      {
        baggieSlot.ChangeQuantity(-BAGGIE_UNPACKAGE_THRESHOLD, false);

        var productSlot = adapter.ProductSlots.FirstOrDefault(s => s.ItemInstance != null && s.Quantity > 0 &&
                                                                 s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID);
        if (productSlot != null)
        {
          productSlot.ApplyLock(npc.NetworkObject, "Unpacking baggies");
          productSlot.ChangeQuantity(BAGGIE_UNPACKAGE_THRESHOLD, false);
          productSlot.RemoveLock();
        }

        var baggieItem = Registry.GetItem(BAGGIE_ITEM_ID).GetDefaultInstance();
        if (npc.Inventory.HowManyCanFit(baggieItem) >= BAGGIE_UNPACKAGE_THRESHOLD)
        {
          var shelf = FindShelfForDelivery(npc, baggieItem);
          if (shelf != null)
          {
            var route = new TransitRoute(adapter as ITransitEntity, shelf);
            npc.MoveItemBehaviour.Initialize(route, baggieItem, BAGGIE_UNPACKAGE_THRESHOLD, true);
            npc.MoveItemBehaviour.Enable_Networked(null);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"CheckBaggieUnpackaging: Unpackaged {BAGGIE_UNPACKAGE_THRESHOLD} baggies, returning to shelf {shelf.GUID}", DebugLogger.Category.Handler);
          }
        }
      }
      return unpackCount > 0;
    }

    private static bool InitiatePackagingRetrieval(IStationAdapter<PackagingStation> adapter, Packager npc, string packagingItemId)
    {
      var packagingItem = Registry.GetItem(packagingItemId).GetDefaultInstance();
      var shelves = FindShelvesWithItem(npc, packagingItem, needed: 1);
      var shelf = shelves.Keys.FirstOrDefault(s => npc.movement.CanGetTo(s) &&
                                             (s as ITransitEntity).GetFirstSlotContainingTemplateItem(packagingItem, ITransitEntity.ESlotType.Output) != null);
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitiatePackagingRetrieval: No shelf found with {packagingItemId} for station {adapter.GUID}", DebugLogger.Category.Handler);
        return false;
      }

      var sourceSlot = (shelf as ITransitEntity).GetFirstSlotContainingTemplateItem(packagingItem, ITransitEntity.ESlotType.Output);
      if (sourceSlot == null)
        return false;

      var transitEntity = adapter as ITransitEntity;
      if (transitEntity == null)
        return false;

      var deliverySlots = transitEntity.ReserveInputSlotsForItem(packagingItem, npc.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
        return false;

      var quantity = Mathf.Min(sourceSlot.Quantity, adapter.MaxProductQuantity - adapter.GetInputQuantity());
      if (quantity <= 0 || npc.Inventory.HowManyCanFit(packagingItem) < quantity)
        return false;

      var route = new TransitRoute(shelf, transitEntity);
      npc.MoveItemBehaviour.Initialize(route, packagingItem, quantity, false);
      npc.MoveItemBehaviour.Enable_Networked(null);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"InitiatePackagingRetrieval: Packager fetching {quantity} {packagingItemId} from shelf {shelf.GUID} to station {adapter.GUID}", DebugLogger.Category.Handler);
      return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch("RpcLogic___BeginPackaging_2166136261")]
    static void BeginPackagingPostfix(PackagingStationBehaviour __instance)
    {
      if (!InstanceFinder.IsServer || __instance.Station == null || !(__instance.Npc is Packager npc))
        return;

      var adapter = new PackagingStationAdapter(__instance.Station);
      var outputSlot = adapter.OutputSlot;

      if (outputSlot?.ItemInstance == null || outputSlot.Quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"BeginPackagingPostfix: No packaged item found in output slot for station {__instance.Station.GUID}", DebugLogger.Category.Handler);
        return;
      }

      var packagedItem = outputSlot.ItemInstance;
      var quantity = outputSlot.Quantity;

      var shelf = FindShelvesWithItem(npc, packagedItem, needed: quantity)
          .Keys.FirstOrDefault(s => npc.movement.CanGetTo(s) &&
                                   packagedItem.CanStackWith((s as ITransitEntity).GetFirstSlotContainingTemplateItem(packagedItem, ITransitEntity.ESlotType.Input)?.ItemInstance, checkQuantities: false));

      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"BeginPackagingPostfix: No suitable shelf found for packaged item {packagedItem.ID}", DebugLogger.Category.Handler);
        return;
      }

      var deliverySlots = (shelf as ITransitEntity).ReserveInputSlotsForItem(packagedItem, npc.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"BeginPackagingPostfix: Failed to reserve slots on shelf {shelf.GUID} for {packagedItem.ID}", DebugLogger.Category.Handler);
        return;
      }

      var route = new TransitRoute(adapter as ITransitEntity, shelf);
      npc.MoveItemBehaviour.Initialize(route, packagedItem, quantity, true);
      npc.MoveItemBehaviour.Enable_Networked(null);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"BeginPackagingPostfix: Delivering {quantity} of packaged item {packagedItem.ID} to shelf {shelf.GUID}", DebugLogger.Category.Handler);

      outputSlot.ChangeQuantity(-quantity, false);
      _isFetchingPackaging.Remove(adapter.GUID);
    }
  }
}