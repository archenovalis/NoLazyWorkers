using HarmonyLib;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.Growing;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using System.Reflection;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers
{
    public static class BuildInfo
    {
        public const string Name = "NoLazyWorkers";
        public const string Description = "Botanist supply is moved to each pot and added to mixing stations. Botanists and Chemists will get items from their station's supply.";
        public const string Author = "Archie";
        public const string Company = null;
        public const string Version = "1.0";
        public const string DownloadLink = null;
    }

    public class NoLazyWorkers : MelonMod
    {
        public override void OnInitializeMelon()
        {
            try
            {
                HarmonyInstance.PatchAll();
                MelonLogger.Msg("NoLazyWorkers loaded!");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed to initialize NoLazyWorkers: {e}");
            }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            ConfigurationExtensions.PotSupply.Clear();
            ConfigurationExtensions.PotConfig.Clear();
            ConfigurationExtensions.PotSourceRoute.Clear();
            ConfigurationExtensions.MixerSupply.Clear();
            ConfigurationExtensions.MixerConfig.Clear();
            ConfigurationExtensions.MixerItem.Clear();
            ConfigurationExtensions.MixerSourceRoute.Clear();
            ConfigurationExtensions.PotSupplyData.Clear();
            ConfigurationExtensions.MixerSupplyData.Clear();
            ConfigurationExtensions.MixerItemData.Clear();
            MelonLogger.Msg("Cleared ConfigurationExtensions dictionaries on scene unload.");
        }
    }

    public static class ConfigurationExtensions
    {
        public static Dictionary<PotConfiguration, ObjectField> PotSupply = new();
        public static Dictionary<Pot, PotConfiguration> PotConfig = new();
        public static Dictionary<PotConfiguration, TransitRoute> PotSourceRoute = new();
        public static Dictionary<MixingStationConfiguration, ObjectField> MixerSupply = new();
        public static Dictionary<MixingStation, MixingStationConfiguration> MixerConfig = new();
        public static Dictionary<MixingStationConfiguration, ItemField> MixerItem = new();
        public static Dictionary<MixingStationConfiguration, TransitRoute> MixerSourceRoute = new();

        // Temporary storage for deserialized data
        public static Dictionary<Pot, ObjectFieldData> PotSupplyData = new();
        public static Dictionary<MixingStation, ObjectFieldData> MixerSupplyData = new();
        public static Dictionary<MixingStation, ItemFieldData> MixerItemData = new();

        public static void InvokeChanged(EntityConfiguration config)
        {
            try
            {
                if (config == null)
                {
                    MelonLogger.Error("InvokeChanged: EntityConfiguration is null");
                    return;
                }

                var method = typeof(EntityConfiguration).GetMethod("InvokeChanged",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (method == null)
                {
                    MelonLogger.Error("InvokeChanged: Method not found on EntityConfiguration");
                    return;
                }

                method.Invoke(config, null);
                MelonLogger.Msg($"InvokeChanged called for config: {config}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"InvokeChanged failed: {e}");
            }
        }

        public static void SourceChanged(this PotConfiguration potConfig, TransitRoute SourceRoute, ObjectField Supply, Pot pot)
        {
            if (SourceRoute != null)
            {
                SourceRoute.Destroy();
                PotSourceRoute[potConfig] = null;
            }

            if (Supply.SelectedObject != null)
            {
                SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, pot);
                if (pot.Configuration.IsSelected)
                {
                    SourceRoute.SetVisualsActive(active: true);
                }
                PotSourceRoute[potConfig] = SourceRoute;
            }
            else
            {
                PotSourceRoute[potConfig] = null;
            }
        }

        public static void SourceChanged(this MixingStationConfiguration mixerConfig, TransitRoute SourceRoute, ObjectField Supply, MixingStation station)
        {
            if (SourceRoute != null)
            {
                SourceRoute.Destroy();
                MixerSourceRoute[mixerConfig] = null;
            }

            if (Supply.SelectedObject != null)
            {
                SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, station);
                if (station.Configuration.IsSelected)
                {
                    SourceRoute.SetVisualsActive(active: true);
                }
                MixerSourceRoute[mixerConfig] = SourceRoute;
            }
            else
            {
                MixerSourceRoute[mixerConfig] = null;
            }
        }

        public static void RestoreConfigurations()
        {
            // Restore Pot configurations
            var pots = UnityEngine.Object.FindObjectsOfType<Pot>();
            foreach (var pot in pots)
            {
                try
                {
                    var configProp = typeof(Pot).GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);
                    if (configProp == null)
                    {
                        MelonLogger.Error("Pot.Configuration property not found");
                        continue;
                    }
                    var potConfig = configProp.GetValue(pot) as PotConfiguration;
                    if (potConfig != null && PotSupplyData.TryGetValue(pot, out ObjectFieldData supplyData))
                    {
                        ObjectField supply = PotSupply[potConfig];
                        supply.Load(supplyData);
                        SourceChanged(potConfig, PotSourceRoute.TryGetValue(potConfig, out var route) ? route : null, supply, pot);
                        PotSupplyData.Remove(pot);
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Failed to restore configuration for Pot {pot}: {e}");
                }
            }

            // Restore MixingStation configurations
            var mixingStations = UnityEngine.Object.FindObjectsOfType<MixingStation>();
            foreach (var station in mixingStations)
            {
                try
                {
                    var configProp = typeof(MixingStation).GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);
                    if (configProp == null)
                    {
                        MelonLogger.Error("MixingStation.Configuration property not found");
                        continue;
                    }
                    var mixerConfig = configProp.GetValue(station) as MixingStationConfiguration;
                    if (mixerConfig != null)
                    {
                        if (MixerSupplyData.TryGetValue(station, out ObjectFieldData supplyData))
                        {
                            ObjectField supply = MixerSupply[mixerConfig];
                            supply.Load(supplyData);
                            SourceChanged(mixerConfig, MixerSourceRoute.TryGetValue(mixerConfig, out var route) ? route : null, supply, station);
                            MixerSupplyData.Remove(station);
                        }
                        if (MixerItemData.TryGetValue(station, out ItemFieldData mixerItemData))
                        {
                            ItemField mixerItem = MixerItem[mixerConfig];
                            mixerItem.Load(mixerItemData);
                            MixerItemData.Remove(station);
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Failed to restore configuration for MixingStation {station}: {e}");
                }
            }
        }
    }

    public static class BotanistExtensions
    {
        public static ItemInstance GetItemInSupply(this Botanist botanist, Pot pot, string id)
        {
            PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
            ObjectField supply = ConfigurationExtensions.PotSupply[config];

            List<ItemSlot> list = new();
            BuildableItem supplyEntity = supply.SelectedObject;
            if (supplyEntity != null && botanist.behaviour.Npc.Movement.CanGetTo(supplyEntity as ITransitEntity, 1f))
            {
                list.AddRange((supplyEntity as ITransitEntity).OutputSlots);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Quantity > 0 && list[i].ItemInstance.ID.ToLower() == id.ToLower())
                    {
                        return list[i].ItemInstance;
                    }
                }
            }
            return null;
        }

        public static ItemInstance GetSeedInSupply(this Botanist botanist, Pot pot)
        {
            PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
            ObjectField supply = ConfigurationExtensions.PotSupply[config];

            List<ItemSlot> list = new();
            BuildableItem supplyEntity = supply.SelectedObject;
            if (supplyEntity != null && botanist.behaviour.Npc.Movement.CanGetTo(supplyEntity as ITransitEntity, 1f))
            {
                list.AddRange((supplyEntity as ITransitEntity).OutputSlots);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Quantity > 0 && list[i].ItemInstance.Definition is SeedDefinition)
                    {
                        return list[i].ItemInstance;
                    }
                }
            }
            return null;
        }
    }

    public static class ChemistExtensions
    {
        public static ItemInstance GetItemInSupply(this Chemist chemist, MixingStation station, string id)
        {
            MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
            ObjectField supply = ConfigurationExtensions.MixerSupply[config];

            List<ItemSlot> list = new();
            BuildableItem supplyEntity = supply.SelectedObject;
            if (supplyEntity != null && chemist.behaviour.Npc.Movement.CanGetTo(supplyEntity as ITransitEntity, 1f))
            {
                list.AddRange((supplyEntity as ITransitEntity).OutputSlots);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Quantity > 0 && list[i].ItemInstance.ID.ToLower() == id.ToLower())
                    {
                        return list[i].ItemInstance;
                    }
                }
            }
            return null;
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
                    MelonLogger.Msg("onLoadComplete fired, restoring configurations");
                    ConfigurationExtensions.RestoreConfigurations();
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
        // Store GridItem (Pot) temporarily, keyed by mainPath
        public static Dictionary<string, GridItem> LoadedGridItems = new Dictionary<string, GridItem>();

        static void Postfix(string mainPath, GridItem __result)
        {
            try
            {
                if (__result != null)
                {
                    // Store the GridItem (will be a Pot for PotLoader) with mainPath as the key
                    LoadedGridItems[mainPath] = __result;
                    MelonLogger.Msg($"Captured GridItem for {mainPath}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"GridItemLoaderPatch.Postfix failed: {e}");
            }
        }
    }

    // Patch for PotLoader.Load to use the captured Pot
    [HarmonyPatch(typeof(PotLoader), "Load")]
    public class PotLoaderPatch
    {
        static void Postfix(string mainPath)
        {
            try
            {
                // Check if we captured a GridItem (Pot) for this mainPath
                if (GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) && gridItem is Pot pot)
                {
                    // Process Configuration.json
                    if (File.Exists(Path.Combine(mainPath, "Configuration.json")) && new Loader().TryLoadFile(mainPath, "Configuration", out string text))
                    {
                        ExtendedPotConfigurationData configData = JsonUtility.FromJson<ExtendedPotConfigurationData>(text);
                        if (configData != null && configData.Supply != null)
                        {
                            ConfigurationExtensions.PotSupplyData[pot] = configData.Supply;
                            MelonLogger.Msg($"Associated Supply data with Pot for {mainPath}");
                        }
                    }

                    // Clean up to avoid memory leaks
                    GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
                }
                else
                {
                    MelonLogger.Warning($"No Pot found for {mainPath}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"PotLoaderPatch.Postfix failed: {e}");
            }
        }
    }
    [HarmonyPatch(typeof(MixingStationLoader), "Load")]
    public class MixingStationLoaderPatch
    {
        static void Postfix(string mainPath)
        {
            try
            {
                // Check if we captured a GridItem (MixingStation) for this mainPath
                if (GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem) && gridItem is MixingStation station)
                {
                    // Process Configuration.json
                    if (File.Exists(Path.Combine(mainPath, "Configuration.json")) && new Loader().TryLoadFile(mainPath, "Configuration", out string text))
                    {
                        ExtendedMixingStationConfigurationData configData = JsonUtility.FromJson<ExtendedMixingStationConfigurationData>(text);
                        if (configData != null && configData.Supply != null)
                        {
                            ConfigurationExtensions.MixerSupplyData[station] = configData.Supply;
                            MelonLogger.Msg($"Associated Supply data with MixingStation for {mainPath}");
                        }
                    }

                    // Clean up to avoid memory leaks
                    GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
                }
                else
                {
                    MelonLogger.Warning($"No MixingStation found for {mainPath}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"MixingStationLoaderPatch.Postfix failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(PotActionBehaviour), "DoesBotanistHaveMaterialsForTask")]
    public class DoesBotanistHaveMaterialsForTaskPatch
    {
        private static string[] GetRequiredItemIDs(PotActionBehaviour __instance, PotActionBehaviour.EActionType actionType, Pot pot)
        {
            PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
            switch (actionType)
            {
                case PotActionBehaviour.EActionType.PourSoil:
                    return new string[] { "soil", "longlifesoil", "extralonglifesoil" };
                case PotActionBehaviour.EActionType.SowSeed:
                    if (config.Seed.SelectedItem == null)
                        return Singleton<Registry>.Instance.Seeds.ConvertAll<string>(x => x.ID).ToArray();
                    return new string[] { config.Seed.SelectedItem.ID };
                case PotActionBehaviour.EActionType.ApplyAdditive:
                    if (__instance.AdditiveNumber == 1)
                        return new string[] { config.Additive1.SelectedItem.ID };
                    if (__instance.AdditiveNumber == 2)
                        return new string[] { config.Additive2.SelectedItem.ID };
                    if (__instance.AdditiveNumber == 3)
                        return new string[] { config.Additive3.SelectedItem.ID };
                    break;
            }
            return new string[0];
        }

        static bool Prefix(PotActionBehaviour __instance, Botanist botanist, Pot pot, PotActionBehaviour.EActionType actionType, int additiveNumber, ref bool __result)
        {
            switch (actionType)
            {
                case PotActionBehaviour.EActionType.PourSoil:
                    __result = botanist.GetItemInSupply(pot, "soil") != null ||
                                botanist.GetItemInSupply(pot, "longlifesoil") != null ||
                                botanist.GetItemInSupply(pot, "extralonglifesoil") != null ||
                                botanist.Inventory.GetMaxItemCount(GetRequiredItemIDs(__instance, actionType, pot)) > 0;
                    return false;

                case PotActionBehaviour.EActionType.SowSeed:
                    __result = botanist.GetSeedInSupply(pot) != null ||
                                botanist.Inventory.GetMaxItemCount(GetRequiredItemIDs(__instance, actionType, pot)) > 0;
                    return false;

                case PotActionBehaviour.EActionType.ApplyAdditive:
                    var additiveId = GetAdditiveId(pot, additiveNumber);
                    __result = additiveId != null && botanist.GetItemInSupply(pot, additiveId) != null ||
                                botanist.Inventory.GetMaxItemCount(GetRequiredItemIDs(__instance, actionType, pot)) > 0;
                    return false;

                default:
                    return true; // Let original method handle other cases
            }
        }

        static string GetAdditiveId(Pot pot, int additiveNumber)
        {
            PotConfiguration config = ConfigurationExtensions.PotConfig[pot];
            return additiveNumber switch
            {
                1 => config.Additive1?.SelectedItem?.ID,
                2 => config.Additive2?.SelectedItem?.ID,
                3 => config.Additive3?.SelectedItem?.ID,
                _ => null
            };
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
                TransitRoute SourceRoute = ConfigurationExtensions.PotSourceRoute.TryGetValue(__instance, out var route) ? route : null;
                ObjectField Supply = new(__instance)
                {
                    TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
                    objectFilter = __instance.DestinationFilter,
                    DrawTransitLine = true
                };
                Supply.onObjectChanged.AddListener(delegate
                {
                    ConfigurationExtensions.InvokeChanged(__instance);
                    ConfigurationExtensions.SourceChanged(__instance, SourceRoute, Supply, pot);
                });
                ConfigurationExtensions.PotSupply[__instance] = Supply;
                ConfigurationExtensions.PotConfig[pot] = __instance;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"PotConfigurationPatch failed: {e}");
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
                data.Supply = ConfigurationExtensions.PotSupply[__instance].GetData();
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
                ObjectField supply = ConfigurationExtensions.PotSupply[__instance];
                __result |= supply?.SelectedObject != null;
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

    [HarmonyPatch(typeof(StartMixingStationBehaviour), "ActiveMinPass")]
    public class StartMixingStationBehaviourPatch
    {
        private static readonly FieldInfo startRoutineField = typeof(StartMixingStationBehaviour)
            .GetField("startRoutine", BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Prefix(StartMixingStationBehaviour __instance)
        {
            // Check if startRoutine is not null using reflection
            if (startRoutineField.GetValue(__instance) != null)
                return true; // Cooking is in progress, let original logic handle it

            Chemist chemist = __instance.Npc as Chemist;
            MixingStation station = __instance.targetStation;
            if (station == null)
            {
                __instance.Disable();
                return false; // Skip original logic
            }

            // Get the mixer item from the station's configuration
            MixingStationConfiguration config = ConfigurationExtensions.MixerConfig[station];
            ItemField mixerItem = ConfigurationExtensions.MixerItem[config];
            if (mixerItem?.SelectedItem == null)
            {
                __instance.Disable();
                return false; // Skip original logic
            }

            // Check if the chemist can reach the supply
            if (!chemist.behaviour.Npc.Movement.CanGetTo(station, 1f))
            {
                Debug.LogWarning("Chemist cannot reach supply for mixing station.");
                __instance.Disable();
                return false; // Skip original logic
            }

            // Check if the required mixer item is in the supply
            if (chemist.GetItemInSupply(station, mixerItem.SelectedItem.ID) == null)
            {
                Debug.LogWarning("Required mixer item not found in supply.");
                __instance.Disable();
                return false; // Skip original logic
            }

            return true; // Proceed with original logic
        }
    }

    [HarmonyPatch(typeof(MixingStationConfiguration))]
    public class MixingStationConfigurationPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(MethodType.Constructor, new Type[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation) })]
        static void Postfix(MixingStationConfiguration __instance, MixingStation station)
        {
            try
            {
                TransitRoute SourceRoute = ConfigurationExtensions.MixerSourceRoute.TryGetValue(__instance, out var route) ? route : null;
                ObjectField Supply = new(__instance)
                {
                    TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
                    objectFilter = __instance.DestinationFilter,
                    DrawTransitLine = true
                };
                Supply.onObjectChanged.AddListener(delegate
                {
                    ConfigurationExtensions.InvokeChanged(__instance);
                    ConfigurationExtensions.SourceChanged(__instance, SourceRoute, Supply, station);
                });
                ConfigurationExtensions.MixerSupply[__instance] = Supply;
                ConfigurationExtensions.MixerConfig[station] = __instance;

                ItemField mixerItem = new(__instance)
                {
                    CanSelectNone = false
                };
                List<PropertyItemDefinition> validIngredients = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients;
                if (validIngredients != null)
                {
                    mixerItem.Options = validIngredients.Cast<ItemDefinition>().ToList();
                }
                mixerItem.onItemChanged.AddListener(delegate
                {
                    ConfigurationExtensions.InvokeChanged(__instance);
                });
                ConfigurationExtensions.MixerItem[__instance] = mixerItem;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"MixingStationConfigurationPatch failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(MixingStationConfiguration), "GetSaveString")]
    public class MixingStationConfigurationGetSaveStringPatch
    {
        static void Postfix(MixingStationConfiguration __instance, ref string __result)
        {
            try
            {
                ExtendedMixingStationConfigurationData data = new(
                    __instance.Destination.GetData(),
                    __instance.StartThrehold.GetData()
                );
                data.Supply = ConfigurationExtensions.MixerSupply[__instance].GetData();
                data.MixerItem = ConfigurationExtensions.MixerItem[__instance].GetData();
                __result = data.GetJson(true);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"MixingStationConfigurationGetSaveStringPatch failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(MixingStationConfiguration), "ShouldSave")]
    public class MixingStationConfigurationShouldSavePatch
    {
        static void Postfix(MixingStationConfiguration __instance, ref bool __result)
        {
            try
            {
                ObjectField supply = ConfigurationExtensions.MixerSupply[__instance];
                ItemField mixerItem = ConfigurationExtensions.MixerItem[__instance];
                __result |= supply?.SelectedObject != null || mixerItem?.SelectedItem != null;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"MixingStationConfigurationShouldSavePatch failed: {e}");
            }
        }
    }

    [Serializable]
    public class ExtendedMixingStationConfigurationData : MixingStationConfigurationData
    {
        public ObjectFieldData Supply;
        public ItemFieldData MixerItem;

        public ExtendedMixingStationConfigurationData(ObjectFieldData destination, NumberFieldData threshold)
            : base(destination, threshold)
        {
            Supply = null;
            MixerItem = null;
        }
    }

    public class MixingStationPreset : Preset
    {
        private static MixingStationPreset DefaultPresetInstance;

        public ItemList MixerItems { get; set; }

        public override Preset GetCopy()
        {
            var preset = new MixingStationPreset();
            CopyTo(preset);
            return preset;
        }

        public override void CopyTo(Preset other)
        {
            base.CopyTo(other);
            if (other is MixingStationPreset targetPreset)
            {
                MixerItems.CopyTo(targetPreset.MixerItems);
            }
        }

        public override void InitializeOptions()
        {
            MixerItems = new ItemList("Mixer Item", NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.ToArray().Select(item => item.ID).ToList(), true, true);
            MixerItems.All = true;
        }

        public static MixingStationPreset GetDefaultPreset()
        {
            if (DefaultPresetInstance == null)
            {
                DefaultPresetInstance = new MixingStationPreset
                {
                    PresetName = "Default",
                    ObjectType = (ManageableObjectType)100,
                    PresetColor = new Color32(180, 180, 180, 255)
                };
                DefaultPresetInstance.InitializeOptions();
            }
            return DefaultPresetInstance;
        }

        public static MixingStationPreset GetNewBlankPreset()
        {
            MixingStationPreset preset = GetDefaultPreset().GetCopy() as MixingStationPreset;
            preset.PresetName = "New Preset";
            return preset;
        }
    }

    public class MixingStationPresetEditScreen : PresetEditScreen
    {
        public GenericOptionUI MixerItemUI { get; set; }
        private MixingStationPreset castedPreset { get; set; }

        protected override void Awake()
        {
            base.Awake();
            if (MixerItemUI?.Button != null)
            {
                MixerItemUI.Button.onClick.AddListener(new UnityAction(MixerItemUIClicked));
            }
        }

        protected virtual void Update()
        {
            if (isOpen)
            {
                UpdateUI();
            }
        }

        public override void Open(Preset preset)
        {
            base.Open(preset);
            castedPreset = (MixingStationPreset)EditedPreset;
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (MixerItemUI?.ValueLabel != null && castedPreset?.MixerItems != null)
            {
                MixerItemUI.ValueLabel.text = castedPreset.MixerItems.GetDisplayString();
            }
        }

        private void MixerItemUIClicked()
        {
            if (Singleton<ItemSetterScreen>.Instance != null)
            {
                Singleton<ItemSetterScreen>.Instance.Open(castedPreset.MixerItems);
            }
            else
            {
                MelonLogger.Error("ItemSetterScreen instance not found!");
            }
        }
    }

    [HarmonyPatch(typeof(Preset), "GetDefault")]
    public class PresetPatch
    {
        static bool Prefix(ManageableObjectType type, ref Preset __result)
        {
            if (type == (ManageableObjectType)100)
            {
                __result = MixingStationPreset.GetDefaultPreset();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PotConfigPanel), "Bind")]
    public class PotConfigPanelBindPatch
    {
        static void Postfix(PotConfigPanel __instance, List<EntityConfiguration> configs)
        {
            var destinationUI = __instance.DestinationUI;
            GameObject supplyUIObj = new("SupplyUI");
            ObjectFieldUI supplyUI = UIExtensions.CloneComponent(destinationUI, supplyUIObj);
            supplyUI.name = "SupplyUI";
            UIExtensions.SetField(__instance, "SupplyUI", supplyUI);
            if (supplyUI == null)
                return;

            List<ObjectField> supplyList = new();
            foreach (EntityConfiguration config in configs)
            {
                if (config is not PotConfiguration potConfig)
                {
                    continue;
                }
                supplyList.Add(ConfigurationExtensions.PotSupply[potConfig]);
            }

            supplyUI.Bind(supplyList);
        }
    }

    [HarmonyPatch(typeof(PotUIElement), "Initialize")]
    public class PotUIElementInitializePatch
    {
        static void Postfix(PotUIElement __instance)
        {
            // Add SupplyIcon by cloning SeedIcon
            Image seedIcon = __instance.SeedIcon;
            if (seedIcon != null)
            {
                GameObject supply = new("SupplyIcon");
                Image supplyIcon = supply.AddComponent<Image>();
                UIExtensions.SetField(__instance, "SupplyIcon", supplyIcon);
            }
        }
    }

    [HarmonyPatch(typeof(PotUIElement), "RefreshUI")]
    public class PotUIElementRefreshUIPatch
    {
        static void Postfix(PotUIElement __instance)
        {
            var supplyIcon = UIExtensions.GetField<Image>(__instance, "SupplyIcon");
            if (supplyIcon == null)
                return;

            PotConfiguration potConfig = ConfigurationExtensions.PotConfig[__instance.AssignedPot];
            ObjectField supply = ConfigurationExtensions.PotSupply[potConfig];
            if (supply?.SelectedObject != null)
            {
                supplyIcon.sprite = supply.SelectedObject.ItemInstance.Icon;
                supplyIcon.gameObject.SetActive(true);
            }
            else
            {
                supplyIcon.gameObject.SetActive(false);
            }
        }
    }

    [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
    public class MixingStationConfigPanelBindPatch
    {
        static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
        {
            var destinationUI = __instance.DestinationUI;
            GameObject supplyUIObj = new("SupplyUI");
            ObjectFieldUI supplyUI = UIExtensions.CloneComponent(destinationUI, supplyUIObj);
            supplyUI.name = "SupplyUI";
            UIExtensions.SetField(__instance, "SupplyUI", supplyUI);

            PotConfigPanel potConfigPanel = UnityEngine.Object.FindObjectOfType<PotConfigPanel>();
            if (potConfigPanel == null || potConfigPanel.SeedUI == null)
            {
                MelonLogger.Error("Could not find PotConfigPanel with SeedUI");
                return;
            }

            GameObject mixerItemUIObj = new("MixerItemUI");
            ItemFieldUI mixerItemUI = UIExtensions.CloneComponent(potConfigPanel.SeedUI, mixerItemUIObj);
            mixerItemUI.name = "MixerItemUI";
            UIExtensions.SetField(__instance, "MixerItemUI", mixerItemUI);
            if (supplyUI == null || mixerItemUI == null)
                return;

            List<ObjectField> supplyList = new();
            List<ItemField> mixerItemList = new();
            foreach (EntityConfiguration config in configs)
            {
                if (config is not MixingStationConfiguration mixConfig)
                {
                    continue;
                }
                supplyList.Add(ConfigurationExtensions.MixerSupply[mixConfig]);
                mixerItemList.Add(ConfigurationExtensions.MixerItem[mixConfig]);
            }

            supplyUI.Bind(supplyList);
            mixerItemUI.Bind(mixerItemList);
        }
    }

    [HarmonyPatch(typeof(MixingStationUIElement), "Initialize")]
    public class MixingStationUIElementInitializePatch
    {
        static void Postfix(MixingStationUIElement __instance)
        {
            // Add SupplyIcon and MixerItemIcon
            GameObject supplyIconObj = new("SupplyIcon");
            Image supplyIcon = supplyIconObj.AddComponent<Image>();
            UIExtensions.SetField(__instance, "SupplyIcon", supplyIcon);

            GameObject mixerItemIconObj = new("MixerItemIcon");
            Image mixerItemIcon = mixerItemIconObj.AddComponent<Image>();
            UIExtensions.SetField(__instance, "MixerItemIcon", mixerItemIcon);
        }
    }

    [HarmonyPatch(typeof(MixingStationUIElement), "RefreshUI")]
    public class MixingStationUIElementRefreshUIPatch
    {
        static void Postfix(MixingStationUIElement __instance)
        {
            Image supplyIcon = UIExtensions.GetField<Image>(__instance, "SupplyIcon");
            Image mixerItemIcon = UIExtensions.GetField<Image>(__instance, "MixerItemIcon");
            if (supplyIcon == null || mixerItemIcon == null)
                return;

            MixingStationConfiguration mixConfig = ConfigurationExtensions.MixerConfig[__instance.AssignedStation];
            ObjectField supply = ConfigurationExtensions.MixerSupply[mixConfig];
            if (supply?.SelectedObject != null)
            {
                supplyIcon.sprite = supply.SelectedObject.ItemInstance.Icon;
                supplyIcon.gameObject.SetActive(true);
            }
            else
            {
                supplyIcon.gameObject.SetActive(false);
            }

            var mixerItem = ConfigurationExtensions.MixerItem[mixConfig];
            if (mixerItem?.SelectedItem != null)
            {
                mixerItemIcon.sprite = mixerItem.SelectedItem.Icon;
                mixerItemIcon.gameObject.SetActive(true);
            }
            else
            {
                mixerItemIcon.gameObject.SetActive(false);
            }
        }
    }
}