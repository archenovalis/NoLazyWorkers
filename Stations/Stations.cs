using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Property;
using ScheduleOne.NPCs;
using UnityEngine;
using ScheduleOne.Employees;
using static NoLazyWorkers.Storage.Extensions;
using NoLazyWorkers.Storage;

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
      List<ItemInstance> RefillList();
      bool CanRefill(ItemInstance item);
      Type TypeOf { get; }
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
        // Initialize Station in CacheManager
        var cacheManager = CacheService.GetOrCreateCacheManager(adapter.ParentProperty);
        var storageKey = new StorageKey { Guid = adapter.GUID, Type = StorageType.Station };
        foreach (var slot in adapter.InsertSlots)
          cacheManager.RegisterItemSlot(slot, storageKey);
        foreach (var slot in adapter.ProductSlots)
          cacheManager.RegisterItemSlot(slot, storageKey);
        cacheManager.RegisterItemSlot(adapter.OutputSlot, storageKey);
        var slotData = new List<SlotData>();
        for (int i = 0; i < adapter.InsertSlots.Count; i++)
          slotData.Add(new SlotData
          {
            Item = adapter.InsertSlots[i].ItemInstance != null ? new ItemData(adapter.InsertSlots[i].ItemInstance) : ItemData.Empty,
            Quantity = adapter.InsertSlots[i].Quantity,
            SlotIndex = adapter.InsertSlots[i].SlotIndex,
            StackLimit = adapter.InsertSlots[i].ItemInstance != null ? adapter.InsertSlots[i].GetCapacityForItem(adapter.InsertSlots[i].ItemInstance) : -1,
            IsValid = true
          });
        for (int i = 0; i < adapter.ProductSlots.Count; i++)
          slotData.Add(new SlotData
          {
            Item = adapter.ProductSlots[i].ItemInstance != null ? new ItemData(adapter.ProductSlots[i].ItemInstance) : ItemData.Empty,
            Quantity = adapter.ProductSlots[i].Quantity,
            SlotIndex = adapter.ProductSlots[i].SlotIndex,
            StackLimit = adapter.ProductSlots[i].ItemInstance != null ? adapter.ProductSlots[i].GetCapacityForItem(adapter.ProductSlots[i].ItemInstance) : -1,
            IsValid = true
          });
        slotData.Add(new SlotData
        {
          Item = adapter.OutputSlot.ItemInstance != null ? new ItemData(adapter.OutputSlot.ItemInstance) : ItemData.Empty,
          Quantity = adapter.OutputSlot.Quantity,
          SlotIndex = adapter.OutputSlot.SlotIndex,
          StackLimit = adapter.OutputSlot.ItemInstance != null ? adapter.OutputSlot.GetCapacityForItem(adapter.OutputSlot.ItemInstance) : -1,
          IsValid = true
        });
        cacheManager.QueueSlotUpdate(storageKey, slotData);
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