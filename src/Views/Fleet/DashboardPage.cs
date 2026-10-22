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

    /// <summary>
    /// The dashboard in skeleton, in the same layout it will take -- four columns
    /// on a wide display, three with the cards below on a desktop -- so nothing moves
    /// when the data lands.
    /// </summary>
    protected override UIElement LoadingBody()
    {
        var status = new StackPanel();
        var fleet = new StackPanel();
        fleet.Children.Add(Skeleton.Figures(1));
        var bars = Skeleton.Bars(3);
        bars.Margin = new Thickness(0, 16, 0, 0);
        fleet.Children.Add(bars);
        Stack(status, Skeleton.Card(fleet));
        Stack(status, Skeleton.Card(Skeleton.InsightBody(InsightShape.Figures)));
        Stack(status, Skeleton.Card(Skeleton.InsightBody(InsightShape.Bars)));
        Stack(status, Skeleton.Card(Skeleton.InsightBody(InsightShape.Figures)));

        var activity = new StackPanel();
        Stack(activity, Skeleton.Card(Skeleton.List(10, leadingPill: true)));
        Stack(activity, Skeleton.Card(Skeleton.Bars(2)));
        Stack(activity, Ui.Columns(2, 14, Skeleton.Card(Skeleton.Bars(5)), Skeleton.Card(Skeleton.Bars(5))));

        List<FrameworkElement> Cards(InsightPlacement column) => InsightWidgets.All
            .Where(card => card.Placement == column)
            .Select(card => (FrameworkElement)Skeleton.Card(Skeleton.InsightBody(card.Shape)))
            .ToList();
        return new DashboardLayout(status, [Skeleton.Card(Skeleton.List(6)), .. Cards(InsightPlacement.ColumnA)],
            Cards(InsightPlacement.ColumnB), activity);
    }

    protected override async Task<UIElement> BuildAsync()
    {
        // The insights document and the dashboard payload are asked for together;
        // the cards fill in whenever theirs arrives.
        var insights = DashboardInsightsModel.Shared;
        _ = insights.LoadAsync();
        // Two sources: the dashboard payload and the insights document.
        Progress(insights.HasDocument ? 1 : 0, 2);
        var result = await FleetApiClient.Instance.GetDashboardAsync();
        Progress(insights.HasDocument ? 2 : 1, 2);
        if (!result.Ok) return FleetUnavailable(result.Status, result.Detail);

        var data = result.Data!;
        // The toolbar's platform toggle is global, and the web applies it to every
        // widget on this page. Leaving the dashboard unfiltered made the control
        // look broken on the one page everybody lands on first.
        var devices = PlatformFilter.Apply(data.Devices);

        var page = new StackPanel();
        page.Children.Add(Ui.TabHeader("Dashboard", "Fleet status at a glance", "", Accent.Blue,
            Ui.Caption($"{devices.Count:N0} devices reporting")));

        // The insight cards draw from the shared model and redraw in place when the
        // insights document lands, which was asked for before the dashboard payload.
        var hosts = InsightWidgets.All.Select(card => new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Content = InsightWidgets.Build(card),
            Tag = card,
        }).ToList();
        WatchInsights(hosts);
        List<FrameworkElement> Hosts(InsightPlacement placement) =>
            hosts.Where(h => ((InsightCard)h.Tag).Placement == placement).Cast<FrameworkElement>().ToList();

        // Column 1: Fleet Status, then Installs (with the dashboard's largest
        // numbers), Management Tools Logs and Check-in Gaps.
        var status = new StackPanel();
        Stack(status, StatusWidget(devices));
        foreach (var host in Hosts(InsightPlacement.StatusColumn))
            Stack(status, host);

        var activity = new StackPanel();
        Stack(activity, RecentEventsWidget(data.Events));
        Stack(activity, PlatformDistributionWidget(devices));
        Stack(activity, Ui.Columns(2, 14,
            OsVersionWidget(devices, "Windows"),
            OsVersionWidget(devices, "macOS")));

        // Rod's order: New Clients heads the first insight column; Reboot Debt ends the second.
        var layout = new DashboardLayout(status,
            [(FrameworkElement)NewClientsWidget(devices), .. Hosts(InsightPlacement.ColumnA)],
            Hosts(InsightPlacement.ColumnB), activity)
        { Margin = new Thickness(0, 20, 0, 0) };
        page.Children.Add(layout);
        return page;
    }

    private Action? _insightsHandler;

    /// <summary>
    /// Redraw the cards when the insights document changes, for as long as this page
    /// is shown. The model outlives the page, so the handler has to come off when the
    /// page goes or every dashboard ever built would keep redrawing cards nobody sees.
    /// </summary>
    private void WatchInsights(List<ContentControl> hosts)
    {
        var model = DashboardInsightsModel.Shared;
        if (_insightsHandler is not null) model.Changed -= _insightsHandler;
        _insightsHandler = () =>
        {
            foreach (var host in hosts)
                if (host.Tag is InsightCard card) host.Content = InsightWidgets.Build(card);
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
        body.Children.Add(Linked(BigNumber(devices.Count, "devices total"), To("devices")));
        body.Children.Add(Linked(Charts.Bar("Active", Count(DeviceLiveness.Active), devices.Count, Tone.Success), To("devices", ("status", "active"))));
        body.Children.Add(Linked(Charts.Bar("Stale", Count(DeviceLiveness.Stale), devices.Count, Tone.Warning), To("devices", ("status", "stale"))));
        body.Children.Add(Linked(Charts.Bar("Missing", Count(DeviceLiveness.Missing), devices.Count, Tone.Error), To("devices", ("status", "missing"))));
        if (Count(DeviceLiveness.Archived) > 0)
            body.Children.Add(Linked(Charts.Bar("Archived", Count(DeviceLiveness.Archived), devices.Count, Tone.Neutral), To("devices", ("status", "archived"))));
        return Linkable.LinkHeader(Ui.StatBlock("Fleet Status", "Seen in the last 24 hours", "", Accent.Green, Pad(body)), To("devices"));
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
            return Linkable.LinkHeader(Ui.StatBlock("New Clients", "Recently enrolled", "", Accent.Indigo,
                Pad(Ui.EmptyState("No devices have first reported recently."))), To("devices"));

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
            body.Children.Add(Linked(row, DeviceLink(d.SerialNumber)));
        }
        return Linkable.LinkHeader(Ui.StatBlock("New Clients", "Recently enrolled", "", Accent.Indigo, Pad(body)), To("devices"));
    }

    /// <summary>The web's RecentEventsTable.</summary>
    private static UIElement RecentEventsWidget(List<FleetEvent> events)
    {
        if (events.Count == 0)
            return Linkable.LinkHeader(Ui.StatBlock("Recent Events", "Across the fleet", "", Accent.Cyan,
                Pad(Ui.EmptyState("No events have been reported."))), To("events"));

        var body = new StackPanel();
        foreach (var e in events.OrderByDescending(e => e.When ?? DateTime.MinValue).Take(12))
        {
            var row = new Grid();
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
            // An event opens the device it came from; one with no serial opens the
            // events list, filtered to its kind.
            body.Children.Add(Linked(row, string.IsNullOrWhiteSpace(e.SerialNumber)
                ? To("events", ("filter", kind))
                : DeviceLink(e.SerialNumber)));
        }
        return Linkable.LinkHeader(Ui.StatBlock("Recent Events", "Across the fleet", "", Accent.Cyan, Pad(body)), To("events"));
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
            body.Children.Add(Linked(Charts.Bar(name, count, total, name == "Windows" ? Tone.Info : Tone.Neutral, platform: name),
                name == "Unknown" ? To("devices") : To("devices", ("platform", name))));

        return Linkable.LinkHeader(Ui.StatBlock("Platforms", "Devices by operating system", "", Accent.Purple, Pad(body)), To("devices"));
    }

    private static UIElement OsVersionWidget(List<FleetDevice> devices, string platform)
    {
        var matching = devices.Where(d => NormalizePlatform(d) == platform).ToList();
        if (matching.Count == 0)
            return Linkable.LinkHeader(PlatformTile(Ui.StatBlock($"{platform} Versions", null, "", Accent.Gray,
                Pad(Ui.EmptyState($"No {platform} devices are reporting."))), platform, Accent.Gray), To("system"));

        var groups = matching
            .GroupBy(OsLabel)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .Take(8)
            .ToList();

        var body = new StackPanel();
        foreach (var (name, count) in groups)
            body.Children.Add(Linked(Charts.Bar(name, count, matching.Count, Tone.Info),
                name == "Unknown" ? To("system") : To("system", ("osVersion", name))));

        return Linkable.LinkHeader(PlatformTile(Ui.StatBlock($"{platform} Versions", $"{matching.Count:N0} devices", "", Accent.Teal, Pad(body)), platform, Accent.Teal), To("system"));
    }

    // ── Small parts ──────────────────────────────────────────────────────

    /// <summary>Put the platform's logo in a card's icon tile in place of the font glyph.</summary>
    private static Border PlatformTile(Border card, string platform, Accent accent)
    {
        if (card.Child is StackPanel { Children.Count: > 0 } root && root.Children[0] is Grid header
            && header.Children.Count > 0 && header.Children[0] is Border tile
            && PlatformIcon.For(platform, 18, Ui.Brush($"Icon{accent}Foreground")) is { } logo)
        {
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            tile.Child = logo;
        }
        return card;
    }

    private static DeepLink To(string section, params (string, string?)[] filters) => DeepLink.For(section, null, filters);

    /// <summary>Where a device row goes: that device's own page.</summary>
    private static DeepLink DeviceLink(string? serial) =>
        string.IsNullOrWhiteSpace(serial) ? To("devices") : DeepLink.For("device", serial);

    /// <summary>A widget row that follows a link, inset so its hover tint has room.</summary>
    private static UIElement Linked(UIElement content, DeepLink link)
    {
        // The bars carry their own bottom spacing; move it outside the tint.
        var gap = 0.0;
        if (content is FrameworkElement fe && fe.Margin.Bottom > 0)
        {
            gap = fe.Margin.Bottom;
            fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, 0);
        }
        var wrap = Linkable.Wrap(content, link);
        wrap.Padding = new Thickness(6, 3, 6, 3);
        wrap.Margin = new Thickness(-6, 0, -6, Math.Max(gap - 6, 3));
        return wrap;
    }

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
/// The dashboard's groups -- status, the insight cards, activity -- laid out for the
/// width the window actually has, and re-laid as it is resized.
/// </summary>
/// <remarks>
/// The layout is the shared dashboard spec's, so the web, the Mac and this app put
/// the same card in the same place at the same width:
/// <list type="bullet">
/// <item>Wide (27-inch class): four stacks 1/1/1/2 -- status, the two insight
/// columns in Rod's order, then activity.</item>
/// <item>Desktop: three stacks 1/1/2, with the second insight column's cards dealt
/// into them, each to the stack holding the fewest.</item>
/// <item>Medium: two stacks 30/70, status and activity, with every insight card dealt
/// into them the same way.</item>
/// <item>Narrow: one stack.</item>
/// </list>
/// Every tier is independent column stacks, never a grid row below the columns: a
/// row underneath leaves gaps beside whichever column runs tallest.
/// Decided on the layout's own width rather than the window's, so a host that embeds
/// the dashboard in part of a window gets the layout that fits it. Cards keep their
/// own height; nothing is stretched to match a neighbour.
/// </remarks>
public sealed class DashboardLayout : Grid
{
    /// <summary>
    /// Content width for four columns: a window of about 2,000 effective pixels, which
    /// is a 27-inch display at 125% scaling, once the page padding and scroll bar are
    /// taken off.
    /// </summary>
    public const double WideWidth = 1940;

