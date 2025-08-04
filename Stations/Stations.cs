using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Property;
using ScheduleOne.NPCs;
using UnityEngine;
using ScheduleOne.Employees;
using static NoLazyWorkers.CacheManager.Extensions;
using NoLazyWorkers.CacheManager;
using static NoLazyWorkers.TaskService.Extensions;
using Unity.Collections;

namespace NoLazyWorkers.Stations
{
  public static class Extensions
  {
    public interface IStationAdapter
    {
      Guid GUID { get; }
      string Name { get; }
      Vector3 GetAccessPoint(NPC npc);
      List<ItemSlot> InsertSlots { get; }
      List<ItemSlot> ProductSlots { get; }
      ItemSlot OutputSlot { get; }
      bool IsInUse { get; }
      bool HasActiveOperation { get; }
      int StartThreshold { get; }
      void StartOperation(Employee employee);
      List<ItemField> GetInputItemForProduct();
      int MaxProductQuantity { get; }
      ITransitEntity TransitEntity { get; }
      BuildableItem Buildable { get; }
      Property ParentProperty { get; }
      NativeList<ItemKey> RefillList();
      StationData StationData { get; }
      bool CanRefill(ItemInstance item);
      EntityType EntityType { get; }
      IStationState StationState { get; set; }
    }

    public interface IStationState
    {
      Enum State { get; set; } // Non-generic enum access
      float LastValidatedTime { get; set; }
      bool IsValid(float currentTime);
      void SetData<T>(string key, T value);
      T GetData<T>(string key, T defaultValue = default);
    }

    public class StationState<TStates> : IStationState where TStates : Enum
    {
      public StationState(IStationAdapter adapter)
      {
        // Initialize Station in CacheService
        var cacheService = CacheService.GetOrCreateService(adapter.ParentProperty);
        cacheService.StationDataCache.Add(adapter.GUID, new StationData(adapter));
        foreach (var slot in adapter.InsertSlots)
          cacheService.RegisterItemSlot(slot, adapter.GUID);
        foreach (var slot in adapter.ProductSlots)
          cacheService.RegisterItemSlot(slot, adapter.GUID);
        cacheService.RegisterItemSlot(adapter.OutputSlot, adapter.GUID);
        List<ItemSlot> itemSlots = [.. adapter.InsertSlots, .. adapter.ProductSlots, .. new[] { adapter.OutputSlot }];
        CacheManager.CacheManager.UpdateStorageCache(adapter.ParentProperty, adapter.GUID, itemSlots, StorageType.Station);
      }

      public TStates State { get; set; } // Type-safe state
      public float LastValidatedTime { get; set; }
      public Dictionary<string, object> StateData { get; } = new();

      Enum IStationState.State
      {
        get => State;
        set => State = (TStates)value;
      }

      public bool IsValid(float currentTime) => currentTime < LastValidatedTime + 5f;

      public void SetData<T>(string key, T value) => StateData[key] = value;
      public T GetData<T>(string key, T defaultValue = default) =>
          StateData.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;
    }
  }
}