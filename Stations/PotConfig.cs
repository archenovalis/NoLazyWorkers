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
using static NoLazyWorkers.Debug;

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
          Log(Level.Error, "SourceChanged(Pot): PotConfiguration is null", Category.Pot, Category.Botanist);
          return;
        }

        Pot pot = potConfig.Pot;
        if (pot == null)
        {
          Log(Level.Warning, "SourceChanged(pot): pot is null", Category.Pot, Category.Botanist);
          return;
        }

        Guid guid = pot.GUID;
        Log(Level.Verbose, $"SourceChanged(Pot): Called for PotConfig: {guid.ToString() ?? "null"}, Item: {item?.name ?? "null"}", Category.Pot, Category.Botanist);

        if (!Supply.ContainsKey(guid))
        {
          Log(Level.Warning, $"SourceChanged(Pot): PotSupply does not contain key for pot: {pot}", Category.Pot, Category.Botanist);
          return;
        }

        if (!SupplyRoute.ContainsKey(guid))
        {
          SupplyRoute[guid] = null;
        }

        if (SupplyRoute[guid] is TransitRoute supplyRoute)
        {
          Log(Level.Info, $"SourceChanged(pot): Destroying existing TransitRoute for pot: {guid}", Category.Pot, Category.Botanist);
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
          Log(Level.Info, $"SourceChanged(Pot): Created new TransitRoute for pot: {guid}, Supply: {item.name}", Category.Pot, Category.Botanist);
        }
        else
        {
          Log(Level.Info, $"SourceChanged(Pot): Item is null, no TransitRoute created for pot: {guid}", Category.Pot, Category.Botanist);
          SupplyRoute[guid] = null;
        }

        Log(Level.Verbose, $"SourceChanged(pot): Updated for MixerConfig: {potConfig}, Supply: {supply?.SelectedObject?.name ?? "null"}", Category.Pot, Category.Botanist);
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}", Category.Pot, Category.Botanist);
      }
    }

    public static void SupplyOnObjectChangedInvoke(BuildableItem item, PotConfiguration __instance, Pot pot, ObjectField Supply)
    {
      __instance.InvokeChanged();
      SourceChanged(__instance, item);
      Log(Level.Info, $"SupplyOnObjectChangedInvoke: Supply changed for pot {pot?.GUID.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.GUID.ToString() ?? "null"}", Category.Pot, Category.Botanist);
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
            Log(Level.Verbose, $"RestoreConfigurations: Started for pot: {guid}", Category.Pot, Category.Botanist);
            Config[guid] = potConfig;
            Log(Level.Info, $"RestoreConfigurations: Registered missing PotConfig for pot: {pot.name}", Category.Pot, Category.Botanist);

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
              Log(Level.Info, $"RestoreConfigurations: Initialized Supply for pot: {guid}", Category.Pot, Category.Botanist);
            }
          }
        }
        catch (Exception e)
        {
          Log(Level.Stacktrace, $"RestoreConfigurations: Failed to restore configuration for pot: {pot?.name ?? "null"}, error: {e}", Category.Pot, Category.Botanist);
        }
      }

      // Reload failed Supply entries
      try
      {
        Log(Level.Info, $"RestoreConfigurations: Reloading {FailedSupply.Count} failed Supply entries", Category.Pot, Category.Botanist);

        // Use ToList to avoid modifying collection during iteration
        foreach (var entry in FailedSupply.ToList())
        {
          Guid guid = entry.Key;
          ObjectFieldData supplyData = entry.Value;
          try
          {
            if (!Supply.TryGetValue(guid, out ObjectField supply) || supply == null)
            {
              Log(Level.Warning, $"RestoreConfigurations: Skipping reload for pot: {guid.ToString() ?? "null"}, supply not found", Category.Pot, Category.Botanist);
              FailedSupply.Remove(guid);
              continue;
            }

            Log(Level.Verbose, $"RestoreConfigurations: Reloading Supply for pot: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}", Category.Pot, Category.Botanist);
            supply.Load(supplyData);
            if (supply.SelectedObject == null)
            {
              Log(Level.Warning, $"RestoreConfigurations: Reload failed to set SelectedObject for pot: {guid}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}", Category.Pot, Category.Botanist);
            }
            else
            {
              Log(Level.Info, $"RestoreConfigurations: Reload succeeded, SelectedObject: {supply.SelectedObject.name} for pot: {guid}", Category.Pot, Category.Botanist);
              if (Config.TryGetValue(guid, out var config))
              {
                SourceChanged(config, supply.SelectedObject);
              }
            }
            FailedSupply.Remove(guid);
          }
          catch (Exception e)
          {
            Log(Level.Stacktrace, $"RestoreConfigurations: Failed to reload Supply for pot: {guid.ToString() ?? "null"}, error: {e}", Category.Pot, Category.Botanist);
            FailedSupply.Remove(guid);
          }
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"RestoreConfigurations: Failed to process FailedSupply entries, error: {e}", Category.Pot, Category.Botanist);
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
        Log(Level.Verbose, $"PotConfigurationPatch: Initializing for pot: {pot?.name ?? "null"}", Category.Pot, Category.Botanist);
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
        Log(Level.Info, $"PotConfigurationPatch: Registered supply and config for pot: {pot?.name ?? "null"}", Category.Pot, Category.Botanist);
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"PotConfigurationPatch: Failed for pot: {pot?.name ?? "null"}, error: {e}", Category.Pot, Category.Botanist);
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
          Log(Level.Verbose, $"PotConfiguration GetSaveStringPatch: supplyData: {supplyObject["ObjectGUID"]}", Category.Pot, Category.Botanist);
          __result = jsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
          Log(Level.Info, $"PotConfiguration GetSaveStringPatch: Saved JSON for station={guid}: {__result}", Category.Pot, Category.Botanist);
        }
        else
        {
          Log(Level.Warning, $"PotConfiguration GetSaveStringPatch: No Supply for pot {guid}", Category.Pot, Category.Botanist);
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"PotConfiguration GetSaveStringPatch failed: {e}", Category.Pot, Category.Botanist);
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
          Log(Level.Info, $"PotConfigurationDestroyPatch: Cleaned up for pot: {guid.ToString() ?? "null"}", Category.Pot, Category.Botanist);
        }
        else
        {
          Log(Level.Verbose, $"PotConfigurationDestroyPatch: Skipped removal for pot {__instance.Pot?.GUID}, station still exists", Category.Pot, Category.Botanist);
        }
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"PotConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}", Category.Pot, Category.Botanist);
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
            Log(Level.Verbose, $"PotConfigPanelBindPatch: Before Bind, station: {guid}, SelectedObject: {supply.SelectedObject?.name ?? "null"}", Category.Pot, Category.Botanist);
          }
          else
          {
            Log(Level.Warning, $"PotConfigPanelBindPatch: No supply found for MixingStationConfiguration, station: {guid}", Category.Pot, Category.Botanist);
          }
          PotExtensions.Supply[guid] = supply ?? new(config);
          supplyList.Add(PotExtensions.Supply[guid]);
        }

        Log(Level.Info, $"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}", Category.Pot, Category.Botanist);
        supplyUI.Bind(supplyList);

        RectTransform destRect = __instance.DestinationUI.GetComponent<RectTransform>();
        destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, -340f);
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"PotConfigPanelBindPatch: Failed, error: {e}", Category.Pot, Category.Botanist);
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
      Log(Level.Info, $"PotLoaderPatch: Supply changed for pot {pot.GUID}, newSupply={PotExtensions.Supply[pot.GUID].SelectedObject?.GUID.ToString() ?? "null"}", Category.Pot, Category.Botanist);
    }

    static void Postfix(string mainPath)
    {
      try
      {
        Log(Level.Info, $"PotLoaderPatch: Processing Postfix for mainPath: {mainPath}", Category.Pot, Category.Botanist);
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          Log(Level.Warning, $"PotLoaderPatch: No GridItem found for mainPath: {mainPath}", Category.Pot, Category.Botanist);
          return;
        }

        if (gridItem is not Pot pot)
        {
          Log(Level.Warning, $"PotLoaderPatch: GridItem is not a Pot for mainPath: {mainPath}, type: {gridItem.GetType().Name}", Category.Pot, Category.Botanist);
          return;
        }

        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          Log(Level.Warning, $"PotLoaderPatch: No Configuration.json found at: {configPath}", Category.Pot, Category.Botanist);
          return;
        }

        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          Log(Level.Warning, $"PotLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}", Category.Pot, Category.Botanist);
          return;
        }

        Log(Level.Stacktrace, $"PotLoaderPatch: Loaded JSON: {text}", Category.Pot);
        JObject jsonObject = JObject.Parse(text);
        JToken supplyJToken = jsonObject["Supply"];
        Log(Level.Verbose, $"PotLoaderPatch: Extracted supplyJToken: {supplyJToken}", Category.Pot, Category.Botanist);

        Guid guid = pot.GUID;
        PotConfiguration config = pot.Configuration as PotConfiguration;
        if (config == null)
        {
          Log(Level.Error, $"PotLoaderPatch: No valid PotConfiguration for pot: {guid}", Category.Pot, Category.Botanist);
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
          Log(Level.Info, $"PotLoaderPatch: Registered PotConfig for pot: {guid}", Category.Pot, Category.Botanist);
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

          Log(Level.Verbose, $"PotLoaderPatch: supplyJToken[ObjectGUID].ToString(): {supplyJToken["ObjectGUID"].ToString()}", Category.Pot, Category.Botanist);
          PotExtensions.Supply[guid].Load(new(supplyJToken["ObjectGUID"].ToString()));
          if (PotExtensions.Supply[guid].SelectedObject != null)
          {
            Log(Level.Info, $"PotLoaderPatch: Loaded Supply for pot: {guid}", Category.Pot, Category.Botanist);
            PotExtensions.SourceChanged(config, PotExtensions.Supply[guid].SelectedObject);
          }
          else
          {
            Log(Level.Warning, $"PotLoaderPatch: Supply.SelectedObject is null for pot: {guid}", Category.Pot, Category.Botanist);
            PotExtensions.FailedSupply[guid] = new(supplyJToken["ObjectGUID"].ToString());
          }
        }
        else
        {
          Log(Level.Warning, $"PotLoaderPatch: Supply data is null in config for mainPath: {mainPath}", Category.Pot, Category.Botanist);
        }

        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        Log(Level.Info, $"PotLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems", Category.Pot, Category.Botanist);
      }
      catch (Exception e)
      {
        Log(Level.Stacktrace, $"PotLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}", Category.Pot, Category.Botanist);
      }
    }
  }
}