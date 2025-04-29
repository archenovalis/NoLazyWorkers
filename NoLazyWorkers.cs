using HarmonyLib;
using MelonLoader;
using Il2CppScheduleOne.Employees;
using MelonLoader.Utils;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.Management.SetterScreens;
using Il2CppScheduleOne.Management.UI;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Persistence.Loaders;
using Il2CppScheduleOne.Product;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.UI.Management;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

using NoLazyWorkers_IL2CPP.Chemists;
using NoLazyWorkers_IL2CPP.Botanists;

[assembly: MelonInfo(typeof(NoLazyWorkers_IL2CPP.NoLazyWorkersMod), "NoLazyWorkers_IL2CPP", "1.1.5", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers_IL2CPP
{
  public static class DebugConfig
  {
    public static bool EnableDebugLogs = false; // enables all but stacktrace logs
    public static bool EnableDebugCore = false;
    public static bool EnableDebugSettings = false;
    public static bool EnableDebugPot = false;
    public static bool EnableDebugMixingStation = false;
    public static bool EnableDebugChemistBehavior = false;
    public static bool EnableDebugBotanistBehavior = false;
    public static bool EnableDebugPackagerBehavior = false;
    public static bool EnableDebugStacktrace = false;
  }

  public static class BuildInfo
  {
    public const string Name = "NoLazyWorkers_IL2CPP";
    public const string Description = "Supply is added to mixing stations. Chemists get items from their station's supply. Mixing Stations can have multiple recipes that loop the output. Added Employee-related configurable settings.";
    public const string Author = "Archie";
    public const string Version = "1.1.5";
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
        MelonLogger.Msg("NoLazyWorkers_Standard loaded!");
        MelonLogger.Warning("=====================================================");
        MelonLogger.Warning("       Please ignore the initial yellow errors ");
        MelonLogger.Warning("=====================================================");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"Failed to initialize NoLazyWorkers_Standard: {e}");
      }

      Instance = this;
      string configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "NoLazyWorkers.cfg");

      if (File.Exists(configPath))
      {
        Config = Settings.Default.LoadFromFile(configPath);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg("Config loaded.");
      }
      else
      {
        Config = new Settings.Default();
        Config.SaveToFile(configPath);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg("Started one-shot settings coroutine on main scene load.");

        MixingStationExtensions.InitializeStaticRouteListTemplate();
      }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
      ConfigurationExtensions.NPCSupply.Clear();
      Settings.SettingsExtensions.Configured.Clear();
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
        MelonLogger.Msg("Cleared ConfigurationExtensions and SettingsExtensions on scene unload.");
    }
  }

  public static class ConfigurationExtensions
  {
    public static Dictionary<Il2CppSystem.Guid, ObjectField> NPCSupply = [];

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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
            MelonLogger.Msg($"InvokeChanged debounced for config: {config}");
          return;
        }
        lastInvokeTimes[config] = currentTime;
        if (DebugConfig.EnableDebugStacktrace)
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
    /// <summary>
    /// Converts a System.Collections.Generic.List<T> to an Il2CppSystem.Collections.Generic.List<T>.
    /// </summary>
    /// <typeparam name="T">The type of elements in the list, must inherit from Il2CppSystem.Object.</typeparam>
    /// <param name="systemList">The System list to convert.</param>
    /// <returns>An Il2CppSystem list containing the same elements, or an empty list if the input is null.</returns>
    public static Il2CppSystem.Collections.Generic.List<T> ConvertList<T>(List<T> systemList)
        where T : Il2CppSystem.Object
    {
      if (systemList == null)
        return new Il2CppSystem.Collections.Generic.List<T>();

      Il2CppSystem.Collections.Generic.List<T> il2cppList = new(systemList.Count);
      foreach (var item in systemList)
      {
        if (item != null)
          il2cppList.Add(item);
      }
      return il2cppList;
    }

    /// <summary>
    /// Converts an Il2CppSystem.Collections.Generic.List<T> to a System.Collections.Generic.List<T>.
    /// </summary>
    /// <typeparam name="T">The type of elements in the list, must inherit from Il2CppSystem.Object.</typeparam>
    /// <param name="il2cppList">The Il2CppSystem list to convert.</param>
    /// <returns>A System list containing the same elements, or an empty list if the input is null.</returns>
    public static List<T> ConvertList<T>(Il2CppSystem.Collections.Generic.List<T> il2cppList)
        where T : Il2CppSystem.Object
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

    public static int GetAmountInInventoryAndSupply(NPC npc, ItemDefinition item)
    {
      if (npc == null || item == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Warning($"GetAmountInInventoryAndSupply: NPC or item is null");
        return 0;
      }

      int inventoryCount = npc.Inventory?._GetItemAmount(item.ID) ?? 0;
      int supplyCount = GetAmountInSupply(npc, item.GetDefaultInstance());
      return inventoryCount + supplyCount;
    }

    public static int GetAmountInSupply(NPC npc, ItemInstance item)
    {
      if (npc == null || item == null || string.IsNullOrEmpty(item.ID))
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Warning($"GetAmountInSupplies: NPC={npc}, item={item}, or item.ID={item?.ID} is null for {npc?.fullName ?? "null"}");
        return 0;
      }

      if (!ConfigurationExtensions.NPCSupply.TryGetValue(npc.GUID, out var supply) || supply?.SelectedObject == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Warning($"GetAmountInSupplies: Supply or SelectedObject is null for {npc.fullName}");
        return 0;
      }

      if (supply.SelectedObject.TryCast<ITransitEntity>() is not ITransitEntity supplyT)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Warning($"GetAmountInSupplies: Supply is not ITransitEntity for {npc.fullName}");
        return 0;
      }

      if (!npc.Movement.CanGetTo(supplyT, 1f))
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Warning($"GetAmountInSupplies: Cannot reach supply for {npc.fullName}");
        return 0;
      }

      var slots = new Il2CppSystem.Collections.Generic.List<ItemSlot>();
      if (supplyT.OutputSlots != null)
      {
        for (int i = 0; i < supplyT.OutputSlots.Count; i++)
        {
          var slot = supplyT.OutputSlots[i];
          if (slot != null && slot.ItemInstance != null && slot.Quantity > 0)
          {
            if (!slots.Contains(slot)) // Manual Distinct
              slots.Add(slot);
          }
        }
      }
      if (supplyT.InputSlots != null)
      {
        for (int i = 0; i < supplyT.InputSlots.Count; i++)
        {
          var slot = supplyT.InputSlots[i];
          if (slot != null && slot.ItemInstance != null && slot.Quantity > 0)
          {
            if (!slots.Contains(slot)) // Manual Distinct
              slots.Add(slot);
          }
        }
      }

      if (slots.Count == 0)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Warning($"GetAmountInSupplies: No valid slots in supply {supplyT?.Name} for {npc.fullName}");
        return 0;
      }

      int quantity = 0;
      string itemIdLower = item.ID.ToLower();
      foreach (ItemSlot slot in slots)
      {
        if (slot.ItemInstance.ID.ToLower() == itemIdLower)
        {
          quantity += slot.Quantity;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
            MelonLogger.Msg($"GetAmountInSupplies: Found {itemIdLower} with quantity={slot.Quantity} in slot {slot.GetHashCode()} for {npc.fullName}");
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
            MelonLogger.Msg($"GetAmountInSupplies: Slot {slot.GetHashCode()} contains {slot.ItemInstance.ID} (quantity={slot.Quantity}), not {itemIdLower} for {npc.fullName}");
        }
      }

      if (quantity == 0)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Msg($"GetAmountInSupplies: No items of {item.ID} found in supply {supplyT?.Name} for {npc.fullName}");
      }
      else
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore || DebugConfig.EnableDebugChemistBehavior || DebugConfig.EnableDebugBotanistBehavior)
          MelonLogger.Msg($"GetAmountInSupplies: Total quantity of {item.ID} is {quantity} in supply {supplyT?.Name} for {npc.fullName}");
      }
      return quantity;
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
            config = new PotConfiguration(replicator, pot.TryCast<IConfigurable>(), pot);
            configPanelPrefab = GetPrefabGameObject("PotConfigPanel");
            break;
          case EConfigurableType.Packager:
            Packager packager = dummyEntity.AddComponent<Packager>();
            config = new PackagerConfiguration(replicator, packager.TryCast<IConfigurable>(), packager);
            configPanelPrefab = GetPrefabGameObject("PackagerConfigPanel");
            break;
          case EConfigurableType.MixingStation:
            MixingStation mixingStation = dummyEntity.AddComponent<MixingStation>();
            config = new MixingStationConfiguration(replicator, mixingStation.TryCast<IConfigurable>(), mixingStation);
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
        tempPanel.Bind(ConvertList(configs));
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg($"Bound temporary ConfigPanel for {configType} to initialize UI components");

        // Get the UI template
        var uiTemplate = tempPanel.transform.Find(componentStr);
        if (uiTemplate == null)
          MelonLogger.Error($"Failed to retrieve UI template from ConfigPanel for {configType}");
        else if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
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
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
              MelonLogger.Msg($"Found prefab: {obj.name}");
            prefab = obj;
            break;
          }
        }
        if (prefab != null)
        {
          GameObject instance = UnityEngine.Object.Instantiate(prefab);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
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
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg("=== ItemFieldUI Details ==="); }

      // Log basic info
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"ItemFieldUI GameObject: {(itemfieldUI.gameObject != null ? itemfieldUI.gameObject.name : "null")}"); }
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"ItemFieldUI Active: {itemfieldUI.gameObject?.activeSelf}"); }
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"ItemFieldUI Type: {itemfieldUI.GetType().Name}"); }

      // Log ItemFieldUI properties
      LogComponentDetails(itemfieldUI, 0);

      // Log hierarchy and components
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg("--- Hierarchy and Components ---"); }
      if (itemfieldUI.gameObject != null)
      {
        LogGameObjectDetails(itemfieldUI.gameObject, 0);
      }
      else
      {
        MelonLogger.Warning("ItemFieldUI GameObject is null, cannot log hierarchy and components");
      }
    }

    static void LogGameObjectDetails(GameObject go, int indentLevel)
    {
      if (go == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg(new string(' ', indentLevel * 2) + "GameObject: null"); }
        return;
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"GameObject: {go.name}, Active: {go.activeSelf}"); }

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

    static void LogComponentDetails(Component component, int indentLevel)
    {
      if (component == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg(new string(' ', indentLevel * 2) + "Component: null"); }
        return;
      }

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"Component: {component.GetType().Name}"); }

      // Use reflection to log all public fields
      var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
      foreach (var field in fields)
      {
        try
        {
          var value = field.GetValue(component);
          string valueStr = ValueToString(value);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"  Field: {field.Name} = {valueStr}"); }
        }
        catch (Exception e)
        {
          MelonLogger.Warning(new string(' ', indentLevel * 2) + $"  Failed to get field {field.Name}: {e.Message}");
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"  Property: {property.Name} = {valueStr}"); }
        }
        catch (Exception e)
        {
          MelonLogger.Warning(new string(' ', indentLevel * 2) + $"  Failed to get property {property.Name}: {e.Message}");
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
        var items = new Il2CppSystem.Collections.Generic.List<string>();
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
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: Opening for ItemField, SelectedItem: {itemField.SelectedItem?.Name ?? "null"}"); }
          if (itemField.Options != null)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: ItemField options count: {itemField.Options.Count}"); }
          }
          else
          {
            MelonLogger.Warning("ItemSetterScreenOpenPatch: ItemField Options is null");
          }
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: Opening for {option?.GetType().Name ?? "null"}"); }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ItemSetterScreenOpenPatch: Prefix failed, error: {e}");
      }
    }
  }

  // todo: are select and deselect necessary?
  [HarmonyPatch(typeof(EntityConfiguration), "Selected")]
  public class EntityConfigurationSelectedPatch
  {
    static void Postfix(EntityConfiguration __instance)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior || DebugConfig.EnableDebugChemistBehavior) { MelonLogger.Msg($"EntityConfigurationSelectedPatch: {__instance.GetType()?.Name} selected"); }
        if (__instance is PotConfiguration potConfig && PotExtensions.SupplyRoute.TryGetValue(potConfig.Pot.GUID, out var potRoute))
        {
          if (potRoute != null)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior || DebugConfig.EnableDebugChemistBehavior) { MelonLogger.Msg("EntityConfigurationSelectedPatch: Enabling visuals for Pot SourceRoute"); }
            potRoute.SetVisualsActive(active: true);
          }
          else
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior || DebugConfig.EnableDebugChemistBehavior) MelonLogger.Warning("EntityConfigurationSelectedPatch: Pot SourceRoute is null");
          }
        }
        else if (__instance is MixingStationConfiguration mixerConfig && MixingStationExtensions.SupplyRoute.TryGetValue(mixerConfig.station.GUID, out var mixerRoute))
        {
          if (mixerRoute != null)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior || DebugConfig.EnableDebugChemistBehavior) { MelonLogger.Msg("EntityConfigurationSelectedPatch: Enabling visuals for MixingStation SourceRoute"); }
            mixerRoute.SetVisualsActive(active: true);
          }
          else
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBotanistBehavior || DebugConfig.EnableDebugChemistBehavior) MelonLogger.Warning("EntityConfigurationSelectedPatch: MixingStation SourceRoute is null");
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"EntityConfigurationSelectedPatch: Failed for {__instance?.GetType().Name}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(EntityConfiguration), "Deselected")]
  public class EntityConfigurationDeselectedPatch
  {
    static void Postfix(EntityConfiguration __instance)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"EntityConfigurationDeselectedPatch: {__instance.GetType().Name} deselected"); }
        if (__instance is PotConfiguration potConfig && PotExtensions.SupplyRoute.TryGetValue(potConfig.Pot.GUID, out TransitRoute potRoute))
        {
          if (potRoute != null)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg("EntityConfigurationDeselectedPatch: Disabling visuals for Pot SourceRoute"); }
            potRoute.SetVisualsActive(active: false);
          }
          else
          {
            MelonLogger.Warning("EntityConfigurationDeselectedPatch: Pot SourceRoute is null");
          }
        }
        else if (__instance is MixingStationConfiguration mixerConfig && MixingStationExtensions.SupplyRoute.TryGetValue(mixerConfig.station.GUID, out TransitRoute mixerRoute))
        {
          if (mixerRoute != null)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg("EntityConfigurationDeselectedPatch: Disabling visuals for MixingStation SourceRoute"); }
            mixerRoute.SetVisualsActive(active: false);
          }
          else
          {
            MelonLogger.Warning("EntityConfigurationDeselectedPatch: MixingStation SourceRoute is null");
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"EntityConfigurationDeselectedPatch: Failed for {__instance?.GetType().Name}, error: {e}");
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
        button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() => sourceButton.onClick.Invoke()));
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
        __instance.onLoadComplete.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(() =>
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg("onLoadComplete fired, restoring configurations"); }
          PotExtensions.RestoreConfigurations();
          MixingStationExtensions.RestoreConfigurations();
          //StorageExtensions.RestoreConfigurations();
        }));
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"GridItemLoaderPatch: Processing LoadAndCreate for mainPath: {mainPath}"); }
        if (__result != null)
        {
          LoadedGridItems[mainPath] = __result;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore) { MelonLogger.Msg($"GridItemLoaderPatch: Captured GridItem (type: {__result.GetType().Name}) for mainPath: {mainPath}"); }
        }
        else
        {
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
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
      {
        MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Received update for fieldIndex={fieldIndex}, value={value ?? "null"}");
        MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Fields count={__instance.Configuration.Fields.Count}");
        for (int i = 0; i < __instance.Configuration.Fields.Count; i++)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Fields[{i}]={__instance.Configuration.Fields[i]?.GetType().Name ?? "null"}");
      }
      if (fieldIndex < 0 || fieldIndex >= __instance.Configuration.Fields.Count)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Invalid fieldIndex={fieldIndex}, Configuration.Fields.Count={__instance.Configuration.Fields.Count}, skipping");
        return false;
      }
      var itemField = __instance.Configuration.Fields[fieldIndex] as ItemField;
      if (itemField == null)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: No ItemField at fieldIndex={fieldIndex}, Fields[{fieldIndex}]={__instance.Configuration.Fields[fieldIndex]?.GetType().Name ?? "null"}, skipping");
        return false;
      }
      if (string.IsNullOrEmpty(value) && !itemField.CanSelectNone)
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Blocked null update for ItemField with CanSelectNone={itemField.CanSelectNone}, CurrentItem={itemField.SelectedItem?.Name ?? "null"}");
        return false;
      }
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
        MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Allowing update for ItemField, CanSelectNone={itemField.CanSelectNone}, value={value}");
      return true;
    }
    static void Postfix(ConfigurationReplicator __instance, int fieldIndex, string value)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
          MelonLogger.Msg($"ConfigurationReplicatorReceiveObjectFieldPatch: Received update for fieldIndex={fieldIndex}, value={value}");
        if (__instance.Configuration is PotConfiguration potConfig && fieldIndex == 6) // Supply is Fields[6]
        {
          if (PotExtensions.Supply.TryGetValue(potConfig.Pot.GUID, out ObjectField supply))
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCore)
              MelonLogger.Msg($"ConfigurationReplicatorReceiveObjectFieldPatch: Updated supply for pot: {potConfig.Pot.GUID}, SelectedObject: unknown because value is a string");
          }
          else
          {
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
      if (DebugConfig.EnableDebugStacktrace)
        MelonLogger.Msg($"ItemFieldSetItemPatch: Called for ItemField, network={network}, CanSelectNone={__instance.CanSelectNone}, Item={item?.Name ?? "null"}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}, StackTrace: {new System.Diagnostics.StackTrace().ToString()}");

      // Check if this is the Product field (assume Product has CanSelectNone=false or is paired with Mixer)
      bool isProductField = __instance.Options != null && NoLazyUtilities.ConvertList(__instance.Options).Any(o => NoLazyUtilities.ConvertList(ProductManager.FavouritedProducts).Contains(o));
      if ((item == null && __instance.CanSelectNone) || isProductField)
      {
        if (DebugConfig.EnableDebugStacktrace)
          MelonLogger.Msg($"ItemFieldSetItemPatch: Blocked null update for Product field, CanSelectNone={__instance.CanSelectNone}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}, StackTrace: {new System.Diagnostics.StackTrace().ToString()}");
        /* return false; */
      }
      return true;
    }
  }
}