using Humanizer;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Polly.Timeout;
using Spectre.Console;

namespace Flextime.Daemon;

public class PrintInfo(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, Computer computer, Sync sync)
{
    public async Task Invoke()
    {
        var httpClient = httpClientFactory.CreateClient("ApiHttpClient");
        
        // I want the nice spinners on Windows
        // https://github.com/spectreconsole/spectre.console/issues/391
        Console.OutputEncoding = Encoding.UTF8;

        var version = VersionHelper.GetVersion();

        AnsiConsole.MarkupLineInterpolated($"Client version  :  {version ?? "Unknown"}");
        AnsiConsole.MarkupLineInterpolated($"Hostname        :  {TerminalText.Clean(computer.Hostname)}");
        AnsiConsole.MarkupLineInterpolated($"Computer id     :  {computer.Id}");
        AnsiConsole.MarkupLineInterpolated($"Time zone       :  {PrintTimeZone()}");
        AnsiConsole.MarkupLineInterpolated($"Measurements    :  {Path.GetFullPath(computer.MeasurementsFolder)}");

        var logs = LogsStatus();

        if (logs != null)
        {
            AnsiConsole.MarkupLineInterpolated($"Logs            :  {logs}");
        }

        AnsiConsole.MarkupLineInterpolated($"Local data      :  {LocalDataStatus()}");
        AnsiConsole.MarkupLineInterpolated($"Listen process  :  {ListenStatus()}");
        AnsiConsole.MarkupLineInterpolated($"Sync schedule   :  {SyncStatus()}");
        AnsiConsole.MarkupLineInterpolated($"Location        :  {LocationStatus()}");
        AnsiConsole.MarkupLineInterpolated($"Server URL      :  {Constants.ApiUri}");

        // Lines are printed after the spinner stops: a line wider than the
        // terminal, written while the spinner is live, leaves the spinner's
        // text behind on the screen.
        string? serverVersion = null;
        // Why the server was not asked for the days: the version request
        // already failed, and asking again would fail the same way.
        string? notFetched = null;
        var expired = false;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Plain) // No colors
            .StartAsync("Fetching server version...", async _ =>
            {
                try
                {
                    var pingResult = await httpClient.GetFromJsonAsync("/ping", PingSourceGenerationContext.Default.PingDataContract);

                    serverVersion = TerminalText.Clean(pingResult?.Version);
                }
                catch (TokenRefreshException)
                {
                    // Every request refreshes the sign-in first; the server
                    // itself was never asked.
                    expired = true;
                    notFetched = "Not fetched, the sign-in has expired";
                }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                {
                    notFetched = NotFetched(exception);
                    serverVersion = Failure(exception);
                }
            });

        AnsiConsole.MarkupLineInterpolated($"Server version  :  {serverVersion ?? notFetched}");

        if (deviceCode.IsAuthenticated)
        {
            var (accessToken, _, _) = await TokenStorage.Read();

            if (expired)
            {
                AnsiConsole.MarkupLine("Signed in       :  Expired. Use the login command to log in again.");
            }
            else if (string.IsNullOrEmpty(accessToken) || !accessToken.Contains('.'))
            {
                AnsiConsole.MarkupLine("Signed in       :  Yes");
            }
            else
            {
                var user = GetUserInfo(accessToken);
                AnsiConsole.MarkupLineInterpolated($"Signed in       :  {TerminalText.Clean(user)}");
            }

            if (notFetched != null)
            {
                AnsiConsole.MarkupLineInterpolated($"Last 5 days     :  {notFetched}");

                return;
            }

            string? failure = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Plain) // No colors
                .StartAsync("Fetching server data...",
                    async _ =>
                    {
                        AnsiConsole.MarkupLine("Last 5 days     :");
                        AnsiConsole.WriteLine();

                        try
                        {
                            await PrintSummary(5);
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

            if (failure != null)
            {
                AnsiConsole.MarkupLineInterpolated($"{failure}");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("Logged in       :  No. Use login command to log in.");
        }
    }

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

    private static string GetUserInfo(string accessToken)
    {
        var token = new JwtSecurityToken(accessToken);

        var name = token.Claims.FirstOrDefault(claim => claim.Type == "name")?.Value;
        var email = token.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value;

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
        {
            return "Yes.";
        }

        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(email))
        {
            return email;
        }

        if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
        {
            return name;
        }
        
        return $"{name} <{email}>";
    }
    
    private async Task PrintSummary(int count)
    {
        await sync.Print(count);
    }

    private static string ListenStatus()
    {
        if (SingleInstance.IsHeld(SingleInstance.ListenLockPath))
        {
            // Written by the running listen process, which holds the lock.
            return StateFiles.TryRead(StateFiles.ListenPath) is [var zone, ..]
                ? $"Running ({zone})"
                : "Running";
        }

        // Written by the stop command; removed by start.
        return StateFiles.TryRead(StateFiles.StopPath) is [var date, ..]
            ? $"Stopped {date}. Use start command to start again."
            : "Not running";
    }

    private static string LocationStatus()
    {
        var on = Location.IsOn() ? "On" : "Off";

        return Location.ReadClear() is { Pending: true }
            ? $"{on}, clearing the server's place history at the next sync"
            : on;
    }

    private static string SyncStatus()
    {
        if (SingleInstance.IsHeld(SingleInstance.SyncLockPath))
        {
            // A sync --every loop is running and published its interval.
            return StateFiles.TryRead(StateFiles.SyncPath) is [var every, ..]
                ? $"Running (every {HumanizeInterval(every)})"
                : "Running";
        }

        // No loop; the install command may have registered a schedule.
        return StateFiles.TryRead(StateFiles.InstallPath) is [var interval, var date, ..]
            ? $"Every {HumanizeInterval(interval)} (installed {date})"
            : "Not scheduled";
    }

    // How far the local record reaches, and what it costs on disk.
    private static string LocalDataStatus()
    {
        var byDates = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);

        if (byDates.Count == 0)
        {
            return "None";
        }

        long bytes;

        try
        {
            bytes = Directory.EnumerateFiles(Constants.MeasurementsFolder, "*.bin")
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The days are worth showing even when the size is not.
            return $"{byDates.Count} days, {byDates.Keys.First():yyyy-MM-dd} – {byDates.Keys.Last():yyyy-MM-dd}";
        }

        return $"{byDates.Count} days, {byDates.Keys.First():yyyy-MM-dd} – {byDates.Keys.Last():yyyy-MM-dd}, {FormatBytes(bytes)}";
    }

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PingDataContract))]
internal partial class PingSourceGenerationContext : JsonSerializerContext;
