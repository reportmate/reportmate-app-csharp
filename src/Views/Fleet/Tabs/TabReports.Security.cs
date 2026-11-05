using System;
using System.Collections.Generic;
using System.Linq;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Security › Vulnerabilities (a CVE summary that drills into device rows) and Threat
/// Detections. Windows sends MSRC entries with status and severity, macOS sends
/// unpatched CVEs with no severity, and only Windows sends individual detections; a
/// field that was not collected reads "Not reported", never a guess.
/// </summary>
public static partial class TabReports
{
    public static string SeverityLabel(string? value) => string.IsNullOrEmpty(value) || value == "not_reported" ? "Not reported" : value;

    public static string ExploitedLabel(bool? value) => value switch { true => "Yes", false => "No", _ => "Not reported" };

    public static string CveStatusLabel(string? value) => string.IsNullOrEmpty(value) || value == "unknown"
        ? "Unknown"
        : char.ToUpperInvariant(value[0]) + value[1..];

    private static double ExploitedRank(bool? value) => value switch { true => 2, false => 0, _ => 1 };

    /// <summary>A known page for a real CVE id, else the collected link when it is plain http(s).</summary>
    public static string VulnerabilityHref(string? cve, string? url, string? source)
    {
        if (!string.IsNullOrEmpty(cve))
            return source == "MSRC" ? $"https://msrc.microsoft.com/update-guide/vulnerability/{cve}" : $"https://nvd.nist.gov/vuln/detail/{cve}";
        return SafeHttpUrl(url);
    }

    public static string SafeHttpUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(u.UserInfo)
            ? u.ToString()
            : "";

    private static string[] VulnerabilityParams => WithScope("cves", "statuses", "severities", "exploited", "freshness", "maxAgeHours");

    private static IEnumerable<TabChoice> VulnerabilityChoices() =>
    [
        new TabChoice("view", "View", ChoiceMode.Single)
        {
            Default = "summary", Prominent = true, Resets = ["sort", "dir"],
            Options = [new("summary", "CVE summary"), new("devices", "Device rows")],
        },
        new TabChoice("statuses", "Status") { Options = [new("unpatched", "Unpatched"), new("patched", "Patched"), new("unknown", "Unknown")] },
        new TabChoice("severities", "Severity")
        {
            From = c => TabJson.Array(c.Catalog, "severities")
                .Select(e => TabJson.Str(e, "value") is { } v ? new TabOption(v.ToLowerInvariant(), SeverityLabel(v)) : null)
                .Where(o => o is not null).Select(o => o!).ToList(),
        },
        new TabChoice("exploited", "Actively exploited", ChoiceMode.Single)
        {
            Default = "all",
            Options = [new("all", "All"), new("yes", "Actively exploited"), new("no", "Not exploited"), new("unknown", "Not reported")],
        },
        .. Scope("Security collection age", "168"),
    ];

    private static IReadOnlyList<TabSelection> CveSelection =>
    [
        new("cves", "CVEs", c => TabJson.Array(c.Catalog, "cves")
            .Select(e => new TabOption(TabJson.Str(e, "identifier") ?? "", TabJson.Str(e, "identifier") ?? "", TabJson.Int(e, "devices")))
            .Where(o => o.Value.Length > 0).ToList()),
    ];

