using Google.Protobuf;

namespace Flextime;

/// <summary>The bits of <see cref="Place.Flags"/>.</summary>
public static class PlaceFlags
{
    /// <summary>Internet traffic went through a tunnel, so the router says little about where the computer is.</summary>
    public const uint NotDependable = 1;

    /// <summary>A VPN client was connected.</summary>
    public const uint Vpn = 2;

    /// <summary>Location was turned off at this point; the time after it has no place.</summary>
    public const uint LocationOff = 4;
}

/// <summary>
/// The hint kept with a router: the DNS domain of its network, like
/// <c>omegapoint.se</c>.  The API takes the same rules and refuses
/// anything else, so an invalid hint is dropped, never sent.
/// </summary>
public static class PlaceHints
{
    public const int MaxLength = 253;

    /// <summary>
    /// A domain as the system reports it, made a hint: a trailing dot
    /// (the root) trimmed, lowercase.  Null when empty or invalid.
    /// </summary>
    public static string? Normalize(string? domain)
    {
        if (domain == null)
        {
            return null;
        }

        var hint = domain.Trim();

        if (hint.EndsWith('.'))
        {
            hint = hint[..^1];
        }

        hint = hint.ToLowerInvariant();

        return IsValid(hint) ? hint : null;
    }

    /// <summary>1–253 characters of a–z, 0–9, dot and hyphen, not starting or ending with a dot.</summary>
    public static bool IsValid(string? hint) =>
        hint is { Length: > 0 and <= MaxLength }
        && hint[0] != '.'
        && hint[^1] != '.'
        && hint.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}

public record PlaceWithZone(Place Place, string Zone)
{
    public DateTimeOffset Timestamp { get; } =
        TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(Place.Timestamp),
            TimeZones.Get(Zone));
}

/// <summary>
/// The place change points kept in the measurement files (field 4).
/// A file holds the place in effect when it was started and every
/// change after that; a measurement belongs to the latest place at or
/// before it.
/// </summary>
public static class PlaceFiles
{
    /// <summary>
    /// Every change point on disk with the zone of the file it is in,
    /// sorted by time.  A file starts with the place in effect, so the
    /// same point can be in two files; it is listed once, with a hint if
    /// either copy has one.
    /// </summary>
    /// <param name="from">
    /// Read only the files <see cref="Files"/> gives for this time; null
    /// reads every file.  Points before it may still be listed.
    /// </param>
    public static List<PlaceWithZone> Read(string folder, DateTime? from = null)
    {
        var list = new List<PlaceWithZone>();
        var seen = new Dictionary<(uint, ulong, uint), int>();

        foreach (var file in Files(folder, from))
        {
            var measurements = TryParse(file);

            if (measurements == null)
            {
                continue;
            }

            foreach (var place in measurements.Places)
            {
                var key = (place.Timestamp, place.Router, place.Flags);

                if (!seen.TryGetValue(key, out var index))
                {
                    seen[key] = list.Count;
                    list.Add(new PlaceWithZone(place, measurements.Zone));
                }
                else if (list[index].Place.Hint.Length == 0 && place.Hint.Length > 0)
                {
                    list[index] = new PlaceWithZone(place, list[index].Zone);
                }
            }
        }

        list.Sort((left, right) => left.Place.Timestamp.CompareTo(right.Place.Timestamp));

        return list;
    }

    /// <summary>
    /// The measurement files that can hold change points at or after
    /// <paramref name="from"/> (UTC), oldest first, found from their
    /// names alone: listen names a file by the UTC time it starts it,
    /// and everything in it comes before the next file is started.  So
    /// the files started at or after <paramref name="from"/> and the
    /// last one started before it — which holds the place in effect, and
    /// after a long sleep may run past it.  A file named otherwise is
    /// always included.  Null gives every file.
    /// </summary>
    public static List<string> Files(string folder, DateTime? from)
    {
        var files = EnumerateFiles(folder).Order(StringComparer.Ordinal).ToList();

        if (from == null)
        {
            return files;
        }

        var result = new List<string>();
        string? before = null;

        foreach (var file in files)
        {
            if (!TryStarted(file, out var started) || started >= from.Value)
            {
                result.Add(file);
            }
            else
            {
                // Ordered by name, so the last one seen is the newest.
                before = file;
            }
        }

        if (before != null)
        {
            result.Insert(0, before);
        }

        return result;
    }

    /// <summary>
    /// The latest change point at or after <paramref name="notBefore"/>,
    /// looking at the newest files first.  Null when there is none.
    /// </summary>
    public static Place? Last(string folder, uint notBefore)
    {
        foreach (var file in EnumerateFiles(folder).OrderDescending(StringComparer.Ordinal))
        {
            var places = TryParse(file)?.Places;

            if (places is { Count: > 0 })
            {
                var last = places.MaxBy(place => place.Timestamp)!;

                return last.Timestamp >= notBefore ? last : null;
            }
        }

        return null;
    }

    /// <summary>The newest measurement file, the one listen may still be writing.</summary>
    public static string? Newest(string folder) =>
        EnumerateFiles(folder).OrderDescending(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Rewrites every file holding change points before
    /// <paramref name="before"/> without them; measurements are kept
    /// exactly.  <paramref name="skip"/> is left alone: listen writes
    /// that file from memory and drops the points itself.  Each file is
    /// replaced in one rename, so a reader never sees half of one.
    /// Returns how many files were rewritten.
    /// </summary>
    public static int ClearBefore(string folder, uint before, string? skip)
    {
        var rewritten = 0;

        foreach (var file in EnumerateFiles(folder))
        {
            if (skip != null && Path.GetFullPath(file) == Path.GetFullPath(skip))
            {
                continue;
            }

            var measurements = TryParse(file);

            if (measurements == null || measurements.Places.All(place => place.Timestamp >= before))
            {
                continue;
            }

            var kept = measurements.Places.Where(place => place.Timestamp >= before).ToArray();

            measurements.Places.Clear();
            measurements.Places.AddRange(kept);

            var temporary = $"{file}.{Guid.NewGuid():N}.tmp";

            try
            {
                using (var stream = File.Create(temporary))
                {
                    measurements.WriteTo(stream);
                }

                File.Move(temporary, file, overwrite: true);

                rewritten++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The marker still holds: nothing before it is uploaded,
                // and the next clear tries the file again.
                TryDelete(temporary);
            }
        }

        return rewritten;
    }

    private static IEnumerable<string> EnumerateFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.bin").ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool TryStarted(string file, out DateTime started) =>
        DateTime.TryParseExact(
            Path.GetFileNameWithoutExtension(file),
            "yyyy-MM-ddTHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out started);

    private static Measurements? TryParse(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);

            return Measurements.Parser.ParseFrom(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidProtocolBufferException)
        {
            // Being written by listen this very moment, or removed.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file is not a .bin file; nothing reads it.
        }
    }
}
