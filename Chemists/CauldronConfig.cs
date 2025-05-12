using ScheduleOne;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using UnityEngine;
using VLB;
using static NoLazyWorkers.General.GeneralExtensions;

namespace NoLazyWorkers.Chemists
{
  public static class CauldronExtensions
  {
    public class CauldronAdapter : IStationAdapter<Cauldron>
    {
      private readonly Cauldron _station;

      public CauldronAdapter(Cauldron station)
      {
        _station = station;
      }

      public Cauldron Station => _station;
      public Vector3 GetAccessPoint() => _station.AccessPoints?.FirstOrDefault()?.position ?? _station.Transform.position;
      public ItemSlot InsertSlot => _station.LiquidSlot;
      public List<ItemSlot> ProductSlots => _station.InputSlots;
      public ItemSlot OutputSlot => _station.OutputSlot;
      public bool IsInUse => _station.isOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
      public bool HasActiveOperation => _station.RemainingCookTime > 0;
      public int StartThreshold => Cauldron.COCA_LEAF_REQUIRED;
      public Guid GUID => _station.GUID;
      public int MaxProductQuantity => 60;
      public int GetInputQuantity() => _station.LiquidSlot?.Quantity ?? 0;
      public void StartOperation() => _station.onCookStart.Invoke();
      public ITransitEntity TransitEntity => _station as ITransitEntity;

      public List<ItemField> GetInputItemForProduct()
      {
        return [new ItemField(_station.cauldronConfiguration) { SelectedItem = Registry.GetItem("gasoline") },
                        new ItemField(_station.cauldronConfiguration) { SelectedItem = Registry.GetItem("cocaleaf") }];
      }

      public void StartOperation(ScheduleOne.NPCs.Behaviour.Behaviour behaviour)
      {
        (behaviour as StartCauldronBehaviour).BeginCauldron();
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"CauldronAdapter.StartOperation: Started cook for station {_station.GUID}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      }
    }
  }
}