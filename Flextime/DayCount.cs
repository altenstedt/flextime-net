namespace Flextime;

/// <summary>
/// A day as the web client's day list shows it: the range its row
/// prints, the work it sums, whether the reader changed the day (the
/// row's dot), and whether the whole day is marked as not work.
/// </summary>
public sealed record CountedDay(
    DateOnly Date,
    string Zone,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Work,
    bool Custom,
    bool NotWork);

/// <summary>
/// The web client's arithmetic for one day, shared by the commands that
/// must give the numbers the user read there.  The steps are the web's,
/// in its order: the idle profile the day runs on, the measured work at
/// that profile, the time marked as not work taken out after bridging,
/// and the time added by hand counted last, outside every block.
/// </summary>
public static class DayCount
{
    /// <summary>The idle limit used when the user has stored no profiles, as in the web.</summary>
    public const int DefaultIdle = 10;

    /// <summary>
    /// The idle limit a day runs on, by the time of day a gap begins:
    /// the day's curve, or without stored profiles the flat limit, which
    /// is what the web's default profile is.  The flat limit is taken as
    /// given, not held to a curve's bounds, so a limit typed on the
    /// command line means what it says.
    /// </summary>
    public static Func<DateTimeOffset, TimeSpan> LimitFor(IdlePolicy? policy, int flat, DateOnly date, DayOfWeek weekday)
    {
        if (policy?.For(date, weekday) is { } profile)
        {
            return at => profile.LimitAt(at.TimeOfDay);
        }

        var limit = TimeSpan.FromMinutes(flat);

        return _ => limit;
    }

