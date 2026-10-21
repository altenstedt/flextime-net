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
    private static readonly string[] Documents = ["profiles", "marks", "places"];

    public async Task<int> Invoke(string? output, CancellationToken cancellationToken)
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
                VersionHelper.GetVersion() ?? "unknown");

            if (path == "-")
            {
                await using var stdout = Console.OpenStandardOutput();
                Write(export, stdout);
                Console.WriteLine();
            }
            else
            {
                // Only the user may read the file: it is all their data.
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                await using (var stream = new FileStream(path, options))
                {
                    Write(export, stream);
                }

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
    /// the ids answer does not say which computer a day came from.
    /// </summary>
    public static async Task<JsonObject> Build(Func<string, Task<string>> get, DateTimeOffset now, string version)
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
            // bytes on the wire and nothing for a reader.
            var days = Items(await get($"/ids?api-version=1.5&id={Uri.EscapeDataString(id)}"));

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
            ["about"] = About(),
            ["computers"] = computers,
            ["activity"] = activity,
            ["hints"] = hints,
            ["settings"] = settings,
        };
    }

    // A reader of the file should not need the source to understand it.
    private static JsonObject About() => new()
    {
        ["computers"] = "Each computer with its id, the name you gave it, the hostnames it reported, and its first and last day with data.",
        ["activity"] = "For each computer, one entry per day and time zone. Timestamps are Unix seconds, one for each minute the computer was in use.",
        ["places"] = "Only with location on. Each place is [start, router, flags]: when it took effect (Unix seconds), a hashed router id, and flags. Flag 1 means the internet went through a tunnel, 2 a VPN was connected, 4 location was turned off.",
        ["hints"] = "The network domain each router was last seen on.",
        ["settings"] = "Your idle profiles, the time you marked as not work, and your names for places, as the web app stores them.",
    };

    private static JsonArray Items(string body) =>
        JsonNode.Parse(body)?["items"]?.AsArray() is { } items
            ? (JsonArray)items.DeepClone()
            : [];

    private static void Write(JsonObject export, Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        export.WriteTo(writer);
    }
}
