namespace Flextime;

public static class Constants
{
    public static readonly string MeasurementsFolder =
        $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify)}/Flextime/measurements";

    // FLEXTIME_API_URI lets an installed client follow the API to a new host
    // without a rebuild. Only an https origin is accepted; anything else falls
    // back to the built-in default.
    public static readonly Uri ApiUri = ApiUriFrom(Environment.GetEnvironmentVariable("FLEXTIME_API_URI"));

    // Scheme, host and port only: a path is dropped, and so is any user
    // info, which would otherwise be printed by info.
    public static Uri ApiUriFrom(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? new Uri(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/", UriKind.Absolute)
            : new Uri("https://api.flextime.inhill.com/", UriKind.Absolute);

    public static readonly Uri AuthorityUri = new Uri($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/", UriKind.Absolute);

    public static readonly Uri TokenUri = new Uri(AuthorityUri, "token");

    public const string TenantId = "6b3c1467-664f-4edb-8328-43b7687d0366";

    public const string ClientId = "506c78bf-3e07-4caa-b20c-0deec3356d4d";

    public const string Scope =
        "openid offline_access api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Read api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Write";
}