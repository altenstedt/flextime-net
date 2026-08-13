using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Flextime;

public static class Reader
{
    private static List<MeasurementWithZone> ReadFiles(string folder)
    {
        var list = new List<MeasurementWithZone>();

        IEnumerable<string> files;
        try {
            files = Directory.EnumerateFiles(folder, "*.bin");
        }
        catch (DirectoryNotFoundException) {
            return [];
        }

        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var measurements = Measurements.Parser.ParseFrom(stream);

            list.AddRange(measurements.Measurements_.Select(item => new MeasurementWithZone(item, measurements.Zone, measurements.Interval)));
        }

        list.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));

        return list;
    }

    private static SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)> GroupAndHash(List<MeasurementWithZone> list, TimeSpan since, DateTimeOffset now)
    {
        var byDates = list
            .GroupBy(item => DateOnly.FromDateTime(item.Timestamp.Date))
            // The cutoff date is computed in the zone the day was recorded
            // in, so that a day in a zone ahead of or behind this machine
            // is kept or dropped as a whole.
            .Where(group => since <= TimeSpan.Zero || group.Key > TimeZones.DateAt(now - since, group.First().Zone))
            .ToDictionary(item => item.Key, item => (item.ToList(), HashMeasurements(item)));

        return new SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)>(byDates);
    }

    public static async Task<SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)>> ReadRemote(HttpClient httpClient, TimeSpan since, string computerId, DateTimeOffset? now = null)
    {
        PagedMeasurementsDataContract? pagedMeasurements = null;

        try {
            pagedMeasurements = await httpClient.GetFromJsonAsync($"/{computerId}", PagedMeasurementsSourceGenerationContext.Default.PagedMeasurementsDataContract);
        } catch (HttpRequestException exception) {
            Console.WriteLine($"Error contacting backend: {exception.Message}.");

            if (exception.InnerException != null) {
                Console.WriteLine($"  {exception.InnerException.Message}");
            }
        }

        if (pagedMeasurements == null) {
            return [];
        }

        var measurements = pagedMeasurements.Items.SelectMany(item => item.Items.Select(x => new MeasurementWithZone(new Measurement { Idle = x.Idle, Kind = Measurement.Types.Kind.None, Timestamp = x.Timestamp}, item.Zone, item.Interval))).ToList();

        var byDates = GroupAndHash(measurements, since, now ?? DateTimeOffset.UtcNow);

        return byDates;
    }

    public static SortedDictionary<DateOnly, (List<MeasurementWithZone> list, long hash)> ReadFiles(string folder, TimeSpan since, DateTimeOffset? now = null)
    {
        var list = ReadFiles(folder);

        var byDates = GroupAndHash(list, since, now ?? DateTimeOffset.UtcNow);

        return byDates;
    }

    public static (List<MeasurementWithZone> list, bool found) ReadFiles(string folder, TimeSpan since, DateOnly date, long hash) {
        var byDates = ReadFiles(folder, since);

        if (!byDates.TryGetValue(date, out var byDate)) {
            return ([], false);
        }

        var measurements = byDate.list;

        for (var i = 0; i < measurements.Count; i++) {
            var tmp = HashMeasurements(measurements[..i]);

            if (tmp == hash) {
                return (measurements[i..], true);
            }
        }

        return ([], false);
    }

    public static async Task<string> ReadComputerId(string path) {
        return await File.ReadAllTextAsync(path);
    }

    private static long HashMeasurements(IEnumerable<MeasurementWithZone> measurements)
    {
        var measurementWithZones = measurements as MeasurementWithZone[] ?? measurements.ToArray();
        
        long hashCode = measurementWithZones.Length;
        
        foreach (long val in measurementWithZones.Select(item => item.Measurement.Timestamp))
        {
            hashCode = unchecked(hashCode * 31 + val);
        }

        return hashCode;
    }
}

public record MeasurementDataContract(
    uint Kind,
    uint Timestamp,
    uint Idle);

public record MeasurementsDataContract(
    string ComputerId,
    string Zone,
    uint Interval,
    MeasurementDataContract[] Items);

public record PagedMeasurementsDataContract(
    MeasurementsDataContract[] Items);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(PagedMeasurementsDataContract))]
internal partial class PagedMeasurementsSourceGenerationContext : JsonSerializerContext;
