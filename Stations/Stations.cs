using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Property;
using ScheduleOne.NPCs;
using UnityEngine;
using ScheduleOne.Employees;

namespace NoLazyWorkers.Stations
{
  public static class Extensions
  {
    public static Dictionary<Property, Dictionary<Guid, IStationAdapter>> IStations = [];
    public static Dictionary<Guid, List<ItemInstance>> StationRefillLists = [];

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