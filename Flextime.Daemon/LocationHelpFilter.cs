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
        Network        The network's DNS domain, if it has one. Metered network
                       means the system marks it so, usually a phone's hotspot.
        Router maker   Who made the router, read from the first half of its
                       hardware address. Only shown here, never sent.
        Route          How internet traffic leaves this computer. It never
                       changes the place.
        VPN            VPN connections that are up.

        How to read Router maker

        Ubiquiti, Cisco, AVM (Fritz!Box) and other network makers
            Office or home equipment. It stays where it is, so a new place
            gets a new router.
        Apple, Samsung, Google and other phone makers
            Often a phone's hotspot. It goes with you, so every place you
            use it looks like the same place.
        GL.iNet
            A travel router. It also goes with you, VPN or not.
        VMware, Parallels, VirtualBox, QEMU, Hyper-V
            A virtual machine's network. It looks the same wherever the
            computer running the machine is.
        Unknown, the router uses a random address
            The router hides its maker. Phone hotspots often do this.
        Unknown
            A maker that is not in flextimed's short built-in list.

        The maker is a hint. To check a place, look at Here now in the web
        app. A place you have never been should show up as Not named yet.
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

        if (context.CommandArguments is ["-h" or "--help"])
        {
            Console.WriteLine(LocationHelp.Key);
        }
    }
}
