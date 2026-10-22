// Explicit usings: this file is also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The Applications report as the web and Mac apps run it: a Versions report of the
/// installed copies of the chosen applications, a Usage report of how much the fleet
/// used them over a period, and a Missing report of the devices that should have them
/// and do not. Everything here is free of UI types so the state, the queries and the
/// arithmetic can be tested; the page only draws it.
/// </summary>
public static class ApplicationsReport
{
    /// <summary>The periods the web's selector offers, in days, with its labels.</summary>
    public static readonly IReadOnlyList<(int Days, string Label)> Periods =
    [
        (7, "7 days"), (30, "30 days"), (45, "45 days"), (90, "90 days"), (365, "Last year"),
    ];

    /// <summary>The API rejects a window outside 1 to 548 days, so a hand-written link is held inside it.</summary>
    private const int MaxDays = 548;

    // ── URL state ───────────────────────────────────────────────────────

    /// <summary>
    /// Everything the report shows, held in the link so a copied link reopens the same
    /// report. The names are the web's (<c>type</c>, <c>period</c>, <c>mode</c>,
    /// <c>apps</c> …), so one link opens the same view in all three apps.
    /// </summary>
    public sealed record State
    {
        /// <summary><c>usage</c> or <c>versions</c>. A link without one opens Versions, as this page always has.</summary>
        public string Type { get; init; } = "versions";
        public int Period { get; init; } = 30;
        /// <summary>The Missing report: devices without the chosen apps. Only a Versions report has one.</summary>
        public bool Missing { get; init; }
        public IReadOnlyList<string> Apps { get; init; } = [];
        /// <summary>Plain versions, or <c>App:version</c> pairs as the web's version cards write them.</summary>
        public IReadOnlyList<string> Versions { get; init; } = [];
        public IReadOnlyList<string> Publishers { get; init; } = [];
        public IReadOnlyList<string> Usages { get; init; } = [];
        public IReadOnlyList<string> Catalogs { get; init; } = [];
        public IReadOnlyList<string> Locations { get; init; } = [];
        public IReadOnlyList<string> Rooms { get; init; } = [];
        public IReadOnlyList<string> Areas { get; init; } = [];
        public IReadOnlyList<string> Fleets { get; init; } = [];
        /// <summary>The platform the link named, as written; the toolbar's applies when it names none.</summary>
        public string? Platform { get; init; }
        public string? Query { get; init; }

        public bool IsUsage => Type == "usage";
        public bool IsMissing => !IsUsage && Missing;

