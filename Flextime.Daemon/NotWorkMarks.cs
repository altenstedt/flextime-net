using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Flextime.Daemon;

/// <summary>A range of one day, in seconds of the day in the day's zone, as the server stores a mark.</summary>
public readonly record struct DayRange(DateOnly Date, double From, double To);

/// <summary>
/// The stretch of activity just before a pause, which the pause command
/// marks as not work.
/// </summary>
public static class Backdate
{
    /// <summary>A gap this long ends a stretch.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(15);

    /// <summary>A longer stretch is likely work, so it is not marked unasked.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    /// <summary>How far back --since may reach, so a slip cannot mark days of work.</summary>
    public static readonly TimeSpan MaxSince = TimeSpan.FromHours(24);

    /// <summary>
    /// When the active stretch that is going on at <paramref name="now"/>
    /// began, and the zone it was measured in: the first measurement
    /// after the last gap of at least <see cref="Gap"/>, or after listen
    /// last started.  Null when nothing has been measured for a gap's
    /// length, so there is no stretch going on.
    /// </summary>
    /// <remarks>
    /// A wake is not written to the measurements, so a sleep shorter
    /// than the gap does not end a stretch.  The limit covers that.
    /// </remarks>
    public static (DateTimeOffset From, string Zone)? FindStretch(
        IReadOnlyList<MeasurementWithZone> measurements, DateTimeOffset now)
    {
        // A measurement from ahead of now came from a clock that was
        // wrong; it cannot belong to the stretch going on now.
        var list = measurements
            .Where(item => item.Timestamp <= now + TimeSpan.FromMinutes(1))
            .OrderBy(item => item.Timestamp)
            .ToList();

        if (list.Count == 0 || now - list[^1].Timestamp >= Gap)
        {
            return null;
        }

        var index = list.Count - 1;

        while (index > 0
               && list[index].Measurement.Kind != Measurement.Types.Kind.Start
               && list[index].Timestamp - list[index - 1].Timestamp < Gap)
        {
            index--;
        }

        return (list[index].Timestamp, list[index].Zone);
    }

    /// <summary>
    /// The ranges of the days from <paramref name="from"/> to
    /// <paramref name="to"/>, split at midnight in <paramref name="zone"/>.
    /// A range is its start's time of day plus its length, as the web
    /// reads a block, so a night the clock changes does not run a range
    /// backwards.
    /// </summary>
    public static List<DayRange> Split(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var ranges = new List<DayRange>();
        var start = from;

        // A day at a time; a cap keeps a bad zone from looping.
        for (var day = 0; day < 4 && start < to; day++)
        {
            var local = TimeZoneInfo.ConvertTime(start, zone);
            var date = DateOnly.FromDateTime(local.DateTime);
            var midnight = PauseFile.StartOfDay(start, 1, zone);
            var end = to < midnight ? to : midnight;
            var ofDay = Math.Floor(local.TimeOfDay.TotalSeconds);
            var length = Math.Ceiling((end - start).TotalSeconds);
            var toOfDay = Math.Min(86_400, ofDay + length);

            if (toOfDay > ofDay)
            {
                ranges.Add(new DayRange(date, ofDay, toOfDay));
            }

            start = end;
        }

        return ranges;
    }

    /// <summary>"19:42–19:45", with dates when the range is not today's.</summary>
    public static string Describe(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, DateTimeOffset now)
    {
        var localFrom = TimeZoneInfo.ConvertTime(from, zone);
        var localTo = TimeZoneInfo.ConvertTime(to, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        var sameDay = DateOnly.FromDateTime(localFrom.DateTime) == today
                      && (DateOnly.FromDateTime(localTo.DateTime) == today
                          || (DateOnly.FromDateTime(localTo.DateTime) == today.AddDays(1) && localTo.TimeOfDay == TimeSpan.Zero));

        var format = sameDay ? "HH:mm" : "yyyy-MM-dd HH:mm";

        return $"{localFrom.ToString(format, CultureInfo.InvariantCulture)}–{localTo.ToString(format, CultureInfo.InvariantCulture)}";
    }
}

public enum MarkResult
{
    /// <summary>The server holds the ranges now.</summary>
    Written,

