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

        return lines.Length < 2
            ? (string.Empty, DateTimeOffset.MinValue, string.Empty)
            : lines.Length == 2
                ? (lines[0], ParseExpires(lines[1]), string.Empty)
                : (lines[0], ParseExpires(lines[1]), lines[2]);

        static DateTimeOffset ParseExpires(string text) =>
            DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
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
        
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            // The file contains tokens; keep it private to the user.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}