        /// <summary>
        /// Read a link's parameters, accepting the web's older single-value names
        /// (<c>application</c>, <c>usage</c>, <c>catalog</c>, <c>room</c>, <c>search</c>)
        /// that dashboard links written before the lists still carry.
        /// </summary>
        public static State From(Func<string, string?> read)
        {
            IReadOnlyList<string> List(string key, string? legacy = null)
            {
                var values = Split(read(key)).ToList();
                if (legacy is not null && read(legacy) is { Length: > 0 } one
                    && !values.Contains(one.Trim(), StringComparer.OrdinalIgnoreCase))
                    values.Add(one.Trim());
                return values;
            }

            var type = (read("type") ?? "").Trim().ToLowerInvariant() == "usage" ? "usage" : "versions";
            return new State
            {
                Type = type,
                Period = int.TryParse(read("period"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
                    ? Math.Clamp(p, 1, MaxDays) : 30,
                Missing = string.Equals(read("mode")?.Trim(), "missing", StringComparison.OrdinalIgnoreCase),
                Apps = List("apps", "application"),
                Versions = List("versions"),
                Publishers = List("publishers"),
                Usages = List("usages", "usage"),
                Catalogs = List("catalogs", "catalog"),
                Locations = List("locations"),
                Rooms = List("rooms", "room"),
                Areas = List("areas"),
                Fleets = List("fleets"),
                Platform = Blank(read("platform")),
                Query = Blank(read("q")) ?? Blank(read("search")),
            };
        }

        /// <summary>The link that reopens exactly this report.</summary>
        public DeepLink Link() => DeepLink.For("applications", null,
            ("type", Type),
            ("period", IsUsage ? Period.ToString(CultureInfo.InvariantCulture) : null),
            ("mode", IsMissing ? "missing" : null),
            ("apps", Join(Apps)), ("versions", Join(Versions)), ("publishers", Join(Publishers)),
            ("usages", Join(Usages)), ("catalogs", Join(Catalogs)), ("locations", Join(Locations)),
            ("rooms", Join(Rooms)), ("areas", Join(Areas)), ("fleets", Join(Fleets)),
            ("platform", Platform), ("q", Query));

        /// <summary>
        /// <c>/api/v1/applications/usage</c> for this report. The inventory scope goes to
        /// the server, which counts in SQL; filtering a capped response here would count
        /// only what fitted in it.
        /// </summary>
        public string UsagePath(string? platforms) => "/api/v1/applications/usage?" + QueryString(
            ("days", Period.ToString(CultureInfo.InvariantCulture)),
            ("applicationNames", Join(Apps)), ("usages", Join(Usages)), ("catalogs", Join(Catalogs)),
            ("locations", Join(Locations)), ("areas", Join(Areas)), ("fleets", Join(Fleets)),
            ("rooms", Join(Rooms)), ("platforms", platforms));

        /// <summary>
        /// <c>/api/v1/applications</c>, the installed rows behind the Versions report.
        /// Versions stay off the request so the distribution still shows every version
        /// while the table narrows to the chosen ones.
        /// </summary>
        public string InventoryPath(string? platforms) => "/api/v1/applications?" + QueryString(
            ("applicationNames", Join(Apps)), ("publishers", Join(Publishers)),
            ("usages", Join(Usages)), ("catalogs", Join(Catalogs)), ("areas", Join(Areas)),
            ("fleets", Join(Fleets)), ("rooms", Join(Rooms)), ("locations", Join(Locations)),
            ("platforms", platforms), ("limit", "5000"));

        /// <summary>
        /// <c>/api/v1/applications/distribution</c>, counted per version in SQL and so not
        /// bounded by the inventory's 5,000-row cap; null when no app is chosen, since
        /// the server cannot bucket "everything".
        /// </summary>
        public string? DistributionPath(string? platforms) => Apps.Count == 0 ? null
            : "/api/v1/applications/distribution?" + QueryString(
                ("applicationNames", Join(Apps)), ("usages", Join(Usages)), ("catalogs", Join(Catalogs)),
                ("areas", Join(Areas)), ("fleets", Join(Fleets)), ("rooms", Join(Rooms)),
                ("locations", Join(Locations)), ("platforms", platforms));

        /// <summary>The per-device drill-down for one app of a Usage report, over the same period and scope.</summary>
        public DeepLink UsageDrillLink(string app) => DeepLink.For("applications", "usage/" + app,
            ("days", Period.ToString(CultureInfo.InvariantCulture)),
            ("usages", Join(Usages)), ("catalogs", Join(Catalogs)), ("locations", Join(Locations)),
            ("platform", Platform));

        /// <summary>The same report with one app chosen or unchosen.</summary>
        public State ToggleApp(string app) => this with { Apps = Toggle(Apps, app) };
    }

    /// <summary>
    /// The platform as the API's <c>platforms</c> parameter names it: <c>macos</c> or
    /// <c>windows</c>. The link's own wins over the toolbar's.
    /// </summary>
    public static string? PlatformToken(string? linkPlatform, string? toolbar)
    {
        foreach (var raw in new[] { linkPlatform, toolbar })
        {
            var p = (raw ?? "").Trim().ToLowerInvariant();
            if (p.Length == 0 || p == "all") continue;
            if (p is "mac" or "macos" or "darwin" || p.StartsWith("mac", StringComparison.Ordinal)) return "macos";
            if (p is "win" || p.StartsWith("windows", StringComparison.Ordinal)) return "windows";
        }
        return null;
    }

    /// <summary>Whether a row's reported platform is the one the report is scoped to.</summary>
    public static bool PlatformMatches(string? token, string? platform)
    {
        if (token is null) return true;
        var p = (platform ?? "").Trim().ToLowerInvariant();
        return token == "macos"
            ? p.StartsWith("mac", StringComparison.Ordinal) || p.Contains("darwin") || p.StartsWith("os x", StringComparison.Ordinal)
            : p.StartsWith("windows", StringComparison.Ordinal);
    }

    public static IEnumerable<string> Split(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(",", values);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string QueryString(params (string Key, string? Value)[] pairs) => string.Join("&", pairs
        .Where(p => !string.IsNullOrWhiteSpace(p.Value))
        .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));

    /// <summary>The list with the value added, or taken out when it is already there.</summary>
    public static IReadOnlyList<string> Toggle(IReadOnlyList<string> list, string value) =>
        list.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? list.Where(v => !string.Equals(v, value, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. list, value];

    // ── Filter options (/applications/filters) ──────────────────────────

    /// <summary>A device the filter endpoint knows; the Missing report subtracts from this list.</summary>
    public sealed record FilterDevice(string Serial, string Name, string Usage, string Catalog, string Location,
        string Room, string Area, string Fleet);

    /// <summary>The chip cloud's names and the inventory values, from <c>/api/v1/applications/filters</c>.</summary>
    public sealed record Options(
        IReadOnlyList<string> ApplicationNames, IReadOnlyList<string> WindowsApplicationNames,
        IReadOnlyList<string> MacApplicationNames, IReadOnlyList<FilterDevice> Devices)
    {
        public static readonly Options Empty = new([], [], [], []);

        /// <summary>The names collected on the platform in scope, as the web's chip cloud shows them.</summary>
        public IReadOnlyList<string> NamesFor(string? platformToken) => platformToken switch
        {
            "windows" when WindowsApplicationNames.Count > 0 => WindowsApplicationNames,
            "macos" when MacApplicationNames.Count > 0 => MacApplicationNames,
            _ => ApplicationNames,
        };
    }

    public static Options ReadOptions(JsonElement root)
    {
        IReadOnlyList<string> Names(string key) => Insights.Prop(root, key) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => (e.GetString() ?? "").Trim()).Where(s => s.Length > 0).ToList()
            : [];
        var devices = Insights.Prop(root, "devices") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(d =>
            {
                var serial = Insights.Str(d, "serialNumber");
                var location = Insights.Str(d, "location");
                return new FilterDevice(serial, Or(Insights.Str(d, "name"), Insights.Str(d, "deviceName"), serial),
                    Insights.Str(d, "usage"), Insights.Str(d, "catalog"), location,
                    Or(Insights.Str(d, "room"), location), Or(Insights.Str(d, "area"), Insights.Str(d, "department")),
                    Insights.Str(d, "fleet"));
            }).Where(d => d.Serial.Length > 0).ToList()
            : [];
        return new Options(Names("applicationNames"), Names("windowsApplicationNames"), Names("macApplicationNames"), devices);
    }

    /// <summary>
    /// The chip cloud: the platform's names that contain the search text, sorted. The
    /// chosen apps are always among them, so one can be unchosen whatever the search.
    /// </summary>
    public static IReadOnlyList<string> ChipNames(Options options, string? platformToken, string? search, IReadOnlyList<string> chosen)
    {
        var q = (search ?? "").Trim();
        return options.NamesFor(platformToken)
            .Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Concat(chosen)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Installed rows (/applications) ──────────────────────────────────

    public sealed record Installed(string Name, string Version, string Publisher, string Device, string Serial,
        string Usage, string Catalog, string Location, string Room, string Area, string Fleet, string Platform, string Architecture);

    public static IReadOnlyList<Installed> ReadInstalled(JsonElement root)
    {
        // The bulk endpoint answers with the array itself; an envelope is accepted too.
        var rows = root.ValueKind == JsonValueKind.Array ? root
            : Insights.Prop(root, "applications") ?? Insights.Prop(root, "data") ?? default;
        if (rows.ValueKind != JsonValueKind.Array) return [];
        return rows.EnumerateArray().Select(r =>
        {
            var serial = Or(Insights.Str(r, "serialNumber"), Insights.Str(r, "deviceId"));
            return new Installed(Insights.Str(r, "name"), Insights.Str(r, "version"),
                Or(Insights.Str(r, "vendor"), Insights.Str(r, "publisher")),
                Or(Insights.Str(r, "deviceName"), serial), serial,
                Insights.Str(r, "usage"), Insights.Str(r, "catalog"), Insights.Str(r, "location"),
                Insights.Str(r, "room"), Or(Insights.Str(r, "department"), Insights.Str(r, "area")),
                Insights.Str(r, "fleet"), Insights.Str(r, "platform"), Insights.Str(r, "architecture"));
        }).ToList();
    }

    /// <summary>
    /// The installed rows the report counts, before any version is chosen: the junk
    /// filter, the platform, and the inventory selections, compared without case. The
    /// server already applied them; doing it again keeps an older API that ignores a
    /// parameter from widening the report without saying so.
    /// </summary>
    public static IReadOnlyList<Installed> BaseFilter(IEnumerable<Installed> rows, State state, string? platformToken) =>
        rows.Where(a => ShouldInclude(a.Name)
            && PlatformMatches(platformToken, a.Platform)
            && InAny(state.Usages, a.Usage)
            && InAny(state.Catalogs, a.Catalog)
            && InAny(state.Locations, a.Location)
            && InRoom(state.Rooms, a.Location, a.Room)
            && InAny(state.Fleets, a.Fleet)
            && InAny(state.Areas, a.Area)).ToList();

    /// <summary>The rows for the table: the base rows narrowed to the chosen versions.</summary>
    public static IReadOnlyList<Installed> WithVersions(IEnumerable<Installed> rows, IReadOnlyList<string> versions) =>
        versions.Count == 0 ? rows.ToList() : rows.Where(a => versions.Any(v => MatchesVersion(a, v))).ToList();

    /// <summary>
    /// A version filter: <c>App:1.2</c> matches that version of the app, by its
    /// normalised name as the version cards key it; a plain <c>1.2</c> matches any app.
    /// "Unknown" matches nothing, as on the web.
    /// </summary>
    public static bool MatchesVersion(Installed app, string filter)
    {
        var colon = filter.LastIndexOf(':');
        if (colon > 0)
        {
            var name = filter[..colon];
            var version = filter[(colon + 1)..];
            if (version == "Unknown") return false;
            var normal = Normalize(app.Name);
            return (string.Equals(normal, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase))
                && app.Version == version;
        }
        return filter != "Unknown" && app.Version == filter;
    }

    private static bool InAny(IReadOnlyList<string> wanted, string value) =>
        wanted.Count == 0 || wanted.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>A room matches by substring of the location or the room, as the web compares it.</summary>
    private static bool InRoom(IReadOnlyList<string> rooms, string location, string room) =>
        rooms.Count == 0 || rooms.Any(r => location.Contains(r, StringComparison.OrdinalIgnoreCase)
                                           || room.Contains(r, StringComparison.OrdinalIgnoreCase));

    private static string Or(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    // ── Missing report ──────────────────────────────────────────────────

    /// <summary>
    /// Devices that match the report's inventory selection and have none of the chosen
    /// apps installed: the filter endpoint's device list less every device the
    /// installed rows name. Area and fleet narrow it as well as usage, catalog,
    /// location and room, because the installed rows were fetched with them, and a
    /// device outside the area would otherwise count as missing an app it was never
    /// meant to have.
    /// </summary>
    public static IReadOnlyList<FilterDevice> MissingDevices(IEnumerable<FilterDevice> all, IEnumerable<Installed> installed, State state)
    {
        if (state.Apps.Count == 0) return [];
        var have = new HashSet<string>(installed.Select(a => a.Serial), StringComparer.OrdinalIgnoreCase);
        return all.Where(d => !have.Contains(d.Serial)
                && InAny(state.Usages, d.Usage)
                && InAny(state.Catalogs, d.Catalog)
                && InAny(state.Locations, d.Location)
                && InRoom(state.Rooms, d.Location, d.Room)
                && InAny(state.Areas, d.Area)
                && InAny(state.Fleets, d.Fleet))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Usage report (/applications/usage) ─────────────────────────────

    /// <param name="ActiveHours">
    /// Focus with input in the prior five minutes: the utilisation figure. Null when
    /// no client in scope reports idle time yet, which is not the same as zero.
    /// </param>
    /// <param name="TotalHours">Process lifetime across concurrent processes; diagnostic only.</param>
    public sealed record UsageApp(string Name, double TotalSeconds, double TotalHours, double? ActiveSeconds, double? ActiveHours,
        double? ForegroundHours, double? ActiveRatio, int LaunchCount, int DeviceCount, int UserCount,
        int? ActiveDeviceCount, int? ActiveUserCount, int? InstalledDeviceCount, DateTime? LastUsed,
        IReadOnlyList<string> Devices, bool IsSingleUser)
    {
        /// <summary>Devices that gave it attention; the footprint only when no active count is reported.</summary>
        public int ShownDevices => ActiveDeviceCount ?? DeviceCount;
        public int ShownUsers => ActiveUserCount ?? UserCount;
        public bool HasNoData => DeviceCount == 0 && LaunchCount == 0 && TotalSeconds == 0;

        public string Status => HasNoData ? "No data" : IsSingleUser ? "Single User" : UserCount > 5 ? "Popular" : "Normal";
    }

    public sealed record UsageSummary(int AppsTracked, double? ActiveHours, double UsageHours, int Launches,
        int Users, int Devices, int NoActiveUse);

    /// <summary>A device in the usage report's device roll-up.</summary>
    public sealed record UsageDevice(string Serial, string Name, string Usage, string Catalog, string Location,
        string Area, string Fleet, double TotalHours, int LaunchCount);

    public sealed record UsageReport(string Status, string? Message, IReadOnlyList<UsageApp> Apps, UsageSummary Summary,
        IReadOnlyList<UsageDevice> Devices, IReadOnlyDictionary<string, VersionBucket> VersionDistribution)
    {
        public bool Unavailable => Status == "unavailable";

        /// <summary>The line under the report title, as the web words it.</summary>
        public string Headline
        {
            get
            {
                var s = Summary;
                var parts = new List<string> { $"{s.AppsTracked:N0} apps", $"{s.Devices:N0} devices" };
                if (s.ActiveHours is { } active) parts.Add($"{Math.Round(active):N0} active hours");
                parts.Add($"{s.Launches:N0} launches");
                parts.Add($"{s.Users:N0} unique users");
                if (s.NoActiveUse > 0) parts.Add($"{s.NoActiveUse:N0} with no active use");
                return string.Join(" · ", parts);
            }
        }
    }

    public static UsageReport ReadUsage(JsonElement root)
    {
        var apps = Array(root, "applications").Select(a =>
        {
            var total = Insights.Number(a, "totalSeconds") ?? 0;
            return new UsageApp(Insights.Str(a, "name"), total, Insights.Number(a, "totalHours") ?? total / 3600,
                Insights.Number(a, "activeSeconds"), Insights.Number(a, "activeHours"), Insights.Number(a, "foregroundHours"),
                Insights.Number(a, "activeRatio"), Insights.Int(a, "launchCount"), Insights.Int(a, "deviceCount"),
                Insights.Int(a, "userCount"), NullableInt(a, "activeDeviceCount"), NullableInt(a, "activeUserCount"),
                NullableInt(a, "installedDeviceCount"), When(Insights.Str(a, "lastUsed")),
                Array(a, "devices").Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString() ?? "").ToList(),
                Insights.Boolish(a, "isSingleUser"));
        }).Where(a => a.Name.Length > 0).ToList();

        var s = Insights.Prop(root, "summary") is { ValueKind: JsonValueKind.Object } found ? found : (JsonElement?)null;
        var summary = s is { } sum
            ? new UsageSummary(Insights.Int(sum, "totalAppsTracked"), Insights.Number(sum, "totalActiveHours"),
                Insights.Number(sum, "totalUsageHours") ?? 0, Insights.Int(sum, "totalLaunches"), Insights.Int(sum, "uniqueUsers"),
                Insights.Int(sum, "uniqueDevices"), Insights.Int(sum, "noActiveUseAppCount"))
            : new UsageSummary(apps.Count, null, apps.Sum(a => a.TotalHours), apps.Sum(a => a.LaunchCount), 0,
                apps.SelectMany(a => a.Devices).Distinct(StringComparer.OrdinalIgnoreCase).Count(), 0);

        var devices = Array(root, "devicesAggregate").Select(d =>
        {
            var serial = Insights.Str(d, "serialNumber");
            var seconds = Insights.Number(d, "totalSeconds") ?? 0;
            return new UsageDevice(serial, Or(Insights.Str(d, "deviceName"), serial), Insights.Str(d, "usage"),
                Insights.Str(d, "catalog"), Insights.Str(d, "location"), Or(Insights.Str(d, "area"), Insights.Str(d, "department")),
                Insights.Str(d, "fleet"), Insights.Number(d, "totalHours") ?? seconds / 3600, Insights.Int(d, "launchCount"));
        }).Where(d => d.Serial.Length > 0).ToList();

        var message = Or(Insights.Str(root, "message"), Insights.Str(root, "error"));
        return new UsageReport(Insights.Str(root, "status"), message.Length > 0 ? message : null, apps, summary, devices,
            Insights.Prop(root, "versionDistribution") is { ValueKind: JsonValueKind.Object } vd
                ? ReadDistribution(vd) : new Dictionary<string, VersionBucket>());
    }

    // ── Usage widgets ───────────────────────────────────────────────────

    /// <summary>What the usage widgets add up: launches, the web's default, or hours.</summary>
    public enum UsageMetric { Launches, Hours }

    public sealed record Bin(string Label, double Min, double Max, int Count);

    /// <summary>
    /// The usage widgets, rolled up from the report's per-device totals in the web's
    /// order: by usage type, by catalog, the device distribution, then by fleet, by
    /// area and by location, the last two cut to their top ten.
    /// </summary>
    public sealed record UsageAggregates(double GrandTotal, int DeviceCount,
        IReadOnlyList<(string Label, double Value)> ByUsage, IReadOnlyList<(string Label, double Value)> ByCatalog,
        IReadOnlyList<Bin> Bins, IReadOnlyList<(string Label, double Value)> ByFleet,
        IReadOnlyList<(string Label, double Value)> ByArea, IReadOnlyList<(string Label, double Value)> ByLocation)
    {
        public bool HasBins => Bins.Any(b => b.Count > 0);
    }

    /// <summary>
    /// Roll the devices up by inventory dimension. With apps switched off, only the
    /// devices that used one still switched on are counted. As on the web this is an
    /// approximation: a device's totals still include its other apps' time, but the
    /// device set is what moves when a reader focuses on a few apps.
    /// </summary>
    public static UsageAggregates Aggregate(IReadOnlyList<UsageDevice> devices, IReadOnlyList<UsageApp> apps,
        IReadOnlySet<string> switchedOff, UsageMetric metric)
    {
        IEnumerable<UsageDevice> scope = devices;
        if (switchedOff.Count > 0)
        {
            var used = new HashSet<string>(apps.Where(a => !switchedOff.Contains(a.Name)).SelectMany(a => a.Devices),
                StringComparer.OrdinalIgnoreCase);
            scope = devices.Where(d => used.Contains(d.Serial));
        }
        var list = scope.ToList();
        double Value(UsageDevice d) => metric == UsageMetric.Hours ? d.TotalHours : d.LaunchCount;
        var total = list.Sum(Value);

        static bool Unknown(string k) => k.Trim().ToLowerInvariant() is "" or "unknown" or "null" or "n/a";
        IReadOnlyList<(string, double)> Sum(Func<UsageDevice, string> key) => list
            .Where(d => !Unknown(key(d)))
            .GroupBy(key)
            .Select(g => (g.Key, g.Sum(Value)))
            .Where(p => p.Item2 > 0)
            .OrderByDescending(p => p.Item2).ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        (string, double, double)[] defs = metric == UsageMetric.Hours
            ? [("0–10h", 0, 10), ("10–50h", 10, 50), ("50–100h", 50, 100), ("100–250h", 100, 250), ("250h+", 250, double.PositiveInfinity)]
            : [("0–10", 0, 10), ("10–50", 10, 50), ("50–250", 50, 250), ("250–1000", 250, 1000), ("1000+", 1000, double.PositiveInfinity)];
        var bins = defs.Select(b => new Bin(b.Item1, b.Item2, b.Item3,
            list.Count(d => Value(d) >= b.Item2 && Value(d) < b.Item3))).ToList();

        return new UsageAggregates(total == 0 ? 1 : total, list.Count,
            Sum(d => d.Usage), Sum(d => d.Catalog), bins, Sum(d => d.Fleet),
            Sum(d => d.Area).Take(10).ToList(), Sum(d => d.Location).Take(10).ToList());
    }

    /// <summary>A widget value as the web prints it: <c>12h</c>, or a plain count.</summary>
    public static string FormatMetric(double value, UsageMetric metric) => metric == UsageMetric.Hours
        ? $"{Math.Round(value).ToString("0", CultureInfo.InvariantCulture)}h"
        : Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The report's apps with the switched-off ones taken out, for the table and widgets.</summary>
    public static IReadOnlyList<UsageApp> SwitchedOn(IEnumerable<UsageApp> apps, IReadOnlySet<string> switchedOff) =>
        apps.Where(a => !switchedOff.Contains(a.Name)).ToList();

    /// <summary>The apps in the order the web defaults to: active time first, ties broken by process time.</summary>
    public static IReadOnlyList<UsageApp> ByActiveTime(IEnumerable<UsageApp> apps) =>
        apps.OrderByDescending(a => a.ActiveHours ?? 0).ThenByDescending(a => a.TotalHours)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The web's duration: <c>0m</c>, <c>45m</c>, <c>3h</c>, <c>3h 20m</c>.</summary>
    public static string Duration(double seconds)
    {
        if (!(seconds > 0)) return "0m";
        var hours = (long)Math.Floor(seconds / 3600);
        var minutes = (long)Math.Floor(seconds % 3600 / 60);
        if (hours == 0) return $"{minutes}m";
        return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        Insights.Prop(parent, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    private static int? NullableInt(JsonElement e, string name) => Format.ToInt(Insights.Number(e, name));

    private static DateTime? When(string text) =>
        text.Length > 0 && DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    // ── Version distribution ────────────────────────────────────────────

    public sealed record VersionBucket(int TotalDevices, IReadOnlyDictionary<string, int> Versions);

    /// <summary>
    /// <c>{app: {totalDevices, versions}}</c>, from the distribution endpoint, where a
    /// version maps to a count, or from the usage response, where it maps to
    /// <c>{count, devices}</c>. Anything else in the shape is skipped rather than thrown on.
    /// </summary>
    public static IReadOnlyDictionary<string, VersionBucket> ReadDistribution(JsonElement root)
    {
        var result = new Dictionary<string, VersionBucket>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object || Insights.Prop(root, "error") is not null) return result;
        foreach (var app in root.EnumerateObject())
        {
            if (app.Value.ValueKind != JsonValueKind.Object
                || Insights.Prop(app.Value, "versions") is not { ValueKind: JsonValueKind.Object } versions) continue;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var v in versions.EnumerateObject())
            {
                int? n = v.Value.ValueKind switch
                {
                    JsonValueKind.Number => Format.ToInt(v.Value.TryGetDouble(out var d) && double.IsFinite(d) ? d : null),
                    JsonValueKind.Object => Format.ToInt(Insights.Number(v.Value, "count"))
                        ?? (Insights.Prop(v.Value, "devices") is { ValueKind: JsonValueKind.Array } ds ? ds.GetArrayLength() : null),
                    _ => null,
                };
                if (n is { } count) counts[v.Name] = count;
            }
            var total = Format.ToInt(Insights.Number(app.Value, "totalDevices")) ?? counts.Values.Sum();
            result[app.Name] = new VersionBucket(total, counts);
        }
        return result;
    }

    /// <summary>
    /// Normalised app → version → devices. The server's counts are preferred when there
    /// are any, because the installed rows are capped and its counts are not; their
    /// keys are normalised the same way so a version card and the table's version
    /// filter agree on an app's name.
    /// </summary>
    public static IReadOnlyDictionary<string, Dictionary<string, int>> VersionAnalysis(
        IReadOnlyDictionary<string, VersionBucket>? server, IEnumerable<Installed> installed)
    {
        var analysis = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string version, int count)
        {
            if (!analysis.TryGetValue(key, out var target)) analysis[key] = target = new Dictionary<string, int>(StringComparer.Ordinal);
            target[version] = target.GetValueOrDefault(version) + count;
        }

        if (server is { Count: > 0 })
        {
            foreach (var (app, bucket) in server)
                foreach (var (version, count) in bucket.Versions)
                    Add(Normalize(app) is { Length: > 0 } n ? n : app, version, count);
            return analysis;
        }
        foreach (var app in installed)
            if (Normalize(app.Name) is { Length: > 0 } key)
                Add(key, app.Version.Length == 0 ? "Unknown" : app.Version, 1);
        return analysis;
    }

    public sealed record VersionCard(string App, int Total, IReadOnlyList<(string Version, int Count)> Versions);

    /// <summary>One card per app, most devices first, each listing its versions newest first.</summary>
    public static IReadOnlyList<VersionCard> VersionCards(IReadOnlyDictionary<string, Dictionary<string, int>> analysis) =>
        analysis.Select(kv => new VersionCard(kv.Key, kv.Value.Values.Sum(),
                kv.Value.OrderByDescending(v => v.Key, VersionComparer.Instance).Select(v => (v.Key, v.Value)).ToList()))
            .OrderByDescending(c => c.Total).ThenBy(c => c.App, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>"v2.1 (40), v2.0 (3) +2 more": the versions column of the usage table.</summary>
    public static string VersionsSummary(IReadOnlyDictionary<string, Dictionary<string, int>> analysis, string app)
    {
        if (!analysis.TryGetValue(Normalize(app), out var versions) && !analysis.TryGetValue(app, out versions)) return "";
        if (versions.Count == 0) return "";
        var sorted = versions.OrderByDescending(v => v.Key, VersionComparer.Instance).ToList();
        if (sorted.Count == 1) return "v" + sorted[0].Key;
        var shown = string.Join(", ", sorted.Take(3).Select(v => $"v{v.Key} ({v.Value})"));
        return sorted.Count > 3 ? $"{shown} +{sorted.Count - 3} more" : shown;
    }

    /// <summary>
    /// Versions compared the way the web's <c>localeCompare(…, {numeric: true})</c>
    /// does: runs of digits as numbers, so 10.2 is newer than 9.8.
    /// </summary>
    public sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var si = i; while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    var sj = j; while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    var c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    // ── CSV, as the web's export buttons write it ───────────────────────

    public static readonly string[] UsageCsvHeaders =
        ["Application", "Active Hours", "Foreground Hours", "Process Hours", "Launches", "Active Devices",
         "Devices Installed", "Active Users", "Users Attributed", "Last Used", "Single User"];

    public static IEnumerable<string?> UsageCsvRow(UsageApp a) =>
    [
        a.Name, a.ActiveHours?.ToString("0.0", CultureInfo.InvariantCulture),
        a.ForegroundHours?.ToString("0.0", CultureInfo.InvariantCulture),
        a.TotalHours.ToString("0.0", CultureInfo.InvariantCulture),
        a.LaunchCount.ToString(CultureInfo.InvariantCulture),
        a.ActiveDeviceCount?.ToString(CultureInfo.InvariantCulture),
        a.InstalledDeviceCount?.ToString(CultureInfo.InvariantCulture),
        a.ActiveUserCount?.ToString(CultureInfo.InvariantCulture),
        a.UserCount.ToString(CultureInfo.InvariantCulture),
        a.LastUsed?.ToString("o", CultureInfo.InvariantCulture), a.IsSingleUser ? "Yes" : "No",
    ];

    public static readonly string[] InstalledCsvHeaders =
        ["Application", "Version", "Usage", "Catalog", "Area", "Location", "Device", "Serial Number"];

    public static IEnumerable<string?> InstalledCsvRow(Installed a) =>
        [a.Name, a.Version, a.Usage, a.Catalog, a.Area, a.Location, a.Device, a.Serial];

    public static readonly string[] MissingCsvHeaders = ["Device", "Serial Number", "Location", "Catalog", "Missing Application"];

    public static IEnumerable<string?> MissingCsvRow(FilterDevice d, IReadOnlyList<string> apps) =>
        [d.Name, d.Serial, d.Location, d.Catalog, string.Join(", ", apps)];

    /// <summary>The file names the web downloads under, dated in the local day.</summary>
    public static string CsvFileName(State state, DateTime today)
    {
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (state.IsUsage) return $"applications-usage-{state.Period}days-{date}.csv";
        if (state.IsMissing)
        {
            var app = state.Apps.Count > 0 ? Regex.Replace(state.Apps[0], "[^a-zA-Z0-9]", "-", Safe) : "app";
            return $"missing-{app}-{date}.csv";
        }
        return $"applications-{date}.csv";
    }

    // ── Names: the web's normalizeAppName and shouldIncludeApplication ──

    /// <summary>
    /// Every pattern here runs on names devices report, so none may backtrack: a
    /// crafted name would otherwise hold the page on one regex for as long as it liked.
    /// </summary>
    private const RegexOptions Safe = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>Names longer than this are cut before they are normalised; no real one comes close.</summary>
    private const int MaxNameLength = 256;

    private static Regex R(string pattern, bool ignoreCase = true) =>
        new(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | Safe);

    private static readonly Regex AdobeProduct = R(@"Adobe ([A-Za-z\s]+)", ignoreCase: false);

    /// <summary>Dassault's suite, whose name the web's rules test for in capitals.</summary>
    private static readonly string ExperienceSuite = "3D" + "EXPERIENCE";

    /// <summary>Each runs once, first match only: the web's replaces carry no g flag.</summary>
    private static readonly Regex[] Strip =
    [
        R(@"\s+v?\d+(\.\d+)*$"), R(@"\s+\d{4}(\.\d+)*$"), R(@"\s+-\s+\d+(\.\d+)*$"),
        R(@"\s+\(\d+(\.\d+)*\)$"), R(@"\s+build\s+\d+"), R(@"\s+\d+(\.\d+)*$"),
        R(@"\s+\d{4}\.\d{2}-\d+"), R(@"\s+\d{1,2}\.\d+\.\d+"), R(@"\s+\d{4}"), R(@"\s+v\d+(\.\d+)*"),
        R(@"\s+(x64|x86|64-bit|32-bit|amd64|i386)$"), R(@"\s+\((x64|x86|64-bit|32-bit|amd64|i386)\)$"),
        R(@"\s+\(Python\s+[\d\.]+\s+(64-bit|32-bit)\)$"), R(@"\s+\(git\s+[a-f0-9]+\)$"),
        R(@"\s+\([^)]*bit[^)]*\)"), R(@"\s+\([^)]*\d+\.\d+\.\d+[^)]*\)"),
        R(@"\s+(Additional Runtime|Minimum Runtime|Redistributable|Shared Framework|Targeting Pack|AppHost Pack|Host FX Resolver|Hosting Support)$"),
        R(@"\s+-\s+(en-us|x64|x86|\d+(\.\d+)*)$"),
        R(@"\s+(Desktop|App|Application|Software|Program|Tool|Suite|Client|Server)$"),
        R(@"\s+(Pro|Professional|Standard|Basic|Free|Premium|Enterprise|Business|Personal|Home|Student|Education)$"),
        R(@"\s+(Trial|Beta|Alpha|RC|Release|Final|Portable|Standalone)$"),
        R(@"\s+Update\s+\d+"), R(@"\s+SP\d+"), R(@"\s+Patch\s+\d+"),
        R(@"\s+-\s+en-us$"), R(@"\s+for\s+Windows$"), R(@"\s+for\s+Microsoft\s+Windows$"), R(@"\s+Windows\s+Edition$"),
    ];

    private static readonly Regex Whitespace = R(@"\s+");
    private static readonly Regex TrailingDash = R(@"\s*-\s*$", ignoreCase: false);
    private static readonly Regex LeadingDash = R(@"^\s*-\s*", ignoreCase: false);

    /// <summary>
    /// The web's application-name normalisation, rule for rule, so a version card here
    /// is keyed as the web keys it and an <c>App:version</c> link means the same app.
    /// </summary>
    public static string Normalize(string? appName)
    {
        var n = (appName ?? "").Trim();
        if (n.Length > MaxNameLength) n = n[..MaxNameLength].Trim();
        if (n.Length == 0 || n.Contains("${{") || n.Contains("}}") || n is "Unknown" or "N/A") return "";

        bool Has(string pattern) => Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase | Safe);
        if (Has(@"Microsoft Visual C\+\+ \d{4}")) return "Microsoft Visual C++ Redistributable";
        if (n.StartsWith("Microsoft.NET", StringComparison.Ordinal) || n.Contains("Microsoft ASP.NET Core"))
        {
            if (n.Contains("Workload")) return "Microsoft .NET Workload";
            if (n.Contains("Sdk") || n.Contains("SDK")) return "Microsoft .NET SDK";
            if (n.Contains("ASP.NET Core")) return "Microsoft ASP.NET Core";
            if (n.Contains("Runtime") || n.Contains("AppHost") || n.Contains("Targeting Pack") || n.Contains("Host FX Resolver"))
                return "Microsoft .NET Runtime";
            return "Microsoft .NET";
        }
        if (n.Contains("Microsoft Visual Studio Tools")) return "Microsoft Visual Studio Tools";
        if (Has("Microsoft (365|Office 365)")) return "Microsoft 365";
        if (Has("Kinect for Windows Speech Recognition Language Pack")) return "Kinect for Windows Speech Recognition Language Pack";
        if (Has("Microsoft.*Language Pack")) return "Microsoft Language Pack";
        if (Has("Kits Configuration Installer")) return "Kits Configuration Installer";
        if (Has("Kofax VRS")) return "Kofax VRS";
        if (n.StartsWith("Adobe ", StringComparison.Ordinal) && AdobeProduct.Match(n) is { Success: true } adobe)
        {
            var product = Whitespace.Split(adobe.Groups[1].Value)[0];
            if (product.Length > 0 && product != "AIR") return "Adobe " + product;
        }
        if (Has("^7-Zip")) return "7-Zip";
        if (Has("Google Chrome|Chrome")) return "Google Chrome";
        if (Has("Mozilla Firefox|Firefox")) return "Mozilla Firefox";
        if (n.StartsWith("AMD ", StringComparison.Ordinal) && (n.Contains("Driver") || n.Contains("Chipset")))
            return n.Contains("Chipset") ? "AMD Chipset Software" : "AMD Drivers";
        if (n.Contains("SOLIDWORKS")) return n.Contains(ExperienceSuite) ? ExperienceSuite + " for SOLIDWORKS" : "SOLIDWORKS";
        if (n.StartsWith("HP ", StringComparison.Ordinal) || n.Contains("HP ")) return "HP Software";

        foreach (var pattern in Strip) n = pattern.Replace(n, "", 1);
        n = Whitespace.Replace(n, " ");
        n = TrailingDash.Replace(n, "", 1);
        n = LeadingDash.Replace(n, "", 1);
        n = n.Trim();
        return n.Length < 2 ? "" : n;
    }

    private static readonly Regex[] Excluded =
    [
        R(@"^Microsoft\.NET\.Workload\."), R(@"^Microsoft\.NET\.Sdk\."), R("^Windows Software Development Kit"),
        R("^Microsoft Visual Studio Installer$"), R("Update for Windows"), R("Security Update for Microsoft"), R(@"^KB\d+"),
        R("^64 Bit HP CIO Components Installer"), R("^1394 OHCI Compliant Host Controller"), R("^AVG.*Helper$"),
        R("^AVG.*Browser$"), R("^AVerMedia.*HD Series"), R("^AVerMedia RECentral$"),
        R(@"Microsoft Visual C\+\+ \d{4} x\d{2} (Additional|Minimum) Runtime"),
        R(@"Microsoft .NET (Runtime|AppHost Pack|Targeting Pack|Host FX Resolver) - [\d\.]+ \(x\d+"),
        R(@"Microsoft ASP.NET Core [\d\.]+ (Shared Framework|Targeting Pack) \(x\d+"),
        R(@"^\$\{\{.*\}\}$"), R("^Unknown$"), R("^N/A$"), R(@"^\s*$"),
    ];

    /// <summary>Installer, runtime, patch and placeholder rows the report leaves out, as the web does.</summary>
    public static bool ShouldInclude(string? appName)
    {
        var t = (appName ?? "").Trim();
        if (t.Length > MaxNameLength) t = t[..MaxNameLength];
        if (t.Length == 0 || t.Contains("${{") || t.Contains("}}") || t is "Unknown" or "N/A") return false;
        return !Excluded.Any(r => r.IsMatch(t));
    }
}
