using NoLazyWorkers.Employees;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using System.Threading.Tasks;
using Unity.Services.Qos.Internal;

namespace NoLazyWorkers.Stations
{
  public class PackagingStationBeh(Packager packager, IEmployeeAdapter adapter) : PackagerBehaviour(packager, adapter)
  {
    public class PackagingStation_Work : IEmployeeTask
    {
      public static readonly string JAR_ITEM_ID = "jar";
      public static readonly string BAGGIE_ITEM_ID = "baggie";
      private static readonly int BAGGIE_THRESHOLD = 4;
      private static readonly int BAGGIE_UNPACKAGE_THRESHOLD = 5;
      private readonly int _priority;
      private readonly int _scanIndex;
      public int Priority => _priority;
      public int ScanIndex => _scanIndex;

      public PackagingStation_Work(int priority, int scanIndex)
      {
        _priority = priority;
        _scanIndex = scanIndex;
      }

      private enum PackagingStep
      {
        CheckStation,
        FetchPackaging,
        MovingToShelf,
        MovingToStation,
        UnpackageBaggies,
        StartPackaging,
        DeliverOutput
      }

      public async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
      {
        if (!(employee is Packager packager))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "PackagingStation_OperateTask.CanExecute: Invalid packager or state", DebugLogger.Category.Packager);
          return false;
        }
        var state = GetState(employee);
        foreach (var station in packager.configuration.AssignedStations ?? Enumerable.Empty<PackagingStation>())
        {
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            stationAdapter = new PackagingStationAdapter(station);
            StationAdapters[station.GUID] = stationAdapter;
          }
          if (!stationAdapter.IsInUse && !stationAdapter.HasActiveOperation && await IsStationReady(stationAdapter, packager, state))
          {
            state.Station = stationAdapter;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagingStation_OperateTask.CanExecute: Station {station.GUID} ready for {packager.fullName}", DebugLogger.Category.Packager);
            return true;
          }
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagingStation_OperateTask.CanExecute: Station {station.GUID} not ready (InUse={stationAdapter.IsInUse}, HasActiveOperation={stationAdapter.HasActiveOperation})", DebugLogger.Category.Packager);
        }
        if (!state.Station.IsInUse && !state.Station.HasActiveOperation && await IsStationReady(state.Station, packager, state))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagingStation_OperateTask.CanExecute: Current station {state.Station.GUID} ready for {packager.fullName}", DebugLogger.Category.Packager);
          return true;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PackagingStation_OperateTask.CanExecute: No stations ready for {packager.fullName}", DebugLogger.Category.Packager);
        return false;
      }

      public async Task Execute(Employee employee, StateData state)
      {
        if (!(employee is Packager packager) || state == null || state.Station == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PackagingStation_OperateTask.Execute: Invalid packager, state, or station", DebugLogger.Category.Packager);
          state.CurrentState = EState.Idle;
          return;
        }

        if (!state.TryGetValue<PackagingStep>("PackagingStep", out var currentStep))
        {
          currentStep = PackagingStep.CheckStation;
          state.SetValue("PackagingStep", currentStep);
        }

        switch (currentStep)
        {
          case PackagingStep.CheckStation:
            await HandleCheckStation(packager, state);
            break;
          case PackagingStep.FetchPackaging:
            HandleFetchPackaging(packager, state);
            break;
          case PackagingStep.MovingToShelf:
          case PackagingStep.MovingToStation:
            // Movement handled by EmployeeBehaviour
            break;
          case PackagingStep.UnpackageBaggies:
            await HandleUnpackageBaggies(packager, state);
            break;
          case PackagingStep.StartPackaging:
            await HandleStartPackaging(packager, state);
            break;
          case PackagingStep.DeliverOutput:
            await HandleDeliverOutput(packager, state);
            break;
        }
      }

      private async Task<bool> IsStationReady(IStationAdapter adapter, Packager packager, StateData state)
      {
        int productCount = 0;
        foreach (ItemSlot slot in adapter.ProductSlots)
        {
          if (slot != null && slot.ItemInstance != null &&
              slot.ItemInstance.ID != JAR_ITEM_ID && slot.ItemInstance.ID != BAGGIE_ITEM_ID)
          {
            productCount += slot.Quantity;
          }
        }

        IEnumerable<ItemInstance> refillItems = adapter.RefillList();
        bool canRefillProducts = productCount < adapter.MaxProductQuantity;

        if (canRefillProducts)
        {
          foreach (ItemInstance item in refillItems)
          {
            KeyValuePair<PlaceableStorageEntity, int> shelfResult = await FindShelfWithItemAsync(packager, item, 1);
            bool found = shelfResult.Key != null;
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, string.Format("IsStationReady: Checking item {0} for station {1}, productCount={2}/{3}, shelvesFound={4}", item.ID, adapter.GUID, productCount, adapter.MaxProductQuantity, found), DebugLogger.Category.Packager);
            if (found)
            {
              canRefillProducts = true;
              break;
            }
          }
        }

        if (canRefillProducts)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, string.Format("IsStationReady: Station {0} can be refilled (productCount={1}/{2})", adapter.GUID, productCount, adapter.MaxProductQuantity), DebugLogger.Category.Packager);
          return true;
        }

        bool isFetching;
        if (IsFetchingPackaging.TryGetValue(adapter.GUID, out isFetching) && isFetching)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, string.Format("IsStationReady: Station {0} is fetching packaging", adapter.GUID), DebugLogger.Category.Packager);
          return false;
        }

        bool hasProducts = productCount > 0;
        if (!hasProducts)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, string.Format("IsStationReady: Station {0} has no products", adapter.GUID), DebugLogger.Category.Packager);
          return false;
        }

        var packagingResult = await CheckPackagingAvailability(adapter, packager);
        bool hasPackaging = packagingResult.Item1;
        string requiredPackagingId = packagingResult.Item2;

        if (hasPackaging)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, string.Format("IsStationReady: Station {0} has packaging ({1})", adapter.GUID, requiredPackagingId), DebugLogger.Category.Packager);
          return true;
        }

        bool initiatedRetrieval = await InitiatePackagingRetrieval(adapter, packager, requiredPackagingId, state);
        if (initiatedRetrieval)
        {
          IsFetchingPackaging[adapter.GUID] = true;
          DebugLogger.Log(DebugLogger.LogLevel.Info, string.Format("IsStationReady: Initiated packaging retrieval for {0} at station {1}", requiredPackagingId, adapter.GUID), DebugLogger.Category.Packager);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, string.Format("IsStationReady: Failed to initiate packaging retrieval for {0} at station {1}", requiredPackagingId, adapter.GUID), DebugLogger.Category.Packager);
        }

        return false;
      }

      private static async Task<(bool, string)> CheckPackagingAvailability(IStationAdapter adapter, Packager packager)
      {
        int productCount = adapter.ProductSlots
            .Where(s => s.ItemInstance != null && s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID)
            .Sum(s => s.Quantity);
        bool preferBaggies = productCount <= BAGGIE_THRESHOLD;
        var requiredPackagingId = preferBaggies ? BAGGIE_ITEM_ID : JAR_ITEM_ID;
        var packagingSlot = adapter.InsertSlot;

        bool hasJars = packagingSlot != null && packagingSlot.Quantity > 0 && packagingSlot.ItemInstance.ID == JAR_ITEM_ID && !preferBaggies;
        bool hasBaggies = packagingSlot != null && packagingSlot.Quantity > 0 && packagingSlot.ItemInstance.ID == BAGGIE_ITEM_ID;

        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckPackagingAvailability: Station {adapter.GUID}, productCount={productCount}, preferBaggies={preferBaggies}, hasJars={hasJars}, hasBaggies={hasBaggies}", DebugLogger.Category.Packager);

        if (hasJars || hasBaggies)
          return (true, requiredPackagingId);

        if (!preferBaggies)
        {
          int neededForJars = 5 - productCount % 5;
          if (neededForJars > 0 && neededForJars < 5)
          {
            var productSlot = adapter.ProductSlots.FirstOrDefault(s => s.ItemInstance != null && s.Quantity > 0 &&
                s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID);
            if (productSlot != null)
            {
              var item = productSlot.ItemInstance;
              var shelf = await FindShelfWithItemAsync(packager, item, neededForJars);
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"CheckPackagingAvailability: Need {neededForJars} more {item.ID} for jars at station {adapter.GUID}, shelvesFound={shelf.Key.GUID}", DebugLogger.Category.Packager);
              if (shelf.Key != null)
                return (false, requiredPackagingId);
            }
          }
          if (CheckBaggieUnpackaging(adapter, packager))
          {
            requiredPackagingId = JAR_ITEM_ID;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckPackagingAvailability: Unpackaging baggies at station {adapter.GUID}", DebugLogger.Category.Packager);
            return (false, requiredPackagingId);
          }
        }
        return (false, requiredPackagingId);
      }

      private static bool CheckBaggieUnpackaging(IStationAdapter adapter, Packager packager)
      {
        var baggieSlot = adapter.InsertSlot;
        if (baggieSlot == null || baggieSlot.Quantity < BAGGIE_UNPACKAGE_THRESHOLD || baggieSlot.ItemInstance.ID != BAGGIE_ITEM_ID)
          return false;

        var productSlot = adapter.ProductSlots.FirstOrDefault(s => s.ItemInstance != null && s.Quantity > 0 &&
            s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID);
        if (productSlot == null)
          return false;

        int unpackCount = baggieSlot.Quantity / BAGGIE_UNPACKAGE_THRESHOLD;
        bool unpackPerformed = false;
        for (int i = 0; i < unpackCount; i++)
        {
          if (productSlot.Quantity >= adapter.MaxProductQuantity)
            break;
          baggieSlot.ChangeQuantity(-BAGGIE_UNPACKAGE_THRESHOLD, false);
          productSlot.ApplyLock(packager.NetworkObject, "Unpacking baggies");
          productSlot.ChangeQuantity(BAGGIE_UNPACKAGE_THRESHOLD, false);
          productSlot.RemoveLock();
          unpackPerformed = true;
        }
        if (unpackPerformed)
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"CheckBaggieUnpackaging: Unpackaged {unpackCount * BAGGIE_UNPACKAGE_THRESHOLD} baggies for station {adapter.GUID}", DebugLogger.Category.Packager);
        return unpackPerformed;
      }

      private async Task<bool> InitiatePackagingRetrieval(IStationAdapter adapter, Packager packager, string packagingItemId, StateData state)
      {
        var packagingItem = Registry.GetItem(packagingItemId).GetDefaultInstance();
        var shelf = await FindShelfWithItemAsync(packager, packagingItem, 1);
        if (shelf.Key == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"InitiatePackagingRetrieval: No shelf found with {packagingItemId} for station {adapter.GUID}", DebugLogger.Category.Packager);
          return false;
        }

        var sourceSlot = (shelf.Key as ITransitEntity).GetFirstSlotContainingTemplateItem(packagingItem, ITransitEntity.ESlotType.Output);
        if (sourceSlot == null)
          return false;

        var transitEntity = adapter as ITransitEntity;
        if (transitEntity == null)
          return false;

        var deliverySlots = transitEntity.ReserveInputSlotsForItem(packagingItem, packager.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0)
          return false;

        int quantity = Math.Min(sourceSlot.Quantity, adapter.MaxProductQuantity - adapter.GetInputQuantity());
        if (quantity <= 0 || packager.Inventory.HowManyCanFit(packagingItem) < quantity)
          return false;

        var request = new TransferRequest(packager, packagingItem, quantity, packager.Inventory.ItemSlots.Find(s => s.ItemInstance == null),
            shelf.Key, new List<ItemSlot> { sourceSlot }, transitEntity, deliverySlots);
        state.SetValue("PackagingRequest", request);
        state.SetValue("PackagingStep", PackagingStep.MovingToShelf);
        StartPackagingMovement(packager, state, new List<PrioritizedRoute> { CreatePrioritizedRoute(request, Priority) }, PackagingStep.CheckStation);
        return true;
      }

      private async Task HandleCheckStation(Packager packager, StateData state)
      {
        if (!await IsStationReady(state.Station, packager, state))
        {
          int productCount = state.Station.ProductSlots
              .Where(s => s.ItemInstance != null && s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID)
              .Sum(s => s.Quantity);
          if (productCount > 0 && productCount < 5)
          {
            var productSlot = state.Station.ProductSlots.FirstOrDefault(s => s.ItemInstance != null && s.Quantity > 0 &&
                s.ItemInstance.ID != JAR_ITEM_ID && s.ItemInstance.ID != BAGGIE_ITEM_ID);
            if (productSlot != null)
            {
              var item = productSlot.ItemInstance;
              int needed = 5 - productCount % 5;
              var shelf = await FindShelfWithItemAsync(packager, item, needed);
              if (shelf.Key != null)
              {
                var sourceSlots = GetOutputSlotsContainingTemplateItem(shelf.Key, item)
                    .Where(s => s.Quantity > 0 && (!s.IsLocked || s.ActiveLock?.LockOwner == packager.NetworkObject)).ToList();
                if (sourceSlots.Count > 0)
                {
                  var deliverySlots = state.Station.TransitEntity.ReserveInputSlotsForItem(item, packager.NetworkObject);
                  if (deliverySlots != null && deliverySlots.Count > 0)
                  {
                    int quantity = Math.Min(sourceSlots.Sum(s => s.Quantity), needed);
                    var inventorySlot = packager.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
                    if (inventorySlot != null)
                    {
                      var request = new TransferRequest(packager, item, quantity, inventorySlot, shelf.Key, sourceSlots, state.Station.TransitEntity, deliverySlots);
                      state.SetValue("PackagingRequest", request);
                      state.SetValue("PackagingStep", PackagingStep.MovingToShelf);
                      StartPackagingMovement(packager, state, new List<PrioritizedRoute> { CreatePrioritizedRoute(request, Priority) }, PackagingStep.CheckStation);
                      return;
                    }
                  }
                }
              }
            }
          }
          if (CheckBaggieUnpackaging(state.Station, packager))
          {
            state.SetValue("PackagingStep", PackagingStep.UnpackageBaggies);
            await Execute(packager, state);
            return;
          }
          if (await InitiatePackagingRetrieval(state.Station, packager, productCount <= BAGGIE_THRESHOLD ? BAGGIE_ITEM_ID : JAR_ITEM_ID, state))
          {
            state.SetValue("PackagingStep", PackagingStep.MovingToShelf);
            await Execute(packager, state);
            return;
          }
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagingStation_OperateTask.CheckStation: Station {state.Station.GUID} not ready", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }
        state.SetValue("PackagingStep", PackagingStep.StartPackaging);
        await Execute(packager, state);
      }

      private void HandleFetchPackaging(Packager packager, StateData state)
      {
        if (!state.TryGetValue<TransferRequest>("PackagingRequest", out var request))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagingStation_OperateTask.FetchPackaging: No packaging request for {packager.fullName}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var route = CreatePrioritizedRoute(request, Priority);
        state.SetValue("PackagingStep", PackagingStep.MovingToShelf);
        StartPackagingMovement(packager, state, new List<PrioritizedRoute> { route }, PackagingStep.CheckStation);
      }

      private async Task HandleUnpackageBaggies(Packager packager, StateData state)
      {
        if (CheckBaggieUnpackaging(state.Station, packager))
        {
          state.SetValue("PackagingStep", PackagingStep.CheckStation);
          await Execute(packager, state);
        }
        else
        {
          state.SetValue("PackagingStep", PackagingStep.StartPackaging);
          await Execute(packager, state);
        }
      }

      private async Task HandleStartPackaging(Packager packager, StateData state)
      {
        if (state.Station.HasActiveOperation || !await IsStationReady(state.Station, packager, state))
        {
          state.SetValue("PackagingStep", PackagingStep.CheckStation);
          await Execute(packager, state);
          return;
        }
        packager.PackagingBehaviour.StartPackaging();
        state.SetValue("PackagingStep", PackagingStep.DeliverOutput);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagingStation_OperateTask.StartPackaging: Started packaging for station {state.Station.GUID}", DebugLogger.Category.Packager);
      }

      private async Task HandleDeliverOutput(Packager packager, StateData state)
      {
        var outputSlot = state.Station.OutputSlot;
        if (outputSlot?.ItemInstance == null || outputSlot.Quantity <= 0)
        {
          IsFetchingPackaging.Remove(state.Station.GUID);
          state.SetValue("PackagingStep", PackagingStep.CheckStation);
          await Execute(packager, state);
          return;
        }

        var packagedItem = outputSlot.ItemInstance;
        var quantity = outputSlot.Quantity;
        var shelf = await FindShelfWithItemAsync(packager, packagedItem, quantity);
        if (shelf.Key == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagingStation_OperateTask.DeliverOutput: No suitable shelf for {packagedItem.ID}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var deliverySlots = (shelf.Key as ITransitEntity).ReserveInputSlotsForItem(packagedItem, packager.NetworkObject);
        if (deliverySlots == null || deliverySlots.Count == 0)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagingStation_OperateTask.DeliverOutput: Failed to reserve slots on shelf {shelf.Key.GUID}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var inventorySlot = packager.Inventory.ItemSlots.Find(s => s.ItemInstance == null);
        if (inventorySlot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PackagingStation_OperateTask.DeliverOutput: No inventory slot for {packager.fullName}", DebugLogger.Category.Packager);
          ResetTask(state);
          return;
        }

        var request = new TransferRequest(packager, packagedItem, quantity, inventorySlot, state.Station.TransitEntity,
            new List<ItemSlot> { outputSlot }, shelf.Key, deliverySlots);
        outputSlot.ChangeQuantity(-quantity, false);
        IsFetchingPackaging.Remove(state.Station.GUID);
        var route = CreatePrioritizedRoute(request, Priority);
        state.SetValue("PackagingStep", PackagingStep.MovingToShelf);
        StartPackagingMovement(packager, state, new List<PrioritizedRoute> { route }, PackagingStep.CheckStation);
      }

      private void StartPackagingMovement(Packager packager, StateData state, List<PrioritizedRoute> routes, PackagingStep nextStep)
      {
        state.EmployeeBeh.StartMovement(routes, async (emp, s) =>
        {
          s.SetValue("PackagingStep", nextStep);
          await Execute(emp, s);
        });
      }

      private void ResetTask(StateData state)
      {
        state.CurrentState = EState.Idle;
        state.RemoveValue<PackagingStep>("PackagingStep");
        state.RemoveValue<TransferRequest>("PackagingRequest");
        if (state.Station != null)
        {
          IsFetchingPackaging.Remove(state.Station.GUID);
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info, "PackagingStation_OperateTask.ResetTask: Task reset", DebugLogger.Category.Packager);
      }
    }
  }
}