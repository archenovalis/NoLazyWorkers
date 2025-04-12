using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Employees;
using ScheduleOne.ObjectScripts;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using ScheduleOne.DevUtilities;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Growing;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.EntityFramework;
using ScheduleOne;
using System.Reflection;
using ScheduleOne.UI.Management;
using UnityEngine.UI;

namespace NoLazyWorkers
{
    public static class BuildInfo
    {
        public const string Name = "NoLazyWorkers";
        public const string Description = "Supply is move to each pot and added to mixing stations. Botanists and Chemists will get items from their station's supply.";
        public const string Author = "Archie";
        public const string Company = null;
        public const string Version = "1.0";
        public const string DownloadLink = null;
    }
    public class NoLazyWorkers : MelonMod
    {
        public override void OnInitializeMelon()
        {
            HarmonyInstance.PatchAll();
            MelonLogger.Msg("EnhancedWorkstations loaded!");
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
        [HarmonyPatch(typeof(PotActionBehaviour), "CanGetToSupply")]
        public class CanGetToSupplyPatch
        {
            static bool Prefix(PotActionBehaviour __instance, ref bool __result)
            {
                return false;
            }
        }
        // PotConfigurationPatch
        [HarmonyPatch(typeof(PotConfiguration), MethodType.Constructor, new[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(Pot) })]
        public class PotConfigurationPatch
        {
            static void Postfix(PotConfiguration __instance)
            {
                // Add Supply field
                if (!ConfigurationExtensions.PotSupply.ContainsKey(__instance))
                {
                    ObjectField supply = new(__instance)
                    {
                        TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) }
                    };
                    supply.onObjectChanged.AddListener(delegate
                    {
                        ConfigurationExtensions.InvokeChanged(__instance.Pot.Configuration);
                    });
                    ConfigurationExtensions.PotSupply[__instance] = supply;
                    ConfigurationExtensions.PotConfig[__instance.Pot] = __instance;
                }
            }

            [HarmonyPostfix]
            [HarmonyPatch("GetSaveString")]
            static void ExtendSaveString(PotConfiguration __instance, ref string __result)
            {
                var data = new PotConfigurationData(
                    __instance.Seed.GetData(),
                    __instance.Additive1.GetData(),
                    __instance.Additive2.GetData(),
                    __instance.Additive3.GetData(),
                    __instance.Destination.GetData()
                );

                PotConfigurationDataPatch.Supply = ConfigurationExtensions.PotSupply[__instance].GetData();
                __result = data.GetJson(true);
            }

            [HarmonyPostfix]
            [HarmonyPatch("ShouldSave")]
            static void ExtendShouldSave(PotConfiguration __instance, ref bool __result)
            {
                __result |= ConfigurationExtensions.PotSupply[__instance].SelectedObject != null;
            }
        }
        [HarmonyPatch(typeof(PotConfigurationData))]
        public class PotConfigurationDataPatch
        {
            [HarmonyReversePatch]
            [HarmonyPatch(MethodType.Constructor, new[] { typeof(ItemFieldData), typeof(ItemFieldData), typeof(ItemFieldData), typeof(ItemFieldData), typeof(ObjectFieldData) })]
            public static void BaseConstructor(PotConfigurationData instance, ItemFieldData seed, ItemFieldData additive1, ItemFieldData additive2, ItemFieldData additive3, ObjectFieldData destination) { }

            [HarmonyPostfix]
            [HarmonyPatch(MethodType.Constructor, new[] { typeof(ItemFieldData), typeof(ItemFieldData), typeof(ItemFieldData), typeof(ItemFieldData), typeof(ObjectFieldData) })]
            static void ExtendConstructor(PotConfigurationData __instance, ItemFieldData seed, ItemFieldData additive1, ItemFieldData additive2, ItemFieldData additive3, ObjectFieldData destination)
            {
                Supply = null;
            }

