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
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.General;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using ScheduleOne.EntityFramework;
using NoLazyWorkers.Stations;
using FishNet.Object;
using FishNet.Managing.Object;

namespace NoLazyWorkers.Employees
{
  public class ChemistAdapter : IEmployeeAdapter
  {
    private readonly Chemist _chemist;
    private readonly EmployeeBehaviour _employeeBehaviour;
    public EmployeeBehaviour EmpBehaviour => _employeeBehaviour;
    public Property AssignedProperty => _chemist.AssignedProperty;
    public NpcSubType SubType => NpcSubType.Chemist;

    public ChemistAdapter(Chemist chemist)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      _employeeBehaviour = new ChemistBehaviour(chemist, this);
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"ChemistAdapter: Initialized for NPC {_chemist.fullName}",
          DebugLogger.Category.Chemist);
    }
  }

  public static class ChemistTaskConfigurations
  {

  }
}
