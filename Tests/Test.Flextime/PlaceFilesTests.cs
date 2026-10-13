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

    [Fact]
    public void Files_from_a_time_are_the_later_files_and_the_one_in_effect()
    {
        Write("2026-10-01T080000.bin", "UTC", [1]);
        Write("2026-10-05T230000.bin", "UTC", [2]);
        Write("2026-10-06T000000.bin", "UTC", [3]);
        Write("2026-10-07T120000.bin", "UTC", [4]);
        Write("unnamed.bin", "UTC", [5]);

        var files = PlaceFiles.Files(folder, new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            ["2026-10-05T230000.bin", "2026-10-06T000000.bin", "2026-10-07T120000.bin", "unnamed.bin"],
            files.Select(Path.GetFileName));
        Assert.Equal(5, PlaceFiles.Files(folder, null).Count);
    }

    [Fact]
    public void Read_from_a_time_does_not_open_older_files()
    {
        // Garbage would be skipped silently, so the old file holds a
        // valid point that would show if it were read.
        Write("2026-10-01T080000.bin", "UTC", [1], (5000, 9, 0));
        Write("2026-10-05T230000.bin", "UTC", [2], (900, 1, 0));
        Write("2026-10-06T080000.bin", "UTC", [3], (900, 1, 0), (1030, 2, 0));

        var places = PlaceFiles.Read(folder, new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal([900u, 1030u], places.Select(item => item.Place.Timestamp));
    }

    [Fact]
    public void Read_keeps_the_hint_of_either_copy_of_a_point()
    {
        Write("2026-10-12T080000.bin", "UTC", [1000], (900, 1, 0));

        var path = Path.Combine(folder, "2026-10-12T090000.bin");
        var file = new Measurements { Interval = 60, Zone = "UTC" };

        file.Places.Add(new Place { Timestamp = 900, Router = 1, Hint = "omegapoint.se" });

        using (var stream = File.Create(path))
        {
            file.WriteTo(stream);
        }

        Assert.Equal("omegapoint.se", Assert.Single(PlaceFiles.Read(folder)).Place.Hint);
    }

    [Fact]
    public void Last_looks_back_a_bounded_number_of_files()
    {
        Write("2026-10-01T000000.bin", "UTC", [1000], (900, 1, 0));

        var start = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < PlaceFiles.LastFilesBack - 1; i++)
        {
            Write($"{start.AddHours(i):yyyy-MM-ddTHHmmss}.bin", "UTC", [2000]);
        }

        Assert.Equal(900u, PlaceFiles.Last(folder, 0)?.Timestamp);

        Write($"{start.AddHours(PlaceFiles.LastFilesBack):yyyy-MM-ddTHHmmss}.bin", "UTC", [3000]);

        Assert.Null(PlaceFiles.Last(folder, 0));
    }

    [Theory]
    [InlineData("omegapoint.se", "omegapoint.se")]
    [InlineData("Omegapoint.SE.", "omegapoint.se")]
    [InlineData("  corp.example-1.com  ", "corp.example-1.com")]
    [InlineData("", null)]
    [InlineData(".", null)]
    [InlineData(null, null)]
    [InlineData(".example.com", null)]
    [InlineData("example.com..", null)]
    [InlineData("exa mple.com", null)]
    [InlineData("exämple.se", null)]
    [InlineData("under_score.se", null)]
    public void Hints_are_normalized_or_dropped(string? domain, string? expected)
    {
        Assert.Equal(expected, PlaceHints.Normalize(domain));
    }

    [Fact]
    public void Hints_are_at_most_253_characters()
    {
        Assert.True(PlaceHints.IsValid(new string('a', 253)));
        Assert.False(PlaceHints.IsValid(new string('a', 254)));
        Assert.False(PlaceHints.IsValid(string.Empty));
    }

    [Theory]
    [InlineData("localdomain")]
    [InlineData("lan")]
    [InlineData("home")]
    [InlineData("local")]
    [InlineData("home.arpa")]
    [InlineData("localhost")]
    [InlineData("domain")]
    [InlineData("workgroup")]
    public void Router_defaults_are_not_hints(string domain)
    {
        Assert.True(PlaceHints.IsValid(domain));
        Assert.False(PlaceHints.IsHint(domain));
    }

    [Theory]
    [InlineData("fritz.box")]
    [InlineData("omegapoint.se")]
    [InlineData("home.example.com")]
    [InlineData("lan.se")]
    public void Other_domains_are_hints(string domain)
    {
        Assert.True(PlaceHints.IsHint(domain));
    }
}
