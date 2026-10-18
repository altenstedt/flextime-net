using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;

namespace Flextime.Daemon;

/// <summary>
/// The computer commands: the computers of the account, their names and
/// the hostnames they reported, and renaming one.  A name is the user's
/// and lives on the server; the hostname is what a computer calls
/// itself, and sync reports it.
/// </summary>
public class ComputerClient(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, Computer computer)
{
    // A computer with no data for this long is dormant: the list puts it
    // last, as the web tucks it away.  Its data is all still there.
    public const int DormantDays = 90;

    private const int NameWidth = 20;
    private const int HostnameWidth = 18;

    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public async Task<int> Show()
    {
        Console.WriteLine($"Computer id     :  {computer.Id}");
        Console.WriteLine($"Hostname        :  {TerminalText.Clean(computer.Hostname)}");

        if (!deviceCode.IsAuthenticated)
        {
            Console.WriteLine("Name            :  Unknown, not logged in");

            return 0;
        }

        return await WithComputers(items =>
        {
            var here = items.SingleOrDefault(item => item.Id == computer.Id);

            foreach (var line in FormatShow(here))
            {
                Console.WriteLine(line);
            }

            return 0;
        });
    }

    public async Task<int> List(bool json)
    {
        if (!deviceCode.IsAuthenticated)
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");

            return 2;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);

