using System.Globalization;

namespace Flextime;

public record DaySummary(
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Span,
    TimeSpan Work,
    int Measurements);

public class MeasurementsFormatter(TimeSpan idle, int blocksPerDay)
{
    /// <summary>
    /// How much of the clock one stamp stands for.  A stamp t is written
    /// when there was input during the minute ending at t, so it covers
    /// (t − 1 min, t]: a block starts a minute before its first stamp.
    /// </summary>
    public static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The blocks of a day: runs of stamps whose neighbours are bridged,
    /// each from a minute before its first stamp to its last.  Between
    /// neighbours the idle time is the step less the minute the later
    /// stamp covers, and a gap is bridged when that idle time is no
    /// longer than the limit (inclusive, matching the web client).  The
    /// limit is the one in force at the earlier stamp — the second the
    /// gap starts — not its end or its middle, again as the web does.
    /// A duplicate or unscheduled stamp less than a minute after the
    /// previous one has no idle time and is always bridged.
    ///
    /// The day's first block alone is clipped at local midnight: its
    /// leading minute stops at 00:00 in the stamp's own zone, so a day
    /// never starts on the date before it.  Later blocks cannot reach
    /// midnight.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset Stop)> Blocks(
        IReadOnlyList<DateTimeOffset> timestamps, Func<TimeSpan, TimeSpan> limitAt)
    {
        var blocks = new List<(DateTimeOffset Start, DateTimeOffset Stop)>();

        for (var i = 0; i < timestamps.Count; i++)
        {
            if (i > 0 && timestamps[i] - timestamps[i - 1] - Minute <= limitAt(timestamps[i - 1].TimeOfDay))
            {
                blocks[^1] = (blocks[^1].Start, timestamps[i]);
            }
            else
            {
                var lead = i == 0 && timestamps[i].TimeOfDay < Minute ? timestamps[i].TimeOfDay : Minute;

                blocks.Add((timestamps[i] - lead, timestamps[i]));
            }
        }

        return blocks;
    }

    public DaySummary? ComputeDay(IReadOnlyList<DateTimeOffset> timestamps) =>
        ComputeDay(timestamps, _ => idle);

    /// <summary>
    /// The same day, computed against a curve rather than one limit.
    /// See <see cref="Blocks"/> for where along a gap the limit is read.
    /// </summary>
    public static DaySummary? ComputeDay(IReadOnlyList<DateTimeOffset> timestamps, IdleProfile profile) =>
        ComputeDay(timestamps, profile.LimitAt);

    // Work is the sum of the block lengths: a minute for each block (less
    // for a first block clipped at midnight) plus every bridged step.
    private static DaySummary? ComputeDay(IReadOnlyList<DateTimeOffset> timestamps, Func<TimeSpan, TimeSpan> limitAt)
    {
        if (timestamps.Count < 2)
        {
            return null;
        }

        var blocks = Blocks(timestamps, limitAt);
        var start = blocks[0].Start;
        var end = timestamps[^1];
        var work = TimeSpan.Zero;

        foreach (var (blockStart, blockStop) in blocks)
        {
            work += blockStop - blockStart;
        }

        return new DaySummary(DateOnly.FromDateTime(timestamps[0].Date), start, end, end - start, work, timestamps.Count);
    }

    public string FormatDay(DaySummary day) =>
        $@"{day.Start:yyyy-MM-dd} {day.Start:HH:mm} – {day.End:HH:mm} {day.Span:hh\:mm} | {day.Work:hh\:mm} w/{ISOWeek.GetWeekOfYear(day.Start.DateTime):00} {day.Start:ddd}";

    // Everything FormatDay writes ahead of the week marker: date, the
    // range, the span and the work.  A day with no span pads to it, so
    // the columns after it stay where the eye already expects them.
    private const int HeadWidth = 38;

    /// <summary>
    /// A day holding a single measurement — a wake, a stop, a lock, and
    /// nothing after it.  There is no span to summarize, but it is
    /// still a day on disk, and a line that left out the date gave the
    /// reader a bare status label with no way to tell which day it
    /// belonged to.  The one timestamp goes where the start time goes,
    /// and the count says why the range is missing.
    /// </summary>
    public static string FormatSparseDay(DateTimeOffset timestamp)
    {
        var head = $"{timestamp:yyyy-MM-dd} {timestamp:HH:mm} (1 measurement)";

        return $"{head,-HeadWidth} w/{ISOWeek.GetWeekOfYear(timestamp.DateTime):00} {timestamp:ddd}";
    }

    public string SummarizeDay(MeasurementWithZone[] measurements)
    {
        if (measurements.Length == 0)
        {
            return string.Empty;
        }

        if (measurements.Length == 1)
        {
            return FormatSparseDay(measurements[0].Timestamp);
        }

        var day = ComputeDay(measurements.Select(item => item.Timestamp).ToArray())!;

        var @base = FormatDay(day);

        if (blocksPerDay > 0)
        {
            var blocks = Blocks(measurements.Select(item => item.Timestamp).ToArray(), _ => idle);

            var suffix = string.Join(", ",
                Enumerable
                    .Range(2, Math.Min(blocksPerDay, blocks.Count - 1))
                    .Select(i => $@"{blocks[^i].Stop:HH:mm}/{blocks[^i].Stop - day.Start:hh\:mm}"));

            return string.IsNullOrEmpty(suffix) ? @base : $"{@base} [{suffix}]";
        }

        return @base;
    }
}
