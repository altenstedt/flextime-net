namespace ConsoleAppFramework;

internal static partial class ConsoleApp
{
    // --version prints the version, and the home page below it.
    public static string VersionWithHomePage => DefaultVersion + Environment.NewLine + Flextime.Constants.HomePage;
}
