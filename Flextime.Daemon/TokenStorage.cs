using System.Globalization;
using System.Text;

namespace Flextime.Daemon;

public static class TokenStorage
{
    private static string DefaultPath => Path.Combine(Constants.MeasurementsFolder, "../user");

    public static async Task<(string accessToken, DateTimeOffset expires, string refreshToken)> Read(string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= DefaultPath;

        if (!File.Exists(path))
        {
            return (string.Empty, DateTimeOffset.MinValue, string.Empty);
        }
        
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken);

        // A file this cannot make sense of reads as logged out, so that
        // login can write over it and logout remove it.
        if (lines.Length < 2
            || !DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
        {
            return (string.Empty, DateTimeOffset.MinValue, string.Empty);
        }

        return (lines[0], expires, lines.Length == 2 ? string.Empty : lines[2]);
    }

    public static async Task Write(string accessToken, int expiresInSeconds, string refreshToken, string? path = null, CancellationToken cancellationToken = default)
    {
        path ??= DefaultPath;

        var expires = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(expiresInSeconds));

        string[] lines =
        [
            accessToken,
            expires.ToString("O"),
            refreshToken
        ];
        
        // The file contains tokens; keep it private to the user from the
        // moment it exists, then replace the old one in a single rename so
        // a reader never sees half of it.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            // Encoding.UTF8 writes a byte order mark, as File.WriteAllLines
            // always has; the file stays byte for byte what it was.
            await using (var writer = new StreamWriter(temporary, Encoding.UTF8, fileOptions))
            {
                foreach (var line in lines)
                {
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                }
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
            // A stray temporary file is private to the user; nothing reads it.
        }
    }

    /// <summary>Whether the token file is there, that is, whether logged in.</summary>
    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    /// <summary>Removes the token file; false when there was none.</summary>
    public static bool Delete(string? path = null)
    {
        path ??= DefaultPath;

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);

        return true;
    }
}
