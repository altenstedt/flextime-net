using Flextime.Daemon;

namespace Test.Flextime;

public class ServiceFilesTests
{
    private static readonly string[] Command = ["/opt/flextime/flextimed", "listen", "--time-zone", "Europe/Stockholm"];

    [Fact]
    public void LaunchAgentPlistContainsCommandAndLog()
    {
        var plist = ServiceFiles.LaunchAgentPlist("se.flextime.listen", Command, "/Users/x/Library/Logs/Flextime/listen.log", keepAlive: true, startInterval: null);

        Assert.Contains("<string>se.flextime.listen</string>", plist);
        Assert.Contains("<string>/opt/flextime/flextimed</string>", plist);
        Assert.Contains("<string>--time-zone</string>", plist);
        Assert.Contains("<string>Europe/Stockholm</string>", plist);
        Assert.Contains("<string>/Users/x/Library/Logs/Flextime/listen.log</string>", plist);
        Assert.Contains("<key>KeepAlive</key>", plist);
        // Restart on crash only, so that a clean exit does not loop.
        Assert.Contains("<key>SuccessfulExit</key>", plist);
        Assert.DoesNotContain("<key>StartInterval</key>", plist);
    }

    [Fact]
    public void LaunchAgentPlistWithIntervalDoesNotKeepAlive()
    {
        var plist = ServiceFiles.LaunchAgentPlist("se.flextime.sync", ["/opt/flextime/flextimed", "sync", "--once"], "/tmp/sync.log", keepAlive: false, startInterval: TimeSpan.FromMinutes(20));

        Assert.Contains("<key>StartInterval</key>", plist);
        Assert.Contains("<integer>1200</integer>", plist);
        Assert.DoesNotContain("<key>KeepAlive</key>", plist);
    }

    [Fact]
    public void LaunchAgentPlistEscapesXml()
    {
        var plist = ServiceFiles.LaunchAgentPlist("label", ["/Users/a & b/flextimed", "listen"], "/tmp/listen.log", keepAlive: true, startInterval: null);

        Assert.Contains("<string>/Users/a &amp; b/flextimed</string>", plist);
    }

    [Fact]
    public void SystemdListenServiceRestartsAndBindsToSession()
    {
        var unit = ServiceFiles.SystemdListenService(Command);

        Assert.Contains("ExecStart=/opt/flextime/flextimed listen --time-zone Europe/Stockholm", unit);
        Assert.Contains("Restart=on-failure", unit);
        // The monitor needs the session D-Bus bus, so the unit follows the
        // graphical session.
        Assert.Contains("PartOf=graphical-session.target", unit);
        Assert.Contains("WantedBy=graphical-session.target", unit);
    }

    [Fact]
    public void SystemdCommandQuotesSpaces()
    {
        var unit = ServiceFiles.SystemdSyncService(["/opt/my tools/flextimed", "sync", "--once"]);

        Assert.Contains("ExecStart=\"/opt/my tools/flextimed\" sync --once", unit);
        Assert.Contains("Type=oneshot", unit);
    }

    [Fact]
    public void SystemdSyncTimerRepeats()
    {
        var timer = ServiceFiles.SystemdSyncTimer(TimeSpan.FromMinutes(20));

        Assert.Contains("OnUnitActiveSec=1200", timer);
        Assert.Contains("WantedBy=timers.target", timer);
    }

    [Fact]
    public void WindowsTaskXmlRunsInteractively()
    {
        var xml = ServiceFiles.WindowsTaskXml("Flextime listen", @"MACHINE\user", [@"C:\Program Files\Flextime\flextimed.exe", "listen", "--time-zone", "Europe/Stockholm"], repetition: null, executionTimeLimit: null);

        Assert.Contains(@"<Command>C:\Program Files\Flextime\flextimed.exe</Command>", xml);
        Assert.Contains("<Arguments>listen --time-zone Europe/Stockholm</Arguments>", xml);
        Assert.Contains(@"<UserId>MACHINE\user</UserId>", xml);
        // Runs in the user session; a service could not see user input.
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
        // PT0S means no execution time limit.
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.DoesNotContain("<Repetition>", xml);
    }

    [Fact]
    public void WindowsTaskXmlRepeatsSync()
    {
        var xml = ServiceFiles.WindowsTaskXml("Flextime sync", @"MACHINE\user", [@"C:\Flextime\flextimed.exe", "sync", "--once"], repetition: TimeSpan.FromMinutes(20), executionTimeLimit: TimeSpan.FromHours(1));

        Assert.Contains("<Interval>PT20M</Interval>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT1H</ExecutionTimeLimit>", xml);
    }

    [Fact]
    public void WindowsTaskXmlQuotesArgumentsWithSpaces()
    {
        var xml = ServiceFiles.WindowsTaskXml("Flextime listen", @"MACHINE\user", [@"C:\flextimed.exe", "listen", "--time-zone", "My Zone & Co"], repetition: null, executionTimeLimit: null);

        Assert.Contains("<Arguments>listen --time-zone &quot;My Zone &amp; Co&quot;</Arguments>", xml);
    }
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("/opt/my tools/flextimed", "\"/opt/my tools/flextimed\"")]
    [InlineData("100%", "100%%")]
    [InlineData("$HOME", "$$HOME")]
    [InlineData("${HOME}", "$${HOME}")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("it's", "\"it's\"")]
    [InlineData(";", "\";\"")]
    [InlineData("", "\"\"")]
    [InlineData("/a b/%h $x", "\"/a b/%%h $$x\"")]
    public void SystemdArgumentEscapes(string argument, string expected)
    {
        Assert.Equal(expected, ServiceFiles.SystemdArgument(argument));
    }

    [Fact]
    public void SystemdServiceEscapesSpecifiers()
    {
        var unit = ServiceFiles.SystemdSyncService(["/home/a%nb/$USER/flextimed", "sync", "--once"]);

        Assert.Contains("ExecStart=/home/a%%nb/$$USER/flextimed sync --once", unit);
    }

    [Theory]
    [InlineData("/opt/flex\ntimed")]
    [InlineData("/opt/flex\rtimed")]
    [InlineData("/opt/flex\u001btimed")]
    public void ServiceFilesRejectControlCharacters(string path)
    {
        Assert.Throws<ArgumentException>(() => ServiceFiles.SystemdSyncService([path, "sync", "--once"]));
        Assert.Throws<ArgumentException>(() => ServiceFiles.WindowsTaskXml("d", "u", [path, "sync"], null, null));
        Assert.Throws<ArgumentException>(() => ServiceFiles.WindowsTaskXml("d", "u", [@"C:\f.exe", path], null, null));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData(@"a\\b c", "\"a\\\\b c\"")]
    public void WindowsArgumentQuotes(string argument, string expected)
    {
        Assert.Equal(expected, ServiceFiles.WindowsArgument(argument));
    }
}
