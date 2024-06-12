namespace Flextime.Daemon;

public static class ApiHttpClient
{
    private const string TenantId = "6b3c1467-664f-4edb-8328-43b7687d0366";

    private const string ClientId = "506c78bf-3e07-4caa-b20c-0deec3356d4d";

    private const string Scope =
        "openid offline_access api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Read api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Write";

    public static async Task<HttpClient> Create(CancellationToken cancellationToken = default)
    {
        var (_, _, refreshToken) = await TokenStorage.Read(cancellationToken);

        var tokenHttpClient = new HttpClient
            { BaseAddress = new Uri($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token") };
        
        var httpClient = new HttpClient(new RefreshTokenDelegatingHandler(refreshToken, tokenHttpClient, ClientId, Scope)) { BaseAddress = Constants.ApiUri };

        return httpClient;
    }    
}