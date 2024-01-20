using Microsoft.Extensions.Logging;

namespace Inhill.Flextime.Server;

public struct Options()
{
    public string MeasurementsFolder { get; set; }
    
    public bool Verbose { get; set; }

    public LogLevel LogLevel { get; set; }
}