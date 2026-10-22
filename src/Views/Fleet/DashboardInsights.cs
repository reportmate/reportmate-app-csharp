using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The dashboard's insight cards, read in one call: <c>GET /api/v1/dashboard/insights</c>.
/// </summary>
/// <remarks>
/// The server computes every card with aggregate SQL and caches it for five
/// minutes, so a dashboard downloads a few kilobytes of counts and short lists
/// instead of a dozen bulk reports. Reading those reports from every client pushed
/// the API container to its memory limit until it restarted.
///
/// One shared copy rather than one per page: the platform toggle and every
/// navigation rebuild the dashboard page. The document is kept per platform scope,
/// so toggling back is instant, and refreshed when older than four minutes -- the
/// page's own timer fires every five.
/// </remarks>
public sealed class DashboardInsightsModel
{
    public static DashboardInsightsModel Shared { get; } = new();

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(4);

    private readonly Dictionary<PlatformScope, (JsonElement Document, DateTime At)> _documents = new();
    private readonly Dictionary<PlatformScope, string> _errors = new();
    private readonly Dictionary<PlatformScope, Task> _inFlight = new();

    /// <summary>Raised on the UI thread whenever a document arrives or a read fails.</summary>
    public event Action? Changed;

    private static PlatformScope Scope => PlatformFilter.Current;

    /// <summary>The <c>platform</c> query value for a scope.</summary>
    public static string PlatformParameter(PlatformScope scope) => scope switch
    {
        PlatformScope.Mac => "macOS",
        PlatformScope.Windows => "Windows",
        _ => "all",
    };

    /// <summary>One card's section for the current scope, or null until the document arrives.</summary>
    public JsonElement? Card(string key) =>
        _documents.TryGetValue(Scope, out var d) && Insights.Prop(d.Document, "cards") is { } cards
            ? Insights.Prop(cards, key)
            : null;

    public bool HasDocument => _documents.ContainsKey(Scope);

    /// <summary>Why the last read for the current scope failed, while there is no document to show.</summary>
    public string? Error => _errors.GetValueOrDefault(Scope);

    public bool IsLoading => _inFlight.TryGetValue(Scope, out var t) && !t.IsCompleted;

    /// <summary>When the server computed the document on screen.</summary>
    public DateTime? GeneratedAt =>
        _documents.TryGetValue(Scope, out var d) && DateTime.TryParse(Insights.Str(d.Document, "generatedAt"),
            null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    public Task LoadAsync(bool force = false)
    {
        var scope = Scope;
        if (_inFlight.TryGetValue(scope, out var running) && !running.IsCompleted) return running;
        if (!force && _documents.TryGetValue(scope, out var d) && DateTime.UtcNow - d.At < Fresh) return Task.CompletedTask;
        var task = LoadCoreAsync(scope);
        _inFlight[scope] = task;
        Changed?.Invoke();
        return task;
    }

    /// <summary>Read the document again, for a card's Retry.</summary>
    public Task RetryAsync() => LoadAsync(force: true);

    private async Task LoadCoreAsync(PlatformScope scope)
    {
        JsonElement? document = null;
        var outcome = await ReportQueue.RunAsync(async ct =>
        {
            var r = await FleetApiClient.Instance.GetRawAsync(
                $"/api/v1/dashboard/insights?platform={PlatformParameter(scope)}", ct);
            if (!r.Ok) return new FetchOutcome(r.Detail ?? "The dashboard insights could not be loaded.", r.Transient);
            using var doc = r.Data!;
            document = doc.RootElement.Clone();
            return FetchOutcome.Success;
        }, ReportQueue.DefaultTimeout);

        if (outcome.Ok && document is { } d)
        {
            _documents[scope] = (d, DateTime.UtcNow);
            _errors.Remove(scope);
        }
        else
        {
            // The last good document stays on screen: a failed refresh should not
            // blank cards that were showing something true five minutes ago.
            _errors[scope] = outcome.Error ?? "The dashboard insights could not be loaded.";
        }
        _inFlight.Remove(scope);
        Changed?.Invoke();
    }
}

/// <summary>One insight card: its title, where its header goes, and its skeleton's shape.</summary>
public sealed record InsightCard(
    string Key, string Title, string Subtitle, Accent Accent, string Glyph, DeepLink Header,
    InsightShape Shape = InsightShape.Figures);

/// <summary>
/// The twelve insight cards of the shared spec's revision 2, each answering one
/// question. Every number, bar, list row and chip follows the link the server sent
/// with it, and every header opens its report unfiltered.
/// </summary>
public static class InsightWidgets
{
    private static string GlyphOf(string report) => ReportArea.ById(report)?.Glyph ?? "";

