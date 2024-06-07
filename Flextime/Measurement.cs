using ProtoBuf;

namespace Flextime;

[ProtoContract]
public enum MeasurementKind
{
    [ProtoEnum(Name = "NONE")]
    None = 0,
    
    [ProtoEnum(Name = "MEASUREMENT")]
    Measurement = 1,
    
    [ProtoEnum(Name = "START")]
    Start = 2,
    
    [ProtoEnum(Name = "STOP")]
    Stop = 3,

    [ProtoEnum(Name = "SESSION_LOCK")]
    SessionLock = 4,

    [ProtoEnum(Name = "SESSION_UNLOCK")]
    SessionUnlock = 5,
}

[ProtoContract]
public class Measurement
{
    [ProtoMember(1)]
    public uint Timestamp { get; set; }   
    
    [ProtoMember(2)]
    public MeasurementKind Kind { get; set; }   

    [ProtoMember(3)]
    public uint Idle { get; set; }   
}

[ProtoContract]
public class Measurements
{
    [ProtoMember(1)]
    public uint Interval { get; set; }

    [ProtoMember(2)] public string Zone { get; set; } = string.Empty;

    [ProtoMember(3, Name = "measurements")]
    public ICollection<Measurement> Items { get; set; } = new List<Measurement>();
}

public record MeasurementWithZone(Measurement Measurement, string Zone, uint Interval)
{
    public DateTimeOffset Timestamp { get; } = 
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTimeOffset.FromUnixTimeSeconds(Measurement.Timestamp).DateTime,
            TimeZoneInfo.FindSystemTimeZoneById(Zone));
}

public record MeasurementDataContract(
    uint Kind,
    uint Timestamp,
    uint Idle);

public record MeasurementsDataContract(
    string ComputerId,
    string Zone,
    uint Interval,
    MeasurementDataContract[] Items);

public record PagedMeasurementsDataContract(
    MeasurementsDataContract[] Items);
