using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The web app's home dashboard: fleet status and counters, the four insight cards,
/// and recent activity with the platform and OS breakdowns. One consolidated API
/// call backs the status and activity widgets, as on the web; the insight cards
/// read the bulk module reports after it. <see cref="DashboardLayout"/> decides how
/// many columns the window has room for.
/// </summary>
public sealed class DashboardPage : FleetPage
{
    protected override (string, string, Accent)? Heading =>
        ("Dashboard", "Fleet status at a glance", Accent.Blue);

    protected override async Task<UIElement> BuildAsync()
    {
        var result = await FleetApiClient.Instance.GetDashboardAsync();
        if (!result.Ok) return FleetUnavailable(result.Status, result.Detail);

        var data = result.Data!;
        // The toolbar's platform toggle is global, and the web applies it to every
        // widget on this page. Leaving the dashboard unfiltered made the control
        // look broken on the one page everybody lands on first.
        var devices = PlatformFilter.Apply(data.Devices);

        var page = new StackPanel();
        page.Children.Add(Ui.TabHeader("Dashboard", "Fleet status at a glance", "", Accent.Blue,
            Ui.Caption($"{devices.Count:N0} devices reporting")));

        var status = new StackPanel();
        Stack(status, StatusWidget(devices));
        Stack(status, Ui.Columns(2, 14,
            CounterCard("Errors", Errors(data.InstallStats), Tone.Error),
            CounterCard("Warnings", Warnings(data.InstallStats), Tone.Warning)));
        Stack(status, NewClientsWidget(devices));

        var activity = new StackPanel();
        Stack(activity, RecentEventsWidget(data.Events));
        Stack(activity, PlatformDistributionWidget(devices));
        Stack(activity, Ui.Columns(2, 14,
            OsVersionWidget(devices, "Windows"),
            OsVersionWidget(devices, "macOS")));

        // The insight cards draw from the shared report model and redraw in place as
        // each report lands, so the page is on screen before the slow reads finish.
        var insights = Ui.CardGrid(340, 14);
        insights.MaxColumns = 2;
        // Each card is as tall as what it shows, not as tall as its row-mate.
        insights.StretchRows = false;
        var hosts = DashboardInsightsModel.Reports.ToDictionary(r => r, r =>
        {
            var host = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = InsightWidgets.Build(r),
            };
            insights.Children.Add(host);
            return host;
        });
        WatchInsights(hosts);

