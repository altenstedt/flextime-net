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
        
        var handler = new RefreshTokenDelegatingHandler(
            string.Empty,
            new HttpClient(tokenHandler) { BaseAddress = new Uri("https://example.com") }, 
            "client id 1", 
            "scope",
            writeToStorage: false)
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
        
        var handler = new RefreshTokenDelegatingHandler(
            string.Empty,
            new HttpClient(tokenHandler) { BaseAddress = new Uri("https://example.com") }, 
            "client id 2", 
            "scope",
            writeToStorage: false)
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