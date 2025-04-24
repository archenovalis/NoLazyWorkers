using HarmonyLib;
using MelonLoader;
using ScheduleOne.EntityFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoLazyWorkers.Botanists
{
  public static class PotExtensions
  {
    public static Dictionary<Pot, ObjectField> PotSupply = [];
    public static Dictionary<Pot, PotConfiguration> PotConfig = [];
    public static Dictionary<Pot, TransitRoute> PotSourceRoute = [];
    public static Dictionary<Pot, ObjectFieldData> FailedSupply = [];

    public static void SourceChanged(this PotConfiguration potConfig, BuildableItem item)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs)
          MelonLogger.Msg($"SourceChanged(Pot): Called for PotConfig: {potConfig?.Pot.GUID.ToString() ?? "null"}, Item: {item?.name ?? "null"}");
        if (potConfig == null)
        {
          MelonLogger.Error("SourceChanged(Pot): PotConfiguration is null");
          return;
        }
        Pot pot = potConfig.Pot;
        if (!PotSupply.ContainsKey(pot))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Warning($"SourceChanged(Pot): PotSupply does not contain key for pot: {pot}");
          return;
        }
        if (!PotSourceRoute.ContainsKey(pot))
        {
          PotSourceRoute[pot] = null;
        }
        TransitRoute SourceRoute = PotSourceRoute[pot];
        if (SourceRoute != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"SourceChanged(Pot): Destroying existing TransitRoute for pot: {pot.GUID}");
          SourceRoute.Destroy();
          PotSourceRoute[pot] = null;
        }
        if (item != null)
        {
          SourceRoute = new TransitRoute(item as ITransitEntity, pot);
          PotSourceRoute[pot] = SourceRoute;
          if (pot.Configuration.IsSelected)
          {
            SourceRoute.SetVisualsActive(true);
          }
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"SourceChanged(Pot): Created new TransitRoute for pot: {pot.GUID}, Supply: {item.name}");
          if (PotSupply[pot].SelectedObject != item)
            PotSupply[pot].SelectedObject = item;
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"SourceChanged(Pot): Item is null, no TransitRoute created for pot: {pot.GUID}");
          PotSourceRoute[pot] = null;
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}");
      }
    }

    public static void RestoreConfigurations()
    {
      Pot[] pots = UnityEngine.Object.FindObjectsOfType<Pot>();
      foreach (Pot pot in pots)
      {
        try
        {
          if (pot.Configuration is PotConfiguration potConfig)
          {
            if (!PotConfig.ContainsKey(pot))
            {
              PotConfig[pot] = potConfig;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                MelonLogger.Warning($"RestoreConfigurations: Registered missing PotConfig for pot: {pot.name}");
            }
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
              MelonLogger.Msg($"RestoreConfigurations: Registered configuration for pot: {pot.name}");
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Msg($"RestoreConfigurations: Reloading {FailedSupply.Count} failed Supply entries");

        // Use ToList to avoid modifying collection during iteration
        foreach (var entry in FailedSupply.ToList())
        {
          Pot pot = entry.Key;
          ObjectFieldData supplyData = entry.Value;

          try
          {
            if (pot == null || !PotSupply.TryGetValue(pot, out ObjectField supply) || supply == null)
            {
              MelonLogger.Warning($"RestoreConfigurations: Skipping reload for pot: {pot?.GUID.ToString() ?? "null"}, supply not found");
              FailedSupply.Remove(pot);
              continue;
            }

            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
              MelonLogger.Msg($"RestoreConfigurations: Reloading Supply for pot: {pot.GUID}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}");

            supply.Load(supplyData);
            FailedSupply.Remove(pot);

            if (supply.SelectedObject == null)
            {
              MelonLogger.Warning($"RestoreConfigurations: Reload failed to set SelectedObject for pot: {pot.GUID}, ObjectGUID: {supplyData.ObjectGUID ?? "null"}");
            }
            else
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                MelonLogger.Msg($"RestoreConfigurations: Reload succeeded, SelectedObject: {supply.SelectedObject.name} for pot: {pot.GUID}");
              if (PotConfig.TryGetValue(pot, out var config))
              {
                SourceChanged(config, supply.SelectedObject);
              }
            }
          }
          catch (Exception e)
          {
            MelonLogger.Error($"RestoreConfigurations: Failed to reload Supply for pot: {pot?.GUID.ToString() ?? "null"}, error: {e}");
            FailedSupply.Remove(pot);
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
    static void Postfix(PotConfiguration __instance, Pot pot)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs)
          MelonLogger.Msg($"PotConfigurationPatch: Initializing for pot: {pot?.name ?? "null"}");
        ObjectField supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = true
        };
        supply.onObjectChanged.RemoveAllListeners();
        supply.onObjectChanged.AddListener(item =>
        {
          try
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs)
              MelonLogger.Msg($"PotConfigurationPatch: supply.onObjectChanged Pot: {pot.GUID} Supply: {supply.SelectedObject?.GUID.ToString() ?? "null"} Item: {item?.GUID.ToString() ?? "null"}");
            ConfigurationExtensions.InvokeChanged(__instance);
            PotExtensions.SourceChanged(__instance, item);
          }
          catch (Exception e)
          {
            MelonLogger.Error($"PotConfigurationPatch: onObjectChanged failed for pot: {pot?.name ?? "null"}, error: {e}");
          }
        });
        if (!PotExtensions.PotSupply.ContainsKey(pot))
        {
          PotExtensions.PotSupply[pot] = supply;
        }
        if (!PotExtensions.PotConfig.ContainsKey(pot))
        {
          PotExtensions.PotConfig[pot] = __instance;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs)
          MelonLogger.Msg($"PotConfigurationPatch: Registered supply and config for pot: {pot?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationPatch: Failed for pot: {pot?.name ?? "null"}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfiguration), "GetSaveString")]
  public class PotConfigurationGetSaveStringPatch
  {
    static void Postfix(PotConfiguration __instance, ref string __result)
    {
      try
      {
        ExtendedPotConfigurationData data = new(
            __instance.Seed.GetData(),
            __instance.Additive1.GetData(),
            __instance.Additive2.GetData(),
            __instance.Additive3.GetData(),
            __instance.Destination.GetData()
        ); if (PotExtensions.PotSupply.TryGetValue(__instance.Pot, out var supply))
        {
          data.Supply = supply.GetData();
        }
        __result = data.GetJson(true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs)
          MelonLogger.Msg($"PotConfigurationGetSaveStringPatch: Saved JSON for pot={__instance.Pot.GUID}: {__result}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationGetSaveStringPatch failed: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfiguration), "ShouldSave")]
  public class PotConfigurationShouldSavePatch
  {
    static void Postfix(PotConfiguration __instance, ref bool __result)
    {
      try
      {
        ObjectField supply = PotExtensions.PotSupply[__instance.Pot];
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotConfigurationShouldSavePatch: Pot: {__instance.Pot.GUID} Supply: {supply.SelectedObject?.GUID}"); }
        __result |= supply.SelectedObject != null;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationShouldSavePatch failed: {e}");
      }
    }
  }

  [Serializable]
  public class ExtendedPotConfigurationData : PotConfigurationData
  {
    public ObjectFieldData Supply;

    public ExtendedPotConfigurationData(ItemFieldData seed, ItemFieldData additive1, ItemFieldData additive2, ItemFieldData additive3, ObjectFieldData destination)
        : base(seed, additive1, additive2, additive3, destination)
    {
      Supply = null;
    }
  }


  [HarmonyPatch(typeof(PotConfigPanel), "Bind")]
  public class PotConfigPanelBindPatch
  {
    static void Postfix(PotConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}");
        // Clean up existing SupplyUI
        foreach (Transform child in __instance.transform)
        {
          if (child.name == "SupplyUI")
            UnityEngine.Object.Destroy(child.gameObject);
        }

        // one new object for the panel
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = $"SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();

        List<ObjectField> supplyList = new();
        // bind all selected pots
        foreach (var config in configs.OfType<PotConfiguration>())
        {
          // Supply UI setup
          foreach (TextMeshProUGUI child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title") child.text = "Supplies";
            else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
          }
          if (PotExtensions.PotSupply.TryGetValue(config.Pot, out ObjectField supply))
          {
            if (DebugConfig.EnableDebugLogs)
              MelonLogger.Msg($"PotConfigPanelBindPatch: Before Bind, pot: {config.Pot.GUID}, SelectedObject: {supply.SelectedObject?.name ?? "null"}");
            supplyList.Add(supply);
          }
          else
          {
            MelonLogger.Warning($"PotConfigPanelBindPatch: No supply found for PotConfiguration, pot: {config.Pot.GUID}");
          }

          // Destination UI update
          foreach (TextMeshProUGUI child in __instance.DestinationUI.GetComponentsInChildren<TextMeshProUGUI>())
          {
            if (child.gameObject.name == "Title") child.text = "Destination";
            else if (child.gameObject.name == "Description") child.gameObject.SetActive(false);
          }
        }
        supplyUI.Bind(supplyList);
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
    static void Postfix(string mainPath)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Msg($"PotLoaderPatch: Processing Postfix for mainPath: {mainPath}");
        if (!GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) || gridItem == null)
        {
          MelonLogger.Warning($"PotLoaderPatch: No GridItem found for mainPath: {mainPath}");
          return;
        }
        if (gridItem is not Pot pot)
        {
          MelonLogger.Warning($"PotLoaderPatch: GridItem is not a MixingStation for mainPath: {mainPath}, type: {gridItem.GetType().Name}");
          return;
        }
        string configPath = Path.Combine(mainPath, "Configuration.json");
        if (!File.Exists(configPath))
        {
          MelonLogger.Warning($"PotLoaderPatch: No Configuration.json found at: {configPath}");
          return;
        }
        if (!new Loader().TryLoadFile(mainPath, "Configuration", out string text))
        {
          MelonLogger.Warning($"PotLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
          return;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"PotLoaderPatch: Loaded JSON: {text}");
        ExtendedPotConfigurationData configData = JsonUtility.FromJson<ExtendedPotConfigurationData>(text);
        if (configData == null)
        {
          MelonLogger.Error($"PotLoaderPatch: Failed to deserialize Configuration.json for mainPath: {mainPath}");
          return;
        }
        PotConfiguration config = PotExtensions.PotConfig.TryGetValue(pot, out var cfg)
            ? cfg
            : pot.Configuration as PotConfiguration;
        if (config == null)
        {
          MelonLogger.Error($"PotLoaderPatch: No valid PotConfiguration for pot: {pot.ObjectId}");
          return;
        }
        if (!PotExtensions.PotConfig.ContainsKey(pot))
        {
          PotExtensions.PotConfig[pot] = config;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"PotLoaderPatch: Registered PotConfig for pot: {pot.ObjectId}");
        }
        if (configData.Supply != null)
        {
          if (!PotExtensions.PotSupply.ContainsKey(pot))
          {
            PotExtensions.PotSupply[pot] = new ObjectField(config)
            {
              TypeRequirements = [typeof(PlaceableStorageEntity)],
              DrawTransitLine = true
            };
            PotExtensions.PotSupply[pot].onObjectChanged.RemoveAllListeners();
            PotExtensions.PotSupply[pot].onObjectChanged.AddListener(delegate
            {
              ConfigurationExtensions.InvokeChanged(config);
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
                MelonLogger.Msg($"PotLoaderPatch: Supply changed for pot {pot.ObjectId}, newSupply={PotExtensions.PotSupply[pot].SelectedObject?.ObjectId.ToString() ?? "null"}");
            });
            PotExtensions.PotSupply[pot].onObjectChanged.AddListener(item => PotExtensions.SourceChanged(config, item));
          }
          PotExtensions.PotSupply[pot].Load(configData.Supply);
          if (PotExtensions.PotSupply[pot].SelectedObject != null)
            PotExtensions.SourceChanged(config, PotExtensions.PotSupply[pot].SelectedObject);
          else
            PotExtensions.FailedSupply[pot] = configData.Supply;
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
            MelonLogger.Msg($"PotLoaderPatch: Loaded Supply for pot: {pot.ObjectId}");
        }
        else
        {
          MelonLogger.Warning($"PotLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
        }
        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
          MelonLogger.Msg($"PotLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(PotConfiguration), "Destroy")]
  public class PotConfigurationDestroyPatch
  {
    static void Postfix(PotConfiguration __instance)
    {
      try
      {
        PotExtensions.PotSupply.Remove(__instance.Pot);
        PotExtensions.PotSourceRoute.Remove(__instance.Pot);
        foreach (var pair in PotExtensions.PotConfig.Where(p => p.Value == __instance).ToList())
        {
          PotExtensions.PotConfig.Remove(pair.Key);
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs)
          MelonLogger.Msg($"PotConfigurationDestroyPatch: Cleaned up for pot: {__instance.Pot?.GUID.ToString() ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationDestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
    }
  }
}