using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Flextime;
using Inhill.Flextime.Server;
using Microsoft.Extensions.Logging;

var options = new Options();

var folderOption = new Option<DirectoryInfo>("--folder", () => new DirectoryInfo(Constants.MeasurementsFolder), "Folder to read measurements from");
folderOption.AddAlias("-f");

var logLevelOption = new Option<LogLevel>("--log-level", () => LogLevel.Information , "Set logging severity level");
logLevelOption.AddAlias("-v");

var dryRunOption = new Option<bool>("--dry-run", "Exit before starting the daemon and capturing measurements");
dryRunOption.AddAlias("--exit");

var ignoreSessionLockedOption = new Option<bool>("--ignore-session-locked", "Keep tracking measurements when the computer is locked");

var logSummaryIntervalOption = new Option<TimeSpan>("--log-summary-interval", () => TimeSpan.FromHours(1), "Log summary interval");

var timeZoneOption = new Option<string?>("--time-zone", "Set time zone");
timeZoneOption.AddAlias("-t");

var rootCommand = new RootCommand("Flextime -- tracking working hours");
rootCommand.AddOption(folderOption);
rootCommand.AddOption(logLevelOption);
rootCommand.AddOption(dryRunOption);
rootCommand.AddOption(logSummaryIntervalOption);
rootCommand.AddOption(ignoreSessionLockedOption);
rootCommand.AddOption(timeZoneOption);

rootCommand.SetHandler(async (folder, logLevel, dryRun, logSummaryInterval, ignoreSessionLocked, timeZone) =>
    {
        options.MeasurementsFolder = folder.FullName;
        options.LogLevel = logLevel;
        options.DryRun = dryRun;
        options.LogSummaryInterval = logSummaryInterval;
        options.IgnoreSessionLocked = ignoreSessionLocked;
        options.TimeZone = timeZone;

        using var loggerFactory =
            LoggerFactory.Create(builder =>
            {
                builder.AddSystemdConsole();

                builder
                    .AddSimpleConsole(formatterOptions =>
                    {
                        formatterOptions.SingleLine = true;
                        formatterOptions.TimestampFormat = "HH:mm:ss ";
                    });

                builder.SetMinimumLevel(options.LogLevel);
            });

        var logger = loggerFactory.CreateLogger<Daemon>();
        var version = FileVersionInfo
            .GetVersionInfo(Environment.GetCommandLineArgs()[0])
            .ProductVersion;

        var daemon = new Daemon(logger, options);

        var tokenSource = new CancellationTokenSource();

        Console.CancelKeyPress += (_, args) =>
        {
            tokenSource.Cancel();
            args.Cancel = true; // We want to run the rest of our code
        };


        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu
            var sortVersion = CultureInfo.InvariantCulture.CompareInfo.Version;
            var bytes = sortVersion.SortId.ToByteArray();
            var tmp = bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0];
            var isUsingIcu = tmp != 0 && tmp == sortVersion.FullVersion;

            if (isUsingIcu)
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

        if (string.IsNullOrEmpty(options.TimeZone)) {
            logger.LogInformation("Time zone is {Id}.", Daemon.GetTimeZoneInfo());
        } else {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone, out var byOption)) {
                var optionOffset = byOption.GetUtcOffset(DateTime.Now);
                var localOffset = DateTimeOffset.Now.Offset;
                if (optionOffset != localOffset) {
                    logger.LogWarning("Time zone {Id} with offset {OptionOffset} does not match local {LocalOffet}.", options.TimeZone, optionOffset, localOffset);
                } else {
                    logger.LogInformation("Time zone set to {Id}.", options.TimeZone);
                }
            } else {
                logger.LogCritical("Time zone {Id} not found on this system.", options.TimeZone);
                
                throw new InvalidOperationException($"Time zone {options.TimeZone} not found on this system.");
            }
        }

        if (!options.DryRun)
        {
            daemon.Initialize();

            logger.LogInformation("Flextime daemon {Version} started.", version);
            logger.LogInformation("Summary is logged every {Interval}.", options.LogSummaryInterval);

            await daemon.MarkStart();

            // We could also ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
            // since .NET 8, but I want to limit the exceptions we catch to just
            // the cancellation token.
            // https://blog.stephencleary.com/2023/11/configureawait-in-net-8.html
            try
            {
                await daemon.Run(tokenSource.Token);
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }

            await daemon.MarkStop();
        }

        logger.LogInformation("Flextime daemon {Version} stopped.", version);
    },
    folderOption,
    logLevelOption,
    dryRunOption,
    logSummaryIntervalOption,
    ignoreSessionLockedOption,
    timeZoneOption);

return await rootCommand.InvokeAsync(args);
