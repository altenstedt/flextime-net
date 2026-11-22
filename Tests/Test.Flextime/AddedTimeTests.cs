using System.Text.Json;
using Flextime;
using Flextime.Daemon;

namespace Test.Flextime;

public class AddedTimeTests
{
    private static readonly DateOnly Date = new(2026, 11, 10);

    private static readonly DateTimeOffset Midnight = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    // 08:00 to 09:00 and 19:00 to 20:00 UTC, a sample a minute, as the
    // web's own tests have it.
    private static readonly DateTimeOffset[] Timestamps =
    [
        .. Enumerable.Range(0, 61).Select(i => Midnight.AddHours(8).AddMinutes(i)),
        .. Enumerable.Range(0, 61).Select(i => Midnight.AddHours(19).AddMinutes(i)),
    ];

    private static TimeSpan FiveMinutes(DateTimeOffset _) => TimeSpan.FromMinutes(5);

    private static AddedTime Decode(string json) => AddedTime.Decode(JsonDocument.Parse(json).RootElement);

    private static TimeSpan Declared(AddedDay day) => AddedTime.Declared(Timestamps, FiveMinutes, day);

    private static AddedDay Ranges(params (double From, double To)[] ranges) =>
        new(null, null, ranges.Select(range => new MarkRange(range.From, range.To)).ToList());

    [Fact]
    public void Time_added_in_a_gap_counts_as_work() =>
        Assert.Equal(TimeSpan.FromHours(1), Declared(Ranges((9 * 3600, 10 * 3600))));

    [Fact]
    public void A_range_leaves_out_what_was_measured() =>
        // 07:00 to 10:00 keeps 07:00 to 08:00 and 09:00 to 10:00.
        Assert.Equal(TimeSpan.FromHours(2), Declared(Ranges((7 * 3600, 10 * 3600))));

    [Fact]
    public void A_range_does_not_count_time_started_already_added()
    {
        var day = new AddedDay(7 * 3600, null, [new MarkRange(6 * 3600 + 1800, 7 * 3600 + 1800)]);

        Assert.Equal(TimeSpan.FromSeconds(5400), Declared(day));
    }

    [Fact]
    public void Overlapping_ranges_count_once() =>
        Assert.Equal(TimeSpan.FromMinutes(90), Declared(Ranges((9 * 3600, 10 * 3600), (9 * 3600 + 1800, 10 * 3600 + 1800))));

    [Fact]
    public void Started_and_ended_inside_the_measured_day_add_nothing() =>
        Assert.Equal(TimeSpan.Zero, Declared(new AddedDay(8 * 3600, 20 * 3600, [])));

    [Fact]
    public void Ended_after_the_last_measurement_adds_the_time_from_it() =>
        Assert.Equal(TimeSpan.FromMinutes(30), Declared(new AddedDay(null, 20 * 3600 + 1800, [])));

    [Fact]
    public void A_gap_the_limit_does_not_bridge_is_open_to_a_range()
    {
        // At a five-minute limit 08:00 and 08:10 are two blocks, so the
        // time between them can be added; at fifteen it is measured work.
        var start = Midnight.AddHours(8);
        DateTimeOffset[] timestamps = [start, start.AddMinutes(10), start.AddMinutes(11)];
        var day = Ranges((8 * 3600, 8 * 3600 + 600));

        Assert.Equal(TimeSpan.FromMinutes(10), AddedTime.Declared(timestamps, FiveMinutes, day));
        Assert.Equal(TimeSpan.Zero, AddedTime.Declared(timestamps, _ => TimeSpan.FromMinutes(15), day));
    }

    [Fact]
    public void Started_is_read_in_the_days_own_zone()
    {
        // 2026-11-20 in Stockholm: first activity at 08:07, started at
        // 07:45 in the web.
        var zone = TimeZones.Get("Europe/Stockholm");
        var first = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 11, 20, 7, 7, 0, TimeSpan.Zero), zone);
        DateTimeOffset[] timestamps = [first, first.AddMinutes(1)];

        Assert.Equal(TimeSpan.FromMinutes(22), AddedTime.Declared(timestamps, FiveMinutes, new AddedDay(27_900, null, [])));
    }

    [Fact]
    public void No_added_time_adds_nothing() =>
        Assert.Equal(TimeSpan.Zero, AddedTime.Declared(Timestamps, FiveMinutes, null));

    // An item of the server's days, with the added row only.
    private static string Day(string date, string value) =>
        $$"""[ { "date": "{{date}}", "added": { "value": {{value}}, "version": "W/\"1\"" } } ]""";

    [Fact]
    public void The_added_rows_are_read()
    {
        var added = Decode("""
            [ { "date": "2026-11-10", "added": { "value": { "started": 25200, "ended": 73800.5, "ranges": [{ "from": 32400, "to": 36000 }] }, "version": "v" } },
              { "date": "2026-11-12", "marks": { "value": "day", "version": "v" } } ]
            """);

        Assert.Equal(new AddedDay(25_200, 73_800.5, [new MarkRange(32_400, 36_000)]), added.On(Date), new AddedDayComparer());
        Assert.Null(added.On(Date.AddDays(1)));
        Assert.Null(added.On(Date.AddDays(2)));
    }

    [Fact]
    public void No_days_are_no_added_time()
    {
        Assert.Empty(Decode("null").Days);
        Assert.Empty(Decode("[]").Days);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[3]")]
    [InlineData("""[ { "added": { "value": { "started": 0 } } } ]""")]
    [InlineData("""[ { "date": "someday", "added": { "value": { "started": 0 } } } ]""")]
    [InlineData("""[ { "date": "2026-11-10", "added": { "version": "v" } } ]""")]
    [InlineData("""[ { "date": "2026-11-10", "added": [] } ]""")]
    public void Malformed_days_are_refused(string stored) =>
        Assert.Throws<IdlePolicyException>(() => Decode(stored));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{ "started": null }""")]
    [InlineData("""{ "started": "07:45" }""")]
    [InlineData("""{ "ended": 86401 }""")]
    [InlineData("""{ "started": -1 }""")]
    [InlineData("""{ "ranges": {} }""")]
    [InlineData("""{ "ranges": [{ "from": 10, "to": 10 }] }""")]
    [InlineData("""{ "ranges": [{ "from": 0 }] }""")]
    [InlineData("""{ "ranges": [3] }""")]
    public void Malformed_added_time_is_refused(string value) =>
        Assert.Throws<IdlePolicyException>(() => Decode(Day("2026-11-10", value)));

    private sealed class AddedDayComparer : IEqualityComparer<AddedDay?>
    {
        public bool Equals(AddedDay? x, AddedDay? y) =>
            x != null && y != null && x.Started == y.Started && x.Ended == y.Ended && x.Ranges.SequenceEqual(y.Ranges);

        public int GetHashCode(AddedDay? obj) => 0;
    }
}
