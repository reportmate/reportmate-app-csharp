using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;
using P = ReportMate.App.Services.MacModules.MacPeripheralsInfo;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Peripherals tab, as the web and Mac apps draw it: the category
/// filter strip, then one section per category of device cards. The cards match
/// the Windows tab's; the fields are the Mac client's.
/// </summary>
public static class MacPeripheralsView
{
    private static readonly Dictionary<string, Accent> Accents = new()
    {
        ["storage"] = Accent.Red, ["usb-thunderbolt"] = Accent.Blue, ["audio"] = Accent.Green, ["input"] = Accent.Purple,
        ["printers"] = Accent.Orange, ["scanners"] = Accent.Indigo, ["microphones"] = Accent.Pink, ["bluetooth"] = Accent.Cyan,
    };

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("peripherals", out var raw)) return null;
        var p = new P(raw);
        var page = new StackPanel();
        page.Children.Add(Ui.TabHeader("Peripherals", "Connected devices by category", "\uE88E", Accent.Cyan));

        if (!p.HasData)
        {
            page.Children.Add(Ui.Card(Ui.EmptyState("Peripheral device information is not available for this device. This could be because the device has not reported recently or the peripherals module is not enabled.", "")));
            return page;
        }

        string? filter = null;
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Render()
        {
            var root = new StackPanel();
            root.Children.Add(Ui.Card(Strip(p, filter, id => { filter = filter == id ? null : id; Render(); }), new Thickness(16, 16, 8, 8)));
            bool Visible(string id) => filter is null || filter == id;

            if (Visible("storage"))
                AddSection(root, "External Storage", p.ExternalStorageDevices.Select(d => DeviceCard(d.DisplayName, d.StorageType,
                    ("File System", d.FileSystem?.ToUpperInvariant()), ("Mount Point", d.MountPoint), ("Device", d.DevicePath), ("Size", d.TotalSize), ("Protocol", d.Protocol))));

            if (Visible("printers") && p.Printers.Count > 0)
            {
                var section = Section("Printers");
                foreach (var pr in p.Printers)
                {
                    var card = PrinterCard(pr);
                    card.Margin = new Thickness(0, 0, 0, 12);
                    section.Children.Add(card);
                }
                root.Children.Add(section);
            }

            if (Visible("audio"))
                AddSection(root, "Audio Output", p.AudioOutputs.Select(a => AudioCard(a, "Audio Output")));
            if (Visible("scanners"))
                AddSection(root, "Scanners", p.Scanners.Select(sc => DeviceCard(sc.Name, sc.ScannerType,
                    ("Manufacturer", sc.Manufacturer), ("Connection", sc.ConnectionType), ("Status", sc.Status))));
            if (Visible("microphones"))
                AddSection(root, "Microphones", p.Microphones.Select(a => AudioCard(a, "Microphone")));

            if (Visible("usb-thunderbolt") && p.UsbDevices.Count + p.ThunderboltDevices.Count > 0)
            {
                var section = Section("USB & Thunderbolt");
                AddGroup(section, "Thunderbolt", p.ThunderboltDevices.Select(t => DeviceCard(t.Name, t.DeviceType,
                    ("Vendor", t.Vendor), ("Device ID", t.DeviceId), ("UID", t.Uid))));
                AddGroup(section, "USB Devices", p.UsbDevices.Select(u => DeviceCard(u.Name, u.DeviceType,
                    ("Vendor", u.Vendor), ("Vendor ID", u.VendorId), ("Product ID", u.ProductId), ("Serial", u.SerialNumber),
                    u.LinkSpeed is { } link ? ("Link Speed", link) : ("Speed", u.Speed),
                    ("Location ID", u.LocationId), ("Power", u.PowerAllocated), ("USB Version", u.UsbVersion), ("Connection", u.ConnectionType))));
                root.Children.Add(section);
            }

            if (Visible("bluetooth") && p.BluetoothDevices.Count > 0)
            {
                var section = Section("Bluetooth Peripherals");
                AddGroup(section, "Connected", p.BluetoothDevices.Where(b => b.IsConnected).Select(b => DeviceCard(Or(b.Name, "Bluetooth Device"), "Connected", Tone.Success,
                    ("Category", b.DeviceCategory), ("Address", b.Address), ("Battery", b.BatteryLevel is { } lvl ? $"{lvl}%" : null))));
                AddGroup(section, "Paired", p.BluetoothDevices.Where(b => !b.IsConnected).Select(b => DeviceCard(Or(b.Name, "Bluetooth Device"), "Paired", Tone.Neutral,
                    ("Category", b.DeviceCategory), ("Address", b.Address))));
                root.Children.Add(section);
            }

            if (Visible("input") && p.InputCount > 0)
            {
                var section = Section("Input Devices");
                AddGroup(section, "Keyboards", p.Keyboards.Select(d => DeviceCard(Or(d.Name, "Keyboard"), d.ConnectionType,
                    ("Vendor", d.Vendor), ("Vendor ID", d.VendorId), ("Product ID", d.ProductId), ("Serial", d.SerialNumber), ("Type", BuiltIn(d.IsBuiltIn)))));
                AddGroup(section, "Mice", p.Mice.Select(d => DeviceCard(Or(d.Name, "Mouse"), d.ConnectionType,
                    ("Vendor", d.Vendor), ("Vendor ID", d.VendorId), ("Product ID", d.ProductId), ("Serial", d.SerialNumber))));
                AddGroup(section, "Trackpads", p.Trackpads.Select(d => DeviceCard(Or(d.Name, "Trackpad"), d.ConnectionType,
                    ("Vendor", d.Vendor), ("Vendor ID", d.VendorId), ("Product ID", d.ProductId), ("Serial", d.SerialNumber), ("Type", BuiltIn(d.IsBuiltIn)),
                    ("Force Touch", d.SupportsForceTouch is { } f ? (f ? "Yes" : "No") : null))));
                AddGroup(section, P.PenInputLabel, p.Tablets.Select(d => DeviceCard(Or(d.Name, "Graphics Tablet"), d.TabletType ?? P.PenInputLabel,
                    ("Vendor", d.Vendor), ("Vendor ID", d.VendorId), ("Product ID", d.ProductId), ("Serial", d.SerialNumber), ("Connection", d.ConnectionType))));
                root.Children.Add(section);
            }

            if (root.Children.Count == 1)
            {
                var empty = Ui.Card(Ui.EmptyState("No devices in this category."));
                empty.Margin = new Thickness(0, 24, 0, 0);
                root.Children.Add(empty);
            }
            host.Content = root;
        }
        Render();
        page.Children.Add(host);

        var json = Ui.Collapsible("device.modules.peripherals", JsonTree.Build(raw));
        json.Margin = new Thickness(0, 24, 0, 0);
        page.Children.Add(json);
        return page;
    }

    private static WrapPanel Strip(P p, string? filter, Action<string> toggle)
    {
        var strip = new WrapPanel();
        foreach (var (id, name) in P.Categories)
        {
            var active = filter == id;
            var count = p.Count(id);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Ui.Icon("", 13, active ? Ui.Brush("CardBackgroundBrush") : Ui.Brush($"Icon{Accents[id]}Foreground")));
            content.Children.Add(new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.Medium, Margin = new Thickness(7, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = active ? Ui.Brush("CardBackgroundBrush") : Ui.Brush("TextPrimaryBrush") });
            content.Children.Add(Ui.Pill(count.ToString(), Tone.Neutral, 10.5));
            var btn = new Border
            {
                Padding = new Thickness(10, 6, 8, 6), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = active ? Ui.Brush("IconBlueForeground") : Ui.Brush("CardBackgroundBrush"),
                BorderBrush = Ui.Brush("CardBorderBrush"), BorderThickness = new Thickness(1), Child = content, Opacity = count == 0 ? 0.55 : 1,
            };
            btn.MouseLeftButtonUp += (_, _) => toggle(id);
            strip.Children.Add(btn);
        }
        return strip;
    }

    private static StackPanel Section(string title)
    {
        var section = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        section.Children.Add(Ui.SectionTitle("", title));
        return section;
    }

    private static UniformCardPanel Grid(IEnumerable<UIElement> cards)
    {
        var grid = Ui.CardGrid(300, 16);
        grid.MaxColumns = 4;
        foreach (var card in cards) grid.Children.Add(card);
        return grid;
    }

    private static void AddSection(Panel root, string title, IEnumerable<UIElement> cards)
    {
        var list = cards.ToList();
        if (list.Count == 0) return;
        var section = Section(title);
        section.Children.Add(Grid(list));
        root.Children.Add(section);
    }

    private static void AddGroup(Panel section, string title, IEnumerable<UIElement> cards)
    {
        var list = cards.ToList();
        if (list.Count == 0) return;
        section.Children.Add(Ui.Eyebrow(title));
        var g = Grid(list);
        g.Margin = new Thickness(0, 6, 0, 14);
        section.Children.Add(g);
    }

    private static string Or(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name;

    private static string? BuiltIn(bool? builtIn) => builtIn is { } b ? (b ? "Built-in" : "External") : null;

    private static Border AudioCard(P.AudioDevice a, string fallback) => DeviceCard(Or(a.Name, fallback), a.IsDefault ? "Default" : null,
        ("Manufacturer", a.Manufacturer), ("Connection", a.ConnectionType), ("Type", BuiltIn(a.IsBuiltIn)));

    private static Border DeviceCard(string title, string? badge, params (string Label, string? Value)[] rows)
        => DeviceCard(title, badge, Tone.Neutral, rows);

    private static Border DeviceCard(string title, string? badge, Tone badgeTone, params (string Label, string? Value)[] rows)
    {
        var head = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Ui.Icon("", 14, Ui.Brush("TextSecondaryBrush")));
        var t = Ui.Text(title); t.FontWeight = FontWeights.SemiBold; t.Margin = new Thickness(8, 0, 0, 0); t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; t.ToolTip = title;
        titleRow.Children.Add(t);
        head.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(badge))
        {
            var b = Ui.Pill(badge, badgeTone, 10.5);
            System.Windows.Controls.Grid.SetColumn(b, 1);
            head.Children.Add(b);
        }
        var body = new StackPanel();
        body.Children.Add(head);
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var r = Ui.Row(label, value, mono: label is "Vendor ID" or "Product ID" or "Serial" or "Address" or "Device" or "Device ID" or "UID" or "Location ID" or "Mount Point", semibold: false);
            r.Margin = new Thickness(0, 2, 0, 2);
            body.Children.Add(r);
        }
        return Ui.Card(body, new Thickness(16, 12, 16, 12));
    }

    private static Border PrinterCard(P.Printer pr)
    {
        var body = new StackPanel();
        var head = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Ui.Icon("", 16, Ui.Brush("IconOrangeForeground")));
        var t = Ui.Text(Or(pr.Name, "Printer")); t.FontWeight = FontWeights.SemiBold; t.FontSize = 15; t.Margin = new Thickness(8, 0, 0, 0);
        titleRow.Children.Add(t);
        head.Children.Add(titleRow);
        if (pr.IsDefault)
        {
            var b = Ui.Pill("Default", Tone.Success);
            System.Windows.Controls.Grid.SetColumn(b, 1);
            head.Children.Add(b);
        }
        body.Children.Add(head);

        void Code(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var stat = Ui.Stat(label, value, mono: true, copy: true);
            stat.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(stat);
        }
        Code("Queue", pr.Uri);
        Code("PostScript Printer Description", pr.Ppd);

        if (pr.CupsFilters.Count > 0)
        {
            body.Children.Add(Ui.Label("CUPS Filters"));
            foreach (var f in pr.CupsFilters)
            {
                var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
                var copy = Ui.CopyButton(f.Path ?? f.Name);
                DockPanel.SetDock(copy, Dock.Right);
                line.Children.Add(copy);
                var text = new StackPanel();
                var name = Ui.Text(f.Version is { } v ? $"{f.Name}  v{v}" : f.Name); name.FontSize = 12.5;
                text.Children.Add(name);
                if (f.Path is { } path)
                {
                    var m = Ui.Mono(path); m.TextTrimming = TextTrimming.CharacterEllipsis; m.TextWrapping = TextWrapping.NoWrap; m.ToolTip = path; m.Foreground = Ui.Brush("TextSecondaryBrush");
                    text.Children.Add(m);
                }
                line.Children.Add(text);
                body.Children.Add(new Border { Child = line, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 4, 0, 0), Background = Ui.Brush("CardBorderBrush") });
            }
        }

        var left = new StackPanel();
        var right = new StackPanel();
        void Add(StackPanel col, string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) col.Children.Add(Ui.Row(label, value, semibold: false)); }
        Add(left, "Manufacturer", pr.Manufacturer);
        Add(left, "Model", pr.Model);
        Add(left, "Identifier", pr.Identifier);
        Add(left, "Connection", pr.ConnectionType);
        Add(left, "CUPS Version", pr.CupsVersion);
        Add(right, "Status", pr.Status);
        Add(right, "Scanning", pr.ScanningSupport);
        Add(right, "Fax", pr.FaxSupport);
        var grid = Ui.Columns(2, 24, left, right);
        grid.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(grid);

        if (pr.StateReasons.Count > 0)
        {
            var label = Ui.Eyebrow("State Reasons");
            label.Margin = new Thickness(0, 12, 0, 6);
            body.Children.Add(label);
            body.Children.Add(Ui.Wrap(6, pr.StateReasons.Select(r => (UIElement)Ui.Pill(r, Tone.Orange)).ToArray()));
        }
        return Ui.Card(body, new Thickness(20, 14, 20, 16));
    }
}
