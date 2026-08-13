namespace Flextime.Daemon;

/// <summary>
/// A per-user, machine-wide lock so that only one instance records or syncs
/// at a time.  The lock is a file held open with FileShare.None, which maps
/// to an exclusive lock on every platform and is released by the operating
/// system when the process exits, however it exits.
/// </summary>
public static class SingleInstance
{
    /// <summary>Returns null when another instance holds the lock.</summary>
    public static IDisposable? TryAcquire(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