    private static DeepLink L(string section) => DeepLink.For(section);

    public static readonly IReadOnlyList<InsightCard> All =
    [
        new("rebootDebt", "Reboot & Update Debt", "Long uptimes and waiting updates", Accent.Orange, GlyphOf("system"), L("system"), InsightShape.Bars),
        new("protectionGaps", "Protection Gaps", "Devices missing a protection", Accent.Red, GlyphOf("security"), L("security"), InsightShape.Bars),
        new("unmanagedDevices", "Unmanaged Devices", "Not enrolled in MDM", Accent.Purple, GlyphOf("management"), L("management")),
        new("hardwareAtRisk", "Hardware at Risk", "Storage, memory and Intel Macs", Accent.Orange, GlyphOf("hardware"), L("hardware")),
        new("installHealth", "Install Health", "Munki and Cimian across the fleet", Accent.Emerald, GlyphOf("installs"), L("installs")),
        new("checkinGaps", "Check-in Gaps", "Turned away, and gone quiet", Accent.Red, "",
            DeepLink.For("events", "failures", ("hours", "24"))),
        new("osCurrency", "OS Currency", "On the latest patch for their version", Accent.Purple, GlyphOf("system"), L("system"), InsightShape.Donut),
        new("softwareRisk", "Software Risk", "Vulnerabilities and items behind", Accent.Red, GlyphOf("security"), L("security")),
        new("expiringCertificates", "Expiring Certificates", "Expiring soon, and already expired", Accent.Yellow, "", L("security")),
        new("localAdminExposure", "Local Admin Exposure", "Extra local admins and unjoined devices", Accent.Indigo, GlyphOf("identity"), L("identity")),
        new("unusedSoftware", "Unused Software", "Installed widely, rarely used", Accent.Blue, GlyphOf("applications"), L("applications"), InsightShape.Bars),
        new("connectivityProblems", "Connectivity Problems", "Weak Wi-Fi, poor networks and VPN", Accent.Teal, GlyphOf("network"), L("network")),
    ];

    /// <summary>The card in whatever state the shared model holds it.</summary>
    public static UIElement Build(InsightCard card)
    {
        var model = DashboardInsightsModel.Shared;
        UIElement body;
        if (!model.HasDocument)
            body = model.Error is { } error && !model.IsLoading
                ? Failed(error)
                : Skeleton.Pulse(Skeleton.InsightBody(card.Shape));
        else
        {
            var view = InsightCards.Build(card.Key, model.Card(card.Key), PlatformFilter.Current != PlatformScope.All);
            body = view.Error is { } error ? Failed(error) : Render(view);
        }

        var chevron = Ui.Icon("", 12, Ui.Brush("TextTertiaryBrush"));
        var block = Ui.StatBlock(card.Title, card.Subtitle, card.Glyph, card.Accent, body, chevron);
        return Linkable.LinkHeader(block, card.Header);
    }

    // ── Rendering a card's parts ─────────────────────────────────────────

    private static UIElement Render(CardView view)
    {
        var body = new StackPanel();
        var first = true;
        foreach (var part in view.Parts)
        {
            var element = part switch
            {
                FiguresPart f => Figures(f),
                BarsPart b => Bars(b, first),
                ChipsPart c => Chips(c.Items.Select(Chip).ToArray()),
                DonutPart d => Donut(d),
                ListPart l => List(l, first),
                NotePart n => Note(n.Text),
                _ => new Border(),
            };
            body.Children.Add(element);
            first = false;
        }
        return body;
    }

