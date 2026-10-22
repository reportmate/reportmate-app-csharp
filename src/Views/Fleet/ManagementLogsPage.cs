using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Management › Management Tools Logs: the management tools' own logs across the fleet, the
/// second view of the Management report beside Enrollment. Per tool, the devices
/// that logged errors and warnings and the trend over the window; the common
/// messages; and the devices behind them, each opening its device page on the log
/// viewer. The filters live in the link, so a copied link reopens the same view.
/// </summary>
public sealed class ManagementLogsPage : FleetPage
{
    private ManagementLogsReport.Filters _filters = new();
    private bool _seeded;

    private static ReportArea Area => ReportArea.ById("management")!;

    private const string LogsSubtitle = "The management tools' own logs across the fleet";

    protected override (string, string, Accent)? Heading => ("Management Tools Logs", LogsSubtitle, Area.Accent);

    /// <summary>
    /// The Management report's header: in the title's place, a switch between its two
    /// views, with the subtitle of whichever one is showing.
    /// </summary>
    internal static Grid Header(FrameworkElement from, bool logs, string glyph, UIElement? right = null)
    {
        var views = Ui.Segmented([("service", "Device Management Service"), ("logs", "Management Tools Logs")],
            logs ? "logs" : "service", key =>
            {
                if (key == "logs" && !logs) DashboardView.Follow(from, InsightCards.ToolsLogsLink());
                else if (key == "service" && logs) DashboardView.Follow(from, DeepLink.For("management"));
            });
        return Ui.TabHeader(views, logs ? LogsSubtitle : Area.Subtitle, glyph, Area.Accent, right);
    }

    protected override async Task<UIElement> BuildAsync()
    {
        if (!_seeded)
        {
            _seeded = true;
            _filters = ManagementLogsReport.Filters.From(Filter);
        }
        // The toolbar's platform scope applies unless the link named one of its own.
        var filters = _filters.Platform is null && PlatformFilter.Current != PlatformScope.All
            ? _filters with { Platform = PlatformFilter.Current == PlatformScope.Mac ? "macOS" : "Windows" }
            : _filters;

        var page = new StackPanel();
        page.Children.Add(Header(this, logs: true, Area.Glyph));

        var result = await FleetApiClient.Instance.GetRawAsync(filters.Path());
        page.Children.Add(FilterBar());
        if (!result.Ok)
        {
            page.Children.Add(result.HttpStatus == 404
                ? Ui.Card(Ui.EmptyState("Not available yet: this API does not serve the fleet tools logs report (404)."))
                : FleetUnavailable(result.Status, result.Detail));
            return page;
        }

        ManagementLogsReport.Report report;
        using (var doc = result.Data!) report = ManagementLogsReport.Read(doc.RootElement);

        page.Children.Add(Spaced(ToolsTable(report)));
        page.Children.Add(Spaced(MessagesTable(report)));
        page.Children.Add(Spaced(DevicesTable(report)));
        if (report.DevicesTruncated)
        {
            var note = Ui.Caption($"Showing the first {report.Devices.Count:N0} devices; narrow by tool, level or message to see the rest.");
            note.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(note);
        }
        return page;
    }

