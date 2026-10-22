// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ReportMate.App.Services.MacModules.MacJson;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A Mac's network module read the way the Mac app reads it (ReportMateKit's
/// Processing/NetworkInfo.swift, itself a port of the web's network.ts): the active
/// connection, interfaces merged by name, Wi-Fi details, DNS, VPN and network quality.
/// </summary>
public sealed class MacNetworkInfo
{
    public sealed class Interface
    {
        public string Name { get; init; } = "Unknown";
        public string? FriendlyName { get; init; }
        public string? IpAddress { get; init; }
        public IReadOnlyList<string> IpAddresses { get; init; } = [];
        public string? MacAddress { get; init; }
        public string? Type { get; init; }
        public bool IsActive { get; init; }
        public string Status => IsActive ? "Active" : "Disconnected";
        public int? Mtu { get; init; }
        public string? LinkSpeed { get; init; }
        public string? WirelessProtocol { get; set; }
        public string? WirelessBand { get; set; }
        public string? Ssid { get; set; }
        public string? Channel { get; set; }
        public IReadOnlyList<string> DnsServers { get; set; } = [];
        public double? BytesSent { get; init; }
        public double? BytesReceived { get; init; }

        public bool IsWireless
        {
            get
            {
                var t = (Type ?? "").ToLowerInvariant();
                return t.Contains("wifi") || t.Contains("wireless") || (Name == "en0" && t.Length == 0);
            }
        }

        public bool IsEthernet
        {
            get
            {
                var t = (Type ?? "").ToLowerInvariant();
                return t.Contains("ethernet") || Name == "en1";
            }
        }

        /// <summary>The IPv4 address the card shows: the display address when it is IPv4, else the first IPv4.</summary>
        public string? Ipv4 => IsIPv4(IpAddress) ? IpAddress : IpAddresses.FirstOrDefault(IsIPv4);

