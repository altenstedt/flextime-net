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