    /// <summary>The coverage line both security reports carry: who reports, who does not, and why that is not "clean".</summary>
    public static string? CoverageText(TabReportData? data, bool detections)
    {
        if (TabJson.Prop(data?.Root, "coverage") is not { } coverage) return null;
        var devices = Count(TabJson.Int(coverage, "devices"));
        var reporting = Count(TabJson.Int(coverage, "reporting"));
        var not = Count(TabJson.Int(coverage, "notReporting"));
        var unknown = Count(TabJson.Int(coverage, "unknown"));
        var text = $"{devices:N0} devices in scope: {reporting:N0} report {(detections ? "detections" : "a CVE list")}";
        if (not > 0) text += detections
            ? $", {not:N0} do not (macOS clients report no individual detections; not counted as clean)"
            : $", {not:N0} do not (coverage unknown, not counted as clean)";
        if (unknown > 0) text += $", {unknown:N0} sent a list that could not be read";
        text += ".";
        if (!detections && TabJson.Int(coverage, "listMayBeIncomplete") is > 0 and var partial)
            text += $" {partial:N0} macOS devices reached the client's 50-entry limit, so their lists may be incomplete.";
        if (detections && TabJson.Int(coverage, "devicesWithDetections") is { } with)
            text += $" {with:N0} devices have matching detections.";
        return text;
    }

    private static string Joined(TabRow r, string path) => string.Join(", ", r.Strs(path));

    public static readonly TabReportDef VulnerabilitySummary = new()
    {
        Module = "security", Tab = "vulnerabilities", Title = "Vulnerabilities",
        Description = "CVEs collected on each device: Windows security updates and macOS unpatched CVEs.",
        Footnote = "Rows are what clients collected. A device that sends no CVE list is not counted as clean.",
        Api = "/api/v1/security/vulnerabilities", FiltersApi = "/api/v1/security/vulnerabilities/filters", FiltersParams = ["includeArchived"],
        RowsProperty = "cves", Paged = false, RowNoun = "CVEs",
        ApiParams = VulnerabilityParams, ExtraApi = _ => [("view", "summary")],
        Selections = CveSelection, Choices = [.. VulnerabilityChoices()],
        SortParam = "sort", DirParam = "dir", DefaultSort = null, DefaultDescending = true, SearchParam = "q",
        Columns =
        [
            new("identifier", "CVE", r => r.Str("identifier"), TabCell.Mono) { Width = 170 },
            new("devices", "Devices", r => r.Str("devices"), TabCell.Number) { Width = 90 },
            new("unpatchedDevices", "Unpatched", r => r.Str("unpatchedDevices"), TabCell.Number) { Width = 100 },
            new("severities", "Severity", r => Joined(r, "severities"), TabCell.State) { Empty = "Not reported" },
            new("exploited", "Actively exploited", r => ExploitedLabel(r.Bool("activelyExploited")), TabCell.State) { Rank = r => ExploitedRank(r.Bool("activelyExploited")) },
            new("fixedIn", "Fixed in", r => Joined(r, "fixedIn")),
            new("products", "Affected product", r => Joined(r, "products")) { Width = 240 },
            new("platforms", "Platform", r => Joined(r, "platforms")),
        ],
        ColumnChooser = false,
        // A CVE opens the devices that reported it.
        RowLink = (r, q) => q.With(("view", "devices"), ("sort", null), ("dir", null), ("q", null))
            .WithList("cves", [r.Str("identifier") ?? ""], json: true).Link("security", "vulnerabilities"),
        CsvName = "cve-summary",
        CsvHeaders = _ => ["CVE", "Update Article", "Devices", "Unpatched Devices", "Patched Devices", "Severity", "Actively Exploited",
            "Fixed In", "Affected Product", "Platforms", "Sources", "Report Generated At", "Report URL"],
        CsvRow = (r, c, url) =>
        [
            r.Str("cve") ?? r.Str("identifier"), r.Str("kbArticle"), r.Str("devices"), r.Str("unpatchedDevices"), r.Str("patchedDevices"),
            r.Strs("severities") is { Count: > 0 } s ? string.Join("; ", s) : "Not reported", ExploitedLabel(r.Bool("activelyExploited")),
            string.Join("; ", r.Strs("fixedIn")), string.Join("; ", r.Strs("products")), string.Join("; ", r.Strs("platforms")),
            string.Join("; ", r.Strs("sources")), c.Data?.GeneratedAt, url,
        ],
        Caption = (c, _) => CoverageText(c.Data, detections: false),
    };

