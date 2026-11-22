namespace Flextime.Daemon;

/// <summary>
/// The days a command covers, from its --since, --week and --month
/// options.  One reading for every command that takes them, so the same
/// words name the same days in data and export.
/// </summary>
public static class RangeOptions
{
    /// <summary>
    /// Reads the options.  --week and --month exclude each other; either
    /// one wins over --since, which commands give a default.  With none
    /// of the three there is no range: <paramref name="range"/> is null.
    /// What cannot be read is said on standard error, and false returned.
    /// </summary>
    public static bool TryRead(
        string? since,
        string? week,
        string? month,
        DateTimeOffset now,
        out (DateOnly From, DateOnly To)? range)
    {
        range = null;

        if (week != null && month != null)
        {
            Console.Error.WriteLine("Give either --week or --month, not both.");

            return false;
        }

        DateOnly from;
        DateOnly to;

        if (month != null)
        {
            if (!Months.TryParse(month, now, out from, out to))
            {
                Console.Error.WriteLine($"Cannot read \"{month}\" as a month.");
                Console.Error.WriteLine("Try this, last, aug, jun-aug, 8, 6-8, 2026-08 or 2026-jun-aug.");

                return false;
            }
        }
        else if (week != null)
        {
            if (!IsoWeeks.TryParse(week, now, out from, out to))
            {
                Console.Error.WriteLine($"Cannot read \"{week}\" as an ISO week.");
                Console.Error.WriteLine("Try this, last, 34, 32-34, 2026-W34 or 2026-W32-34.");

                return false;
            }
        }
        else if (since != null)
        {
            if (!DurationParser.TryParse(since, now, out var sinceValue))
            {
                Console.Error.WriteLine($"Cannot read \"{since}\" as a length of time.");
                Console.Error.WriteLine("Try 3d, 1w, \"2 weeks ago\", yesterday, \"last week\", P7D or 7.00:00:00.");

                return false;
            }

            from = DateOnly.FromDateTime((now - sinceValue).Date);
            to = DateOnly.FromDateTime(now.Date);
        }
        else
        {
            return true;
        }

        range = (from, to);

        return true;
    }
}
