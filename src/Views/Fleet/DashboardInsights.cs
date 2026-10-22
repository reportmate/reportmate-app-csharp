using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The dashboard's insight cards, read from the server's cached insights.
/// </summary>
/// <remarks>
/// The server computes every card with aggregate SQL and caches it, so a dashboard
/// downloads a few kilobytes of counts and short lists instead of a dozen bulk
/// reports. Reading those reports from every client pushed the API container to its
/// memory limit until it restarted.
///
/// Two ways in, one store: the whole document from <c>/api/v1/dashboard/insights</c>,
/// or, with <see cref="PerCard"/>, each card from
/// <c>/api/v1/dashboard/insights/{card}</c> in parallel, so they paint as they land.
/// Either way each card keeps its own section, error and start time, so a card can
/// fail, retry or time out alone.
///
/// One shared copy rather than one per page: the platform toggle and every
/// navigation rebuild the dashboard page. Cards are kept per platform scope, so
/// toggling back is instant, and refreshed when older than four minutes -- the
/// page's own timer fires every five.
/// </remarks>
public sealed class DashboardInsightsModel
{
    public static DashboardInsightsModel Shared { get; } = new();

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(4);

    /// <summary>A card shows a skeleton for at most this long, then says it is still loading.</summary>
    public static readonly TimeSpan SkeletonLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Read each card from its own endpoint, in parallel, so cards paint as they land.
    /// Off, the whole document is one call, and App Usage is not read.
    /// </summary>
    public static bool PerCard { get; set; } = true;

    /// <summary>How many per-card reads run at once.</summary>
    private const int PerCardConcurrency = 4;

    private sealed class CardState
    {
        public JsonElement? Section;
        public DateTime? LoadedAt;
        public string? Error;
        public DateTime? StartedAt;
        public bool Loading;
    }

    private readonly Dictionary<(PlatformScope, string), CardState> _cards = new();
    private readonly Dictionary<PlatformScope, Task> _inFlight = new();

    /// <summary>Raised on the UI thread whenever a card's state changes.</summary>
    public event Action? Changed;

    private static PlatformScope Scope => PlatformFilter.Current;

    /// <summary>The <c>platform</c> query value for a scope.</summary>
    public static string PlatformParameter(PlatformScope scope) => scope switch
    {
        PlatformScope.Mac => "macOS",
        PlatformScope.Windows => "Windows",
        _ => "all",
    };

    private CardState State(PlatformScope scope, string key)
    {
        if (!_cards.TryGetValue((scope, key), out var state)) _cards[(scope, key)] = state = new CardState();
        return state;
    }

    /// <summary>One card's section for the current scope, or null until it arrives.</summary>
    public JsonElement? Card(string key) => State(Scope, key).Section;

    public bool HasCard(string key) => State(Scope, key).Section is not null;

    /// <summary>Why the last read of a card failed, while it has nothing to show.</summary>
    public string? Error(string key) => State(Scope, key).Error;

    public bool IsLoading(string key) => State(Scope, key).Loading;

    /// <summary>A first load that has run past <see cref="SkeletonLimit"/>.</summary>
    public bool IsSlow(string key) =>
        State(Scope, key) is { Loading: true, StartedAt: { } at } && DateTime.UtcNow - at >= SkeletonLimit;

    /// <summary>True once any card has data, for the page's progress bar.</summary>
    public bool HasDocument => InsightCards.Keys.Any(HasCard);

    private static IReadOnlyList<string> CardKeys => PerCard ? InsightCards.AllKeys : InsightCards.Keys;

    // ── App Usage filters ────────────────────────────────────────────────

    private readonly SortedSet<string> _usages = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> AppUsageUsages => _usages;
    public IReadOnlySet<string> AppUsageCatalogs => _catalogs;

    /// <summary>Turn an App Usage usage or catalog chip on or off, and read the card again for it.</summary>
    public Task ToggleAppUsageFilter(bool catalog, string value)
    {
        var set = catalog ? _catalogs : _usages;
        if (!set.Remove(value)) set.Add(value);
        var scope = Scope;
        var state = State(scope, InsightCards.AppUsage);
        state.LoadedAt = null;
        return state.Loading ? Task.CompletedTask : LoadCardsAsync(scope, [InsightCards.AppUsage]);
    }

