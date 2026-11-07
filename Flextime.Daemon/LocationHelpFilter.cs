using ConsoleAppFramework;

namespace Flextime.Daemon;

/// <summary>The key to the lines of location, printed after its help.</summary>
public static class LocationHelp
{
    // Every line fits in 80 columns.
    public const string Key = """

        The place is the router your computer reaches the network through.
        Flextime records a hash of its hardware address, nothing more.

        Place          The router's address and the interface it is on, such as
                       en0. It is always a physical interface, never a VPN.
        Name           With --name, the name you gave the place in the web app,
                       Not named yet, or Not a place. A new place has no name
                       until the next sync has sent it.
        Network        The network's DNS domain, if it has one. Metered network
                       means the system marks it so, usually a phone's hotspot.
        Router maker   Who made the router, read from the first half of its
                       hardware address. Only shown here, never sent.
        Route          How internet traffic leaves this computer. It never
                       changes the place.
        VPN            VPN connections the system lists as up. A VPN it does not
                       list shows only in Route.

        The maker is a hint. To check a place, look at Here now in the web
        app. A place you have never been should show up as Not named yet.
        If it shows a name you know, the router travels with you, like a
        phone's hotspot.
        """;
}

/// <summary>
/// Adds the key after the generated help of location.
/// ConsoleAppFramework renders only the summary, and the key needs room.
/// </summary>
internal sealed class LocationHelpFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
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
                Console.WriteLine(LocationHelp.Key);

                break;
            }
        }
    }
}
