using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Flextime.Monitor;

namespace Flextime.Daemon;

/// <summary>A place clear: nothing before <see cref="At"/> exists.</summary>
/// <param name="At">Epoch seconds.</param>
/// <param name="Pending">The server has not confirmed the delete yet; sync asks again before uploading places.</param>
public sealed record ClearMarker(uint At, bool Pending);

/// <summary>
/// The location settings in the state folder: <c>location.txt</c> says
/// whether places are recorded, <c>location-clear.txt</c> holds the
/// clear marker.  Off, and never cleared, when the files are missing.
/// </summary>
public static class Location
{
    private const string Pending = "delete pending";
    private const string Deleted = "deleted";

    public static bool IsOn(string? path = null) =>
        StateFiles.TryRead(path ?? StateFiles.LocationPath) is [var state, ..] && state.Trim() == "on";

    public static void SetOn(bool on, string? path = null) =>
        StateFiles.Write(path ?? StateFiles.LocationPath, on ? "on" : "off", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

    public static ClearMarker? ReadClear(string? path = null)
    {
        if (StateFiles.TryRead(path ?? StateFiles.LocationClearPath) is not [var at, ..] lines
            || !uint.TryParse(at.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return new ClearMarker(seconds, lines.Any(line => line.Trim() == Pending));
    }

    public static void WriteClear(ClearMarker marker, string? path = null) =>
        StateFiles.Write(
            path ?? StateFiles.LocationClearPath,
            marker.At.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.FromUnixTimeSeconds(marker.At).ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
            marker.Pending ? Pending : Deleted);

    public static PlaceSettings ReadSettings(string? locationPath = null, string? clearPath = null) =>
        new(IsOn(locationPath), ReadClear(clearPath)?.At ?? 0);

    /// <summary>
    /// The user id the API keys everything by: the object id in the
    /// access token.  Null when not logged in or the token is not a JWT.
    /// </summary>
    public static string? UserId(string accessToken)
    {
        if (string.IsNullOrEmpty(accessToken) || !accessToken.Contains('.'))
        {
            return null;
        }

        try
        {
            var token = new JwtSecurityToken(accessToken);

            var id = token.Claims.FirstOrDefault(claim => claim.Type == "oid")?.Value;

            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static async Task<string?> ReadUserId() => UserId((await TokenStorage.Read()).accessToken);

    /// <summary>
    /// The client hash of a router: the first 48 bits of SHA-256 over
    /// the user id and the router's hardware address.  Not a secret —
    /// the API keys it again before storing anything — but it keeps the
    /// address out of files and uploads, and two users' hashes of the
    /// same router differ.
    /// </summary>
    public static ulong ClientHash(string userId, string mac)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"flextime-place:{userId}:{mac}"));

        ulong value = 0;

        for (var i = 0; i < 6; i++)
        {
            value = value << 8 | hash[i];
        }

        return value;
    }

    /// <summary>12 lowercase hex characters, the way the API takes a router.</summary>
    public static string Hex(ulong router) => router.ToString("x12", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a network report says about the place: the router on the
    /// interface internet traffic leaves through, or, when that is a
    /// tunnel, the router of the physical network under it — a VPN does
    /// not move the computer.  The flags say how far to trust it.
    /// </summary>
    public static PlaceReading ToReading(NetworkReport report, string? userId)
    {
        if (report.Routes.Length == 0)
        {
            return new PlaceReading(0, 0, Online: false);
        }

        var flags = 0u;

        if (report.Routes.Any(route => route.Tunnel))
        {
            flags |= PlaceFlags.NotDependable;
        }

        if (report.VpnServices?.Any(service => service.Status == "Connected") == true)
        {
            flags |= PlaceFlags.Vpn;
        }

        var ipv4 = report.Routes.FirstOrDefault(route => route.Family == "IPv4" && !route.Tunnel);

        var router = report.Routers.FirstOrDefault(candidate => ipv4 != null && candidate.Interface == ipv4.Interface)
                     ?? report.Routers.FirstOrDefault(candidate => candidate.Mac != null)
                     ?? report.Routers.FirstOrDefault();

        if (router?.Mac == null || userId == null)
        {
            return new PlaceReading(0, flags, Pending: router != null && router.Mac == null);
        }

        return new PlaceReading(ClientHash(userId, router.Mac), flags);
    }
}
