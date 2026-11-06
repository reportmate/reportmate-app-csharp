using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReportMate.App.Services;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>System › Services, Updates, Background and Extensions.</summary>
public static partial class TabReports
{
    // ── Services ─────────────────────────────────────────────────────────

    public static readonly string[] ServiceStates = ["running", "stopped", "mixed", "missing", "unknown"];

    public static string ServiceStateLabel(string? value) => value switch
    {
        "missing" => "Not found",
        null or "" or "unknown" => "Unknown",
        _ => char.ToUpperInvariant(value[0]) + value[1..],
    };

    private static bool HasApplication(TabQuery q) => q.Has("applicationNames") || q.Has("application");

    /// <summary>Selected names, resolving an old link's substring filter against what devices reported.</summary>
    private static IReadOnlyList<string> Legacy(TabContext c, string exact, string substring, string list, Func<JsonElement, string> haystack, string valueField)
    {
        if (c.Query.Has(exact)) return c.Query.List(exact);
        if (c.Query[substring]?.ToLowerInvariant() is not { } needle) return [];
        return TabJson.Array(c.Catalog, list)
            .Where(e => haystack(e).ToLowerInvariant().Contains(needle))
            .Select(e => TabJson.Str(e, valueField) ?? "").Where(s => s.Length > 0).ToList();
    }

    private static IReadOnlyList<string> SelectedServices(TabContext c) =>
        Legacy(c, "serviceNames", "service", "services", e => $"{TabJson.Str(e, "name")} {TabJson.Str(e, "displayName")}", "name");

    private static IReadOnlyList<string> SelectedApplications(TabContext c) =>
        Legacy(c, "applicationNames", "application", "applications", e => TabJson.Str(e, "name") ?? "", "name");

    private static string ServiceName(TabRow s) => s.Str("name") ?? s.Str("displayName") ?? "";

    public static double? CheckinAge(TabRow r) => AgeHours(r.Str("lastSeen"), r.Report?.Snapshot ?? DateTime.UtcNow);

    public static string CheckinState(double? age, double maximum) => age is null ? "unknown" : age <= maximum ? "recent" : "stale";

