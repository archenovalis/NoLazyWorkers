// Summary: Defines core logic for NPC item movement and storage management in a Unity game mod.
//          Handles route planning, item pickup/delivery, and storage configuration using MelonLoader and Harmony patches.
// Role: Extends NPC behavior to manage item transfers between shelves, stations, and docks, and customizes storage rack behavior.
// Related Files: DebugLogger.cs, NavMeshUtility.cs, CoroutineRunner.cs, StorageConfigurableProxy.cs
// Dependencies: Unity, MelonLoader, HarmonyLib, Newtonsoft.Json
// Assumptions: All game fields are publicized at compile time; server-side logic runs on InstanceFinder.IsServer.

using System.Collections;
using FishNet;
using HarmonyLib;
using MelonLoader;
using NoLazyWorkers.Employees;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
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
using static NoLazyWorkers.Employees.PackagerExtensions;
using static NoLazyWorkers.NoLazyUtilities;
using GameKit.Utilities;
using Beautify.Demos;
using FishNet.Object;
using Pathfinding.Examples;
using ScheduleOne.NPCs;
using UnityEngine.InputSystem;
using NoLazyWorkers.Structures;
using System;
using static NoLazyWorkers.Employees.EmployeeExtensions.PrioritizedRoute;
using UnityEngine.InputSystem.EnhancedTouch;
using System.Data.Common;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.Employees.EmployeeUtilities;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;

namespace NoLazyWorkers.Employees
{
  public class PackagerBehaviour : EmployeeBehaviour
  {
    public PackagerBehaviour(Behaviour behaviour, IStationAdapter station, IEmployeeAdapter employee) : base(behaviour.Npc, employee)
    {
      RegisterStationBehaviour(behaviour, station);
    }
  }
}