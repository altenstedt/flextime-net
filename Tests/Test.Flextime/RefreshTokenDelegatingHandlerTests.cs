using System.Net;
using System.Net.Http.Json;
using Flextime.Daemon;
using Xunit.Abstractions;

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
            RefreshToken = string.Empty,
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
        var response = await httpClient.SendAsync(message);
        
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
            RefreshToken = string.Empty,
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
}

public class TokenHandler : DelegatingHandler
{
    public int Count { get; private set; }
    
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        
        var message = new HttpResponseMessage(HttpStatusCode.OK);

        message.Content = JsonContent.Create(new { access_token = "access token", expires_in = 360, refresh_token = "refresh token" });

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        
        return message;
    }
}

public class OkHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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