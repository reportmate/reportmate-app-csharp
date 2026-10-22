using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Info tab: the overview the web and Mac apps draw -- Inventory,
/// System, Hardware, Management, Security and Network -- in the same card grid the
/// Windows Info tab uses, each card laid out as the Mac app's InfoTab lays it out.
/// </summary>
public static class MacInfoView
{
    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!MacInfoSummary.HasAny(s.RawModules)) return null;
        var info = MacInfoSummary.Read(s.RawModules, s.RemoteName, s.RemoteSerial);

        var grid = Ui.CardGrid(400, 24);
        grid.MaxColumns = 6;
        grid.Children.Add(Inventory(info.Inventory, s.CreatedAt));
        grid.Children.Add(SystemCard(info.System));
        grid.Children.Add(Hardware(info.Hardware));
        grid.Children.Add(Management(info.Management));
        grid.Children.Add(Security(info.Security));
        grid.Children.Add(Network(info.Network));
        return grid;
    }

    private static Tone ToneOf(MacInfoSummary.Tone t) => t switch
    {
        MacInfoSummary.Tone.Success => Tone.Success,
        MacInfoSummary.Tone.Warning => Tone.Warning,
        MacInfoSummary.Tone.Error => Tone.Error,
        _ => Tone.Neutral,
    };

    private static FrameworkElement Spaced(FrameworkElement e, double top)
    {
        e.Margin = new Thickness(0, top, 0, 0);
        return e;
    }

    // ── Inventory ───────────────────────────────────────────────────

    private static UIElement Inventory(MacInventorySummary inv, DateTime? registered)
    {
        var identity = Ui.VStack(
            Ui.Stat("Device Name", inv.DeviceName),
            inv.AssetTag is not null ? Ui.Stat("Asset Tag", inv.AssetTag, mono: true, copy: true) : null,
            Ui.Stat("Serial Number", inv.SerialNumber, mono: true, copy: true),
            // When the device first reported, as the web's Inventory card shows it.
            registered is { } at ? Ui.Stat("Registered", Format.ShortDate(at)) : null);

        UIElement body = inv.Assignments.Count > 0
            ? Ui.Columns([3, 2], 24, identity, Ui.VStack(inv.Assignments.Select(a => (UIElement?)Ui.Stat(a.Label, a.Value)).ToArray()))
            : identity;
        return Ui.StatBlock("Inventory", "Device identity and assignment details", "", Accent.Blue, body);
    }

    // ── System ──────────────────────────────────────────────────────

    private static UIElement SystemCard(MacSystemSummary sys)
    {
        const string title = "System", subtitle = "Operating system details";
        if (!sys.HasData)
            return Ui.StatBlock(title, subtitle, "", Accent.Purple, Ui.EmptyState("System information not available"));

        var top = Ui.Columns(3, 16,
            Ui.Stat("macOS", sys.MarketingName),
            Ui.Stat("Version", sys.Version),
            Ui.Stat("Build", Format.OrUnknown(sys.Build)));

        var updates = new StackPanel();
        updates.Children.Add(Ui.Caption("Software Update"));
        switch (sys.Updates)
        {
            case MacSystemSummary.UpdateState.Pending:
                var line = Ui.HStack(Ui.Icon("", 14, Ui.StatusBrush(Tone.Warning)),
                    Ui.Status(Format.Plural(sys.UpdateCount, "Pending Update"), Tone.Warning));
                if (sys.DeferredCount > 0)
                {
                    var deferred = Ui.Caption($"+{sys.DeferredCount} deferred" + (sys.SoonestDeferral is { } d ? $" · {Format.ShortDate(d)}" : ""));
                    deferred.Foreground = Ui.StatusBrush(Tone.Orange);
                    deferred.VerticalAlignment = VerticalAlignment.Center;
                    line.Children.Add(deferred);
                }
                updates.Children.Add(line);
                break;
            case MacSystemSummary.UpdateState.DeferredOnly:
                updates.Children.Add(Ui.HStack(Ui.Icon("", 14, Ui.StatusBrush(Tone.Orange)),
                    Ui.Status($"{sys.UpdateCount} Update{(sys.UpdateCount == 1 ? "" : "s")} Deferred", Tone.Orange)));
                if (sys.SoonestDeferral is { } soonest)
                {
                    var available = Ui.Caption($"Available {Format.ShortDate(soonest)}");
                    available.Foreground = Ui.StatusBrush(Tone.Orange);
                    available.Margin = new Thickness(22, 2, 0, 0);
                    updates.Children.Add(available);
                }
                break;
            default:
                updates.Children.Add(Ui.HStack(Ui.Icon("", 14, Ui.StatusBrush(Tone.Success)),
                    Ui.Status("Up to Date", Tone.Success)));
                break;
        }

        var localisation = Ui.Columns([3, 2], 24,
            Ui.VStack(Ui.Stat("Keyboard Layout", Format.OrUnknown(sys.KeyboardLayout)), Ui.Stat("Time Zone", Format.OrUnknown(sys.TimeZone))),
            Ui.VStack(Ui.Stat("Uptime", Format.OrUnknown(sys.Uptime)), Ui.Stat("Locale", Format.OrUnknown(sys.Locale))));

        return Ui.StatBlock(title, subtitle, "", Accent.Purple,
            Ui.Stack(18, Orientation.Vertical, top, updates, localisation));
    }

    // ── Hardware ────────────────────────────────────────────────────

    private static UIElement Hardware(MacHardwareSummary hw)
    {
        const string title = "Hardware", subtitle = "Device specs";
        if (!hw.HasData)
            return Ui.StatBlock(title, subtitle, "", Accent.Orange, Ui.EmptyState("Hardware information not available"));

        var body = Ui.VStack(
            hw.Model is not null ? Ui.Stat("Model", hw.Model, sublabel: hw.ModelIdentifier) : null,
            Ui.Stat("Chip", Format.OrUnknown(hw.Chip)),
            Ui.Columns(2, 16,
                Ui.VStack(Ui.Stat("CPU", hw.CpuLabel), Ui.Stat("GPU", hw.GpuLabel)),
                Ui.VStack(Ui.Stat("RAM", hw.MemoryLabel), hw.HasNpu ? Ui.Stat("NPU", hw.NpuLabel) : null)),
            Ui.Stat("Storage", hw.StorageLabel),
            hw.BatteryLabel is { } battery ? Ui.Stat("Battery", battery) : null);
        return Ui.StatBlock(title, subtitle, "", Accent.Orange, body);
    }

    // ── Management ──────────────────────────────────────────────────

    private static UIElement Management(MacManagementSummary m)
    {
        const string title = "Management", subtitle = "Device Management Service";
        if (!m.HasData)
            return Ui.StatBlock(title, subtitle, "", Accent.Yellow, Ui.EmptyState("Management information not available"));

        var body = new StackPanel();
        if (m.Provider is not null)
        {
            var providerRow = Ui.Row("Provider", m.Provider, semibold: true);
            providerRow.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(providerRow);
        }
        body.Children.Add(Ui.StatusBadge("Enrollment", m.IsEnrolled ? "Enrolled" : "Not Enrolled", m.IsEnrolled ? Tone.Success : Tone.Error));
        if (m.EnrollmentType is not null)
            body.Children.Add(Ui.StatusBadge("Enrollment Type", m.EnrollmentType, ToneOf(m.EnrollmentTone)));
        if (m.IsSupervised is { } supervised)
            body.Children.Add(Ui.StatusBadge("Supervised", Format.YesNo(supervised), supervised ? Tone.Success : Tone.Warning));

        if (m.IsEnrolled)
        {
            if (m.ServerHost is not null) body.Children.Add(Spaced(Ui.Stat("Server", m.ServerHost, copy: true), 10));
            if (m.CertificateExpires is { } expires) body.Children.Add(Spaced(Ui.Stat("Certificate Expiry", Format.ShortDate(expires)), 10));
            else if (m.CertificateExpiresText is { } text) body.Children.Add(Spaced(Ui.Stat("Certificate Expiry", text), 10));
            if (m.Organization is not null) body.Children.Add(Spaced(Ui.Stat("Organization", m.Organization), 10));
            if (!m.ProviderIsIntune && m.HardwareUuid is not null)
                body.Children.Add(Spaced(Ui.Stat("Hardware UUID", m.HardwareUuid, mono: true, copy: true, truncate: true), 12));
        }
        return Ui.StatBlock(title, subtitle, "", Accent.Yellow, body);
    }

    // ── Security ────────────────────────────────────────────────────

    private static UIElement Security(MacSecuritySummary sec)
    {
        const string title = "Security", subtitle = "macOS protection status";
        if (!sec.HasData)
            return Ui.StatBlock(title, subtitle, "", Accent.Red, Ui.EmptyState("Security information not available"));

        var body = new StackPanel();
        foreach (var row in sec.Rows)
        {
            var badge = Ui.StatusBadge(row.Label, row.Value, ToneOf(row.Tone));
            badge.Margin = new Thickness(row.Indented ? 16 : 0, 3, 0, 3);
            body.Children.Add(badge);
        }
        return Ui.StatBlock(title, subtitle, "", Accent.Red, body);
    }

    // ── Network ─────────────────────────────────────────────────────

    private static UIElement Network(MacNetworkSummary net)
    {
        const string title = "Network", subtitle = "Connectivity and configuration";
        if (net.PrimaryIp is null)
            return Ui.StatBlock(title, subtitle, "", Accent.Teal, Ui.EmptyState("Network information not available"));

        var body = new StackPanel();
        void Add(FrameworkElement e) { if (body.Children.Count > 0) e.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(e); }

        var ethernet = net.Ethernet;
        var wifi = net.Wireless;
        if (ethernet is not null && wifi is not null)
        {
            Add(Ui.Columns(2, 16,
                Ui.Stat("Hostname", net.Hostname, mono: true, copy: true),
                net.WifiSsid is not null ? Ui.Stat("WiFi Name", net.WifiSsid) : null));
            Add(Ui.Columns(2, 16,
                Ui.VStack(
                    Ui.Stat("Wired IP Address", ethernet.IpAddress ?? net.IpAddress, mono: true, copy: true),
                    Ui.Stat("Wired MAC Address", ethernet.MacAddress ?? net.MacAddress, mono: true, copy: true)),
                Ui.VStack(
                    Ui.Stat("WiFi IP Address", net.WifiIpAddress ?? wifi.IpAddress, mono: true, copy: true),
                    Ui.Stat("WiFi MAC Address", net.WifiMacAddress ?? wifi.MacAddress, mono: true, copy: true))));
        }
        else if (ethernet is not null)
        {
            Add(Ui.Stat("Connection", "Ethernet"));
            Add(Ui.Stat("Hostname", net.Hostname, mono: true, copy: true));
            Add(Ui.Stat("IP Address", ethernet.IpAddress ?? net.IpAddress, mono: true, copy: true));
            Add(Ui.Stat("MAC Address", ethernet.MacAddress ?? net.MacAddress, mono: true, copy: true));
        }
        else if (wifi is not null)
        {
            Add(Ui.Stat("Connection", "Wireless"));
            Add(Ui.Stat("Hostname", net.Hostname, mono: true, copy: true));
            if ((net.WifiSsid ?? wifi.Ssid) is { } ssid)
            {
                var proto = net.WifiProtocol ?? wifi.WirelessProtocol;
                Add(Ui.Stat("SSID", proto is null ? ssid : $"{ssid} ({proto})"));
            }
            Add(Ui.Stat("IP Address", net.WifiIpAddress ?? wifi.IpAddress ?? net.IpAddress, mono: true, copy: true));
            Add(Ui.Stat("MAC Address", net.WifiMacAddress ?? wifi.MacAddress ?? net.MacAddress, mono: true, copy: true));
        }
        else
        {
            Add(Ui.Stat("Connection", Format.OrUnknown(net.ConnectionType)));
            if (net.Hostname is not null) Add(Ui.Stat("Hostname", net.Hostname, mono: true, copy: true));
            Add(Ui.Stat("IP Address", net.IpAddress, mono: true, copy: true));
            if (net.MacAddress is not null) Add(Ui.Stat("MAC Address", net.MacAddress, mono: true, copy: true));
        }

        if (net.VpnActive && net.VpnName is not null)
            Add(Ui.StatusBadge("VPN", net.VpnName, Tone.Info));

        if (net.HasQuality)
        {
            var quality = new StackPanel();
            quality.Children.Add(Ui.Eyebrow("Network Quality"));
            var grid = Ui.Columns(2, 8,
                Ui.VStack(
                    net.Download is not null ? Mini("Download", net.Download) : null,
                    net.Responsiveness is not null ? Mini("Responsiveness", net.Responsiveness) : null),
                Ui.VStack(
                    net.Upload is not null ? Mini("Upload", net.Upload) : null,
                    net.Latency is not null ? Mini("Latency", net.Latency) : null));
            grid.Margin = new Thickness(0, 4, 0, 0);
            quality.Children.Add(grid);
            Add(quality);
        }

        return Ui.StatBlock(title, subtitle, "", Accent.Teal, body);
    }

    private static FrameworkElement Mini(string label, string value)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Caption(label));
        panel.Children.Add(Ui.Text(value));
        return panel;
    }
}
