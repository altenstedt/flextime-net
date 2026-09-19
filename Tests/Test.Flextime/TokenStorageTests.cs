using Flextime.Daemon;

namespace Test.Flextime;

public class TokenStorageTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-token-tests-{Guid.NewGuid():N}");

    private string TokenPath => Path.Combine(folder, "user");

    public TokenStorageTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task RoundTripsTokens()
    {
        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        var (accessToken, expires, refreshToken) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal("access token", accessToken);
        Assert.Equal("refresh token", refreshToken);
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task MissingFileIsEmpty()
    {
        var (accessToken, expires, refreshToken) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, accessToken);
        Assert.Equal(DateTimeOffset.MinValue, expires);
        Assert.Equal(string.Empty, refreshToken);
    }

    [Fact]
    public async Task TruncatedFileIsEmpty()
    {
        await File.WriteAllLinesAsync(TokenPath, ["access token"], TestContext.Current.CancellationToken);

        var (accessToken, expires, refreshToken) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, accessToken);
        Assert.Equal(DateTimeOffset.MinValue, expires);
        Assert.Equal(string.Empty, refreshToken);
    }

    [Fact]
    public async Task FileWithoutRefreshTokenReadsEmptyRefreshToken()
    {
        await File.WriteAllLinesAsync(TokenPath, ["access token", "2024-01-01T00:00:00.0000000+00:00"], TestContext.Current.CancellationToken);

        var (accessToken, expires, refreshToken) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal("access token", accessToken);
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"), expires);
        Assert.Equal(string.Empty, refreshToken);
    }

    [Fact]
    public async Task FileIsPrivateToUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
    }
}