    private static UIElement Figures(FiguresPart part)
    {
        var cells = part.Items.Select(Figure).ToArray();
        return Ui.Columns(Math.Max(1, cells.Length), 10, cells);
    }

    private static UIElement Bars(BarsPart part, bool first)
    {
        var panel = new StackPanel();
        if (part.Heading is { } heading) panel.Children.Add(Eyebrow(heading, first ? 0 : 14));
        var max = Math.Max(1, part.Items.Count == 0 ? 1 : part.Items.Max(i => i.Count));
        foreach (var item in part.Items)
            panel.Children.Add(BarRow(item, item.Fraction ?? item.Count / max));
        return panel;
    }

    private static UIElement List(ListPart part, bool first)
    {
        var panel = new StackPanel();
        if (part.Heading is { } heading) panel.Children.Add(Eyebrow(heading, first ? 0 : 14));
        foreach (var item in part.Items)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Ui.Text(item.Label);
            label.FontSize = 13;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.ToolTip = item.Label;
            row.Children.Add(PlatformIcon.Labelled(item.Platform, label, 12));
            var detail = Ui.Caption(item.Detail ?? "");
            detail.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(detail, 1);
            row.Children.Add(detail);
            panel.Children.Add(item.Link is { } link ? Row(row, link) : row);
        }
        return panel;
    }

    private static UIElement Donut(DonutPart part)
    {
        Brush Paint(Severity s) => s switch
        {
            Severity.Good => Ui.StatusBrush(Tone.Success),
            Severity.Warning => Ui.StatusBrush(Tone.Warning),
            Severity.Bad => Ui.StatusBrush(Tone.Error),
            Severity.Info => Ui.Brush("IconBlueForeground"),
            _ => Ui.Brush("SubtleFillHoverBrush"),
        };
        return DonutWithLegend(part.Slices.Select(s => (s.Label, (int)s.Count, Paint(s.Severity), s.Link)).ToList(),
            part.Centre, part.CentreLabel);
    }

