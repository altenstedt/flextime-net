using ConsoleAppFramework;

namespace Flextime.Daemon;

/// <summary>
/// Rejects a word that is not a command.  ConsoleAppFramework routes an
/// unknown first word to the root command, and commands without options
/// never look at their arguments, so "flextimed typo" would otherwise show
/// the installation and exit 0.  No command takes positional arguments, so
/// a first argument that is not an option is always an unknown command.
/// </summary>
internal sealed class UnknownCommandFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        if (context.CommandArguments is [var first, ..] && !first.StartsWith('-'))
        {
            var command = string.IsNullOrEmpty(context.CommandName) ? first : $"{context.CommandName} {first}";

            ConsoleApp.LogError($"Unknown command '{command}'.");
            ConsoleApp.Log("");
            ConsoleApp.ConsoleAppBuilder.PrintHelp();

            Environment.ExitCode = 1;

            return Task.CompletedTask;
        }

        return Next.InvokeAsync(context, cancellationToken);
    }
}
