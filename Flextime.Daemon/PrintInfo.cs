using Humanizer;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;
using Spectre.Console;

namespace Flextime.Daemon;

public class PrintInfo(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, Computer computer, Sync sync)
{
    public async Task<int> Invoke(int? days = null, bool noColor = false, bool json = false,
        CancellationToken cancellationToken = default)
    {
        var httpClient = httpClientFactory.CreateClient("ApiHttpClient");

        // The spinner frames and the en dash are not in every Windows
        // code page.
        Console.OutputEncoding = Encoding.UTF8;

        var text = !json;
        var colour = text && ColourWanted(noColor, Environment.GetEnvironmentVariable("NO_COLOR"))
            && !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Ansi;
        var now = DateTimeOffset.Now;
        // Any red row makes the exit code 1, so a script can tell.
        var bad = false;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void Print(string label, string? value, Tone tone = Tone.Plain)
        {
            bad |= tone == Tone.Bad;

            if (text)
            {
                Row(label, colour ? Paint(value, tone) : value);
            }
        }

        // Ctrl+C ends the wait, and then cancels the request: left
        // running, disposing the host on the way out waits for it.
        using var abandon = new CancellationTokenSource();

        Task Fetch(string label, Func<Task> work) =>
            text ? Spin(label, work, abandon, cancellationToken) : Until(work(), abandon, cancellationToken);

        // Whether it works, in the order the data flows: recorded,
        // scheduled for upload, allowed to upload, arrived.

        var local = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);
        var listen = ReadListen(LastMinute(local));

        Print("Listen process", ListenStatus(listen, now), listen.State switch
        {
            "running" => Tone.Plain,
            "stopped" => Tone.Warn,
            _ => Tone.Bad,
        });

        var schedule = ReadSync();

        Print("Sync schedule", SyncStatus(schedule), schedule.State == "notScheduled" ? Tone.Bad : Tone.Plain);

        string? serverVersion = null;
        string? serverError = null;
        // Why the server was not asked for the days: the version request
        // already failed, and asking again would fail the same way.
        string? notFetched = null;
        var expired = false;

        // The ping refreshes the sign-in first, so it is what finds an
        // expired one.
        await Fetch("Signed in", async () =>
        {
            try
            {
                var pingResult = await httpClient.GetFromJsonAsync("/ping", PingSourceGenerationContext.Default.PingDataContract, abandon.Token);

                serverVersion = TerminalText.Clean(pingResult?.Version);
            }
            catch (TokenRefreshException)
            {
                // The server itself was never asked.
                expired = true;
                notFetched = "Not fetched, the sign-in has expired";
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
            {
                notFetched = NotFetched(exception);
                serverError = Failure(exception);
            }
        });

        var signIn = await ReadSignIn(deviceCode.IsAuthenticated, expired);

        Print("Signed in", SignInStatus(signIn), signIn.State == "yes" ? Tone.Plain : Tone.Bad);

        if (!deviceCode.IsAuthenticated)
        {
            notFetched = "Not fetched, not logged in";
        }

        List<Sync.DayResult>? compared = null;
        var failure = notFetched;

        if (notFetched == null)
        {
            await Fetch("Server data", async () =>
            {
                try
                {
                    compared = await sync.Compare(abandon.Token);
                }
                catch (TokenRefreshException exception)
                {
                    failure = TerminalText.Clean(exception.Message);
                }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                {
                    failure = Failure(exception);
                }
            });
        }

        if (compared == null)
        {
            // Not logged in or expired is already said, in red, above.
            Print("Server data", failure, signIn.State == "yes" ? Tone.Warn : Tone.Plain);
        }
        else
        {
            var (line, rows) = ServerData(compared, now, Grace(schedule), days);

            Print("Server data", line,
                compared.Any(day => !Fine(day, now, Grace(schedule))) ? Tone.Bad : Tone.Plain);

            foreach (var row in text ? rows : [])
            {
                Console.WriteLine(row);
            }
        }

        var location = new StatusLocationDataContract(Location.IsOn(), Location.ReadClear() is { Pending: true });

        Print("Location", LocationStatus(location));

        // What it is and where, for looking up now and then.

        var version = VersionHelper.GetVersion();
        var zone = TimeZoneId();
        var localData = ReadLocalData(local);
        var measurements = Path.GetFullPath(computer.MeasurementsFolder);
        var logs = LogsStatus();

        if (text)
        {
            Console.WriteLine();
        }

        Print("Hostname", TerminalText.Clean(computer.Hostname));
        Print("Computer id", computer.Id);
        Print("Time zone", TimeZoneStatus(PrintTimeZone(), zone, listen.Zone));
        Print("Local data", LocalDataStatus(localData));
        Print("Client version", version ?? "Unknown");
        Print("Server version", serverVersion != null ? ShortVersion(serverVersion) : serverError ?? notFetched,
            serverVersion != null ? Tone.Plain : Tone.Warn);
        Print("Server URL", Constants.ApiUri.ToString());
        Print("Measurements", HomeRelative(measurements, home));

        if (logs != null)
        {
            Print("Logs", HomeRelative(logs, home));
        }

        if (json)
        {
            var dataContract = new StatusDataContract(
                listen,
                schedule,
                signIn,
                ServerDataContract(compared, failure, now, Grace(schedule), days),
                location,
                TerminalText.Clean(computer.Hostname),
                computer.Id,
                zone,
                localData,
                version,
                serverVersion,
                serverError,
                Constants.ApiUri.ToString(),
                measurements,
                logs);

            Console.WriteLine(ToJson(dataContract));
        }

        return bad ? 1 : 0;
    }