            public static ObjectFieldData Supply
            {
                get => AccessTools.Field(typeof(PotConfigurationData), "Supply")?.GetValue(null) as ObjectFieldData;
                set => AccessTools.Field(typeof(PotConfigurationData), "Supply")?.SetValue(null, value);
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
        // MixingStationConfigurationPatch
        [HarmonyPatch(typeof(MixingStationConfiguration), MethodType.Constructor, new[] { typeof(ConfigurationReplicator), typeof(IConfigurable), typeof(MixingStation) })]
        public class MixingStationConfigurationPatch
        {
            static void Postfix(MixingStationConfiguration __instance)
            {
                // Add Supply field
                if (!ConfigurationExtensions.MixerSupply.ContainsKey(__instance))
                {
                    ObjectField supply = new(__instance)
                    {
                        TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) }
                    };
                    supply.onObjectChanged.AddListener(delegate
                    {
                        ConfigurationExtensions.InvokeChanged(__instance.station.Configuration);
                    });
                    ConfigurationExtensions.MixerSupply[__instance] = supply;
                    ConfigurationExtensions.MixerConfig[__instance.station] = __instance;
                }

                // Add MixerItem field
                FieldInfo mixerItemField = AccessTools.Field(typeof(MixingStationConfiguration), "MixerItem");
                if (mixerItemField == null)
                {
                    MelonLogger.Error("MixerItem field not found in MixingStationConfiguration; ensure it's declared.");
                    return;
                }
                if (mixerItemField.GetValue(__instance) is not ItemField mixerItem)
                {
                    mixerItem = new ItemField(__instance);
                    mixerItemField.SetValue(__instance, mixerItem);
                    mixerItem.CanSelectNone = false;
                    List<PropertyItemDefinition> validIngredients = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients;
                    if (validIngredients != null)
                    {
                        mixerItem.Options = validIngredients.Cast<ItemDefinition>().ToList();
                    }
                    mixerItem.onItemChanged.AddListener(delegate
                    {
                        ConfigurationExtensions.InvokeChanged(__instance.station.Configuration);
                    });
                    ConfigurationExtensions.MixerItem[__instance] = mixerItem;
                }
            }

            [HarmonyPostfix]
            [HarmonyPatch("GetSaveString")]
            static void ExtendSaveString(MixingStationConfiguration __instance, ref string __result)
            {
                MixingStationConfigurationData data = new(
                    __instance.Destination.GetData(),
                    __instance.StartThrehold.GetData()
                );
                MixingStationConfigurationDataPatch.Supply = ConfigurationExtensions.MixerSupply[__instance].GetData();
                MixingStationConfigurationDataPatch.MixerItem = ConfigurationExtensions.MixerItem[__instance].GetData();
                __result = data.GetJson(true);
            }

            [HarmonyPostfix]
            [HarmonyPatch("ShouldSave")]
            static void ExtendShouldSave(MixingStationConfiguration __instance, ref bool __result)
            {
                ObjectField supply = AccessTools.Field(typeof(MixingStationConfiguration), "Supply")?.GetValue(__instance) as ObjectField;
                ItemField mixerItem = AccessTools.Field(typeof(MixingStationConfiguration), "MixerItem")?.GetValue(__instance) as ItemField;
                __result |= supply?.SelectedObject != null || mixerItem?.SelectedItem != null;
            }
        }
        [HarmonyPatch(typeof(MixingStationConfigurationData))]
        public class MixingStationConfigurationDataPatch
        {
            [HarmonyReversePatch]
            [HarmonyPatch(MethodType.Constructor, new[] { typeof(ObjectFieldData), typeof(NumberFieldData) })]
            public static void BaseConstructor(MixingStationConfigurationData instance, ObjectFieldData destination, NumberFieldData threshold) { }

            [HarmonyPostfix]
            [HarmonyPatch(MethodType.Constructor, new[] { typeof(ObjectFieldData), typeof(NumberFieldData) })]
            static void ExtendConstructor(MixingStationConfigurationData __instance, ObjectFieldData destination, NumberFieldData threshold)
            {
                Supply = null;
                MixerItem = null;
            }

            public static ObjectFieldData Supply
            {
                get => AccessTools.Field(typeof(MixingStationConfigurationData), "Supply")?.GetValue(null) as ObjectFieldData;
                set => AccessTools.Field(typeof(MixingStationConfigurationData), "Supply")?.SetValue(null, value);
            }

            public static ItemFieldData MixerItem
            {
                get => AccessTools.Field(typeof(MixingStationConfigurationData), "MixerItem")?.GetValue(null) as ItemFieldData;
                set => AccessTools.Field(typeof(MixingStationConfigurationData), "MixerItem")?.SetValue(null, value);
            }
        }
        // MixingStationPreset
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

        // MixingStationPresetEditScreen
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


