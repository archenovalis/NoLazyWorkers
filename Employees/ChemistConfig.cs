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
      public bool HandleIdle(Employee employee, StateData state) => false;
      public bool HandleTransfer(Employee employee, StateData state) => false;
      public bool HandleMoving(Employee employee, StateData state) => false;
      public bool HandleDelivery(Employee employee, StateData state) => false;
      public bool HandleOperating(Employee employee, StateData state) => false;
      public bool HandleInventoryItems(Employee employee, StateData state) => false;
      public bool HandlePlanning(Employee employee, StateData state) => false;
      public bool HandleCompleted(Employee employee, StateData state) => false;
      public bool GetEmployeeBehaviour(NPC npc, out EmployeeBehaviour employeeBehaviour) => RetrieveBehaviour(_chemist, this, out employeeBehaviour);
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

    public static bool RetrieveBehaviour(Chemist chemist, ChemistAdapter adapter, out EmployeeBehaviour employeeBehaviour)
    {
      employeeBehaviour = null;
      if (chemist == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetEmployeeBehaviour: Invalid NPC for chemist {chemist.fullName}", DebugLogger.Category.Chemist);
        return false;
      }
      if (!EmployeeAdapters.ContainsKey(chemist.GUID) || EmployeeAdapters[chemist.GUID] == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GetEmployeeBehaviour: No adapter found for NPC {chemist.fullName}, creating new", DebugLogger.Category.Chemist);
        EmployeeAdapters[chemist.GUID] = adapter;
      }

      var chemistBehaviour = ActiveBehaviours.TryGetValue(chemist.GUID, out var beh) ? beh as ChemistBehaviour : null;
      if (chemistBehaviour == null)
      {
        chemistBehaviour = new ChemistBehaviour(chemist, adapter);
        ActiveBehaviours[chemist.GUID] = chemistBehaviour;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created ChemistBehaviour for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
      }

      employeeBehaviour = chemistBehaviour;
      return true;
    }
  }
}
