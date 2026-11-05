using System;
using System.Collections.Generic;
using System.Linq;
using ReportMate.App.Services;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>Management › Configuration Profiles and Managed Preferences.</summary>
public static partial class TabReports
{
    public const string ProfilesApi = "/api/v1/management/profiles";
    public const int MaxProfileSelections = 50;

    public static string ProfileStateLabel(string? state) => state switch
    {
        "installed" => "Installed",
        "missing" => "Not installed",
        _ => "Unknown",
    };

    public static string ProfileScopeLabel(string? scope) => scope switch
    {
        "system" => "System",
        "user" => "User",
        "system+user" => "System and user",
        _ => "Unknown",
    };

    /// <summary>The catalog entries visible under the platform: seen on it, or seen nowhere in particular.</summary>
    private static IEnumerable<JsonElement> ProfileOptions(TabContext c, string list) =>
        TabJson.Array(c.Catalog, list).Where(e =>
        {
            if (c.Platform == "all") return true;
            var platforms = TabJson.Array(e, "platforms").Select(TabJson.Text).ToList();
            return platforms.Count == 0 || platforms.Any(p => string.Equals(p, c.Platform, StringComparison.OrdinalIgnoreCase));
        });

    private static TabReportDef ProfileReport(bool preference)
    {
        var kind = preference ? "preference" : "profile";
        var list = preference ? "preferences" : "profiles";
        var noun = preference ? "managed preference domains" : "configuration profiles";
        var tab = preference ? "preferences" : "profiles";
        return new TabReportDef
        {
            Module = "management", Tab = tab,
            Title = preference ? "Managed Preferences" : "Configuration Profiles",
            Description = preference
                ? "Managed preference domains and the values devices report."
                : "Configuration profiles each device reports installed.",
            Footnote = "One row per device and selected identifier. Not installed is shown only against a fresh, nonempty management inventory; anything else is unknown.",
            Api = ProfilesApi, FiltersApi = ProfilesApi + "/filters", FiltersPlatform = false, FiltersParams = ["includeArchived"],
            RowsProperty = "devices", PageSize = 2000, RowNoun = "rows",
            ApiParams = WithScope(preference
                ? ["identifiers", "preferenceKey", "states", "freshness", "maxAgeHours"]
                : ["identifiers", "states", "freshness", "maxAgeHours"]),
            ExtraApi = _ => [("kind", kind)],
            HasQuery = q => q.List("identifiers").Count > 0,
            Prompt = $"Choose {noun} from Selections to build your report. Pick collected {noun} to see which devices have them, which lack them, and which are unknown.",
            Selections =
            [
                new("identifiers", preference ? "Preference domains" : "Profiles", c => ProfileOptions(c, list)
                    .Select(e => new TabOption(TabJson.Str(e, "identifier") ?? "", TabJson.Str(e, "name") ?? TabJson.Str(e, "identifier") ?? "", TabJson.Int(e, "devices")))
                    .Where(o => o.Value.Length > 0).ToList())
                {
                    Max = MaxProfileSelections, Resets = ["preferenceKey"],
                    Selected = c => c.Query.List("identifiers").Take(MaxProfileSelections).ToList(),
                },
            ],
            Choices =
            [
                new TabChoice("preferenceKey", "Preference key", ChoiceMode.Single)
                {
                    Default = "",
                    Visible = c => preference && c.Query.List("identifiers").Count > 0,
                    From = c =>
                    {
                        var chosen = c.Query.List("identifiers");
                        return new[] { new TabOption("", "None") }.Concat(ProfileOptions(c, list)
                            .Where(e => chosen.Contains(TabJson.Str(e, "identifier") ?? ""))
                            .SelectMany(e => TabJson.Array(e, "keys").Select(TabJson.Text))
                            .Where(k => k is not null).Select(k => k!).Distinct().Order(StringComparer.OrdinalIgnoreCase).Take(200)
                            .Select(k => new TabOption(k, k))).ToList();
                    },
                },
                .. Scope("Management collection age", "24"),
                new TabChoice("states", "State")
                {
                    AllLabel = "All states", Prominent = true, SummaryKey = "",
                    Options = [new("installed", "Installed"), new("missing", "Not installed"), new("unknown", "Unknown")],
                },
            ],
            Columns =
            [
                DeviceColumn(),
                new("state", "State", r => ProfileStateLabel(r.Str("state")), TabCell.State)
                { Rank = r => Array.IndexOf(["installed", "missing", "unknown"], r.Str("state") ?? "unknown") is var i and >= 0 ? i : 2 },
                new("name", preference ? "Domain" : "Profile", r => r.Str("name") ?? r.Str("identifier"))
                {
                    Width = 300,
                    Display = r =>
                    {
                        var parts = new List<string?> { r.Str("name") ?? r.Str("identifier") };
                        if (r.Str("identifier") is { } id && id != r.Str("name")) parts.Add(id);
                        parts.Add(r.Str("organization"));
                        if (preference && r.Node("values") is { ValueKind: JsonValueKind.Array } values)
                            parts.Add(values.GetArrayLength() == 0 ? "Key not set in this domain"
                                : string.Join("; ", r.Items("values").Select(v => $"{v.Str("scope") ?? "Unknown scope"}: {v.Str("value") ?? ""}")));
                        return string.Join(" · ", parts.Where(p => p is not null));
                    },
                },
                new("scope", "Scope", r => r.Str("scope") is { } s ? ProfileScopeLabel(s) : null)
                {
                    Empty = "Not applicable",
                    Display = r => r.Str("scope") is { } s ? ProfileScopeLabel(s) + (r.Str("user") is { } u ? $" · {u}" : "") : null,
                },
                preference
                    ? new("version", "Settings", r => r.Str("settingCount"), TabCell.Number) { Empty = "Unknown" }
                    : new("version", "Version", r => r.Str("version"))
                    {
                        Display = r => r.Str("version") is { } v
                            ? string.Join(" · ", new[] { v, r.Str("uuid"), r.Str("verificationState") }.Where(p => p is not null))
                            : null,
                    },
                new("installDate", "Installed", r => r.Str("state") == "installed" && !preference ? r.Str("installDate") : null, TabCell.Time)
                {
                    Display = r => r.Str("state") == "installed" && !preference
                        ? r.Time("installDate") is { } at ? Format.RelativeTime(at) : "Unknown"
                        : null,
                    Empty = preference ? "Not reported" : "Not applicable",
                },
                new("inventoryState", "Inventory", r => r.Str("inventoryState"))
                {
                    Display = r => InventoryLabel(r.Str("inventoryState")) + (r.Bool("archived") == true ? " (archived)" : "")
                        + (new[] { r.Str("catalog"), r.Str("usage"), r.Str("room") }.Where(p => p is not null).ToList() is { Count: > 0 } extra ? " · " + string.Join(" · ", extra) : ""),
                },
                new("collectedAt", "Collected", r => r.Str("collectedAt"), TabCell.Time) { Display = r => CollectionText(r), Width = 220 },
            ],
            ColumnChooser = false,
            CsvName = preference ? "managed-preferences-report" : "configuration-profiles-report",
            CsvHeaders = _ =>
            [
                "Device Name", "Serial Number", "Asset Tag", "Platform", "Inventory State", "Archived in ReportMate",
                "Usage", "Catalog", "Area", "Fleet", "Location", "Room",
                preference ? "Preference Domain" : "Profile Identifier", preference ? "Domain" : "Profile Name",
                "State", "Scope", "User",
                "Version", "UUID", "Organization", "Install Date", "Source", "Status", "Verification", "Removal Disallowed", "Payload Count",
                "Setting Count", "Preference Values",
                "Management Collection Time", "Collection Freshness", "Last Seen", "Report Generated At", "Report URL",
            ],
            CsvRow = (r, c, url) =>
            [
                r.Str("deviceName"), r.Serial, r.Str("assetTag"), r.Platform, InventoryLabel(r.Str("inventoryState")), r.Bool("archived") == true ? "Yes" : "No",
                r.Str("usage"), r.Str("catalog"), r.Str("area"), r.Str("fleet"), r.Str("location"), r.Str("room"),
                r.Str("identifier"), r.Str("name"), ProfileStateLabel(r.Str("state")), r.Str("scope") is { } s ? ProfileScopeLabel(s) : "", r.Str("user"),
                r.Str("version"), r.Str("uuid"), r.Str("organization"), r.Str("installDate"), r.Str("source"), r.Str("status"), r.Str("verificationState"),
                r.Bool("removalDisallowed") switch { true => "Yes", false => "No", _ => "" }, r.Str("payloadCount"),
                r.Str("settingCount"), string.Join("\n", r.Items("values").Select(v => $"{v.Str("scope") ?? "Unknown scope"}: {v.Str("value") ?? ""}")),
                r.Str("collectedAt"), r.Str("freshness"), r.Str("lastSeen"), c.Data?.GeneratedAt, url,
            ],
            RowLink = (r, _) => DeviceLink(r, "management"),
        };
    }

    public static readonly TabReportDef Profiles = ProfileReport(preference: false);
    public static readonly TabReportDef Preferences = ProfileReport(preference: true);
}