    /// <summary>Content width for three columns, around 1,500 px of window.</summary>
    public const double DesktopWidth = 1420;

    /// <summary>Below this, status and activity are too cramped side by side.</summary>
    public const double NarrowWidth = 860;

    private const double Gap = 18;

    public enum Mode { None, Narrow, Medium, Desktop, Wide }

    private readonly FrameworkElement _status;
    private readonly IReadOnlyList<FrameworkElement> _cards;
    private readonly FrameworkElement _activity;
    private readonly IReadOnlyList<FrameworkElement> _columnA;
    private readonly IReadOnlyList<FrameworkElement> _columnB;
    private Mode _mode = Mode.None;

    /// <param name="columnA">The first insight column, top to bottom (New Clients first).</param>
    /// <param name="columnB">The second insight column, top to bottom.</param>
    /// <remarks>
    /// The two columns are the order Rod set: on a wide display they stand side by
    /// side; on a desktop the first stays beside the status column and the second
    /// runs four across underneath; narrower, both flow into one grid, first then second.
    /// </remarks>
    public DashboardLayout(FrameworkElement status, IReadOnlyList<FrameworkElement> columnA,
        IReadOnlyList<FrameworkElement> columnB, FrameworkElement activity)
    {
        _status = status;
        _columnA = columnA;
        _columnB = columnB;
        _cards = [.. columnA, .. columnB];
        _activity = activity;
        Apply(Mode.Medium);
        SizeChanged += (_, e) => Apply(ModeFor(e.NewSize.Width));
    }

