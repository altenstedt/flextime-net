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

    /// <summary>
    /// Asked every tick, with the time now in UTC: the end of the pause
    /// in force, or null to measure.  Must be cheap; whatever it throws
    /// is taken as no pause.
    /// </summary>
    public Func<DateTimeOffset, DateTimeOffset?>? PausedUntil { get; init; }
}
