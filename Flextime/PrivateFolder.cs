namespace Flextime;

/// <summary>
/// The Flextime data folders hold what the computer did all day, and the
/// sign-in; on Linux the default mode would let every user read them.
/// </summary>
public static class PrivateFolder
{
    private const UnixFileMode UserOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Creates <paramref name="path"/> readable by the user only, and
    /// tightens it if it already exists looser — a folder from before
    /// this was done.  Missing parents get the default mode; every
    /// command makes the Flextime folder itself private first, so that is
    /// never one of them.  On macOS the parent, Application Support, is
    /// private already.
    /// </summary>
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);

            return;
        }

        var directory = Directory.CreateDirectory(path, UserOnly);

        if ((directory.UnixFileMode & GroupOrOther) == 0)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory.FullName, UserOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another user's folder; it works as it always has.
        }
    }
}
