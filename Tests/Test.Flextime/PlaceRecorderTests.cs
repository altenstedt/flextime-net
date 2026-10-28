using Flextime;
using Flextime.Monitor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Flextime;

public class PlaceRecorderTests
{
    private const ulong Home = 0x95313127c39c;
    private const ulong Office = 0xa1b2c3d4e5f6;

    private static readonly DateTimeOffset Start = new(2026, 10, 12, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource : IPlaceSource
    {
        public PlaceSettings Settings { get; set; } = new(true, 0);

        public Queue<PlaceReading> Script { get; } = new();

        public PlaceReading Network { get; set; } = new(Home, 0);

        public int Reads { get; private set; }

        // What a read does to the clock, for reads that take a while.
        public Action? OnRead { get; set; }

        public PlaceSettings ReadSettings() => Settings;

        public Task<PlaceReading> Read()
        {
            Reads++;
            OnRead?.Invoke();

            return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : Network);
        }
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Start;

        public List<TimeSpan> Delays { get; } = [];

        public Task Delay(TimeSpan delay)
        {
            Delays.Add(delay);
            Now += delay;

            return Task.CompletedTask;
        }
    }

    private static (PlaceRecorder recorder, FakeSource source, Clock clock) Create(Place? onDisk = null, PlaceSettings? settings = null)
    {
        var source = new FakeSource();

        if (settings.HasValue)
        {
            source.Settings = settings.Value;
        }

        var clock = new Clock();

        var recorder = new PlaceRecorder(source, NullLogger.Instance)
        {
            Now = () => clock.Now,
            Delay = clock.Delay,
        };

        recorder.Initialize(_ => onDisk);

        return (recorder, source, clock);
    }

    private static uint Seconds(DateTimeOffset time) => (uint)time.ToUnixTimeSeconds();

    private static (uint, ulong, uint)[] Points(PlaceRecorder recorder) =>
        recorder.Places.Select(place => (place.Timestamp, place.Router, place.Flags)).ToArray();

    [Fact]
    public async Task Writes_a_change_point_only_when_router_or_flags_change()
    {
        var (recorder, source, clock) = Create();

        source.Script.Enqueue(new PlaceReading(Home, 0));
        source.Script.Enqueue(new PlaceReading(Home, 0));
        source.Script.Enqueue(new PlaceReading(Home, PlaceFlags.Vpn));
        source.Script.Enqueue(new PlaceReading(Office, PlaceFlags.Vpn));

        Assert.True((await recorder.NetworkChanged()).Changed);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False((await recorder.NetworkChanged()).Changed);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True((await recorder.NetworkChanged()).Changed);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True((await recorder.NetworkChanged()).Changed);

        Assert.Equal(
            [
                (Seconds(Start), Home, 0u),
                (Seconds(Start.AddMinutes(2)), Home, PlaceFlags.Vpn),
                (Seconds(Start.AddMinutes(3)), Office, PlaceFlags.Vpn),
            ],
            Points(recorder));
    }

    [Fact]
    public async Task A_new_file_starts_with_the_place_in_effect()
    {
        var (recorder, source, clock) = Create();

        source.Network = new PlaceReading(Home, 0);
        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(Office, 0);
        await recorder.NetworkChanged();

        recorder.StartFile();

        Assert.Equal([(Seconds(Start.AddMinutes(1)), Office, 0u)], Points(recorder));
    }

