using System.Net;
using Flextime.Daemon;
using Polly.Timeout;

namespace Test.Flextime;

public class PrintInfoTests
{
    [Fact]
    public void A_timeout_is_not_reachable_in_plain_words()
    {
        var timeout = new TimeoutRejectedException("The operation didn't complete within the allowed timeout of '00:00:30'.", TimeSpan.FromSeconds(30));

        Assert.Equal("Not reachable, no answer within 30 seconds", PrintInfo.Failure(timeout));
        Assert.Equal("Not fetched, the server is not reachable", PrintInfo.NotFetched(timeout));
    }

    [Fact]
    public void A_refused_connection_says_why()
    {
        var refused = new HttpRequestException("Connection refused (127.0.0.1:9)");

        Assert.Equal("Not reachable, Connection refused (127.0.0.1:9)", PrintInfo.Failure(refused));
    }

    [Fact]
    public void A_server_that_answered_with_an_error_was_reachable()
    {
        var error = new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable).", null, HttpStatusCode.ServiceUnavailable);

        Assert.Equal("Server error 503", PrintInfo.Failure(error));
        Assert.Equal("Not fetched, the server answered 503", PrintInfo.NotFetched(error));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.DateTime);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    private static Sync.DayResult Day(int daysAgo, Sync.DayStatus status, TimeSpan? waitingFor = null) =>
        new(Today.AddDays(-daysAgo), $"day {daysAgo} [{status}]", status,
            waitingFor is { } wait ? (Now - wait).ToUnixTimeSeconds() : null);

    private static (string Line, List<string> Rows) ServerData(int? latest, params Sync.DayResult[] days) =>
        PrintInfo.ServerData(days, Now, Grace, latest);

    [Fact]
    public void Days_in_sync_are_one_line()
    {
        var (line, rows) = ServerData(null, Day(2, Sync.DayStatus.InSync), Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.InSync));

        Assert.Equal("3 days in sync", line);
        Assert.Empty(rows);
    }

    [Fact]
    public void Today_waiting_for_the_next_sync_is_in_sync()
    {
        var (line, rows) = ServerData(null, Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.CanSync, TimeSpan.FromMinutes(2)));

        Assert.Equal("2 days in sync", line);
        Assert.Empty(rows);

        (line, _) = ServerData(null, Day(0, Sync.DayStatus.LocalOnly, TimeSpan.FromMinutes(14)));

        Assert.Equal("1 day in sync", line);
    }

    [Fact]
    public void Today_waiting_past_the_grace_is_out_of_sync()
    {
        var (line, rows) = ServerData(null, Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.CanSync, TimeSpan.FromHours(7)));

        Assert.Equal("1 of 2 days out of sync", line);
        Assert.Equal(["day 0 [CanSync]"], rows);
    }

    [Fact]
    public void Yesterday_just_after_midnight_still_waits()
    {
        var (line, _) = ServerData(null, Day(1, Sync.DayStatus.CanSync, TimeSpan.FromMinutes(5)));

        Assert.Equal("1 day in sync", line);
    }

    [Fact]
    public void A_mismatch_with_nothing_newer_is_out_of_sync()
    {
        // Minutes missing in the middle of the day: nothing is newer
        // than the server's last, so nothing will arrive by waiting.
        var (line, _) = ServerData(null, Day(0, Sync.DayStatus.CanSync));

        Assert.Equal("1 of 1 day out of sync", line);
    }

    [Fact]
    public void An_earlier_day_not_synced_is_out_of_sync()
    {
        var (line, rows) = ServerData(null, Day(2, Sync.DayStatus.InSync), Day(1, Sync.DayStatus.CanSync, TimeSpan.FromHours(20)), Day(0, Sync.DayStatus.CanSync, TimeSpan.FromMinutes(1)));

        Assert.Equal("1 of 3 days out of sync", line);
        Assert.Equal(["day 1 [CanSync]"], rows);
    }

    [Fact]
    public void Only_the_days_out_of_sync_are_listed()
    {
        var (line, rows) = ServerData(null, Day(2, Sync.DayStatus.ServerHasMore), Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.Refused));

        Assert.Equal("2 of 3 days out of sync", line);
        Assert.Equal(["day 2 [ServerHasMore]", "day 0 [Refused]"], rows);
    }

    [Fact]
    public void Days_asked_for_are_listed()
    {
        var (line, rows) = ServerData(2, Day(2, Sync.DayStatus.InSync), Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.CanSync, TimeSpan.FromMinutes(1)));

        Assert.Equal("3 days in sync", line);
        Assert.Equal(["day 1 [InSync]", "day 0 [CanSync]"], rows);

        Assert.Equal(3, ServerData(0, Day(2, Sync.DayStatus.InSync), Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.InSync)).Rows.Count);
        Assert.Equal(2, ServerData(9, Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.InSync)).Rows.Count);
    }

    [Fact]
    public void Latest_days_come_with_older_days_out_of_sync()
    {
        var (line, rows) = ServerData(2,
            Day(3, Sync.DayStatus.ServerHasMore), Day(2, Sync.DayStatus.InSync), Day(1, Sync.DayStatus.InSync), Day(0, Sync.DayStatus.CanSync, TimeSpan.FromMinutes(1)));

        Assert.Equal("1 of 4 days out of sync", line);
        Assert.Equal(["day 3 [ServerHasMore]", "day 1 [InSync]", "day 0 [CanSync]"], rows);
    }

    [Fact]
    public void No_local_days_say_so()
    {
        Assert.Equal("No local days to compare", ServerData(null).Line);
    }

    [Fact]
    public void The_grace_is_three_syncs_and_at_least_a_quarter_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), PrintInfo.Grace(new StatusSyncDataContract("scheduled", "00:01:00", "2026-10-28")));
        Assert.Equal(TimeSpan.FromMinutes(30), PrintInfo.Grace(new StatusSyncDataContract("running", "00:10:00", null)));
        Assert.Equal(TimeSpan.FromMinutes(15), PrintInfo.Grace(new StatusSyncDataContract("notScheduled", null, null)));
    }

    [Fact]
    public void The_json_names_stay_put()
    {
        var json = PrintInfo.ToJson(new StatusDataContract(
            new StatusListenDataContract("running", "Europe/Stockholm", null, Now),
            new StatusSyncDataContract("scheduled", "00:01:00", "2026-10-28"),
            new StatusSignInDataContract("yes", "Name", "name@example.com"),
            new StatusServerDataContract(2, 1, null, [new StatusDayDataContract(Today, "canSync", false)]),
            new StatusLocationDataContract(true, false),
            "Martin’s Mac",
            "f204899922822897",
            "Europe/Stockholm",
            new StatusLocalDataContract(2, Today.AddDays(-1), Today, 1024),
            "7.23.0+abcdef0",
            "7.18.5+12ae77c3c3aa3c263535a451dcbb684b1e35cf2e",
            null,
            "https://api.flextime.inhill.com/",
            "/Users/x/Library/Application Support/Flextime/measurements",
            null));

        // One line in the output; broken here at the top-level names.
        const string expected = """
            {"listen":{"state":"running","zone":"Europe/Stockholm","stoppedOn":null,"lastMinute":"2026-11-12T15:00:00+01:00"},
            "sync":{"state":"scheduled","every":"00:01:00","installedOn":"2026-10-28"},
            "signedIn":{"state":"yes","name":"Name","email":"name@example.com"},
            "serverData":{"days":2,"outOfSync":1,"error":null,"items":[{"date":"2026-11-12","status":"canSync","inSync":false}]},
            "location":{"on":true,"clearPending":false},
            "hostname":"Martin’s Mac",
            "computerId":"f204899922822897",
            "timeZone":"Europe/Stockholm",
            "localData":{"days":2,"first":"2026-11-11","last":"2026-11-12","bytes":1024},
            "clientVersion":"7.23.0+abcdef0",
            "serverVersion":"7.18.5+12ae77c3c3aa3c263535a451dcbb684b1e35cf2e",
            "serverError":null,
            "serverUrl":"https://api.flextime.inhill.com/",
            "measurements":"/Users/x/Library/Application Support/Flextime/measurements",
            "logs":null}
            """;

        Assert.Equal(expected.ReplaceLineEndings(""), json);
    }

    [Fact]
    public void The_last_minute_has_a_date_only_before_today()
    {
        var now = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal("08:04", PrintInfo.FormatLastMinute(now.AddMinutes(-56), now));
        Assert.Equal("2026-11-11 16:13", PrintInfo.FormatLastMinute(new DateTimeOffset(2026, 11, 11, 16, 13, 0, TimeSpan.FromHours(1)), now));
    }

    [Fact]
    public void The_listen_zone_is_named_only_when_it_differs()
    {
        Assert.Equal("Europe/Stockholm", PrintInfo.TimeZoneStatus("Europe/Stockholm", "Europe/Stockholm", "Europe/Stockholm"));
        Assert.Equal("Europe/Stockholm", PrintInfo.TimeZoneStatus("Europe/Stockholm", "Europe/Stockholm", null));
        Assert.Equal("Europe/London, listen records in Europe/Stockholm",
            PrintInfo.TimeZoneStatus("Europe/London", "Europe/London", "Europe/Stockholm"));

        // Windows shows where the IANA id came from; listen writes the id.
        const string windows = "Europe/Stockholm (converted from W. Europe Standard Time)";
        Assert.Equal(windows, PrintInfo.TimeZoneStatus(windows, "Europe/Stockholm", "Europe/Stockholm"));
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(false, "1", false)]
    [InlineData(false, "0", false)]
    [InlineData(true, null, false)]
    public void No_color_follows_the_option_and_the_variable(bool option, string? variable, bool wanted)
    {
        Assert.Equal(wanted, PrintInfo.ColourWanted(option, variable));
    }

    [Fact]
    public void The_server_hash_is_as_short_as_the_client_hash()
    {
        Assert.Equal("7.18.5+12ae77c", PrintInfo.ShortVersion("7.18.5+12ae77c3c3aa3c263535a451dcbb684b1e35cf2e"));
        Assert.Equal("7.22.5+e1b7b15", PrintInfo.ShortVersion("7.22.5+e1b7b15"));
        Assert.Equal("7.18.5", PrintInfo.ShortVersion("7.18.5"));
    }

    [Fact]
    public void Paths_under_home_start_with_a_tilde()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps full paths.");

        Assert.Equal("~/Library/Logs/Flextime", PrintInfo.HomeRelative("/Users/martin/Library/Logs/Flextime", "/Users/martin"));
        Assert.Equal("/Users/martina/x", PrintInfo.HomeRelative("/Users/martina/x", "/Users/martin"));
        Assert.Equal("/var/x", PrintInfo.HomeRelative("/var/x", "/Users/martin"));
        Assert.Equal("/var/x", PrintInfo.HomeRelative("/var/x", "/"));
    }
}
