using BepInEx.AssemblyPublicizer;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.Presets;
using ScheduleOne.Management.Presets.Options;
using ScheduleOne.Management.SetterScreens;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using UnityEngine;
using UnityEngine.Events;
using TMPro;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(NoLazyWorkers.NoLazyWorkers), "NoLazyWorkers", "1.0.1", "Archie")]
[assembly: MelonGame("TVGS", "Schedule I")]
namespace NoLazyWorkers
{

  [Serializable]
  public class ExtendedMixingStationConfigurationData : MixingStationConfigurationData
  {
    public ObjectFieldData Supply;
    public List<MixingRouteData> MixingRoutes;

    public ExtendedMixingStationConfigurationData(ObjectFieldData destination, NumberFieldData threshold)
        : base(destination, threshold)
    {
      Supply = null;
      MixingRoutes = new List<MixingRouteData>();
    }
  }

  public class MixingRoute
  {
    public ItemField Product { get; set; }
    public ItemField MixerItem { get; set; }

    public MixingRoute(MixingStationConfiguration config)
    {
      Product = new ItemField(config)
      {
        CanSelectNone = true,
        Options = ProductManager.DiscoveredProducts.Cast<ItemDefinition>().ToList() ?? new List<ItemDefinition>()
      };
      MixerItem = new ItemField(config)
      {
        CanSelectNone = true,
        Options = NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.Cast<ItemDefinition>().ToList()
      };
    }

    public void SetData(MixingRouteData data)
    {
      Product.Load(data.Product);
      MixerItem.Load(data.MixerItem);
    }
  }

