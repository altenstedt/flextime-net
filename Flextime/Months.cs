using System.Globalization;
using System.Text.RegularExpressions;

namespace Flextime;

/// <summary>
/// Reads a stretch of calendar written as months.  Invoicing follows
/// calendar months, so a month boundary is a real edge in a way a
/// number of days back from today never is.
/// </summary>
public static partial class Months
{
    [GeneratedRegex(
        @"^(?:(?<year>\d{4})-)?(?<from>[A-Za-z]+|\d{1,2})(?:\s*-\s*(?<to>[A-Za-z]+|\d{1,2}))?$")]
    private static partial Regex Spec { get; }

    /// <summary>
    /// Accepts "this", "last", a month by number or name, a range of
    /// them, and any of those with an explicit year: 8, 6-8, aug,
    /// jun-aug, 2026-08, 2026-jun-aug.  Names are less ambiguous than
    /// numbers once a year is involved — 2026-06-08 does read like a
    /// date — so the help leads with them for ranges.
    /// </summary>
    public static bool TryParse(string? text, DateTimeOffset now, out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var today = DateOnly.FromDateTime(now.Date);
        var trimmed = text.Trim();

        if (trimmed.Equals("this", StringComparison.OrdinalIgnoreCase))
        {
            return Range(now.Year, now.Month, null, today, out from, out to);
        }

        if (trimmed.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            var previous = now.Date.AddMonths(-1);

            return Range(previous.Year, previous.Month, null, today, out from, out to);
        }

        var match = Spec.Match(trimmed);

        if (!match.Success)
        {
            return false;
        }

        var year = match.Groups["year"].Success
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
            : now.Year;

        if (!TryMonth(match.Groups["from"].Value, out var first))
        {
            return false;
        }

        int? last = null;

        if (match.Groups["to"].Success)
        {
            if (!TryMonth(match.Groups["to"].Value, out var parsed))
            {
                return false;
            }

            last = parsed;
        }

        return Range(year, first, last, today, out from, out to);
    }

    private static bool TryMonth(string text, out int month)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out month))
        {
            return month is >= 1 and <= 12;
        }

        // Month names are read in the invariant culture on purpose: the
        // option is typed by a person who may well be running under a
        // different one, and "jun" should not stop working abroad.
        var names = CultureInfo.InvariantCulture.DateTimeFormat;

        for (var candidate = 1; candidate <= 12; candidate++)
        {
            if (text.Equals(names.GetMonthName(candidate), StringComparison.OrdinalIgnoreCase)
                || text.Equals(names.GetAbbreviatedMonthName(candidate), StringComparison.OrdinalIgnoreCase))
            {
                month = candidate;

                return true;
            }
        }

        month = 0;

        return false;
    }

    private static bool Range(int year, int first, int? last, DateOnly today, out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;

        if (year < 1 || year > 9999 || last < first)
        {
            return false;
        }

        var final = last ?? first;

        from = new DateOnly(year, first, 1);
        to = new DateOnly(year, final, DateTime.DaysInMonth(year, final));

        // Days that have not happened yet hold nothing on either side.
        if (to > today)
        {
            to = today;
        }

        return from <= to;
    }
}
