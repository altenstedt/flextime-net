using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polly.Timeout;

namespace Flextime.Daemon;

/// <summary>
/// Writes everything the server holds for the user to one JSON file:
/// computers with their name and hostname histories, every day of
/// activity with the places in effect, the network hints, and the
/// settings documents.  The About page promises a copy of all your
/// data, and this is it.  The server's answers are copied as they come,
/// so a field the server adds later is exported without a new client.
/// </summary>
public class Export(IHttpClientFactory httpClientFactory, DeviceCode deviceCode)
{
    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    // The documents the server's store accepts.
    private static readonly string[] Documents = ["profiles", "marks", "added", "places"];

    /// <summary>What export prints after its generated help.  Every line fits in 80 columns.</summary>
    public const string HelpKey = """

        Without a range it saves all of it: every computer, every timestamp, and
        your settings.  A range limits the activity to those days.  Settings are
        always saved whole, since they do not belong to single days.
        """;

    public async Task<int> Invoke(string? output, (DateOnly From, DateOnly To)? range, CancellationToken cancellationToken)
    {
        if (!deviceCode.IsAuthenticated)
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");
            return 2;
        }

        var path = output ?? $"flextime-export-{DateTime.Now:yyyy-MM-dd}.json";

        try
        {
            var export = await Build(
                async uri => await httpClient.GetStringAsync(uri, cancellationToken),
                DateTimeOffset.UtcNow,
                VersionHelper.GetVersion() ?? "unknown",
                range);

            if (path == "-")
            {
                await using var stdout = Console.OpenStandardOutput();
                Write(export, stdout);
                Console.WriteLine();
            }
            else
            {
                Save(export, path);

                var computers = export["computers"]!.AsArray().Count;
                var days = export["activity"]!.AsArray().Sum(item => item!["days"]!.AsArray().Count);

                Console.Error.WriteLine($"Exported {computers} computers and {days} days to {Path.GetFullPath(path)}");
            }

            return 0;
        }
        catch (TokenRefreshException exception)
        {
            Console.Error.WriteLine(TerminalText.Clean(exception.Message));
            return 2;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            Console.Error.WriteLine($"Network error: {TerminalText.Clean(exception.Message)}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot write {TerminalText.Clean(path)}: {TerminalText.Clean(exception.Message)}");
            return 1;
        }
    }

    /// <summary>
    /// The export, from <paramref name="get"/>, which answers the body of
    /// a GET on the API.  Activity is read one computer at a time, since
    /// the ids answer does not say which computer a day came from.  With
    /// a <paramref name="range"/>, only the days in it, from and to
    /// inclusive.
    /// </summary>
    public static async Task<JsonObject> Build(
        Func<string, Task<string>> get,
        DateTimeOffset now,
        string version,
        (DateOnly From, DateOnly To)? range = null)
    {
        var computers = Items(await get("/computers?api-version=1.7"));

        var ids = computers
            .Select(item => item?["id"]?.GetValue<string>())
            .OfType<string>()
            .ToArray();

        var activity = new JsonArray();

        foreach (var id in ids)
        {
            // 1.5 answers whole timestamps; 1.6 answers deltas, which save
            // bytes on the wire and nothing for a reader.  Only 1.8 takes
            // a window of days, so a range is read there, and its deltas
            // turned back into the timestamps 1.5 gives, so the file has
            // one shape with a range or without.
            var days = range is { } window
                ? Undelta(Items(await get(
                    $"/ids?api-version=1.8&id={Uri.EscapeDataString(id)}"
                    + $"&from={window.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                    + $"&before={window.To.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}")))
                : Items(await get($"/ids?api-version=1.5&id={Uri.EscapeDataString(id)}"));

            // As a JsonNode: the generic Add is not safe to trim.
            activity.Add((JsonNode)new JsonObject { ["computer"] = id, ["days"] = days });
        }

        var hints = ids.Length == 0
            ? []
            : Items(await get("/places/hints?api-version=1.5&" + string.Join("&", ids.Select(id => $"id={Uri.EscapeDataString(id)}"))));

        var settings = new JsonObject();

        foreach (var name in Documents)
        {
            // A document never written is the JSON null.
            settings[name] = JsonNode.Parse(await get($"/store/{name}?api-version=1.0"));
        }

        return new JsonObject
        {
            ["exported"] = now.ToString("O"),
            ["client"] = $"flextimed {version}",
            ["about"] = About(range),
            ["computers"] = computers,
            ["activity"] = activity,
            ["hints"] = hints,
            ["settings"] = settings,
        };
    }

    // A reader of the file should not need the source to understand it,
    // nor to know that a file with a range is not all of their data.
    private static JsonObject About((DateOnly From, DateOnly To)? range) => new()
    {
        ["computers"] = "Each computer with its id, the name you gave it, the hostnames it reported, and its first and last day with data.",
        ["activity"] = range is { } window
            ? "For each computer, one entry per day and time zone, "
              + $"only for the days from {window.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {window.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: "
              + "this file holds part of your activity, not all of it. Timestamps are Unix seconds, one for each minute the computer was in use."
            : "For each computer, one entry per day and time zone. Timestamps are Unix seconds, one for each minute the computer was in use.",
        ["places"] = "Only with location on. Each place is [start, router, flags]: when it took effect (Unix seconds), a hashed router id, and flags. Flag 1 means the internet went through a tunnel, 2 a VPN was connected, 4 location was turned off, 8 the network was metered, like a phone hotspot.",
        ["hints"] = "The network domain each router was last seen on.",
        ["settings"] = "Your idle profiles, the time you marked as not work, the time you added to days by hand, and your names for places, as the web app stores them.",
    };

    // Each item as 1.5 writes it: zone, timestamps, places.  A running
    // sum of the deltas gives the timestamps back.
    private static JsonArray Undelta(JsonArray items)
    {
        var result = new JsonArray();

        foreach (var item in items)
        {
            var timestamps = new JsonArray();
            var timestamp = 0L;

            foreach (var delta in item?["deltas"]?.AsArray() ?? [])
            {
                timestamp += delta!.GetValue<long>();
                timestamps.Add((JsonNode)timestamp);
            }

            var rebuilt = new JsonObject
            {
                ["zone"] = item?["zone"]?.DeepClone(),
                ["timestamps"] = timestamps,
            };

            // Anything else the server sends, places among them, as it
            // comes.
            foreach (var (name, value) in item?.AsObject() ?? [])
            {
                if (name is not ("zone" or "deltas"))
                {
                    rebuilt[name] = value?.DeepClone();
                }
            }

            result.Add((JsonNode)rebuilt);
        }

        return result;
    }

    private static JsonArray Items(string body) =>
        JsonNode.Parse(body)?["items"]?.AsArray() is { } items
            ? (JsonArray)items.DeepClone()
            : [];

    /// <summary>
    /// Writes the export to <paramref name="path"/>, readable by the user
    /// only: it is all their data.  Through a new file renamed over the
    /// path, so a symbolic link planted there is replaced, not written
    /// through, and an existing file's looser mode is not kept.
    /// </summary>
    public static void Save(JsonObject export, string path) =>
        PrivateFile.Replace(path, stream => Write(export, stream));

    private static void Write(JsonObject export, Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        export.WriteTo(writer);
    }
}
