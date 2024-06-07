namespace Flextime.Daemon;

public record TimeZoneOptions 
{
    public string? TimeZone { get; set; }
}

public record OnceOptions
{
    public bool Once { get; set; }
}

public record EveryOptions
{
    public TimeSpan? Every { get; set; }
}

public record IgnoreSessionLockedOptions
{
    public bool IgnoreSessionLocked { get; set; }
}

public record LogSummaryIntervalOptions
{
    public TimeSpan Interval { get; set; }
}