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
        var accessToken = await cache.GetOrAddAsync<string>(options.ClientId, async entry =>
        {
            // This is guaranteed to be single threaded since we use LazyCache.

            // This cache belongs to the handler, which the client factory
            // rebuilds on its own rotation schedule, and `sync --once` is a
            // fresh process on every pass.  The options singleton — seeded
            // from the token file at startup — is what carries a token
            // across both boundaries, so spend a round trip only when what
            // it holds is missing or too close to expiry.
            if (!string.IsNullOrEmpty(options.AccessToken) && options.Expires > DateTimeOffset.UtcNow.Add(grace))
            {
                entry.AbsoluteExpiration = options.Expires.Subtract(grace);

                return options.AccessToken;
            }

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
                    throw new TokenRefreshException("The sign-in has expired. Use the login command to log in again.");
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

            // Kept on the singleton so the next handler — or, once written
            // below, the next process — starts from a token instead of
            // another grant.
            options.AccessToken = accessToken;
            options.Expires = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.expires_in);

            // The token endpoint rotates the refresh token; use the new one
            // from now on or refreshes will eventually stop working.
            if (!string.IsNullOrEmpty(refreshToken))
            {
                options.RefreshToken = refreshToken;
            }

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(tokenResponse.expires_in).Subtract(grace);

            if (options.WriteToStorage)
            {
                await TokenStorage.Write(accessToken, tokenResponse.expires_in, refreshToken, cancellationToken: cancellationToken);
            }

            return accessToken;
        });

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
public record RefreshTokenDelegatingHandlerOptions
{
    public required string RefreshToken { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset Expires { get; set; } = DateTimeOffset.MinValue;
    public required string ClientId { get; init; }
    public required string Scope { get; init; }
    public required bool WriteToStorage { get; init; }
}

public class TokenRefreshException(string message) : Exception(message);

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
internal record TokenResponse(string access_token, int expires_in, string refresh_token);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TokenResponse))]
internal partial class TokenResponseSourceGenerationContext : JsonSerializerContext;


