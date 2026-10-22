using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's System tab, laid out as the web's SystemTab.tsx and the Mac app's
/// SystemTabView lay out a Mac: OS tiles, macOS updates, install history, the
/// statistics strip, Background Activity, extensions beside Open at Login, kernel
/// extensions, privileged helpers and the environment.
/// </summary>
public static class MacSystemView
{
    private const string Glyph = "";

    private sealed record UpdateRow(string Name, string Detail, string Version, string Status, Tone StatusTone, string Note, string Installed);
    private sealed record HistoryRow(string Installed, DateTime Sort, string Package, string File, string Version);
    private sealed record LaunchdRow(string Name, string Location, string Kind, Tone KindTone, string Source, string Status, Tone StatusTone,
        string RunAtLoad, Tone RunAtLoadTone, string KeepAlive, Tone KeepAliveTone, string Scope, string Type, string StatusKey, MacLaunchdItem Item);
    private sealed record LoginRow(string Name, string Path, string Kind);
    private sealed record KextRow(string Name, string Path, string Version, string Size, string Status, Tone StatusTone);
    private sealed record HelperRow(string Name, string Path, string TeamId, string Signed, Tone SignedTone, string BundleId);

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("system", out var raw)) return null;
        if (MacSystemInfo.From(raw) is not { } sys) return null;
        var os = sys.OperatingSystem;

        var page = new StackPanel();
        void Add(UIElement e, double top = 24) { if (e is FrameworkElement fe && page.Children.Count > 0) fe.Margin = new Thickness(0, top, 0, 0); page.Children.Add(e); }

        var pending = sys.PendingUpdates.Count;
        var badge = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        badge.Children.Add(Ui.Caption("Software Update"));
        badge.Children.Add(pending > 0 ? Ui.Pill(Format.Plural(pending, "Pending Update"), Tone.Warning) : Ui.Pill("Up to Date", Tone.Success));
        Add(Ui.TabHeader("System Information", "Operating system and apps access", Glyph, Accent.Purple, badge), 0);

        Add(Ui.TileRow(12,
            Ui.Tile("macOS", os.MarketingName),
            Ui.Tile("Version", os.FormattedVersion, mono: true),
            Ui.Tile("Build", Format.OrUnknown(os.Build), mono: true)), 0);
        Add(Ui.TileRow(12,
            Ui.Tile("System Uptime", Format.OrUnknown(sys.Uptime)),
            Ui.Tile("Last Boot", sys.BootTime is { } boot ? $"{Format.ExactTime(boot)} ({Format.RelativeTime(boot)})" : "Unknown"),
            Ui.Tile("System Locale", Format.OrUnknown(os.Locale)),
            Ui.Tile("Keyboard Layout", Format.OrDash(os.KeyboardLayout)),
            Ui.Tile("Time Zone", Format.OrUnknown(os.TimeZone))), 12);

        Add(Updates(sys));

        if (sys.InstallHistory.Count > 0)
        {
            var rows = sys.InstallHistoryNewestFirst.Select(i => new HistoryRow(
                i.InstallDate is { } d ? Format.ExactTime(d) : "Unknown", i.InstallDate ?? DateTime.MinValue,
                i.PackageId, i.PackageFilename ?? "", i.Version ?? "Unknown")).ToList();
            Add(Table.Card("Install History", $"Packages installed in the last 90 days ({rows.Count} packages)", rows, null, "",
                Col.Text("Install Date", "Installed", 180, sortBy: "Sort"),
                Col.Text("Package", "Package", star: true, mono: true, sub: "File"),
                Col.Text("Version", "Version", 160)));
        }

        Add(Ui.Columns(4, 12,
            Ui.Metric(sys.BackgroundItemCount.ToString(), "Background Items"),
            Ui.Metric(sys.RunningServiceCount.ToString(), "Running", Tone.Success),
            Ui.Metric(sys.Extensions.Count.ToString(), "Extensions"),
            Ui.Metric(sys.LoginItems.Count.ToString(), "Open at Login")));

        if (sys.LaunchdItems.Count > 0) Add(BackgroundActivity(sys.LaunchdItems));

        if (sys.Extensions.Count > 0 || sys.LoginItems.Count > 0)
        {
            UIElement? extensions = sys.Extensions.Count > 0 ? new ExtensionsCard(sys.Extensions) : null;
            UIElement? login = sys.LoginItems.Count > 0 ? OpenAtLogin(sys.LoginItems) : null;
            Add(extensions is not null && login is not null
                ? Ui.Columns([3, 2], 24, extensions, login)
                : (extensions ?? login)!);
        }

        if (sys.KernelExtensions.Count > 0)
        {
            var rows = sys.KernelExtensions.Select(k => new KextRow(k.Name, k.Path ?? "", k.Version ?? "Unknown", k.SizeText,
                k.Loaded ? "Loaded" : "Not Loaded", k.Loaded ? Tone.Success : Tone.Neutral)).ToList();
            Add(Table.Card("Kernel Extensions", $"Third-party kernel extensions (kexts) - {rows.Count} loaded", rows, null, "",
                Col.Text("Name", "Name", star: true, mono: true, sub: "Path"),
                Col.Text("Version", "Version", 140),
                Col.Text("Size", "Size", 110),
                Col.Pill("Status", "Status", "StatusTone", 120)));
        }

        if (sys.HelperTools.Count > 0)
        {
            var rows = sys.HelperTools.Select(h => new HelperRow(h.Name, h.Path ?? "", h.TeamId ?? "Unknown",
                h.Signed ? "Signed" : "Unsigned", h.Signed ? Tone.Success : Tone.Warning, h.BundleIdentifier ?? "Unknown")).ToList();
            Add(new FilteredTable<HelperRow>("Privileged Helper Tools", "Background services installed by applications ({0} of {1} tools)", rows,
                    (r, q) => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.BundleId.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.TeamId.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(q, StringComparison.OrdinalIgnoreCase),
                    [
                        Col.Text("Name", "Name", star: true, sub: "Path"),
                        Col.Text("Team ID", "TeamId", 140, mono: true),
                        Col.Pill("Signed", "Signed", "SignedTone", 100),
                        Col.Text("Bundle ID", "BundleId", 280, mono: true, secondary: true),
                    ], "Search helpers...", "No helper tools match the search")
                .Build());
        }

        if (sys.Environment.Count > 0) Add(Environment(sys.Environment));

        return page;
    }

    /// <summary>
    /// Pending Apple updates, then the last ten Apple receipts, in one table. The
    /// card stays when both are empty, its subtitle alone saying there is nothing
    /// to install, as the web leaves it.
    /// </summary>
    private static UIElement Updates(MacSystemInfo sys)
    {
        var pending = sys.PendingUpdates;
        var subtitle = pending.Count > 0
            ? $"{pending.Count} available system update{(pending.Count == 1 ? "" : "s")} from Apple"
            : "System software and security updates";
        var rows = pending.Select(u => new UpdateRow(u.Name, u.Detail ?? "", u.Version ?? "Unknown", u.StatusLabel,
                u.Deferred ? Tone.Orange : u.RestartRequired ? Tone.Warning : Tone.Info, u.StatusNote ?? "", ""))
            .Concat(sys.RecentSystemUpdates.Select(i => new UpdateRow(i.PackageId, "", i.Version ?? "Unknown", "", Tone.Neutral, "",
                i.InstallDate is { } d ? Format.RelativeTime(d) : "Unknown")))
            .ToList();
        if (rows.Count == 0) return Ui.TableCard("macOS Updates", subtitle, new Border());
        return Table.Card("macOS Updates", subtitle, rows, null, "",
            Col.Text("Update", "Name", star: true, sub: "Detail"),
            Col.Text("Version", "Version", 140),
            Col.Pill("Status", "Status", "StatusTone", 140),
            Col.Text("", "Note", 150, secondary: true),
            Col.Text("Installed", "Installed", 130, secondary: true));
    }

    /// <summary>
    /// launchd daemons and agents, opening on third-party system items as the web
    /// and Mac apps do; Apple's own jobs outnumber everything else ten to one.
    /// </summary>
    private static UIElement BackgroundActivity(IReadOnlyList<MacLaunchdItem> items)
    {
        var rows = items.Select(i => new LaunchdRow(
            i.DisplayName, i.Location,
            i.Kind == LaunchdKind.Daemon ? "Daemon" : "Agent", i.Kind == LaunchdKind.Daemon ? Tone.Purple : Tone.Info,
            SourceLabel(i),
            i.LaunchdStatus.ToString(), i.LaunchdStatus switch { LaunchdStatus.Running => Tone.Success, LaunchdStatus.Stopped => Tone.Warning, _ => Tone.Neutral },
            Format.YesNo(i.RunAtLoad), i.RunAtLoad ? Tone.Success : Tone.Neutral,
            Format.YesNo(i.KeepAlive), i.KeepAlive ? Tone.Success : Tone.Neutral,
            i.Scope.ToString().ToLowerInvariant(), i.Kind.ToString().ToLowerInvariant(), i.LaunchdStatus.ToString().ToLowerInvariant(), i)).ToList();
        int Count(Func<LaunchdRow, bool> p) => rows.Count(p);
        return new FilteredTable<LaunchdRow>("Background Activity", "Launch daemons and agents ({0} of {1} items)", rows,
                (r, q) => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Location.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (r.Item.Program ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || r.Item.Status.Contains(q, StringComparison.OrdinalIgnoreCase),
                [
                    Col.Text("Label / Name", "Name", star: true, sub: "Location"),
                    Col.Pill("Type", "Kind", "KindTone", 100),
                    Col.Text("Source", "Source", 150, secondary: true),
                    Col.Pill("Status", "Status", "StatusTone", 100),
                    Col.Pill("Run at Load", "RunAtLoad", "RunAtLoadTone", 100),
                    Col.Pill("Keep Alive", "KeepAlive", "KeepAliveTone", 100),
                ], "Search by name or path...", "No items match the current filters")
            .Filter([
                    new("all", "All Sources", rows.Count),
                    new("system", "Third-Party (System)", Count(r => r.Scope == "system")),
                    new("user", "User Level", Count(r => r.Scope == "user")),
                    new("apple", "Apple", Count(r => r.Scope == "apple")),
                ], (r, k) => r.Scope == k, initial: "system")
            .Filter([new("all", "All", rows.Count), new("daemon", "Daemons", Count(r => r.Type == "daemon")), new("agent", "Agents", Count(r => r.Type == "agent"))],
                (r, k) => r.Type == k)
            .Filter([
                    new("all", "All", rows.Count),
                    new("running", "Running", Count(r => r.StatusKey == "running")),
                    new("stopped", "Stopped", Count(r => r.StatusKey == "stopped")),
                    new("disabled", "Disabled", Count(r => r.StatusKey == "disabled")),
                ], (r, k) => r.StatusKey == k)
            .WithDetails(r => PlistDetails(r.Item))
            .Build();
    }

    private static string SourceLabel(MacLaunchdItem i)
    {
        var scope = i.Scope switch { LaunchdScope.Apple => "Apple", LaunchdScope.User => "User", _ => "Third-party" };
        return i.ManagedByProfile ? $"{scope} · Managed" : scope;
    }

    private static UIElement PlistDetails(MacLaunchdItem item)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 8, 20, 12) };
        if (item.PlistPretty is { } plist)
        {
            panel.Children.Add(Ui.Label("Plist Configuration"));
            var text = Ui.Mono(plist);
            text.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(new ScrollViewer { MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = text });
        }
        else
        {
            panel.Children.Add(Ui.Caption("The client sent no plist for this item."));
        }
        if (item.ManagedByProfile) panel.Children.Add(Ui.Caption($"Managed by {item.ProfileIdentifier ?? "an MDM profile"}"));
        return panel;
    }

    private static UIElement OpenAtLogin(IReadOnlyList<MacLoginItem> items)
    {
        var rows = items.Select(i => new LoginRow(i.Name, i.Path ?? "", string.IsNullOrWhiteSpace(i.Type) ? "Application" : i.Type)).ToList();
        return Table.Card("Open at Login", $"Applications that open automatically when you log in ({rows.Count} items)", rows, null, "",
            Col.Text("Item", "Name", star: true),
            Col.Text("Kind", "Kind", 110, secondary: true));
    }

    private static UIElement Environment(IReadOnlyList<MacEnvironmentVariable> env)
    {
        var body = new StackPanel();
        foreach (var e in env)
        {
            var row = new Grid { Margin = new Thickness(20, 8, 20, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var n = Ui.Mono(e.Name);
            n.FontWeight = FontWeights.Medium;
            n.Margin = new Thickness(0);
            row.Children.Add(n);
            UIElement value;
            if (e.IsPath || e.IsHostsFile)
            {
                var pre = Ui.Mono(e.IsPath ? string.Join("\n", e.PathEntries) : e.Value);
                pre.Margin = new Thickness(0, 6, 0, 0);
                value = Ui.Collapsible(e.IsPath ? $"{e.PathEntries.Count} paths" : "hosts file", pre, open: true);
            }
            else
            {
                var v = Ui.Mono(e.Value);
                v.Margin = new Thickness(0);
                v.TextWrapping = TextWrapping.Wrap;
                value = v;
            }
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            body.Children.Add(new Border { BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
        }
        return Ui.TableCard("Environment Variables", "System environment variables", body);
    }

    /// <summary>
    /// Extensions grouped as System Settings groups them, by category or by app,
    /// each group a collapsible list. Searching, or filtering to the ones waiting
    /// for the user, opens every group, since the point is to see what matched.
    /// </summary>
    private sealed class ExtensionsCard : ContentControl
    {
        private readonly IReadOnlyList<MacExtension> _all;
        private readonly StackPanel _body = new();
        private bool _byCategory = true;
        private string _status = "all";
        private string _query = "";

        public ExtensionsCard(IReadOnlyList<MacExtension> extensions)
        {
            _all = extensions;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            int Count(ExtensionStatus st) => _all.Count(e => e.NormalizedState.Status == st);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 12) };
            controls.Children.Add(Ui.Segmented([("category", "By Category"), ("app", "By App")], "category", k => { _byCategory = k == "category"; Refresh(); }));
            List<FilterOption> options = [new("all", "All", _all.Count)];
            if (Count(ExtensionStatus.Enabled) > 0) options.Add(new("enabled", "Enabled", Count(ExtensionStatus.Enabled)));
            if (Count(ExtensionStatus.Waiting) > 0) options.Add(new("waiting", "Waiting for User Action", Count(ExtensionStatus.Waiting)));
            if (Count(ExtensionStatus.Disabled) > 0) options.Add(new("disabled", "Disabled", Count(ExtensionStatus.Disabled)));
            var pills = new FilterPills(options, "all") { Margin = new Thickness(8, 0, 0, 0) };
            pills.Changed += k => { _status = k; Refresh(); };
            controls.Children.Add(pills);
            controls.Children.Add(new SearchBox("Search...", q => { _query = q.Trim(); Refresh(); }));

            var root = new StackPanel();
            root.Children.Add(controls);
            root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
            root.Children.Add(new ScrollViewer { MaxHeight = 800, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
            Content = Ui.TableCard("Extensions", "Extensions add extra functionality to your Mac and apps, and some may run in the background.", root);
            Refresh();
        }

        private void Refresh()
        {
            _body.Children.Clear();
            var shown = _status switch
            {
                "enabled" => _all.Where(e => e.NormalizedState.Status == ExtensionStatus.Enabled),
                "waiting" => _all.Where(e => e.NormalizedState.Status == ExtensionStatus.Waiting),
                "disabled" => _all.Where(e => e.NormalizedState.Status == ExtensionStatus.Disabled),
                _ => _all,
            };
            var groups = _byCategory
                ? MacExtension.Categories.Select(c => (Key: c, Items: shown.Where(e => e.ResolvedCategory == c).ToList()))
                    .Concat(shown.GroupBy(e => e.ResolvedCategory).Where(g => !MacExtension.Categories.Contains(g.Key))
                        .Select(g => (Key: g.Key, Items: g.ToList())))
                : shown.GroupBy(e => e.ResolvedAppName).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (Key: g.Key, Items: g.ToList()));
            var open = _query.Length > 0 || _status == "waiting";
            foreach (var (key, items) in groups)
            {
                var kept = _query.Length == 0 ? items : items.Where(e =>
                    e.Identifier.Contains(_query, StringComparison.OrdinalIgnoreCase) || key.Contains(_query, StringComparison.OrdinalIgnoreCase)
                    || (_byCategory ? e.ResolvedAppName : e.ResolvedCategory).Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0) continue;
                var list = Ui.List(kept.Select(e =>
                {
                    var (label, status) = e.NormalizedState;
                    var tone = status switch { ExtensionStatus.Enabled => Tone.Success, ExtensionStatus.Waiting => Tone.Warning, _ => Tone.Neutral };
                    var right = Ui.Pill(label, tone);
                    return (UIElement)Ui.ListItem(_byCategory ? e.ResolvedAppName : e.ResolvedCategory, e.Identifier,
                        e.ManagedByProfile ? "Managed" : null, Tone.Purple, right: right);
                }));
                var title = _byCategory
                    ? $"{key}  —  {Summary(kept)}"
                    : $"{key}  —  {Format.Plural(kept.Count, "extension")} • {string.Join(", ", kept.Select(e => e.ResolvedCategory).Distinct())}";
                var section = Ui.Collapsible(title, list, open);
                section.Margin = new Thickness(20, 6, 20, 6);
                _body.Children.Add(section);
            }
            if (_body.Children.Count == 0)
                _body.Children.Add(Ui.EmptyState(_query.Length > 0 ? $"No extensions found matching “{_query}”" : "No extensions match the current filter"));
        }

        /// <summary>The first three apps and how many more, as System Settings summarises a category.</summary>
        private static string Summary(List<MacExtension> items)
        {
            var apps = items.Select(e => e.ResolvedAppName).Distinct().Take(3);
            return string.Join(", ", apps) + (items.Count > 3 ? $" and {items.Count - 3} more." : "");
        }
    }
}
