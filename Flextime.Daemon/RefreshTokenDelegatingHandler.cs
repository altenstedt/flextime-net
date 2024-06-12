using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LazyCache;

namespace Flextime.Daemon;

public class RefreshTokenDelegatingHandler(
    string refreshToken,
    HttpClient tokenHttpClient,
    string clientId,
    string scope,
    bool writeToStorage = true) : DelegatingHandler(new HttpClientHandler())
{
    private readonly CachingService cache = new();
    private readonly TimeSpan grace = TimeSpan.FromMinutes(1); // Enough to never use expired access tokens

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await cache.GetOrAddAsync<string?>(clientId, async entry =>
        {
            // This is guaranteed to be single threaded since we use LazyCache.
            KeyValuePair<string, string>[] collection = [ 
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("scope", scope),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken)
            ];
        
            var responseMessage = await tokenHttpClient.PostAsync(string.Empty, new FormUrlEncodedContent(collection), cancellationToken);

            responseMessage.EnsureSuccessStatusCode();

            var tokenResponse = await responseMessage.Content.ReadFromJsonAsync(
                TokenResponseSourceGenerationContext.Default.TokenResponse, 
                cancellationToken);

            if (tokenResponse == null)
            {
                throw new InvalidOperationException("Token response was null.");
            }
        
            var accessToken = tokenResponse.access_token;
            refreshToken = tokenResponse.refresh_token;

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(tokenResponse.expires_in).Subtract(grace);

            if (writeToStorage)
            {
                await TokenStorage.Write(accessToken, tokenResponse.expires_in, refreshToken);
            }

            return accessToken;
        });

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken);
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
internal record TokenResponse(string access_token, int expires_in, string refresh_token);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TokenResponse))]
internal partial class TokenResponseSourceGenerationContext : JsonSerializerContext;


