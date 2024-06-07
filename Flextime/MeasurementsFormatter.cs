using System.Globalization;

namespace Flextime;

public class MeasurementsFormatter(TimeSpan idle, bool verbose, int blocksPerDay)
{
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
        
        var start = measurements.First().Timestamp;
        var end = measurements.Last().Timestamp;
        var work = TimeSpan.Zero;

        for (var i = 1; i < measurements.Length; i++)
        {
            if (measurements[i].Timestamp - measurements[i - 1].Timestamp < idle)
            {
                work += measurements[i].Timestamp - measurements[i - 1].Timestamp;
            }
        }

        var @base = $@"{start:yyyy-MM-dd} {start:HH:mm} – {end:HH:mm} {end - start:hh\:mm} | {work:hh\:mm} w/{ISOWeek.GetWeekOfYear(start.LocalDateTime):00} {start:ddd}";

        if (blocksPerDay > 0)
        {
            var blocks = new List<(DateTimeOffset start, DateTimeOffset stop)> { (measurements[0].Timestamp, measurements[0].Timestamp) };

            for (var i = 1; i < measurements.Length; i++)
            {
                if (measurements[i].Timestamp - measurements[i - 1].Timestamp < idle)
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
                    .Select(i => $@"{blocks[^i].stop:HH:mm}/{blocks[^i].stop - start:hh\:mm}"));

            if (string.IsNullOrEmpty(suffix))
            {
                return @base;
            }
            
            return
                $@"{@base} [{string.Join(", ", Enumerable.Range(2, Math.Min(blocksPerDay, blocks.Count - 1)).Select(i => $@"{blocks[^i].stop:HH:mm}/{blocks[^i].stop - start:hh\:mm}"))}]";
        }

        return @base;
    }
}