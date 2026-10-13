using System.Net.NetworkInformation;
using Flextime.Daemon;

namespace Test.Flextime;

public class NetworkProbeTests
{
    [Theory]
    [InlineData("? (192.168.4.1) at 0:1a:2b:c:d4:e5 on en0 ifscope [ethernet]", "00:1a:2b:0c:d4:e5")]
    [InlineData("  192.168.4.1           00-1a-2b-0c-d4-e5     dynamic", "00:1a:2b:0c:d4:e5")]
    [InlineData("192.168.4.1      0x1         0x2         00:1A:2B:0C:D4:E5     *        wlp2s0", "00:1a:2b:0c:d4:e5")]
    [InlineData("192.168.4.1      0x1         0x0         00:00:00:00:00:00     *        wlp2s0", null)]
    [InlineData("? (192.168.4.1) at (incomplete) on en0 ifscope [ethernet]", null)]
    public void ParseMac(string text, string? expected)
    {
        Assert.Equal(expected, NetworkProbe.ParseMac(text));
    }

    [Fact]
    public void ParseScutil()
    {
        const string text = """
            Available network connection services in the current set (*=enabled):
            * (Disconnected)   26D6C04A-0D77-4763-B4D8-DCE8458B61C2 PPP --> Monitor Controls "Monitor Controls"            [PPP:Modem]
            * (Connected)      B7402CD6-01F8-48EB-8E47-9E4CDF0098A2 VPN (com.wireguard.macos) "Office"                      [VPN:com.wireguard.macos]
            * (Disconnected)   0A51B1D8-EB9D-43B4-BF5A-FB507F22301B VPN (com.example.vpn) "A name long enough to be cut o [VPN:com.example.vpn]
            """;

        var services = NetworkProbe.ParseScutil(text);

        Assert.Equal(
            [new VpnService("Office", "Connected"), new VpnService("A name long enough to be cut o", "Disconnected")],
            services);
    }

    // The string values configd holds for a Wi-Fi service, as
    // SCDynamicStoreCopyValue returns them (Addresses and the other
    // arrays are left out by the reader).
    private static Dictionary<string, string> WiFi() => new()
    {
        ["ARPResolvedHardwareAddress"] = "0:1a:2b:c:d4:e5",
        ["ARPResolvedIPAddress"] = "192.168.4.1",
        ["ConfirmedInterfaceName"] = "en0",
        ["InterfaceName"] = "en0",
        ["NetworkSignature"] = "IPv4.Router=192.168.4.1;IPv4.RouterHardwareAddress=0:1a:2b:c:d4:e5",
        ["Router"] = "192.168.4.1",
    };

    [Fact]
    public void ParseServiceRouter()
    {
        Assert.Equal(
            new ServiceRouter("en0", "192.168.4.1", "00:1a:2b:0c:d4:e5"),
            NetworkProbe.ParseServiceRouter(WiFi()));
    }

    [Fact]
    public void ParseServiceRouter_falls_back_to_the_network_signature()
    {
        var ipv4 = WiFi();

        ipv4.Remove("ARPResolvedHardwareAddress");
        ipv4.Remove("ARPResolvedIPAddress");

        Assert.Equal("00:1a:2b:0c:d4:e5", NetworkProbe.ParseServiceRouter(ipv4)?.Mac);
    }

    [Fact]
    public void ParseServiceRouter_ignores_an_address_resolved_for_another_host()
    {
        var ipv4 = WiFi();

        ipv4["ARPResolvedIPAddress"] = "192.168.4.7";
        ipv4["ARPResolvedHardwareAddress"] = "aa:bb:cc:dd:ee:ff";

        Assert.Equal("00:1a:2b:0c:d4:e5", NetworkProbe.ParseServiceRouter(ipv4)?.Mac);
    }

    [Fact]
    public void ParseServiceRouter_router_not_resolved_yet()
    {
        var ipv4 = new Dictionary<string, string>
        {
            ["InterfaceName"] = "en0",
            ["Router"] = "192.168.4.1",
        };

        Assert.Equal(new ServiceRouter("en0", "192.168.4.1", null), NetworkProbe.ParseServiceRouter(ipv4));
    }

    [Fact]
    public void ParseServiceRouter_tunnel_has_no_router()
    {
        var ipv4 = new Dictionary<string, string>
        {
            ["InterfaceName"] = "utun4",
        };

        Assert.Null(NetworkProbe.ParseServiceRouter(ipv4));
    }

