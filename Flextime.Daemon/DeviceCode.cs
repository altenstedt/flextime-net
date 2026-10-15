using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Flextime.Daemon;

/// <param name="handler">Sends the requests; null for the network, set by tests.</param>
/// <param name="tokenPath">Where the tokens are kept; null is the token file.</param>
/// <param name="delay">Waits between polls; null for Task.Delay, set by tests.</param>
public class DeviceCode(
    HttpMessageHandler? handler = null,
    string? tokenPath = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    // RFC 8628: poll every 5 seconds unless told otherwise, and 5 seconds
    // slower each time the server answers slow_down.
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    private DateTimeOffset expires = DateTimeOffset.MinValue;

    private string? accessToken;

    private string? refreshToken;

    public bool IsAuthenticated => !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken);
    
    public async Task Initialize()
    {
        (accessToken, expires, refreshToken) = await TokenStorage.Read(tokenPath);
    }
    
    public async Task LogOn(CancellationToken cancellationToken)
    {
        var httpClient = handler == null
            ? new HttpClient { BaseAddress = Constants.AuthorityUri }
            : new HttpClient(handler, disposeHandler: false) { BaseAddress = Constants.AuthorityUri };

        delay ??= Task.Delay;

        KeyValuePair<string, string>[] collection = [
            new KeyValuePair<string, string>("client_id", Constants.ClientId),
            new KeyValuePair<string, string>("scope", Constants.Scope)
        ];
        
        var responseMessage = await httpClient.PostAsync("deviceCode", new FormUrlEncodedContent(collection), cancellationToken);

        if (!responseMessage.IsSuccessStatusCode)
        {
            // An error body, if any, has the same shape as a poll error.
            var error = await TryReadError(responseMessage, cancellationToken);

            Console.Error.WriteLine($"Could not start the login: {(int)responseMessage.StatusCode} {responseMessage.ReasonPhrase}. {error}".TrimEnd());

            return;
        }

        var deviceCodeResponse = await responseMessage.Content.ReadFromJsonAsync(DeviceCodeResponseSourceGenerationContext.Default.DeviceCodeResponse, cancellationToken: cancellationToken);

        if (deviceCodeResponse == null || string.IsNullOrEmpty(deviceCodeResponse.device_code))
        {
            Console.Error.WriteLine("Could not start the login: the server sent no device code.");

            return;
        }

        var deviceCodeExpires = DateTime.Now.Add(TimeSpan.FromSeconds(deviceCodeResponse.expires_in));

        var interval = deviceCodeResponse.interval > 0
            ? TimeSpan.FromSeconds(deviceCodeResponse.interval)
            : DefaultInterval;

        Console.WriteLine(deviceCodeResponse.message);

        KeyValuePair<string, string>[] pullCollection =
        [
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            new KeyValuePair<string, string>("client_id", Constants.ClientId),
            new KeyValuePair<string, string>("device_code", deviceCodeResponse.device_code)
        ];

        do
        {
            var pollResponseMessage = await httpClient.PostAsync("token", new FormUrlEncodedContent(pullCollection), cancellationToken);

            var pollResponse = await pollResponseMessage.Content.ReadFromJsonAsync(PollResponseSourceGenerationContext.Default.PollResponse, cancellationToken: cancellationToken);

            if (pollResponseMessage.StatusCode == HttpStatusCode.BadRequest)
            {
                if (pollResponse?.error == "authorization_pending")
                {
                    await delay(interval, cancellationToken);
                }
                else if (pollResponse?.error == "slow_down")
                {
                    // Still pending, but polled too often; slower from now on.
                    interval += SlowDownStep;

                    await delay(interval, cancellationToken);
                }
                else
                {
                    Console.WriteLine($"Unexpected response {pollResponse?.error}. {pollResponse?.error_description}");

                    return;
                }
            } 
            else if (pollResponseMessage.StatusCode == HttpStatusCode.OK)
            {
                if (pollResponse == null)
                {
                    throw new InvalidOperationException("Device code poll response is null.");
                }

                await TokenStorage.Write(
                    pollResponse.access_token,
                    pollResponse.expires_in,
                    pollResponse.refresh_token,
                    tokenPath);

                accessToken = pollResponse.access_token;
                refreshToken = pollResponse.refresh_token;
                expires = DateTimeOffset.Now.AddSeconds(pollResponse.expires_in);

                Console.WriteLine($"Session ends {expires:t}");
                return;
            }
            else
            {
                Console.WriteLine($"Unexpected status code {pollResponseMessage.StatusCode}");
                return;
            }
        } while (deviceCodeExpires > DateTime.Now);
    }

    private static async Task<string> TryReadError(HttpResponseMessage responseMessage, CancellationToken cancellationToken)
    {
        try
        {
            var error = await responseMessage.Content.ReadFromJsonAsync(PollResponseSourceGenerationContext.Default.PollResponse, cancellationToken: cancellationToken);

            return $"{error?.error} {error?.error_description}".Trim();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            // Not JSON, or not this shape; the status says enough.
            return string.Empty;
        }
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
internal record DeviceCodeResponse(string message, string device_code, int expires_in, int interval);

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
internal record PollResponse(
    string access_token,
    int expires_in,
    string refresh_token,
    string error,
    string error_description);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(DeviceCodeResponse))]
internal partial class DeviceCodeResponseSourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(PollResponse))]
internal partial class PollResponseSourceGenerationContext : JsonSerializerContext;
