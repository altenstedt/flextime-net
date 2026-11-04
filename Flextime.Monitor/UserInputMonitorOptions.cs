namespace Flextime.Monitor;

public record UserInputMonitorOptions
{
    public TimeSpan LogSummaryInterval { get; init; }

    public bool IgnoreSessionLocked { get; init; }

    public string? TimeZone { get; init; }

    /// <summary>
    /// Told the zone the next file begins in when the computer changes
    /// zone, so what listen published at start does not go stale.
    /// </summary>
    public Action<string>? ZoneChanged { get; init; }
}
