using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.Employees;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Structures.StorageUtilities;
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Structures;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using ScheduleOne.EntityFramework;

namespace NoLazyWorkers.Employees
{
  public static class ChemistExtensions
  {
    public class ChemistAdapter : IEmployeeAdapter
    {
      private readonly Chemist _chemist;
      public Property AssignedProperty => _chemist.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Chemist;

      public ChemistAdapter(Chemist chemist)
      {
        _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      }
      public bool HandleGrabbing(Behaviour behaviour, StateData state) => false;
      public bool HandleInserting(Behaviour behaviour, StateData state) => false;
      public bool HandleOperating(Behaviour behaviour, StateData state) => false;
      public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item) => false;
      public bool HandlePlanning(Behaviour behaviour, StateData state) => false;

      public bool GetEmployeeBehaviour(NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour)
      {
        employeeBehaviour = null;
        if (npc == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetEmployeeBehaviour: Invalid NPC for packager {_chemist.fullName}", DebugLogger.Category.Packager);
          return false;
        }
        if (!EmployeeAdapters.TryGetValue(npc.GUID, out var adapter) || adapter == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetEmployeeBehaviour: No adapter found for NPC {npc.fullName}, creating new", DebugLogger.Category.Packager);
          adapter = new PackagerAdapter(npc as Packager);
          EmployeeAdapters[npc.GUID] = adapter;
        }

        var chemistBehaviour = ActiveBehaviours.TryGetValue(npc.GUID, out var beh) ? beh as ChemistBehaviour : null;
        if (chemistBehaviour == null)
        {
          if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
          {
            if (station is MixingStation)
              stationAdapter = new MixingStationAdapter(station as MixingStation);
            /* else if (station is ChemistryStation)
              stationAdapter = new ChemistryStationAdapter(station as ChemistryStation);
            else if (station is LabOven)
              stationAdapter = new LabOvenAdapter(station as LabOven);
            else if (station is Cauldron)
              stationAdapter = new CauldronAdapter(station as Cauldron); */
            StationAdapters[station.GUID] = stationAdapter;
          }
          chemistBehaviour = new ChemistBehaviour(_chemist, stationAdapter, this);
          ActiveBehaviours[npc.GUID] = chemistBehaviour;
        }

        employeeBehaviour = chemistBehaviour;
        return true;
      }
    }
  }
}