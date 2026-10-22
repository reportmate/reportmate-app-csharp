using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The link parameters a report honours that are rules rather than a field to
/// compare: <c>/security?encryption=off</c>, <c>/system?patch=behind</c> and the
/// like. The names and values are the shared dashboard contract, the same on the
/// web, the Mac and here, and each rule is the one the matching dashboard card counts with.
/// </summary>
/// <remarks>
/// A rule is built from the whole population first, because some answers depend on
/// the fleet: "behind" means below the highest version any device reports.
/// Values are comma-separated and a row matches any of them.
/// </remarks>
public static class ReportFilters
{
    public delegate bool RowMatch(JsonElement row, string wanted);

    private delegate RowMatch Rule(IReadOnlyList<JsonElement> population);

    private static Rule Simple(Func<JsonElement, string, bool> match) => _ => (row, wanted) => match(row, wanted);

    private static bool Off(string wanted) => wanted.ToLowerInvariant() is "off" or "0" or "false" or "no";
    private static bool On(string wanted) => wanted.ToLowerInvariant() is "on" or "1" or "true" or "yes";

    /// <summary><c>off</c> keeps rows where the flag is off, <c>on</c> where it is on.</summary>
    private static Rule Flag(Func<JsonElement, bool?> state) => Simple((row, wanted) => state(row) switch
    {
        { } on when Off(wanted) => !on,
        { } on when On(wanted) => on,
        _ => false,
    });

    /// <summary><c>1</c> keeps rows that have the thing; <c>0</c> those that do not.</summary>
    private static Rule Has(Func<JsonElement, bool> test) => Simple((row, wanted) =>
        On(wanted) ? test(row) : Off(wanted) && !test(row));

    private static bool Eq(string? a, string b) => string.Equals(a?.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Dictionary<string, Rule>> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // The dashboard's version bars come from the device list, which may carry
            // the release name, the version or the build, so any of them matches.
            ["osVersion"] = Simple((r, w) => new[] { "osVersion", "displayVersion", "buildNumber", "version" }
                .Any(f => Eq(Insights.Str(r, f), w))),
            ["uptime"] = Simple((r, w) => Eq(Insights.UptimeKey(r), w)),
            ["pending"] = Simple((r, w) => Eq(Insights.PendingKey(r), w)),
            ["deferred"] = Has(r => Insights.Int(r, "deferredUpdatesCount") > 0),
            ["os"] = rows =>
            {
                var baseline = new Insights.OsBaseline(rows);
                return (r, w) => w.ToLowerInvariant() switch
                {
                    "unsupported" => baseline.IsUnsupported(r),
                    "windows10" => Insights.IsWindows10(r),
                    _ => false,
                };
            },
            ["patch"] = rows =>
            {
                var baseline = new Insights.OsBaseline(rows);
                return (r, w) => w.ToLowerInvariant() switch
                {
                    "behind" => baseline.IsBehind(r),
                    "latest" => baseline.IsKnown(r) && !baseline.IsBehind(r),
                    _ => false,
                };
            },
        },

        ["security"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["encryption"] = Flag(r => Insights.Boolish(r, "encryptionEnabled")),
            ["firewall"] = Flag(r => Insights.Boolish(r, "firewallEnabled")),
            ["antivirus"] = Flag(r => Insights.ReportsAntivirus(r) ? Insights.Boolish(r, "antivirusEnabled") : null),
            ["sip"] = Flag(r => Insights.IsWindows(r) ? null : Insights.BoolishIfPresent(r, "sipEnabled")),
            ["secureBoot"] = Flag(r => Insights.IsWindows(r) ? Insights.Boolish(r, "secureBootEnabled") : null),
            ["threats"] = Has(r => Insights.ActiveThreats(r) > 0),
            ["certs"] = Simple((r, w) => w.ToLowerInvariant() switch
            {
                "expired" => Insights.ExpiredCerts(r) > 0,
                "expiring" => Insights.ExpiringCerts(r) > 0,
                _ => false,
            }),
            ["cve"] = Simple((r, w) => w.ToLowerInvariant() switch
            {
                "critical" => Insights.CriticalCves(r) > 0,
                "exploited" => Insights.ExploitedCves(r) > 0,
                _ => false,
            }),
        },

