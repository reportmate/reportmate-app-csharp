using System.Globalization;
using System.Text.Json;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>How a figure on a card reads: alarming, worth watching, or neither.</summary>
public enum Severity { Neutral, Good, Warning, Bad, Info }

/// <summary>
/// One clickable thing on an insight card: a number, a bar, a list row or a chip.
/// </summary>
/// <param name="Count">What a bar's length is measured by, and the figure when Value is null.</param>
/// <param name="Value">The text shown for the figure, when it is not just the count.</param>
/// <param name="Fraction">A bar's length as a share, when it is not count over the longest bar.</param>
/// <param name="Platform">A platform whose logo goes before the label.</param>
public sealed record Metric(
    string Label, double Count, DeepLink? Link,
    Severity Severity = Severity.Neutral, string? Value = null, string? Detail = null,
    double? Fraction = null, string? Platform = null)
{
    public string Shown => Value ?? Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A piece of a card, in the order it is drawn.</summary>
public abstract record CardPart;

/// <summary>Large numbers side by side.</summary>
public sealed record FiguresPart(IReadOnlyList<Metric> Items) : CardPart;

/// <summary>Labelled bars under an optional heading.</summary>
public sealed record BarsPart(string? Heading, IReadOnlyList<Metric> Items) : CardPart;

/// <summary>Small "n label" chips.</summary>
public sealed record ChipsPart(IReadOnlyList<Metric> Items) : CardPart;

/// <summary>A donut with a centre figure and a legend of its slices.</summary>
public sealed record DonutPart(string Centre, string CentreLabel, IReadOnlyList<Metric> Slices) : CardPart;

/// <summary>Rows of a name and a detail, such as devices or certificates.</summary>
public sealed record ListPart(string? Heading, IReadOnlyList<Metric> Items) : CardPart;

/// <summary>A line of explanation.</summary>
public sealed record NotePart(string Text) : CardPart;

/// <summary>What one card draws, or why it cannot.</summary>
public sealed record CardView(IReadOnlyList<CardPart> Parts, string? Error = null);

/// <summary>
/// The twelve insight cards as the shared spec's revision 2 defines them, read from
/// the cached <c>GET /api/v1/dashboard/insights</c> document. The server computes
/// every number and every link; this turns a card's section into the parts a card
/// draws, so the reading can be tested without a window.
/// </summary>
public static class InsightCards
{
    /// <summary>The card keys in the document, in display order.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "rebootDebt", "protectionGaps", "unmanagedDevices", "hardwareAtRisk",
        "installHealth", "checkinGaps", "osCurrency", "softwareRisk",
        "expiringCertificates", "localAdminExposure", "unusedSoftware", "connectivityProblems",
    ];

    // ── Links ────────────────────────────────────────────────────────────

    /// <summary>
    /// The app link for a server <c>link: {path, params}</c>. A device link opens the
    /// device list narrowed to that serial, because the device page shows this
    /// machine only.
    /// </summary>
    public static DeepLink? ToDeepLink(JsonElement? node)
    {
        if (node is not { ValueKind: JsonValueKind.Object } link) return null;
        var path = Insights.Str(link, "path").Trim('/');
        if (path.Length == 0) return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        var parameters = new List<(string, string?)>();
        if (Insights.Prop(link, "params") is { ValueKind: JsonValueKind.Object } p)
            foreach (var kv in p.EnumerateObject())
                parameters.Add((kv.Name, kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.ToString()));

        if (segments[0].Equals("device", StringComparison.OrdinalIgnoreCase) && segments.Count > 1)
            return DeepLink.For("devices", null, ("search", segments[1]));

        var argument = segments.Count > 1 ? string.Join('/', segments.Skip(1)) : null;
        return DeepLink.For(segments[0].ToLowerInvariant(), argument, parameters.ToArray());
    }

    /// <summary>A link with filters added where the server left them off.</summary>
    private static DeepLink? WithDefault(DeepLink? link, string key, string value)
    {
        if (link is null || link[key] is not null || link.Query.Count > 0) return link;
        var filters = new List<(string, string?)> { (key, value) };
        return DeepLink.For(link.Section, link.Argument, filters.ToArray());
    }

    // ── Reading ──────────────────────────────────────────────────────────

    private static JsonElement? Node(JsonElement parent, string name) => Insights.Prop(parent, name);

    private static int Count(JsonElement parent, string name, string field = "count") =>
        Node(parent, name) is { } n ? Insights.Int(n, field) : 0;

    private static DeepLink? LinkOf(JsonElement parent, string name) =>
        Node(parent, name) is { } n ? ToDeepLink(Insights.Prop(n, "link")) : null;

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        Node(parent, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    private static Metric Figure(JsonElement card, string name, string label, Severity severity, string? detail = null) =>
        new(label, Count(card, name), LinkOf(card, name), severity, Detail: detail);

    /// <summary>A count that is zero reads as neither good nor bad.</summary>
    private static Severity When(double count, Severity severity) => count > 0 ? severity : Severity.Neutral;

    // ── Cards ────────────────────────────────────────────────────────────

    /// <summary>The card's parts, or its error when the server could not compute it.</summary>
    /// <param name="scoped">True when the toolbar narrows the dashboard to one platform.</param>
    public static CardView Build(string key, JsonElement? card, bool scoped = false)
    {
        if (card is not { ValueKind: JsonValueKind.Object } c)
            return new CardView([], "The server sent nothing for this card.");
        if (Insights.Str(c, "error") is { Length: > 0 } error)
            return new CardView([], error);

        return key switch
        {
            "rebootDebt" => RebootDebt(c),
            "protectionGaps" => ProtectionGaps(c),
            "unmanagedDevices" => Unmanaged(c),
            "hardwareAtRisk" => HardwareAtRisk(c),
            "installHealth" => InstallHealth(c),
            "checkinGaps" => CheckinGaps(c, scoped),
            "osCurrency" => OsCurrency(c),
            "softwareRisk" => SoftwareRisk(c),
            "expiringCertificates" => Certificates(c),
            "localAdminExposure" => LocalAdmins(c),
            "unusedSoftware" => UnusedSoftware(c),
            "connectivityProblems" => Connectivity(c),
            _ => new CardView([], $"This app does not know the card {key}."),
        };
    }

    private static CardView RebootDebt(JsonElement c)
    {
        var up30 = Count(c, "uptime30dPlus");
        var up2to4 = Count(c, "uptime2to4w");
        return new CardView(
        [
            new FiguresPart(
            [
                Figure(c, "uptime30dPlus", "up 30+ days", When(up30, Severity.Bad)),
                Figure(c, "uptime2to4w", "up 2–4 weeks", When(up2to4, Severity.Warning)),
            ]),
            new BarsPart("UPTIME", Array(c, "uptimeBuckets")
                .Select(b => new Metric(Insights.Str(b, "label"), Insights.Int(b, "count"),
                    ToDeepLink(Insights.Prop(b, "link")), Severity.Warning))
                .ToList()),
            new ChipsPart(
            [
                Figure(c, "pendingUpdates", "with pending updates", Severity.Warning),
                Figure(c, "deferredUpdates", "with deferred updates", Severity.Warning),
            ]),
        ]);
    }

    private static CardView ProtectionGaps(JsonElement c)
    {
        var gaps = Array(c, "gaps")
            .Select(g =>
            {
                var count = Insights.Int(g, "count");
                var total = Insights.Int(g, "total");
                return new Metric(Insights.Str(g, "label"), count, ToDeepLink(Insights.Prop(g, "link")),
                    When(count, Severity.Bad), Value: $"{count:N0} of {total:N0}",
                    Fraction: total == 0 ? 0 : (double)count / total);
            })
            .ToList();
        var parts = new List<CardPart>();
        parts.Add(gaps.Count == 0 || gaps.All(g => g.Count == 0)
            ? new NotePart("Every protection is on across these devices.")
            : new BarsPart("GAPS, WORST FIRST", gaps));
        parts.Add(new ChipsPart(
        [
            Figure(c, "withThreats", "with active threats", Severity.Bad),
            Figure(c, "criticalCves", "with critical CVEs", Severity.Bad),
        ]));
        return new CardView(parts);
    }

    private static CardView Unmanaged(JsonElement c)
    {
        var devices = Insights.Int(c, "devices");
        var unmanaged = Count(c, "unmanaged");
        var pct = Count(c, "unmanaged", "pct");
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                new Metric("not enrolled in MDM", unmanaged, LinkOf(c, "unmanaged"), When(unmanaged, Severity.Bad),
                    Detail: $"{pct}% of {devices:N0} devices"),
            ]),
            new NotePart($"{Insights.Int(c, "enrolledPct")}% of devices are enrolled."),
        };

        var byPlatform = Array(c, "byPlatform")
            .Select(p => new Metric(Insights.Str(p, "platform"), Insights.Int(p, "count"),
                ToDeepLink(Insights.Prop(p, "link")), Severity.Bad,
                Value: $"{Insights.Int(p, "count"):N0} of {Insights.Int(p, "devices"):N0}",
                Fraction: Insights.Int(p, "devices") == 0 ? 0 : (double)Insights.Int(p, "count") / Insights.Int(p, "devices"),
                Platform: Insights.Str(p, "platform")))
            .ToList();
        if (byPlatform.Count > 0) parts.Add(new BarsPart("BY PLATFORM", byPlatform));

        var lastSeen = Array(c, "lastSeen")
            .Select(s => new Metric(Insights.Str(s, "label"), Insights.Int(s, "count"), ToDeepLink(Insights.Prop(s, "link"))))
            .ToList();
        if (lastSeen.Count > 0) parts.Add(new BarsPart("LAST SEEN", lastSeen));

        var recent = Array(c, "recent")
            .Select(r => new Metric(Insights.Str(r, "name"), 0, ToDeepLink(Insights.Prop(r, "link")),
                Detail: Ago(Insights.Str(r, "lastSeen")), Platform: Insights.Str(r, "platform")))
            .ToList();
        if (recent.Count > 0) parts.Add(new ListPart("RECENTLY SEEN", recent));
        return new CardView(parts);
    }

    private static CardView HardwareAtRisk(JsonElement c) => new(
    [
        new FiguresPart(
        [
            Figure(c, "lowStorage", "under 10% storage free", When(Count(c, "lowStorage"), Severity.Bad)),
            Figure(c, "lowMemory", "8 GB RAM or less", When(Count(c, "lowMemory"), Severity.Warning)),
            Figure(c, "intelMacs", "Intel Macs", When(Count(c, "intelMacs"), Severity.Warning),
                $"of {Count(c, "intelMacs", "macs"):N0} Macs; at risk on the next macOS"),
        ]),
    ]);

    private static CardView InstallHealth(JsonElement c)
    {
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                Figure(c, "withErrors", "devices with errors", When(Count(c, "withErrors"), Severity.Bad)),
                Figure(c, "withWarnings", "devices with warnings", When(Count(c, "withWarnings"), Severity.Warning)),
            ]),
        };
        var failing = Array(c, "topFailing")
            .Select(i => new Metric(Insights.Str(i, "name"), Insights.Int(i, "devices"), ToDeepLink(Insights.Prop(i, "link")), Severity.Bad))
            .ToList();
        parts.Add(failing.Count > 0 ? new BarsPart("TOP FAILING ITEMS", failing) : new NotePart("No managed item is failing."));
        parts.Add(new ChipsPart(
        [
            Figure(c, "silentAgents", $"agents silent over {Count(c, "silentAgents", "thresholdDays")} days", Severity.Warning),
        ]));
        return new CardView(parts);
    }

    private static CardView CheckinGaps(JsonElement c, bool scoped)
    {
        var hours = Insights.Int(c, "hours");
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                Figure(c, "rejected", "rejected", When(Count(c, "rejected"), Severity.Bad)),
                Figure(c, "retried", "retried", Severity.Neutral),
                Figure(c, "accepted", "repaired", When(Count(c, "accepted"), Severity.Info)),
            ]),
            new NotePart(scoped && Insights.Boolish(c, "checkinsAllPlatforms")
                ? $"In the last {hours} hours · All platforms: check-ins are recorded before a device is known"
                : $"In the last {hours} hours"),
        };
        var reasons = Array(c, "reasons")
            .Select(r => new Metric(FailureReason.Label(Insights.Str(r, "reason")), Insights.Int(r, "count"),
                ToDeepLink(Insights.Prop(r, "link")), Severity.Bad,
                Detail: $"{Insights.Int(r, "devices"):N0} devices"))
            .ToList();
        if (reasons.Count > 0) parts.Add(new BarsPart("REASONS", reasons));
        parts.Add(new ChipsPart(
        [
            Figure(c, "stale", "silent 1–7 days", Severity.Warning),
            Figure(c, "missing", "silent over 7 days", Severity.Bad),
        ]));
        return new CardView(parts);
    }

    private static CardView OsCurrency(JsonElement c)
    {
        var onLatest = Insights.Int(c, "onLatest");
        var behind = Count(c, "behind");
        var parts = new List<CardPart>
        {
            new DonutPart($"{Insights.Int(c, "pctOnLatest")}%", "latest",
            [
                new Metric("On latest patch", onLatest, null, Severity.Good),
                new Metric("Behind", behind, LinkOf(c, "behind"), Severity.Warning),
            ]),
            new ChipsPart(
            [
                Figure(c, "unsupportedMac", "unsupported macOS", Severity.Bad),
                Figure(c, "windows10", "Windows 10", Severity.Warning),
            ]),
        };
        var lagging = Array(c, "lagging")
            .Select(l => new Metric(Insights.Str(l, "label"), Insights.Int(l, "devices"), ToDeepLink(Insights.Prop(l, "link")), Severity.Warning))
            .ToList();
        if (lagging.Count > 0) parts.Add(new BarsPart("MOST BEHIND", lagging));
        parts.Add(new NotePart($"Latest means the newest version at least {Insights.Int(c, "minDevices")} devices run."));
        return new CardView(parts);
    }

    private static CardView SoftwareRisk(JsonElement c)
    {
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                Figure(c, "criticalCves", "devices with critical CVEs", When(Count(c, "criticalCves"), Severity.Bad)),
                Figure(c, "exploitedCves", "with actively exploited CVEs", When(Count(c, "exploitedCves"), Severity.Bad)),
            ]),
        };
        var behind = Array(c, "itemsBehind")
            .Select(i => new Metric(Insights.Str(i, "name"), Insights.Int(i, "devices"), ToDeepLink(Insights.Prop(i, "link")), Severity.Warning))
            .ToList();
        parts.Add(behind.Count > 0 ? new BarsPart("MOST DEVICES BEHIND", behind) : new NotePart("Every managed item is on its latest version."));
        return new CardView(parts);
    }

    private static CardView Certificates(JsonElement c)
    {
        var days = Insights.Int(c, "windowDays");
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                new Metric($"expiring in {days} days", Count(c, "expiring", "certificates"), LinkOf(c, "expiring"),
                    When(Count(c, "expiring", "certificates"), Severity.Warning),
                    Detail: $"on {Count(c, "expiring", "devices"):N0} devices"),
                new Metric("devices with expired user certs", Count(c, "expiredUser", "devices"), LinkOf(c, "expiredUser"),
                    When(Count(c, "expiredUser", "devices"), Severity.Bad)),
            ]),
        };
        var soonest = Array(c, "soonest")
            .Select(s =>
            {
                var d = Insights.Int(s, "daysUntilExpiry");
                var n = Insights.Int(s, "devices");
                return new Metric(Insights.Str(s, "commonName"), d, ToDeepLink(Insights.Prop(s, "link")),
                    Detail: $"{(d <= 0 ? "today" : $"{d:N0}d")} · {n:N0} {(n == 1 ? "device" : "devices")}");
            })
            .ToList();
        if (soonest.Count > 0) parts.Add(new ListPart("EXPIRING SOONEST", soonest));
        return new CardView(parts);
    }

    private static CardView LocalAdmins(JsonElement c)
    {
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                Figure(c, "extraAdmins", "devices with extra local admins", When(Count(c, "extraAdmins"), Severity.Warning)),
                Figure(c, "workgroup", "not Entra or domain joined", When(Count(c, "workgroup"), Severity.Warning)),
            ]),
            new BarsPart("ADMINS PER DEVICE", Array(c, "byAdminCount")
                .Select(b => new Metric(Insights.Str(b, "label"), Insights.Int(b, "count"), ToDeepLink(Insights.Prop(b, "link")), Severity.Warning))
                .ToList()),
        };
        var top = Array(c, "topDevices")
            .Select(d => new Metric(Insights.Str(d, "name"), Insights.Int(d, "admins"), ToDeepLink(Insights.Prop(d, "link")),
                Detail: $"{Insights.Int(d, "admins"):N0} admins", Platform: Insights.Str(d, "platform")))
            .ToList();
        if (top.Count > 0) parts.Add(new ListPart("MOST ADMINS", top));
        return new CardView(parts);
    }

    private static CardView UnusedSoftware(JsonElement c)
    {
        var least = Array(c, "leastUsed")
            .Select(a =>
            {
                var active = Insights.Int(a, "activeDevices");
                var installed = Insights.Int(a, "installedDevices");
                return new Metric(Insights.Str(a, "name"), active, ToDeepLink(Insights.Prop(a, "link")), Severity.Warning,
                    Value: $"{active:N0} of {installed:N0}", Fraction: Insights.Number(a, "usageRate") ?? 0);
            })
            .ToList();
        var most = Array(c, "mostUsed")
            .Select(a => new Metric(Insights.Str(a, "name"), Insights.Number(a, "totalHours") ?? 0, ToDeepLink(Insights.Prop(a, "link")),
                Severity.Info, Value: $"{Insights.Number(a, "totalHours") ?? 0:N0} h"))
            .ToList();

        var parts = new List<CardPart>();
        parts.Add(least.Count > 0
            ? new BarsPart($"LEAST USED, ON {Insights.Int(c, "minInstalls")}+ DEVICES", least)
            : new NotePart($"No app installed on {Insights.Int(c, "minInstalls")} or more devices has usage to compare."));
        if (most.Count > 0) parts.Add(new BarsPart("MOST USED", most));
        parts.Add(new NotePart($"Over the last {Insights.Int(c, "days")} days."));
        return new CardView(parts);
    }

    private static CardView Connectivity(JsonElement c) => new(
    [
        new FiguresPart(
        [
            Figure(c, "weakSignal", "on a weak Wi-Fi signal", When(Count(c, "weakSignal"), Severity.Warning)),
            new Metric("with a poor network rating", Count(c, "poorQuality"),
                WithDefault(LinkOf(c, "poorQuality"), "quality", "poor"), When(Count(c, "poorQuality"), Severity.Warning),
                Detail: $"of {Count(c, "poorQuality", "measured"):N0} measured"),
            new Metric("on VPN", Count(c, "onVpn"), WithDefault(LinkOf(c, "onVpn"), "vpn", "1"), Severity.Info),
        ]),
    ]);

    private static string Ago(string iso)
    {
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when)) return "";
        var delta = DateTime.UtcNow - when;
        if (delta < TimeSpan.FromHours(1)) return "seen just now";
        if (delta < TimeSpan.FromDays(1)) return $"seen {(int)delta.TotalHours}h ago";
        return $"seen {(int)delta.TotalDays}d ago";
    }
}
