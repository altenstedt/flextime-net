using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;

namespace Flextime.Daemon;

/// <summary>
/// Uploads place change points (api-version 1.5), after the activity.
/// Never in the way of the activity: every failure — an API without
/// 1.5, Key Vault unable to key a router, no network — is reported and
/// tried again next sync.
/// </summary>
public sealed class PlaceSync(HttpClient httpClient, string computerId, string folder, string markerPath, bool listenRunning)
{
    /// <summary>The most change points the API takes in one request.</summary>
    public const int MaxItems = 1000;

    /// <param name="placesClearedAt">When the server's place history was last cleared, from the summary.</param>
    /// <param name="remote">The summary items by (date, zone).</param>
    /// <param name="since">First day to compare; null compares every day.</param>
    /// <param name="note">Reports a line; true for a warning.</param>
    public async Task Run(
        long? placesClearedAt,
        IReadOnlyDictionary<(DateOnly Date, string Zone), SummaryItemDataContract> remote,
        DateOnly? since,
        Action<string, bool> note)
    {
        try
        {
            var marker = Location.ReadClear(markerPath);

            if (marker is { Pending: true })
            {
                // A clear the server has not confirmed comes first: until
                // it has, an upload could not be told from history.
                await LocationClient.Delete(httpClient, computerId, CancellationToken.None);

                LocationClient.ConfirmDelete(marker.At, markerPath);

                note("Place history removed from the server.", false);
            }

            var floor = marker?.At ?? 0u;

            if (placesClearedAt is { } cleared && cleared > floor)
            {
                // Cleared from another computer: clear here too, so this
                // one neither shows nor uploads what it held before.
                var at = (uint)Math.Min(cleared, uint.MaxValue);

                Location.WriteClear(new ClearMarker(at, Pending: false), markerPath);

                var rewritten = LocationClient.ClearLocal(at, folder, listenRunning);

                note($"Place history was cleared on {FormatTime(at)}; removed from {rewritten} local files.", false);

                floor = at;
            }

            var groups = PlaceFiles.Read(folder, From(since))
                .Where(item => item.Place.Timestamp >= floor)
                .GroupBy(item => (Date: DateOnly.FromDateTime(item.Timestamp.Date), item.Zone))
                .Where(group => !since.HasValue || group.Key.Date >= since.Value)
                .OrderBy(group => group.Key.Date)
                .ThenBy(group => group.Key.Zone, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                // The server keeps one router per timestamp, the first it
                // was given; so does this.
                var points = group
                    .DistinctBy(item => item.Place.Timestamp)
                    .OrderBy(item => item.Place.Timestamp)
                    .Select(item => item.Place)
                    .ToArray();

                var count = points.Length;
                var last = (long)points[^1].Timestamp;
                var sum = points.Sum(item => (long)item.Timestamp);

                var match = remote.GetValueOrDefault(group.Key);

                if (match != null && match.PlacesCount == count && match.PlacesLast == last && match.PlacesSum == sum)
                {
                    continue;
                }

                if (match != null && match.PlacesCount > count && match.PlacesLast >= last)
                {
                    // Uploading only adds; it cannot make the server hold
                    // less, as with activity.
                    continue;
                }

                foreach (var chunk in points.Chunk(MaxItems))
                {
                    var payload = new PlacesDataContract(
                        group.Key.Zone,
                        chunk.Select(item => new PlaceDataContract(
                                item.Timestamp,
                                item.Router == 0 ? null : Location.Hex(item.Router),
                                item.Flags,
                                // The API refuses the whole request over a
                                // bad hint; a file may hold anything.
                                item.Router != 0 && PlaceHints.IsValid(item.Hint) ? item.Hint : null))
                            .ToArray());

                    var response = await httpClient.PatchAsJsonAsync(
                        $"/{computerId}/places?api-version=1.5",
                        payload,
                        PlacesSourceGenerationContext.Default.PlacesDataContract);

                    response.EnsureSuccessStatusCode();
                }

                note(count == 1
                    ? $"{group.Key.Date:yyyy-MM-dd} {group.Key.Zone}: 1 place change [synced]"
                    : $"{group.Key.Date:yyyy-MM-dd} {group.Key.Zone}: {count} place changes [synced]", false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or JsonException)
        {
            note($"Places not synced, trying again next sync: {exception.Message}", true);
        }
    }

    /// <summary>
    /// The first file time worth reading for a window starting on
    /// <paramref name="since"/>: the day before it in UTC, which covers
    /// every zone's start of that day.  The file in effect then comes
    /// along too (<see cref="PlaceFiles.Files"/>).  Every file without
    /// a window.
    /// </summary>
    public static DateTime? From(DateOnly? since) =>
        since?.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static string FormatTime(uint seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <param name="Router">The client hash as 12 hex characters; null when no router was found.</param>
/// <param name="Hint">
/// The network's DNS domain, with the first change point for a router;
/// left out when there is none, so a request without hints is what it
/// was before them.  An API before hints ignores it: it binds with the
/// System.Text.Json defaults, which skip unknown members.
/// </param>
public record PlaceDataContract(
    long Timestamp,
    string? Router,
    uint Flags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null);

public record PlacesDataContract(string Zone, PlaceDataContract[] Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlacesDataContract))]
internal partial class PlacesSourceGenerationContext : JsonSerializerContext;
