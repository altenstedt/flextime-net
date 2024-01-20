namespace Inhill.Flextime.Sync.DataContracts;

public record DayDataContract(DateOnly Date, uint Hash);

public record SummaryDataContract(DayDataContract[] Items);
