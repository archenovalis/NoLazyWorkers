using ScheduleOne.Management;

namespace NoLazyWorkers.Chemists
{
  public static class CauldronExtensions
  {
    public static Dictionary<Guid, ObjectField> Supply = [];
    public static Dictionary<Guid, CauldronConfiguration> CauldronConfig = [];
    public static Dictionary<Guid, TransitRoute> SupplyRoute = [];
  }
}