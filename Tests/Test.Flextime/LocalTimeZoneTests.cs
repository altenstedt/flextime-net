using Flextime.Monitor;

namespace Test.Flextime;

// Changes TZ for the whole process, so nothing else may run meanwhile.
[CollectionDefinition(nameof(LocalTimeZoneTests), DisableParallelization = true)]
public class LocalTimeZoneCollection;

[Collection(nameof(LocalTimeZoneTests))]
public class LocalTimeZoneTests
{
    [Fact]
    public void A_changed_local_zone_is_read_again()
    {
        // Windows takes its zone from the registry, not TZ.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "TZ does not set the zone on Windows.");

        var saved = Environment.GetEnvironmentVariable("TZ");

        try
        {
            Environment.SetEnvironmentVariable("TZ", "Europe/Stockholm");
            Assert.Equal("Europe/Stockholm", UserInputMonitor.GetTimeZoneInfo());

            Environment.SetEnvironmentVariable("TZ", "Asia/Tokyo");
            Assert.Equal("Asia/Tokyo", UserInputMonitor.GetTimeZoneInfo());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", saved);
            TimeZoneInfo.ClearCachedData();
        }
    }
}
