using Microsoft.Extensions.Logging;

namespace Flextime.Monitor;

/// <summary>What the network says about where the computer is.</summary>
/// <param name="Router">Client hash of the router, 48 bits; 0 when none was found.</param>
/// <param name="Flags">The <see cref="PlaceFlags"/> bits, never <see cref="PlaceFlags.LocationOff"/>.</param>
/// <param name="Online">Whether there is a route to the internet at all.</param>
/// <param name="Pending">A router was found, but not its hardware address yet: worth asking again shortly.</param>
/// <param name="Hint">The DNS domain of the router's network, valid as a hint; null when there is none.</param>
public readonly record struct PlaceReading(ulong Router, uint Flags, bool Online = true, bool Pending = false, string? Hint = null);

/// <summary>The user's location settings, read every tick.</summary>
/// <param name="On">Whether location is recorded.</param>
/// <param name="ClearedAt">Nothing before this time (epoch seconds) exists any more; 0 when never cleared.</param>
public readonly record struct PlaceSettings(bool On, uint ClearedAt);

public interface IPlaceSource
{
    PlaceSettings ReadSettings();

    Task<PlaceReading> Read();
}

/// <summary>The outcome of a tick or a sample.</summary>
/// <param name="Changed">The change points for the current file changed; it needs writing.</param>
/// <param name="ClearedAt">A clear newer than the last one was noticed; other files need clearing to this time.</param>
/// <param name="Pending">The router's hardware address was not known yet; sample again shortly.</param>
public readonly record struct PlaceUpdate(bool Changed, uint? ClearedAt = null, bool Pending = false);

/// <summary>A reading taken by <see cref="PlaceRecorder.ReadNetwork"/>, numbered in the order the reads started.</summary>
public readonly record struct PlaceSample(PlaceReading Reading, long Number);

