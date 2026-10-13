using System.Globalization;
using System.Net.Http.Json;
using Polly.Timeout;

namespace Flextime.Daemon;

/// <summary>
/// The location commands: whether listen records the place, and
/// clearing what it recorded.  The setting lives in a file listen
/// reads every tick, so nothing here needs a restart.
/// </summary>
public class LocationClient(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, Computer computer)
{
    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public async Task<int> Status()
    {
        var on = Location.IsOn();

        Console.WriteLine($"Location        :  {(on ? "On" : "Off")}");

        var userId = await Location.ReadUserId();
        var report = await NetworkProbe.Read();
        var reading = Location.ToReading(report, userId);

        if (userId == null)
        {
            Console.WriteLine("Place           :  Unknown, not logged in");
        }
        else if (!reading.Online)
        {
            Console.WriteLine("Place           :  Unknown, no route to the internet");
        }
        else if (reading.Router == 0)
        {
            Console.WriteLine(reading.Pending
                ? "Place           :  Unknown, the router's hardware address is not known yet"
                : "Place           :  Unknown, no router found");
        }
        else
        {
            // Not the hash: the web shows the id the server makes of it,
            // and two ids for one router help nobody.  The web marks the
            // place measured now as "Here now"; name it there.
            var router = report.Routers.FirstOrDefault(candidate => candidate.Mac != null);

            Console.WriteLine(router == null
                ? "Place           :  Recorded"
                : $"Place           :  Router {router.Address} on {router.Interface}");

            if (reading.Hint != null)
            {
                Console.WriteLine($"Network         :  {reading.Hint}");
            }
        }

        Console.WriteLine($"Position        :  {(report.Dependable ? "Dependable" : "Not dependable")}. {report.Reason}");

        var connected = report.VpnServices?.Where(service => service.Status == "Connected").Select(service => service.Name).ToArray();

        Console.WriteLine(connected == null
            ? "VPN             :  Not listed on this platform"
            : connected.Length == 0
                ? "VPN             :  None connected"
                : $"VPN             :  {string.Join(", ", connected)}");

        if (Location.ReadClear() is { } marker)
        {
            Console.WriteLine($"Cleared         :  {FormatTime(marker.At)}{(marker.Pending ? ", server delete pending" : string.Empty)}");
        }

        Console.WriteLine();
        Console.WriteLine(on
            ? "The place is the router, not a position; the web marks it \"Here now\", so name it there while you are here. Use location off to stop, location clear to remove the history."
            : "Use location on to record the place with the measurements.");

        return 0;
    }

    public async Task<int> On()
    {
        if (!TurnOn(deviceCode.IsAuthenticated, await Location.ReadUserId()))
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");

            return 2;
        }

        Console.WriteLine("Location is on. The place is recorded from the next minute.");

        if (!SingleInstance.IsHeld(SingleInstance.ListenLockPath))
        {
            Console.WriteLine("Listen is not running; it starts recording the place when it starts.");
        }

