using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flextime.Daemon;

public sealed record InternetRoute(string Family, string LocalAddress, string Interface, string Kind, bool Tunnel);

public sealed record VpnService(string Name, string Status);

// The hardware address is kept for the place hash and never shown or
// written: the id is what the diagnostic prints.  The hint is the DNS
// domain of the router's network, valid as a place hint, or null.
public sealed record Router(
    string Address,
    string Interface,
    string? Id,
    [property: JsonIgnore] string? Mac = null,
    string? Hint = null);

// One network service's router as configd recorded it on macOS.
public sealed record ServiceRouter(string? Interface, string Router, string? Mac);

public sealed record NetworkReport(
    InternetRoute[] Routes,
    VpnService[]? VpnServices,
    Router[] Routers,
    bool Dependable,
    string Reason);

// What flextimed can tell about where it is on the network without any
// permission prompt: the interface internet traffic leaves through,
// whether that is a VPN tunnel, and a fingerprint of the router on the
// physical network.  A diagnostic for the location feature — nothing
// here is stored or sent.
public static partial class NetworkProbe
{
    // Public resolvers, used only to ask the routing table which way
    // internet traffic would go.  Connecting a UDP socket sends nothing.
    private static readonly IPAddress[] Targets =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("2606:4700:4700::1111"),
    ];

    // Interface names VPN clients create: utun (macOS, every
    // Network Extension VPN), tun/tap (OpenVPN), wg (WireGuard),
    // tailscale, zt (ZeroTier), cscotun (Cisco), gpd (GlobalProtect).
    private static readonly string[] TunnelNamePrefixes =
        ["utun", "ipsec", "ppp", "tun", "tap", "wg", "tailscale", "zt", "nordlynx", "cscotun", "gpd", "proton"];

    // Windows names adapters for people ("Ethernet 2"), so the driver
    // description is what gives a VPN away.  Some VPN adapters report
    // themselves as plain Ethernet.
    private static readonly string[] TunnelDescriptions =
    [
        "WireGuard", "Wintun", "TAP-Windows", "OpenVPN", "AnyConnect", "Secure Client", "GlobalProtect",
        "PANGP", "Fortinet", "Tailscale", "ZeroTier", "Juniper", "Pulse Secure", "Zscaler", "VPN",
    ];

    public static async Task<NetworkReport> Read()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();

        var routes = Targets
            .Select(target => FindRoute(target, interfaces))
            .OfType<InternetRoute>()
            .ToArray();

        var routers = new List<Router>();

        // Read once: every gateway below is looked up in the same table.
        var (serviceRouters, domains) = OperatingSystem.IsMacOS() ? ReadServices() : ([], null);

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || IsTunnel(nic.Name, nic.NetworkInterfaceType, nic.Description))
            {
                continue;
            }

            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                var address = gateway.Address;

                if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
                {
                    continue;
                }

                var mac = await GatewayMac(address, nic.Name, serviceRouters);

                var hint = domains != null ? domains.GetValueOrDefault(nic.Name) : SuffixHint(nic, routes);

                routers.Add(new Router(address.ToString(), nic.Name, mac == null ? null : RouterId(mac), mac, hint));
            }
        }

        var services = await ReadVpnServices();

        var (dependable, reason) = Judge(routes, services);

        return new NetworkReport(routes, services, routers.ToArray(), dependable, reason);
    }

    public static (bool dependable, string reason) Judge(InternetRoute[] routes, VpnService[]? services)
    {
        if (routes.Length == 0)
        {
            return (false, "No route to the internet.");
        }

        var tunnel = routes.FirstOrDefault(route => route.Tunnel);

        if (tunnel != null)
        {
            return (false, $"Internet traffic goes through {tunnel.Interface}, a VPN or other tunnel.");
        }

        var direct = routes[0].Interface;
        var connected = services?.Where(service => service.Status == "Connected").Select(service => service.Name).ToArray() ?? [];

        // A split-tunnel VPN carries only its own networks; traffic to
        // the internet still leaves from where the computer is.
        return connected.Length > 0
            ? (true, $"Internet traffic leaves through {direct} directly. {string.Join(", ", connected)} carries only its own networks.")
            : (true, $"Internet traffic leaves through {direct} directly.");
    }

    public static bool IsTunnel(string name, NetworkInterfaceType type, string description)
    {
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        return TunnelNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || TunnelDescriptions.Any(text => description.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // A stable id for a router that does not reveal its hardware
    // address.  The prefix keeps the hash from matching any other
    // table of hashed MAC addresses.
    public static string RouterId(string mac)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"flextime-router:{mac}"));

        return Convert.ToHexStringLower(hash, 0, 6);
    }

    // The first hardware address in arp output, normalized to
    // lowercase, colon separated, two digits a part.  macOS drops
    // leading zeros (0:1a:...), Windows separates with dashes.
    public static string? ParseMac(string text)
    {
        var match = MacPattern().Match(text);

        if (!match.Success)
        {
            return null;
        }

        var parts = match.Value.Split(':', '-').Select(part => part.PadLeft(2, '0').ToLowerInvariant()).ToArray();

        return parts.All(part => part == "00") ? null : string.Join(':', parts);
    }

    // `scutil --nc list` on macOS: one line per network service, VPNs
    // among them.  Names longer than 32 characters are cut off, closing
    // quote and all.
    public static VpnService[] ParseScutil(string text) =>
        text.Split('\n')
            .Select(line => ScutilPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => new VpnService(match.Groups["name"].Value.Trim(), match.Groups["status"].Value))
            .ToArray();

    // A service's IPv4 dictionary in configd, where it keeps the router
    // it resolved and that router's hardware address.  The keys are not
    // documented; NetworkSignature has carried the address for many
    // releases because macOS recognises known networks by it.  A tunnel
    // service has no router, or one without a hardware address.
    public static ServiceRouter? ParseServiceRouter(IReadOnlyDictionary<string, string> ipv4)
    {
        if (!ipv4.TryGetValue("Router", out var router) || string.IsNullOrWhiteSpace(router))
        {
            return null;
        }

        ipv4.TryGetValue("InterfaceName", out var interfaceName);

        string? mac = null;

        // The resolved address belongs to whichever IP address configd
        // resolved, which is the router unless it says otherwise.
        if (ipv4.TryGetValue("ARPResolvedHardwareAddress", out var resolved)
            && (!ipv4.TryGetValue("ARPResolvedIPAddress", out var resolvedFor) || resolvedFor == router))
        {
            mac = ParseMac(resolved);
        }

        if (mac == null && ipv4.TryGetValue("NetworkSignature", out var signature))
        {
            var parts = signature
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToLookup(pair => pair[0], pair => pair[1]);

            if (parts["IPv4.Router"].FirstOrDefault() is not { } signed || signed == router)
            {
                mac = parts["IPv4.RouterHardwareAddress"].Select(ParseMac).FirstOrDefault(item => item != null);
            }
        }

        return new ServiceRouter(interfaceName, router, mac);
    }

    // `nmcli -t -f NAME,TYPE,ACTIVE connection show` on Linux, where a
    // colon inside a name is escaped as \:.
    public static VpnService[] ParseNmcli(string text) =>
        text.Split('\n')
            .Select(line => NmcliSeparator().Split(line.TrimEnd('\r')))
            .Where(fields => fields.Length == 3 && fields[1] is "vpn" or "wireguard")
            .Select(fields => new VpnService(fields[0].Replace(@"\:", ":"), fields[2] == "yes" ? "Connected" : "Disconnected"))
            .ToArray();

    private static InternetRoute? FindRoute(IPAddress target, NetworkInterface[] interfaces)
    {
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            socket.Connect(new IPEndPoint(target, 53));

            if (socket.LocalEndPoint is not IPEndPoint local)
            {
                return null;
            }

            var nic = interfaces.FirstOrDefault(candidate =>
                candidate.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(local.Address)));

            var family = target.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";

            if (nic == null)
            {
                return new InternetRoute(family, local.Address.ToString(), "unknown", "Unknown", false);
            }

            var tunnel = IsTunnel(nic.Name, nic.NetworkInterfaceType, nic.Description);

            return new InternetRoute(family, local.Address.ToString(), nic.Name, Kind(nic.NetworkInterfaceType, tunnel), tunnel);
        }
        catch (SocketException)
        {
            // No route for this address family.
            return null;
        }
    }

    private static string Kind(NetworkInterfaceType type, bool tunnel) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet when !tunnel => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP",
        _ when tunnel => "Tunnel",
        _ => type.ToString(),
    };

    // The neighbour cache forgets an idle router after a minute or so.
    // When it has, one datagram to the router makes the system look its
    // hardware address up again; the router discards the datagram.
    private static async Task<string?> GatewayMac(IPAddress address, string interfaceName, ServiceRouter[] serviceRouters)
    {
        // macOS hides the neighbour cache from programs without the
        // Local Network permission, but configd records the router's
        // hardware address with each network service, and anyone may
        // read that.
        var known = serviceRouters.FirstOrDefault(service =>
                        service.Router == address.ToString() && service.Interface == interfaceName && service.Mac != null)
                    ?? serviceRouters.FirstOrDefault(service =>
                        service.Router == address.ToString() && service.Mac != null);

        if (known?.Mac != null)
        {
            return known.Mac;
        }

        var mac = await CachedMac(address);

        if (mac != null)
        {
            return mac;
        }

        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            await socket.SendToAsync(new byte[1], new IPEndPoint(address, 9));
        }
        catch (SocketException)
        {
            return null;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        return await CachedMac(address);
    }

    /// <summary>
    /// The DNS domain of each interface's network service, made a hint,
    /// from configd's per-service dictionaries: the IPv4 one names the
    /// interface, the DNS one with the same service id holds
    /// <c>DomainName</c>.  Interfaces without a valid one are left out.
    /// </summary>
    public static Dictionary<string, string> ParseServiceDomains(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values)
    {
        var domains = new Dictionary<string, string>();

        foreach (var (key, ipv4) in values)
        {
            if (!key.EndsWith("/IPv4", StringComparison.Ordinal)
                || !ipv4.TryGetValue("InterfaceName", out var interfaceName)
                || !values.TryGetValue($"{key[..^"/IPv4".Length]}/DNS", out var dns)
                || PlaceHints.Normalize(dns.GetValueOrDefault("DomainName")) is not { } hint)
            {
                continue;
            }

            domains.TryAdd(interfaceName, hint);
        }

        return domains;
    }

    // Where configd cannot be read, the interface's own DNS suffix.  On
    // Windows that is the adapter's; elsewhere .NET reads it from
    // /etc/resolv.conf for every interface alike, and while a tunnel
    // carries the traffic that is likely the VPN's domain, not the
    // network the router is on — no hint then.
    private static string? SuffixHint(NetworkInterface nic, InternetRoute[] routes)
    {
        if (!OperatingSystem.IsWindows() && routes.Any(route => route.Tunnel))
        {
            return null;
        }

        try
        {
            return PlaceHints.Normalize(nic.GetIPProperties().DnsSuffix);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or NetworkInformationException)
        {
            return null;
        }
    }

    // configd's routers and DNS domains; no domains (null) when the
    // store cannot be read, so the caller falls back to the DNS suffix.
    private static (ServiceRouter[] routers, Dictionary<string, string>? domains) ReadServices()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return ([], null);
        }

        var values = SystemConfiguration.ReadValues(SystemConfiguration.ServiceIPv4Pattern, SystemConfiguration.ServiceDnsPattern);

        if (values == null)
        {
            return ([], null);
        }

        var routers = values
            .Where(pair => pair.Key.EndsWith("/IPv4", StringComparison.Ordinal))
            .Select(pair => ParseServiceRouter(pair.Value))
            .OfType<ServiceRouter>()
            .ToArray();

        return (routers, ParseServiceDomains(values));
    }

    private static async Task<string?> CachedMac(IPAddress address)
    {
        if (OperatingSystem.IsLinux())
        {
            // IP address, HW type, Flags, HW address, Mask, Device.
            const string table = "/proc/net/arp";

            if (!File.Exists(table))
            {
                return null;
            }

            var line = (await File.ReadAllLinesAsync(table))
                .Skip(1)
                .FirstOrDefault(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == address.ToString());

            return line == null ? null : ParseMac(line);
        }

        var output = OperatingSystem.IsWindows()
            ? await Run(Path.Combine(Environment.SystemDirectory, "arp.exe"), "-a", address.ToString())
            : await Run("/usr/sbin/arp", "-n", address.ToString());

        return output == null ? null : ParseMac(output);
    }

    private static async Task<VpnService[]?> ReadVpnServices()
    {
        if (OperatingSystem.IsMacOS())
        {
            var output = await Run("/usr/sbin/scutil", "--nc", "list");

            return output == null ? null : ParseScutil(output);
        }

        if (OperatingSystem.IsLinux())
        {
            // NetworkManager installs nmcli in /usr/bin; a distribution
            // without it has no VPN list to give.
            var nmcli = new[] { "/usr/bin/nmcli", "/bin/nmcli", "/usr/local/bin/nmcli" }.FirstOrDefault(File.Exists);

            if (nmcli == null)
            {
                return null;
            }

            var output = await Run(nmcli, "-t", "-f", "NAME,TYPE,ACTIVE", "connection", "show");

            return output == null ? null : ParseNmcli(output);
        }

        // Windows lists only its built-in VPN connections; the adapter
        // check above is what finds third-party clients there.
        return null;
    }

    // System tools are named by full path where they have a fixed home:
    // a bare name is looked up in the application's own folder first.
    private static async Task<string?> Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);

            if (process == null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(output, error, process.WaitForExitAsync());

            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Win32Exception)
        {
            // Not installed here, like nmcli without NetworkManager.
            return null;
        }
    }

    [GeneratedRegex(@"(?<![0-9a-f])([0-9a-f]{1,2}[:-]){5}[0-9a-f]{1,2}(?![0-9a-f])", RegexOptions.IgnoreCase)]
    private static partial Regex MacPattern();

    [GeneratedRegex(@"^\*?\s*\((?<status>[^)]+)\)\s+[0-9A-Fa-f-]{36}\s+VPN\s+\([^)]*\)\s+""(?<name>.*?)""?\s+\[")]
    private static partial Regex ScutilPattern();

    [GeneratedRegex(@"(?<!\\):")]
    private static partial Regex NmcliSeparator();
}