    // Relaxed escaping: the output goes to a terminal or a script, never
    // into HTML, and "7.18.5\u002B12ae77c" helps neither.
    private static readonly StatusSourceGenerationContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string ToJson(StatusDataContract status) =>
        JsonSerializer.Serialize(status, JsonContext.StatusDataContract);

    // How long today's newest minutes may wait for an upload before it
    // counts as out of sync: a few sync passes, and never under a
    // quarter of an hour.
    public static TimeSpan Grace(StatusSyncDataContract schedule)
    {
        var floor = TimeSpan.FromMinutes(15);

        return schedule.Every is { } every && TimeSpan.TryParse(every, CultureInfo.InvariantCulture, out var interval)
               && interval * 3 > floor
            ? interval * 3
            : floor;
    }

    // Which rows to list besides the ones out of sync: none, all for 0,
    // or the latest n.
    private static int FirstListed(int count, int? latest) => latest switch
    {
        null => count,
        0 => 0,
        var n => Math.Max(0, count - n.Value),
    };

    public enum Tone
    {
        Plain,
        Warn,
        Bad,
    }

    // https://no-color.org: set and not empty, whatever its value.
    public static bool ColourWanted(bool noColorOption, string? noColorVariable) =>
        !noColorOption && string.IsNullOrEmpty(noColorVariable);

    private static string? Paint(string? value, Tone tone) => tone switch
    {
        Tone.Bad => $"\e[31m{value}\e[0m",
        Tone.Warn => $"\e[33m{value}\e[0m",
        _ => value,
    };

    // The git hash is cut to the length the client's own version has.
    public static string ShortVersion(string version) =>
        version.Split('+') is [var number, { Length: > 7 } hash] ? $"{number}+{hash[..7]}" : version;

    // Under the home folder, as ~/...; not on Windows, where cmd does
    // not know ~.
    public static string HomeRelative(string path, string home)
    {
        var root = home.TrimEnd('/');

        // A home of / would make every path look like it is under it.
        return !OperatingSystem.IsWindows() && root.Length > 0
            && path.StartsWith(root + "/", StringComparison.Ordinal)
            ? "~" + path[root.Length..]
            : path;
    }

    // Waits for the task or Ctrl+C, and on Ctrl+C cancels abandon, which
    // the task's requests take.  The signal is noticed on a pool thread,
    // never inline in ConsoleAppFramework's signal handler: unwinding
    // there lets the app dispose the handler's timeout source before the
    // handler calls CancelAfter on it, which crashes with
    // ObjectDisposedException (ConsoleAppFramework 5.8.1).  That is also
    // why the requests do not take the signal's token directly.
    private static async Task Until(Task task, CancellationTokenSource abandon, CancellationToken cancellationToken)
    {
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (cancellationToken.Register(() => stop.TrySetResult()))
        {
            if (await Task.WhenAny(task, stop.Task) != task)
            {
                await abandon.CancelAsync();

                throw new OperationCanceledException(cancellationToken);
            }
        }

        await task;
    }

