using System.Globalization;
using System.Text.RegularExpressions;
using Flextime.Monitor;

namespace Flextime.Daemon;

/// <summary>
/// The pause and resume commands.  The pause is written first and on its
/// own, so nothing that follows (reading measurements, the network) can
/// keep it from taking effect; the not-work mark is saved for sync before
/// the server is tried, so an interrupted or failed write is not lost.
/// </summary>
/// <remarks>
/// Listen writes nothing for a pause, not even where it begins or ends:
/// it is a gap like any other.  A pause shorter than the idle limit in
/// force is therefore bridged and still counts as work; only a longer
/// pause leaves the time out.
/// </remarks>
public partial class PauseCommand(IHttpClientFactory httpClientFactory, DeviceCode deviceCode)
{
    // Where the state lives, the clock and the outputs; tests set them.
    public string PausePath { get; init; } = StateFiles.PausePath;
    public string StopPath { get; init; } = StateFiles.StopPath;
    public string MeasurementsFolder { get; init; } = Constants.MeasurementsFolder;
    public PendingMarks Pending { get; init; } = PendingMarks.Default;
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;
    public Func<string> ZoneId { get; init; } = UserInputMonitor.GetTimeZoneInfo;
    public Func<bool> IsAuthenticated { get; init; } = () => deviceCode.IsAuthenticated;
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;

    public async Task<int> Pause(string? @for, string? until, string? since, bool noBackdate,
        CancellationToken cancellationToken)
    {
        var now = Now();

        if (@for != null && until != null)
        {
            Error.WriteLine("Give either --for or --until, not both.");

            return 1;
        }

        if (since != null && noBackdate)
        {
            Error.WriteLine("Give either --since or --no-backdate, not both.");

            return 1;
        }

        if (StateFiles.TryRead(StopPath) != null)
        {
            Error.WriteLine("Listen is stopped, so there is nothing to pause. Use the start command to measure again.");

            return 1;
        }

        var latest = PauseFile.Latest(now, Zone);
        DateTimeOffset end;

        if (@for != null)
        {
            if (!DurationParser.TryParseInterval(@for, out var length))
            {
                Error.WriteLine($"Cannot read \"{TerminalText.Clean(@for)}\" as a length of time. Try 2h, 90m or \"1 hour\".");

                return 1;
            }

            if (length < TimeSpan.FromMinutes(1))
            {
                Error.WriteLine("A pause lasts a minute at least.");

                return 1;
            }

            end = now + length;
        }
        else if (until != null)
        {
            if (!TryReadUntil(until, out var time, out var day))
            {
                Error.WriteLine($"Cannot read \"{TerminalText.Clean(until)}\" as a time. Try 07:00 or \"mon 07:00\".");

                return 1;
            }

            end = PauseFile.Next(now, time, Zone, day);
        }
        else
        {
            end = PauseFile.StartOfDay(now, 1, Zone);
        }

        if (end > latest)
        {
            var lastDay = TimeZoneInfo.ConvertTime(latest, Zone).AddDays(-1).DayOfWeek;

            Error.WriteLine($"A pause ends by the end of {lastDay} at the latest. Use the stop command to stop for longer.");

            return 1;
        }

        DateTimeOffset? sinceFrom = null;

        if (since != null)
        {
            if (!TryReadSince(since, now, out var from, out var problem))
            {
                Error.WriteLine(problem);

                return 1;
            }

            sinceFrom = from;
        }

        // Checked before writing, so a pause in force is never replaced
        // by one listen would ignore.
        if (PauseFile.Parse(PauseFile.Format(end, now), now) is { IsPaused: false } check)
        {
            Error.WriteLine($"Cannot pause until {PauseFile.Describe(end, now, Zone)}: the pause {check.Problem ?? "would already be over"}.");

            return 1;
        }

        try
        {
            PauseFile.Write(PausePath, end, now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"Could not write the pause: {exception.Message}");

            return 1;
        }

        // What listen will read is what counts.
        var status = new PauseReader(new PauseFileSource(PausePath)).Check(now);

        if (!status.IsPaused)
        {
            Error.WriteLine($"The pause was written, but listen would not take it: the file {status.Problem ?? "is not there"}.");

            return 1;
        }

        Out.WriteLine($"Paused until {PauseFile.Describe(end, now, Zone)}. Use the resume command to measure again sooner.");

        if (noBackdate)
        {
            return 0;
        }

        await MarkBefore(now, sinceFrom, cancellationToken);

        // The pause stands whatever happened to the mark.
        return 0;
    }

    private async Task MarkBefore(DateTimeOffset now, DateTimeOffset? sinceFrom, CancellationToken cancellationToken)
    {
        List<MeasurementWithZone> measurements;

        try
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

            measurements = Reader.ReadWindow(MeasurementsFolder, today.AddDays(-1))
                .Values
                .SelectMany(day => day.list)
                .ToList();
        }
        catch (Exception exception)
        {
            Error.WriteLine($"Could not read the measurements, so nothing is marked as not work: {exception.Message}");

            return;
        }

        var to = now;
        DateTimeOffset from;
        string zoneId;

        if (sinceFrom is { } chosen)
        {
            from = chosen;

            // The day's zone is the one the time was measured in; with
            // nothing measured, the one listen would measure in now.
            zoneId = measurements.Count > 0 ? measurements.MaxBy(item => item.Timestamp)!.Zone : ZoneId();
        }
        else
        {
            if (Backdate.FindStretch(measurements, now) is not { } stretch)
            {
                Out.WriteLine("Nothing was measured just before the pause, so nothing is marked as not work.");

                return;
            }

            if (to - stretch.From > Backdate.Limit)
            {
                Out.WriteLine(
                    $"Not marking {FormatLength(to - stretch.From)} before the pause as not work; "
                    + "mark it in the web if it was not work.");

                return;
            }

            (from, zoneId) = stretch;
        }

