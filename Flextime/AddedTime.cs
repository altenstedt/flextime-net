using System.Globalization;
using System.Text.Json;

namespace Flextime;

/// <summary>
/// Time the user added to a day by hand in the web client: an earlier
/// start, a later end, and stretches in the day's gaps, in seconds of
/// the day in the day's own zone.  Either time may be missing, and a
/// day may have no ranges.
/// </summary>
public sealed record AddedDay(double? Started, double? Ended, IReadOnlyList<MarkRange> Ranges);

/// <summary>
/// The time added by hand, per day.  The web counts it as work whatever
/// the idle limit, so a sum that left it out would disagree with the
/// day the user read there.
/// </summary>
public sealed class AddedTime(IReadOnlyDictionary<DateOnly, AddedDay> days)
{
    private const double Day = 86_400;

    public static AddedTime Empty { get; } = new(new Dictionary<DateOnly, AddedDay>());

    public IReadOnlyDictionary<DateOnly, AddedDay> Days { get; } = days;

    public AddedDay? On(DateOnly date) => Days.GetValueOrDefault(date);

    /// <summary>
    /// Reads the stored document the way the web's decoder does: any
    /// shape it would refuse is refused here, since a document read
    /// differently would be counted differently.
    /// </summary>
    public static AddedTime Decode(JsonElement document)
    {
        if (document.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Empty;
        }

        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new IdlePolicyException("The added document must be an object.");
        }

        if (!document.TryGetProperty("days", out var stored))
        {
            return Empty;
        }

        if (stored.ValueKind != JsonValueKind.Object)
        {
            throw new IdlePolicyException("Added days must be an object.");
        }

        var days = new Dictionary<DateOnly, AddedDay>();

        foreach (var item in stored.EnumerateObject())
        {
            var day = DecodeDay(item.Name, item.Value);

            // The web looks a day up by its ISO date and never checks
            // the keys, so a key that is not one is read but never
            // counted, rather than refused.
            if (DateOnly.TryParseExact(item.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                days[date] = day;
            }
        }

        return new AddedTime(days);
    }

    private static AddedDay DecodeDay(string name, JsonElement stored)
    {
        if (stored.ValueKind != JsonValueKind.Object)
        {
            throw new IdlePolicyException($"The added time on {name} is not a shape this version knows.");
        }

        double? started = null;
        double? ended = null;
        var ranges = new List<MarkRange>();

        if (stored.TryGetProperty("started", out var startedElement))
        {
            started = Second(name, startedElement);
        }

        if (stored.TryGetProperty("ended", out var endedElement))
        {
            ended = Second(name, endedElement);
        }

        if (stored.TryGetProperty("ranges", out var rangesElement))
        {
            if (rangesElement.ValueKind != JsonValueKind.Array)
            {
                throw new IdlePolicyException($"The added time on {name} is not a shape this version knows.");
            }

            foreach (var range in rangesElement.EnumerateArray())
            {
                if (range.ValueKind != JsonValueKind.Object
                    || !range.TryGetProperty("from", out var fromElement)
                    || !range.TryGetProperty("to", out var toElement))
                {
                    throw new IdlePolicyException($"The added time on {name} is not a shape this version knows.");
                }

                var from = Second(name, fromElement);
                var to = Second(name, toElement);

                if (from >= to)
                {
                    throw new IdlePolicyException($"The added time on {name} is not a shape this version knows.");
                }

                // Kept as stored, not merged: the count takes out what
                // an earlier range already added.
                ranges.Add(new MarkRange(from, to));
            }
        }

        return new AddedDay(started, ended, ranges);
    }

    private static double Second(string name, JsonElement element) =>
        element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var value)
        && value is >= 0 and <= Day
            ? value
            : throw new IdlePolicyException($"The added time on {name} is not a shape this version knows.");

    /// <summary>
    /// The time added to a day, as the web counts it: the sum of
    /// <see cref="Spans"/>.
    /// </summary>
    public static TimeSpan Declared(
        IReadOnlyList<DateTimeOffset> timestamps,
        Func<DateTimeOffset, TimeSpan> limitAt,
        AddedDay? day) =>
        TimeSpan.FromSeconds(Spans(timestamps, limitAt, day).Sum(span => span.End - span.Start));

    /// <summary>
    /// The stretches added to a day, in Unix seconds, as the web's
    /// declaredSpans finds them: a started time before the first
    /// measurement adds the time up to it, an ended time after the last
    /// adds the time from it, and a range adds only what lies outside
    /// the measured blocks and the time already added, so no second
    /// counts twice.  The blocks are the measurements bridged where the
    /// gap is no longer than the limit in force where it begins, as for
    /// the work.
    /// </summary>
    /// <remarks>
    /// All of it lies outside every block, so a marked range never
    /// takes any of it away; only a day marked whole does, and that is
    /// the caller's to leave out.  Seconds of the day become instants
    /// from the day's first measurement, as the web places them, so a
    /// day that crosses a change to or from summer time can land an
    /// hour off.
    /// </remarks>
    public static List<(double Start, double End)> Spans(
        IReadOnlyList<DateTimeOffset> timestamps,
        Func<DateTimeOffset, TimeSpan> limitAt,
        AddedDay? day)
    {
        if (day == null || timestamps.Count == 0)
        {
            return [];
        }

        static double Instant(DateTimeOffset timestamp) => (timestamp - DateTimeOffset.UnixEpoch).TotalSeconds;

        var start = Instant(timestamps[0]);
        var end = Instant(timestamps[^1]);
        var first = timestamps[0].TimeOfDay.TotalSeconds;
        var last = first + end - start;

        double At(double seconds) => start + seconds - first;

        var blocks = DayCount.Blocks(timestamps, limitAt);
        var spans = new List<(double Start, double End)>();

        if (day.Started is { } started && started < first)
        {
            spans.Add((At(Math.Max(0, started)), start));
        }

        if (day.Ended is { } ended && ended > last)
        {
            spans.Add((end, At(Math.Min(Day, ended))));
        }

        foreach (var range in day.Ranges)
        {
            List<(double Start, double End)> pieces = [(At(Math.Max(0, range.From)), At(Math.Min(Day, range.To)))];

            foreach (var taken in blocks.Concat(spans).ToList())
            {
                var cut = new List<(double Start, double End)>();

                foreach (var piece in pieces)
                {
                    if (taken.End <= piece.Start || taken.Start >= piece.End)
                    {
                        cut.Add(piece);

                        continue;
                    }

                    if (taken.Start > piece.Start)
                    {
                        cut.Add((piece.Start, taken.Start));
                    }

                    if (taken.End < piece.End)
                    {
                        cut.Add((taken.End, piece.End));
                    }
                }

                pieces = cut;
            }

            spans.AddRange(pieces.Where(piece => piece.End > piece.Start));
        }

        spans.Sort();

        return spans;
    }
}
