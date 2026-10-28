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

    [Theory]
    // The layout macOS uses now.
    [InlineData(null, "/var/db/timezone/zoneinfo/Europe/Stockholm", true)]
    [InlineData("", "/var/db/timezone/zoneinfo/America/Phoenix", true)]
    // A TZDIR someone set is theirs.
    [InlineData("/usr/share/zoneinfo", "/var/db/timezone/zoneinfo/Europe/Stockholm", false)]
    // Any other layout is left to .NET.
    [InlineData(null, "/usr/share/zoneinfo/Europe/Stockholm", false)]
    [InlineData(null, "/var/db/timezone/tz/2026e.1.0/zoneinfo/Europe/Stockholm", false)]
    [InlineData(null, "/var/db/timezone/zoneinfo", false)]
    [InlineData(null, "/var/db/timezone/zoneinfo-old/Europe/Stockholm", false)]
    [InlineData(null, "../var/db/timezone/zoneinfo/Europe/Stockholm", false)]
    // Not a link at all.
    [InlineData(null, null, false)]
    public void Mac_zone_folder_is_named_only_for_its_own_links(string? tzdir, string? target, bool expected)
    {
        Assert.Equal(expected, TimeZones.PointsIntoMacZones(tzdir, target));
    }
}
