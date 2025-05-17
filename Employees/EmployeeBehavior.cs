using FishNet;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using System.Collections;
using UnityEngine;
using static NoLazyWorkers.Stations.StationExtensions;
using static NoLazyWorkers.General.StorageUtilities;

using static NoLazyWorkers.Stations.MixingStationExtensions;
using static NoLazyWorkers.Employees.EmployeeExtensions;
using ScheduleOne.Property;
using ScheduleOne.Delivery;
using ScheduleOne.NPCs.Behaviour;
using static NoLazyWorkers.NoLazyUtilities;
using NoLazyWorkers.General;
using NoLazyWorkers.Stations;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using FishNet.Object;
using FishNet.Serializing;
using FishNet.Transporting;
using static FishNet.Object.NetworkBehaviour;
using static NoLazyWorkers.Employees.PackagingStationExtensions;
using Random = UnityEngine.Random;
using FishNet.Managing.Object;
using UnityEngine.InputSystem.EnhancedTouch;
using System.Threading.Tasks;

namespace NoLazyWorkers.Employees
{
  public static class EmployeeExtensions
  {
    // Generic tasks shared by all employees
    public static readonly List<IEmployeeTask> GenericTasks = new()
    {
        new DeliverInventoryTask(40, 0)
    };

    public interface IEmployeeAdapter
    {
      Property AssignedProperty { get; }
      NpcSubType SubType { get; }
      StateData State { get; }
      bool HandleMovementComplete(Employee employee, StateData state);
      bool GetEmployeeBehaviour(NPC npc, out EmployeeBehaviour employeeBehaviour);
      void ResetMovement(StateData state);
    }

    public interface IEmployeeTask
    {
      int Priority { get; }
      int ScanIndex { get; } // For task cycling
      async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
      {
        return false;
      }
      async void Execute(Employee employee, StateData state) { }
    }

    public enum NpcSubType { Chemist, Packager, Botanist, Cleaner, Driver }

    public enum EState
    {
      Idle,
      Working,
      Moving
    }

    public class StateData
    {
      public Employee Employee { get; }
      public EmployeeBehaviour EmployeeBeh { get; private set; }
      public AdvancedMoveItemBehaviour AdvMoveItemBehaviour { get; }
      public EState CurrentState { get; set; } = EState.Idle;
      public IStationAdapter Station { get; set; }
      public ItemInstance TargetItem { get; set; }
      public IEmployeeTask CurrentTask { get; set; }
      public int QuantityInventory { get; set; }
      public int QuantityNeeded { get; set; }
      public int QuantityWanted { get; set; }
      private Dictionary<string, object> _taskData = new();

      public StateData(Employee employee, EmployeeBehaviour beh, AdvancedMoveItemBehaviour advmove)
      {
        Employee = employee ?? throw new ArgumentNullException(nameof(Employee));
        EmployeeBeh = beh ?? throw new ArgumentNullException(nameof(EmployeeBeh));
        AdvMoveItemBehaviour = advmove ?? throw new ArgumentNullException(nameof(AdvMoveItemBehaviour));
        EmployeeBehaviour.States[employee.GUID] = this;
        beh.SetState(this);
      }

      public void Clear()
      {
        _taskData = new();
        QuantityInventory = 0;
        QuantityNeeded = 0;
        QuantityWanted = 0;
        Station = null;
        TargetItem = null;
        CurrentTask = null;
        CurrentState = EState.Idle;
      }

      public void SetValue<T>(string key, T value)
      {
        _taskData[key] = value;
      }

      public bool TryGetValue<T>(string key, out T value)
      {
        if (_taskData.TryGetValue(key, out var obj) && obj is T typedValue)
        {
          value = typedValue;
          return true;
        }
        value = default;
        return false;
      }

      public T TryGetValue<T>(string key)
      {
        if (_taskData.TryGetValue(key, out var obj) && obj is T typedValue)
        {
          return typedValue;
        }
        return default;
      }

      public void RemoveValue<T>(string key)
      {
        _taskData.Remove(key);
      }
    }

    public class TransferRequest
    {
      public ItemInstance Item { get; }
      public int Quantity { get; }
      public ItemSlot InventorySlot { get; }
      public ITransitEntity PickUp { get; }
      public List<ItemSlot> PickupSlots { get; }
      public ITransitEntity DropOff { get; }
      public List<ItemSlot> DropOffSlots { get; }