    // Spinner frames, Spectre's Dots2.
    private static readonly string[] Frames = ["⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷"];

    // Runs work with a spinner where the row's value will go.  The row
    // itself is printed by the caller once the work is done, over the
    // spinner: only the label and one character are ever drawn, so the
    // line cannot wrap and leave a stray copy behind.  Not drawn when
    // the output is not a terminal.
    private static async Task Spin(string label, Func<Task> work, CancellationTokenSource abandon,
        CancellationToken cancellationToken)
    {
        var task = Until(work(), abandon, cancellationToken);

        if (Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Ansi)
        {
            await task;

            return;
        }

        var frame = 0;

        Console.Write("\e[?25l");

        try
        {
            while (!task.IsCompleted)
            {
                Console.Write($"\r{label,-16}:  {Frames[frame++ % Frames.Length]}");

                await Task.WhenAny(task, Task.Delay(80, CancellationToken.None));
            }
        }
        finally
        {
            // Also after Ctrl+C, which ends the wait above.
            Console.Write("\r\e[K\e[?25h");
        }

        await task;
    }

    // Written past Spectre, which wraps a line at the terminal's width
    // and so breaks a long path in two when it is copied.
    private static void Row(string label, string? value) => Console.WriteLine($"{label,-16}:  {value}");

    // One line for every local day compared, and the rows worth
    // showing: the days that differ, and the latest days when asked
    // for.  Today not yet on the server is how it should look between
    // two syncs.
    public static (string Line, List<string> Rows) ServerData(
        IReadOnlyList<Sync.DayResult> days, DateTimeOffset now, TimeSpan grace, int? latest)
    {
        if (days.Count == 0)
        {
            return ("No local days to compare", []);
        }

        var differ = days.Count(day => !Fine(day, now, grace));

        var line = (differ, days.Count) switch
        {
            (0, 1) => "1 day in sync",
            (0, var n) => $"{n} days in sync",
            (var k, var n) => $"{k} of {n} {(n == 1 ? "day" : "days")} out of sync",
        };

        var first = FirstListed(days.Count, latest);

        return (line, [.. days
            .Where((day, index) => index >= first || !Fine(day, now, grace))
            .Select(day => day.Line)]);
    }

    // A day may lag the server by the minutes since the last sync, but
    // not by more than the grace: then the uploads have stopped.  By
    // the minute, not the date: just after midnight yesterday still
    // waits, and a day in another zone has another date.  A mismatch
    // with nothing newer than the server's last minute is not waiting
    // for anything, so it is out of sync.
    private static bool Fine(Sync.DayResult day, DateTimeOffset now, TimeSpan grace) => day.Status switch
    {
        Sync.DayStatus.InSync => true,
        Sync.DayStatus.CanSync or Sync.DayStatus.LocalOnly or Sync.DayStatus.Synced =>
            day.Waiting is { } waiting && now - DateTimeOffset.FromUnixTimeSeconds(waiting) <= grace,
        _ => false,
    };

