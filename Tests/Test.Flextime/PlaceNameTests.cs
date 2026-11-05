using System.Text.Json;
using Flextime;
using Flextime.Daemon;

namespace Test.Flextime;

public class PlaceNameTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // Two days; the second has timestamps 1000, 1050 and 1100, and its
    // last run starts at 1050.
    private const string Ids = """
        {"items":[
          {"zone":"Europe/Stockholm","deltas":[500,100],"places":[[0,"aaaaaaaaaaaa",0]]},
          {"zone":"Europe/Berlin","deltas":[1000,50,50],"places":[[0,"aaaaaaaaaaaa",0],[1,"b9186da7f83b",3]]}
        ],"count":2,"continuation":""}
        """;

    [Theory]
    [InlineData(1050u)]
    [InlineData(1001u)]
    public void RouterAt_takes_the_last_run_when_it_began_in_the_stay(uint start)
    {
        Assert.Equal("b9186da7f83b", PlaceName.RouterAt(Json(Ids), start));
    }

    [Fact]
    public void RouterAt_is_null_when_the_server_is_behind_the_place()
    {
        Assert.Null(PlaceName.RouterAt(Json(Ids), 1101));
    }

    [Fact]
    public void RouterAt_is_null_when_activity_synced_but_places_did_not()
    {
        // Activity reaches 1100, past the arrival at 1060, but the last
        // run is still the router before, from 1050.
        Assert.Null(PlaceName.RouterAt(Json(Ids), 1060));
    }

    [Fact]
    public void RouterAt_counts_a_run_carried_into_a_new_day()
    {
        // A stay from the evening before: today's only run starts at its
        // first timestamp.
        var ids = """{"items":[{"zone":"UTC","deltas":[2000,60],"places":[[0,"b9186da7f83b",0]]}]}""";

        Assert.Equal("b9186da7f83b", PlaceName.RouterAt(Json(ids), 1500));
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