    private static UIElement Spaced(UIElement e)
    {
        if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 18, 0, 0);
        return e;
    }

    private void Apply(ManagementLogsReport.Filters next)
    {
        _filters = next;
        _ = ReloadAsync();
    }

    private UIElement FilterBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Add(UIElement e) { if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 12, 8); bar.Children.Add(e); }

        Add(Ui.Segmented([("24", "24 hours"), ("168", "7 days")], _filters.NormalHours.ToString(),
            key => Apply(_filters with { Hours = int.Parse(key) })));
        Add(Ui.Segmented([("all", "All levels"), ("error", "Errors"), ("warning", "Warnings")], _filters.NormalLevel ?? "all",
            key => Apply(_filters with { Level = key == "all" ? null : key })));

        foreach (var (label, value, clear) in new (string, string?, Func<ManagementLogsReport.Filters>)[]
                 {
                     ("Tool", _filters.Tool, () => _filters with { Tool = null }),
                     ("Message", _filters.Message, () => _filters with { Message = null }),
                     ("Platform", _filters.Platform, () => _filters with { Platform = null }),
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(Ui.Pill($"{label}: {value}", Tone.Info));
            var x = Ui.IconButton("", "Clear", (_, _) => Apply(clear()));
            x.Margin = new Thickness(4, 0, 0, 0);
            chip.Children.Add(x);
            Add(chip);
        }
        return bar;
    }

    private static string Ago(DateTime? at) => at is { } when ? Format.RelativeTime(when) : "";

    public sealed class ToolRow
    {
        public ManagementLogsReport.Tool Tool { get; init; } = null!;
        public string Name => Tool.Name;
        public string Platform => Tool.Platform ?? "";
        public int WithErrors => Tool.DevicesWithErrors;
        public int WithWarnings => Tool.DevicesWithWarnings;
        public int ErrorLines => Tool.ErrorLines;
        public int WarningLines => Tool.WarningLines;
        public string LastSeen => Ago(Tool.LastSeen);
        public DateTime LastSeenAt => Tool.LastSeen ?? DateTime.MinValue;
        public string Trend => ManagementLogsReport.Sparkline(Tool.Trend);
    }

    private UIElement ToolsTable(ManagementLogsReport.Report report)
    {
        var rows = report.Tools.Select(t => new ToolRow { Tool = t }).ToList();
        return new FilteredTable<ToolRow>("Tools", "{0} of {1} tools", rows,
                (r, q) => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase),
                [
                    Col.Text("Tool", "Name", star: true),
                    Col.Platform("Platform", "Platform", 110),
                    Col.Text("Devices with errors", "WithErrors", 150),
                    Col.Text("With warnings", "WithWarnings", 120),
                    Col.Text("Error lines", "ErrorLines", 100),
                    Col.Text("Warning lines", "WarningLines", 110),
                    Col.Text("Trend", "Trend", 120, mono: true),
                    Col.Text("Last seen", "LastSeen", 110, sortBy: "LastSeenAt"),
                ], "Search tools...", "No management tool logged anything in this window.")
            .WithRowLink(r => (_filters with { Tool = r.Tool.Key, Message = null }).Link())
            .WithCsv("management-tools-logs.csv",
                ["Tool", "Platform", "Devices with errors", "Devices with warnings", "Error lines", "Warning lines", "Last seen"],
                r => [r.Name, r.Platform, r.WithErrors.ToString(), r.WithWarnings.ToString(), r.ErrorLines.ToString(), r.WarningLines.ToString(),
                    r.Tool.LastSeen?.ToString("o")])
            .Build();
    }

    public sealed class MessageRow
    {
        public ManagementLogsReport.Message Message { get; init; } = null!;
        public string Text => Message.Text;
        public string ToolName => Message.ToolName;
        public int Devices => Message.Devices;
        public int Lines => Message.Lines;
        public string LastSeen => Ago(Message.LastSeen);
        public DateTime LastSeenAt => Message.LastSeen ?? DateTime.MinValue;
    }

    private UIElement MessagesTable(ManagementLogsReport.Report report)
    {
        var rows = report.Messages.Select(m => new MessageRow { Message = m }).ToList();
        return new FilteredTable<MessageRow>("Common messages", "{0} of {1} messages", rows,
                (r, q) => $"{r.Text} {r.ToolName}".Contains(q, StringComparison.OrdinalIgnoreCase),
                [
                    Col.Text("Message", "Text", star: true, wrap: true),
                    Col.Text("Tool", "ToolName", 130),
                    Col.Text("Devices", "Devices", 90),
                    Col.Text("Lines", "Lines", 80),
                    Col.Text("Last seen", "LastSeen", 110, sortBy: "LastSeenAt"),
                ], "Search messages...", "No message matches these filters.")
            // A message opens the devices that logged it.
            .WithRowLink(r => (_filters with { Message = r.Message.Id, Tool = r.Message.Tool }).Link())
            .WithCsv("management-tools-messages.csv", ["Message", "Tool", "Devices", "Lines", "Last seen"],
                r => [r.Text, r.ToolName, r.Devices.ToString(), r.Lines.ToString(), r.Message.LastSeen?.ToString("o")])
            .Build();
    }

    public sealed class DeviceRow
    {
        public ManagementLogsReport.Device Device { get; init; } = null!;
        public string Name => Device.Name;
        public string Serial => Device.Serial;
        public string Platform => Device.Platform ?? "";
        public string ToolName => Device.ToolName;
        public int Errors => Device.Errors;
        public int Warnings => Device.Warnings;
        public string LastAt => Ago(Device.LastAt);
        public DateTime LastAtValue => Device.LastAt ?? DateTime.MinValue;
    }

    private UIElement DevicesTable(ManagementLogsReport.Report report)
    {
        var rows = report.Devices.Select(d => new DeviceRow { Device = d }).ToList();
        return new FilteredTable<DeviceRow>("Devices", "{0} of {1} devices", rows,
                (r, q) => $"{r.Name} {r.Serial} {r.ToolName}".Contains(q, StringComparison.OrdinalIgnoreCase),
                [
                    Col.Text("Device", "Name", star: true),
                    Col.Text("Serial", "Serial", 150, mono: true),
                    Col.Platform("Platform", "Platform", 110),
                    Col.Text("Tool", "ToolName", 130),
                    Col.Text("Errors", "Errors", 80),
                    Col.Text("Warnings", "Warnings", 90),
                    Col.Text("Last at", "LastAt", 110, sortBy: "LastAtValue"),
                ], "Search devices...", "No device matches these filters.")
            // The device's own page, on its Management tab's log viewer for this tool.
            .WithRowLink(r => DeepLink.For("device", r.Serial, ("tab", "management"), ("tool", r.Device.Tool),
                ("level", _filters.NormalLevel ?? "error")))
            .WithCsv("management-tools-devices.csv", ["Device", "Serial", "Platform", "Tool", "Errors", "Warnings", "Last at"],
                r => [r.Name, r.Serial, r.Platform, r.ToolName, r.Errors.ToString(), r.Warnings.ToString(), r.Device.LastAt?.ToString("o")])
            .Build();
    }
}