    [Fact]
    public async Task Fallback_timer_samples_when_no_event_arrives()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);
        Assert.Equal(1, source.Reads);

        // Every minute in between: not due, the network is not read.
        for (var minute = 1; minute < 5; minute++)
        {
            clock.Now = Start.AddMinutes(minute);
            await recorder.Tick(wake: false);
        }

        Assert.Equal(1, source.Reads);

        source.Network = new PlaceReading(Office, 0);
        clock.Now = Start + PlaceRecorder.FallbackInterval;

        Assert.True((await recorder.Tick(wake: false)).Changed);
        Assert.Equal(2, source.Reads);
        Assert.Equal(Office, recorder.Places[^1].Router);
    }

    [Fact]
    public async Task Network_change_samples_at_once()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromSeconds(10);
        source.Network = new PlaceReading(Office, 0);

        Assert.True((await recorder.NetworkChanged()).Changed);
        Assert.Equal(2, source.Reads);
        Assert.Equal((Seconds(Start.AddSeconds(10)), Office, 0u), Points(recorder)[^1]);
    }

    [Fact]
    public async Task Wake_reads_the_network_before_the_measurement_even_when_not_due()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        // Lid closed at the office a minute later, opened at home: the
        // fallback timer is not due, the wake is.  The network takes a
        // few seconds to come back.
        clock.Now += TimeSpan.FromMinutes(1);
        source.Script.Enqueue(new PlaceReading(0, 0, Online: false));
        source.Script.Enqueue(new PlaceReading(0, 0, Online: false));
        source.Network = new PlaceReading(Office, 0);

        var wokeAt = clock.Now;

        var update = await recorder.Tick(wake: true);

        Assert.True(update.Changed);
        Assert.Equal(2, clock.Delays.Count);

        var place = recorder.Places[^1];

        // The measurement taken right after the tick carries the new place.
        Assert.Equal(Office, place.Router);
        Assert.True(place.Timestamp <= Seconds(clock.Now));
        Assert.True(place.Timestamp >= Seconds(wokeAt));
    }

    [Fact]
    public async Task Slow_reads_after_a_wake_stop_within_the_budget()
    {
        var (recorder, source, clock) = Create();

        // Each read waits out its tools' timeout.
        source.Network = new PlaceReading(0, 0, Online: false);
        source.OnRead = () => clock.Now += TimeSpan.FromSeconds(5);

        await recorder.Tick(wake: true);

        Assert.Equal(3, source.Reads);
        Assert.True(clock.Now - Start <= TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Wake_gives_up_waiting_and_records_unknown_when_offline()
    {
        var (recorder, source, _) = Create();

        source.Network = new PlaceReading(0, 0, Online: false);

        await recorder.Tick(wake: true);

        Assert.Equal(0ul, recorder.Places[^1].Router);
        Assert.Equal(6, source.Reads);
    }

    [Fact]
    public async Task Offline_writes_router_zero()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(0, 0, Online: false);
        await recorder.NetworkChanged();

        Assert.Equal((Seconds(Start.AddMinutes(1)), 0ul, 0u), Points(recorder)[^1]);
    }

    [Fact]
    public async Task Location_off_writes_bit_two_once()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        source.Settings = new PlaceSettings(false, 0);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True((await recorder.Tick(wake: false)).Changed);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False((await recorder.Tick(wake: true)).Changed);
        Assert.False((await recorder.NetworkChanged()).Changed);

        Assert.Equal(
            [(Seconds(Start), Home, 0u), (Seconds(Start.AddMinutes(1)), 0ul, PlaceFlags.LocationOff)],
            Points(recorder));
        Assert.Equal(1, source.Reads);

        // And a file started while off does not carry the marker.
        recorder.StartFile();
        Assert.Empty(recorder.Places);
    }

    [Fact]
    public async Task Location_never_on_writes_nothing_and_reads_no_network()
    {
        var (recorder, source, clock) = Create(settings: new PlaceSettings(false, 0));

        for (var minute = 0; minute < 10; minute++)
        {
            clock.Now = Start.AddMinutes(minute);
            Assert.False((await recorder.Tick(wake: minute == 3)).Changed);
        }

        Assert.False((await recorder.NetworkChanged()).Changed);
        recorder.StartFile();

        Assert.Empty(recorder.Places);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Turned_on_again_samples_at_the_next_tick()
    {
        var (recorder, source, clock) = Create(settings: new PlaceSettings(false, 0));

        await recorder.Tick(wake: false);

        source.Settings = new PlaceSettings(true, 0);
        clock.Now += TimeSpan.FromMinutes(1);

        Assert.True((await recorder.Tick(wake: false)).Changed);
        Assert.Equal([(Seconds(Start.AddMinutes(1)), Home, 0u)], Points(recorder));
    }

    [Fact]
    public async Task Restart_at_the_same_place_repeats_nothing()
    {
        var onDisk = new Place { Timestamp = Seconds(Start.AddHours(-12)), Router = Home };

        var (recorder, _, _) = Create(onDisk);

        // The new file starts with the place in effect, as written before.
        Assert.Equal([(onDisk.Timestamp, Home, 0u)], Points(recorder));

        Assert.False((await recorder.Tick(wake: true)).Changed);
    }

    [Fact]
    public async Task Turned_off_while_listen_was_stopped_marks_off_at_start()
    {
        var onDisk = new Place { Timestamp = Seconds(Start.AddHours(-12)), Router = Home };

        var (recorder, source, _) = Create(onDisk, new PlaceSettings(false, 0));

        Assert.True((await recorder.Tick(wake: true)).Changed);
        Assert.Equal((Seconds(Start), 0ul, PlaceFlags.LocationOff), Points(recorder)[^1]);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Clear_drops_points_before_the_marker_and_records_the_place_again()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(Office, 0);
        await recorder.NetworkChanged();

        clock.Now += TimeSpan.FromMinutes(1);
        var clearedAt = Seconds(clock.Now);
        source.Settings = new PlaceSettings(true, clearedAt);

        clock.Now += TimeSpan.FromMinutes(1);
        var update = await recorder.Tick(wake: false);

        Assert.True(update.Changed);
        Assert.Equal(clearedAt, update.ClearedAt);
        Assert.Equal([(Seconds(clock.Now), Office, 0u)], Points(recorder));

        // Noticed once.
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Null((await recorder.Tick(wake: false)).ClearedAt);
    }

    [Fact]
    public async Task Clear_with_location_off_leaves_the_file_without_places()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Settings = new PlaceSettings(false, Seconds(clock.Now));

        clock.Now += TimeSpan.FromMinutes(1);
        var update = await recorder.Tick(wake: false);

        Assert.True(update.Changed);
        Assert.Empty(recorder.Places);
    }

    [Fact]
    public void A_point_on_disk_before_the_clear_is_forgotten()
    {
        var onDisk = new Place { Timestamp = Seconds(Start.AddHours(-1)), Router = Home };

        var (recorder, _, _) = Create(onDisk, new PlaceSettings(true, Seconds(Start.AddMinutes(-30))));

        Assert.Empty(recorder.Places);
    }

    [Fact]
    public async Task Pending_hardware_address_is_reported()
    {
        var (recorder, source, _) = Create();

        source.Network = new PlaceReading(0, 0, Pending: true);

        Assert.True((await recorder.Tick(wake: false)).Pending);
    }

    private static (uint, ulong, uint, string)[] PointsWithHints(PlaceRecorder recorder) =>
        recorder.Places.Select(place => (place.Timestamp, place.Router, place.Flags, place.Hint)).ToArray();

    [Fact]
    public async Task The_first_change_point_for_a_router_in_a_file_carries_the_hint()
    {
        var (recorder, source, clock) = Create();

        source.Script.Enqueue(new PlaceReading(Office, 0, Hint: "omegapoint.se"));
        source.Script.Enqueue(new PlaceReading(Office, PlaceFlags.Vpn, Hint: "omegapoint.se"));
        source.Script.Enqueue(new PlaceReading(Home, 0, Hint: "localdomain"));
        source.Script.Enqueue(new PlaceReading(Office, 0, Hint: "omegapoint.se"));
        source.Script.Enqueue(new PlaceReading(0, 0, Hint: "ignored"));

        for (var i = 0; i < 5; i++)
        {
            await recorder.NetworkChanged();
            clock.Now += TimeSpan.FromMinutes(1);
        }

        Assert.Equal(
            [
                (Seconds(Start), Office, 0u, "omegapoint.se"),
                (Seconds(Start.AddMinutes(1)), Office, PlaceFlags.Vpn, ""),
                (Seconds(Start.AddMinutes(2)), Home, 0u, "localdomain"),
                (Seconds(Start.AddMinutes(3)), Office, 0u, ""),
                (Seconds(Start.AddMinutes(4)), 0ul, 0u, ""),
            ],
            PointsWithHints(recorder));
    }

    [Fact]
    public async Task A_hint_alone_changing_is_not_a_change_point_and_reaches_the_next_file()
    {
        var (recorder, source, clock) = Create();

        source.Network = new PlaceReading(Office, 0);
        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(Office, 0, Hint: "omegapoint.se");

        Assert.False((await recorder.NetworkChanged()).Changed);
        Assert.Equal([(Seconds(Start), Office, 0u, "")], PointsWithHints(recorder));

        recorder.StartFile();

        Assert.Equal([(Seconds(Start), Office, 0u, "omegapoint.se")], PointsWithHints(recorder));
    }

    [Fact]
    public async Task A_new_file_starts_with_the_hint_even_after_a_later_point_for_the_router()
    {
        var (recorder, source, clock) = Create();

        source.Network = new PlaceReading(Office, 0, Hint: "omegapoint.se");
        await recorder.Tick(wake: false);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(Office, PlaceFlags.Vpn, Hint: "omegapoint.se");
        await recorder.NetworkChanged();

        recorder.StartFile();

        Assert.Equal([(Seconds(Start.AddMinutes(1)), Office, PlaceFlags.Vpn, "omegapoint.se")], PointsWithHints(recorder));
    }

    [Fact]
    public async Task A_network_read_that_a_tick_overtook_is_not_recorded()
    {
        var (recorder, source, clock) = Create();

        source.Network = new PlaceReading(Home, 0);
        var stale = await recorder.ReadNetwork();

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(Office, 0);
        Assert.True((await recorder.Tick(wake: false)).Changed);

        clock.Now += TimeSpan.FromSeconds(5);
        Assert.False(recorder.Apply(stale!.Value).Changed);

        Assert.Equal([(Seconds(Start.AddMinutes(1)), Office, 0u)], Points(recorder));
    }

    [Fact]
    public async Task A_network_read_is_recorded_after_an_earlier_one()
    {
        var (recorder, source, clock) = Create();

        source.Network = new PlaceReading(Home, 0);
        var earlier = await recorder.ReadNetwork();

        source.Network = new PlaceReading(Office, 0);
        var later = await recorder.ReadNetwork();

        Assert.True(recorder.Apply(later!.Value).Changed);
        Assert.False(recorder.Apply(earlier!.Value).Changed);

        clock.Now += TimeSpan.FromMinutes(1);
        source.Network = new PlaceReading(Home, 0);
        Assert.True(recorder.Apply((await recorder.ReadNetwork())!.Value).Changed);

        Assert.Equal([(Seconds(Start), Office, 0u), (Seconds(Start.AddMinutes(1)), Home, 0u)], Points(recorder));
    }

    [Fact]
    public async Task A_network_read_is_not_recorded_once_location_is_off()
    {
        var (recorder, source, clock) = Create();

        await recorder.Tick(wake: false);

        source.Network = new PlaceReading(Office, 0);
        var sample = await recorder.ReadNetwork();

        clock.Now += TimeSpan.FromMinutes(1);
        source.Settings = new PlaceSettings(false, 0);
        Assert.True((await recorder.Tick(wake: false)).Changed);

        Assert.False(recorder.Apply(sample!.Value).Changed);
        Assert.Null(await recorder.ReadNetwork());

        Assert.Equal(
            [(Seconds(Start), Home, 0u), (Seconds(Start.AddMinutes(1)), 0ul, PlaceFlags.LocationOff)],
            Points(recorder));
    }

    [Fact]
    public async Task Without_hints_change_points_are_what_they_were()
    {
        var (recorder, source, _) = Create();

        source.Network = new PlaceReading(Home, 0);
        await recorder.Tick(wake: false);

        recorder.StartFile();

        Assert.All(recorder.Places, place => Assert.Equal(new Place { Timestamp = place.Timestamp, Router = Home }, place));
    }
}
