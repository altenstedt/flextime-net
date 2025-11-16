namespace Flextime.Client;

public struct Options()
{
    public required string MeasurementsFolder { get; set; }
    
    public bool Verbose { get; set; }
    
    public bool SplitWeek { get; set; }

    public TimeSpan Since { get; set; }

    public int BlocksPerDay { get; set; }
    
    public TimeSpan Idle { get; set; }
}