    private string CardPath(string key, PlatformScope scope, string? endpoint = null)
    {
        var platform = PlatformParameter(scope);
        if (key != InsightCards.AppUsage)
            return $"/api/v1/dashboard/insights/{Uri.EscapeDataString(endpoint ?? key)}?platform={platform}";
        var query = $"platform={platform}&days=30";
        if (_usages.Count > 0) query += "&usages=" + Uri.EscapeDataString(string.Join(',', _usages));
        if (_catalogs.Count > 0) query += "&catalogs=" + Uri.EscapeDataString(string.Join(',', _catalogs));
        return $"/api/v1/dashboard/insights/appUsage?{query}";
    }

    /// <summary>Redraw every card, after a card-local choice such as Directory Join's view.</summary>
    public void Redraw() => Changed?.Invoke();

    public Task LoadAsync(bool force = false)
    {
        var scope = Scope;
        if (_inFlight.TryGetValue(scope, out var running) && !running.IsCompleted) return running;
        var stale = CardKeys.Where(k => force || State(scope, k).LoadedAt is not { } at || DateTime.UtcNow - at >= Fresh).ToList();
        if (stale.Count == 0) return Task.CompletedTask;
        var task = PerCard ? LoadCardsAsync(scope, stale) : LoadDocumentAsync(scope, stale);
        _inFlight[scope] = task;
        return task;
    }

    /// <summary>Read a card again, for its Retry: that card alone when cards are read one by one.</summary>
    public Task RetryAsync(string key)
    {
        if (!PerCard) return LoadAsync(force: true);
        var scope = Scope;
        return State(scope, key).Loading ? Task.CompletedTask : LoadCardsAsync(scope, [key]);
    }

    private void Begin(PlatformScope scope, IEnumerable<string> keys)
    {
        var now = DateTime.UtcNow;
        foreach (var key in keys)
        {
            var state = State(scope, key);
            state.Loading = true;
            state.StartedAt = now;
        }
        Changed?.Invoke();
        // Redraw once the skeleton limit passes, so a card still waiting says so.
        _ = RemindAsync();
    }

    private async Task RemindAsync()
    {
        await Task.Delay(SkeletonLimit + TimeSpan.FromMilliseconds(100));
        Changed?.Invoke();
    }

    private void Finish(PlatformScope scope, string key, JsonElement? section, string? error)
    {
        var state = State(scope, key);
        state.Loading = false;
        if (section is { } s)
        {
            state.Section = s;
            state.LoadedAt = DateTime.UtcNow;
            state.Error = null;
        }
        else
        {
            // The last good section stays on screen: a failed refresh should not
            // blank a card that was showing something true five minutes ago.
            state.Error = error ?? "This card could not be loaded.";
        }
    }

    private async Task LoadDocumentAsync(PlatformScope scope, IReadOnlyList<string> keys)
    {
        Begin(scope, keys);
        JsonElement? document = null;
        var outcome = await ReportQueue.RunAsync(async ct =>
        {
            var r = await FleetApiClient.Instance.GetRawAsync(
                $"/api/v1/dashboard/insights?platform={PlatformParameter(scope)}", ct);
            if (!r.Ok) return Failure(r);
            using var doc = r.Data!;
            document = doc.RootElement.Clone();
            return FetchOutcome.Success;
        }, ReportQueue.DefaultTimeout);

        var cards = document is { } d ? Insights.Prop(d, "cards") : null;
        foreach (var key in keys)
            Finish(scope, key, cards is { } c
                    ? InsightCards.Endpoints(key).Select(e => Insights.Prop(c, e)).FirstOrDefault(x => x is not null)
                    : null,
                outcome.Ok ? "The server sent nothing for this card." : outcome.Error);
        _inFlight.Remove(scope);
        Changed?.Invoke();
    }

    private async Task LoadCardsAsync(PlatformScope scope, IReadOnlyList<string> keys)
    {
        Begin(scope, keys);
        var sections = new Dictionary<string, JsonElement>();
        await ReportQueue.RunAllAsync(keys, PerCardConcurrency,
            async (key, ct) =>
            {
                // A renamed card is asked for by its new name first, and by its old
                // one when the API answers 404 because it predates the rename.
                FleetApiClient.FleetResult<JsonDocument>? r = null;
                string endpoint = key;
                foreach (var candidate in InsightCards.Endpoints(key))
                {
                    endpoint = candidate;
                    r = await FleetApiClient.Instance.GetRawAsync(CardPath(key, scope, candidate), ct);
                    if (r.HttpStatus != 404) break;
                }
                if (!r!.Ok) return Failure(r);
                using var doc = r.Data!;
                sections[key] = InsightCards.SectionOf(doc.RootElement, endpoint).Clone();
                return FetchOutcome.Success;
            },
            (key, outcome) =>
            {
                // Each card paints as soon as its own read lands.
                Finish(scope, key, sections.TryGetValue(key, out var s) ? s : null, outcome.Error);
                Changed?.Invoke();
            });
        _inFlight.Remove(scope);
    }

