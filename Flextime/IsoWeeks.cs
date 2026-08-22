using System.Globalization;
using System.Text.RegularExpressions;

namespace Flextime;

/// <summary>
/// Reads a stretch of calendar written as ISO weeks.  Billing runs in
/// weeks, so asking for one by number beats counting days back to find
/// where it started.
/// </summary>
public static partial class IsoWeeks
{
    [GeneratedRegex(@"^(?:(?<year>\d{4})-?[Ww])?(?<from>\d{1,2})(?:\s*-\s*(?:(?:\d{4})-?[Ww])?(?<to>\d{1,2}))?$")]
    private static partial Regex Spec { get; }

    /// <summary>
    /// Accepts "this", "last", a week number, a range of them, and any
    /// of those written with an explicit year: 34, 32-34, 2026-W34,
    /// 2026-W32-34.  A bare number means the current ISO year, which is
    /// not always the calendar year in the days around New Year — hence
    /// reading it from the week itself rather than from the date.
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

        if (trimmed.Equals("this", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            var moment = trimmed.Equals("last", StringComparison.OrdinalIgnoreCase)
                ? now.Date.AddDays(-7)
                : now.Date;

            return Range(ISOWeek.GetYear(moment), ISOWeek.GetWeekOfYear(moment), null, today, out from, out to);
        }

        var match = Spec.Match(trimmed);

        if (!match.Success)
        {
            return false;
        }

        var year = match.Groups["year"].Success
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
            : ISOWeek.GetYear(now.Date);

        var first = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);

        int? last = match.Groups["to"].Success
            ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture)
            : null;

        return Range(year, first, last, today, out from, out to);
    }

    private static bool Range(int year, int first, int? last, DateOnly today, out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;

        var weeks = ISOWeek.GetWeeksInYear(year);

        if (first < 1 || first > weeks || last is < 1 || last > weeks || last < first)
        {
            return false;
        }

        from = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, first, DayOfWeek.Monday));
        to = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, last ?? first, DayOfWeek.Monday)).AddDays(6);

        // Days that have not happened yet hold nothing on either side,
        // and a range that runs into the future reads as though it
        // might.
        if (to > today)
        {
            to = today;
        }

        return from <= to;
    }
}
