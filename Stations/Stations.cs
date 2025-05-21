using FishNet.Connection;
using FishNet.Object;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Property;
using Grid = ScheduleOne.Tiles.Grid;
using ScheduleOne.UI.Management;
using System.Collections;
using TMPro;
using Object = UnityEngine.Object;
using UnityEngine.UI;
using UnityEngine.Events;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.NoLazyUtilities;
using static NoLazyWorkers.ConfigurationExtensions;
using static NoLazyWorkers.General.StorageExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using FishNet.Managing;
using FishNet.Managing.Object;
using ScheduleOne.Product.Packaging;
using ScheduleOne.Persistence;
using ScheduleOne.NPCs.Behaviour;
using UnityEngine;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Stations;
using System.Reflection;
using NoLazyWorkers.Employees;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using ScheduleOne.Employees;

namespace NoLazyWorkers.Stations
{
  public static class StationExtensions
  {
    public static Dictionary<Property, List<IStationAdapter>> PropertyStations = [];
    public static Dictionary<Guid, IStationAdapter> StationAdapters = [];
    public static Dictionary<Guid, List<ItemInstance>> StationRefills = [];

    public interface IStationAdapter
    {
      Guid GUID { get; }
      Vector3 GetAccessPoint(NPC npc);
      ItemSlot InsertSlot { get; }
      List<ItemSlot> ProductSlots { get; }
      ItemSlot OutputSlot { get; }
      bool IsInUse { get; }
      bool HasActiveOperation { get; }
      int StartThreshold { get; }
      void StartOperation(Employee employee);
      int GetInputQuantity();
      List<ItemField> GetInputItemForProduct();
      int MaxProductQuantity { get; }
      ITransitEntity TransitEntity { get; }
      List<ItemInstance> RefillList();
      bool CanRefill(ItemInstance item);
      bool MoveOutputToShelf();
      Type TypeOf { get; }
    }
  }


  public static class StationUtilities
  {
    public static void GetStationAdapter(BuildableItem station, out IStationAdapter adapter)
    {
      if (!StationAdapters.TryGetValue(station.GUID, out var stationAdapter))
      {
        if (station is PackagingStation)
          stationAdapter = new PackagingStationAdapter(station as PackagingStation);
        else if (station is MixingStation)
          stationAdapter = new MixingStationAdapter(station as MixingStation);
        /*else if (station is LabOven)
          stationAdapter = new LabOvenAdapter(station as LabOven);
        else if (station is Cauldron)
          stationAdapter = new CauldronAdapter(station as Cauldron); */
        StationAdapters[station.GUID] = stationAdapter;
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RetrieveBehaviour: Created station adapter for station {station.GUID}", DebugLogger.Category.Packager);
      }
      adapter = stationAdapter;
    }
    public static IStationAdapter GetStationBehaviour(Employee employee)
    {
      var adapter = EmployeeExtensions.StationAdapterBehaviours.FirstOrDefault(a => a.Value == employee).Key;
      if (adapter == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GetStation: No station adapter found for behaviour {employee.GetHashCode()} (Type={employee.GetType().Name})", DebugLogger.Category.AllEmployees);
        foreach (var entry in EmployeeExtensions.StationAdapterBehaviours)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"GetStation: Registered behaviour {entry.Value.GetHashCode()} (Type={entry.Value.GetType().Name}) for adapter {entry.Key.GUID}", DebugLogger.Category.AllEmployees);
        }
      }
      return adapter;
    }
  }
}