      public TransferRequest(NPC npc, ItemInstance item, int quantity, ItemSlot inventorySlot, ITransitEntity pickup, List<ItemSlot> pickupSlots,
          ITransitEntity dropOff, List<ItemSlot> dropOffSlots)
      {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Quantity = quantity > 0 ? quantity : throw new ArgumentException("Quantity must be positive", nameof(quantity));
        InventorySlot = inventorySlot ?? throw new ArgumentNullException(nameof(inventorySlot));
        PickUp = pickup;
        DropOff = dropOff ?? throw new ArgumentNullException(nameof(dropOff));
        PickupSlots = pickupSlots != null ? pickupSlots.Where(slot => slot != null && slot.ItemInstance != null && slot.Quantity > 0).ToList() : new List<ItemSlot>();
        DropOffSlots = dropOffSlots != null ? dropOffSlots.Where(slot => slot != null).ToList() : throw new ArgumentNullException(nameof(dropOffSlots));
        if (PickUp == null && PickupSlots.Count == 0)
          throw new ArgumentException("No valid pickup slots for inventory route");
        if (DropOffSlots.Count == 0)
          throw new ArgumentException("No valid delivery slots");
      }
    }

    public struct PrioritizedRoute
    {
      public ItemInstance Item;
      public int Quantity;
      public ItemSlot InventorySlot;
      public ITransitEntity PickUp;
      public List<ItemSlot> PickupSlots;
      public ITransitEntity DropOff;
      public List<ItemSlot> DropoffSlots;
      public int Priority;
      public AdvancedTransitRoute TransitRoute;

      public PrioritizedRoute(TransferRequest request, int priority)
      {
        Item = request.Item ?? throw new ArgumentNullException(nameof(request.Item));
        Quantity = request.Quantity;
        InventorySlot = request.InventorySlot ?? throw new ArgumentNullException(nameof(request.InventorySlot));
        PickUp = request.PickUp;
        PickupSlots = request.PickupSlots;
        DropOff = request.DropOff ?? throw new ArgumentNullException(nameof(request.DropOff));
        DropoffSlots = request.DropOffSlots ?? throw new ArgumentNullException(nameof(request.DropOffSlots));
        Priority = priority;
        TransitRoute = new AdvancedTransitRoute(request.PickUp, request.DropOff);
      }
    }

    public static readonly Dictionary<Type, Func<NPC, Behaviour>> StationTypeToBehaviourMap = new()
        {
            { typeof(MixingStation), npc => (npc as Chemist)?.StartMixingStationBehaviour },
            { typeof(MixingStationMk2), npc => (npc as Chemist)?.StartMixingStationBehaviour },
            { typeof(PackagingStation), npc => (npc as Packager)?.PackagingBehaviour },
        };

    public static Dictionary<Guid, IEmployeeAdapter> EmployeeAdapters = new();
    public static Dictionary<IStationAdapter, Employee> StationAdapterBehaviours = new();
    public static Dictionary<Guid, List<ItemSlot>> ReservedSlots = new();
    public static Dictionary<Property, List<ItemInstance>> NoDropOffCache = new();
    public static Dictionary<Property, Dictionary<ItemInstance, float>> TimedOutItems = new();

    public static void ClearAll()
    {
      StationAdapterBehaviours.Clear();
      EmployeeAdapters.Clear();
      NoDropOffCache.Clear();
      TimedOutItems.Clear();
      PropertyStations.Clear();
      StationAdapters.Clear();
      ReservedSlots.Clear();
      DebugLogger.Log(DebugLogger.LogLevel.Info, "EmployeeExtensions: Cleared all dictionaries", DebugLogger.Category.AllEmployees);
    }
  }

  public static class EmployeeUtilities
  {
    public static bool IsItemTimedOut(Property property, ItemInstance item)
    {
      if (!TimedOutItems.TryGetValue(property, out var timedOutItems))
        return false;
      var key = timedOutItems.Keys.FirstOrDefault(i => i.CanStackWith(item, false));
      if (key == null)
        return false;
      if (Time.time >= timedOutItems[key])
      {
        timedOutItems.Remove(key);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"IsItemTimedOut: Removed expired timeout for item {item.ID}", DebugLogger.Category.Packager);
        return false;
      }
      return true;
    }

