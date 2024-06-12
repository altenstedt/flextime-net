using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Flextime.Daemon;

public class DeviceCode
{
    private DateTimeOffset expires = DateTimeOffset.MinValue;

    private static string TenantId => "6b3c1467-664f-4edb-8328-43b7687d0366";

    private static string ClientId => "506c78bf-3e07-4caa-b20c-0deec3356d4d";

    private static string Scope =>
        "openid offline_access api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Read api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Write";

    private string? accessToken;

    private string? refreshToken;

    public bool IsAuthenticated => !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken);
    
    public async Task Initialize()
    {
        (accessToken, expires, refreshToken) = await TokenStorage.Read();
    }
    
    public async Task LogOn(CancellationToken cancellationToken)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/") };

        KeyValuePair<string, string>[] collection = [ 
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("scope", Scope)
        ];
        
        var responseMessage = await httpClient.PostAsync("deviceCode", new FormUrlEncodedContent(collection), cancellationToken);

        var deviceCodeResponse = await responseMessage.Content.ReadFromJsonAsync(DeviceCodeResponseSourceGenerationContext.Default.DeviceCodeResponse, cancellationToken: cancellationToken);

        var deviceCodeExpires = DateTime.Now.Add(TimeSpan.FromSeconds(deviceCodeResponse!.expires_in));
        
        Console.WriteLine(deviceCodeResponse.message);

        KeyValuePair<string, string>[] pullCollection =
        [
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            new KeyValuePair<string, string>("client_id", ClientId),
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
                    await Task.Delay(TimeSpan.FromSeconds(deviceCodeResponse.interval), cancellationToken);
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
                    pollResponse.refresh_token);
                
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
