using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using Pathfinding.Poly2Tri;
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
          DebugLogger.Log(DebugLogger.LogLevel.Error, "SourceChanged(Pot): PotConfiguration is null", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        Pot pot = potConfig.Pot;
        if (pot == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, "SourceChanged(pot): pot is null", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        Guid guid = pot.GUID;
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SourceChanged(Pot): Called for PotConfig: {guid.ToString() ?? "null"}, Item: {item?.name ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);

        if (!Supply.ContainsKey(guid))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"SourceChanged(Pot): PotSupply does not contain key for pot: {pot}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        if (!SupplyRoute.ContainsKey(guid))
        {
          SupplyRoute[guid] = null;
        }

        if (SupplyRoute[guid] is TransitRoute supplyRoute)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"SourceChanged(pot): Destroying existing TransitRoute for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
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
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"SourceChanged(Pot): Created new TransitRoute for pot: {guid}, Supply: {item.name}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"SourceChanged(Pot): Item is null, no TransitRoute created for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          SupplyRoute[guid] = null;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SourceChanged(pot): Updated for MixerConfig: {potConfig}, Supply: {supply?.SelectedObject?.name ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
      }
    }

    public static void SupplyOnObjectChangedInvoke(BuildableItem item, PotConfiguration __instance, Pot pot, ObjectField Supply)
    {
      __instance.InvokeChanged();
      SourceChanged(__instance, item);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"SupplyOnObjectChangedInvoke: Supply changed for pot {pot?.GUID.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.GUID.ToString() ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
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
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RestoreConfigurations: Started for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
            Config[guid] = potConfig;
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestoreConfigurations: Registered missing PotConfig for pot: {pot.name}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);

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
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestoreConfigurations: Initialized Supply for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
            }
          }
        }
        catch (Exception e)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestoreConfigurations: Failed to restore configuration for pot: {pot?.name ?? "null"}, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
        }
      }

      // Reload failed Supply entries
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestoreConfigurations: Reloading {FailedSupply.Count} failed Supply entries", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);

        // Use ToList to avoid modifying collection during iteration
        foreach (var entry in FailedSupply.ToList())
        {
          Guid guid = entry.Key;
          ObjectFieldData supplyData = entry.Value;
          try
          {
            if (!Supply.TryGetValue(guid, out ObjectField supply) || supply == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestoreConfigurations: Skipping reload for pot: {guid.ToString() ?? "null"}, supply not found", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
              FailedSupply.Remove(guid);
              continue;
            }

            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"RestoreConfigurations: Reloading Supply for pot: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
            supply.Load(supplyData);
            if (supply.SelectedObject == null)
            {
              DebugLogger.Log(DebugLogger.LogLevel.Warning, $"RestoreConfigurations: Reload failed to set SelectedObject for pot: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
            }
            else
            {
              DebugLogger.Log(DebugLogger.LogLevel.Info, $"RestoreConfigurations: Reload succeeded, SelectedObject: {supply.SelectedObject.name} for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
              if (Config.TryGetValue(guid, out var config))
              {
                SourceChanged(config, supply.SelectedObject);
              }
            }
            FailedSupply.Remove(guid);
          }
          catch (Exception e)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestoreConfigurations: Failed to reload Supply for pot: {guid.ToString() ?? "null"}, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
            FailedSupply.Remove(guid);
          }
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"RestoreConfigurations: Failed to process FailedSupply entries, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
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
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotConfigurationPatch: Initializing for pot: {pot?.name ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
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
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotConfigurationPatch: Registered supply and config for pot: {pot?.name ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PotConfigurationPatch: Failed for pot: {pot?.name ?? "null"}, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
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
          JObject supplyObject = new JObject
          {
            ["ObjectGUID"] = supply.SelectedObject.GUID.ToString()
          };
          jsonObject["Supply"] = supplyObject;
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotConfiguration GetSaveStringPatch: supplyData: {supplyObject["ObjectGUID"]}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotConfiguration GetSaveStringPatch: Saved JSON for station={guid}: {__result}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotConfiguration GetSaveStringPatch: No Supply for pot {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PotConfiguration GetSaveStringPatch failed: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
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
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotConfigurationDestroyPatch: Cleaned up for pot: {guid.ToString() ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotConfigurationDestroyPatch: Skipped removal for pot {__instance.Pot?.GUID}, station still exists", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        }
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PotConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
      }
    }
  }

  [HarmonyPatch(typeof(PotConfigPanel), "Bind")]
  public class PotConfigPanelBindPatch
  {
    static void BindPostfix(PotConfigPanel __instance, List<EntityConfiguration> configs)
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
          if (PotExtensions.Supply.TryGetValue(guid, out ObjectField supply))
          {
            DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotConfigPanelBindPatch: Before Bind, station: {guid}, SelectedObject: {supply.SelectedObject?.name ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotConfigPanelBindPatch: No supply found for MixingStationConfiguration, station: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          }
          PotExtensions.Supply[guid] = supply ?? new(config);
          supplyList.Add(PotExtensions.Supply[guid]);
        }

        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        supplyUI.Bind(supplyList);

        RectTransform destRect = __instance.DestinationUI.GetComponent<RectTransform>();
        destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, -340f);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PotConfigPanelBindPatch: Failed, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
      }
    }
  }

  [HarmonyPatch(typeof(PotLoader), "Load")]
  public class PotLoaderPatch
  {
    private static void SupplySourceChanged(PotConfiguration config, Pot pot, BuildableItem item)
    {
      PotExtensions.SourceChanged(config, item);
      config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotLoaderPatch: Supply changed for pot {pot.GUID}, newSupply={PotExtensions.Supply[pot.GUID].SelectedObject?.GUID.ToString() ?? "null"}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
    }

    static void Postfix(string mainPath)
    {
      try
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotLoaderPatch: Processing Postfix for mainPath: {mainPath}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotLoaderPatch: No GridItem found for mainPath: {mainPath}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        if (gridItem is not Pot pot)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotLoaderPatch: GridItem is not a Pot for mainPath: {mainPath}, type: {gridItem.GetType().Name}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotLoaderPatch: No Configuration.json found at: {configPath}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotLoaderPatch: Loaded JSON: {text}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        JObject jsonObject = JObject.Parse(text);
        JToken supplyJToken = jsonObject["Supply"];
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotLoaderPatch: Extracted supplyJToken: {supplyJToken}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);

        Guid guid = pot.GUID;
        PotConfiguration config = pot.Configuration as PotConfiguration;
        if (config == null)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Error, $"PotLoaderPatch: No valid PotConfiguration for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          return;
        }

        /* // Clear hidden destination
        if (config.Destination.SelectedObject != null)
        {
          config.Destination.SelectedObject = null;
          config.DestinationRoute = null;
        } */

        if (!PotExtensions.Config.ContainsKey(guid))
        {
          PotExtensions.Config[guid] = config;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotLoaderPatch: Registered PotConfig for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
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

          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"PotLoaderPatch: supplyJToken[ObjectGUID].ToString(): {supplyJToken["ObjectGUID"].ToString()}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
          PotExtensions.Supply[guid].Load(new(supplyJToken["ObjectGUID"].ToString()));
          if (PotExtensions.Supply[guid].SelectedObject != null)
          {
            DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotLoaderPatch: Loaded Supply for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
            PotExtensions.SourceChanged(config, PotExtensions.Supply[guid].SelectedObject);
          }
          else
          {
            DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotLoaderPatch: Supply.SelectedObject is null for pot: {guid}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
            PotExtensions.FailedSupply[guid] = new(supplyJToken["ObjectGUID"].ToString());
          }
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"PotLoaderPatch: Supply data is null in config for mainPath: {mainPath}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
        }

        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"PotLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems", DebugLogger.Category.Pot, DebugLogger.Category.Botanist);
      }
      catch (Exception e)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, $"PotLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}", DebugLogger.Category.Pot, DebugLogger.Category.Botanist, DebugLogger.Category.Stacktrace);
      }
    }
  }
}