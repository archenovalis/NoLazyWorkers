using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Employees;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.ChemistExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using NoLazyWorkers.General;
using ScheduleOne.DevUtilities;
using UnityEngine;
using ScheduleOne;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using ScheduleOne.EntityFramework;
using System.Threading.Tasks;

namespace NoLazyWorkers.Stations
{
  namespace NoLazyWorkers.Stations
  {
    public static class MixingStationBehaviourUtilities
    {

      /* out bool canStart, out bool canRestock, out bool hasOutput, out RestockObj restock */
      public static async Task<(bool, bool, bool, bool, RestockObj)> ValidateStationState(Chemist chemist, IStationAdapter stationAdapter)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateStationState: Entered for chemist={chemist?.fullName ?? "null"}, type={stationAdapter.TypeOf}, station={stationAdapter?.GUID.ToString() ?? "null"}", DebugLogger.Category.MixingStation);
        var restock = new RestockObj
        {
          Item = null,
          Quantity = 0,
          Shelf = null,
          PickupSlots = null
        };
        var canStart = false;
        var canRestock = false;
        var hasOutput = false;
        if (chemist == null || stationAdapter == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ValidateStationState: Invalid chemist or station adapter, chemist={chemist?.fullName ?? "null"}, station={stationAdapter?.GUID.ToString() ?? "null"}", DebugLogger.Category.MixingStation);
          return (false, canStart, canRestock, hasOutput, restock);
        }
        if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ValidateStationState: Station in use, active, or has output for station={stationAdapter.GUID}", DebugLogger.Category.MixingStation);
          return (false, canStart, canRestock, hasOutput, restock);
        }
        if (stationAdapter.OutputSlot.Quantity > 0)
        {
          hasOutput = true;
          return (true, canStart, canRestock, hasOutput, restock);
        }
        var inputItems = stationAdapter.GetInputItemForProduct();
        if (inputItems == null || inputItems.Count == 0 || inputItems[0]?.SelectedItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateStationState: Input item null or empty for station={stationAdapter.GUID}", DebugLogger.Category.MixingStation);
          return (false, canStart, canRestock, hasOutput, restock);
        }
        ItemField inputItem = inputItems[0];
        ItemInstance targetItem = inputItem.SelectedItem.GetDefaultInstance();
        restock.Item = targetItem;
        if (targetItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"ValidateStationState: Target item null for station={stationAdapter.GUID}", DebugLogger.Category.MixingStation);
          return (false, canStart, canRestock, hasOutput, restock);
        }
        int threshold = stationAdapter.StartThreshold;
        int desiredQty = Math.Min(stationAdapter.MaxProductQuantity, stationAdapter.ProductSlots.Sum(s => s.Quantity));
        int invQty = chemist.Inventory._GetItemAmount(targetItem.ID);
        int inputQty = stationAdapter.GetInputQuantity();
        var state = EmployeeBehaviour.GetState(chemist);
        state.TargetItem = targetItem;
        state.QuantityInventory = invQty;
        state.QuantityNeeded = Math.Max(0, threshold - inputQty);
        state.QuantityWanted = Math.Max(0, desiredQty - inputQty);

