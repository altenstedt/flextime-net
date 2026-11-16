using System.Diagnostics;

namespace Flextime.Daemon;

/// <summary>
/// A per-user, machine-wide lock so that only one instance records or syncs
/// at a time.  The lock is a file held open with FileShare.None, which maps
/// to an exclusive lock on every platform and is released by the operating
/// system when the process exits, however it exits.
/// </summary>
public static class SingleInstance
{
    public static readonly string ListenLockPath = Path.Combine(Constants.MeasurementsFolder, "..", "listen.lock");
    public static readonly string SyncLockPath = Path.Combine(Constants.MeasurementsFolder, "..", "sync.lock");

    /// <summary>Whether another process currently holds the lock.</summary>
    public static bool IsHeld(string path)
    {
        var held = TryAcquire(path);

        if (held == null)
        {
            return true;
        }

        held.Dispose();

        return false;
    }

    /// <summary>
    /// Like <see cref="TryAcquire"/>, but tries again for a while before
    /// giving up.  <see cref="IsHeld"/> takes the lock for a moment to
    /// look at it, so a process starting in that moment would otherwise
    /// take a probe for a running instance.
    /// </summary>
    public static async Task<IDisposable?> TryAcquire(string path, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var retry = TimeSpan.FromMilliseconds(100);

        // The monotonic clock: a wall clock stepped back would make the
        // wait as long as the step.
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var held = TryAcquire(path);

            if (held != null || Stopwatch.GetElapsedTime(started) >= wait)
            {
                return held;
            }

            await Task.Delay(retry, cancellationToken);
        }
    }

    /// <summary>Returns null when another instance holds the lock.</summary>
    public static IDisposable? TryAcquire(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            PrivateFolder.Ensure(directory);
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
