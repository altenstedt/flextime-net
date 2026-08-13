using Flextime;

namespace Test.Flextime;

public class TimeZonesTests
{
    [Theory]
    [InlineData("UTC", "2024-01-10")]
    [InlineData("Asia/Tokyo", "2024-01-11")] // 05:00 the next day
    [InlineData("America/New_York", "2024-01-10")] // 15:00 the same day
    public void DateAtFollowsZone(string zone, string expected)
    {
        var instant = DateTimeOffset.Parse("2024-01-10T20:00:00+00:00");

        Assert.Equal(DateOnly.Parse(expected), TimeZones.DateAt(instant, zone));
    }

    [Fact]
    public void TryGetKnownZone()
    {
        Assert.True(TimeZones.TryGet("Europe/Stockholm", out var zone));
        Assert.Equal("Europe/Stockholm", zone.Id);
    }

    [Fact]
    public void TryGetUnknownZone()
    {
        Assert.False(TimeZones.TryGet("Not/AZone", out _));
    }
}
