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
using NoLazyWorkers.Handlers;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkersMod), "NoLazyWorkers", "1.1.9", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers
{
  public static class DebugLogs
  {
    public static int level = 4;
    public static bool Production = false;
    public static bool All = false; // enables all but stacktrace logs
    public static bool Core = false;
    public static bool Settings = false;
    public static bool Pot = false;
    public static bool MixingStation = true;
    public static bool Storage = false;
    public static bool Chemist = true;
    public static bool Botanist = false;
    public static bool Packager = false;
    public static bool Stacktrace = false;
  }

  public static class DebugLogger
  {
    public enum LogLevel { None, Error, Warning, Info, Verbose }
    public static LogLevel CurrentLevel { get; set; } = (LogLevel)DebugLogs.level;

    public static void Log(LogLevel level, string message, bool isChemist = false, bool isStation = false, bool isStorage = false)
    {
      if (DebugLogs.Production || level > CurrentLevel || (!DebugLogs.All && !isChemist && !isStation && !isStorage))
        return;
      switch (level)
      {
        case LogLevel.Error: MelonLogger.Error(message); break;
        case LogLevel.Warning: MelonLogger.Warning(message); break;
        default: MelonLogger.Msg(message); break;
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
        MelonLogger.Error($"Failed to initialize NoLazyWorkers_Alternative: {e}");
      }

      Instance = this;
      string configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "NoLazyWorkers.cfg");

      if (File.Exists(configPath))
      {
        Config = Settings.Default.LoadFromFile(configPath);
        if (DebugLogs.All || DebugLogs.Core)
          MelonLogger.Msg("Config loaded.");
      }
      else
      {
        Config = new Settings.Default();
        Config.SaveToFile(configPath);
        if (DebugLogs.All || DebugLogs.Core)
          MelonLogger.Msg("Default config created.");
      }

      // Register scene load callback
      MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneWasLoaded);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
      if (sceneName == "Main")
      {
        var configure = new Settings.Configure();
        MelonCoroutines.Start(configure.ApplyOneShotSettingsRoutine());
        if (DebugLogs.All || DebugLogs.Core)
          MelonLogger.Msg("Applied Fixer and Misc settings on main scene load.");

        MixingStationExtensions.InitializeStaticRouteListTemplate();
        StorageExtensions.InitializeStorageModule();
      }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
      ConfigurationExtensions.NPCSupply.Clear();
      Settings.SettingsExtensions.Configured.Clear();
      if (DebugLogs.All || DebugLogs.Core)
        MelonLogger.Msg("Cleared ConfigurationExtensions and SettingsExtensions on scene unload.");
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
          MelonLogger.Error("InvokeChanged: EntityConfiguration is null");
          return;
        }
        float currentTime = Time.time;
        if (lastInvokeTimes.TryGetValue(config, out float lastTime) && currentTime - lastTime < debounceTime)
        {
          if (DebugLogs.All || DebugLogs.Core)
            MelonLogger.Msg($"InvokeChanged debounced for config: {config}");
          return;
        }
        lastInvokeTimes[config] = currentTime;
        if (DebugLogs.Stacktrace)
          MelonLogger.Msg($"InvokeChanged called for config: {config}, StackTrace: {new System.Diagnostics.StackTrace()}");
        config.InvokeChanged();
      }
      catch (Exception e)
      {
        MelonLogger.Error($"InvokeChanged failed: {e}");
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
            MelonLogger.Error($"CoroutineRunner: Exception in coroutine: {e.Message}, stack: {e.StackTrace}");
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
            MelonLogger.Error($"Unsupported EConfigurableType: {configType}");
            UnityEngine.Object.Destroy(dummyEntity);
            return null;
        }
        if (configPanelPrefab == null)
        {
          MelonLogger.Error($"No ConfigPanel prefab found for {configType}");
          return null;
        }

        // Instantiate prefab temporarily
        GameObject tempPanelObj = UnityEngine.Object.Instantiate(configPanelPrefab);
        tempPanelObj.SetActive(false); // Keep inactive to avoid rendering
        ConfigPanel tempPanel = tempPanelObj.GetComponent<ConfigPanel>();
        if (tempPanel == null)
        {
          MelonLogger.Error($"Instantiated prefab for {configType} lacks ConfigPanel component");
          UnityEngine.Object.Destroy(tempPanelObj);
          return null;
        }

        // Bind to initialize UI components (mimic game behavior)
        List<EntityConfiguration> configs = [];

        configs.Add(config);
        tempPanel.Bind(configs);
        if (DebugLogs.All || DebugLogs.Core)
          MelonLogger.Msg($"Bound temporary ConfigPanel for {configType} to initialize UI components");

        // Get the UI template
        var uiTemplate = tempPanel.transform.Find(componentStr);
        if (uiTemplate == null)
          MelonLogger.Error($"Failed to retrieve UI template from ConfigPanel for {configType}");
        else if (DebugLogs.All || DebugLogs.Core)
          MelonLogger.Msg($"Successfully retrieved UI template from ConfigPanel for {configType}");


        // Clean up
        UnityEngine.Object.Destroy(tempPanelObj);
        UnityEngine.Object.Destroy(dummyEntity);
        return uiTemplate;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"Failed to get UI template from ConfigPanel for {configType}: {e}");
        return null;
      }
    }

    public static GameObject GetPrefabGameObject(string id)
    {
      try
      {
        GameObject prefab = null;
        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (GameObject obj in allObjects)
        {
          if (obj.name.Contains(id))
          {
            if (DebugLogs.All || DebugLogs.Core)
              MelonLogger.Msg($"Found prefab: {obj.name}");
            prefab = obj;
            break;
          }
        }
        if (prefab != null)
        {
          GameObject instance = UnityEngine.Object.Instantiate(prefab);
          if (DebugLogs.All || DebugLogs.Core)
            MelonLogger.Msg($"Instantiated prefab: {instance.name}");
          return instance;
        }
        else
        {
          MelonLogger.Error($"Prefab {id} not found in Resources.");
          return null;
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"Failed to find prefab: {e}");
        return null;
      }
    }

    public static void LogItemFieldUIDetails(ItemFieldUI itemfieldUI)
    {
      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg("=== ItemFieldUI Details ==="); }

      // Log basic info
      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"ItemFieldUI GameObject: {(itemfieldUI.gameObject != null ? itemfieldUI.gameObject.name : "null")}"); }
      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"ItemFieldUI Active: {itemfieldUI.gameObject?.activeSelf}"); }
      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"ItemFieldUI Type: {itemfieldUI.GetType().Name}"); }

      // Log ItemFieldUI properties
      LogComponentDetails(itemfieldUI, 0);

      // Log hierarchy and components
      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg("--- Hierarchy and Components ---"); }
      if (itemfieldUI.gameObject != null)
      {
        LogGameObjectDetails(itemfieldUI.gameObject, 0);
      }
      else
      {
        if (DebugLogs.All || DebugLogs.Core)
          MelonLogger.Warning("ItemFieldUI GameObject is null, cannot log hierarchy and components");
      }
    }

    static void LogGameObjectDetails(GameObject go, int indentLevel)
    {
      if (go == null)
      {
        if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg(new string(' ', indentLevel * 2) + "GameObject: null"); }
        return;
      }

      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"GameObject: {go.name}, Active: {go.activeSelf}"); }

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
        if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg(new string(' ', indentLevel * 2) + "Component: null"); }
        return;
      }

      if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"Component: {component.GetType().Name}"); }

      // Use reflection to log all public fields
      var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
      foreach (var field in fields)
      {
        try
        {
          var value = field.GetValue(component);
          string valueStr = ValueToString(value);
          if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"  Field: {field.Name} = {valueStr}"); }
        }
        catch (Exception e)
        {
          MelonLogger.Error(new string(' ', indentLevel * 2) + $"  Failed to get field {field.Name}: {e.Message}");
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
          if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"  Property: {property.Name} = {valueStr}"); }
        }
        catch (Exception e)
        {
          MelonLogger.Error(new string(' ', indentLevel * 2) + $"  Failed to get property {property.Name}: {e.Message}");
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
          if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: Opening for ItemField, SelectedItem: {itemField.SelectedItem?.Name ?? "null"}"); }
          if (itemField.Options != null)
          {
            if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: ItemField options count: {itemField.Options.Count}"); }
          }
          else
          {
            if (DebugLogs.All || DebugLogs.Core)
              MelonLogger.Warning("ItemSetterScreenOpenPatch: ItemField Options is null");
          }
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: Opening for {option?.GetType().Name ?? "null"}"); }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ItemSetterScreenOpenPatch: Prefix failed, error: {e}");
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
        Debug.LogError($"Field {fieldName} not found on {obj.GetType().Name}");
        return null;
      }
      return field.GetValue(obj) as T;
    }

    public static void SetField(object obj, string fieldName, object value)
    {
      var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
      if (field == null)
      {
        Debug.LogError($"Field {fieldName} not found on {obj.GetType().Name}");
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
          if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg("onLoadComplete fired, restoring configurations"); }
          PotExtensions.RestoreConfigurations();
          MixingStationExtensions.RestoreConfigurations();
          //StorageExtensions.RestoreConfigurations();
        });
      }
      catch (Exception e)
      {
        MelonLogger.Error($"LoadManagerPatch.Awake failed: {e}");
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
        if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"GridItemLoaderPatch: Processing LoadAndCreate for mainPath: {mainPath}"); }
        if (__result != null)
        {
          LoadedGridItems[mainPath] = __result;
          if (DebugLogs.All || DebugLogs.Core) { MelonLogger.Msg($"GridItemLoaderPatch: Captured GridItem (type: {__result.GetType().Name}) for mainPath: {mainPath}"); }
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Core)
            MelonLogger.Warning($"GridItemLoaderPatch: No GridItem returned for mainPath: {mainPath}");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"GridItemLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(ConfigurationReplicator), "RpcLogic___ReceiveItemField_2801973956")]
  public class ConfigurationReplicatorReceiveItemFieldPatch
  {
    static bool Prefix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
      {
        MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Received update for fieldIndex={fieldIndex}, value={value ?? "null"}");
        MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Fields count={__instance.Configuration.Fields.Count}");
        for (int i = 0; i < __instance.Configuration.Fields.Count; i++)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Fields[{i}]={__instance.Configuration.Fields[i]?.GetType().Name ?? "null"}");
      }
      if (fieldIndex < 0 || fieldIndex >= __instance.Configuration.Fields.Count)
      {
        if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Invalid fieldIndex={fieldIndex}, Configuration.Fields.Count={__instance.Configuration.Fields.Count}, skipping");
        return false;
      }
      var itemField = __instance.Configuration.Fields[fieldIndex] as ItemField;
      if (itemField == null)
      {
        if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: No ItemField at fieldIndex={fieldIndex}, Fields[{fieldIndex}]={__instance.Configuration.Fields[fieldIndex]?.GetType().Name ?? "null"}, skipping");
        return false;
      }
      if (string.IsNullOrEmpty(value) && !itemField.CanSelectNone)
      {
        if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Blocked null update for ItemField with CanSelectNone={itemField.CanSelectNone}, CurrentItem={itemField.SelectedItem?.Name ?? "null"}");
        return false;
      }
      if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
        MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Allowing update for ItemField, CanSelectNone={itemField.CanSelectNone}, value={value}");
      return true;
    }
    static void Postfix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      try
      {
        if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveObjectFieldPatch: Received update for fieldIndex={fieldIndex}, value={value}");
        if (__instance.Configuration is PotConfiguration potConfig && fieldIndex == 6) // Supply is Fields[6]
        {
          if (PotExtensions.Supply.TryGetValue(potConfig.Pot.GUID, out ObjectField supply))
          {
            if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
              MelonLogger.Msg($"ConfigurationReplicatorReceiveObjectFieldPatch: Updated supply for pot: {potConfig.Pot.GUID}, SelectedObject: unknown because value is a string");
          }
          else
          {
            if (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist)
              MelonLogger.Warning($"ConfigurationReplicatorReceiveObjectFieldPatch: No supply found for pot: {potConfig.Pot.GUID}");
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ConfigurationReplicatorReceiveObjectFieldPatch: Failed for fieldIndex={fieldIndex}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(ItemField), "SetItem", [typeof(ItemDefinition), typeof(bool)])]
  public class ItemFieldSetItemPatch
  {
    static bool Prefix(ItemField __instance, ItemDefinition item, bool network)
    {
      if (DebugLogs.Stacktrace && (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist))
        MelonLogger.Msg($"ItemFieldSetItemPatch: Called for ItemField, network={network}, CanSelectNone={__instance.CanSelectNone}, Item={item?.Name ?? "null"}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}, StackTrace: {new System.Diagnostics.StackTrace().ToString()}");

      // Check if this is the Product field (assume Product has CanSelectNone=false or is paired with Mixer)
      bool isProductField = __instance.Options != null && __instance.Options.Any(o => ProductManager.FavouritedProducts.Contains(o));
      if ((item == null && __instance.CanSelectNone) || isProductField)
      {
        if (DebugLogs.Stacktrace && (DebugLogs.All || DebugLogs.Core || DebugLogs.Chemist || DebugLogs.Botanist))
          MelonLogger.Msg($"ItemFieldSetItemPatch: Blocked null update for Product field, CanSelectNone={__instance.CanSelectNone}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}, StackTrace: {new System.Diagnostics.StackTrace().ToString()}");
        /* return false; */
      }
      return true;
    }
  }
}