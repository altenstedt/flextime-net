namespace Flextime.Client;

public record Options
{
    public required string MeasurementsFolder { get; init; }

    public bool Verbose { get; init; }

    public bool SplitWeek { get; init; }

    public TimeSpan Since { get; init; }

    public int BlocksPerDay { get; init; }

    public TimeSpan Idle { get; init; }
}