    private static double CheckinMaximum(TabQuery q) =>
        double.TryParse(q["checkinAgeHours"], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0 ? Math.Min(h, 87600) : 168;

    public static bool MatchesEvidence(int services, int applications, string evidence) => evidence switch
    {
        "any" => services > 0 || applications > 0,
        "services" => services > 0,
        "applications" => applications > 0,
        "none" => services == 0 && applications == 0,
        _ => true,
    };

    private static string CollectionText(TabRow r, string at = "collectedAt", string freshness = "freshness") =>
        r.Str(at) is { } when
            ? $"{Format.RelativeTime(TabJson.Time(when))} · {FreshnessLabel(r.Str(freshness))}"
            : "Unknown · Collection age unknown";

    public static readonly TabReportDef Services = new()
    {
        Module = "system", Tab = "services", Title = "Services",
        Description = "Service coverage and installed versions across devices.",
        Footnote = "Last collected local state; matching evidence can be stale. Device check-in is ReportMate activity. A running service does not prove vendor connectivity.",
        Api = "/api/v1/system/services", FiltersApi = "/api/v1/system/services/filters",
        RowsProperty = "devices", PageSize = 500, RowNoun = "devices",
        ApiParams = WithScope("service", "application", "serviceNames", "applicationNames", "versions", "states", "startups", "freshness", "maxAgeHours"),
        RowKey = r => r.Serial,
        HasQuery = q => q.Has("serviceNames") || q.Has("service"),
        Prompt = "Choose services from Selections to build your report. Pick the collected services you want to compare.",
        Selections =
        [
            new("serviceNames", "Services", c => TabJson.Array(c.Catalog, "services")
                .Select(e => new TabOption(TabJson.Str(e, "name") ?? "", TabJson.Str(e, "displayName") ?? TabJson.Str(e, "name") ?? "", TabJson.Int(e, "devices")))
                .Where(o => o.Value.Length > 0).ToList())
            { Selected = SelectedServices, Resets = ["service"] },
            new("applicationNames", "Applications", c => TabJson.Array(c.Catalog, "applications")
                .Select(e => new TabOption(TabJson.Str(e, "name") ?? "", TabJson.Str(e, "name") ?? "", TabJson.Int(e, "devices")))
                .Where(o => o.Value.Length > 0).ToList())
            { Selected = SelectedApplications, Resets = ["application", "versions"] },
        ],
        Choices =
        [
            new TabChoice("versions", "Versions")
            {
                AllLabel = "All versions",
                Visible = c => HasApplication(c.Query),
                From = c =>
                {
                    var apps = SelectedApplications(c);
                    return TabJson.Array(c.Catalog, "applications")
                        .Where(e => apps.Contains(TabJson.Str(e, "name") ?? ""))
                        .SelectMany(e => TabJson.Array(e, "versions").Select(TabJson.Text))
                        .Where(v => v is not null).Select(v => v!).Distinct()
                        .OrderByDescending(v => v, Comparer<string>.Create(SortKey.Natural))
                        .Select(v => new TabOption(v, v)).ToList();
                },
            },
            new TabChoice("startups", "Startup type")
            {
                From = c =>
                {
                    var names = SelectedServices(c);
                    return TabJson.Array(c.Catalog, "services")
                        .Where(e => names.Contains(TabJson.Str(e, "name") ?? ""))
                        .SelectMany(e => TabJson.Array(e, "startups").Select(TabJson.Text))
                        .Where(v => v is not null).Select(v => v!).Distinct().Order(StringComparer.Ordinal)
                        .Select(v => new TabOption(v, ServiceStateLabel(v))).ToList();
                },
                Visible = c => c.Query.Has("serviceNames") || c.Query.Has("service"),
            },
            .. Ages("Service collection age", "24"),
            new TabChoice("checkin", "Device check-in age", ChoiceMode.Single)
            {
                Default = "all",
                Options = [new("all", "All"), new("recent", "Within maximum age"), new("stale", "Older"), new("unknown", "Unknown")],
                Client = (r, v, q) => CheckinState(CheckinAge(r), CheckinMaximum(q)) == v,
            },
            new TabChoice("checkinAgeHours", "Maximum check-in age", ChoiceMode.Single)
            {
                Default = "168",
                From = c => new[] { "24", "168", "720", c.Query["checkinAgeHours"] ?? "168" }.Distinct().Select(v => new TabOption(v, HoursLabel(v))).ToList(),
                // A view setting: it changes what "Older" means, not which rows load.
                Client = (_, _, _) => true,
            },
            .. Scope("Service collection age", "24").Where(c => c.Param is not ("freshness" or "maxAgeHours")),
            new TabChoice("states", "Service state")
            {
                AllLabel = "All states", Prominent = true, SummaryKey = "",
                Options = ServiceStates.Select(s => new TabOption(s, ServiceStateLabel(s))).ToList(),
            },
            new TabChoice("evidence", "Matching evidence", ChoiceMode.Single)
            {
                Default = "all", Prominent = true,
                From = c => new TabOption[] { new("all", "All devices"), new("any", "Any matching evidence"), new("services", "Matching services"),
                        new("applications", "Matching applications"), new("none", "No matching evidence") }
                    .Where(o => HasApplication(c.Query) || o.Value != "applications").ToList(),
                Client = (r, v, _) => MatchesEvidence(r.Items("services").Count, r.Items("applications").Count, v),
            },
        ],
        Columns =
        [
            DeviceColumn(),
            new("serialNumber", "Serial number", r => r.Serial, TabCell.Mono),
            new("assetTag", "Asset tag", r => r.Str("assetTag")),
            new("platform", "Platform", r => r.Platform),
            new("state", "Service state", r => ServiceStateLabel(r.Str("state")), TabCell.State)
            {
                Display = r => r.Str("state") is "unknown" && r.Items("services").Count > 0
                    ? $"Unknown (last reported: {ServiceStateLabel(r.Str("reportedState"))})"
                    : ServiceStateLabel(r.Str("state")),
            },
            new("services", "Matching services", r => string.Join("; ", r.Items("services").Select(ServiceName).Order(StringComparer.Ordinal)))
            {
                Width = 300,
                Display = r => r.Items("services") is { Count: > 0 } s
                    ? string.Join("; ", s.Select(x => $"{(ServiceName(x) is { Length: > 0 } n ? n : "Unnamed service")} {x.Str("status") ?? "Unknown state"} · {x.Str("startType") ?? x.Str("type") ?? "Startup type unknown"}"))
                    : r.Str("state") == "missing" ? "Not found in service inventory" : "No matching service reported; coverage unknown",
            },
            new("applications", "Application versions", r => string.Join("; ", r.Items("applications").Select(a => $"{a.Str("name")} {a.Str("version")}".Trim()).Order(StringComparer.Ordinal)))
            {
                Width = 260, Empty = "No matching application reported",
                Display = r => string.Join("; ", r.Items("applications").Select(a => $"{a.Str("name")} {a.Str("version") ?? "Version not reported"}")),
            },
            new("inventoryState", "Inventory", r => r.Str("inventoryState") is null ? null : InventoryLabel(r.Str("inventoryState")) + (r.Bool("archived") == true ? " (archived)" : ""))
            { Empty = "Unknown" },
            new("usage", "Usage", r => r.Str("usage")),
            new("catalog", "Catalog", r => r.Str("catalog")),
            new("area", "Area", r => r.Str("area")),
            new("fleet", "Fleet", r => r.Str("fleet")),
            new("location", "Location", r => r.Str("location")),
            new("room", "Room", r => r.Str("room")),
            new("collectedAt", "Service collection", r => r.Str("collectedAt"), TabCell.Time) { Display = r => CollectionText(r), Width = 220 },
            new("applicationsCollectedAt", "Application collection", r => r.Str("applicationsCollectedAt"), TabCell.Time)
            { Display = r => CollectionText(r, "applicationsCollectedAt", "applicationsFreshness"), Width = 220 },
            new("lastSeen", "Last seen", r => r.Str("lastSeen"), TabCell.Time),
            new("checkinAge", "Check-in age", r => CheckinAge(r) is { } h ? Invariant(h) : null, TabCell.Number)
            { Display = r => CheckinAge(r) is { } h ? Hours(h) : null, Empty = "Unknown", Rank = CheckinAge },
        ],
        Available = (c, col) => HasApplication(c.Query) || col.Key is not ("applications" or "applicationsCollectedAt"),
        DefaultColumns = _ => ["deviceName", "state", "services", "applications", "inventoryState", "collectedAt", "lastSeen"],
        CsvName = "services-report",
        CsvHeaders = _ =>
        [
            "Device Name", "Hostname", "Serial Number", "Asset Tag", "Platform", "Inventory State", "Archived in ReportMate",
            "Usage", "Catalog", "Area", "Fleet", "Location", "Room", "Service State", "Last Reported Service State",
            "Matching Services", "Reported Service States", "Startup Types", "Executable Paths", "Application Versions",
            "Service Collection Time", "Service Collection Freshness", "Application Collection Time", "Application Collection Freshness",
            "Last Seen", "Report Generated At", "Report URL",
            "Matching Service Reported", "Matching Application Reported", "Application Paths",
            "ReportMate Check-in Age (Hours)", "ReportMate Check-in Freshness", "Maximum Check-in Age (Hours)",
        ],
        CsvRow = (r, c, url) =>
        {
            var services = r.Items("services");
            var apps = r.Items("applications");
            var max = CheckinMaximum(c.Query);
            var age = CheckinAge(r);
            return
            [
                r.Str("deviceName"), r.Str("hostname"), r.Serial, r.Str("assetTag"), r.Platform,
                InventoryLabel(r.Str("inventoryState")), r.Bool("archived") == true ? "Yes" : "No",
                r.Str("usage"), r.Str("catalog"), r.Str("area"), r.Str("fleet"), r.Str("location"), r.Str("room"),
                ServiceStateLabel(r.Str("state")), ServiceStateLabel(r.Str("reportedState")),
                string.Join("\n", services.Select(ServiceName)),
                string.Join("\n", services.Select(s => $"{ServiceName(s)}: {s.Str("status") ?? "Unknown"}")),
                string.Join("\n", services.Select(s => $"{ServiceName(s)}: {s.Str("startType") ?? "Unknown"}")),
                string.Join("\n", services.Select(s => $"{ServiceName(s)}: {s.Str("path") ?? "Not reported"}")),
                string.Join("\n", apps.Select(a => $"{a.Str("name")}: {a.Str("version") ?? "Version not reported"}")),
                r.Str("collectedAt"), r.Str("freshness"), r.Str("applicationsCollectedAt"), r.Str("applicationsFreshness"),
                r.Str("lastSeen"), c.Data?.GeneratedAt, url,
                services.Count > 0 ? "Yes" : "No", apps.Count > 0 ? "Yes" : "No",
                string.Join("\n", apps.Select(a => $"{a.Str("name")}: {a.Str("path") ?? "Not reported"}")),
                age?.ToString("0.00", CultureInfo.InvariantCulture), CheckinState(age, max), Invariant(max),
            ];
        },
    };

    // ── Updates ──────────────────────────────────────────────────────────

    public static readonly string[] UpdateStates = ["pending", "installed", "not_reported", "none", "unknown"];

    public static string UpdateStateLabel(string? state) => state switch
    {
        "pending" => "Pending",
        "installed" => "Installed",
        "not_reported" => "Not reported",
        "none" => "None pending reported",
        "missing" => "No collection",
        _ => "Unknown",
    };

    private static bool HasUpdateSelection(TabQuery q) => q.List("updates").Count > 0 || q.List("cves").Count > 0;

    public static IReadOnlyList<string> UpdateFlags(TabRow r) =>
        new[]
        {
            r.Bool("isSecurity") == true ? "Security" : null,
            r.Bool("mandatory") == true ? "Mandatory" : null,
            r.Bool("downloaded") == true ? "Downloaded" : null,
            r.Bool("rebootRequired") == true ? "Restart required" : null,
            r.Bool("deferred") == true ? "Deferred" : null,
        }.Where(f => f is not null).Select(f => f!).ToList();

    public static readonly TabReportDef Updates = new()
    {
        Module = "system", Tab = "updates", Title = "Updates",
        Description = "Pending and installed operating system updates across devices.",
        Footnote = "Each device in scope shows the selected updates as pending or installed. Not reported means the update is in neither the pending list nor the recent install history, which is not proof it is missing.",
        Api = "/api/v1/system/updates", FiltersApi = "/api/v1/system/updates/filters", FiltersParams = ["includeArchived"],
        PageSize = 1000, RowNoun = "rows",
        ApiParams = WithScope("updates", "cves", "states", "securityOnly", "deferred", "rebootRequired", "minAgeDays", "freshness", "maxAgeHours"),
        RowKey = r => $"{r.Serial}|{r.Str("updateKey")}|{r.Str("reportedState")}",
        Selections =
        [
            new("updates", "Updates", c => TabJson.Array(c.Catalog, "updates").Select(e =>
            {
                var key = TabJson.Str(e, "key") ?? "";
                var kb = TabJson.Str(e, "kb");
                var title = TabJson.Str(e, "title") ?? kb ?? key;
                var label = kb is not null && !title.Contains(kb, StringComparison.Ordinal) ? $"{kb} {title}" : title;
                if (c.Platform == "all") label += TabJson.Str(e, "platform") == "windows" ? " (Windows)" : " (macOS)";
                return new TabOption(key, label, Count(TabJson.Int(e, "pendingDevices")) + Count(TabJson.Int(e, "installedDevices")));
            }).Where(o => o.Value.Length > 0).ToList()),
            new("cves", "CVEs", c => TabJson.Array(c.Catalog, "cves")
                .Select(e => new TabOption(TabJson.Str(e, "cve") ?? "", TabJson.Str(e, "cve") ?? "", TabJson.Int(e, "devices")))
                .Where(o => o.Value.Length > 0).ToList()),
        ],
        Choices =
        [
            new TabChoice("securityOnly", "Update type", ChoiceMode.Toggle) { Options = [new("true", "Security only")] },
            new TabChoice("rebootRequired", "Restart", ChoiceMode.Toggle) { Options = [new("true", "Restart required")] },
            new TabChoice("deferred", "Deferred (macOS)", ChoiceMode.Single)
            { Default = "all", Options = [new("all", "All"), new("yes", "Deferred"), new("no", "Not deferred")] },
            new TabChoice("minAgeDays", "Pending at least", ChoiceMode.Single)
            {
                Default = "0",
                From = c => new[] { "0", "7", "30", "90", c.Query["minAgeDays"] ?? "0" }.Distinct()
                    .Select(v => new TabOption(v, v == "0" ? "Any age" : $"{v} days")).ToList(),
            },
            .. Scope("Update collection age", "24"),
            new TabChoice("states", "Update state")
            {
                AllLabel = "All states", Prominent = true, SummaryKey = "",
                From = c => UpdateStates
                    .Where(s => HasUpdateSelection(c.Query) ? s != "none" : s is not ("installed" or "not_reported"))
                    .Select(s => new TabOption(s, UpdateStateLabel(s))).ToList(),
            },
        ],
        Columns =
        [
            DeviceColumn(),
            new("serialNumber", "Serial number", r => r.Serial, TabCell.Mono),
            new("assetTag", "Asset tag", r => r.Str("assetTag")),
            new("platform", "Platform", r => r.Platform),
            new("osVersion", "OS version", r => r.Str("osVersion")),
            new("state", "State", r => UpdateStateLabel(r.Str("state")), TabCell.State)
            {
                Display = r => r.Str("state") is "unknown" or null
                    ? r.Bool("collectionValid") == true ? $"Unknown (last reported: {UpdateStateLabel(r.Str("reportedState"))})" : "Unknown (no valid update collection)"
                    : UpdateStateLabel(r.Str("state")),
            },
            new("title", "Update", r => r.Str("title") ?? r.Str("kb"))
            {
                Width = 300,
                Display = r => r.Str("updateKey") is null
                    ? r.Str("state") switch
                    {
                        "none" => "Empty pending list reported",
                        "not_reported" => "Selection not reported pending or installed",
                        _ => "No update data",
                    }
                    : string.Join(" · ", new[] { r.Str("title") ?? r.Str("kb"), r.Str("kb"), r.Str("productKey"), r.Str("buildVersion") }.Where(s => s is not null).Distinct()),
            },
            new("category", "Category", r => r.Str("category")) { Empty = "-" },
            new("severity", "Severity", r => r.Str("severity")) { Empty = "-" },
            new("cves", "CVEs", r => string.Join(" ", r.Strs("cves"))) { Empty = "-", Width = 200 },
            new("flags", "Flags", r => string.Join(" ", UpdateFlags(r))) { Empty = "", Display = r => string.Join(", ", UpdateFlags(r)) },
            new("version", "Version", r => r.Str("version")) { Empty = "-" },
            new("ageDays", "Age (days)", r => r.Str("ageDays"), TabCell.Number) { Empty = "-" },
            new("deferredUntil", "Deferred until", r => r.Str("deferredUntil"), TabCell.Time) { Empty = "-" },
            new("installDate", "Installed", r => r.Str("installDate"), TabCell.Time) { Empty = "-" },
            new("inventoryState", "Inventory", r => r.Str("inventoryState") is null ? null : InventoryLabel(r.Str("inventoryState")) + (r.Bool("archived") == true ? " (archived)" : ""))
            { Empty = "Unknown" },
            new("usage", "Usage", r => r.Str("usage")) { Empty = "-" },
            new("catalog", "Catalog", r => r.Str("catalog")) { Empty = "-" },
            new("area", "Area", r => r.Str("area")) { Empty = "-" },
            new("fleet", "Fleet", r => r.Str("fleet")) { Empty = "-" },
            new("location", "Location", r => r.Str("location")) { Empty = "-" },
            new("room", "Room", r => r.Str("room")) { Empty = "-" },
            new("collectedAt", "Update collection", r => r.Str("collectedAt"), TabCell.Time) { Display = r => CollectionText(r), Width = 220, Empty = "Not reported" },
            new("lastSeen", "Last seen", r => r.Str("lastSeen"), TabCell.Time) { Empty = "Not reported" },
        ],
        DefaultColumns = _ => ["deviceName", "state", "title", "category", "severity", "cves", "flags", "ageDays", "collectedAt"],
        CsvName = "updates-report",
        CsvHeaders = _ =>
        [
            "Device Name", "Hostname", "Serial Number", "Asset Tag", "Platform", "OS Version", "Inventory State",
            "Archived in ReportMate", "Usage", "Catalog", "Area", "Fleet", "Location", "Room",
            "State", "Last Reported State", "Update", "KB", "Product Key", "Category", "Severity", "CVEs", "Security",
            "Mandatory", "Downloaded", "Restart Required", "Deferred", "Deferred Until", "First Offered", "Released",
            "Installed", "Version", "Build", "Update Type", "Age (Days)", "Age Measured From",
            "Pending Updates Reported", "Update Collection Valid", "Update Collection Time", "Update Collection Freshness",
            "Last Seen", "Report Generated At", "Report URL",
        ],
        CsvRow = (r, c, url) =>
        {
            var has = r.Str("updateKey") is not null;
            return
            [
                r.Str("deviceName"), r.Str("hostname"), r.Serial, r.Str("assetTag"), r.Platform, r.Str("osVersion"),
                InventoryLabel(r.Str("inventoryState")), r.Bool("archived") == true ? "Yes" : "No",
                r.Str("usage"), r.Str("catalog"), r.Str("area"), r.Str("fleet"), r.Str("location"), r.Str("room"),
                UpdateStateLabel(r.Str("state")), UpdateStateLabel(r.Str("reportedState")), r.Str("title"), r.Str("kb"), r.Str("productKey"),
                r.Str("category"), r.Str("severity"), string.Join(" ", r.Strs("cves")),
                has ? YesNo(r.Bool("isSecurity")) : null, has ? YesNo(r.Bool("mandatory")) : null, has ? YesNo(r.Bool("downloaded")) : null,
                has ? YesNo(r.Bool("rebootRequired")) : null, has ? YesNo(r.Bool("deferred")) : null,
                r.Str("deferredUntil"), r.Str("firstOfferedAt"), r.Str("releaseDate"), r.Str("installDate"), r.Str("version"), r.Str("buildVersion"),
                r.Str("updateType"), r.Str("ageDays"), r.Str("ageSource"), r.Str("pendingCount"), r.Bool("collectionValid") == true ? "Yes" : "No",
                r.Str("collectedAt"), r.Str("freshness"), r.Str("lastSeen"), c.Data?.GeneratedAt, url,
            ];
        },
    };

    // ── Background and Extensions ────────────────────────────────────────

    private static string ItemPlatform(string platform) => platform switch { "Windows" => "windows", "macOS" => "macos", _ => "all" };

    private static readonly IReadOnlyList<TabColumn> ItemTrailing =
    [
        new("inventoryState", "Inventory", r => InventoryLabel(r.Str("inventoryState")) + (r.Bool("archived") == true ? " (archived)" : "")),
        new("usage", "Usage", r => r.Str("usage")),
        new("catalog", "Catalog", r => r.Str("catalog")),
        new("room", "Room", r => r.Str("room")),
        new("collectedAt", "System collection", r => r.Str("collectedAt"), TabCell.Time),
        new("freshness", "Collection age", r => FreshnessLabel(r.Str("freshness")), TabCell.State),
        new("lastSeen", "Last seen", r => r.Str("lastSeen"), TabCell.Time),
    ];

    private static TabColumn[] ItemLeading() =>
    [
        DeviceColumn(),
        new("serialNumber", "Serial number", r => r.Serial, TabCell.Mono),
        new("platform", "Platform", r => r.Platform),
    ];

    private static string Label(IReadOnlyDictionary<string, string> labels, string? value, string fallback = "Unknown") =>
        value is not null && labels.TryGetValue(value, out var label) ? label : fallback;

    /// <summary>A text value that may be a boolean or a list, read as the web's text() does.</summary>
    public static string? ItemText(TabRow r, string path) => r.Node(path) is not { } e ? null : e.ValueKind switch
    {
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(" ", e.EnumerateArray().Select(x => TabJson.Text(x) ?? x.GetRawText())) is { Length: > 0 } s ? s : null,
        JsonValueKind.Object => e.GetRawText(),
        _ => TabJson.Text(e),
    };

    /// <summary>Task Scheduler reports 1999-11-30 for a task that never ran and 1899-12-30 when no run is scheduled.</summary>
    public static string? TaskTime(string? raw, string sentinel) =>
        raw is null ? null : int.TryParse(raw.Length >= 4 ? raw[..4] : "", out var year) && year is > 0 and < 2000 ? sentinel : raw;

    private static TabReportDef Items(string tab, string title, string api, string description, string footnote, string prompt,
        (string Key, string Label)[] kinds, TabChoice[] filters, TabColumn[] columns, string[] defaults, Func<TabQuery, bool> runsWithout,
        string? notOn = null, string notText = "") => new()
    {
        Module = "system", Tab = tab, Title = title, Description = description, Footnote = footnote,
        Api = api, FiltersApi = api + "/filters", PlatformValue = ItemPlatform,
        PageSize = 2000, MaxRows = 60000, RowNoun = "rows",
        ApiParams = WithScope(["items", "kinds", .. filters.Select(f => f.Param), "freshness", "maxAgeHours"]),
        HasQuery = q => q.List("items").Any(i => i.Contains(':')) || runsWithout(q),
        Prompt = prompt,
        NotApplicableOn = notOn, NotApplicableText = notText,
        Selections =
        [
            new("items", "Items", c =>
            {
                var chosen = c.Query.List("kinds");
                return TabJson.Array(c.Catalog, "items")
                    .Where(e => chosen.Count == 0 || chosen.Contains(TabJson.Str(e, "kind") ?? ""))
                    .Select(e => new TabOption($"{TabJson.Str(e, "kind")}:{TabJson.Str(e, "key")}", TabJson.Str(e, "key") ?? "", TabJson.Int(e, "devices")))
                    .Where(o => o.Label.Length > 0).ToList();
            }) { Selected = c => c.Query.List("items").Where(i => i.Contains(':')).ToList() },
        ],
        Choices =
        [
            new TabChoice("kinds", "Item kinds") { Options = kinds.Select(k => new TabOption(k.Key, k.Label)).ToList() },
            .. filters,
            .. Scope("System collection age", "24"),
        ],
        Columns = [.. ItemLeading(), .. columns, .. ItemTrailing],
        DefaultColumns = _ => defaults,
        CsvName = tab == "background" ? "background-items-report" : "extensions-report",
        CsvHeaders = _ => [.. ItemLeading().Concat(columns).Concat(ItemTrailing).Select(c => c.Label), "Hostname", "Asset Tag", "Area", "Fleet", "Location", "Report Generated At", "Report URL"],
        CsvRow = (r, c, url) =>
            ItemLeading().Concat(columns).Concat(ItemTrailing)
                .Select(col => col.Applies(r) ? col.Value(r) : "Not applicable")
                .Concat([r.Str("hostname"), r.Str("assetTag"), r.Str("area"), r.Str("fleet"), r.Str("location"), c.Data?.GeneratedAt, url]),
        Caption = (c, shown) => c.Data is not { } d ? null
            : $"{shown:N0} rows on {Count(TabJson.Int(d.Root, "devicesReporting")):N0} devices"
              + (TabJson.Int(d.Root, "devicesWithoutInventory") is > 0 and var missing
                  ? $". {missing:N0} of {Count(TabJson.Int(d.Root, "devicesInScope")):N0} devices in scope have no usable inventory for this report; their items are unknown."
                  : ""),
    };

    private static readonly Dictionary<string, string> BackgroundKinds = new() { ["task"] = "Scheduled task", ["launchd"] = "launchd job", ["loginItem"] = "Open at Login" };
    private static readonly Dictionary<string, string> LastRunLabels = new()
    {
        ["succeeded"] = "Succeeded", ["failed"] = "Failed", ["running"] = "Running", ["notRun"] = "Has not run",
        ["informational"] = "Informational", ["unknown"] = "Unknown", ["notApplicable"] = "Not applicable",
    };
    private static readonly Dictionary<string, string> OriginLabels = new() { ["builtIn"] = "Built in", ["thirdParty"] = "Third party", ["unknown"] = "Unknown" };
    private static readonly Dictionary<string, string> EnabledLabels = new() { ["enabled"] = "Enabled", ["disabled"] = "Disabled", ["unknown"] = "Unknown" };

    private static TabChoice Labelled(string param, string label, string summaryKey, IEnumerable<KeyValuePair<string, string>> labels) =>
        new(param, label) { SummaryKey = summaryKey, Options = labels.Select(kv => new TabOption(kv.Key, kv.Value)).ToList() };

    public static readonly TabReportDef Background = Items("background", "Background", "/api/v1/system/background",
        "Scheduled tasks, launchd jobs and Open at Login items across devices.",
        "Services covers Windows services and the launchd jobs macOS reports as loaded, with running state. This tab covers what Services does not: launchd jobs configured on disk, Open at Login items and Windows scheduled tasks. Rows list reported items only; a device that does not list an item may not have reported it.",
        "Choose scheduled tasks, launchd jobs or Open at Login items from Selections, or show failed scheduled tasks across the fleet.",
        [("task", "Scheduled tasks"), ("launchd", "launchd jobs"), ("loginItem", "Open at Login")],
        [
            Labelled("enabled", "Enabled", "enabledState", EnabledLabels),
            Labelled("lastRuns", "Task last result", "lastRun", LastRunLabels.Where(kv => kv.Key != "notApplicable")),
            Labelled("origins", "Source", "origin", OriginLabels),
        ],
        [
            new("kind", "Kind", r => Label(BackgroundKinds, r.Kind, r.Kind)),
            new("name", "Item", r => ItemText(r, r.Kind == "task" ? "path" : "name"), TabCell.Mono) { Width = 260 },
            new("enabledState", "Enabled", r => Label(EnabledLabels, r.Str("enabledState")), TabCell.State),
            new("state", "Reported state", r => ItemText(r, "state")),
            new("origin", "Source", r => Label(OriginLabels, r.Str("origin"))),
            new("lastRun", "Last result", r => Label(LastRunLabels, r.Str("lastRun")), TabCell.State) { AppliesTo = ["task"] },
            new("lastRunCode", "Last result code", r => ItemText(r, "lastRunCode"), TabCell.Mono) { AppliesTo = ["task"] },
            new("lastRunMessage", "Last result message", r => ItemText(r, "lastRunMessage")) { AppliesTo = ["task"], Width = 240 },
            new("lastRunTime", "Last run", r => TaskTime(ItemText(r, "lastRunTime"), "Never"), TabCell.Time) { AppliesTo = ["task"] },
            new("nextRunTime", "Next run", r => TaskTime(ItemText(r, "nextRunTime"), "Not scheduled"), TabCell.Time) { AppliesTo = ["task"] },
            new("type", "Type", r => ItemText(r, "type")),
            new("source", "Reported source", r => ItemText(r, "source")),
            new("runAtLoad", "Run at load", r => r.Kind == "launchd" ? YesNo(r.Bool("runAtLoad")) : null) { AppliesTo = ["launchd"] },
            new("keepAlive", "Keep alive", r => r.Kind == "launchd" ? YesNo(r.Bool("keepAlive")) : null) { AppliesTo = ["launchd"] },
            new("detail", "Program or action", r => r.Kind switch
            {
                "task" => ItemText(r, "action"),
                "launchd" => string.Join(" ", new[] { ItemText(r, "program"), ItemText(r, "arguments") }.Where(s => s is not null)) is { Length: > 0 } s ? s : null,
                _ => ItemText(r, "path") ?? ItemText(r, "bundleId"),
            }, TabCell.Mono) { Width = 260 },
            new("username", "User", r => ItemText(r, "username")),
        ],
        ["deviceName", "kind", "name", "enabledState", "lastRun", "lastRunTime", "nextRunTime", "origin", "detail", "collectedAt"],
        q => q.List("lastRuns").Contains("failed"));

    private static readonly Dictionary<string, string> ExtensionKinds = new() { ["systemExtension"] = "System extension", ["kernelExtension"] = "Kernel extension", ["helper"] = "Privileged helper" };
    private static readonly Dictionary<string, string> ActivationLabels = new() { ["activated"] = "Activated", ["notActivated"] = "Not activated", ["unknown"] = "Unknown" };
    private static readonly Dictionary<string, string> SignatureLabels = new() { ["signed"] = "Signed", ["unsigned"] = "Unsigned", ["unknown"] = "Unknown" };
    private static readonly Dictionary<string, string> ManagedLabels = new() { ["yes"] = "Managed", ["no"] = "Not managed", ["unknown"] = "Not reported" };

    public static readonly TabReportDef Extensions = Items("extensions", "Extensions", "/api/v1/system/extensions",
        "System extensions, kernel extensions and privileged helper tools across Macs.",
        "macOS only. Rows list reported items only. Profile management for system extensions is not collected by the current macOS client, so it shows as not reported.",
        "Choose extensions or helper tools from Selections, pick a team id, or show extensions that are not activated or helpers that are unsigned.",
        [("systemExtension", "System extensions"), ("kernelExtension", "Kernel extensions"), ("helper", "Privileged helpers")],
        [
            Labelled("activations", "Activation", "activation", ActivationLabels),
            Labelled("signatures", "Signature", "signature", SignatureLabels),
            Labelled("managed", "Managed by profile", "managed", ManagedLabels),
            new TabChoice("teamIds", "Team id")
            {
                From = c => TabJson.Array(c.Catalog, "team")
                    .Select(e => TabJson.Str(e, "value") is { } v ? new TabOption(v, $"{v} ({Count(TabJson.Int(e, "devices"))})") : null)
                    .Where(o => o is not null).Select(o => o!).ToList(),
                Visible = c => TabJson.Array(c.Catalog, "team").Any(),
            },
        ],
        [
            new("kind", "Kind", r => Label(ExtensionKinds, r.Kind, r.Kind)),
            new("name", "Identifier or name", r => ItemText(r, "name"), TabCell.Mono) { Width = 260 },
            new("version", "Version", r => ItemText(r, "version"), TabCell.Mono),
            new("state", "Reported state", r => ItemText(r, "state")),
            new("activation", "Activation", r => r.Str("activation") is { } a ? Label(ActivationLabels, a) : null, TabCell.State) { AppliesTo = ["systemExtension"] },
            new("teamId", "Team id", r => ItemText(r, "teamId"), TabCell.Mono),
            new("signature", "Signature", r => r.Str("signature") is { } s ? Label(SignatureLabels, s) : null, TabCell.State) { AppliesTo = ["helper"] },
            new("managed", "Managed by profile", r => r.Str("managed") is { } m ? Label(ManagedLabels, m, "Not reported") : null) { AppliesTo = ["systemExtension"] },
            new("type", "Type", r => ItemText(r, "type")),
            new("category", "Category", r => ItemText(r, "category")),
            new("appName", "App", r => ItemText(r, "appName")),
            new("bundleId", "Bundle id", r => ItemText(r, "bundleId"), TabCell.Mono),
            new("loaded", "Loaded", r => r.Kind == "kernelExtension" ? YesNo(r.Bool("loaded")) : null) { AppliesTo = ["kernelExtension"] },
            new("path", "Path", r => ItemText(r, "path"), TabCell.Mono) { Width = 260 },
        ],
        ["deviceName", "kind", "name", "version", "activation", "teamId", "signature", "managed", "appName", "collectedAt"],
        q => q.List("activations").Contains("notActivated") || q.List("signatures").Contains("unsigned") || q.List("teamIds").Count > 0,
        notOn: "Windows",
        notText: "Not applicable to Windows. System extensions, kernel extensions and privileged helper tools exist only on macOS. Switch the platform filter to macOS or All to see this report.");
}
