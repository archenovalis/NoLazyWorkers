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
    }

    public interface IStationAdapter<TStation> : IStationAdapter where TStation : class
    {
      TStation Station { get; }
    }

    public static class StationTypeRegistry
    {
      private static readonly Dictionary<Type, (Type StationType, EmployeeBehaviour Handler)> _registry = new();

      static StationTypeRegistry()
      {
        Register<StartMixingStationBehaviour, MixingStation>(new MixingStationBehaviour());
        Register<StartCauldronBehaviour, Cauldron>(new CauldronBehaviour());
        Register<StartLabOvenBehaviour, LabOven>(new LabOvenBehaviour());
        Register<StartChemistryStationBehaviour, ChemistryStation>(new ChemistryStationBehaviour());
      }

      public static void Register<TBehaviour, TStation>(EmployeeBehaviour handler)
          where TBehaviour : Behaviour
          where TStation : class
      {
        _registry[typeof(TBehaviour)] = (typeof(TStation), handler);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"StationTypeRegistry: Registered TBehaviour={typeof(TBehaviour).Name}, TStation={typeof(TStation).Name}",
            DebugLogger.Category.General);
      }

      public static bool TryGetStationTypes(Type behaviourType, out (Type StationType, EmployeeBehaviour Handler) types)
      {
        return _registry.TryGetValue(behaviourType, out types);
      }
    }

    public static class StationHandlerFactory
    {
      private static readonly Dictionary<Type, MethodInfo> _cachedMethods = new();

      public static IStationAdapter GetStation(Behaviour behaviour)
      {
        if (behaviour == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "GetStation: Behaviour is null", DebugLogger.Category.General);
          return null;
        }

        Type behaviourType = behaviour.GetType();
        if (!StationTypeRegistry.TryGetStationTypes(behaviourType, out var types))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error,
              $"GetStation: No registered station types for behaviour {behaviourType.Name}",
              DebugLogger.Category.General);
          return null;
        }

        var key = types.StationType;
        if (!_cachedMethods.TryGetValue(key, out var constructedMethod))
        {
          MethodInfo genericMethod = typeof(StationHandlerFactory)
              .GetMethod(nameof(GetStationGeneric), BindingFlags.NonPublic | BindingFlags.Static);
          constructedMethod = genericMethod.MakeGenericMethod(types.StationType);
          _cachedMethods[key] = constructedMethod;
        }

        object result = constructedMethod.Invoke(null, new object[] { behaviour, types.Handler });
        return (IStationAdapter)result;
      }

      private static IStationAdapter<TStation> GetStationGeneric<TStation>(
          Behaviour behaviour, EmployeeBehaviour handler) where TStation : class
      {
        return handler.GetStation<TStation>(behaviour);
      }
    }
  }
}