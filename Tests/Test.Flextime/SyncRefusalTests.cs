using System.Net;
using System.Text;
using Flextime;
using Flextime.Daemon;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Flextime;

public class SyncRefusalTests : IDisposable
{
    private const string ComputerId = "0123456789abcdef";

    // 2025-10-12 08:00 UTC, and the same time the day after.
    private const uint Monday = 1760256000;
    private const uint Tuesday = Monday + 86400;

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-sync-refusal-tests-{Guid.NewGuid():N}");

    private string Measurements => Path.Combine(folder, "measurements");

    private string ReconcilePath => Path.Combine(folder, "reconcile.txt");

    public SyncRefusalTests()
    {
        Directory.CreateDirectory(Measurements);

        var file = new Measurements { Interval = 60, Zone = "Europe/Stockholm" };

        file.Measurements_.AddRange(new[] { Monday, Monday + 60, Tuesday, Tuesday + 60 }
            .Select(timestamp => new Measurement { Timestamp = timestamp, Kind = Measurement.Types.Kind.Measurement }));

        using var stream = File.Create(Path.Combine(Measurements, "2025-10-12T080000.bin"));

        file.WriteTo(stream);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private sealed class FakeApi(Func<string, HttpResponseMessage> upload) : HttpMessageHandler
    {
        public List<string> Uploads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"name":"Test","items":[]}""", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);

            Uploads.Add(body);

            return upload(body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.example/") };
    }

    private Sync CreateSync(FakeApi api) =>
        new(new Factory(api), new Computer(ComputerId, "Test"))
        {
            Folder = Measurements,
            ReconcilePath = ReconcilePath,
            LocationPath = Path.Combine(folder, "location.txt"),
            LocationClearPath = Path.Combine(folder, "location-clear.txt"),
            ListenRunning = () => false,
        };

    // Refuses the first day with the given answer and takes the rest.
    private static FakeApi RefusingMonday(HttpStatusCode status, string body = "") =>
        new(upload => upload.Contains($"{Monday}")
            ? new HttpResponseMessage(status) { Content = new StringContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NoContent));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void A_refusal_of_the_day_is_skipped(HttpStatusCode status)
    {
        Assert.True(Sync.IsRefusal(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Anything_else_is_not_a_refusal(HttpStatusCode status)
    {
        Assert.False(Sync.IsRefusal(status));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  \n ", null)]
    [InlineData("Unknown zone.", "Unknown zone.")]
    [InlineData("First line\nSecond line", "First line")]
    [InlineData("""{"title":"Bad Request","status":400}""", "Bad Request")]
    [InlineData("""{"title":"Bad Request","detail":"Unknown zone."}""", "Unknown zone.")]
    [InlineData("""{"status":400}""", """{"status":400}""")]
    [InlineData("{not json", "{not json")]
    public void Reason_is_short_and_from_the_body(string? body, string? expected)
    {
        Assert.Equal(expected, Sync.Reason(body));
    }

    [Fact]
    public void A_long_reason_is_cut()
    {
        var reason = Sync.Reason(new string('x', 500))!;

        Assert.Equal(80, reason.Length);
        Assert.EndsWith("...", reason);
    }

    [Fact]
    public async Task A_refused_day_does_not_stop_the_days_after_it()
    {
        var api = RefusingMonday(HttpStatusCode.BadRequest, "Unknown zone.");
        var lines = new List<string>();

        await CreateSync(api).SyncAndLog(new ListLogger(lines));

        Assert.Equal(2, api.Uploads.Count);
        Assert.Contains(lines, line => line.StartsWith("2025-10-12", StringComparison.Ordinal)
                                       && line.Contains("[refused Europe/Stockholm 400: Unknown zone.]"));
        Assert.Contains(lines, line => line.StartsWith("2025-10-13", StringComparison.Ordinal)
                                       && line.EndsWith("[synced]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pass_with_only_refusals_moves_the_reconcile_stamp()
    {
        await CreateSync(RefusingMonday(HttpStatusCode.UnprocessableEntity)).SyncAndLog(NullLogger.Instance);

        Assert.True(File.Exists(ReconcilePath));
    }

    [Fact]
    public async Task A_refused_day_is_tried_again_next_pass()
    {
        // Nothing is remembered locally for a refused day; the summary
        // still lacks it, so the next pass over it sends it again.
        var api = RefusingMonday(HttpStatusCode.BadRequest);
        var sync = CreateSync(api);

        await sync.SyncAndLog(NullLogger.Instance);

        api.Uploads.Clear();

        // The day is older than the window, so it is the next full pass,
        // a day later, that covers it.
        File.Delete(ReconcilePath);

        await sync.SyncAndLog(NullLogger.Instance);

        Assert.Contains(api.Uploads, upload => upload.Contains($"{Monday}"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Anything_else_ends_the_pass_as_before(HttpStatusCode status)
    {
        var api = RefusingMonday(status);

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSync(api).SyncAndLog(NullLogger.Instance));

        Assert.Single(api.Uploads);
        Assert.False(File.Exists(ReconcilePath));
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