        return await WithComputers(items =>
        {
            if (json)
            {
                // Id and name as before, name being the one each is shown
                // by, so scripts written against the older list still work.
                Console.WriteLine(JsonSerializer.Serialize(
                    new ComputerListDataContract(items
                        .Select(item => new ComputerListItemDataContract(
                            item.Id, item.DisplayName, item.Hostname, item.FirstDay, item.LastDay, IsDormant(item, today)))
                        .ToArray()),
                    ComputerSourceGenerationContext.Default.ComputerListDataContract));

                return 0;
            }

            foreach (var line in FormatList(items, computer.Id, today))
            {
                Console.WriteLine(line);
            }

            return 0;
        });
    }

    public async Task<int> Rename(string name, string? id)
    {
        if (!deviceCode.IsAuthenticated)
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");

            return 2;
        }

        var target = id ?? computer.Id!;
        var trimmed = name.Trim();

        // Checked here too, so a 400 from the server can only mean that
        // it is older than this client.
        if (!IsValidName(trimmed))
        {
            Console.Error.WriteLine("Not a name: at most 256 characters, all of them printable.");

            return 1;
        }

        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"/{Uri.EscapeDataString(target)}/name?api-version=1.7",
                trimmed,
                ComputerSourceGenerationContext.Default.String);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent or HttpStatusCode.OK:
                    Console.WriteLine(trimmed.Length == 0
                        ? "Name cleared; the computer is shown by its hostname."
                        : TerminalText.Clean($"Renamed to {trimmed}."));

                    return 0;

                case HttpStatusCode.Conflict:
                    Console.Error.WriteLine(TerminalText.Clean($"Another computer is already called \"{trimmed}\"."));

                    return 1;

                case HttpStatusCode.NotFound:
                    Console.Error.WriteLine(TerminalText.Clean($"No computer {target} on this account. The computers command lists them."));

                    return 1;

                case HttpStatusCode.BadRequest:
                    Console.Error.WriteLine(OlderServer);

                    return 1;

                default:
                    Console.Error.WriteLine($"Not renamed: the server answered {(int)response.StatusCode}.");

                    return 1;
            }
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
    }

    private async Task<int> WithComputers(Func<ComputerNamesDataContract[], int> act)
    {
        try
        {
            var known = await httpClient.GetFromJsonAsync(
                "/computers?api-version=1.7",
                ComputerSourceGenerationContext.Default.ComputerNamesListDataContract);

            return act(known?.Items ?? []);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            Console.Error.WriteLine(OlderServer);

            return 1;
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
    }

    private const string OlderServer = "The server is older than this flextimed: it has no api-version 1.7 yet.";

    // What the server takes as a name, empty being none.
    public static bool IsValidName(string name) => name.Length <= 256 && !name.Any(char.IsControl);

    // A computer without data has nothing to show either.
    public static bool IsDormant(ComputerNamesDataContract item, DateOnly today) =>
        item.LastDay is not { } last || last < today.AddDays(-DormantDays);

    public static IEnumerable<string> FormatShow(ComputerNamesDataContract? here)
    {
        if (here == null)
        {
            // Sync has not uploaded anything from here yet.
            yield return "Name            :  None yet; the server learns of it at the next sync";

            yield break;
        }

        yield return here.Name != null
            ? TerminalText.Clean($"Name            :  {here.Name}")
            : "Name            :  None given; shown by its hostname";

        var former = Former(here).ToArray();

        if (former.Length > 0)
        {
            yield return TerminalText.Clean($"Formerly        :  {string.Join(", ", former)}");
        }

        yield return $"Data            :  {Span(here, "yyyy-MM-dd")}";
    }

    // Earlier names and hostnames, newest first, each once, without the
    // ones it goes by now.
    public static IEnumerable<string> Former(ComputerNamesDataContract item)
    {
        var names = item.Names.Reverse().Select(change => change.Name).OfType<string>();
        var hostnames = item.Hostnames.Reverse().Select(seen => seen.Hostname);

        return names
            .Concat(hostnames)
            .Where(name => name != item.Name && name != item.Hostname)
            .Distinct();
    }

    // One line per computer, id first: the ids are what report's
    // --machines takes.  The hostname is left out where the computer is
    // shown by it anyway.  Dormant computers come last, under their own
    // heading.  Every line fits 80 columns.
    public static IEnumerable<string> FormatList(ComputerNamesDataContract[] items, string? hereId, DateOnly today)
    {
        if (items.Length == 0)
        {
            yield return "No computers.";

            yield break;
        }

        var active = items.Where(item => !IsDormant(item, today)).ToArray();
        var dormant = items.Where(item => IsDormant(item, today)).ToArray();

        yield return $"{"Id",-16}  {"Name",-NameWidth}  {"Hostname",-HostnameWidth}  Data";

        foreach (var item in active)
        {
            yield return Row(item, hereId);
        }

        if (dormant.Length > 0)
        {
            yield return string.Empty;
            yield return $"Not used in {DormantDays} days:";

            foreach (var item in dormant)
            {
                yield return Row(item, hereId);
            }
        }
    }

    private static string Row(ComputerNamesDataContract item, string? hereId)
    {
        var hostname = item.Hostname == item.DisplayName ? string.Empty : item.Hostname ?? string.Empty;

        // The computer running this is worth pointing out: it is the
        // one whose id is hardest to look up elsewhere.
        var here = item.Id == hereId ? " *" : string.Empty;

        var line = $"{Fit(item.Id, 16),-16}  {Fit(item.DisplayName, NameWidth),-NameWidth}  {Fit(hostname, HostnameWidth),-HostnameWidth}  {Span(item, "yyyy-MM")}";

        return TerminalText.Clean(line.TrimEnd() + here);
    }

    private static string Span(ComputerNamesDataContract item, string format) =>
        item is { FirstDay: { } first, LastDay: { } last }
            ? $"{first.ToString(format)} – {last.ToString(format)}"
            : "None";

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : string.Concat(text.AsSpan(0, width - 1), "…");
}

public record NameChangeDataContract(string? Name, DateTimeOffset At);

public record HostnameSeenDataContract(string Hostname, DateOnly First, DateOnly Last);

// A computer as the 1.7 list answers it: the user's name, null until
// given, the hostname it last reported, and the name it is shown by.
public record ComputerNamesDataContract(
    string Id,
    string? Name,
    string? Hostname,
    string DisplayName,
    NameChangeDataContract[] Names,
    HostnameSeenDataContract[] Hostnames,
    DateOnly? FirstDay,
    DateOnly? LastDay);

public record ComputerNamesListDataContract(ComputerNamesDataContract[] Items);

internal record ComputerListItemDataContract(
    string Id,
    string Name,
    string? Hostname,
    DateOnly? FirstDay,
    DateOnly? LastDay,
    bool Dormant);

internal record ComputerListDataContract(ComputerListItemDataContract[] Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ComputerNamesListDataContract))]
[JsonSerializable(typeof(ComputerListDataContract))]
internal partial class ComputerSourceGenerationContext : JsonSerializerContext;
