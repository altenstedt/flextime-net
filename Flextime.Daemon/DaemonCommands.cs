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
    Report report,
    ILogger<DaemonCommands> logger,
    ILogger<UserInputMonitor> monitorLogger,
    DeviceCode deviceCode,
    Sync sync,
    Installer installer)
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
    /// <param name="every">Sync data recurring with remote, for example 20m, 1h30m, "2 hours" or 00:20:00.</param>
    public async Task Sync(
        bool once = false,
        string? every = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Sync invoked.");

        if (!TryReadInterval(every, out var everyValue))
        {
            return;
        }

        if (!deviceCode.IsAuthenticated)
        {
            AnsiConsole.MarkupLine("You need to log on first.");
            return;
        }

        try
        {
            // The name is no longer written here: the summary carries
            // what the server holds, so sync writes it only when it
            // differs.
            if (once)
            {
                await sync.SyncAndPrint();
            }
            else if (everyValue.HasValue)
            {
                using var singleInstance = SingleInstance.TryAcquire(SingleInstance.SyncLockPath);

                if (singleInstance == null)
                {
                    logger.LogCritical("Another recurring sync instance is already running.");
                    return;
                }

                // Publish the interval for the info command.  Trusted only
                // while this process holds the sync lock.
                StateFiles.Write(StateFiles.SyncPath, everyValue.Value.ToString());

                var version = VersionHelper.GetVersion();

                logger.LogInformation("Flextime sync {Version} started.", version);
                logger.LogInformation("Data is synced every {Every}.", everyValue.Value);

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await sync.SyncAndLog(logger);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                    {
                        // Network might be down — or the API too old for
                        // this client; the message says which.  Try
                        // again next interval either way.
                        logger.LogWarning("Sync failed: {Message}", exception.Message);
                    }

                    await Task.Delay(everyValue.Value, cancellationToken);
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

    /// <summary>Show the computers on this account, with the ids the report command takes</summary>
    /// <param name="json">Write JSON to standard out.</param>
    public Task<int> Computers(bool json = false)
    {
        logger.LogDebug("Computers invoked.");

        return printData.InvokeComputers(json);
    }

    /// <summary>
    /// Show activity data stored on the server.
    ///
    /// Note that these numbers are not the web client's, even with idle
    /// profiles applied: days are grouped here rather than on the
    /// server, so a day can begin and end differently near midnight.
    /// The report command is the one that reproduces what the web
    /// client shows.
    /// </summary>
    /// <param name="days">-d, Number of days to show.</param>
    /// <param name="computer">-c, Computer id to show, comma separated for multiple. Defaults to this computer.</param>
    /// <param name="allComputers">Show all computers.</param>
    /// <param name="idle">-i, Idle limit in minutes. Overrides any stored idle profiles.</param>
    /// <param name="noProfile">Ignore stored idle profiles and use the flat idle limit.</param>
    /// <param name="timestamps">Include raw timestamps (Unix seconds) in JSON output.</param>
    /// <param name="json">Write JSON to standard out.</param>
    public Task<int> Data(
        int days = 30,
        string[]? computer = null,
        bool allComputers = false,
        int? idle = null,
        bool noProfile = false,
        bool timestamps = false,
        bool json = false)
    {
        logger.LogDebug("Data invoked.");

        return printData.Invoke(days, computer ?? [], allComputers, idle, noProfile, timestamps, json);
    }

    /// <summary>Compare the hours you reported against the activity measured, and show where they disagree</summary>
    /// <param name="timesheet">-t, Path to a JSON file holding the hours you reported — the side this checks the measurements against. The file holds a list of {"date": "2026-06-26", "tag": "ClientA", "minutes": 540}, tag optional, written by whatever you report your time from. Read from a pipe when omitted, or with -.</param>
    /// <param name="since">-s, How far back to reconcile, for example 1w, 3d, "2 weeks ago" or P7D. Ignored when --week is given.</param>
    /// <param name="week">-w, Reconcile whole ISO weeks instead: this, last, 34, 32-34, 2026-W34 or 2026-W32-34.</param>
    /// <param name="month">Reconcile whole calendar months instead, which is what invoicing follows: this, last, aug, jun-aug, 8, 6-8, 2026-08 or 2026-jun-aug.</param>
    /// <param name="machines">-m, Computer id(s) whose activity counts, comma separated. Defaults to every computer you have, which is rarely what you want if some of them are personal.</param>
    /// <param name="idle">-i, Flat idle limit in minutes, for a user with no stored idle profiles.</param>
    /// <param name="noProfile">Ignore stored idle profiles and marks, and use the flat idle limit.</param>
    /// <param name="threshold">How many minutes a day may differ by before it is worth mentioning.</param>
    /// <param name="verbose">-v, Also list the machines counted, the sections that found nothing, and the activity not counted as findings.</param>
    /// <param name="json">Write JSON to standard out.</param>
    public async Task<int> Report(
        string? timesheet = null,
        string since = "1w",
        string? week = null,
        string? month = null,
        string[]? machines = null,
        int? idle = null,
        bool noProfile = false,
        int threshold = Daemon.Report.DefaultThreshold,
        bool verbose = false,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Report invoked.");

        DateOnly from;
        DateOnly to;

        if (week != null && month != null)
        {
            Console.Error.WriteLine("Give either --week or --month, not both.");

            return 1;
        }

        if (month != null)
        {
            if (!Months.TryParse(month, DateTimeOffset.Now, out from, out to))
            {
                Console.Error.WriteLine($"Cannot read \"{month}\" as a month.");
                Console.Error.WriteLine("Try this, last, aug, jun-aug, 8, 6-8, 2026-08 or 2026-jun-aug.");

                return 1;
            }
        }
        else if (week != null)
        {
            if (!IsoWeeks.TryParse(week, DateTimeOffset.Now, out from, out to))
            {
                Console.Error.WriteLine($"Cannot read \"{week}\" as an ISO week.");
                Console.Error.WriteLine("Try this, last, 34, 32-34, 2026-W34 or 2026-W32-34.");

                return 1;
            }
        }
        else
        {
            if (!DurationParser.TryParse(since, DateTimeOffset.Now, out var sinceValue))
            {
                Console.Error.WriteLine($"Cannot read \"{since}\" as a length of time.");
                Console.Error.WriteLine("Try 3d, 1w, \"2 weeks ago\", yesterday, \"last week\", P7D or 7.00:00:00.");

                return 1;
            }

            from = DateOnly.FromDateTime((DateTimeOffset.Now - sinceValue).Date);
            to = DateOnly.FromDateTime(DateTimeOffset.Now.Date);
        }

        return await report.Invoke(timesheet, from, to, machines ?? [], idle, noProfile, threshold, verbose, json, cancellationToken);
    }

    /// <summary>Listen to events on device</summary>
    /// <param name="timeZone">-t, Time zone used.</param>
    /// <param name="ignoreSessionLocked">Keep tracking measurements when the computer is locked</param>
    /// <param name="logSummaryInterval">Log summary interval, for example 30m or 01:00:00 (default: 1 hour)</param>
    public async Task Listen(
        string? timeZone = null,
        bool ignoreSessionLocked = false,
        string? logSummaryInterval = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Listen invoked.");

        if (!TryReadInterval(logSummaryInterval, out var logSummaryIntervalValue))
        {
            return;
        }

        using var singleInstance = SingleInstance.TryAcquire(SingleInstance.ListenLockPath);

        if (singleInstance == null)
        {
            logger.LogCritical("Another listen instance is already running.");
            return;
        }

        var interval = logSummaryIntervalValue ?? TimeSpan.FromHours(1);

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

        // Publish the effective zone for the info command.  Trusted only
        // while this process holds the listen lock.
        StateFiles.Write(StateFiles.ListenPath, timeZone ?? UserInputMonitor.GetTimeZoneInfo());

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

    /// <summary>Install listen and sync as user services that start at logon</summary>
    /// <param name="timeZone">-t, Time zone used. Required on Windows.</param>
    /// <param name="every">Sync interval, for example 20m or 00:20:00 (default: 20 minutes).</param>
    public async Task<int> Install(string? timeZone = null, string? every = null)
    {
        logger.LogDebug("Install invoked.");

        if (!TryReadInterval(every, out var everyValue))
        {
            return 1;
        }

        return await installer.Install(timeZone, everyValue ?? TimeSpan.FromMinutes(20));
    }

    // Left null when nothing was written, so each caller keeps its own
    // default.  Reports the failure itself, the way the other bad-input
    // paths in this class do.
    private bool TryReadInterval(string? text, out TimeSpan? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DurationParser.TryParseInterval(text, out var parsed))
        {
            logger.LogCritical("Cannot read {Text} as an interval. Try 20m, 1h30m, \"2 hours\" or 00:20:00.", text);

            return false;
        }

        value = parsed;

        return true;
    }

    /// <summary>Uninstall the user services. Measurements and tokens are kept.</summary>
    public async Task<int> Uninstall()
    {
        logger.LogDebug("Uninstall invoked.");

        return await installer.Uninstall();
    }

    /// <summary>Stop the listen service without uninstalling. It stays stopped across logons until started again. Sync keeps running.</summary>
    public async Task<int> Stop()
    {
        logger.LogDebug("Stop invoked.");

        return await installer.Stop();
    }

    /// <summary>Start the listen service after a stop</summary>
    public async Task<int> Start()
    {
        logger.LogDebug("Start invoked.");

        return await installer.Start();
    }
}
