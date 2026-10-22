using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;
using State = ReportMate.App.Views.Fleet.ApplicationsReport.State;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Reports › Applications, with the web's three reports: Versions, the installed copies
/// of the chosen applications and how their versions split; Usage, how much the fleet
/// used them over a period; and Missing, the devices that match the selection and do
/// not have them. The applications are chosen from a chip cloud, and everything the
/// report shows is held in its link, so a copied link reopens the same report here, on
/// the Mac and on the web.
/// </summary>
public sealed class ApplicationsReportPage : FleetPage
{
    private readonly DeepLink? _carried;
    private State _state = new();
    private bool _seeded;
    private ApplicationsReport.Options? _options;
    private string _chipSearch = "";

    /// <summary>
    /// Apps in the Usage report switched off by their toggle: out of the table and the
    /// widgets, still in the selection. The web keeps this on the page, not in its
    /// link, and starts every new report with all of them on, so this does too.
    /// </summary>
    private readonly HashSet<string> _switchedOff = new(StringComparer.Ordinal);
    private string _toggleSearch = "";
    private bool _togglesOpen;
    private bool _widgetsOpen = true;
    private ApplicationsReport.UsageMetric _metric = ApplicationsReport.UsageMetric.Launches;

    /// <summary>The chip cloud draws this many names before asking for a narrower search.</summary>
    private const int ChipLimit = 150;

    /// <summary>Version cards drawn at most, as the web caps them.</summary>
    private const int CardLimit = 50;

    /// <param name="carried">
    /// The link the page this one replaces was showing. A page is rebuilt when the
    /// toolbar's platform changes, and without it the rebuilt report would forget
    /// which applications it was about.
    /// </param>
    public ApplicationsReportPage(DeepLink? carried = null) => _carried = carried;

    /// <summary>The link that reopens this report as it is showing now.</summary>
    public DeepLink CurrentLink() => _state.Link();

    private static ReportArea Area => ReportArea.ById("applications")!;

    protected override (string, string, Accent)? Heading => (Area.Title, Area.Subtitle, Area.Accent);

    protected override async Task<UIElement> BuildAsync()
    {
        if (!_seeded)
        {
            _seeded = true;
            var link = Link ?? _carried;
            _state = State.From(k => link?[k]);
        }
        var state = _state;
        var platform = ApplicationsReport.PlatformToken(state.Platform, PlatformFilter.Label);

        var page = new StackPanel();
        page.Children.Add(Ui.TabHeader(Area.Title, Area.Subtitle, Area.Glyph, Area.Accent, ModeSwitch(state)));

        // The chip cloud's names and the device list Missing subtracts from come from
        // one read, taken once: they change on the scale of days, not refreshes.
        var total = (_options is null ? 1 : 0) + 1 + (state.Apps.Count > 0 ? 1 : 0);
        var done = 0;
        void Tick() => Progress(++done, total);
        Progress(0, total);

        var optionsTask = _options is null ? Track(FleetApiClient.Instance.GetRawAsync("/api/v1/applications/filters"), Tick) : null;
        var mainTask = Track(FleetApiClient.Instance.GetRawAsync(state.IsUsage ? state.UsagePath(platform) : state.InventoryPath(platform)), Tick);
        var distributionTask = state.DistributionPath(platform) is { } d ? Track(FleetApiClient.Instance.GetRawAsync(d), Tick) : null;

        string? optionsError = null;
        if (optionsTask is not null)
        {
            var options = await optionsTask;
            if (options.Ok) using (var doc = options.Data!) _options = ApplicationsReport.ReadOptions(doc.RootElement);
            else optionsError = options.Detail ?? "The application names could not be loaded.";
        }

        page.Children.Add(Spaced(Controls(state, platform, optionsError)));

        var main = await mainTask;
        IReadOnlyDictionary<string, ApplicationsReport.VersionBucket>? distribution = null;
        if (distributionTask is not null)
        {
            // The distribution is a better count, not the only one: without it the
            // versions are folded from the installed rows, capped as they are.
            var dist = await distributionTask;
            if (dist.Ok) using (var doc = dist.Data!) distribution = ApplicationsReport.ReadDistribution(doc.RootElement);
        }

        if (!main.Ok)
        {
            page.Children.Add(Spaced(main.HttpStatus == 404
                ? Ui.Card(Ui.EmptyState(state.IsUsage
                    ? "Not available yet: this API does not serve fleet application usage (404)."
                    : "Not available yet: this API does not serve the fleet applications list (404)."))
                : FleetUnavailable(main.Status, main.Detail)));
            return page;
        }

        using (var doc = main.Data!)
        {
            if (state.IsUsage) Usage(page, state, ApplicationsReport.ReadUsage(doc.RootElement), distribution);
            else
            {
                var rows = ApplicationsReport.BaseFilter(ApplicationsReport.ReadInstalled(doc.RootElement), state, platform);
                var fetched = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : rows.Count;
                if (state.IsMissing) Missing(page, state, rows, platform, optionsError);
                else Versions(page, state, rows, distribution, capped: fetched >= 5000);
            }
        }
        return page;
    }