        if (!TimeZones.TryGet(zoneId, out var zone))
        {
            Error.WriteLine($"The time zone {TerminalText.Clean(zoneId)} is unknown, so nothing is marked as not work.");

            return;
        }

        if (to <= from)
        {
            return;
        }

        var span = Backdate.Describe(from, to, zone, now);
        var mark = new PendingMark(
            DateTimeOffset.FromUnixTimeSeconds(from.ToUnixTimeSeconds()),
            DateTimeOffset.FromUnixTimeSeconds(to.ToUnixTimeSeconds()),
            zoneId,
            DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()));

        // Saved first: if what follows fails or is interrupted, sync
        // writes it.  Writing it twice changes nothing.
        var saved = await Boxed(token => Pending.Add(mark, now, token), false, cancellationToken);

        if (!IsAuthenticated())
        {
            Out.WriteLine(saved
                ? $"Not logged in; {span} is marked as not work once you log in and sync runs."
                : $"Not logged in, and {span} could not be saved to mark as not work later.");

            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(NetworkTimeout);

        try
        {
            var writer = new MarksWriter(httpClientFactory.CreateClient("ApiHttpClient"));
            var (result, reason) = await writer.Apply(Backdate.Split(mark.From, mark.To, zone), timeout.Token);

            await Boxed(async token =>
            {
                await Pending.Remove(mark, now, token);

                return true;
            }, false, cancellationToken);

            if (result == MarkResult.Refused)
            {
                Error.WriteLine($"Could not mark {span} as not work: {reason}.");
            }
            else
            {
                Out.WriteLine($"Marked {span} as not work.");
            }
        }
        catch (Exception exception)
        {
            Out.WriteLine(saved
                ? $"Could not reach the server; sync marks {span} as not work later."
                : $"Could not mark {span} as not work: {TerminalText.Clean(exception.Message)}");
        }
    }

    // Runs on another thread and waits no longer than the network
    // timeout and a margin: a file operation can block where no token
    // reaches (a pipe at the path, say), and the pause is already written,
    // so the command must still finish.  A worker left behind is a
    // background thread and does not keep the process alive.
    private async Task<T> Boxed<T>(Func<CancellationToken, Task<T>> work, T fallback, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(NetworkTimeout);

        try
        {
            return await Task.Run(() => work(timeout.Token), CancellationToken.None)
                .WaitAsync(NetworkTimeout + TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return fallback;
        }
    }

    public int Resume()
    {
        var before = new PauseReader(new PauseFileSource(PausePath)).Check(Now());

        if (PauseFile.Clear(PausePath) is { } failure)
        {
            Error.WriteLine($"Could not remove the pause: {failure}");

            return 1;
        }

        Out.WriteLine(before.IsPaused
            ? "Measuring again from the next minute."
            : "Not paused.");

        return 0;
    }

    private bool TryReadSince(string text, DateTimeOffset now, out DateTimeOffset from, out string problem)
    {
        from = default;
        problem = "";

        if (TryReadClock(text, out var time))
        {
            // A time of day means today.
            var date = TimeZoneInfo.ConvertTime(now, Zone).Date + time.ToTimeSpan();

            while (Zone.IsInvalidTime(date))
            {
                date = date.AddMinutes(1);
            }

            from = new DateTimeOffset(date, Zone.GetUtcOffset(date));
        }
        else if (DurationParser.TryParse(text, TimeZoneInfo.ConvertTime(now, Zone), out var length) && length > TimeSpan.Zero)
        {
            from = now - length;
        }
        else
        {
            problem = $"Cannot read \"{TerminalText.Clean(text)}\" as a time. Try 19:42 or 10m.";

            return false;
        }

        if (from > now)
        {
            problem = $"--since {TerminalText.Clean(text)} is in the future.";

            return false;
        }

        if (now - from > Backdate.MaxSince)
        {
            problem = "--since reaches back 24 hours at the most.";

            return false;
        }

        return true;
    }

    private static bool TryReadClock(string text, out TimeOnly time)
    {
        time = default;

        return ClockPattern().IsMatch(text.Trim())
               && TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out time);
    }

    // A time of day, or a weekday and a time of day: 07:00, mon 07:00,
    // Monday 7:00.  Weekdays in English, as the rest of the command.
    private static bool TryReadUntil(string text, out TimeOnly time, out DayOfWeek? day)
    {
        day = null;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 2)
        {
            var name = parts[0].ToLowerInvariant();
            var match = Enum.GetValues<DayOfWeek>().Where(weekday =>
            {
                var full = weekday.ToString().ToLowerInvariant();

                return name == full || name == full[..3];
            }).ToList();

            if (match.Count != 1)
            {
                time = default;

                return false;
            }

            day = match[0];
        }
        else if (parts.Length != 1)
        {
            time = default;

            return false;
        }

        return TryReadClock(parts[^1], out time);
    }

    public static string FormatLength(TimeSpan length)
    {
        var minutes = (int)Math.Round(length.TotalMinutes);

        return minutes >= 60
            ? $"{minutes / 60} h {minutes % 60} min"
            : $"{minutes} min";
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}$")]
    private static partial Regex ClockPattern();
}
