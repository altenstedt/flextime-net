using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Flextime.Daemon;

public static class PrintInfo
{
    public static async Task Invoke()
    {
        // I want the nice spinners on Windows
        // https://github.com/spectreconsole/spectre.console/issues/391
        Console.OutputEncoding = Encoding.UTF8;

        var computer = new Computer();
        await computer.Initialize();

        var deviceCode = new DeviceCode();
        await deviceCode.Initialize();

        var version = FileVersionInfo
            .GetVersionInfo(Environment.GetCommandLineArgs()[0])
            .ProductVersion;

        AnsiConsole.MarkupLine($"Client version    : {version ?? "Unknown"}");
        AnsiConsole.MarkupLine($"Measurement folder: {Path.GetFullPath(computer.MeasurementsFolder)}");
        AnsiConsole.MarkupLine($"Computer name     : {computer.Name}");
        AnsiConsole.MarkupLine($"Computer id       : {computer.Id}");
        AnsiConsole.MarkupLine($"Time zone         : {PrintTimeZone()}");

        var uri = new Uri("https://api.mangoground-e628dd34.swedencentral.azurecontainerapps.io/", UriKind.Absolute);
        var httpClient = new HttpClient { BaseAddress = uri };

        AnsiConsole.MarkupLine($"Server            : {uri}");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Plain) // No colors
            .StartAsync("Fetching server version...", async _ =>
            {
                try
                {
                    var pingResult = await httpClient.GetFromJsonAsync<PingDataContract>("/ping");

                    AnsiConsole.MarkupLine(
                        $"Server version    : {pingResult?.Version} {pingResult?.Details} {pingResult?.Runtime} {pingResult?.InstanceId}");
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
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Plain) // No colors
                .StartAsync("Fetching user information",
                    async _ =>
                    {
                        var accessToken = await deviceCode.GetAccessToken();
                        httpClient.DefaultRequestHeaders.Authorization =
                            new AuthenticationHeaderValue("Bearer", accessToken);

                        if (string.IsNullOrEmpty(accessToken) || !accessToken.Contains('.'))
                        {
                            AnsiConsole.MarkupLine("Signed in         : Yes");
                        }
                        else
                        {
                            var user = GetUserInfo(accessToken);
                            AnsiConsole.MarkupLine($"Signed in         : {user}");
                        }
                    });

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Plain) // No colors
                .StartAsync("Fetching server data...",
                    async _ =>
                    {
                        var accessToken = await deviceCode.GetAccessToken();
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                        AnsiConsole.MarkupLine("Server data       :");
                        AnsiConsole.WriteLine();
                        await PrintSummary(httpClient, deviceCode, computer);
                    });
        }
        else
        {
            AnsiConsole.MarkupLine("Logged in         : No. Use login command to log in.");
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
    
    private static async Task PrintSummary(HttpClient httpClient, DeviceCode deviceCode, Computer computer)
    {
        await Sync.Invoke(httpClient, deviceCode, computer, false, TimeSpan.FromMinutes(10), 0, false, false,
            (text, _) => AnsiConsole.WriteLine(text), AnsiConsole.WriteLine, 5);
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

    private record PingDataContract(
        string Version,
        string? Details,
        string Runtime,
        string InstanceId);
}