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
using static NoLazyWorkers.NoLazyWorkersExtensions;
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
using static NoLazyWorkers.Stations.PackagingStationExtensions;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using ScheduleOne.Employees;
using FishNet;

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
      string Name { get; }
      Vector3 GetAccessPoint(NPC npc);
      List<ItemSlot> InsertSlots { get; }
      List<ItemSlot> ProductSlots { get; }
      ItemSlot OutputSlot { get; }
      bool IsInUse { get; }
      bool HasActiveOperation { get; }
      int StartThreshold { get; }
      void StartOperation(Employee employee);
      List<ItemField> GetInputItemForProduct();
      int MaxProductQuantity { get; }
      ITransitEntity TransitEntity { get; }
      List<ItemInstance> RefillList();
      bool CanRefill(ItemInstance item);
      Type TypeOf { get; }
    }
  }
}