        /// <summary>A routable IPv6 address, skipping link-local.</summary>
        public string? Ipv6 => IpAddresses.FirstOrDefault(ip => ip.Contains(':') && !ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase));
    }

    public sealed record WiFiInterface(string? Ssid, string? ProtocolName, string? Channel, string? Band,
        string? SignalStrength, string? IpAddress, string? MacAddress);

    public sealed record WiFiNetwork(string Ssid, string Security, bool IsConnected, string? Channel, string? SignalStrength);

    public sealed record Vpn(string Name, string? Type, string? Status, string? ServerAddress)
    {
        public bool IsConnected => string.Equals(Status, "connected", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record Route(string? Destination, string? Gateway, string? Interface, string? Metric);

    public sealed class Quality
    {
        public string? UplinkCapacity { get; set; }
        public string? DownlinkCapacity { get; set; }
        public string? UplinkResponsiveness { get; set; }
        public string? DownlinkResponsiveness { get; set; }
        public string? IdleLatency { get; set; }
        public string? Jitter { get; set; }
        public string? ServerName { get; set; }
        public string? Source { get; set; }
        public string? Raw { get; set; }
        public bool HasCapacity => UplinkCapacity is not null || DownlinkCapacity is not null;
    }

    public string? IpAddress { get; private set; }
    public string? MacAddress { get; private set; }
    public string? Hostname { get; private set; }
    public string? LocalHostname { get; private set; }
    public string? Gateway { get; private set; }
    public string? ConnectionType { get; private set; }
    public string? InterfaceName { get; private set; }
    public string? Ssid { get; private set; }
    public string? SignalStrength { get; private set; }
    public string? VpnName { get; private set; }
    public bool VpnActive { get; private set; }
    public IReadOnlyList<Vpn> VpnConnections { get; private set; } = [];
    public IReadOnlyList<string> DnsServers { get; private set; } = [];
    public IReadOnlyList<string> SearchDomains { get; private set; } = [];
    public IReadOnlyList<string> ActiveDnsServers { get; private set; } = [];
    public string? DnsAddress { get; private set; }
    public IReadOnlyList<Interface> Interfaces { get; private set; } = [];
    public IReadOnlyList<WiFiNetwork> WifiNetworks { get; private set; } = [];
    public WiFiInterface? WifiInterface { get; private set; }
    public string? ActiveWifiSsid { get; private set; }

    /// <summary>
    /// The current Wi-Fi network's details: the parsed `output` text when the client
    /// sent a command's output, otherwise the object itself (the Mac client's shape).
    /// </summary>
    public JsonElement? CurrentWiFi { get; private set; }
    public Quality? NetworkQuality { get; private set; }
    public IReadOnlyList<Route> Routes { get; private set; } = [];

    public IEnumerable<Interface> ActiveInterfaces => Interfaces.Where(i => i.IsActive);
    public Interface? EthernetInterface => ActiveInterfaces.FirstOrDefault(i => i.IsEthernet);
    public Interface? WirelessInterface => ActiveInterfaces.FirstOrDefault(i => i.IsWireless);

    private const string LocationServicesRequired = "[Location Services Required]";

    /// <summary>Null when the module is absent.</summary>
    public static MacNetworkInfo? Read(JsonElement? network, JsonElement? system = null)
    {
        var n = Unwrap(network);
        if (n is not { ValueKind: JsonValueKind.Object }) return null;
        var info = new MacNetworkInfo();
        info.Load(n.Value, system);
        return info;
    }

    private static JsonElement? Unwrap(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 1 ? a[0] : e;

    /// <summary>A JSON value given as a string of JSON, parsed; anything else as it is.</summary>
    private static JsonElement? Parsed(JsonElement? e)
    {
        if (e is not { ValueKind: JsonValueKind.String } s) return e;
        try
        {
            using var doc = JsonDocument.Parse(s.GetString() ?? "");
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Family(JsonElement a) => Str(a, "family") ?? "";
    private static string Address(JsonElement a) => Str(a, "address") ?? "";

    private void Load(JsonElement network, JsonElement? system)
    {
        var ifacesRaw = Arr(network, "interfaces");

        // Active connection
        var active = Node(network, "active_connection", "activeConnection");
        if (active is not null)
        {
            var primaryInterface = Str(active, "interface_name", "interfaceName", "interface", "friendly_name", "friendlyName");
            var primaryIP = Str(active, "ip_address", "ipAddress");
            var primaryType = Str(active, "connection_type", "connectionType");
            var isVpnTunnel = (primaryInterface ?? "").StartsWith("utun", StringComparison.Ordinal) || (primaryInterface ?? "").StartsWith("ppp", StringComparison.Ordinal);
            if (isVpnTunnel && ifacesRaw.Count > 0)
            {
                // A tunnel holds the route: show the physical interface underneath.
                var physical = ifacesRaw.Cast<JsonElement?>().FirstOrDefault(iface =>
                {
                    var hasIP = Arr(iface, "addresses").Any(a => Family(a) == "IPv4" && Address(a).Length > 0 && !Address(a).StartsWith("127.", StringComparison.Ordinal))
                        || (Str(iface, "address") is { } addr && !addr.StartsWith("127.", StringComparison.Ordinal));
                    var t = Str(iface, "type") ?? "";
                    return hasIP && t is "Ethernet" or "WiFi" or "Wireless";
                });
                if (physical is not null)
                {
                    primaryInterface = Str(physical, "displayName", "name", "interface");
                    var ipv4 = Arr(physical, "addresses").Where(a => Family(a) == "IPv4").Select(a => Str(a, "address")).FirstOrDefault();
                    if (ipv4 is not null) primaryIP = ipv4;
                    else if (Str(physical, "address") is { } addr) primaryIP = addr;
                    primaryType = Str(physical, "type") ?? "Ethernet";
                }
            }
            IpAddress = primaryIP;
            MacAddress = Str(active, "mac_address", "macAddress");
            Gateway = Str(active, "gateway");
            ConnectionType = primaryType;
            InterfaceName = primaryInterface;
            Ssid = Str(active, "active_wifi_ssid", "activeWifiSsid");
            SignalStrength = Str(active, "wifi_signal_strength", "wifiSignalStrength");
            VpnName = Str(active, "vpn_name", "vpnName");
            VpnActive = Bool(active, "is_vpn_active", "isVpnActive") == true || isVpnTunnel;
            if (MacAddress is null && ifacesRaw.Count > 0)
            {
                static string? Mac(JsonElement? i) => Str(i, "macAddress", "mac_address", "mac");
                JsonElement? activeIface = ifacesRaw.Cast<JsonElement?>().FirstOrDefault(i => (Str(i, "name") ?? Str(i, "interface")) == InterfaceName);
                if (Mac(activeIface) is null)
                    activeIface = ifacesRaw.Cast<JsonElement?>().FirstOrDefault(i => Mac(i) is not null && (Str(i, "type") ?? "") is "Ethernet" or "WiFi" or "Wireless");
                MacAddress = Mac(activeIface);
            }
        }

        // VPN connections
        var vpns = new List<Vpn>();
        foreach (var v in Arr(network, "vpn_connections", "vpnConnections"))
        {
            var name = Str(v, "name");
            if (name is null || name == "Unknown VPN") continue;
            vpns.Add(new Vpn(name, Str(v, "type"), Str(v, "status"), Str(v, "serverAddress", "server_address")));
        }
        if (vpns.FirstOrDefault(v => v.IsConnected) is { } connected)
        {
            VpnActive = true;
            VpnName = connected.Name;
        }
        VpnConnections = vpns;

        // DNS
        var dnsConfig = Node(network, "dns", "dnsConfiguration", "dns_configuration");
        if (dnsConfig is not null)
        {
            var servers = Arr(dnsConfig, "servers", "nameservers")
                .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(x => x.Length > 0).ToList();
            DnsServers = servers;
            SearchDomains = Arr(dnsConfig, "searchDomains", "search_domains", "search")
                .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            if (servers.Count > 0)
            {
                var v4 = servers.Where(IsIPv4).Take(2);
                var v6 = servers.Where(x => x.Contains(':') && Regex.IsMatch(x, "^[0-9a-fA-F:]+$")).Take(1);
                ActiveDnsServers = v4.Concat(v6).Take(3).ToList();
            }
            if (Str(dnsConfig, "domainName") is { } domainName && Hostname is null) Hostname = domainName;
        }

        // The current Wi-Fi network, from a command's output or as an object.
        var current = Node(network, "currentWiFiNetwork");
        CurrentWiFi = Str(current, "output") is not null ? Parsed(Node(current, "output")) : current;
        if (CurrentWiFi is not { ValueKind: JsonValueKind.Object }) CurrentWiFi = null;
        var wifiDetails = CurrentWiFi;

        // Interfaces, deduplicated by name with their addresses merged
        if (ifacesRaw.Count > 0)
        {
            var order = new List<string>();
            var merged = new Dictionary<string, (JsonElement Iface, List<JsonElement> Addresses, bool IsUp, bool HasIPv4)>();
            foreach (var iface in ifacesRaw)
            {
                var name = Str(iface, "name", "interface") ?? "Unknown";
                var up = Bool(iface, "isUp") == true;
                if (!merged.TryGetValue(name, out var entry))
                {
                    order.Add(name);
                    entry = (iface, new List<JsonElement>(), up, false);
                }
                var addresses = Arr(iface, "addresses");
                entry.Addresses.AddRange(addresses);
                if (up) entry.IsUp = true;
                if (addresses.Any(a => Family(a) == "IPv4" && IsIPv4(Address(a)) && !Address(a).StartsWith("127.", StringComparison.Ordinal)))
                    entry.HasIPv4 = true;
                merged[name] = entry;
            }

            var all = new List<Interface>();
            foreach (var name in order)
            {
                var entry = merged[name];
                var iface = entry.Iface;
                var ips = Arr(iface, "ipAddresses").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                ips.AddRange(entry.Addresses.Select(a => Str(a, "address")).OfType<string>());
                if (ips.Count == 0 && Str(iface, "address") is { } single) ips.Add(single);
                var status = Str(iface, "status") ?? "";
                var flaggedActive = Bool(iface, "isActive") == true || Bool(iface, "is_active") == true;
                var isUp = status is "Up" or "Active" or "Connected" or "active" || entry.IsUp
                    || ips.Any(ip => IsIPv4(ip) && !ip.StartsWith("127.", StringComparison.Ordinal));
                string? display = null;
                if (ips.Count > 0)
                {
                    display = isUp || flaggedActive
                        ? ips.FirstOrDefault(IsIPv4) ?? ips[0]
                        : ips.FirstOrDefault(ip => !ip.StartsWith("fe80::", StringComparison.Ordinal) && !ip.StartsWith("169.254.", StringComparison.Ordinal) && !ip.StartsWith("127.", StringComparison.Ordinal)) ?? ips[0];
                }
                var hasValidIP = ips.Any(ip => IsIPv4(ip) && !ip.StartsWith("127.", StringComparison.Ordinal) && !ip.StartsWith("169.254.", StringComparison.Ordinal));
                var isActive = flaggedActive || entry.IsUp || entry.HasIPv4 || hasValidIP;
                all.Add(new Interface
                {
                    Name = Str(iface, "name", "interface", "friendly_name", "friendlyName") ?? "Unknown",
                    FriendlyName = Str(iface, "friendly_name", "friendlyName", "displayName"),
                    IpAddress = display,
                    IpAddresses = ips,
                    MacAddress = Str(iface, "mac_address", "macAddress", "mac"),
                    Type = Str(iface, "type"),
                    IsActive = isActive,
                    Mtu = Int(iface, "mtu"),
                    LinkSpeed = Str(iface, "link_speed", "linkSpeed"),
                    WirelessProtocol = Str(iface, "wireless_protocol", "wirelessProtocol"),
                    WirelessBand = Str(iface, "wireless_band", "wirelessBand"),
                    BytesSent = Num(iface, "bytes_sent", "bytesSent"),
                    BytesReceived = Num(iface, "bytes_received", "bytesReceived"),
                });
            }

            // Leave out virtual adapters; en<n> is always a real one on a Mac.
            string[] virtualMacs = ["00:15:5d", "00:50:56", "08:00:27", "0a:00:27"];
            var physical = all.Where(i =>
            {
                if (Regex.IsMatch(i.Name, @"^en\d+$")) return true;
                var mac = (i.MacAddress ?? "").ToLowerInvariant();
                var virtualMac = virtualMacs.Any(mac.StartsWith);
                var virtualIP = i.IpAddresses.Any(ip => ip.StartsWith("172.", StringComparison.Ordinal) || ip.StartsWith("10.0.75.", StringComparison.Ordinal) || ip.StartsWith("169.254.", StringComparison.Ordinal));
                return !virtualMac && !virtualIP;
            })
            .OrderBy(i => i.IsActive ? 0 : 1)
            .ThenBy(i => i.IsActive && i.Type == "Wireless" ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

            // The Wi-Fi interface takes the current network's details.
            if (wifiDetails is not null)
            {
                var detailsIface = Str(wifiDetails, "interface");
                var w = physical.FirstOrDefault(i => (detailsIface is not null && i.Name == detailsIface) || i.IsWireless);
                if (w is not null)
                {
                    w.WirelessBand ??= Band(Str(wifiDetails, "channel_band"));
                    w.Channel ??= Str(wifiDetails, "channel");
                    if (w.WirelessProtocol is null && Str(wifiDetails, "mode") is { } mode) w.WirelessProtocol = WifiProtocol(mode);
                    w.WirelessProtocol ??= Str(wifiDetails, "wifi_version");
                    var ssid = Str(wifiDetails, "ssid");
                    if (ssid is not null && ssid != LocationServicesRequired) w.Ssid = ssid;
                    else if (ssid == LocationServicesRequired
                        && Arr(network, "wifiInfo.knownNetworks").Select(k => Str(k, "ssid")).FirstOrDefault() is { } known)
                        w.Ssid = known;
                }
            }
            if (ActiveDnsServers.Count > 0)
                foreach (var i in physical.Where(i => i.IsActive)) i.DnsServers = ActiveDnsServers;
            Interfaces = physical;
            if (active is null && physical.FirstOrDefault(i => i.IsActive) is { } first)
            {
                IpAddress ??= first.IpAddress;
                MacAddress ??= first.MacAddress;
                InterfaceName ??= first.FriendlyName ?? first.Name;
                ConnectionType ??= first.Type;
                if (first.WirelessProtocol is { } proto) ConnectionType = proto;
            }
        }

        // Wi-Fi networks
        var listed = Arr(network, "wifiNetworks");
        var wifiInfo = Node(network, "wifiInfo");
        if (listed.Count > 0)
        {
            WifiNetworks = listed.Select(x => ToWifiNetwork(x, null)).ToList();
        }
        else if (wifiInfo is not null)
        {
            string? activeSSID = null;
            if (Str(CurrentWiFi, "ssid") is { } s && s != LocationServicesRequired) activeSSID = s;
            activeSSID ??= Str(wifiInfo, "currentNetwork.ssid");
            ActiveWifiSsid = activeSSID;
            var known = Arr(wifiInfo, "knownNetworks");
            var available = Arr(wifiInfo, "availableNetworks");
            if (known.Count > 0) WifiNetworks = known.Select(x => ToWifiNetwork(x, activeSSID)).ToList();
            else if (available.Count > 0) WifiNetworks = available.Select(x => ToWifiNetwork(x, null)).ToList();

            var currentNetwork = Node(wifiInfo, "currentNetwork");
            if (currentNetwork is not null)
            {
                Ssid ??= Str(currentNetwork, "ssid", "networkName");
                if (SignalStrength is null && Str(currentNetwork, "rssi") is { } rssi) SignalStrength = $"{rssi} dBm";
            }
            var proto = Str(wifiInfo, "wifiProtocol") ?? (Str(wifiInfo, "phyMode") is { } phy ? WifiProtocol(phy) : null);
            if (proto is not null && ConnectionType == "WiFi") ConnectionType = proto;

            if (CurrentWiFi is not null)
            {
                var ssid = Str(CurrentWiFi, "ssid");
                WifiInterface = new WiFiInterface(
                    ssid == LocationServicesRequired ? null : ssid,
                    Str(CurrentWiFi, "mode") is { } mode ? WifiProtocol(mode) : null,
                    Str(CurrentWiFi, "channel"), Band(Str(CurrentWiFi, "channel_band")),
                    Str(CurrentWiFi, "rssi") is { } rssi ? $"{rssi} dBm" : null, null, null);
            }
            if (WifiInterface is null && currentNetwork is not null)
            {
                WifiInterface = new WiFiInterface(Str(currentNetwork, "ssid", "networkName"), null,
                    Str(currentNetwork, "channel"), null,
                    Str(currentNetwork, "rssi") is { } rssi ? $"{rssi} dBm" : null, null, null);
            }
        }
        if (WifiInterface is null && WirelessInterface is { } wi)
        {
            WifiInterface = new WiFiInterface(wi.Ssid, wi.WirelessProtocol, wi.Channel, wi.WirelessBand, null, wi.IpAddress, wi.MacAddress);
        }
        else if (WifiInterface is { } wf && WirelessInterface is { } iface)
        {
            WifiInterface = wf with
            {
                IpAddress = wf.IpAddress ?? iface.IpAddress,
                MacAddress = wf.MacAddress ?? iface.MacAddress,
                Ssid = wf.Ssid ?? iface.Ssid,
                ProtocolName = wf.ProtocolName ?? iface.WirelessProtocol,
            };
        }

        Routes = Arr(network, "routes").Select(r => new Route(
            Str(r, "destination"), Str(r, "gateway"), Str(r, "interface"), Str(r, "metric"))).ToList();

        // Hostname
        LocalHostname = Str(network, "localHostname", "local_hostname");
        if (Str(network, "hostname") is { } h) Hostname = h;
        else if (Hostname is null)
        {
            var env = Arr(system, "environment");
            Hostname = env.Where(e => Str(e, "name") is "COMPUTERNAME" or "HOSTNAME").Select(e => Str(e, "value")).FirstOrDefault(v => v is not null);
        }
        if (Hostname is { } host)
        {
            var domain = Str(network, "domain") ?? Str(network, "dns.domain") ?? Str(network, "dns.dhcpDomain");
            DnsAddress = domain is not null ? $"{host}.{domain}" : host;
        }
        if (DnsAddress is null && ActiveDnsServers.Count > 0) DnsAddress = ActiveDnsServers[0];

        // Network quality: `networkQuality` text output, or the client's parsed fields.
        var nq = Node(network, "networkQuality", "network_quality");
        if (Str(nq, "output") is { } output)
        {
            string? Match(string pattern)
            {
                var m = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
                return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : null;
            }
            static double? D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            var q = new Quality { Raw = output };
            if (D(Match(@"Uplink capacity:\s*([\d.]+)\s*Mbps")) is { } up) q.UplinkCapacity = $"{Round(up)} Mbps";
            if (D(Match(@"Downlink capacity:\s*([\d.]+)\s*Mbps")) is { } down) q.DownlinkCapacity = $"{Round(down)} Mbps";
            q.UplinkResponsiveness = Match(@"Uplink Responsiveness:\s*(\w+)");
            q.DownlinkResponsiveness = Match(@"Downlink Responsiveness:\s*(\w+)");
            if (D(Match(@"Idle Latency:\s*([\d.]+)\s*milliseconds")) is { } lat) q.IdleLatency = $"{Round(lat)} ms";
            NetworkQuality = q;
        }
        else if (nq is not null && (Num(nq, "dlThroughput", "dl_throughput", "ulThroughput", "ul_throughput") is not null || Str(nq, "rating") is not null))
        {
            var q = new Quality();
            if (Num(nq, "dlThroughput", "dl_throughput") is { } dl) q.DownlinkCapacity = $"{Round(dl)} Mbps";
            if (Num(nq, "ulThroughput", "ul_throughput") is { } ul) q.UplinkCapacity = $"{Round(ul)} Mbps";
            q.DownlinkResponsiveness = Str(nq, "dlRating", "dl_rating");
            q.UplinkResponsiveness = Str(nq, "ulRating", "ul_rating");
            if (Num(nq, "idleLatency", "idle_latency") is { } l) q.IdleLatency = $"{Round(l)} ms";
            if (Num(nq, "jitter") is { } j) q.Jitter = $"{Round(j)} ms";
            q.ServerName = Str(nq, "serverName", "server_name");
            q.Source = Str(nq, "source");
            NetworkQuality = q;
        }

        if (IpAddress is null && Interfaces.FirstOrDefault(i => i.IsActive && IsIPv4(i.IpAddress)) is { } fallback)
        {
            IpAddress = fallback.IpAddress;
            MacAddress = fallback.MacAddress;
            InterfaceName = fallback.Name;
            if (fallback.Name == "en0") ConnectionType = "WiFi";
            else if (Regex.IsMatch(fallback.Name, @"^en\d+$")) ConnectionType = "Ethernet";
        }
    }

    private static long Round(double d) => (long)Math.Round(d, MidpointRounding.AwayFromZero);

    private static WiFiNetwork ToWifiNetwork(JsonElement net, string? activeSSID)
    {
        var ssid = Str(net, "ssid", "name", "networkName") ?? "";
        var connected = activeSSID is not null ? ssid == activeSSID : Bool(net, "isConnected") == true || Bool(net, "connected") == true;
        return new WiFiNetwork(ssid, Str(net, "security", "securityType") ?? "Unknown", connected,
            Str(net, "channel"), Str(net, "rssi", "signalStrength"));
    }

    /// <summary>
    /// A channel band as the client reports it ("2", "5", "6" GHz). The Mac client
    /// writes -1 when the radio would not say, which is no band at all.
    /// </summary>
    public static string? Band(string? band) => band is null or "-1" or "0" ? null : band;

    public static bool IsIPv4(string? s) => s is not null && Regex.IsMatch(s, @"^(\d{1,3}\.){3}\d{1,3}$");

    /// <summary>
    /// The Wi-Fi generation for an 802.11 PHY mode ("802.11ax" → "WiFi 6"). An
    /// interface mode such as "Station" is not a PHY mode and gives no protocol;
    /// the Mac app's reader reads its "n" as Wi-Fi 4.
    /// </summary>
    public static string? WifiProtocol(string mode)
    {
        var m = mode.Trim().ToLowerInvariant();
        if (m.StartsWith("802.11", StringComparison.Ordinal)) m = m[6..];
        if (m.Length == 0 || m.Length > 6 || !m.All(c => char.IsLetter(c) || c == '/' || c == ' ')) return null;
        if (m.Contains("be")) return "WiFi 7";
        if (m.Contains("ax")) return "WiFi 6";
        if (m.Contains("ac")) return "WiFi 5";
        if (m.Contains('n')) return "WiFi 4";
        return mode;
    }
}
