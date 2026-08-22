using System.Text.Json;

namespace Flextime;

/// <summary>
/// The days the user has marked as not work.  A marked day keeps its
/// measurements and its place in the listing, but contributes nothing
/// to the sums — so a report that ignored marks would disagree with
/// every number the web client showed.
/// </summary>
public sealed class Marks(IReadOnlySet<DateOnly> days)
{
    public static Marks Empty { get; } = new(new HashSet<DateOnly>());

    public IReadOnlySet<DateOnly> Days { get; } = days;

    public bool IsMarked(DateOnly date) => Days.Contains(date);

    public static Marks Decode(JsonElement document)
    {
        if (document.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Empty;
        }

        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new IdlePolicyException("The marks document must be an object.");
        }

        if (!document.TryGetProperty("marks", out var marks))
        {
            return Empty;
        }

        if (marks.ValueKind != JsonValueKind.Object)
        {
            throw new IdlePolicyException("Marks must be an object.");
        }

        var days = new HashSet<DateOnly>();

        foreach (var item in marks.EnumerateObject())
        {
            if (!DateOnly.TryParse(item.Name, out var date))
            {
                throw new IdlePolicyException($"A mark is keyed by \"{item.Name}\", which is not a date.");
            }

            // A whole marked day is the only shape anything writes
            // today.  Ranges are part of the stored format and the web
            // decodes them, but nothing subtracts them yet — so a
            // document holding one means this build and that one would
            // count the day differently.  Refuse rather than agree by
            // accident.
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                throw new IdlePolicyException(
                    $"The mark on {item.Name} covers ranges of the day, which this version cannot count. "
                    + "Update flextimed.");
            }

            if (item.Value.ValueKind != JsonValueKind.String || item.Value.GetString() != "day")
            {
                throw new IdlePolicyException($"The mark on {item.Name} is not a shape this version knows.");
            }

            days.Add(date);
        }

        return new Marks(days);
    }
}
