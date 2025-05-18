using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Structures.StorageUtilities;

using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Stations.LabOvenExtensions;
using static NoLazyWorkers.Stations.ChemistryStationExtensions;
using static NoLazyWorkers.Stations.CauldronExtensions;
using NoLazyWorkers.Employees;

namespace NoLazyWorkers.Employees
{
  /* [HarmonyPatch(typeof(Employee))]
  public class EmployeePatch
  {
    [HarmonyPatch("OnDestroy")]
    [HarmonyPostfix]
    static void OnDestroyPostfix(Employee __instance)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"EmployeePatch.OnDestroy: Entered for {__instance?.fullName}",
          DebugLogger.Category.Chemist);
      if (__instance is Chemist chemist)
      {
        EmployeeBehaviour.Cleanup(chemist.StartCauldronBehaviour);
        EmployeeBehaviour.Cleanup(chemist.StartMixingStationBehaviour);
        DebugLogger.Log(DebugLogger.LogLevel.Info,
            $"EmployeePatch.OnDestroy: Cleaned up states for {chemist?.fullName}",
            DebugLogger.Category.Chemist);
      }
    }
  } */
}