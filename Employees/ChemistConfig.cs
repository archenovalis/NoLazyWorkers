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
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.Employees.ChemistExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.ChemistUtilities;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.General;
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
      public bool HandleIdle(Behaviour behaviour, StateData state) => false;
      public bool HandleGrabbing(Behaviour behaviour, StateData state) => false;
      public bool HandleMoving(Behaviour behaviour, StateData state) => false;
      public bool HandleDelivering(Behaviour behaviour, StateData state) => false;
      public bool HandleOperating(Behaviour behaviour, StateData state) => false;
      public bool HandleInventoryItems(Behaviour behaviour, StateData state) => false;
      public bool HandlePlanning(Behaviour behaviour, StateData state) => false;
      public bool HandleCompleted(Behaviour behaviour, StateData state) => false;
      public bool GetEmployeeBehaviour(NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour) => RetrieveBehaviour(_chemist, this, npc, station, out employeeBehaviour);
    }
  }
  public static class ChemistUtilities
  {
    public static ChemistBehaviour GetChemistBehaviour(NPC chemist)
    {
      var chemistBehaviour = ActiveBehaviours.TryGetValue(chemist.GUID, out var beh) ? beh as ChemistBehaviour : null;
      if (chemistBehaviour == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetChemistBehaviour: No ChemistBehaviour for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
      }
      return chemistBehaviour;
    }

    public static bool RetrieveBehaviour(Chemist chemist, ChemistAdapter adapter, NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour)
    {
      employeeBehaviour = null;
      if (npc == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetEmployeeBehaviour: Invalid NPC for chemist {chemist.fullName}", DebugLogger.Category.Chemist);
        return false;
      }
      if (!EmployeeAdapters.ContainsKey(npc.GUID) || EmployeeAdapters[npc.GUID] == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetEmployeeBehaviour: No adapter found for NPC {npc.fullName}, creating new", DebugLogger.Category.Chemist);
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
        chemistBehaviour = new ChemistBehaviour(chemist, adapter);
        ActiveBehaviours[npc.GUID] = chemistBehaviour;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created ChemistBehaviour for NPC {npc.fullName} and station {station.GUID}", DebugLogger.Category.Chemist);
      }

      employeeBehaviour = chemistBehaviour;
      return true;
    }
  }
}