    public static readonly TabReportDef VulnerabilityRows = new()
    {
        Module = "security", Tab = "vulnerabilities", Title = "Vulnerabilities",
        Description = VulnerabilitySummary.Description, Footnote = VulnerabilitySummary.Footnote,
        Api = VulnerabilitySummary.Api, FiltersApi = VulnerabilitySummary.FiltersApi, FiltersParams = ["includeArchived"],
        PageSize = 5000, RowNoun = "rows",
        ApiParams = VulnerabilityParams,
        Selections = CveSelection, Choices = [.. VulnerabilityChoices()],
        SortParam = "sort", DirParam = "dir", DefaultSort = "identifier",
        Columns =
        [
            new("identifier", "CVE", r => r.Str("identifier"), TabCell.Mono)
            { Width = 190, Display = r => r.Str("identifier") + (r.Str("kbArticle") is { } kb && kb != r.Str("identifier") ? $" · {kb}" : "") },
            new("severity", "Severity", r => SeverityLabel(r.Str("severity")), TabCell.State),
            new("product", "Affected product", r => r.Str("product")) { Width = 220 },
            new("affectedVersion", "Affected version", r => r.Str("affectedVersion")),
            new("fixedIn", "Fixed in", r => r.Str("fixedIn")),
            new("exploited", "Actively exploited", r => ExploitedLabel(r.Bool("activelyExploited")), TabCell.State) { Rank = r => ExploitedRank(r.Bool("activelyExploited")) },
            new("status", "Status", r => CveStatusLabel(r.Str("status")), TabCell.State),
            new("device", "Device", r => r.DeviceName, TabCell.Device) { Width = 200 },
            new("platform", "Platform", r => r.Platform),
            new("collectedAt", "Collected", r => r.Str("collectedAt"), TabCell.Time)
            { Display = r => CollectionText(r) + (r.Bool("listMayBeIncomplete") == true ? " · List may be incomplete" : ""), Width = 240 },
        ],
        ColumnChooser = false,
        RowLink = (r, _) => DeviceLink(r, "security"),
        CsvName = "vulnerabilities",
        CsvHeaders = _ => ["CVE", "Update Article", "Reported Identifier", "Source", "Status", "Severity", "Affected Product", "Affected Version",
            "Fixed In", "Actively Exploited", "Installed Date", "Reference URL", "Security Collection Time", "Collection Freshness",
            "List May Be Incomplete", .. DeviceCsvHeaders, "Report Generated At", "Report URL"],
        CsvRow = (r, c, url) =>
        [
            r.Str("cve"), r.Str("kbArticle"), r.Str("label"), r.Str("source"), CveStatusLabel(r.Str("status")),
            SeverityLabel(r.Str("severity")), r.Str("product"), r.Str("affectedVersion"), r.Str("fixedIn"), ExploitedLabel(r.Bool("activelyExploited")),
            r.Str("installedDate"), SafeHttpUrl(r.Str("url")), r.Str("collectedAt"), r.Str("freshness"), r.Bool("listMayBeIncomplete") == true ? "Yes" : "No",
            .. DeviceCsvCells(r), c.Data?.GeneratedAt, url,
        ],
        Caption = (c, _) => CoverageText(c.Data, detections: false),
    };

    private static TabChoice CatalogValues(string param, string label, string list) => new(param, label)
    {
        From = c => TabJson.Array(c.Catalog, list)
            .Select(e => TabJson.Str(e, "value") is { } v ? new TabOption(v.ToLowerInvariant(), $"{SeverityLabel(v)} {Count(TabJson.Int(e, "devices"))}") : null)
            .Where(o => o is not null).Select(o => o!).ToList(),
        Visible = c => TabJson.Array(c.Catalog, list).Any(),
    };