        var layout = new DashboardLayout(status, insights, activity) { Margin = new Thickness(0, 20, 0, 0) };
        page.Children.Add(layout);
        _ = DashboardInsightsModel.Shared.LoadAsync();
        return page;
    }

    private Action<string>? _insightsHandler;

    /// <summary>
    /// Redraw a card when its report changes, for as long as this page is shown.
    /// The model outlives the page, so the handler has to come off when the page
    /// goes or every dashboard ever built would keep redrawing cards nobody sees.
    /// </summary>
    private void WatchInsights(Dictionary<string, ContentControl> hosts)
    {
        var model = DashboardInsightsModel.Shared;
        if (_insightsHandler is not null) model.Changed -= _insightsHandler;
        _insightsHandler = report =>
        {
            if (hosts.TryGetValue(report, out var host)) host.Content = InsightWidgets.Build(report);
        };
        model.Changed += _insightsHandler;
    }

    public DashboardPage()
    {
        Unloaded += (_, _) =>
        {
            if (_insightsHandler is null) return;
            DashboardInsightsModel.Shared.Changed -= _insightsHandler;
            _insightsHandler = null;
        };
    }

    private static void Stack(Panel host, UIElement child)
    {
        if (host.Children.Count > 0 && child is FrameworkElement fe) fe.Margin = new Thickness(0, 14, 0, 0);
        host.Children.Add(child);
    }

    // ── Widgets ──────────────────────────────────────────────────────────

    /// <summary>Active / stale / missing counts, the web's StatusWidget.</summary>
    private static UIElement StatusWidget(List<FleetDevice> devices)
    {
        // Derived from lastSeen, not the API's own status field, because that is what
        // the web dashboard does -- see DeviceStatus for why the two disagree.
        var buckets = devices.GroupBy(DeviceStatus.Calculate)
            .ToDictionary(g => g.Key, g => g.Count());
        int Count(DeviceLiveness l) => buckets.TryGetValue(l, out var n) ? n : 0;

        var body = new StackPanel();
        body.Children.Add(BigNumber(devices.Count, "devices total"));
        body.Children.Add(Charts.Bar("Active", Count(DeviceLiveness.Active), devices.Count, Tone.Success));
        body.Children.Add(Charts.Bar("Stale", Count(DeviceLiveness.Stale), devices.Count, Tone.Warning));
        body.Children.Add(Charts.Bar("Missing", Count(DeviceLiveness.Missing), devices.Count, Tone.Error));
        if (Count(DeviceLiveness.Archived) > 0)
            body.Children.Add(Charts.Bar("Archived", Count(DeviceLiveness.Archived), devices.Count, Tone.Neutral));
        return Ui.StatBlock("Fleet Status", "Seen in the last 24 hours", "", Accent.Green, Pad(body));
    }

    /// <summary>
    /// The counters are pre-aggregated by the API, so the platform scope picks a
    /// different pair of fields rather than filtering rows. Cimian reports the
    /// Windows items and Munki the macOS ones, and the API keeps them separable
    /// precisely so this toggle can work without re-counting.
    /// </summary>
    private static (int Items, int Devices) Errors(InstallStats? s) => s is null ? (0, 0) : PlatformFilter.Current switch
    {
        PlatformScope.Windows => (s.WinErrorItems, s.WinDevicesWithErrors),
        PlatformScope.Mac => (s.MacErrorItems, s.MacDevicesWithErrors),
        _ => (s.TotalErrorItems, s.DevicesWithErrors),
    };

    private static (int Items, int Devices) Warnings(InstallStats? s) => s is null ? (0, 0) : PlatformFilter.Current switch
    {
        PlatformScope.Windows => (s.WinWarningItems, s.WinDevicesWithWarnings),
        PlatformScope.Mac => (s.MacWarningItems, s.MacDevicesWithWarnings),
        _ => (s.TotalWarningItems, s.DevicesWithWarnings),
    };

    private static UIElement CounterCard(string title, (int Items, int Devices) counts, Tone tone)
    {
        var (total, deviceCount) = counts;
        var body = new StackPanel();
        var n = Ui.Text(total.ToString("N0"), "StatValueStyle", Ui.StatusBrush(tone));
        n.FontSize = 30;
        n.FontWeight = FontWeights.SemiBold;
        body.Children.Add(n);
        body.Children.Add(Ui.Caption(deviceCount == 1 ? "on 1 device" : $"across {deviceCount:N0} devices"));
        return Ui.StatBlock(title, null, "", tone == Tone.Error ? Accent.Red : Accent.Yellow, Pad(body));
    }

    /// <summary>Devices that first reported recently, the web's NewClientsWidget.</summary>
    private static UIElement NewClientsWidget(List<FleetDevice> devices)
    {
        var recent = devices
            .Where(d => d.CreatedAt is not null)
            .OrderByDescending(d => d.CreatedAt)
            .Take(8)
            .ToList();

        if (recent.Count == 0)
            return Ui.StatBlock("New Clients", "Recently enrolled", "", Accent.Indigo,
                Pad(Ui.EmptyState("No devices have first reported recently.")));

        var body = new StackPanel();
        foreach (var d in recent)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Ui.Text(DisplayName(d));
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(name);
            var when = Ui.Caption(Relative(d.CreatedAt));
            Grid.SetColumn(when, 1);
            row.Children.Add(when);
            row.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(row);
        }
        return Ui.StatBlock("New Clients", "Recently enrolled", "", Accent.Indigo, Pad(body));
    }

    /// <summary>The web's RecentEventsTable.</summary>
    private static UIElement RecentEventsWidget(List<FleetEvent> events)
    {
        if (events.Count == 0)
            return Ui.StatBlock("Recent Events", "Across the fleet", "", Accent.Cyan,
                Pad(Ui.EmptyState("No events have been reported.")));

        var body = new StackPanel();
        foreach (var e in events.OrderByDescending(e => e.When ?? DateTime.MinValue).Take(12))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var kind = (e.Kind ?? e.EventType ?? "info").ToLowerInvariant();
            var pill = Ui.Pill(Format.Capitalize(kind), kind switch
            {
                "error" or "failed" => Tone.Error,
                "warning" or "warn" => Tone.Warning,
                "success" or "installed" => Tone.Success,
                _ => Tone.Neutral,
            });
            pill.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(pill);

            var text = Ui.Text(Truncate(e.Message, 110));
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.ToolTip = e.Message;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var meta = Ui.Caption($"{e.DeviceName ?? e.SerialNumber ?? ""}  ·  {Relative(e.When)}");
            Grid.SetColumn(meta, 2);
            row.Children.Add(meta);
            body.Children.Add(row);
        }
        return Ui.StatBlock("Recent Events", "Across the fleet", "", Accent.Cyan, Pad(body));
    }

    /// <summary>Windows / macOS split, the web's PlatformDistributionWidget.</summary>
    private static UIElement PlatformDistributionWidget(List<FleetDevice> devices)
    {
        var groups = devices
            .GroupBy(d => NormalizePlatform(d))
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();

        var body = new StackPanel();
        var total = devices.Count;
        foreach (var (name, count) in groups)
            body.Children.Add(Charts.Bar(name, count, total, name == "Windows" ? Tone.Info : Tone.Neutral));

        return Ui.StatBlock("Platforms", "Devices by operating system", "", Accent.Purple, Pad(body));
    }

    private static UIElement OsVersionWidget(List<FleetDevice> devices, string platform)
    {
        var matching = devices.Where(d => NormalizePlatform(d) == platform).ToList();
        if (matching.Count == 0)
            return Ui.StatBlock($"{platform} Versions", null, "", Accent.Gray,
                Pad(Ui.EmptyState($"No {platform} devices are reporting.")));

        var groups = matching
            .GroupBy(OsLabel)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .Take(8)
            .ToList();

        var body = new StackPanel();
        foreach (var (name, count) in groups)
            body.Children.Add(Charts.Bar(name, count, matching.Count, Tone.Info));

        return Ui.StatBlock($"{platform} Versions", $"{matching.Count:N0} devices", "", Accent.Teal, Pad(body));
    }

    // ── Small parts ──────────────────────────────────────────────────────

    private static UIElement BigNumber(int value, string caption)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var n = Ui.Text(value.ToString("N0"));
        n.FontSize = 34;
        n.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(n);
        panel.Children.Add(Ui.Caption(caption));
        return panel;
    }

    private static UIElement Pad(UIElement body) =>
        new Border { Padding = new Thickness(20, 4, 20, 18), Child = body };

    private static string NormalizePlatform(FleetDevice d)
    {
        var raw = (d.Platform ?? d.OsName ?? d.Modules?.System?.OperatingSystem?.Name ?? "").ToLowerInvariant();
        if (raw.Contains("win")) return "Windows";
        if (raw.Contains("mac") || raw.Contains("darwin") || raw.Contains("os x")) return "macOS";
        return string.IsNullOrWhiteSpace(raw) ? "Unknown" : Format.Capitalize(raw);
    }

    /// <summary>The OS version, preferring the flat field and falling back to the module summary.</summary>
    private static string OsLabel(FleetDevice d)
    {
        var os = d.Modules?.System?.OperatingSystem;
        var version = Pick(d.OsVersion, os?.DisplayVersion, os?.Version, os?.Build);
        return string.IsNullOrWhiteSpace(version) ? "Unknown" : version!;
    }

    private static string? Pick(params string?[] options) =>
        options.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));

    private static string DisplayName(FleetDevice d) =>
        !string.IsNullOrWhiteSpace(d.Name) ? d.Name
        : !string.IsNullOrWhiteSpace(d.Modules?.Inventory?.DeviceName) ? d.Modules!.Inventory!.DeviceName!
        : !string.IsNullOrWhiteSpace(d.SerialNumber) ? d.SerialNumber
        : d.DeviceId;

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static string Relative(DateTime? when)
    {
        if (when is null) return "unknown";
        var delta = DateTime.UtcNow - when.Value.ToUniversalTime();
        if (delta < TimeSpan.Zero) return "just now";
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }
}

