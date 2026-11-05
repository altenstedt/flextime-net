using System.Text.Json;

namespace Flextime.Daemon;

/// <summary>
/// The name the user gave the place in the web app.  Names are kept in
/// the places document under the server's router id, which only the
/// server can make from the hash flextimed sends, so the id is read back
/// from this computer's place data on the server first.
/// </summary>
public static class PlaceName
{
    /// <summary>
    /// The server's router id in effect at the latest timestamp it has
    /// for this computer, from an /ids 1.8 answer.  Null when the server
    /// has nothing at or after <paramref name="start"/>, the time the
    /// current place took effect, since its latest place is then an
    /// older one.  An empty string is a stretch without a router.
    /// </summary>
    public static string? RouterAt(JsonElement ids, uint start)
    {
        var latest = 0L;
        string? router = null;

        if (ids.ValueKind != JsonValueKind.Object
            || !ids.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("deltas", out var deltas)
                || deltas.ValueKind != JsonValueKind.Array
                || deltas.EnumerateArray().Any(delta => delta.ValueKind != JsonValueKind.Number))
            {
                continue;
            }

            // A running sum of the deltas gives the timestamps back.
            var last = 0L;
            var count = 0;

            foreach (var delta in deltas.EnumerateArray())
            {
                last += delta.GetInt64();
                count++;
            }

            if (count == 0 || last <= latest)
            {
                continue;
            }

            if (!item.TryGetProperty("places", out var places) || places.ValueKind != JsonValueKind.Array || places.GetArrayLength() == 0)
            {
                continue;
            }

            // Runs are [startIndex, router, flags], each lasting until
            // the next, so the last one is in effect at the end.
            var run = places[places.GetArrayLength() - 1];

            latest = last;
            router = run.ValueKind == JsonValueKind.Array && run.GetArrayLength() > 1 && run[1].ValueKind == JsonValueKind.String
                ? run[1].GetString()
                : string.Empty;
        }

        return latest >= start ? router : null;
    }

    /// <summary>
    /// When the stay at <paramref name="current"/> began: the first of the
    /// change points at the end that all have this router.  A VPN going
    /// on or off starts a change point of its own, with new flags and the
    /// same router, and must not start a new stay.  Null when the latest
    /// point is another router, which listen has not caught up with yet.
    /// </summary>
    public static uint? StayStart(IReadOnlyList<Place> points, ulong current)
    {
        uint? start = null;

        for (var i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].Router != current || (points[i].Flags & PlaceFlags.LocationOff) != 0)
            {
                break;
            }

            start = points[i].Timestamp;
        }

        return start;
    }

    /// <summary>What the places document says about one router, as the Name line shows it.</summary>
    public static string Describe(JsonElement document, string router)
    {
        if (document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty("places", out var places)
            && places.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in places.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("router", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() != router)
                {
                    continue;
                }

                if (entry.TryGetProperty("notAPlace", out var notAPlace) && notAPlace.ValueKind == JsonValueKind.True)
                {
                    return "Not a place";
                }

                if (entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    return TerminalText.Clean(name.GetString()!.Trim());
                }

                break;
            }
        }

        return "Not named yet";
    }
}
