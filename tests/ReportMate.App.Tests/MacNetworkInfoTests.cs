using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

public class MacNetworkInfoTests
{
    // Shaped like the Mac client's network module: one interfaces row per address,
    // osquery flags as 0/1, the current Wi-Fi network as an object, quality as strings.
    private const string MacPayload = """
    {
      "activeConnection": { "connectionType": "Ethernet", "gateway": "", "interface": "en0", "ipAddress": "192.0.2.10", "isPrimary": 1 },
      "currentWiFiNetwork": {
        "channel": "36", "channel_band": "-1", "channel_width": "80", "interface": "en1", "mode": "Station",
        "noise": "-90", "rssi": "-60", "security_type": "WPA3 Personal", "ssid": "Sample-WiFi", "transmit_rate": "500.000000"
      },
      "dnsConfiguration": {
        "nameservers": ["192.0.2.53", "2001:db8::53", "192.0.2.54", "192.0.2.55", "2001:db8::54"],
        "searchDomains": ["example.org"]
      },
      "hostname": "sample-mac.example.org",
      "localHostname": "sample-mac.local",
      "interfaces": [
        { "name": "en0", "type": "Ethernet", "isUp": 0, "mtu": 1500, "macAddress": "00:00:5e:00:53:01", "addresses": [ { "family": "IPv6", "address": "fe80::1%en0" } ] },
        { "name": "en0", "type": "Ethernet", "isUp": 1, "mtu": 1500, "macAddress": "00:00:5e:00:53:01", "addresses": [ { "family": "IPv4", "address": "192.0.2.10" } ] },
        { "name": "en0", "type": "Ethernet", "isUp": 0, "mtu": 1500, "macAddress": "00:00:5e:00:53:01", "addresses": [ { "family": "IPv6", "address": "2001:db8::10" } ] },
        { "name": "en1", "type": "WiFi", "isUp": 1, "mtu": 1500, "macAddress": "00:00:5e:00:53:02", "addresses": [ { "family": "IPv4", "address": "192.0.2.11" } ] },
        { "name": "en2", "type": "Ethernet", "isUp": 0, "mtu": 1500, "macAddress": "00:00:5e:00:53:03", "addresses": [] },
        { "name": "bridge0", "type": "Bridge", "isUp": 0, "macAddress": "00:00:5e:00:53:04", "addresses": [ { "family": "IPv4", "address": "169.254.1.1" } ] }
      ],
      "networkQuality": { "dl_rating": "High", "dl_throughput": "760.088", "idle_latency": "27.950", "rating": "Medium", "ul_rating": "Medium", "ul_throughput": "791.754" },
      "routes": [],
      "vpnConnections": [
        { "name": "Office VPN", "status": "Connected", "type": "IPSec" },
        { "name": "Mesh", "status": "Disconnected", "type": "VPN" },
        { "name": "Unknown VPN", "status": "Connected" }
      ],
      "wifiInfo": {
        "knownNetworks": [
          { "ssid": "Sample-WiFi", "securityType": "Unknown", "isHidden": 0 },
          { "ssid": "Guest", "securityType": "WPA2 Personal", "isHidden": 0 }
        ],
        "powerStatus": 1
      }
    }
    """;

    private static MacNetworkInfo Read(string json) =>
        MacNetworkInfo.Read(JsonDocument.Parse(json).RootElement)!;

    [Fact]
    public void AbsentModuleReadsAsNull()
    {
        Assert.Null(MacNetworkInfo.Read(null));
        Assert.Null(MacNetworkInfo.Read(JsonDocument.Parse("null").RootElement));
    }

    [Fact]
    public void ActiveConnectionComesFromTheModule()
    {
        var net = Read(MacPayload);
        Assert.Equal("192.0.2.10", net.IpAddress);
        Assert.Equal("en0", net.InterfaceName);
        Assert.Equal("Ethernet", net.ConnectionType);
        Assert.Null(net.Gateway);
        Assert.Equal("00:00:5e:00:53:01", net.MacAddress);
    }

    [Fact]
    public void InterfacesMergeByNameAndDropVirtualAdapters()
    {
        var net = Read(MacPayload);
        Assert.Equal(["en0", "en1", "en2"], net.Interfaces.Select(i => i.Name));
        var en0 = net.Interfaces[0];
        Assert.True(en0.IsActive);
        Assert.Equal(3, en0.IpAddresses.Count);
        Assert.Equal("192.0.2.10", en0.Ipv4);
        Assert.Equal("2001:db8::10", en0.Ipv6);
        Assert.Equal(1500, en0.Mtu);
        Assert.False(net.Interfaces[2].IsActive);
        Assert.Equal("Disconnected", net.Interfaces[2].Status);
    }

    [Fact]
    public void WirelessInterfaceTakesTheCurrentNetworkDetails()
    {
        var net = Read(MacPayload);
        var wifi = net.WirelessInterface!;
        Assert.Equal("en1", wifi.Name);
        Assert.Equal("Sample-WiFi", wifi.Ssid);
        Assert.Equal("36", wifi.Channel);
        Assert.Null(wifi.WirelessBand);
        Assert.Null(wifi.WirelessProtocol);
        Assert.Equal("-60 dBm", net.WifiInterface!.SignalStrength);
        Assert.Equal("192.0.2.11", net.WifiInterface.IpAddress);
        Assert.Equal("WPA3 Personal", MacJson.Str(net.CurrentWiFi, "security_type"));
    }

    [Fact]
    public void DnsKeepsTwoIPv4AndOneIPv6ServerOnActiveInterfaces()
    {
        var net = Read(MacPayload);
        Assert.Equal(["192.0.2.53", "192.0.2.54", "2001:db8::53"], net.ActiveDnsServers);
        Assert.Equal(net.ActiveDnsServers, net.Interfaces[0].DnsServers);
        Assert.Empty(net.Interfaces[2].DnsServers);
        Assert.Equal(["example.org"], net.SearchDomains);
    }

