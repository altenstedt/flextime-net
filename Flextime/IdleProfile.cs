using System.Text.Json;

namespace Flextime;

/// <summary>
/// The idle limit as a function of time of day: one limit per
/// minute-of-day bin.  A flat limit is the special case where every bin
/// holds the same value.
///
/// This mirrors the rule the web client applies, because the numbers a
/// user reads there are the ones they write down.  Where the two could
/// drift, this file follows the web: limits are clamped and rounded the
/// same way, a short curve is padded the same way, and a gap is bridged
/// when it is no longer than the limit at the second the gap starts.
/// </summary>
public sealed class IdleProfile
{
    public const int Bins = 24 * 60;
    public const int LimitMinimum = 1;
    public const int LimitMaximum = 24 * 60;

    private const int PaddingLimit = 10;

    private readonly int[] limits;

    private IdleProfile(int[] limits) => this.limits = limits;

    public static IdleProfile Flat(int minutes)
    {
        var limits = new int[Bins];

        Array.Fill(limits, Clamp(minutes));

        return new IdleProfile(limits);
    }

    /// <summary>The idle limit in force at a given time of day.</summary>
    public TimeSpan LimitAt(TimeSpan timeOfDay)
    {
        var bin = (int)(timeOfDay.TotalMinutes);

        return TimeSpan.FromMinutes(limits[Math.Clamp(bin, 0, Bins - 1)]);
    }

    /// <summary>
    /// Reads a stored curve.  Two shapes are accepted, because two have
    /// been written: a raw array of one limit per bin, and the
    /// run-length pairs written since.  Anything else is refused rather
    /// than guessed at — a curve read wrong produces confident wrong
    /// hours, which is worse than a refusal.
    /// </summary>
    public static IdleProfile Decode(JsonElement stored)
    {
        if (stored.ValueKind == JsonValueKind.Undefined || stored.ValueKind == JsonValueKind.Null)
        {
            return Flat(PaddingLimit);
        }

        if (stored.ValueKind != JsonValueKind.Array)
        {
            throw new IdlePolicyException("A profile must be an array.");
        }

        var items = stored.EnumerateArray().ToArray();

        // An empty curve is not an error: it is how the web writes "no
        // opinion", and it means the plain default limit.
        if (items.Length == 0)
        {
            return Flat(PaddingLimit);
        }

        var limits = new List<int>(Bins);

        if (items.All(item => item.ValueKind == JsonValueKind.Number))
        {
            limits.AddRange(items.Select(item => Clamp(item.GetDouble())));
        }
        else
        {
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Array)
                {
                    throw new IdlePolicyException("A profile must hold either limits or run-length pairs.");
                }

                var pair = item.EnumerateArray().ToArray();

                if (pair.Length != 2
                    || pair[0].ValueKind != JsonValueKind.Number
                    || pair[1].ValueKind != JsonValueKind.Number)
                {
                    throw new IdlePolicyException("A run-length pair must be a length and a limit.");
                }

                var length = pair[0].GetDouble();

                if (length <= 0 || length != Math.Floor(length))
                {
                    throw new IdlePolicyException("A run length must be a whole number above zero.");
                }

                var limit = Clamp(pair[1].GetDouble());

                for (var i = 0; i < (int)length && limits.Count < Bins; i++)
                {
                    limits.Add(limit);
                }
            }
        }

        // Guard against malformed storage: always yield a full day.
        while (limits.Count < Bins)
        {
            limits.Add(PaddingLimit);
        }

        return new IdleProfile(limits.Take(Bins).ToArray());
    }

    // The web rounds with JavaScript's Math.round, which is half-up.
    // .NET rounds to even by default, so a limit landing on a half
    // would differ by a minute — say it explicitly.  All limits are
    // positive, so away-from-zero and half-up agree.
    private static int Clamp(double value) =>
        Math.Clamp(
            (int)Math.Round(value, MidpointRounding.AwayFromZero),
            LimitMinimum,
            LimitMaximum);
}

/// <summary>A named curve and the weekdays it claims.</summary>
public sealed record NamedProfile(string Id, string Name, IReadOnlySet<DayOfWeek> Days, IdleProfile Profile);

