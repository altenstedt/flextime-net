using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Inhill.Flextime.Sync;

public class DeviceCode(Options options)
{
    private DateTimeOffset expires = DateTimeOffset.MinValue;

    public bool IsLoggedOn => expires > DateTime.Now;

    public string? AccessToken { get; private set; }

    public async Task Initialize()
    {
        var result = await ReadFromFile();

        AccessToken = result.accessToken;
        expires = result.expires;
    }
    
    public async Task LogOn()
    {
        var httpClient = new HttpClient { BaseAddress = new Uri($"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/") };

        KeyValuePair<string, string>[] collection = [ 
            new KeyValuePair<string, string>("client_id", options.ClientId),
            new KeyValuePair<string, string>("scope", "openid offline_access api://80ae8503-ef51-4443-8f05-e677f52a56d1/Flextime.User.Read api://80ae8503-ef51-4443-8f05-e677f52a56d1/Flextime.User.Write")
        ];
        
        var responseMessage = await httpClient.PostAsync("devicecode", new FormUrlEncodedContent(collection));

        var deviceCodeResponse = await responseMessage.Content.ReadFromJsonAsync<DeviceCodeResponse>();

        var deviceCodeExpires = DateTime.Now.Add(TimeSpan.FromSeconds(deviceCodeResponse!.expires_in));
        
        Console.WriteLine(deviceCodeResponse.message);

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
                if (pollResponse?.error == "authorization_pending")
                {
                    await Task.Delay(TimeSpan.FromSeconds(deviceCodeResponse.interval));
                }
            } 
            else if (pollResponseMessage.StatusCode == HttpStatusCode.OK)
            {
                if (pollResponse == null)
                {
                    throw new InvalidOperationException("Device code poll response is null.");
                }

                AccessToken = pollResponse.access_token;
                expires = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(pollResponse.expires_in));

                await WriteToFile(AccessToken, expires);
                
                Console.WriteLine($"Session ends {expires:t}");
                return;
            }
            else
            {
                return;
            }
        } while (deviceCodeExpires > DateTime.Now);
    }

    private async Task<(string accessToken, DateTimeOffset expires)> ReadFromFile()
    {
        var path = Path.Combine(options.MeasurementsFolder, "../user");

        if (!File.Exists(path))
        {
            return (string.Empty, DateTimeOffset.MinValue);
        }
        
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);

        return lines.Length < 2 
            ? (string.Empty, DateTimeOffset.MinValue) 
            : (lines[0], DateTimeOffset.Parse(lines[1]));
    }

    private async Task WriteToFile(string accessToken, DateTimeOffset expirez)
    {
        var path = Path.Combine(options.MeasurementsFolder, "../user");

        string[] lines =
        [
            accessToken,
            expirez.ToString("O")
        ];
        
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
    }
}

public record DeviceCodeResponse(string message, string device_code, int expires_in, int interval);

public record PollResponse(string error, string access_token, int expires_in);
