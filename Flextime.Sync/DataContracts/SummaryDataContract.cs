namespace Inhill.Flextime.Sync.DataContracts;

public record DayDataContract(DateOnly Date, long Hash);

public record SummaryDataContract(DayDataContract[] Items);