        return 0;
    }

    public int Off()
    {
        Location.SetOn(false);

        Console.WriteLine("Location is off. From the next minute the time has no place; the history is kept. Use location clear to remove it.");

        return 0;
    }

    public async Task<int> Clear(bool allComputers, CancellationToken cancellationToken)
    {
        var at = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        ClearOutcome outcome;

        try
        {
            outcome = await ClearComputer(
                at,
                deviceCode.IsAuthenticated ? token => Delete(httpClient, computer.Id!, token) : null,
                Constants.MeasurementsFolder,
                StateFiles.LocationClearPath,
                SingleInstance.IsHeld(SingleInstance.ListenLockPath),
                cancellationToken);
        }
        catch (TokenRefreshException exception)
        {
            Console.Error.WriteLine(exception.Message);

            return 2;
        }

        Console.WriteLine(outcome.Rewritten == 1
            ? "Place history removed from 1 local file."
            : $"Place history removed from {outcome.Rewritten} local files.");

        if (!deviceCode.IsAuthenticated)
        {
            Console.WriteLine("Not logged in: the server is cleared at the first sync after you log in.");

            return 0;
        }

        var failed = outcome.Error != null;

        if (failed)
        {
            Console.Error.WriteLine($"The server could not be cleared now ({outcome.Error}). Sync tries again before it uploads any place.");
        }
        else
        {
            Console.WriteLine("Place history removed from the server for this computer.");
        }

        if (!allComputers)
        {
            return failed ? 1 : 0;
        }

        try
        {
            var known = await httpClient.GetFromJsonAsync(
                "/computers?api-version=1.1",
                PrintDataSourceGenerationContext.Default.ComputersDataContract,
                cancellationToken);

            foreach (var other in known?.Items.Where(item => item.Id != computer.Id) ?? [])
            {
                var name = string.IsNullOrEmpty(other.Name) ? other.Id : $"{other.Name} ({other.Id})";

                try
                {
                    await Delete(httpClient, other.Id, cancellationToken);

                    Console.WriteLine($"Place history removed from the server for {name}.");
                }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
                {
                    Console.Error.WriteLine($"Could not clear {name}: {exception.Message}. Run clear --all-computers again.");

                    failed = true;
                }
            }

            Console.WriteLine("Other computers clear their local files the next time they sync.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            Console.Error.WriteLine($"Could not list the other computers: {exception.Message}. Run clear --all-computers again.");

            failed = true;
        }
        catch (TokenRefreshException exception)
        {
            Console.Error.WriteLine(exception.Message);

            return 2;
        }

        return failed ? 1 : 0;
    }

    /// <summary>Turns location on, if there is a user to salt the place hash with.</summary>
    public static bool TurnOn(bool authenticated, string? userId, string? path = null)
    {
        if (!authenticated || userId == null)
        {
            return false;
        }

        Location.SetOn(true, path);

        return true;
    }

    /// <summary>
    /// Clears this computer's place history: the marker first, so that
    /// from then on nothing before it is uploaded whatever happens
    /// below and listen drops what it holds; then the local files; then
    /// the server.  A failed delete leaves the marker pending for sync.
    /// </summary>
    /// <param name="delete">Deletes on the server; null when not logged in.</param>
    public static async Task<ClearOutcome> ClearComputer(
        uint at,
        Func<CancellationToken, Task>? delete,
        string folder,
        string markerPath,
        bool listenRunning,
        CancellationToken cancellationToken)
    {
        Location.WriteClear(new ClearMarker(at, Pending: true), markerPath);

        var rewritten = ClearLocal(at, folder, listenRunning);

        if (delete == null)
        {
            return new ClearOutcome(rewritten, null);
        }

        try
        {
            await delete(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            return new ClearOutcome(rewritten, exception.Message);
        }

        ConfirmDelete(at, markerPath);

        return new ClearOutcome(rewritten, null);
    }

    /// <summary>
    /// Removes change points before <paramref name="at"/> from every
    /// local file but the one listen may be writing; listen rewrites
    /// that one from memory at its next tick.
    /// </summary>
    public static int ClearLocal(uint at, string? folder = null, bool? listenRunning = null)
    {
        folder ??= Constants.MeasurementsFolder;

        var skip = (listenRunning ?? SingleInstance.IsHeld(SingleInstance.ListenLockPath))
            ? PlaceFiles.Newest(folder)
            : null;

        return PlaceFiles.ClearBefore(folder, at, skip);
    }

    /// <summary>
    /// Records that the server holds nothing before <paramref name="at"/>
    /// — unless a newer clear was written meanwhile, which still needs
    /// its own delete.
    /// </summary>
    public static void ConfirmDelete(uint at, string? path = null)
    {
        if (Location.ReadClear(path) is { } marker && marker.At == at)
        {
            Location.WriteClear(marker with { Pending = false }, path);
        }
    }

    public static async Task Delete(HttpClient httpClient, string computerId, CancellationToken cancellationToken)
    {
        var response = await httpClient.DeleteAsync($"/{computerId}/places?api-version=1.5", cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private static string FormatTime(uint seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <param name="Rewritten">Local files rewritten.</param>
/// <param name="Error">Why the server delete failed; null when it succeeded or was not tried.</param>
public sealed record ClearOutcome(int Rewritten, string? Error);
