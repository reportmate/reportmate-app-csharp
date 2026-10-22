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
/// <param name="Swatch">
/// A fixed colour (#rrggbb) for this item's slice or bar, where the report it opens
/// has its own colour for the same category and the card should match it.
/// </param>
public sealed record Metric(
    string Label, double Count, DeepLink? Link,
    Severity Severity = Severity.Neutral, string? Value = null, string? Detail = null,
    double? Fraction = null, string? Platform = null, string? Swatch = null)
{
    public string Shown => Value ?? Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A piece of a card, in the order it is drawn.</summary>
public abstract record CardPart;

/// <summary>Large numbers side by side.</summary>
public sealed record FiguresPart(IReadOnlyList<Metric> Items) : CardPart;

/// <summary>Labelled bars under an optional heading.</summary>
/// <param name="Tabular">
/// Lay the rows out as fixed columns: name, bar, the value right-aligned, then the
/// detail right-aligned, so every column lines up down the list.
/// </param>
public sealed record BarsPart(string? Heading, IReadOnlyList<Metric> Items, bool Tabular = false) : CardPart;

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
    /// <summary>The cards the insights endpoints serve.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "installHealth", "rebootDebt", "protectionGaps", "managementToolLogs", "storage",
        "checkinGaps", "updateDebt", "softwareRisk", "expiringCertificates", "directoryServices",
        "connectivityProblems",
    ];

    /// <summary>
    /// The endpoints to try for a card, newest name first. The renamed cards keep
    /// their old names until the API that serves the new ones is deployed: a 404 on
    /// the new name falls back to the old one.
    /// </summary>
    public static IReadOnlyList<string> Endpoints(string key) => key switch
    {
        "managementToolLogs" => ["managementToolLogs", "managementToolErrors"],
        "directoryServices" => ["directoryServices", "directoryJoin"],
        // OS Currency became Update Debt; the API keeps the old name for one release.
        "updateDebt" => ["updateDebt", "osCurrency"],
        _ => [key],
    };

    /// <summary>App Usage: its own endpoint, filtered by usage and catalog.</summary>
    public const string AppUsage = "appUsage";

    /// <summary>Every card the dashboard reads, App Usage included.</summary>
    public static readonly IReadOnlyList<string> AllKeys = [.. Keys, AppUsage];

    // ── Responses ────────────────────────────────────────────────────────

    /// <summary>
    /// A per-card response's section (<c>/api/v1/dashboard/insights/{card}</c>): the
    /// card itself, the card under its key, or under <c>cards</c>, whichever shape
    /// the endpoint settles on.
    /// </summary>
    public static JsonElement SectionOf(JsonElement root, string key) =>
        // The deployed shape: {card, platform, includeArchived, generatedAt, data}.
        Insights.Prop(root, "data") is { ValueKind: JsonValueKind.Object } data && Insights.Prop(root, "card") is not null ? data
        : Insights.Prop(root, "cards") is { } cards && Insights.Prop(cards, key) is { } inCards ? inCards
        : Insights.Prop(root, key) is { ValueKind: JsonValueKind.Object } named ? named
        : root;

    /// <summary>
    /// A failed read in words. A 404 means the server has no such endpoint yet: it
    /// is final, so the card says so at once rather than waiting out its skeleton.
    /// </summary>
    public static string FailureText(int? httpStatus, string? detail) => httpStatus == 404
        ? "This API does not serve dashboard insights yet (404)."
        : detail ?? "The dashboard insights could not be loaded.";

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
    /// <param name="view">Directory Join's view: all, windows or mac.</param>
    public static CardView Build(string key, JsonElement? card, bool scoped = false, string view = "all")
    {
        if (card is not { ValueKind: JsonValueKind.Object } c)
            return new CardView([], "The server sent nothing for this card.");
        if (Insights.Str(c, "error") is { Length: > 0 } error)
            return new CardView([], error);

        return key switch
        {
            "installHealth" => InstallHealth(c),
            "rebootDebt" => RebootDebt(c),
            "protectionGaps" => ProtectionGaps(c),
            "managementToolLogs" or "managementToolErrors" => ManagementTools(c),
            "storage" => Storage(c),
            "checkinGaps" => CheckinGaps(c, scoped),
            "updateDebt" or "osCurrency" => UpdateDebt(c),
            "softwareRisk" => SoftwareRisk(c),
            "expiringCertificates" => Certificates(c),
            "directoryServices" or "directoryJoin" => DirectoryServices(c, view),
            "connectivityProblems" => Connectivity(c),
            AppUsage => AppUsageCard(c),
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

    /// <summary>
    /// Install Health: devices with errors and warnings as the headline, then each
    /// agent's split, item counts, pending installs, silent agents and the items
    /// failing on the most devices.
    /// </summary>
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

        var agents = Array(c, "byAgent").ToList();
        if (agents.Count > 0)
            parts.Add(new ListPart("BY AGENT", agents.Select(a =>
            {
                var errors = Count(a, "withErrors");
                var warnings = Count(a, "withWarnings");
                return new Metric(Insights.Str(a, "name"), errors, LinkOf(a, "withErrors"), When(errors, Severity.Bad),
                    Detail: $"{errors:N0} with errors · {warnings:N0} with warnings · {Insights.Int(a, "devices"):N0} devices",
                    Platform: Insights.Str(a, "platform"));
            }).ToList()));

        var pendingItems = Count(c, "pending", "items");
        parts.Add(new ChipsPart(
        [
            new Metric("error items", Count(c, "errorItems"), LinkOf(c, "errorItems"), Severity.Bad),
            new Metric("warning items", Count(c, "warningItems"), LinkOf(c, "warningItems"), Severity.Warning),
            new Metric($"pending on {Count(c, "pending", "devices"):N0} devices", pendingItems, LinkOf(c, "pending"), Severity.Info),
            Figure(c, "silentAgents", $"agents silent over {Count(c, "silentAgents", "thresholdDays")} days", Severity.Warning),
        ]));

        var failing = Array(c, "topFailing")
            .Select(i => new Metric(Insights.Str(i, "name"), Insights.Int(i, "devices"), ToDeepLink(Insights.Prop(i, "link")), Severity.Bad))
            .ToList();
        parts.Add(failing.Count > 0 ? new BarsPart("TOP FAILING ITEMS", failing) : new NotePart("No managed item is failing."));
        return new CardView(parts);
    }

    /// <summary>
    /// Management Tools Errors: per tool, the devices whose logs carried errors in the
    /// window, and the error messages seen on the most devices.
    /// </summary>
    private static CardView ManagementTools(JsonElement c)
    {
        var hours = Insights.Int(c, "windowHours");
        var tools = Array(c, "tools")
            .Where(t => !IsMdmTool(t))
            .Select(t =>
            {
                var withErrors = Insights.Int(t, "devicesWithErrors");
                return new Metric(Insights.Str(t, "name"), withErrors, ToDeepLink(Insights.Prop(t, "link")), When(withErrors, Severity.Bad),
                    Value: $"{withErrors:N0}",
                    Detail: $"{withErrors:N0} devices with errors · {Insights.Int(t, "devicesWithWarnings"):N0} with warnings · {Insights.Int(t, "errorLines"):N0} error lines",
                    Platform: Insights.Str(t, "platform") is { Length: > 0 } p ? p : null);
            })
            .ToList();
        // Per tool only: a device can log errors from several tools, so a fleet
        // total would count it more than once.
        var parts = new List<CardPart> { new NotePart($"In the last {hours} hours, from the tools' own logs.") };
        if (tools.Count > 0) parts.Add(new BarsPart("DEVICES WITH ERRORS, BY TOOL", tools));
        else parts.Add(new NotePart("No management tool logged an error."));

        // Grouped again here, by tool and the message without its timestamp and level
        // prefix: the first deployment of the endpoint listed one message five times.
        var messages = Array(c, "topErrors")
            .Where(m => !IsMdmTool(m))
            .GroupBy(m => (Tool: Insights.Str(m, "tool"), Message: NormalizeMessage(Insights.Str(m, "message"))))
            .Select(g => (g.Key.Message, Name: Insights.Str(g.First(), "name"), Devices: g.Sum(m => Insights.Int(m, "devices")),
                Link: ToDeepLink(Insights.Prop(g.First(), "link"))))
            .OrderByDescending(g => g.Devices)
            .Take(5)
            .Select(g => new Metric(g.Message, g.Devices, g.Link, Detail: $"{g.Name} · {g.Devices:N0} devices"))
            .ToList();
        if (messages.Count > 0) parts.Add(new ListPart("TOP ERRORS", messages));
        return new CardView(parts);
    }

    /// <summary>
    /// Storage: devices under 10% free by platform, the free-space buckets, the
    /// capacity mix and the fullest devices.
    /// </summary>
    private static CardView Storage(JsonElement c)
    {
        var parts = new List<CardPart>
        {
            new FiguresPart(
            [
                new Metric("devices under 10% free", Count(c, "lowStorage"), LinkOf(c, "lowStorage"), When(Count(c, "lowStorage"), Severity.Bad),
                    Detail: $"of {Insights.Int(c, "devices"):N0} devices measured"),
            ]),
        };

        var low = Node(c, "lowStorage") is { } l ? l : default;
        var byPlatform = Array(low, "byPlatform")
            .Select(p => new Metric(Insights.Str(p, "platform"), Insights.Int(p, "count"), ToDeepLink(Insights.Prop(p, "link")),
                Severity.Bad, Platform: Insights.Str(p, "platform")))
            .ToList();
        if (byPlatform.Count > 1) parts.Add(new BarsPart("UNDER 10% FREE, BY PLATFORM", byPlatform));

        parts.Add(new BarsPart("FREE SPACE", Array(c, "freeSpace")
            .Select(b => new Metric(Insights.Str(b, "label"), Insights.Int(b, "count"), ToDeepLink(Insights.Prop(b, "link")),
                Insights.Str(b, "key") switch { "lt5" => Severity.Bad, "5-10" => Severity.Warning, _ => Severity.Neutral }))
            .ToList()));
        parts.Add(new BarsPart("CAPACITY", Array(c, "capacity")
            .Select(b => new Metric(Insights.Str(b, "label"), Insights.Int(b, "count"), ToDeepLink(Insights.Prop(b, "link"))))
            .ToList()));

        var fullest = Array(c, "fullest")
            .Select(d => new Metric(Insights.Str(d, "name"), Insights.Number(d, "freePct") ?? 0, ToDeepLink(Insights.Prop(d, "link")),
                Detail: $"{Insights.Number(d, "freePct") ?? 0:0.#}% free · {Gb(Insights.Number(d, "freeBytes"))} of {Gb(Insights.Number(d, "totalBytes"))}",
                Platform: Insights.Str(d, "platform")))
            .ToList();
        if (fullest.Count > 0) parts.Add(new ListPart("FULLEST", fullest));
        return new CardView(parts);
    }

    /// <summary>
    /// The MDM agent's own logs (Intune here, whatever the MDM is elsewhere) are out
    /// of scope for this card. The runner files them under the <c>mdm</c> root.
    /// </summary>
    private static bool IsMdmTool(JsonElement node) =>
        Insights.Str(node, "tool").Equals("mdm", StringComparison.OrdinalIgnoreCase)
        || Insights.Str(node, "tool").Equals("enrollment", StringComparison.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex LeadingStamp = new(
        @"^\s*(?:<ts>|\[?\d{4}-\d{2}-\d{2}[T ]?[\d:.,]*\]?|\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?)?\s*"
        + @"(?:[+-]?(?:<n>|\d{1,2}):?(?:<n>|\d{2}))?\s*"
        + @"(?:\[(?:ERR|ERROR|WRN|WARN|WARNING|INF|INFO|DBG|DEBUG|E|W|I)\]|(?:ERROR|ERR|WARNING|WARN)\s*[:\-]?)?\s*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A log message without its leading timestamp, offset and level, so one message
    /// logged at different times groups as one: <c>&lt;ts&gt; -&lt;n&gt;:&lt;n&gt; [ERR] Device
    /// registration failed for &lt;guid&gt;</c> is <c>Device registration failed for &lt;guid&gt;</c>.
    /// </summary>
    public static string NormalizeMessage(string message)
    {
        var stripped = LeadingStamp.Replace(message, "", 1).Trim();
        return stripped.Length == 0 ? message.Trim() : stripped;
    }

    private static string Gb(double? bytes) => bytes is not > 0 ? "?"
        : bytes >= 1e12 ? $"{bytes / 1e12:0.#} TB" : $"{bytes / 1e9:0} GB";

    /// <summary>
    /// The Identity report's Directory Services colours, so the card and the widget
    /// it opens agree: Cloud Joined green, Domain Joined amber with Trusted yellow,
    /// Unconfirmed orange and Broken red, Standard blue, Unjoined red.
    /// </summary>
    public static string? DirectorySwatch(string? key) =>
        new string((key ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() switch
        {
            "cloudjoined" or "cloud" or "entra" or "psso" => "#10b981",
            "domainjoined" or "domain" or "hybrid" or "bound" => "#f59e0b",
            "trusted" => "#eab308",
            "unconfirmed" => "#f97316",
            "broken" => "#ef4444",
            "standard" => "#3b82f6",
            "unjoined" or "workgroup" or "unbound" => "#ef4444",
            _ => null,
        };

    /// <summary>The Identity report's directory classes, in its order: <c>/identity?directory=</c>.</summary>
    public static readonly IReadOnlyList<string> DirectoryClasses = ["Cloud Joined", "Domain Joined", "Standard", "Unjoined"];

    /// <summary>
    /// Directory Services, classified exactly as the Identity report's Directory
    /// Services widget: Cloud Joined, Domain Joined (Trusted, Unconfirmed, Broken),
    /// Standard and Unjoined, with the extra-admin, shared and assigned chips.
    /// Reads the earlier Directory Join card too, taking the view that matches the
    /// platform toggle, until the API serving the new card is deployed.
    /// </summary>
    private static CardView DirectoryServices(JsonElement c, string view)
    {
        var v = c;
        if (Node(c, "views") is { } views)
            v = Node(views, view) is { ValueKind: JsonValueKind.Object } chosen ? chosen : Node(views, "all") ?? default;

        Severity Tone(string key) => key.Replace(" ", "").Replace("-", "").ToLowerInvariant() switch
        {
            "cloudjoined" or "entra" or "psso" => Severity.Good,
            "domainjoined" or "domain" or "hybrid" or "bound" or "trusted" => Severity.Info,
            "standard" => Severity.Neutral,
            _ => Severity.Warning,
        };
        var segments = Array(v, "segments")
            .Select(s =>
            {
                var key = Insights.Str(s, "key") is { Length: > 0 } k ? k : Insights.Str(s, "label");
                return new Metric(Insights.Str(s, "label"), Insights.Int(s, "count"), ToDeepLink(Insights.Prop(s, "link")),
                    Tone(key), Swatch: DirectorySwatch(key) ?? DirectorySwatch(Insights.Str(s, "label")));
            })
            .ToList();

        // The current card sends the device count as total; the earlier one as devices.
        var devices = Insights.Number(v, "total") is { } total ? (int)total : Insights.Int(v, "devices");
        var parts = new List<CardPart>
        {
            new DonutPart(devices.ToString("N0", CultureInfo.CurrentCulture), "devices", segments),
        };

        // Domain Joined's trust breakdown, under whichever name the card gives it.
        var trust = new[] { "trust", "domainTrust", "breakdown" }
            .Select(name => Array(v, name).ToList())
            .FirstOrDefault(list => list.Count > 0)
            ?? Array(v, "segments").SelectMany(s => new[] { "children", "breakdown", "trust" }.SelectMany(n => Array(s, n))).ToList();
        if (trust.Count > 0)
            parts.Add(new BarsPart("DOMAIN JOINED TRUST", trust
                .Select(t => new Metric(Insights.Str(t, "label"), Insights.Int(t, "count"), ToDeepLink(Insights.Prop(t, "link")),
                    Insights.Str(t, "label").ToLowerInvariant() switch
                    {
                        "broken" => Severity.Bad,
                        "unconfirmed" => Severity.Warning,
                        _ => Severity.Good,
                    }, Swatch: DirectorySwatch(Insights.Str(t, "label"))))
                .ToList()));

        parts.Add(new ChipsPart(
        [
            Figure(v, "extraAdmins", "with extra local admins", Severity.Warning),
            Figure(v, "shared", "shared", Severity.Neutral),
            Figure(v, "assigned", "assigned", Severity.Neutral),
        ]));
        return new CardView(parts);
    }

    /// <summary>App Usage: the ten most-used apps within the chosen usages and catalogs.</summary>
    private static CardView AppUsageCard(JsonElement c)
    {
        var apps = Array(c, "apps")
            .Select(a => new Metric(Insights.Str(a, "name"), Insights.Number(a, "totalHours") ?? 0, ToDeepLink(Insights.Prop(a, "link")),
                Severity.Info, Value: $"{Insights.Number(a, "totalHours") ?? 0:N0} h",
                Detail: $"{(Insights.Int(a, "activeDevices") is var d and > 0 ? d : Insights.Int(a, "devices")):N0} devices"))
            .ToList();
        var parts = new List<CardPart>();
        parts.Add(apps.Count > 0
            ? new BarsPart("MOST USED", apps, Tabular: true)
            : new NotePart("No application usage matches these filters."));
        parts.Add(new NotePart($"Over the last {Insights.Int(c, "days")} days."));
        return new CardView(parts);
    }

    /// <summary>The usage and catalog values App Usage can be filtered by, with their device counts.</summary>
    public static (IReadOnlyList<(string Value, int Devices)> Usages, IReadOnlyList<(string Value, int Devices)> Catalogs) AppUsageFilters(JsonElement? card)
    {
        if (card is not { ValueKind: JsonValueKind.Object } c || Node(c, "available") is not { } available) return ([], []);
        IReadOnlyList<(string, int)> Read(string name) => Array(available, name)
            .Select(o => (Insights.Str(o, "value"), Insights.Int(o, "devices")))
            .Where(o => o.Item1.Length > 0)
            .ToList();
        return (Read("usages"), Read("catalogs"));
    }

    /// <summary>App Usage's header link: the applications report in usage mode with the same filters.</summary>
    public static DeepLink? HeaderLink(JsonElement? card) =>
        card is { ValueKind: JsonValueKind.Object } c ? ToDeepLink(Insights.Prop(c, "link")) : null;

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

    /// <summary>
    /// Update Debt, formerly OS Currency: devices behind their latest patch, plus the
    /// pending and deferred updates that moved here from Reboot Debt. An earlier
    /// OS Currency section has no pending or deferred counts, and shows without them.
    /// </summary>
    private static CardView UpdateDebt(JsonElement c)
    {
        var view = OsCurrency(c);
        var waiting = new List<Metric>();
        if (Node(c, "pendingUpdates") is not null) waiting.Add(Figure(c, "pendingUpdates", "with pending updates", Severity.Warning));
        if (Node(c, "deferredUpdates") is not null) waiting.Add(Figure(c, "deferredUpdates", "with deferred updates", Severity.Warning));
        if (waiting.Count == 0) return view;
        var parts = view.Parts.ToList();
        // Beside the version chips, before the list of versions most behind.
        var chips = parts.FindIndex(p => p is ChipsPart);
        if (chips >= 0) parts[chips] = new ChipsPart([.. waiting, .. ((ChipsPart)parts[chips]).Items]);
        else parts.Insert(1, new ChipsPart(waiting));
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
