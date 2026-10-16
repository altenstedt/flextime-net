using System.Net;
using System.Text;
using System.Text.Json;
using Flextime;
using Flextime.Daemon;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Flextime;

public class PlaceSyncTests : IDisposable
{
    private const string ComputerId = "f204899922822897";

    // 2025-10-12 08:00 UTC, 10:00 in Stockholm.
    private const uint Morning = 1760256000;

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-place-sync-tests-{Guid.NewGuid():N}");

    private string Measurements => Path.Combine(folder, "measurements");

    public PlaceSyncTests()
    {
        Directory.CreateDirectory(Measurements);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Body)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));

            return Respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.example/") };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private Sync CreateSync(FakeApi api, string computerId = ComputerId) =>
        new(new Factory(api), new Computer(computerId, "Test"))
        {
            Folder = Measurements,
            ReconcilePath = Path.Combine(folder, "reconcile.txt"),
            LocationPath = Path.Combine(folder, "location.txt"),
            LocationClearPath = Path.Combine(folder, "location-clear.txt"),
            ListenRunning = () => false,
        };

    private static string FileName(uint started) =>
        $"{DateTimeOffset.FromUnixTimeSeconds(started).UtcDateTime:yyyy-MM-ddTHHmmss}.bin";

    private void WriteFile(string name, uint[] measurements, params (uint timestamp, ulong router, uint flags)[] places) =>
        WriteFile(name, measurements, places.Select(place => new Place { Timestamp = place.timestamp, Router = place.router, Flags = place.flags }).ToArray());

    private void WriteFile(string name, uint[] measurements, Place[] places)
    {
        var file = new Measurements { Interval = 60, Zone = "Europe/Stockholm" };

        file.Measurements_.AddRange(measurements.Select(timestamp => new Measurement { Timestamp = timestamp, Kind = Measurement.Types.Kind.Measurement }));
        file.Places.AddRange(places);

        using var stream = File.Create(Path.Combine(Measurements, name));

        file.WriteTo(stream);
    }

    // What the server holds for the day: nothing.
    private const string EmptySummary = """{"name":"Test","items":[]}""";

    private static (HttpMethod, string)[] Calls(FakeApi api) =>
        api.Requests.Select(request => (request.Method, request.Uri)).ToArray();

    [Fact]
    public async Task Location_off_sends_exactly_what_it_did_before_places()
    {
        WriteFile("2026-10-12T080000.bin", [Morning, Morning + 60]);

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.4"),
                (HttpMethod.Patch, $"/{ComputerId}"),
            ],
            Calls(api));
        Assert.Equal(
            """{"zone":"Europe/Stockholm","items":[{"kind":1,"timestamp":1760256000},{"kind":1,"timestamp":1760256060}]}""",
            api.Requests[1].Body);
    }

    [Fact]
    public async Task Computer_id_is_escaped_in_every_request_path()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning, Morning + 60], (Morning, 0x95313127c39c, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json("""{"name":"Old","items":[]}""") : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api, "a/b c?").SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, "/a%2Fb%20c%3F/summary?api-version=1.5"),
                (HttpMethod.Patch, "/a%2Fb%20c%3F/name"),
                (HttpMethod.Patch, "/a%2Fb%20c%3F"),
                (HttpMethod.Patch, "/a%2Fb%20c%3F/places?api-version=1.5"),
            ],
            Calls(api));
    }

    [Fact]
    public async Task Places_are_uploaded_after_the_activity()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning, Morning + 60], (Morning, 0x95313127c39c, 0), (Morning + 30, 0, PlaceFlags.Vpn));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.5"),
                (HttpMethod.Patch, $"/{ComputerId}"),
                (HttpMethod.Patch, $"/{ComputerId}/places?api-version=1.5"),
            ],
            Calls(api));
        Assert.Equal(
            """{"zone":"Europe/Stockholm","items":[{"timestamp":1760256000,"router":"95313127c39c","flags":0},{"timestamp":1760256030,"router":null,"flags":2}]}""",
            api.Requests[2].Body);
    }

    [Fact]
    public async Task Places_on_disk_are_uploaded_after_location_is_turned_off()
    {
        // The off marker is what stops the last place extending forward.
        Location.SetOn(false, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 0x95313127c39c, 0), (Morning + 30, 0, PlaceFlags.LocationOff));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Contains((HttpMethod.Patch, $"/{ComputerId}/places?api-version=1.5"), Calls(api));
    }

    [Fact]
    public async Task Never_set_location_ignores_places_on_disk()
    {
        // Only listen with location on writes places; without the setting
        // file there is nothing to look for, and no 1.5 request is made.
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 0x95313127c39c, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.4"),
                (HttpMethod.Patch, $"/{ComputerId}"),
            ],
            Calls(api));
    }

    [Fact]
    public async Task Places_in_sync_are_not_uploaded()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 0x95313127c39c, 0), (Morning + 30, 0, 0));

        var summary = $$"""
            {"name":"Test","placesClearedAt":null,"items":[{"date":"2025-10-12","zone":"Europe/Stockholm",
             "count":1,"last":{{Morning}},"sum":{{Morning}},
             "placesCount":2,"placesLast":{{Morning + 30}},"placesSum":{{2L * Morning + 30}}}]}
            """;

        var api = new FakeApi { Respond = _ => Json(summary) };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal([(HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.5")], Calls(api));
    }

    [Fact]
    public async Task A_summary_repeating_a_day_does_not_fail_the_sync()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 0x95313127c39c, 0), (Morning + 30, 0, 0));

        var item = $$"""
            {"date":"2025-10-12","zone":"Europe/Stockholm",
             "count":1,"last":{{Morning}},"sum":{{Morning}},
             "placesCount":2,"placesLast":{{Morning + 30}},"placesSum":{{2L * Morning + 30}}}
            """;

        var summary = $$"""{"name":"Test","placesClearedAt":null,"items":[{{item}},{{item}}]}""";

        var api = new FakeApi { Respond = _ => Json(summary) };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal([(HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.5")], Calls(api));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_api_without_1_5_still_gets_the_activity(HttpStatusCode status)
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 0x95313127c39c, 0));

        var api = new FakeApi
        {
            Respond = request => request.RequestUri!.Query.Contains("api-version=1.5")
                ? new HttpResponseMessage(status)
                : request.Method == HttpMethod.Get
                    ? Json(EmptySummary)
                    : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        var sync = CreateSync(api);

        await sync.SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.5"),
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.4"),
                (HttpMethod.Patch, $"/{ComputerId}"),
            ],
            Calls(api));

        // And places are tried again next round, now over the window.
        api.Requests.Clear();

        await sync.SyncAndLog(NullLogger.Instance);

        Assert.StartsWith($"/{ComputerId}/summary?api-version=1.5&since=", api.Requests[0].Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_failed_places_upload_does_not_stop_the_activity(HttpStatusCode status)
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        // Recent, so that the second, windowed pass still looks at it.
        var recent = (uint)DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

        // Named for when it was started, as listen names it: the
        // windowed pass picks files by name.
        WriteFile(FileName(recent), [recent], (recent, 0x95313127c39c, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get
                ? Json(EmptySummary)
                : request.RequestUri!.AbsolutePath.EndsWith("/places")
                    ? new HttpResponseMessage(status)
                    : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        var lines = new List<string>();
        var sync = CreateSync(api);

        // Does not throw, and the activity went first.
        await sync.SyncAndLog(new ListLogger(lines));

        Assert.Equal((HttpMethod.Patch, $"/{ComputerId}"), Calls(api)[1]);
        Assert.Contains(lines, line => line.StartsWith("Places not synced", StringComparison.Ordinal));

        api.Requests.Clear();

        await sync.SyncAndLog(NullLogger.Instance);

        Assert.Contains((HttpMethod.Patch, $"/{ComputerId}/places?api-version=1.5"), Calls(api));
    }

    [Fact]
    public async Task Offline_during_places_does_not_throw()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 0x95313127c39c, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get
                ? Json(EmptySummary)
                : request.RequestUri!.AbsolutePath.EndsWith("/places")
                    ? throw new HttpRequestException("No route to host")
                    : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal((HttpMethod.Patch, $"/{ComputerId}"), Calls(api)[1]);
    }

    [Fact]
    public async Task A_pending_clear_is_deleted_before_any_place_is_uploaded()
    {
        var markerPath = Path.Combine(folder, "location-clear.txt");

        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        Location.WriteClear(new ClearMarker(Morning + 10, Pending: true), markerPath);
        WriteFile("2026-10-12T080000.bin", [Morning, Morning + 60], (Morning, 1, 0), (Morning + 20, 2, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.5"),
                (HttpMethod.Patch, $"/{ComputerId}"),
                (HttpMethod.Delete, $"/{ComputerId}/places?api-version=1.5"),
                (HttpMethod.Patch, $"/{ComputerId}/places?api-version=1.5"),
            ],
            Calls(api));

        // Nothing before the clear goes up.
        var items = JsonDocument.Parse(api.Requests[3].Body!).RootElement.GetProperty("items");

        Assert.Equal([Morning + 20], items.EnumerateArray().Select(item => item.GetProperty("timestamp").GetUInt32()));
        Assert.False(Location.ReadClear(markerPath)!.Pending);
    }

    [Fact]
    public async Task A_failed_pending_delete_uploads_no_places_and_keeps_the_marker()
    {
        var markerPath = Path.Combine(folder, "location-clear.txt");

        Location.WriteClear(new ClearMarker(Morning + 10, Pending: true), markerPath);
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning + 20, 2, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get
                ? Json(EmptySummary)
                : request.Method == HttpMethod.Delete
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Get, $"/{ComputerId}/summary?api-version=1.5"),
                (HttpMethod.Patch, $"/{ComputerId}"),
                (HttpMethod.Delete, $"/{ComputerId}/places?api-version=1.5"),
            ],
            Calls(api));
        Assert.True(Location.ReadClear(markerPath)!.Pending);
    }

    [Fact]
    public async Task A_clear_from_another_computer_clears_here_first()
    {
        var markerPath = Path.Combine(folder, "location-clear.txt");

        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning, Morning + 60], (Morning, 1, 0), (Morning + 20, 2, 0));

        var summary = $$"""{"name":"Test","placesClearedAt":{{Morning + 10}},"items":[]}""";

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(summary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(new ClearMarker(Morning + 10, Pending: false), Location.ReadClear(markerPath));
        Assert.Equal([Morning + 20], PlaceFiles.Read(Measurements).Select(item => item.Place.Timestamp));

        var upload = api.Requests.Single(request => request.Uri.EndsWith("/places?api-version=1.5"));
        var items = JsonDocument.Parse(upload.Body!).RootElement.GetProperty("items");

        Assert.Equal([Morning + 20], items.EnumerateArray().Select(item => item.GetProperty("timestamp").GetUInt32()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_clear_in_the_future_is_ignored(bool farAhead)
    {
        var markerPath = Path.Combine(folder, "location-clear.txt");

        // As far as the marker goes, or just past the clock skew.
        var cleared = farAhead
            ? uint.MaxValue
            : DateTimeOffset.UtcNow.Add(PlaceSync.ClockSkew).AddHours(1).ToUnixTimeSeconds();

        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning, Morning + 60], (Morning, 1, 0), (Morning + 20, 2, 0));

        var summary = $$"""{"name":"Test","placesClearedAt":{{cleared}},"items":[]}""";

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(summary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Null(Location.ReadClear(markerPath));
        Assert.Equal([Morning, Morning + 20], PlaceFiles.Read(Measurements).Select(item => item.Place.Timestamp));

        var upload = api.Requests.Single(request => request.Uri.EndsWith("/places?api-version=1.5"));
        var items = JsonDocument.Parse(upload.Body!).RootElement.GetProperty("items");

        Assert.Equal([Morning, Morning + 20], items.EnumerateArray().Select(item => item.GetProperty("timestamp").GetUInt32()));
    }

    [Fact]
    public async Task A_clear_within_the_clock_skew_is_adopted()
    {
        var markerPath = Path.Combine(folder, "location-clear.txt");

        var cleared = (uint)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 1, 0));

        var summary = $$"""{"name":"Test","placesClearedAt":{{cleared}},"items":[]}""";

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(summary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(new ClearMarker(cleared, Pending: false), Location.ReadClear(markerPath));
    }

    [Fact]
    public async Task Change_points_are_grouped_by_local_date()
    {
        // 23:30 UTC on the 12th is the 13th in Stockholm.
        const uint lateEvening = 1760311800;

        Assert.Equal(DateTimeOffset.Parse("2025-10-12T23:30:00Z"), DateTimeOffset.FromUnixTimeSeconds(lateEvening));

        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning], (Morning, 1, 0), (lateEvening, 2, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(2, api.Requests.Count(request => request.Uri.EndsWith("/places?api-version=1.5")));
    }

    [Fact]
    public async Task Large_uploads_are_split()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));

        var places = Enumerable.Range(0, PlaceSync.MaxItems + 5)
            .Select(i => (Morning + (uint)i, (ulong)(i % 2), 0u))
            .ToArray();

        WriteFile("2026-10-12T080000.bin", [Morning], places);

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        var uploads = api.Requests.Where(request => request.Uri.EndsWith("/places?api-version=1.5")).ToArray();

        Assert.Equal(2, uploads.Length);
        Assert.Equal(
            [PlaceSync.MaxItems, 5],
            uploads.Select(upload => JsonDocument.Parse(upload.Body!).RootElement.GetProperty("items").GetArrayLength()));
    }

