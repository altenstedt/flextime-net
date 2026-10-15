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
    public async Task DeleteRemovesTokens()
    {
        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        Assert.True(TokenStorage.Delete(TokenPath));
        Assert.False(File.Exists(TokenPath));
        Assert.False(TokenStorage.Delete(TokenPath));
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

    [Fact]
    public async Task LooseFileIsReplacedPrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(TokenPath, "old", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenPath));
    }

    [Fact]
    public async Task WriteLeavesNoTemporaryFile()
    {
        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);
        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal(["user"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task FileIsWhatWriteAllLinesWrote()
    {
        await TokenStorage.Write("access token", 3600, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(TokenPath, TestContext.Current.CancellationToken);
        var expected = Path.Combine(folder, "expected");

        // The format from before the file was written through a temporary
        // file: UTF-8 with a byte order mark, every line terminated.
        await File.WriteAllLinesAsync(expected, lines, System.Text.Encoding.UTF8, TestContext.Current.CancellationToken);

        Assert.Equal(await File.ReadAllBytesAsync(expected, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(TokenPath, TestContext.Current.CancellationToken));
        Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(TokenPath, TestContext.Current.CancellationToken))[..3]);
    }

    [Fact]
    public async Task FileWithoutByteOrderMarkIsRead()
    {
        await File.WriteAllLinesAsync(TokenPath, ["access token", "2024-01-01T00:00:00.0000000+00:00", "refresh token"], new System.Text.UTF8Encoding(false), TestContext.Current.CancellationToken);

        var (accessToken, _, refreshToken) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal("access token", accessToken);
        Assert.Equal("refresh token", refreshToken);
    }
}