    [Fact]
    public void ParseServiceRouter_tunnel_router_has_no_hardware_address()
    {
        // Some VPN clients name a router for their tunnel; there is no
        // link layer behind it, so no hardware address either.
        var ipv4 = new Dictionary<string, string>
        {
            ["InterfaceName"] = "utun4",
            ["Router"] = "10.8.0.1",
        };

        Assert.Null(NetworkProbe.ParseServiceRouter(ipv4)?.Mac);
    }

    [Fact]
    public void ParseNmcli()
    {
        const string text = "Home Wi-Fi:802-11-wireless:yes\nOffice\\: Stockholm:vpn:no\nwg0:wireguard:yes\n";

        var services = NetworkProbe.ParseNmcli(text);

        Assert.Equal(
            [new VpnService("Office: Stockholm", "Disconnected"), new VpnService("wg0", "Connected")],
            services);
    }

    [Theory]
    [InlineData("utun4", NetworkInterfaceType.Unknown, "utun4", true)]
    [InlineData("en0", NetworkInterfaceType.Wireless80211, "en0", false)]
    [InlineData("wg0", NetworkInterfaceType.Unknown, "wg0", true)]
    [InlineData("tailscale0", NetworkInterfaceType.Unknown, "tailscale0", true)]
    [InlineData("wlp2s0", NetworkInterfaceType.Wireless80211, "wlp2s0", false)]
    [InlineData("Ethernet 2", NetworkInterfaceType.Ethernet, "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", true)]
    [InlineData("Ethernet", NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Connection I219-LM", false)]
    [InlineData("Local Area Connection", NetworkInterfaceType.Ppp, "WAN Miniport (PPTP)", true)]
    public void IsTunnel(string name, NetworkInterfaceType type, string description, bool expected)
    {
        Assert.Equal(expected, NetworkProbe.IsTunnel(name, type, description));
    }

    [Fact]
    public void RouterId_is_stable_and_short()
    {
        var id = NetworkProbe.RouterId("00:1a:2b:0c:d4:e5");

        Assert.Equal(id, NetworkProbe.RouterId("00:1a:2b:0c:d4:e5"));
        Assert.NotEqual(id, NetworkProbe.RouterId("00:1a:2b:0c:d4:e6"));
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void Judge_tunnel_is_not_dependable()
    {
        var (dependable, reason) = NetworkProbe.Judge(
            [new InternetRoute("IPv4", "10.0.0.2", "utun4", "Tunnel", true)], null);

        Assert.False(dependable);
        Assert.Contains("utun4", reason);
    }

    [Fact]
    public void Judge_split_tunnel_is_dependable()
    {
        var (dependable, reason) = NetworkProbe.Judge(
            [new InternetRoute("IPv4", "192.168.4.20", "en0", "Wi-Fi", false)],
            [new VpnService("Tailscale", "Connected")]);

        Assert.True(dependable);
        Assert.Contains("Tailscale carries only its own networks", reason);
    }

    [Fact]
    public void Judge_offline_is_not_dependable()
    {
        var (dependable, _) = NetworkProbe.Judge([], null);

        Assert.False(dependable);
    }

    [Fact]
    public void ParseServiceDomains_joins_the_dns_domain_to_the_interface_by_service()
    {
        var values = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["State:/Network/Service/A/IPv4"] = new Dictionary<string, string> { ["InterfaceName"] = "en0", ["Router"] = "192.168.4.1" },
            ["State:/Network/Service/A/DNS"] = new Dictionary<string, string> { ["DomainName"] = "Omegapoint.se." },
            ["State:/Network/Service/B/IPv4"] = new Dictionary<string, string> { ["InterfaceName"] = "utun4" },
            ["State:/Network/Service/B/DNS"] = new Dictionary<string, string> { ["DomainName"] = "vpn.example" },
            ["State:/Network/Service/C/IPv4"] = new Dictionary<string, string> { ["InterfaceName"] = "en5" },
            ["State:/Network/Service/C/DNS"] = new Dictionary<string, string> { ["DomainName"] = "bad domain" },
            ["State:/Network/Service/D/IPv4"] = new Dictionary<string, string> { ["InterfaceName"] = "en6" },
        };

        Assert.Equal(
            new Dictionary<string, string> { ["en0"] = "omegapoint.se", ["utun4"] = "vpn.example" },
            NetworkProbe.ParseServiceDomains(values));
    }
}
