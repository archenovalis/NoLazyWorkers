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

namespace NoLazyWorkers.Botanists
{
  public static class PotExtensions
  {
    public static Dictionary<Pot, ObjectField> PotSupply = [];
    public static Dictionary<Pot, PotConfiguration> PotConfig = [];
    public static Dictionary<Pot, TransitRoute> PotSourceRoute = [];
    public static Dictionary<Pot, ObjectFieldData> PotSupplyData = [];

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
        // Check if potConfig is registered
        if (!PotSupply.ContainsKey(pot))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(Pot): PotSupply does not contain key for PotConfig: {pot}");
          return;
        }
        if (!PotSourceRoute.ContainsKey(pot))
        {
          PotSourceRoute[pot] = null; // Initialize if missing
        }

        TransitRoute SourceRoute = PotSourceRoute[pot];
        if (SourceRoute != null)
        {
          SourceRoute.Destroy();
          PotSourceRoute[pot] = null;
        }

        ObjectField Supply = PotSupply[pot];
        if (Supply.SelectedObject != null)
        {
          SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, pot);
          PotSourceRoute[pot] = SourceRoute;
          if (potConfig.IsSelected)
          {
            SourceRoute.SetVisualsActive(true);
          }
        }
        else
        {
          PotSourceRoute[pot] = null;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(Pot): Updated for PotConfig: {potConfig}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}");
      }
    }

    public static void SourceChanged(this PotConfiguration potConfig, TransitRoute SourceRoute, ObjectField Supply, Pot pot)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(Pot): Called for PotConfig: {potConfig}, Pot: {pot?.name ?? "null"}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
        if (potConfig == null || pot == null)
        {
          MelonLogger.Error($"SourceChanged(Pot): PotConfig or Pot is null");
          return;
        }
        if (!PotSupply.ContainsKey(pot))
        {
          PotSupply[pot] = Supply; // Register if missing
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(Pot): Registered missing PotSupply for PotConfig: {potConfig}");
        }

        if (SourceRoute != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(Pot): Destroying existing SourceRoute");
          SourceRoute.Destroy();
          PotSourceRoute[pot] = null;
        }

        if (Supply.SelectedObject != null)
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(Pot): Creating new TransitRoute from {Supply.SelectedObject.name} to Pot");
          SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, pot);
          PotSourceRoute[pot] = SourceRoute;
          if (pot.Configuration.IsSelected)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(Pot): Pot is selected, enabling TransitRoute visuals");
            SourceRoute.SetVisualsActive(true);
          }
        }
        else
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(Pot): Supply.SelectedObject is null, setting SourceRoute to null");
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
      // Restore Pot configurations
      Pot[] pots = UnityEngine.Object.FindObjectsOfType<Pot>();
      foreach (Pot pot in pots)
      {
        try
        {
          if (pot.Configuration is PotConfiguration potConfig && PotSupplyData.TryGetValue(pot, out ObjectFieldData supplyData))
          {
            if (!PotConfig.ContainsKey(pot))
            {
              PotConfig[pot] = potConfig;
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                MelonLogger.Warning($"RestoreConfigurations: Registered missing PotConfig for pot: {pot.name}");
            }
            if (!PotSupply.ContainsKey(pot))
            {
              PotSupply[pot] = new ObjectField(potConfig)
              {
                TypeRequirements = [typeof(PlaceableStorageEntity)],
                DrawTransitLine = true
              };
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                MelonLogger.Warning($"RestoreConfigurations: Created new ObjectField for PotConfig: {potConfig}");
            }
            ObjectField supply = PotSupply[pot];
            supply.Load(supplyData);
            SourceChanged(potConfig, PotSourceRoute.TryGetValue(pot, out var route) ? route : null, supply, pot);
            PotSupplyData.Remove(pot);
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
              MelonLogger.Msg($"RestoreConfigurations: Restored configuration for pot: {pot.name}");
          }
        }
        catch (Exception e)
        {
          MelonLogger.Error($"RestoreConfigurations: Failed to restore configuration for pot: {pot?.name ?? "null"}, error: {e}");
        }
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs) MelonLogger.Msg($"PotConfigurationPatch: Initializing for pot: {pot?.name ?? "null"}");
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
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs) MelonLogger.Msg($"PotConfigurationPatch: supply.onObjectChanged Pot: {pot.GUID} Supply: {supply.SelectedObject.GUID}");
            ConfigurationExtensions.InvokeChanged(__instance);
            PotExtensions.SourceChanged(__instance, item);
          }
          catch (Exception e)
          {
            MelonLogger.Error($"PotConfigurationPatch: onObjectChanged failed for pot: {pot?.name ?? "null"}, error: {e}");
          }
        });
        // Ensure dictionary entries are created
        if (!PotExtensions.PotSupply.ContainsKey(pot))
        {
          PotExtensions.PotSupply[pot] = supply;
        }
        if (!PotExtensions.PotConfig.ContainsKey(pot))
        {
          PotExtensions.PotConfig[pot] = __instance;
        }
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugPotLogs) MelonLogger.Msg($"PotConfigurationPatch: Registered supply and config for pot: {pot?.name ?? "null"}");
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
        );
        data.Supply = PotExtensions.PotSupply[__instance.Pot].GetData();
        __result = data.GetJson(true);
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
        if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg($"PotConfigPanelBindPatch: Processing Postfix, instance: {__instance?.GetType().Name}, configs count: {configs?.Count ?? 0}"); }
        ObjectFieldUI destinationUI = __instance.DestinationUI;

        // Instantiate DestinationUI's GameObject to copy hierarchy
        GameObject supplyUIObj = UnityEngine.Object.Instantiate(destinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg("PotConfigPanelBindPatch: Instantiated SupplyUI successfully"); }

        // Ensure CanvasRenderer and other components
        supplyUIObj.AddComponent<CanvasRenderer>();

        foreach (var child in supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>())
        {
          if (child.gameObject.name == "Title")
          {
            child.text = "Supplies";
          }
          else
          {
            if (child.gameObject.name == "Description")
            {
              child.gameObject.SetActive(false);
            }
          }
        }

        // Position DestinationUI: y = -340 (move -60 from cloned)
        RectTransform destinationRect = destinationUI.GetComponent<RectTransform>();
        destinationRect.anchoredPosition = new Vector2(destinationRect.anchoredPosition.x, -340f);

        // Bind supply data
        List<ObjectField> supplyList = new();
        foreach (EntityConfiguration config in configs)
        {
          if (config is PotConfiguration potConfig)
          {
            if (PotExtensions.PotSupply.TryGetValue(potConfig.Pot, out ObjectField supply))
            {
              supplyList.Add(supply);
              if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg($"PotConfigPanelBindPatch: Added supply for PotConfiguration, SelectedObject: {(supply.SelectedObject != null ? supply.SelectedObject.name : "null")}"); }
            }
            else
            {
              MelonLogger.Warning($"PotConfigPanelBindPatch: No supply found for PotConfiguration");
            }
            if (DebugConfig.EnableDebugLogs) { MelonLogger.Msg($"PotConfigPanelBindPatch: Pot: {potConfig.Pot} Supply: {supply} {supply?.SelectedObject?.GUID}"); }
          }
          else
          {
            MelonLogger.Warning($"PotConfigPanelBindPatch: Config is not a PotConfiguration, type: {config?.GetType().Name}");
          }
        }

        supplyUI.Bind(supplyList);
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotConfigurationPatch: Failed, error: {e}");
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Processing Postfix for mainPath: {mainPath}"); }
        if (GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem))
        {
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Found GridItem type: {gridItem?.GetType().Name}"); }
          if (gridItem is Pot pot)
          {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: GridItem is Pot"); }
            string configPath = Path.Combine(mainPath, "Configuration.json");
            if (File.Exists(configPath))
            {
              if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Found Configuration.json at: {configPath}"); }
              if (new Loader().TryLoadFile(mainPath, "Configuration", out string text))
              {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Successfully loaded Configuration.json"); }
                ExtendedPotConfigurationData configData = JsonUtility.FromJson<ExtendedPotConfigurationData>(text);
                if (configData != null)
                {
                  if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Deserialized ExtendedPotConfigurationData"); }
                  if (configData.Supply != null)
                  {
                    if (!PotExtensions.PotSupply.ContainsKey(pot) || PotExtensions.PotSupply[pot] is null)
                      PotExtensions.PotSupply[pot] = new ObjectField(pot.Configuration);
                    PotExtensions.PotSupply[pot].Load(configData.Supply);
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Associated Supply data for Pot: {pot.GUID} with Supply: {configData.Supply.ObjectGUID}"); }
                  }
                  else
                  {
                    MelonLogger.Warning($"PotLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
                  }
                }
                else
                {
                  MelonLogger.Error($"PotLoaderPatch: Failed to deserialize Configuration.json for mainPath: {mainPath}");
                }
              }
              else
              {
                MelonLogger.Warning($"PotLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
              }
            }
            else
            {
              MelonLogger.Warning($"PotLoaderPatch: No Configuration.json found at: {configPath}");
            }
            GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems"); }
          }
          else
          {
            MelonLogger.Warning($"PotLoaderPatch: GridItem is not a Pot for mainPath: {mainPath}, type: {gridItem?.GetType().Name}");
          }
        }
        else
        {
          MelonLogger.Warning($"PotLoaderPatch: No GridItem found for mainPath: {mainPath} in LoadedGridItems");
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"PotLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
      }
    }
  }
}