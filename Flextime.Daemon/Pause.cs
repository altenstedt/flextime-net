using System.Globalization;
using System.Text;

namespace Flextime.Daemon;

public enum PauseState
{
    /// <summary>No pause file: measure.</summary>
    None,

    /// <summary>A valid pause that has not ended yet.</summary>
    Paused,

    /// <summary>A valid pause whose end has passed: measure.</summary>
    Ended,

    /// <summary>A pause file that cannot be trusted: measure.</summary>
    Invalid,
}

/// <summary>
/// What the pause file says now.  Only <see cref="PauseState.Paused"/>
/// stops measuring; every other state, a file that could not be read
/// included, measures.
/// </summary>
public readonly record struct PauseStatus(PauseState State, DateTimeOffset? End = null, string? Problem = null)
{
    public bool IsPaused => State == PauseState.Paused;
}

/// <summary>
/// The pause file: written by the pause command, read by listen every
/// tick, deleted by resume, start, install and uninstall.
/// </summary>
/// <remarks>
/// The file is a setting, like location.txt, so it is trusted without a
/// lock, but nothing in it is trusted without a check.  Listen must keep
/// measuring whatever is at the path, so anything this class cannot read
/// as a whole, recent, short pause is no pause at all.
/// </remarks>
public static class PauseFile
{
    /// <summary>More than anything the pause command writes.</summary>
    public const int MaxBytes = 512;

    /// <summary>
    /// The longest a pause may last.  The command stops at the end of the
    /// next day, at most 48 hours away, plus a daylight saving hour; one
    /// written by anything else that lasts longer is ignored.
    /// </summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(49);

    /// <summary>
    /// How far ahead of now the time a pause was written at may lie.  A
    /// file from further ahead was written before the clock moved back.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    // 2000-01-01 and 9999-12-31: anything outside is garbage, and the
    // upper bound keeps FromUnixTimeSeconds from throwing.
    private const long MinUnix = 946_684_800;
    private const long MaxUnix = 253_402_300_799;

    /// <summary>
    /// The file's text: the end, then when it was written, both in Unix
    /// seconds, then the end for people to read.
    /// </summary>
    public static string Format(DateTimeOffset end, DateTimeOffset written) =>
        string.Join('\n',
            end.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            written.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            $"Paused until {end.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)}",
            "");

