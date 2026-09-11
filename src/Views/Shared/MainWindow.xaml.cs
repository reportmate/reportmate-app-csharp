using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using ReportMate.App.Views.Device;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;

namespace ReportMate.App.Views.Shared;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();

    /// <summary>
    /// Below this width the nine reports collapse behind a single Reports tab; above
    /// it each report gets its own tab, which is how the web header behaves.
    /// </summary>
    private const double ReportsInlineWidth = 1500;

    private bool _reportsInline;
    private string _current = "dashboard";

    public MainWindow()
    {
        InitializeComponent();

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
        ProtocolHandler.LinkReceived += OpenDeepLink;
        SizeChanged += (_, _) => BuildTabs();
        BuildTabs();
        Navigate("dashboard");
    }

    /// <summary>
    /// The primary sections, in the same order as the web app and the Mac client.
    /// Reports either sit inline as their own tabs or collapse into one tab,
    /// depending on how much room the window has.
    /// </summary>
    private void BuildTabs()
    {
        var inline = ActualWidth >= ReportsInlineWidth;
        if (TabBar.Children.Count > 0 && inline == _reportsInline) { SyncChecked(); return; }
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
        foreach (var child in TabBar.Children)
            if (child is RadioButton rb && rb.Tag?.ToString() == _current) { rb.IsChecked = true; return; }

        // The current page has no tab at this width (a report while collapsed):
        // show Reports as the active section instead of leaving nothing checked.
        if (_current.StartsWith("report:", StringComparison.Ordinal))
            foreach (var child in TabBar.Children)
                if (child is RadioButton rb && rb.Tag?.ToString() == "reports") { rb.IsChecked = true; return; }
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (ContentFrame is null) return;
        if (sender is RadioButton { Tag: string tag }) Navigate(tag);
    }

    private void Navigate(string tag, bool force = false)
    {
        // Pages are cached, so a change to what they should be showing has to empty
        // the cache or the change is invisible until the app restarts.
        if (force) _pages.Clear();
        _current = tag;
        ContentFrame.Navigate(GetOrCreatePage(tag));
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
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

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
                    NavigateTo("coverage");
                    return;
                }
                if (argument.StartsWith("usage/", StringComparison.OrdinalIgnoreCase))
                {
                    var days = int.TryParse(link["days"], out var d) ? d : 30;
                    NavigateTo($"usage:{days}:{argument["usage/".Length..]}");
                    return;
                }
                PendingLink = link;
                NavigateTo("report:applications");
                return;

            // events/failures is a page of its own on the web, the same shape as
            // applications/coverage, so a link to it opens that page rather than the
            // events list.
            case "events" when link.Argument is { } events
                && events.Equals("failures", StringComparison.OrdinalIgnoreCase):
                NavigateTo("failures");
                return;

            case "dashboard" or "devices" or "events":
                PendingLink = link;
                NavigateTo(link.Section);
                return;

            default:
                // Every remaining section is a report, and they share their names with
                // the web routes, so the section is the report id.
                PendingLink = link;
                NavigateTo("report:" + link.Section);
                return;
        }
    }

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
        else if (tag == "coverage") page = new CoveragePage();
        else if (tag == "failures") page = new FailuresPage();
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

        if (_searchable is null)
        {
            var result = await FleetApiClient.Instance.GetDevicesAsync();
            if (!result.Ok) return;
            _searchable = result.Data!.Devices;
        }

        // The scope applies here too: searching while filtered to Windows should not
        // offer a Mac.
        sender.ItemsSource = _searchable
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