    public static Mode ModeFor(double width) =>
        width >= WideWidth ? Mode.Wide
        : width >= DesktopWidth ? Mode.Desktop
        : width >= NarrowWidth ? Mode.Medium
        : Mode.Narrow;

    private void Apply(Mode mode)
    {
        if (mode == _mode) return;
        _mode = mode;

        // Take every element out of whatever held it before re-placing it.
        Children.Clear();
        foreach (var element in _cards.Append(_status).Append(_activity))
            if (element.Parent is Panel holder) holder.Children.Remove(element);
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        foreach (var child in new[] { _status, _activity })
        {
            SetRow(child, 0);
            SetColumn(child, 0);
            SetColumnSpan(child, 1);
            child.Margin = new Thickness(0);
        }

        switch (mode)
        {
            case Mode.Wide:
            {
                // Insight columns share column 1's width; activity, whose event
                // messages are the content that wants room, takes the rest.
                AddColumns(1, 1, 1, 2);
                var a = Column(_columnA);
                var b = Column(_columnB);
                Place(_status, 0, 0);
                Place(a, 0, 2);
                Place(b, 0, 4);
                Place(_activity, 0, 6);
                break;
            }

            case Mode.Desktop:
            {
                // Three stacks. The second insight column's cards are dealt into the
                // stacks, each to the one holding the fewest, never laid out as a row
                // underneath: a row leaves gaps beside a tall first column.
                AddColumns(1, 1, 2);
                var stacks = Deal(_columnB, [_status], [.. _columnA], [_activity]);
                Place(Column(stacks[0]), 0, 0);
                Place(Column(stacks[1]), 0, 2);
                Place(Column(stacks[2]), 0, 4);
                break;
            }

            case Mode.Medium:
            {
                // Two stacks, status and activity, with every insight card dealt into them.
                AddColumns(3, 7);
                var stacks = Deal(_cards, [_status], [_activity]);
                Place(Column(stacks[0]), 0, 0);
                Place(Column(stacks[1]), 0, 2);
                break;
            }

            default:
            {
                // One stack: status, activity, then every insight card in order.
                Place(Column([_status, _activity, .. _cards]), 0, 0);
                break;
            }
        }
    }

