namespace Flextime.Daemon;

public enum MakerKind
{
    // Office or home network equipment, which stays where it is.
    Network,

    // A phone maker: often a hotspot, which travels with the computer.
    Phone,

    // A router made to be carried.
    Travel,

    // The network of a virtual machine, the same wherever the host is.
    Virtual,
}

/// <summary>
/// Who made a router, from the first three bytes of its hardware
/// address.  Only shown on this computer: sync sends the hashed id and
/// never the address or the maker.  The list is built in and short on
/// purpose (tools/router-makers.py), so most makers read as unknown.
/// </summary>
public static partial class RouterMakers
{
    private static readonly Lazy<byte[]> Table = new(() => Convert.FromBase64String(Packed));

    /// <summary>The maker of a lowercase, colon-separated address, or null when not in the list.</summary>
    public static (string Name, MakerKind Kind)? Find(string mac)
    {
        if (Prefix(mac) is not { } prefix)
        {
            return null;
        }

        var table = Table.Value;
        var low = 0;
        var high = table.Length / 4 - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            var at = middle * 4;
            var candidate = (table[at] << 16) | (table[at + 1] << 8) | table[at + 2];

            if (candidate == prefix)
            {
                return Makers[table[at + 3]];
            }

            if (candidate < prefix)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    /// <summary>
    /// A random address sets the locally administered bit, the second
    /// lowest of the first byte.  It names no maker.
    /// </summary>
    public static bool IsRandom(string mac) => Prefix(mac) is { } prefix && (prefix & 0x020000) != 0;

    /// <summary>The text after "Router maker :", or null when the address is not known.</summary>
    public static string? Describe(string? mac)
    {
        if (mac == null)
        {
            return null;
        }

        // QEMU's prefix is locally administered too, so look it up first.
        if (Find(mac) is { } maker)
        {
            return maker.Kind switch
            {
                MakerKind.Phone => $"{maker.Name}, often a phone's hotspot that travels with you",
                MakerKind.Travel => $"{maker.Name}, a travel router that goes where you go",
                MakerKind.Virtual => $"{maker.Name}, a virtual machine's network",
                _ => maker.Name,
            };
        }

        return IsRandom(mac) ? "Unknown, the router uses a random address" : "Unknown";
    }

    private static int? Prefix(string mac)
    {
        var parts = mac.Split(':');

        if (parts.Length < 3)
        {
            return null;
        }

        var prefix = 0;

        foreach (var part in parts.AsSpan(0, 3))
        {
            if (!byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                return null;
            }

            prefix = (prefix << 8) | value;
        }

        return prefix;
    }
}
