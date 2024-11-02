using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LazyCache;

namespace Flextime.Daemon;

public class RefreshTokenDelegatingHandler(
    IHttpClientFactory httpClientFactory,
    RefreshTokenDelegatingHandlerOptions options) : DelegatingHandler(new HttpClientHandler())
{
    private readonly HttpClient tokenHttpClient = httpClientFactory.CreateClient("TokenHttpClient"); 
    private readonly CachingService cache = new();
    private readonly TimeSpan grace = TimeSpan.FromMinutes(1); // Enough to never use expired access tokens

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await cache.GetOrAddAsync<string?>(options.ClientId, async entry =>
        {
            // This is guaranteed to be single threaded since we use LazyCache.
            KeyValuePair<string, string>[] collection = [ 
                new("client_id", options.ClientId),
                new("scope", options.Scope),
                new("grant_type", "refresh_token"),
                new("refresh_token", options.RefreshToken)
            ];
        
            var responseMessage = await tokenHttpClient.PostAsync(string.Empty, new FormUrlEncodedContent(collection), cancellationToken);

            if (!responseMessage.IsSuccessStatusCode)
            {
                if (responseMessage.StatusCode == HttpStatusCode.BadRequest)
                {
                    return null;
                }

                responseMessage.EnsureSuccessStatusCode();
            }

            var tokenResponse = await responseMessage.Content.ReadFromJsonAsync(
                TokenResponseSourceGenerationContext.Default.TokenResponse, 
                cancellationToken);

            if (tokenResponse == null)
            {
                throw new InvalidOperationException("Token response was null.");
            }
        
            var accessToken = tokenResponse.access_token;
            var refreshToken = tokenResponse.refresh_token;

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(tokenResponse.expires_in).Subtract(grace);

            if (options.WriteToStorage)
            {
                await TokenStorage.Write(accessToken, tokenResponse.expires_in, refreshToken, cancellationToken);
            }

            return accessToken;
        });

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
public record RefreshTokenDelegatingHandlerOptions
{
    public required string RefreshToken { get; init; }
    public required string ClientId { get; init; }
    public required string Scope { get; init; }
    public required bool WriteToStorage { get; init; }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
internal record TokenResponse(string access_token, int expires_in, string refresh_token);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TokenResponse))]
internal partial class TokenResponseSourceGenerationContext : JsonSerializerContext;


