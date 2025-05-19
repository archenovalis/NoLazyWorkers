using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Structures.StorageUtilities;

using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Employees.EmployeeExtensions;

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : EmployeeBehaviour
  {
    public IEmployeeAdapter Employee;
    public ChemistBehaviour(Chemist chemist, IStationAdapter station, IEmployeeAdapter employee) : base(chemist, employee)
    {
      Employee = employee ?? throw new ArgumentNullException(nameof(employee));
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistBehaviour: Station adapter is null for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
        return;
      }

      var behaviour = GetInstancedBehaviour(chemist, station);
      if (behaviour == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ChemistBehaviour: Failed to get behaviour for station {station.GUID} for NPC {chemist.fullName}", DebugLogger.Category.Chemist);
        return;
      }

      RegisterStationBehaviour(behaviour, station);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ChemistBehaviour: Initialized for NPC {chemist.fullName} with behaviour for station {station.GUID}", DebugLogger.Category.Chemist);
    }
  }
}