/// <summary>
/// The dashboard's three groups -- status, insights, activity -- laid out for the
/// width the window actually has, and re-laid as it is resized.
/// </summary>
/// <remarks>
/// Wide: three columns side by side, so a desktop-sized window shows everything
/// without scrolling instead of a laptop-width column with margins. Medium: status
/// and activity side by side, the insight cards below in two columns. Narrow: one
/// column. Decided on the layout's own width rather than the window's, so a host
/// that embeds the dashboard in part of a window gets the layout that fits it.
/// </remarks>
public sealed class DashboardLayout : Grid
{
    /// <summary>
    /// The content width at which three columns fit, around 1500 px of window once
    /// the page padding and scroll bar are taken off.
    /// </summary>
    public const double WideWidth = 1420;

    /// <summary>Below this, status and activity are too cramped side by side.</summary>
    public const double NarrowWidth = 860;

    private const double Gap = 18;

    private enum Mode { None, Narrow, Medium, Wide }

    private readonly FrameworkElement _status;
    private readonly Shared.UniformCardPanel _insights;
    private readonly FrameworkElement _activity;
    private Mode _mode = Mode.None;

    public DashboardLayout(FrameworkElement status, Shared.UniformCardPanel insights, FrameworkElement activity)
    {
        _status = status;
        _insights = insights;
        _activity = activity;
        Children.Add(status);
        Children.Add(insights);
        Children.Add(activity);
        Apply(Mode.Medium);
        SizeChanged += (_, e) => Apply(ModeFor(e.NewSize.Width));
    }

