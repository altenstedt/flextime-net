namespace ConsoleAppFramework;

// Extends the generated builder so the full help can be printed from a filter.
internal static partial class ConsoleApp
{
    internal partial class ConsoleAppBuilder
    {
        public static void PrintHelp() => ShowHelp(-1);
    }
}
