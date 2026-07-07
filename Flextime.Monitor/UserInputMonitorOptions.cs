namespace Flextime.Monitor;

public record UserInputMonitorOptions
{
    public TimeSpan LogSummaryInterval { get; init; }

    public bool IgnoreSessionLocked { get; init; }

    public string? TimeZone { get; init; }
}
