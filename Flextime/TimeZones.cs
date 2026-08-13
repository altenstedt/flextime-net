using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Flextime;

public static class TimeZones
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> zonesById = new();

    public static TimeZoneInfo Get(string id) =>
        zonesById.GetOrAdd(id, TimeZoneInfo.FindSystemTimeZoneById);

    public static bool TryGet(string id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        if (zonesById.TryGetValue(id, out zone))
        {
            return true;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone))
        {
            zonesById.TryAdd(id, zone);

            return true;
        }

        return false;
    }

    /// <summary>The wall clock date of an instant in a zone.</summary>
    public static DateOnly DateAt(DateTimeOffset instant, string zoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Get(zoneId)).Date);
}
