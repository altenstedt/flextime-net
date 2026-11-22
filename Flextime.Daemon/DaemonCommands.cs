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
    Export export,
    ILogger<DaemonCommands> logger,
    ILogger<UserInputMonitor> monitorLogger,
    DeviceCode deviceCode,
    Sync sync,
    Installer installer,
    LocationClient locationClient,
    ComputerClient computerClient,
    PauseCommand pauseCommand)
{
    /// <summary>Flextime -- track working hours. Without a command, show the installation.</summary>
    /// <param name="days">-d, Also list the latest days, or 0 for all.</param>
    /// <param name="noColor">No colour; NO_COLOR set and not empty does the same.</param>
    /// <param name="json">Write JSON to standard out.</param>
    [Command("")]
    public async Task<int> Root(
        [HideDefaultValue] int? days = null, bool noColor = false, bool json = false,
        CancellationToken cancellationToken = default)
    {
        if (days is < 0)
        {
            ConsoleApp.LogError("--days must be 0 or more.");

            return 1;
        }

        return await printInfo.Invoke(days, noColor, json, cancellationToken);
    }

    /// <summary>Log in to the Flextime server, so sync can upload</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    public async Task Login(CancellationToken cancellationToken)
    {
        logger.LogDebug("Login invoked.");
        await deviceCode.LogOn(cancellationToken);
    }

    /// <summary>Remove the sign-in from this computer; measurements are kept</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    public int Logout()
    {
        logger.LogDebug("Logout invoked.");

        Console.WriteLine(TokenStorage.Delete()
            ? "Logged out. Sync uploads nothing until you log in again."
            : "Not logged in.");

        return 0;
    }

    /// <summary>Upload measurements and places, --once or --every</summary>
    /// <param name="once">Sync once.</param>
    /// <param name="every">Sync every interval: 20m, 1h30m, "2 hours" or 00:20:00.</param>
    public async Task Sync(
        bool once = false,
        [HideDefaultValue] string? every = null,
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
                        logger.LogWarning("Sync failed: {Message}", TerminalText.Clean(exception.Message));
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
            AnsiConsole.WriteLine(TerminalText.Clean(exception.Message));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            AnsiConsole.WriteLine($"Network error: {TerminalText.Clean(exception.Message)}");
        }
    }

    /// <summary>Show your computers and the ids data takes</summary>
    /// <param name="json">Write JSON to standard out.</param>
    public Task<int> Computers(bool json = false)
    {
        logger.LogDebug("Computers invoked.");

        return computerClient.List(json);
    }

    /// <summary>Print the agent skill that reads your Flextime data</summary>
    /// <param name="install">Write it to ~/.claude/skills/flextime.</param>
    /// <param name="output">-o, Write it to this folder instead.</param>
    [ConsoleAppFilter<SkillHelpFilter>]
    public int Skill(bool install = false, [HideDefaultValue] string? output = null)
    {
        logger.LogDebug("Skill invoked.");

        return Flextime.Daemon.Skill.Invoke(install, output);
    }

    /// <summary>Show this computer's name, hostname and former names</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    [Command("computer")]
    public Task<int> Computer()
    {
        logger.LogDebug("Computer invoked.");

        return computerClient.Show();
    }

    /// <summary>Give a computer a name of your own, or "" for none</summary>
    /// <param name="name">The name, or "" to go by the hostname again.</param>
    /// <param name="id">Another computer's id, from the computers command.</param>
    [Command("computer rename")]
    public Task<int> ComputerRename([Argument] string name, [HideDefaultValue] string? id = null)
    {
        logger.LogDebug("Computer rename invoked.");

        return computerClient.Rename(name, id);
    }

    /// <summary>Show the network as seen without permissions; sends nothing</summary>
    /// <param name="json">Write JSON to standard out.</param>
    public Task<int> Network(bool json = false)
    {
        logger.LogDebug("Network invoked.");

        return PrintNetwork.Invoke(json);
    }

    /// <summary>Show whether the place is recorded, and the place now</summary>
    /// <param name="name">Look up the name given in the web app.</param>
    [ConsoleAppFilter<LocationHelpFilter>]
    [Command("location")]
    public Task<int> Location(bool name = false, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Location invoked.");

        return locationClient.Status(name, cancellationToken);
    }

    /// <summary>Record the place, a hash of the router. Requires login</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    [Command("location on")]
    public Task<int> LocationOn()
    {
        logger.LogDebug("Location on invoked.");

        return locationClient.On();
    }

    /// <summary>Stop recording the place; the history is kept</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    [Command("location off")]
    public int LocationOff()
    {
        logger.LogDebug("Location off invoked.");

        return locationClient.Off();
    }

    /// <summary>Remove this computer's place history, here and on the server</summary>
    /// <param name="allComputers">Clear every computer on the account, on the server too.</param>
    [Command("location clear")]
    public Task<int> LocationClear(bool allComputers = false, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Location clear invoked.");

        return locationClient.Clear(allComputers, cancellationToken);
    }

    /// <summary>Show your hours as the web app counts them</summary>
    /// <param name="since">-s, How far back: 30d (default), 1w or P7D.</param>
    /// <param name="week">-w, ISO weeks: this, last, 34, 32-34 or 2026-W34.</param>
    /// <param name="month">Months: this, last, aug, jun-aug, 8 or 2026-08.</param>
    /// <param name="computer">-c, Computer ids, comma separated. Default: here.</param>
    /// <param name="allComputers">Count all your computers.</param>
    /// <param name="newestFirst">Newest day first, as the web app lists them.</param>
    /// <param name="hideNotWork">Leave out days marked as not work.</param>
    /// <param name="json">Write JSON to standard out.</param>
    [ConsoleAppFilter<DataHelpFilter>]
    public async Task<int> Data(
        [HideDefaultValue] string since = "30d",
        [HideDefaultValue] string? week = null,
        [HideDefaultValue] string? month = null,
        [HideDefaultValue] string[]? computer = null,
        bool allComputers = false,
        bool newestFirst = false,
        bool hideNotWork = false,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Data invoked.");

        if (computer is { Length: > 0 } && allComputers)
        {
            Console.Error.WriteLine("Give either --computer or --all-computers, not both.");

            return 1;
        }

        if (!RangeOptions.TryRead(since, week, month, DateTimeOffset.Now, out var range))
        {
            return 1;
        }

        var (from, to) = range!.Value;

        return await printData.Invoke(from, to, computer ?? [], allComputers, newestFirst, hideNotWork, json, cancellationToken);
    }

    /// <summary>Save your data on the server to one JSON file</summary>
    /// <param name="output">-o, File to write, or - for standard out.</param>
    /// <param name="since">-s, How far back: 30d, 1w or P7D. Default: all.</param>
    /// <param name="week">-w, ISO weeks: this, last, 34, 32-34 or 2026-W34.</param>
    /// <param name="month">Months: this, last, aug, jun-aug, 8 or 2026-08.</param>
    [ConsoleAppFilter<ExportHelpFilter>]
    public async Task<int> Export(
        [HideDefaultValue] string? output = null,
        [HideDefaultValue] string? since = null,
        [HideDefaultValue] string? week = null,
        [HideDefaultValue] string? month = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Export invoked.");

        if (!RangeOptions.TryRead(since, week, month, DateTimeOffset.Now, out var range))
        {
            return 1;
        }

        return await export.Invoke(output, range, cancellationToken);
    }

    /// <summary>Record use once a minute, and the place when location is on</summary>
    /// <param name="timeZone">-t, Time zone used.</param>
    /// <param name="ignoreSessionLocked">Keep measuring while locked.</param>
    /// <param name="logSummaryInterval">Log a summary this often (default: 1h).</param>
    public async Task Listen(
        [HideDefaultValue] string? timeZone = null,
        bool ignoreSessionLocked = false,
        [HideDefaultValue] string? logSummaryInterval = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Listen invoked.");

        if (!TryReadInterval(logSummaryInterval, out var logSummaryIntervalValue))
        {
            return;
        }

        // Sync, info and location look at the lock by taking it for a
        // moment, so a lock that is taken now may be free again shortly.
        using var singleInstance = await SingleInstance.TryAcquire(SingleInstance.ListenLockPath, TimeSpan.FromSeconds(5), cancellationToken);

        // A clean exit, so that the service manager leaves this one down
        // and does not restart it every ten seconds against the instance
        // that holds the lock.  Each manager runs only one of its own, so
        // the holder is a listen started some other way.
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

        // Always there, but silent while location is off: it then only
        // reads the setting every tick, so turning location on needs no
        // restart.
        var placeRecorder = new PlaceRecorder(new PlaceSource(logger), monitorLogger);

        var pauseReader = new PauseReader(new PauseFileSource(StateFiles.PausePath),
            message => logger.LogWarning("{Message}", message));

        var monitor = new UserInputMonitor(monitorLogger, new UserInputMonitorOptions
        {
            IgnoreSessionLocked = ignoreSessionLocked,
            TimeZone = timeZone,
            LogSummaryInterval = interval,
            // Seen from use: in Hamburg, listen had moved to Berlin but
            // the info command still said Stockholm, from the start.
            ZoneChanged = zone => StateFiles.Write(StateFiles.ListenPath, zone),
            // Read every tick.  Anything wrong with the file reads as no
            // pause, said once in the log, so listen goes on measuring.
            PausedUntil = now => pauseReader.Check(now) is { State: PauseState.Paused, End: { } end } ? end : null,
        }, placeRecorder);

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
        await monitor.Begin();

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
        await monitor.End();
    }

    /// <summary>Install listen and sync as services that start at logon</summary>
    /// <param name="timeZone">-t, Time zone used. Required on Windows.</param>
    /// <param name="every">Sync interval, for example 20m (default: 20m).</param>
    public async Task<int> Install([HideDefaultValue] string? timeZone = null, [HideDefaultValue] string? every = null)
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

    /// <summary>Uninstall the services; measurements and tokens are kept</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    public async Task<int> Uninstall()
    {
        logger.LogDebug("Uninstall invoked.");

        return await installer.Uninstall();
    }

    /// <summary>Stop listen, across logons, until start; sync keeps going</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    public async Task<int> Stop()
    {
        logger.LogDebug("Stop invoked.");

        return await installer.Stop();
    }

    /// <summary>Stop measuring until midnight, or --for or --until</summary>
    /// <remarks>
    /// Marks the active stretch just before the pause as not work, when
    /// it is no longer than 30 minutes, or from --since.  The pause ends
    /// by itself; resume ends it sooner.  A pause lasts until the end of
    /// tomorrow at the most.  Listen's stop and start are measurements like
    /// any other, so a pause shorter than the idle limit is bridged and
    /// still counts as work.
    /// </remarks>
    /// <param name="for">Pause this long instead: 2h, 90m or "1 hour".</param>
    /// <param name="until">Pause until this time of day: 07:00.</param>
    /// <param name="since">Mark as not work from 19:42, or 10m ago.</param>
    /// <param name="noBackdate">Mark nothing before the pause.</param>
    [ConsoleAppFilter<PauseHelpFilter>]
    public async Task<int> Pause(
        [HideDefaultValue] string? @for = null,
        [HideDefaultValue] string? until = null,
        [HideDefaultValue] string? since = null,
        bool noBackdate = false,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Pause invoked.");

        return await pauseCommand.Pause(@for, until, since, noBackdate, cancellationToken);
    }

    /// <summary>Measure again after a pause; marks are kept</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    public int Resume()
    {
        logger.LogDebug("Resume invoked.");

        return pauseCommand.Resume();
    }

    /// <summary>Start listen after a stop</summary>
    [ConsoleAppFilter<NoArgumentsFilter>]
    public async Task<int> Start()
    {
        logger.LogDebug("Start invoked.");

        return await installer.Start();
    }
}
