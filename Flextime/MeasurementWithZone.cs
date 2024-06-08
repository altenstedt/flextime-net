namespace Flextime;

public record MeasurementWithZone(Measurement Measurement, string Zone, uint Interval)
{
    public DateTimeOffset Timestamp { get; } =
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTimeOffset.FromUnixTimeSeconds(Measurement.Timestamp).DateTime,
            TimeZoneInfo.FindSystemTimeZoneById(Zone));
}