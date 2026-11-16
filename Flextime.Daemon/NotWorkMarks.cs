using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flextime.Daemon;

/// <summary>A range of one day, in seconds of the day in the day's zone, as the marks document stores it.</summary>
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
    /// The end of a marked range for a pause at <paramref name="now"/>:
    /// the next whole minute, where listen writes the stop that begins
    /// the pause.
    /// </summary>
    public static DateTimeOffset RangeEnd(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeMilliseconds();
        var minute = 60_000L;

        return DateTimeOffset.FromUnixTimeMilliseconds((seconds + minute - 1) / minute * minute);
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

/// <summary>
/// Adds ranges to the marks document the web client keeps, the way the
/// web does: merged where they touch or overlap, a whole marked day left
/// alone, and everything this build does not know kept as it was.
/// </summary>
public static class MarksDocument
{
    /// <summary>
    /// Merges <paramref name="ranges"/> into <paramref name="document"/>.
    /// Returns why it cannot, or null.  Nothing is changed when it
    /// cannot: a document the web could no longer read would cost every
    /// mark in it.
    /// </summary>
    public static string? Merge(JsonObject document, IEnumerable<DayRange> ranges, out bool changed)
    {
        changed = false;

        var marks = document["marks"];

        if (marks != null && marks is not JsonObject)
        {
            return "the stored marks are not a shape this version knows";
        }

        var merged = new Dictionary<string, List<(double From, double To)>>();
        var byDate = (JsonObject?)marks;

        foreach (var range in ranges)
        {
            var key = range.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (!merged.TryGetValue(key, out var list))
            {
                var current = byDate?[key];

                if (current is JsonValue value && value.TryGetValue<string>(out var text) && text == "day")
                {
                    // Already not work, as a whole day.
                    continue;
                }

                if (current != null && !TryReadRanges(current, out list))
                {
                    return $"the mark on {key} is not a shape this version knows";
                }

                list ??= [];
                merged[key] = list;
            }

            list.Add((range.From, range.To));
        }

        if (merged.Count == 0)
        {
            return null;
        }

        var target = byDate ?? new JsonObject();

        foreach (var (key, list) in merged)
        {
            var result = Combine(list);
            var before = target[key] is { } existing && TryReadRanges(existing, out var old) ? old : null;

            if (before != null && before.SequenceEqual(result))
            {
                continue;
            }

            target[key] = new JsonArray(result
                .Select(item => (JsonNode)new JsonObject { ["from"] = item.From, ["to"] = item.To })
                .ToArray());
            changed = true;
        }

        if (changed && byDate == null)
        {
            document["marks"] = target;
        }

        return null;
    }

    // Exactly the ranges the web's decoder accepts.
    private static bool TryReadRanges(JsonNode node, out List<(double From, double To)>? ranges)
    {
        ranges = null;

        if (node is not JsonArray array)
        {
            return false;
        }

        var list = new List<(double, double)>();

        foreach (var item in array)
        {
            if (item is not JsonObject range
                || !TryReadSecond(range["from"], out var from)
                || !TryReadSecond(range["to"], out var to)
                || from >= to)
            {
                return false;
            }

            list.Add((from, to));
        }

        ranges = list;

        return true;
    }

    private static bool TryReadSecond(JsonNode? node, out double value)
    {
        value = 0;

        return node is JsonValue json
               && json.GetValueKind() == JsonValueKind.Number
               && json.TryGetValue(out value)
               && double.IsFinite(value)
               && value is >= 0 and <= 86_400;
    }

    private static List<(double From, double To)> Combine(List<(double From, double To)> ranges)
    {
        var merged = new List<(double From, double To)>();

        foreach (var next in ranges.OrderBy(item => item.From))
        {
            if (merged.Count > 0 && next.From <= merged[^1].To)
            {
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, next.To));
            }
            else
            {
                merged.Add(next);
            }
        }

        return merged;
    }
}

public enum MarkResult
{
    /// <summary>The server holds the ranges now.</summary>
    Written,

    /// <summary>The server already held them.</summary>
    AlreadyMarked,

    /// <summary>They can never be written; see the reason.</summary>
    Refused,
}

/// <summary>
/// Writes ranges to the server's marks document: read, merge, write
/// back conditionally, and again from the top when someone else wrote
/// in between.  Throws when the server cannot be reached, so the caller
/// keeps the ranges for later.
/// </summary>
public sealed class MarksWriter(HttpClient httpClient)
{
    private const int Attempts = 4;

    public async Task<(MarkResult Result, string? Reason)> Apply(IReadOnlyList<DayRange> ranges, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var get = await httpClient.GetAsync("/store/marks?api-version=1.0", cancellationToken);

            get.EnsureSuccessStatusCode();

            var body = await get.Content.ReadAsStringAsync(cancellationToken);
            var etag = get.Headers.ETag?.ToString();

            JsonNode? parsed;

            try
            {
                parsed = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return (MarkResult.Refused, "the stored marks are not JSON");
            }

            // Nothing stored yet reads as the literal null, with no
            // version: then only creating the document is allowed.
            JsonObject document;

            if (parsed == null)
            {
                document = new JsonObject();
                etag = null;
            }
            else if (parsed is JsonObject value)
            {
                document = value;

                if (etag == null)
                {
                    // Without a version the write would be create-only,
                    // and fail; better to say why.
                    return (MarkResult.Refused, "the server gave no version of the stored marks");
                }
            }
            else
            {
                return (MarkResult.Refused, "the stored marks are not a shape this version knows");
            }

            if (MarksDocument.Merge(document, ranges, out var changed) is { } problem)
            {
                return (MarkResult.Refused, problem);
            }

            if (!changed)
            {
                return (MarkResult.AlreadyMarked, null);
            }

            var json = document.ToJsonString();

            using var request = new HttpRequestMessage(HttpMethod.Put, "/store/marks?api-version=1.0");

            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            if (etag != null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            }

            using var put = await httpClient.SendAsync(request, cancellationToken);

            switch (put.StatusCode)
            {
                case HttpStatusCode.NoContent or HttpStatusCode.OK:
                    return (MarkResult.Written, null);

                case HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict:
                    // Someone else wrote in between: read it again.
                    continue;

                case HttpStatusCode.BadRequest:
                    // The size limit is the server's alone, so a later
                    // raise needs no new client; a full document lands
                    // here.
                    return (MarkResult.Refused, "the server refused the marks, perhaps because they are full");

                default:
                    put.EnsureSuccessStatusCode();

                    return (MarkResult.Written, null);
            }
        }

        throw new HttpRequestException("The stored marks kept changing while they were written.");
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