[Fact]
    public async Task Hints_are_sent_when_valid_and_left_out_otherwise()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));
        WriteFile("2026-10-12T080000.bin", [Morning],
        [
            new Place { Timestamp = Morning, Router = 0x95313127c39c, Hint = "omegapoint.se" },
            new Place { Timestamp = Morning + 10, Router = 0xa1b2c3d4e5f6, Hint = "Not Valid" },
            new Place { Timestamp = Morning + 20, Router = 0, Hint = "no-router.example" },
            new Place { Timestamp = Morning + 30, Router = 0x95313127c39d, Hint = "localdomain" },
        ]);

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            """{"zone":"Europe/Stockholm","items":[{"timestamp":1760256000,"router":"95313127c39c","flags":0,"hint":"omegapoint.se"},{"timestamp":1760256010,"router":"a1b2c3d4e5f6","flags":0},{"timestamp":1760256020,"router":null,"flags":0},{"timestamp":1760256030,"router":"95313127c39d","flags":0}]}""",
            api.Requests.Single(request => request.Uri.EndsWith("/places?api-version=1.5")).Body);
    }

    [Fact]
    public async Task A_windowed_sync_does_not_open_files_outside_its_window()
    {
        Location.SetOn(true, Path.Combine(folder, "location.txt"));

        // A full compare ran a moment ago, so this pass is windowed.
        File.WriteAllText(Path.Combine(folder, "reconcile.txt"), DateTimeOffset.UtcNow.ToString("O"));

        var now = DateTimeOffset.UtcNow;
        var recent = (uint)now.AddHours(-1).ToUnixTimeSeconds();
        var old = now.AddDays(-30);

        // Its point is recent: were the file read, the point would be
        // uploaded.  Only its name says it is outside the window.
        WriteFile(FileName((uint)old.ToUnixTimeSeconds()), [], (recent - 60, 0xdeadbeef0001, 0));

        // The file in effect when the window starts is read; its old
        // point falls before the window and is not compared.
        WriteFile(FileName((uint)old.AddDays(1).ToUnixTimeSeconds()), [], ((uint)old.AddDays(1).ToUnixTimeSeconds(), 0xdeadbeef0002, 0));

        WriteFile(FileName(recent), [recent], (recent, 0x95313127c39c, 0));

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.StartsWith($"/{ComputerId}/summary?api-version=1.5&since=", api.Requests[0].Uri);

        var routers = api.Requests
            .Where(request => request.Uri.EndsWith("/places?api-version=1.5"))
            .SelectMany(request => JsonDocument.Parse(request.Body!).RootElement.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("router").GetString())
            .ToArray();

        Assert.Equal(["95313127c39c"], routers);
    }

