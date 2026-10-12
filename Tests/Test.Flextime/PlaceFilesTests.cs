using Flextime;
using Google.Protobuf;

namespace Test.Flextime;

public class PlaceFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-place-files-tests-{Guid.NewGuid():N}");

    public PlaceFilesTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private string Write(string name, string zone, uint[] measurements, params (uint timestamp, ulong router, uint flags)[] places)
    {
        var file = new Measurements { Interval = 60, Zone = zone };

        file.Measurements_.AddRange(measurements.Select(timestamp =>
            new Measurement { Timestamp = timestamp, Kind = Measurement.Types.Kind.Measurement }));
        file.Places.AddRange(places.Select(place =>
            new Place { Timestamp = place.timestamp, Router = place.router, Flags = place.flags }));

        var path = Path.Combine(folder, name);

        using var stream = File.Create(path);

        file.WriteTo(stream);

        return path;
    }

    private static Measurements Parse(string path)
    {
        using var stream = File.OpenRead(path);

        return Measurements.Parser.ParseFrom(stream);
    }

    [Fact]
    public void Read_lists_each_point_once_sorted_with_its_zone()
    {
        Write("2026-10-12T080000.bin", "Europe/Stockholm", [1000, 1060], (900, 1, 0), (1030, 2, 0));
        Write("2026-10-12T090000.bin", "Europe/Stockholm", [4600], (1030, 2, 0), (4630, 0, 0));
        Write("2026-10-12T070000.bin", "Europe/Stockholm", [500]);

        var places = PlaceFiles.Read(folder);

        Assert.Equal([900u, 1030u, 4630u], places.Select(item => item.Place.Timestamp));
        Assert.All(places, item => Assert.Equal("Europe/Stockholm", item.Zone));
    }

    [Fact]
    public void Read_of_a_missing_folder_is_empty()
    {
        Assert.Empty(PlaceFiles.Read(Path.Combine(folder, "missing")));
        Assert.False(PlaceFiles.Any(Path.Combine(folder, "missing")));
    }

    [Fact]
    public void Any_is_false_for_files_without_places()
    {
        Write("2026-10-12T070000.bin", "UTC", [500]);

        Assert.False(PlaceFiles.Any(folder));

        Write("2026-10-12T080000.bin", "UTC", [1000], (900, 1, 0));

        Assert.True(PlaceFiles.Any(folder));
    }

    [Fact]
    public void Last_is_the_latest_point_in_the_newest_file_with_places()
    {
        Write("2026-10-12T080000.bin", "UTC", [1000], (900, 1, 0), (1030, 2, 0));
        Write("2026-10-12T090000.bin", "UTC", [4600]);

        Assert.Equal(1030u, PlaceFiles.Last(folder, 0)?.Timestamp);
        Assert.Null(PlaceFiles.Last(folder, 2000));
    }

    [Fact]
    public void ClearBefore_drops_points_and_keeps_every_measurement()
    {
        var first = Write("2026-10-12T080000.bin", "UTC", [1000, 1060], (900, 1, 0), (1030, 2, 0));
        var second = Write("2026-10-12T090000.bin", "UTC", [4600], (1030, 2, 0), (4630, 3, 0));

        var rewritten = PlaceFiles.ClearBefore(folder, 4630, skip: null);

        Assert.Equal(2, rewritten);
        Assert.Empty(Parse(first).Places);
        Assert.Equal([1000u, 1060u], Parse(first).Measurements_.Select(item => item.Timestamp));
        Assert.Equal([4630u], Parse(second).Places.Select(item => item.Timestamp));
        Assert.Equal([4600u], Parse(second).Measurements_.Select(item => item.Timestamp));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp"));
    }

    [Fact]
    public void ClearBefore_leaves_the_skipped_file_and_files_without_old_points()
    {
        var old = Write("2026-10-12T070000.bin", "UTC", [500]);
        var held = Write("2026-10-12T080000.bin", "UTC", [1000], (900, 1, 0));
        var before = File.ReadAllBytes(old);

        var rewritten = PlaceFiles.ClearBefore(folder, 2000, skip: held);

        Assert.Equal(0, rewritten);
        Assert.Single(Parse(held).Places);
        Assert.Equal(before, File.ReadAllBytes(old));
    }

    [Fact]
    public void A_file_cleared_of_all_places_is_byte_identical_to_one_never_with_places()
    {
        var with = Write("2026-10-12T080000.bin", "UTC", [1000, 1060], (900, 1, 0));
        var without = Write("never.bin", "UTC", [1000, 1060]);

        PlaceFiles.ClearBefore(folder, 2000, skip: without);

        Assert.Equal(File.ReadAllBytes(without), File.ReadAllBytes(with));
    }

    [Fact]
    public void Newest_is_by_name()
    {
        Write("2026-10-12T090000.bin", "UTC", [4600]);
        Write("2026-10-12T080000.bin", "UTC", [1000]);

        Assert.Equal("2026-10-12T090000.bin", Path.GetFileName(PlaceFiles.Newest(folder)));
    }

    [Fact]
    public void Place_timestamp_is_in_the_file_zone()
    {
        // 23:30 UTC is the next day in Stockholm.
        var place = new PlaceWithZone(new Place { Timestamp = (uint)DateTimeOffset.Parse("2026-10-12T23:30:00Z").ToUnixTimeSeconds() }, "Europe/Stockholm");

        Assert.Equal(new DateOnly(2026, 10, 13), DateOnly.FromDateTime(place.Timestamp.Date));
    }
}