    /// <summary>
    /// Writes the file whole or not at all, so listen never reads half
    /// of it.
    /// </summary>
    public static void Write(string path, DateTimeOffset end, DateTimeOffset written)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            PrivateFolder.Ensure(directory);
        }

        var bytes = Encoding.UTF8.GetBytes(Format(end, written));

        PrivateFile.Replace(path, stream => stream.Write(bytes));
    }

    /// <summary>
    /// Reads <paramref name="text"/> against <paramref name="now"/>.
    /// Never throws.
    /// </summary>
    public static PauseStatus Parse(string text, DateTimeOffset now)
    {
        var lines = text.Split('\n');

        if (lines.Length < 2)
        {
            return new PauseStatus(PauseState.Invalid, Problem: "is incomplete");
        }

        if (!TryReadInstant(lines[0], out var end) || !TryReadInstant(lines[1], out var written))
        {
            return new PauseStatus(PauseState.Invalid, Problem: "does not hold a pause");
        }

        if (end <= written || end - written > MaxLength)
        {
            return new PauseStatus(PauseState.Invalid, Problem: "holds a pause longer than a pause can be");
        }

        if (written > now + ClockSkew)
        {
            return new PauseStatus(PauseState.Invalid, Problem: "was written in the future; the clock may have moved back");
        }

        if (end <= now)
        {
            return new PauseStatus(PauseState.Ended, end);
        }

        // Covered by the two checks above, unless they change.
        if (end - now > MaxLength)
        {
            return new PauseStatus(PauseState.Invalid, Problem: "holds a pause longer than a pause can be");
        }

        return new PauseStatus(PauseState.Paused, end);
    }

    private static bool TryReadInstant(string line, out DateTimeOffset instant)
    {
        instant = default;

        if (!long.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < MinUnix or > MaxUnix)
        {
            return false;
        }

        instant = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return true;
    }

    /// <summary>
    /// Removes the pause, whatever is at the path.  Returns null when
    /// nothing is left that listen would take as a pause, otherwise why
    /// it could not be removed.
    /// </summary>
    public static string? Clear(string path)
    {
        string? failure = null;

        try
        {
            // Removes a symbolic link itself, not what it points at, and
            // does nothing when there is no file.
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception.Message;

            // A file that cannot be removed may still be emptied, and an
            // empty file is no pause.  Never through a link, which would
            // empty whatever it points at.
            try
            {
                if (new FileInfo(path).LinkTarget == null && !Directory.Exists(path))
                {
                    File.WriteAllBytes(path, []);
                }
            }
            catch (Exception second) when (second is IOException or UnauthorizedAccessException)
            {
                // What listen makes of it decides below.
            }
        }

        // Listen measures unless it reads a valid pause, so what it
        // would read is what counts, not whether the file is gone.
        var status = new PauseReader(new PauseFileSource(path)).Check(DateTimeOffset.UtcNow);

        return status.IsPaused ? failure ?? "the file is still there" : null;
    }

    /// <summary>
    /// The start of the day <paramref name="days"/> after the one
    /// <paramref name="now"/> falls on, in <paramref name="zone"/>.  A
    /// midnight that does not exist, where the clock skips it, becomes
    /// the first minute that does.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateTimeOffset now, int days, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = local.Date.AddDays(days);

        // Never more than a few hours in any zone.
        for (var minutes = 0; minutes < 24 * 60 && zone.IsInvalidTime(start); minutes++)
        {
            start = start.AddMinutes(1);
        }

        return new DateTimeOffset(start, zone.GetUtcOffset(start));
    }

    /// <summary>
    /// The latest a pause started at <paramref name="now"/> may end: the
    /// end of the next day.
    /// </summary>
    public static DateTimeOffset Latest(DateTimeOffset now, TimeZoneInfo zone) => StartOfDay(now, 2, zone);

    /// <summary>
    /// The next time the clock shows <paramref name="time"/>: today, or
    /// tomorrow when it has passed.
    /// </summary>
    public static DateTimeOffset Next(DateTimeOffset now, TimeOnly time, TimeZoneInfo zone)
    {
        for (var days = 0; days <= 2; days++)
        {
            var date = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(days);
            var candidate = date + time.ToTimeSpan();

            // A time the clock skips that night is taken as the first
            // minute after it.
            while (zone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
            }

            var instant = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));

            if (instant > now)
            {
                return instant;
            }
        }

        // Unreachable: two days on is always later.
        return now.AddDays(1);
    }

    /// <summary>"00:00" for the coming midnight, a date for any other day.</summary>
    public static string Describe(DateTimeOffset end, DateTimeOffset now, TimeZoneInfo zone)
    {
        var localEnd = TimeZoneInfo.ConvertTime(end, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var endDate = DateOnly.FromDateTime(localEnd.DateTime);

        var sameDay = endDate == today
                      || (endDate == today.AddDays(1) && localEnd.TimeOfDay == TimeSpan.Zero);

        return sameDay
            ? localEnd.ToString("HH:mm", CultureInfo.InvariantCulture)
            : localEnd.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>What is at the pause file's path, without opening it.</summary>
public sealed record PauseFileStat(bool IsDirectory, bool IsLink, long Length, DateTime LastWriteUtc);

/// <summary>The file system as the pause reader sees it; tests inject faults.</summary>
public interface IPauseFileSource
{
    /// <summary>Null when nothing is at the path.  May throw.</summary>
    PauseFileStat? Stat();

    /// <summary>At most <paramref name="maxBytes"/> + 1 bytes.  May throw.</summary>
    byte[] Read(int maxBytes);
}

public sealed class PauseFileSource(string path) : IPauseFileSource
{
    public PauseFileStat? Stat()
    {
        var info = new FileInfo(path);

        if (info.LinkTarget != null)
        {
            return new PauseFileStat(false, true, 0, default);
        }

        if (Directory.Exists(path))
        {
            return new PauseFileStat(true, false, 0, default);
        }

        if (!info.Exists)
        {
            return null;
        }

        return new PauseFileStat(false, false, info.Length, info.LastWriteTimeUtc);
    }

    public byte[] Read(int maxBytes)
    {
        // Shared every way, so the pause command's rename is never held
        // up, and read no further than the limit.
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0,
        });

        var buffer = new byte[maxBytes + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer[..total];
    }
}

/// <summary>
/// Reads the pause file for listen, once a minute.  Never throws, never
/// waits more than a moment, and anything it cannot trust reads as no
/// pause: listen goes on measuring.  A problem is reported once, when it
/// first appears, not every tick.
/// </summary>
/// <remarks>
/// The file is looked at and read on another thread, waited on for no
/// longer than <c>readTimeout</c>: a pipe put at the path between the
/// look and the open would otherwise block the open, and the tick with
/// it.  A read that has not come back is not started again until it
/// does, so a stuck read costs one thread, not one a minute.
/// </remarks>
public sealed class PauseReader(IPauseFileSource source, Action<string>? warn = null, TimeSpan? readTimeout = null)
{
    private readonly TimeSpan timeout = readTimeout ?? TimeSpan.FromSeconds(1);

    private PauseFileStat? lastStat;
    private string? lastText;
    private string? lastProblem;
    private Task<Snapshot>? stuck;

    // What the other thread found: the file as it was looked at, its
    // text when it was read, or why it was not.
    private sealed record Snapshot(PauseFileStat? Stat, string? Text, string? Problem);

    public PauseStatus Check(DateTimeOffset now)
    {
        try
        {
            return CheckCore(now);
        }
        catch (Exception exception)
        {
            lastStat = null;
            lastText = null;

            return Problem($"cannot be read: {exception.Message}");
        }
    }

    private PauseStatus CheckCore(DateTimeOffset now)
    {
        if (stuck is { IsCompleted: false })
        {
            return Problem("is taking too long to read");
        }

        stuck = null;

        var knownStat = lastStat;
        var knownText = lastText;
        var look = Task.Run(() => Look(knownStat, knownText));

        bool done;

        try
        {
            done = look.Wait(timeout);
        }
        catch (AggregateException exception)
        {
            lastStat = null;
            lastText = null;

            return Problem($"cannot be read: {exception.InnerException?.Message ?? exception.Message}");
        }

        if (!done)
        {
            stuck = look;
            lastStat = null;
            lastText = null;

            return Problem("is taking too long to read");
        }

        var snapshot = look.Result;

        if (snapshot.Stat == null)
        {
            lastStat = null;
            lastText = null;
            lastProblem = null;

            return new PauseStatus(PauseState.None);
        }

        if (snapshot.Problem != null)
        {
            return Problem(snapshot.Problem);
        }

        lastStat = snapshot.Stat;
        lastText = snapshot.Text;

        var status = PauseFile.Parse(snapshot.Text!, now);

        if (status.State == PauseState.Invalid)
        {
            return Problem(status.Problem!);
        }

        lastProblem = null;

        return status;
    }

    // Runs on the other thread; touches no field.
    private Snapshot Look(PauseFileStat? knownStat, string? knownText)
    {
        var stat = source.Stat();

        if (stat == null)
        {
            return new Snapshot(null, null, null);
        }

        // A directory, a link, an empty file and a large one are never
        // opened: a link could lead anywhere, and an empty "file" may be
        // a pipe, which would block the open.
        if (stat.IsDirectory)
        {
            return new Snapshot(stat, null, "is a directory");
        }

        if (stat.IsLink)
        {
            return new Snapshot(stat, null, "is a symbolic link");
        }

        if (stat.Length == 0)
        {
            return new Snapshot(stat, null, "is empty");
        }

        if (stat.Length > PauseFile.MaxBytes)
        {
            return new Snapshot(stat, null, "is too large");
        }

        if (stat == knownStat && knownText != null)
        {
            // The file has not changed since the last tick.
            return new Snapshot(stat, knownText, null);
        }

        var bytes = source.Read(PauseFile.MaxBytes);

        if (bytes.Length > PauseFile.MaxBytes)
        {
            return new Snapshot(stat, null, "is too large");
        }

        return new Snapshot(stat, new UTF8Encoding(false, false).GetString(bytes), null);
    }

    private PauseStatus Problem(string problem)
    {
        if (problem != lastProblem)
        {
            lastProblem = problem;

            try
            {
                warn?.Invoke($"Ignoring the pause file and measuring: it {problem}.");
            }
            catch
            {
                // Logging must not stop measuring either.
            }
        }

        return new PauseStatus(PauseState.Invalid, Problem: problem);
    }
}
