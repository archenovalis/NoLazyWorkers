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
using NoLazyWorkers.Structures;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using ScheduleOne.EntityFramework;

namespace NoLazyWorkers.Employees
{
  public static class PackagerUtilities
  {
    public static bool RetrieveBehaviour(Packager packager, PackagerAdapter employee, NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour)
    {
      employeeBehaviour = null;
      if (npc == null || station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RetrieveBehaviour: Invalid NPC or station for packager {packager?.fullName ?? "null"}", DebugLogger.Category.Packager);
        return false;
      }
      if (npc != packager)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RetrieveBehaviour: NPC mismatch. Expected {packager?.fullName ?? "null"}, got {npc.fullName}", DebugLogger.Category.Packager);
        return false;
      }
      if (!(station is PackagingStation packagingStation))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RetrieveBehaviour: Invalid station type {station?.GetType().Name ?? "null"} for packager {packager.fullName}", DebugLogger.Category.Packager);
        return false;
      }
      if (!EmployeeAdapters.TryGetValue(npc.GUID, out var adapter) || adapter == null)
      {
        adapter = new PackagerAdapter(npc as Packager);
        EmployeeAdapters[npc.GUID] = adapter;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created adapter for NPC {npc.fullName}", DebugLogger.Category.Packager);
      }
      if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
      {
        stationAdapter = new PackagingStationAdapter(packagingStation);
        StationAdapters[station.GUID] = stationAdapter;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created station adapter for station {station.GUID}", DebugLogger.Category.Packager);
      }
      var packagerBehaviour = ActiveBehaviours.TryGetValue(npc.GUID, out var beh) ? beh as PackagerBehaviour : null;
      if (packagerBehaviour == null)
      {
        packagerBehaviour = new PackagerBehaviour(packager, stationAdapter, employee);
        ActiveBehaviours[npc.GUID] = packagerBehaviour;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created PackagerBehaviour for NPC {npc.fullName} and station {station.GUID}", DebugLogger.Category.Packager);
      }
      employeeBehaviour = packagerBehaviour;
      return true;
    }

    public static PackagerBehaviour GetPackagerBehaviour(Packager packager)
    {
      var packagerBehaviour = ActiveBehaviours.TryGetValue(packager.GUID, out var beh) ? beh as PackagerBehaviour : null;
      if (packagerBehaviour == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"HandlePlanning: No PackagerBehaviour for NPC {packager.fullName}", DebugLogger.Category.Packager);
        return null;
      }
      return packagerBehaviour;
    }
  }
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

      public bool GetEmployeeBehaviour(NPC npc, BuildableItem station, out EmployeeBehaviour employeeBehaviour) => RetrieveBehaviour(_packager, this, npc, station, out employeeBehaviour);
      public bool HandleIdle(Behaviour behaviour, StateData state) => false;
      public bool HandlePlanning(Behaviour behaviour, StateData state) => GetPackagerBehaviour(_packager).Planning(behaviour, state);
      public bool HandleMoving(Behaviour behaviour, StateData state) => false;
      public bool HandleGrabbing(Behaviour behaviour, StateData state) => GetPackagerBehaviour(_packager).Grabbing(behaviour, state);
      public bool HandleInserting(Behaviour behaviour, StateData state) => GetPackagerBehaviour(_packager).Inserting(behaviour, state);
      public bool HandleOperating(Behaviour behaviour, StateData state) => GetPackagerBehaviour(_packager).Operating(behaviour, state);
      public bool HandleCompleted(Behaviour behaviour, StateData state) => false;
      public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item) => GetPackagerBehaviour(_packager).InventoryItem(behaviour, state, item);
    }
  }
}