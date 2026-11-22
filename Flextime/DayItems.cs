using System.Globalization;
using System.Text.Json;

namespace Flextime;

/// <summary>
/// Reads the <c>days</c> the server answers from api-version 1.10, one
/// item per date with a row of marks, of added time, or both:
/// <c>{ "date": "2026-11-20", "marks": { "value": …, "version": … },
/// "added": { "value": …, "version": … } }</c>.
/// </summary>
internal static class DayItems
{
    /// <summary>
    /// The value of each item's <paramref name="kind"/> row, with its
    /// date.  An item without that row is skipped; anything that is not
    /// the shape above is refused, since a day read differently would be
    /// counted differently.  Absent or null <paramref name="days"/> has
    /// no rows.
    /// </summary>
    public static IEnumerable<(DateOnly Date, string Name, JsonElement Value)> Values(JsonElement days, string kind)
    {
        if (days.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            yield break;
        }

        if (days.ValueKind != JsonValueKind.Array)
        {
            throw new IdlePolicyException("The days must be an array.");
        }

        foreach (var item in days.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("date", out var dateElement)
                || dateElement.ValueKind != JsonValueKind.String)
            {
                throw new IdlePolicyException("A day has no date.");
            }

            var name = dateElement.GetString()!;

            // The API stores exactly this form, so any other is not a
            // date it wrote.
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new IdlePolicyException($"A day is dated \"{name}\", which is not a date.");
            }

            if (!item.TryGetProperty(kind, out var row) || row.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("value", out var value))
            {
                throw new IdlePolicyException($"The {kind} row on {name} is not a shape this version knows.");
            }

            yield return (date, name, value);
        }
    }
}