public static class PrintNetwork
{
    public static async Task<int> Invoke(bool json)
    {
        var report = await NetworkProbe.Read();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, NetworkSourceGenerationContext.Default.NetworkReport));

            return 0;
        }

        foreach (var family in new[] { "IPv4", "IPv6" })
        {
            var route = report.Routes.FirstOrDefault(candidate => candidate.Family == family);

            Console.WriteLine(route == null
                ? $"Internet ({family}) :  No route"
                : $"Internet ({family}) :  {route.Interface}, {route.Kind}, local address {route.LocalAddress}");
        }

        if (report.VpnServices == null)
        {
            Console.WriteLine("VPN services    :  Not listed on this platform");
        }
        else if (report.VpnServices.Length == 0)
        {
            Console.WriteLine("VPN services    :  None");
        }
        else
        {
            for (var i = 0; i < report.VpnServices.Length; i++)
            {
                var service = report.VpnServices[i];

                Console.WriteLine($"{(i == 0 ? "VPN services    :  " : "                   ")}{service.Name}, {service.Status}");
            }
        }

        if (report.Routers.Length == 0)
        {
            Console.WriteLine("Router          :  None found");
        }

        foreach (var router in report.Routers)
        {
            Console.WriteLine(router.Id == null
                ? $"Router          :  {router.Address} on {router.Interface}, hardware address not known yet"
                : $"Router          :  {router.Address} on {router.Interface}");

            if (router.Hint != null)
            {
                Console.WriteLine($"Network         :  {router.Hint}");
            }
        }

        Console.WriteLine($"Position        :  {(report.Dependable ? "Dependable" : "Not dependable")}. {report.Reason}");
        Console.WriteLine();
        Console.WriteLine("Nothing here is stored or sent; the router's hardware address is not shown.");

        return 0;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NetworkReport))]
internal partial class NetworkSourceGenerationContext : JsonSerializerContext;
