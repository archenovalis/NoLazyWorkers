using BepInEx.AssemblyPublicizer;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.Management.UI;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using static ScheduleOne.Registry;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Reflection;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers
{
    public static class DebugConfig
    {
        public static bool EnableDebugLogs = true; // true enables Msg and Warning logs
        public static bool EnableDebugCoreLogs = true; // true enables Core-only Msg and Warning Logs
        public static bool EnableDebugPotLogs = true; // true enables Pot-only Msg and Warning Logs
        public static bool EnableDebugMixingLogs = true; // true enables Mixing-only Msg and Warning Logs
        public static bool EnableDebugBehaviorLogs = true; // true enables Behavior-only Msg and Warning Logs
    }

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
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("NoLazyWorkers loaded!"); }
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
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("Cleared ConfigurationExtensions dictionaries on scene unload."); }
        }
    }

    public static class ConfigurationExtensions
    {
        public static GameObject ItemFieldUITemplate;
        public static Dictionary<PotConfiguration, ObjectField> PotSupply = new();
        public static Dictionary<Pot, PotConfiguration> PotConfig = new();
        public static Dictionary<PotConfiguration, TransitRoute> PotSourceRoute = new();
        public static Dictionary<MixingStationConfiguration, ObjectField> MixerSupply = new();
        public static Dictionary<MixingStation, MixingStationConfiguration> MixerConfig = new();
        public static Dictionary<MixingStationConfiguration, ItemField> MixerItem = new();
        public static Dictionary<MixingStationConfiguration, TransitRoute> MixerSourceRoute = new();
        public static Dictionary<Pot, ObjectFieldData> PotSupplyData = new();
        public static Dictionary<MixingStation, ObjectFieldData> MixerSupplyData = new();
        public static Dictionary<MixingStation, ItemFieldData> MixerItemData = new();
        public static Dictionary<EntityConfiguration, ObjectField> NPCSupply = new();
        public static Dictionary<NPC, EntityConfiguration> NPCConfig = new();

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
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"InvokeChanged called for config: {config}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"InvokeChanged failed: {e}");
            }
        }

        public static void SourceChanged(this PotConfiguration potConfig, BuildableItem item)
        {
            try
            {
                if (potConfig == null)
                {
                    MelonLogger.Error("SourceChanged(Pot): PotConfiguration is null");
                    return;
                }
                // Check if potConfig is registered
                if (!PotSupply.ContainsKey(potConfig))
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(Pot): PotSupply does not contain key for PotConfig: {potConfig}");
                    return;
                }
                if (!PotSourceRoute.ContainsKey(potConfig))
                {
                    PotSourceRoute[potConfig] = null; // Initialize if missing
                }

                TransitRoute SourceRoute = PotSourceRoute[potConfig];
                if (SourceRoute != null)
                {
                    SourceRoute.Destroy();
                    PotSourceRoute[potConfig] = null;
                }

                ObjectField Supply = PotSupply[potConfig];
                if (Supply.SelectedObject != null)
                {
                    SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, potConfig.Pot);
                    PotSourceRoute[potConfig] = SourceRoute;
                    if (potConfig.Pot.Configuration.IsSelected)
                    {
                        SourceRoute.SetVisualsActive(true);
                    }
                }
                else
                {
                    PotSourceRoute[potConfig] = null;
                }
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(Pot): Updated for PotConfig: {potConfig}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}");
            }
        }

        public static void SourceChanged(this MixingStationConfiguration mixerConfig, BuildableItem item)
        {
            try
            {
                if (mixerConfig == null)
                {
                    MelonLogger.Error("SourceChanged(MixingStation): MixingStationConfiguration is null");
                    return;
                }
                // Check if mixerConfig is registered
                if (!MixerSupply.ContainsKey(mixerConfig))
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): MixerSupply does not contain key for MixerConfig: {mixerConfig}");
                    return;
                }
                if (!MixerSourceRoute.ContainsKey(mixerConfig))
                {
                    MixerSourceRoute[mixerConfig] = null; // Initialize if missing
                }

                TransitRoute SourceRoute = MixerSourceRoute[mixerConfig];
                if (SourceRoute != null)
                {
                    SourceRoute.Destroy();
                    MixerSourceRoute[mixerConfig] = null;
                }

                ObjectField Supply = MixerSupply[mixerConfig];
                if (Supply.SelectedObject != null)
                {
                    SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, mixerConfig.station);
                    MixerSourceRoute[mixerConfig] = SourceRoute;
                    if (mixerConfig.station.Configuration.IsSelected)
                    {
                        SourceRoute.SetVisualsActive(true);
                        return;
                    }
                }
                else
                {
                    MixerSourceRoute[mixerConfig] = null;
                }
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Updated for MixerConfig: {mixerConfig}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"SourceChanged(MixingStation): Failed for MixerConfig: {mixerConfig}, error: {e}");
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
                if (!PotSupply.ContainsKey(potConfig))
                {
                    PotSupply[potConfig] = Supply; // Register if missing
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(Pot): Registered missing PotSupply for PotConfig: {potConfig}");
                }

                if (SourceRoute != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(Pot): Destroying existing SourceRoute");
                    SourceRoute.Destroy();
                    PotSourceRoute[potConfig] = null;
                }

                if (Supply.SelectedObject != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(Pot): Creating new TransitRoute from {Supply.SelectedObject.name} to Pot");
                    SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, pot);
                    PotSourceRoute[potConfig] = SourceRoute;
                    if (pot.Configuration.IsSelected)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(Pot): Pot is selected, enabling TransitRoute visuals");
                        SourceRoute.SetVisualsActive(true);
                    }
                }
                else
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(Pot): Supply.SelectedObject is null, setting SourceRoute to null");
                    PotSourceRoute[potConfig] = null;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"SourceChanged(Pot): Failed for PotConfig: {potConfig}, error: {e}");
            }
        }

        public static void SourceChanged(this MixingStationConfiguration mixerConfig, TransitRoute SourceRoute, ObjectField Supply, MixingStation station)
        {
            try
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Called for MixerConfig: {mixerConfig}, Station: {station?.name ?? "null"}, Supply: {Supply?.SelectedObject?.name ?? "null"}");
                if (mixerConfig == null || station == null)
                {
                    MelonLogger.Error($"SourceChanged(MixingStation): MixerConfig or Station is null");
                    return;
                }
                if (!MixerSupply.ContainsKey(mixerConfig))
                {
                    MixerSupply[mixerConfig] = Supply; // Register if missing
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): Registered missing MixerSupply for MixerConfig: {mixerConfig}");
                }

                if (SourceRoute != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Destroying existing SourceRoute");
                    SourceRoute.Destroy();
                    MixerSourceRoute[mixerConfig] = null;
                }

                if (Supply.SelectedObject != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Creating new TransitRoute from {Supply.SelectedObject.name} to MixingStation");
                    SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, station);
                    MixerSourceRoute[mixerConfig] = SourceRoute;
                    if (station.Configuration.IsSelected)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Station is selected, enabling TransitRoute visuals");
                        SourceRoute.SetVisualsActive(true);
                    }
                }
                else
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Supply.SelectedObject is null, setting SourceRoute to null");
                    MixerSourceRoute[mixerConfig] = null;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"SourceChanged(MixingStation): Failed for MixerConfig: {mixerConfig}, error: {e}");
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
                    PropertyInfo configProp = typeof(Pot).GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);
                    if (configProp == null)
                    {
                        MelonLogger.Error($"RestoreConfigurations: Pot.Configuration property not found for pot: {pot?.name ?? "null"}");
                        continue;
                    }
                    if (configProp.GetValue(pot) is PotConfiguration potConfig && PotSupplyData.TryGetValue(pot, out ObjectFieldData supplyData))
                    {
                        if (!PotConfig.ContainsKey(pot))
                        {
                            PotConfig[pot] = potConfig;
                            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Registered missing PotConfig for pot: {pot.name}");
                        }
                        if (!PotSupply.ContainsKey(potConfig))
                        {
                            PotSupply[potConfig] = new ObjectField(potConfig)
                            {
                                TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
                                DrawTransitLine = true
                            };
                            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Created new ObjectField for PotConfig: {potConfig}");
                        }
                        ObjectField supply = PotSupply[potConfig];
                        supply.Load(supplyData);
                        SourceChanged(potConfig, PotSourceRoute.TryGetValue(potConfig, out var route) ? route : null, supply, pot);
                        PotSupplyData.Remove(pot);
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"RestoreConfigurations: Restored configuration for pot: {pot.name}");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"RestoreConfigurations: Failed to restore configuration for pot: {pot?.name ?? "null"}, error: {e}");
                }
            }

            // Restore MixingStation configurations
            MixingStation[] mixingStations = UnityEngine.Object.FindObjectsOfType<MixingStation>();
            foreach (MixingStation station in mixingStations)
            {
                try
                {
                    PropertyInfo configProp = typeof(MixingStation).GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);
                    if (configProp == null)
                    {
                        MelonLogger.Error($"RestoreConfigurations: MixingStation.Configuration property not found for station: {station?.name ?? "null"}");
                        continue;
                    }
                    if (configProp.GetValue(station) is MixingStationConfiguration mixerConfig)
                    {
                        if (!MixerConfig.ContainsKey(station))
                        {
                            MixerConfig[station] = mixerConfig;
                            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Registered missing MixerConfig for station: {station.name}");
                        }
                        if (MixerSupplyData.TryGetValue(station, out ObjectFieldData supplyData))
                        {
                            if (!MixerSupply.ContainsKey(mixerConfig))
                            {
                                MixerSupply[mixerConfig] = new ObjectField(mixerConfig)
                                {
                                    TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
                                    DrawTransitLine = true
                                };
                                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Created new ObjectField for MixerConfig: {mixerConfig}");
                            }
                            ObjectField supply = MixerSupply[mixerConfig];
                            supply.Load(supplyData);
                            SourceChanged(mixerConfig, MixerSourceRoute.TryGetValue(mixerConfig, out var route) ? route : null, supply, station);
                            MixerSupplyData.Remove(station);
                        }
                        if (MixerItemData.TryGetValue(station, out ItemFieldData mixerItemData))
                        {
                            if (!MixerItem.ContainsKey(mixerConfig))
                            {
                                MixerItem[mixerConfig] = new ItemField(mixerConfig)
                                {
                                    CanSelectNone = true,
                                    Options = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients.Cast<ItemDefinition>().ToList()
                                };
                                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Created new ItemField for MixerConfig: {mixerConfig}");
                            }
                            ItemField mixerItem = MixerItem[mixerConfig];
                            mixerItem.Load(mixerItemData);
                            MixerItemData.Remove(station);
                        }
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"RestoreConfigurations: Restored configuration for station: {station.name}");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"RestoreConfigurations: Failed to restore configuration for station: {station?.name ?? "null"}, error: {e}");
                }
            }
        }
    }

    public static class NoLazyUtilities
    {
        public static int GetAmountInInventoryAndSupply(NPC npc, ItemDefinition item)
        {
            if (npc == null || item == null)
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"GetAmountInInventoryAndSupply: NPC or item is null");
                return 0;
            }

            int inventoryCount = npc.Inventory?._GetItemAmount(item.ID) ?? 0;
            int supplyCount = GetAmountInSupplies(npc, item.GetDefaultInstance());
            return inventoryCount + supplyCount;
        }

        public static int GetAmountInSupplies(NPC npc, ItemInstance item)
        {
            if (npc == null || item == null || string.IsNullOrEmpty(item.ID))
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"GetAmountInSupplies: NPC={npc}, item={item}, or item.ID={item?.ID} is null for {npc?.fullName ?? "null"}");
                return 0;
            }

            if (!ConfigurationExtensions.NPCConfig.TryGetValue(npc, out var config))
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"GetAmountInSupplies: NPCConfig not found for {npc.fullName}");
                return 0;
            }

            if (!ConfigurationExtensions.NPCSupply.TryGetValue(config, out var supply) || supply?.SelectedObject == null)
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"GetAmountInSupplies: Supply or SelectedObject is null for {npc.fullName}");
                return 0;
            }

            if (supply.SelectedObject is not ITransitEntity supplyT)
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"GetAmountInSupplies: Supply is not ITransitEntity for {npc.fullName}");
                return 0;
            }

            if (!npc.Movement.CanGetTo(supplyT, 1f))
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Warning($"GetAmountInSupplies: Cannot reach supply for {npc.fullName}");
                return 0;
            }

            var slots = (supplyT.OutputSlots ?? Enumerable.Empty<ItemSlot>())
                .Concat(supplyT.InputSlots ?? Enumerable.Empty<ItemSlot>())
                .Where(s => s?.ItemInstance != null && s.Quantity > 0)
                .Distinct();

            if (!slots.Any())
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                        MelonLogger.Msg($"GetAmountInSupplies: Found {itemIdLower} with quantity={slot.Quantity} in slot {slot.GetHashCode()} for {npc.fullName}");
                }
                else
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                        MelonLogger.Msg($"GetAmountInSupplies: Slot {slot.GetHashCode()} contains {slot.ItemInstance.ID} (quantity={slot.Quantity}), not {itemIdLower} for {npc.fullName}");
                }
            }

            if (quantity == 0)
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Msg($"GetAmountInSupplies: No items of {item.ID} found in supply {supplyT?.Name} for {npc.fullName}");
            }
            else
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
                    MelonLogger.Msg($"GetAmountInSupplies: Total quantity of {item.ID} is {quantity} in supply {supplyT?.Name} for {npc.fullName}");
            }
            return quantity;
        }

        public static GameObject GetItemFieldUITemplateFromPotConfigPanel()
        {
            try
            {
                // Get ManagementInterface instance
                ManagementInterface managementInterface = ManagementInterface.Instance;
                if (managementInterface == null)
                {
                    MelonLogger.Error("ManagementInterface instance is null");
                    return null;
                }

                // Get PotConfigPanel prefab
                ConfigPanel configPanelPrefab = managementInterface.GetConfigPanelPrefab(EConfigurableType.Pot);
                if (configPanelPrefab == null)
                {
                    MelonLogger.Error("No ConfigPanel prefab found for EConfigurableType.Pot");
                    return null;
                }

                // Verify it's a PotConfigPanel
                PotConfigPanel potConfigPanelPrefab = configPanelPrefab.GetComponent<PotConfigPanel>();
                if (potConfigPanelPrefab == null)
                {
                    MelonLogger.Error("ConfigPanel prefab for EConfigurableType.Pot is not a PotConfigPanel");
                    return null;
                }

                // Instantiate prefab temporarily
                GameObject tempPanelObj = UnityEngine.Object.Instantiate(configPanelPrefab.gameObject);
                tempPanelObj.SetActive(false); // Keep inactive to avoid rendering
                PotConfigPanel tempPanel = tempPanelObj.GetComponent<PotConfigPanel>();
                if (tempPanel == null)
                {
                    MelonLogger.Error("Instantiated prefab lacks PotConfigPanel component");
                    UnityEngine.Object.Destroy(tempPanelObj);
                    return null;
                }

                // Bind to initialize SeedUI (mimic game behavior)
                List<EntityConfiguration> configs = new List<EntityConfiguration>();
                // Create a dummy PotConfiguration if needed
                GameObject dummyPot = new GameObject("DummyPot");
                Pot pot = dummyPot.AddComponent<Pot>();
                ConfigurationReplicator replicator = new ConfigurationReplicator(); // Adjust if needed
                PotConfiguration potConfig = new PotConfiguration(replicator, pot, pot);
                configs.Add(potConfig);
                tempPanel.Bind(configs);
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("Bound temporary PotConfigPanel to initialize SeedUI"); }

                // Get SeedUI
                GameObject seedUITemplate = null;
                if (tempPanel.SeedUI != null && tempPanel.SeedUI.gameObject != null)
                {
                    seedUITemplate = tempPanel.SeedUI.gameObject;
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("Successfully retrieved SeedUI template from PotConfigPanel prefab"); }
                }
                else
                {
                    MelonLogger.Error("SeedUI is null in instantiated PotConfigPanel");
                }

                // Clean up
                UnityEngine.Object.Destroy(tempPanelObj);
                UnityEngine.Object.Destroy(dummyPot);
                return seedUITemplate;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed to get ItemFieldUI template from PotConfigPanel prefab: {e}");
                return null;
            }
        }

        public static GameObject GetPrefabGameObject(string id)
        {
            try
            {
                GameObject prefab = GetPrefab(id);
                if (prefab != null && prefab.GetComponent<PotConfigPanel>() != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"Found PotConfigPanel prefab with ID: {id}"); }
                    return prefab;
                }
                else if (prefab != null)
                {
                    MelonLogger.Warning($"Found prefab with ID: {id}, but it lacks PotConfigPanel component");
                }

                MelonLogger.Warning("No PotConfigPanel prefab found in Registry with any tested ID");
                return null;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed to find PotConfigPanel prefab: {e}");
                return null;
            }
        }

        public static void LogItemFieldUIDetails(ItemFieldUI itemfieldUI)
        {
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("=== ItemFieldUI Details ==="); }

            // Log basic info
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"ItemFieldUI GameObject: {(itemfieldUI.gameObject != null ? itemfieldUI.gameObject.name : "null")}"); }
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"ItemFieldUI Active: {itemfieldUI.gameObject?.activeSelf}"); }
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"ItemFieldUI Type: {itemfieldUI.GetType().Name}"); }

            // Log ItemFieldUI properties
            LogComponentDetails(itemfieldUI, 0);

            // Log hierarchy and components
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("--- Hierarchy and Components ---"); }
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
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg(new string(' ', indentLevel * 2) + "GameObject: null"); }
                return;
            }

            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"GameObject: {go.name}, Active: {go.activeSelf}"); }

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
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg(new string(' ', indentLevel * 2) + "Component: null"); }
                return;
            }

            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"Component: {component.GetType().Name}"); }

            // Use reflection to log all public fields
            var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(component);
                    string valueStr = ValueToString(value);
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"  Field: {field.Name} = {valueStr}"); }
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
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg(new string(' ', indentLevel * 2) + $"  Property: {property.Name} = {valueStr}"); }
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
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: Opening for ItemField, SelectedItem: {itemField.SelectedItem?.Name ?? "null"}"); }
                    if (itemField.Options != null)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: ItemField options count: {itemField.Options.Count}"); }
                    }
                    else
                    {
                        MelonLogger.Warning("ItemSetterScreenOpenPatch: ItemField Options is null");
                    }
                }
                else
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"ItemSetterScreenOpenPatch: Opening for {option?.GetType().Name ?? "null"}"); }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"ItemSetterScreenOpenPatch: Prefix failed, error: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(EntityConfiguration), "Selected")]
    public class EntityConfigurationSelectedPatch
    {
        static void Postfix(EntityConfiguration __instance)
        {
            try
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"EntityConfigurationSelectedPatch: {__instance.GetType().Name} selected"); }
                if (__instance is PotConfiguration potConfig && ConfigurationExtensions.PotSourceRoute.TryGetValue(potConfig, out var potRoute))
                {
                    if (potRoute != null)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("EntityConfigurationSelectedPatch: Enabling visuals for Pot SourceRoute"); }
                        potRoute.SetVisualsActive(active: true);
                    }
                    else
                    {
                        MelonLogger.Warning("EntityConfigurationSelectedPatch: Pot SourceRoute is null");
                    }
                }
                else if (__instance is MixingStationConfiguration mixerConfig && ConfigurationExtensions.MixerSourceRoute.TryGetValue(mixerConfig, out var mixerRoute))
                {
                    if (mixerRoute != null)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("EntityConfigurationSelectedPatch: Enabling visuals for MixingStation SourceRoute"); }
                        mixerRoute.SetVisualsActive(active: true);
                    }
                    else
                    {
                        MelonLogger.Warning("EntityConfigurationSelectedPatch: MixingStation SourceRoute is null");
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
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"EntityConfigurationDeselectedPatch: {__instance.GetType().Name} deselected"); }
                if (__instance is PotConfiguration potConfig && ConfigurationExtensions.PotSourceRoute.TryGetValue(potConfig, out TransitRoute potRoute))
                {
                    if (potRoute != null)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("EntityConfigurationDeselectedPatch: Disabling visuals for Pot SourceRoute"); }
                        potRoute.SetVisualsActive(active: false);
                    }
                    else
                    {
                        MelonLogger.Warning("EntityConfigurationDeselectedPatch: Pot SourceRoute is null");
                    }
                }
                else if (__instance is MixingStationConfiguration mixerConfig && ConfigurationExtensions.MixerSourceRoute.TryGetValue(mixerConfig, out TransitRoute mixerRoute))
                {
                    if (mixerRoute != null)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("EntityConfigurationDeselectedPatch: Disabling visuals for MixingStation SourceRoute"); }
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
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("onLoadComplete fired, restoring configurations"); }
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
        public static Dictionary<string, GridItem> LoadedGridItems = new Dictionary<string, GridItem>();

        static void Postfix(string mainPath, GridItem __result)
        {
            try
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"GridItemLoaderPatch: Processing LoadAndCreate for mainPath: {mainPath}"); }
                if (__result != null)
                {
                    LoadedGridItems[mainPath] = __result;
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"GridItemLoaderPatch: Captured GridItem (type: {__result.GetType().Name}) for mainPath: {mainPath}"); }
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

    // Patch for PotLoader.Load to use the captured Pot
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
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Found GridItem for mainPath: {mainPath}, type: {gridItem?.GetType().Name}"); }
                    if (gridItem is Pot pot)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: GridItem is Pot for mainPath: {mainPath}"); }
                        string configPath = Path.Combine(mainPath, "Configuration.json");
                        if (File.Exists(configPath))
                        {
                            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Found Configuration.json at: {configPath}"); }
                            if (new Loader().TryLoadFile(mainPath, "Configuration", out string text))
                            {
                                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Successfully loaded Configuration.json for mainPath: {mainPath}"); }
                                ExtendedPotConfigurationData configData = JsonUtility.FromJson<ExtendedPotConfigurationData>(text);
                                if (configData != null)
                                {
                                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Deserialized ExtendedPotConfigurationData for mainPath: {mainPath}"); }
                                    if (configData.Supply != null)
                                    {
                                        ConfigurationExtensions.PotSupplyData[pot] = configData.Supply;
                                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"PotLoaderPatch: Associated Supply data with Pot for mainPath: {mainPath}, PotSupplyData count: {ConfigurationExtensions.PotSupplyData.Count}"); }
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

    [HarmonyPatch(typeof(MixingStationLoader), "Load")]
    public class MixingStationLoaderPatch
    {
        static void Postfix(string mainPath)
        {
            try
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Processing Postfix for mainPath: {mainPath}"); }
                if (GridItemLoaderPatch.LoadedGridItems.TryGetValue(mainPath, out GridItem gridItem))
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Found GridItem for mainPath: {mainPath}, type: {gridItem?.GetType().Name}"); }
                    if (gridItem is MixingStation station)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: GridItem is MixingStation for mainPath: {mainPath}"); }
                        string configPath = Path.Combine(mainPath, "Configuration.json");
                        if (File.Exists(configPath))
                        {
                            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Found Configuration.json at: {configPath}"); }
                            if (new Loader().TryLoadFile(mainPath, "Configuration", out string text))
                            {
                                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Successfully loaded Configuration.json for mainPath: {mainPath}"); }
                                ExtendedMixingStationConfigurationData configData = JsonUtility.FromJson<ExtendedMixingStationConfigurationData>(text);
                                if (configData != null)
                                {
                                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Deserialized ExtendedMixingStationConfigurationData for mainPath: {mainPath}"); }
                                    // Store Supply data
                                    if (configData.Supply != null)
                                    {
                                        ConfigurationExtensions.MixerSupplyData[station] = configData.Supply;
                                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Associated Supply data with MixingStation for mainPath: {mainPath}, MixerSupplyData count: {ConfigurationExtensions.MixerSupplyData.Count}"); }
                                    }
                                    else
                                    {
                                        MelonLogger.Warning($"MixingStationLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
                                    }
                                    // Store MixerItem data
                                    if (configData.MixerItem != null)
                                    {
                                        ConfigurationExtensions.MixerItemData[station] = configData.MixerItem;
                                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Associated MixerItem data with MixingStation for mainPath: {mainPath}, MixerItemData count: {ConfigurationExtensions.MixerItemData.Count}"); }
                                    }
                                    else
                                    {
                                        MelonLogger.Warning($"MixingStationLoaderPatch: MixerItem data is null in config for mainPath: {mainPath}");
                                    }
                                }
                                else
                                {
                                    MelonLogger.Error($"MixingStationLoaderPatch: Failed to deserialize Configuration.json for mainPath: {mainPath}");
                                }
                            }
                            else
                            {
                                MelonLogger.Warning($"MixingStationLoaderPatch: Failed to load Configuration.json for mainPath: {mainPath}");
                            }
                        }
                        else
                        {
                            MelonLogger.Warning($"MixingStationLoaderPatch: No Configuration.json found at: {configPath}");
                        }
                        GridItemLoaderPatch.LoadedGridItems.Remove(mainPath);
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg($"MixingStationLoaderPatch: Removed mainPath: {mainPath} from LoadedGridItems"); }
                    }
                    else
                    {
                        MelonLogger.Warning($"MixingStationLoaderPatch: GridItem is not a MixingStation for mainPath: {mainPath}, type: {gridItem?.GetType().Name}");
                    }
                }
                else
                {
                    MelonLogger.Warning($"MixingStationLoaderPatch: No GridItem found for mainPath: {mainPath} in LoadedGridItems");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"MixingStationLoaderPatch: Postfix failed for mainPath: {mainPath}, error: {e}");
            }
        }
    }
}