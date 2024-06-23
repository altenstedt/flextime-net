using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Flextime.Daemon;

public class Sync(IHttpClientFactory httpClientFactory, Computer computer)
{
    private readonly MeasurementsFormatter formatter = new(TimeSpan.FromMinutes(10), false, 0);

    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");
    
    public async Task SyncAndPrint()
    {
        await ActOnRemoteStatus(localOnly: LocalOnly, inSync: InSync, canSync: CanSync, cannotSync: CannotSync);

        return;

        void CannotSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [cannot sync]");
        }

        async Task CanSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair, List<MeasurementWithZone> mismatch)
        {
            var measurements = new MeasurementsDataContract(
                mismatch.First().Zone, 
                mismatch
                    .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                    .ToArray());

            await httpClient.PatchAsJsonAsync(
                $"/{computer.Id}", 
                measurements, 
                MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [synced]");
        }

        void InSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [in sync]");
        }

        async Task LocalOnly(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            var measurements = new MeasurementsDataContract(
                pair.Value.list.First().Zone, 
                pair.Value.list
                    .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                    .ToArray());

            await httpClient.PatchAsJsonAsync(
                $"/{computer.Id}", 
                measurements, 
                MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [synced]");
        }
    }

    public async Task SyncAndLog(ILogger logger)
    {
        await ActOnRemoteStatus(localOnly: LocalOnly, inSync: InSync, canSync: CanSync, cannotSync: CannotSync);
        
        return;

        void CannotSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            logger.LogWarning($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [cannot sync]");
        }

        async Task CanSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair, List<MeasurementWithZone> mismatch)
        {
            var measurements = new MeasurementsDataContract(
                mismatch.First().Zone,
                mismatch
                    .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                    .ToArray());

            await httpClient.PatchAsJsonAsync(
                $"/{computer.Id}", 
                measurements, 
                MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

            logger.LogInformation($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [synced]");
        }

        void InSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
        }

        async Task LocalOnly(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            var measurements = new MeasurementsDataContract(
                pair.Value.list.First().Zone,
                pair.Value.list
                    .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                    .ToArray());

            await httpClient.PatchAsJsonAsync(
                $"/{computer.Id}", 
                measurements, 
                MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

            logger.LogInformation($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [synced]");
        }
    }

    public async Task Print(int count)
    {
        await ActOnRemoteStatus(localOnly: LocalOnly, inSync: InSync, canSync: CanSync, cannotSync: CannotSync, limit: count);
        
        return;

        void CannotSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [cannot sync]");
        }

        Task CanSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair, List<MeasurementWithZone> _)
        {
            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [can sync]");

            return Task.CompletedTask;
        }

        void InSync(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [in sync]");
        }

        Task LocalOnly(KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)> pair)
        {
            AnsiConsole.WriteLine($"{formatter.SummarizeDay(pair.Value.list.ToArray())} [local only]");

            return Task.CompletedTask;
        }
    }
    
    private async Task ActOnRemoteStatus(
        Func<KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)>, Task> localOnly,
        Action<KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)>> inSync,
        Func<KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)>, List<MeasurementWithZone>, Task> canSync,
        Action<KeyValuePair<DateOnly, (List<MeasurementWithZone> list, long hash)>> cannotSync,
        int limit = int.MaxValue)
    {
        var remoteSummary = await httpClient.GetFromJsonAsync(
            $"/{computer.Id}/summary",
            SummarySourceGenerationContext.Default.SummaryDataContract);

        var localByDates = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);

        foreach (var date in localByDates.TakeLast(limit))
        {
            var match = remoteSummary?.Items.SingleOrDefault(item => item.Date == date.Key);

            if (match == null)
            {
                await localOnly(date);
            }
            else if (match.Hash == date.Value.hash)
            {
                inSync(date);
            }
            else
            {
                var mismatch = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue, date.Key, match.Hash);

                if (mismatch.found)
                {
                    await canSync(date, mismatch.list);
                }
                else
                {
                    cannotSync(date);
                }
            }
        }
    }
}

internal record DayDataContract(DateOnly Date, long Hash);
internal record SummaryDataContract(DayDataContract[] Items);

internal record MeasurementDataContract(int Kind, long Timestamp);
internal record MeasurementsDataContract(string Zone, MeasurementDataContract[] Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
internal partial class StringSourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SummaryDataContract))]
internal partial class SummarySourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MeasurementsDataContract))]
internal partial class MeasurementsSourceGenerationContext : JsonSerializerContext;
