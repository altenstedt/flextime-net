namespace Inhill.Flextime.Sync.DataContracts;

public record PingDataContract(
    string Version,
    string? Details,
    string Runtime,
    string InstanceId);
