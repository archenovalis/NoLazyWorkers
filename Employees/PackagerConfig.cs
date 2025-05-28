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
using static NoLazyWorkers.Employees.PackagerBehaviour;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.DevUtilities;
using NoLazyWorkers.General;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using ScheduleOne.EntityFramework;
using NoLazyWorkers.Stations;

namespace NoLazyWorkers.Employees
{
  public static class PackagerExtensions
  {
    public class PackagerAdapter : IEmployeeAdapter
    {
      private readonly Packager _packager;
      private readonly EmployeeBehaviour _employeeBehaviour;

      public PackagerAdapter(Packager packager)
      {
        _packager = packager ?? throw new ArgumentNullException(nameof(packager));
        _employeeBehaviour = new PackagerBehaviour(packager, this);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter: Initialized for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      }

      public NpcSubType SubType => NpcSubType.Packager;
      public Property AssignedProperty => _packager.AssignedProperty;
      public EmployeeBehaviour EmpBehaviour => _employeeBehaviour;
    }
  }
}