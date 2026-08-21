using Flextime.Client;

namespace Test.Flextime;

public class AppTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 21, 9, 30, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(30, 30)]
    [InlineData(1, 1)]
    public void DaysCountBackFromToday(int days, int expected)
    {
        Assert.Equal(TimeSpan.FromDays(expected), App.ResolveSince(days, null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoDaysMeansEveryDayOnDisk(int days)
    {
        // The reader treats a cutoff of zero or less as no cutoff.
        Assert.True(App.ResolveSince(days, null, Now) <= TimeSpan.Zero);
    }

    [Fact]
    public void SinceWinsOverDays()
    {
        var since = TimeSpan.FromDays(3);

        Assert.Equal(since, App.ResolveSince(30, since, Now));
    }

    [Theory]
    // Anything shorter than a day is today, because whole days are all
    // that is printed.
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(23, 59)]
    public void ShortLookbacksBecomeToday(int hours, int minutes)
    {
        var since = new TimeSpan(hours, minutes, 0);

        var resolved = App.ResolveSince(30, since, Now);

        Assert.Equal(new DateOnly(2026, 8, 20), DateOnly.FromDateTime((Now - resolved).Date));
    }

    [Fact]
    public void ADayOrMoreIsLeftAlone()
    {
        var since = TimeSpan.FromDays(3);

        Assert.Equal(since, App.ResolveSince(30, since, Now));
    }
}