    private static UIElement Note(string text)
    {
        var t = Ui.Caption(text);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 6, 0, 4);
        return t;
    }

    private static Tone ToneOf(Severity s) => s switch
    {
        Severity.Good => Tone.Success,
        Severity.Warning => Tone.Warning,
        Severity.Bad => Tone.Error,
        Severity.Info => Tone.Info,
        _ => Tone.Neutral,
    };

    // ── Marks ────────────────────────────────────────────────────────────

    /// <summary>A linked row, inset so its hover tint has room around the content.</summary>
    private static Border Row(UIElement content, DeepLink link, string? tip = null)
    {
        var wrap = Linkable.Wrap(content, link, tip);
        wrap.Padding = new Thickness(6, 3, 6, 3);
        wrap.Margin = new Thickness(-6, 0, -6, 3);
        return wrap;
    }

    /// <summary>"label  [bar]  n", the shape every bar list on these cards uses.</summary>
    private static UIElement BarRow(Metric item, double fraction)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 44 });
        var name = Ui.Text(item.Label);
        name.FontSize = 13;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = item.Detail is null ? item.Label : $"{item.Label} · {item.Detail}";
        var label = PlatformIcon.Labelled(item.Platform, name, 12);
        if (label is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(label);
        var track = Track(8, fraction, item.Severity == Severity.Neutral ? Ui.Brush("IconBlueForeground") : Ui.StatusBrush(ToneOf(item.Severity)));
        Grid.SetColumn(track, 1);
        row.Children.Add(track);
        var n = Ui.Text(item.Shown);
        n.FontSize = 13;
        n.FontWeight = FontWeights.Medium;
        n.TextAlignment = TextAlignment.Right;
        n.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(n, 2);
        row.Children.Add(n);
        return item.Link is { } link ? Row(row, link) : row;
    }

    /// <summary>A large number over its caption, linked when the server gave it a link.</summary>
    private static UIElement Figure(Metric item)
    {
        var panel = new StackPanel();
        var value = item.Shown;
        var n = item.Count == 0 || item.Severity == Severity.Neutral
            ? Ui.Text(value, "StatValueStyle", Ui.Brush(item.Count == 0 ? "TextSecondaryBrush" : "TextPrimaryBrush"))
            : Ui.Status(value, ToneOf(item.Severity), 24, FontWeights.SemiBold);
        n.FontSize = 24;
        n.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(n);
        var c = Ui.Caption(item.Label);
        c.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(c);
        if (item.Detail is { } detail)
        {
            var d = Ui.Caption(detail);
            d.TextWrapping = TextWrapping.Wrap;
            d.Opacity = 0.8;
            panel.Children.Add(d);
        }
        if (item.Link is not { } link) return panel;
        var wrap = Linkable.Wrap(panel, link);
        wrap.Padding = new Thickness(6, 4, 6, 6);
        wrap.Margin = new Thickness(-6, 0, 0, 6);
        return wrap;
    }

    private static TextBlock Eyebrow(string text, double top = 14)
    {
        var e = Ui.Eyebrow(text);
        e.Margin = new Thickness(0, top, 0, 6);
        return e;
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

    private static UIElement DonutWithLegend(IReadOnlyList<(string Label, int Count, Brush Brush, DeepLink? Link)> data, string centre, string centreLabel)
    {
        var total = data.Sum(d => d.Count);
        var donut = Ui.Donut(data.Select(d => ((double)d.Count, d.Brush, $"{d.Label}: {d.Count:N0}")), total,
            centre, centreLabel, size: 104, thickness: 14);
        // The shared donut's centre figure is sized for the larger storage chart.
        foreach (var child in donut.Children.OfType<StackPanel>())
            if (child.Children.Count > 0 && child.Children[0] is TextBlock big) big.FontSize = 17;
        donut.VerticalAlignment = VerticalAlignment.Center;

        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var (label, count, brush, link) in data)
        {
            var row = new Grid();
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
            var n = Ui.Text(count.ToString("N0"));
            n.FontSize = 13;
            n.FontWeight = FontWeights.Medium;
            n.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(n, 2);
            row.Children.Add(n);
            var share = Ui.Caption(total == 0 ? "" : $"{(double)count / total:P0}");
            share.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(share, 3);
            row.Children.Add(share);
            if (link is { } l)
            {
                var wrap = Linkable.Wrap(row, l);
                wrap.Padding = new Thickness(6, 2, 6, 2);
                wrap.Margin = new Thickness(0, 0, 0, 2);
                legend.Children.Add(wrap);
            }
            else
            {
                row.Margin = new Thickness(6, 2, 6, 4);
                legend.Children.Add(row);
            }
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
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
        wrap.Margin = new Thickness(0, 8, 0, 0);
        return wrap;
    }

    /// <summary>A small "n label" chip; a zero reads in the secondary colour, not the alarm one.</summary>
    private static UIElement Chip(Metric item)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var n = item.Count == 0 || item.Severity == Severity.Neutral
            ? Ui.Text(item.Shown, "StatValueStyle", Ui.Brush("TextSecondaryBrush"))
            : Ui.Status(item.Shown, ToneOf(item.Severity), 13, FontWeights.SemiBold);
        n.FontWeight = FontWeights.SemiBold;
        n.FontSize = 13;
        n.Margin = new Thickness(0, 0, 6, 0);
        n.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(n);
        var text = Ui.Caption(item.Label);
        text.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(text);
        var chip = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("SubtleFillBrush"),
            BorderBrush = Ui.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 6),
        };
        if (item.Link is { } link) Linkable.Attach(chip, link);
        return chip;
    }

    /// <summary>The error, and a Retry that reads the insights again.</summary>
    private static UIElement Failed(string error)
    {
        var panel = new StackPanel();
        var t = Ui.Caption(error);
        t.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(t);
        var retry = Ui.IconButton("", "Retry", (_, _) => _ = DashboardInsightsModel.Shared.RetryAsync());
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        retry.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(retry);
        return panel;
    }
}
