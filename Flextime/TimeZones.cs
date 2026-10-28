using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Flextime;

public static class TimeZones
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> zonesById = new();

    // dotnet/runtime#103196: on macOS /etc/localtime points into this
    // folder, which .NET does not know as a zone folder, so it names the
    // local zone by matching file contents instead, and Stockholm comes
    // out as Atlantic/Jan_Mayen.  Naming the folder in TZDIR is enough.
    public const string MacZoneDirectory = "/var/db/timezone/zoneinfo";

    // Only when the link points into that folder: another layout, after
    // an update, and a TZDIR someone set are left alone.  Harmless once
    // .NET is fixed, since it then finds the same files under the same
    // names.
    public static bool PointsIntoMacZones(string? tzdir, string? localtimeTarget) =>
        string.IsNullOrEmpty(tzdir)
        && localtimeTarget != null
        && localtimeTarget.StartsWith(MacZoneDirectory + "/", StringComparison.Ordinal);

    // Called first thing, before anything has read the local zone.
    public static void UseMacZoneDirectory()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string? target;

        try
        {
            target = new FileInfo("/etc/localtime").LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!PointsIntoMacZones(Environment.GetEnvironmentVariable("TZDIR"), target) || !File.Exists(target))
        {
            return;
        }

        Environment.SetEnvironmentVariable("TZDIR", MacZoneDirectory);
        TimeZoneInfo.ClearCachedData();
    }

    public static TimeZoneInfo Get(string id) =>
        zonesById.GetOrAdd(id, TimeZoneInfo.FindSystemTimeZoneById);

    public static bool TryGet(string id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        if (zonesById.TryGetValue(id, out zone))
        {
            return true;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone))
        {
            zonesById.TryAdd(id, zone);

            return true;
        }

        return false;
    }

    /// <summary>The wall clock date of an instant in a zone.</summary>
    public static DateOnly DateAt(DateTimeOffset instant, string zoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Get(zoneId)).Date);
}
