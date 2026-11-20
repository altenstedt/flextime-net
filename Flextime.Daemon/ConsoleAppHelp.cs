namespace ConsoleAppFramework;

// Extends the generated builder so the full help can be printed from a filter.
internal static partial class ConsoleApp
{
    // --version prints the version, and the home page below it.
    public static string VersionWithHomePage => DefaultVersion + Environment.NewLine + Flextime.Constants.HomePage;

    internal partial class ConsoleAppBuilder
    {
        public static void PrintHelp() => ShowHelp(-1);
    }
}
