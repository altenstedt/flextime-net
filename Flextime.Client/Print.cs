using System.Globalization;

namespace Flextime.Client;

public class Print(Options options)
{
    public void PrintMeasurements()
    {
        var byDates = Reader.ReadFiles(options.MeasurementsFolder, options.Since);

        var currentWeek = ISOWeek.GetWeekOfYear(byDates.First().Key.ToDateTime(TimeOnly.MinValue));

        var formatter = new MeasurementsFormatter(options.Idle, options.Verbose, options.BlocksPerDay);
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
    }
}