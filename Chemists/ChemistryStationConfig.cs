using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.StationFramework;
using ScheduleOne.VoiceOver;

namespace NoLazyWorkers.Chemists
{
  public static class ChemistryStationExtensions
  {
    public static Dictionary<Guid, ObjectField> Supply = [];
    public static Dictionary<Guid, ChemistryStationConfiguration> ChemistryStationConfig = [];
    public static Dictionary<Guid, TransitRoute> SupplyRoute = [];
  }
}