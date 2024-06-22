namespace Flextime;

public static class Constants
{
    public static readonly string MeasurementsFolder =
        $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}/Flextime/measurements";

    public static readonly Uri ApiUri = new Uri("https://api.mangoground-e628dd34.swedencentral.azurecontainerapps.io/", UriKind.Absolute);

    public static readonly Uri TokenUri = new Uri($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token");
    
    public const string TenantId = "6b3c1467-664f-4edb-8328-43b7687d0366";

    public const string ClientId = "506c78bf-3e07-4caa-b20c-0deec3356d4d";

    public const string Scope =
        "openid offline_access api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Read api://77d3d897-f62d-4f69-a3db-5394049156c1/Flextime.User.Write";
}