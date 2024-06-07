using System.Runtime.InteropServices;
using Flextime;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProtoBuf;
using Tmds.DBus;

namespace Inhill.Flextime.Monitor;

public class UserInputMonitor(ILogger<UserInputMonitor> logger, UserInputMonitorOptions options)
{
    private readonly TimeSpan interval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan fileTimeLimit = TimeSpan.FromHours(1);
    private readonly IList<Measurement> measurements = [];
    
    private DateTimeOffset? lastFlush;
    private string? lastPath;
    private DateTimeOffset lastLogSummary = DateTimeOffset.UtcNow;
    private int lastLogSummaryCount = 0;
    
    // https://github.com/tmds/Tmds.DBus
    private IIdleMonitor? idleMonitor;

    private bool sessionLocked = false;

    public void Initialize()
    {
        idleMonitor = Connection.Session.CreateProxy<IIdleMonitor>(
            "org.gnome.Mutter.IdleMonitor",
            "/org/gnome/Mutter/IdleMonitor/Core");

        if (!options.IgnoreSessionLocked) {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SystemEvents.SessionSwitch += async (object sender, SessionSwitchEventArgs e) =>
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (e.Reason == SessionSwitchReason.SessionLock) {

                            // A measurement here seems natural, or it will be dropped if 
                            // the session is locked for longer than idle.
                            await Mark(MeasurementKind.Measurement);

                            await Mark(MeasurementKind.SessionLock);

                            sessionLocked = true;
                        }
                        else if (e.Reason == SessionSwitchReason.SessionUnlock)
                        {
                            sessionLocked = false;

                            // Since we marked a measurement at lock, we just mark unlock now
                            await Mark(MeasurementKind.SessionUnlock);
                        }

                        logger.LogTrace("Session switch: {Reason}", e.Reason);
                    }
                };
            }
        }
    }
    
    public async Task Run(CancellationToken token)
    {
        do
        {
            // We want to create measurements as close to the interval as possible.
            // The reason is that when the user runs a client to inspect working
            // hours, we want the end time to match the clock.  Since we typically
            // resolve down to a minute, we want each measurement /and/ flush to disk
            // to happen /just/ as a new minute on the clock starts.
            var now = DateTime.Now;
            var next = new DateTime(
                now.Add(interval).Ticks / interval.Ticks // Round to nearest interval 
                * interval.Ticks // Convert back to ticks
                + TimeSpan.FromMilliseconds(1).Ticks); // Add a small interval to ensure we're in the next minute

            await Task.Delay(next.Subtract(now), token);

            await Mark(MeasurementKind.Measurement);

        } while (!token.IsCancellationRequested);
    }

    public Task MarkStart()
    {
        return Mark(MeasurementKind.Start);
    }
    
    public Task MarkStop()
    {
        return Mark(MeasurementKind.Stop);
    }

    private async Task Mark(MeasurementKind kind)
    {
        if (idleMonitor == null)
        {
            throw new InvalidOperationException("Initialize must be called first.");
        }

        TimeSpan idle;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            idle = TimeSpan.FromSeconds(
                Monitor.CGEventSource.SecondsSinceLastEventType(
                    Monitor.CGEventSource.CGEventSourceStateID.HidSystemState,
                    Monitor.CGEventSource.CGEventType.MouseAndKeyboard));
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            // Note that this does not take screen lock into account.  If the user
            // interacts with the computer while the screen is locked, we will
            // measure that time as well.  This is simply the way the IdleMonitor
            // API works.  It is unfortunate, but I cannot find a way to get around
            // it.
            idle = TimeSpan.FromMilliseconds(await idleMonitor.GetIdletimeAsync());
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            idle = TimeSpan.FromMilliseconds(LastInputInfo.GetIdleTimeSinceLastInputInMilliSeconds());
        } else {
            throw new InvalidOperationException($"OS {RuntimeInformation.OSDescription} is not supported");
        }

        if (kind == MeasurementKind.Start) {
            logger.LogDebug("Start measurement at {Now:o}", DateTimeOffset.Now);
        }

        if (kind == MeasurementKind.Stop) {
            logger.LogDebug("Stop measurement at {Now:o}", DateTimeOffset.Now);
        }

        if (kind == MeasurementKind.Measurement && sessionLocked) {
            logger.LogDebug("Session is locked, measurement is not added.");

            return;
        }

        logger.LogTrace("Mark {Kind} with {Seconds}s idle.", kind, idle);

        // The time between measurements is not perfect, so be on the safe side,
        // we compare to a somewhat larger value.
        var active = idle < interval * 1.2;
            
        if (active) {
            var measurement = new Measurement
            {
                Idle = (uint)idle.TotalSeconds,
                Kind = kind,
                Timestamp = (uint)DateTimeOffset.Now.ToUnixTimeSeconds()
            };

            measurements.Add(measurement);

            FlushMeasurements();

            lastLogSummaryCount++;
        }

        if (DateTimeOffset.UtcNow - lastLogSummary >= options.LogSummaryInterval) {
            switch (lastLogSummaryCount)
            {
                case 0:
                    logger.LogInformation("No measurements since {LastLogSummary:HH:mm:ss (K)}", lastLogSummary.ToLocalTime());
                    break;

                case 1:
                    logger.LogInformation("1 measurement since {LastLogSummary:HH:mm:ss (K)}", lastLogSummary.ToLocalTime());
                    break;

                default:
                    logger.LogInformation("{LastLogSummaryCount} measurements since {LastLogSummary:HH:mm:ss (K)}", lastLogSummaryCount, lastLogSummary.ToLocalTime());
                    break;
            }

            lastLogSummary = DateTimeOffset.UtcNow;
            lastLogSummaryCount = 0;
        }
    }

    public static string GetTimeZoneInfo() {
        var zone = TimeZoneInfo.Local.Id;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana))
            {
                zone = iana;
            }
        }

        return zone;
    }

    private void FlushMeasurements() 
    {
        if (measurements.Count == 0)
        {
            return;
        }

        var zone = options.TimeZone ?? GetTimeZoneInfo();

        logger.LogTrace("Use time zone {Zone}.", zone);

        var measurement = new Measurements
        {
            Interval = (uint)interval.TotalSeconds,
            Zone = zone,
            Items = measurements
        };

        if ((lastFlush == null) || (lastPath == null)) {
            // Write to a new file
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);

                logger.LogDebug("Created directories for {Path}.", path);
            }

            using var stream = File.OpenWrite(path);
            Serializer.Serialize(stream, measurement);
        
            logger.LogTrace("Flushed {Path}.", path);

            lastPath = path;
            lastFlush = DateTimeOffset.Now;
        } else {
            // Write to an existing file
            using var stream = File.OpenWrite(lastPath);
            Serializer.Serialize(stream, measurement);
    
            logger.LogTrace("Flushed {Path}.", lastPath);

            if (lastFlush < DateTimeOffset.Now.Subtract(fileTimeLimit))
            {
                logger.LogDebug("File time limit {Limit} reached for file {File}.", fileTimeLimit, Path.GetFileName(lastPath));

                // Create a new file for subsequent writes so that we do not create big files
                lastFlush = null;
                lastPath = null;

                measurements.Clear();
            }
        }
    }

    private static string GetPath()
    {
        var fileName = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHHmmss}.bin";

        return Path.Combine(Constants.MeasurementsFolder, fileName);
    }

    public enum Kind
    {
        None = 0,
        Measurement = 1,
        Start = 2,
        Stop = 3
    }
}