using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The reports behind the dashboard's insight cards. The dashboard payload carries
/// devices, events and install counters only, so these load after it and each card
/// fills in as the reads it needs answer.
/// </summary>
/// <remarks>
/// One shared copy rather than one per page: the platform toggle and every
/// navigation rebuild the dashboard page, and re-reading a dozen fleet reports each
/// time would cost minutes of API time to redraw numbers that have not changed.
/// Each read is shared by every card that needs it -- one <c>/security</c> read feeds
/// Security Posture, Software Risk and Certificates. Rows are kept unfiltered and the
/// platform scope is applied when a card is drawn, so a toggle redraws from what is
/// already here.
/// </remarks>
public sealed class DashboardInsightsModel
{
    public static DashboardInsightsModel Shared { get; } = new();

    /// <summary>
    /// The reads, in the order they are requested. The managed-items read is last:
    /// it is 130,000 rows across a couple of dozen pages, it sometimes answers 502,
    /// and the two cards that use it show their counts without it.
    /// </summary>
    public static readonly IReadOnlyList<string> Sources =
    [
        "security", "management", "hardware", "system",
        "installs-filters", "failures", "identity", "network",
        "peripherals", "certificates", "app-usage", "installs",
    ];

    /// <summary>The bulk module reports, read as rows: <c>/api/v1/&lt;module&gt;</c>.</summary>
    private static readonly HashSet<string> Modules = ["security", "management", "hardware", "system", "identity", "network", "peripherals"];

    // Four minutes, not five: the page's own timer fires every five, and a load
    // stamped when it finished would otherwise still look fresh at the next tick and
    // skip a refresh.
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(4);

    private readonly Dictionary<string, List<JsonElement>> _rows = new();
    private readonly Dictionary<string, JsonElement> _documents = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly HashSet<string> _loading = new();
    private List<Insights.InstallItem>? _installs;
    private DateTime? _loadedAt;
    private Task? _inFlight;

    /// <summary>Raised on the UI thread with the read's id when its state changes.</summary>
    public event Action<string>? Changed;

    /// <summary>The dashboard payload the page loaded, for the cards that count from it.</summary>
    public DashboardPayload? Dashboard { get; private set; }

    public void SetDashboard(DashboardPayload payload)
    {
        Dashboard = payload;
        Changed?.Invoke("dashboard");
    }

    /// <summary>A report's rows, narrowed to the toolbar's platform scope.</summary>
    public IReadOnlyList<JsonElement> Rows(string source) =>
        _rows.TryGetValue(source, out var rows)
            ? PlatformFilter.Current == PlatformScope.All ? rows : rows.Where(r => PlatformFilter.Includes(Insights.Platform(r))).ToList()
            : [];

    public bool Has(string source) => _rows.ContainsKey(source) || _documents.ContainsKey(source)
        || (source == "installs" && _installs is not null);

    public JsonElement? Document(string source) => _documents.TryGetValue(source, out var d) ? d : null;

    /// <summary>Managed items, reduced to what the cards count; null until (or unless) they load.</summary>
    public IReadOnlyList<Insights.InstallItem>? Installs =>
        _installs is null ? null
        : PlatformFilter.Current == PlatformScope.All ? _installs
        : _installs.Where(i => PlatformFilter.Includes(i.Platform)).ToList();

    public string? Error(string source) => _errors.GetValueOrDefault(source);

    public bool IsLoading(string source) => _loading.Contains(source);

    public Task LoadAsync(bool force = false)
    {
        if (_inFlight is { IsCompleted: false }) return _inFlight;
        if (!force && _loadedAt is { } at && DateTime.UtcNow - at < Fresh) return Task.CompletedTask;
        return _inFlight = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        foreach (var source in Sources)
        {
            _loading.Add(source);
            Changed?.Invoke(source);
        }

        // Two at a time: each bulk report takes the API ten seconds or more, and
        // four at once had the gateway answer 503 for the last of them.
        var queue = new Queue<string>(Sources);
        var running = new List<Task<(string Source, string? Error)>>();
        while (running.Count < 2 && queue.Count > 0) running.Add(FetchAsync(queue.Dequeue()));

        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running);
            running.Remove(done);
            if (queue.Count > 0) running.Add(FetchAsync(queue.Dequeue()));

