using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The four bulk reports behind the dashboard's insight widgets. The dashboard
/// payload carries devices and events only, so these load after it and the cards
/// fill in as each report answers.
/// </summary>
/// <remarks>
/// One shared copy rather than one per page: the platform toggle and every
/// navigation rebuild the dashboard page, and re-reading four fleet reports each
/// time would cost a minute of API time to redraw numbers that have not changed.
/// The rows are kept unfiltered and the platform scope is applied when a card is
/// drawn, so a toggle redraws from what is already here.
/// </remarks>
public sealed class DashboardInsightsModel
{
    public static DashboardInsightsModel Shared { get; } = new();

    /// <summary>The reports read, in the order they are requested.</summary>
    public static readonly IReadOnlyList<string> Reports = ["security", "management", "hardware", "system"];

    // Four minutes, not five: the page's own timer fires every five, and a load
    // stamped when it finished would otherwise still look fresh at the next tick and
    // skip a refresh.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(4);

    private readonly Dictionary<string, List<JsonElement>> _rows = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly HashSet<string> _loading = new();
    private DateTime? _loadedAt;
    private Task? _inFlight;

    /// <summary>Raised on the UI thread with the report's id when its state changes.</summary>
    public event Action<string>? Changed;

    public IReadOnlyList<JsonElement> Rows(string report) =>
        _rows.TryGetValue(report, out var rows) ? rows : [];

    public string? Error(string report) => _errors.GetValueOrDefault(report);

    public bool IsLoading(string report) => _loading.Contains(report);

    public Task LoadAsync(bool force = false)
    {
        if (_inFlight is { IsCompleted: false }) return _inFlight;
        if (!force && _loadedAt is { } at && DateTime.UtcNow - at < Fresh) return Task.CompletedTask;
        return _inFlight = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        foreach (var report in Reports)
        {
            _loading.Add(report);
            Changed?.Invoke(report);
        }

        // Two at a time: each bulk report takes the API ten seconds or more, and
        // four at once had the gateway answer 503 for the last of them.
        var queue = new Queue<string>(Reports);
        var running = new List<Task<(string Report, FleetApiClient.FleetResult<List<JsonElement>> Result)>>();
        while (running.Count < 2 && queue.Count > 0) running.Add(FetchAsync(queue.Dequeue()));

        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running);
            running.Remove(done);
            if (queue.Count > 0) running.Add(FetchAsync(queue.Dequeue()));

            var (report, result) = await done;
            if (result.Ok)
            {
                _rows[report] = result.Data!;
                _errors.Remove(report);
            }
            else
            {
                // Rows from the last good read stay: a failed refresh should not
                // blank a card that was showing something true five minutes ago.
                _errors[report] = result.Detail ?? "The report could not be loaded.";
            }
            _loading.Remove(report);
            Changed?.Invoke(report);
        }
        _loadedAt = DateTime.UtcNow;
    }

    /// <summary>One read, retried once after a short pause on a 5xx or a dropped connection.</summary>
    private static async Task<(string, FleetApiClient.FleetResult<List<JsonElement>>)> FetchAsync(string report)
    {
        var result = await FleetApiClient.Instance.GetModuleAsync(report);
        if (!result.Ok && result.Transient)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            result = await FleetApiClient.Instance.GetModuleAsync(report);
        }
        return (report, result);
    }
}

/// <summary>
/// The insight cards themselves: System Health, Security Posture, Management and
/// Hardware, each drawn from one bulk report and each opening that report from its
/// header. A port of the Mac app's InsightWidgets.
/// </summary>
public static class InsightWidgets
{
    /// <summary>The card for one report, in whatever state the shared model holds it.</summary>
    public static UIElement Build(string report)
    {
        var model = DashboardInsightsModel.Shared;
        var rows = model.Rows(report)
            .Where(r => PlatformFilter.Includes(Insights.Platform(r)))
            .ToList();

        var (title, subtitle, accent) = report switch
        {
            "security" => ("Security Posture", "Fleet-wide protections in place", Accent.Green),
            "management" => ("Management", "MDM enrollment across the fleet", Accent.Purple),
            "hardware" => ("Hardware", "Architecture, memory and storage", Accent.Teal),
            _ => ("System Health", "Uptime and pending updates", Accent.Orange),
        };

        UIElement body;
        var error = model.Error(report);
        if (rows.Count == 0 && error is not null) body = Message(error);
        else if (rows.Count == 0 && model.IsLoading(report)) body = Loading();
        else if (rows.Count == 0) body = Message("No data");
        else body = report switch
        {
            "security" => SecurityBody(rows),
            "management" => ManagementBody(rows),
            "hardware" => HardwareBody(rows),
            _ => SystemBody(rows),
        };

        return Card(title, subtitle, report, accent, body);
    }

