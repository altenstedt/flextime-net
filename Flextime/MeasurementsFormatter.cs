using System.Globalization;

namespace Flextime;

public record DaySummary(
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Span,
    TimeSpan Work,
    int Measurements);

public class MeasurementsFormatter(TimeSpan idle, bool verbose, int blocksPerDay)
{
    public DaySummary? ComputeDay(IReadOnlyList<DateTimeOffset> timestamps)
    {
        if (timestamps.Count < 2)
        {
            return null;
        }

        var start = timestamps[0];
        var end = timestamps[^1];
        var work = TimeSpan.Zero;

        for (var i = 1; i < timestamps.Count; i++)
        {
            var diff = timestamps[i] - timestamps[i - 1];

            // Inclusive: a gap counts as active when it is no longer than
            // the idle limit.  Matches the web client.
            if (diff <= idle)
            {
                work += diff;
            }
        }

        return new DaySummary(DateOnly.FromDateTime(start.Date), start, end, end - start, work, timestamps.Count);
    }

    public string FormatDay(DaySummary day) =>
        $@"{day.Start:yyyy-MM-dd} {day.Start:HH:mm} – {day.End:HH:mm} {day.Span:hh\:mm} | {day.Work:hh\:mm} w/{ISOWeek.GetWeekOfYear(day.Start.DateTime):00} {day.Start:ddd}";

    public string SummarizeDay(MeasurementWithZone[] measurements)
    {
        if (measurements.Length == 0)
        {
            return string.Empty;
        }

        if (measurements.Length == 1)
        {
            return verbose ? "Single measurement" : string.Empty;
        }

        var day = ComputeDay(measurements.Select(item => item.Timestamp).ToArray())!;

        var @base = FormatDay(day);

        if (blocksPerDay > 0)
        {
            var blocks = new List<(DateTimeOffset start, DateTimeOffset stop)> { (measurements[0].Timestamp, measurements[0].Timestamp) };

            for (var i = 1; i < measurements.Length; i++)
            {
                if (measurements[i].Timestamp - measurements[i - 1].Timestamp <= idle)
                {
                    var tmp = blocks[^1];
                    tmp.stop = measurements[i].Timestamp;

                    blocks[^1] = tmp;
                }
                else
                {
                    blocks.Add((measurements[i].Timestamp, measurements[i].Timestamp));
                }
            }

            var suffix = string.Join(", ",
                Enumerable
                    .Range(2, Math.Min(blocksPerDay, blocks.Count - 1))
                    .Select(i => $@"{blocks[^i].stop:HH:mm}/{blocks[^i].stop - day.Start:hh\:mm}"));

            return string.IsNullOrEmpty(suffix) ? @base : $"{@base} [{suffix}]";
        }

        return @base;
    }
}
