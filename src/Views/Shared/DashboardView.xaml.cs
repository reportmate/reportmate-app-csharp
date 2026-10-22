using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using ReportMate.App.Views.Device;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;

namespace ReportMate.App.Views.Shared;

/// <summary>
/// The ReportMate dashboard: the header -- platform scope, device search, section
/// tabs, settings -- over the page it navigates between. The ReportMate app shows
/// it as its window's content; another WPF app can host it to embed the dashboard.
/// A host whose window already has a search field passes
/// <see cref="DashboardChrome.HostProvided"/> and feeds that field into
/// <see cref="DeviceSearch"/>.
/// </summary>
public partial class DashboardView : UserControl
{
    private readonly Dictionary<string, Page> _pages = new();

    /// <summary>
    /// Pages replaced by a rebuild -- a platform change, a link -- kept until their
    /// successor is made, so it can open on what they were showing.
    /// </summary>
    private readonly Dictionary<string, Page> _replaced = new();

    private bool _reportsInline;
    private string _current = "dashboard";
    private DashboardChrome _chrome = DashboardChrome.Standard;

    /// <summary>
    /// The width of the tab bar with every report as its own tab, measured the last
    /// time it was drawn that way; zero until then.
    /// </summary>
    private double _inlineTabsWidth;

    /// <summary>The dashboard reading its palette and styles from the application's resources.</summary>
    public DashboardView() : this(scopedResources: false) { }

    /// <summary>
    /// The dashboard, optionally carrying its own palette and styles. An app that
    /// embeds it passes true: its own resources are left alone, and the dashboard
    /// keeps a copy of its own on this view and the pages it shows. The host still
    /// needs ModernWpf's ThemeResources and XamlControlsResources loaded.
    /// </summary>
    /// <param name="chrome">
    /// Which of its own controls the header draws; null is
    /// <see cref="DashboardChrome.Standard"/>, the header the ReportMate app shows.
    /// </param>
    public DashboardView(bool scopedResources, DashboardChrome? chrome = null)
    {
        if (scopedResources) DashboardResources.EnableScoped();
        DashboardResources.Adopt(this);
        InitializeComponent();
        _chrome = chrome ?? DashboardChrome.Standard;
        ApplyChrome();

        // The scope belongs to the window, so a page opened after it was set opens
        // already narrowed, and pages already built are rebuilt to match.
        PlatformFilter.Changed += () =>
        {
            SyncPlatformToggle();
            Navigate(_current, force: true);
        };
        SyncPlatformToggle();

        ReportsPage.ReportChosen += id =>
            NavigateTo(id switch
            {
                "applications/coverage" => "coverage",
                "failures" => "failures",
                _ => "report:" + id,
            });
        HeaderGrid.SizeChanged += (_, _) => BuildTabs();
        TabBarFrame.SizeChanged += (_, _) =>
        {
            if (!_reportsInline || TabBarFrame.ActualWidth <= 0) return;
            _inlineTabsWidth = Math.Max(_inlineTabsWidth, TabBarFrame.ActualWidth);
            BuildTabs();
        };
        // The arithmetic below decides before layout; this checks after it, so a
        // header that still ran under the caption buttons folds its reports.
        HeaderGrid.LayoutUpdated += (_, _) => FoldIfOverflowing();
        BuildTabs();
        Navigate("dashboard");
    }

    /// <summary>
    /// Which of its own controls the header draws. Changing it rebuilds the pages,
    /// since the Devices list draws its search box or leaves it to the host.
    /// </summary>
    public DashboardChrome Chrome
    {
        get => _chrome;
        set
        {
            if (value == _chrome) return;
            _chrome = value;
            ApplyChrome();
            Navigate(_current, force: true);
        }
    }

    private void ApplyChrome()
    {
        Brand.Visibility = _chrome.ShowsBrand ? Visibility.Visible : Visibility.Collapsed;
        SearchHost.Visibility = _chrome.ShowsSearchField ? Visibility.Visible : Visibility.Collapsed;
        // Without the search the column is only the gap between the controls, and
        // its width goes to the tabs.
        SearchColumn.MinWidth = _chrome.ShowsSearchField ? 140 : 0;
        BuildTabs();
    }

