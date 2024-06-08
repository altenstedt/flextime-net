using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Inhill.Flextime.Monitor;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Flextime.Daemon;

public class Worker(
    ILogger<Worker> logger,
    ILogger<UserInputMonitor> monitorLogger,
    IHostApplicationLifetime hostApplicationLifetime,
    IOptions<TimeZoneOptions> timeZoneOptions,
    IOptions<EveryOptions> everyOptions,
    IOptions<OnceOptions> onceOptions,
    IOptions<IgnoreSessionLockedOptions> ignoreSessionLockedOptions,
    IOptions<LogSummaryIntervalOptions> logSummaryIntervalOptions,
    IOptions<CommandOptions> command) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Time zone {TimeZone}.", timeZoneOptions.Value.TimeZone);
        logger.LogDebug("Sync command {Command}.", command.Value.SyncInvoked);
        logger.LogDebug("Listen command {Command}.", command.Value.ListenInvoked);
        logger.LogDebug("Login command {Command}.", command.Value.LogInInvoked);

        if (command.Value.LogInInvoked)
        {
            logger.LogDebug("Login invoked.");
            
            var deviceCode = new DeviceCode();
            await deviceCode.Initialize();
            await deviceCode.LogOn(stoppingToken);

            // I want to terminate the host when the worker has completed.
            hostApplicationLifetime.StopApplication();
            return;
        }
        
        if (command.Value.SyncInvoked)
        {
            logger.LogDebug("Sync invoked.");

            var deviceCode = new DeviceCode();
            await deviceCode.Initialize();

            var computer = new Computer();
            await computer.Initialize();

            if (!deviceCode.IsAuthenticated)
            {
                AnsiConsole.MarkupLine("You need to log on first.");
                return;
            }

            var uri = new Uri("https://api.mangoground-e628dd34.swedencentral.azurecontainerapps.io/", UriKind.Absolute);
            var httpClient = new HttpClient { BaseAddress = uri };
            var accessToken = await deviceCode.GetAccessToken();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            if (onceOptions.Value.Once)
            {
                await Sync.Invoke(httpClient, deviceCode, computer, TimeSpan.Zero, 0, true,
                    (text, _) =>
                    {
                        AnsiConsole.WriteLine(text);
                    },
                    AnsiConsole.WriteLine);
            } else if (everyOptions.Value.Every.HasValue)
            {
                var version = VersionHelper.GetVersion();

                logger.LogInformation("Flextime sync {Version} started.", version);
                logger.LogInformation("Data is synced every {Every}.", everyOptions.Value.Every.Value);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Sync.Invoke(httpClient, deviceCode, computer, TimeSpan.Zero, 0, true,
                        (text, kind) =>
                        {
                            if (kind == Sync.PrintDayKind.Synced)
                            {
                                logger.LogInformation(text);
                            }
                        },
                        text => logger.LogInformation(text));
                    
                    await Task.Delay(everyOptions.Value.Every.Value, stoppingToken);
                }
            }
            else
            {
                AnsiConsole.MarkupLine("--once or --every must be provided.");
            }

            // I want to terminate the host when the worker has completed.
            hostApplicationLifetime.StopApplication();
            return;
        }

        if (command.Value.ListenInvoked)
        {
            logger.LogDebug("Listen invoked.");
            
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

            if (string.IsNullOrEmpty(timeZoneOptions.Value.TimeZone))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    logger.LogError("The --time-zone option is required on Windows. (System reports {Id}.)", UserInputMonitor.GetTimeZoneInfo());
                    hostApplicationLifetime.StopApplication();
                    return;
                }

                logger.LogInformation("Time zone is {Id}.", UserInputMonitor.GetTimeZoneInfo());
            } else {
                if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneOptions.Value.TimeZone, out var byOption)) {
                    var optionOffset = byOption.GetUtcOffset(DateTime.Now);
                    var localOffset = DateTimeOffset.Now.Offset;
                    if (optionOffset != localOffset) {
                        logger.LogWarning("Time zone {Id} with offset {OptionOffset} does not match local {LocalOffset}.", timeZoneOptions.Value.TimeZone, optionOffset, localOffset);
                    } else {
                        logger.LogInformation("Time zone set to {Id}.", timeZoneOptions.Value.TimeZone);
                    }
                } else {
                    logger.LogCritical("Time zone {Id} not found on this system.", timeZoneOptions.Value.TimeZone);
                    
                    hostApplicationLifetime.StopApplication();
                    return;
                }
            }
            
            var monitor = new UserInputMonitor(monitorLogger, new UserInputMonitorOptions
            {
                IgnoreSessionLocked = ignoreSessionLockedOptions.Value.IgnoreSessionLocked,
                TimeZone = timeZoneOptions.Value.TimeZone,
                LogSummaryInterval = logSummaryIntervalOptions.Value.Interval
            });
            
            await monitor.Initialize();

            var version = VersionHelper.GetVersion();

            logger.LogInformation("Flextime listener {Version} started.", version);
            logger.LogInformation("Summary is logged every {Interval}.", logSummaryIntervalOptions.Value.Interval);

            logger.LogDebug("Start.");
            await monitor.MarkStart();

            // We could also ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
            // since .NET 8, but I want to limit the exceptions we catch to just
            // the cancellation token.
            // https://blog.stephencleary.com/2023/11/configureawait-in-net-8.html
            try
            {
                await monitor.Run(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Ignore.
                logger.LogDebug("Task cancelled.");
            }

            logger.LogDebug("Stop.");
            await monitor.MarkStop();
            
            // I want to terminate the host when the worker has completed.
            hostApplicationLifetime.StopApplication();
        }
    }
}
