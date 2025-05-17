using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;

namespace NoLazyWorkers.Stations
{
  /*   public static class LabOvenExtensions
    {
      public class LabOvenAdapter : IStationAdapter
      {
        private readonly LabOven _station;

        public LabOvenAdapter(LabOven station)
        {
          _station = station;
        }

        public LabOven Station => _station;
        public string Name => _station.Name;
        public Vector3 GetAccessPoint(NPC npc) => NavMeshUtility.GetAccessPoint(_station, npc).position;
        public bool IsInUse => _station.isOpen || _station.NPCUserObject != null || _station.PlayerUserObject != null;
        public bool HasActiveOperation => _station.CurrentOperation != null;
        public Guid GUID => _station.GUID;
        public ItemSlot InsertSlot => null;
        public List<ItemSlot> ProductSlots => [_station.IngredientSlot];
        public ItemSlot OutputSlot => _station.OutputSlot;
        int IStationAdapter.StartThreshold => 1;
        public int MaxProductQuantity => 20;
        public int GetInputQuantity() => _station.IngredientSlot?.Quantity ?? 0;
        public ITransitEntity TransitEntity => _station as ITransitEntity;
        public bool MoveOutputToShelf() => false;

        public List<ItemField> GetInputItemForProduct()
        {
          var item = _station.IngredientSlot?.ItemInstance?.Definition;
          return [item != null ? new ItemField(_station.ovenConfiguration) { SelectedItem = item } : null];
        }

        public void StartOperation(ScheduleOne.NPCs.Behaviour.Behaviour behaviour)
        {
          (behaviour as StartLabOvenBehaviour).StartCook();
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"LabOvenAdapter.StartOperation: Started cook for station {_station.GUID}",
              DebugLogger.Category.Chemist, DebugLogger.Category.LabOven);
        }

        public List<ItemInstance> RefillList()
        {
          throw new NotImplementedException();
        }
      }
    }
   */
}