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
using static NoLazyWorkers.Structures.StorageExtensions;
using static NoLazyWorkers.Structures.StorageUtilities;
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

namespace NoLazyWorkers.Stations
{
  public static class StationExtensions
  {
    public static Dictionary<Property, List<IStationAdapter>> PropertyStations = [];

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
      void StartOperation(Behaviour behaviour);
      int GetInputQuantity();
      List<ItemField> GetInputItemForProduct();
      int MaxProductQuantity { get; }
      ITransitEntity TransitEntity { get; }
      List<ItemInstance> RefillList();
      bool MoveOutputToShelf();
      bool CanAcceptItem(ItemInstance item);
    }
  }
}