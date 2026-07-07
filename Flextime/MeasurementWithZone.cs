using System.Collections.Concurrent;

namespace Flextime;

public record MeasurementWithZone(Measurement Measurement, string Zone, uint Interval)
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> zonesById = new();

    public DateTimeOffset Timestamp { get; } =
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(Measurement.Timestamp),
            zonesById.GetOrAdd(Zone, TimeZoneInfo.FindSystemTimeZoneById));
}