    // The newest measurement on this computer, in the zone it was
    // recorded in.
    private static DateTimeOffset? LastMinute(
        SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)> local) =>
        local.Count == 0 ? null : local.Last().Value.list.Max(item => item.Timestamp);

    // "08:04" today, the date as well before that.
    public static string FormatLastMinute(DateTimeOffset last, DateTimeOffset now) =>
        DateOnly.FromDateTime(last.DateTime) == DateOnly.FromDateTime(now.DateTime)
            ? last.ToString("HH:mm", CultureInfo.InvariantCulture)
            : last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // One short line for the status column.  Polly's timeout message
    // and its "A task was canceled" inner exception say less than this,
    // and a server that answered with an error was reachable.
    public static string Failure(Exception exception) => exception switch
    {
        TimeoutRejectedException timeout =>
            $"Not reachable, no answer within {timeout.Timeout.Humanize(culture: CultureInfo.InvariantCulture)}",
        HttpRequestException { StatusCode: { } status } => $"Server error {(int)status}",
        _ => $"Not reachable, {TerminalText.Clean(exception.Message).TrimEnd('.')}",
    };

    // Why the days were not asked for, after the version request failed.
    public static string NotFetched(Exception exception) => exception is HttpRequestException { StatusCode: { } status }
        ? $"Not fetched, the server answered {(int)status}"
        : "Not fetched, the server is not reachable";

    // Every day that is out of sync, and the latest days when asked for.
    private static StatusServerDataContract ServerDataContract(
        List<Sync.DayResult>? compared, string? failure, DateTimeOffset now, TimeSpan grace, int? latest)
    {
        if (compared == null)
        {
            return new StatusServerDataContract(null, null, failure, []);
        }

        var first = FirstListed(compared.Count, latest);

        var items = compared
            .Select((day, index) => (day, index, inSync: Fine(day, now, grace)))
            .Where(item => item.index >= first || !item.inSync)
            .Select(item => new StatusDayDataContract(item.day.Date, Camel(item.day.Status.ToString()), item.inSync))
            .ToArray();

        return new StatusServerDataContract(
            compared.Count,
            compared.Count(day => !Fine(day, now, grace)),
            null,
            items);
    }

    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static async Task<StatusSignInDataContract> ReadSignIn(bool authenticated, bool expired)
    {
        if (!authenticated)
        {
            return new StatusSignInDataContract("no", null, null);
        }

        if (expired)
        {
            return new StatusSignInDataContract("expired", null, null);
        }

        var (accessToken, _, _) = await TokenStorage.Read();

        if (string.IsNullOrEmpty(accessToken) || !accessToken.Contains('.'))
        {
            return new StatusSignInDataContract("yes", null, null);
        }

        var token = new JwtSecurityToken(accessToken);

        var name = token.Claims.FirstOrDefault(claim => claim.Type == "name")?.Value;
        var email = token.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value;

        return new StatusSignInDataContract(
            "yes",
            string.IsNullOrEmpty(name) ? null : TerminalText.Clean(name),
            string.IsNullOrEmpty(email) ? null : TerminalText.Clean(email));
    }

    private static string SignInStatus(StatusSignInDataContract signIn) => signIn switch
    {
        { State: "no" } => "No. Use the login command to log in.",
        { State: "expired" } => "Expired. Use the login command to log in again.",
        { Name: { } name, Email: { } email } => $"{name} <{email}>",
        { Name: { } name } => name,
        { Email: { } email } => email,
        _ => "Yes",
    };

    private static StatusListenDataContract ReadListen(DateTimeOffset? lastMinute)
    {
        if (SingleInstance.IsHeld(SingleInstance.ListenLockPath))
        {
            // Written by the running listen process, which holds the
            // lock; a file left by one that has stopped says nothing
            // about now.
            var zone = StateFiles.TryRead(StateFiles.ListenPath) is [var value, ..] ? TerminalText.Clean(value) : null;

            return new StatusListenDataContract("running", zone, null, lastMinute);
        }

        // Written by the stop command; removed by start.
        return StateFiles.TryRead(StateFiles.StopPath) is [var date, ..]
            ? new StatusListenDataContract("stopped", null, TerminalText.Clean(date), lastMinute)
            : new StatusListenDataContract("notRunning", null, null, lastMinute);
    }

    public static string ListenStatus(StatusListenDataContract listen, DateTimeOffset now)
    {
        var lastMinute = listen.LastMinute is { } value ? $", last minute {FormatLastMinute(value, now)}" : "";

        return listen.State switch
        {
            "running" => $"Running{lastMinute}",
            "stopped" => $"Stopped {listen.StoppedOn}. Use start command to start again.",
            _ => $"Not running{lastMinute}",
        };
    }

    // Listen follows the zone, but only when it notices the change, so
    // for a while after travel it can still record in the old one.
    // Named only then.
    // Listen publishes the IANA id, so that is what it is compared with;
    // the shown text can say more, like the Windows id it came from.
    public static string TimeZoneStatus(string shown, string ianaId, string? listenZone) =>
        listenZone is null || listenZone == ianaId
            ? shown
            : $"{shown}, listen records in {TerminalText.Clean(listenZone)}";

    private static string LocationStatus(StatusLocationDataContract location)
    {
        var on = location.On ? "On" : "Off";

        return location.ClearPending
            ? $"{on}, clearing the server's place history at the next sync"
            : on;
    }

    private static StatusSyncDataContract ReadSync()
    {
        if (SingleInstance.IsHeld(SingleInstance.SyncLockPath))
        {
            // A sync --every loop is running and published its interval.
            return new StatusSyncDataContract("running",
                StateFiles.TryRead(StateFiles.SyncPath) is [var every, ..] ? TerminalText.Clean(every) : null, null);
        }

        // No loop; the install command may have registered a schedule.
        return StateFiles.TryRead(StateFiles.InstallPath) is [var interval, var date, ..]
            ? new StatusSyncDataContract("scheduled", TerminalText.Clean(interval), TerminalText.Clean(date))
            : new StatusSyncDataContract("notScheduled", null, null);
    }

    private static string SyncStatus(StatusSyncDataContract schedule) => schedule switch
    {
        { State: "running", Every: { } every } => $"Running (every {HumanizeInterval(every)})",
        { State: "running" } => "Running",
        { State: "scheduled", Every: { } every } => $"Every {HumanizeInterval(every)} (installed {schedule.InstalledOn})",
        _ => "Not scheduled",
    };

    // How far the local record reaches, and what it costs on disk.
    private static StatusLocalDataContract ReadLocalData(SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)> byDates)
    {
        if (byDates.Count == 0)
        {
            return new StatusLocalDataContract(0, null, null, null);
        }

        long? bytes;

        try
        {
            bytes = Directory.EnumerateFiles(Constants.MeasurementsFolder, "*.bin")
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The days are worth showing even when the size is not.
            bytes = null;
        }

        return new StatusLocalDataContract(byDates.Count, byDates.Keys.First(), byDates.Keys.Last(), bytes);
    }

    private static string LocalDataStatus(StatusLocalDataContract local) => local switch
    {
        { Days: 0 } => "None",
        { Bytes: { } bytes } => $"{local.Days} days, {local.First:yyyy-MM-dd} – {local.Last:yyyy-MM-dd}, {FormatBytes(bytes)}",
        _ => $"{local.Days} days, {local.First:yyyy-MM-dd} – {local.Last:yyyy-MM-dd}",
    };

    // Where install points the services' output on this platform.  The
    // other platforms leave the log to the service manager itself —
    // journalctl, Event Viewer — so there is no folder to name.
    private static string? LogsStatus()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Flextime");

        return Directory.Exists(folder) ? folder : null;
    }

    private static string FormatBytes(long bytes) => bytes.Bytes().Humanize("0.#");

    // The state files hold TimeSpan round-trip strings like 00:10:00;
    // shown as "10 minutes".  Anything unparsable is shown as written.
    private static string HumanizeInterval(string value)
    {
        return TimeSpan.TryParse(value, out var interval)
            ? interval.Humanize(precision: 2)
            : value;
    }

    // The IANA id, also on Windows when it can be had.
    private static string TimeZoneId() =>
        OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaId)
            ? ianaId
            : TimeZoneInfo.Local.Id;

    private static string PrintTimeZone()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (Icu.IsInUse())
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaId))
                {
                    return
                        $"{ianaId} (converted from {TimeZoneInfo.Local.Id})";
                }

                return $"Windows platform is unable to convert {TimeZoneInfo.Local.Id} to ICU time zone";
            }

            return "Windows platform is not using ICU";
        }

        return TimeZoneInfo.Local.Id;
    }
}

internal record PingDataContract(string Version);

public record StatusListenDataContract(string State, string? Zone, string? StoppedOn, DateTimeOffset? LastMinute);

public record StatusSyncDataContract(string State, string? Every, string? InstalledOn);

public record StatusSignInDataContract(string State, string? Name, string? Email);

public record StatusDayDataContract(DateOnly Date, string Status, bool InSync);

public record StatusServerDataContract(int? Days, int? OutOfSync, string? Error, StatusDayDataContract[] Items);

public record StatusLocationDataContract(bool On, bool ClearPending);

public record StatusLocalDataContract(int Days, DateOnly? First, DateOnly? Last, long? Bytes);

public record StatusDataContract(
    StatusListenDataContract Listen,
    StatusSyncDataContract Sync,
    StatusSignInDataContract SignedIn,
    StatusServerDataContract ServerData,
    StatusLocationDataContract Location,
    string Hostname,
    string? ComputerId,
    string TimeZone,
    StatusLocalDataContract LocalData,
    string? ClientVersion,
    string? ServerVersion,
    string? ServerError,
    string ServerUrl,
    string Measurements,
    string? Logs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusDataContract))]
internal partial class StatusSourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PingDataContract))]
internal partial class PingSourceGenerationContext : JsonSerializerContext;