        // ConfigReplicatorPatch
        [HarmonyPatch(typeof(ConfigurationReplicator), "OpenConfigurationScreen")]
        public class ConfigReplicatorPatch
        {
            static bool Prefix(ConfigurationReplicator __instance, IConfigurable configurable)
            {
                if (configurable is MixingStation mixingStation)
                {
                    var prefab = Resources.Load<GameObject>("MixingStationPresetEditScreen");
                    if (prefab == null)
                    {
                        MelonLogger.Error("MixingStationPresetEditScreen prefab not found! Ensure it's added to Resources.");
                        return true;
                    }
                    var uiInstance = UnityEngine.Object.Instantiate(prefab);
                    var screen = uiInstance.GetComponent<MixingStationPresetEditScreen>();
                    var config = mixingStation.Configuration;
                    var preset = AccessTools.Field(typeof(MixingStationConfiguration), "Preset")?.GetValue(config) as Preset;
                    screen.Open(preset ?? MixingStationPreset.GetDefaultPreset());
                    uiInstance.transform.SetParent(GameObject.Find("Canvas")?.transform, false);
                    uiInstance.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
                    return false;
                }
                return true;
            }
        }

        // PresetPatch
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
                foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    field.SetValue(newComponent, field.GetValue(source));
                }
                return newComponent;
            }
        }

        [HarmonyPatch(typeof(PotConfigPanel), "Awake")]
        public class PotConfigPanelAwakePatch
        {
            static void Postfix(PotConfigPanel __instance)
            {
                // Add SupplyUI by cloning DestinationUI
                var destinationUI = __instance.DestinationUI;
                if (destinationUI != null)
                {
                    GameObject supplyUIObj = new("SupplyUI");
                    ObjectFieldUI supplyUI = UIExtensions.CloneComponent(destinationUI, supplyUIObj);
                    supplyUI.name = "SupplyUI";
                    UIExtensions.SetField(__instance, "SupplyUI", supplyUI);
                }
            }
        }
        [HarmonyPatch(typeof(PotConfigPanel), "Bind")]
        public class PotConfigPanelBindPatch
        {
            static void Postfix(PotConfigPanel __instance, List<EntityConfiguration> configs)
            {
                var supplyUI = UIExtensions.GetField<ObjectFieldUI>(__instance, "SupplyUI");
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
        [HarmonyPatch(typeof(MixingStationConfigPanel), "Awake")]
        public class MixingStationConfigPanelAwakePatch
        {
            static void Postfix(MixingStationConfigPanel __instance)
            {
                // Add SupplyUI by cloning DestinationUI
                var destinationUI = __instance.DestinationUI;
                if (destinationUI != null)
                {
                    GameObject supplyUIObj = new("SupplyUI");
                    ObjectFieldUI supplyUI = UIExtensions.CloneComponent(destinationUI, supplyUIObj);
                    supplyUI.name = "SupplyUI";
                    UIExtensions.SetField(__instance, "SupplyUI", supplyUI);

                    GameObject mixerItemUIObj = new("MixerItemUI");
                    ObjectFieldUI mixerItemUI = UIExtensions.CloneComponent(destinationUI, mixerItemUIObj);
                    mixerItemUI.name = "MixerItemUI";
                    UIExtensions.SetField(__instance, "MixerItemUI", mixerItemUI);
                }
            }
        }
        [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
        public class MixingStationConfigPanelBindPatch
        {
            static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
            {
                ObjectFieldUI supplyUI = UIExtensions.GetField<ObjectFieldUI>(__instance, "SupplyUI");
                ItemFieldUI mixerItemUI = UIExtensions.GetField<ItemFieldUI>(__instance, "MixerItemUI");
                if (supplyUI == null || mixerItemUI == null)
                    return;

                List<ObjectField> supplyList = new List<ObjectField>();
                List<ItemField> mixerItemList = new List<ItemField>();
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
                GameObject supplyIconObj = new GameObject("SupplyIcon");
                Image supplyIcon = supplyIconObj.AddComponent<Image>();
                UIExtensions.SetField(__instance, "SupplyIcon", supplyIcon);

                GameObject mixerItemIconObj = new GameObject("MixerItemIcon");
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
    public static class ConfigurationExtensions
    {
        public static Dictionary<PotConfiguration, ObjectField> PotSupply = new();
        public static Dictionary<Pot, PotConfiguration> PotConfig = new();
        public static Dictionary<MixingStationConfiguration, ObjectField> MixerSupply = new();
        public static Dictionary<MixingStation, MixingStationConfiguration> MixerConfig = new();
        public static Dictionary<MixingStationConfiguration, ItemField> MixerItem = new();

        public static void InvokeChanged(EntityConfiguration config)
        {
            var method = typeof(EntityConfiguration).GetMethod("InvokeChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(config, null);
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
}