    /// <summary>
    /// The timestamps of a row, read in its zone and in order.  The date
    /// they belong to is the date of the first one, as the web files it.
    /// </summary>
    public static DateTimeOffset[] Read(IEnumerable<long> timestamps, TimeZoneInfo zone) =>
        timestamps
            .Select(item => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(item), zone))
            .OrderBy(item => item)
            .ToArray();

    /// <summary>
    /// The measured work of a row, less what is marked as not work, or
    /// none for a day marked whole.  Null for a row the web does not
    /// list: one with fewer than two measurements.
    /// </summary>
    public static TimeSpan? Measured(
        IReadOnlyList<DateTimeOffset> timestamps,
        Func<DateTimeOffset, TimeSpan> limitAt,
        Marks marks,
        DateOnly date)
    {
        if (timestamps.Count < 2)
        {
            return null;
        }

        if (marks.IsMarked(date))
        {
            return TimeSpan.Zero;
        }

        // Inclusive: a gap counts as work when it is no longer than the
        // limit in force where it begins, as in the web.
        var work = TimeSpan.Zero;

        for (var i = 1; i < timestamps.Count; i++)
        {
            var gap = timestamps[i] - timestamps[i - 1];

            if (gap <= limitAt(timestamps[i - 1]))
            {
                work += gap;
            }
        }

        var marked = Marks.Overlap(timestamps, limitAt, marks.RangesOn(date), date);

        return work > marked ? work - marked : TimeSpan.Zero;
    }

    /// <summary>
    /// The measurements bridged where a gap is no longer than the limit
    /// in force where it begins: the web's blocks, in Unix seconds.
    /// </summary>
    public static List<(double Start, double End)> Blocks(
        IReadOnlyList<DateTimeOffset> timestamps,
        Func<DateTimeOffset, TimeSpan> limitAt)
    {
        var blocks = new List<(double Start, double End)>();

        for (var i = 0; i < timestamps.Count; i++)
        {
            var at = Seconds(timestamps[i]);

            if (i > 0 && timestamps[i] - timestamps[i - 1] <= limitAt(timestamps[i - 1]))
            {
                blocks[^1] = (blocks[^1].Start, at);
            }
            else
            {
                blocks.Add((at, at));
            }
        }

        return blocks;
    }

    /// <summary>
    /// One row of the web's day list.  The timestamps are one item of
    /// the server's answer, read in its zone; null when the web would
    /// not list it.
    /// </summary>
    public static CountedDay? Day(
        string zoneName,
        IReadOnlyList<DateTimeOffset> timestamps,
        IdlePolicy? policy,
        int flat,
        Marks marks,
        AddedTime added)
    {
        if (timestamps.Count < 2)
        {
            return null;
        }

        var date = DateOnly.FromDateTime(timestamps[0].Date);
        var limitAt = LimitFor(policy, flat, date, timestamps[0].DayOfWeek);
        var own = policy?.Overrides.ContainsKey(date) ?? false;
        var zone = TimeZones.Get(zoneName);

        DateTimeOffset At(double seconds) =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddSeconds(seconds), zone);

        // A day marked whole keeps its row and its range, and counts
        // nothing, the added time included.  Only its own profile makes
        // it custom then: the web adds no time to it to show a dot for.
        if (marks.IsMarked(date))
        {
            return new CountedDay(date, zoneName, timestamps[0], timestamps[^1], TimeSpan.Zero, own, true);
        }

        var work = Measured(timestamps, limitAt, marks, date)!.Value;
        var blocks = Blocks(timestamps, limitAt);

        // With marked time the range runs over what is left, as the
        // web's row prints it; a day with nothing left keeps its whole
        // range rather than lose its times.
        var left = Unmarked(blocks, marks.RangesOn(date), seconds => At(seconds).TimeOfDay.TotalSeconds);
        var from = left.Count > 0 ? left[0].Start : Seconds(timestamps[0]);
        var to = left.Count > 0 ? left[^1].End : Seconds(timestamps[^1]);

        // Time added by hand widens the range and counts as work.
        var declared = AddedTime.Spans(timestamps, limitAt, added.On(date));

        foreach (var span in declared)
        {
            from = Math.Min(from, span.Start);
            to = Math.Max(to, span.End);
            work += TimeSpan.FromSeconds(span.End - span.Start);
        }

        return new CountedDay(date, zoneName, At(from), At(to), work, own || declared.Count > 0, false);
    }

    private static double Seconds(DateTimeOffset timestamp) => (timestamp - DateTimeOffset.UnixEpoch).TotalSeconds;

    /// <summary>
    /// The blocks with the marked ranges cut out, as the web's
    /// <c>unmarked</c> leaves them for the row's range.  A block is
    /// placed at its start's time of day plus its length, as the web
    /// places it; a block of one sample is marked whole or not at all.
    /// </summary>
    private static List<(double Start, double End)> Unmarked(
        List<(double Start, double End)> blocks,
        IReadOnlyList<MarkRange> ranges,
        Func<double, double> ofDay)
    {
        if (ranges.Count == 0)
        {
            return blocks;
        }

        // The web's markedSpans: each block's overlap with each range,
        // as instants, and a block of one sample inside a range as a
        // span of no length.
        var spans = new List<(double Start, double End)>();

        foreach (var block in blocks)
        {
            var start = ofDay(block.Start);
            var end = start + block.End - block.Start;

            if (end == start)
            {
                if (ranges.Any(range => range.From <= start && start < range.To))
                {
                    spans.Add((block.Start, block.Start));
                }

                continue;
            }

            foreach (var range in ranges)
            {
                var from = Math.Max(start, range.From);
                var to = Math.Min(end, range.To);

                if (to > from)
                {
                    spans.Add((block.Start + from - start, block.Start + to - start));
                }
            }
        }

        if (spans.Count == 0)
        {
            return blocks;
        }

        spans.Sort();

        // The web's splitMarked, keeping only the pieces not marked.
        var pieces = new List<(double Start, double End)>();

        foreach (var block in blocks)
        {
            if (block.End == block.Start)
            {
                var marked = spans.Any(span =>
                    span.Start == block.Start || (span.Start < block.Start && block.Start < span.End));

                if (!marked)
                {
                    pieces.Add(block);
                }

                continue;
            }

            var at = block.Start;
            var cut = false;

            foreach (var span in spans)
            {
                if (span.End <= at || span.Start >= block.End)
                {
                    continue;
                }

                if (span.Start > at)
                {
                    pieces.Add((at, span.Start));
                }

                cut = true;
                at = Math.Min(block.End, span.End);
            }

            if (at < block.End || !cut)
            {
                pieces.Add((at, block.End));
            }
        }

        return pieces;
    }
}
