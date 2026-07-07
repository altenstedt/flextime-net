using System.Globalization;

namespace Flextime.Daemon;

internal static class Icu
{
    /// <summary>
    /// Whether the runtime uses ICU for globalization, which is needed for
    /// cross-platform time zone handling on Windows.
    /// https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu
    /// </summary>
    public static bool IsInUse()
    {
        var sortVersion = CultureInfo.InvariantCulture.CompareInfo.Version;
        var bytes = sortVersion.SortId.ToByteArray();
        var version = bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0];

        return version != 0 && version == sortVersion.FullVersion;
    }
}
