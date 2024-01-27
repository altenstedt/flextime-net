using System.Net;
using System.Net.Http.Json;

namespace Inhill.Flextime.Sync;

public class DeviceCode(Options options)
{
    private DateTime expires = DateTime.MinValue;

    public bool IsLoggedOn => expires > DateTime.Now;

    public string? AccessToken { get; private set; } = null;

    public async Task LogOn()
    {
        var httpClient = new HttpClient { BaseAddress = new Uri($"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/") };

        KeyValuePair<string, string>[] collection = [ 
            new KeyValuePair<string, string>("client_id", options.ClientId),
            new KeyValuePair<string, string>("scope", "openid")
        ];
        
        var responseMessage = await httpClient.PostAsync("devicecode", new FormUrlEncodedContent(collection));

        var deviceCodeResponse = await responseMessage.Content.ReadFromJsonAsync<DeviceCodeResponse>();

        var deviceCodeExpires = DateTime.Now.Add(TimeSpan.FromSeconds(deviceCodeResponse!.expires_in));
        
        Console.WriteLine(deviceCodeResponse?.message);

        KeyValuePair<string, string>[] pullCollection =
        [
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            new KeyValuePair<string, string>("client_id", options.ClientId),
            new KeyValuePair<string, string>("device_code", deviceCodeResponse.device_code)
        ];

        do
        {
            var pollResponseMessage = await httpClient.PostAsync("token", new FormUrlEncodedContent(pullCollection));

            var pollResponse = await pollResponseMessage.Content.ReadFromJsonAsync<PollResponse>();

            if (pollResponseMessage.StatusCode == HttpStatusCode.BadRequest)
            {
                if (pollResponse.error == "authorization_pending")
                {
                    await Task.Delay(TimeSpan.FromSeconds(deviceCodeResponse.interval));
                }
            } else if (pollResponseMessage.StatusCode == HttpStatusCode.OK)
            {
                AccessToken = pollResponse.access_token;
                expires = DateTime.Now.Add(TimeSpan.FromSeconds(pollResponse.expires_in));
                
                Console.WriteLine($"Session ends {expires:t}");
                return;
            }
            else
            {
                return;
            }
        } while (deviceCodeExpires > DateTime.Now);
    }
}

public record DeviceCodeResponse(string message, string device_code, int expires_in, int interval);

public record PollResponse(string error, string access_token, int expires_in);
