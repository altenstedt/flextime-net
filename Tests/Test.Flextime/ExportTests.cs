using Flextime.Daemon;

namespace Test.Flextime;

public class ExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static Func<string, Task<string>> Server(Dictionary<string, string> answers, List<string>? asked = null) =>
        uri =>
        {
            asked?.Add(uri);

            return Task.FromResult(answers[uri]);
        };

    [Fact]
    public async Task Exports_every_computer_with_its_days_hints_and_settings()
    {
        var asked = new List<string>();

        var export = await Export.Build(Server(new()
        {
            ["/computers?api-version=1.7"] = """{ "items": [ { "id": "a1", "name": "Work", "hostnames": [ { "hostname": "mac" } ] }, { "id": "b2" } ] }""",
            ["/ids?api-version=1.5&id=a1"] = """{ "items": [ { "zone": "Europe/Stockholm", "timestamps": [1000, 1060], "places": [ [1000, "95313127c39c", 2] ] } ], "count": 1, "continuation": "" }""",
            ["/ids?api-version=1.5&id=b2"] = """{ "items": [], "count": 0, "continuation": "" }""",
            ["/places/hints?api-version=1.5&id=a1&id=b2"] = """{ "items": [ { "router": "95313127c39c", "hint": "lan", "updated": 1000 } ] }""",
            ["/store/profiles?api-version=1.0"] = """{ "v": 1 }""",
            ["/store/marks?api-version=1.0"] = "null",
            ["/store/places?api-version=1.0"] = """{ "names": {} }""",
        }, asked), Now, "7.15.0");

        Assert.Equal("2026-10-21T08:00:00.0000000+00:00", export["exported"]!.GetValue<string>());
        Assert.Equal("flextimed 7.15.0", export["client"]!.GetValue<string>());

        Assert.Equal(2, export["computers"]!.AsArray().Count);
        Assert.Equal("mac", export["computers"]![0]!["hostnames"]![0]!["hostname"]!.GetValue<string>());

        var activity = export["activity"]!.AsArray();
        Assert.Equal("a1", activity[0]!["computer"]!.GetValue<string>());
        Assert.Equal(1060, activity[0]!["days"]![0]!["timestamps"]![1]!.GetValue<long>());
        Assert.Equal(2, activity[0]!["days"]![0]!["places"]![0]![2]!.GetValue<int>());
        Assert.Empty(activity[1]!["days"]!.AsArray());

        Assert.Equal("lan", export["hints"]![0]!["hint"]!.GetValue<string>());

        Assert.Equal(1, export["settings"]!["profiles"]!["v"]!.GetValue<int>());
        Assert.True(export["settings"]!.AsObject().ContainsKey("marks"));
        Assert.Null(export["settings"]!["marks"]);

        // Every answer is read once, the activity one computer at a time.
        Assert.Equal(7, asked.Count);
    }

    [Fact]
    public async Task A_user_without_computers_gets_settings_and_no_hints_request()
    {
        var export = await Export.Build(Server(new()
        {
            ["/computers?api-version=1.7"] = """{ "items": [] }""",
            ["/store/profiles?api-version=1.0"] = "null",
            ["/store/marks?api-version=1.0"] = "null",
            ["/store/places?api-version=1.0"] = "null",
        }), Now, "7.15.0");

        Assert.Empty(export["computers"]!.AsArray());
        Assert.Empty(export["activity"]!.AsArray());
        Assert.Empty(export["hints"]!.AsArray());
    }

    [Fact]
    public async Task Computer_ids_are_escaped_in_the_query()
    {
        var asked = new List<string>();

        await Export.Build(Server(new()
        {
            ["/computers?api-version=1.7"] = """{ "items": [ { "id": "a b" } ] }""",
            ["/ids?api-version=1.5&id=a%20b"] = """{ "items": [] }""",
            ["/places/hints?api-version=1.5&id=a%20b"] = """{ "items": [] }""",
            ["/store/profiles?api-version=1.0"] = "null",
            ["/store/marks?api-version=1.0"] = "null",
            ["/store/places?api-version=1.0"] = "null",
        }, asked), Now, "7.15.0");

        Assert.Contains("/ids?api-version=1.5&id=a%20b", asked);
    }
}
