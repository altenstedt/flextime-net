namespace Flextime.Daemon;

public static class ApiHttpClient
{
    public static void AddApiHttpClient(this IServiceCollection services, string refreshToken)
    {
        var options = new RefreshTokenDelegatingHandlerOptions
        {
            Scope = Constants.Scope,
            ClientId = Constants.ClientId,
            RefreshToken = refreshToken,
            WriteToStorage = true
        };
        
        services.AddSingleton(options);
        services.AddTransient<RefreshTokenDelegatingHandler>();

        services.AddHttpClient("TokenHttpClient", client =>
            {
                client.BaseAddress =
                    new Uri($"https://login.microsoftonline.com/{Constants.TenantId}/oauth2/v2.0/token");
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