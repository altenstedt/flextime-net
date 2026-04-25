using Flextime;

namespace Flextime.Client;

internal static class App
{
    /// <summary>Flextime -- tracking working hours</summary>
    /// <param name="folder">-f, Folder to read measurements from</param>
    /// <param name="verbose">-v, More verbose output</param>
    /// <param name="splitWeek">-s, Split weeks with a new line</param>
    /// <param name="blocks">Number of blocks per day</param>
    /// <param name="idle">Idle limit (default: 10 minutes)</param>
    /// <param name="since">Print measurements since</param>
    public static void Run(
        string? folder = null,
        bool verbose = false,
        bool splitWeek = false,
        int blocks = 0,
        TimeSpan? idle = null,
        TimeSpan since = default)
    {
        var options = new Options
        {
            MeasurementsFolder = folder ?? Constants.MeasurementsFolder,
            Verbose = verbose,
            SplitWeek = splitWeek,
            BlocksPerDay = blocks,
            Idle = idle ?? TimeSpan.FromMinutes(10),
            Since = since
        };

        var print = new Print(options);
        print.PrintMeasurements();
    }
}