    /// <summary>They can never be written; see the reason.</summary>
    Refused,
}

/// <summary>
/// Adds ranges to the server's marks in one request.  The server merges
/// them into each date's marks where they touch or overlap and leaves a
/// whole marked day as it is, so a range already marked changes nothing
/// and writing one twice is safe.  Throws when the server cannot be
/// reached or is busy, so the caller keeps the ranges for later.
/// </summary>
public sealed class MarksWriter(HttpClient httpClient)
{
    public async Task<(MarkResult Result, string? Reason)> Apply(IReadOnlyList<DayRange> ranges, CancellationToken cancellationToken)
    {
        if (ranges.Count == 0)
        {
            return (MarkResult.Written, null);
        }

        // As JsonNodes, which are safe to trim, like the rest of the
        // daemon's hand-built bodies.
        var items = new JsonArray();

        foreach (var range in ranges)
        {
            items.Add((JsonNode)new JsonObject
            {
                ["date"] = range.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["from"] = range.From,
                ["to"] = range.To,
            });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/days/marks?api-version=1.10");

        request.Content = new StringContent(new JsonObject { ["ranges"] = items }.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent or HttpStatusCode.OK:
                return (MarkResult.Written, null);

            case HttpStatusCode.BadRequest:
                // The server validates every mark, and trying again
                // sends the same ranges, so they are dropped rather than
                // kept for a week.
                return (MarkResult.Refused, "the server refused the range as not a mark it can store");

            default:
                // 503, when the server's merge kept meeting other writes,
                // lands here with everything else, as does a server older
                // than 1.10: kept, and tried again at the next sync.
                response.EnsureSuccessStatusCode();

                return (MarkResult.Written, null);
        }
    }
}

/// <summary>A range the pause command has not written to the server yet.</summary>
public sealed record PendingMark(DateTimeOffset From, DateTimeOffset To, string Zone, DateTimeOffset Created)
{
    public string ToLine() => string.Join(' ',
        From.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        To.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        Zone,
        Created.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>Null, with the reason, when the line is not a pending mark this version would write.</summary>
    public static PendingMark? Parse(string line, DateTimeOffset now, out string? problem)
    {
        problem = null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 4
            || !TryReadInstant(parts[0], out var from)
            || !TryReadInstant(parts[1], out var to)
            || !TryReadInstant(parts[3], out var created))
        {
            problem = "it cannot be read";

            return null;
        }

        if (to <= from || to - from > Backdate.MaxSince + TimeSpan.FromMinutes(2))
        {
            problem = "its range cannot be right";

            return null;
        }

        if (!TimeZones.TryGet(parts[2], out _))
        {
            problem = $"its time zone {TerminalText.Clean(parts[2])} is unknown";

            return null;
        }

        if (created > now + PauseFile.ClockSkew)
        {
            problem = "it was saved in the future";

            return null;
        }

        if (now - created > PendingMarks.MaxAge)
        {
            problem = "it is older than a week";

            return null;
        }

        return new PendingMark(from, to, parts[2], created);
    }

    private static bool TryReadInstant(string text, out DateTimeOffset value)
    {
        value = default;

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 946_684_800 or > 253_402_300_799)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return true;
    }
}

/// <summary>
/// The ranges the pause command saved for sync to write.  Saved before
/// the pause command tries the server, so an interrupted or failed
/// write is not lost; writing a range twice changes nothing.  Every
/// change goes through a short lock and a whole-file replace.
/// </summary>
public sealed class PendingMarks(string path, string lockPath)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const int MaxBytes = 64 * 1024;
    private const int MaxEntries = 100;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    public static PendingMarks Default { get; } = new(StateFiles.PauseMarkPath, StateFiles.PauseMarkLockPath);

