using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The numbers behind the dashboard's insight widgets, worked out from the same bulk
/// report rows the report pages read. Kept free of any UI type so the counting can be
/// tested on its own; the cards that draw these live with the dashboard.
/// </summary>
/// <remarks>
/// Ported from the Mac app's InsightWidgets and the Kit row readers behind them
/// (SecurityReportRow, ManagementReportRow, HardwareReportRow, SystemReportRow), so
/// the two dashboards count the same fleet the same way.
/// </remarks>
public static partial class Insights
{
    // ── Row reading ──────────────────────────────────────────────────────

    /// <summary>
    /// Whether a report row came from a Windows device. The modules name the field
    /// differently -- platform on most rows, operatingSystem on network, osName on
    /// hardware -- so each is tried in turn.
    /// </summary>
    public static bool IsWindows(JsonElement row) =>
        Platform(row).Contains("win", StringComparison.OrdinalIgnoreCase);

    /// <summary>The row's platform as reported, empty when it names none.</summary>
    public static string Platform(JsonElement row) =>
        new[] { "platform", "operatingSystem", "osName" }
            .Select(name => Str(row, name))
            .FirstOrDefault(s => s.Length > 0) ?? "";

    internal static JsonElement? Prop(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (parent.TryGetProperty(name, out var v)) return v;
        foreach (var p in parent.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    internal static string Str(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.String } v ? (v.GetString() ?? "").Trim() : "";

    /// <summary>A number, read from a JSON number or a numeric string.</summary>
    internal static double? Number(JsonElement parent, string name) => Prop(parent, name) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    internal static int Int(JsonElement parent, string name) => (int)(Number(parent, name) ?? 0);

    /// <summary>
    /// True, 1, "true", "1" and "yes" read as on; anything else as off. The clients
    /// disagree on whether these flags are booleans or strings.
    /// </summary>
    internal static bool Boolish(JsonElement parent, string name) => BoolishIfPresent(parent, name) ?? false;

    /// <summary>
    /// The flag when the row carries one, null when it does not, so "not reported"
    /// stays apart from "off".
    /// </summary>
    internal static bool? BoolishIfPresent(JsonElement parent, string name) => Prop(parent, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } v => v.GetDouble() != 0,
        { ValueKind: JsonValueKind.String } v => (v.GetString() ?? "").Trim().ToLowerInvariant() is var s && s.Length > 0
            ? s is "true" or "1" or "yes"
            : null,
        _ => null,
    };

    // ── Security posture ─────────────────────────────────────────────────

    /// <param name="Key">The security report's link parameter for this protection: <c>/security?&lt;key&gt;=off</c>.</param>
    public sealed record Compliance(string Label, int On, int Total, string Key = "")
    {
        public double Fraction => Total == 0 ? 0 : (double)On / Total;
    }

    public sealed record SecurityPosture(
        IReadOnlyList<Compliance> Rows, int WithThreats, int ExpiredCerts, int CriticalCves);

    /// <summary>
    /// Encryption and firewall across every row; antivirus only over rows that
    /// report it at all (Windows, or a Mac that names a product), because the API
    /// sends false for a Mac that has nothing to say and counting that as disabled
    /// made every Mac a failure. SIP is a Mac question and Secure Boot a Windows one.
    /// </summary>
    public static SecurityPosture Security(IReadOnlyList<JsonElement> rows)
    {
        var list = new List<Compliance>
        {
            new("Disk encryption", rows.Count(r => Boolish(r, "encryptionEnabled")), rows.Count, "encryption"),
            new("Firewall", rows.Count(r => Boolish(r, "firewallEnabled")), rows.Count, "firewall"),
        };

        var protectors = rows.Where(ReportsAntivirus).ToList();
        if (protectors.Count > 0)
            list.Add(new("Antivirus protection", protectors.Count(r => Boolish(r, "antivirusEnabled")), protectors.Count, "antivirus"));

        var sip = rows.Where(r => !IsWindows(r)).Select(r => BoolishIfPresent(r, "sipEnabled")).Where(v => v is not null).ToList();
        if (sip.Count > 0)
            list.Add(new("System Integrity Protection", sip.Count(v => v == true), sip.Count, "sip"));

        var windows = rows.Where(IsWindows).ToList();
        if (windows.Count > 0)
            list.Add(new("Secure Boot", windows.Count(r => Boolish(r, "secureBootEnabled")), windows.Count, "secureBoot"));

        return new SecurityPosture(list,
            rows.Count(r => ActiveThreats(r) > 0),
            rows.Count(r => ExpiredCerts(r) > 0),
            rows.Count(r => CriticalCves(r) > 0));
    }

    // ── Management ───────────────────────────────────────────────────────

    public sealed record ManagementSummary(int Total, int Enrolled, IReadOnlyList<(string Name, int Count)> TopProviders);

    /// <summary>Enrolled share and the four providers enrolling the most devices.</summary>
    public static ManagementSummary ManagementOverview(IReadOnlyList<JsonElement> rows)
    {
        var enrolled = rows.Where(r => Boolish(r, "isEnrolled")).ToList();
        var top = enrolled
            .GroupBy(Management.Provider)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Take(4)
            .ToList();
        return new ManagementSummary(rows.Count, enrolled.Count, top);
    }

    // ── Hardware ─────────────────────────────────────────────────────────

    public sealed record HardwareSummary(
        int Total, IReadOnlyList<(string Family, int Count)> Families, int LowStorage, int LowMemory, int Laptops);

    /// <summary>
    /// The architecture family a device belongs to: Apple Silicon or Intel on a Mac,
    /// ARM64 or x64 on Windows. Built on <see cref="Hardware.Normalize"/>, which
    /// already reads the processor and graphics names so a Snapdragon or an M-series
    /// part that reports no architecture still lands in the right family.
    /// </summary>
    public static string Family(JsonElement row)
    {
        var arch = Hardware.Normalize(row);
        if (IsWindows(row)) return arch == "ARM64" ? "ARM64" : "x64";
        return arch switch
        {
            "ARM64" => "Apple Silicon",
            null => "Unknown",
            _ => "Intel",
        };
    }

    public static HardwareSummary HardwareOverview(IReadOnlyList<JsonElement> rows)
    {
        var families = rows
            .GroupBy(Family)
            .Select(g => (Family: g.Key, Count: g.Count()))
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Family, StringComparer.Ordinal)
            .ToList();

        return new HardwareSummary(
            rows.Count,
            families,
            rows.Count(IsLowStorage),
            rows.Count(IsLowMemory),
            rows.Count(Hardware.IsLaptop));
    }

