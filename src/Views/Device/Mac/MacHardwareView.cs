using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Device.Tabs;
using ReportMate.App.Views.Shared;
using WinModules = ReportMate.WindowsClient.Models.Modules;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Hardware tab, as the Mac app's HardwareTab and the web's lay it out:
/// identity, then either the chip package (CPU, memory, GPU and Neural Engine inside a
/// dashed outline, the rest beside it) or the discrete spec grid, then displays,
/// storage analysis, storage devices, battery and memory modules.
/// </summary>
public static class MacHardwareView
{
    private const string Glyph = "";

    private sealed record DriveRow(string Name, string Model, string Serial, string Type, string Capacity, string Free, double UsedPercent, string FileSystem, string Health, Tone HealthTone, string Interface);
    private sealed record ModuleRow(string Location, string Type, string Capacity, string Speed, string Manufacturer);

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("hardware", out var module)) return null;
        var hw = MacHardwareInfo.Read(module, s.RawModules.TryGetValue("system", out var system) ? system : null);

        var page = new StackPanel();
        void Add(UIElement e, double top = 24) { if (e is FrameworkElement fe && page.Children.Count > 0) fe.Margin = new Thickness(0, top, 0, 0); page.Children.Add(e); }

        UIElement? right = null;
        if (hw.Architecture is { } arch)
        {
            var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            p.Children.Add(Ui.Caption("Architecture"));
            p.Children.Add(Ui.Pill(arch, Tone.Orange));
            right = p;
        }
        Add(Ui.TabHeader("Hardware Overview", "System hardware specifications and components", Glyph, Accent.Orange, right), 0);

        if (!hw.HasData)
        {
            Add(Ui.Card(Ui.EmptyState("Hardware data is not available for this device.", Glyph)));
            return page;
        }

        Add(Ui.Card(Ui.VStack(Identity(hw), hw.IsUnifiedMemory ? UnifiedGrid(hw) : DiscreteGrid(hw)), new Thickness(16)));
        if (hw.Displays.Count > 0) Add(Displays(hw));
        Add(StorageAnalysis(hw));
        if (StorageDevices(hw) is { } drives) Add(drives);
        if (hw.HasBattery) Add(BatterySection(hw.Battery!));
        if (hw.MemoryModules.Count > 0) Add(MemoryModules(hw));
        return page;
    }

    private static Border Identity(MacHardwareInfo hw)
    {
        var identity = Ui.Columns(3, 16,
            Ui.Stat("Manufacturer", Format.OrUnknown(hw.Manufacturer)),
            Ui.Stat("Model", Format.OrUnknown(hw.Model)),
            hw.ModelIdentifier is { } id ? Ui.Stat("Identifier", id, mono: true) : null);
        return new Border
        {
            BorderBrush = Ui.Brush("CardBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 14), Child = identity, Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var joined = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p) && p != "Unknown"));
        return joined.Length == 0 ? null : joined;
    }

    private static string? CoreSplit(MacHardwareInfo hw) =>
        hw.HasAppleSilicon ? $"{hw.PerformanceCores} Performance + {hw.EfficiencyCores} Efficiency" : null;

    private static string Percent(double value) => $"{(int)Math.Round(value, MidpointRounding.AwayFromZero)}%";

    private static Border BatteryCard(MacBattery b) =>
        Ui.SpecCard(Glyph, "Battery", $"{b.CycleCount} Cycles", $"{Percent(b.ChargePercent)} • {Format.OrUnknown(b.Health)}");

    private static Border? NpuCard(MacHardwareInfo hw, bool withName) => hw.HasNpu
        ? Ui.SpecCard(Glyph, "NPU", hw.NpuTops is { } tops ? $"{tops} TOPS" : $"{hw.NpuCores} Cores",
            withName ? hw.NpuName : null, hw.NpuTops is not null ? $"{hw.NpuCores} Cores" : null)
        : null;

    /// <summary>Items two to a row, rows stacked; empty slots stay empty so the pairs line up.</summary>
    private static StackPanel Pairs(params UIElement?[] items)
    {
        var present = items.Where(i => i is not null).ToList();
        var stack = new StackPanel();
        for (var i = 0; i < present.Count; i += 2)
        {
            var row = Ui.Columns(2, 12, present[i], i + 1 < present.Count ? present[i + 1] : null);
            if (i > 0) row.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>
    /// Apple silicon: one chip holds the CPU, GPU, Neural Engine and memory, so those
    /// four sit inside a dashed outline under the chip's name, and storage, battery and
    /// radios sit outside it.
    /// </summary>
    private static UIElement UnifiedGrid(MacHardwareInfo hw)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 8, 0, 10) };
        var chip = Ui.SectionHeader($"{hw.ProcessorName ?? hw.ChipName ?? "Apple"} Chip");
        chip.VerticalAlignment = VerticalAlignment.Bottom;
        title.Children.Add(chip);
        var sub = Ui.Caption("Unified Memory Architecture");
        sub.Margin = new Thickness(10, 0, 0, 2);
        sub.VerticalAlignment = VerticalAlignment.Bottom;
        title.Children.Add(sub);

        var package = Pairs(
            Ui.SpecCard(Glyph, "CPU", $"{hw.CpuCores} Cores", CoreSplit(hw)),
            Ui.SpecCard(Glyph, "Memory", Format.Bytes(hw.TotalMemoryBytes), Join(" ", hw.MemoryType, hw.MemoryManufacturer)),
            Ui.SpecCard(Glyph, "GPU", $"{hw.GpuCores} Cores", hw.GraphicsMetalSupport ?? hw.DisplayGraphicsName),
            NpuCard(hw, withName: false));
        package.Margin = new Thickness(12);
        var outline = new Rectangle
        {
            RadiusX = 14, RadiusY = 14, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 },
            Stroke = Ui.Brush("TextSecondaryBrush"), Opacity = 0.5, Fill = Brushes.Transparent, IsHitTestVisible = false,
        };
        var packageBox = new Grid();
        packageBox.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.Brush("SubtleFillBrush") });
        packageBox.Children.Add(package);
        packageBox.Children.Add(outline);

        var w = hw.Wireless;
        var bt = hw.Bluetooth;
        var outside = Pairs(
            Ui.SpecCard(Glyph, "Storage", Format.Bytes(hw.TotalStorageBytes),
                hw.TotalStorageBytes > 0 ? $"{Math.Min(100, (int)Math.Round(hw.FreeStorageBytes / hw.TotalStorageBytes * 100, MidpointRounding.AwayFromZero))}% Free" : null),
            hw.HasBattery ? BatteryCard(hw.Battery!) : null,
            w is { IsAvailable: true } ? Ui.SpecCard(Glyph, "Wireless", w.Generation ?? "Available", Join(" • ", w.Version, w.Status)) : null,
            bt is { IsAvailable: true } ? Ui.SpecCard(Glyph, "Bluetooth", bt.Version ?? "N/A", bt.Status ?? "Available") : null);
        outside.Margin = new Thickness(12);

        var columns = Ui.Columns(2, 16, packageBox, outside);
        return Ui.VStack(title, columns);
    }

    /// <summary>Intel or other discrete parts: one tile per component, as the Windows tab draws them.</summary>
    private static UIElement DiscreteGrid(MacHardwareInfo hw)
    {
        var grid = new UniformCardPanel { MinItemWidth = 200, Gap = 12, Margin = new Thickness(0, 12, 0, 0) };
        var w = hw.Wireless;
        var bt = hw.Bluetooth;
        var tiles = new UIElement?[]
        {
            Ui.SpecCard(Glyph, "CPU", $"{hw.CpuCores} Cores", hw.ProcessorName,
                CoreSplit(hw) ?? (hw.ProcessorMaxSpeed is { } max ? $"Max: {max} GHz" : null)),
            Ui.SpecCard(Glyph, "Memory", Format.Bytes(hw.TotalMemoryBytes), Join(" ", hw.MemoryType, hw.MemoryManufacturer),
                hw.MemoryModules.Count > 0 ? $"{hw.MemoryModules.Count} Modules" : null),
            Ui.SpecCard(Glyph, "Storage", Format.Bytes(hw.TotalStorageBytes), $"{Format.Bytes(hw.FreeStorageBytes)} Free",
                hw.InternalDrives.FirstOrDefault()?.Type ?? "Internal"),
            hw.HasBattery ? BatteryCard(hw.Battery!) : null,
            Ui.SpecCard(Glyph, "GPU", hw.GraphicsName ?? "Unknown",
                Join(" • ", hw.GpuCores > 0 ? $"{hw.GpuCores} Cores" : null, hw.GraphicsMemoryGB > 0 ? $"{(int)hw.GraphicsMemoryGB} GB VRAM" : null),
                hw.GraphicsManufacturer is { } mfg
                    ? $"{mfg} Discrete Card{(hw.GraphicsDriverVersion is { } dv ? $" - Driver {dv}" : "")}"
                    : hw.GraphicsMetalSupport ?? "Discrete GPU"),
            NpuCard(hw, withName: true),
            w is { IsAvailable: true } ? Ui.SpecCard(Glyph, "Wireless", w.Generation ?? "Available", w.Protocol, w.Name) : null,
            bt is { IsAvailable: true } ? Ui.SpecCard(Glyph, "Bluetooth", bt.Version ?? "N/A", bt.Status ?? "Available") : null,
        };
        foreach (var t in tiles.Where(t => t is not null)) grid.Children.Add(t!);
        return grid;
    }

    private static UIElement Displays(MacHardwareInfo hw)
    {
        var list = new StackPanel();
        foreach (var d in hw.Displays)
        {
            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.Icon(Glyph, 18, Ui.Brush("IconBlueForeground"));
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            left.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = Ui.Brush("IconBlueBackground"),
                Child = icon, Margin = new Thickness(0, 0, 12, 0),
            });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(d.Name);
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0);
            titles.Children.Add(name);
            var inches = d.DiagonalInches is { } di && di > 0 ? $"{(int)di}\" " : "";
            titles.Children.Add(Ui.Caption($"{inches}{(d.IsInternal ? "Built-in" : "External")} {d.DisplayType ?? ""}".Trim()));
            left.Children.Add(titles);
            card.Children.Add(left);

            var specs = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            void Spec(string label, string? value, bool copy = false)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var st = Ui.Stat(label, value, mono: copy, copy: copy);
                st.Margin = new Thickness(0, 0, 24, 0);
                specs.Children.Add(st);
            }
            Spec("Resolution", Format.OrUnknown(d.Resolution));
            Spec("Scaled", d.ScaledResolution);
            Spec("Pixel Density", d.Ppi is { } ppi ? $"{ppi} PPI" : null);
            Spec("Brightness", d.BrightnessNits is { } nits ? $"{nits} nits" : null);
            Spec("Color Gamut", d.ColorGamut);
            Spec("Serial Number", d.SerialNumber ?? "N/A", copy: d.SerialNumber is not null);
            Spec("Firmware", d.FirmwareVersion);
            Grid.SetColumn(specs, 1);
            card.Children.Add(specs);

            var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            void Badge(string text, Tone tone) { var b = Ui.Pill(text, tone); if (badges.Children.Count > 0) b.Margin = new Thickness(8, 0, 0, 0); badges.Children.Add(b); }
            if (d.TrueTone) Badge("True Tone", Tone.Orange);
            if (d.RefreshRate is { } rate) Badge(rate, Tone.Purple);
            if (d.IsMainDisplay) Badge("Main Display", Tone.Info);
            Badge(d.Online ? "Connected" : "Disconnected", d.Online ? Tone.Success : Tone.Neutral);
            Grid.SetColumn(badges, 2);
            card.Children.Add(badges);

            var border = Ui.Card(card, new Thickness(16, 14, 16, 14));
            if (list.Children.Count > 0) border.Margin = new Thickness(0, 12, 0, 0);
            list.Children.Add(border);
        }
        return Ui.VStack(Ui.SectionTitle(Glyph, "Displays"), list);
    }

    /// <summary>
    /// The directory breakdown when the client analysed a drive, drawn by the Windows
    /// tab's StorageVisualization from the Mac drives recast as its model; otherwise a
    /// usage bar per drive, as the Mac app falls back to.
    /// </summary>
    private static UIElement StorageAnalysis(MacHardwareInfo hw)
    {
        var analysed = hw.StorageDevices.Where(d => d.CapacityBytes > 0 && d.RootDirectories.Count > 0).ToList();
        UIElement body;
        if (analysed.Count > 0)
        {
            body = new StorageVisualization(analysed.Select(ToWindows).ToList());
        }
        else
        {
            var usable = hw.StorageDevices.Where(d => d.CapacityBytes > 0 && d.FreeBytes > 0).ToList();
            if (usable.Count == 0)
            {
                body = Ui.Card(Ui.EmptyState("No storage devices detected", Glyph));
            }
            else
            {
                var rows = new StackPanel();
                foreach (var d in usable)
                {
                    var name = Ui.Text(d.Name);
                    name.FontWeight = FontWeights.Medium;
                    name.Margin = new Thickness(0);
                    name.TextTrimming = TextTrimming.CharacterEllipsis;
                    var bar = Ui.UsageBar(d.UsedPercent, 10);
                    bar.VerticalAlignment = VerticalAlignment.Center;
                    bar.Margin = new Thickness(12, 0, 12, 0);
                    var text = Ui.Caption($"{Format.Bytes(d.CapacityBytes - d.FreeBytes)} used of {Format.Bytes(d.CapacityBytes)} · {100 - d.UsedPercent}% free");
                    text.TextAlignment = TextAlignment.Right;
                    text.VerticalAlignment = VerticalAlignment.Center;
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
                    row.Children.Add(name);
                    Grid.SetColumn(bar, 1);
                    row.Children.Add(bar);
                    Grid.SetColumn(text, 2);
                    row.Children.Add(text);
                    if (rows.Children.Count > 0) row.Margin = new Thickness(0, 10, 0, 0);
                    rows.Children.Add(row);
                }
                body = Ui.Card(rows, new Thickness(16));
            }
        }
        return Ui.VStack(Ui.SectionTitle(Glyph, "Storage Analysis"), body);
    }

    private static WinModules.StorageDevice ToWindows(MacStorageDevice d) => new()
    {
        Name = d.Name,
        Model = d.Model ?? "",
        SerialNumber = d.SerialNumber ?? "",
        Type = d.Type ?? "",
        Interface = d.Interface ?? "",
        Health = d.Health ?? "",
        Capacity = (long)d.CapacityBytes,
        FreeSpace = (long)d.FreeBytes,
        IsInternal = d.IsInternal,
        LastAnalyzed = d.LastAnalyzed,
        RootDirectories = d.RootDirectories.Select(ToWindows).ToList(),
    };

    private static WinModules.DirectoryInformation ToWindows(MacStorageDirectory d) => new()
    {
        Name = d.Name,
        Path = d.Path,
        Size = (long)d.Size,
        FileCount = d.FileCount,
        PercentageOfDrive = d.PercentageOfDrive,
        Category = Enum.TryParse<WinModules.DirectoryCategory>(d.Category, true, out var c) ? c : WinModules.DirectoryCategory.Other,
        Subdirectories = d.Subdirectories.Select(ToWindows).ToList(),
    };

    private static UIElement? StorageDevices(MacHardwareInfo hw)
    {
        var drives = hw.StorageDevices.Where(d => d.CapacityBytes > 0).ToList();
        if (drives.Count == 0) return null;
        var rows = drives.Select(d => new DriveRow(
            d.Name, Format.OrDash(d.Model), Format.OrDash(d.SerialNumber), Format.OrDash(d.Type), Format.Bytes(d.CapacityBytes),
            d.FreeBytes > 0 ? $"{Format.Bytes(d.FreeBytes)} ({100 - d.UsedPercent}% free)" : "-", d.UsedPercent,
            Format.OrDash(d.FileSystem), d.Health ?? "",
            d.Health switch { "Good" or "Verified" => Tone.Success, "Warning" => Tone.Warning, "Unknown" => Tone.Neutral, _ => Tone.Error },
            Format.OrDash(d.Interface))).ToList();
        return Ui.VStack(Ui.SectionTitle(Glyph, "Storage Devices"), Table.Card("", null, rows, null, "",
            Col.Text("Name", "Name", 140),
            Col.Text("Model", "Model", 180),
            Col.Text("Serial Number", "Serial", 160, mono: true),
            Col.Text("Type", "Type", 80),
            Col.Text("Capacity", "Capacity", 100),
            Col.Bar("Free Space", "Free", "UsedPercent", 190),
            Col.Text("File System", "FileSystem", 130),
            Col.Pill("Health", "Health", "HealthTone", 90),
            Col.Text("Interface", "Interface", star: true)));
    }

    private static UIElement BatterySection(MacBattery b)
    {
        UIElement? cycles = null;
        if (b.CycleCount > 0)
        {
            var bar = new StackPanel();
            bar.Children.Add(Ui.Text($"{b.CycleCount} / 1000"));
            bar.Children.Add(Ui.UsageBar(Math.Min(b.CycleCount / 10.0, 100)));
            cycles = Ui.Tile("Cycle Count", "", bar);
        }
        return Ui.VStack(Ui.SectionTitle(Glyph, "Battery Information"), Ui.Card(Ui.TileRow(12,
            Ui.Tile("Status", b.IsCharging ? "Charging" : "Not Charging"),
            b.Health is { } h ? Ui.Tile("Health", h) : null,
            cycles,
            b.ChargePercent > 0 ? Ui.Tile("Charge", Percent(b.ChargePercent)) : null,
            b.EstimatedRuntime is { } rt ? Ui.Tile("Runtime", rt) : null,
            b.DesignCapacity is { } dc ? Ui.Tile("Design Capacity", $"{(int)dc} mAh") : null,
            b.CurrentCapacity is { } cc ? Ui.Tile("Current Capacity", $"{(int)cc} mAh") : null,
            b.Condition is { } cond ? Ui.Tile("Condition", cond) : null), new Thickness(16)));
    }

    private static UIElement MemoryModules(MacHardwareInfo hw)
    {
        var rows = hw.MemoryModules.Select(m => new ModuleRow(
            m.Location, Format.OrDash(m.Type), Format.Bytes(m.CapacityMB * 1024 * 1024),
            m.Speed is { } speed ? $"{speed} MHz" : "-", Format.OrDash(m.Manufacturer))).ToList();
        return Ui.VStack(Ui.SectionTitle(Glyph, "Memory Modules"), Table.Card("", null, rows, null, "",
            Col.Text("Location", "Location", 200),
            Col.Text("Type", "Type", 120),
            Col.Text("Capacity", "Capacity", 120),
            Col.Text("Speed", "Speed", 120),
            Col.Text("Manufacturer", "Manufacturer", star: true)));
    }
}