    public static readonly TabReportDef Threats = new()
    {
        Module = "security", Tab = "threats", Title = "Threat Detections",
        Description = "Threat detections collected from endpoint protection, including blocked attack surface reduction events.",
        Footnote = "Only Windows clients report individual detections. A device with no detections listed is not counted as clean unless it reports detections at all.",
        Api = "/api/v1/security/detections", FiltersApi = "/api/v1/security/detections/filters", FiltersParams = ["includeArchived"],
        PageSize = 5000, RowNoun = "detections",
        ApiParams = WithScope("vendors", "categories", "severities", "statuses"),
        ExtraApi = q => [("days", DetectionDays(q).ToString(System.Globalization.CultureInfo.InvariantCulture))],
        Choices =
        [
            new TabChoice("days", "Detected within", ChoiceMode.Single)
            {
                Default = "30",
                Options = [new("1", "24 hours"), new("7", "7 days"), new("30", "30 days"), new("90", "90 days"), new("0", "All collected")],
                // Sent as the API's days parameter, always, with the default filled in.
                Client = (_, _, _) => true,
            },
            CatalogValues("vendors", "Vendor / product", "vendors"),
            CatalogValues("categories", "Category", "categories"),
            CatalogValues("severities", "Severity", "severities"),
            CatalogValues("statuses", "Status", "statuses"),
            .. Scope("Security collection age", "168").Where(c => c.Param is not ("freshness" or "maxAgeHours")),
        ],
        SortParam = "sort", DirParam = "dir", DefaultSort = "lastSeenAt", DefaultDescending = true,
        Columns =
        [
            new("threatName", "Threat", r => r.Str("threatName")) { Width = 240, Display = r => r.Str("threatName") is { } n ? n + (r.Str("threatId") is { } id ? $" · {id}" : "") : null },
            new("severity", "Severity", r => SeverityLabel(r.Str("severity")), TabCell.State),
            new("vendor", "Vendor / product", r => r.Str("vendor")),
            new("category", "Category", r => r.Str("category")),
            new("status", "Status", r => r.Str("status")) { Display = r => r.Str("status") is { } s ? s + (r.Str("actionTaken") is { } a ? $" · {a}" : "") : null },
            new("lastSeenAt", "Detected", r => r.Str("lastSeenAt") ?? r.Str("detectedAt"), TabCell.Time),
            new("count", "Count", r => r.Str("count"), TabCell.Number) { Width = 80 },
            new("path", "Path", r => r.Str("path"), TabCell.Mono) { Width = 260, Display = r => r.Str("path") is { } p ? p + (r.Str("processName") is { } proc ? $" · {proc}" : "") : null },
            new("device", "Device", r => r.DeviceName, TabCell.Device) { Width = 200 },
            new("platform", "Platform", r => r.Platform),
        ],
        ColumnChooser = false,
        RowLink = (r, _) => DeviceLink(r, "security"),
        CsvName = "threat-detections",
        CsvHeaders = _ => ["Threat", "Threat ID", "Severity", "Vendor / Product", "Category", "Status", "Action Taken", "Detected At",
            "First Seen", "Last Seen", "Count", "Path", "Process", "User", .. DeviceCsvHeaders, "Report Generated At", "Report URL"],
        CsvRow = (r, c, url) =>
        [
            r.Str("threatName"), r.Str("threatId"), SeverityLabel(r.Str("severity")), r.Str("vendor"), r.Str("category"), r.Str("status"),
            r.Str("actionTaken"), r.Str("detectedAt"), r.Str("firstSeenAt"), r.Str("lastSeenAt"), r.Str("count"), r.Str("path"),
            r.Str("processName"), r.Str("user"), .. DeviceCsvCells(r), c.Data?.GeneratedAt, url,
        ],
        Caption = (c, _) => CoverageText(c.Data, detections: true),
    };

    public static int DetectionDays(TabQuery q) =>
        q["days"] is not { } raw ? 30 : int.TryParse(raw, out var d) && d is >= 0 and <= 3650 ? d : 30;
}
