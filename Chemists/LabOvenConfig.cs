using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using UnityEngine;
using static NoLazyWorkers.Chemists.ChemistExtensions;

namespace NoLazyWorkers.Chemists
{
  public static class LabOvenExtensions
  {
    public class LabOvenAdapter : IStationAdapter
    {
      private readonly LabOven _station;

      public LabOvenAdapter(LabOven station)
      {
        _station = station;
      }

      public string Name => _station.Name;
      Vector3 IStationAdapter.GetAccessPoint() => _station.AccessPoints?.FirstOrDefault()?.position ?? _station.Transform.position;
      public bool IsInUse => _station.isOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
      public bool HasActiveOperation => _station.CurrentOperation != null;

      Guid IStationAdapter.GUID => _station.GUID;

      public ItemSlot InsertSlot => null;

      public List<ItemSlot> ProductSlots => [_station.IngredientSlot];

      ItemSlot IStationAdapter.OutputSlot => _station.OutputSlot;

      int IStationAdapter.StartThreshold => 1;

      public object Station => _station;

      public int MaxProductQuantity => throw new NotImplementedException();

      public int GetInputQuantity() => _station.IngredientSlot?.Quantity ?? 0;
      public ItemField GetInputItemForProduct()
      {
        var item = _station.IngredientSlot?.ItemInstance?.Definition;
        return item != null ? new ItemField(_station.ovenConfiguration) { SelectedItem = item } : null;
      }
      public void StartOperation(ScheduleOne.NPCs.Behaviour.Behaviour behaviour)
      {
        if (behaviour is StartLabOvenBehaviour labOvenBehaviour)
        {
          labOvenBehaviour.StartCook();
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