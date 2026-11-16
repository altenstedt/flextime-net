using System.Net;
using System.Text;
using Flextime;
using Flextime.Daemon;
using Google.Protobuf;

namespace Test.Flextime;

// The pause and resume commands, against a temporary state folder and a
// fake server: the pause must stand whatever happens to the mark.
public class PauseCommandTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-pause-command-tests-{Guid.NewGuid():N}");

    private static readonly TimeZoneInfo Stockholm = TimeZones.Get("Europe/Stockholm");

    // 2026-11-16 19:44:30 in Stockholm.
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 18, 44, 30, TimeSpan.Zero);

    private readonly StringWriter output = new();
    private readonly StringWriter errors = new();

    public PauseCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "measurements"));
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private string PausePath => Path.Combine(folder, "pause.txt");
    private string PendingPath => Path.Combine(folder, "pause-mark.txt");

    private sealed class FakeServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            return answer(request, cancellationToken);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.example/") };
    }

    private static FakeServer Accepting() => new((request, _) => Task.FromResult(request.Method == HttpMethod.Get
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") }
        : new HttpResponseMessage(HttpStatusCode.NoContent)));

    private PauseCommand Command(FakeServer? server = null, bool authenticated = true, TimeSpan? timeout = null) =>
        new(new Factory(server ?? Accepting()), new DeviceCode())
        {
            PausePath = PausePath,
            StopPath = Path.Combine(folder, "stop.txt"),
            MeasurementsFolder = Path.Combine(folder, "measurements"),
            Pending = new PendingMarks(PendingPath, Path.Combine(folder, "pause-mark.lock")),
            Now = () => Now,
            Zone = Stockholm,
            ZoneId = () => "Europe/Stockholm",
            IsAuthenticated = () => authenticated,
            NetworkTimeout = timeout ?? TimeSpan.FromSeconds(10),
            Out = output,
            Error = errors,
        };

    // Every minute from minutesAgo up to now.
    private void Measured(int minutesAgo)
    {
        var file = new Measurements { Interval = 60, Zone = "Europe/Stockholm" };
        var start = Now.AddSeconds(-30).AddMinutes(-minutesAgo).ToUnixTimeSeconds();

        for (var minute = 0; minute <= minutesAgo; minute++)
        {
            file.Measurements_.Add(new Measurement { Timestamp = (uint)(start + minute * 60), Kind = Measurement.Types.Kind.Measurement });
        }

        using var stream = File.Create(Path.Combine(folder, "measurements", "2026-11-16T180000.bin"));

        file.WriteTo(stream);
    }

    private PauseStatus Status() => new PauseReader(new PauseFileSource(PausePath)).Check(Now);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pause_lasts_until_midnight_and_marks_the_stretch_before()
    {
        Measured(3);

        Assert.Equal(0, await Command().Pause(null, null, null, false, Token));

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.FromHours(1)), Status().End);
        Assert.Equal(
            "Paused until 00:00. Use the resume command to measure again sooner.\nMarked 19:41–19:45 as not work.\n",
            output.ToString().ReplaceLineEndings("\n"));
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task A_long_stretch_is_not_marked()
    {
        Measured(45);

        await Command().Pause(null, null, null, false, Token);

        Assert.Contains("Not marking 46 min before the pause as not work", output.ToString());
        Assert.True(Status().IsPaused);
    }

    [Fact]
    public async Task Since_marks_from_the_time_given_whatever_its_length()
    {
        Measured(45);

        var server = Accepting();

        await Command(server).Pause(null, null, "19:00", false, Token);

        Assert.Contains("Marked 19:00–19:45 as not work.", output.ToString());
    }

    [Theory]
    [InlineData("--for", "1h", "--until", "07:00")]
    [InlineData("--since", "19:00", "--no-backdate", "")]
    public async Task Options_that_contradict_write_nothing(string first, string firstValue, string second, string secondValue)
    {
        _ = (firstValue, secondValue);

        var result = await Command().Pause(
            first == "--for" ? "1h" : null,
            second == "--until" ? "07:00" : null,
            first == "--since" ? "19:00" : null,
            second == "--no-backdate",
            Token);

        Assert.Equal(1, result);
        Assert.False(File.Exists(PausePath));
    }

    [Theory]
    [InlineData("49h")]
    [InlineData("3 days")]
    [InlineData("nonsense")]
    public async Task A_length_past_tomorrow_or_unreadable_writes_nothing(string length)
    {
        Assert.Equal(1, await Command().Pause(length, null, null, true, Token));
        Assert.False(File.Exists(PausePath));
    }

    [Theory]
    [InlineData("30s")]
    [InlineData("0.5s")]
    public async Task A_pause_too_short_to_hold_leaves_the_pause_in_force(string length)
    {
        await Command().Pause("2h", null, null, true, Token);

        var before = await File.ReadAllTextAsync(PausePath, Token);

        Assert.Equal(1, await Command().Pause(length, null, null, true, Token));
        Assert.Equal(before, await File.ReadAllTextAsync(PausePath, Token));
        Assert.True(Status().IsPaused);
    }

    [Fact]
    public async Task For_and_until_set_the_end()
    {
        await Command().Pause("2h", null, null, true, Token);
        Assert.Equal(Now.AddHours(2).ToUnixTimeSeconds(), Status().End!.Value.ToUnixTimeSeconds());

        await Command().Pause(null, "07:00", null, true, Token);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.FromHours(1)), Status().End);
    }

    [Theory]
    [InlineData("21:00")]
    [InlineData("2d")]
    [InlineData("tomorrow")]
    public async Task A_since_in_the_future_too_far_back_or_unreadable_writes_nothing(string since)
    {
        Assert.Equal(1, await Command().Pause(null, null, since, false, Token));
        Assert.False(File.Exists(PausePath));
    }

    [Fact]
    public async Task A_stopped_listen_is_not_paused()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "stop.txt"), "2026-11-16", Token);

        Assert.Equal(1, await Command().Pause(null, null, null, false, Token));
        Assert.False(File.Exists(PausePath));
        Assert.Contains("Listen is stopped", errors.ToString());
    }

    [Fact]
    public async Task A_hanging_server_does_not_hold_up_the_pause_and_the_mark_waits_for_sync()
    {
        Measured(3);

        var server = new FakeServer(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var started = DateTimeOffset.UtcNow;

        Assert.Equal(0, await Command(server, timeout: TimeSpan.FromMilliseconds(200)).Pause(null, null, null, false, Token));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(Status().IsPaused);
        Assert.Contains("sync marks 19:41–19:45 as not work later", output.ToString());
        Assert.Single(File.ReadAllLines(PendingPath));
    }

    [Fact]
    public async Task An_interrupted_pause_keeps_the_pause_and_the_mark()
    {
        Measured(3);

        using var interrupt = new CancellationTokenSource();
        var server = new FakeServer(async (_, cancellationToken) =>
        {
            await interrupt.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Command(server).Pause(null, null, null, false, interrupt.Token);

        Assert.True(Status().IsPaused);
        Assert.Single(File.ReadAllLines(PendingPath));
    }

    [Fact]
    public async Task Logged_out_the_mark_waits_for_sync()
    {
        Measured(3);

        var server = Accepting();

        await Command(server, authenticated: false).Pause(null, null, null, false, Token);

        Assert.Equal(0, server.Requests);
        Assert.Single(File.ReadAllLines(PendingPath));
    }

    [Fact]
    public async Task No_backdate_never_reaches_the_server()
    {
        Measured(3);

        var server = Accepting();

        await Command(server).Pause(null, null, null, true, Token);

        Assert.Equal(0, server.Requests);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task A_pause_path_that_cannot_hold_a_pause_says_so()
    {
        Directory.CreateDirectory(PausePath);

        Assert.Equal(1, await Command().Pause(null, null, null, true, Token));
    }

    [Fact]
    public async Task Resume_ends_the_pause_and_leaves_the_marks()
    {
        Measured(3);

        await Command().Pause(null, null, null, false, Token);
        await File.WriteAllTextAsync(PendingPath, "kept", Token);

        Assert.Equal(0, Command().Resume());

        Assert.False(File.Exists(PausePath));
        Assert.Equal("kept", await File.ReadAllTextAsync(PendingPath, Token));
        Assert.EndsWith("Measuring again from the next minute.\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Resume_without_a_pause_says_so_and_succeeds()
    {
        Assert.Equal(0, Command().Resume());
        Assert.Equal("Not paused.\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Resume_removes_a_file_listen_ignores()
    {
        await File.WriteAllTextAsync(PausePath, "garbage", Token);

        Assert.Equal(0, Command().Resume());
        Assert.False(File.Exists(PausePath));
    }

    [Fact]
    public void Info_says_when_listen_is_paused_or_ignores_the_file()
    {
        var paused = new StatusListenDataContract("running", "Europe/Stockholm", null, Now,
            PausedUntil: new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.FromHours(1)));

        Assert.StartsWith("Paused until ", PrintInfo.ListenStatus(paused, Now));
        Assert.EndsWith("Use resume command to measure again.", PrintInfo.ListenStatus(paused, Now));

        var ignored = new StatusListenDataContract("running", "Europe/Stockholm", null, null, PauseIgnored: "is empty");

        Assert.Equal("Running; pause file ignored, it is empty", PrintInfo.ListenStatus(ignored, Now));
    }
}
