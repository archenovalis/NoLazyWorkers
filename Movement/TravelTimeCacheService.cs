using ScheduleOne.Management;
using static NoLazyWorkers.Extensions.FishNetExtensions;
using static NoLazyWorkers.Debug;
using ScheduleOne.Property;

namespace NoLazyWorkers.Movement
{
  public class TravelTimeCacheService
  {
    private static readonly Dictionary<Property, TravelTimeCacheService> _services = new();
    private readonly Dictionary<(Guid, Guid), float> _travelTimeCache = new();
    private readonly Dictionary<Guid, (Guid, double)> _activeTimings = new();

    public static TravelTimeCacheService GetOrCreateService(Property property)
    {
      if (!_services.TryGetValue(property, out var service))
      {
        service = new TravelTimeCacheService();
        _services[property] = service;
        Log(Level.Info, $"TravelTimeCacheService: Created for property {property.name}", Category.Movement);
      }
      return service;
    }

    public float GetTravelTime(ITransitEntity source, ITransitEntity destination)
    {
      return _travelTimeCache.TryGetValue((source?.GUID ?? Guid.Empty, destination.GUID), out var time) ? time : float.MaxValue;
    }

    public void StartTiming(Guid sourceGuid)
    {
      _activeTimings[sourceGuid] = (Guid.Empty, TimeManagerInstance.Tick);
      Log(Level.Verbose, $"TravelTimeCacheService: Started timing for source {sourceGuid}", Category.Movement);
    }

    public float StopTiming(Guid destGuid)
    {
      var sourceEntry = _activeTimings.FirstOrDefault(kvp => kvp.Value.Item1 == Guid.Empty);
      if (sourceEntry.Key == Guid.Empty) return 0f;
      var (sourceGuid, startTick) = (sourceEntry.Key, sourceEntry.Value.Item2);
      float travelTime = (float)((TimeManagerInstance.Tick - startTick) / TimeManagerInstance.TickRate);
      _activeTimings.Remove(sourceGuid);
      Log(Level.Verbose, $"TravelTimeCacheService: Stopped timing for source {sourceGuid} to dest {destGuid}, time {travelTime:F2}s", Category.Movement);
      return travelTime;
    }

    public void ClearTiming()
    {
      _activeTimings.Clear();
      Log(Level.Verbose, $"TravelTimeCacheService: Cleared all active timings", Category.Movement);
    }

    public void UpdateTravelTimeCache(Guid sourceGuid, Guid destGuid, float travelTime)
    {
      _travelTimeCache[(sourceGuid, destGuid)] = travelTime;
      Log(Level.Info, $"TravelTimeCacheService: Updated cache for {sourceGuid} to {destGuid} with time {travelTime:F2}s", Category.Movement);
    }
  }
}