        if (desiredQty < threshold)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"ValidateStationState: Below threshold and cannot restock for station={stationAdapter.GUID}, inputQty={inputQty}, threshold={threshold}", DebugLogger.Category.MixingStation);
          return (false, canStart, canRestock, hasOutput, restock);
        }
        int num = 0;
        KeyValuePair<ScheduleOne.ObjectScripts.PlaceableStorageEntity, int> shelf = new();
        if (inputQty >= threshold && desiredQty >= threshold)
        {
          if (inputQty >= desiredQty)
          {
            canStart = true;
          }
          else
          {
            shelf = await FindShelfWithItemAsync(chemist, targetItem, state.QuantityNeeded - invQty);
            if (shelf.Key == null)
            {
              canStart = true;
            }
            else
            {
              canRestock = true;
            }
          }
        }
        else
        {
          shelf = await FindShelfWithItemAsync(chemist, targetItem, state.QuantityNeeded - invQty);
          canRestock = shelf.Value >= state.QuantityNeeded - invQty;
        }
        if (canRestock)
        {
          restock.Shelf = shelf.Key;
          restock.Quantity = Math.Min(num, state.QuantityWanted - invQty);
          restock.PickupSlots = shelf.Key.StorageEntity.ItemSlots.FindAll(s => s.ItemInstance != null && s.ItemInstance.CanStackWithMinQuality(targetItem, checkQuantities: false));
        }
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"ValidateStationState: Completed for chemist={chemist.fullName}, station={stationAdapter.GUID}, canStart={canStart}, canRestock={canRestock}, invQty={invQty}, inputQty={inputQty}, desiredQty={desiredQty}, threshold={threshold}, qtyNeeded={state.QuantityNeeded}, qtyWanted={state.QuantityWanted}", DebugLogger.Category.MixingStation);
        return (canStart || canRestock, canStart, canRestock, hasOutput, restock);
      }
    }

    public class MixingStationBeh(Chemist chemist, IEmployeeAdapter adapter) : ChemistBehaviour(chemist, adapter)
    {
      public class Work_MixingStation : IEmployeeTask
      {
        private readonly int _priority;
        private readonly int _scanIndex;
        public int Priority => _priority;
        public int ScanIndex => _scanIndex;

        public Work_MixingStation(int priority, int scanIndex)
        {
          _priority = priority;
          _scanIndex = scanIndex;
        }

        public async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.CanExecute: Entered for chemist={employee?.fullName ?? "null"}", DebugLogger.Category.MixingStation);
          if (!(employee is Chemist chemist))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationBeh.Work.CanExecute: Employee {employee.fullName} is not a Chemist", DebugLogger.Category.Chemist);
            return false;
          }

          if (chemist.AssignedProperty == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationBeh.Work.CanExecute: AssignedProperty is null for {chemist.fullName}", DebugLogger.Category.Chemist);
            return false;
          }

          List<MixingStation> stations;
          if (recheck == null)
            stations = chemist.configuration.MixStations;
          else
            stations = [recheck as MixingStation];
          // Iterate through assigned mixing stations
          foreach (MixingStation station in stations)
          {
            if (station == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"MixingStationBeh.Work.CanExecute: Null station in MixStations for {chemist.fullName}", DebugLogger.Category.Chemist);
              continue;
            }

            if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
            {
              stationAdapter = new MixingStationAdapter(station);
              StationAdapters[station.GUID] = stationAdapter;
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.CanExecute: Created station adapter for station {station.GUID}", DebugLogger.Category.Chemist);
            }

            if (stationAdapter.IsInUse || stationAdapter.HasActiveOperation)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.CanExecute: Station {station.GUID} in use or active, skipping for {chemist.fullName}", DebugLogger.Category.Chemist);
              continue;
            }

            // Check station state and determine WorkStep
            var result = await MixingStationBehaviourUtilities.ValidateStationState(chemist, stationAdapter);
            if (result.Item1)
            {
              var state = GetState(chemist);
              WorkStep? workStep = await HandleCheckStation(chemist, state, stationAdapter, result.Item2, result.Item3, result.Item4, result.Item5);
              if (workStep.HasValue)
              {
                state.Station = stationAdapter;
                state.SetValue("WorkStep", workStep.Value);
                DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.CanExecute: Selected station {station.GUID} with WorkStep {workStep.Value} for {chemist.fullName}", DebugLogger.Category.Chemist);
                await Execute(chemist, state);
                return true;
              }
            }
          }

          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.CanExecute: No suitable stations found for {chemist.fullName}", DebugLogger.Category.Chemist);
          return false;
        }

        public async Task Execute(Employee employee, StateData state)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.Execute: Entered for chemist={employee?.fullName ?? "null"}", DebugLogger.Category.MixingStation);
          if (!(employee is Chemist chemist))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationBeh.Work.Execute: Employee {employee.fullName} is not a Chemist", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          if (!state.TryGetValue<WorkStep>("WorkStep", out var workStep) || state.Station == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationBeh.Work.Execute: Invalid WorkStep or Station for {chemist.fullName}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          switch (workStep)
          {
            case WorkStep.CheckStation:
              if (!await CanExecute(chemist, state.Station.TransitEntity))
                ResetTask(state);
              break;
            case WorkStep.RestockIngredients:
              await HandleRestockIngredients(chemist, state);
              break;
            case WorkStep.OperateStation:
              HandleOperateStation(chemist, state);
              break;
            case WorkStep.LoopOutput:
              await HandleLoopOutput(chemist, state);
              break;
            case WorkStep.DeliverProduct:
              await HandleDeliverProduct(chemist, state);
              break;
            default:
              DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationBeh.Work.Execute: Unknown WorkStep {workStep} for {chemist.fullName}", DebugLogger.Category.Chemist);
              ResetTask(state);
              break;
          }
        }

        private async Task<WorkStep?> HandleCheckStation(Chemist chemist, StateData state, IStationAdapter stationAdapter, bool canStart, bool canRestock, bool hasOutput, RestockObj restock)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.HandleCheckStation: Station {stationAdapter.GUID} for {chemist.fullName}", DebugLogger.Category.Chemist);
          // Check for output to move (equivalent to GetMixStationsReadyToMove)
          if (hasOutput)
          {
            var outputItem = stationAdapter.OutputSlot.ItemInstance;
            if (stationAdapter.RefillList().FirstOrDefault(i => outputItem.CanStackWithMinQuality(i)) != null)
            {
              return WorkStep.LoopOutput;
            }
            else
            {
              var destination = FindPackagingStation(EmployeeAdapters[chemist.GUID], outputItem)
                  ?? await FindShelfForDeliveryAsync(chemist, outputItem);
              if (destination != null)
              {
                return WorkStep.DeliverProduct;
              }
            }
          }

          // Check for restocking or operation (equivalent to GetMixingStationsReadyToStart)
          if (canRestock && restock.Item != null)
          {
            var shelf = restock.Shelf;
            if (shelf != null && restock.PickupSlots != null && restock.PickupSlots.Count > 0)
            {
              return WorkStep.RestockIngredients;
            }
          }

          if (canStart)
          {
            return WorkStep.OperateStation;
          }

          DebugLogger.Log(DebugLogger.LogLevel.Error, $"MixingStationBeh.Work.HandleCheckStation: No valid WorkStep for station {state.Station.GUID} for {chemist.fullName}", DebugLogger.Category.Chemist);
          return null;
        }

        private async Task HandleRestockIngredients(Chemist chemist, StateData state)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.RestockIngredients: Entered for chemist={chemist?.fullName ?? "null"}", DebugLogger.Category.MixingStation);
          var result = await MixingStationBehaviourUtilities.ValidateStationState(chemist, state.Station);
          var canRestock = result.Item3;
          var restock = result.Item5;
          if (!result.Item1 || !canRestock)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.RestockIngredients: Cannot restock station {state.Station.GUID}", DebugLogger.Category.Chemist);
            state.SetValue("WorkStep", WorkStep.CheckStation);
            await Execute(chemist, state);
            return;
          }

          var inventorySlot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.RestockIngredients: No inventory slot for {chemist.fullName}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          var shelf = restock.Shelf;
          if (shelf == null || restock.PickupSlots == null || restock.PickupSlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.RestockIngredients: No shelf or pickup slots for item {restock.Item.ID}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          List<ItemSlot> deliverySlots = state.Station.InsertSlot.GetCapacityForItem(restock.Item) > 0 ? [state.Station.InsertSlot] : new();
          if (deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.RestockIngredients: No delivery slots for station {state.Station.GUID}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          int quantity = Math.Min(restock.Quantity, restock.PickupSlots.Sum(s => s.Quantity));
          var request = new TransferRequest(chemist, restock.Item, quantity, inventorySlot, shelf, restock.PickupSlots, state.Station.TransitEntity, deliverySlots);
          var route = CreatePrioritizedRoute(request, Priority);
          StartMixingMovement(state, new List<PrioritizedRoute> { route }, WorkStep.OperateStation);
        }

        private void HandleOperateStation(Chemist chemist, StateData state)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.StartOperation: Entered for chemist={chemist?.fullName ?? "null"}", DebugLogger.Category.MixingStation);
          state.Station.StartOperation(chemist);
          state.CurrentState = EState.Idle;
          return;
        }

        private async Task HandleLoopOutput(Chemist chemist, StateData state)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"MixingStationBeh.Work.LoopOutput: Entered for chemist={chemist?.fullName ?? "null"}", DebugLogger.Category.MixingStation);

          var outputItem = state.Station.OutputSlot.ItemInstance;
          if (outputItem == null)
          {
            state.SetValue("WorkStep", WorkStep.CheckStation);
            await Execute(chemist, state);
            return;
          }

          var inventorySlot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null || s.ItemInstance.CanStackWith(outputItem));
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.LoopOutput: No inventory slot for {chemist.fullName}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          var deliverySlot = state.Station.ProductSlots[0];
          if (deliverySlot.ItemInstance != null && deliverySlot.ItemInstance.CanStackWith(outputItem, false))
          {
            state.SetValue("WorkStep", WorkStep.DeliverProduct);
            await Execute(chemist, state);
            return;
          }

          int quantity = Math.Min(state.Station.OutputSlot.Quantity, deliverySlot.GetCapacityForItem(outputItem));
          var request = new TransferRequest(chemist, outputItem, quantity, inventorySlot, state.Station.TransitEntity, new List<ItemSlot> { state.Station.OutputSlot }, state.Station.TransitEntity, [deliverySlot]);
          var route = CreatePrioritizedRoute(request, Priority);
          StartMixingMovement(state, new List<PrioritizedRoute> { route }, WorkStep.CheckStation);
        }

        private async Task HandleDeliverProduct(Chemist chemist, StateData state)
        {
          var outputItem = state.Station.OutputSlot.ItemInstance;
          if (outputItem == null || state.Station.OutputSlot.Quantity <= 0)
          {
            state.SetValue("WorkStep", WorkStep.CheckStation);
            await Execute(chemist, state);
            return;
          }

          var inventorySlot = chemist.Inventory.ItemSlots.FirstOrDefault(s => s.ItemInstance == null);
          if (inventorySlot == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.DeliverProduct: No inventory slot for {chemist.fullName}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          var destination = FindPackagingStation(EmployeeAdapters[chemist.GUID], outputItem)
              ?? await FindShelfForDeliveryAsync(chemist, outputItem);
          if (destination == null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.DeliverProduct: No destination for item {outputItem.ID}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          var deliverySlots = destination.ReserveInputSlotsForItem(outputItem, chemist.NetworkObject);
          if (deliverySlots == null || deliverySlots.Count == 0)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.DeliverProduct: No delivery slots at {destination.GUID}", DebugLogger.Category.Chemist);
            ResetTask(state);
            return;
          }

          int quantity = Math.Min(state.Station.OutputSlot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(outputItem)));
          var request = new TransferRequest(chemist, outputItem, quantity, inventorySlot, state.Station.TransitEntity, new List<ItemSlot> { state.Station.OutputSlot }, destination, deliverySlots);
          var route = CreatePrioritizedRoute(request, Priority);
          state.EmployeeBeh.StartMovement(new List<PrioritizedRoute> { route }, (emp, s) =>
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"MixingStationBeh.Work.DeliverProduct: Delivery completed for {emp.fullName}", DebugLogger.Category.EmployeeCore);
            state.EmployeeBeh.Disable();
          });
        }

        private void StartMixingMovement(StateData state, List<PrioritizedRoute> routes, WorkStep nextStep)
        {
          state.EmployeeBeh.StartMovement(routes, async (emp, s) =>
          {
            s.SetValue("WorkStep", nextStep);
            await Execute(emp, s);
          });
        }

        private void ResetTask(StateData state)
        {
          state.CurrentTask = null;
          state.CurrentState = EState.Idle;
          state.RemoveValue<WorkStep>("WorkStep");
          state.Station = null;
        }
      }

      public enum WorkStep
      {
        CheckStation,
        RestockIngredients,
        OperateStation,
        LoopOutput,
        DeliverProduct
      }
    }

    [HarmonyPatch(typeof(Chemist))]
    public class MixingStationChemistPatch
    {
      [HarmonyPrefix]
      [HarmonyPatch("GetMixingStationsReadyToStart")]
      public static bool GetMixingStationsReadyToStartPrefix(Chemist __instance, ref List<MixingStation> __result)
      {
        return false;
      }

      [HarmonyPrefix]
      [HarmonyPatch("GetMixStationsReadyToMove")]
      public static bool GetMixStationsReadyToMovePrefix(Chemist __instance, ref List<MixingStation> __result)
      {
        return false;
      }
    }
  }
}