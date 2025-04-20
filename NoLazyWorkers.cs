using HarmonyLib;
using MelonLoader;
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
using ScheduleOne.Employees;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0.1", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: HarmonyDontPatchAll]
namespace NoLazyWorkers
{
    public static class DebugConfig
    {
        public static bool EnableDebugLogs = true; // true enables Msg and Warning logs
        public static bool EnableDebugCoreLogs = false; // true enables Core-only Msg and Warning Logs
        public static bool EnableDebugPotLogs = false; // true enables Pot-only Msg and Warning Logs
        public static bool EnableDebugMixingLogs = false; // true enables Mixing-only Msg and Warning Logs
        public static bool EnableDebugBehaviorLogs = false; // true enables Behavior-only Msg and Warning Logs
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
            ConfigurationExtensions.PotSupplyData.Clear();
            ConfigurationExtensions.PotConfig.Clear();
            ConfigurationExtensions.PotSourceRoute.Clear();
            ConfigurationExtensions.MixingSupply.Clear();
            ConfigurationExtensions.MixingSupplyData.Clear();
            ConfigurationExtensions.MixingConfig.Clear();
            ConfigurationExtensions.MixingSourceRoute.Clear();
            ConfigurationExtensions.MixingRoutes.Clear();
            ConfigurationExtensions.MixingRoutesData.Clear();
            ConfigurationExtensions.NPCSupply.Clear();
            ConfigurationExtensions.NPCConfig.Clear();
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) { MelonLogger.Msg("Cleared ConfigurationExtensions dictionaries on scene unload."); }
        }
    }

    public static class ConfigurationExtensions
    {
        public static GameObject ItemFieldUITemplate;
        public static Dictionary<PotConfiguration, ObjectField> PotSupply = [];
        public static Dictionary<Pot, PotConfiguration> PotConfig = [];
        public static Dictionary<PotConfiguration, TransitRoute> PotSourceRoute = [];
        public static Dictionary<Pot, ObjectFieldData> PotSupplyData = [];
        public static Dictionary<MixingStationConfiguration, ObjectField> MixingSupply = [];
        public static Dictionary<MixingStation, ObjectFieldData> MixingSupplyData = [];
        public static Dictionary<MixingStation, MixingStationConfiguration> MixingConfig = [];
        public static Dictionary<MixingStationConfiguration, TransitRoute> MixingSourceRoute = [];
        public static Dictionary<MixingStation, List<MixingRoute>> MixingRoutes = [];
        public static Dictionary<MixingStation, List<MixingRouteData>> MixingRoutesData = [];
        public static Dictionary<EntityConfiguration, ObjectField> NPCSupply = [];
        public static Dictionary<NPC, EntityConfiguration> NPCConfig = [];
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

                var method = typeof(EntityConfiguration).GetMethod("InvokeChanged",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (method == null)
                {
                    MelonLogger.Error("InvokeChanged: Method not found on EntityConfiguration");
                    return;
                }

                float currentTime = Time.time;
                if (lastInvokeTimes.TryGetValue(config, out float lastTime) && currentTime - lastTime < debounceTime)
                {
                    if (DebugConfig.EnableDebugLogs)
                        MelonLogger.Msg($"InvokeChanged debounced for config: {config}");
                    return;
                }
                lastInvokeTimes[config] = currentTime;
                if (DebugConfig.EnableDebugLogs)
                    MelonLogger.Msg($"InvokeChanged called for config: {config}, StackTrace: {new System.Diagnostics.StackTrace()}");
                method.Invoke(config, null);
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
                if (!MixingSupply.ContainsKey(mixerConfig))
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): MixerSupply does not contain key for MixerConfig: {mixerConfig}");
                    return;
                }
                if (!MixingSourceRoute.ContainsKey(mixerConfig))
                {
                    MixingSourceRoute[mixerConfig] = null; // Initialize if missing
                }

                TransitRoute SourceRoute = MixingSourceRoute[mixerConfig];
                if (SourceRoute != null)
                {
                    SourceRoute.Destroy();
                    MixingSourceRoute[mixerConfig] = null;
                }

                ObjectField Supply = MixingSupply[mixerConfig];
                if (Supply.SelectedObject != null)
                {
                    SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, mixerConfig.station);
                    MixingSourceRoute[mixerConfig] = SourceRoute;
                    if (mixerConfig.station.Configuration.IsSelected)
                    {
                        SourceRoute.SetVisualsActive(true);
                        return;
                    }
                }
                else
                {
                    MixingSourceRoute[mixerConfig] = null;
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
                if (!MixingSupply.ContainsKey(mixerConfig))
                {
                    MixingSupply[mixerConfig] = Supply; // Register if missing
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"SourceChanged(MixingStation): Registered missing MixerSupply for MixerConfig: {mixerConfig}");
                }

                if (SourceRoute != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Destroying existing SourceRoute");
                    SourceRoute.Destroy();
                    MixingSourceRoute[mixerConfig] = null;
                }

                if (Supply.SelectedObject != null)
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg($"SourceChanged(MixingStation): Creating new TransitRoute from {Supply.SelectedObject.name} to MixingStation");
                    SourceRoute = new TransitRoute(Supply.SelectedObject as ITransitEntity, station);
                    MixingSourceRoute[mixerConfig] = SourceRoute;
                    if (station.Configuration.IsSelected)
                    {
                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Station is selected, enabling TransitRoute visuals");
                        SourceRoute.SetVisualsActive(true);
                    }
                }
                else
                {
                    if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Msg("SourceChanged(MixingStation): Supply.SelectedObject is null, setting SourceRoute to null");
                    MixingSourceRoute[mixerConfig] = null;
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
                                TypeRequirements = [typeof(PlaceableStorageEntity)],
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
                        if (!MixingConfig.ContainsKey(station))
                        {
                            MixingConfig[station] = mixerConfig;
                            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Registered missing MixerConfig for station: {station.name}");
                        }
                        if (MixingSupplyData.TryGetValue(station, out ObjectFieldData supplyData))
                        {
                            if (!MixingSupply.ContainsKey(mixerConfig))
                            {
                                MixingSupply[mixerConfig] = new ObjectField(mixerConfig)
                                {
                                    TypeRequirements = [typeof(PlaceableStorageEntity)],
                                    DrawTransitLine = true
                                };
                                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Created new ObjectField for MixerConfig: {mixerConfig}");
                            }
                            ObjectField supply = MixingSupply[mixerConfig];
                            supply.Load(supplyData);
                            SourceChanged(mixerConfig, MixingSourceRoute.TryGetValue(mixerConfig, out var route) ? route : null, supply, station);
                            MixingSupplyData.Remove(station);
                        }
                        if (MixingRoutesData.TryGetValue(station, out List<MixingRouteData> mixerItemData))
                        {
                            if (!MixingRoutes.ContainsKey(station))
                            {
                                MixingRoutes[station] = [];
                                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs) MelonLogger.Warning($"RestoreConfigurations: Created new ItemField for MixerConfig: {mixerConfig}");
                            }
                            else
                            {
                                List<MixingRoute> mixingRoutes = MixingRoutes[station];
                                foreach (var routeData in MixingRoutesData[station])
                                {
                                    var route = new MixingRoute(mixerConfig);
                                    route.Product.Load(routeData.Product);
                                    route.MixerItem.Load(routeData.MixerItem);
                                    mixingRoutes.Add(route);
                                }
                                MixingRoutesData.Remove(station);
                            }
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
        public static ItemField GetMixerItemForProductSlot(MixingStation station)
        {
            // Get the product from the product slot
            var productInSlot = station.ProductSlot.ItemInstance.Definition as ProductDefinition;
            if (productInSlot == null)
            {
                MelonLogger.Warning($"GetMixerItemForProductSlot: Product slot item is not a ProductDefinition for station={station?.ObjectId.ToString() ?? "null"}");
                return null;
            }

            // Get the routes for the station
            if (!ConfigurationExtensions.MixingRoutes.TryGetValue(station, out var routes) || routes == null || !routes.Any())
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
                    MelonLogger.Msg($"GetMixerItemForProductSlot: No routes defined for station={station.ObjectId}");
                return null;
            }

            // Find the first route where the product matches
            var matchingRoute = routes.FirstOrDefault(route =>
                route.Product?.SelectedItem != null &&
                route.Product.SelectedItem == productInSlot);

            if (matchingRoute == null)
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
                    MelonLogger.Msg($"GetMixerItemForProductSlot: No route matches product={productInSlot.Name} for station={station.ObjectId.ToString()}");
                return null;
            }
            // Return the mixerItem from the matching route
            if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
                MelonLogger.Msg($"GetMixerItemForProductSlot: Found mixerItem={matchingRoute.MixerItem.SelectedItem?.Name ?? "null"} for product={productInSlot.Name} in station={station.ObjectId.ToString()}");
            return matchingRoute.MixerItem;
        }

        public static int GetAmountInInventoryAndSupply(NPC npc, ItemDefinition item)
        {
            if (npc == null || item == null)
            {
                if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugBehaviorLogs)
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

        public static T GetUITemplateFromConfigPanel<T>(EConfigurableType configType, Func<ConfigPanel, T> getUITemplate) where T : Component
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

                // Get ConfigPanel prefab
                ConfigPanel configPanelPrefab = managementInterface.GetConfigPanelPrefab(configType);
                if (configPanelPrefab == null)
                {
                    MelonLogger.Error($"No ConfigPanel prefab found for {configType}");
                    return null;
                }

                // Instantiate prefab temporarily
                GameObject tempPanelObj = UnityEngine.Object.Instantiate(configPanelPrefab.gameObject);
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
                // Create a dummy configuration based on the config type
                GameObject dummyEntity = new GameObject($"Dummy{configType}");
                EntityConfiguration config = null;
                ConfigurationReplicator replicator = new ConfigurationReplicator();
                switch (configType)
                {
                    case EConfigurableType.Pot:
                        Pot pot = dummyEntity.AddComponent<Pot>();
                        config = new PotConfiguration(replicator, pot, pot);
                        break;
                    case EConfigurableType.Packager:
                        Packager packager = dummyEntity.AddComponent<Packager>();
                        config = new PackagerConfiguration(replicator, packager, packager);
                        break;
                    case EConfigurableType.MixingStation:
                        MixingStation mixingStation = dummyEntity.AddComponent<MixingStation>();
                        config = new MixingStationConfiguration(replicator, mixingStation, mixingStation);
                        break;
                    // Add other types as needed
                    default:
                        MelonLogger.Error($"Unsupported EConfigurableType: {configType}");
                        UnityEngine.Object.Destroy(tempPanelObj);
                        UnityEngine.Object.Destroy(dummyEntity);
                        return null;
                }

                configs.Add(config);
                tempPanel.Bind(configs);
                if (DebugConfig.EnableDebugLogs)
                    MelonLogger.Msg($"Bound temporary ConfigPanel for {configType} to initialize UI components");

                // Get the UI template
                T uiTemplate = getUITemplate(tempPanel);
                if (uiTemplate == null)
                {
                    MelonLogger.Error($"Failed to retrieve UI template from ConfigPanel for {configType}");
                }
                else if (DebugConfig.EnableDebugLogs)
                {
                    MelonLogger.Msg($"Successfully retrieved UI template from ConfigPanel for {configType}");
                }

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
                else if (__instance is MixingStationConfiguration mixerConfig && ConfigurationExtensions.MixingSourceRoute.TryGetValue(mixerConfig, out var mixerRoute))
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
                else if (__instance is MixingStationConfiguration mixerConfig && ConfigurationExtensions.MixingSourceRoute.TryGetValue(mixerConfig, out TransitRoute mixerRoute))
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
        public static Dictionary<string, GridItem> LoadedGridItems = [];

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
    /* [HarmonyPatch(typeof(PotLoader), "Load")]
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
    } */

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
                                        ConfigurationExtensions.MixingSupplyData[station] = configData.Supply;
                                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                                            MelonLogger.Msg($"MixingStationLoaderPatch: Associated Supply data with MixingStation for mainPath: {mainPath}, MixerSupplyData count: {ConfigurationExtensions.MixingSupplyData.Count}");
                                    }
                                    else
                                    {
                                        MelonLogger.Warning($"MixingStationLoaderPatch: Supply data is null in config for mainPath: {mainPath}");
                                    }

                                    // Convert and store MixerRoutes
                                    if (configData.MixingRoutes != null)
                                    {
                                        List<MixingRoute> mixerRoutes = [];
                                        MixingStationConfiguration config = ConfigurationExtensions.MixingConfig.TryGetValue(station, out var cfg) ? cfg : station.Configuration as MixingStationConfiguration;

                                        foreach (var routeData in configData.MixingRoutes)
                                        {
                                            var route = new MixingRoute(config);
                                            route.Product.Load(routeData.Product);
                                            route.MixerItem.Load(routeData.MixerItem);
                                            mixerRoutes.Add(route);
                                        }

                                        if (ConfigurationExtensions.MixingRoutes.ContainsKey(station))
                                        {
                                            MelonLogger.Warning($"MixingStationLoaderPatch: MixerRoutes already contains key for station={station?.ObjectId.ToString() ?? "null"}. Overwriting.");
                                        }
                                        ConfigurationExtensions.MixingRoutes[station] = mixerRoutes;

                                        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugCoreLogs)
                                            MelonLogger.Msg($"MixingStationLoaderPatch: Associated {mixerRoutes.Count} MixerRoutes with MixingStation for mainPath: {mainPath}, MixerRoutes count: {ConfigurationExtensions.MixingRoutes.Count}");
                                    }
                                    else
                                    {
                                        MelonLogger.Warning($"MixingStationLoaderPatch: MixerRoutes data is null in config for mainPath: {mainPath}");
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

    [HarmonyPatch(typeof(ConfigurationReplicator), "RpcLogic___ReceiveItemField_2801973956")]
    public class ConfigurationReplicatorReceiveItemFieldPatch
    {
        static bool Prefix(ConfigurationReplicator __instance, int fieldIndex, string value)
        {
            if (DebugConfig.EnableDebugLogs)
            {
                MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Received update for fieldIndex={fieldIndex}, value={value ?? "null"}");
                MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Fields count={__instance.Configuration.Fields.Count}");
                for (int i = 0; i < __instance.Configuration.Fields.Count; i++)
                    MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Fields[{i}]={__instance.Configuration.Fields[i]?.GetType().Name ?? "null"}");
            }
            if (fieldIndex < 0 || fieldIndex >= __instance.Configuration.Fields.Count)
            {
                if (DebugConfig.EnableDebugLogs)
                    MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Invalid fieldIndex={fieldIndex}, Configuration.Fields.Count={__instance.Configuration.Fields.Count}, skipping");
                return false;
            }
            var itemField = __instance.Configuration.Fields[fieldIndex] as ItemField;
            if (itemField == null)
            {
                if (DebugConfig.EnableDebugLogs)
                    MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: No ItemField at fieldIndex={fieldIndex}, Fields[{fieldIndex}]={__instance.Configuration.Fields[fieldIndex]?.GetType().Name ?? "null"}, skipping");
                return false;
            }
            if (string.IsNullOrEmpty(value) && !itemField.CanSelectNone)
            {
                if (DebugConfig.EnableDebugLogs)
                    MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Blocked null update for ItemField with CanSelectNone={itemField.CanSelectNone}, CurrentItem={itemField.SelectedItem?.Name ?? "null"}");
                return false;
            }
            if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"ConfigurationReplicatorReceiveItemFieldPatch: Allowing update for ItemField, CanSelectNone={itemField.CanSelectNone}, value={value}");
            return true;
        }
    }

    [HarmonyPatch(typeof(ItemField), "SetItem", new Type[] { typeof(ItemDefinition), typeof(bool) })]
    public class ItemFieldSetItemPatch
    {
        static bool Prefix(ItemField __instance, ItemDefinition item, bool network)
        {
            if (DebugConfig.EnableDebugLogs)
                MelonLogger.Msg($"ItemFieldSetItemPatch: Called for ItemField, network={network}, CanSelectNone={__instance.CanSelectNone}, Item={item?.Name ?? "null"}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}, StackTrace: {new System.Diagnostics.StackTrace().ToString()}");

            // Check if this is the Product field (assume Product has CanSelectNone=false or is paired with Mixer)
            bool isProductField = __instance.Options != null && __instance.Options.Any(o => ProductManager.FavouritedProducts.Contains(o));
            if ((item == null && __instance.CanSelectNone) || isProductField)
            {
                if (DebugConfig.EnableDebugLogs)
                    MelonLogger.Msg($"ItemFieldSetItemPatch: Blocked null update for Product field, CanSelectNone={__instance.CanSelectNone}, CurrentItem={__instance.SelectedItem?.Name ?? "null"}, StackTrace: {new System.Diagnostics.StackTrace().ToString()}");
                return false;
            }
            return true;
        }
    }
}