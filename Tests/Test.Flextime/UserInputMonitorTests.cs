using Flextime.Monitor;

namespace Test.Flextime;

public class UserInputMonitorTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    [Theory]
    // On time: input since the previous read, with a small margin.
    [InlineData(60, 62)]
    // Late, after a busy minute: the window grows with it.
    [InlineData(70, 72)]
    [InlineData(150, 152)]
    // Early, or a wall clock set back: never stricter than an interval.
    [InlineData(59, 62)]
    [InlineData(-3600, 62)]
    // A wall clock set forward: no more than three intervals.
    [InlineData(3600, 182)]
    public void Scheduled_limit_follows_the_time_since_the_last_read(int seconds, int expected)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expected),
            UserInputMonitor.ActiveLimit(Interval, TimeSpan.FromSeconds(seconds), wake: false));
    }

    [Fact]
    public void One_input_counts_in_one_on_time_minute()
    {
        // Input 11 s before a read is 71 s old at the next one.
        var limit = UserInputMonitor.ActiveLimit(Interval, TimeSpan.FromSeconds(60), wake: false);

        Assert.False(TimeSpan.FromSeconds(71) < limit);
        Assert.True(TimeSpan.FromSeconds(59) < limit);
    }

    [Theory]
    [InlineData(true, 60)]
    [InlineData(false, null)]
    public void Wakes_and_unscheduled_reads_keep_the_old_limit(bool wake, int? seconds)
    {
        var since = seconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        Assert.Equal(TimeSpan.FromSeconds(72), UserInputMonitor.ActiveLimit(Interval, since, wake));
    }

    [Theory]
    // A delay that ended a little early is still in its minute, and the
    // next tick is a minute on, not the same minute again.
    [InlineData("2026-10-28T08:01:00.2400000Z", "2026-10-28T08:02:00.2500000Z")]
    [InlineData("2026-10-28T08:00:59.9900000Z", "2026-10-28T08:01:00.2500000Z")]
    [InlineData("2026-10-28T08:00:00.2500000Z", "2026-10-28T08:01:00.2500000Z")]
    [InlineData("2026-10-28T08:00:00.0000000Z", "2026-10-28T08:01:00.2500000Z")]
    [InlineData("2026-10-28T23:59:30.0000000Z", "2026-10-29T00:00:00.2500000Z")]
    public void Next_tick_is_past_the_next_minute_by_the_margin(string now, string expected)
    {
        var next = UserInputMonitor.NextTick(DateTime.Parse(now).ToUniversalTime(), Interval);

        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), next);
        Assert.Equal(DateTimeKind.Utc, next.Kind);
    }
}
