using Flextime;

namespace Flextime.Client;

public static class App
{
    /// <summary>Flextime -- tracking working hours. Reads the measurements this computer stored on disk; never uses the network.</summary>
    /// <param name="folder">-f, Folder to read measurements from</param>
    /// <param name="verbose">-v, More verbose output</param>
    /// <param name="splitWeek">-s, Split weeks with a new line</param>
    /// <param name="blocks">Number of blocks per day</param>
    /// <param name="days">-d, Number of days to show. 0 shows every day on disk.</param>
    /// <param name="idle">-i, Idle limit in minutes. Gaps no longer than this count as active time.</param>
    /// <param name="since">Print measurements since, for example 3d, "2 weeks ago", yesterday, P3D or 3.00:00:00. Takes precedence over --days.</param>
    /// <param name="timestamps">Include raw timestamps (Unix seconds) in JSON output.</param>
    /// <param name="json">Write JSON to standard out.</param>
    public static int Run(
        string? folder = null,
        bool verbose = false,
        bool splitWeek = false,
        int blocks = 0,
        int days = 30,
        int idle = 10,
        string? since = null,
        bool timestamps = false,
        bool json = false)
    {
        TimeSpan? sinceValue = null;

        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DurationParser.TryParse(since, DateTimeOffset.Now, out var parsed))
            {
                Console.Error.WriteLine($"Cannot read \"{since}\" as a length of time.");
                Console.Error.WriteLine("Try 3d, 1h30m, \"2 weeks ago\", yesterday, \"last week\", P3D or 3.00:00:00.");

                return 1;
            }

            sinceValue = parsed;
        }

        var options = new Options
        {
            MeasurementsFolder = folder ?? Constants.MeasurementsFolder,
            Verbose = verbose,
            SplitWeek = splitWeek,
            BlocksPerDay = blocks,
            Idle = TimeSpan.FromMinutes(idle),
            Since = ResolveSince(days, sinceValue, DateTimeOffset.Now),
            Timestamps = timestamps,
            Json = json
        };

        var print = new Print(options);

        return print.PrintMeasurements();
    }

    /// <summary>
    /// The cutoff the reader is given.  --since is the older spelling and
    /// wins when written out; otherwise --days counts back from today, and
    /// zero or less means every day on disk.
    /// </summary>
    public static TimeSpan ResolveSince(int days, TimeSpan? since, DateTimeOffset now)
    {
        if (since == null)
        {
            return days <= 0 ? TimeSpan.Zero : TimeSpan.FromDays(days);
        }

        // Whole days are all this prints, so a lookback shorter than a day
        // means today: without this, --since 1h30m lands on today, the
        // reader keeps only days after it, and nothing is left to show.
        return since < TimeSpan.FromDays(1)
            ? now - new DateTimeOffset(now.Date, now.Offset).AddSeconds(-1)
            : since.Value;
    }
}
