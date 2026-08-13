using System.Collections.Concurrent;

namespace Flextime;

public static class TimeZones
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> zonesById = new();

    public static TimeZoneInfo Get(string id) =>
        zonesById.GetOrAdd(id, TimeZoneInfo.FindSystemTimeZoneById);

    /// <summary>The wall clock date of an instant in a zone.</summary>
    public static DateOnly DateAt(DateTimeOffset instant, string zoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Get(zoneId)).Date);
}
