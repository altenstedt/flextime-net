using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Flextime.Daemon;

public class Sync(IHttpClientFactory httpClientFactory, Computer computer)
{
    public enum DayStatus
    {
        Synced,
        LocalOnly,
        CanSync,
        InSync,
        CannotSync,
    }

    private readonly MeasurementsFormatter formatter = new(TimeSpan.FromMinutes(10), false, 0);

    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public Task SyncAndPrint()
    {
        return ActOnRemoteStatus(upload: true, (line, _) => AnsiConsole.WriteLine(line));
    }

    public Task SyncAndLog(ILogger logger)
    {
        return ActOnRemoteStatus(upload: true, (line, status) =>
        {
            switch (status)
            {
                case DayStatus.InSync:
                    // Logging every day as in sync on every pass would be noise.
                    break;

                case DayStatus.CannotSync:
                    logger.LogWarning(line);
                    break;

                default:
                    logger.LogInformation(line);
                    break;
            }
        });
    }

    public Task Print(int count)
    {
        return ActOnRemoteStatus(upload: false, (line, _) => AnsiConsole.WriteLine(line), limit: count);
    }

    private async Task ActOnRemoteStatus(bool upload, Action<string, DayStatus> output, int limit = int.MaxValue)
    {
        var remoteSummary = await httpClient.GetFromJsonAsync(
            $"/{computer.Id}/summary",
            SummarySourceGenerationContext.Default.SummaryDataContract);

        var localByDates = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);

        foreach (var date in localByDates.TakeLast(limit))
        {
            var summary = formatter.SummarizeDay(date.Value.list.ToArray());
            var match = remoteSummary?.Items.SingleOrDefault(item => item.Date == date.Key);

            if (match == null)
            {
                if (upload)
                {
                    await Upload(date.Value.list);
                    output($"{summary} [synced]", DayStatus.Synced);
                }
                else
                {
                    output($"{summary} [local only]", DayStatus.LocalOnly);
                }
            }
            else if (match.Hash == date.Value.hash)
            {
                output($"{summary} [in sync]", DayStatus.InSync);
            }
            else
            {
                var mismatch = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue, date.Key, match.Hash);

                if (!mismatch.found)
                {
                    output($"{summary} [cannot sync]", DayStatus.CannotSync);
                }
                else if (upload)
                {
                    await Upload(mismatch.list);
                    output($"{summary} [synced]", DayStatus.Synced);
                }
                else
                {
                    output($"{summary} [can sync]", DayStatus.CanSync);
                }
            }
        }
    }

    private async Task Upload(List<MeasurementWithZone> measurements)
    {
        var payload = new MeasurementsDataContract(
            measurements.First().Zone,
            measurements
                .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                .ToArray());

        var response = await httpClient.PatchAsJsonAsync(
            $"/{computer.Id}",
            payload,
            MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

        response.EnsureSuccessStatusCode();
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
