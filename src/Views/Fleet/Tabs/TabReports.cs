using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>One view of a module report, as the web's title switch names it.</summary>
public sealed record ModuleTab(string Key, string Label, string Subtitle);

/// <summary>
/// The title switches of the four module reports that have more than one view, with
/// the same keys and labels as the web, and the link each view opens.
/// </summary>
public static class ModuleTabs
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ModuleTab>> All = new Dictionary<string, IReadOnlyList<ModuleTab>>
    {
        ["system"] =
        [
            new("os", "System", "Operating systems and updates"),
            new("services", "Services", "Service coverage and installed versions across devices."),
            new("updates", "Updates", "Pending and installed operating system updates across devices."),
            new("background", "Background", "Scheduled tasks, launchd jobs and Open at Login items across devices."),
            new("extensions", "Extensions", "System extensions, kernel extensions and privileged helper tools across Macs."),
        ],
        ["management"] =
        [
            new("enrollment", "Device Management Service", "Enrolment and policy"),
            new("profiles", "Configuration Profiles", "Configuration profiles each device reports installed."),
            new("preferences", "Managed Preferences", "Managed preference domains and the values devices report."),
            new("logs", "Management Tools Logs", "The management tools' own logs across the fleet"),
        ],
        ["security"] =
        [
            new("posture", "Fleet Security", "Protection status and findings"),
            new("vulnerabilities", "Vulnerabilities", "CVEs collected on each device: Windows security updates and macOS unpatched CVEs."),
            new("threats", "Threat Detections", "Threat detections collected from endpoint protection, including blocked attack surface reduction events."),
        ],
        ["identity"] =
        [
            new("users", "Identity & Users", "Accounts, groups and sign-in"),
            new("posture", "Sign-in & Recovery Posture", "Sign-in and recovery posture each device last reported."),
        ],
    };

    /// <summary>The module's own report, which the bare link opens.</summary>
    public static string DefaultKey(string module) => All.TryGetValue(module, out var tabs) ? tabs[0].Key : "";

    /// <summary>The tab a link names, as <c>/system?tab=services</c> or <c>/system/services</c>; null for the module's own report.</summary>
    public static string? TabOf(DeepLink link)
    {
        if (!All.TryGetValue(link.Section, out var tabs)) return null;
        var key = (link.Argument ?? link["tab"])?.Trim().ToLowerInvariant();
        return key is not null && key != tabs[0].Key && tabs.Any(t => t.Key == key) ? key : null;
    }

    public static DeepLink Link(string module, string key) =>
        key == DefaultKey(module)
            ? DeepLink.For(module)
            : module == "management" && key == "logs"
                ? DeepLink.For("management", "logs")
                : DeepLink.For(module, null, ("tab", key));
}

/// <summary>The definitions of every module tab report, and the pieces they share.</summary>
public static partial class TabReports
{
    // Properties rather than fields: the definitions in the other parts of this class
    // read them while they initialise, and field order across partial files is not defined.
    public static string[] ScopeKeys => ["usages", "catalogs", "areas", "fleets", "locations", "rooms", "inventoryStates"];

    public static string[] WithScope(params string[] keys) => keys.Concat(ScopeKeys).Append("includeArchived").ToArray();

    /// <summary>The report for a module tab, or null when the tab is not a tab report.</summary>
    public static TabReportDef? Find(string module, string tab, TabQuery query) => (module, tab) switch
    {
        ("system", "services") => Services,
        ("system", "updates") => Updates,
        ("system", "background") => Background,
        ("system", "extensions") => Extensions,
        ("management", "profiles") => Profiles,
        ("management", "preferences") => Preferences,
        ("security", "vulnerabilities") => query["view"] == "devices" ? VulnerabilityRows : VulnerabilitySummary,
        ("security", "threats") => Threats,
        ("identity", "posture") => Posture,
        _ => null,
    };

    // ── Labels the web uses ──────────────────────────────────────────────

    public static string InventoryLabel(string? value) => value switch
    {
        "checked_out" => "Checked out",
        "storage" => "Storage",
        "decommissioning" => "Decommissioning",
        _ => "Unknown",
    };

    public static string FreshnessLabel(string? value) => value switch
    {
        "fresh" => "Within maximum age",
        "stale" => "Older than maximum age",
        _ => "Unknown",
    };

    public static string YesNo(bool? value) => value switch { true => "Yes", false => "No", _ => "Not reported" };

    public static string HoursLabel(string value) => value switch
    {
        "24" => "24 hours",
        "168" => "7 days",
        "720" => "30 days",
        _ => $"{value} hours",
    };

    public static DeepLink DeviceLink(TabRow row, string tab) =>
        DeepLink.For("device", row.Serial, ("tab", tab));

