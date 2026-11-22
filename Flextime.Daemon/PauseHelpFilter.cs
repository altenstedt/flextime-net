using ConsoleAppFramework;

namespace Flextime.Daemon;

/// <summary>What pause does beyond its options, printed after its help.</summary>
public static class PauseHelp
{
    // Every line fits in 80 columns.
    public const string Key = """

        The pause ends by itself, at midnight unless --for or --until says
        otherwise, and never later than the end of tomorrow. Resume ends it
        sooner.

        The active stretch just before the pause is marked as not work, if
        it is no longer than 30 minutes. --since marks from the time you give
        instead. Undo a mark in the web app with Counts as work again.

        A pause shorter than the idle limit is bridged like any other gap,
        so it may still count as work.
        """;
}

/// <summary>
/// Adds a key after a command's generated help.  ConsoleAppFramework
/// renders only the summary, and a key needs room.
/// </summary>
internal abstract class HelpKeyFilter(ConsoleAppFilter next, string key) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        await Next.InvokeAsync(context, cancellationToken);

        // ConsoleAppFramework shows the help for -h anywhere among the
        // arguments, so the key follows it there too.
        foreach (var argument in context.CommandArguments)
        {
            if (argument is "-h" or "--help")
            {
                Console.WriteLine(key);

                break;
            }
        }
    }
}

/// <summary>Adds the key after the generated help of pause.</summary>
internal sealed class PauseHelpFilter(ConsoleAppFilter next) : HelpKeyFilter(next, PauseHelp.Key);

/// <summary>Adds the key after the generated help of data.</summary>
internal sealed class DataHelpFilter(ConsoleAppFilter next) : HelpKeyFilter(next, PrintData.HelpKey);

/// <summary>Adds the key after the generated help of export.</summary>
internal sealed class ExportHelpFilter(ConsoleAppFilter next) : HelpKeyFilter(next, Export.HelpKey);
