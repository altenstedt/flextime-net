namespace Flextime.Monitor;

public struct UserInputMonitorOptions
{
    public TimeSpan LogSummaryInterval { get; init; }

    public bool IgnoreSessionLocked { get; init; }

    public string? TimeZone { get; init; }
}