    /// <summary>
    /// The primary sections, in the same order as the web app and the Mac client.
    /// Reports sit inline as their own tabs when the header has the room for them
    /// beside its other controls, and collapse into one Reports tab when it does not.
    /// </summary>
    private void BuildTabs(bool rebuild = false)
    {
        // On the device page the reports always sit inline, as on the web: if the
        // header is short of room the search field gives way instead.
        var inline = _current == "device" || ReportsFitInline();
        SearchHost.Visibility = _chrome.ShowsSearchField && !(_current == "device" && _searchYielded)
            ? Visibility.Visible : Visibility.Collapsed;
        SearchColumn.MinWidth = SearchHost.Visibility == Visibility.Visible ? 140 : 0;
        if (!rebuild && TabBar.Children.Count > 0 && inline == _reportsInline) { SyncChecked(); return; }
        _reportsInline = inline;

        TabBar.Children.Clear();
        AddTab("dashboard", "Dashboard", "");
        AddTab("devices", "Devices", "");
        AddTab("events", "Events", "");

        // The reports were the tabs without an icon: they passed none and so read as
        // a run of bare words beside the three that had one, where the web and the
        // Mac client give every report its own.
        if (inline)
            foreach (var area in ReportArea.All) AddTab("report:" + area.Id, area.Title, area.Glyph);
        else
            AddTab("reports", "Reports", "");

        SyncChecked();
    }

    /// <summary>
    /// Whether every report fits as its own tab in the width the header's other
    /// controls leave: the mark, the platform toggle, the search field's minimum
    /// and the trailing buttons. Before the inline tabs have been measured they are
    /// drawn once to find out, which is what records their width.
    /// </summary>
    private bool ReportsFitInline()
    {
        if (_inlineTabsWidth <= 0) return true;
        var columns = HeaderGrid.ColumnDefinitions;
        // The search keeps its margins as well as its minimum, and the trailing
        // column's width already carries the caption buttons' reserve.
        var search = SearchColumn.MinWidth > 0 ? SearchColumn.MinWidth + SearchHost.Margin.Left + SearchHost.Margin.Right : 0;
        var others = columns[0].ActualWidth + columns[1].ActualWidth + columns[4].ActualWidth + search + FitSlack;
        return HeaderGrid.ActualWidth > 0 && _inlineTabsWidth <= HeaderGrid.ActualWidth - others;
    }

    /// <summary>Room kept spare so a tab never ends flush against the caption buttons.</summary>
    private const double FitSlack = 16;

    /// <summary>
    /// Fold the reports into one tab when the drawn header is wider than the window:
    /// the settings button's far edge, plus the room kept for the caption buttons,
    /// has to land inside the header. At about 1200 px the inline tabs ran on under
    /// the minimise, maximise and close buttons, because the fit was judged on widths
    /// measured before the tabs had been laid out.
    /// </summary>
    /// <summary>The device page gave the search field's room to its report tabs.</summary>
    private bool _searchYielded;

