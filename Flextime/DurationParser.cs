using System.Globalization;
using System.Text.RegularExpressions;

namespace Flextime;

/// <summary>
/// Reads how far back to look, written the ways it is already written on
/// a command line: the .NET TimeSpan forms, the compact unit form Go and
/// Prometheus made common, the words GNU date and git accept, and ISO
/// 8601 durations.  English only, and numbers are read invariantly, so
/// the same string means the same thing on every machine.
/// </summary>
public static partial class DurationParser
{
    /// <param name="now">
    /// The instant the words are relative to.  Passed in rather than read
    /// here so the keywords can be tested.
    /// </param>
    public static bool TryParse(string? text, DateTimeOffset now, out TimeSpan value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // "3 days ago" and "3 days" are the same to a lookback; the word
        // only says out loud which way we were already going.
        if (trimmed.EndsWith(" ago", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^" ago".Length].TrimEnd();
        }

        return TryParseKeyword(trimmed, now, out value)
               || TryParseIso8601(trimmed, out value)
               || TryParseUnits(trimmed, out value)
               // Last, so that the colon forms keep the meaning they have
               // always had here — 36:00:00 is 36 days, not 36 hours.
               || TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseKeyword(string text, DateTimeOffset now, out TimeSpan value)
    {
        var today = DateOnly.FromDateTime(now.Date);

        DateOnly? from = text.ToLowerInvariant() switch
        {
            "today" => today,
            "yesterday" => today.AddDays(-1),
            "this week" => StartOfWeek(today),
            "last week" => StartOfWeek(today).AddDays(-7),
            _ => null
        };

        if (from == null)
        {
            value = default;

            return false;
        }

        value = SinceStartOf(from.Value, now);

        return true;
    }

    // Monday, matching the ISO week the day lines are numbered with.
    private static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    // Readers drop days on or before the cutoff date, so aim the cutoff at
    // the last moment of the previous day to keep the named day whole.
    private static TimeSpan SinceStartOf(DateOnly from, DateTimeOffset now) =>
        now - new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), now.Offset).AddSeconds(-1);

    private static bool TryParseIso8601(string text, out TimeSpan value)
    {
        value = default;

        var match = Iso8601Pattern().Match(text);

        if (!match.Success)
        {
            return false;
        }

        try
        {
            value = Component(match, "weeks", days => TimeSpan.FromDays(days * 7))
                    + Component(match, "days", TimeSpan.FromDays)
                    + Component(match, "hours", TimeSpan.FromHours)
                    + Component(match, "minutes", TimeSpan.FromMinutes)
                    + Component(match, "seconds", TimeSpan.FromSeconds);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentException)
        {
            return false;
        }

        return true;
    }

    private static TimeSpan Component(Match match, string name, Func<double, TimeSpan> scale)
    {
        var group = match.Groups[name];

        // The standard allows a comma for the decimal mark.
        return group.Success
            ? scale(double.Parse(group.Value.Replace(',', '.'), CultureInfo.InvariantCulture))
            : TimeSpan.Zero;
    }

    private static bool TryParseUnits(string text, out TimeSpan value)
    {
        value = default;

        var total = TimeSpan.Zero;
        var position = 0;
        var matched = false;

        while (position < text.Length)
        {
            var match = UnitPattern().Match(text, position);

            // Anything the pattern cannot take is a typo, not a unit we
            // silently skip: "3 dayz" must fail rather than mean 3 days.
            if (!match.Success)
            {
                value = default;

                return false;
            }

            try
            {
                total += Scale(
                    double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture),
                    match.Groups["unit"].Value);
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentException)
            {
                value = default;

                return false;
            }

            position = match.Index + match.Length;
            matched = true;
        }

        value = total;

        return matched;
    }

    // The alternation decides the meaning, so the first letter is enough.
    private static TimeSpan Scale(double number, string unit) =>
        char.ToLowerInvariant(unit[0]) switch
        {
            'w' => TimeSpan.FromDays(number * 7),
            'd' => TimeSpan.FromDays(number),
            'h' => TimeSpan.FromHours(number),
            'm' => TimeSpan.FromMinutes(number),
            _ => TimeSpan.FromSeconds(number)
        };

    // Weeks and days, then an optional time part.  Months and years are
    // left out: neither has a fixed length, and M would fight minutes.
    [GeneratedRegex(
        @"^P(?!$)(?:(?<weeks>\d+(?:[.,]\d+)?)W)?(?:(?<days>\d+(?:[.,]\d+)?)D)?" +
        @"(?:T(?!$)(?:(?<hours>\d+(?:[.,]\d+)?)H)?(?:(?<minutes>\d+(?:[.,]\d+)?)M)?(?:(?<seconds>\d+(?:[.,]\d+)?)S)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601Pattern();

    // Longer spellings first, so "minutes" is not read as "m" and "inutes".
    // Anchored to the position it is given, so the whole string has to be
    // consumed by repeated matches for the parse to stand.
    [GeneratedRegex(
        @"\G\s*(?<number>\d+(?:\.\d+)?)\s*(?<unit>weeks?|w|days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern();
}
