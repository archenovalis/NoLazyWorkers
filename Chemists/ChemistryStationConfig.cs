using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;

namespace NoLazyWorkers_IL2CPP.Chemists
{
  public static class ChemistryStationExtensions
  {
    public static Dictionary<Guid, ObjectField> Supply = [];
    public static Dictionary<Guid, ChemistryStationConfiguration> ChemistryStationConfig = [];
    public static Dictionary<Guid, TransitRoute> SupplyRoute = [];
  }
}