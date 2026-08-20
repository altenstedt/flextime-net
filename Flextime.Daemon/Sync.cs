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
        ServerHasMore,
    }

    private readonly MeasurementsFormatter formatter = new(TimeSpan.FromMinutes(10), false, 0);

    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public async Task SyncAndPrint()
    {
        var inSync = 0;

        await ActOnRemoteStatus(upload: true, (line, status) =>
        {
            if (status == DayStatus.InSync)
            {
                // A recurring install runs this once a minute, and by
                // then almost every day is in sync.  Printing them all
                // every pass is what fills the sync log; the count below
                // says they were still checked.
                inSync++;
                return;
            }

            AnsiConsole.WriteLine(line);
        });

        AnsiConsole.WriteLine(inSync == 1 ? "1 day in sync." : $"{inSync} days in sync.");
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

                case DayStatus.ServerHasMore:
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
            $"/{computer.Id}/summary?api-version=1.3",
            SummarySourceGenerationContext.Default.SummaryDataContract);

        var localByDates = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);

        // (Date, Zone) is unique in the response — the server stores one
        // row per day and zone — so a dictionary probes each group in
        // constant time.
        var remoteByKey = remoteSummary?.Items.ToDictionary(item => (item.Date, item.Zone)) ?? new();

        foreach (var date in localByDates.TakeLast(limit))
        {
            var summary = formatter.SummarizeDay(date.Value.list.ToArray());

            // Compared per zone, mirroring the storage rows.  A matching
            // watermark — the same number of distinct timestamps, the
            // same last one, and the same sum — is taken to mean the
            // server holds the day.  Sets agreeing on all three can
            // still differ in principle, but only by trading multiple
            // timestamps that cancel out exactly.
            var stale = new List<IGrouping<string, MeasurementWithZone>>();
            var serverHasMore = false;
            var known = false;

            foreach (var byZone in date.Value.list.GroupBy(item => item.Zone))
            {
                var timestamps = byZone
                    .Select(item => item.Measurement.Timestamp)
                    .Distinct()
                    .Order()
                    .ToArray();

                var match = remoteByKey.GetValueOrDefault((date.Key, byZone.Key));

                known |= match != null;

                if (match != null && match.Count == timestamps.Length && match.Last == timestamps[^1]
                    && match.Sum == timestamps.Sum(item => (long)item))
                {
                    continue;
                }

                if (match != null && match.Count > timestamps.Length && match.Last >= timestamps[^1])
                {
                    // The server holds more than this machine: local
                    // files restored from an older backup, a reused
                    // computer id, or rows the old client uploaded with
                    // the whole day under one zone.  Uploading cannot
                    // reconcile that — the merge only adds — so say so
                    // instead of re-uploading forever.
                    serverHasMore = true;
                    continue;
                }

                stale.Add(byZone);
            }

            if (stale.Count == 0)
            {
                output(
                    serverHasMore
                        ? $"{summary} [server has more]"
                        : $"{summary} [in sync]",
                    serverHasMore ? DayStatus.ServerHasMore : DayStatus.InSync);
            }
            else if (upload)
            {
                // The whole day is sent; the server merge is idempotent,
                // so anything it already holds is deduplicated there.
                foreach (var byZone in stale)
                {
                    await Upload(byZone.Key, [.. byZone]);
                }

                output($"{summary} [synced]", DayStatus.Synced);
            }
            else if (known)
            {
                output($"{summary} [can sync]", DayStatus.CanSync);
            }
            else
            {
                output($"{summary} [local only]", DayStatus.LocalOnly);
            }
        }
    }

    private async Task Upload(string zone, List<MeasurementWithZone> measurements)
    {
        var payload = new MeasurementsDataContract(
            zone,
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

internal record SummaryItemDataContract(DateOnly Date, string Zone, int Count, long Last, long Sum);
internal record SummaryDataContract(SummaryItemDataContract[] Items);

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