            var (source, error) = await done;
            // Data from the last good read stays: a failed refresh should not blank
            // a card that was showing something true five minutes ago.
            if (error is null) _errors.Remove(source);
            else _errors[source] = error;
            _loading.Remove(source);
            Changed?.Invoke(source);
        }
        _loadedAt = DateTime.UtcNow;
    }

    /// <summary>One read, retried once after a short pause on a 5xx or a dropped connection.</summary>
    private async Task<(string, string?)> FetchAsync(string source)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (ok, transient, detail) = await FetchOnceAsync(source);
            if (ok) return (source, null);
            if (!transient || attempt > 0) return (source, detail ?? "This could not be loaded.");
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private async Task<(bool Ok, bool Transient, string? Detail)> FetchOnceAsync(string source)
    {
        var api = FleetApiClient.Instance;
        if (Modules.Contains(source))
        {
            var r = await api.GetModuleAsync(source);
            if (r.Ok) _rows[source] = r.Data!;
            return (r.Ok, r.Transient, r.Detail);
        }

        switch (source)
        {
            case "certificates":
            {
                var r = await api.GetTypedAsync<List<JsonElement>>("/api/v1/security/certificates?status=expiring&limit=10000");
                if (r.Ok) _rows[source] = r.Data!;
                return (r.Ok, r.Transient, r.Detail);
            }
            case "installs":
            {
                // Followed page by page, as the installs report does: one request
                // returns the first fifty devices' items and nothing else.
                var r = await api.GetAllPagesAsync("installs", 5000);
                if (r.Ok)
                {
                    var rows = r.Data!;
                    _installs = await Task.Run(() => rows.Select(Insights.ToInstallItem).OfType<Insights.InstallItem>().ToList());
                }
                return (r.Ok, r.Transient, r.Detail);
            }
            default:
            {
                var path = source switch
                {
                    "installs-filters" => "/api/v1/installs/filters",
                    "failures" => "/api/v1/events/failures?hours=24&limit=1",
                    _ => "/api/v1/applications/usage?days=30",
                };
                var r = await api.GetRawAsync(path);
                if (r.Ok)
                    using (var doc = r.Data!)
                        _documents[source] = doc.RootElement.Clone();
                return (r.Ok, r.Transient, r.Detail);
            }
        }
    }
}

/// <summary>One insight card: what it is called, what it reads, and how it draws.</summary>
/// <param name="Primary">
/// The read the card cannot draw without. Null for a card that always has something
/// to show, because the dashboard payload carries part of it.
/// </param>
public sealed record InsightCard(
    string Id, string Title, string Subtitle, Accent Accent, string Glyph, DeepLink Header,
    string? Primary, IReadOnlyList<string> Sources, Func<DashboardInsightsModel, UIElement> Body);

/// <summary>
/// The twelve insight cards, in the order the shared spec lists them. Every number,
/// bar, legend row and chip opens the report that owns it with that filter applied,
/// and every header opens the report unfiltered.
/// </summary>
public static class InsightWidgets
{
    private static string GlyphOf(string report) => ReportArea.ById(report)?.Glyph ?? "";

    private static DeepLink L(string section, params (string, string?)[] filters) => DeepLink.For(section, null, filters);

