using System.Text;
using Flextime;
using Flextime.Daemon;

namespace Test.Flextime;

public class LocationTests : IDisposable
{
    private const string UserId = "3f1d2c4b-0000-4000-8000-1234567890ab";
    private const string Mac = "00:1a:2b:0c:d4:e5";

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-location-tests-{Guid.NewGuid():N}");

    public LocationTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Off_when_never_set()
    {
        Assert.False(Location.IsOn(Path.Combine(folder, "location.txt")));
        Assert.Null(Location.ReadClear(Path.Combine(folder, "location-clear.txt")));
    }

    [Fact]
    public void On_and_off_round_trip()
    {
        var path = Path.Combine(folder, "location.txt");

        Location.SetOn(true, path);
        Assert.True(Location.IsOn(path));

        Location.SetOn(false, path);
        Assert.False(Location.IsOn(path));
    }

    [Fact]
    public void Clear_marker_round_trips()
    {
        var path = Path.Combine(folder, "location-clear.txt");

        Location.WriteClear(new ClearMarker(1760281200, Pending: true), path);
        Assert.Equal(new ClearMarker(1760281200, true), Location.ReadClear(path));

        Location.WriteClear(new ClearMarker(1760281200, Pending: false), path);
        Assert.Equal(new ClearMarker(1760281200, false), Location.ReadClear(path));
    }

    [Fact]
    public void Settings_combine_both_files()
    {
        var on = Path.Combine(folder, "location.txt");
        var clear = Path.Combine(folder, "location-clear.txt");

        Location.SetOn(true, on);
        Location.WriteClear(new ClearMarker(42, false), clear);

        Assert.Equal(new global::Flextime.Monitor.PlaceSettings(true, 42), Location.ReadSettings(on, clear));
    }

    [Fact]
    public void Client_hash_is_deterministic_and_12_hex()
    {
        var hash = Location.ClientHash(UserId, Mac);

        Assert.Equal(hash, Location.ClientHash(UserId, Mac));
        Assert.Matches("^[0-9a-f]{12}$", Location.Hex(hash));
        Assert.True(hash < 1ul << 48);
    }

    [Fact]
    public void Client_hash_depends_on_user_and_address()
    {
        var hash = Location.ClientHash(UserId, Mac);

        Assert.NotEqual(hash, Location.ClientHash("another-user", Mac));
        Assert.NotEqual(hash, Location.ClientHash(UserId, "00:1a:2b:0c:d4:e6"));
    }

    [Fact]
    public void Hex_pads_to_12()
    {
        Assert.Equal("00000000002a", Location.Hex(42));
    }

    private static string Jwt(string payload)
    {
        static string Encode(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode(payload)}.";
    }

    [Fact]
    public void User_id_is_the_object_id_in_the_token()
    {
        Assert.Equal(UserId, Location.UserId(Jwt($$"""{"oid":"{{UserId}}","name":"Martin"}""")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("opaque-token")]
    public void No_user_id_without_a_jwt(string token)
    {
        Assert.Null(Location.UserId(token));
    }

    [Fact]
    public void No_user_id_without_the_claim()
    {
        Assert.Null(Location.UserId(Jwt("""{"name":"Martin"}""")));
    }

    private static NetworkReport Report(InternetRoute[] routes, Router[] routers, VpnService[]? services = null) =>
        new(routes, services, routers, true, string.Empty);

    private static readonly InternetRoute WiFi = new("IPv4", "192.168.4.20", "en0", "Wi-Fi", false);
    private static readonly InternetRoute Tunnel = new("IPv4", "10.8.0.2", "utun4", "Tunnel", true);
    private static readonly Router HomeRouter = new("192.168.4.1", "en0", "95313127c39c", Mac);

    [Fact]
    public void Reading_takes_the_router_internet_traffic_leaves_through()
    {
        var other = new Router("10.0.0.1", "en5", "x", "aa:bb:cc:dd:ee:ff");

        var reading = Location.ToReading(Report([WiFi], [other, HomeRouter]), UserId);

        Assert.Equal(Location.ClientHash(UserId, Mac), reading.Router);
        Assert.Equal(0u, reading.Flags);
        Assert.True(reading.Online);
        Assert.False(reading.Pending);
    }

    [Fact]
    public void Reading_through_a_tunnel_keeps_the_physical_router_and_is_not_dependable()
    {
        var reading = Location.ToReading(
            Report([Tunnel], [HomeRouter], [new VpnService("Office", "Connected")]), UserId);

        Assert.Equal(Location.ClientHash(UserId, Mac), reading.Router);
        Assert.Equal(PlaceFlags.NotDependable | PlaceFlags.Vpn, reading.Flags);
    }

    [Fact]
    public void Reading_with_a_split_tunnel_vpn_is_dependable()
    {
        var reading = Location.ToReading(
            Report([WiFi], [HomeRouter], [new VpnService("Tailscale", "Connected"), new VpnService("Other", "Disconnected")]), UserId);

        Assert.Equal(PlaceFlags.Vpn, reading.Flags);
    }

    [Fact]
    public void Reading_offline_is_router_zero()
    {
        var reading = Location.ToReading(Report([], []), UserId);

        Assert.Equal(new global::Flextime.Monitor.PlaceReading(0, 0, Online: false), reading);
    }

    [Fact]
    public void Reading_without_the_hardware_address_is_pending()
    {
        var reading = Location.ToReading(Report([WiFi], [HomeRouter with { Id = null, Mac = null }]), UserId);

        Assert.Equal(0ul, reading.Router);
        Assert.True(reading.Pending);
    }

    [Fact]
    public void Reading_without_a_user_is_router_zero()
    {
        Assert.Equal(0ul, Location.ToReading(Report([WiFi], [HomeRouter]), null).Router);
    }

    [Fact]
    public void Reading_carries_the_hint_of_the_router_it_takes()
    {
        var other = new Router("10.0.0.1", "en5", "x", "aa:bb:cc:dd:ee:ff", "other.example");

        var reading = Location.ToReading(Report([WiFi], [other, HomeRouter with { Domain = "omegapoint.se" }]), UserId);

        Assert.Equal("omegapoint.se", reading.Hint);
    }

    [Fact]
    public void Reading_without_a_router_has_no_hint()
    {
        var reading = Location.ToReading(Report([WiFi], [HomeRouter with { Id = null, Mac = null, Domain = "omegapoint.se" }]), UserId);

        Assert.Null(reading.Hint);
    }

    [Fact]
    public void Reading_has_no_hint_for_a_router_default_domain()
    {
        var reading = Location.ToReading(Report([WiFi], [HomeRouter with { Domain = "localdomain" }]), UserId);

        Assert.NotEqual(0ul, reading.Router);
        Assert.Null(reading.Hint);
    }

    [Theory]
    [InlineData("omegapoint.se", "Network         :  omegapoint.se")]
    [InlineData("fritz.box", "Network         :  fritz.box")]
    [InlineData("localdomain", "Network         :  none (router default localdomain)")]
    [InlineData(null, null)]
    public void The_network_line_says_why_there_is_no_hint(string? domain, string? line)
    {
        Assert.Equal(line, (HomeRouter with { Domain = domain }).NetworkLine);
    }
}
