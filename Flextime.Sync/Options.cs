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

    public string TenantId => "6b3c1467-664f-4edb-8328-43b7687d0366";

    public string ClientId => "506c78bf-3e07-4caa-b20c-0deec3356d4d";
}