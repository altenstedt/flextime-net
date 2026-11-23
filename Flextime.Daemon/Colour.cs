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

    /// <summary>
    /// The arguments with a --no-color before the command moved behind
    /// it.  ConsoleAppFramework picks the command by the first words, so
    /// "flextimed --no-color status" would otherwise run the default
    /// command with "status" as an unknown argument.  Behind the command
    /// it is found anywhere up to "--".
    /// </summary>
    public static string[] CommandFirst(string[] args)
    {
        var leading = args.TakeWhile(IsOption).Count();

        if (leading == 0)
        {
            return args;
        }

        var rest = args[leading..];

        // The root help is shown only for a lone --help, and has no colour.
        if (rest is ["-h" or "--help"])
        {
            return rest;
        }

        var escape = Array.IndexOf(rest, "--");
        var at = escape == -1 ? rest.Length : escape;

        return [.. rest[..at], .. args[..leading], .. rest[at..]];
    }

    private static bool IsOption(string arg) => arg.Equals(Option, StringComparison.OrdinalIgnoreCase);

    public static bool Wanted =>
        PrintInfo.ColourWanted(NoColorOption, Environment.GetEnvironmentVariable("NO_COLOR"));
}