/// <summary>
/// Keeps the place change points for the measurement file listen is
/// writing.  A change point is recorded when (router, flags) changes,
/// when location is turned off, and as the first entry of each file.
/// The first change point for a router in a file carries its hint; a
/// hint alone changing is not a change of place.
/// The network is sampled on the network-change event, on wake, and
/// every <see cref="FallbackInterval"/> in case an event never comes.
/// </summary>
public sealed class PlaceRecorder(IPlaceSource source, ILogger logger)
{
    /// <summary>How often the network is sampled without an event.</summary>
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);

    // After a wake the network can take a few seconds to come back.
    // Sampling at once would start the first minute as unknown; waiting
    // briefly gives it the place the computer woke up at.
    private static readonly TimeSpan WakeRetryDelay = TimeSpan.FromSeconds(3);
    private const int WakeRetries = 5;

    private readonly List<Place> places = [];

    // The last change point recorded, the off marker included.
    private Place? current;

    // The hint for the router in current, which may have come after the
    // change point did; the next file starts with it.
    private string hint = string.Empty;

    private DateTimeOffset? lastSample;
    private uint clearedAt;

    // Reads are numbered as they start, and the network may be read
    // outside the caller's lock: a slow read that is recorded after a
    // later one would otherwise put back the place that was left.
    private long readsStarted;
    private long recorded;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public Func<TimeSpan, Task> Delay { get; init; } = delay => Task.Delay(delay);

    /// <summary>The change points to write into the current file.</summary>
    public IReadOnlyList<Place> Places => places;

    /// <summary>
    /// Starts from what is on disk: the latest change point listen wrote
    /// before it stopped, so a restart neither repeats an unchanged
    /// place nor forgets to mark location turned off in the meantime.
    /// </summary>
    /// <param name="lastOnDisk">The latest change point on disk at or after the given clear time.</param>
    public void Initialize(Func<uint, Place?> lastOnDisk)
    {
        clearedAt = source.ReadSettings().ClearedAt;

        var last = lastOnDisk(clearedAt);

        current = last != null && last.Timestamp >= clearedAt ? last : null;
        hint = current?.Hint ?? string.Empty;
        lastSample = null;

        StartFile();
    }

    /// <summary>A new file starts with the place in effect, unless location is off.</summary>
    public void StartFile()
    {
        places.Clear();

        if (current != null && !IsOff(current))
        {
            var first = current.Clone();

            first.Hint = first.Router == 0 ? string.Empty : hint;

            places.Add(first);
            current = first;
        }
    }

    /// <summary>
    /// Called every minute, before the measurement is stamped, so that a
    /// measurement after a wake or a change already has its place.
    /// </summary>
    public async Task<PlaceUpdate> Tick(bool wake)
    {
        var settings = source.ReadSettings();

        uint? cleared = null;

        if (settings.ClearedAt > clearedAt)
        {
            clearedAt = settings.ClearedAt;
            cleared = clearedAt;

            var dropped = places.RemoveAll(place => place.Timestamp < clearedAt);

            if (current != null && current.Timestamp < clearedAt)
            {
                current = null;
            }

            logger.LogInformation("Place history cleared before {ClearedAt:o}; {Count} change points dropped from the current file.",
                DateTimeOffset.FromUnixTimeSeconds(clearedAt), dropped);
        }

        var changed = cleared.HasValue;

        if (!settings.On)
        {
            lastSample = null;

            if (current != null && !IsOff(current))
            {
                Add(new Place { Timestamp = Timestamp(), Flags = PlaceFlags.LocationOff });

                logger.LogInformation("Location turned off.");

                changed = true;
            }

            return new PlaceUpdate(changed, cleared);
        }

        var due = wake || current == null || IsOff(current) || lastSample == null
                  || Now() - lastSample.Value >= FallbackInterval;

        if (!due)
        {
            return new PlaceUpdate(changed, cleared);
        }

        var sample = await Sample(wake);

        return new PlaceUpdate(changed || sample.Changed, cleared, sample.Pending);
    }

    /// <summary>The network changed: sample now, whatever the timer says.</summary>
    public async Task<PlaceUpdate> NetworkChanged() =>
        await ReadNetwork() is { } sample ? Apply(sample) : new PlaceUpdate(false);

    /// <summary>
    /// The first half of <see cref="NetworkChanged"/>: reads the network
    /// and changes nothing here, so the caller need not hold the lock
    /// the measurements share while the network is probed.  Null when
    /// location is off.
    /// </summary>
    public async Task<PlaceSample?> ReadNetwork()
    {
        if (!source.ReadSettings().On)
        {
            return null;
        }

        var number = Interlocked.Increment(ref readsStarted);

        return new PlaceSample(await source.Read(), number);
    }

    /// <summary>
    /// The second half: records the reading, unless location was turned
    /// off meanwhile or a read that started later was recorded first.
    /// </summary>
    public PlaceUpdate Apply(PlaceSample sample)
    {
        if (!source.ReadSettings().On || sample.Number <= recorded)
        {
            return new PlaceUpdate(false);
        }

        return Record(sample.Reading, sample.Number);
    }

    private async Task<PlaceUpdate> Sample(bool wake)
    {
        var number = Interlocked.Increment(ref readsStarted);
        var reading = await source.Read();

        for (var retry = 0; wake && retry < WakeRetries && (!reading.Online || reading.Pending); retry++)
        {
            await Delay(WakeRetryDelay);

            number = Interlocked.Increment(ref readsStarted);
            reading = await source.Read();
        }

        return Record(reading, number);
    }

    private PlaceUpdate Record(PlaceReading reading, long number)
    {
        recorded = number;
        lastSample = Now();

        var readingHint = reading.Router == 0 ? string.Empty : reading.Hint ?? string.Empty;

        if (current != null && !IsOff(current) && current.Router == reading.Router && current.Flags == reading.Flags)
        {
            // Kept for the next file; this one's change point stays as
            // it was written.
            if (hint.Length == 0)
            {
                hint = readingHint;
            }

            return new PlaceUpdate(false, Pending: reading.Pending);
        }

        if (current == null || current.Router != reading.Router || hint.Length == 0)
        {
            hint = readingHint;
        }

        Add(new Place { Timestamp = Timestamp(), Router = reading.Router, Flags = reading.Flags });

        logger.LogInformation("Place is now {Router}{Flags}.",
            reading.Router == 0 ? "unknown" : reading.Router.ToString("x12"),
            Describe(reading.Flags));

        return new PlaceUpdate(true, Pending: reading.Pending);
    }

    private void Add(Place place)
    {
        // Two changes within a second: the later one is what held.
        if (places.Count > 0 && places[^1].Timestamp == place.Timestamp)
        {
            places.RemoveAt(places.Count - 1);
        }

        // Once per router and file: the router's later change points
        // here, a VPN coming up for instance, would only repeat it.
        if (place.Router != 0 && places.All(other => other.Router != place.Router))
        {
            place.Hint = hint;
        }

        places.Add(place);
        current = place;
    }

    private uint Timestamp() => (uint)Now().ToUnixTimeSeconds();

    private static bool IsOff(Place place) => (place.Flags & PlaceFlags.LocationOff) != 0;

    private static string Describe(uint flags) =>
        ((flags & PlaceFlags.NotDependable) != 0, (flags & PlaceFlags.Vpn) != 0) switch
        {
            (true, true) => " (through a tunnel, VPN connected)",
            (true, false) => " (through a tunnel)",
            (false, true) => " (VPN connected)",
            _ => string.Empty,
        };
}
