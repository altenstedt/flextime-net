using Microsoft.Extensions.DependencyInjection;

namespace Flextime.Daemon;

public static class ApiHttpClient
{
    public static void AddApiHttpClient(
        this IServiceCollection services,
        string accessToken,
        DateTimeOffset expires,
        string refreshToken)
    {
        var options = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = Constants.Scope,
            ClientId = Constants.ClientId,
            AccessToken = accessToken,
            Expires = expires,
            RefreshToken = refreshToken,
            WriteToStorage = true
        };
        
        services.AddSingleton(options);
        services.AddTransient<RefreshTokenDelegatingHandler>();

        services.AddHttpClient("TokenHttpClient", client =>
            {
                client.BaseAddress = Constants.TokenUri;
            })
            .AddStandardResilienceHandler();

        services.AddHttpClient("ApiHttpClient", client =>
            {
                client.BaseAddress = Constants.ApiUri;
            })
            .ConfigurePrimaryHttpMessageHandler<RefreshTokenDelegatingHandler>()
            .AddStandardResilienceHandler();
    }
}