    // ── Bodies ───────────────────────────────────────────────────────────

    private static UIElement SecurityBody(List<JsonElement> rows)
    {
        var posture = Insights.Security(rows);
        var body = new StackPanel();
        foreach (var row in posture.Rows) body.Children.Add(ComplianceRow(row));
        body.Children.Add(Chips(
            Chip(posture.WithThreats, "with threats", Tone.Error),
            Chip(posture.ExpiredCerts, "expired certs", Tone.Warning),
            Chip(posture.CriticalCves, "critical CVEs", Tone.Orange)));
        return body;
    }

    private static UIElement ManagementBody(List<JsonElement> rows)
    {
        var m = Insights.ManagementOverview(rows);
        var percent = m.Total == 0 ? 0 : (int)Math.Round((double)m.Enrolled / m.Total * 100);

        var body = new StackPanel();
        body.Children.Add(DonutWithLegend(
            [
                ("Enrolled", m.Enrolled, Ui.StatusBrush(Tone.Success)),
                ("Not Enrolled", m.Total - m.Enrolled, Ui.Brush("SubtleFillHoverBrush")),
            ], $"{percent}%", "enrolled"));

        if (m.TopProviders.Count > 0)
        {
            var eyebrow = Ui.Eyebrow("PROVIDERS");
            eyebrow.Margin = new Thickness(0, 16, 0, 6);
            body.Children.Add(eyebrow);
            var max = Math.Max(1, m.TopProviders[0].Count);
            foreach (var (name, count) in m.TopProviders)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                var label = Ui.Text(name);
                label.FontSize = 13;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.ToolTip = name;
                row.Children.Add(label);
                var bar = new Border
                {
                    Height = 6, CornerRadius = new CornerRadius(3),
                    Width = Math.Max(2, 70.0 * count / max),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Ui.Brush("IconPurpleForeground"),
                    Opacity = 0.55,
                };
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var n = Number(count);
                Grid.SetColumn(n, 2);
                row.Children.Add(n);
                body.Children.Add(row);
            }
        }
        return body;
    }

    private static UIElement HardwareBody(List<JsonElement> rows)
    {
        var h = Insights.HardwareOverview(rows);
        Brush FamilyBrush(string family) => family switch
        {
            "Apple Silicon" => Ui.Brush("IconBlueForeground"),
            "Intel" => Ui.Brush("IconIndigoForeground"),
            "ARM64" => Ui.Brush("IconTealForeground"),
            "x64" => Ui.Brush("IconCyanForeground"),
            _ => Ui.Brush("SubtleFillHoverBrush"),
        };

        var body = new StackPanel();
        body.Children.Add(DonutWithLegend(
            h.Families.Select(f => (f.Family, f.Count, FamilyBrush(f.Family))).ToList(),
            h.Total.ToString("N0"), "devices"));
        var chips = Chips(
            Chip(h.LowStorage, "under 10% free", Tone.Error),
            Chip(h.LowMemory, "8 GB or less", Tone.Warning),
            Chip(h.Laptops, "laptops", Tone.Info));
        chips.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(chips);
        return body;
    }

    private static UIElement SystemBody(List<JsonElement> rows)
    {
        var s = Insights.SystemOverview(rows);
        var max = Math.Max(1, s.Uptime.Max(b => b.Count));

        var body = new StackPanel();
        var eyebrow = Ui.Eyebrow("UPTIME");
        eyebrow.Margin = new Thickness(0, 0, 0, 6);
        body.Children.Add(eyebrow);
        foreach (var (label, count) in s.Uptime)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            var name = Ui.Text(label);
            name.FontSize = 13;
            row.Children.Add(name);
            var track = Track(8, (double)count / max, Ui.StatusBrush(Tone.Orange));
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            var n = Number(count);
            Grid.SetColumn(n, 2);
            row.Children.Add(n);
            body.Children.Add(row);
        }
        var chips = Chips(
            Chip(s.Pending, "with pending updates", Tone.Warning),
            Chip(s.Deferred, "with deferred updates", Tone.Orange));
        chips.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(chips);
        return body;
    }

    // ── Marks ────────────────────────────────────────────────────────────

    /// <summary>"Label … n of N  pct" over a bar, coloured by how close to complete it is.</summary>
    private static UIElement ComplianceRow(Insights.Compliance c)
    {
        var tone = c.Fraction >= 0.9 ? Tone.Success : c.Fraction >= 0.7 ? Tone.Warning : Tone.Error;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var label = Ui.Text(c.Label);
        label.FontSize = 13;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(label);
        var of = Ui.Caption($"{c.On:N0} of {c.Total:N0}");
        Grid.SetColumn(of, 1);
        head.Children.Add(of);
        var pct = Ui.Status($"{c.Fraction:P0}", tone, 12, FontWeights.SemiBold);
        pct.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(pct, 2);
        head.Children.Add(pct);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(head);
        var track = Track(6, c.Fraction, Ui.StatusBrush(tone));
        track.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(track);
        return panel;
    }

    private static Border Track(double height, double fraction, Brush fillBrush)
    {
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = Ui.Brush("SubtleFillBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fill = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = fillBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.Child = fill;
        track.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * Math.Clamp(fraction, 0, 1));
        return track;
    }

    private static UIElement DonutWithLegend(IReadOnlyList<(string Label, int Count, Brush Brush)> data, string centre, string centreLabel)
    {
        var total = data.Sum(d => d.Count);
        var donut = Ui.Donut(data.Select(d => ((double)d.Count, d.Brush, $"{d.Label}: {d.Count:N0}")), total,
            centre, centreLabel, size: 104, thickness: 14);
        // The shared donut's centre figure is sized for the larger storage chart.
        foreach (var child in donut.Children.OfType<StackPanel>())
            if (child.Children.Count > 0 && child.Children[0] is TextBlock big) big.FontSize = 17;
        donut.VerticalAlignment = VerticalAlignment.Center;

        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        foreach (var (label, count, brush) in data)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 9, Height = 9, Fill = brush,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            };
            row.Children.Add(dot);
            var name = Ui.Text(label);
            name.FontSize = 13;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var n = Number(count);
            Grid.SetColumn(n, 2);
            row.Children.Add(n);
            var share = Ui.Caption(total == 0 ? "" : $"{(double)count / total:P0}");
            share.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(share, 3);
            row.Children.Add(share);
            legend.Children.Add(row);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(donut);
        Grid.SetColumn(legend, 1);
        grid.Children.Add(legend);
        return grid;
    }

    private static WrapPanel Chips(params UIElement[] chips)
    {
        var wrap = Ui.Wrap(8, chips);
        wrap.Margin = new Thickness(0, 4, 0, 0);
        return wrap;
    }

    /// <summary>A small "n label" chip; a zero reads in the secondary colour, not the alarm one.</summary>
    private static UIElement Chip(int value, string label, Tone tone)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var n = value == 0
            ? Ui.Text(value.ToString("N0"), "StatValueStyle", Ui.Brush("TextSecondaryBrush"))
            : Ui.Status(value.ToString("N0"), tone, 13, FontWeights.SemiBold);
        n.FontWeight = FontWeights.SemiBold;
        n.FontSize = 13;
        n.Margin = new Thickness(0, 0, 6, 0);
        n.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(n);
        var text = Ui.Caption(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(text);
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("SubtleFillBrush"),
            BorderBrush = Ui.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private static TextBlock Number(int value)
    {
        var n = Ui.Text(value.ToString("N0"));
        n.FontSize = 13;
        n.FontWeight = FontWeights.Medium;
        n.TextAlignment = TextAlignment.Right;
        n.VerticalAlignment = VerticalAlignment.Center;
        return n;
    }

    private static UIElement Message(string text)
    {
        var t = Ui.Caption(text);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static UIElement Loading()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new ModernWpf.Controls.ProgressRing { IsActive = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        var text = Ui.Caption("Loading…");
        text.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(text);
        return panel;
    }

    // ── Card chrome ──────────────────────────────────────────────────────

    /// <summary>
    /// The shared card with its header made into a way into the report, by mouse
    /// and by keyboard, the way every dashboard card header behaves on the web and
    /// the Mac.
    /// </summary>
    private static UIElement Card(string title, string subtitle, string report, Accent accent, UIElement body)
    {
        var area = ReportArea.ById(report);
        var chevron = Ui.Icon("\uE76C", 12, Ui.Brush("TextTertiaryBrush"));
        var card = Ui.StatBlock(title, subtitle, area?.Glyph ?? "\uE9D9", accent, body, chevron);

        if (card.Child is StackPanel { Children.Count: > 0 } root && root.Children[0] is Grid header)
        {
            header.Background = Brushes.Transparent;
            header.Cursor = Cursors.Hand;
            header.Focusable = true;
            header.ToolTip = $"Open the {area?.Title ?? title} report";
            System.Windows.Automation.AutomationProperties.SetName(header, header.ToolTip.ToString());
            void Open() => DashboardView.Containing(header)?.NavigateTo("report:" + report);
            header.MouseLeftButtonUp += (_, _) => Open();
            header.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space)) return;
                e.Handled = true;
                Open();
            };
        }
        return card;
    }
}
