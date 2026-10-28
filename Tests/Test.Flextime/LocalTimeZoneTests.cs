using Flextime;
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

    [Fact]
    public void The_macOS_zone_folder_names_the_zone_the_link_points_at()
    {
        // Fails if .NET ever resolves the link differently, or fixes
        // dotnet/runtime#103196 in a way that TZDIR now gets in the way of.
        var target = OperatingSystem.IsMacOS() ? new FileInfo("/etc/localtime").LinkTarget : null;

        Assert.SkipUnless(TimeZones.PointsIntoMacZones(null, target), "Not the macOS zone layout.");

        var saved = Environment.GetEnvironmentVariable("TZDIR");
        var savedTz = Environment.GetEnvironmentVariable("TZ");

        try
        {
            Environment.SetEnvironmentVariable("TZDIR", null);
            Environment.SetEnvironmentVariable("TZ", null);

            TimeZones.UseMacZoneDirectory();

            Assert.Equal(target![(TimeZones.MacZoneDirectory.Length + 1)..], TimeZoneInfo.Local.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZDIR", saved);
            Environment.SetEnvironmentVariable("TZ", savedTz);
            TimeZoneInfo.ClearCachedData();
        }
    }
}
