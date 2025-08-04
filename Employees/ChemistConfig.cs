using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Property;
using UnityEngine;
using static NoLazyWorkers.Employees.Extensions;
using NoLazyWorkers.CacheManager;
using static NoLazyWorkers.Debug;
using static NoLazyWorkers.CacheManager.ManagedDictionaries;
using static NoLazyWorkers.TaskService.Extensions;
using NoLazyWorkers.SmartExecution;
using NoLazyWorkers.Extensions;
using ScheduleOne.DevUtilities;
using ScheduleOne.GameTime;

namespace NoLazyWorkers.Employees
{
  public class ChemistBehaviour : AdvEmployeeBehaviour
  {
    protected readonly Chemist _chemist;

    public ChemistBehaviour(Chemist chemist, IEmployeeAdapter adapter)
        : base(chemist, adapter)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      Log(Level.Info, $"ChemistBehaviour: Initialized for NPC {_chemist.fullName}", Category.Chemist);
    }

    protected override bool CheckIdleConditions()
    {
      bool noWork = false;
      bool needsPay = false;

      if (_chemist.Fired || (_chemist.behaviour.activeBehaviour != null && _chemist.behaviour.activeBehaviour != _chemist.WaitOutside))
      {
        Log(Level.Verbose, $"CheckIdleConditions: Fired={_chemist.Fired} or activeBehaviour={_chemist.behaviour.activeBehaviour?.Name ?? "null"} for NPC={_chemist.fullName}", Category.Chemist);
        noWork = true;
      }
      else if (_chemist.GetHome() == null)
      {
        Log(Level.Verbose, $"CheckIdleConditions: No bed assigned for NPC={_chemist.fullName}", Category.Chemist);
        noWork = true;
        _chemist.SubmitNoWorkReason("I haven't been assigned a bed", "You can use your management clipboard to assign me a bed.");
      }
      else if (NetworkSingleton<TimeManager>.Instance.IsEndOfDay)
      {
        Log(Level.Verbose, $"CheckIdleConditions: End of day for NPC={_chemist.fullName}", Category.Chemist);
        noWork = true;
        _chemist.SubmitNoWorkReason("Sorry boss, my shift ends at 4AM.", string.Empty);
      }
      else if (!_chemist.PaidForToday)
      {
        Log(Level.Verbose, $"CheckIdleConditions: Not paid for NPC={_chemist.fullName}", Category.Chemist);
        if (_chemist.IsPayAvailable())
        {
          needsPay = true;
        }
        else
        {
          noWork = true;
          _chemist.SubmitNoWorkReason("I haven't been paid yet", "You can place cash in my briefcase on my bed.");
        }
      }

      if (noWork)
      {
        _chemist.SetWaitOutside(true);
        Info.CurrentAction = EmployeeAction.Idle;
      }
      else if (FishNetExtensions.IsServer && needsPay && _chemist.IsPayAvailable())
      {
        var options = new SmartOptions { IsTaskSafe = true };
        var outputs = new List<int>();
        Action<int, int, int[], List<int>> processPayment = (start, count, inputs, outList) =>
        {
          _chemist.RemoveDailyWage();
          _chemist.SetIsPaid();
          Log(Level.Verbose, $"CheckIdleConditions: Processed payment for NPC={_chemist.fullName}", Category.Chemist);
          outList.Add(1);
        };
        Smart.Execute("ProcessPayment", 1, processPayment, outputs: outputs, options: options);
      }

      return noWork;
    }
  }

  [HarmonyPatch(typeof(Chemist))]
  public class ChemistPatch
  {
    [HarmonyPrefix]
    [HarmonyPatch("UpdateBehaviour")]
    public static bool UpdateBehaviourPrefix(Chemist __instance)
    {
      try
      {
        Log(Level.Verbose, $"UpdateBehaviourPrefix: NPC={__instance.fullName} at position={__instance.transform.position}", Category.Chemist);

        if (__instance == null)
        {
          Log(Level.Error, "UpdateBehaviourPrefix: Chemist instance is null", Category.Chemist);
          return false;
        }

        if (InitializedEmployees.ContainsKey(__instance.GUID))
        {
          return false;
        }

        var cacheService = CacheService.GetOrCreateService(__instance.AssignedProperty);
        var options = new SmartOptions { IsTaskSafe = true };
        var outputs = new List<int>();
        Action<int, int, int[], List<int>> registerAdapter = (start, count, inputs, outList) =>
        {
          if (!cacheService.IEmployees.TryGetValue(__instance.GUID, out var employeeAdapter))
          {
            if (PendingAdapters.TryGetValue(__instance.GUID, out float requestTime))
            {
              float elapsed = Time.time - requestTime;
              if (elapsed < Constants.ADAPTER_DELAY_SECONDS)
              {
                Log(Level.Verbose, $"UpdateBehaviourPrefix: Delaying adapter for NPC={__instance.fullName}, {Constants.ADAPTER_DELAY_SECONDS - elapsed:F2}s remaining", Category.Chemist);
                return;
              }

              employeeAdapter = new ChemistAdapter(__instance);
              cacheService.IEmployees[__instance.GUID] = employeeAdapter;
              PendingAdapters.Remove(__instance.GUID);
              Log(Level.Info, $"UpdateBehaviourPrefix: Registered ChemistAdapter for NPC={__instance.fullName} after {elapsed:F2}s delay", Category.Chemist);
            }
            else
            {
              PendingAdapters[__instance.GUID] = Time.time;
              Log(Level.Info, $"UpdateBehaviourPrefix: Initiated {Constants.ADAPTER_DELAY_SECONDS}s delay for NPC={__instance.fullName}", Category.Chemist);
              return;
            }
          }
          outList.Add(1);
        };
        Smart.Execute("RegisterAdapter", 1, registerAdapter, outputs: outputs, options: options);

        if (!cacheService.IEmployees.TryGetValue(__instance.GUID, out var adapter))
        {
          return false;
        }

        InitializedEmployees[__instance.GUID] = true;
        var behaviour = adapter.AdvBehaviour as ChemistBehaviour;
        behaviour?.InitializeEmployee();
        var updater = __instance.gameObject.AddComponent<AdvEmployeeUpdater>();
        updater.Setup(behaviour);
        Utilities.CreateAdvMoveItemBehaviour(__instance);
        return false;
      }
      catch (Exception e)
      {
        Log(Level.Error, $"UpdateBehaviourPrefix: Failed for chemist {__instance?.fullName ?? "null"}, error: {e}", Category.Chemist);
        if (__instance != null)
          __instance.SetIdle(true);
        return false;
      }
    }

    [HarmonyPostfix]
    [HarmonyPatch("Fire")]
    public static void FirePostfix(Chemist __instance)
    {
      try
      {
        Log(Level.Info, $"ChemistFirePatch: Disabled MixingStationBeh for NPC={__instance.fullName}", Category.Chemist);
        var cacheService = CacheService.GetOrCreateService(__instance.AssignedProperty);
        if (cacheService.IEmployees.TryGetValue(__instance.GUID, out var adapter))
        {
          adapter.AdvBehaviour.Disable();
          cacheService.IEmployees.Remove(__instance.GUID);
          Log(Level.Info, $"ChemistFirePatch: Removed ChemistAdapter for NPC={__instance.fullName}", Category.Chemist);
        }
      }
      catch (Exception e)
      {
        Log(Level.Error, $"ChemistFirePatch: Failed for Chemist {__instance?.fullName ?? "null"}, error: {e}", Category.Chemist);
      }
    }
  }

  public class ChemistAdapter : IEmployeeAdapter
  {
    private readonly Chemist _chemist;
    private readonly AdvEmployeeBehaviour _employeeBehaviour;
    public Guid Guid => _chemist.GUID;
    public AdvEmployeeBehaviour AdvBehaviour => _employeeBehaviour;
    public Property AssignedProperty => _chemist.AssignedProperty;
    public EntityType EntityType => EntityType.Chemist;
    public List<ItemSlot> InventorySlots => _chemist.Inventory.ItemSlots;

    public ChemistAdapter(Chemist chemist)
    {
      _chemist = chemist ?? throw new ArgumentNullException(nameof(chemist));
      _employeeBehaviour = new ChemistBehaviour(chemist, this);
      Log(Level.Info,
          $"ChemistAdapter: Initialized for NPC {chemist.fullName}",
          Category.Chemist);
    }
  }
}
