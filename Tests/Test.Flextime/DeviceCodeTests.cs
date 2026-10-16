using System.Net;
using System.Net.Http.Json;
using Flextime.Daemon;

namespace Test.Flextime;

public class DeviceCodeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-device-code-tests-{Guid.NewGuid():N}");

    private string TokenPath => Path.Combine(folder, "user");

    private readonly List<TimeSpan> delays = [];

    public DeviceCodeTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private Task Delay(TimeSpan interval, CancellationToken cancellationToken)
    {
        delays.Add(interval);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SlowDownPollsFiveSecondsSlower()
    {
        var handler = new DeviceCodeHandler(
            DeviceCodeHandler.DeviceCode(interval: 5),
            DeviceCodeHandler.Error("authorization_pending"),
            DeviceCodeHandler.Error("slow_down"),
            DeviceCodeHandler.Error("authorization_pending"),
            DeviceCodeHandler.Tokens());

        var deviceCode = new DeviceCode(handler, TokenPath, Delay);

        await deviceCode.LogOn(TestContext.Current.CancellationToken);

        Assert.True(deviceCode.IsAuthenticated);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], delays);

        var (accessToken, _, refreshToken) = await TokenStorage.Read(TokenPath, TestContext.Current.CancellationToken);

        Assert.Equal("access token", accessToken);
        Assert.Equal("refresh token", refreshToken);
    }

    [Fact]
    public async Task MissingIntervalPollsEveryFiveSeconds()
    {
        var handler = new DeviceCodeHandler(
            DeviceCodeHandler.DeviceCode(interval: 0),
            DeviceCodeHandler.Error("authorization_pending"),
            DeviceCodeHandler.Tokens());

        var deviceCode = new DeviceCode(handler, TokenPath, Delay);

        await deviceCode.LogOn(TestContext.Current.CancellationToken);

        Assert.True(deviceCode.IsAuthenticated);
        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task FailedDeviceCodeRequestEndsWithoutLogin()
    {
        var handler = new DeviceCodeHandler(DeviceCodeHandler.Error("invalid_client"));

        var deviceCode = new DeviceCode(handler, TokenPath, Delay);

        await deviceCode.LogOn(TestContext.Current.CancellationToken);

        Assert.False(deviceCode.IsAuthenticated);
        Assert.Equal(1, handler.Count);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public async Task FailedDeviceCodeRequestWithoutJsonEndsWithoutLogin()
    {
        var handler = new DeviceCodeHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>down</html>")
        });

        var deviceCode = new DeviceCode(handler, TokenPath, Delay);

        await deviceCode.LogOn(TestContext.Current.CancellationToken);

        Assert.False(deviceCode.IsAuthenticated);
    }

    [Fact]
    public async Task OtherPollErrorEndsWithoutLogin()
    {
        var handler = new DeviceCodeHandler(
            DeviceCodeHandler.DeviceCode(interval: 5),
            DeviceCodeHandler.Error("expired_token"));

        var deviceCode = new DeviceCode(handler, TokenPath, Delay);

        await deviceCode.LogOn(TestContext.Current.CancellationToken);

        Assert.False(deviceCode.IsAuthenticated);
        Assert.Empty(delays);
    }
    [Fact]
    public async Task PollErrorWithoutJsonEndsWithoutLogin()
    {
        var handler = new DeviceCodeHandler(
            DeviceCodeHandler.DeviceCode(interval: 5),
            () => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("<html>bad request</html>")
            });

        var deviceCode = new DeviceCode(handler, TokenPath, Delay);

        await deviceCode.LogOn(TestContext.Current.CancellationToken);

        Assert.False(deviceCode.IsAuthenticated);
        Assert.Equal(2, handler.Count);
        Assert.Empty(delays);
        Assert.False(File.Exists(TokenPath));
    }
}

/// <summary>Answers each request with the next response, in order.</summary>
public class DeviceCodeHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    public int Count { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(responses[Count++]());
    }

    public static Func<HttpResponseMessage> DeviceCode(int interval) => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { message = "Go to the page.", device_code = "device code", expires_in = 900, interval })
    };

    public static Func<HttpResponseMessage> Error(string error) => () => new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = JsonContent.Create(new { error, error_description = "description" })
    };

    public static Func<HttpResponseMessage> Tokens() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { access_token = "access token", expires_in = 3600, refresh_token = "refresh token" })
    };
}
