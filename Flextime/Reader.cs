using System.Net.Http.Json;
using ProtoBuf;

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
            var measurements = Serializer.Deserialize<Measurements>(stream);

            list.AddRange(measurements.Items.Select(item => new MeasurementWithZone(item, measurements.Zone, measurements.Interval)));
        }

        list.Sort((left, right) => left.Timestamp < right.Timestamp ? -1 : 1);

        return list;
    }

    private static Dictionary<DateOnly, (List<MeasurementWithZone> list, uint hash)> GroupAndHash(List<MeasurementWithZone> list, TimeSpan since)
    {
        var byDates = list
            .GroupBy(item => DateOnly.FromDateTime(item.Timestamp.Date))
            .Where(date => date.Key > DateOnly.FromDateTime(since > TimeSpan.Zero ? DateTime.Now - since : DateTime.MinValue))
            .ToDictionary(item => item.Key, item => (item.ToList(), HashMeasurements(item)));

        return byDates;
    }

    public static async Task<Dictionary<DateOnly, (List<MeasurementWithZone> list, uint hash)>> ReadRemote(HttpClient httpClient, TimeSpan since, string computerId)
    {
        PagedMeasurementsDataContract? pagedMeasurements = null;

        try {
            pagedMeasurements = await httpClient.GetFromJsonAsync<PagedMeasurementsDataContract>($"/{computerId}");
        } catch (HttpRequestException exception) {
            Console.WriteLine($"Error contacting backend: {exception.Message}.");

            if (exception.InnerException != null) {
                Console.WriteLine($"  {exception.InnerException.Message}");
            }
        }

        if (pagedMeasurements == null) {
            return [];
        }

        var measurements = pagedMeasurements.Items.SelectMany(item => item.Items.Select(x => new MeasurementWithZone(new Measurement { Idle = x.Idle, Kind = (MeasurementKind)x.Kind, Timestamp = x.Timestamp}, item.Zone, item.Interval))).ToList();


        var byDates = GroupAndHash(measurements, since);

        return byDates;
    }

    public static Dictionary<DateOnly, (List<MeasurementWithZone> list, uint hash)> ReadFiles(string folder, TimeSpan since)
    {
        var list = ReadFiles(folder);

        var byDates = GroupAndHash(list, since);

        return byDates;
    }

    public static (List<MeasurementWithZone> list, bool found) ReadFiles(string folder, TimeSpan since, DateOnly date, uint hash) {
        var byDates = ReadFiles(folder, since);

        if (!byDates.ContainsKey(date)) {
            return ([], false);
        }

        var measurements = byDates[date].list;

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

    private static uint HashMeasurements(IEnumerable<MeasurementWithZone> measurements)
    {
        uint hashCode = (uint)measurements.Count();
        foreach (uint val in measurements.Select(item => item.Measurement.Timestamp))
        {
            hashCode = unchecked(hashCode * 31 + val);
        }

        return hashCode;
    }
}