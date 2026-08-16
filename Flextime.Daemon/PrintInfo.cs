using Humanizer;
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

        AnsiConsole.MarkupLine($"Client version  :  {version ?? "Unknown"}");
        AnsiConsole.MarkupLine($"Computer name   :  {computer.Name}");
        AnsiConsole.MarkupLine($"Computer id     :  {computer.Id}");
        AnsiConsole.MarkupLine($"Time zone       :  {PrintTimeZone()}");
        AnsiConsole.MarkupLine($"Measurements    :  {Path.GetFullPath(computer.MeasurementsFolder)}");

        var logs = LogsStatus();

        if (logs != null)
        {
            AnsiConsole.MarkupLine($"Logs            :  {logs}");
        }

        AnsiConsole.MarkupLine($"Local data      :  {LocalDataStatus()}");
        AnsiConsole.MarkupLine($"Listen process  :  {ListenStatus()}");
        AnsiConsole.MarkupLine($"Sync schedule   :  {SyncStatus()}");
        AnsiConsole.MarkupLine($"Server URL      :  {Constants.ApiUri}");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Plain) // No colors
            .StartAsync("Fetching server version...", async _ =>
            {
                try
                {
                    var pingResult = await httpClient.GetFromJsonAsync("/ping", PingSourceGenerationContext.Default.PingDataContract);

                    AnsiConsole.MarkupLine($"Server version  :  {pingResult?.Version}");
                }
                catch (TokenRefreshException exception)
                {
                    AnsiConsole.WriteLine(exception.Message);
                }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                {
                    AnsiConsole.MarkupLine($"Error contacting backend: {exception.Message}.");

                    if (exception.InnerException != null)
                    {
                        AnsiConsole.MarkupLine($"  {exception.InnerException.Message}");
                    }
                }
            });

        if (deviceCode.IsAuthenticated)
        {
            var (accessToken, _, _) = await TokenStorage.Read();

            if (string.IsNullOrEmpty(accessToken) || !accessToken.Contains('.'))
            {
                AnsiConsole.MarkupLine("Signed in       :  Yes");
            }
            else
            {
                var user = GetUserInfo(accessToken);
                AnsiConsole.MarkupLine($"Signed in       :  {user}");
            }

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
                            AnsiConsole.WriteLine(exception.Message);
                        }
                        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                        {
                            AnsiConsole.MarkupLine($"Error contacting backend: {exception.Message}.");
                        }
                    });
        }
        else
        {
            AnsiConsole.MarkupLine("Logged in       :  No. Use login command to log in.");
        }
    }

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