    public static readonly IReadOnlyList<InsightCard> All =
    [
        new("system", "System Health", "Uptime and pending updates", Accent.Orange, GlyphOf("system"),
            L("system"), "system", ["system"], m => SystemBody(m.Rows("system"))),
        new("security", "Security Posture", "Fleet-wide protections in place", Accent.Green, GlyphOf("security"),
            L("security"), "security", ["security"], m => SecurityBody(m.Rows("security"))),
        new("management", "Management", "MDM enrollment across the fleet", Accent.Purple, GlyphOf("management"),
            L("management"), "management", ["management"], m => ManagementBody(m.Rows("management"))),
        new("hardware", "Hardware", "Architecture, memory and storage", Accent.Teal, GlyphOf("hardware"),
            L("hardware"), "hardware", ["hardware"], m => HardwareBody(m.Rows("hardware"))),
        new("install-health", "Install Health", "Munki and Cimian across the fleet", Accent.Emerald, GlyphOf("installs"),
            L("installs"), null, ["dashboard", "installs-filters", "installs"], InstallHealthBody),
        new("check-ins", "Check-in Gaps", "Turned away, and gone quiet", Accent.Red, "",
            DeepLink.For("events", "failures", ("hours", "24")), null, ["dashboard", "failures"], CheckInsBody),
        new("os-currency", "OS Currency", "On the latest patch for their version", Accent.Purple, GlyphOf("system"),
            L("system"), "system", ["system"], m => OsCurrencyBody(m.Rows("system"))),
        new("software-risk", "Software Risk", "Vulnerabilities and items behind", Accent.Red, GlyphOf("security"),
            L("security"), "security", ["security", "installs"], SoftwareRiskBody),
        new("certificates", "Certificates", "Expiring within 30 days", Accent.Yellow, "",
            L("security"), "certificates", ["certificates", "security"], CertificatesBody),
        new("identity", "Identity", "Directory join, admins and usage", Accent.Indigo, GlyphOf("identity"),
            L("identity"), "identity", ["identity"], m => IdentityBody(m.Rows("identity"))),
        new("app-usage", "App Usage", "The last 30 days", Accent.Blue, GlyphOf("applications"),
            L("applications"), "app-usage", ["app-usage"], AppUsageBody),
        new("network", "Network & Peripherals", "Connections, printers and displays", Accent.Teal, GlyphOf("network"),
            L("network"), "network", ["network", "peripherals", "hardware"], NetworkBody),
    ];

    /// <summary>The card in whatever state the shared model holds its reads.</summary>
    public static UIElement Build(InsightCard card)
    {
        var model = DashboardInsightsModel.Shared;
        UIElement body;
        if (card.Primary is { } primary && !model.Has(primary))
        {
            var error = model.Error(primary);
            body = error is not null ? Message(error)
                : model.IsLoading(primary) ? Loading()
                : Message("No data");
        }
        else
        {
            try { body = card.Body(model); }
            catch (Exception ex) { body = Message($"This card could not be drawn: {ex.Message}"); }
        }

        var chevron = Ui.Icon("", 12, Ui.Brush("TextTertiaryBrush"));
        var block = Ui.StatBlock(card.Title, card.Subtitle, card.Glyph, card.Accent, body, chevron);
        return Linkable.LinkHeader(block, card.Header);
    }

    // ── 1 System Health ──────────────────────────────────────────────────

    private static UIElement SystemBody(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) return Message("No data");
        var s = Insights.SystemOverview(rows);
        var max = Math.Max(1, s.Uptime.Max(b => b.Count));

