using ConsoleAppFramework;

namespace Flextime.Daemon;

/// <summary>
/// Rejects any argument to a command without options.  ConsoleAppFramework
/// checks arguments only for commands that have options, so without this
/// "flextimed stop --foo" would stop and exit 0.  A lone --help or --version
/// passes, since the generated code checks for them after the filters.
/// </summary>
internal sealed class NoArgumentsFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        if (context.CommandArguments is [var first, ..] and not ["-h" or "--help" or "--version"])
        {
            ConsoleApp.LogError($"Argument '{first}' is not recognized.");

            Environment.ExitCode = 1;

            return Task.CompletedTask;
        }

        return Next.InvokeAsync(context, cancellationToken);
    }
}
