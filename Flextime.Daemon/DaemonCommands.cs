using System.Net.Http.Json;
using System.Runtime.InteropServices;
using ConsoleAppFramework;
using Flextime.Monitor;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using Spectre.Console;

namespace Flextime.Daemon;

public class DaemonCommands(
    PrintInfo printInfo,
    PrintData printData,
    ILogger<DaemonCommands> logger,
    ILogger<UserInputMonitor> monitorLogger,
    IHttpClientFactory httpClientFactory,
    DeviceCode deviceCode,
    Computer computer,
    Sync sync)
{
    /// <summary>Flextime -- tracking working hours. With no command, displays information about the installation and exits.</summary>
    [Command("")]
    public async Task Root()
    {
        await printInfo.Invoke();
    }

    /// <summary>Log in to remote</summary>
    public async Task Login(CancellationToken cancellationToken)
    {
        logger.LogDebug("Login invoked.");
        await deviceCode.LogOn(cancellationToken);
    }

    /// <summary>Synchronize data with remote</summary>
    /// <param name="once">Sync data once with remote.</param>
    /// <param name="every">Sync data recurring with remote.</param>
    public async Task Sync(
        bool once = false,
        TimeSpan? every = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Sync invoked.");

        if (!deviceCode.IsAuthenticated)
        {
            AnsiConsole.MarkupLine("You need to log on first.");
            return;
        }

        var httpClient = httpClientFactory.CreateClient("ApiHttpClient");

        try
        {
            if (!string.IsNullOrEmpty(computer.Name))
            {
                var response = await httpClient.PatchAsJsonAsync($"/{computer.Id}/name", computer.Name, StringSourceGenerationContext.Default.String, cancellationToken: cancellationToken);

                response.EnsureSuccessStatusCode();
            }

            if (once)
            {
                await sync.SyncAndPrint();
            }
            else if (every.HasValue)
            {
                var version = VersionHelper.GetVersion();

                logger.LogInformation("Flextime sync {Version} started.", version);
                logger.LogInformation("Data is synced every {Every}.", every.Value);

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await sync.SyncAndLog(logger);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                    {
                        // Network might be down — try again next interval.
                        logger.LogWarning("Network error.");
                    }

                    await Task.Delay(every.Value, cancellationToken);
                }
            }
            else
            {
                AnsiConsole.MarkupLine("--once or --every must be provided.");
            }
        }
        catch (TokenRefreshException exception)
        {
            // Retrying will not help until the user logs in again.
            AnsiConsole.WriteLine(exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            AnsiConsole.WriteLine($"Network error: {exception.Message}");
        }
    }

    /// <summary>Show activity data stored on the server</summary>
    /// <param name="days">-d, Number of days to show.</param>
    /// <param name="computer">-c, Computer id to show, comma separated for multiple. Defaults to this computer.</param>
    /// <param name="allComputers">Show all computers.</param>
    /// <param name="idle">-i, Idle limit in minutes. Gaps no longer than this count as active time.</param>
    /// <param name="timestamps">Include raw timestamps (Unix seconds) in JSON output.</param>
    /// <param name="json">Write JSON to standard out.</param>
    public Task<int> Data(
        int days = 30,
        string[]? computer = null,
        bool allComputers = false,
        int idle = 10,
        bool timestamps = false,
        bool json = false)
    {
        logger.LogDebug("Data invoked.");

        return printData.Invoke(days, computer ?? [], allComputers, idle, timestamps, json);
    }

    /// <summary>Listen to events on device</summary>
    /// <param name="timeZone">-t, Time zone used.</param>
    /// <param name="ignoreSessionLocked">Keep tracking measurements when the computer is locked</param>
    /// <param name="logSummaryInterval">Log summary interval (default: 1 hour)</param>
    public async Task Listen(
        string? timeZone = null,
        bool ignoreSessionLocked = false,
        TimeSpan? logSummaryInterval = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Listen invoked.");

        var interval = logSummaryInterval ?? TimeSpan.FromHours(1);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (Icu.IsInUse())
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaId))
                {
                    logger.LogDebug("Local time zone is {ICU}, converted from {Id} on {Runtime}", ianaId,
                        TimeZoneInfo.Local.Id, RuntimeInformation.RuntimeIdentifier);
                }
                else
                {
                    logger.LogWarning("Windows platform is not able to convert {Id} to ICU time zone",
                        TimeZoneInfo.Local.Id);
                }
            }
            else
            {
                logger.LogWarning("Windows platform is not using ICU which is needed for cross-platform functionality");
            }
        }
        else
        {
            logger.LogDebug("Local time zone is {Id}", TimeZoneInfo.Local.Id);
        }

        if (string.IsNullOrEmpty(timeZone))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                logger.LogError("The --time-zone option is required on Windows. (System reports {Id}.)", UserInputMonitor.GetTimeZoneInfo());
                return;
            }

            logger.LogInformation("Time zone is {Id}.", UserInputMonitor.GetTimeZoneInfo());
        }
        else
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var byOption))
            {
                var optionOffset = byOption.GetUtcOffset(DateTime.Now);
                var localOffset = DateTimeOffset.Now.Offset;
                if (optionOffset != localOffset)
                {
                    logger.LogWarning("Time zone {Id} with offset {OptionOffset} does not match local {LocalOffset}.", timeZone, optionOffset, localOffset);
                }
                else
                {
                    logger.LogInformation("Time zone set to {Id}.", timeZone);
                }
            }
            else
            {
                logger.LogCritical("Time zone {Id} not found on this system.", timeZone);
                return;
            }
        }

        var monitor = new UserInputMonitor(monitorLogger, new UserInputMonitorOptions
        {
            IgnoreSessionLocked = ignoreSessionLocked,
            TimeZone = timeZone,
            LogSummaryInterval = interval
        });

        try
        {
            await monitor.Initialize();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogCritical("{Message}", exception.Message);
            return;
        }

        var version = VersionHelper.GetVersion();

        logger.LogInformation("Flextime listener {Version} started.", version);
        logger.LogInformation("Summary is logged every {Interval}.", interval);

        logger.LogDebug("Start.");
        await monitor.MarkStart();

        // Limit the catch to cancellation, not all exceptions.
        // https://blog.stephencleary.com/2023/11/configureawait-in-net-8.html
        try
        {
            await monitor.Run(cancellationToken);
        }
        catch (TaskCanceledException)
        {
            logger.LogDebug("Task cancelled.");
        }

        logger.LogDebug("Stop.");
        await monitor.MarkStop();
    }
}