    private static Mode ModeFor(double width) =>
        width >= WideWidth ? Mode.Wide : width >= NarrowWidth ? Mode.Medium : Mode.Narrow;

    private void Apply(Mode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        foreach (var child in new[] { _status, (FrameworkElement)_insights, _activity })
        {
            SetRow(child, 0);
            SetColumn(child, 0);
            SetColumnSpan(child, 1);
            child.Margin = new Thickness(0);
        }

        switch (mode)
        {
            case Mode.Wide:
                // Status and insights share one width; activity, whose event
                // messages are the content that wants room, takes the rest.
                AddColumns(1, 1, 2);
                SetColumn(_insights, 2);
                SetColumn(_activity, 4);
                // One card per row: the column is the width of one card.
                _insights.MaxColumns = 1;
                break;

            case Mode.Medium:
                AddColumns(3, 7);
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                SetColumn(_activity, 2);
                SetRow(_insights, 1);
                SetColumnSpan(_insights, 3);
                _insights.Margin = new Thickness(0, Gap, 0, 0);
                _insights.MaxColumns = 2;
                break;

            default:
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                SetRow(_activity, 1);
                SetRow(_insights, 2);
                _activity.Margin = new Thickness(0, Gap, 0, 0);
                _insights.Margin = new Thickness(0, Gap, 0, 0);
                _insights.MaxColumns = 2;
                break;
        }
        _insights.InvalidateMeasure();
    }

    private void AddColumns(params double[] weights)
    {
        for (var i = 0; i < weights.Length; i++)
        {
            if (i > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights[i], GridUnitType.Star) });
        }
    }
}
