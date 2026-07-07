using Flextime;
using Google.Protobuf;

namespace Test.Flextime;

public class ReaderTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-reader-tests-{Guid.NewGuid():N}");

    private int fileNumber;

    public ReaderTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void MissingFolderIsEmpty()
    {
        var byDates = Reader.ReadFiles(Path.Combine(folder, "missing"), TimeSpan.MinValue);

        Assert.Empty(byDates);
    }

    [Fact]
    public void GroupsByDateInMeasurementZone()
    {
        // 23:30 UTC is still January 1 in UTC, but January 2 in Tokyo.
        var timestamp = DateTimeOffset.Parse("2024-01-01T23:30:00+00:00");

        WriteFile(folder, "UTC", timestamp);

        var byDates = Reader.ReadFiles(folder, TimeSpan.MinValue);

        Assert.Equal(new[] { new DateOnly(2024, 1, 1) }, byDates.Keys);
    }

    [Fact]
    public void MidnightBoundaryFollowsMeasurementZone()
    {
        var timestamp = DateTimeOffset.Parse("2024-01-01T23:30:00+00:00");

        WriteFile(folder, "Asia/Tokyo", timestamp);

        var byDates = Reader.ReadFiles(folder, TimeSpan.MinValue);

        Assert.Equal(new[] { new DateOnly(2024, 1, 2) }, byDates.Keys);
    }

    [Fact]
    public void MergesAndSortsMeasurementsAcrossFiles()
    {
        var first = DateTimeOffset.Parse("2024-01-01T10:00:00+00:00");

        WriteFile(folder, "UTC", first.AddMinutes(2), first.AddMinutes(3));
        WriteFile(folder, "UTC", first, first.AddMinutes(1));

        var byDates = Reader.ReadFiles(folder, TimeSpan.MinValue);

        var timestamps = Assert.Single(byDates).Value.list.Select(item => item.Measurement.Timestamp);

        Assert.Equal(
            Enumerable.Range(0, 4).Select(i => (uint)first.AddMinutes(i).ToUnixTimeSeconds()),
            timestamps);
    }

    [Fact]
    public void DatesAreSorted()
    {
        WriteFile(folder, "UTC", DateTimeOffset.Parse("2024-01-03T10:00:00+00:00"));
        WriteFile(folder, "UTC", DateTimeOffset.Parse("2024-01-01T10:00:00+00:00"));
        WriteFile(folder, "UTC", DateTimeOffset.Parse("2024-01-02T10:00:00+00:00"));

        var byDates = Reader.ReadFiles(folder, TimeSpan.MinValue);

        Assert.Equal(
            new[] { new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3) },
            byDates.Keys);
    }

    [Fact]
    public void SinceExcludesOldDates()
    {
        var now = DateTimeOffset.UtcNow;

        WriteFile(folder, "UTC", now);
        WriteFile(folder, "UTC", now.AddDays(-10));

        var all = Reader.ReadFiles(folder, TimeSpan.MinValue);
        var recent = Reader.ReadFiles(folder, TimeSpan.FromDays(3));

        Assert.Equal(2, all.Count);

        var day = Assert.Single(recent);
        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), day.Key);
    }

    [Fact]
    public void RemainderAfterSyncedPrefixIsFound()
    {
        var timestamps = Timestamps("2024-01-01T10:00:00+00:00", count: 5);

        WriteFile(folder, "UTC", timestamps);

        // The remote has the first three measurements from an earlier sync.
        var remoteHash = HashOf(timestamps[..3]);

        var (list, found) = Reader.ReadFiles(folder, TimeSpan.MinValue, new DateOnly(2024, 1, 1), remoteHash);

        Assert.True(found);
        Assert.Equal(
            timestamps[3..].Select(timestamp => (uint)timestamp.ToUnixTimeSeconds()),
            list.Select(item => item.Measurement.Timestamp));
    }

    [Fact]
    public void EmptyRemoteDayMatchesWholeDay()
    {
        var timestamps = Timestamps("2024-01-01T10:00:00+00:00", count: 3);

        WriteFile(folder, "UTC", timestamps);

        var (list, found) = Reader.ReadFiles(folder, TimeSpan.MinValue, new DateOnly(2024, 1, 1), hash: 0);

        Assert.True(found);
        Assert.Equal(timestamps.Length, list.Count);
    }

    [Fact]
    public void MismatchedHashIsNotFound()
    {
        var timestamps = Timestamps("2024-01-01T10:00:00+00:00", count: 5);

        WriteFile(folder, "UTC", timestamps);

        // A remote day that is not a prefix of the local measurements
        // cannot be synced.
        var remoteHash = HashOf(timestamps[0], timestamps[2], timestamps[3]);

        var (list, found) = Reader.ReadFiles(folder, TimeSpan.MinValue, new DateOnly(2024, 1, 1), remoteHash);

        Assert.False(found);
        Assert.Empty(list);
    }

    [Fact]
    public void HashOfAllMeasurementsIsNotFound()
    {
        // A remote day matching all local measurements is the in sync case,
        // which the caller detects before looking for a prefix.
        var timestamps = Timestamps("2024-01-01T10:00:00+00:00", count: 5);

        WriteFile(folder, "UTC", timestamps);

        var (list, found) = Reader.ReadFiles(folder, TimeSpan.MinValue, new DateOnly(2024, 1, 1), HashOf(timestamps));

        Assert.False(found);
        Assert.Empty(list);
    }

    [Fact]
    public void MissingDateIsNotFound()
    {
        var timestamps = Timestamps("2024-01-01T10:00:00+00:00", count: 3);

        WriteFile(folder, "UTC", timestamps);

        var (list, found) = Reader.ReadFiles(folder, TimeSpan.MinValue, new DateOnly(2024, 1, 2), HashOf(timestamps));

        Assert.False(found);
        Assert.Empty(list);
    }

    [Fact]
    public void HashDependsOnlyOnTimestamps()
    {
        var timestamps = Timestamps("2024-01-01T10:00:00+00:00", count: 3);

        WriteFile(folder, "UTC", timestamps);

        var hash = Assert.Single(Reader.ReadFiles(folder, TimeSpan.MinValue)).Value.hash;

        var other = Path.Combine(folder, "other");
        Directory.CreateDirectory(other);
        WriteFile(other, "UTC", idle: 300, timestamps);

        var otherHash = Assert.Single(Reader.ReadFiles(other, TimeSpan.MinValue)).Value.hash;

        Assert.Equal(hash, otherHash);
    }

    private static DateTimeOffset[] Timestamps(string start, int count)
    {
        var first = DateTimeOffset.Parse(start);

        return Enumerable.Range(0, count).Select(i => first.AddMinutes(i)).ToArray();
    }

    private long HashOf(params DateTimeOffset[] timestamps)
    {
        var hashFolder = Path.Combine(folder, $"hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hashFolder);

        WriteFile(hashFolder, "UTC", timestamps);

        return Assert.Single(Reader.ReadFiles(hashFolder, TimeSpan.MinValue)).Value.hash;
    }

    private void WriteFile(string targetFolder, string zone, params DateTimeOffset[] timestamps)
    {
        WriteFile(targetFolder, zone, idle: 0, timestamps);
    }

    private void WriteFile(string targetFolder, string zone, uint idle, DateTimeOffset[] timestamps)
    {
        var measurements = new Measurements { Interval = 60, Zone = zone };

        measurements.Measurements_.AddRange(timestamps.Select(timestamp => new Measurement
        {
            Timestamp = (uint)timestamp.ToUnixTimeSeconds(),
            Idle = idle,
            Kind = Measurement.Types.Kind.Measurement
        }));

        using var stream = File.Create(Path.Combine(targetFolder, $"{fileNumber++:D3}.bin"));
        measurements.WriteTo(stream);
    }
}
