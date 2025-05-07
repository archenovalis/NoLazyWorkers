using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne.EntityFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NoLazyWorkers.Botanists
{
  public static class PotExtensions
  {
    public static Dictionary<Guid, ObjectField> Supply = [];
    public static Dictionary<Guid, PotConfiguration> Config = [];
    public static Dictionary<Guid, TransitRoute> SupplyRoute = [];
    public static Dictionary<Guid, ObjectFieldData> FailedSupply = [];

    public static void SourceChanged(this PotConfiguration potConfig, BuildableItem item)
    {
      try
      {
        if (potConfig == null)
        {
          MelonLogger.Error("SourceChanged(Pot): PotConfiguration is null");
          return;
        }
        Pot pot = potConfig.Pot;
        if (pot == null)
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning("SourceChanged(pot): pot is null");
          return;
        }
        Guid guid = pot.GUID;
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"SourceChanged(Pot): Called for PotConfig: {guid.ToString() ?? "null"}, Item: {item?.name ?? "null"}");
        if (!Supply.ContainsKey(guid))
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"SourceChanged(Pot): PotSupply does not contain key for pot: {pot}");
          return;
        }
        if (!SupplyRoute.ContainsKey(guid))
        {
          SupplyRoute[guid] = null; // initialize
        }

        if (SupplyRoute[guid] is TransitRoute supplyRoute)
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"SourceChanged(pot): Destroying existing TransitRoute for pot: {guid}");
          supplyRoute.Destroy();
          SupplyRoute[guid] = null;
        }

        ObjectField supply = Supply[guid];
        ITransitEntity entity = null;
        if ((supply.SelectedObject != null && supply.SelectedObject is ITransitEntity) || item is ITransitEntity)
        {
          entity = supply.SelectedObject as ITransitEntity ?? item as ITransitEntity;
          supplyRoute = new TransitRoute(entity, pot);
          SupplyRoute[guid] = supplyRoute;
          if (pot.Configuration.IsSelected)
          {
            supplyRoute.SetVisualsActive(true);
          }
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"SourceChanged(Pot): Created new TransitRoute for pot: {guid}, Supply: {item.name}");
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"SourceChanged(Pot): Item is null, no TransitRoute created for pot: {guid}");
          SupplyRoute[guid] = null;
        }
        if (DebugLogs.All || DebugLogs.Pot) MelonLogger.Msg($"SourceChanged(pot): Updated for MixerConfig: {potConfig}, Supply: {supply?.SelectedObject?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}");
      }
    }

    public static void SupplyOnObjectChangedInvoke(BuildableItem item, PotConfiguration __instance, Pot pot, ObjectField Supply)
    {
      ConfigurationExtensions.InvokeChanged(__instance);
      SourceChanged(__instance, item);
      if (DebugLogs.All || DebugLogs.Pot)
        MelonLogger.Msg($"SupplyOnObjectChangedInvoke: Supply changed for pot {pot?.GUID.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.GUID.ToString() ?? "null"}");
    }

    public static void RestoreConfigurations()
    {
      Pot[] pots = Object.FindObjectsOfType<Pot>();
      foreach (Pot pot in pots)
      {
        try
        {
          if (pot.Configuration is PotConfiguration potConfig)
          {
            Guid guid = pot.GUID;
            if (DebugLogs.All || DebugLogs.Pot)
              MelonLogger.Warning($"RestoreConfigurations: Started for pot: {guid}");

            Config[guid] = potConfig;
            if (DebugLogs.All || DebugLogs.Pot)
              MelonLogger.Warning($"RestoreConfigurations: Registered missing PotConfig for pot: {pot.name}");

            // Initialize Supply if missing
            if (!Supply.TryGetValue(guid, out var supply))
            {
              supply = new ObjectField(potConfig)
              {
                TypeRequirements = [],
                DrawTransitLine = true
              };
              Supply[guid] = supply;
              Supply[guid].onObjectChanged.RemoveAllListeners();
              Supply[guid].onObjectChanged.AddListener(item => SupplyOnObjectChangedInvoke(item, potConfig, pot, supply));
              if (DebugLogs.All || DebugLogs.MixingStation)
                MelonLogger.Msg($"RestoreConfigurations: Initialized Supply for pot: {guid}");
            }
          }
        }
        catch (Exception e)
        {
          MelonLogger.Error($"RestoreConfigurations: Failed to restore configuration for pot: {pot?.name ?? "null"}, error: {e}");
        }
      }

      // Reload failed Supply entries
      try
      {
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"RestoreConfigurations: Reloading {FailedSupply.Count} failed Supply entries");

        // Use ToList to avoid modifying collection during iteration
        foreach (var entry in FailedSupply.ToList())
        {
          Guid guid = entry.Key;
          ObjectFieldData supplyData = entry.Value;

          try
          {
            if (!Supply.TryGetValue(guid, out ObjectField supply) || supply == null)
            {
              if (DebugLogs.All || DebugLogs.Pot)
                MelonLogger.Warning($"RestoreConfigurations: Skipping reload for pot: {guid.ToString() ?? "null"}, supply not found");
              FailedSupply.Remove(guid);
              continue;
            }

            if (DebugLogs.All || DebugLogs.Pot)
              MelonLogger.Msg($"RestoreConfigurations: Reloading Supply for pot: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}");

            supply.Load(supplyData);

            if (supply.SelectedObject == null)
            {
              if (DebugLogs.All || DebugLogs.Pot)
                MelonLogger.Warning($"RestoreConfigurations: Reload failed to set SelectedObject for pot: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}");
            }
            else
            {
              if (DebugLogs.All || DebugLogs.Pot)
                MelonLogger.Msg($"RestoreConfigurations: Reload succeeded, SelectedObject: {supply.SelectedObject.name} for pot: {guid}");
              if (Config.TryGetValue(guid, out var config))
              {
                SourceChanged(config, supply.SelectedObject);
              }
            }
            FailedSupply.Remove(guid);
          }
          catch (Exception e)
          {
            MelonLogger.Error($"RestoreConfigurations: Failed to reload Supply for pot: {guid.ToString() ?? "null"}, error: {e}");
            FailedSupply.Remove(guid);
          }
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"RestoreConfigurations: Failed to process FailedSupply entries, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfiguration))]
  public class PotConfigurationPatch
  {
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(Pot) })]
    static void ConstructorPostfix(PotConfiguration __instance, Pot pot)
    {
      try
      {
        Guid guid = pot.GUID;
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"PotConfigurationPatch: Initializing for pot: {pot?.name ?? "null"}");
        ObjectField supply = new(__instance)
        {
          TypeRequirements = [],
          DrawTransitLine = true
        };
        supply.onObjectChanged.RemoveAllListeners();
        supply.onObjectChanged.AddListener(item => PotExtensions.SupplyOnObjectChangedInvoke(item, __instance, pot, supply));

        if (!PotExtensions.Supply.ContainsKey(guid))
        {
          PotExtensions.Supply[guid] = supply;
        }
        if (!PotExtensions.Config.ContainsKey(guid))
        {
          PotExtensions.Config[guid] = __instance;
        }
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"PotConfigurationPatch: Registered supply and config for pot: {pot?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationPatch: Failed for pot: {pot?.name ?? "null"}, error: {e}");
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("GetSaveString")]
    static void GetSaveStringPostfix(PotConfiguration __instance, ref string __result)
    {
      try
      {
        if (__instance == null || __instance.Pot == null)
          return;
        Guid guid = __instance.Pot.GUID;
        if (PotExtensions.Supply.TryGetValue(guid, out var supply) && supply != null && supply.SelectedObject != null)
        {
          // Create the config data
          PotConfigurationData data = new(
            __instance.Seed.GetData(),
            __instance.Additive1.GetData(),
            __instance.Additive2.GetData(),
            __instance.Additive3.GetData(),
            __instance.Destination.GetData()
          );
          // Serialize config data to JSON
          string configJson = JsonUtility.ToJson(data, true);
          JObject jsonObject = JObject.Parse(configJson);

          JObject supplyObject = null;
          supplyObject = new JObject
          {
            ["ObjectGUID"] = supply.SelectedObject.GUID.ToString()
          };
          jsonObject["Supply"] = supplyObject;
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"PotConfiguration GetSaveStringPatch: supplyData: {supplyObject["ObjectGUID"]}");
          __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"PotConfiguration GetSaveStringPatch: Saved JSON for station={guid}: {__result}");
        }
        else if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Warning($"PotConfiguration GetSaveStringPatch: No Supply for pot {guid}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfiguration GetSaveStringPatch failed: {e}");
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Destroy")]
    static void DestroyPostfix(PotConfiguration __instance)
    {
      try
      {
        // Only remove if the station GameObject is actually being destroyed
        if (__instance.Pot == null || __instance.Pot.gameObject == null)
        {
          Guid guid = __instance.Pot.GUID;
          PotExtensions.Supply.Remove(guid);
          PotExtensions.SupplyRoute.Remove(guid);
          foreach (var pair in PotExtensions.Config.Where(p => p.Value == __instance).ToList())
          {
            PotExtensions.Config.Remove(pair.Key);
          }
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"PotConfigurationDestroyPatch: Cleaned up for pot: {guid.ToString() ?? "null"}");
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"PotConfigurationDestroyPatch: Skipped removal for pot {__instance.Pot?.GUID}, station still exists");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfigPanel), "Bind")]
  public class PotConfigPanelBindPatch
  {
    static void Postfix(PotConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {

        // Destination UI update
        foreach (TextMeshProUGUI child in __instance.DestinationUI.GetComponentsInChildren<TextMeshProUGUI>())
          if (child.gameObject.name == "Description") { child.gameObject.SetActive(false); break; }

        // Instantiate UI objects
        GameObject supplyUIObj = Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
          if (child.gameObject.name == "Title") { child.text = "Supplies"; break; }
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        supplyUI.InstructionText = "Select supply";
        supplyUI.ExtendedInstructionText = "Select supply for mixer item input";

        // bind all selected pots
        List<ObjectField> supplyList = [];
        foreach (var config in configs.OfType<PotConfiguration>())
        {
          Guid guid = config.Pot.GUID;
          if (PotExtensions.Supply.TryGetValue(guid, out ObjectField supply) && (DebugLogs.All || DebugLogs.Pot))
            MelonLogger.Msg($"MixingStationConfigPanelBindPatch: Before Bind, station: {guid}, SelectedObject: {supply.SelectedObject?.name ?? "null"}");
          else if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"MixingStationConfigPanelBindPatch: No supply found for MixingStationConfiguration, station: {guid}");
          PotExtensions.Supply[guid] = supply ?? new(config);
          supplyList.Add(PotExtensions.Supply[guid]);
        }

        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}");
        supplyUI.Bind(supplyList);

        // Position adjustments
        RectTransform destRect = __instance.DestinationUI.GetComponent<RectTransform>();
        destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, -340f);
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigPanelBindPatch: Failed, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotLoader), "Load")]
  public class PotLoaderPatch
  {
    private static void SupplySourceChanged(PotConfiguration config, Pot pot, BuildableItem item)
    {
      PotExtensions.SourceChanged(config, item);
      ConfigurationExtensions.InvokeChanged(config);
      if (DebugLogs.All || DebugLogs.Pot)
        MelonLogger.Msg($"PotLoaderPatch: Supply changed for pot {pot.GUID}, newSupply={PotExtensions.Supply[pot.GUID].SelectedObject?.GUID.ToString() ?? "null"}");
    }

    static void Postfix(string mainPath)
    {
      try
      {
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"PotLoaderPatch: Processing Postfix for mainPath: {mainPath}");
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"PotLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem is not Pot pot)
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"PotLoaderPatch: GridItem is not a Pot for mainPath: {mainPath}, type: {gridItem.GetType().Name}");
          return;
        }
        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"PotLoaderPatch: No Configuration.json found at: {configPath}");
          return;
        }
        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"PotLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
          return;
        }
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"PotLoaderPatch: Loaded JSON: {text}");

        // Parse JSON using Newtonsoft.Json
        JObject jsonObject = JObject.Parse(text);
        JToken supplyJToken = jsonObject["Supply"];

        if (DebugLogs.All || DebugLogs.Pot)
        {
          MelonLogger.Msg($"PotLoaderPatch: Extracted supplyJToken: {supplyJToken}");
        }

        Guid guid = pot.GUID;
        PotConfiguration config = pot.Configuration as PotConfiguration;
        if (config == null)
        {
          MelonLogger.Error($"PotLoaderPatch: No valid PotConfiguration for pot: {guid}");
          return;
        }
        if (!PotExtensions.Config.ContainsKey(guid))
        {
          PotExtensions.Config[guid] = config;
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"PotLoaderPatch: Registered PotConfig for pot: {guid}");
        }
        if (supplyJToken != null && supplyJToken["ObjectGUID"] != null)
        {
          if (!PotExtensions.Supply.ContainsKey(guid))
          {
            PotExtensions.Supply[guid] = new ObjectField(config)
            {
              TypeRequirements = [],
              DrawTransitLine = true
            };
            PotExtensions.Supply[guid].onObjectChanged.RemoveAllListeners();
            PotExtensions.Supply[guid].onObjectChanged.AddListener(item => SupplySourceChanged(config, pot, item));
          }
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Msg($"PotLoaderPatch: supplyJToken[ObjectGUID].ToString(): {supplyJToken["ObjectGUID"].ToString()}");
          PotExtensions.Supply[guid].Load(new(supplyJToken["ObjectGUID"].ToString()));
          if (PotExtensions.Supply[guid].SelectedObject != null)
          {
            if (DebugLogs.All || DebugLogs.Pot)
              MelonLogger.Msg($"PotLoaderPatch: Loaded Supply for pot: {guid}");
            PotExtensions.SourceChanged(config, PotExtensions.Supply[guid].SelectedObject);
          }
          else
          {
            if (DebugLogs.All || DebugLogs.Pot)
              MelonLogger.Warning($"PotLoaderPatch: Supply.SelectedObject is null for pot: {guid}");
            PotExtensions.FailedSupply[guid] = new(supplyJToken["ObjectGUID"].ToString());
          }
        }
        else
        {
          if (DebugLogs.All || DebugLogs.Pot)
            MelonLogger.Warning($"PotLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
        }
        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        if (DebugLogs.All || DebugLogs.Pot)
          MelonLogger.Msg($"PotLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }
}