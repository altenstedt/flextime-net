using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Flextime.Daemon;

public static class Sync
{
    public enum PrintDayKind
    {
        Synced,
        LocalOnly,
        InSync,
        CanSync,
        CannotSync
    }
    
    public static async Task Invoke(
        HttpClient httpClient,
        DeviceCode deviceCode,
        Computer computer,
        TimeSpan idle,
        int blocksPerDay,
        bool syncWithRemote,
        Action<string, PrintDayKind> printDay,
        Action<string> printInformation,
        int limit = int.MaxValue)
    {
        if (!deviceCode.IsAuthenticated)
        {
            return;
        }

        if (!string.IsNullOrEmpty(computer.Name))
        {
            await httpClient.PatchAsJsonAsync($"/{computer.Id}/name", computer.Name, StringSourceGenerationContext.Default.String);
        }
        
        var remoteSummary = await httpClient.GetFromJsonAsync($"/{computer.Id}/summary", SummarySourceGenerationContext.Default.SummaryDataContract);

        var byDates = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);

        if (byDates.Count == 0)
        {
            printInformation("No data");
        }
        else
        {
            var formatter = new MeasurementsFormatter(idle, false, blocksPerDay);

            foreach (var date in byDates.TakeLast(limit))
            {
                var match = remoteSummary?.Items.SingleOrDefault(item => item.Date == date.Key);

                if (match == null)
                {
                    if (syncWithRemote)
                    {
                        var measurements = new MeasurementsDataContract(
                            date.Value.list.First().Zone,
                            date.Value.list
                                .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                                .ToArray());

                        await httpClient.PatchAsJsonAsync($"/{computer.Id}", measurements, MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

                        printDay($"{formatter.SummarizeDay(date.Value.list.ToArray())} [synced]", PrintDayKind.Synced);
                    }
                    else
                    {
                        printDay($"{formatter.SummarizeDay(date.Value.list.ToArray())} [local only]", PrintDayKind.LocalOnly);
                    }
                }
                else if (match.Hash == date.Value.hash)
                {
                    printDay($"{formatter.SummarizeDay(date.Value.list.ToArray())} [in sync]", PrintDayKind.InSync);
                }
                else
                {
                    var mismatch = Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue, date.Key,
                        match.Hash);

                    if (mismatch.found)
                    {
                        if (syncWithRemote)
                        {
                            var measurements = new MeasurementsDataContract(
                                mismatch.list.First().Zone,
                                mismatch.list
                                    .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                                    .ToArray());
                            
                            await httpClient.PatchAsJsonAsync($"/{computer.Id}", measurements, MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);
                            printDay($"{formatter.SummarizeDay(date.Value.list.ToArray())} [synced]", PrintDayKind.Synced);

                        }
                        else
                        {
                            printDay($"{formatter.SummarizeDay(date.Value.list.ToArray())} [can sync]", PrintDayKind.CanSync);
                        }
                    }
                    else
                    {
                        printDay($"{formatter.SummarizeDay(date.Value.list.ToArray())} [cannot sync]", PrintDayKind.CannotSync);
                    }
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
