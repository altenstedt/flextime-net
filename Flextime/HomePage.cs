namespace Flextime;

/// <summary>The line that ends the root --help of flextime and flextimed.</summary>
public static class HomePage
{
    public const string Line = Constants.HomePage;

    // ConsoleAppFramework shows the help for -h anywhere before "--".
    public static bool AskedForHelp(ReadOnlySpan<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument == "--")
            {
                return false;
            }

            if (argument is "-h" or "--help")
            {
                return true;
            }
        }

        return false;
    }
}
