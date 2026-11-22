using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;
using Spectre.Console;

namespace Flextime.Daemon;

/// <summary>
/// The data command: the hours of the chosen computers as the web app's
/// day list shows them.  The server merges the computers, as it does for
/// the web, and each day is counted by <see cref="DayCount"/>, the same
/// arithmetic report reconciles with, so a day here is the day the user
/// read there, to the minute.  Nothing on the command line changes the
/// arithmetic: a number that could differ from the web's would need
/// explaining every time it was read.
/// </summary>
public class PrintData(
    IHttpClientFactory httpClientFactory,
    DeviceCode deviceCode,
    Computer computer,
    PolicyClient policyClient)
{
    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    /// <summary>What data prints after its generated help.  Every line fits in 80 columns.</summary>
    public const string HelpKey = """

        The computers chosen are merged into one day list, as the web app shows
        them: time on two computers at once counts once.  Each day applies its
        idle profile, leaves out time marked as not work, and counts time you
        added by hand.  Work, start and end match the web's day rows.

        For your raw measurements and settings, use export.
        """;

    public async Task<int> Invoke(
        DateOnly from,
        DateOnly to,
        string[] computers,
        bool allComputers,
        bool newestFirst,
        bool hideNotWork,
        bool json,
        CancellationToken cancellationToken)
    {
        if (!deviceCode.IsAuthenticated)
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");
            return 2;
        }

        try
        {
            var data = await Load(from, to, computers, allComputers, cancellationToken);

            if (data == null)
            {
                return 1;
            }

            if (hideNotWork)
            {
                data = HideNotWork(data);
            }

            if (json)
            {
                Console.WriteLine(ToJson(data));
            }
            else
            {
                // The en dash and the dot are not in every Windows code page.
                Console.OutputEncoding = Encoding.UTF8;

                var colour = !Console.IsOutputRedirected
                             && AnsiConsole.Profile.Capabilities.Ansi
                             && PrintInfo.ColourWanted(false, Environment.GetEnvironmentVariable("NO_COLOR"));

                var today = DateOnly.FromDateTime(DateTime.Now);

                foreach (var line in Format(data, allComputers, colour, from, to, today, newestFirst))
                {
                    Console.WriteLine(line);
                }
            }

            return 0;
        }
        catch (IdlePolicyException exception)
        {
            Console.Error.WriteLine(TerminalText.Clean(exception.Message));
            return 1;
        }
        catch (TokenRefreshException exception)
        {
            // Retrying will not help until the user logs in again.
            Console.Error.WriteLine(TerminalText.Clean(exception.Message));
            return 2;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            Console.Error.WriteLine($"Network error: {TerminalText.Clean(exception.Message)}");
            return 1;
        }
    }

    // Relaxed escaping, as for the status: the output goes to a terminal
    // or a script, never into HTML, and "+01:00" reads better unescaped.
    private static readonly DataSourceGenerationContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string ToJson(DataDataContract data) =>
        JsonSerializer.Serialize(data, JsonContext.DataDataContract);

    /// <summary>
    /// The computers and their days from <paramref name="from"/> to
    /// <paramref name="to"/>, oldest first, or null when a computer
    /// asked for is not on the account, which is said on standard error.
    /// </summary>
    public async Task<DataDataContract?> Load(
        DateOnly from,
        DateOnly to,
        string[] computers,
        bool allComputers,
        CancellationToken cancellationToken)
    {
        var profiles = await policyClient.GetProfiles(cancellationToken);
        var marks = (await policyClient.GetMarks(cancellationToken)).Value ?? Marks.Empty;
        var added = (await policyClient.GetAdded(cancellationToken)).Value ?? AddedTime.Empty;

        var known = (await httpClient.GetFromJsonAsync(
            "/computers?api-version=1.7",
            ComputerSourceGenerationContext.Default.ComputerNamesListDataContract,
            cancellationToken))?.Items ?? [];

        List<DataComputerDataContract> targets = [];

        if (allComputers)
        {
            targets.AddRange(known.Select(item => new DataComputerDataContract(item.Id, item.DisplayName)));
        }
        else if (computers.Length > 0)
        {
            foreach (var id in computers.Distinct())
            {
                var match = known.SingleOrDefault(item => item.Id == id);

                if (match == null)
                {
                    Console.Error.WriteLine(TerminalText.Clean($"Computer {id} not found on server. The computers command lists them."));
                    return null;
                }

                targets.Add(new DataComputerDataContract(match.Id, match.DisplayName));
            }
        }
        else
        {
            // A computer that has not synced yet is not on the server,
            // and has no days there either; it is still the one asked for.
            var here = known.SingleOrDefault(item => item.Id == computer.Id);

            targets.Add(new DataComputerDataContract(computer.Id!, here?.DisplayName ?? computer.Hostname));
        }

        if (targets.Count == 0)
        {
            return new DataDataContract([], []);
        }

        var zones = await httpClient.GetFromJsonAsync(
            IdsUri(targets.Select(item => item.Id), from, to),
            DataSourceGenerationContext.Default.DeltaZonesDataContract,
            cancellationToken);

        var days = Count(zones?.Items ?? [], profiles.Value, marks, added, from, to);

        return new DataDataContract(targets.ToArray(), days);
    }

    /// <summary>
    /// The request the web makes for these computers' days: the ids in
    /// one request, sorted, so the server merges them; API 1.9, which
    /// answers each date as one item even on a day measured in two
    /// zones, so the day owes its hours once, as in the web.  The window
    /// is the server's, from inclusive, before exclusive.
    /// </summary>
    public static string IdsUri(IEnumerable<string> ids, DateOnly from, DateOnly to) =>
        "/ids?api-version=1.9&"
        + string.Join("&", ids.Order(StringComparer.Ordinal).Select(id => $"id={Uri.EscapeDataString(id)}"))
        + $"&from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        + $"&before={to.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The web's day rows for the server's items, oldest first.  Each
    /// item is a row of its own, as in the web.  A date with time added
    /// by hand and nothing measured has no item, and so no row, as in
    /// the web.
    /// </summary>
    public static DataDayDataContract[] Count(
        IEnumerable<DeltaZoneDataContract> items,
        IdlePolicy? policy,
        Marks marks,
        AddedTime added,
        DateOnly from,
        DateOnly to)
    {
        var days = new List<CountedDay>();

        foreach (var item in items)
        {
            if (!TimeZones.TryGet(item.Zone, out var zone))
            {
                // A zone the server knows but this machine cannot
                // resolve, for example on Windows without ICU.
                Console.Error.WriteLine($"Skipping measurements in unknown time zone {TerminalText.Clean(item.Zone)}.");
                continue;
            }

            var day = DayCount.Day(item.Zone, DayCount.Read(Timestamps(item.Deltas ?? []), zone), policy, DayCount.DefaultIdle, marks, added);

            // The server's window is by the date stored, the range here
            // by the date the day is filed under; they agree, and this
            // holds the range should they not.
            if (day != null && day.Date >= from && day.Date <= to)
            {
                days.Add(day);
            }
        }

        return days
            .OrderBy(day => day.Start)
            .Select(day => new DataDayDataContract(day.Date, day.Zone, day.Start, day.End, day.Work, day.Custom, day.NotWork))
            .ToArray();
    }

    // From API 1.6 the timestamps come as deltas: the first is the
    // timestamp itself, every other the seconds since the one before.
    private static long[] Timestamps(long[] deltas)
    {
        var timestamps = new long[deltas.Length];
        var timestamp = 0L;

        for (var i = 0; i < deltas.Length; i++)
        {
            timestamp += deltas[i];
            timestamps[i] = timestamp;
        }

        return timestamps;
    }

    // Everything on a day's row ahead of its work: weekday, day, range.
    private const int HeadWidth = 27;

    // The work column ends here, and every total with it.
    private const int Width = HeadWidth + 5;

    /// <summary>
    /// The day list as text: the computers counted, then a month heading
    /// for each month, a week heading for each week part with its total
    /// after it, and the month's total after its days.  Weeks are split
    /// where they cross a month, as the web splits them, so that every
    /// total sums within one month.  A month the range only touches gets
    /// no total: under the month's name, the sum of a few of its days
    /// would read as the month's.  The month so far counts as whole.
    /// Newest first, it reads as the web app does: the month's total on
    /// its heading, then its weeks and days, latest first, each week's
    /// total under its days.  JSON keeps the days oldest first either
    /// way.
    /// </summary>
    public static IEnumerable<string> Format(
        DataDataContract data, bool allComputers, bool colour, DateOnly from, DateOnly to, DateOnly today,
        bool newestFirst = false)
    {
        yield return allComputers
            ? "All computers"
            : TerminalText.Clean(string.Join(", ", data.Computers.Select(item => item.Name)));

        if (data.Days.Length == 0)
        {
            yield return "No data.";
            yield break;
        }

        var days = newestFirst ? data.Days.Reverse() : data.Days;

        foreach (var month in days.GroupBy(day => (day.Date.Year, day.Date.Month)))
        {
            var first = month.First().Date;
            var start = new DateOnly(first.Year, first.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var whole = from <= start && to >= (today < end ? today : end);
            var heading = first.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

            yield return string.Empty;
            yield return newestFirst && whole
                ? heading + Hours(Sum(month)).PadLeft(Width - heading.Length)
                : heading;

            // Consecutive days of one ISO week; the days are in order,
            // so a week is never met twice within a month.
            foreach (var week in month.GroupBy(day => ISOWeek.GetWeekOfYear(day.Date.ToDateTime(TimeOnly.MinValue))))
            {
                yield return $"Week {week.Key}";

                foreach (var day in week)
                {
                    yield return Row(day, colour);
                }

                yield return Hours(Sum(week)).PadLeft(Width);
            }

            if (!newestFirst && whole)
            {
                var name = first.ToString("MMMM", CultureInfo.InvariantCulture);

                yield return name + Hours(Sum(month)).PadLeft(Width - name.Length);
            }
        }
    }

    private static string Row(DataDayDataContract day, bool colour)
    {
        // The dot for a day changed by hand sits in the indent before the
        // weekday, where the web puts it, so the columns stay put.
        var dot = day.Custom ? "• " : "  ";
        var label = $"{dot}{day.Date.ToString("ddd", CultureInfo.InvariantCulture)} {day.Date.Day,2}   ";

        if (day.NotWork)
        {
            // Muted, as the web mutes the row, where colour is wanted.
            const string notWork = "not work";

            return label.PadRight(HeadWidth) + (colour ? $"\e[2m{notWork}\e[0m" : notWork);
        }

        var range = $"{day.Start:HH:mm} – {day.End:HH:mm}";
        return $"{label}{range}".PadRight(HeadWidth) + $"{Hours(day.Work),5}";
    }

    // Days marked whole as not work left out, as the web's Hide not work
    // leaves them out: the JSON too, since asking to hide them asks for
    // the list without them.  A week of nothing else goes with them.
    public static DataDataContract HideNotWork(DataDataContract data) =>
        data with { Days = [.. data.Days.Where(day => !day.NotWork)] };

    // The sums run over the exact work and are rounded once, as the web
    // sums a week and a month, so a total can differ by a minute from
    // the rounded days added up by hand.
    private static TimeSpan Sum(IEnumerable<DataDayDataContract> days) =>
        days.Aggregate(TimeSpan.Zero, (sum, day) => sum + day.Work);

    // Hours and minutes, rounded to the nearest minute as the web rounds
    // them; a total can run past 99 hours.
    public static string Hours(TimeSpan value)
    {
        var minutes = (long)Math.Round(value.TotalSeconds / 60, MidpointRounding.AwayFromZero);

        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}

internal record ComputerDataContract(string Id, string? Name);
internal record ComputersDataContract(ComputerDataContract[] Items);

internal record ZoneDataContract(string Zone, long[] Timestamps);
internal record ZonesDataContract(ZoneDataContract[] Items);

/// <summary>An item of API 1.6 and later: a day's timestamps as deltas.</summary>
public record DeltaZoneDataContract(string Zone, long[]? Deltas);

public record DeltaZonesDataContract(DeltaZoneDataContract[] Items);

// The shape data --json writes, its own and not the flextime CLI's: a
// day here is the web's day row, merged over the computers, so it has
// no computer to belong to.  Dates, times and durations are written as
// in that contract.  Week and month totals are left out; they are sums
// of the days.

public record DataComputerDataContract(string Id, string Name);

public record DataDayDataContract(
    DateOnly Date,
    string Zone,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Work,
    bool Custom,
    bool NotWork);

public record DataDataContract(DataComputerDataContract[] Computers, DataDayDataContract[] Days);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ComputersDataContract))]
[JsonSerializable(typeof(ZonesDataContract))]
internal partial class PrintDataSourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeltaZonesDataContract))]
[JsonSerializable(typeof(DataDataContract))]
public partial class DataSourceGenerationContext : JsonSerializerContext;
