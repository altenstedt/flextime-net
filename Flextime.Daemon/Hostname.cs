namespace Flextime.Daemon;

/// <summary>
/// What this computer calls itself, read fresh every time, so a renamed
/// machine reports its new name on the next sync.  On macOS the computer
/// name from System Settings; elsewhere the host name.
/// </summary>
public static class Hostname
{
    public static string Read()
    {
        if (OperatingSystem.IsMacOS() && SystemConfiguration.ReadComputerName() is { Length: > 0 } name)
        {
            return name.Trim();
        }

        return Environment.MachineName;
    }
}
