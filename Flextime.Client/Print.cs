using System.Globalization;
using System.Text.Json;

namespace Flextime.Client;

public class Print(Options options)
{
    public int PrintMeasurements()
    {
        if (options.Verbose && !options.Json)
        {
            Console.WriteLine($"Measurements folder is \"{options.MeasurementsFolder}\".");
        }

        if (!Directory.Exists(options.MeasurementsFolder))
        {
            Console.Error.WriteLine($"Measurements folder \"{options.MeasurementsFolder}\" not found.");
            return 1;
        }

        var byDates = Reader.ReadFiles(options.MeasurementsFolder, options.Since);

        var formatter = new MeasurementsFormatter(options.Idle, options.Verbose, options.BlocksPerDay);

        if (options.Json)
        {
            WriteJson(byDates, formatter);

            return 0;
        }

        if (byDates.Count == 0)
        {
            Console.WriteLine("No measurements");
            return 0;
        }

        var currentWeek = ISOWeek.GetWeekOfYear(byDates.First().Key.ToDateTime(TimeOnly.MinValue));

        foreach (var day in byDates)
        {
            if (currentWeek != ISOWeek.GetWeekOfYear(day.Key.ToDateTime(TimeOnly.MinValue)))
            {
                if (options.SplitWeek)
                {
                    Console.WriteLine();
                }
            }

            Console.WriteLine(formatter.SummarizeDay(day.Value.list.ToArray()));

            currentWeek = ISOWeek.GetWeekOfYear(day.Key.ToDateTime(TimeOnly.MinValue));
        }

        return 0;
    }

    // The same envelope `flextimed data --json` writes, so a script can read
    // either one.  Measurements arrive sorted by timestamp from the reader.
    private void WriteJson(
        SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)> byDates,
        MeasurementsFormatter formatter)
    {
        var days = new List<DayActivityDataContract>();

        foreach (var (_, value) in byDates)
        {
            var measurements = value.list;

            var day = formatter.ComputeDay(measurements.Select(item => item.Timestamp).ToArray());

            if (day == null)
            {
                continue;
            }

            days.Add(new DayActivityDataContract(
                day.Date,
                measurements[0].Zone,
                day.Start,
                day.End,
                day.Span,
                day.Work,
                day.Measurements,
                options.Timestamps ? measurements.Select(item => item.Timestamp.ToUnixTimeSeconds()).ToArray() : null));
        }

        var (id, name) = ReadComputer();

        Console.WriteLine(JsonSerializer.Serialize(
            new ActivityDataContract([new ComputerActivityDataContract(id, name, days.ToArray())]),
            ActivitySourceGenerationContext.Default.ActivityDataContract));
    }

    // The daemon owns computer.txt and creates it.  This only borrows the id
    // so the JSON matches; without a daemon there is simply no id to write.
    private (string Id, string? Name) ReadComputer()
    {
        var path = Path.Combine(options.MeasurementsFolder, "..", "computer.txt");

        try
        {
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);

                if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    return (lines[0], string.IsNullOrWhiteSpace(lines[1]) ? null : lines[1]);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The days are the point; the id is decoration.
        }

        return (string.Empty, null);
    }
}