  [Serializable]
  public class MixingRouteData
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData(ItemFieldData product, ItemFieldData mixerItem)
    {
      Product = product;
      MixerItem = mixerItem;
    }
  }

  public class ProductSelectionScreen : MonoBehaviour
  {
    public GameObject EntryPrefab; // Prefab for product entry
    public Transform Container;    // Where entries are instantiated
    public TextMeshProUGUI NameLabel;
    private Action<ProductDefinition> onProductSelected;

    private static ProductSelectionScreen _instance;
    public static ProductSelectionScreen Instance => _instance;

    private void Awake()
    {
      _instance = this;
      gameObject.SetActive(false);
    }

    public void Open(Action<ProductDefinition> callback)
    {
      onProductSelected = callback;
      foreach (Transform child in Container)
        Destroy(child.gameObject);

      foreach (var product in ProductManager.DiscoveredProducts)
      {
        var entry = Instantiate(EntryPrefab, Container).GetComponent<ProductEntry>();
        entry.Initialize(product);
        var button = entry.gameObject.AddComponent<Button>();
        button.onClick.AddListener(() => SelectProduct(product));
      }
      gameObject.SetActive(true);
    }

    private void SelectProduct(ProductDefinition product)
    {
      NameLabel.text = product.Name;
      var selectButton = NameLabel.transform.parent.Find("SelectButton")?.GetComponent<Button>();
      if (selectButton == null)
      {
        selectButton = new GameObject("SelectButton").AddComponent<Button>();
        selectButton.transform.SetParent(NameLabel.transform.parent, false);
        var buttonText = new GameObject("Text").AddComponent<TextMeshProUGUI>();
        buttonText.transform.SetParent(selectButton.transform, false);
        buttonText.text = "Select";
        buttonText.alignment = TextAlignmentOptions.Center;

        // Shift NameLabel and other siblings downward
        foreach (Transform sibling in NameLabel.transform.parent)
        {
          var rect = sibling.GetComponent<RectTransform>();
          if (rect != null && sibling != selectButton.transform)
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y - 50f);
        }
      }
      selectButton.onClick.RemoveAllListeners();
      selectButton.onClick.AddListener(() =>
      {
        onProductSelected?.Invoke(product);
        gameObject.SetActive(false);
      });
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
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"MixingStationConfigurationPatch: Initializing for station: {station?.ObjectId.ToString() ?? "null"}, configHash={__instance.GetHashCode()}");

        ObjectField Supply = new(__instance)
        {
          TypeRequirements = new List<Type> { typeof(PlaceableStorageEntity) },
          DrawTransitLine = true
        };
        Supply.onObjectChanged.RemoveAllListeners();
        Supply.onObjectChanged.AddListener(delegate
        {
          ConfigurationExtensions.InvokeChanged(__instance);
          if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
            MelonLogger.Msg($"Supply changed for station {station?.ObjectId.ToString() ?? "null"}, newSupply={Supply.SelectedObject?.ObjectId.ToString() ?? "null"}");
        });
        Supply.onObjectChanged.AddListener(item => ConfigurationExtensions.SourceChanged(__instance, item));
        ConfigurationExtensions.MixingSupply[__instance] = Supply;

        ConfigurationExtensions.MixingConfig[station] = __instance;

        List<MixingRoute> mixerRoutes = new List<MixingRoute> { new MixingRoute(__instance) };
        if (!ConfigurationExtensions.MixingRoutes.ContainsKey(station))
        {
          ConfigurationExtensions.MixingRoutes[station] = mixerRoutes;
        }
        else
        {
          MelonLogger.Warning($"MixerRoutes already contains key for station={station?.ObjectId.ToString() ?? "null"}. Overwriting.");
          ConfigurationExtensions.MixingRoutes[station] = mixerRoutes;
        }

        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"Registered supply and mixer routes for station: {station?.ObjectId.ToString() ?? "null"}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"MixingStationConfigurationPatch: Failed for station: {station?.ObjectId.ToString() ?? "null"}, error: {e}");
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
        var data = new ExtendedMixingStationConfigurationData(
            __instance.Destination.GetData(),
            __instance.StartThrehold.GetData()
        )
        {
          Supply = ConfigurationExtensions.MixingSupply[__instance].GetData(),
          MixingRoutes = ConfigurationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes)
                ? routes.Select(r => new MixingRouteData(r.Product.GetData(), r.MixerItem.GetData())).ToList()
                : new List<MixingRouteData>()
        };
        __result = data.GetJson(true);
        if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugMixingLogs)
          MelonLogger.Msg($"Saved for configHash={__instance.GetHashCode()}, routesCount={data.MixingRoutes.Count}");
      }
      catch (Exception e)
      {
        MelonLogger.Error($"GetSaveStringPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
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
        ObjectField supply = ConfigurationExtensions.MixingSupply[__instance];
        bool hasRoutes = ConfigurationExtensions.MixingRoutes.TryGetValue(__instance.station, out var routes) &&
                        routes.Any(r => r.Product.SelectedItem != null || r.MixerItem.SelectedItem != null);
        __result |= supply.SelectedObject != null || hasRoutes;
      }
      catch (Exception e)
      {
        MelonLogger.Error($"ShouldSavePatch failed: {e}");
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfigPanel), "Bind")]
  public class MixingStationConfigPanelBindPatch
  {
    static void Postfix(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      try
      {
        if (DebugConfig.EnableDebugLogs)
          MelonLogger.Msg($"Binding configs, count: {configs?.Count ?? 0}");

        if (__instance == null)
        {
          MelonLogger.Error("BindPatch: __instance is null");
          return;
        }

        RectTransform nameLabelRect = __instance.transform.Find("NameLabel")?.GetComponent<RectTransform>();
        if (nameLabelRect != null)
          nameLabelRect.anchoredPosition = new Vector2(nameLabelRect.anchoredPosition.x, nameLabelRect.anchoredPosition.y - 150f);

        RectTransform destRect = __instance.DestinationUI.GetComponent<RectTransform>();
        destRect.anchoredPosition = new Vector2(destRect.anchoredPosition.x, destRect.anchoredPosition.y - 150f);

        GameObject supplyUIObj = UnityEngine.Object.Instantiate(__instance.DestinationUI.gameObject, __instance.transform, false);
        supplyUIObj.name = "SupplyUI";
        ObjectFieldUI supplyUI = supplyUIObj.GetComponent<ObjectFieldUI>();
        RectTransform supplyRect = supplyUIObj.GetComponent<RectTransform>();
        supplyRect.anchoredPosition = new Vector2(supplyRect.anchoredPosition.x, -185.76f - 150f);
        supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>().FirstOrDefault(t => t.gameObject.name == "Title").text = "Supplies";
        supplyUIObj.GetComponentsInChildren<TextMeshProUGUI>().FirstOrDefault(t => t.gameObject.name == "Description")?.gameObject.SetActive(false);

        GameObject routesContainer = new GameObject("RoutesContainer");
        routesContainer.transform.SetParent(__instance.transform, false);
        var layout = routesContainer.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.spacing = 10f;
        RectTransform routesRect = routesContainer.GetComponent<RectTransform>();
        routesRect.anchoredPosition = new Vector2(0, -50f);

        GameObject addButtonObj = new GameObject("AddRouteButton");
        addButtonObj.transform.SetParent(routesContainer.transform, false);
        var addButton = addButtonObj.AddComponent<Button>();
        addButtonObj.AddComponent<Image>().color = new Color(0.8f, 0.8f, 0.8f);
        var addText = new GameObject("Text").AddComponent<TextMeshProUGUI>();
        addText.transform.SetParent(addButtonObj.transform, false);
        addText.text = "Add Route";
        addText.alignment = TextAlignmentOptions.Center;
        addButton.onClick.AddListener(() =>
        {
          foreach (var config in configs.OfType<MixingStationConfiguration>())
          {
            ConfigurationExtensions.MixingRoutes[config.station].Add(new MixingRoute(config));
          }
          BindRoutes(__instance, configs);
        });

        List<ObjectField> supplyList = new();
        foreach (var config in configs.OfType<MixingStationConfiguration>())
        {
          supplyList.Add(ConfigurationExtensions.MixingSupply[config]);
          supplyUI.Bind(supplyList);
          BindRoutes(__instance, configs);
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"BindPatch: Postfix failed, error: {e}");
      }
    }

    private static void BindRoutes(MixingStationConfigPanel __instance, List<EntityConfiguration> configs)
    {
      var routesContainer = __instance.transform.Find("RoutesContainer");
      foreach (Transform child in routesContainer)
      {
        if (child.name != "AddRouteButton")
          UnityEngine.Object.Destroy(child.gameObject);
      }

      foreach (var config in configs.OfType<MixingStationConfiguration>())
      {
        var routes = ConfigurationExtensions.MixingRoutes[config.station];
        for (int i = 0; i < routes.Count; i++)
        {
          var route = routes[i];
          GameObject routeEntry = new GameObject($"Route_{i}");
          routeEntry.transform.SetParent(routesContainer, false);
          var layout = routeEntry.AddComponent<HorizontalLayoutGroup>();
          layout.childAlignment = TextAnchor.MiddleCenter;
          layout.spacing = 10f;

          GameObject productButtonObj = new GameObject("ProductButton");
          productButtonObj.transform.SetParent(routeEntry.transform, false);
          var productButton = productButtonObj.AddComponent<Button>();
          productButtonObj.AddComponent<Image>().color = new Color(0.9f, 0.9f, 0.9f);
          var productText = new GameObject("Text").AddComponent<TextMeshProUGUI>();
          productText.transform.SetParent(productButtonObj.transform, false);
          productText.text = route.Product.SelectedItem?.Name ?? "Select Product";
          productText.alignment = TextAlignmentOptions.Center;
          productButton.onClick.AddListener(() =>
              ProductSelectionScreen.Instance.Open(product =>
              {
                route.Product.SetItem(product, true);
                productText.text = product?.Name ?? "Select Product";
              })
          );

          GameObject mixerItemUIObj = UnityEngine.Object.Instantiate(NoLazyUtilities.GetItemFieldUITemplateFromPotConfigPanel(), routeEntry.transform, false);
          ItemFieldUI mixerItemUI = mixerItemUIObj.GetComponent<ItemFieldUI>();
          mixerItemUI.Bind(new List<ItemField> { route.MixerItem });
        }
      }
    }
  }

  [HarmonyPatch(typeof(MixingStationConfiguration), "Destroy")]
  public class MixingStationConfigurationDestroyPatch
  {
    static void Postfix(MixingStationConfiguration __instance)
    {
      try
      {
        ConfigurationExtensions.MixingSupply.Remove(__instance);
        ConfigurationExtensions.MixingRoutes.Remove(__instance.station);
        foreach (var pair in ConfigurationExtensions.MixingConfig.Where(p => p.Value == __instance).ToList())
        {
          ConfigurationExtensions.MixingConfig.Remove(pair.Key);
        }
      }
      catch (Exception e)
      {
        MelonLogger.Error($"DestroyPatch: Failed for configHash={__instance.GetHashCode()}, error: {e}");
      }
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
      MixerItems = new ItemList("Mixer", NetworkSingleton<ProductManager>.Instance.ValidMixIngredients.ToArray().Select(item => item.ID).ToList(), true, true);
      MixerItems.All = true;
      MixerItems.None = true;
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

    public override void Awake()
    {
      base.Awake();
      MixerItemUI.Button.onClick.AddListener(new UnityAction(MixerItemUIClicked));
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
      MixerItemUI.ValueLabel.text = castedPreset.MixerItems.GetDisplayString();
    }

    private void MixerItemUIClicked()
    {
      Singleton<ItemSetterScreen>.Instance.Open(castedPreset.MixerItems);
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
}