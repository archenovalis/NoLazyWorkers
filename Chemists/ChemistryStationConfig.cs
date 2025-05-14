using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using UnityEngine;
using static NoLazyWorkers.General.GeneralExtensions;

namespace NoLazyWorkers.Chemists
{
  public static class ChemistryStationExtensions
  {
    public class ChemistryStationAdapter : IStationAdapter<ChemistryStation>
    {
      private readonly ChemistryStation _station;

      public ChemistryStationAdapter(ChemistryStation station)
      {
        _station = station;
      }

      public ChemistryStation Station => _station;
      public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
      public bool IsInUse => _station.isOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
      public bool HasActiveOperation => _station.CurrentCookOperation != null;
      public Guid GUID => _station.GUID;
      public ItemSlot InsertSlot => _station.IngredientSlots?[0];
      public List<ItemSlot> ProductSlots => [_station.IngredientSlots[1]];
      public ItemSlot OutputSlot => _station.OutputSlot;
      public int StartThreshold => 1;
      public int MaxProductQuantity => 20;
      public ITransitEntity TransitEntity => _station as ITransitEntity;

      public int GetInputQuantity() => _station.IngredientSlots?.Sum(slot => slot?.Quantity ?? 0) ?? 0;

      public List<ItemField> GetInputItemForProduct()
      {
        var item = _station.IngredientSlots?.FirstOrDefault(slot => slot?.ItemInstance != null)?.ItemInstance?.Definition;
        return [item != null ? new ItemField(_station.stationConfiguration) { SelectedItem = item } : null];
      }

      public void StartOperation(ScheduleOne.NPCs.Behaviour.Behaviour behaviour)
      {
        (behaviour as StartChemistryStationBehaviour).StartCook();
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"ChemistryStation.StartOperation: Started cook for station {_station.GUID}",
            DebugLogger.Category.Chemist, DebugLogger.Category.MixingStation);
      }
    }
  }
}