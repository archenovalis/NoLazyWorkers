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
using static NoLazyWorkers.Employees.PackagerUtilities;
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.DevUtilities;
using NoLazyWorkers.General;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using ScheduleOne.EntityFramework;

namespace NoLazyWorkers.Employees
{
  public static class PackagerExtensions
  {
    public class PackagerAdapter : IEmployeeAdapter
    {
      private readonly Packager _packager;

      public PackagerAdapter(Packager packager)
      {
        _packager = packager ?? throw new ArgumentNullException(nameof(packager));
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PackagerAdapter: Initialized for NPC {_packager.fullName}", DebugLogger.Category.Packager);
      }

      public Property AssignedProperty => _packager.AssignedProperty;
      public NpcSubType SubType => NpcSubType.Packager;

      public bool GetEmployeeBehaviour(NPC npc, out EmployeeBehaviour employeeBehaviour) => RetrieveBehaviour(_packager, this, out employeeBehaviour);
      public bool HandleIdle(Employee employee, StateData state) => false;
      public bool HandlePlanning(Employee employee, StateData state) => GetPackagerBehaviour(_packager, this).Planning(employee, state);
      public bool HandleMoving(Employee employee, StateData state) => false;
      public bool HandleTransfer(Employee employee, StateData state) => false;
      public bool HandleDelivery(Employee employee, StateData state) => false;
      public bool HandleOperating(Employee employee, StateData state) => GetPackagerBehaviour(_packager, this).Operating(employee, state);
      public bool HandleCompleted(Employee employee, StateData state) => false;
      public bool HandleInventoryItems(Employee employee, StateData state) => false;
    }
  }

  public static class PackagerUtilities
  {
    public static bool RetrieveBehaviour(Packager packager, PackagerAdapter adapter, out EmployeeBehaviour employeeBehaviour)
    {
      employeeBehaviour = null;
      if (packager == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RetrieveBehaviour: Invalid NPC or station for packager {packager?.fullName ?? "null"}", DebugLogger.Category.Packager);
        return false;
      }
      if (!EmployeeAdapters.TryGetValue(packager.GUID, out var adaptr) || adaptr == null)
      {
        EmployeeAdapters[packager.GUID] = adapter;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created adapter for NPC {packager.fullName}", DebugLogger.Category.Packager);
      }
      var packagerBehaviour = ActiveBehaviours.TryGetValue(packager.GUID, out var beh) ? beh as PackagerBehaviour : null;
      if (packagerBehaviour == null)
      {
        packagerBehaviour = new PackagerBehaviour(packager, adapter);
        ActiveBehaviours[packager.GUID] = packagerBehaviour;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created PackagerBehaviour for NPC {packager.fullName}", DebugLogger.Category.Packager);
      }
      employeeBehaviour = packagerBehaviour;
      return true;
    }

    public static PackagerBehaviour GetPackagerBehaviour(Packager packager, PackagerAdapter adapter)
    {
      var packagerBehaviour = ActiveBehaviours.TryGetValue(packager.GUID, out var beh) ? beh as PackagerBehaviour : null;
      if (packagerBehaviour == null)
      {
        packagerBehaviour = new PackagerBehaviour(packager, adapter);
        ActiveBehaviours[packager.GUID] = packagerBehaviour;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created PackagerBehaviour for NPC {packager.fullName}", DebugLogger.Category.Packager);
      }
      return packagerBehaviour;
    }
  }
}