    private static FetchOutcome Failure<T>(FleetApiClient.FleetResult<T> r) =>
        new(InsightCards.FailureText(r.HttpStatus, r.Detail), r.Transient && r.HttpStatus != 404);
}

/// <summary>Where on the dashboard a card sits.</summary>
public enum InsightPlacement
{
    /// <summary>Column 1, under Fleet Status: Installs, Management Tools Logs, Check-in Gaps.</summary>
    StatusColumn,

    /// <summary>The first insight column, under New Clients.</summary>
    ColumnA,

    /// <summary>The second insight column.</summary>
    ColumnB,
}

/// <summary>One insight card: its title, where its header goes, and its skeleton's shape.</summary>
/// <param name="Hero">Its headline figures are the dashboard's largest numbers.</param>
public sealed record InsightCard(
    string Key, string Title, string Subtitle, Accent Accent, string Glyph, DeepLink Header,
    InsightShape Shape = InsightShape.Figures,
    InsightPlacement Placement = InsightPlacement.ColumnA,
    bool Hero = false);

/// <summary>
/// The twelve insight cards of the shared spec's revision 2, each answering one
/// question. Every number, bar, list row and chip follows the link the server sent
/// with it, and every header opens its report unfiltered.
/// </summary>
public static class InsightWidgets
{
    private static string GlyphOf(string report) => ReportArea.ById(report)?.Glyph ?? "";

    private static DeepLink L(string section) => DeepLink.For(section);

    /// <summary>
    /// The cards, in Rod's order (the shared spec's revision 5). Column 1: Fleet
    /// Status, then Installs, Management Tools Logs and Check-in Gaps. First insight
    /// column: New Clients, Directory Services, Connectivity Problems, Protection Gaps,
    /// Expiring Certificates. Second: Update Debt, Storage, App Usage, Software Risk,
    /// Reboot Debt. Every card follows the toolbar's platform toggle and has none of its own.
    /// </summary>
    public static readonly IReadOnlyList<InsightCard> All =
    [
        new("installHealth", "Installs", "Munki and Cimian across the fleet", Accent.Emerald, GlyphOf("installs"), L("installs"),
            Placement: InsightPlacement.StatusColumn, Hero: true),
        // The Management report's own shield, in its yellow, as the nav and the report draw it.
        new("managementToolLogs", "Management Tools Logs", "Errors in the management tools' own logs", Accent.Yellow, GlyphOf("management"),
            InsightCards.ToolsLogsLink(), InsightShape.Bars, Placement: InsightPlacement.StatusColumn),
        new("checkinGaps", "Check-in Gaps", "Turned away, and gone quiet", Accent.Red, "\uE7BA",
            DeepLink.For("events", "failures", ("hours", "24")), Placement: InsightPlacement.StatusColumn),

        new("directoryServices", "Directory Services", "How devices are joined to a directory", Accent.Indigo, GlyphOf("identity"), L("identity"), InsightShape.Donut),
        new("userAccounts", "User Accounts", "Sign-in method and administrator accounts", Accent.Indigo, GlyphOf("identity"), L("identity"), InsightShape.Donut),
        new("connectivityProblems", "Connectivity Problems", "Weak Wi-Fi, poor networks and VPN", Accent.Teal, GlyphOf("network"), L("network")),
        new("protectionGaps", "Protection Gaps", "Devices missing a protection", Accent.Red, GlyphOf("security"), L("security"), InsightShape.Bars),
        new("expiringCertificates", "Expiring Certificates", "Expiring soon, and already expired", Accent.Yellow, "\uEB95", L("security")),

        new("updateDebt", "Update Debt", "Behind on patches, and updates waiting", Accent.Purple, GlyphOf("system"), L("system"), InsightShape.Donut,
            Placement: InsightPlacement.ColumnB),
        new("storage", "Storage", "Free space and capacity", Accent.Orange, GlyphOf("hardware"), L("hardware"), InsightShape.Bars,
            Placement: InsightPlacement.ColumnB),
        new(InsightCards.AppUsage, "App Usage", "The most-used apps", Accent.Blue, GlyphOf("applications"),
            DeepLink.For("applications", null, ("type", "usage"), ("period", "30")), InsightShape.Bars,
            Placement: InsightPlacement.ColumnB),
        new("softwareRisk", "Software Risk", "Vulnerabilities and items behind", Accent.Red, GlyphOf("security"), L("security"),
            Placement: InsightPlacement.ColumnB),
        new("rebootDebt", "Uptime Overview", "How long devices have run since their last restart", Accent.Orange, GlyphOf("system"), L("system"), InsightShape.Bars,
            Placement: InsightPlacement.ColumnB),
    ];

