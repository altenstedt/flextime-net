using Flextime;

namespace Test.Flextime;

public class MeasurementWithZoneTests
{
    private static MeasurementWithZone Create(string timestamp, string zone)
    {
        var measurement = new Measurement
        {
            Idle = 0,
            Kind = Measurement.Types.Kind.Measurement,
            Timestamp = (uint)DateTimeOffset.Parse(timestamp).ToUnixTimeSeconds()
        };

        return new MeasurementWithZone(measurement, zone, 60);
    }

    [Theory]
    [InlineData("2024-01-15T12:00:00+00:00", "Europe/Stockholm", 1)] // Winter, CET
    [InlineData("2024-07-15T12:00:00+00:00", "Europe/Stockholm", 2)] // Summer, CEST
    [InlineData("2024-01-15T12:00:00+00:00", "Asia/Tokyo", 9)] // No DST
    public void OffsetFollowsMeasurementZoneAtThatInstant(string timestamp, string zone, int expectedOffsetHours)
    {
        var subject = Create(timestamp, zone);

        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), subject.Timestamp.Offset);
    }

    [Theory]
    [InlineData("Europe/Stockholm")]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    public void ConversionPreservesTheInstant(string zone)
    {
        var instant = DateTimeOffset.Parse("2024-06-01T10:30:00+00:00");

        var subject = Create("2024-06-01T10:30:00+00:00", zone);

        Assert.Equal(instant.ToUnixTimeSeconds(), subject.Timestamp.ToUnixTimeSeconds());
    }

    [Fact]
    public void WallClockFollowsMeasurementZoneNotMachineZone()
    {
        // 23:30 UTC on December 31 is already 08:30 on January 1 in Tokyo,
        // regardless of the zone of the machine running this test.
        var subject = Create("2024-12-31T23:30:00+00:00", "Asia/Tokyo");

        Assert.Equal(new DateTime(2025, 1, 1, 8, 30, 0), subject.Timestamp.DateTime);
    }
}
