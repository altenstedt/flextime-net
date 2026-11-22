using System.Text.Json;

namespace Flextime;

/// <summary>
/// The time the user has marked as not work: whole days, and ranges of
/// days in seconds of the day, in the day's own zone.  Marked time keeps
/// its measurements and its place in the listing, but contributes
/// nothing to the sums — so a report that ignored marks would disagree
/// with every number the web client showed.
/// </summary>
public sealed class Marks(IReadOnlySet<DateOnly> days, IReadOnlyDictionary<DateOnly, IReadOnlyList<MarkRange>>? ranges = null)
{
    public static Marks Empty { get; } = new(new HashSet<DateOnly>());

    /// <summary>Days marked whole.</summary>
    public IReadOnlySet<DateOnly> Days { get; } = days;

    /// <summary>Days with parts marked, merged and sorted.</summary>
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<MarkRange>> Ranges { get; } =
        ranges ?? new Dictionary<DateOnly, IReadOnlyList<MarkRange>>();

    public bool IsMarked(DateOnly date) => Days.Contains(date);

    public IReadOnlyList<MarkRange> RangesOn(DateOnly date) =>
        Ranges.TryGetValue(date, out var list) ? list : [];

    /// <summary>
    /// Reads the marks rows of the server's <c>days</c> (api-version
    /// 1.10): <c>"day"</c> for a whole day, or a list of ranges.
    /// </summary>
    public static Marks Decode(JsonElement days)
    {
        var marked = new HashSet<DateOnly>();
        var ranges = new Dictionary<DateOnly, IReadOnlyList<MarkRange>>();

        foreach (var (date, name, value) in DayItems.Values(days, "marks"))
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                ranges[date] = DecodeRanges(name, value);

                continue;
            }

            if (value.ValueKind != JsonValueKind.String || value.GetString() != "day")
            {
                throw new IdlePolicyException($"The mark on {name} is not a shape this version knows.");
            }

            marked.Add(date);
        }

        return new Marks(marked, ranges);
    }

    // The ranges the web's decoder accepts, and no others: a mark read
    // differently here would be counted differently.
    private static List<MarkRange> DecodeRanges(string name, JsonElement array)
    {
        var list = new List<MarkRange>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("from", out var fromElement)
                || !item.TryGetProperty("to", out var toElement)
                || fromElement.ValueKind != JsonValueKind.Number
                || toElement.ValueKind != JsonValueKind.Number
                || !fromElement.TryGetDouble(out var from)
                || !toElement.TryGetDouble(out var to)
                || from is < 0 or > 86_400
                || to is < 0 or > 86_400
                || from >= to)
            {
                throw new IdlePolicyException($"The mark on {name} is not a shape this version knows.");
            }

            list.Add(new MarkRange(from, to));
        }

        // Merged where they touch or overlap, as the web merges them, so
        // no second counts twice.
        var merged = new List<MarkRange>();

        foreach (var next in list.OrderBy(item => item.From))
        {
            if (merged.Count > 0 && next.From <= merged[^1].To)
            {
                merged[^1] = merged[^1] with { To = Math.Max(merged[^1].To, next.To) };
            }
            else
            {
                merged.Add(next);
            }
        }

        return merged;
    }

    /// <summary>
    /// The part of a day's work that falls in <paramref name="ranges"/>.
    /// Work is the gaps between measurements no longer than the limit in
    /// force where each begins; the marks take their overlap out after
    /// that, so marking never changes which gaps count.  Each gap is
    /// placed at its start's time of day plus its length, as the web
    /// places a block, counted from the start of <paramref name="date"/>:
    /// a row that runs past midnight places its last gaps past 24 hours,
    /// where no range of the date reaches, rather than on the date's own
    /// early hours.
    /// </summary>
    public static TimeSpan Overlap(
        IReadOnlyList<DateTimeOffset> timestamps,
        Func<DateTimeOffset, TimeSpan> limitAt,
        IReadOnlyList<MarkRange> ranges,
        DateOnly date)
    {
        if (ranges.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = 0.0;

        for (var i = 1; i < timestamps.Count; i++)
        {
            var diff = timestamps[i] - timestamps[i - 1];

            if (diff > limitAt(timestamps[i - 1]))
            {
                continue;
            }

            var at = timestamps[i - 1];
            var days = DateOnly.FromDateTime(at.DateTime).DayNumber - date.DayNumber;
            var start = at.TimeOfDay.TotalSeconds + days * 86_400.0;
            var end = start + diff.TotalSeconds;

            foreach (var range in ranges)
            {
                var overlap = Math.Min(end, range.To) - Math.Max(start, range.From);

                if (overlap > 0)
                {
                    seconds += overlap;
                }
            }
        }

        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>Part of a day, in seconds of the day.</summary>
public readonly record struct MarkRange(double From, double To);