    /// <summary>False when the mark could not be saved.</summary>
    public async Task<bool> Add(PendingMark mark, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            using var held = await SingleInstance.TryAcquire(lockPath, LockWait, cancellationToken);

            if (held == null)
            {
                return false;
            }

            var entries = ReadLocked(now, null);

            if (!entries.Contains(mark))
            {
                entries.Add(mark);
            }

            Write(entries.TakeLast(MaxEntries).ToList());

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Removes one mark, best effort.</summary>
    public async Task Remove(PendingMark mark, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            using var held = await SingleInstance.TryAcquire(lockPath, LockWait, cancellationToken);

            if (held == null)
            {
                return;
            }

            var entries = ReadLocked(now, null);

            if (entries.Remove(mark))
            {
                Write(entries);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Left for the next sync, which finds it already written.
        }
    }

    /// <summary>
    /// The marks waiting.  Lines that are corrupt or too old are dropped
    /// here, once, and said so through <paramref name="dropped"/>.
    /// </summary>
    public async Task<List<PendingMark>> Read(DateTimeOffset now, Action<string>? dropped, CancellationToken cancellationToken = default)
    {
        // The common case, every sync: nothing waits, and no lock file
        // needs to exist for that.
        if (!Path.Exists(path) && new FileInfo(path).LinkTarget == null)
        {
            return [];
        }

        try
        {
            using var held = await SingleInstance.TryAcquire(lockPath, LockWait, cancellationToken);

            if (held == null)
            {
                return [];
            }

            return ReadLocked(now, dropped);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            dropped?.Invoke($"Cannot read the pending not-work marks: {exception.Message}");

            return [];
        }
    }

    private List<PendingMark> ReadLocked(DateTimeOffset now, Action<string>? dropped)
    {
        var info = new FileInfo(path);

        if (info.LinkTarget == null && !info.Exists && !Directory.Exists(path))
        {
            return [];
        }

        // As the pause file: a link, a directory, an empty "file" (which
        // may be a pipe, and would block the open) and a large one are
        // never opened.
        if (info.LinkTarget != null || Directory.Exists(path) || info.Length == 0 || info.Length > MaxBytes)
        {
            dropped?.Invoke("Dropped the pending not-work marks: the file is not one this version writes.");
            Discard();

            return [];
        }

        string text;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, false)))
        {
            var buffer = new char[MaxBytes + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);

            text = new string(buffer, 0, Math.Min(length, MaxBytes));
        }

        var entries = new List<PendingMark>();
        var changed = false;

        foreach (var line in text.Split('\n').Select(item => item.Trim()).Where(item => item.Length > 0))
        {
            if (PendingMark.Parse(line, now, out var problem) is { } mark)
            {
                if (!entries.Contains(mark))
                {
                    entries.Add(mark);
                }
            }
            else
            {
                dropped?.Invoke($"Dropped a pending not-work mark: {problem}.");
                changed = true;
            }
        }

        if (changed)
        {
            Write(entries);
        }

        return entries;
    }

    private void Write(List<PendingMark> entries)
    {
        if (entries.Count == 0)
        {
            Discard();

            return;
        }

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            PrivateFolder.Ensure(directory);
        }

        var bytes = Encoding.UTF8.GetBytes(string.Concat(entries.Select(item => item.ToLine() + "\n")));

        PrivateFile.Replace(path, stream => stream.Write(bytes));
    }

    private void Discard()
    {
        try
        {
            if (Directory.Exists(path) && new FileInfo(path).LinkTarget == null)
            {
                // Not ours to remove recursively; listen and sync ignore it.
                return;
            }

            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Read and dropped again next time.
        }
    }

    /// <summary>
    /// Writes what is waiting, oldest first, until the server cannot be
    /// reached.  Never throws: a failure is said through
    /// <paramref name="note"/> and the rest waits for the next sync.
    /// </summary>
    public async Task Retry(MarksWriter writer, DateTimeOffset now, Action<string, bool> note, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await Read(now, line => note(line, true), cancellationToken);

            foreach (var mark in entries)
            {
                var zone = TimeZones.Get(mark.Zone);
                var span = Backdate.Describe(mark.From, mark.To, zone, now);
                var (result, reason) = await writer.Apply(Backdate.Split(mark.From, mark.To, zone), cancellationToken);

                await Remove(mark, now, cancellationToken);

                if (result == MarkResult.Refused)
                {
                    note($"Dropped the not-work mark {span}: {reason}.", true);
                }
                else
                {
                    note($"Marked {span} as not work.", false);
                }
            }
        }
        catch (Exception exception)
        {
            note($"Not-work marks wait for the next sync: {TerminalText.Clean(exception.Message)}", true);
        }
    }
}
