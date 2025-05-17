using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json.Linq;
using ScheduleOne.DevUtilities;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Management.UI;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.Product;
using ScheduleOne.UI.Management;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

using static NoLazyWorkers.NoLazyUtilities;
using ScheduleOne.NPCs.Behaviour;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using ScheduleOne.NPCs;
using GameKit.Utilities;
using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Stations.MixingStationUtilities;
using ScheduleOne.Employees;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using static NoLazyWorkers.General.StorageUtilities;
using Unity.Mathematics;

namespace NoLazyWorkers.Stations
{
  public class StationRouteManager
  {
    public Guid StationGuid { get; }
    public MixingStationConfiguration Config { get; }
    public QualityField Quality { get; private set; }
    public List<MixingRoute> Routes { get; } = new List<MixingRoute>();
    public List<ItemInstance> Refills { get; } = new List<ItemInstance>();
    public const int MaxRoutes = 11;
    public const float MaxThreshold = 20f;

    public StationRouteManager(MixingStationConfiguration config, MixingStation station)
    {
      if (station == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error, "StationRouteManager: Constructor failed, station is null", DebugLogger.Category.MixingStation);
        throw new ArgumentNullException(nameof(station));
      }
      StationGuid = station.GUID;
      Config = config;
      if (Config == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"StationRouteManager: Constructor failed for station {station.GUID}, Configuration is null or not MixingStationConfiguration",
            DebugLogger.Category.MixingStation);
        throw new InvalidOperationException("MixingStation Configuration is null or invalid");
      }
      Quality = new QualityField(Config);
      Quality.onValueChanged.AddListener(q =>
      {
        UpdateRefillsQuality(q);
        Config.InvokeChanged();
      });
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"StationRouteManager: Initialized for station {StationGuid}", DebugLogger.Category.MixingStation);
    }

    public void AddRoute()
    {
      if (Routes.Count >= MaxRoutes) return;
      var route = new MixingRoute(Config);
      Routes.Add(route);
      Refills.Add(null);
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Added route for station {StationGuid}, total routes: {Routes.Count}",
          DebugLogger.Category.MixingStation);
    }

    public void RemoveRoute(int index)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes.RemoveAt(index);
      Refills.RemoveAt(index);
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Removed route at index {index} for station {StationGuid}, total routes: {Routes.Count}",
          DebugLogger.Category.MixingStation);
    }

    public void UpdateProduct(int index, ItemDefinition product)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes[index].Product.SelectedItem = product;
      UpdateRefill(index);
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Updated product for route {index} in station {StationGuid}",
          DebugLogger.Category.MixingStation);
    }

    public void UpdateMixer(int index, ItemDefinition mixer)
    {
      if (index < 0 || index >= Routes.Count) return;
      Routes[index].MixerItem.SelectedItem = mixer;
      Config.InvokeChanged();
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Updated mixer for route {index} in station {StationGuid}",
          DebugLogger.Category.MixingStation);
    }

    private void UpdateRefill(int index)
    {
      if (index < 0 || index >= Routes.Count) return;
      var product = Routes[index].Product.SelectedItem;
      if (product == null)
      {
        Refills[index] = null;
        return;
      }
      var prodItem = product.GetDefaultInstance() as ProductItemInstance;
      prodItem?.SetQuality(Quality.Value);
      Refills[index] = prodItem;
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Updated refill for route {index} in station {StationGuid}",
          DebugLogger.Category.MixingStation);
    }

    private void UpdateRefillsQuality(EQuality quality)
    {
      for (int i = 0; i < Routes.Count; i++)
      {
        UpdateRefill(i);
      }
    }

    public JObject Serialize()
    {
      var json = new JObject
      {
        ["Quality"] = Quality.Value.ToString(),
        ["MixingRoutes"] = new JArray(Routes.Select(route => new JObject
        {
          ["Product"] = route.Product?.GetData()?.ItemID,
          ["MixerItem"] = route.MixerItem?.GetData()?.ItemID
        }))
      };
      return json;
    }

    public void Deserialize(JObject json)
    {
      Routes.Clear();
      Refills.Clear();
      if (json["MixingRoutes"] is JArray routesArray)
      {
        foreach (var routeData in routesArray)
        {
          var route = new MixingRoute(Config);
          var data = new MixingRouteData
          {
            Product = new ItemFieldData(routeData["Product"]?.ToString()),
            MixerItem = new ItemFieldData(routeData["MixerItem"]?.ToString())
          };
          route.SetData(data);
          Routes.Add(route);
          Refills.Add(null);
          UpdateRefill(Routes.Count - 1);
        }
      }
      if (json["Quality"]?.ToString() is string qualityStr && Enum.TryParse<EQuality>(qualityStr, out var quality))
      {
        Quality.SetValue(quality, false);
      }
      DebugLogger.Log(DebugLogger.LogLevel.Info,
          $"StationRouteManager: Deserialized {Routes.Count} routes for station {StationGuid}",
          DebugLogger.Category.MixingStation);
    }
  }

  public class RouteListener
  {
    public UnityEvent OnDeleteClicked { get; } = new UnityEvent();
    public UnityEvent OnProductClicked { get; } = new UnityEvent();
    public UnityEvent OnMixerClicked { get; } = new UnityEvent();
  }

  [Serializable]
  public class MixingRoute
  {
    public ItemField Product { get; set; }
    public ItemField MixerItem { get; set; }

    public MixingRoute(MixingStationConfiguration config)
    {
      Product = new ItemField(config)
      {
        CanSelectNone = false,
        Options = ProductManager.FavouritedProducts?.Where(item => item != null).ToList<ItemDefinition>() ?? []
      };
      MixerItem = new ItemField(config)
      {
        CanSelectNone = false,
        Options = NetworkSingleton<ProductManager>.Instance?.ValidMixIngredients?.Where(item => item != null).ToList<ItemDefinition>() ?? []
      };
    }

    public void SetData(MixingRouteData data)
    {
      if (data.Product != null)
        Product.Load(data.Product);
      if (data.MixerItem != null)
        MixerItem.Load(data.MixerItem);
    }
  }

  [Serializable]
  public class MixingRouteData
  {
    public ItemFieldData Product;
    public ItemFieldData MixerItem;

    public MixingRouteData()
    {
      Product = new ItemFieldData("");
      MixerItem = new ItemFieldData("");
    }
  }
}