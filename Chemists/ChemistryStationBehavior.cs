
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
using static NoLazyWorkers.Chemists.ChemistryStationExtensions;
using Behaviour = ScheduleOne.NPCs.Behaviour.Behaviour;
using static NoLazyWorkers.Chemists.LabOvenExtensions;

namespace NoLazyWorkers.Chemists
{
  public class ChemistryStationBehaviour : ChemistBehaviour
  {
    public override IStationAdapter<TStation> GetStation<TStation>(Behaviour behaviour)
    {
      DebugLogger.Log(DebugLogger.LogLevel.Verbose,
          $"GetStation: Entered for behaviour={behaviour?.Npc?.fullName}, type={behaviour?.GetType().Name}",
          DebugLogger.Category.Chemist, DebugLogger.Category.ChemistryStation);

      if (behaviour is StartChemistryStationBehaviour stationBehaviour && stationBehaviour.targetStation != null)
      {
        if (typeof(TStation) == typeof(ChemistryStation))
        {
          DebugLogger.Log(DebugLogger.LogLevel.Info,
              $"GetStation: Returning ChemistryStationAdapter for station={stationBehaviour.targetStation.GUID}, chemist={behaviour.Npc?.fullName}",
              DebugLogger.Category.Chemist, DebugLogger.Category.ChemistryStation);
          return new ChemistryStationAdapter(stationBehaviour.targetStation) as IStationAdapter<TStation>;
        }
        DebugLogger.Log(DebugLogger.LogLevel.Error,
            $"GetStation: Type mismatch for {behaviour?.Npc?.fullName}, expected TStation=ChemistryStation, got TStation={typeof(TStation).Name}",
            DebugLogger.Category.Chemist, DebugLogger.Category.ChemistryStation, DebugLogger.Category.Stacktrace);
        return null;
      }
      DebugLogger.Log(DebugLogger.LogLevel.Error,
          $"GetStation: Invalid behaviour or null target station for {behaviour?.Npc?.fullName}",
          DebugLogger.Category.Chemist, DebugLogger.Category.ChemistryStation, DebugLogger.Category.Stacktrace);
      return null;
    }
  }
}