[Fact]
    public async Task A_windowed_activity_sync_does_not_open_files_outside_its_window()
    {
        // A full compare ran a moment ago, so this pass is windowed.
        File.WriteAllText(Path.Combine(folder, "reconcile.txt"), DateTimeOffset.UtcNow.ToString("O"));

        var now = DateTimeOffset.UtcNow;
        var recent = (uint)now.AddHours(-1).ToUnixTimeSeconds();
        var old = now.AddDays(-30);

        // Recent measurements the server lacks: were the file read, they
        // would be uploaded.  Only its name puts it outside the window.
        WriteFile(FileName((uint)old.ToUnixTimeSeconds()), [recent - 120, recent - 60]);

        // The file in effect when the window starts is read; its day is
        // before the window and not compared.
        WriteFile(FileName((uint)old.AddDays(1).ToUnixTimeSeconds()), [(uint)old.AddDays(1).ToUnixTimeSeconds()]);

        WriteFile(FileName(recent), [recent]);

        var api = new FakeApi
        {
            Respond = request => request.Method == HttpMethod.Get ? Json(EmptySummary) : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.StartsWith($"/{ComputerId}/summary?api-version=1.4&since=", api.Requests[0].Uri);
        Assert.Equal(
            $$"""{"zone":"Europe/Stockholm","items":[{"kind":1,"timestamp":{{recent}}}]}""",
            api.Requests.Single(request => request.Method == HttpMethod.Patch).Body);
    }

        [Fact]
    public void The_window_reads_from_the_day_before_it_in_utc()
    {
        Assert.Null(PlaceSync.From(null));
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), PlaceSync.From(new DateOnly(2026, 10, 6)));
    }

        private sealed class ListLogger(List<string> lines) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