    /// <summary>Under 10% of internal storage free: <c>/hardware?storage=low</c>.</summary>
    public static bool IsLowStorage(JsonElement row) =>
        Hardware.StorageBytes(row) is { Total: > 0, Free: > 0 } s && (double)s.Free / s.Total < 0.1;

    /// <summary>8 GB of memory or less: <c>/hardware?memory=le8</c>.</summary>
    public static bool IsLowMemory(JsonElement row) =>
        Hardware.MemoryBytes(row) is > 0 and <= 8d * 1024 * 1024 * 1024;

    // ── System health ────────────────────────────────────────────────────

    /// <summary>The uptime buckets the system report and the Mac app chart, in order.</summary>
    public static readonly IReadOnlyList<(string Label, double FromSeconds)> UptimeBuckets =
    [
        ("< 1 day", 0),
        ("1-3 days", 86_400),
        ("3-7 days", 259_200),
        ("1-2 weeks", 604_800),
        ("2-4 weeks", 1_209_600),
        ("30+ days", 2_592_000),
    ];

    /// <summary>The link keys for <see cref="UptimeBuckets"/>, in the same order: <c>/system?uptime=</c>.</summary>
    public static readonly IReadOnlyList<string> UptimeKeys = ["lt1d", "1-3d", "3-7d", "1-2w", "2-4w", "30d+"];

    /// <summary>The uptime bucket's link key, or null when the row reports no uptime.</summary>
    public static string? UptimeKey(JsonElement row) =>
        UptimeBucket(UptimeSeconds(row)) is { } label
            ? UptimeKeys[UptimeBuckets.Select(b => b.Label).ToList().IndexOf(label)]
            : null;

    /// <summary>The pending-updates bucket's link key: <c>up-to-date</c>, <c>1-5</c>, <c>6-10</c> or <c>10+</c>.</summary>
    public static string PendingKey(JsonElement row) => Int(row, "pendingUpdatesCount") switch
    {
        <= 0 => "up-to-date",
        <= 5 => "1-5",
        <= 10 => "6-10",
        _ => "10+",
    };

    /// <summary>The pending keys that mean "has pending updates", as one link value.</summary>
    public const string AnyPending = "1-5,6-10,10+";

    public sealed record SystemSummary(IReadOnlyList<(string Label, int Count)> Uptime, int Pending, int Deferred);

    public static string? UptimeBucket(double? seconds)
    {
        if (seconds is not { } s || s <= 0) return null;
        return UptimeBuckets.Last(b => s >= b.FromSeconds).Label;
    }

    /// <summary>
    /// Uptime in seconds. The Windows runner leaves the number null on many devices
    /// but always writes uptimeString ("3d 4h", "5h, 12m", "45m"), so that is read
    /// back when the number is missing.
    /// </summary>
    public static double? UptimeSeconds(JsonElement row) =>
        Number(row, "uptime") ?? ParseUptime(Str(row, "uptimeString"));

    private static readonly (string Unit, double Factor)[] UptimeUnits = [("d", 86_400), ("h", 3_600), ("m", 60), ("s", 1)];

    public static double? ParseUptime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        double total = 0;
        var matched = false;
        foreach (var (unit, factor) in UptimeUnits)
        {
            var m = Regex.Match(text, $@"(\d+)\s*{unit}\b");
            if (!m.Success) continue;
            total += double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor;
            matched = true;
        }
        return matched ? total : null;
    }

    public static SystemSummary SystemOverview(IReadOnlyList<JsonElement> rows)
    {
        var counts = rows
            .Select(r => UptimeBucket(UptimeSeconds(r)))
            .Where(b => b is not null)
            .GroupBy(b => b!)
            .ToDictionary(g => g.Key, g => g.Count());
        var uptime = UptimeBuckets.Select(b => (b.Label, counts.GetValueOrDefault(b.Label))).ToList();
        return new SystemSummary(uptime,
            rows.Count(r => Int(r, "pendingUpdatesCount") > 0),
            rows.Count(r => Int(r, "deferredUpdatesCount") > 0));
    }
}