    private static async Task<T> Track<T>(Task<T> task, Action tick)
    {
        try { return await task; }
        finally { tick(); }
    }

    private static UIElement Spaced(UIElement e, double top = 18)
    {
        if (e is FrameworkElement fe) fe.Margin = new Thickness(0, top, 0, 0);
        return e;
    }

    /// <summary>
    /// Show the report a changed selection describes. The newest change wins over a
    /// load still running, so clicking through chips never lands on a stale report.
    /// </summary>
    private void Apply(State next)
    {
        _state = next;
        _switchedOff.Clear();
        _toggleSearch = "";
        _ = ReloadAsync(force: true);
    }

    // ── Controls ───────────────────────────────────────────────────────

    /// <summary>Versions, Usage or Missing: the web's three report buttons as one switch.</summary>
    private UIElement ModeSwitch(State state)
    {
        var current = state.IsUsage ? "usage" : state.IsMissing ? "missing" : "versions";
        return Ui.Segmented([("versions", "Versions"), ("usage", "Usage"), ("missing", "Missing")], current, key =>
        {
            if (key == current) return;
            Apply(key switch
            {
                "usage" => state with { Type = "usage", Missing = false },
                "missing" => state with { Type = "versions", Missing = true },
                _ => state with { Type = "versions", Missing = false },
            });
        });
    }

    private UIElement Controls(State state, string? platform, string? optionsError)
    {
        var panel = new StackPanel();

        var bar = new WrapPanel();
        void Add(UIElement e) { if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 12, 8); bar.Children.Add(e); }
        if (state.IsUsage)
        {
            Add(Ui.Segmented(ApplicationsReport.Periods.Select(p => (p.Days.ToString(), p.Label)), state.Period.ToString(),
                key => Apply(state with { Period = int.Parse(key) })));
        }

        // What else the link narrowed by, each with a way to drop it.
        foreach (var (label, values, clear) in new (string, IReadOnlyList<string>, Func<State>)[]
                 {
                     ("Versions", state.Versions, () => state with { Versions = [] }),
                     ("Publishers", state.Publishers, () => state with { Publishers = [] }),
                     ("Usage", state.Usages, () => state with { Usages = [] }),
                     ("Catalog", state.Catalogs, () => state with { Catalogs = [] }),
                     ("Location", state.Locations, () => state with { Locations = [] }),
                     ("Room", state.Rooms, () => state with { Rooms = [] }),
                     ("Area", state.Areas, () => state with { Areas = [] }),
                     ("Fleet", state.Fleets, () => state with { Fleets = [] }),
                     ("Platform", state.Platform is { } p ? [p] : [], () => state with { Platform = null }),
                 })
        {
            if (values.Count == 0) continue;
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(Ui.Pill($"{label}: {string.Join(", ", values)}", Tone.Info));
            var x = Ui.IconButton("", "Clear", (_, _) => Apply(clear()));
            x.Margin = new Thickness(4, 0, 0, 0);
            chip.Children.Add(x);
            Add(chip);
        }
        if (bar.Children.Count > 0) panel.Children.Add(bar);

