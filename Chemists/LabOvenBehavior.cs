
using FishNet;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.Product;
using static NoLazyWorkers.Chemists.ChemistBehaviour;
using static NoLazyWorkers.General.StorageUtilities;
using static NoLazyWorkers.General.GeneralExtensions;
using static NoLazyWorkers.Chemists.LabOvenExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;

namespace NoLazyWorkers.Chemists
{
  public class LabOvenBehaviour : ChemistBehaviour
  {
    public override IStationAdapter<TStation> GetStation<TStation>(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetStation: Entered for behaviour={behaviour?.Npc?.fullName}, type={behaviour?.GetType().Name}",
          DebugLogger.Category.Chemist, DebugLogger.Category.LabOven);

      if (behaviour is StartLabOvenBehaviour labOvenBehaviour && labOvenBehaviour.targetOven != null)
      {
        if (typeof(TStation) == typeof(LabOven))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"GetStation: Returning LabOvenAdapter for station={labOvenBehaviour.targetOven.GUID}, chemist={behaviour.Npc?.fullName}",
              DebugLogger.Category.Chemist, DebugLogger.Category.LabOven);
          return new LabOvenAdapter(labOvenBehaviour.targetOven) as IStationAdapter<TStation>;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetStation: Type mismatch for {behaviour?.Npc?.fullName}, expected TStation=LabOven, got TStation={typeof(TStation).Name}",
            DebugLogger.Category.Chemist, DebugLogger.Category.LabOven, DebugLogger.Category.Stacktrace);
        return null;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error,
          $"GetStation: Invalid behaviour or null target station for {behaviour?.Npc?.fullName}",
          DebugLogger.Category.Chemist, DebugLogger.Category.LabOven, DebugLogger.Category.Stacktrace);
      return null;
    }
  }
}