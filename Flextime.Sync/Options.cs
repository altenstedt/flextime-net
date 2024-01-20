namespace Inhill.Flextime.Sync;

public struct Options()
{
    public string MeasurementsFolder { get; set; }
    
    public bool Verbose { get; set; }
    
    public Uri Uri { get; set; }

    public bool ListSync { get; set; }

    public int BlocksPerDay { get; set; }
    
    public TimeSpan Idle { get; set; }

    public bool Remote { get; set; }

    public string Computer { get; set; }

    public bool Computers { get; set; }
}