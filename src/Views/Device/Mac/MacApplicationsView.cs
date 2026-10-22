using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Applications tab, as the Mac and web apps lay it out: installed
/// count, usage tiles, the most-used list, and the searchable table with usage
/// filter pills and bundled helpers hidden by default.
/// </summary>
public static class MacApplicationsView
{
    private const string Glyph = "\uE71D";
    private const Accent TabAccent = Accent.Blue;
    private const string Subtitle = "Versions installed and how much each is used";

    /// <summary>One table row, the reader's item with its labels worked out.</summary>
    public sealed class Row
    {
        public required MacApplicationItem App { get; init; }
        public string Name => App.CleanName;
        public string Sub => App.SecondaryLine;
        public string VersionLabel => App.Version == "Unknown" ? "—" : App.Version;
        public string Architecture => App.Architecture ?? "";
        public string SourceLabel => App.Source is { } s && s.ToLowerInvariant() is "apple" or "system" or "user" ? s : "";
        public Tone SourceTone => string.Equals(App.Source, "user", StringComparison.OrdinalIgnoreCase) ? Tone.Orange : Tone.Neutral;
        public string Running => App.IsRunning ? "running" : App.Nested ? "helper" : "";
        public Tone RunningTone => App.IsRunning ? Tone.Success : Tone.Neutral;
        public string LastUsedLabel => App.HasUsage && App.Usage?.LastUsed is { } last ? Format.RelativeTime(last) : "—";
        public string TimeLabel => App.HasUsage && App.UsageSeconds > 0 ? MacApplicationsInfo.FormatDuration(App.UsageSeconds) : "—";
        public double Share { get; set; }
        public double Seconds => App.UsageSeconds;
        public long Launches => App.Usage?.LaunchCount ?? 0;
        public string LaunchesLabel => App.HasUsage && Launches > 0 ? Format.Number(Launches) : "—";
        public string DaysLabel => App.HasUsage && App.Usage?.DaysSeen is > 0 and var d ? Format.Plural(d, "day") : "";
        public string UsersLabel
        {
            get
            {
                var users = App.Usage?.Users ?? [];
                if (!App.HasUsage || users.Count == 0) return "—";
                return string.Join(", ", users.Take(2).Select(MacApplicationsInfo.ShortUser)) + (users.Count > 2 ? $" +{users.Count - 2}" : "");
            }
        }
    }

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("applications", out var raw)) return null;
        var info = new MacApplicationsInfo(raw);
        var page = new StackPanel();

        if (!info.HasData)
        {
            page.Children.Add(Ui.TabHeader("Applications", Subtitle, Glyph, TabAccent));
            page.Children.Add(Ui.Card(Ui.EmptyState(s.ModuleError("applications") is { } error
                ? $"The applications data on this device could not be read: {error}"
                : "The applications module has not reported an inventory for this device.", Glyph)));
            page.Children.Add(RawCard(raw, s));
            return page;
        }

        page.Children.Add(Ui.TabHeader("Applications", Subtitle, Glyph, TabAccent, CountBadge(info)));

        var days = info.HistoryDays;
        var sessions = info.ActiveSessionCount;
        page.Children.Add(Ui.TileRow(12,
            Ui.Tile("Used", Format.Number(info.UsedApps.Count), Ui.Caption(days > 0 ? $"across {Format.Plural(days, "day")} of history" : "in the current capture window")),
            Ui.Tile("Running now", Format.Number(info.RunningApps.Count), sessions > 0 ? Ui.Caption($"{Format.Plural(sessions, "session")} in the capture window") : null),
            Ui.Tile("Time in apps", MacApplicationsInfo.FormatDuration(info.TotalUsageSeconds), Ui.Caption("process lifetime, summed")),
            Ui.Tile("People", Format.Number(info.DistinctUsers), Ui.Caption(info.DistinctUsers == 1 ? "one account seen" : "accounts seen in usage"))));

        if (info.TopApps.Count > 0) page.Children.Add(MostUsed(info.TopApps));

        var rows = MacApplicationsInfo.Deduplicate(info.Applications).Select(a => new Row { App = a }).ToList();
        var maxSeconds = rows.Count > 0 ? rows.Max(r => r.Seconds) : 0;
        foreach (var r in rows) r.Share = maxSeconds > 0 && r.Seconds > 0 ? Math.Max(2, Math.Round(r.Seconds / maxSeconds * 100)) : 0;

        var table = new FilteredTable<Row>("Applications", "{0} of {1} applications", rows,
            (r, q) => MacApplicationsInfo.Relevance(r.App, q.Trim().ToLowerInvariant()) > 0,
            [
                Col.Text("Application", "Name", star: true, sub: "Sub"),
                Col.Pill("", "Running", "RunningTone", 84),
                Col.Text("Version", "VersionLabel", 140, mono: true, sub: "Architecture"),
                Col.Pill("Source", "SourceLabel", "SourceTone", 90),
                Col.Text("Last used", "LastUsedLabel", 120),
                Col.Bar("Time", "TimeLabel", "Share", 140),
                Col.Text("Launches", "LaunchesLabel", 100, sub: "DaysLabel", sortBy: "Launches"),
                Col.Text("Users", "UsersLabel", 150),
            ], "Search applications...", "No applications in this view")
            .Filter([
                    new("all", "All", info.BaseApps.Count), new("used", "Used", info.UsedApps.Count),
                    new("active", "Running", info.RunningApps.Count), new("unused", "No usage", info.UnusedApps.Count),
                ],
                (r, k) => k switch { "used" => r.App.HasUsage, "active" => r.App.IsRunning, "unused" => !r.App.HasUsage, _ => true });
        if (info.NestedCount > 0)
            table.Filter([new("apps", "Hide helpers", info.BaseApps.Count), new("all", "With helpers", info.Applications.Count)],
                (r, k) => k == "all" || !r.App.Nested, initial: "apps");
        var built = table.WithDetails(Details).Build();
        built.Margin = new Thickness(0, 24, 0, 0);
        page.Children.Add(built);

        page.Children.Add(RawCard(raw, s));
        return page;
    }

    private static StackPanel CountBadge(MacApplicationsInfo info)
    {
        var badge = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        badge.Children.Add(Ui.Caption("Applications"));
        var count = Ui.Text(Format.Number(info.BaseApps.Count));
        count.FontSize = 22; count.FontWeight = FontWeights.Bold; count.TextAlignment = TextAlignment.Right;
        count.Foreground = Ui.Brush("IconBlueForeground");
        badge.Children.Add(count);
        if (info.NestedCount > 0)
        {
            var helpers = Ui.Caption($"+{Format.Number(info.NestedCount)} bundled helpers");
            helpers.TextAlignment = TextAlignment.Right;
            badge.Children.Add(helpers);
        }
        return badge;
    }

    private static Border MostUsed(IReadOnlyList<MacApplicationItem> top)
    {
        var topMax = top[0].UsageSeconds;
        var list = new StackPanel();
        foreach (var app in top)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            if (app.IsRunning)
                left.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Ui.StatusBrush(Tone.Success), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Running now" });
            var n = Ui.Text(app.CleanName); n.FontWeight = FontWeights.Medium; n.Margin = new Thickness(0);
            left.Children.Add(n);
            if (app.Version != "Unknown") { var v = Ui.Caption(app.Version); v.Margin = new Thickness(8, 0, 0, 0); v.VerticalAlignment = VerticalAlignment.Center; left.Children.Add(v); }
            line.Children.Add(left);
            var launches = app.Usage?.LaunchCount ?? 0;
            var users = app.Usage?.UniqueUserCount ?? 0;
            var meta = string.Join("  •  ", new[]
            {
                MacApplicationsInfo.FormatDuration(app.UsageSeconds),
                $"{Format.Number(launches)} launch{(launches == 1 ? "" : "es")}",
                users > 0 ? Format.Plural(users, "user") : null,
                app.Usage?.LastUsed is { } last ? Format.RelativeTime(last) : null,
            }.Where(x => !string.IsNullOrEmpty(x)));
            var m = Ui.Caption(meta); m.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(m, 1);
            line.Children.Add(m);
            item.Children.Add(line);
            var bar = Ui.UsageBar(topMax > 0 ? Math.Max(2, app.UsageSeconds / topMax * 100) : 0, 6);
            bar.Margin = new Thickness(0, 6, 0, 0);
            foreach (var child in bar.Children)
                if (child is Border b && Grid.GetColumn(b) == 0 && b.Background != Ui.Brush("SubtleFillBrush")) b.Background = Ui.Brush("IconBlueForeground");
            item.Children.Add(bar);
            list.Children.Add(item);
        }
        var card = Ui.TableCard("Most used", null, new Border { Padding = new Thickness(20, 14, 20, 8), Child = list });
        card.Margin = new Thickness(0, 24, 0, 0);
        return card;
    }

    /// <summary>The bundle's own facts, shown when a row is opened.</summary>
    private static UIElement Details(Row row)
    {
        var a = row.App;
        var left = new StackPanel();
        var right = new StackPanel();
        void Add(StackPanel col, string label, string? value, bool mono = false)
        {
            if (!string.IsNullOrWhiteSpace(value)) col.Children.Add(Ui.Row(label, value, mono, semibold: false));
        }
        Add(left, "Path", a.Path, mono: true);
        Add(left, "Bundle ID", a.BundleId, mono: true);
        Add(left, "Version", a.Version == "Unknown" ? null : a.Version, mono: true);
        Add(left, "Build", a.BuildVersion, mono: true);
        Add(left, "Minimum macOS", a.MinimumSystemVersion);
        Add(left, "Category", a.CategoryLabel);
        Add(right, "Publisher", a.Publisher == "Unknown Publisher" ? null : a.Publisher);
        Add(right, "Signed by", a.SignedBy);
        Add(right, "Obtained from", a.ObtainedFrom);
        Add(right, "Source", a.Source);
        Add(right, "Size", a.Size);
        Add(right, "Installed", a.InstallDate);
        if (a.Usage is { } u)
        {
            Add(right, "First seen", u.FirstSeen is { } f ? Format.RelativeTime(f) : null);
            Add(right, "Average session", u.AverageSessionSeconds is { } avg ? MacApplicationsInfo.FormatDuration(avg) : null);
            Add(right, "Users", u.Users.Count > 0 ? string.Join(", ", u.Users) : null);
        }
        return new Border { Padding = new Thickness(14, 4, 14, 14), Child = Ui.Columns(2, 24, left, right) };
    }

    private static Border RawCard(System.Text.Json.JsonElement raw, DeviceSnapshot s)
    {
        var card = Ui.StatBlock("Applications module",
            s.CollectedAtFor("applications") is { } at ? $"As the Mac sent it, collected {Format.RelativeTime(at)}" : "As the Mac sent it",
            Glyph, TabAccent, JsonTree.Build(raw));
        card.Margin = new Thickness(0, 24, 0, 0);
        return card;
    }
}
