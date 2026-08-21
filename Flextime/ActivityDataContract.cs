using System.Text.Json.Serialization;

namespace Flextime;

// The shape written by `flextime --json` and `flextimed data --json`.
// One contract, so the same script reads local and remote data.

public record DayActivityDataContract(
    DateOnly Date,
    string Zone,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Span,
    TimeSpan Work,
    int Measurements,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long[]? Timestamps);

public record ComputerActivityDataContract(string Id, string? Name, DayActivityDataContract[] Days);

public record ActivityDataContract(ComputerActivityDataContract[] Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ActivityDataContract))]
public partial class ActivitySourceGenerationContext : JsonSerializerContext;
