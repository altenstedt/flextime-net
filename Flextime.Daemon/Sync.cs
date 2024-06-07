using System.Net.Http.Json;

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
        bool listRemote,
        TimeSpan idle,
        int blocksPerDay,
        bool verbose,
        bool syncWithRemote,
        Action<string, PrintDayKind> printDay,
        Action<string> printInformation,
        int limit = int.MaxValue)
    {
        if (!deviceCode.IsAuthenticated)
        {
            return;
        }
        
        await httpClient.PatchAsJsonAsync($"/{computer.Id}/name", computer.Name);
        
        var remoteSummary = await httpClient.GetFromJsonAsync<SummaryDataContract>($"/{computer.Id}/summary");

        var byDates = listRemote
            ? await Reader.ReadRemote(httpClient, TimeSpan.MinValue, computer.Id!)
            : Reader.ReadFiles(Constants.MeasurementsFolder, TimeSpan.MinValue);

        if (byDates.Count == 0)
        {
            printInformation("No data");
        }
        else
        {
            var formatter = new MeasurementsFormatter(idle, verbose, blocksPerDay);

            foreach (var date in byDates.TakeLast(limit))
            {
                var match = remoteSummary?.Items.SingleOrDefault(item => item.Date == date.Key);

                if (match == null)
                {
                    if (syncWithRemote)
                    {
                        var measurements = new Measurements
                        {
                            Interval = date.Value.list.First().Interval,
                            Zone = date.Value.list.First().Zone,
                            Items = date.Value.list.Select(item => item.Measurement).ToList()
                        };

                        await httpClient.PatchAsJsonAsync($"/{computer.Id}", measurements);

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
                            var measurements = new Measurements
                            {
                                Interval = mismatch.list.First().Interval,
                                Zone = mismatch.list.First().Zone,
                                Items = mismatch.list.Select(item => item.Measurement).ToList()
                            };

                            await httpClient.PatchAsJsonAsync($"/{computer.Id}", measurements);
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

    private record DayDataContract(DateOnly Date, long Hash);

    private record SummaryDataContract(DayDataContract[] Items);
}