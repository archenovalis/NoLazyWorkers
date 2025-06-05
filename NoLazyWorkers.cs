using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Reflection;

//using NoLazyWorkers.Packagers;
using NoLazyWorkers.Stations;
using NoLazyWorkers.Botanists;
using NoLazyWorkers.Employees;
using FluffyUnderware.DevTools.Extensions;
using NoLazyWorkers.Storage;
using FishNet.Object;
using ScheduleOne.DevUtilities;
using FishNet;
using FishNet.Connection;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.Management.UI;
using UnityEngine.Events;
using System.Collections.Concurrent;
using UnityEngine.AI;
using ScheduleOne.NPCs;
using NoLazyWorkers.TaskService;
using FishNet.Managing.Timing;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkersMod), "NoLazyWorkers", "1.1.9", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers
{
  public static class DebugLogs
  {
    public static bool Enabled = true;
    public static int Level = 4; //LogLevel { None(0), Error(1), Warning(2), Info(3), Verbose(4), Stacktrace(5) }
    public static bool All = true; // enables all but stacktrace logs
    public static bool Core = true;
    public static bool Settings = false;
    // employees
    public static bool AllEmployees = false;
    public static bool EmployeeCore = true;
    public static bool Chemist = false;
    public static bool Botanist = true;
    public static bool Packager = true;
    // generic
    public static bool Storage = true;
    public static bool General = true;
    // stations
    public static bool Pot = false;
    public static bool DryingRack = false;
    public static bool BrickPress = false;
    public static bool Cauldron = false;
    public static bool LabOven = false;
    public static bool ChemistryStation = false;
    public static bool MixingStation = false;
    public static bool PackagingStation = true;
    //
    public static bool TaskManager = true;
    public static bool Stacktrace = false;
  }

  public static class DebugLogger
  {
    public enum LogLevel { None, Error, Warning, Info, Verbose, Stacktrace }
    public enum Category
    {
      None,
      Core,
      Settings,
      AllEmployees,
      AnyEmployee,
      EmployeeCore,
      Chemist,
      Botanist,
      Handler,
      Storage,
      Pot,
      DryingRack,
      BrickPress,
      PackagingStation,
      LabOven,
      Cauldron,
      ChemistryStation,
      MixingStation,
      General,
      TaskManager,
      Stacktrace
    }
    public static bool AnyEmployee;
    public static LogLevel CurrentLevel { get; set; } = (LogLevel)DebugLogs.Level;

    private static readonly Dictionary<Category, Func<bool>> CategoryEnabled = new()
    {
        { Category.Core, () => DebugLogs.Core },
        { Category.Settings, () => DebugLogs.Settings },
        { Category.AllEmployees, () => DebugLogs.AllEmployees },
        { Category.AnyEmployee, () => AnyEmployee },
        { Category.EmployeeCore, () => DebugLogs.EmployeeCore },
        { Category.Chemist, () => DebugLogs.Chemist },
        { Category.Botanist, () => DebugLogs.Botanist },
        { Category.Handler, () => DebugLogs.Packager },
        { Category.Storage, () => DebugLogs.Storage },
        { Category.Pot, () => DebugLogs.Pot },
        { Category.BrickPress, () => DebugLogs.BrickPress },
        { Category.Cauldron, () => DebugLogs.Cauldron },
        { Category.DryingRack, () => DebugLogs.DryingRack },
        { Category.LabOven, () => DebugLogs.LabOven },
        { Category.ChemistryStation, () => DebugLogs.ChemistryStation },
        { Category.General, () => DebugLogs.General },
        { Category.MixingStation, () => DebugLogs.MixingStation },
        { Category.PackagingStation, () => DebugLogs.PackagingStation },
        { Category.Stacktrace, () => DebugLogs.Stacktrace },
        { Category.TaskManager, () => DebugLogs.TaskManager },
        { Category.None, () => true } // Always enabled if All is true
    };

    public static void Log(LogLevel level, string message, params Category[] categories)
    {
      if (!DebugLogs.Enabled || level > CurrentLevel)
        return;

      // Determine if any category is enabled
      AnyEmployee = DebugLogs.Chemist || DebugLogs.Botanist || DebugLogs.Packager;
      bool isEnabled = DebugLogs.All || categories.Any(c => c != Category.Stacktrace && CategoryEnabled[c]());
      if (!isEnabled)
        return;

      // Get the first non-Stacktrace category for labeling (or "None" if only Stacktrace)
      Category labelCategory = categories.FirstOrDefault(c => c != Category.Stacktrace);
      string prefix = $"[{labelCategory}]";
      bool includeStacktrace = DebugLogs.Stacktrace;

      // Format the message
      string fullMessage = $"{prefix} {message}";
      if (includeStacktrace)
      {
        fullMessage += $"\nStacktrace: {Environment.StackTrace}";
      }

      // Output to MelonLogger
      switch (level)
      {
        case LogLevel.Error:
          MelonLogger.Error(fullMessage);
          break;
        case LogLevel.Warning:
          MelonLogger.Warning(fullMessage);
          break;
        default:
          MelonLogger.Msg(fullMessage);
          break;
      }
    }
  }

  public static class BuildInfo
  {
    public const string Name = "NoLazyWorkers";
    public const string Description = "Botanist's supply is moved to each pot and a supply is added to mixing stations. Botanists and Chemists will get items from their station's supply. Mixing Stations can have multiple recipes that loop the output. Multiple employee-related configurable settings.";
    public const string Author = "Archie";
    public const string Version = "1.1.9";
  }

  public class NoLazyWorkersMod : MelonMod
  {
    public static NoLazyWorkersMod Instance { get; private set; }
    public Settings.Default Config { get; private set; }

    public override void OnInitializeMelon()
    {
      try
      {
        HarmonyInstance.PatchAll();
        MelonLogger.Msg("NoLazyWorkers_Alternative loaded!");
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"Failed to initialize NoLazyWorkers_Alternative: {e}", DebugLogger.Category.Core);
      }

      Instance = this;
      string configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "NoLazyWorkers.cfg");
      if (File.Exists(configPath))
      {
        Config = Settings.Default.LoadFromFile(configPath);
        DebugLogger.Log(DebugLogger.LogLevel.Info, "Config loaded.", DebugLogger.Category.Core);
      }
      else
      {
        Config = new Settings.Default();
        Config.SaveToFile(configPath);
        DebugLogger.Log(DebugLogger.LogLevel.Info, "Default config created.", DebugLogger.Category.Core);
      }

      MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneWasLoaded);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
      if (sceneName == "Main")
      {
        var configure = new Settings.Configure();
        MelonCoroutines.Start(configure.ApplyOneShotSettingsRoutine());
        DebugLogger.Log(DebugLogger.LogLevel.Info, "Applied Fixer and Misc settings on main scene load.", DebugLogger.Category.Core);
        MixingStationConfigUtilities.InitializeStaticRouteListTemplate();
        ShelfUtilities.InitializeStorageModule();
        CacheManager.Initialize();
        TaskCoordinator.Initialize();
      }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
      NoLazyUtilities.ClearPrefabs();
      Employees.Utilities.ClearAll();
      NoLazyWorkersExtensions.NPCSupply.Clear();
      Settings.SettingsExtensions.Configured.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, "Cleared ConfigurationExtensions and SettingsExtensions on scene unload.", DebugLogger.Category.Core);
    }
  }

  public static class TimeManagerExtensions
  {
    /// <summary>
    /// Asynchronously waits for the next FishNet TimeManager tick.
    /// </summary>
    /// <param name="timeManager">The FishNet TimeManager instance.</param>
    /// <returns>A Task that completes on the next tick.</returns>
    public static async Task AwaitNextTickAsync(this TimeManager timeManager)
    {
      var tcs = new TaskCompletionSource<bool>();
      double currentTick = timeManager.Tick;

      void OnTick()
      {
        if (timeManager.Tick > currentTick)
        {
          timeManager.OnTick -= OnTick;
          tcs.SetResult(true);
        }
      }

      timeManager.OnTick += OnTick;
      await tcs.Task;
    }

    /// <summary>
    /// Asynchronously waits for the specified duration in seconds, aligned with FishNet TimeManager ticks.
    /// </summary>
    /// <param name="timeManager">The FishNet TimeManager instance.</param>
    /// <param name="seconds">The duration to wait in seconds.</param>
    /// <returns>A Task that completes after the specified delay.</returns>
    public static async Task AwaitNextTickAsync(this TimeManager timeManager, float seconds)
    {
      if (seconds <= 0f)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"AwaitNextTickAsync: Invalid delay {seconds}s, using no delay", DebugLogger.Category.TaskManager);
        return;
      }

      int ticksToWait = Mathf.CeilToInt(seconds * timeManager.TickRate);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"AwaitNextTickAsync: Waiting for {seconds}s ({ticksToWait} ticks) at tick rate {timeManager.TickRate}", DebugLogger.Category.TaskManager);

      for (int i = 0; i < ticksToWait; i++)
      {
        await timeManager.AwaitNextTickAsync();
      }
    }
  }

  public static class UnityEventExtensions
  {
    public static int GetListenerCount(this UnityEventBase unityEvent)
    {
      return unityEvent.GetPersistentEventCount() + unityEvent.m_Calls.Count;
    }
  }

  public static class NoLazyWorkersExtensions
  {
    public static Dictionary<Guid, ObjectField> NPCSupply = [];
    private static readonly Dictionary<EntityConfiguration, float> lastInvokeTimes = [];
    private static readonly float debounceTime = 0.2f;

    /* public static void InvokeChanged(EntityConfiguration config)
    {
      try
      {
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, "InvokeChanged: EntityConfiguration is null", DebugLogger.Category.Core);
          return;
        }

        float currentTime = Time.time;
        if (lastInvokeTimes.TryGetValue(config, out float lastTime) && currentTime - lastTime < debounceTime)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InvokeChanged debounced for config: {config}", DebugLogger.Category.Core);
          return;
        }

        lastInvokeTimes[config] = currentTime;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"InvokeChanged called for config: {config}", DebugLogger.Category.Core);
        config.InvokeChanged();
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"InvokeChanged failed: {e}", DebugLogger.Category.Core);
      }
    } */

  }

  public static class NoLazyUtilities
  {
    public static void ClearPrefabs()
    {
      CachedPrefabs.Clear();
    }
    public static void LogHierarchy(Transform transform, int depth = 0)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"{new string(' ', depth * 2)}{transform.name}", DebugLogger.Category.MixingStation);
      for (int i = 0; i < transform.childCount; i++)
      {
        LogHierarchy(transform.GetChild(i), depth + 1);
      }
    }
    /* /// <summary>
    /// Converts a Collections.Generic.List<T> to an Il2CppCollections.Generic.List<T>.
    /// </summary>
    /// <typeparam name="T">The type of elements in the list, must inherit from Il2CppObject.</typeparam>
    /// <param name="systemList">The System list to convert.</param>
    /// <returns>An Il2CppSystem list containing the same elements, or an empty list if the input is null.</returns>
    public static Il2CppCollections.Generic.List<T> ConvertList<T>(List<T> systemList)
        where T : Il2CppObject
    {
      if (systemList == null)
        return new Il2CppCollections.Generic.List<T>();

      Il2CppCollections.Generic.List<T> il2cppList = new(systemList.Count);
      foreach (var item in systemList)
      {
        if (item != null)
          il2cppList.Add(item);
      }
      return il2cppList;
    }

    /// <summary>
    /// Converts an Il2CppCollections.Generic.List<T> to a Collections.Generic.List<T>.
    /// </summary>
    /// <typeparam name="T">The type of elements in the list, must inherit from Il2CppObject.</typeparam>
    /// <param name="il2cppList">The Il2CppSystem list to convert.</param>
    /// <returns>A System list containing the same elements, or an empty list if the input is null.</returns>
    public static List<T> ConvertList<T>(Il2CppCollections.Generic.List<T> il2cppList)
        where T : Il2CppObject
    {
      if (il2cppList == null)
        return [];

      List<T> systemList = new(il2cppList.Count);
      for (int i = 0; i < il2cppList.Count; i++)
      {
        var item = il2cppList[i];
        if (item != null)
          systemList.Add(item);
      }
      return systemList;
    } 
  } */

    public class CoroutineRunner : MonoBehaviour
    {
      private static CoroutineRunner _instance;
      public static CoroutineRunner Instance
      {
        get
        {
          if (_instance == null)
          {
            var go = new GameObject("CoroutineRunner");
            _instance = go.AddComponent<CoroutineRunner>();
            DontDestroyOnLoad(go);
          }
          return _instance;
        }
      }

      public void RunCoroutine(IEnumerator coroutine)
      {
        StartCoroutine(RunCoroutineInternal(coroutine));
      }

      private IEnumerator RunCoroutineInternal(IEnumerator coroutine)
      {
        while (true)
        {
          object current;
          try
          {
            if (!coroutine.MoveNext())
              yield break;
            current = coroutine.Current;
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"CoroutineRunner: Exception in coroutine: {e.Message}", DebugLogger.Category.Core);
            yield break;
          }
          yield return current;
        }
      }

      public void RunCoroutineWithResult<T>(IEnumerator coroutine, Action<T> callback)
      {
        StartCoroutine(RunCoroutineInternal(coroutine, callback));
      }

      private IEnumerator RunCoroutineInternal<T>(IEnumerator coroutine, Action<T> callback)
      {
        while (true)
        {
          object current;
          try
          {
            if (!coroutine.MoveNext())
              yield break;
            current = coroutine.Current;
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"CoroutineRunner: Exception in coroutine: {e.Message}", DebugLogger.Category.Core);
            callback?.Invoke(default);
            yield break;
          }
          if (current is T result)
          {
            callback?.Invoke(result);
            yield break;
          }
          yield return current;
        }
      }
    }

    public static Transform GetTransformTemplateFromConfigPanel(EConfigurableType configType, string componentStr)
    {
      try
      {
        // Create a dummy configuration based on the config type
        GameObject dummyEntity = new($"Dummy{configType}");
        EntityConfiguration config = null;
        GameObject configPanelPrefab = null;
        ConfigurationReplicator replicator = new();
        switch (configType)
        {
          case EConfigurableType.Pot:
            Pot pot = dummyEntity.AddComponent<Pot>();
            config = new PotConfiguration(replicator, pot, pot);
            configPanelPrefab = GetPrefabGameObject("PotConfigPanel");
            break;
          case EConfigurableType.Packager:
            Packager packager = dummyEntity.AddComponent<Packager>();
            config = new PackagerConfiguration(replicator, packager, packager);
            configPanelPrefab = GetPrefabGameObject("PackagerConfigPanel");
            break;
          case EConfigurableType.MixingStation:
            MixingStation mixingStation = dummyEntity.AddComponent<MixingStation>();
            config = new MixingStationConfiguration(replicator, mixingStation, mixingStation);
            configPanelPrefab = GetPrefabGameObject("MixingStationPanel");
            break;
          // Add other types as needed
          default:
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"Unsupported EConfigurableType: {configType}", DebugLogger.Category.Core);
            UnityEngine.Object.Destroy(dummyEntity);
            return null;
        }

        if (configPanelPrefab == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"No ConfigPanel prefab found for {configType}", DebugLogger.Category.Core);
          return null;
        }

        GameObject tempPanelObj = UnityEngine.Object.Instantiate(configPanelPrefab);
        tempPanelObj.SetActive(false);
        ConfigPanel tempPanel = tempPanelObj.GetComponent<ConfigPanel>();
        if (tempPanel == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"Instantiated prefab for {configType} lacks ConfigPanel component", DebugLogger.Category.Core);
          UnityEngine.Object.Destroy(tempPanelObj);
          return null;
        }

        // Bind to initialize UI components (mimic game behavior)
        List<EntityConfiguration> configs = [];
        configs.Add(config);
        tempPanel.Bind(configs);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Bound temporary ConfigPanel for {configType} to initialize UI components", DebugLogger.Category.Core);

        // Get the UI template
        var uiTemplate = tempPanel.transform.Find(componentStr);
        if (uiTemplate == null)
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"Failed to retrieve UI template from ConfigPanel for {configType}", DebugLogger.Category.Core);
        else
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"Successfully retrieved UI template from ConfigPanel for {configType}", DebugLogger.Category.Core);

        // Clean up
        UnityEngine.Object.Destroy(tempPanelObj);
        UnityEngine.Object.Destroy(dummyEntity);
        return uiTemplate;
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"Failed to get UI template from ConfigPanel for {configType}: {e}", DebugLogger.Category.Core);
        return null;
      }
    }

    private static readonly Dictionary<string, GameObject> CachedPrefabs = [];
    public static GameObject GetPrefabGameObject(string id)
    {
      try
      {
        if (CachedPrefabs.Count > 0 && CachedPrefabs.Keys.Contains(id))
          return CachedPrefabs[id];
        GameObject prefab = null;
        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (GameObject obj in allObjects)
        {
          if (obj.name.Contains(id))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Found prefab: {obj.name}", DebugLogger.Category.Core);
            prefab = obj;
            break;
          }
        }

        if (prefab != null)
        {
          GameObject instance = UnityEngine.Object.Instantiate(prefab);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"Instantiated prefab: {instance.name}", DebugLogger.Category.Core);
          CachedPrefabs[id] = prefab;
          return instance;
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"Prefab {id} not found in Resources.", DebugLogger.Category.Core);
          return null;
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"Failed to find prefab: {e}", DebugLogger.Category.Core);
        return null;
      }
    }

    public static void LogItemFieldUIDetails(ItemFieldUI itemfieldUI)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, "=== ItemFieldUI Details ===", DebugLogger.Category.Core);

      // Log basic info
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ItemFieldUI GameObject: {(itemfieldUI.gameObject != null ? itemfieldUI.gameObject.name : "null")}", DebugLogger.Category.Core);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ItemFieldUI Active: {itemfieldUI.gameObject?.activeSelf}", DebugLogger.Category.Core);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"ItemFieldUI Type: {itemfieldUI.GetType().Name}", DebugLogger.Category.Core);

      // Log ItemFieldUI properties
      LogComponentDetails(itemfieldUI, 0);
      DebugLogger.Log(DebugLogger.LogLevel.Info, "--- Hierarchy and Components ---", DebugLogger.Category.Core);

      // Log hierarchy and components
      if (itemfieldUI.gameObject != null)
      {
        LogGameObjectDetails(itemfieldUI.gameObject, 0);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, "ItemFieldUI GameObject is null, cannot log hierarchy and components", DebugLogger.Category.Core);
      }
    }

    static void LogGameObjectDetails(GameObject go, int indentLevel)
    {
      if (go == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, new string(' ', indentLevel * 2) + "GameObject: null", DebugLogger.Category.Core);
        return;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, new string(' ', indentLevel * 2) + $"GameObject: {go.name}, Active: {go.activeSelf}", DebugLogger.Category.Core);

      // Log components on this GameObject
      foreach (var component in go.GetComponents<Component>())
      {
        LogComponentDetails(component, indentLevel + 1);
      }

      // Recursively log children
      foreach (Transform child in go.transform)
      {
        LogGameObjectDetails(child.gameObject, indentLevel + 1);
      }
    }

    public static string DebugTransformHierarchy(Transform transform, int indent = 0)
    {
      string indentStr = new string(' ', indent * 2);
      string result = $"{indentStr}{transform.name} (Active: {transform.gameObject.activeSelf}, Layer: {LayerMask.LayerToName(transform.gameObject.layer)})\n";
      foreach (Transform child in transform)
      {
        result += DebugTransformHierarchy(child, indent + 1);
      }
      return result;
    }

    static void LogComponentDetails(Component component, int indentLevel)
    {
      if (component == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, new string(' ', indentLevel * 2) + "Component: null", DebugLogger.Category.Core);
        return;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose, new string(' ', indentLevel * 2) + $"Component: {component.GetType().Name}", DebugLogger.Category.Core);

      // Use reflection to log all public fields
      var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
      foreach (var field in fields)
      {
        try
        {
          var value = field.GetValue(component);
          string valueStr = ValueToString(value);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, new string(' ', indentLevel * 2) + $"  Field: {field.Name} = {valueStr}", DebugLogger.Category.Core);
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, new string(' ', indentLevel * 2) + $"  Failed to get field {field.Name}: {e.Message}", DebugLogger.Category.Core);
        }
      }

      // Use reflection to log all public properties
      var properties = component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
      foreach (var property in properties)
      {
        // Skip properties that can't be read
        if (!property.CanRead) continue;
        try
        {
          var value = property.GetValue(component);
          string valueStr = ValueToString(value);
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, new string(' ', indentLevel * 2) + $"  Property: {property.Name} = {valueStr}", DebugLogger.Category.Core);
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, new string(' ', indentLevel * 2) + $"  Failed to get property {property.Name}: {e.Message}", DebugLogger.Category.Core);
        }
      }
    }

    static string ValueToString(object value)
    {
      if (value == null) return "null";
      if (value is GameObject go) return $"GameObject({go.name})";
      if (value is Component comp) return $"{comp.GetType().Name} on {comp.gameObject.name}";
      if (value is IEnumerable enumerable && !(value is string))
      {
        var items = new List<string>();
        foreach (var item in enumerable)
        {
          items.Add(ValueToString(item));
        }
        return $"[{string.Join(", ", items)}]";
      }
      return value.ToString();
    }
  }

  [HarmonyPatch(typeof(ItemSetterScreen), "Open")]
  public class ItemSetterScreenOpenPatch
  {
    static void Prefix(ItemSetterScreen __instance, object option)
    {
      try
      {
        if (option is ItemField itemField)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ItemSetterScreenOpenPatch: Opening for ItemField, SelectedItem: {itemField.SelectedItem?.Name ?? "null"}", DebugLogger.Category.Core);
          if (itemField.Options != null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"ItemSetterScreenOpenPatch: ItemField options count: {itemField.Options.Count}", DebugLogger.Category.Core);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, "ItemSetterScreenOpenPatch: ItemField Options is null", DebugLogger.Category.Core);
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"ItemSetterScreenOpenPatch: Opening for {option?.GetType().Name ?? "null"}", DebugLogger.Category.Core);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"ItemSetterScreenOpenPatch: Prefix failed, error: {e}", DebugLogger.Category.Core);
      }
    }
  }

  public static class UIExtensions
  {
    public static T GetField<T>(object obj, string fieldName) where T : Component
    {
      var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
      if (field == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"Field {fieldName} not found on {obj.GetType().Name}", DebugLogger.Category.Core);
        return null;
      }
      return field.GetValue(obj) as T;
    }

    public static void SetField(object obj, string fieldName, object value)
    {
      var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
      if (field == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"Field {fieldName} not found on {obj.GetType().Name}", DebugLogger.Category.Core);
        return;
      }
      field.SetValue(obj, value);
    }

    public static T CloneComponent<T>(T source, GameObject target) where T : Component
    {
      if (source == null || target == null)
        return null;
      var newComponent = target.AddComponent<T>();
      // Copy fields
      foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
      {
        field.SetValue(newComponent, field.GetValue(source));
      }
      // Copy properties
      foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
      {
        if (prop.CanRead && prop.CanWrite)
        {
          prop.SetValue(newComponent, prop.GetValue(source));
        }
      }
      // Copy specific event listeners (e.g., for Button)
      if (newComponent is Button button && source is Button sourceButton)
      {
        button.onClick.AddListener(() => sourceButton.onClick.Invoke());
      }
      return newComponent;
    }
  }

  [HarmonyPatch(typeof(LoadManager))]
  public class LoadManagerPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch("Awake")]
    static void Postfix(LoadManager __instance)
    {
      try
      {
        __instance.onLoadComplete.AddListener(delegate
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, "onLoadComplete fired, restoring configurations", DebugLogger.Category.Core);
          PotExtensions.RestoreConfigurations();
          MixingStationConfigUtilities.RestoreConfigurations();
          //StorageExtensions.RestoreConfigurations();
        });
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"LoadManagerPatch.Awake failed: {e}", DebugLogger.Category.Core);
      }
    }
  }

  [HarmonyPatch(typeof(GridItemLoader), "LoadAndCreate")]
  public class GridItemLoaderPatch
  {
    public static Dictionary<string, GridItem> LoadedGridItems = [];

    static void Postfix(string mainPath, GridItem __result)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"GridItemLoaderPatch: Processing LoadAndCreate for mainPath: {mainPath}", DebugLogger.Category.Core);
        if (__result != null)
        {
          LoadedGridItems[mainPath] = __result;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"GridItemLoaderPatch: Captured GridItem (type: {__result.GetType().Name}) for mainPath: {mainPath}", DebugLogger.Category.Core);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"GridItemLoaderPatch: No GridItem returned for mainPath: {mainPath}", DebugLogger.Category.Core);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"GridItemLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}", DebugLogger.Category.Core);
      }
    }
  }

  [HarmonyPatch(typeof(ConfigurationReplicator), "RpcLogic___ReceiveItemField_2801973956")]
  public class ConfigurationReplicatorReceiveItemFieldPatch
  {
    static bool Prefix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ConfigurationReplicatorReceiveItemFieldPatch: Received update for fieldIndex={fieldIndex}, value={value ?? "null"}",
          DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ConfigurationReplicatorReceiveItemFieldPatch: Fields count={__instance.Configuration.Fields.Count}",
          DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
      for (int i = 0; i < __instance.Configuration.Fields.Count; i++)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ConfigurationReplicatorReceiveItemFieldPatch: Fields[{i}]={__instance.Configuration.Fields[i]?.GetType().Name ?? "null"}",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
      }

      if (fieldIndex < 0 || fieldIndex >= __instance.Configuration.Fields.Count)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ConfigurationReplicatorReceiveItemFieldPatch: Invalid fieldIndex={fieldIndex}, Configuration.Fields.Count={__instance.Configuration.Fields.Count}, skipping",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
        return false;
      }

      var itemField = __instance.Configuration.Fields[fieldIndex] as ItemField;
      if (itemField == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ConfigurationReplicatorReceiveItemFieldPatch: No ItemField at fieldIndex={fieldIndex}, Fields[{fieldIndex}]={__instance.Configuration.Fields[fieldIndex]?.GetType().Name ?? "null"}, skipping",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
        return false;
      }

      if (string.IsNullOrEmpty(value) && !itemField.CanSelectNone)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ConfigurationReplicatorReceiveItemFieldPatch: Blocked null update for ItemField with CanSelectNone={itemField.CanSelectNone}, CurrentItem={itemField.SelectedItem?.Name ?? "null"}",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
        return false;
      }

      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ConfigurationReplicatorReceiveItemFieldPatch: Allowing update for ItemField, CanSelectNone={itemField.CanSelectNone}, value={value}",
          DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
      return true;
    }

    static void Postfix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"ConfigurationReplicatorReceiveObjectFieldPatch: Received update for fieldIndex={fieldIndex}, value={value}",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);

        if (__instance.Configuration is PotConfiguration potConfig && fieldIndex == 6)
        {
          if (PotExtensions.Supply.TryGetValue(potConfig.Pot.GUID, out ObjectField supply))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info,
                $"ConfigurationReplicatorReceiveObjectFieldPatch: Updated supply for pot: {potConfig.Pot.GUID}, SelectedObject: unknown because value is a string",
                DebugLogger.Category.Core, DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning,
                $"ConfigurationReplicatorReceiveObjectFieldPatch: No supply found for pot: {potConfig.Pot.GUID}",
                DebugLogger.Category.Core, DebugLogger.Category.Botanist, DebugLogger.Category.Pot);
          }
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Stacktrace, $"ConfigurationReplicatorReceiveObjectFieldPatch: Failed for fieldIndex={fieldIndex}, error: {e}", DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
      }
    }
  }

  [HarmonyPatch(typeof(ItemField), "SetItem", new[] { typeof(ItemDefinition), typeof(bool) })]
  public class ItemFieldSetItemPatch
  {
    private static readonly Dictionary<ItemField, (float Time, ItemDefinition Item, bool Network)> RecentUpdates = new();
    private const float AntiBounceWindow = 0.15f;

    static bool Prefix(ItemField __instance, ItemDefinition item, bool network)
    {
      string callStack = Environment.StackTrace; // Capture stack trace for debugging
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"ItemFieldSetItemPatch: Called for ItemField, network={network}, CanSelectNone={__instance.CanSelectNone}, Item={item?.Name ?? "null"}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}",
          DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);

      // Anti-bounce: Block redundant updates within 0.2s
      if (!network && RecentUpdates.TryGetValue(__instance, out var recentUpdate) &&
          Time.time - recentUpdate.Time < AntiBounceWindow)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning,
            $"ItemFieldSetItemPatch: Blocked redundant update for ItemField, Item={item?.Name ?? "null"}, Network={network}, TimeSinceLast={Time.time - recentUpdate.Time:F3}s\nCallStack: {callStack}",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist);
        return false;
      }

      // Update recent updates cache
      RecentUpdates[__instance] = (Time.time, item, network);

      return true;
    }

    static void Postfix(ItemField __instance)
    {
      // Clean up old entries (optional, to prevent memory buildup)
      var keysToRemove = RecentUpdates
        .Where(kvp => Time.time - kvp.Value.Time > AntiBounceWindow * 2)
        .Select(kvp => kvp.Key)
        .ToList();

      foreach (var key in keysToRemove)
      {
        RecentUpdates.Remove(key);
      }
    }
  }

  /* [HarmonyPatch(typeof(MoveItemBehaviour))]
  public class MoveItemBehaviourTestPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("IsDestinationValid")]
    public static bool IsDestinationValidPrefix(MoveItemBehaviour __instance, TransitRoute route, ItemInstance item, ref bool __result)
    {
      MelonLogger.Warning($"[Test] [MoveItemBehaviourTestPatch] 0");
      MelonLogger.Warning($"[Test] [MoveItemBehaviourTestPatch] 1: {__instance?.Npc}");
      MelonLogger.Warning($"[Test] [MoveItemBehaviourTestPatch] 1: {__instance?.Npc?.name}");
      MelonLogger.Warning($"[Test] [MoveItemBehaviourTestPatch] 1:  | {__instance?.Npc?.NetworkObject?.name}");
      MelonLogger.Warning($"[Test] [MoveItemBehaviourTestPatch] 1:  | {route?.Destination?.GUID}");
      MelonLogger.Warning($"[Test] [MoveItemBehaviourTestPatch] 1:  | {route?.Destination?.InputSlots?.Count}");
      if (route.Destination.GetInputCapacityForItem(item, __instance.Npc) == 0)
      {
        MelonLogger.Warning("[Test] [MoveItemBehaviourTestPatch] Destination has no capacity for item!");
        __result = false;
        return false;
      }
      if (!__instance.CanGetToDestination(route))
      {
        MelonLogger.Warning("[Test] [MoveItemBehaviourTestPatch] Cannot get to destination!");
        __result = false;
        return false;
      }
      if (!__instance.CanGetToSource(route))
      {
        MelonLogger.Warning("[Test] [MoveItemBehaviourTestPatch] Cannot get to source!");
        __result = false;
        return false;
      }
      __result = true;
      return false;
    }
  }

  [HarmonyPatch]
  public static class MovementDebugPatches
  {
    [HarmonyPatch(typeof(NPCBehaviour))]
    public static class MovementDebugNPCBehaviour
    {
      [HarmonyPrefix]
      [HarmonyPatch("Update")]
      public static void NPCBehaviour_Update_Prefix(NPCBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string activeBehaviourName = __instance.activeBehaviour?.Name ?? "null";
        string enabledBehaviourName = __instance.GetEnabledBehaviour()?.Name ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
              $"NPCBehaviour.Update: NPC={npcName}, ActiveBehaviour={activeBehaviourName}, EnabledBehaviour={enabledBehaviourName}, IsHost={InstanceFinder.IsHost}",
              DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("GetEnabledBehaviour")]
      public static void NPCBehaviour_GetEnabledBehaviour_Prefix(NPCBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string enabledBehaviours = string.Join(", ", __instance.enabledBehaviours.Select(b => $"{b.Name} (Priority={b.Priority}, Enabled={b.Enabled})"));
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"NPCBehaviour.GetEnabledBehaviour: NPC={npcName}, EnabledBehaviours=[{enabledBehaviours}]",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("AddEnabledBehaviour")]
      public static void NPCBehaviour_AddEnabledBehaviour_Prefix(NPCBehaviour __instance, Behaviour b)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = b?.Name ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"NPCBehaviour.AddEnabledBehaviour: NPC={npcName}, Behaviour={behaviourName}, EnabledBehavioursCount={__instance.enabledBehaviours.Count}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("RemoveEnabledBehaviour")]
      public static void NPCBehaviour_RemoveEnabledBehaviour_Prefix(NPCBehaviour __instance, Behaviour b)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = b?.Name ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"NPCBehaviour.RemoveEnabledBehaviour: NPC={npcName}, Behaviour={behaviourName}, EnabledBehavioursCount={__instance.enabledBehaviours.Count}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("MinPass")]
      public static void NPCBehaviour_MinPass_Prefix(NPCBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string activeBehaviourName = __instance.activeBehaviour?.Name ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"NPCBehaviour.MinPass: NPC={npcName}, ActiveBehaviour={activeBehaviourName}, Active={__instance.activeBehaviour?.Active ?? false}",
            DebugLogger.Category.AllEmployees);
      }
    }

    [HarmonyPatch(typeof(Behaviour))]
    public static class MovementDebugBehaviour
    {
      [HarmonyPostfix]
      [HarmonyPatch("Enable")]
      public static void Behaviour_Enable_Postfix(Behaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.Enable: NPC={npcName}, Behaviour={behaviourName}, Enabled={__instance.Enabled}, Active={__instance.Active}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("Enable_Networked")]
      public static void Behaviour_Enable_Networked_Prefix(Behaviour __instance, NetworkConnection conn)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        string connInfo = conn == null ? "null" : $"ClientId={conn.ClientId}";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.Enable_Networked: NPC={npcName}, Behaviour={behaviourName}, Conn={connInfo}, IsServer={__instance.IsServerInitialized}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("Begin")]
      public static void Behaviour_Begin_Prefix(Behaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.Begin: NPC={npcName}, Behaviour={behaviourName}, Started={__instance.Started}, Active={__instance.Active}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("Begin_Networked")]
      public static void Behaviour_Begin_Networked_Prefix(Behaviour __instance, NetworkConnection conn)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        string connInfo = conn == null ? "null" : $"ClientId={conn.ClientId}";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.Begin_Networked: NPC={npcName}, Behaviour={behaviourName}, Conn={connInfo}, IsServer={__instance.IsServerInitialized}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("Disable")]
      public static void Behaviour_Disable_Prefix(Behaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.Disable: NPC={npcName}, Behaviour={behaviourName}, Enabled={__instance.Enabled}, Active={__instance.Active}, Started={__instance.Started}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("RpcLogic___Enable_Networked_328543758")]
      public static void Behaviour_RpcLogic_Enable_Networked_Prefix(Behaviour __instance, NetworkConnection conn)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        string connInfo = conn == null ? "null" : $"ClientId={conn.ClientId}";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.RpcLogic___Enable_Networked: NPC={npcName}, Behaviour={behaviourName}, Conn={connInfo}, Enabled={__instance.Enabled}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("RpcLogic___Disable_Networked_328543758")]
      public static void Behaviour_RpcLogic_Disable_Networked_Prefix(Behaviour __instance, NetworkConnection conn)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        string connInfo = conn == null ? "null" : $"ClientId={conn.ClientId}";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.RpcLogic___Disable_Networked: NPC={npcName}, Behaviour={behaviourName}, Conn={connInfo}, Enabled={__instance.Enabled}, Active={__instance.Active}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("End_Networked")]
      public static void Behaviour_End_Networked_Prefix(Behaviour __instance, NetworkConnection conn)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string behaviourName = __instance.Name;
        string connInfo = conn == null ? "null" : $"ClientId={conn.ClientId}";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.End_Networked: NPC={npcName}, Behaviour={behaviourName}, Conn={connInfo}, Active={__instance.Active}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("SetDestination", new[] { typeof(Vector3), typeof(bool) })]
      public static void Behaviour_SetDestination_Prefix(Behaviour __instance, Vector3 position, bool teleportIfFail)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        bool canGetTo = __instance.Npc?.Movement.CanGetTo(position, 1f) ?? false;
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"Behaviour.SetDestination: NPC={npcName}, Position={position}, TeleportIfFail={teleportIfFail}, CanGetTo={canGetTo}, PathingFailures={__instance.consecutivePathingFailures}",
            DebugLogger.Category.AllEmployees);
      }
    }

    [HarmonyPatch(typeof(MoveItemBehaviour))]
    public static class MovementDebugMoveItemBehaviour
    {
      [HarmonyPrefix]
      [HarmonyPatch("StartTransit")]
      public static void MoveItemBehaviour_StartTransit_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string routeInfo = __instance.assignedRoute != null
            ? $"Source={__instance.assignedRoute.Source?.Name ?? "null"}, Dest={__instance.assignedRoute.Destination?.Name ?? "null"}"
            : "null";
        string itemInfo = __instance.itemToRetrieveTemplate != null
            ? $"Item={__instance.itemToRetrieveTemplate.ID}, Qty={__instance.grabbedAmount}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.StartTransit: NPC={npcName}, Route={routeInfo}, Item={itemInfo}, State={__instance.currentState}, IsServer={InstanceFinder.IsServer}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("ActiveMinPass")]
      public static void MoveItemBehaviour_ActiveMinPass_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string routeInfo = __instance.assignedRoute != null
            ? $"Source={__instance.assignedRoute.Source?.Name ?? "null"}, Dest={__instance.assignedRoute.Destination?.Name ?? "null"}"
            : "null";
        string itemInfo = __instance.itemToRetrieveTemplate != null
            ? $"Item={__instance.itemToRetrieveTemplate.ID}, Qty={__instance.grabbedAmount}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.ActiveMinPass: NPC={npcName}, State={__instance.currentState}, Route={routeInfo}, Item={itemInfo}, IsMoving={__instance.Npc?.Movement.IsMoving}, IsServer={InstanceFinder.IsServer}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("WalkToSource")]
      public static void MoveItemBehaviour_WalkToSource_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string sourcePos = __instance.assignedRoute?.Source != null
            ? NavMeshUtility.GetAccessPoint(__instance.assignedRoute.Source, __instance.Npc)?.position.ToString() ?? "null"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.WalkToSource: NPC={npcName}, SourcePos={sourcePos}, State={__instance.currentState}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("WalkToDestination")]
      public static void MoveItemBehaviour_WalkToDestination_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string destPos = __instance.assignedRoute?.Destination != null
            ? NavMeshUtility.GetAccessPoint(__instance.assignedRoute.Destination, __instance.Npc)?.position.ToString() ?? "null"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.WalkToDestination: NPC={npcName}, DestPos={destPos}, State={__instance.currentState}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("Initialize", new[] { typeof(TransitRoute), typeof(ItemInstance), typeof(int), typeof(bool) })]
      public static void MoveItemBehaviour_Initialize_Prefix(MoveItemBehaviour __instance, TransitRoute route, ItemInstance _itemToRetrieveTemplate, int _maxMoveAmount, bool _skipPickup)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string routeInfo = route != null
            ? $"Source={route.Source?.Name ?? "null"}, Dest={route.Destination?.Name ?? "null"}"
            : "null";
        string itemInfo = _itemToRetrieveTemplate != null
            ? $"Item={_itemToRetrieveTemplate.ID}, Qty={_itemToRetrieveTemplate.Quantity}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.Initialize: NPC={npcName}, Route={routeInfo}, Item={itemInfo}, MaxMoveAmount={_maxMoveAmount}, SkipPickup={_skipPickup}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("End")]
      public static void MoveItemBehaviour_End_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string routeInfo = __instance.assignedRoute != null
            ? $"Source={__instance.assignedRoute.Source?.Name ?? "null"}, Dest={__instance.assignedRoute.Destination?.Name ?? "null"}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.End: NPC={npcName}, Route={routeInfo}, Active={__instance.Active}, State={__instance.currentState}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("EndTransit")]
      public static void MoveItemBehaviour_EndTransit_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string routeInfo = __instance.assignedRoute != null
            ? $"Source={__instance.assignedRoute.Source?.Name ?? "null"}, Dest={__instance.assignedRoute.Destination?.Name ?? "null"}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.EndTransit: NPC={npcName}, Route={routeInfo}, Initialized={__instance.Initialized}, GrabbedAmount={__instance.grabbedAmount}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("EndTransit")]
      public static void MoveItemBehaviour_EndTransit_Postfix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.EndTransit: Completed for NPC={npcName}, Initialized={__instance.Initialized}, AssignedRoute={__instance.assignedRoute == null}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("GetAmountToGrab")]
      public static void MoveItemBehaviour_GetAmountToGrab_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string itemInfo = __instance.itemToRetrieveTemplate != null
            ? $"Item={__instance.itemToRetrieveTemplate.ID}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GetAmountToGrab: NPC={npcName}, Item={itemInfo}, MaxMoveAmount={__instance.maxMoveAmount}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("GetAmountToGrab")]
      public static void MoveItemBehaviour_GetAmountToGrab_Postfix(MoveItemBehaviour __instance, int __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GetAmountToGrab: NPC={npcName}, Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("IsDestinationValid")]
      public static void MoveItemBehaviour_IsDestinationValid_Prefix(MoveItemBehaviour __instance, TransitRoute route, ItemInstance item)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string routeInfo = route != null
            ? $"Source={route.Source?.Name ?? "null"}, Dest={route.Destination?.Name ?? "null"}"
            : "null";
        string itemInfo = item != null
            ? $"Item={item.ID}, Qty={item.Quantity}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.IsDestinationValid: NPC={npcName}, Route={routeInfo}, Item={itemInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("IsDestinationValid")]
      public static void MoveItemBehaviour_IsDestinationValid_Postfix(MoveItemBehaviour __instance, bool __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.IsDestinationValid: NPC={npcName}, Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("CanGetToSource")]
      public static void MoveItemBehaviour_CanGetToSource_Prefix(MoveItemBehaviour __instance, TransitRoute route)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string sourceInfo = route?.Source != null ? $"Source={route.Source.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.CanGetToSource: NPC={npcName}, {sourceInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("CanGetToSource")]
      public static void MoveItemBehaviour_CanGetToSource_Postfix(MoveItemBehaviour __instance, bool __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.CanGetToSource: NPC={npcName}, Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("CanGetToDestination")]
      public static void MoveItemBehaviour_CanGetToDestination_Prefix(MoveItemBehaviour __instance, TransitRoute route)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string destInfo = route?.Destination != null ? $"Dest={route.Destination.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.CanGetToDestination: NPC={npcName}, {destInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("CanGetToDestination")]
      public static void MoveItemBehaviour_CanGetToDestination_Postfix(MoveItemBehaviour __instance, bool __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.CanGetToDestination: NPC={npcName}, Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("GetSourceAccessPoint")]
      public static void MoveItemBehaviour_GetSourceAccessPoint_Prefix(MoveItemBehaviour __instance, TransitRoute route)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string sourceInfo = route?.Source != null ? $"Source={route.Source.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GetSourceAccessPoint: NPC={npcName}, {sourceInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("GetSourceAccessPoint")]
      public static void MoveItemBehaviour_GetSourceAccessPoint_Postfix(MoveItemBehaviour __instance, Transform __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string resultInfo = __result != null ? $"Position={__result.position}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GetSourceAccessPoint: NPC={npcName}, Result={resultInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("GetDestinationAccessPoint")]
      public static void MoveItemBehaviour_GetDestinationAccessPoint_Prefix(MoveItemBehaviour __instance, TransitRoute route)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string destInfo = route?.Destination != null ? $"Dest={route.Destination.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GetDestinationAccessPoint: NPC={npcName}, {destInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("GetDestinationAccessPoint")]
      public static void MoveItemBehaviour_GetDestinationAccessPoint_Postfix(MoveItemBehaviour __instance, Transform __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string resultInfo = __result != null ? $"Position={__result.position}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GetDestinationAccessPoint: NPC={npcName}, Result={resultInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("IsAtSource")]
      public static void MoveItemBehaviour_IsAtSource_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string sourceInfo = __instance.assignedRoute?.Source != null ? $"Source={__instance.assignedRoute.Source.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.IsAtSource: NPC={npcName}, {sourceInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("IsAtSource")]
      public static void MoveItemBehaviour_IsAtSource_Postfix(MoveItemBehaviour __instance, bool __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.IsAtSource: NPC={npcName}, Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("IsAtDestination")]
      public static void MoveItemBehaviour_IsAtDestination_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string destInfo = __instance.assignedRoute?.Destination != null ? $"Dest={__instance.assignedRoute.Destination.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.IsAtDestination: NPC={npcName}, {destInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("IsAtDestination")]
      public static void MoveItemBehaviour_IsAtDestination_Postfix(MoveItemBehaviour __instance, bool __result)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.IsAtDestination: NPC={npcName}, Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("TakeItem")]
      public static void MoveItemBehaviour_TakeItem_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string itemInfo = __instance.itemToRetrieveTemplate != null
            ? $"Item={__instance.itemToRetrieveTemplate.ID}"
            : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.TakeItem: NPC={npcName}, Item={itemInfo}, GrabbedAmount={__instance.grabbedAmount}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("TakeItem")]
      public static void MoveItemBehaviour_TakeItem_Postfix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.TakeItem: Completed for NPC={npcName}, GrabbedAmount={__instance.grabbedAmount}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("GrabItem")]
      public static void MoveItemBehaviour_GrabItem_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string sourceInfo = __instance.assignedRoute?.Source != null ? $"Source={__instance.assignedRoute.Source.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.GrabItem: NPC={npcName}, {sourceInfo}, State={__instance.currentState}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPrefix]
      [HarmonyPatch("PlaceItem")]
      public static void MoveItemBehaviour_PlaceItem_Prefix(MoveItemBehaviour __instance)
      {
        string npcName = __instance.Npc?.fullName ?? "null";
        string destInfo = __instance.assignedRoute?.Destination != null ? $"Dest={__instance.assignedRoute.Destination.Name}" : "null";
        if (npcName == "Christopher Anderson" || npcName == "Karen Green")
          DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"MoveItemBehaviour.PlaceItem: NPC={npcName}, {destInfo}, State={__instance.currentState}, GrabbedAmount={__instance.grabbedAmount}",
            DebugLogger.Category.AllEmployees);
      }
    }

    [HarmonyPatch(typeof(TransitRoute))]
    public static class MovementDebugTransitRoute
    {
      [HarmonyPrefix]
      [HarmonyPatch("AreEntitiesNonNull")]
      public static void TransitRoute_AreEntitiesNonNull_Prefix(TransitRoute __instance)
      {
        string sourceInfo = __instance.Source != null ? $"Source={__instance.Source.Name}" : "Source=null";
        string destInfo = __instance.Destination != null ? $"Dest={__instance.Destination.Name}" : "Dest=null";
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TransitRoute.AreEntitiesNonNull: {sourceInfo}, {destInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("AreEntitiesNonNull")]
      public static void TransitRoute_AreEntitiesNonNull_Postfix(TransitRoute __instance, bool __result)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TransitRoute.AreEntitiesNonNull: Result={__result}",
            DebugLogger.Category.AllEmployees);
      }

      /* [HarmonyPrefix]
      [HarmonyPatch("ValidateEntities")]
      public static void TransitRoute_ValidateEntities_Prefix(TransitRoute __instance)
      {
        string sourceInfo = __instance.Source != null ? $"Source={__instance.Source.Name}, IsDestroyed={__instance.Source.IsDestroyed}" : "Source=null";
        string destInfo = __instance.Destination != null ? $"Dest={__instance.Destination.Name}, IsDestroyed={__instance.Destination.IsDestroyed}" : "Dest=null";
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TransitRoute.ValidateEntities: {sourceInfo}, {destInfo}",
            DebugLogger.Category.AllEmployees);
      }

      [HarmonyPostfix]
      [HarmonyPatch("ValidateEntities")]
      public static void TransitRoute_ValidateEntities_Postfix(TransitRoute __instance)
      {
        string sourceInfo = __instance.Source != null ? $"Source={__instance.Source.Name}" : "Source=null";
        string destInfo = __instance.Destination != null ? $"Dest={__instance.Destination.Name}" : "Dest=null";
        DebugLogger.Log(DebugLogger.LogLevel.Verbose,
            $"TransitRoute.ValidateEntities: Completed, {sourceInfo}, {destInfo}",
            DebugLogger.Category.AllEmployees);
      }
    }
  } */
}
