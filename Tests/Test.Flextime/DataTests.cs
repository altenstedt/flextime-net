using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flextime;
using Flextime.Daemon;

namespace Test.Flextime;

// The data command: the web's day rows, from the server's merged answer.
public class DataTests
{
    private const string Stockholm = "Europe/Stockholm";

    private static readonly TimeZoneInfo Zone = TimeZones.Get(Stockholm);

    // Monday 2026-11-02, 08:00 in Stockholm, which is UTC+1 then.
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static long At(DateOnly date, int hour, int minute = 0) =>
        new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(1)).ToUnixTimeSeconds();

    // A sample a minute, from and to the times given, inclusive.
    private static long[] Minutes(DateOnly date, (int Hour, int Minute) from, (int Hour, int Minute) to)
    {
        var first = At(date, from.Hour, from.Minute);
        var last = At(date, to.Hour, to.Minute);

        return Enumerable.Range(0, (int)((last - first) / 60) + 1).Select(i => first + i * 60L).ToArray();
    }

    private static DeltaZoneDataContract Item(params long[] timestamps) =>
        new(Stockholm, timestamps.Select((item, i) => i == 0 ? item : item - timestamps[i - 1]).ToArray());

    private static DataDayDataContract[] Count(
        DeltaZoneDataContract[] items,
        IdlePolicy? policy = null,
        Marks? marks = null,
        AddedTime? added = null) =>
        PrintData.Count(items, policy, marks ?? Marks.Empty, added ?? AddedTime.Empty, Monday.AddDays(-30), Monday.AddDays(30));

    private static AddedTime Added(DateOnly date, AddedDay day) => new(new Dictionary<DateOnly, AddedDay> { [date] = day });

    private static string Time(DateTimeOffset value) => value.ToString("HH:mm");

    [Fact]
    public void The_computers_are_asked_for_in_one_request_as_the_web_asks()
    {
        Assert.Equal(
            "/ids?api-version=1.9&id=a%20b&id=b2&id=c3&from=2026-11-01&before=2026-12-01",
            PrintData.IdsUri(["c3", "a b", "b2"], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public void A_day_is_its_range_and_its_bridged_work()
    {
        // 08:00 to 12:00, then 13:00 to 17:00: the hour between is past
        // the default ten minutes.
        var day = Assert.Single(Count([Item([.. Minutes(Monday, (8, 0), (12, 0)), .. Minutes(Monday, (13, 0), (17, 0))])]));

        Assert.Equal(Monday, day.Date);
        Assert.Equal(Stockholm, day.Zone);
        Assert.Equal("08:00", Time(day.Start));
        Assert.Equal("17:00", Time(day.End));
        Assert.Equal(TimeSpan.FromHours(8), day.Work);
        Assert.False(day.Custom);
        Assert.False(day.NotWork);
    }

    [Fact]
    public void A_day_on_its_own_profile_is_custom()
    {
        var policy = new IdlePolicy(
            IdleProfile.Flat(10),
            [],
            [],
            new Dictionary<DateOnly, IdleProfile> { [Monday] = IdleProfile.Flat(60) });

        var day = Assert.Single(Count([Item([.. Minutes(Monday, (8, 0), (12, 0)), .. Minutes(Monday, (13, 0), (17, 0))])], policy));

        // The hour's gap is bridged at sixty minutes.
        Assert.Equal(TimeSpan.FromHours(9), day.Work);
        Assert.True(day.Custom);
    }

    [Fact]
    public void A_profile_for_the_weekday_is_not_custom()
    {
        var policy = new IdlePolicy(
            IdleProfile.Flat(10),
            [new NamedProfile("a", "Office", new HashSet<DayOfWeek> { DayOfWeek.Monday }, IdleProfile.Flat(60))],
            ["a"],
            new Dictionary<DateOnly, IdleProfile>());

        var day = Assert.Single(Count([Item([.. Minutes(Monday, (8, 0), (12, 0)), .. Minutes(Monday, (13, 0), (17, 0))])], policy));

        Assert.Equal(TimeSpan.FromHours(9), day.Work);
        Assert.False(day.Custom);
    }

    [Fact]
    public void Added_time_widens_the_range_and_counts_and_makes_the_day_custom()
    {
        var added = Added(Monday, new AddedDay(7 * 3600 + 45 * 60, 18 * 3600, []));

        var day = Assert.Single(Count([Item(Minutes(Monday, (8, 0), (17, 0)))], added: added));

        Assert.Equal("07:45", Time(day.Start));
        Assert.Equal("18:00", Time(day.End));
        Assert.Equal(TimeSpan.FromHours(10.25), day.Work);
        Assert.True(day.Custom);
    }

    [Fact]
    public void Added_time_inside_the_measured_day_changes_nothing()
    {
        var added = Added(Monday, new AddedDay(9 * 3600, 16 * 3600, []));

        var day = Assert.Single(Count([Item(Minutes(Monday, (8, 0), (17, 0)))], added: added));

        Assert.Equal(TimeSpan.FromHours(9), day.Work);
        Assert.False(day.Custom);
    }

    [Fact]
    public void A_day_marked_whole_is_not_work_and_counts_nothing_not_even_added_time()
    {
        var marks = new Marks(new HashSet<DateOnly> { Monday });
        var added = Added(Monday, new AddedDay(7 * 3600, null, []));

        var day = Assert.Single(Count([Item(Minutes(Monday, (8, 0), (17, 0)))], marks: marks, added: added));

        Assert.True(day.NotWork);
        Assert.Equal(TimeSpan.Zero, day.Work);
        Assert.False(day.Custom);
        Assert.Equal("08:00", Time(day.Start));
        Assert.Equal("17:00", Time(day.End));
    }

    [Fact]
    public void A_marked_range_comes_out_of_the_work_and_the_range()
    {
        // 08:00 to 09:00 marked: the row starts at nine, as the web's does.
        var marks = new Marks(new HashSet<DateOnly>(), new Dictionary<DateOnly, IReadOnlyList<MarkRange>>
        {
            [Monday] = [new MarkRange(8 * 3600, 9 * 3600)],
        });

        var day = Assert.Single(Count([Item(Minutes(Monday, (8, 0), (17, 0)))], marks: marks));

        Assert.Equal("09:00", Time(day.Start));
        Assert.Equal("17:00", Time(day.End));
        Assert.Equal(TimeSpan.FromHours(8), day.Work);
        Assert.False(day.NotWork);
        Assert.False(day.Custom);
    }

    [Fact]
    public void A_day_marked_in_every_minute_keeps_its_whole_range()
    {
        var marks = new Marks(new HashSet<DateOnly>(), new Dictionary<DateOnly, IReadOnlyList<MarkRange>>
        {
            [Monday] = [new MarkRange(0, 86_400)],
        });

        var day = Assert.Single(Count([Item(Minutes(Monday, (8, 0), (17, 0)))], marks: marks));

        Assert.Equal("08:00", Time(day.Start));
        Assert.Equal("17:00", Time(day.End));
        Assert.Equal(TimeSpan.Zero, day.Work);
        Assert.False(day.NotWork);
    }

    [Fact]
    public void A_date_with_added_time_and_no_measurements_has_no_row()
    {
        // The web builds its rows from the server's items only.
        var added = Added(Monday.AddDays(1), new AddedDay(8 * 3600, 17 * 3600, []));

        Assert.Equal([Monday], Count([Item(Minutes(Monday, (8, 0), (9, 0)))], added: added).Select(day => day.Date));
    }

    [Fact]
    public void A_day_of_one_measurement_has_no_row() =>
        Assert.Empty(Count([Item(At(Monday, 8))]));

    [Fact]
    public void Days_outside_the_range_are_left_out_and_the_rest_are_oldest_first()
    {
        DeltaZoneDataContract[] items =
        [
            Item(Minutes(Monday.AddDays(1), (8, 0), (9, 0))),
            Item(Minutes(Monday, (8, 0), (9, 0))),
            Item(Minutes(Monday.AddDays(-1), (8, 0), (9, 0))),
        ];

        var days = PrintData.Count(items, null, Marks.Empty, AddedTime.Empty, Monday, Monday.AddDays(1));

        Assert.Equal([Monday, Monday.AddDays(1)], days.Select(day => day.Date));
    }

    private static DataDayDataContract Row(DateOnly date, string start, string end, TimeSpan work, bool custom = false, bool notWork = false)
    {
        DateTimeOffset Of(string time) => new(date.ToDateTime(TimeOnly.Parse(time)), Zone.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)));

        return new DataDayDataContract(date, Stockholm, Of(start), Of(end), work, custom, notWork);
    }

    [Fact]
    public void The_text_has_months_weeks_and_their_totals()
    {
        var data = new DataDataContract(
            [new DataComputerDataContract("a1", "MacBook Pro M4"), new DataComputerDataContract("b2", "Omegapoint")],
            [
                Row(new DateOnly(2026, 10, 30), "08:00", "16:00", TimeSpan.FromHours(7.5)),
                Row(new DateOnly(2026, 11, 2), "07:54", "21:35", new TimeSpan(8, 5, 0)),
                Row(new DateOnly(2026, 11, 6), "08:27", "17:00", new TimeSpan(1, 55, 0), custom: true),
                Row(new DateOnly(2026, 11, 20), "07:45", "18:26", new TimeSpan(8, 35, 0), custom: true),
                Row(new DateOnly(2026, 11, 21), "09:00", "10:00", TimeSpan.Zero, notWork: true),
            ]);

        Assert.Equal(
            """
            MacBook Pro M4, Omegapoint

            October 2026
            Week 44
              Fri 30   08:00 – 16:00   07:30
                                       07:30
            October                    07:30

            November 2026
            Week 45
              Mon  2   07:54 – 21:35   08:05
              Fri  6   08:27 – 17:00   01:55 •
                                       10:00
            Week 47
              Fri 20   07:45 – 18:26   08:35 •
              Sat 21                   not work
                                       08:35
            November                   18:35
            """.ReplaceLineEndings("\n"),
            string.Join("\n", PrintData.Format(data, allComputers: false, colour: false, October, NovemberEnd, Later)));
    }

    [Fact]
    public void A_week_across_two_months_is_split_and_all_computers_say_so()
    {
        // Thursday 30 October and Monday 3 November... are week 44 and 45;
        // Friday 31 October and Saturday 1 November are both week 44.
        var data = new DataDataContract(
            [new DataComputerDataContract("a1", "One")],
            [
                Row(new DateOnly(2026, 10, 31), "08:00", "09:00", TimeSpan.FromHours(1)),
                Row(new DateOnly(2026, 11, 1), "08:00", "10:00", TimeSpan.FromHours(2)),
            ]);

        var lines = PrintData.Format(data, allComputers: true, colour: false, October, NovemberEnd, Later).ToArray();

        Assert.Equal("All computers", lines[0]);
        Assert.Equal(2, lines.Count(line => line == "Week 44"));
        Assert.Contains("October                    01:00", lines);
        Assert.Contains("November                   02:00", lines);
    }

    [Fact]
    public void Totals_round_the_exact_sum_as_the_web_does()
    {
        // Two days of 30.5 seconds past the minute each round down alone,
        // to 01:00, and sum to 02:01.
        var work = new TimeSpan(0, 1, 0, 30, 500);
        var data = new DataDataContract(
            [new DataComputerDataContract("a1", "One")],
            [
                Row(Monday, "08:00", "09:00", work),
                Row(Monday.AddDays(1), "08:00", "09:00", work),
            ]);

        var lines = PrintData.Format(data, allComputers: false, colour: false, October, NovemberEnd, Later).ToArray();

        Assert.Equal("  Mon  2   08:00 – 09:00   01:01", lines[4]);
        Assert.Equal("                           02:01", lines[6]);
        Assert.Equal("01:00", PrintData.Hours(TimeSpan.FromSeconds(3629)));
        Assert.Equal("01:01", PrintData.Hours(TimeSpan.FromSeconds(3630)));
        Assert.Equal("102:47", PrintData.Hours(new TimeSpan(102, 47, 0)));
    }

    // A range that takes in all of October and November, read later.
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);
    private static readonly DateOnly Later = new(2026, 12, 31);

    [Fact]
    public void A_month_the_range_only_touches_gets_no_total()
    {
        var data = new DataDataContract(
            [new DataComputerDataContract("a1", "One")],
            [
                Row(new DateOnly(2026, 11, 16), "08:00", "09:00", TimeSpan.FromHours(1)),
                Row(new DateOnly(2026, 11, 20), "08:00", "10:00", TimeSpan.FromHours(2)),
            ]);

        // Week 47 alone: the week's total, and no November line.
        var week = PrintData.Format(data, false, false, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 22), Later).ToArray();

        Assert.Contains("                           03:00", week);
        Assert.DoesNotContain(week, line => line.StartsWith("November ") && !line.EndsWith("2026"));

        // November so far, read on the 22nd, counts as the whole month.
        var soFar = PrintData.Format(data, false, false, new DateOnly(2026, 10, 23), new DateOnly(2026, 11, 22), new DateOnly(2026, 11, 22)).ToArray();

        Assert.Contains("November                   03:00", soFar);
    }

    [Fact]
    public void Newest_first_reads_as_the_web_does()
    {
        var data = new DataDataContract(
            [new DataComputerDataContract("a1", "One")],
            [
                Row(new DateOnly(2026, 10, 30), "08:00", "09:00", TimeSpan.FromHours(1)),
                Row(new DateOnly(2026, 11, 16), "08:00", "09:00", TimeSpan.FromHours(1)),
                Row(new DateOnly(2026, 11, 20), "08:00", "10:00", TimeSpan.FromHours(2)),
            ]);

        Assert.Equal(
            """
            One

            November 2026              03:00
            Week 47
              Fri 20   08:00 – 10:00   02:00
              Mon 16   08:00 – 09:00   01:00
                                       03:00

            October 2026
            Week 44
              Fri 30   08:00 – 09:00   01:00
                                       01:00
            """.ReplaceLineEndings("\n"),
            string.Join("\n", PrintData.Format(data, false, false, new DateOnly(2026, 10, 23), new DateOnly(2026, 11, 22), new DateOnly(2026, 11, 22), newestFirst: true)));
    }

    [Fact]
    public void No_days_says_so()
    {
        Assert.Equal(
            ["One", "No data."],
            PrintData.Format(new DataDataContract([new DataComputerDataContract("a1", "One")], []), false, false, October, NovemberEnd, Later));
    }

    [Fact]
    public void The_json_contract_is_the_days_and_the_computers()
    {
        var data = new DataDataContract(
            [new DataComputerDataContract("a1", "One")],
            [Row(Monday, "08:00", "17:00", new TimeSpan(8, 5, 0), custom: true)]);

        var json = PrintData.ToJson(data);

        Assert.Equal(
            """{"computers":[{"id":"a1","name":"One"}],"days":[{"date":"2026-11-02","zone":"Europe/Stockholm","start":"2026-11-02T08:00:00+01:00","end":"2026-11-02T17:00:00+01:00","work":"08:05:00","custom":true,"notWork":false}]}""",
            json);
    }

    [Fact]
    public void The_help_key_fits_80_columns() =>
        Assert.All(PrintData.HelpKey.Split('\n').Concat(Export.HelpKey.Split('\n')), line => Assert.True(line.Length <= 80, line));

    private sealed class FakeApi(Dictionary<string, string> answers) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.PathAndQuery;

            Asked.Add(uri);

            return Task.FromResult(answers.TryGetValue(uri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.example/") };
    }

    private static PrintData Create(FakeApi api, string hereId = "a1") =>
        new(new Factory(api), new DeviceCode(), new Computer(hereId, "macbook"), new PolicyClient(new Factory(api)));

    private static Dictionary<string, string> Answers(string ids, string items) => new()
    {
        ["/store/profiles?api-version=1.0"] = "null",
        ["/store/marks?api-version=1.0"] = """{ "marks": { "2026-11-03": "day" } }""",
        ["/store/added?api-version=1.0"] = """{ "days": { "2026-11-02": { "started": 25200 } } }""",
        ["/computers?api-version=1.7"] = """
            { "items": [
              { "id": "a1", "name": null, "hostname": "macbook", "displayName": "macbook", "names": [], "hostnames": [], "firstDay": null, "lastDay": null },
              { "id": "b2", "name": "Omegapoint", "hostname": "op", "displayName": "Omegapoint", "names": [], "hostnames": [], "firstDay": null, "lastDay": null }
            ] }
            """,
        [$"/ids?api-version=1.9&{ids}&from=2026-11-01&before=2026-12-01"] = items,
    };

    private static string Items(params long[][] days) =>
        JsonSerializer.Serialize(new DeltaZonesDataContract(days.Select(day => Item(day)).ToArray()), DataSourceGenerationContext.Default.DeltaZonesDataContract);

    [Fact]
    public async Task Load_counts_the_chosen_computers_from_one_merged_answer()
    {
        var api = new FakeApi(Answers(
            "id=a1&id=b2",
            Items(Minutes(Monday, (8, 0), (17, 0)), Minutes(Monday.AddDays(1), (8, 0), (17, 0)))));

        var data = await Create(api).Load(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), ["b2", "a1"], false, TestContext.Current.CancellationToken);

        Assert.NotNull(data);
        Assert.Equal(["Omegapoint", "macbook"], data.Computers.Select(item => item.Name));
        Assert.Single(api.Asked, uri => uri.StartsWith("/ids?"));

        // Added from 07:00 on Monday; Tuesday marked whole.
        Assert.Equal("07:00", Time(data.Days[0].Start));
        Assert.Equal(TimeSpan.FromHours(10), data.Days[0].Work);
        Assert.True(data.Days[0].Custom);
        Assert.True(data.Days[1].NotWork);
        Assert.Equal(TimeSpan.Zero, data.Days[1].Work);
    }

    [Fact]
    public async Task Load_takes_this_computer_by_default_and_all_when_asked()
    {
        var here = new FakeApi(Answers("id=a1", Items()));

        var mine = await Create(here).Load(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), [], false, TestContext.Current.CancellationToken);

        Assert.Equal(["a1"], mine!.Computers.Select(item => item.Id));

        var everyone = new FakeApi(Answers("id=a1&id=b2", Items()));

        var all = await Create(everyone).Load(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), [], true, TestContext.Current.CancellationToken);

        Assert.Equal(["a1", "b2"], all!.Computers.Select(item => item.Id));
    }

    [Fact]
    public async Task Load_refuses_a_computer_not_on_the_account()
    {
        var api = new FakeApi(Answers("id=zz", Items()));

        Assert.Null(await Create(api).Load(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), ["zz"], false, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(api.Asked, uri => uri.StartsWith("/ids?"));
    }

    [Fact]
    public void Computers_json_marks_this_computer_only()
    {
        ComputerNamesDataContract Named(string id) => new(id, null, id, id, [], [], null, null);

        var json = JsonNode.Parse(ComputerClient.ToJson([Named("a1"), Named("b2")], "b2", Monday))!["items"]!.AsArray();

        Assert.False(json[0]!.AsObject().ContainsKey("here"));
        Assert.True(json[1]!["here"]!.GetValue<bool>());
    }

    [Fact]
    public void The_range_options_read_as_in_report()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromHours(1));

        Assert.True(RangeOptions.TryRead(null, null, null, now, out var none));
        Assert.Null(none);

        Assert.True(RangeOptions.TryRead("30d", null, null, now, out var since));
        Assert.Equal((new DateOnly(2026, 10, 23), new DateOnly(2026, 11, 22)), since);

        // A week or a month wins over --since, which has a default.
        Assert.True(RangeOptions.TryRead("30d", null, "2026-08", now, out var month));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)), month);

        Assert.False(RangeOptions.TryRead(null, "34", "aug", now, out _));
        Assert.False(RangeOptions.TryRead("soon", null, null, now, out _));
    }
}