    [Fact]
    public void VpnListSkipsUnnamedAndMarksTheConnectedOne()
    {
        var net = Read(MacPayload);
        Assert.Equal(["Office VPN", "Mesh"], net.VpnConnections.Select(v => v.Name));
        Assert.True(net.VpnActive);
        Assert.Equal("Office VPN", net.VpnName);
        Assert.False(net.VpnConnections[1].IsConnected);
    }

    [Fact]
    public void KnownNetworksMarkTheCurrentSsidConnected()
    {
        var net = Read(MacPayload);
        Assert.Equal("Sample-WiFi", net.ActiveWifiSsid);
        Assert.Equal(2, net.WifiNetworks.Count);
        Assert.True(net.WifiNetworks[0].IsConnected);
        Assert.Equal("Unknown", net.WifiNetworks[0].Security);
        Assert.False(net.WifiNetworks[1].IsConnected);
        Assert.Equal("WPA2 Personal", net.WifiNetworks[1].Security);
    }

    [Fact]
    public void QualityFromParsedFieldsRoundsToWholeUnits()
    {
        var q = Read(MacPayload).NetworkQuality!;
        Assert.Equal("760 Mbps", q.DownlinkCapacity);
        Assert.Equal("792 Mbps", q.UplinkCapacity);
        Assert.Equal("28 ms", q.IdleLatency);
        Assert.Equal("High", q.DownlinkResponsiveness);
        Assert.Equal("Medium", q.UplinkResponsiveness);
    }

    [Fact]
    public void QualityFromCommandOutputIsParsed()
    {
        var net = Read("""
        { "networkQuality": { "output": "==== SUMMARY ====\nUplink capacity: 40.512 Mbps\nDownlink capacity: 310.2 Mbps\nUplink Responsiveness: Medium (900 RPM)\nDownlink Responsiveness: High (2000 RPM)\nIdle Latency: 18.4 milliseconds" } }
        """);
        var q = net.NetworkQuality!;
        Assert.Equal("41 Mbps", q.UplinkCapacity);
        Assert.Equal("310 Mbps", q.DownlinkCapacity);
        Assert.Equal("Medium", q.UplinkResponsiveness);
        Assert.Equal("High", q.DownlinkResponsiveness);
        Assert.Equal("18 ms", q.IdleLatency);
    }

    [Fact]
    public void HostnamesAndDnsAddress()
    {
        var net = Read(MacPayload);
        Assert.Equal("sample-mac.local", net.LocalHostname);
        Assert.Equal("sample-mac.example.org", net.Hostname);
        Assert.Equal("sample-mac.example.org", net.DnsAddress);
    }

    [Fact]
    public void HostnameFallsBackToSystemEnvironment()
    {
        var system = JsonDocument.Parse("""{ "environment": [ { "name": "HOSTNAME", "value": "sample-host" } ] }""").RootElement;
        var net = MacNetworkInfo.Read(JsonDocument.Parse("""{ "interfaces": [] }""").RootElement, system)!;
        Assert.Equal("sample-host", net.Hostname);
    }

    [Fact]
    public void TunnelPrimaryShowsThePhysicalInterfaceUnderneath()
    {
        var net = Read("""
        {
          "activeConnection": { "interface": "utun3", "ipAddress": "198.51.100.2" },
          "interfaces": [
            { "name": "utun3", "type": "Other", "addresses": [ { "family": "IPv4", "address": "198.51.100.2" } ] },
            { "name": "en0", "type": "WiFi", "macAddress": "00:00:5e:00:53:05", "addresses": [ { "family": "IPv4", "address": "192.0.2.20" } ] }
          ]
        }
        """);
        Assert.Equal("en0", net.InterfaceName);
        Assert.Equal("192.0.2.20", net.IpAddress);
        Assert.Equal("WiFi", net.ConnectionType);
        Assert.True(net.VpnActive);
        Assert.Equal("00:00:5e:00:53:05", net.MacAddress);
    }

    [Fact]
    public void WifiOutputTextIsParsedAndLocationPlaceholderFallsBackToKnownNetwork()
    {
        var net = Read("""
        {
          "currentWiFiNetwork": { "output": "{\"ssid\":\"[Location Services Required]\",\"mode\":\"802.11ax\",\"channel\":\"149\",\"channel_band\":\"5\"}" },
          "interfaces": [ { "name": "en0", "type": "WiFi", "isUp": "1", "addresses": [ { "family": "IPv4", "address": "192.0.2.30" } ] } ],
          "wifiInfo": { "knownNetworks": [ { "ssid": "Sample-WiFi" } ] }
        }
        """);
        var wifi = net.WirelessInterface!;
        Assert.Equal("Sample-WiFi", wifi.Ssid);
        Assert.Equal("WiFi 6", wifi.WirelessProtocol);
        Assert.Equal("5", wifi.WirelessBand);
        Assert.Equal("149", wifi.Channel);
        Assert.Equal("Sample-WiFi", net.WifiInterface!.Ssid);
        Assert.Equal("WiFi 6", net.WifiInterface.ProtocolName);
        Assert.Equal("192.0.2.30", net.IpAddress);
    }

    [Theory]
    [InlineData("802.11be", "WiFi 7")]
    [InlineData("ax", "WiFi 6")]
    [InlineData("802.11ac", "WiFi 5")]
    [InlineData("802.11n", "WiFi 4")]
    [InlineData("Station", null)]
    public void WifiProtocolFromPhyMode(string mode, string? expected) =>
        Assert.Equal(expected, MacNetworkInfo.WifiProtocol(mode));
}
