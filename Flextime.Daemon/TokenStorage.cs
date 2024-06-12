using System.Text;

namespace Flextime.Daemon;

public static class TokenStorage
{
    public static async Task<(string accessToken, DateTimeOffset expires, string refreshToken)> Read(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Constants.MeasurementsFolder, "../user");

        if (!File.Exists(path))
        {
            return (string.Empty, DateTimeOffset.MinValue, string.Empty);
        }
        
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken);

        return lines.Length < 2 
            ? (string.Empty, DateTimeOffset.MinValue, string.Empty) 
            : lines.Length == 2
                ? (lines[0], DateTimeOffset.Parse(lines[1]), string.Empty)
                : (lines[0], DateTimeOffset.Parse(lines[1]), lines[2]);
    }

    public static async Task Write(string accessToken, int expiresInSeconds, string refreshToken, CancellationToken cancellationToken = default)
    {
        var expires = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(expiresInSeconds));

        string[] lines =
        [
            accessToken,
            expires.ToString("O"),
            refreshToken
        ];
        
        var path = Path.Combine(Constants.MeasurementsFolder, "../user");

        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken);
    }
}