using System.Net;
using System.Net.Http.Json;
using Flextime.Daemon;

namespace Test.Flextime;

public class RefreshTokenDelegatingHandlerTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task Test()
    {
        var tokenHandler = new TokenHandler();

        var handlerOptions = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = "scope",
            ClientId = "client id 1",
            RefreshToken = "refresh token",
            WriteToStorage = false
        };
        
        var handler = new RefreshTokenDelegatingHandler(
            new MockHttpClientFactory(tokenHandler),
            handlerOptions)
        {
            InnerHandler = new OkHandler()
        };

        var httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://localhost");

        HttpRequestMessage message = new HttpRequestMessage();
        var response = await httpClient.SendAsync(message, TestContext.Current.CancellationToken);
        
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, tokenHandler.Count);
    }
    
    [Fact]
    public async Task TestInParallel()
    {
        var tokenHandler = new TokenHandler();

        var handlerOptions = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = "scope",
            ClientId = "client id 2",
            RefreshToken = "refresh token",
            WriteToStorage = false
        };

        var handler = new RefreshTokenDelegatingHandler(
            new MockHttpClientFactory(tokenHandler),
            handlerOptions)
        {
            InnerHandler = new OkHandler()
        };

        var httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://localhost");

        testOutputHelper.WriteLine(Environment.ProcessorCount.ToString());

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 1000
        };

        await Parallel.ForEachAsync(Enumerable.Repeat(0, 1000), options, async (_, cancellationToken) =>
        {
            var message = new HttpRequestMessage();
            await httpClient.SendAsync(message, cancellationToken);
        });
        
        Assert.Equal(1, tokenHandler.Count);
    }

    [Fact]
    public async Task TestStoredAccessTokenIsUsed()
    {
        var tokenHandler = new TokenHandler();
        var okHandler = new OkHandler();

        var handlerOptions = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = "scope",
            ClientId = "client id 3",
            AccessToken = "stored access token",
            Expires = DateTimeOffset.UtcNow.AddMinutes(30),
            RefreshToken = "refresh token",
            WriteToStorage = false
        };

        var handler = new RefreshTokenDelegatingHandler(
            new MockHttpClientFactory(tokenHandler),
            handlerOptions)
        {
            InnerHandler = okHandler
        };

        var httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://localhost");

        var response = await httpClient.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer stored access token", okHandler.Authorization);
        Assert.Equal(0, tokenHandler.Count);
    }

    [Fact]
    public async Task TestStoredAccessTokenWithinGraceIsRefreshed()
    {
        var tokenHandler = new TokenHandler();
        var okHandler = new OkHandler();

        var handlerOptions = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = "scope",
            ClientId = "client id 4",
            AccessToken = "stored access token",

            // Inside the handler's one minute grace, so it must not be used.
            Expires = DateTimeOffset.UtcNow.AddSeconds(30),
            RefreshToken = "refresh token",
            WriteToStorage = false
        };

        var handler = new RefreshTokenDelegatingHandler(
            new MockHttpClientFactory(tokenHandler),
            handlerOptions)
        {
            InnerHandler = okHandler
        };

        var httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://localhost");

        var response = await httpClient.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer access token", okHandler.Authorization);
        Assert.Equal(1, tokenHandler.Count);
        Assert.Equal("access token", handlerOptions.AccessToken);
    }

    [Fact]
    public async Task TestRefreshTokenIsKeptWhenNoneIsReturned()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"flextime-refresh-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            var tokenPath = Path.Combine(folder, "user");

            // Logged in: a process only refreshes tokens it read from the file.
            await TokenStorage.Write("old access token", 0, "refresh token", tokenPath, TestContext.Current.CancellationToken);

            var handlerOptions = new RefreshTokenDelegatingHandlerOptions
            {
                Scope = "scope",
                ClientId = "client id 5",
                RefreshToken = "refresh token",
                WriteToStorage = true,
                TokenPath = tokenPath
            };

            var handler = new RefreshTokenDelegatingHandler(
                new MockHttpClientFactory(new TokenHandler(refreshToken: null)),
                handlerOptions)
            {
                InnerHandler = new OkHandler()
            };

            var httpClient = new HttpClient(handler);
            httpClient.BaseAddress = new Uri("https://localhost");

            await httpClient.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken);

            var (accessToken, _, refreshToken) = await TokenStorage.Read(tokenPath, TestContext.Current.CancellationToken);

            Assert.Equal("access token", accessToken);
            Assert.Equal("refresh token", refreshToken);
            Assert.Equal("refresh token", handlerOptions.RefreshToken);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class RefreshTokenLogoutTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-logout-tests-{Guid.NewGuid():N}");

    private string TokenPath => Path.Combine(folder, "user");

    public RefreshTokenLogoutTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private (HttpClient client, TokenHandler tokens, OkHandler api, RefreshTokenDelegatingHandlerOptions options) Create(
        string clientId, string accessToken = "", DateTimeOffset? expires = null)
    {
        var tokens = new TokenHandler();
        var api = new OkHandler();

        var options = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = "scope",
            ClientId = clientId,
            AccessToken = accessToken,
            Expires = expires ?? DateTimeOffset.MinValue,
            RefreshToken = "refresh token",
            WriteToStorage = true,
            TokenPath = TokenPath
        };

        var handler = new RefreshTokenDelegatingHandler(new MockHttpClientFactory(tokens), options)
        {
            InnerHandler = api
        };

        return (new HttpClient(handler) { BaseAddress = new Uri("https://localhost") }, tokens, api, options);
    }

    [Fact]
    public async Task RefreshAfterLogoutDoesNotWriteTheTokenFile()
    {
        var (client, tokens, api, options) = Create("logout client 1");

        await TokenStorage.Write("old", 0, "refresh token", TokenPath, TestContext.Current.CancellationToken);
        TokenStorage.Delete(TokenPath);

        await Assert.ThrowsAsync<TokenRefreshException>(() =>
            client.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken));

        Assert.False(File.Exists(TokenPath));
        Assert.Equal(0, tokens.Count);
        Assert.Null(api.Authorization);
        Assert.True(options.LoggedOut);
        Assert.Equal(string.Empty, options.RefreshToken);
    }

    [Fact]
    public async Task HeldAccessTokenIsDroppedAfterLogout()
    {
        var (client, tokens, api, options) = Create(
            "logout client 2", "held access token", DateTimeOffset.UtcNow.AddMinutes(30));

        await TokenStorage.Write("held access token", 1800, "refresh token", TokenPath, TestContext.Current.CancellationToken);

        await client.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer held access token", api.Authorization);

        TokenStorage.Delete(TokenPath);

        await Assert.ThrowsAsync<TokenRefreshException>(() =>
            client.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken));

        // Stays logged out, even once a later login writes the file again:
        // this process holds no tokens to send.
        await TokenStorage.Write("new", 3600, "new refresh token", TokenPath, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TokenRefreshException>(() =>
            client.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken));

        var (accessToken, _, _) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal("new", accessToken);
        Assert.Equal(0, tokens.Count);
        Assert.Equal(string.Empty, options.AccessToken);
    }

    [Fact]
    public async Task NeverLoggedInSendsAnonymously()
    {
        var api = new OkHandler();

        var options = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = "scope",
            ClientId = "logout client 3",
            RefreshToken = string.Empty,
            WriteToStorage = true,
            TokenPath = TokenPath
        };

        var handler = new RefreshTokenDelegatingHandler(new MockHttpClientFactory(new TokenHandler()), options)
        {
            InnerHandler = api
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost") };

        var response = await client.SendAsync(new HttpRequestMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(api.Authorization);
        Assert.False(options.LoggedOut);
    }
}

public class TokenHandler(string? refreshToken = "refresh token") : DelegatingHandler
{
    public int Count { get; private set; }
    
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        
        var message = new HttpResponseMessage(HttpStatusCode.OK);

        message.Content = refreshToken == null
            ? JsonContent.Create(new { access_token = "access token", expires_in = 360 })
            : JsonContent.Create(new { access_token = "access token", expires_in = 360, refresh_token = refreshToken });

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        
        return message;
    }
}

public class OkHandler : DelegatingHandler
{
    public string? Authorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Authorization = request.Headers.Authorization?.ToString();

        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

public class MockHttpClientFactory(TokenHandler tokenHandler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(tokenHandler) { BaseAddress = new Uri("https://example.com") };
    }
}