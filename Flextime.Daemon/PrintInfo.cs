using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Flextime.Daemon;

public class PrintInfo(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, Computer computer, Sync sync)
{
    public async Task Invoke()
    {
        var httpClient = httpClientFactory.CreateClient("ApiHttpClient");
        
        // I want the nice spinners on Windows
        // https://github.com/spectreconsole/spectre.console/issues/391
        Console.OutputEncoding = Encoding.UTF8;

        var version = VersionHelper.GetVersion();

        AnsiConsole.MarkupLine($"Client version     : {version ?? "Unknown"}");
        AnsiConsole.MarkupLine($"Measurement folder : {Path.GetFullPath(computer.MeasurementsFolder)}");
        AnsiConsole.MarkupLine($"Computer name      : {computer.Name}");
        AnsiConsole.MarkupLine($"Computer id        : {computer.Id}");
        AnsiConsole.MarkupLine($"Time zone          : {PrintTimeZone()}");
        AnsiConsole.MarkupLine($"Server             : {Constants.ApiUri}");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Plain) // No colors
            .StartAsync("Fetching server version...", async _ =>
            {
                try
                {
                    var pingResult = await httpClient.GetFromJsonAsync("/ping", PingSourceGenerationContext.Default.PingDataContract);

                    AnsiConsole.MarkupLine(
                        $"Server version     : {pingResult?.Version} {pingResult?.Details} {pingResult?.Runtime} {pingResult?.InstanceId}");
                }
                catch (HttpRequestException exception)
                {
                    AnsiConsole.MarkupLine($"Error contacting backend: {exception.Message}.");

                    if (exception.InnerException != null)
                    {
                        AnsiConsole.MarkupLine($"  {exception.InnerException.Message}");
                    }
                }
            });

        if (deviceCode.IsAuthenticated)
        {
            var (accessToken, _, _) = await TokenStorage.Read();

            if (string.IsNullOrEmpty(accessToken) || !accessToken.Contains('.'))
            {
                AnsiConsole.MarkupLine("Signed in          : Yes");
            }
            else
            {
                var user = GetUserInfo(accessToken);
                AnsiConsole.MarkupLine($"Signed in          : {user}");
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Plain) // No colors
                .StartAsync("Fetching server data...",
                    async _ =>
                    {
                        AnsiConsole.MarkupLine("Server data        :");
                        AnsiConsole.WriteLine();
                        await PrintSummary(5);
                    });
        }
        else
        {
            AnsiConsole.MarkupLine("Logged in          : No. Use login command to log in.");
        }
    }

    private static string GetUserInfo(string accessToken)
    {
        var token = new JwtSecurityToken(accessToken);

        var name = token.Claims.FirstOrDefault(claim => claim.Type == "name")?.Value;
        var email = token.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value;

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
        {
            return "Yes.";
        }

        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(email))
        {
            return email;
        }

        if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
        {
            return name;
        }
        
        return $"{name} <{email}>";
    }
    
    private async Task PrintSummary(int count)
    {
        await sync.Print(count);
    }

    private static string PrintTimeZone()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu
            var sortVersion = CultureInfo.InvariantCulture.CompareInfo.Version;
            var bytes = sortVersion.SortId.ToByteArray();
            var tmp = bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0];
            var isUsingIcu = tmp != 0 && tmp == sortVersion.FullVersion;

            if (isUsingIcu)
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaId))
                {
                    return
                        $"{ianaId} (converted from {TimeZoneInfo.Local.Id})";
                }

                return $"Windows platform is unable to convert {TimeZoneInfo.Local.Id} to ICU time zone";
            }

            return "Windows platform is not using ICU";
        }

        return TimeZoneInfo.Local.Id;
    }
}

internal record PingDataContract(
    string Version,
    string? Details,
    string Runtime,
    string InstanceId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PingDataContract))]
internal partial class PingSourceGenerationContext : JsonSerializerContext;