    /// <summary>The view of an older Directory Join card that matches the toolbar's platform toggle.</summary>
    public static string DirectoryView => PlatformFilter.Current switch
    {
        PlatformScope.Windows => "windows",
        PlatformScope.Mac => "mac",
        _ => "all",
    };

    /// <summary>The card in whatever state the shared model holds it.</summary>
    public static UIElement Build(InsightCard card)
    {
        var model = DashboardInsightsModel.Shared;
        UIElement body;
        if (!model.HasCard(card.Key))
        {
            // A skeleton for ten seconds at most: then the error if there is one, or
            // a plain "still loading" with Retry.
            body = model.Error(card.Key) is { } error && !model.IsLoading(card.Key) ? Failed(error, card.Key)
                : model.IsSlow(card.Key) ? Skeleton.StillLoading(() => _ = model.RetryAsync(card.Key))
                : Skeleton.Pulse(Skeleton.InsightBody(card.Shape));
        }
        else
        {
            var view = InsightCards.Build(card.Key, model.Card(card.Key), PlatformFilter.Current != PlatformScope.All, DirectoryView);
            body = view.Error is { } error ? Failed(error, card.Key) : Render(view, card.Hero);
        }
        body = WithControls(card, model, body);
        var header = card.Key == InsightCards.AppUsage && InsightCards.HeaderLink(model.Card(card.Key)) is { } served ? served : card.Header;

        var chevron = Ui.Icon("", 12, Ui.Brush("TextTertiaryBrush"));
        var block = Ui.StatBlock(card.Title, card.Subtitle, card.Glyph, card.Accent, body, chevron);
        return Linkable.LinkHeader(block, header);
    }

    /// <summary>
    /// The controls a card carries above its body: App Usage's usage and catalog chips.
    /// </summary>
    private static UIElement WithControls(InsightCard card, DashboardInsightsModel model, UIElement body)
    {
        if (card.Key == InsightCards.AppUsage)
        {
            var (usages, catalogs) = InsightCards.AppUsageFilters(model.Card(card.Key));
            if (usages.Count == 0 && catalogs.Count == 0) return body;
            var filters = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            void Row(string heading, IReadOnlyList<(string Value, int Devices)> options, IReadOnlySet<string> chosen, bool catalog)
            {
                if (options.Count == 0) return;
                filters.Children.Add(Eyebrow(heading, filters.Children.Count == 0 ? 0 : 8));
                var wrap = new WrapPanel();
                foreach (var (value, devices) in options)
                {
                    var chip = Ui.FilterChip(value, devices, Tone.Info, chosen.Contains(value));
                    chip.Margin = new Thickness(0, 0, 6, 6);
                    chip.Cursor = System.Windows.Input.Cursors.Hand;
                    chip.ToolTip = chosen.Contains(value) ? $"Stop filtering by {value}" : $"Show only {value}";
                    chip.MouseLeftButtonUp += (_, _) => _ = model.ToggleAppUsageFilter(catalog, value);
                    wrap.Children.Add(chip);
                }
                filters.Children.Add(wrap);
            }
            Row("USAGE", usages, model.AppUsageUsages, catalog: false);
            Row("CATALOG", catalogs, model.AppUsageCatalogs, catalog: true);
            return Stack(filters, body);
        }
        return body;

        static StackPanel Stack(UIElement top, UIElement rest)
        {
            var panel = new StackPanel();
            panel.Children.Add(top);
            panel.Children.Add(rest);
            return panel;
        }
    }

    // ── Rendering a card's parts ─────────────────────────────────────────

