using System.Globalization;

namespace Flextime;

/// <summary>
/// Picks measurement files by name, without opening them.  Listen names
/// a file by the UTC time it starts it, and everything in a file comes
/// before the next one is started — so a file started before a given
/// time can only reach past it if it is the newest such file (a long
/// sleep, say).
/// </summary>
public static class MeasurementFiles
{
    /// <summary>
    /// The files that can hold anything at or after <paramref name="from"/>
    /// (UTC), oldest first: those started at or after it, and the last
    /// one started before it.  A file named otherwise is always
    /// included.  Null gives every file.
    /// </summary>
    public static List<string> From(string folder, DateTime? from)
    {
        List<string> files;

        try
        {
            files = Directory.EnumerateFiles(folder, "*.bin").Order(StringComparer.Ordinal).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

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
    /// The first file time worth reading for a sync window starting on
    /// <paramref name="since"/> (a local date): the day before it in
    /// UTC, which covers every zone's start of that day.  Null, every
    /// file, without a window.
    /// </summary>
    public static DateTime? WindowStart(DateOnly? since) =>
        since?.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static bool TryStarted(string file, out DateTime started) =>
        DateTime.TryParseExact(
            Path.GetFileNameWithoutExtension(file),
            "yyyy-MM-ddTHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out started);
}
