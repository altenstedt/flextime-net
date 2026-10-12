using Flextime;
using Flextime.Daemon;
using Google.Protobuf;

namespace Test.Flextime;

public class LocationClientTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-location-client-tests-{Guid.NewGuid():N}");

    private string Measurements => Path.Combine(folder, "measurements");
    private string MarkerPath => Path.Combine(folder, "location-clear.txt");
    private string LocationPath => Path.Combine(folder, "location.txt");

    public LocationClientTests()
    {
        Directory.CreateDirectory(Measurements);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private string Write(string name, uint[] measurements, params uint[] places)
    {
        var file = new Measurements { Interval = 60, Zone = "UTC" };

        file.Measurements_.AddRange(measurements.Select(timestamp => new Measurement { Timestamp = timestamp, Kind = Measurement.Types.Kind.Measurement }));
        file.Places.AddRange(places.Select(timestamp => new Place { Timestamp = timestamp, Router = 7 }));

        var path = Path.Combine(Measurements, name);

        using var stream = File.Create(path);

        file.WriteTo(stream);

        return path;
    }

    private static Measurements Parse(string path)
    {
        using var stream = File.OpenRead(path);

        return global::Measurements.Parser.ParseFrom(stream);
    }

    [Fact]
    public void On_refuses_without_login()
    {
        Assert.False(LocationClient.TurnOn(authenticated: false, "user", LocationPath));
        Assert.False(LocationClient.TurnOn(authenticated: true, userId: null, LocationPath));
        Assert.False(Location.IsOn(LocationPath));

        Assert.True(LocationClient.TurnOn(authenticated: true, "user", LocationPath));
        Assert.True(Location.IsOn(LocationPath));
    }

    [Fact]
    public async Task Clear_writes_the_marker_before_anything_else_and_confirms_after_delete()
    {
        var older = Write("2026-10-12T080000.bin", [1000, 1060], 900, 1030);
        var newest = Write("2026-10-12T090000.bin", [4600], 1030, 4630);

        ClearMarker? seenByDelete = null;
        var olderPlacesSeenByDelete = -1;

        var outcome = await LocationClient.ClearComputer(5000, _ =>
        {
            seenByDelete = Location.ReadClear(MarkerPath);
            olderPlacesSeenByDelete = Parse(older).Places.Count;

            return Task.CompletedTask;
        }, Measurements, MarkerPath, listenRunning: false, TestContext.Current.CancellationToken);

        Assert.Equal(new ClearMarker(5000, Pending: true), seenByDelete);
        Assert.Equal(0, olderPlacesSeenByDelete);
        Assert.Equal(new ClearOutcome(2, null), outcome);
        Assert.Equal(new ClearMarker(5000, Pending: false), Location.ReadClear(MarkerPath));

        Assert.Empty(Parse(older).Places);
        Assert.Empty(Parse(newest).Places);
        Assert.Equal([1000u, 1060u], Parse(older).Measurements_.Select(item => item.Timestamp));
        Assert.Equal([4600u], Parse(newest).Measurements_.Select(item => item.Timestamp));
    }

    [Fact]
    public async Task Clear_leaves_the_file_listen_writes()
    {
        Write("2026-10-12T080000.bin", [1000], 900);
        var newest = Write("2026-10-12T090000.bin", [4600], 4630);

        var outcome = await LocationClient.ClearComputer(5000, null, Measurements, MarkerPath, listenRunning: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Rewritten);
        Assert.Single(Parse(newest).Places);
    }

    [Fact]
    public async Task Failed_delete_keeps_the_marker_pending()
    {
        Write("2026-10-12T080000.bin", [1000], 900);

        var outcome = await LocationClient.ClearComputer(
            5000,
            _ => throw new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable)."),
            Measurements, MarkerPath, listenRunning: false, TestContext.Current.CancellationToken);

        Assert.Contains("503", outcome.Error);
        Assert.Equal(new ClearMarker(5000, Pending: true), Location.ReadClear(MarkerPath));
    }

    [Fact]
    public async Task Clear_without_login_clears_locally_and_stays_pending()
    {
        var file = Write("2026-10-12T080000.bin", [1000], 900);

        var outcome = await LocationClient.ClearComputer(5000, null, Measurements, MarkerPath, listenRunning: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ClearOutcome(1, null), outcome);
        Assert.Empty(Parse(file).Places);
        Assert.True(Location.ReadClear(MarkerPath)!.Pending);
    }

    [Fact]
    public void Confirm_does_not_settle_a_newer_clear()
    {
        Location.WriteClear(new ClearMarker(6000, Pending: true), MarkerPath);

        LocationClient.ConfirmDelete(5000, MarkerPath);

        Assert.True(Location.ReadClear(MarkerPath)!.Pending);
    }
}