    /// <summary>
    /// Deal cards, in order, into stacks, each to the stack holding the fewest
    /// widgets so far (the leftmost on a tie). A status or activity panel counts its
    /// own widgets, so a column already holding four does not take the next card.
    /// </summary>
    private static List<FrameworkElement>[] Deal(IEnumerable<FrameworkElement> cards, params List<FrameworkElement>[] stacks)
    {
        static int Weight(FrameworkElement e) => e is Panel { Children.Count: > 0 } p ? p.Children.Count : 1;
        var list = cards.ToList();
        var targets = ColumnDeal.Assign(list.Count, stacks.Select(st => st.Sum(Weight)).ToArray());
        for (var i = 0; i < list.Count; i++) stacks[targets[i]].Add(list[i]);
        return stacks;
    }

    private void Place(FrameworkElement child, int row, int column, int span = 1)
    {
        SetRow(child, row);
        SetColumn(child, column);
        SetColumnSpan(child, span);
        Children.Add(child);
    }

    /// <summary>Cards stacked in one column, each as tall as what it shows.</summary>
    private static StackPanel Column(IEnumerable<FrameworkElement> cards)
    {
        var column = new StackPanel();
        foreach (var card in cards)
        {
            card.Margin = new Thickness(0, column.Children.Count == 0 ? 0 : 14, 0, 0);
            column.Children.Add(card);
        }
        return column;
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
