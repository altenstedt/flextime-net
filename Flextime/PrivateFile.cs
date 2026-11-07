namespace Flextime;

/// <summary>
/// Files only the user may read, written whole or not at all.
/// </summary>
public static class PrivateFile
{
    /// <summary>
    /// Writes <paramref name="path"/> through a new temporary file next to
    /// it, replaced in one rename.  A reader sees the old file or the new
    /// one, never half of one, and a crash midway leaves the old file as
    /// it was.
    /// </summary>
    /// <remarks>
    /// The temporary file is created exclusively, private to the user
    /// from the moment it exists whatever the umask, so nothing planted
    /// at its name is followed.  The rename replaces whatever is at
    /// <paramref name="path"/>, a symbolic link included, rather than
    /// writing through it, and the file it leaves there has the new
    /// mode, not the old file's.  On Windows the file takes the folder's
    /// permissions, as any other.
    /// </remarks>
    public static void Replace(string path, Action<Stream> write)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, fileOptions))
            {
                write(stream);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file has a name nothing reads.
        }
    }
}
