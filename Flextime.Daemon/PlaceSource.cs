using Flextime.Monitor;
using Microsoft.Extensions.Logging;

namespace Flextime.Daemon;

/// <summary>Where listen reads the location settings and the network from.</summary>
public sealed class PlaceSource(ILogger logger) : IPlaceSource
{
    // Warnings that would otherwise repeat every few minutes.
    private static readonly TimeSpan WarningInterval = TimeSpan.FromHours(1);

    private string? userId;
    private DateTimeOffset lastUserWarning = DateTimeOffset.MinValue;
    private DateTimeOffset lastPendingWarning = DateTimeOffset.MinValue;
    private int pendingInARow;

    public PlaceSettings ReadSettings() => Location.ReadSettings();

    public async Task<PlaceReading> Read()
    {
        // Read until found: location on requires a login, but the login
        // may come after listen started.
        userId ??= await Location.ReadUserId();

        if (userId == null && DateTimeOffset.UtcNow - lastUserWarning >= WarningInterval)
        {
            logger.LogWarning("Location is on, but no user id was found. Log in again; until then the place is unknown.");

            lastUserWarning = DateTimeOffset.UtcNow;
        }

        var report = await NetworkProbe.Read();

        var reading = Location.ToReading(report, userId);

        pendingInARow = reading.Pending ? pendingInARow + 1 : 0;

        // A moment without the hardware address is normal while a network
        // comes up.  For long, it means configd no longer records it —
        // the keys are undocumented — and every place reads as unknown.
        if (pendingInARow >= 10 && DateTimeOffset.UtcNow - lastPendingWarning >= WarningInterval)
        {
            logger.LogWarning(OperatingSystem.IsMacOS()
                ? "The router's hardware address has not been found in configd's ARPResolvedHardwareAddress or NetworkSignature for {Count} samples. If macOS no longer records it, places stay unknown."
                : "The router's hardware address has not been found in the neighbour cache for {Count} samples; places stay unknown.",
                pendingInARow);

            lastPendingWarning = DateTimeOffset.UtcNow;
        }

        return reading;
    }
}
