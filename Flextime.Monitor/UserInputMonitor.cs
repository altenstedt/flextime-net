using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Tmds.DBus.Protocol;

namespace Flextime.Monitor;

public class UserInputMonitor(
    ILogger<UserInputMonitor> logger,
    UserInputMonitorOptions options,
    PlaceRecorder? placeRecorder = null)
{
    // Network changes come in bursts while an interface comes up; one
    // sample after it settles is enough.
    private static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(2);

    // configd resolves the router's hardware address shortly after the
    // address itself, so a sample that found a router without one is
    // repeated a few times.
    private static readonly TimeSpan PendingRetryDelay = TimeSpan.FromSeconds(5);
    private const int PendingRetries = 6;

    private readonly TimeSpan interval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan fileTimeLimit = TimeSpan.FromHours(1);
    private readonly IList<Measurement> measurements = [];

    // Mark is called both from the Run loop and from session switch events,
    // which arrive on other threads.
    private readonly SemaphoreSlim markSemaphore = new(1, 1);

    private DateTimeOffset? lastFlush;
    private string? lastPath;
    private DateTimeOffset lastLogSummary = DateTimeOffset.UtcNow;
    private int lastLogSummaryCount;

    // https://github.com/tmds/Tmds.DBus
    private IdleMonitor.DBus.IdleMonitor? idleMonitor;
    private IdleMonitor.DBus.ScreenSaver? screenSaverMonitor;

    private IntPtr sessionScreenIsLockedString = IntPtr.Zero;

    private bool sessionLocked;

    private int networkGeneration;

    public async Task Initialize()
    {
        if (placeRecorder != null)
        {
            placeRecorder.Initialize(clearedAt => PlaceFiles.Last(Constants.MeasurementsFolder, clearedAt));

            // The event is what makes a change take effect within
            // seconds; the recorder's own timer only covers an event
            // that never comes, after sleep for instance.
            NetworkChange.NetworkAddressChanged += (_, _) => _ = OnNetworkChanged();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => _ = OnNetworkChanged();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (string.IsNullOrEmpty(DBusAddress.Session))
            {
                throw new InvalidOperationException(
                    "No D-Bus session bus address found. The monitor needs a desktop session to measure idle time.");
            }

            var connection = new DBusConnection(DBusAddress.Session);

            await connection.ConnectAsync();

            idleMonitor = new IdleMonitor.DBus.IdleMonitor(connection, "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core");
            screenSaverMonitor = new IdleMonitor.DBus.ScreenSaver(connection, "org.gnome.Mutter.IdleMonitor", "/org/gnome/ScreenSaver");
        }

        if (!options.IgnoreSessionLocked) {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SystemEvents.SessionSwitch += async (_, e) =>
                {
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        return;
                    }

                    // The handler is async void; an unhandled exception here
                    // would crash the process.
                    try
                    {
                        if (e.Reason == SessionSwitchReason.SessionLock) {

                            // A measurement here seems natural, or it will be dropped if
                            // the session is locked for longer than idle.
                            await Mark(Measurement.Types.Kind.Measurement);

                            await Mark(Measurement.Types.Kind.SessionLock);

                            sessionLocked = true;
                        }
                        else if (e.Reason == SessionSwitchReason.SessionUnlock)
                        {
                            sessionLocked = false;

                            // Since we marked a measurement at lock, we just mark unlock now
                            await Mark(Measurement.Types.Kind.SessionUnlock);
                        }

                        logger.LogTrace("Session switch: {Reason}", e.Reason);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Session switch handling failed.");
                    }
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (screenSaverMonitor == null)
                {
                    throw new InvalidOperationException("Screen saver monitor not found.");
                }

                await screenSaverMonitor.WatchActiveChangedAsync((Notification<bool> notification) =>
                {
                    if (notification.Exception == null)
                    {
                        var active = notification.Value;

                        sessionLocked = active;
                        logger.LogTrace(active ? "ScreenSaver switched to active" : "ScreenSaver switched to inactive");
                    }
                    else
                    {
                        logger.LogError(notification.Exception, "ScreenSaver exception.");
                    }
                }, ObserverFlags.None);

            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // We could use DistributedNotificationCenter, but I do not know
                // how to P/Invoke that.  Instead, we use CGSession at the time
                // of measurement below.
                const string Key = "CGSSessionScreenIsLocked";
                var pointer = Marshal.StringToHGlobalUni(Key);
                sessionScreenIsLockedString = CGSession.CFStringCreateWithCharacters(IntPtr.Zero, pointer, Key.Length);
            }
        }
    }

    public async Task Run(CancellationToken token)
    {
        var lastTick = DateTimeOffset.UtcNow;

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

            // Whatever clock the delay runs on, the wall clock jumps
            // across a sleep: a long gap means a wake, and the network
            // is checked before the next measurement so that it does not
            // carry the place the lid was closed at.
            var tick = DateTimeOffset.UtcNow;
            var wake = tick - lastTick > interval * 3;

            lastTick = tick;

            await Mark(Measurement.Types.Kind.Measurement, wake);

        } while (!token.IsCancellationRequested);
    }

    public Task MarkStart()
    {
        // Like a wake: the place is unknown until the network is read.
        return Mark(Measurement.Types.Kind.Start, wake: true);
    }

    public Task MarkStop()
    {
        return Mark(Measurement.Types.Kind.Stop);
    }

    private async Task Mark(Measurement.Types.Kind kind, bool wake = false)
    {
        await markSemaphore.WaitAsync();

        try
        {
            await MarkCore(kind, wake);
        }
        finally
        {
            markSemaphore.Release();
        }
    }

    private async Task MarkCore(Measurement.Types.Kind kind, bool wake)
    {
        var placesChanged = kind != Measurement.Types.Kind.Stop && await TickPlaces(wake);

        TimeSpan idle;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            idle = TimeSpan.FromSeconds(
                CGEventSource.SecondsSinceLastEventType(
                    CGEventSource.CGEventSourceStateID.HidSystemState,
                    CGEventSource.CGEventType.MouseAndKeyboard));

            // Figure out if the screen is locked or not.
            var dictionary = CGSession.CGSessionCopyCurrentDictionary();

            var sessionScreenIsLocked = CGSession.CFDictionaryGetValue(dictionary, sessionScreenIsLockedString);

            sessionLocked = sessionScreenIsLocked != IntPtr.Zero;
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            if (idleMonitor == null)
            {
                throw new InvalidOperationException("Initialize must be called first.");
            }

            // Note that this does not take screen lock into account.  If the user
            // interacts with the computer while the screen is locked, we will
            // measure that time as well.  This is simply the way the IdleMonitor
            // API works.  It is unfortunate, but I cannot find a way to get around
            // it.
            idle = TimeSpan.FromMilliseconds(await idleMonitor.GetIdletimeAsync());
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            // Idle time that cannot be read counts as idle: a minute is
            // only measured when there is input to show for it.
            idle = LastInputInfo.GetIdleTimeSinceLastInputInMilliSeconds() is { } milliseconds
                ? TimeSpan.FromMilliseconds(milliseconds)
                : TimeSpan.MaxValue;
        } else {
            throw new InvalidOperationException($"OS {RuntimeInformation.OSDescription} is not supported");
        }

        if (kind == Measurement.Types.Kind.Start) {
            logger.LogDebug("Start measurement at {Now:o}", DateTimeOffset.Now);
        }

        if (kind == Measurement.Types.Kind.Stop) {
            logger.LogDebug("Stop measurement at {Now:o}", DateTimeOffset.Now);
        }

        if (kind == Measurement.Types.Kind.Measurement && sessionLocked) {
            logger.LogDebug("Session is locked, measurement is not added.");

            if (placesChanged)
            {
                FlushMeasurements();
            }

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
        else if (placesChanged)
        {
            // A change of place is written when it is noticed, measured
            // minute or not, so it survives a crash or a shutdown.
            FlushMeasurements();
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

    // Never lets a failure to read the network stop a measurement.
    private async Task<bool> TickPlaces(bool wake)
    {
        if (placeRecorder == null)
        {
            return false;
        }

        try
        {
            var update = await placeRecorder.Tick(wake);

            if (update.ClearedAt is { } clearedAt)
            {
                // The file being written is rewritten from memory below;
                // the clear command leaves it alone for that reason.
                var rewritten = PlaceFiles.ClearBefore(Constants.MeasurementsFolder, clearedAt, lastPath);

                logger.LogDebug("Cleared place history from {Count} files.", rewritten);
            }

            if (update.Pending)
            {
                _ = OnNetworkChanged();
            }

            return update.Changed;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reading the place failed.");

            return false;
        }
    }

    private async Task OnNetworkChanged()
    {
        if (placeRecorder == null)
        {
            return;
        }

        try
        {
            var generation = Interlocked.Increment(ref networkGeneration);

            await Task.Delay(NetworkSettleDelay);

            // A newer event restarts the wait; the last one samples.
            for (var attempt = 0; attempt < PendingRetries && generation == Volatile.Read(ref networkGeneration); attempt++)
            {
                PlaceUpdate update;

                await markSemaphore.WaitAsync();

                try
                {
                    update = await placeRecorder.NetworkChanged();

                    if (update.Changed)
                    {
                        FlushMeasurements();
                    }
                }
                finally
                {
                    markSemaphore.Release();
                }

                if (!update.Pending)
                {
                    return;
                }

                await Task.Delay(PendingRetryDelay);
            }
        }
        catch (Exception exception)
        {
            // Called from an event handler; an exception here would end
            // the process.
            logger.LogError(exception, "Reading the place after a network change failed.");
        }
    }

    private void FlushMeasurements()
    {
        var places = placeRecorder?.Places ?? [];

        // Nothing to write, and no file whose place points need rewriting.
        if (measurements.Count == 0 && places.Count == 0 && lastPath == null)
        {
            return;
        }

        var zone = options.TimeZone ?? GetTimeZoneInfo();

        logger.LogTrace("Use time zone {Zone}.", zone);

        var measurement = new Measurements
        {
            Interval = (uint)interval.TotalSeconds,
            Zone = zone
        };
        measurement.Measurements_.AddRange(measurements);

        // Empty unless location is or was on: the file is then exactly
        // what it was before places existed.
        measurement.Places.AddRange(places);

        if ((lastFlush == null) || (lastPath == null)) {
            // Write to a new file
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                PrivateFolder.Ensure(directory);
            }

            MeasurementFiles.Replace(path, measurement);

            logger.LogTrace("Flushed {Path}.", path);

            lastPath = path;
            lastFlush = DateTimeOffset.Now;
        } else {
            // Rewrite the existing file with all measurements collected so
            // far.  Replaced whole, so a crash or a full disk midway never
            // leaves a truncated file for sync to trip over.
            MeasurementFiles.Replace(lastPath, measurement);

            logger.LogTrace("Flushed {Path}.", lastPath);

            if (lastFlush < DateTimeOffset.Now.Subtract(fileTimeLimit))
            {
                logger.LogDebug("File time limit {Limit} reached for file {File}.", fileTimeLimit, Path.GetFileName(lastPath));

                // Create a new file for subsequent writes so that we do not create big files
                lastFlush = null;
                lastPath = null;

                measurements.Clear();

                placeRecorder?.StartFile();
            }
        }
    }

    private static string GetPath()
    {
        var fileName = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHHmmss}.bin";

        return Path.Combine(Constants.MeasurementsFolder, fileName);
    }
}
