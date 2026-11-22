using System.Text.Json.Serialization;

namespace Flextime;

// The shape written by `flextime --json`: the activity on this
// computer, per computer and day.  `flextimed data --json` wrote it too
// until data came to show the web's merged day rows, which belong to no
// one computer; that contract is DataDataContract in flextimed, and
// writes dates, times and durations as this one does.

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