    private static UIElement Render(CardView view, bool hero = false)
    {
        var body = new StackPanel();
        var first = true;
        foreach (var part in view.Parts)
        {
            var element = part switch
            {
                FiguresPart f => Figures(f, hero && first),
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

    private static UIElement Figures(FiguresPart part, bool hero)
    {
        var cells = part.Items.Select(i => Figure(i, hero)).ToArray();
        return Ui.Columns(Math.Max(1, cells.Length), 10, cells);
    }

    private static UIElement Bars(BarsPart part, bool first)
    {
        var panel = new StackPanel();
        if (part.Heading is { } heading) panel.Children.Add(Eyebrow(heading, first ? 0 : 14));
        if (part.Tabular && part.Columns is { Count: 3 } titles) panel.Children.Add(TableHeader(titles, first));
        var max = Math.Max(1, part.Items.Count == 0 ? 1 : part.Items.Max(i => i.Count));
        foreach (var item in part.Items)
            panel.Children.Add(part.Tabular
                ? TableRow(item, item.Fraction ?? item.Count / max)
                : BarRow(item, item.Fraction ?? item.Count / max));
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
        return DonutWithLegend(part.Slices.Select(s => (s.Label, (int)s.Count, Swatch(s) ?? Paint(s.Severity), s.Link)).ToList(),
            part.Centre, part.CentreLabel);
    }

    /// <summary>An item's own colour, when the card fixes one to match its report.</summary>
    private static Brush? Swatch(Metric item)
    {
        if (item.Swatch is null) return null;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Swatch));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return null;
        }
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
        var track = Track(8, fraction, Swatch(item)
            ?? (item.Severity == Severity.Neutral ? Ui.Brush("IconBlueForeground") : Ui.StatusBrush(ToneOf(item.Severity))));
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

    // The tabular rows' columns: the name takes the rest; the bar, value and detail are fixed.
    private const double TableBar = 60, TableValue = 64, TableDetail = 56;

    private static Grid TableGrid()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableBar) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableValue) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableDetail) });
        return row;
    }

    /// <summary>The header row over a tabular list: name, then value and detail over their columns.</summary>
    private static UIElement TableHeader(IReadOnlyList<string> titles, bool first)
    {
        var row = TableGrid();
        row.Margin = new Thickness(6, first ? 0 : 14, 6, 4);
        void Title(string text, int column, bool right)
        {
            var t = Ui.Eyebrow(text.ToUpperInvariant());
            if (right) t.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(t, column);
            row.Children.Add(t);
        }
        Title(titles[0], 0, false);
        Title(titles[1], 2, true);
        Title(titles[2], 3, true);
        return row;
    }

    /// <summary>
    /// A row on a strict grid: name (the rest, truncated) | 60 px bar | value | detail,
    /// the value and detail right-aligned, so the columns line up down the list --
    /// App Usage's apps, hours and devices.
    /// </summary>
    private static UIElement TableRow(Metric item, double fraction)
    {
        var row = TableGrid();
        var name = Ui.Text(item.Label);
        name.FontSize = 13;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0, 0, 8, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        var track = Track(8, fraction, Ui.Brush("IconBlueForeground"));
        Grid.SetColumn(track, 1);
        row.Children.Add(track);
        var value = Ui.Text(item.Shown);
        value.FontSize = 13;
        value.FontWeight = FontWeights.Medium;
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        var detail = Ui.Caption(item.Detail ?? "");
        detail.TextAlignment = TextAlignment.Right;
        detail.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(detail, 3);
        row.Children.Add(detail);
        row.ToolTip = item.Tip ?? item.Label;
        return item.Link is { } link ? Row(row, link, item.Tip) : row;
    }

    /// <summary>A large number over its caption, linked when the server gave it a link.</summary>
    /// <param name="hero">
    /// Drawn larger than Fleet Status's own count: Install Health's headline figures
    /// are the numbers the dashboard should be read for first.
    /// </param>
    private static UIElement Figure(Metric item, bool hero = false)
    {
        var panel = new StackPanel();
        var value = item.Shown;
        var size = hero ? 44 : 24;
        var n = item.Count == 0 || item.Severity == Severity.Neutral
            ? Ui.Text(value, "StatValueStyle", Ui.Brush(item.Count == 0 ? "TextSecondaryBrush" : "TextPrimaryBrush"))
            : Ui.Status(value, ToneOf(item.Severity), size, FontWeights.SemiBold);
        n.FontSize = size;
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
    private static UIElement Failed(string error, string key)
    {
        var panel = new StackPanel();
        var t = Ui.Caption(error);
        t.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(t);
        var retry = Ui.IconButton("", "Retry", (_, _) => _ = DashboardInsightsModel.Shared.RetryAsync(key));
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        retry.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(retry);
        return panel;
    }
}
