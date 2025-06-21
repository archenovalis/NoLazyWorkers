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
using UnityEngine.InputSystem.EnhancedTouch;
using NoLazyWorkers.Performance;
using static NoLazyWorkers.NoLazyUtilities;
using Unity.Collections;
using Unity.Burst;
using static NoLazyWorkers.Debug;

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
    public static bool Movement = false;
    public static bool Jobs = true;
    // employees
    public static bool AllEmployees = false;
    public static bool EmployeeCore = true;
    public static bool Chemist = false;
    public static bool Cleaner = false;
    public static bool Driver = false;
    public static bool Botanist = true;
    public static bool Handler = true;
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
    public static bool Tasks = true;
    public static bool Stacktrace = false;
    public static bool Performance = false;
  }

  public static class Debug
  {
    /// <summary>
    /// Processes deferred logs from Burst jobs.
    /// </summary>
    public static class Deferred
    {
      public static void ProcessLogs(NativeList<LogEntry> logs)
      {
        foreach (var log in logs)
          Log(log.Level, log.Message.ToString(), log.Category);
        logs.Dispose();
      }

      /// <summary>
      /// Represents a log entry for deferred logging in Burst jobs.
      /// </summary>
      [BurstCompile]
      public struct LogEntry
      {
        public FixedString128Bytes Message;
        public Level Level;
        public Category Category;
      }
    }

    public enum Level { None, Error, Warning, Info, Verbose, Stacktrace }
    public enum Category
    {
      None,
      Core,
      Settings,
      Movement,
      Jobs,
      AnyEmployee,
      EmployeeCore,
      Chemist,
      Cleaner,
      Driver,
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
      Tasks,
      Stacktrace,
      Performance
    }
    public static bool AnyEmployee;
    public static Level CurrentLevel { get; set; } = (Level)DebugLogs.Level;

    private static readonly Dictionary<Category, Func<bool>> CategoryEnabled = new()
    {
        { Category.Core, () => DebugLogs.Core },
        // services
        { Category.Tasks, () => DebugLogs.Tasks },
        { Category.Settings, () => DebugLogs.Settings },
        { Category.Movement, () => DebugLogs.Movement },
        { Category.Jobs, () => DebugLogs.Jobs },
        { Category.Storage, () => DebugLogs.Storage },
        // employees
        { Category.AnyEmployee, () => AnyEmployee },
        { Category.EmployeeCore, () => DebugLogs.EmployeeCore },
        { Category.Chemist, () => DebugLogs.Chemist },
        { Category.Cleaner, () => DebugLogs.Cleaner },
        { Category.Driver, () => DebugLogs.Driver },
        { Category.Botanist, () => DebugLogs.Botanist },
        { Category.Handler, () => DebugLogs.Handler },
        // stations
        { Category.Pot, () => DebugLogs.Pot },
        { Category.BrickPress, () => DebugLogs.BrickPress },
        { Category.Cauldron, () => DebugLogs.Cauldron },
        { Category.DryingRack, () => DebugLogs.DryingRack },
        { Category.LabOven, () => DebugLogs.LabOven },
        { Category.ChemistryStation, () => DebugLogs.ChemistryStation },
        { Category.General, () => DebugLogs.General },
        { Category.MixingStation, () => DebugLogs.MixingStation },
        { Category.PackagingStation, () => DebugLogs.PackagingStation },
        // other
        { Category.Performance, () => DebugLogs.Performance },
        { Category.Stacktrace, () => DebugLogs.Stacktrace },
        { Category.None, () => true } // Always enabled if All is true
    };

    private static readonly Dictionary<Category, bool> CachedCategoryEnabled = new();
    public static void Log(Level level, string message, params Category[] categories)
    {
      if (!DebugLogs.Enabled || level > CurrentLevel)
        return;

      AnyEmployee = DebugLogs.Chemist || DebugLogs.Botanist || DebugLogs.Handler || DebugLogs.Cleaner || DebugLogs.Driver;
      if (!DebugLogs.All && !categories.Any(c => c != Category.Stacktrace &&
          CachedCategoryEnabled.GetOrAdd(c, _ => CategoryEnabled[c]())))
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
        case Level.Error:
          MelonLogger.Error(fullMessage);
          break;
        case Level.Warning:
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
        Log(Level.Stacktrace, $"Failed to initialize NoLazyWorkers_Alternative: {e}", Category.Core);
      }

      Instance = this;
      string configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "NoLazyWorkers.cfg");
      if (File.Exists(configPath))
      {
        Config = Settings.Default.LoadFromFile(configPath);
        Log(Level.Info, "Config loaded.", Category.Core);
      }
      else
      {
        Config = new Settings.Default();
        Config.SaveToFile(configPath);
        Log(Level.Info, "Default config created.", Category.Core);
      }

      MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneWasLoaded);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
      if (sceneName == "Main")
      {
        var configure = new Settings.Configure();
        _ = CoroutineRunner.Instance;
        CoroutineRunner.Instance.RunCoroutine(configure.ApplyOneShotSettingsRoutine());
        Log(Level.Info, "Applied Fixer and Misc settings on main scene load.", Category.Core);
        MixingStationConfigUtilities.InitializeStaticRouteListTemplate();
        ShelfUtilities.InitializeStorageModule();
        //TaskServiceManager.Initialize();
        Performance.Metrics.Initialize();
        FishNetExtensions.IsServer = InstanceFinder.IsServer;
        FishNetExtensions.TimeManagerInstance = InstanceFinder.TimeManager;
      }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
      ClearPrefabs();
      Employees.Utilities.Clear();
      NoLazyWorkersExtensions.NPCSupply.Clear();
      Settings.SettingsExtensions.Configured.Clear();
      TaskServiceManager.Clear();
      Log(Level.Info, "Cleared ConfigurationExtensions and SettingsExtensions on scene unload.", Category.Core);
    }
  }

  public static class UnityEventExtensions
  {
    public static int GetListenerCount(this UnityEventBase unityEvent)
    {
      return unityEvent.GetPersistentEventCount() + unityEvent.m_Calls.Count;
    }
  }

  public static class DictionaryExtensions
  {
    /// <summary>
    /// Gets the value associated with the specified key, or adds a new value using the provided factory if the key doesn't exist.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    /// <param name="dictionary">The dictionary to operate on.</param>
    /// <param name="key">The key to look up or add.</param>
    /// <param name="valueFactory">The function to create a new value if the key is not found.</param>
    /// <returns>The existing or newly added value.</returns>
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> valueFactory)
    {
      if (dictionary == null)
        throw new ArgumentNullException(nameof(dictionary));
      if (key == null)
        throw new ArgumentNullException(nameof(key));
      if (valueFactory == null)
        throw new ArgumentNullException(nameof(valueFactory));

      if (dictionary.TryGetValue(key, out var value))
        return value;

      value = valueFactory(key);
      dictionary[key] = value;
      return value;
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
      Log(Level.Info, $"{new string(' ', depth * 2)}{transform.name}", Category.MixingStation);
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
            Log(Level.Error, $"Unsupported EConfigurableType: {configType}", Category.Core);
            UnityEngine.Object.Destroy(dummyEntity);
            return null;
        }

        if (configPanelPrefab == null)
        {
          Log(Level.Error, $"No ConfigPanel prefab found for {configType}", Category.Core);
          return null;
        }

        GameObject tempPanelObj = UnityEngine.Object.Instantiate(configPanelPrefab);
        tempPanelObj.SetActive(false);
        ConfigPanel tempPanel = tempPanelObj.GetComponent<ConfigPanel>();
        if (tempPanel == null)
        {
          Log(Level.Error, $"Instantiated prefab for {configType} lacks ConfigPanel component", Category.Core);
          UnityEngine.Object.Destroy(tempPanelObj);
          return null;
        }

        // Bind to initialize UI components (mimic game behavior)
        List<EntityConfiguration> configs = [];
        configs.Add(config);
        tempPanel.Bind(configs);
        Log(Level.Verbose, $"Bound temporary ConfigPanel for {configType} to initialize UI components", Category.Core);

        // Get the UI template
        var uiTemplate = tempPanel.transform.Find(componentStr);
        if (uiTemplate == null)
          Log(Level.Error, $"Failed to retrieve UI template from ConfigPanel for {configType}", Category.Core);
        else
          Log(Level.Info, $"Successfully retrieved UI template from ConfigPanel for {configType}", Category.Core);

        // Clean up
        UnityEngine.Object.Destroy(tempPanelObj);
        UnityEngine.Object.Destroy(dummyEntity);
        return uiTemplate;
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"Failed to get UI template from ConfigPanel for {configType}: {e}", Category.Core);
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
            Log(Level.Verbose, $"Found prefab: {obj.name}", Category.Core);
            prefab = obj;
            break;
          }
        }

        if (prefab != null)
        {
          GameObject instance = UnityEngine.Object.Instantiate(prefab);
          Log(Level.Info, $"Instantiated prefab: {instance.name}", Category.Core);
          CachedPrefabs[id] = prefab;
          return instance;
        }
        else
        {
          Log(Level.Error, $"Prefab {id} not found in Resources.", Category.Core);
          return null;
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"Failed to find prefab: {e}", Category.Core);
        return null;
      }
    }

    public static void LogItemFieldUIDetails(ItemFieldUI itemfieldUI)
    {
      Log(Level.Info, "=== ItemFieldUI Details ===", Category.Core);

      // Log basic info
      Log(Level.Info, $"ItemFieldUI GameObject: {(itemfieldUI.gameObject != null ? itemfieldUI.gameObject.name : "null")}", Category.Core);
      Log(Level.Info, $"ItemFieldUI Active: {itemfieldUI.gameObject?.activeSelf}", Category.Core);
      Log(Level.Info, $"ItemFieldUI Type: {itemfieldUI.GetType().Name}", Category.Core);

      // Log ItemFieldUI properties
      LogComponentDetails(itemfieldUI, 0);
      Log(Level.Info, "--- Hierarchy and Components ---", Category.Core);

      // Log hierarchy and components
      if (itemfieldUI.gameObject != null)
      {
        LogGameObjectDetails(itemfieldUI.gameObject, 0);
      }
      else
      {
        Log(Level.Warning, "ItemFieldUI GameObject is null, cannot log hierarchy and components", Category.Core);
      }
    }

    static void LogGameObjectDetails(GameObject go, int indentLevel)
    {
      if (go == null)
      {
        Log(Level.Warning, new string(' ', indentLevel * 2) + "GameObject: null", Category.Core);
        return;
      }

      Log(Level.Verbose, new string(' ', indentLevel * 2) + $"GameObject: {go.name}, Active: {go.activeSelf}", Category.Core);

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
        Log(Level.Warning, new string(' ', indentLevel * 2) + "Component: null", Category.Core);
        return;
      }

      Log(Level.Verbose, new string(' ', indentLevel * 2) + $"Component: {component.GetType().Name}", Category.Core);

      // Use reflection to log all public fields
      var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
      foreach (var field in fields)
      {
        try
        {
          var value = field.GetValue(component);
          string valueStr = ValueToString(value);
          Log(Level.Verbose, new string(' ', indentLevel * 2) + $"  Field: {field.Name} = {valueStr}", Category.Core);
        }
        catch (Exception e)
        {
          Log(Level.Stacktrace, new string(' ', indentLevel * 2) + $"  Failed to get field {field.Name}: {e.Message}", Category.Core);
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
          Log(Level.Verbose, new string(' ', indentLevel * 2) + $"  Property: {property.Name} = {valueStr}", Category.Core);
        }
        catch (Exception e)
        {
          Log(Level.Stacktrace, new string(' ', indentLevel * 2) + $"  Failed to get property {property.Name}: {e.Message}", Category.Core);
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
          Log(Level.Info, $"ItemSetterScreenOpenPatch: Opening for ItemField, SelectedItem: {itemField.SelectedItem?.Name ?? "null"}", Category.Core);
          if (itemField.Options != null)
          {
            Log(Level.Info, $"ItemSetterScreenOpenPatch: ItemField options count: {itemField.Options.Count}", Category.Core);
          }
          else
          {
            Log(Level.Warning, "ItemSetterScreenOpenPatch: ItemField Options is null", Category.Core);
          }
        }
        else
        {
          Log(Level.Info, $"ItemSetterScreenOpenPatch: Opening for {option?.GetType().Name ?? "null"}", Category.Core);
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"ItemSetterScreenOpenPatch: Prefix failed, error: {e}", Category.Core);
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
        Log(Level.Error, $"Field {fieldName} not found on {obj.GetType().Name}", Category.Core);
        return null;
      }
      return field.GetValue(obj) as T;
    }

    public static void SetField(object obj, string fieldName, object value)
    {
      var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
      if (field == null)
      {
        Log(Level.Error, $"Field {fieldName} not found on {obj.GetType().Name}", Category.Core);
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
          Log(Level.Info, "onLoadComplete fired, restoring configurations", Category.Core);
          PotExtensions.RestoreConfigurations();
          MixingStationConfigUtilities.RestoreConfigurations();
          //StorageExtensions.RestoreConfigurations();
        });
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"LoadManagerPatch.Awake failed: {e}", Category.Core);
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
        Log(Level.Info, $"GridItemLoaderPatch: Processing LoadAndCreate for mainPath: {mainPath}", Category.Core);
        if (__result != null)
        {
          LoadedGridItems[mainPath] = __result;
          Log(Level.Info, $"GridItemLoaderPatch: Captured GridItem (type: {__result.GetType().Name}) for mainPath: {mainPath}", Category.Core);
        }
        else
        {
          Log(Level.Warning, $"GridItemLoaderPatch: No GridItem returned for mainPath: {mainPath}", Category.Core);
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"GridItemLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}", Category.Core);
      }
    }
  }

  [HarmonyPatch(typeof(ConfigurationReplicator), "RpcLogic___ReceiveItemField_2801973956")]
  public class ConfigurationReplicatorReceiveItemFieldPatch
  {
    static bool Prefix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      Log(Level.Verbose,
          $"ConfigurationReplicatorReceiveItemFieldPatch: Received update for fieldIndex={fieldIndex}, value={value ?? "null"}",
          Category.Core, Category.AnyEmployee);
      Log(Level.Verbose,
          $"ConfigurationReplicatorReceiveItemFieldPatch: Fields count={__instance.Configuration.Fields.Count}",
          Category.Core, Category.AnyEmployee);
      for (int i = 0; i < __instance.Configuration.Fields.Count; i++)
      {
        Log(Level.Verbose,
            $"ConfigurationReplicatorReceiveItemFieldPatch: Fields[{i}]={__instance.Configuration.Fields[i]?.GetType().Name ?? "null"}",
            Category.Core, Category.AnyEmployee);
      }

      if (fieldIndex < 0 || fieldIndex >= __instance.Configuration.Fields.Count)
      {
        Log(Level.Warning,
            $"ConfigurationReplicatorReceiveItemFieldPatch: Invalid fieldIndex={fieldIndex}, Configuration.Fields.Count={__instance.Configuration.Fields.Count}, skipping",
            Category.Core, Category.AnyEmployee);
        return false;
      }

      var itemField = __instance.Configuration.Fields[fieldIndex] as ItemField;
      if (itemField == null)
      {
        Log(Level.Warning,
            $"ConfigurationReplicatorReceiveItemFieldPatch: No ItemField at fieldIndex={fieldIndex}, Fields[{fieldIndex}]={__instance.Configuration.Fields[fieldIndex]?.GetType().Name ?? "null"}, skipping",
            Category.Core, Category.AnyEmployee);
        return false;
      }

      if (string.IsNullOrEmpty(value) && !itemField.CanSelectNone)
      {
        Log(Level.Warning,
            $"ConfigurationReplicatorReceiveItemFieldPatch: Blocked null update for ItemField with CanSelectNone={itemField.CanSelectNone}, CurrentItem={itemField.SelectedItem?.Name ?? "null"}",
            Category.Core, Category.AnyEmployee);
        return false;
      }

      Log(Level.Verbose,
          $"ConfigurationReplicatorReceiveItemFieldPatch: Allowing update for ItemField, CanSelectNone={itemField.CanSelectNone}, value={value}",
          Category.Core, Category.AnyEmployee);
      return true;
    }

    static void Postfix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      try
      {
        Log(Level.Verbose,
            $"ConfigurationReplicatorReceiveObjectFieldPatch: Received update for fieldIndex={fieldIndex}, value={value}",
            Category.Core, Category.AnyEmployee);

        if (__instance.Configuration is PotConfiguration potConfig && fieldIndex == 6)
        {
          if (PotExtensions.Supply.TryGetValue(potConfig.Pot.GUID, out ObjectField supply))
          {
            Log(Level.Info,
                $"ConfigurationReplicatorReceiveObjectFieldPatch: Updated supply for pot: {potConfig.Pot.GUID}, SelectedObject: unknown because value is a string",
                Category.Core, Category.Botanist, Category.Pot);
          }
          else
          {
            Log(Level.Warning,
                $"ConfigurationReplicatorReceiveObjectFieldPatch: No supply found for pot: {potConfig.Pot.GUID}",
                Category.Core, Category.Botanist, Category.Pot);
          }
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"ConfigurationReplicatorReceiveObjectFieldPatch: Failed for fieldIndex={fieldIndex}, error: {e}", Category.Core, Category.AnyEmployee);
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
      Log(Level.Verbose,
          $"ItemFieldSetItemPatch: Called for ItemField, network={network}, CanSelectNone={__instance.CanSelectNone}, Item={item?.Name ?? "null"}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}",
          Category.Core, Category.AnyEmployee);

      // Anti-bounce: Block redundant updates within 0.2s
      if (!network && RecentUpdates.TryGetValue(__instance, out var recentUpdate) &&
          Time.time - recentUpdate.Time < AntiBounceWindow)
      {
        Log(Level.Warning,
            $"ItemFieldSetItemPatch: Blocked redundant update for ItemField, Item={item?.Name ?? "null"}, Network={network}, TimeSinceLast={Time.time - recentUpdate.Time:F3}s\nCallStack: {callStack}",
            Category.Core, Category.AnyEmployee);
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
            $"MoveItemBehaviour.StartTransit: NPC={npcName}, Route={routeInfo}, Item={itemInfo}, State={__instance.currentState}, IsServer={FishNetExtensions.IsServer}",
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
            $"MoveItemBehaviour.ActiveMinPass: NPC={npcName}, State={__instance.currentState}, Route={routeInfo}, Item={itemInfo}, IsMoving={__instance.Npc?.Movement.IsMoving}, IsServer={FishNetExtensions.IsServer}",
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