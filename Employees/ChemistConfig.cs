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
using static NoLazyWorkers.Stations.Extensions;
using static NoLazyWorkers.Employees.Extensions;
using static NoLazyWorkers.Employees.Utilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using NoLazyWorkers.Storage;
using ScheduleOne.NPCs;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using ScheduleOne.EntityFramework;
using NoLazyWorkers.Stations;
using FishNet.Object;
using FishNet.Managing.Object;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Debug;
using Steamworks;

namespace NoLazyWorkers.Employees
{
  public class ChemistAdapter : IEmployeeAdapter
  {
    private readonly Chemist _chemist;
    private readonly EmployeeBehaviour _employeeBehaviour;
    public Guid Guid => _chemist.GUID;
    public EmployeeBehaviour AdvBehaviour => _employeeBehaviour;
    public Property AssignedProperty => _chemist.AssignedProperty;
    public NpcSubType SubType => NpcSubType.Chemist;
    public List<ItemSlot> InventorySlots => _chemist.Inventory.ItemSlots;

    public ChemistAdapter(Chemist chemist)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      _employeeBehaviour = new ChemistBehaviour(chemist, this);
      Log(Level.Info,
          $"ChemistAdapter: Initialized for NPC {chemist.fullName}",
          Category.Chemist);
    }
  }
}
