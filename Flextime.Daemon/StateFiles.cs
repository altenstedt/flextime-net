namespace Flextime.Daemon;

/// <summary>
/// Small state files next to the measurement data.  Listen and recurring
/// sync publish their effective settings when they start, guarded by their
/// SingleInstance locks: the files are only trusted while the matching
/// lock is held, so stale content cannot lie.  The install manifest records
/// the schedule registered by the install command and is removed by
/// uninstall.
/// </summary>
public static class StateFiles
{
    private static readonly string Folder = Path.Combine(Constants.MeasurementsFolder, "..");

    public static readonly string ListenPath = Path.Combine(Folder, "listen.txt");
    public static readonly string SyncPath = Path.Combine(Folder, "sync.txt");
    public static readonly string InstallPath = Path.Combine(Folder, "install.txt");

    public static void Write(string path, params string[] lines)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(path, lines);
    }

    /// <summary>Returns null when the file is missing or unreadable.</summary>
    public static string[]? TryRead(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover manifest is harmless.
        }
    }
}