        ["management"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // The provider as the card names it: Intune's spellings folded, none as Unmanaged.
            ["provider"] = Simple((r, w) => Eq(Management.Provider(r), w)),
            ["enrolled"] = Has(r => Insights.Boolish(r, "isEnrolled")),
            ["enrollmentType"] = Simple((r, w) => Eq(Insights.Str(r, "enrollmentType"), w)),
            // This report only: the toolbar's platform scope is left as it is.
            ["platform"] = Simple((r, w) => w.ToLowerInvariant() switch
            {
                "macos" or "mac" => Insights.IsMac(r),
                "windows" or "win" => Insights.IsWindows(r),
                _ => false,
            }),
            ["lastSeen"] = Simple((r, w) => Eq(Insights.LastSeenKey(r, DateTime.UtcNow), w)),
        },

        ["hardware"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["arch"] = Simple((r, w) => Eq(Insights.Family(r), w)),
            ["storage"] = Simple((r, w) => Eq(w, "low") && Insights.IsLowStorage(r)),
            ["memory"] = Simple((r, w) => Eq(w, "le8") && Insights.IsLowMemory(r)),
            ["type"] = Simple((r, w) => w.ToLowerInvariant() switch
            {
                "laptop" => Hardware.IsLaptop(r),
                "desktop" => !Hardware.IsLaptop(r),
                _ => false,
            }),
            ["processor"] = Simple((r, w) => (Json(r, "processor", "name") is { Length: > 0 } p ? p : Insights.Str(r, "processor"))
                .Contains(w, StringComparison.OrdinalIgnoreCase)),
        },

        ["installs"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // errors and warnings are classes of status, not one spelling of it; any
            // other value is a status label, compared as the status chart names it.
            ["filter"] = Simple((r, w) => w.ToLowerInvariant() switch
            {
                "errors" or "error" => Insights.IsErrorStatus(InstallLabel.Status(r)),
                "warnings" or "warning" => Insights.IsWarningStatus(InstallLabel.Status(r)),
                _ => Eq(InstallLabel.Status(r), w),
            }),
            ["item"] = Simple((r, w) => Eq(Insights.Str(r, "itemName"), w)),
        },

        ["identity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // "extra" is more than one; a number is exactly that many, and 4+ four or more.
            ["admins"] = Simple((r, w) => w.Trim() switch
            {
                "extra" => Insights.HasExtraAdmins(r),
                "4+" => Insights.AdminCount(r) >= 4,
                var n when int.TryParse(n, out var exact) => Insights.AdminCount(r) == exact,
                _ => false,
            }),
            ["join"] = Simple((r, w) => Eq(Insights.JoinType(r), w)),
            ["usage"] = Simple((r, w) => Eq(Insights.Usage(r), w)),
        },

        ["network"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["connection"] = Simple((r, w) => Eq(Insights.ConnectionKey(r), w is "wireless" ? "wifi" : w)),
            ["signal"] = Simple((r, w) => Eq(w, "weak") && Insights.IsWeakSignal(r)),
            ["quality"] = Simple((r, w) => Eq(w, "poor") && Insights.IsPoorQuality(r)),
            ["vpn"] = Has(Insights.IsOnVpn),
        },

        ["peripherals"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = Simple((r, w) => w.ToLowerInvariant() switch
            {
                "printers" => Insights.Printers(r) > 0,
                "displays" => Insights.Displays(r) > 0,
                _ => false,
            }),
            ["room"] = Simple((r, w) => Eq(Insights.Room(r), w)),
        },
    };

    /// <summary>The rule-based keys a report honours.</summary>
    public static IReadOnlyCollection<string> Keys(string module) =>
        Rules.TryGetValue(module, out var rules) ? rules.Keys : [];

    /// <summary>
    /// Narrow rows by one rule-based key. Null when the report has no rule by that
    /// name, so the caller can fall back to a plain field comparison.
    /// </summary>
    public static List<JsonElement>? Apply(string module, string key, string value, IReadOnlyList<JsonElement> population, List<JsonElement> rows)
    {
        if (!Rules.TryGetValue(module, out var rules) || !rules.TryGetValue(key, out var rule)) return null;
        var wanted = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0) return rows;
        var match = rule(population);
        return rows.Where(r => wanted.Any(w => match(r, w))).ToList();
    }

    private static string? Json(JsonElement row, string parent, string name) =>
        Insights.Prop(row, parent) is { } p ? Insights.Str(p, name) : null;
}
