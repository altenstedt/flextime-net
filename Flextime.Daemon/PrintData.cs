using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;

namespace Flextime.Daemon;

public class PrintData(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, Computer computer)
{
    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public async Task<int> Invoke(int days, string[] computers, bool allComputers, int idle, bool timestamps, bool json)
    {
        var formatter = new MeasurementsFormatter(TimeSpan.FromMinutes(idle), false, 0);

        if (!deviceCode.IsAuthenticated)
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");
            return 2;
        }

        try
        {
            var known = await httpClient.GetFromJsonAsync(
                "/computers?api-version=1.1",
                PrintDataSourceGenerationContext.Default.ComputersDataContract);

            List<ComputerDataContract> targets = [];

            if (allComputers)
            {
                targets.AddRange(known?.Items ?? []);
            }
            else if (computers.Length > 0)
            {
                foreach (var id in computers)
                {
                    var match = known?.Items.SingleOrDefault(item => item.Id == id);

                    if (match == null)
                    {
                        Console.Error.WriteLine($"Computer {id} not found on server.");
                        return 1;
                    }

                    targets.Add(match);
                }
            }
            else
            {
                targets.Add(
                    known?.Items.SingleOrDefault(item => item.Id == computer.Id)
                    ?? new ComputerDataContract(computer.Id!, computer.Name));
            }

            var result = new List<ComputerActivityDataContract>();

            foreach (var target in targets)
            {
                // One request per computer: the /ids response does not echo the
                // computer id back, so batched requests can only be correlated
                // by position. The server returns one zone group per day, so
                // days are regrouped here with each timestamp interpreted in
                // the zone its group reports.
                var zones = await httpClient.GetFromJsonAsync(
                    $"/ids?api-version=1.2&id={target.Id}",
                    PrintDataSourceGenerationContext.Default.ZonesDataContract);

                var items = new List<ZoneDataContract>();

                foreach (var item in zones?.Items ?? [])
                {
                    if (TimeZones.TryGet(item.Zone, out _))
                    {
                        items.Add(item);
                    }
                    else
                    {
                        // A zone the server knows but this machine cannot
                        // resolve, for example on Windows without ICU.
                        Console.Error.WriteLine($"Skipping measurements in unknown time zone {item.Zone}.");
                    }
                }

                var byDate = items
                    .SelectMany(item => item.Timestamps.Select(timestamp => (
                        Timestamp: TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp), TimeZones.Get(item.Zone)),
                        item.Zone)))
                    .GroupBy(item => DateOnly.FromDateTime(item.Timestamp.Date))
                    .OrderBy(group => group.Key);

                var dayActivities = new List<DayActivityDataContract>();

                foreach (var group in byDate)
                {
                    var ordered = group.OrderBy(item => item.Timestamp).ToArray();

                    // Days back count from today in the zone the day was
                    // recorded in, so that today on a computer ahead of or
                    // behind this machine is not cut off.
                    if (group.Key <= TimeZones.DateAt(DateTimeOffset.UtcNow, ordered[0].Zone).AddDays(-days))
                    {
                        continue;
                    }

                    var day = formatter.ComputeDay(ordered.Select(item => item.Timestamp).ToArray());

                    if (day == null)
                    {
                        continue;
                    }

                    dayActivities.Add(new DayActivityDataContract(
                        day.Date,
                        ordered[0].Zone,
                        day.Start,
                        day.End,
                        day.Span,
                        day.Work,
                        day.Measurements,
                        timestamps ? ordered.Select(item => item.Timestamp.ToUnixTimeSeconds()).ToArray() : null));
                }

                result.Add(new ComputerActivityDataContract(target.Id, target.Name, dayActivities.ToArray()));
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new ActivityDataContract(result.ToArray()),
                    ActivitySourceGenerationContext.Default.ActivityDataContract));
            }
            else
            {
                Print(result, formatter);
            }

            return 0;
        }
        catch (TokenRefreshException exception)
        {
            // Retrying will not help until the user logs in again.
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            Console.Error.WriteLine($"Network error: {exception.Message}");
            return 1;
        }
    }

    private static void Print(List<ComputerActivityDataContract> result, MeasurementsFormatter formatter)
    {
        var firstComputer = true;

        foreach (var item in result)
        {
            if (!firstComputer)
            {
                Console.WriteLine();
            }

            firstComputer = false;

            Console.WriteLine(string.IsNullOrEmpty(item.Name) ? item.Id : $"{item.Name} ({item.Id})");

            if (item.Days.Length == 0)
            {
                Console.WriteLine("No data.");
                continue;
            }

            foreach (var day in item.Days)
            {
                Console.WriteLine(formatter.FormatDay(
                    new DaySummary(day.Date, day.Start, day.End, day.Span, day.Work, day.Measurements)));
            }
        }
    }
}

internal record ComputerDataContract(string Id, string? Name);
internal record ComputersDataContract(ComputerDataContract[] Items);

internal record ZoneDataContract(string Zone, long[] Timestamps);
internal record ZonesDataContract(ZoneDataContract[] Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ComputersDataContract))]
[JsonSerializable(typeof(ZonesDataContract))]
internal partial class PrintDataSourceGenerationContext : JsonSerializerContext;
