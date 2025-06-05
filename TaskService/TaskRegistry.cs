using FishNet;
using ScheduleOne.Employees;
using UnityEngine;
using static NoLazyWorkers.Storage.Utilities;
using static NoLazyWorkers.Storage.Extensions;
using static NoLazyWorkers.Stations.Extensions;
using System.Collections.Concurrent;
using Unity.Jobs;
using Unity.Collections;
using ScheduleOne.Property;
using Unity.Burst;
using ScheduleOne.ObjectScripts;
using static NoLazyWorkers.TaskService.Extensions;
using System.Diagnostics;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using NoLazyWorkers.Storage;
using static NoLazyWorkers.Employees.Extensions;
using ScheduleOne.Management;
using ScheduleOne.Delivery;
using NoLazyWorkers.TaskService;
using NoLazyWorkers.TaskService.StationTasks;
using NoLazyWorkers.TaskService.EmployeeTasks;

namespace NoLazyWorkers.TaskService
{
  public static class TaskRegistry
  {
    public static void RegisterTaskDefinitions()
    {
      TaskDefinitionRegistry.Register(AnyEmployeeTasks.Register());
      //TaskDefinitionRegistry.Register(PackagerTasks.Register());
      TaskDefinitionRegistry.Register(MixingStationTasks.Register());
      //TaskDefinitionRegistry.Register(PackagingStationTasks.Register());
    }

    public static class TaskDefinitionRegistry
    {
      private static readonly List<ITaskDefinition> _definitions = new();

      public static void Register(List<ITaskDefinition> definitions)
      {
        foreach (var definition in definitions)
        {
          _definitions.Add(definition);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"Registered task definition: {definition.Type}", DebugLogger.Category.TaskManager);
        }
      }

      public static ITaskDefinition Get(TaskTypes type) => _definitions.FirstOrDefault(d => d.Type == type);

      public static IEnumerable<ITaskDefinition> AllDefinitions => _definitions;
    }
  }
}