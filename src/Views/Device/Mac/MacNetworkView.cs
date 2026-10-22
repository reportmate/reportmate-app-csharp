using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Network tab, laid out as the Mac app's NetworkTabView and the web's
/// NetworkTab lay it out: the hostname and a card per active connection on the left;
/// VPN, network quality, saved Wi-Fi and inactive interfaces on the right.
/// </summary>
public static class MacNetworkView
{
    // The same header glyph and accent as the Windows Network tab.
    private const string Glyph = "";
    private static readonly Accent Accent = Accent.Teal;

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("network", out var raw)) return null;
        JsonElement? system = s.RawModules.TryGetValue("system", out var sys) ? sys : null;
        if (MacNetworkInfo.Read(raw, system) is not { } net) return null;

        var active = net.Interfaces.Where(i => i.IsActive)
            .OrderBy(i => IsWired(i) ? 0 : 1).ToList();
        var inactive = net.Interfaces.Where(i => !i.IsActive).ToList();
        var connectedVpns = net.VpnConnections.Where(v => v.IsConnected).ToList();

        var page = new StackPanel();
        UIElement? right = null;
        if (active.Count > 0)
        {
            var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            p.Children.Add(Ui.Caption("Primary Connection"));
            var v = Ui.Text(active[0].IsWireless ? "Wireless" : "Ethernet");
            v.FontSize = 16; v.FontWeight = FontWeights.SemiBold; v.TextAlignment = TextAlignment.Right;
            p.Children.Add(v);
            right = p;
        }
        var subtitle = Format.Plural(active.Count, "active connection") + (connectedVpns.Count > 0 ? " • VPN connected" : "");
        page.Children.Add(Ui.TabHeader("Network", subtitle, Glyph, Accent, right));

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        // Left: hostname and a card per active connection
        var left = new StackPanel();
        void AddLeft(UIElement e) { if (e is FrameworkElement fe && left.Children.Count > 0) fe.Margin = new Thickness(0, 16, 0, 0); left.Children.Add(e); }
        if ((net.LocalHostname ?? net.Hostname) is { } hostName)
        {
            var host = new StackPanel();
            host.Children.Add(Ui.Caption(net.LocalHostname is not null ? "Local Hostname" : "Hostname"));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var name = Ui.Mono(hostName);
            name.FontSize = 20; name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0);
            row.Children.Add(name);
            row.Children.Add(Ui.CopyButton(hostName));
            host.Children.Add(row);
            if (net.LocalHostname is not null && net.Hostname is { } h && h != net.LocalHostname)
                host.Children.Add(Ui.Caption($"Hostname: {h}"));
            AddLeft(Ui.Card(host, new Thickness(20, 14, 20, 16)));
        }
        if (active.Count == 0)
            AddLeft(Ui.Card(Ui.EmptyState("No active network connections", "")));
        var dnsServers = net.ActiveDnsServers;
        foreach (var iface in active)
            AddLeft(ConnectionCard(iface, net, iface.IsWireless ? net.CurrentWiFi : null, dnsServers.Count > 0 ? dnsServers : iface.DnsServers));
        layout.Children.Add(left);

        // Right column
        var rightCol = new StackPanel();
        void AddRight(UIElement e) { if (e is FrameworkElement fe && rightCol.Children.Count > 0) fe.Margin = new Thickness(0, 16, 0, 0); rightCol.Children.Add(e); }

        if (net.VpnConnections.Count > 0)
        {
            var items = net.VpnConnections.Select(v =>
            {
                var sub = string.Join(" • ", new[] { v.Type is { Length: > 0 } and not "VPN" ? v.Type : null, v.ServerAddress }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return Ui.ListItem(v.Name, sub, v.IsConnected ? "Connected" : "Off", v.IsConnected ? Tone.Success : Tone.Neutral);
            });
            var badge = new StackPanel { Orientation = Orientation.Horizontal };
            if (connectedVpns.Count > 0) { var c = Ui.Pill($"{connectedVpns.Count} connected", Tone.Success); c.Margin = new Thickness(0, 0, 6, 0); badge.Children.Add(c); }
            badge.Children.Add(Ui.Pill(net.VpnConnections.Count.ToString()));
            AddRight(Ui.TableCard("VPN", null, new Border { Padding = new Thickness(20, 4, 20, 8), Child = Ui.List(items) }, badge));
        }

        if (net.NetworkQuality is { } q && (q.HasCapacity || q.IdleLatency is not null))
        {
            var grid = new StackPanel();
            if (q.DownlinkCapacity is { } d) grid.Children.Add(Ui.Row("Download", d));
            if (q.UplinkCapacity is { } u) grid.Children.Add(Ui.Row("Upload", u));
            if (q.IdleLatency is { } l) grid.Children.Add(Ui.Row("Latency", l));
            if (q.Jitter is { } j) grid.Children.Add(Ui.Row("Jitter", j));
            if (q.DownlinkResponsiveness is { } r) grid.Children.Add(Ui.Row("Quality", r));
            if (q.ServerName is { } server) grid.Children.Add(Ui.Row("Server", server, semibold: false));
            AddRight(Ui.StatBlock("Network Quality", q.Source, "", Accent.Teal, grid));
        }

        if (net.WifiNetworks.Count > 0)
        {
            var list = new StackPanel();
            void Fill(string? query)
            {
                list.Children.Clear();
                foreach (var w in net.WifiNetworks
                    .Where(w => string.IsNullOrWhiteSpace(query) || w.Ssid.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(w => w.Ssid, StringComparer.OrdinalIgnoreCase))
                    list.Children.Add(Ui.ListItem(w.Ssid, w.Security != "Unknown" ? w.Security : null, w.IsConnected ? "Connected" : null, Tone.Success));
                if (list.Children.Count == 0) list.Children.Add(Ui.Caption($"No networks match “{query}”"));
            }
            var search = new SearchBox("Search networks...", Fill)
            {
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Fill(null);
            var body = new StackPanel { Margin = new Thickness(20, 0, 20, 8) };
            body.Children.Add(search);
            body.Children.Add(new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
            AddRight(Ui.TableCard("Saved WiFi", null, body, Ui.Pill(net.WifiNetworks.Count.ToString())));
        }

        if (inactive.Count > 0)
        {
            var items = inactive.Select(i => Ui.ListItem(i.Name, i.MacAddress, value: i.Type));
            AddRight(Ui.Card(Ui.Collapsible($"{inactive.Count} Inactive", Ui.List(items)), new Thickness(20, 8, 20, 10)));
        }

        if (net.Routes.Count > 0)
        {
            var rows = net.Routes.Select(r => new RouteRow(r.Destination ?? "", r.Gateway ?? "", r.Interface ?? "", r.Metric ?? "")).ToList();
            AddRight(Ui.Card(Ui.Collapsible($"{rows.Count} Routes", Table.Build(rows,
                Col.Text("Destination", "Destination", star: true, mono: true),
                Col.Text("Gateway", "Gateway", 120, mono: true),
                Col.Text("Metric", "Metric", 60))), new Thickness(20, 8, 20, 10)));
        }

        Grid.SetColumn(rightCol, 2);
        layout.Children.Add(rightCol);
        page.Children.Add(layout);
        return page;
    }

    private sealed record RouteRow(string Destination, string Gateway, string Interface, string Metric);

    private static bool IsWired(MacNetworkInfo.Interface i)
    {
        var t = (i.Type ?? "").ToLowerInvariant();
        return t.Contains("ethernet") || t.Contains("wired");
    }

    private static Border ConnectionCard(MacNetworkInfo.Interface iface, MacNetworkInfo net, JsonElement? wifiData, IReadOnlyList<string> dnsServers)
    {
        var wifi = iface.IsWireless;
        var header = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8),
            Background = Ui.Brush(wifi ? "IconBlueBackground" : "IconEmeraldBackground"),
            Child = Ui.Icon("", 18, Ui.Brush(wifi ? "IconBlueForeground" : "IconEmeraldForeground")),
            Margin = new Thickness(0, 0, 12, 0),
        };
        ((FrameworkElement)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;
        title.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text(wifi ? "Wireless" : "Ethernet", "TitleTextStyle"));
        var sub = iface.Name + (!string.IsNullOrWhiteSpace(iface.FriendlyName) && iface.FriendlyName != iface.Name ? $" • {iface.FriendlyName}" : "");
        titles.Children.Add(Ui.Text(sub, "SubtitleTextStyle"));
        title.Children.Add(titles);
        header.Children.Add(title);
        var connected = Ui.Pill("Connected", Tone.Success);
        Grid.SetColumn(connected, 1);
        header.Children.Add(connected);

        var leftCol = new StackPanel();
        static void Row(StackPanel col, string label, string? value, bool mono = false, bool copy = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            UIElement v;
            if (copy)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal };
                var t = mono ? Ui.Mono(value) : Ui.Text(value);
                t.Margin = new Thickness(0); t.FontWeight = FontWeights.SemiBold;
                p.Children.Add(t);
                p.Children.Add(Ui.CopyButton(value));
                v = p;
            }
            else
            {
                var t = mono ? Ui.Mono(value) : Ui.Text(value);
                t.Margin = new Thickness(0); t.FontWeight = FontWeights.SemiBold; t.TextAlignment = TextAlignment.Right;
                v = t;
            }
            col.Children.Add(Ui.Row(label, v));
        }
        Row(leftCol, "IP Address", iface.Ipv4, mono: true, copy: true);
        Row(leftCol, "IPv6 Address", iface.Ipv6, mono: true, copy: true);
        Row(leftCol, "MAC Address", iface.MacAddress, mono: true, copy: true);
        for (var i = 0; i < dnsServers.Count; i++) Row(leftCol, i == 0 ? "DNS Server" : "", dnsServers[i], mono: true);
        if (iface.Name == net.InterfaceName) Row(leftCol, "Gateway", net.Gateway, mono: true);
        Row(leftCol, "Link Speed", iface.LinkSpeed);
        if (iface.Mtu is > 0) Row(leftCol, "MTU", iface.Mtu.Value.ToString());

        UIElement body;
        if (wifi)
        {
            var ssid = MacJson.Str(wifiData, "ssid") is { } s && s != "[Location Services Required]" ? s : iface.Ssid;
            var rssi = MacJson.Str(wifiData, "rssi");
            var rightCol = new StackPanel();
            Row(rightCol, "SSID", ssid);
            Row(rightCol, "Protocol", MacJson.Str(wifiData, "wifi_version") ?? iface.WirelessProtocol);
            Row(rightCol, "Band", MacNetworkInfo.Band(MacJson.Str(wifiData, "channel_band")) ?? iface.WirelessBand);
            Row(rightCol, "Channel", MacJson.Str(wifiData, "channel") ?? iface.Channel);
            Row(rightCol, "Signal", rssi is not null ? $"{rssi} dBm" : null);
            Row(rightCol, "Security", MacJson.Str(wifiData, "security", "security_type"));
            Row(rightCol, "Mode", MacJson.Str(wifiData, "phyMode", "phy_mode", "mode"));
            body = Ui.Columns(2, 24, leftCol, rightCol);
        }
        else
        {
            var traffic = new StackPanel();
            if (iface.BytesReceived is > 0) Row(traffic, "Received", Format.Bytes((long)iface.BytesReceived.Value));
            if (iface.BytesSent is > 0) Row(traffic, "Sent", Format.Bytes((long)iface.BytesSent.Value));
            body = traffic.Children.Count > 0 ? Ui.Columns(2, 24, leftCol, traffic) : leftCol;
        }

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        root.Children.Add(new Border { Padding = new Thickness(20, 10, 20, 14), Child = body });
        return new Border { Style = (Style)Ui.Res("CardStyle"), Child = root };
    }
}