    /// <summary>A cell's text: "Not applicable" off its item kinds, the empty wording when unreported.</summary>
    public static string Display(TabColumn column, TabRow row)
    {
        if (!column.Applies(row)) return "Not applicable";
        var value = (column.Display ?? column.Value)(row);
        if (string.IsNullOrWhiteSpace(value))
            return column.Empty ?? column.Cell switch
            {
                TabCell.Time => "Unknown",
                TabCell.State => "Not applicable",
                _ => "Not reported",
            };
        if (column.Display is null && column.Cell == TabCell.Time && TabJson.Time(value) is { } at)
            return Format.RelativeTime(at);
        return value;
    }

    /// <summary>The colour of a state cell, read from its words as the web colours its badges.</summary>
    public static string Tone(string text) => text switch
    {
        "Failed" or "Disabled" or "Not activated" or "Unsigned" or "Stopped" or "Not found" or "Not installed"
            or "Not reported" or "Not compliant" or "Unpatched" or "Expired" => "bad",
        "Succeeded" or "Enabled" or "Activated" or "Signed" or "Within maximum age" or "Running" or "Installed"
            or "Compliant" or "Patched" or "None pending reported" => "good",
        "Pending" => "info",
        _ => "warn",
    };

    /// <summary>A device's age at the report's snapshot, in hours; null for a missing, unreadable or future time.</summary>
    public static double? AgeHours(string? at, DateTime snapshot) =>
        TabJson.Time(at) is { } when && when <= snapshot ? (snapshot - when).TotalHours : null;

    public static string Hours(double? hours) => hours is not { } h
        ? "Unknown"
        : h < 24 ? $"{Math.Floor(h):0} hours" : $"{Math.Floor(h / 24):0} days";

    public static string Invariant(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // ── Filters every report shares ──────────────────────────────────────

    /// <summary>The scope filters and the collection-age filters, as the web's Selections panel lists them.</summary>
    public static IEnumerable<TabChoice> Scope(string ageLabel, string maxAgeDefault, bool rooms = false)
    {
        foreach (var (key, label) in new[] { ("usages", "Usage"), ("catalogs", "Catalog"), ("areas", "Area"), ("fleets", "Fleet"), ("locations", "Location") }
                     .Concat(rooms ? [("rooms", "Room")] : []))
            yield return new TabChoice(key, label)
            {
                From = c => c.Scope(key).Select(v => new TabOption(v, v)).ToList(),
                Visible = c => c.Scope(key).Count > 0,
            };
        foreach (var choice in Ages(ageLabel, maxAgeDefault)) yield return choice;
        yield return new TabChoice("inventoryStates", "Inventory state")
        {
            From = c => c.Scope("inventoryStates").Select(v => new TabOption(v, InventoryLabel(v))).ToList(),
        };
        yield return new TabChoice("includeArchived", "Archive", ChoiceMode.Toggle) { Options = [new("true", "Include archived")] };
    }

    public static IEnumerable<TabChoice> Ages(string ageLabel, string maxAgeDefault)
    {
        yield return new TabChoice("freshness", ageLabel, ChoiceMode.Single)
        {
            Default = "all",
            Options = [new("all", "All"), new("fresh", "Within maximum age"), new("stale", "Older"), new("unknown", "Unknown")],
        };
        yield return new TabChoice("maxAgeHours", "Maximum collection age", ChoiceMode.Single)
        {
            Default = maxAgeDefault,
            From = c => new[] { "24", "168", "720", c.Query["maxAgeHours"] ?? maxAgeDefault }
                .Distinct().Select(v => new TabOption(v, HoursLabel(v))).ToList(),
        };
    }

    /// <summary>The device column every report leads with, opening the device page.</summary>
    public static TabColumn DeviceColumn(string label = "Device") =>
        new("deviceName", label, r => r.DeviceName, TabCell.Device) { Width = 220 };

    /// <summary>The device and inventory fields the security exports carry.</summary>
    public static string[] DeviceCsvHeaders =>
        ["Device Name", "Serial Number", "Asset Tag", "Platform", "Inventory State", "Archived in ReportMate", "Usage", "Catalog", "Area", "Fleet", "Location", "Room", "Last Seen"];

    public static IEnumerable<string?> DeviceCsvCells(TabRow r) =>
    [
        r.Str("deviceName"), r.Serial, r.Str("assetTag"), r.Platform, InventoryLabel(r.Str("inventoryState")),
        r.Bool("archived") == true ? "Yes" : "No", r.Str("usage"), r.Str("catalog"), r.Str("area"), r.Str("fleet"),
        r.Str("location"), r.Str("room"), r.Str("lastSeen"),
    ];

    public static int Count(int? value) => value ?? 0;
}
