using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Reflection;

//using NoLazyWorkers.Handlers;
using NoLazyWorkers.Chemists;
using NoLazyWorkers.Botanists;
using NoLazyWorkers.General;
using static NoLazyWorkers.General.GeneralExtensions;
using FluffyUnderware.DevTools.Extensions;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkersMod), "NoLazyWorkers", "1.1.9", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers
{
  public static class DebugLogs
  {
    public static bool Enabled = true;
    public static int Level = 4;
    public static bool All = false; // enables all but stacktrace logs
    public static bool Core = true;
    public static bool Settings = false;
    // employees
    public static bool Chemist = false;
    public static bool Botanist = false;
    public static bool Handler = false;
    // generic
    public static bool Storage = true;
    public static bool General = false;
    // stations
    public static bool Pot = false;
    public static bool LabOven = false;
    public static bool ChemistryStation = false;
    public static bool MixingStation = true;
    public static bool PackagingStation = true;
    //
    public static bool Stacktrace = false;
  }

  public static class DebugLogger
  {
    public enum LogLevel { None, Error, Warning, Info, Verbose }
    public enum Category
    {
      None,
      Core,
      Settings,
      Chemist,
      Botanist,
      Handler,
      Storage,
      Pot,
      LabOven,
      ChemistryStation,
      General,
      MixingStation,
      PackagingStation,
      Stacktrace
    }

    public static LogLevel CurrentLevel { get; set; } = (LogLevel)DebugLogs.Level;

    private static readonly Dictionary<Category, Func<bool>> CategoryEnabled = new()
    {
        { Category.Core, () => DebugLogs.Core },
        { Category.Settings, () => DebugLogs.Settings },
        { Category.Chemist, () => DebugLogs.Chemist },
        { Category.Botanist, () => DebugLogs.Botanist },
        { Category.Handler, () => DebugLogs.Handler },
        { Category.Storage, () => DebugLogs.Storage },
        { Category.Pot, () => DebugLogs.Pot },
        { Category.LabOven, () => DebugLogs.LabOven },
        { Category.ChemistryStation, () => DebugLogs.ChemistryStation },
        { Category.General, () => DebugLogs.General },
        { Category.MixingStation, () => DebugLogs.MixingStation },
        { Category.PackagingStation, () => DebugLogs.PackagingStation },
        { Category.Stacktrace, () => DebugLogs.Stacktrace },
        { Category.None, () => true } // Always enabled if All is true
    };

    public static void Log(LogLevel level, string message, params Category[] categories)
    {
      if (!DebugLogs.Enabled || level > CurrentLevel)
        return;

      // Determine if any category is enabled
      bool isEnabled = DebugLogs.All || categories.Any(c => c != Category.Stacktrace && CategoryEnabled[c]());
      if (!isEnabled)
        return;

      // Get the first non-Stacktrace category for labeling (or "None" if only Stacktrace)
      Category labelCategory = categories.FirstOrDefault(c => c != Category.Stacktrace);
      string prefix = $"[{labelCategory}]";
      bool includeStacktrace = DebugLogs.Stacktrace || categories.Contains(Category.Stacktrace);

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
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"Failed to initialize NoLazyWorkers_Alternative: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
        MixingStationExtensions.InitializeStaticRouteListTemplate();
        StorageExtensions.InitializeStorageModule();
      }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
      ConfigurationExtensions.NPCSupply.Clear();
      Settings.SettingsExtensions.Configured.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, "Cleared ConfigurationExtensions and SettingsExtensions on scene unload.", DebugLogger.Category.Core);
    }
  }

  public static class ConfigurationExtensions
  {
    public static Dictionary<Guid, ObjectField> NPCSupply = [];
    private static readonly Dictionary<EntityConfiguration, float> lastInvokeTimes = [];
    private static readonly float debounceTime = 0.2f;

    public static void InvokeChanged(EntityConfiguration config)
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
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"InvokeChanged failed: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
      }
    }
  }

  public static class NoLazyUtilities
  {
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
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"CoroutineRunner: Exception in coroutine: {e.Message}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"CoroutineRunner: Exception in coroutine: {e.Message}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"Failed to get UI template from ConfigPanel for {configType}: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"Failed to find prefab: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, new string(' ', indentLevel * 2) + $"  Failed to get field {field.Name}: {e.Message}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, new string(' ', indentLevel * 2) + $"  Failed to get property {property.Name}: {e.Message}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"ItemSetterScreenOpenPatch: Prefix failed, error: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
          MixingStationExtensions.RestoreConfigurations();
          //StorageExtensions.RestoreConfigurations();
        });
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"LoadManagerPatch.Awake failed: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"GridItemLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}", DebugLogger.Category.Core, DebugLogger.Category.Stacktrace);
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
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"ConfigurationReplicatorReceiveObjectFieldPatch: Failed for fieldIndex={fieldIndex}, error: {e}",
            DebugLogger.Category.Core, DebugLogger.Category.Chemist, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
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
}