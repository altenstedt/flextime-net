using Flextime;

namespace Test.Flextime;

public class DurationParserTests
{
    // A Friday, so the week keywords have somewhere to reach back to.
    private static readonly DateTimeOffset Now =
        new(2026, 8, 21, 9, 30, 0, TimeSpan.FromHours(2));

    [Theory]
    // .NET TimeSpan, the spelling this option always took.
    [InlineData("3", 3, 0, 0)]
    [InlineData("3.00:00:00", 3, 0, 0)]
    [InlineData("12:00:00", 0, 12, 0)]
    [InlineData("36:00:00", 36, 0, 0)] // d:hh:mm, as .NET has always read it
    // Compact units, as Go and Prometheus write them.
    [InlineData("3d", 3, 0, 0)]
    [InlineData("2w", 14, 0, 0)]
    [InlineData("1h30m", 0, 1, 30)]
    [InlineData("1d12h", 1, 12, 0)]
    [InlineData("90m", 0, 1, 30)]
    [InlineData("1.5d", 1, 12, 0)]
    // Words, as GNU date and git accept them.
    [InlineData("3 days", 3, 0, 0)]
    [InlineData("3 days ago", 3, 0, 0)]
    [InlineData("2 weeks ago", 14, 0, 0)]
    [InlineData("90 minutes", 0, 1, 30)]
    [InlineData("1 hour", 0, 1, 0)]
    [InlineData("45 mins", 0, 0, 45)]
    [InlineData("3 DAYS", 3, 0, 0)]
    [InlineData("  3d  ", 3, 0, 0)]
    // ISO 8601 durations.
    [InlineData("P3D", 3, 0, 0)]
    [InlineData("P2W", 14, 0, 0)]
    [InlineData("PT90M", 0, 1, 30)]
    [InlineData("P1DT12H", 1, 12, 0)]
    [InlineData("PT1H30M", 0, 1, 30)]
    public void ReadsALength(string text, int days, int hours, int minutes)
    {
        Assert.True(DurationParser.TryParse(text, Now, out var value));
        Assert.Equal(new TimeSpan(days, hours, minutes, 0), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3 dayz")]
    [InlineData("yesteryear")]
    [InlineData("last fortnight")]
    [InlineData("3 months")]  // No fixed length.
    [InlineData("P1M")]       // Same, and M would fight minutes.
    [InlineData("P1Y")]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("d3")]
    [InlineData("3 4 days")]
    [InlineData("1,5d")]      // Decimal comma only inside ISO durations.
    [InlineData("ago")]
    public void RefusesWhatItCannotRead(string? text)
    {
        Assert.False(DurationParser.TryParse(text, Now, out _));
    }

    [Theory]
    // The cutoff lands on the last moment of the day before the one named,
    // because readers keep days strictly after the cutoff date.
    [InlineData("today", "2026-08-20")]
    [InlineData("yesterday", "2026-08-19")]
    [InlineData("this week", "2026-08-16")]  // Week starts Monday the 17th.
    [InlineData("last week", "2026-08-09")]  // Week starts Monday the 10th.
    [InlineData("Yesterday", "2026-08-19")]
    public void KeywordsKeepTheirDayWhole(string text, string cutoff)
    {
        Assert.True(DurationParser.TryParse(text, Now, out var value));

        var at = Now - value;

        Assert.Equal(DateOnly.Parse(cutoff), DateOnly.FromDateTime(at.Date));
    }

    [Fact]
    public void KeywordsAreRelativeToTheInstantGiven()
    {
        var monday = new DateTimeOffset(2026, 8, 17, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.True(DurationParser.TryParse("this week", monday, out var value));

        // Monday is the start of its own week, so only that day is kept.
        Assert.Equal(new DateOnly(2026, 8, 16), DateOnly.FromDateTime((monday - value).Date));
    }
}