        var body = new StackPanel();
        body.Children.Add(Eyebrow("UPTIME", top: 0));
        for (var i = 0; i < s.Uptime.Count; i++)
        {
            var (label, count) = s.Uptime[i];
            body.Children.Add(BarRow(label, count, (double)count / max, Ui.StatusBrush(Tone.Orange),
                L("system", ("uptime", Insights.UptimeKeys[i])), labelWidth: 84));
        }
        body.Children.Add(Chips(
            Chip(s.Pending, "with pending updates", Tone.Warning, L("system", ("pending", Insights.AnyPending))),
            Chip(s.Deferred, "with deferred updates", Tone.Orange, L("system", ("deferred", "1")))));
        return body;
    }

    // ── 2 Security Posture ───────────────────────────────────────────────

    private static UIElement SecurityBody(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) return Message("No data");
        var posture = Insights.Security(rows);
        var body = new StackPanel();
        // A protection row opens the devices missing it: that is the list to act on.
        foreach (var row in posture.Rows)
            body.Children.Add(Linkable.Wrap(ComplianceRow(row), L("security", (row.Key, "off")),
                $"Open the security report, filtered to devices without {row.Label.ToLowerInvariant()}"));
        body.Children.Add(Chips(
            Chip(posture.WithThreats, "with threats", Tone.Error, L("security", ("threats", "1"))),
            Chip(posture.ExpiredCerts, "expired certs", Tone.Warning, L("security", ("certs", "expired"))),
            Chip(posture.CriticalCves, "critical CVEs", Tone.Orange, L("security", ("cve", "critical")))));
        return body;
    }

    // ── 3 Management ─────────────────────────────────────────────────────

    private static UIElement ManagementBody(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) return Message("No data");
        var m = Insights.ManagementOverview(rows);
        var percent = m.Total == 0 ? 0 : (int)Math.Round((double)m.Enrolled / m.Total * 100);

        var body = new StackPanel();
        body.Children.Add(DonutWithLegend(
            [
                ("Enrolled", m.Enrolled, Ui.StatusBrush(Tone.Success), L("management", ("enrolled", "1"))),
                ("Not Enrolled", m.Total - m.Enrolled, Ui.Brush("SubtleFillHoverBrush"), L("management", ("enrolled", "0"))),
            ], $"{percent}%", "enrolled"));

        if (m.TopProviders.Count > 0)
        {
            body.Children.Add(Eyebrow("PROVIDERS"));
            var max = Math.Max(1, m.TopProviders[0].Count);
            foreach (var (name, count) in m.TopProviders)
                body.Children.Add(ProviderRow(name, count, max, L("management", ("provider", name))));
        }
        return body;
    }

    private static UIElement ProviderRow(string name, int count, int max, DeepLink link)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var label = Ui.Text(name);
        label.FontSize = 13;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
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
        return Row(row, link);
    }

    // ── 4 Hardware ───────────────────────────────────────────────────────

    private static UIElement HardwareBody(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) return Message("No data");
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
            h.Families.Select(f => (f.Family, f.Count, FamilyBrush(f.Family),
                f.Family == "Unknown" ? L("hardware") : L("hardware", ("arch", f.Family)))).ToList(),
            h.Total.ToString("N0"), "devices"));
        var chips = Chips(
            Chip(h.LowStorage, "under 10% free", Tone.Error, L("hardware", ("storage", "low"))),
            Chip(h.LowMemory, "8 GB or less", Tone.Warning, L("hardware", ("memory", "le8"))),
            Chip(h.Laptops, "laptops", Tone.Info, L("hardware", ("type", "laptop"))));
        chips.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(chips);
        return body;
    }

    // ── 5 Install Health ─────────────────────────────────────────────────

    private static UIElement InstallHealthBody(DashboardInsightsModel m)
    {
        var body = new StackPanel();
        var stats = m.Dashboard?.InstallStats;
        var (errors, warnings) = stats is null ? (0, 0) : PlatformFilter.Current switch
        {
            PlatformScope.Windows => (stats.WinDevicesWithErrors, stats.WinDevicesWithWarnings),
            PlatformScope.Mac => (stats.MacDevicesWithErrors, stats.MacDevicesWithWarnings),
            _ => (stats.DevicesWithErrors, stats.DevicesWithWarnings),
        };
        body.Children.Add(Ui.Columns(2, 10,
            Figure(errors, "devices with errors", Tone.Error, L("installs", ("filter", "errors"))),
            Figure(warnings, "devices with warnings", Tone.Warning, L("installs", ("filter", "warnings")))));

        body.Children.Add(Eyebrow("TOP FAILING ITEMS"));
        if (m.Installs is { } items)
        {
            var top = Insights.TopFailing(items);
            if (top.Count == 0) body.Children.Add(Message("No item is failing."));
            var max = Math.Max(1, top.Count > 0 ? top[0].Devices : 1);
            foreach (var (name, devices) in top)
                body.Children.Add(BarRow(name, devices, (double)devices / max, Ui.StatusBrush(Tone.Error),
                    L("installs", ("item", name), ("filter", "errors"))));
        }
        else body.Children.Add(Pending(m, "installs", "The item list did not load, so only the counts are shown."));

        if (m.Document("installs-filters") is { } filters)
        {
            var stale = Insights.StaleAgents(filters, PlatformFilter.Includes);
            var chips = Chips(Chip(stale.Count, $"agents silent over {stale.ThresholdDays} days", Tone.Warning,
                L("installs", ("stale", "1"))));
            chips.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(chips);
        }
        else if (m.Error("installs-filters") is null && m.IsLoading("installs-filters"))
            body.Children.Add(Loading());
        return body;
    }

    // ── 6 Check-in Gaps ──────────────────────────────────────────────────

    private static UIElement CheckInsBody(DashboardInsightsModel m)
    {
        var body = new StackPanel();
        if (m.Document("failures") is { } failures)
        {
            var c = Insights.CheckIns(failures);
            DeepLink Outcome(string o) => DeepLink.For("events", "failures", ("hours", "24"), ("outcome", o));
            body.Children.Add(Ui.Columns(3, 8,
                Figure(c.Rejected, "rejected", Tone.Error, Outcome("rejected")),
                Figure(c.Retried, "retried", Tone.Neutral, Outcome("retried")),
                Figure(c.Accepted, "repaired", Tone.Info, Outcome("accepted"))));
            var caption = Ui.Caption(PlatformFilter.Current == PlatformScope.All
                ? "In the last 24 hours"
                : "In the last 24 hours · All platforms: check-ins are not split by platform");
            caption.TextWrapping = TextWrapping.Wrap;
            caption.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(caption);

            if (c.Reasons.Count > 0)
            {
                body.Children.Add(Eyebrow("REASONS"));
                var max = Math.Max(1, c.Reasons[0].Count);
                foreach (var (reason, count, _) in c.Reasons.Take(5))
                    body.Children.Add(BarRow(FailureReason.Label(reason), count, (double)count / max, Ui.StatusBrush(Tone.Error),
                        DeepLink.For("events", "failures", ("hours", "24"), ("reason", reason))));
            }
        }
        else body.Children.Add(Pending(m, "failures", "Failed check-ins could not be loaded."));

        if (m.Dashboard is { } dashboard)
        {
            var devices = PlatformFilter.Apply(dashboard.Devices);
            var stale = devices.Count(d => DeviceStatus.Calculate(d) == DeviceLiveness.Stale);
            var missing = devices.Count(d => DeviceStatus.Calculate(d) == DeviceLiveness.Missing);
            body.Children.Add(Eyebrow("SILENT DEVICES"));
            body.Children.Add(Chips(
                Chip(stale, "silent 1–7 days", Tone.Warning, L("devices", ("status", "stale"))),
                Chip(missing, "silent over 7 days", Tone.Error, L("devices", ("status", "missing")))));
        }
        return body;
    }

    // ── 7 OS Currency ────────────────────────────────────────────────────

    private static UIElement OsCurrencyBody(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) return Message("No data");
        var c = Insights.OsCurrency(rows);
        var percent = (int)Math.Round(c.LatestFraction * 100);
        var body = new StackPanel();
        body.Children.Add(DonutWithLegend(
            [
                ("On latest patch", c.OnLatest, Ui.StatusBrush(Tone.Success), L("system", ("patch", "latest"))),
                ("Behind", c.Behind, Ui.StatusBrush(Tone.Warning), L("system", ("patch", "behind"))),
            ], $"{percent}%", "latest"));
        body.Children.Add(Chips(
            Chip(c.UnsupportedMac, "unsupported macOS", Tone.Error, L("system", ("os", "unsupported"))),
            Chip(c.Windows10, "Windows 10", Tone.Warning, L("system", ("os", "windows10")))));
        if (c.Lagging.Count > 0)
        {
            body.Children.Add(Eyebrow("MOST BEHIND"));
            var max = Math.Max(1, c.Lagging[0].Count);
            foreach (var (version, count) in c.Lagging)
                body.Children.Add(BarRow(version, count, (double)count / max, Ui.StatusBrush(Tone.Warning),
                    L("system", ("osVersion", version))));
        }
        return body;
    }

    // ── 8 Software Risk ──────────────────────────────────────────────────

    private static UIElement SoftwareRiskBody(DashboardInsightsModel m)
    {
        var risk = Insights.SoftwareRisk(m.Rows("security"));
        var body = new StackPanel();
        body.Children.Add(Ui.Columns(2, 10,
            Figure(risk.CriticalDevices, "devices with critical CVEs", Tone.Error, L("security", ("cve", "critical"))),
            Figure(risk.ExploitedDevices, "with actively exploited CVEs", Tone.Orange, L("security", ("cve", "exploited")))));

        body.Children.Add(Eyebrow("MOST DEVICES BEHIND"));
        if (m.Installs is { } items)
        {
            var top = Insights.TopBehind(items);
            if (top.Count == 0) body.Children.Add(Message("Every managed item is on its latest version."));
            var max = Math.Max(1, top.Count > 0 ? top[0].Devices : 1);
            foreach (var (name, devices) in top)
                body.Children.Add(BarRow(name, devices, (double)devices / max, Ui.StatusBrush(Tone.Orange),
                    L("installs", ("item", name))));
        }
        else body.Children.Add(Pending(m, "installs", "The item list did not load, so only the CVE counts are shown."));
        return body;
    }

    // ── 9 Certificates ───────────────────────────────────────────────────

    private static UIElement CertificatesBody(DashboardInsightsModel m)
    {
        var c = Insights.Certificates(m.Rows("certificates"), m.Rows("security"));
        var body = new StackPanel();
        body.Children.Add(Ui.Columns(2, 10,
            Figure(c.ExpiringCerts, $"certificates on {c.ExpiringDevices:N0} devices", Tone.Warning,
                L("security", ("certStatus", "expiring"))),
            Figure(c.ExpiredDevices, "devices with expired user certs", Tone.Error, L("security", ("certs", "expired")))));

        if (c.Soonest.Count > 0)
        {
            body.Children.Add(Eyebrow("EXPIRING SOONEST"));
            foreach (var (name, days, count) in c.Soonest)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = Ui.Text(name);
                label.FontSize = 13;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(label);
                var when = Ui.Caption(days <= 0 ? $"today · {count:N0}" : $"{days:N0}d · {count:N0}");
                when.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(when, 1);
                row.Children.Add(when);
                body.Children.Add(Row(row, L("security", ("certSearch", name), ("certStatus", "expiring"))));
            }
        }
        return body;
    }

    // ── 10 Identity ──────────────────────────────────────────────────────

    private static UIElement IdentityBody(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0) return Message("No data");
        var s = Insights.Identity(rows);
        Brush JoinBrush(string key) => key switch
        {
            "entra" => Ui.Brush("IconBlueForeground"),
            "hybrid" => Ui.Brush("IconIndigoForeground"),
            "domain" => Ui.Brush("IconPurpleForeground"),
            _ => Ui.Brush("SubtleFillHoverBrush"),
        };
        var body = new StackPanel();
        body.Children.Add(DonutWithLegend(
            s.Joins.Select(j => (j.Label, j.Count, JoinBrush(j.Key), L("identity", ("join", j.Key)))).ToList(),
            s.Total.ToString("N0"), "devices"));
        var chips = Chips(
            Chip(s.ExtraAdmins, "with extra local admins", Tone.Warning, L("identity", ("admins", "extra"))),
            Chip(s.Shared, "shared", Tone.Info, L("identity", ("usage", "shared"))),
            Chip(s.Assigned, "assigned", Tone.Info, L("identity", ("usage", "assigned"))));
        chips.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(chips);
        return body;
    }

    // ── 11 App Usage ─────────────────────────────────────────────────────

    private static UIElement AppUsageBody(DashboardInsightsModel m)
    {
        var (most, least) = Insights.AppUsage(m.Document("app-usage")!.Value);
        var body = new StackPanel();
        if (most.Count == 0) return Message("No usage has been reported in the last 30 days.");

        static DeepLink Usage(string app) => DeepLink.For("applications", "usage/" + app, ("days", "30"));

        body.Children.Add(Eyebrow("MOST USED", top: 0));
        var max = Math.Max(1, most[0].Hours);
        foreach (var app in most)
            body.Children.Add(BarRow(app.Name, app.Hours, app.Hours / max, Ui.Brush("IconBlueForeground"), Usage(app.Name),
                value: $"{app.Hours:N0} h"));

        if (least.Count > 0)
        {
            body.Children.Add(Eyebrow("LEAST USED, ON 10+ DEVICES"));
            foreach (var app in least)
                body.Children.Add(BarRow(app.Name, app.ActiveDevices, app.ActiveShare, Ui.StatusBrush(Tone.Warning), Usage(app.Name),
                    value: $"{app.ActiveDevices:N0} of {app.InstalledDevices:N0}"));
        }
        if (PlatformFilter.Current != PlatformScope.All)
        {
            var caption = Ui.Caption("All platforms: usage is not split by platform.");
            caption.TextWrapping = TextWrapping.Wrap;
            caption.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(caption);
        }
        return body;
    }

    // ── 12 Network & Peripherals ─────────────────────────────────────────

    private static UIElement NetworkBody(DashboardInsightsModel m)
    {
        var network = m.Rows("network");
        var s = Insights.NetworkAndPeripherals(network, m.Rows("peripherals"), m.Rows("hardware"));
        var body = new StackPanel();
        body.Children.Add(DonutWithLegend(
            [
                ("Wired", s.Wired, Ui.Brush("IconTealForeground"), L("network", ("connection", "wired"))),
                ("Wi-Fi", s.Wifi, Ui.Brush("IconBlueForeground"), L("network", ("connection", "wifi"))),
            ], (s.Wired + s.Wifi).ToString("N0"), "connected"));
        body.Children.Add(Chips(
            Chip(s.Weak, "on a weak Wi-Fi signal", Tone.Warning, L("network", ("signal", "weak")))));

        body.Children.Add(Eyebrow("PERIPHERALS"));
        if (m.Has("peripherals"))
        {
            body.Children.Add(Ui.Columns(2, 10,
                Figure(s.Printers, "printers", Tone.Neutral, L("peripherals", ("kind", "printers"))),
                Figure(s.Displays, "displays", Tone.Neutral, L("peripherals", ("kind", "displays")))));
            if (s.Rooms.Count > 0)
            {
                var max = Math.Max(1, s.Rooms[0].Printers);
                foreach (var (room, printers) in s.Rooms)
                    body.Children.Add(BarRow(room, printers, (double)printers / max, Ui.Brush("IconPinkForeground"),
                        L("peripherals", ("kind", "printers"), ("room", room))));
            }
        }
        else body.Children.Add(Pending(m, "peripherals", "Peripherals could not be loaded."));
        return body;
    }

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
    private static UIElement BarRow(string label, double count, double fraction, Brush brush, DeepLink link,
        double labelWidth = 0, string? value = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(labelWidth > 0
            ? new ColumnDefinition { Width = new GridLength(labelWidth) }
            : new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 44 });
        var name = Ui.Text(label);
        name.FontSize = 13;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = label;
        name.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(name);
        var track = Track(8, fraction, brush);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);
        var n = Ui.Text(value ?? count.ToString("N0"));
        n.FontSize = 13;
        n.FontWeight = FontWeights.Medium;
        n.TextAlignment = TextAlignment.Right;
        n.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(n, 2);
        row.Children.Add(n);
        return Row(row, link);
    }

    /// <summary>A large linked number over its caption.</summary>
    private static UIElement Figure(int value, string caption, Tone tone, DeepLink link)
    {
        var panel = new StackPanel();
        var n = value == 0
            ? Ui.Text(value.ToString("N0"), "StatValueStyle", Ui.Brush("TextSecondaryBrush"))
            : tone == Tone.Neutral ? Ui.Text(value.ToString("N0")) : Ui.Status(value.ToString("N0"), tone, 24, FontWeights.SemiBold);
        n.FontSize = 24;
        n.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(n);
        var c = Ui.Caption(caption);
        c.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(c);
        var wrap = Linkable.Wrap(panel, link);
        wrap.Padding = new Thickness(6, 4, 6, 6);
        wrap.Margin = new Thickness(-6, 0, 0, 0);
        return wrap;
    }

    private static TextBlock Eyebrow(string text, double top = 14)
    {
        var e = Ui.Eyebrow(text);
        e.Margin = new Thickness(0, top, 0, 6);
        return e;
    }

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

        var panel = new StackPanel { Margin = new Thickness(6, 3, 6, 6) };
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

    private static UIElement DonutWithLegend(IReadOnlyList<(string Label, int Count, Brush Brush, DeepLink Link)> data, string centre, string centreLabel)
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
            var n = Number(count);
            Grid.SetColumn(n, 2);
            row.Children.Add(n);
            var share = Ui.Caption(total == 0 ? "" : $"{(double)count / total:P0}");
            share.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(share, 3);
            row.Children.Add(share);
            var wrap = Linkable.Wrap(row, link);
            wrap.Padding = new Thickness(6, 2, 6, 2);
            wrap.Margin = new Thickness(0, 0, 0, 2);
            legend.Children.Add(wrap);
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
        wrap.Margin = new Thickness(0, 6, 0, 0);
        return wrap;
    }

    /// <summary>A small linked "n label" chip; a zero reads in the secondary colour, not the alarm one.</summary>
    private static UIElement Chip(int value, string label, Tone tone, DeepLink link)
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
        var chip = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("SubtleFillBrush"),
            BorderBrush = Ui.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
        };
        Linkable.Attach(chip, link);
        return chip;
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

    /// <summary>An optional read's state where its part of a card would be.</summary>
    private static UIElement Pending(DashboardInsightsModel m, string source, string failed) =>
        m.IsLoading(source) && m.Error(source) is null ? Loading() : Message(failed);

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
}
