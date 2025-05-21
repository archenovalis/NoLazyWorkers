using FishNet;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using NoLazyWorkers.Employees;
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
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.General.StorageUtilities;

namespace NoLazyWorkers.Stations
{
  public class PackagingBehaviour : EmployeeBehaviour
  {
    private static readonly string JAR_ITEM_ID = "jar";
    private static readonly string BAGGIE_ITEM_ID = "baggie";
    private static readonly int BAGGIE_THRESHOLD = 4;
    private static readonly int BAGGIE_UNPACKAGE_THRESHOLD = 5;
    private static readonly Dictionary<Guid, bool> _isFetchingPackaging = new();
    private readonly Packager _packager;
    private readonly IStationAdapter _stationAdapter;

    public PackagingBehaviour(Packager packager, IStationAdapter station, IEmployeeAdapter employee) : base(packager, employee)
    {
      _packager = packager ?? throw new ArgumentNullException(nameof(packager));
      _stationAdapter = station ?? throw new ArgumentNullException(nameof(station));
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagingStationBehaviour: Initialized for NPC {packager.fullName}, station {station.GUID}", DebugLogger.Category.Packager);
    }

    public bool IsStationReady(PackagingStation station)
    {
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"IsStationReady: Invalid station", DebugLogger.Category.Packager);
        return false;
      }
      if (!StationAdapters.TryGetValue(station.GUID, out var adapter))
      {
        adapter = new PackagingStationAdapter(station);
        StationAdapters[station.GUID] = adapter;
      }
      bool hasProducts = adapter.ProductSlots.Any(s => s.ItemInstance != null && s.Quantity > 0 &&
          s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID);
      if (!hasProducts)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsStationReady: No products in station {station.GUID}", DebugLogger.Category.Packager);
        return false;
      }
      if (_isFetchingPackaging.ContainsKey(station.GUID) && _isFetchingPackaging[station.GUID])
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsStationReady: Fetching packaging for station {station.GUID}", DebugLogger.Category.Packager);
        return false;
      }
      bool hasPackaging = CheckPackagingAvailability(adapter, _packager, out string requiredPackagingId);
      if (hasPackaging)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"IsStationReady: Station {station.GUID} ready with packaging {requiredPackagingId}", DebugLogger.Category.Packager);
        return true;
      }
      bool initiatedRetrieval = InitiatePackagingRetrieval(adapter, _packager, requiredPackagingId);
      if (initiatedRetrieval)
      {
        _isFetchingPackaging[station.GUID] = true;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"IsStationReady: Initiated packaging retrieval for station {station.GUID}", DebugLogger.Category.Packager);
        return false;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Warning, $"IsStationReady: Failed to initiate packaging retrieval for station {station.GUID}", DebugLogger.Category.Packager);
      return false;
    }

    private bool CheckPackagingAvailability(IStationAdapter adapter, Packager npc, out string requiredPackagingId)
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

    private bool CheckBaggieUnpackaging(IStationAdapter adapter, Packager npc)
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
          var shelf = StorageUtilities.FindShelfForDelivery(npc, baggieItem);
          if (shelf != null)
          {
            var route = new TransitRoute(adapter as ITransitEntity, shelf);
            npc.MoveItemBehaviour.Initialize(route, baggieItem, BAGGIE_UNPACKAGE_THRESHOLD, true);
            npc.MoveItemBehaviour.Enable_Networked(null);
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"CheckBaggieUnpackaging: Unpackaged {BAGGIE_UNPACKAGE_THRESHOLD} baggies, returning to shelf {shelf.GUID}", DebugLogger.Category.Packager);
          }
        }
      }
      return unpackCount > 0;
    }

    private bool InitiatePackagingRetrieval(IStationAdapter adapter, Packager npc, string packagingItemId)
    {
      var packagingItem = Registry.GetItem(packagingItemId).GetDefaultInstance();
      var shelves = StorageUtilities.FindShelvesWithItem(npc, packagingItem, needed: 1);
      var shelf = shelves.Keys.FirstOrDefault(s => npc.Movement.CanGetTo(s) &&
          (s as ITransitEntity).GetFirstSlotContainingTemplateItem(packagingItem, ITransitEntity.ESlotType.Output) != null);
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitiatePackagingRetrieval: No shelf found with {packagingItemId} for station {adapter.GUID}", DebugLogger.Category.Packager);
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
          $"InitiatePackagingRetrieval: Packager fetching {quantity} {packagingItemId} from shelf {shelf.GUID} to station {adapter.GUID}", DebugLogger.Category.Packager);
      return true;
    }

    public void HandleBeginPackaging(PackagingStation station)
    {
      if (!InstanceFinder.IsServer || station == null)
        return;
      if (!StationAdapters.TryGetValue(station.GUID, out var adapter))
      {
        adapter = new PackagingStationAdapter(station);
        StationAdapters[station.GUID] = adapter;
      }
      var outputSlot = adapter.OutputSlot;
      if (outputSlot?.ItemInstance == null || outputSlot.Quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleBeginPackaging: No packaged item found in output slot for station {station.GUID}", DebugLogger.Category.Packager);
        return;
      }
      var packagedItem = outputSlot.ItemInstance;
      var quantity = outputSlot.Quantity;
      var shelf = StorageUtilities.FindShelvesWithItem(_packager, packagedItem, needed: quantity)
          .Keys.FirstOrDefault(s => _packager.Movement.CanGetTo(s) &&
              packagedItem.CanStackWith((s as ITransitEntity).GetFirstSlotContainingTemplateItem(packagedItem, ITransitEntity.ESlotType.Input)?.ItemInstance, checkQuantities: false));
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleBeginPackaging: No suitable shelf found for packaged item {packagedItem.ID}", DebugLogger.Category.Packager);
        return;
      }
      var deliverySlots = (shelf as ITransitEntity).ReserveInputSlotsForItem(packagedItem, _packager.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleBeginPackaging: Failed to reserve slots on shelf {shelf.GUID} for {packagedItem.ID}", DebugLogger.Category.Packager);
        return;
      }
      var route = new TransitRoute(adapter as ITransitEntity, shelf);
      _packager.MoveItemBehaviour.Initialize(route, packagedItem, quantity, true);
      _packager.MoveItemBehaviour.Enable_Networked(null);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"HandleBeginPackaging: Delivering {quantity} of packaged item {packagedItem.ID} to shelf {shelf.GUID}", DebugLogger.Category.Packager);
      outputSlot.ChangeQuantity(-quantity, false);
      _isFetchingPackaging.Remove(adapter.GUID);
    }
  }
}