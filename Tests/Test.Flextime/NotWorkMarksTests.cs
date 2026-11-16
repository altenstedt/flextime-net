using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flextime;
using Flextime.Daemon;

namespace Test.Flextime;

// Backdating a pause: finding the stretch before it, writing it to the
// web's marks document, and keeping it for sync when that fails.
public class NotWorkMarksTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-marks-tests-{Guid.NewGuid():N}");

    private static readonly TimeZoneInfo Stockholm = TimeZones.Get("Europe/Stockholm");

    // 2026-11-16 19:44:30 in Stockholm.
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 18, 44, 30, TimeSpan.Zero);

    public NotWorkMarksTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private PendingMarks Pending => new(Path.Combine(folder, "pause-mark.txt"), Path.Combine(folder, "pause-mark.lock"));

    private static MeasurementWithZone At(DateTimeOffset instant, Measurement.Types.Kind kind = Measurement.Types.Kind.Measurement) =>
        new(new Measurement { Timestamp = (uint)instant.ToUnixTimeSeconds(), Kind = kind }, "Europe/Stockholm", 60);

    private static List<MeasurementWithZone> Minutes(DateTimeOffset from, int count) =>
        Enumerable.Range(0, count).Select(minute => At(from.AddMinutes(minute))).ToList();

    // ---- Finding the stretch -------------------------------------------

    [Fact]
    public void The_stretch_begins_after_the_last_long_gap()
    {
        var afternoon = Minutes(Now.AddHours(-3), 60);
        var evening = Minutes(Now.AddMinutes(-3), 3);

        var stretch = Backdate.FindStretch([.. afternoon, .. evening], Now);

        Assert.Equal(Now.AddMinutes(-3).ToUnixTimeSeconds(), stretch!.Value.From.ToUnixTimeSeconds());
        Assert.Equal("Europe/Stockholm", stretch.Value.Zone);
    }

    [Fact]
    public void A_short_gap_does_not_end_the_stretch()
    {
        var before = Minutes(Now.AddMinutes(-30), 5);
        var after = Minutes(Now.AddMinutes(-14), 14);

        var stretch = Backdate.FindStretch([.. before, .. after], Now);

        Assert.Equal(Now.AddMinutes(-30).ToUnixTimeSeconds(), stretch!.Value.From.ToUnixTimeSeconds());
    }

    [Fact]
    public void A_listen_start_ends_the_stretch()
    {
        var before = Minutes(Now.AddMinutes(-10), 5);
        var after = new List<MeasurementWithZone> { At(Now.AddMinutes(-4), Measurement.Types.Kind.Start) };

        after.AddRange(Minutes(Now.AddMinutes(-3), 3));

        var stretch = Backdate.FindStretch([.. before, .. after], Now);

        Assert.Equal(Now.AddMinutes(-4).ToUnixTimeSeconds(), stretch!.Value.From.ToUnixTimeSeconds());
    }

    [Fact]
    public void Nothing_measured_lately_is_no_stretch()
    {
        Assert.Null(Backdate.FindStretch(Minutes(Now.AddHours(-1), 30), Now));
        Assert.Null(Backdate.FindStretch([], Now));
    }

    [Fact]
    public void Measurements_from_ahead_of_now_are_left_out()
    {
        var list = Minutes(Now.AddMinutes(-2), 2);

        list.Add(At(Now.AddHours(5)));

        Assert.Equal(Now.AddMinutes(-2).ToUnixTimeSeconds(), Backdate.FindStretch(list, Now)!.Value.From.ToUnixTimeSeconds());
    }

    [Fact]
    public void The_range_ends_at_the_next_whole_minute()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 18, 45, 0, TimeSpan.Zero), Backdate.RangeEnd(Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 18, 45, 0, TimeSpan.Zero),
            Backdate.RangeEnd(new DateTimeOffset(2026, 11, 16, 18, 45, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_range_is_in_seconds_of_the_day_in_the_days_zone()
    {
        var ranges = Backdate.Split(Now.AddMinutes(-2.5), Backdate.RangeEnd(Now), Stockholm);

        Assert.Equal([new DayRange(new DateOnly(2026, 11, 16), 19 * 3600 + 42 * 60, 19 * 3600 + 45 * 60)], ranges);
    }

    [Fact]
    public void A_range_over_midnight_is_split_into_two_days()
    {
        var from = new DateTimeOffset(2026, 11, 16, 23, 50, 0, TimeSpan.FromHours(1));
        var to = new DateTimeOffset(2026, 11, 17, 0, 10, 0, TimeSpan.FromHours(1));

        Assert.Equal(
            [
                new DayRange(new DateOnly(2026, 11, 16), 23 * 3600 + 50 * 60, 86_400),
                new DayRange(new DateOnly(2026, 11, 17), 0, 600),
            ],
            Backdate.Split(from, to, Stockholm));
    }

    [Fact]
    public void A_range_over_a_clock_change_never_runs_backwards()
    {
        // 02:50 summer time to 02:10 winter time, the night the clocks go back.
        var from = new DateTimeOffset(2026, 10, 25, 2, 50, 0, TimeSpan.FromHours(2));
        var to = new DateTimeOffset(2026, 10, 25, 2, 10, 0, TimeSpan.FromHours(1));

        var range = Assert.Single(Backdate.Split(from, to, Stockholm));

        Assert.True(range.To > range.From);
        Assert.Equal(20 * 60, range.To - range.From);
    }

    // ---- The document ---------------------------------------------------

    private static JsonObject Document(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly DateOnly Monday = new(2026, 11, 16);

    [Fact]
    public void A_range_is_added_and_the_rest_kept()
    {
        var document = Document("""{ "v": 3, "future": { "x": 1 }, "marks": { "2026-11-01": "day" } }""");

        Assert.Null(MarksDocument.Merge(document, [new DayRange(Monday, 100, 200)], out var changed));

        Assert.True(changed);
        Assert.Equal(
            """{"v":3,"future":{"x":1},"marks":{"2026-11-01":"day","2026-11-16":[{"from":100,"to":200}]}}""",
            document.ToJsonString());
    }

    [Fact]
    public void Ranges_that_touch_or_overlap_are_merged()
    {
        var document = Document("""{ "marks": { "2026-11-16": [{ "from": 0, "to": 100 }, { "from": 300, "to": 400 }] } }""");

        MarksDocument.Merge(document, [new DayRange(Monday, 100, 350)], out var changed);

        Assert.True(changed);
        Assert.Equal("""{"marks":{"2026-11-16":[{"from":0,"to":400}]}}""", document.ToJsonString());
    }

    [Fact]
    public void A_range_already_marked_changes_nothing()
    {
        var document = Document("""{ "marks": { "2026-11-16": [{ "from": 0, "to": 400 }] } }""");

        MarksDocument.Merge(document, [new DayRange(Monday, 100, 350)], out var changed);

        Assert.False(changed);
    }

    [Fact]
    public void A_whole_marked_day_is_left_alone()
    {
        var document = Document("""{ "marks": { "2026-11-16": "day" } }""");

        Assert.Null(MarksDocument.Merge(document, [new DayRange(Monday, 100, 350)], out var changed));
        Assert.False(changed);
        Assert.Equal("""{"marks":{"2026-11-16":"day"}}""", document.ToJsonString());
    }

    [Theory]
    [InlineData("""{ "marks": [] }""")]
    [InlineData("""{ "marks": { "2026-11-16": "week" } }""")]
    [InlineData("""{ "marks": { "2026-11-16": [{ "from": 5, "to": 1 }] } }""")]
    [InlineData("""{ "marks": { "2026-11-16": [{ "from": "0", "to": 100 }] } }""")]
    [InlineData("""{ "marks": { "2026-11-16": [{ "from": 0, "to": 90000 }] } }""")]
    public void A_shape_this_version_does_not_know_is_left_unchanged(string json)
    {
        var document = Document(json);
        var before = document.ToJsonString();

        Assert.NotNull(MarksDocument.Merge(document, [new DayRange(Monday, 100, 350)], out var changed));
        Assert.False(changed);
        Assert.Equal(before, document.ToJsonString());
    }

    [Fact]
    public void What_is_written_reads_back_as_marks()
    {
        var document = Document("{}");

        MarksDocument.Merge(document, [new DayRange(Monday, 100, 350)], out _);

        var marks = Marks.Decode(JsonDocument.Parse(document.ToJsonString()).RootElement);

        Assert.Equal([new MarkRange(100, 350)], marks.RangesOn(Monday));
    }

    // ---- The server -----------------------------------------------------

    private sealed class FakeStore : HttpMessageHandler
    {
        public string? Document { get; set; }
        public int Version { get; set; } = 1;
        public List<string> Requests { get; } = [];
        public Queue<HttpStatusCode> PutAnswers { get; } = new();
        public Func<CancellationToken, Task>? Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hang != null)
            {
                await Hang(cancellationToken);
            }

            Requests.Add($"{request.Method} {request.Headers.IfMatch}");

            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Document ?? "null", Encoding.UTF8, "application/json"),
                };

                if (Document != null)
                {
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"v{Version}\"", true);
                }

                return response;
            }

            if (PutAnswers.TryDequeue(out var answer))
            {
                // Someone else wrote in between.
                if (answer == HttpStatusCode.PreconditionFailed)
                {
                    Version++;
                }

                return new HttpResponseMessage(answer);
            }

            Document = await request.Content!.ReadAsStringAsync(cancellationToken);
            Version++;

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.example/") };

    private static readonly DayRange Evening = new(Monday, 71_000, 71_100);

    [Fact]
    public async Task A_first_mark_creates_the_document()
    {
        var store = new FakeStore();
        var (result, _) = await new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken);

        Assert.Equal(MarkResult.Written, result);
        Assert.Equal(["GET ", "PUT "], store.Requests);
        Assert.Equal("""{"marks":{"2026-11-16":[{"from":71000,"to":71100}]}}""", store.Document);
    }

    [Fact]
    public async Task A_mark_is_written_against_the_version_read()
    {
        var store = new FakeStore { Document = """{ "marks": {}, "other": true }""" };
        await new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken);

        Assert.Equal(["GET ", "PUT W/\"v1\""], store.Requests);
        Assert.Contains("\"other\":true", store.Document);
    }

    [Fact]
    public async Task A_write_that_lost_a_race_is_merged_again()
    {
        var store = new FakeStore { Document = """{ "marks": {} }""" };

        store.PutAnswers.Enqueue(HttpStatusCode.PreconditionFailed);

        var (result, _) = await new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken);

        Assert.Equal(MarkResult.Written, result);
        Assert.Equal(["GET ", "PUT W/\"v1\"", "GET ", "PUT W/\"v2\""], store.Requests);
    }

    [Fact]
    public async Task A_create_that_lost_a_race_is_merged_again()
    {
        var store = new FakeStore();

        store.PutAnswers.Enqueue(HttpStatusCode.Conflict);

        var (result, _) = await new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken);

        Assert.Equal(MarkResult.Written, result);
    }

    [Fact]
    public async Task A_document_that_never_settles_throws_so_the_mark_is_kept()
    {
        var store = new FakeStore { Document = """{ "marks": {} }""" };

        for (var i = 0; i < 10; i++)
        {
            store.PutAnswers.Enqueue(HttpStatusCode.PreconditionFailed);
        }

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_mark_already_there_is_not_written_again()
    {
        var store = new FakeStore { Document = """{ "marks": { "2026-11-16": [{ "from": 0, "to": 86400 }] } }""" };

        var (result, _) = await new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken);

        Assert.Equal(MarkResult.AlreadyMarked, result);
        Assert.Equal(["GET "], store.Requests);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("""{ "marks": { "2026-11-16": "something new" } }""")]
    public async Task A_document_this_version_cannot_merge_is_refused_untouched(string document)
    {
        var store = new FakeStore { Document = document };

        var (result, reason) = await new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken);

        Assert.Equal(MarkResult.Refused, result);
        Assert.NotNull(reason);
        Assert.Equal(["GET "], store.Requests);
    }

    [Fact]
    public async Task A_server_error_throws_so_the_mark_is_kept()
    {
        var store = new FakeStore { Document = """{ "marks": {} }""" };

        store.PutAnswers.Enqueue(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new MarksWriter(Client(store)).Apply([Evening], TestContext.Current.CancellationToken));
    }

    // ---- Waiting for sync -----------------------------------------------

    private static DateTimeOffset Whole(DateTimeOffset instant) =>
        DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());

    // 19:41:00 to 19:45:00, saved at 19:44:30.
    private static PendingMark Mark() =>
        new(Whole(Now.AddMinutes(-3).AddSeconds(-30)), Whole(Backdate.RangeEnd(Now)), "Europe/Stockholm", Whole(Now));

    [Fact]
    public async Task A_saved_mark_reads_back_and_is_removed()
    {
        var pending = Pending;

        Assert.True(await pending.Add(Mark(), Now));
        Assert.True(await pending.Add(Mark(), Now));

        Assert.Equal([Mark()], await pending.Read(Now, null));

        await pending.Remove(Mark(), Now);

        Assert.Empty(await pending.Read(Now, null));
        Assert.False(File.Exists(Path.Combine(folder, "pause-mark.txt")));
    }

    [Fact]
    public async Task Corrupt_and_old_lines_are_dropped_once_and_the_rest_kept()
    {
        var good = Mark().ToLine();
        var old = (Mark() with { Created = Now.AddDays(-8) }).ToLine();
        var path = Path.Combine(folder, "pause-mark.txt");

        await File.WriteAllTextAsync(path,
            $"garbage\n{good}\n{old}\n1 2 Europe/Stockholm 3\n{Mark().ToLine().Replace("Europe/Stockholm", "Mars/Olympus")}\n",
            TestContext.Current.CancellationToken);

        var dropped = new List<string>();

        Assert.Equal([Mark()], await Pending.Read(Now, dropped.Add));
        Assert.Equal(4, dropped.Count);

        dropped.Clear();

        Assert.Equal([Mark()], await Pending.Read(Now, dropped.Add));
        Assert.Empty(dropped);
    }

    [Fact]
    public async Task A_huge_or_strange_file_is_discarded()
    {
        var path = Path.Combine(folder, "pause-mark.txt");

        await File.WriteAllBytesAsync(path, new byte[1024 * 1024], TestContext.Current.CancellationToken);

        var dropped = new List<string>();

        Assert.Empty(await Pending.Read(Now, dropped.Add));
        Assert.Single(dropped);
        Assert.False(File.Exists(path));

        Directory.CreateDirectory(path);

        Assert.Empty(await Pending.Read(Now, dropped.Add));
    }

    [Fact]
    public async Task Sync_writes_what_waits_and_removes_it()
    {
        var store = new FakeStore();
        var pending = Pending;
        var notes = new List<(string, bool)>();

        await pending.Add(Mark(), Now);
        await pending.Retry(new MarksWriter(Client(store)), Now, (line, warning) => notes.Add((line, warning)),
            TestContext.Current.CancellationToken);

        Assert.Empty(await pending.Read(Now, null));
        Assert.Equal("""{"marks":{"2026-11-16":[{"from":70860,"to":71100}]}}""", store.Document);
        Assert.Equal([("Marked 19:41–19:45 as not work.", false)], notes);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_keeps_the_marks_and_never_throws()
    {
        var store = new FakeStore { Hang = _ => throw new HttpRequestException("No route to host") };
        var pending = Pending;
        var notes = new List<(string Line, bool Warning)>();

        await pending.Add(Mark(), Now);
        await pending.Retry(new MarksWriter(Client(store)), Now, (line, warning) => notes.Add((line, warning)),
            TestContext.Current.CancellationToken);

        Assert.Equal([Mark()], await pending.Read(Now, null));
        Assert.True(Assert.Single(notes).Warning);
    }

    [Fact]
    public async Task A_hanging_server_is_given_up_on_at_the_timeout()
    {
        var store = new FakeStore { Hang = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken) };
        var pending = Pending;
        var notes = new List<string>();

        await pending.Add(Mark(), Now);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await pending.Retry(new MarksWriter(Client(store)), Now, (line, _) => notes.Add(line), timeout.Token);

        Assert.Single(notes);
        Assert.Equal([Mark()], await pending.Read(Now, null));
    }

    [Fact]
    public async Task A_mark_the_server_cannot_take_is_dropped_not_retried_forever()
    {
        var store = new FakeStore { Document = """{ "marks": { "2026-11-16": "something new" } }""" };
        var pending = Pending;
        var notes = new List<(string Line, bool Warning)>();

        await pending.Add(Mark(), Now);
        await pending.Retry(new MarksWriter(Client(store)), Now, (line, warning) => notes.Add((line, warning)),
            TestContext.Current.CancellationToken);

        Assert.Empty(await pending.Read(Now, null));
        Assert.StartsWith("Dropped the not-work mark", Assert.Single(notes).Line);
    }

    // ---- What report counts ---------------------------------------------

    [Fact]
    public void Ranges_are_read_from_the_stored_marks()
    {
        var marks = Marks.Decode(JsonDocument.Parse(
            """{ "marks": { "2026-11-16": [{ "from": 300, "to": 400 }, { "from": 0, "to": 100 }], "2026-11-17": "day" } }""").RootElement);

        Assert.Equal([new MarkRange(0, 100), new MarkRange(300, 400)], marks.RangesOn(Monday));
        Assert.True(marks.IsMarked(new DateOnly(2026, 11, 17)));
        Assert.False(marks.IsMarked(Monday));
    }

    [Fact]
    public void Marked_ranges_come_out_of_the_bridged_work_only()
    {
        // 10:00, 10:01, 10:02, then 10:30 after a gap the limit does not
        // bridge, then 10:31.
        var day = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(1));
        DateTimeOffset[] timestamps = [day, day.AddMinutes(1), day.AddMinutes(2), day.AddMinutes(30), day.AddMinutes(31)];

        // Marked 10:01:30 to 10:31.
        var overlap = Marks.Overlap(timestamps, _ => TimeSpan.FromMinutes(10),
            [new MarkRange(10 * 3600 + 90, 10 * 3600 + 31 * 60)]);

        // Half of the second minute and the last minute; the gap was not work.
        Assert.Equal(TimeSpan.FromSeconds(30 + 60), overlap);
    }
}
