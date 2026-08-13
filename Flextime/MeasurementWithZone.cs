namespace Flextime;

public record MeasurementWithZone(Measurement Measurement, string Zone, uint Interval)
{
    public DateTimeOffset Timestamp { get; } =
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(Measurement.Timestamp),
            TimeZones.Get(Zone));
}