        var title = state.Apps.Count == 0 ? "Applications" : $"Applications · {state.Apps.Count:N0} selected";
        panel.Children.Add(Ui.Collapsible(title, ChipCloud(state, platform, optionsError), open: state.Apps.Count == 0));
        return panel;
    }

    /// <summary>
    /// The application names to choose from, narrowed by the search box above them.
    /// Typing narrows the chips in place; choosing one reloads the report.
    /// </summary>
    private UIElement ChipCloud(State state, string? platform, string? optionsError)
    {
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        if (_options is null)
        {
            body.Children.Add(Ui.Caption(optionsError ?? "The application names could not be loaded."));
            return body;
        }

        var cloud = new WrapPanel();
        var note = Ui.Caption("");
        note.Margin = new Thickness(0, 4, 0, 0);

        void Fill()
        {
            cloud.Children.Clear();
            var names = ApplicationsReport.ChipNames(_options, platform, _chipSearch, state.Apps);
            // The chosen apps lead, so one can be unchosen however long the list.
            var ordered = names.Where(n => state.Apps.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Concat(names.Where(n => !state.Apps.Contains(n, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            foreach (var name in ordered.Take(ChipLimit))
                cloud.Children.Add(Chip(name, state.Apps.Contains(name, StringComparer.OrdinalIgnoreCase),
                    () => Apply(state.ToggleApp(name))));
            note.Text = ordered.Count > ChipLimit
                ? $"Showing {ChipLimit:N0} of {ordered.Count:N0} applications; search to narrow them."
                : ordered.Count == 0 ? "No application matches this search." : $"{ordered.Count:N0} applications";
        }

        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var search = new SearchBox("Filter applications...", q => { _chipSearch = q; Fill(); }) { Width = 280, Margin = new Thickness(0) };
        top.Children.Add(search);
        if (state.Apps.Count > 0)
        {
            var clear = Ui.IconButton("", "Clear selection", (_, _) => Apply(state with { Apps = [], Versions = [], Missing = false }));
            clear.Margin = new Thickness(8, 0, 0, 0);
            top.Children.Add(clear);
        }
        body.Children.Add(top);
        body.Children.Add(cloud);
        body.Children.Add(note);
        if (_chipSearch.Length > 0) search.Text = _chipSearch;
        else Fill();
        return body;
    }

    /// <summary>A name to choose: outlined until it is chosen, then washed in the accent.</summary>
    private static Border Chip(string label, bool active, Action toggle)
    {
        var text = new TextBlock
        {
            Text = label, FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = active ? Ui.StatusBrush(Tone.Info) : Ui.Brush("TextSecondaryBrush"),
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Background = active ? Ui.Tint(Tone.Info, 0.15) : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = active ? Ui.Tint(Tone.Info, 0.45) : Ui.Brush("CardBorderBrush"),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = active ? $"Remove {label} from the report" : $"Add {label} to the report",
            Child = text,
        };
        chip.MouseLeftButtonUp += (_, _) => toggle();
        chip.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { toggle(); e.Handled = true; }
        };
        SetAutomationName(chip, label, active);
        return chip;
    }

    private static void SetAutomationName(Border chip, string label, bool active) =>
        System.Windows.Automation.AutomationProperties.SetName(chip, active ? $"{label}, selected" : label);

    // ── Versions ───────────────────────────────────────────────────────

    private void Versions(StackPanel page, State state, IReadOnlyList<ApplicationsReport.Installed> rows,
        IReadOnlyDictionary<string, ApplicationsReport.VersionBucket>? distribution, bool capped)
    {
        var shown = ApplicationsReport.WithVersions(rows, state.Versions);
        var devices = shown.Select(r => r.Serial).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        page.Children.Add(Spaced(Ui.Caption($"{shown.Count:N0} applications across {devices:N0} devices"), 14));

        var cards = VersionCards(state, ApplicationsReport.VersionAnalysis(distribution, rows));
        if (cards is not null) page.Children.Add(cards);

        var table = new FilteredTable<InstalledRow>("Applications", "{0} of {1} installed applications",
                shown.Select(r => new InstalledRow { Row = r }).ToList(), (r, q) => r.Matches(q),
                [
                    Col.Text("Application", "Name", star: true),
                    Col.Text("Version", "Version", 130, sortBy: "VersionKey"),
                    Col.Text("Publisher", "Publisher", 170),
                    Col.Text("Usage", "Usage", 100),
                    Col.Text("Catalog", "Catalog", 100),
                    Col.Text("Area", "Area", 120),
                    Col.Text("Location", "Location", 130),
                    Col.Text("Device", "Device", 180),
                    Col.Platform("Platform", "Platform", 110),
                ], "Search applications...", rows.Count == 0
                    ? "No installed application matches this selection."
                    : "No application matches the current filters")
            .WithQuery(state.Query)
            .WithRowLink(r => DeviceLink(r.Row.Serial))
            .WithCsv(ApplicationsReport.CsvFileName(state, DateTime.Now), ApplicationsReport.InstalledCsvHeaders,
                r => ApplicationsReport.InstalledCsvRow(r.Row))
            .Build();
        page.Children.Add(Spaced(table, 14));

        if (capped)
            page.Children.Add(Spaced(Ui.Caption(
                "The API returns at most 5,000 installed applications, and this selection reached that. "
                + "Choose applications above for the whole fleet's count; the version cards use the server's count when they can."), 8));
    }

    /// <summary>
    /// One card per application, its versions newest first. A version opens the report
    /// narrowed to it, as clicking a version on the web's cards does.
    /// </summary>
    private UIElement? VersionCards(State state, IReadOnlyDictionary<string, Dictionary<string, int>> analysis)
    {
        var cards = ApplicationsReport.VersionCards(analysis);
        if (cards.Count == 0) return null;

        var grid = Ui.CardGrid(300, 14);
        grid.MaxColumns = 6;
        grid.Margin = new Thickness(0, 14, 0, 0);
        foreach (var card in cards.Take(CardLimit))
        {
            var body = new StackPanel();
            foreach (var (version, count) in card.Versions.Take(8))
            {
                var filter = $"{card.App}:{version}";
                var chosen = state.Versions.Contains(filter, StringComparer.Ordinal);
                var bar = Charts.Bar("v" + version, count, card.Total, chosen ? Tone.Success : Tone.Info);
                var next = state with { Type = "versions", Missing = false, Versions = ApplicationsReport.Toggle(state.Versions, filter) };
                body.Children.Add(Linkable.Wrap(bar, next.Link(),
                    chosen ? $"Show every version of {card.App}" : $"Show the devices with {card.App} {version}"));
            }
            if (card.Versions.Count > 8) body.Children.Add(Ui.Caption($"and {card.Versions.Count - 8:N0} more"));
            grid.Children.Add(Ui.StatBlock(card.App, $"{card.Total:N0} devices · {card.Versions.Count:N0} versions", "",
                Accent.Blue, new Border { Padding = new Thickness(20, 4, 20, 14), Child = body }));
        }

        var panel = new StackPanel();
        panel.Children.Add(Spaced(Ui.Text("Version Distribution", "TitleTextStyle"), 18));
        panel.Children.Add(grid);
        if (cards.Count > CardLimit)
            panel.Children.Add(Spaced(Ui.Caption($"and {cards.Count - CardLimit:N0} more applications"), 6));
        return panel;
    }

    public sealed class InstalledRow
    {
        public ApplicationsReport.Installed Row { get; init; } = null!;
        public string Name => Row.Name;
        public string Version => Row.Version;
        /// <summary>Sorts 10.0 after 9.0, as a version column should.</summary>
        public VersionKey VersionKey => new(Row.Version);
        public string Publisher => Row.Publisher;
        public string Usage => Row.Usage;
        public string Catalog => Row.Catalog;
        public string Area => Row.Area;
        public string Location => Row.Location;
        public string Device => Row.Device;
        public string Platform => Row.Platform;

        public bool Matches(string q) => string.IsNullOrWhiteSpace(q)
            || $"{Name} {Version} {Publisher} {Device} {Row.Serial} {Location}".Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A version that sorts numerically wherever the grid compares it.</summary>
    public sealed class VersionKey(string version) : IComparable
    {
        public string Version { get; } = version;
        public int CompareTo(object? other) =>
            ApplicationsReport.VersionComparer.Instance.Compare(Version, (other as VersionKey)?.Version);
        public override string ToString() => Version;
    }

    // ── Usage ──────────────────────────────────────────────────────────

    private void Usage(StackPanel page, State state, ApplicationsReport.UsageReport report,
        IReadOnlyDictionary<string, ApplicationsReport.VersionBucket>? distribution)
    {
        if (report.Unavailable)
        {
            page.Children.Add(Spaced(Ui.Card(Ui.EmptyState(report.Message ?? "Usage tracking is not available on this API yet."))));
            return;
        }

        var s = report.Summary;
        var stats = Ui.Columns(5, 14,
            AppUsagePage.Stat("Applications", s.AppsTracked.ToString("N0")),
            AppUsagePage.Stat("Active hours", s.ActiveHours is { } a ? Math.Round(a).ToString("N0") : "—"),
            AppUsagePage.Stat("Launches", s.Launches.ToString("N0")),
            AppUsagePage.Stat("Devices", s.Devices.ToString("N0")),
            AppUsagePage.Stat("People", s.Users.ToString("N0")));
        page.Children.Add(Spaced(stats));
        page.Children.Add(Spaced(Ui.Caption($"Last {state.Period} days · {report.Headline}"), 8));

        // The usage response carries no version split of its own, so the versions
        // come from the distribution read when applications are chosen.
        var analysis = distribution is { Count: > 0 } || report.VersionDistribution.Count > 0
            ? ApplicationsReport.VersionAnalysis(distribution is { Count: > 0 } ? distribution : report.VersionDistribution, [])
            : new Dictionary<string, Dictionary<string, int>>();

        // Switching an app or the widgets' metric redraws this part alone; the report
        // itself does not change, so nothing is fetched again.
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Draw() => host.Content = UsageBody(state, report, analysis, Draw);
        Draw();
        page.Children.Add(host);

        if (report.Devices.Count > 0)
        {
            var devices = report.Devices.OrderByDescending(d => d.TotalHours).Select(d => new UsageDeviceRow { Device = d }).ToList();
            var deviceTable = new FilteredTable<UsageDeviceRow>("Devices", "{0} of {1} devices", devices, (r, q) => r.Matches(q),
                    [
                        Col.Text("Device", "Name", star: true),
                        Col.Text("Hours", "Hours", 90, sortBy: "HoursValue"),
                        Col.Text("Launches", "Launches", 90, sortBy: "LaunchValue"),
                        Col.Text("Usage", "Usage", 100),
                        Col.Text("Catalog", "Catalog", 100),
                        Col.Text("Area", "Area", 120),
                        Col.Text("Location", "Location", 140),
                    ], "Search devices...", "No device matches the current filters")
                .WithRowLink(r => DeviceLink(r.Device.Serial))
                .WithCsv($"applications-usage-devices-{state.Period}days-{DateTime.Now:yyyy-MM-dd}.csv",
                    ["Device", "Serial Number", "Hours", "Launches", "Usage", "Catalog", "Area", "Location"],
                    r => [r.Name, r.Device.Serial, r.Device.TotalHours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                        r.Device.LaunchCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        r.Usage, r.Catalog, r.Area, r.Location])
                .Build();
            page.Children.Add(Spaced(deviceTable, 14));
        }
    }

    private UIElement UsageBody(State state, ApplicationsReport.UsageReport report,
        IReadOnlyDictionary<string, Dictionary<string, int>> analysis, Action redraw)
    {
        var body = new StackPanel();
        _switchedOff.IntersectWith(report.Apps.Select(a => a.Name));
        if (report.Apps.Count > 1) body.Children.Add(Spaced(AppToggles(report, redraw), 14));
        if (report.Devices.Count > 0) body.Children.Add(Spaced(Widgets(state, report, redraw), 10));

        var apps = ApplicationsReport.ByActiveTime(ApplicationsReport.SwitchedOn(report.Apps, _switchedOff)).Select(a => new UsageRow
        {
            App = a,
            VersionsLabel = ApplicationsReport.VersionsSummary(analysis, a.Name),
        }).ToList();

        var table = new FilteredTable<UsageRow>("Applications Usage", "{0} of {1} applications", apps, (r, q) => r.Matches(q),
                [
                    Col.Text("Application", "Name", star: true),
                    Col.Text("Versions", "VersionsLabel", 170, wrap: true),
                    Col.Text("Active time", "Active", 120, sub: "ActiveSub", sortBy: "ActiveValue"),
                    Col.Text("Process time", "Process", 120, sub: "ProcessSub", sortBy: "ProcessValue"),
                    Col.Text("Launches", "Launches", 90, sortBy: "LaunchValue"),
                    Col.Text("Active devices", "Devices", 120, sub: "DevicesSub", sortBy: "DeviceValue"),
                    Col.Text("Active users", "Users", 110, sortBy: "UserValue"),
                    Col.Text("Last used", "LastUsed", 120, sortBy: "LastUsedAt"),
                    Col.Text("Status", "Status", 100),
                ], "Search applications...", report.Apps.Count > 0 && apps.Count == 0
                    ? "Every application in this report is switched off."
                    : "No usage was reported for this selection. Try a longer period or fewer filters.")
            .WithQuery(state.Query)
            .WithRowLink(r => state.UsageDrillLink(r.Name))
            .WithCsv(ApplicationsReport.CsvFileName(state, DateTime.Now), ApplicationsReport.UsageCsvHeaders,
                r => ApplicationsReport.UsageCsvRow(r.App))
            .Build();
        body.Children.Add(Spaced(table, 14));
        return body;
    }

    /// <summary>
    /// The web's in-report Applications accordion: a toggle per app in the report,
    /// with its own search, Select All and Clear. A toggle leaves the selection alone.
    /// </summary>
    private UIElement AppToggles(ApplicationsReport.UsageReport report, Action redraw)
    {
        var names = report.Apps.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var on = names.Count(n => !_switchedOff.Contains(n));

        var cloud = new WrapPanel();
        void Fill()
        {
            cloud.Children.Clear();
            var q = _toggleSearch.Trim();
            foreach (var name in names.Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(ChipLimit))
                cloud.Children.Add(Chip(name, !_switchedOff.Contains(name), () =>
                {
                    if (!_switchedOff.Remove(name)) _switchedOff.Add(name);
                    redraw();
                }));
        }

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var search = new SearchBox("Filter applications...", q => { _toggleSearch = q; Fill(); }) { Width = 260, Margin = new Thickness(0) };
        top.Children.Add(search);
        var all = Ui.IconButton("\uE8B3", "Select All", (_, _) => { _switchedOff.Clear(); redraw(); });
        all.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(all);
        var none = Ui.IconButton("\uE894", "Clear", (_, _) => { _switchedOff.UnionWith(names); redraw(); });
        none.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(none);

        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        body.Children.Add(top);
        body.Children.Add(cloud);
        if (_toggleSearch.Length > 0) search.Text = _toggleSearch;
        else Fill();

        var expander = Ui.Collapsible($"Applications in this report · {on:N0} of {names.Count:N0} on", body, _togglesOpen);
        expander.Expanded += (_, _) => _togglesOpen = true;
        expander.Collapsed += (_, _) => _togglesOpen = false;
        return expander;
    }

    /// <summary>
    /// The web's usage widgets, in its order: by usage type, by catalog and the device
    /// distribution, then by fleet, by area and by location. A bar opens the report
    /// narrowed to that value, as the dashboard's bars open theirs.
    /// </summary>
    private UIElement Widgets(State state, ApplicationsReport.UsageReport report, Action redraw)
    {
        var metric = _metric;
        var agg = ApplicationsReport.Aggregate(report.Devices, report.Apps, _switchedOff, metric);
        var label = metric == ApplicationsReport.UsageMetric.Hours ? "Hours" : "Launches";

        var grid = Ui.CardGrid(300, 14);
        grid.MaxColumns = 3;
        grid.Margin = new Thickness(0, 8, 0, 0);

        void Dimension(string title, IReadOnlyList<(string Label, double Value)> rows, Func<string, State>? narrow)
        {
            if (rows.Count == 0) return;
            var list = new StackPanel();
            foreach (var (name, value) in rows)
            {
                var bar = Charts.Bar(name, (int)Math.Round(value), (int)Math.Round(agg.GrandTotal), Tone.Info);
                if (bar is FrameworkElement fe) fe.ToolTip = ApplicationsReport.FormatMetric(value, metric);
                list.Children.Add(narrow is null ? bar : Linkable.Wrap(bar, narrow(name).Link(), $"Show usage for {name} alone"));
            }
            grid.Children.Add(Ui.StatBlock(title, null, "", Accent.Blue, new Border { Padding = new Thickness(20, 4, 20, 14), Child = list }));
        }

        Dimension($"{label} by Usage Type", agg.ByUsage, v => state with { Usages = [v] });
        Dimension($"{label} by Catalog", agg.ByCatalog, v => state with { Catalogs = [v] });
        if (agg.HasBins)
        {
            var bins = new StackPanel();
            foreach (var bin in agg.Bins) bins.Children.Add(Charts.Bar(bin.Label, bin.Count, agg.DeviceCount, Tone.Info));
            grid.Children.Add(Ui.StatBlock($"Device {label} Distribution", null, "", Accent.Blue,
                new Border { Padding = new Thickness(20, 4, 20, 14), Child = bins }));
        }
        Dimension($"{label} by Fleet", agg.ByFleet, v => state with { Fleets = [v] });
        Dimension($"{label} by Area (top 10)", agg.ByArea, v => state with { Areas = [v] });
        Dimension($"{label} by Location (top 10)", agg.ByLocation, v => state with { Locations = [v] });

        var switcher = Ui.Segmented([("launches", "Launches"), ("hours", "Hours")],
            metric == ApplicationsReport.UsageMetric.Hours ? "hours" : "launches", key =>
            {
                _metric = key == "hours" ? ApplicationsReport.UsageMetric.Hours : ApplicationsReport.UsageMetric.Launches;
                redraw();
            });
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        head.Children.Add(switcher);
        var count = Ui.Caption($"Aggregate stats across {agg.DeviceCount:N0} {(agg.DeviceCount == 1 ? "device" : "devices")}");
        count.Margin = new Thickness(12, 0, 0, 0);
        count.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(count);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(grid);
        var expander = Ui.Collapsible("Widgets", body, _widgetsOpen);
        expander.Expanded += (_, _) => _widgetsOpen = true;
        expander.Collapsed += (_, _) => _widgetsOpen = false;
        return expander;
    }

    public sealed class UsageRow
    {
        public ApplicationsReport.UsageApp App { get; init; } = null!;
        public string VersionsLabel { get; init; } = "";
        public string Name => App.Name;

        /// <summary>Active time leads; "Not reported" when no client in scope measures it, which is not zero.</summary>
        public string Active => App.ActiveSeconds is { } s ? ApplicationsReport.Duration(s) : "Not reported";
        public string ActiveSub => App.ActiveHours is { } h
            ? $"{h:0.0} hours" + (App.ForegroundHours is > 0 and var f ? $" · {f:0.0}h foreground" : "")
            : "";
        public double ActiveValue => App.ActiveHours ?? -1;
        public string Process => ApplicationsReport.Duration(App.TotalSeconds);
        public string ProcessSub => $"{App.TotalHours:0.0} hours" + (App.ActiveRatio is { } r ? $" · {r:P0} active" : "");
        public double ProcessValue => App.TotalHours;
        public string Launches => App.LaunchCount.ToString("N0");
        public int LaunchValue => App.LaunchCount;
        public string Devices => App.ShownDevices.ToString("N0");
        public string DevicesSub => App.InstalledDeviceCount is { } n ? $"{n:N0} installed" : "";
        public int DeviceValue => App.ShownDevices;
        public string Users => App.ShownUsers.ToString("N0");
        public int UserValue => App.ShownUsers;
        public string LastUsed => App.LastUsed is { } at ? Format.RelativeTime(at) : "Never";
        public DateTime LastUsedAt => App.LastUsed ?? DateTime.MinValue;
        public string Status => App.Status;

        public bool Matches(string q) => string.IsNullOrWhiteSpace(q) || Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class UsageDeviceRow
    {
        public ApplicationsReport.UsageDevice Device { get; init; } = null!;
        public string Name => Device.Name;
        public string Hours => $"{Device.TotalHours:N1}h";
        public double HoursValue => Device.TotalHours;
        public string Launches => Device.LaunchCount.ToString("N0");
        public int LaunchValue => Device.LaunchCount;
        public string Usage => Device.Usage;
        public string Catalog => Device.Catalog;
        public string Area => Device.Area;
        public string Location => Device.Location;

        public bool Matches(string q) => string.IsNullOrWhiteSpace(q)
            || $"{Name} {Device.Serial} {Location} {Area}".Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ── Missing ────────────────────────────────────────────────────────

    private void Missing(StackPanel page, State state, IReadOnlyList<ApplicationsReport.Installed> rows, string? platform, string? optionsError)
    {
        if (state.Apps.Count == 0)
        {
            page.Children.Add(Spaced(Ui.Card(Ui.EmptyState("Choose at least one application above to see the devices that do not have it."))));
            return;
        }
        if (_options is null)
        {
            page.Children.Add(Spaced(Ui.Card(Ui.EmptyState(
                $"The Missing report needs the fleet's device list, which could not be loaded. {optionsError}".Trim()))));
            return;
        }

        var missing = ApplicationsReport.MissingDevices(_options.Devices, rows, state);
        var apps = string.Join(", ", state.Apps);
        page.Children.Add(Spaced(Ui.Caption(
            $"{missing.Count:N0} devices match this selection and do not have {apps} installed."), 14));
        if (platform is not null)
            page.Children.Add(Spaced(Ui.Caption(
                "The fleet's device list does not say which platform each device runs, so devices on the other platform are included."), 4));

        var table = new FilteredTable<MissingRow>($"Devices Missing: {apps}", "{0} of {1} devices",
                missing.Select(d => new MissingRow { Device = d }).ToList(), (r, q) => r.Matches(q),
                [
                    Col.Text("Device", "Name", star: true),
                    Col.Text("Usage", "Usage", 110),
                    Col.Text("Catalog", "Catalog", 110),
                    Col.Text("Area", "Area", 130),
                    Col.Text("Location", "Location", 160),
                    Col.Text("Status", "Status", 100),
                ], "Search devices...", "Every device in this selection has the application.")
            .WithQuery(state.Query)
            .WithRowLink(r => DeviceLink(r.Device.Serial))
            .WithCsv(ApplicationsReport.CsvFileName(state, DateTime.Now), ApplicationsReport.MissingCsvHeaders,
                r => ApplicationsReport.MissingCsvRow(r.Device, state.Apps))
            .Build();
        page.Children.Add(Spaced(table, 14));
    }

    public sealed class MissingRow
    {
        public ApplicationsReport.FilterDevice Device { get; init; } = null!;
        public string Name => Device.Name;
        public string Usage => Device.Usage;
        public string Catalog => Device.Catalog;
        public string Area => Device.Area;
        public string Location => Device.Location;
        public string Status => "Missing";

        public bool Matches(string q) => string.IsNullOrWhiteSpace(q)
            || $"{Name} {Device.Serial} {Location} {Area}".Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A device's own page, on its Applications tab.</summary>
    private static DeepLink? DeviceLink(string serial) =>
        string.IsNullOrWhiteSpace(serial) ? null : DeepLink.For("device", serial, ("tab", "applications"));
}