    public static void AddItemTimeout(Property property, ItemInstance item)
    {
      if (!TimedOutItems.ContainsKey(property))
        TimedOutItems[property] = new Dictionary<ItemInstance, float>();
      var key = TimedOutItems[property].Keys.FirstOrDefault(i => i.CanStackWith(item, false)) ?? item;
      TimedOutItems[property][key] = Time.time + 30f;
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"AddItemTimeout: Timed out item {item.ID} for 30s in property {property}", DebugLogger.Category.Packager);
    }

    public static void RegisterEmployeeAdapter(NPC npc, IEmployeeAdapter adapter)
    {
      EmployeeAdapters[npc.GUID] = adapter;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Registered adapter for NPC {npc.fullName}, type={adapter.SubType}", DebugLogger.Category.AllEmployees);
    }

    public static IEmployeeAdapter GetEmployee(NPC npc)
    {
      if (EmployeeAdapters.TryGetValue(npc.GUID, out var adapter))
        return adapter;
      DebugLogger.Log(DebugLogger.LogLevel.Error, $"No adapter found for NPC {npc.fullName}", DebugLogger.Category.AllEmployees);
      return null;
    }

    public static void RegisterStationBehaviour(Employee behaviour, IStationAdapter adapter)
    {
      StationAdapterBehaviours[adapter] = behaviour;
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"Registered station adapter for behaviour {behaviour.GetHashCode()}", DebugLogger.Category.AllEmployees);
    }

    public static ITransitEntity FindPackagingStation(IEmployeeAdapter employee, ItemInstance item)
    {
      if (employee == null || item == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindPackagingStation: Employee or item is null", DebugLogger.Category.AllEmployees);
        return null;
      }
      if (!PropertyStations.TryGetValue(employee.AssignedProperty, out var stations) || stations == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"FindPackagingStation: No stations found for property {employee.AssignedProperty}", DebugLogger.Category.AllEmployees);
        return null;
      }
      var suitableStation = stations.FirstOrDefault(s => s is PackagingStationAdapter p && !s.IsInUse && s.CanRefill(item))?.TransitEntity;
      if (suitableStation == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindPackagingStation: No suitable packaging station for item {item.ID} in property {employee.AssignedProperty}", DebugLogger.Category.AllEmployees);
      }
      else
      {
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"FindPackagingStation: Found station {suitableStation.GUID} for item {item.ID}", DebugLogger.Category.AllEmployees);
      }
      return suitableStation;
    }

    public static void SetReservedSlot(Employee employee, ItemSlot slot)
    {
      if (!ReservedSlots.TryGetValue(employee.GUID, out var reserved))
      {
        ReservedSlots[employee.GUID] = new();
        reserved = ReservedSlots[employee.GUID];
      }
      reserved.Add(slot);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"SetReservedSlot: Set slot {slot.SlotIndex} for NPC={employee.fullName}", DebugLogger.Category.Packager);
    }

    public static ItemSlot GetReservedSlot(Employee employee)
    {
      ItemSlot slot = null;
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots))
      {
        slot = slots.FirstOrDefault();
        if (slot != null)
          slots.Remove(slot);
      }
      return slot;
    }

    public static void ReleaseReservations(Employee employee)
    {
      if (ReservedSlots.TryGetValue(employee.GUID, out var slots) && slots != null)
      {
        ReservedSlots.Remove(employee.GUID);
        DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"ReleaseReservations: Released {slots.Count()} slots for employee {employee.fullName}", DebugLogger.Category.Packager);
      }
    }

    public static PrioritizedRoute CreatePrioritizedRoute(TransferRequest request, int priority)
    {
      return new PrioritizedRoute(request, priority);
    }

    public static List<PrioritizedRoute> CreatePrioritizedRoutes(List<TransferRequest> requests, int priority)
    {
      return requests.Select(r => new PrioritizedRoute(r, priority)).ToList();
    }
  }

  public abstract class EmployeeBehaviour
  {
    protected readonly Employee Employee;
    protected readonly IEmployeeAdapter _adapter;
    protected readonly List<IEmployeeTask> _tasks;
    protected StateData State;
    public static readonly Dictionary<Guid, bool> IsFetchingPackaging = new();
    public static Dictionary<Guid, StateData> States = new();
    private readonly float _scanDelay;
    private float _lastScanTime;
    protected readonly List<IEmployeeTask> _allTasks;

    protected EmployeeBehaviour(Employee employee, IEmployeeAdapter adapter, List<IEmployeeTask> tasks)
    {
      Employee = employee ?? throw new ArgumentNullException(nameof(Employee));
      _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
      _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
      _allTasks = GenericTasks.Concat(_tasks).OrderBy(t => t.ScanIndex).ToList();
      _scanDelay = Random.Range(0f, 2f);
      EmployeeUtilities.RegisterEmployeeAdapter(employee, adapter);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour: Initialized AdvancedMoveItemBehaviour for NPC={Employee.fullName}", DebugLogger.Category.Chemist);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour: Initialized for NPC {Employee.fullName}, TaskCount={_tasks.Count}, ScanDelay={_scanDelay}", DebugLogger.Category.EmployeeCore);
    }

    public static StateData GetState(Employee employee)
    {
      return States[employee.GUID];
    }
    public void SetState(StateData state)
    {
      State = state;
    }
    public virtual void Update()
    {
      switch (State.CurrentState)
      {
        case EState.Idle:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmployeeBehaviour.Update: Idle State for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          HandleIdle();
          break;
        case EState.Working:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmployeeBehaviour.Update: Working State for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          HandleWorking();
          break;
        case EState.Moving:
          DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"EmployeeBehaviour.Update: Moving State for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          HandleMoving();
          break;
      }
    }

    protected async virtual void HandleIdle()
    {
      if (Time.time < _lastScanTime + _scanDelay)
        return;
      _lastScanTime = Time.time;
      int lastScanIndex = State.TryGetValue<int>("LastScanIndex", out var index) ? index : -1;
      int startIndex = (lastScanIndex + 1) % _allTasks.Count;
      for (int i = 0; i < _allTasks.Count; i++)
      {
        int currentIndex = (startIndex + i) % _allTasks.Count;
        var task = _allTasks[currentIndex];
        if (await task.CanExecute(Employee))
        {
          State.CurrentTask = task;
          State.CurrentState = EState.Working;
          State.SetValue("LastScanIndex", task.ScanIndex);
          Employee.MarkIsWorking();
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleIdle: Selected task {task.GetType().Name} (Index={task.ScanIndex}) for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          return;
        }
      }
      Employee.SetIdle(true);
      DebugLogger.Log(DebugLogger.LogLevel.Verbose, $"HandleIdle: No tasks available for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
    }

    protected virtual void HandleWorking()
    {
      if (State.CurrentTask != null)
      {
        State.CurrentTask.Execute(Employee, State);
        if (State.CurrentState != EState.Working)
        {
          State.CurrentTask = null;
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"HandleWorking: Task completed or interrupted for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
        }
      }
      else
      {
        State.CurrentState = EState.Idle;
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"HandleWorking: No task to execute for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
      }
    }

    protected virtual void HandleMoving()
    {
      if (!State.TryGetValue<float>("MoveDelay", out var moveDelay))
        moveDelay = 0f;
      if (moveDelay > 0)
      {
        State.SetValue("MoveDelay", moveDelay - Time.deltaTime);
        return;
      }

      if (Employee.Movement.IsMoving)
      {
        State.SetValue("MoveElapsed", State.TryGetValue<float>("MoveElapsed", out var elapsed) ? elapsed + Time.deltaTime : Time.deltaTime);
        if (State.TryGetValue<float>("MoveElapsed", out var elapsedTime) && elapsedTime >= 30f)
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmployeeBehaviour.HandleMoving: Movement timeout for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          ResetMovement(State);
          return;
        }
        return;
      }

      if (!State.TryGetValue<ITransitEntity>("DropOff", out var dropOff) || !NavMeshUtility.IsAtTransitEntity(dropOff, Employee, 0.4f))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmployeeBehaviour.HandleMoving: Not at dropoff {dropOff?.GUID} for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
        ResetMovement(State);
        return;
      }

      if (!EmployeeAdapters.TryGetValue(Employee.GUID, out var adapter) || !adapter.HandleMovementComplete(Employee, State))
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmployeeBehaviour.HandleMoving: Adapter failed for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
        ResetMovement(State);
        return;
      }

      // Check if all routes are complete
      if (State.TryGetValue<List<PrioritizedRoute>>("ActiveRoutes", out var routes) && routes.Any())
      {
        // Routes are processed by AdvancedMoveItemBehaviour; assume completion when movement is done
        if (State.TryGetValue<Action<Employee, StateData>>("MoveCallback", out var callback))
        {
          State.CurrentState = EState.Working;
          State.RemoveValue<ITransitEntity>("DropOff");
          State.RemoveValue<float>("MoveElapsed");
          State.RemoveValue<float>("MoveDelay");
          State.RemoveValue<Action<Employee, StateData>>("MoveCallback");
          State.RemoveValue<List<PrioritizedRoute>>("ActiveRoutes");
          DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour.HandleMoving: Movement and routes completed, resuming task for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          callback(Employee, State);
        }
        else
        {
          DebugLogger.Log(DebugLogger.LogLevel.Warning, $"EmployeeBehaviour.HandleMoving: No callback for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
          ResetMovement(State);
        }
      }
    }

    public void StartMovement(List<PrioritizedRoute> routes, Action<Employee, StateData> callback)
    {
      if (routes != null && routes.Any())
      {
        State.AdvMoveItemBehaviour.Initialize(routes, State, callback);
        State.SetValue("ActiveRoutes", routes);
      }

      State.CurrentState = EState.Moving;
      State.SetValue("MoveElapsed", 0f);
      State.SetValue("MoveDelay", 0.5f);
      State.SetValue("MoveCallback", callback);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour.StartMovement: Started movement with {routes?.Count ?? 0} routes for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
    }

    private void ResetMovement(StateData state)
    {
      State.CurrentState = EState.Idle;
      State.RemoveValue<ITransitEntity>("DropOff");
      State.RemoveValue<float>("MoveElapsed");
      State.RemoveValue<float>("MoveDelay");
      State.RemoveValue<Action<Employee, StateData>>("MoveCallback");
      _adapter.ResetMovement(State);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"EmployeeBehaviour.ResetMovement: Movement reset", DebugLogger.Category.EmployeeCore);
    }

    public virtual void Disable()
    {
      State.CurrentTask = null;
      State.Station = null;
      State.TargetItem = null;
      State.CurrentState = EState.Idle;
      Employee.ShouldIdle();
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"Disable: Behaviour disabled for {Employee.fullName}", DebugLogger.Category.EmployeeCore);
    }
  }

  public class DeliverInventoryTask : IEmployeeTask
  {
    private readonly int _priority;
    private readonly int _scanIndex;
    public int Priority => _priority;
    public int ScanIndex => _scanIndex;

    public DeliverInventoryTask(int priority, int scanIndex)
    {
      _priority = priority;
      _scanIndex = scanIndex;
    }

    public async Task<bool> CanExecute(Employee employee, ITransitEntity recheck = null)
    {
      if (employee.Inventory.ItemSlots.Any(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked))
      {
        await Execute(employee, EmployeeBehaviour.GetState(employee));
        return true;
      }
      return false;
    }

    public async Task Execute(Employee employee, StateData state)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryTask: Executing for {employee.fullName}", DebugLogger.Category.EmployeeCore);

      // Find a valid inventory slot
      var slot = employee.Inventory.ItemSlots.FirstOrDefault(s => s?.ItemInstance != null && s.Quantity > 0 && !s.IsLocked);
      if (slot == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverInventoryTask: No valid inventory slot for {employee.fullName}", DebugLogger.Category.EmployeeCore);
        ResetTask(state);
        return;
      }

      // Find a suitable shelf or station
      var shelf = await FindShelfForDeliveryAsync(employee, slot.ItemInstance, allowAnyShelves: true);
      if (shelf == null)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryTask: No shelf found for item {slot.ItemInstance.ID}", DebugLogger.Category.EmployeeCore);
        EmployeeUtilities.AddItemTimeout(employee.AssignedProperty, slot.ItemInstance);
        ResetTask(state);
        return;
      }

      // Reserve delivery slots
      var deliverySlots = (shelf as ITransitEntity).ReserveInputSlotsForItem(slot.ItemInstance, employee.NetworkObject);
      if (deliverySlots == null || deliverySlots.Count == 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryTask: No delivery slots for item {slot.ItemInstance.ID} on shelf {shelf.GUID}", DebugLogger.Category.EmployeeCore);
        ResetTask(state);
        return;
      }

      // Calculate quantity to deliver
      int quantity = Math.Min(slot.Quantity, deliverySlots.Sum(s => s.GetCapacityForItem(slot.ItemInstance)));
      if (quantity <= 0)
      {
        DebugLogger.Log(DebugLogger.LogLevel.Warning, $"DeliverInventoryTask: Invalid quantity for delivery for {employee.fullName}", DebugLogger.Category.EmployeeCore);
        ResetTask(state);
        return;
      }

      // Create transfer request and start movement
      var request = new TransferRequest(employee, slot.ItemInstance, quantity, slot, null, new List<ItemSlot> { slot }, shelf, deliverySlots);
      var route = new PrioritizedRoute(request, Priority);

      state.EmployeeBeh.StartMovement(new List<PrioritizedRoute> { route }, (emp, s) =>
      {
        DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryTask: Delivery completed for {emp.fullName}", DebugLogger.Category.EmployeeCore);
        state.EmployeeBeh.Disable();
      });

      state.SetValue("DropOff", shelf);
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryTask: Planned delivery of {quantity} {slot.ItemInstance.ID} to shelf {shelf.GUID} for {employee.fullName}", DebugLogger.Category.EmployeeCore);
    }

    private void ResetTask(StateData state)
    {
      state.CurrentTask = null;
      state.CurrentState = EState.Idle;
      state.RemoveValue<ITransitEntity>("DropOff");
      DebugLogger.Log(DebugLogger.LogLevel.Info, $"DeliverInventoryTask: Task reset for {state.Employee.fullName}", DebugLogger.Category.EmployeeCore);
    }
  }
}