/// <summary>
/// The user's whole idle policy: a default curve, a library of named
/// ones with the weekdays they claim, which of them are applied, and
/// any per-day overrides.
/// </summary>
public sealed class IdlePolicy(
    IdleProfile shared,
    IReadOnlyList<NamedProfile> library,
    IReadOnlyList<string> applied,
    IReadOnlyDictionary<DateOnly, IdleProfile> overrides)
{
    public IdleProfile Shared { get; } = shared;

    public IReadOnlyList<NamedProfile> Library { get; } = library;

    public IReadOnlyList<string> Applied { get; } = applied;

    public IReadOnlyDictionary<DateOnly, IdleProfile> Overrides { get; } = overrides;

    /// <summary>
    /// The curve a day runs on: its own override if it has one, else
    /// the last applied profile in library order that claims its
    /// weekday, else the default.  Library order decides who wins a day
    /// two profiles both claim, which is why this walks the whole list
    /// rather than stopping at the first match.
    /// </summary>
    public IdleProfile For(DateOnly date, DayOfWeek weekday)
    {
        if (Overrides.TryGetValue(date, out var dayOverride))
        {
            return dayOverride;
        }

        IdleProfile? winner = null;

        foreach (var entry in Library)
        {
            if (Applied.Contains(entry.Id) && entry.Days.Contains(weekday))
            {
                winner = entry.Profile;
            }
        }

        return winner ?? Shared;
    }

    public static IdlePolicy Decode(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new IdlePolicyException("The profiles document must be an object.");
        }

        var shared = IdleProfile.Decode(Property(document, "shared"));
        var library = DecodeLibrary(Property(document, "library"));
        var applied = DecodeApplied(Property(document, "applied"), library);
        var overrides = new Dictionary<DateOnly, IdleProfile>();

        var stored = Property(document, "overrides");

        if (stored.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in stored.EnumerateObject())
            {
                if (!DateOnly.TryParse(item.Name, out var date))
                {
                    throw new IdlePolicyException($"An override is keyed by \"{item.Name}\", which is not a date.");
                }

                overrides[date] = IdleProfile.Decode(item.Value);
            }
        }
        else if (stored.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new IdlePolicyException("Overrides must be an object.");
        }

        return new IdlePolicy(shared, library, applied, overrides);
    }

    private static JsonElement Property(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) ? value : default;

    private static List<NamedProfile> DecodeLibrary(JsonElement stored)
    {
        var library = new List<NamedProfile>();

        if (stored.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return library;
        }

        if (stored.ValueKind != JsonValueKind.Array)
        {
            throw new IdlePolicyException("The library must be an array.");
        }

        foreach (var entry in stored.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new IdlePolicyException("A named profile must be an object.");
            }

            if (!entry.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(id.GetString()))
            {
                throw new IdlePolicyException("A named profile must have an id.");
            }

            if (!entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                throw new IdlePolicyException("A named profile must have a name.");
            }

            if (!entry.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array)
            {
                throw new IdlePolicyException("A named profile must name its days.");
            }

            var weekdays = new HashSet<DayOfWeek>();

            foreach (var day in days.EnumerateArray())
            {
                var key = day.ValueKind == JsonValueKind.String ? day.GetString() : null;
                var index = Array.IndexOf(DayKeys, key);

                if (index < 0)
                {
                    throw new IdlePolicyException($"A named profile claims \"{key}\", which is not a weekday.");
                }

                weekdays.Add((DayOfWeek)index);
            }

            library.Add(new NamedProfile(
                id.GetString()!,
                name.GetString()!,
                weekdays,
                IdleProfile.Decode(Property(entry, "profile"))));
        }

        return library;
    }

    private static List<string> DecodeApplied(JsonElement stored, List<NamedProfile> library)
    {
        var applied = new List<string>();

        if (stored.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return applied;
        }

        if (stored.ValueKind != JsonValueKind.Array)
        {
            throw new IdlePolicyException("Applied profiles must be a list of ids.");
        }

        foreach (var item in stored.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new IdlePolicyException("Applied profiles must be a list of ids.");
            }

            var id = item.GetString()!;

            // The web drops ids no longer in the library rather than
            // failing on them; a profile that has been deleted is not a
            // malformed document.
            if (library.Any(entry => entry.Id == id))
            {
                applied.Add(id);
            }
        }

        return applied;
    }

    // Index matches DayOfWeek: 0 = Sunday.
    private static readonly string[] DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
}

/// <summary>
/// A stored document this build cannot read.  Always fatal: the whole
/// point of reading the policy is to agree with the web client, and a
/// guess would agree with nothing.
/// </summary>
public class IdlePolicyException(string message) : Exception(message);
