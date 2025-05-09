using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using UnityEngine;
using static NoLazyWorkers.Chemists.ChemistExtensions;

namespace NoLazyWorkers.Chemists
{
  public static class CauldronExtensions
  {
    public class CauldronAdapter : IStationAdapter
    {
      private readonly Cauldron _station;

      public CauldronAdapter(Cauldron station)
      {
        _station = station;
      }

      public string Name => _station.Name;
      Vector3 IStationAdapter.GetAccessPoint() => _station.AccessPoints?.FirstOrDefault()?.position ?? _station.Transform.position;
      ItemSlot IStationAdapter.InsertSlot => _station.LiquidSlot; // Use LiquidSlot for primary input
      List<ItemSlot> IStationAdapter.ProductSlots => _station.InputSlots; // multiple slots
      ItemSlot IStationAdapter.OutputSlot => _station.OutputSlot;
      public bool IsInUse => _station.isOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
      public bool HasActiveOperation => _station.RemainingCookTime > 0;
      int IStationAdapter.StartThreshold => Cauldron.COCA_LEAF_REQUIRED; // 20 as per Cauldron
      Guid IStationAdapter.GUID => _station.GUID;
      public object Station => _station;

      public int MaxProductQuantity => 60;

      public int GetInputQuantity() => _station.LiquidSlot?.Quantity ?? 0;
      public void StartOperation() => _station.onCookStart.Invoke();
      ItemField IStationAdapter.GetInputItemForProduct()
      {
        // Cauldron uses fixed inputs (e.g., Gasoline, CocaLeaf); return configured item
        return new ItemField(_station.cauldronConfiguration) { SelectedItem = Registry.GetItem("gasoline") };
      }
      public void StartOperation(ScheduleOne.NPCs.Behaviour.Behaviour behaviour)
      {
        if (behaviour is StartCauldronBehaviour cauldronBehaviour)
        {
          cauldronBehaviour.BeginCauldron();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"MixingStationAdapter.StartOperation: Started cook for station {_station.GUID}", isStation: true);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"MixingStationAdapter.StartOperation: Invalid behaviour type for station {_station.GUID}, expected StartMixingStationBehaviour, got {behaviour?.GetType().Name}", isStation: true);
        }
      }
    }
  }
}