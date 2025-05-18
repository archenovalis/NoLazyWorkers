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
using static NoLazyWorkers.Structures.StorageUtilities;
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Structures;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;

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
      public bool HandleGrabbing(Behaviour behaviour, StateData state) => false;
      public bool HandleInserting(Behaviour behaviour, StateData state) => false;
      public bool HandleOperating(Behaviour behaviour, StateData state) => false;
      public bool HandleInventoryItem(Behaviour behaviour, StateData state, ItemInstance item) => false;
      public bool HandlePlanning(Behaviour behaviour, StateData state) => false;
    }
  }
}