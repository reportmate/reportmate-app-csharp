using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Installs tab (Munki), laid out as the Mac app's InstallsTabView and the
/// web's InstallsTab: configuration card, last run log on request, the last run's
/// system-level problems, and the managed items table with status filters, category
/// groups and expandable error, warning and pending rows. The look follows the Windows
/// Installs tab so a Mac and a Windows device read the same.
/// </summary>
public static class MacInstallsView
{
    private const string Glyph = "";

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("installs", out var raw)) return null;
        var info = MacInstallsInfo.Read(raw);
        var page = new StackPanel();
        void Add(UIElement e, double top = 24) { if (e is FrameworkElement fe && page.Children.Count > 0) fe.Margin = new Thickness(0, top, 0, 0); page.Children.Add(e); }

        var lastRun = info.LastRunTimestamp;
        UIElement? right = null;
        if (lastRun is not null)
        {
            var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            p.Children.Add(Ui.Caption("Last Run"));
            var v = Ui.Text(CompactRelative(lastRun.Value));
            v.FontSize = 16; v.FontWeight = FontWeights.SemiBold; v.TextAlignment = TextAlignment.Right;
            p.Children.Add(v);
            right = p;
        }
        Add(Ui.TabHeader("Managed Installs", "Software deployment report", Glyph, Accent.Emerald, right), 0);

        if (!info.HasData)
        {
            Add(Ui.Card(Ui.EmptyState("No install data available. The installs module has not reported for this device.")));
            return page;
        }

        // Configuration card
        var c = info.Config;
        var configGrid = Ui.Columns(3, 16,
            Ui.VStack(Ui.Stat("Manifest", NonEmpty(c?.Manifest) ?? "No manifest configured", mono: true, copy: true),
                Ui.Stat("Repo", NonEmpty(c?.SoftwareRepoUrl) ?? "No repo configured", mono: true, copy: true, truncate: true)),
            Ui.VStack(Ui.Row("Run Type", Ui.Pill(Format.Capitalize(c?.RunType ?? "Auto"), Tone.Neutral)),
                Ui.Stat($"{NonEmpty(info.SystemName) ?? "Munki"} Version", NonEmpty(c?.Version) ?? "Unknown")),
            Ui.VStack(Ui.Stat("Catalog", c?.Catalogs ?? "Not configured"),
                Ui.Stat("Last Seen", lastRun is null ? "Never" : lastRun.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    sublabel: c?.DurationSeconds is > 0 ? $"Duration: {Format.Duration(c.DurationSeconds.Value)}" : null)));
        Add(Ui.Card(configGrid, new Thickness(20, 16, 20, 18)));

        // Run log. The API leaves it out of the device payload, since it can run to
        // megabytes, so it is read on request; a payload that does carry it shows it.
        if (MacJson.Str(raw, "runLog") is { } inlineLog)
            Add(RunLogSection(inlineLog));
        else if (s.FetchRunLog is { } fetchRunLog)
        {
            var logHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var load = Ui.IconButton("", "Show the run log", async (_, _) =>
            {
                logHost.Content = Skeleton.Capped(Skeleton.Pulse(Skeleton.Rows(6)), () => { });
                var log = await fetchRunLog(CancellationToken.None);
                logHost.Content = string.IsNullOrWhiteSpace(log)
                    ? Ui.Card(Ui.EmptyState("This device has not reported a run log."))
                    : RunLogSection(log);
            });
            load.HorizontalAlignment = HorizontalAlignment.Left;
            logHost.Content = load;
            Add(logHost);
        }

        // The last run's system-level problems
        if (!info.SystemProblems.IsEmpty) Add(ProblemsBanner(info.SystemProblems));

        // Managed items
        if (info.HasManagementSystem || info.Packages.Count > 0)
        {
            var state = new TableState();
            var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            void Render() => host.Content = PackagesCard(info, state, lastRun, Render);
            Render();
            Add(host);
        }
        else
            Add(Ui.Card(Ui.EmptyState("No Managed Installs. This device does not have managed software installations configured.")));
        return page;
    }

    /// <summary>The table's filters, collapsed groups and open rows, kept across re-renders.</summary>
    private sealed class TableState
    {
        public HashSet<string> Filter { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Collapsed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Expanded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Query { get; set; } = "";
    }

    private static Border ProblemsBanner(MacSystemProblems summary)
    {
        var isError = summary.IsError;
        var tone = isError ? Tone.Error : Tone.Warning;
        var banner = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Pill(isError ? (summary.FailedWithoutItems ? "Last run failed" : "Last run errors") : "Last run warnings", tone));
        if (summary.FailedWithoutItems)
        {
            var note = Ui.Caption("The run did not complete, so no items were reported.");
            note.Margin = new Thickness(10, 0, 0, 0); note.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(note);
        }
        var stampText = string.Join(" · ", new[] { summary.SessionId, summary.Time is null ? null : Format.RelativeTime(summary.Time) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (stampText.Length > 0)
        {
            var stamp = Ui.Caption(stampText);
            stamp.Margin = new Thickness(10, 0, 0, 0); stamp.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(stamp);
        }
        banner.Children.Add(head);
        foreach (var p in summary.Problems)
        {
            var line = Ui.Mono(p.Message);
            line.FontSize = 11.5; line.TextWrapping = TextWrapping.Wrap; line.Foreground = Ui.StatusBrush(p.IsError ? Tone.Error : Tone.Warning);
            banner.Children.Add(new Border { Background = Ui.Brush("SubtleFillBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0), Child = line });
        }
        return new Border { Background = Ui.Brush(isError ? "PillRedBackground" : "PillYellowBackground"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Child = banner };
    }

    // ── Packages card ────────────────────────────────────────────────

    private static UIElement PackagesCard(MacInstallsInfo info, TableState state, DateTime? lastRun, Action rerender)
    {
        var all = info.Packages;
        var filter = state.Filter;
        var lastRunActive = filter.Contains("last_run");
        var statusOnly = filter.Where(f => f != "last_run").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = state.Query.Trim();

        IEnumerable<MacInstallPackage> filtered = all;
        if (lastRunActive) filtered = filtered.Where(p => p.InLastRun);
        if (statusOnly.Count > 0) filtered = filtered.Where(p => statusOnly.Contains(p.Status.ToString()));
        if (query.Length > 0) filtered = filtered.Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        var shown = filtered.ToList();
        var showSize = all.Any(p => p.ItemSizeBytes > 0);

        var root = new StackPanel();
        var header = new StackPanel { Margin = new Thickness(20, 14, 20, 12) };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Ui.Text("Managed Items:", "TitleTextStyle"));
        var count = Ui.Text(info.TotalPackages.ToString(), "TitleTextStyle"); count.Margin = new Thickness(6, 0, 0, 0); count.Foreground = Ui.Brush("TextSecondaryBrush");
        title.Children.Add(count);
        if (info.CacheSizeMb is > 0) { var cache = Ui.Caption($"Cache: {info.CacheSizeMb:F1} MB"); cache.Margin = new Thickness(14, 0, 0, 0); cache.VerticalAlignment = VerticalAlignment.Center; title.Children.Add(cache); }
        if (filter.Count > 0)
        {
            var clear = new Button { Content = "Clear Filters", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(14, 0, 0, 0), FontSize = 11.5 };
            clear.Click += (_, _) => { filter.Clear(); state.Expanded.Clear(); rerender(); };
            title.Children.Add(clear);
        }
        var categories = shown.Select(p => p.Category.Trim()).Where(c => c.Length > 0).Distinct().ToList();
        if (categories.Count > 0 && !lastRunActive)
        {
            var toggle = new Button { Content = state.Collapsed.Count == 0 ? "Collapse All" : "Expand All", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), FontSize = 11.5 };
            toggle.Click += (_, _) =>
            {
                if (state.Collapsed.Count == 0) { foreach (var c in categories) state.Collapsed.Add(c); if (shown.Any(p => p.Category.Trim().Length == 0)) state.Collapsed.Add("Uncategorized"); }
                else state.Collapsed.Clear();
                rerender();
            };
            title.Children.Add(toggle);
        }
        titleRow.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void FilterPill(string key, string label, int n, Tone tone)
        {
            var pill = Ui.FilterChip(label, n, tone, filter.Contains(key));
            pill.MouseLeftButtonUp += (_, _) =>
            {
                if (!filter.Remove(key))
                {
                    filter.Add(key);
                    if (key == "last_run") filter.RemoveWhere(f => f != "last_run");
                    else filter.Remove("last_run");
                }
                // A status filter narrows to the rows that need attention; open their detail.
                state.Expanded.Clear();
                if (filter.Count > 0)
                    foreach (var p in all.Where(p => p.HasExpandableContent && (filter.Contains(p.Status.ToString()) || (filter.Contains("last_run") && p.InLastRun))))
                        state.Expanded.Add(p.Id);
                rerender();
            };
            controls.Children.Add(pill);
        }
        if (info.LastRunCount > 0 && statusOnly.Count == 0) FilterPill("last_run", "Last Run", info.LastRunCount, Tone.Neutral);
        foreach (var st in Enum.GetValues<MacInstallState>())
            FilterPill(st.ToString(), st.ToString(), info.Count(st, lastRunActive), StatusTone(st));
        var search = new SearchBox("Search items...", q => { state.Query = q; rerender(); }) { Text = state.Query };
        controls.Children.Add(search);
        Grid.SetColumn(controls, 1);
        titleRow.Children.Add(controls);
        header.Children.Add(titleRow);
        if (filter.Count > 0)
            header.Children.Add(Ui.Caption($"Showing {shown.Count} of {all.Count} packages, filtered by: {string.Join(", ", filter.Select(f => f == "last_run" ? "Last Run" : f))}"));
        root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        root.Children.Add(HeaderRow(showSize));

        var body = new StackPanel();
        if (all.Count == 0)
            body.Children.Add(EmptyBody(info, lastRun));
        else if (shown.Count == 0)
            body.Children.Add(Ui.EmptyState($"No items with {string.Join(" or ", filter.Select(f => f == "last_run" ? "last run" : f.ToLowerInvariant()))}. No packages match the selected filters."));
        else
        {
            var groups = shown.GroupBy(p => p.Category.Trim().Length > 0 ? p.Category.Trim() : "Uncategorized")
                .OrderBy(g => g.Key == "Uncategorized" ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            // The Last Run view is a short flat list; grouping it only adds headers.
            var grouped = !lastRunActive && groups.Any(g => g.Key != "Uncategorized");
            if (!grouped) groups = [.. shown.GroupBy(_ => "")];
            foreach (var group in groups)
            {
                var items = group.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                if (grouped)
                {
                    var collapsed = state.Collapsed.Contains(group.Key);
                    var head = new StackPanel { Orientation = Orientation.Horizontal };
                    head.Children.Add(Ui.Icon(collapsed ? "" : "", 10, Ui.Brush("TextSecondaryBrush")));
                    var name = Ui.Text(group.Key); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(8, 0, 0, 0);
                    head.Children.Add(name);
                    var n = Ui.Caption($"({Format.Plural(items.Count, "item")})"); n.Margin = new Thickness(8, 0, 0, 0); n.VerticalAlignment = VerticalAlignment.Center;
                    head.Children.Add(n);
                    var bar = new Border { Background = Ui.Brush("SubtleFillBrush"), Padding = new Thickness(20, 7, 20, 7), Child = head, Cursor = Cursors.Hand };
                    var key = group.Key;
                    bar.MouseLeftButtonUp += (_, _) => { if (!state.Collapsed.Remove(key)) state.Collapsed.Add(key); rerender(); };
                    body.Children.Add(bar);
                    if (collapsed) continue;
                }
                foreach (var p in items) body.Children.Add(PackageRow(p, state, showSize, rerender));
            }
        }
        root.Children.Add(body);
        return new Border { Style = (Style)Ui.Res("CardStyle"), Child = root };
    }

    /// <summary>
    /// An empty item list means one of three things: the run failed outright (show its
    /// errors), the run did not complete, or Munki runs with nothing assigned.
    /// </summary>
    private static UIElement EmptyBody(MacInstallsInfo info, DateTime? lastRun)
    {
        if (info.RunErrors.Count > 0)
        {
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            var head = Ui.Text("Last Run Failed"); head.FontWeight = FontWeights.SemiBold; head.Foreground = Ui.StatusBrush(Tone.Error);
            panel.Children.Add(head);
            foreach (var e in info.RunErrors) panel.Children.Add(MessageBlock(e, Tone.Error));
            return panel;
        }
        var runFailed = info.SystemProblems.FailedWithoutItems;
        return Ui.EmptyState(runFailed
            ? "Last run did not complete. No items were reported because the run failed before it could evaluate the manifest."
            : $"{NonEmpty(info.SystemName) ?? "Management"} is configured and running, but no packages are currently assigned to this device."
              + (lastRun is null ? "" : $" Last check: {Format.RelativeTime(lastRun)}"), runFailed ? "" : "");
    }

    private static Grid ColumnsGrid(bool showSize)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showSize ? 90 : 0) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        return g;
    }

    private static Border HeaderRow(bool showSize)
    {
        var g = ColumnsGrid(showSize);
        var i = 0;
        foreach (var h in new[] { "Status", "Package", "Version", showSize ? "Item Size" : "", "Date Processed" })
        {
            var t = new TextBlock { Text = h.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("TextSecondaryBrush") };
            Grid.SetColumn(t, i++);
            g.Children.Add(t);
        }
        return new Border { Background = Ui.Brush("SubtleFillBrush"), Padding = new Thickness(20, 8, 20, 8), BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
    }

    private static Border PackageRow(MacInstallPackage p, TableState state, bool showSize, Action rerender)
    {
        var g = ColumnsGrid(showSize);
        var pill = Ui.Pill(p.Status.ToString(), StatusTone(p.Status));
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(pill);
        var name = Ui.Text(p.DisplayName); name.FontWeight = FontWeights.Medium; name.Margin = new Thickness(0); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap; name.ToolTip = p.Name;
        Grid.SetColumn(name, 1); g.Children.Add(name);
        var version = new StackPanel();
        var v = Ui.Text(Format.OrUnknown(p.Version)); v.Margin = new Thickness(0); v.TextTrimming = TextTrimming.CharacterEllipsis; v.TextWrapping = TextWrapping.NoWrap;
        version.Children.Add(v);
        if (!string.IsNullOrWhiteSpace(p.InstalledVersion) && p.InstalledVersion != p.Version) version.Children.Add(Ui.Caption($"{p.InstalledVersion} installed"));
        Grid.SetColumn(version, 2); g.Children.Add(version);
        if (showSize) { var size = Ui.Caption(p.FormattedItemSize); Grid.SetColumn(size, 3); g.Children.Add(size); }
        var date = new StackPanel { Orientation = Orientation.Horizontal };
        var when = Ui.Caption(p.LastUpdate is null ? "" : Format.RelativeTime(p.LastUpdate));
        if (p.LastUpdate is not null) when.ToolTip = Format.ExactTime(p.LastUpdate);
        date.Children.Add(when);
        if (p.HasExpandableContent) { var chevron = Ui.Icon(state.Expanded.Contains(p.Id) ? "" : "", 10, Ui.Brush("TextSecondaryBrush")); chevron.Margin = new Thickness(8, 0, 0, 0); date.Children.Add(chevron); }
        Grid.SetColumn(date, 4); g.Children.Add(date);

        var stack = new StackPanel();
        var row = new Border { Padding = new Thickness(20, 9, 20, 9), Child = g, Background = System.Windows.Media.Brushes.Transparent, Cursor = p.HasExpandableContent ? Cursors.Hand : Cursors.Arrow };
        if (p.HasExpandableContent)
        {
            row.MouseLeftButtonUp += (_, _) => { if (!state.Expanded.Remove(p.Id)) state.Expanded.Add(p.Id); rerender(); };
            row.MouseEnter += (_, _) => row.Background = Ui.Brush("SubtleFillBrush");
            row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        }
        stack.Children.Add(row);
        if (p.HasExpandableContent && state.Expanded.Contains(p.Id))
        {
            var details = new StackPanel { Margin = new Thickness(20, 0, 20, 12) };
            foreach (var m in p.Errors) details.Children.Add(MessageBlock(m, Tone.Error));
            foreach (var m in p.Warnings) details.Children.Add(MessageBlock(m, Tone.Warning));
            if (p.Status == MacInstallState.Pending && p.PendingReason.Trim().Length > 0)
                details.Children.Add(MessageBlock(new MacInstallMessage($"{p.Id}-pending", "PENDING", p.PendingReason, null, null, p.Name), Tone.Info));
            stack.Children.Add(details);
        }
        return new Border { BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = stack };
    }

    private static Border MessageBlock(MacInstallMessage m, Tone tone)
    {
        var panel = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var meta = new StackPanel { Orientation = Orientation.Horizontal };
        if (m.Code.Length > 0) meta.Children.Add(Ui.Pill(m.Code, tone, 10.5));
        if (m.Timestamp is not null) { var t = Ui.Caption(Format.ExactTime(m.Timestamp)); t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; meta.Children.Add(t); }
        head.Children.Add(meta);
        var full = (m.Code.Length > 0 ? $"[{m.Code}] " : "") + m.Text + (m.Details is null ? "" : $"\n\nDetails: {m.Details}");
        var copy = Ui.CopyButton(full);
        Grid.SetColumn(copy, 1);
        head.Children.Add(copy);
        panel.Children.Add(head);
        var text = Ui.Text(m.Text); text.Foreground = Ui.StatusBrush(tone); text.FontWeight = FontWeights.Medium; text.Margin = new Thickness(0, 4, 0, 0); text.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(text);
        if (!string.IsNullOrWhiteSpace(m.Details))
        {
            var pre = Ui.Mono(m.Details); pre.FontSize = 11; pre.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(new Border { Background = Ui.Brush("SubtleFillBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Child = pre });
        }
        return new Border
        {
            Background = Ui.Brush(tone == Tone.Error ? "PillRedBackground" : tone == Tone.Warning ? "PillYellowBackground" : "PillBlueBackground"),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 6, 0, 0), Child = panel,
        };
    }

    private static UIElement RunLogSection(string log)
    {
        var text = MacInstallsInfo.CleanLogText(log);
        var pre = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = (System.Windows.Media.FontFamily)Ui.Res("MonoFont"), FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Background = Ui.Brush("SubtleFillBrush"),
            Foreground = Ui.Brush("TextPrimaryBrush"), Padding = new Thickness(10), MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new SearchBox("Search log...", q =>
            pre.Text = string.IsNullOrWhiteSpace(q) ? text : string.Join("\n", text.Split('\n').Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase))));
        search.Margin = new Thickness(0);
        search.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.Children.Add(search);
        var copy = Ui.CopyButton(text);
        Grid.SetColumn(copy, 1);
        toolbar.Children.Add(copy);
        var body = new StackPanel();
        body.Children.Add(toolbar);
        body.Children.Add(pre);
        return Ui.Card(Ui.Collapsible("Managed Software Update Last Run Log", body), new Thickness(20, 8, 20, 10));
    }

    private static Tone StatusTone(MacInstallState status) => status switch
    {
        MacInstallState.Installed => Tone.Success,
        MacInstallState.Pending => Tone.Info,
        MacInstallState.Warning => Tone.Warning,
        MacInstallState.Error => Tone.Error,
        MacInstallState.Removed => Tone.Purple,
        _ => Tone.Neutral,
    };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>"2h 37m ago", the header's compact form, as the Windows tab and the web show it.</summary>
    private static string CompactRelative(DateTime t)
    {
        var local = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
        var diff = DateTime.Now - local;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return diff.Minutes > 0 ? $"{(int)diff.TotalHours}h {diff.Minutes}m ago" : $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}