    private void FoldIfOverflowing()
    {
        if (_current != "device" && _searchYielded)
        {
            _searchYielded = false;
            BuildTabs();
        }
        if (!_reportsInline || HeaderGrid.ActualWidth <= 0 || TrailingItems.ActualWidth <= 0) return;
        double right;
        try
        {
            right = TrailingItems.TranslatePoint(new Point(TrailingItems.ActualWidth, 0), HeaderGrid).X
                    + TrailingItems.Margin.Right;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        var overflow = right - HeaderGrid.ActualWidth;
        if (overflow <= 0.5) return;
        if (_current == "device")
        {
            // Never fold the reports here; take the search field out instead.
            if (_searchYielded) return;
            _searchYielded = true;
            BuildTabs();
            return;
        }
        // Remember the shortfall as part of the tabs' width, so they stay folded
        // until the window is wide enough to take them back without overflowing.
        _inlineTabsWidth = Math.Max(_inlineTabsWidth, TabBarFrame.ActualWidth) + overflow + FitSlack;
        BuildTabs();
    }

    /// <summary>
    /// Room left at the header's right edge for a window's caption buttons, when the
    /// header is drawn in the title bar. Zero when hosted inside another window.
    /// </summary>
    public double CaptionButtonsWidth
    {
        get => TrailingItems.Margin.Right;
        set => TrailingItems.Margin = new Thickness(8, 0, value, 0);
    }

    /// <summary>The dashboard an element is shown in, found by walking up the visual tree.</summary>
    public static DashboardView? Containing(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is DashboardView view) return view;
            element = element is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void AddTab(string tag, string label, string? glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
            content.Children.Add(new ModernWpf.Controls.FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var tab = new RadioButton
        {
            Tag = tag,
            GroupName = "MainTabs",
            Style = (Style)FindResource("NavigationTabStyle"),
            Content = content,
        };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(tab, true);
        tab.Checked += OnTabChecked;
        TabBar.Children.Add(tab);
    }

    /// <summary>Keep the checked tab matching the page actually shown after a rebuild.</summary>
    private void SyncChecked()
    {
        // Checking a tab here only marks where the reader is. Letting it navigate
        // too sent a report opened while the reports were folded straight on to the
        // Reports landing page, and rebuilt a page a link had just opened.
        _syncing = true;
        try
        {
            // Tools Logs is the Management report's second view, under its tab.
            var tab = _current == "management-logs" ? "report:management" : _current;
            foreach (var child in TabBar.Children)
                if (child is RadioButton rb && rb.Tag?.ToString() == tab) { rb.IsChecked = true; return; }

            // The current page has no tab at this width (a report while collapsed):
            // show Reports as the active section instead of leaving nothing checked.
            if (_current.StartsWith("report:", StringComparison.Ordinal)
                || _current is "failures" or "coverage" or "management-logs" || _current.StartsWith("usage:", StringComparison.Ordinal))
                foreach (var child in TabBar.Children)
                    if (child is RadioButton rb && rb.Tag?.ToString() == "reports") { rb.IsChecked = true; return; }

            // Nothing on the bar matches (a drill-down page): clear the old mark.
            foreach (var child in TabBar.Children)
                if (child is RadioButton { IsChecked: true } rb) rb.IsChecked = false;
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool _syncing;

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (ContentFrame is null || _syncing) return;
        if (sender is RadioButton { Tag: string tag }) Navigate(tag);
    }

    private void Navigate(string tag, bool force = false)
    {
        // Pages are cached, so a change to what they should be showing has to empty
        // the cache or the change is invisible until the app restarts.
        if (force)
        {
            foreach (var (key, old) in _pages) _replaced[key] = old;
            _pages.Clear();
        }
        var wasDevice = _current == "device";
        _current = tag;
        // The device page always keeps the full row of report tabs.
        if (wasDevice != (tag == "device")) BuildTabs(rebuild: true);
        var page = GetOrCreatePage(tag);
        // A Frame hands its pages straight to the application's resources, so when
        // the dashboard carries its own, each page has to be given them.
        DashboardResources.Adopt(page);
        ContentFrame.Navigate(page);
    }

    /// <summary>Switch sections programmatically (a report card, or a device drill-down).</summary>
    public void NavigateTo(string tag)
    {
        foreach (var child in TabBar.Children)
            if (child is RadioButton rb && rb.Tag?.ToString() == tag) { rb.IsChecked = true; return; }
        if (tag == "Settings") { TabSettings.IsChecked = true; return; }
        Navigate(tag);
        SyncChecked();
    }

    /// <summary>
    /// Open the view a reportmate:// link names. The link's own query is handed to the
    /// page so a copied link reopens the exact filters it was copied from.
    /// </summary>
    public void OpenDeepLink(DeepLink link)
    {
        // A link brings its own filters; the host's last query no longer applies.
        _hostDeviceFilter = null;
        switch (link.Section)
        {
            case "settings":
                NavigateTo("Settings");
                return;

            case "device" or "this-device":
                // A serial addresses a fleet device; without one the link means this
                // machine, which the device page already renders from the local cache.
                var page = (DevicePage)GetOrCreatePage("device");
                page.ApplyDeepLink(link);
                NavigateTo("device");
                return;

            case "applications" when link.Argument is { } argument:
                // applications/coverage and applications/usage/<app> are pages of their
                // own on the web, so a link to one opens that page rather than the
                // applications report with a filter applied.
                if (argument.Equals("coverage", StringComparison.OrdinalIgnoreCase))
                {
                    ShowFresh("coverage");
                    return;
                }
                if (argument.StartsWith("usage/", StringComparison.OrdinalIgnoreCase))
                {
                    var days = int.TryParse(link["days"], out var d) ? d : 30;
                    // The App Usage card's usage and catalog filters ride along.
                    PendingLink = link;
                    ShowFresh($"usage:{days}:{argument["usage/".Length..]}");
                    return;
                }
                PendingLink = link;
                ShowFresh("report:applications");
                return;

            // management/logs is the Management report's second view, Tools Logs.
            case "management" when link.Argument is { } mgmt && mgmt.Equals("logs", StringComparison.OrdinalIgnoreCase):
                PendingLink = link;
                ShowFresh("management-logs");
                return;

            // events/failures is a page of its own on the web, the same shape as
            // applications/coverage, so a link to it opens that page rather than the
            // events list -- with the link's window, reason and outcome.
            case "events" when link.Argument is { } events
                && events.Equals("failures", StringComparison.OrdinalIgnoreCase):
                PendingLink = link;
                ShowFresh("failures");
                return;

            case "dashboard" or "devices" or "events":
                PendingLink = link;
                ShowFresh(link.Section);
                return;

            default:
                // Every remaining section is a report, and they share their names with
                // the web routes, so the section is the report id.
                PendingLink = link;
                ShowFresh("report:" + link.Section);
                return;
        }
    }

    /// <summary>
    /// Show a page built for the link just followed. Pages are cached and read their
    /// link once, so a report already opened unfiltered would otherwise come back
    /// unfiltered and leave the link waiting for whichever page loaded next.
    /// </summary>
    private void ShowFresh(string tag)
    {
        if (_pages.Remove(tag, out var old)) _replaced[tag] = old;
        Navigate(tag);
        SyncChecked();
    }

    /// <summary>
    /// Follow a link from inside the dashboard -- a widget, a bar, a chip -- to the
    /// view it names, filtered as it says.
    /// </summary>
    public static void Follow(DependencyObject from, DeepLink link) => Containing(from)?.OpenDeepLink(link);

    /// <summary>The link a page about to be shown should apply, if any.</summary>
    public static DeepLink? PendingLink { get; set; }

    /// <summary>Taken by the page that consumes it, so it applies once and not again.</summary>
    public static DeepLink? TakePendingLink()
    {
        var link = PendingLink;
        PendingLink = null;
        return link;
    }

    /// <summary>The cached per-device page, if it has been created yet.</summary>
    public DevicePage? DevicePage => _pages.TryGetValue("device", out var p) ? p as DevicePage : null;

    private Page GetOrCreatePage(string tag)
    {
        // Settings is rebuilt each visit so it always reflects the registry.
        if (tag == "Settings") return new SettingsPage();
        if (_pages.TryGetValue(tag, out var cached)) return cached;

        Page page;
        if (tag.StartsWith("usage:", StringComparison.Ordinal))
        {
            // usage:<days>:<app> -- the app name can contain a colon, so only the two
            // leading fields are split off.
            var rest = tag["usage:".Length..];
            var split = rest.IndexOf(':');
            var days = split > 0 && int.TryParse(rest[..split], out var d) ? d : 30;
            page = new AppUsagePage(split > 0 ? rest[(split + 1)..] : rest, days);
        }
        else if (tag == "management-logs") page = new ManagementLogsPage();
        else if (tag == "coverage") page = new CoveragePage();
        else if (tag == "failures") page = new FailuresPage();
        else if (tag == "report:applications")
        {
            // Rebuilt for the platform toggle with no link of its own, the report keeps
            // the applications and filters it was showing rather than starting over.
            var carried = PendingLink is null && _replaced.TryGetValue(tag, out var old) && old is ApplicationsReportPage shown
                ? shown.CurrentLink() : null;
            page = new ApplicationsReportPage(carried);
        }
        else if (tag.StartsWith("report:", StringComparison.Ordinal))
        {
            var area = ReportArea.ById(tag["report:".Length..]);
            page = area is null ? new ReportsPage() : new ReportPage(area);
        }
        else
        {
            page = tag switch
            {
                "devices" => new DevicesPage(),
                "events" => new EventsPage(),
                "reports" => new ReportsPage(),
                "device" => new DevicePage(),
                _ => new DashboardPage(),
            };
        }

        // A rebuilt page opens on what its predecessor showed and refreshes over it.
        if (_replaced.Remove(tag, out var previous) && page is FleetPage fresh && previous is FleetPage stale)
            fresh.Adopt(stale);

        _pages[tag] = page;
        return page;
    }

    // ── Platform scope ──────────────────────────────────────────────────

    private void OnPlatformToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        PlatformFilter.Toggle(tag == "Mac" ? PlatformScope.Mac : PlatformScope.Windows);
    }

    private void SyncPlatformToggle()
    {
        PlatformMac.IsChecked = PlatformFilter.Current == PlatformScope.Mac;
        PlatformWindows.IsChecked = PlatformFilter.Current == PlatformScope.Windows;
    }

    // ── Search ──────────────────────────────────────────────────────────

    /// <summary>
    /// The device list, fetched once and reused for every keystroke. Searching is a
    /// per-character operation and the list is the same list the Devices page just
    /// loaded, so asking the API again on each letter would be both slow and rude.
    /// </summary>
    private List<FleetDevice>? _searchable;

    /// <summary>The device list for search, loaded on first use; null when the API cannot serve it.</summary>
    private async Task<List<FleetDevice>?> SearchableDevicesAsync()
    {
        if (_searchable is null)
        {
            var result = await FleetApiClient.Instance.GetDevicesAsync();
            if (!result.Ok) return null;
            _searchable = result.Data!.Devices;
        }
        return _searchable;
    }

    private async void OnSearchTextChanged(
        ModernWpf.Controls.AutoSuggestBox sender, ModernWpf.Controls.AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only a typed change should search; setting the text in code should not.
        if (args.Reason != ModernWpf.Controls.AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = sender.Text?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2)
        {
            sender.ItemsSource = null;
            return;
        }

        if (await SearchableDevicesAsync() is not { } devices) return;

        // The scope applies here too: searching while filtered to Windows should not
        // offer a Mac.
        sender.ItemsSource = devices
            .Where(d => PlatformFilter.Includes(d.Platform ?? d.OsName))
            .Select(d => DeviceHit.For(d, query))
            .Where(h => h is not null)
            .Take(8)
            .ToList();
    }

    private void OnSearchSuggestionChosen(
        ModernWpf.Controls.AutoSuggestBox sender, ModernWpf.Controls.AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is DeviceHit hit) OpenDevice(hit.Serial);
    }

    private void OnSearchSubmitted(
        ModernWpf.Controls.AutoSuggestBox sender, ModernWpf.Controls.AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter on a highlighted suggestion opens it; Enter on free text opens the
        // single match if there is exactly one, and otherwise leaves the list up
        // rather than guessing which device was meant.
        if (args.ChosenSuggestion is DeviceHit chosen) { OpenDevice(chosen.Serial); return; }
        if (sender.ItemsSource is List<DeviceHit> { Count: 1 } only) OpenDevice(only[0].Serial);
    }

    /// <summary>
    /// Show the chosen device. The device page renders this machine's own report
    /// from the local cache and cannot render another machine's, so a search result
    /// opens the Devices list narrowed to that serial rather than a page that would
    /// show the wrong device's data under the right device's name.
    /// </summary>
    private void OpenDevice(string serial)
    {
        SearchBox.Text = "";
        SearchBox.ItemsSource = null;
        PendingLink = DeepLink.For("devices", null, ("search", serial));
        Navigate("devices", force: true);
        SyncChecked();
    }

    // ── Host search ─────────────────────────────────────────────────────

    private string _deviceSearch = "";

    /// <summary>
    /// What the Devices list is filtered by on the host's behalf: the host's query,
    /// or the serial <see cref="OpenBestDeviceMatchAsync"/> opened. Null until the
    /// host has typed anything, so a link's own filter applies until then.
    /// </summary>
    private string? _hostDeviceFilter;

    internal string? HostDeviceFilter => _hostDeviceFilter;

    /// <summary>
    /// The device query from the host's own search field, for a host that hides the
    /// dashboard's (<see cref="DashboardChrome.ShowsSearchField"/> false). Any text
    /// brings the Devices list forward, filtered by it, as the dashboard's own search
    /// lands there; an empty string clears the filter and leaves the page where it is.
    /// </summary>
    public string DeviceSearch
    {
        get => _deviceSearch;
        set
        {
            value ??= "";
            if (value == _deviceSearch) return;
            _deviceSearch = value;
            ShowDeviceFilter(value, bringForward: value.Trim().Length > 0);
        }
    }

    /// <summary>
    /// Open the device that best matches <see cref="DeviceSearch"/>, for the host's
    /// Return key: the Devices list narrowed to it, as choosing a search result does.
    /// False, leaving the filtered list on screen, when nothing matches.
    /// </summary>
    public async Task<bool> OpenBestDeviceMatchAsync()
    {
        var query = _deviceSearch.Trim();
        if (query.Length == 0 || await SearchableDevicesAsync() is not { } devices) return false;

        var match = DeviceMatch.Best(
            devices.Where(d => PlatformFilter.Includes(d.Platform ?? d.OsName)),
            query,
            d => [d.Name, d.SerialNumber, d.AssetTag, d.Hostname]);
        if (match is null) return false;

        ShowDeviceFilter(match.SerialNumber, bringForward: true);
        return true;
    }

    private void ShowDeviceFilter(string filter, bool bringForward)
    {
        _hostDeviceFilter = filter;
        if (_pages.TryGetValue("devices", out var page) && page is DevicesPage devices)
            devices.ApplyHostSearch(filter);
        if (bringForward && _current != "devices") NavigateTo("devices");
    }
}

/// <summary>
/// Which of its own controls an embedded dashboard draws in its header. The
/// ReportMate app's window always uses <see cref="Standard"/>.
/// </summary>
/// <param name="ShowsSearchField">
/// The header's device search, and the Devices list's own filter box. Off when the
/// host window already has a search field and routes its text in through
/// <see cref="DashboardView.DeviceSearch"/>, so the window shows one search field.
/// </param>
/// <param name="ShowsBrand">
/// The ReportMate mark at the header's leading edge. Off when the host already
/// names what it is showing, which leaves the row's width to the section tabs.
/// </param>
public sealed record DashboardChrome(bool ShowsSearchField = true, bool ShowsBrand = true)
{
    /// <summary>The full header: mark, platform toggle, search, tabs, settings.</summary>
    public static DashboardChrome Standard { get; } = new();

    /// <summary>
    /// For a host that supplies search and names the view itself: one row of the
    /// platform toggle, the section tabs and settings, with no search field or mark.
    /// </summary>
    public static DashboardChrome HostProvided { get; } = new(ShowsSearchField: false, ShowsBrand: false);
}

/// <summary>
/// One search result, with the matched run of text kept apart from the rest so the
/// template can weight it without a converter picking the string back apart.
/// </summary>
public sealed record DeviceHit(string Serial, string Before, string Match, string After, string Detail)
{
    public static DeviceHit? For(FleetDevice device, string query)
    {
        // Name first, then the identifiers, so a device matched by name shows its
        // name highlighted rather than an incidental hit in its serial.
        foreach (var field in new[] { device.Name, device.SerialNumber, device.AssetTag, device.Hostname })
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            var at = field.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var detail = ReferenceEquals(field, device.Name)
                ? device.SerialNumber
                : $"{device.Name} · {device.SerialNumber}";
            return new DeviceHit(device.SerialNumber,
                field[..at], field.Substring(at, query.Length), field[(at + query.Length)..], detail);
        }
        return null;
    }
}
