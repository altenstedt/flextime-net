using System.Text.Json;
using Flextime;
using Flextime.Daemon;

namespace Test.Flextime;

public class PlaceNameTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // Two days; the second ends at 1000 + 50 + 50 = 1100.
    private const string Ids = """
        {"items":[
          {"zone":"Europe/Stockholm","deltas":[500,100],"places":[[0,"aaaaaaaaaaaa",0]]},
          {"zone":"Europe/Berlin","deltas":[1000,50,50],"places":[[0,"aaaaaaaaaaaa",0],[1,"b9186da7f83b",3]]}
        ],"count":2,"continuation":""}
        """;

    [Fact]
    public void RouterAt_takes_the_last_run_of_the_latest_day()
    {
        Assert.Equal("b9186da7f83b", PlaceName.RouterAt(Json(Ids), 1050));
    }

    [Fact]
    public void RouterAt_is_null_when_the_server_is_behind_the_place()
    {
        Assert.Null(PlaceName.RouterAt(Json(Ids), 1101));
    }

    [Fact]
    public void RouterAt_is_empty_for_a_stretch_without_a_router()
    {
        Assert.Equal(string.Empty, PlaceName.RouterAt(Json("""{"items":[{"zone":"UTC","deltas":[10],"places":[[0,null,0]]}]}"""), 5));
    }

    [Fact]
    public void RouterAt_is_null_without_items()
    {
        Assert.Null(PlaceName.RouterAt(Json("""{"items":[],"count":0}"""), 0));
        Assert.Null(PlaceName.RouterAt(Json("null"), 0));
    }

    private const string Places = """
        {"version":1,"places":[
          {"router":"b9186da7f83b","name":"Prism, Hamburg","color":"blue"},
          {"router":"cccccccccccc","name":"Phone","notAPlace":true},
          {"router":"dddddddddddd","color":"green"}
        ]}
        """;

    [Theory]
    [InlineData("b9186da7f83b", "Prism, Hamburg")]
    [InlineData("cccccccccccc", "Not a place")]
    [InlineData("dddddddddddd", "Not named yet")]
    [InlineData("eeeeeeeeeeee", "Not named yet")]
    public void Describe(string router, string expected)
    {
        Assert.Equal(expected, PlaceName.Describe(Json(Places), router));
    }

    [Fact]
    public void Describe_without_a_document_is_not_named_yet()
    {
        Assert.Equal("Not named yet", PlaceName.Describe(Json("null"), "b9186da7f83b"));
    }

    private static Place Point(uint timestamp, ulong router, uint flags = 0) =>
        new() { Timestamp = timestamp, Router = router, Flags = flags };

    [Fact]
    public void StayStart_spans_a_vpn_going_off()
    {
        Assert.Equal(200u, PlaceName.StayStart(
            [Point(100, 1), Point(200, 2, PlaceFlags.Tunnel | PlaceFlags.Vpn), Point(300, 2)], 2));
    }

    [Fact]
    public void StayStart_is_null_when_listen_has_another_router()
    {
        Assert.Null(PlaceName.StayStart([Point(100, 1)], 2));
        Assert.Null(PlaceName.StayStart([], 2));
    }

    [Fact]
    public void StayStart_stops_at_location_off()
    {
        Assert.Equal(300u, PlaceName.StayStart(
            [Point(100, 2), Point(200, 0, PlaceFlags.LocationOff), Point(300, 2)], 2));
    }
}
