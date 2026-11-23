namespace Flextime.Daemon;

/// <summary>
/// Whether output may be coloured: --no-color, a global option, or
/// NO_COLOR set and not empty, turns it off for every command.  Whether
/// the output is a terminal that takes colour is each writer's own check.
/// </summary>
public static class Colour
{
    public const string Option = "--no-color";

    public const string Description = "No colour; NO_COLOR does the same.";

    /// <summary>Set from the global option, before any command runs.</summary>
    public static bool NoColorOption { get; set; }

    public static bool Wanted =>
        PrintInfo.ColourWanted(NoColorOption, Environment.GetEnvironmentVariable("NO_COLOR"));
}
