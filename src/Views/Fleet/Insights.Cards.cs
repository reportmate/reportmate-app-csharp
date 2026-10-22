using System.Globalization;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The numbers behind insight cards 5 to 12, and the row rules the reports use to
/// honour the same cards' links. A card and the report it opens read one rule, so
/// the count on the card is the number of rows the report shows.
/// </summary>
public static partial class Insights
{
    // ── Install health and software risk ────────────────────────────────

    /// <summary>One managed item on one device, reduced to what the cards count.</summary>
    public sealed record InstallItem(string Name, string Serial, string Platform, bool Error, bool Warning, bool Behind);

    /// <summary>
    /// Error and failed states, in either client's spelling, after the status label
    /// has folded them. The installs report's <c>filter=errors</c> reads the same rule.
    /// </summary>
    public static bool IsErrorStatus(string? label) =>
        label is not null && (label.Contains("error", StringComparison.OrdinalIgnoreCase)
                              || label.Contains("fail", StringComparison.OrdinalIgnoreCase));

    public static bool IsWarningStatus(string? label) =>
        label is not null && label.Contains("warn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An installed version that is not the catalog's latest. Both have to be known:
    /// an item that is not installed yet is pending, not behind.
    /// </summary>
    public static bool IsBehind(JsonElement row)
    {
        var installed = Str(row, "installedVersion");
        var latest = Str(row, "latestVersion");
        return installed.Length > 0 && latest.Length > 0
            && !string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
    }

    public static InstallItem? ToInstallItem(JsonElement row)
    {
        var name = Str(row, "itemName");
        if (name.Length == 0) return null;
        var status = InstallLabel.Status(row);
        return new InstallItem(name, Str(row, "serialNumber"), Platform(row),
            IsErrorStatus(status), IsWarningStatus(status), IsBehind(row));
    }

    /// <summary>The items failing on the most devices, by distinct device.</summary>
    public static IReadOnlyList<(string Name, int Devices)> TopFailing(IEnumerable<InstallItem> items, int take = 5) =>
        Top(items.Where(i => i.Error), take);

    /// <summary>The items with the most devices on a version older than the catalog's.</summary>
    public static IReadOnlyList<(string Name, int Devices)> TopBehind(IEnumerable<InstallItem> items, int take = 5) =>
        Top(items.Where(i => i.Behind), take);

    private static IReadOnlyList<(string Name, int Devices)> Top(IEnumerable<InstallItem> items, int take) =>
        items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Name, Devices: g.Select(i => i.Serial).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(x => x.Devices)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();

    public sealed record StaleAgentSummary(int Count, int ThresholdDays, IReadOnlySet<string> Serials);

    /// <summary>
    /// Devices still checking in whose management agent has stopped running, from
    /// <c>/api/v1/installs/filters</c>. They carry no errors -- their records froze
    /// the day the agent died -- so no per-item count can surface them.
    /// </summary>
    public static StaleAgentSummary StaleAgents(JsonElement filters, Func<string, bool>? platformIncludes = null)
    {
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Prop(filters, "staleAgents") is { ValueKind: JsonValueKind.Array } list)
            foreach (var agent in list.EnumerateArray())
            {
                if (platformIncludes is not null && !platformIncludes(Str(agent, "platform"))) continue;
                var serial = Str(agent, "serialNumber");
                if (serial.Length > 0) serials.Add(serial);
            }
        var threshold = (int)(Number(filters, "staleAgentThresholdDays") ?? 7);
        return new StaleAgentSummary(serials.Count, threshold, serials);
    }

    // ── Check-in gaps ────────────────────────────────────────────────────

    public sealed record CheckInSummary(
        int Rejected, int Retried, int Accepted, IReadOnlyList<(string Reason, int Count, int Devices)> Reasons);

    /// <summary>The failures endpoint's counts and its reasons, worst first.</summary>
    public static CheckInSummary CheckIns(JsonElement failures)
    {
        var counts = Prop(failures, "counts") ?? default;
        var reasons = Prop(failures, "summary") is { ValueKind: JsonValueKind.Array } summary
            ? summary.EnumerateArray()
                .Select(r => (Reason: Str(r, "reason"), Count: Int(r, "count"), Devices: Int(r, "devices")))
                .Where(r => r.Reason.Length > 0)
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Reason, StringComparer.Ordinal)
                .ToList()
            : [];
        return new CheckInSummary(Int(counts, "rejected"), Int(counts, "retried"), Int(counts, "accepted"), reasons);
    }

    // ── OS currency ──────────────────────────────────────────────────────

    public sealed record OsCurrencySummary(
        int Total, int OnLatest, int Behind, int UnsupportedMac, int Windows10,
        IReadOnlyList<(string Version, int Count)> Lagging)
    {
        public double LatestFraction => Total == 0 ? 0 : (double)OnLatest / Total;
    }

    /// <summary>
    /// How many devices have to report a version before it counts as a track's
    /// latest patch, so one Mac on a beta does not put the rest of its major behind.
    /// Shared with the web and Mac dashboards.
    /// </summary>
    public const int LatestPatchMinDevices = 5;

    /// <summary>
    /// What "latest" means for a fleet: for each macOS major, and each Windows build,
    /// the highest version at least <see cref="LatestPatchMinDevices"/> devices report.
    /// A track where no version reaches that falls back to its highest version.
    /// Built once from the whole population, so the card and the system report's
    /// <c>patch</c> and <c>os</c> filters agree.
    /// </summary>
    public sealed class OsBaseline
    {
        private readonly Dictionary<string, Version> _latest = new(StringComparer.Ordinal);
        private readonly int _newestMac;

        public OsBaseline(IEnumerable<JsonElement> rows)
        {
            var seen = new Dictionary<string, Dictionary<Version, int>>(StringComparer.Ordinal);
            var majors = new Dictionary<int, int>();
            foreach (var row in rows)
            {
                if (OsKey(row) is not { } key || OsPatchVersion(row) is not { } version) continue;
                if (!seen.TryGetValue(key.Track, out var versions)) seen[key.Track] = versions = new();
                versions[version] = versions.GetValueOrDefault(version) + 1;
                if (key.MacMajor is { } major) majors[major] = majors.GetValueOrDefault(major) + 1;
            }

            // The newest major counts the same way: one Mac on a beta of the next
            // release must not make the oldest supported one read as unsupported.
            var established = majors.Where(m => m.Value >= LatestPatchMinDevices).Select(m => m.Key).ToList();
            _newestMac = established.Count > 0 ? established.Max() : majors.Keys.DefaultIfEmpty(0).Max();

            foreach (var (track, versions) in seen)
            {
                var common = versions.Where(v => v.Value >= LatestPatchMinDevices).Select(v => v.Key).ToList();
                _latest[track] = common.Count > 0 ? common.Max()! : versions.Keys.Max()!;
            }
        }

        /// <summary>
        /// Below its track's latest patch. A device above it -- the beta -- is not behind.
        /// </summary>
        public bool IsBehind(JsonElement row) =>
            OsKey(row) is { } key && OsPatchVersion(row) is { } v
            && _latest.TryGetValue(key.Track, out var best) && v < best;

        /// <summary>
        /// A macOS major more than two behind the newest major at least
        /// <see cref="LatestPatchMinDevices"/> devices report.
        /// </summary>
        public bool IsUnsupported(JsonElement row) =>
            OsKey(row) is { MacMajor: { } major } && _newestMac - major > 2;

        public bool IsKnown(JsonElement row) => OsKey(row) is not null && OsPatchVersion(row) is not null;
    }

    public static bool IsMac(JsonElement row) =>
        Platform(row) is var p && (p.StartsWith("mac", StringComparison.OrdinalIgnoreCase)
                                   || p.Contains("darwin", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Windows 10, by name or by build: the system rows name the product, but a row
    /// that only carries the build is still answerable, since Windows 11 starts at 22000.
    /// </summary>
    public static bool IsWindows10(JsonElement row)
    {
        if (!IsWindows(row)) return false;
        var name = Str(row, "operatingSystem");
        if (name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)) return true;
        return WindowsBuild(row) is > 0 and < 22000;
    }

    private static int? WindowsBuild(JsonElement row)
    {
        foreach (var text in new[] { Str(row, "buildNumber"), Str(row, "osVersion") })
        {
            var parts = Digits(text).Split('.', StringSplitOptions.RemoveEmptyEntries);
            // "10.0.26100.4652" carries the build third; "26100.4652" first.
            var candidate = parts.Length >= 3 ? parts[2] : parts.Length > 0 ? parts[0] : null;
            if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build) && build >= 1000)
                return build;
        }
        return null;
    }

    private static (string Track, int? MacMajor)? OsKey(JsonElement row)
    {
        if (IsMac(row))
        {
            var parts = Digits(Str(row, "osVersion")).Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
                ? ($"mac:{major}", major)
                : null;
        }
        if (IsWindows(row) && WindowsBuild(row) is { } build) return ($"win:{build}", null);
        return null;
    }

    private static Version? OsPatchVersion(JsonElement row)
    {
        string text;
        if (IsMac(row)) text = Digits(Str(row, "osVersion"));
        else
        {
            // The update revision is what moves within a build; it sits after the
            // build in either field, depending on the client.
            var build = Digits(Str(row, "buildNumber"));
            text = build.Contains('.') ? build : Digits(Str(row, "osVersion"));
        }
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        if (parts.Length == 0) return null;
        if (parts.Length == 1) parts = [parts[0], "0"];
        return Version.TryParse(string.Join('.', parts), out var v) ? v : null;
    }

    private static string Digits(string text)
    {
        var cut = text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray();
        return new string(cut).Trim('.');
    }

    public static OsCurrencySummary OsCurrency(IReadOnlyList<JsonElement> rows)
    {
        var baseline = new OsBaseline(rows);
        var known = rows.Where(baseline.IsKnown).ToList();
        var behind = known.Where(baseline.IsBehind).ToList();
        var lagging = behind
            .GroupBy(r => Str(r, "osVersion"), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .Select(g => (Version: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Version, StringComparer.Ordinal)
            .Take(3)
            .ToList();
        return new OsCurrencySummary(known.Count, known.Count - behind.Count, behind.Count,
            rows.Count(baseline.IsUnsupported), rows.Count(IsWindows10), lagging);
    }

    // ── Security extras ──────────────────────────────────────────────────

    public static int ActiveThreats(JsonElement r) => (int)(Number(r, "activeThreatCount") ?? Number(r, "detectionCount") ?? 0);
    public static int ExpiredCerts(JsonElement r) => (int)(Number(r, "userExpiredCertCount") ?? Number(r, "expiredCertCount") ?? 0);
    public static int ExpiringCerts(JsonElement r) => Int(r, "expiringSoonCertCount");
    public static int CriticalCves(JsonElement r) => Int(r, "criticalCveCount");
    public static int ExploitedCves(JsonElement r) => Int(r, "activelyExploitedCveCount");

    /// <summary>
    /// Whether a row reports antivirus at all: Windows always does, a Mac only when it
    /// names a product. The posture card counts over the same rows.
    /// </summary>
    public static bool ReportsAntivirus(JsonElement r) =>
        IsWindows(r) || Boolish(r, "antivirusEnabled") || Str(r, "antivirusName").Length > 0;

    public sealed record RiskSummary(int CriticalDevices, int ExploitedDevices);

    public static RiskSummary SoftwareRisk(IReadOnlyList<JsonElement> security) =>
        new(security.Count(r => CriticalCves(r) > 0), security.Count(r => ExploitedCves(r) > 0));

    // ── Certificates ─────────────────────────────────────────────────────

    public sealed record CertificateSummary(
        int ExpiringCerts, int ExpiringDevices, int ExpiredDevices,
        IReadOnlyList<(string CommonName, int Days, int Count)> Soonest);

    /// <summary>
    /// Expiring within 30 days, from the certificate search; expired user
    /// certificates by device, from the security rows the posture card reads.
    /// </summary>
    public static CertificateSummary Certificates(IReadOnlyList<JsonElement> certificates, IReadOnlyList<JsonElement> security)
    {
        var expiring = certificates
            .Where(c => Number(c, "daysUntilExpiry") is not { } d || (d >= 0 && d <= 30))
            .ToList();
        var soonest = expiring
            .Where(c => Str(c, "commonName").Length > 0)
            .GroupBy(c => Str(c, "commonName"), StringComparer.OrdinalIgnoreCase)
            .Select(g => (CommonName: Str(g.First(), "commonName"),
                Days: (int)g.Min(c => Number(c, "daysUntilExpiry") ?? 0), Count: g.Count()))
            .OrderBy(g => g.Days)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.CommonName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        return new CertificateSummary(
            expiring.Count,
            expiring.Select(c => Str(c, "serialNumber")).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            security.Count(r => ExpiredCerts(r) > 0),
            soonest);
    }

    // ── Identity ─────────────────────────────────────────────────────────

    /// <summary>The join types, in the order the card and the report list them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> JoinTypes =
    [
        ("entra", "Entra joined"),
        ("hybrid", "Hybrid joined"),
        ("domain", "Domain joined"),
        ("workgroup", "Workgroup"),
    ];

    /// <summary>
    /// How a device is joined, from <c>directoryServices</c>: Entra
    /// (<c>azureAd.joined</c>), domain (<c>activeDirectory.isDomainJoined</c>), both
    /// is hybrid, and neither is workgroup -- the same two flags the identity report charts.
    /// </summary>
    public static string JoinType(JsonElement row)
    {
        var services = Prop(row, "directoryServices") ?? default;
        var entra = Prop(services, "azureAd") is { } aad && Boolish(aad, "joined");
        var domain = Prop(services, "activeDirectory") is { } ad && Boolish(ad, "isDomainJoined");
        return (entra, domain) switch
        {
            (true, true) => "hybrid",
            (true, false) => "entra",
            (false, true) => "domain",
            _ => "workgroup",
        };
    }

    /// <summary>More than one local administrator.</summary>
    public static bool HasExtraAdmins(JsonElement row) =>
        Prop(row, "summary") is { } summary && Int(summary, "adminUsers") > 1;

    /// <summary>The inventory usage, lower-cased: "shared", "assigned", or whatever else it says.</summary>
    public static string Usage(JsonElement row) => Str(row, "usage").ToLowerInvariant();

    public sealed record IdentitySummary(
        int Total, IReadOnlyList<(string Key, string Label, int Count)> Joins, int ExtraAdmins, int Shared, int Assigned);

    public static IdentitySummary Identity(IReadOnlyList<JsonElement> rows)
    {
        var joins = rows.GroupBy(JoinType).ToDictionary(g => g.Key, g => g.Count());
        return new IdentitySummary(rows.Count,
            JoinTypes.Select(j => (j.Key, j.Label, joins.GetValueOrDefault(j.Key))).Where(j => j.Item3 > 0).ToList(),
            rows.Count(HasExtraAdmins),
            rows.Count(r => Usage(r) == "shared"),
            rows.Count(r => Usage(r) == "assigned"));
    }

    // ── App usage ────────────────────────────────────────────────────────

    public sealed record AppUse(string Name, double Hours, int ActiveDevices, int InstalledDevices)
    {
        public double ActiveShare => InstalledDevices == 0 ? 0 : (double)ActiveDevices / InstalledDevices;
    }

    /// <summary>
    /// The five most-used apps by <c>totalHours</c>, and the five least-used of the
    /// apps installed on ten or more devices, by the share of those devices that used them.
    /// </summary>
    public static (IReadOnlyList<AppUse> Most, IReadOnlyList<AppUse> Least) AppUsage(JsonElement usage)
    {
        var apps = Prop(usage, "applications") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray()
                .Select(a => new AppUse(Str(a, "name"), Number(a, "totalHours") ?? 0,
                    (int)(Number(a, "activeDeviceCount") ?? Number(a, "deviceCount") ?? 0),
                    Int(a, "installedDeviceCount")))
                .Where(a => a.Name.Length > 0)
                .ToList()
            : [];
        var most = apps.OrderByDescending(a => a.Hours).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        var least = apps.Where(a => a.InstalledDevices >= 10)
            .OrderBy(a => a.ActiveShare).ThenByDescending(a => a.InstalledDevices).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5).ToList();
        return (most, least);
    }

    // ── Network and peripherals ──────────────────────────────────────────

    /// <summary>
    /// Below this a Wi-Fi signal is weak: the network report's "Poor" band, under
    /// 25 percent.
    /// </summary>
    public const int WeakSignalPercent = 25;

    /// <summary>
    /// The active Wi-Fi signal as a percentage. Windows reports a percentage on the
    /// active connection; a Mac reports the current network's RSSI in dBm, which is
    /// mapped onto the same scale (-100 dBm is 0%, -50 dBm and better is 100%).
    /// </summary>
    public static int? WifiSignalPercent(JsonElement row)
    {
        var raw = Prop(row, "raw") ?? row;
        if (Prop(raw, "activeConnection") is { } active
            && (Number(active, "wifiSignalStrength") ?? Number(active, "wifi_signal_strength")) is { } strength && strength != 0)
            return ToPercent(strength);

        if (Prop(raw, "wifiNetworks") is { ValueKind: JsonValueKind.Array } networks)
            foreach (var net in networks.EnumerateArray())
            {
                var current = Boolish(net, "isCurrent") || Boolish(net, "current") || Boolish(net, "isConnected");
                if (current && (Number(net, "rssi") ?? Number(net, "signalStrength")) is { } v && v != 0) return ToPercent(v);
            }
        return null;

        static int ToPercent(double value) => value < 0
            ? (int)Math.Clamp(2 * (value + 100), 0, 100)
            : (int)Math.Clamp(value, 0, 100);
    }

    public static bool IsWeakSignal(JsonElement row) =>
        Network.ConnectionKind(row) == "Wireless" && WifiSignalPercent(row) is > 0 and < WeakSignalPercent;

    /// <summary>The link value for a connection kind: <c>wired</c> or <c>wifi</c>.</summary>
    public static string? ConnectionKey(JsonElement row) => Network.ConnectionKind(row) switch
    {
        "Wired" => "wired",
        "Wireless" => "wifi",
        _ => null,
    };

    public static int Printers(JsonElement row) => Prop(row, "printers") is { ValueKind: JsonValueKind.Array } p ? p.GetArrayLength() : 0;

    public static int Displays(JsonElement row) =>
        Prop(row, "displayDevices") is { ValueKind: JsonValueKind.Array } p ? p.GetArrayLength()
        : Prop(row, "displays") is { ValueKind: JsonValueKind.Array } h ? h.GetArrayLength() : 0;

    /// <summary>The room a device sits in, as the inventory records it.</summary>
    public static string Room(JsonElement row) => Str(row, "location") is { Length: > 0 } l ? l : Str(row, "room");

    public sealed record NetworkSummary(
        int Wired, int Wifi, int Weak, int Printers, int Displays, IReadOnlyList<(string Room, int Printers)> Rooms);

    /// <param name="hardware">
    /// The hardware rows, for displays: the Macs leave the peripherals display list
    /// empty and report their displays in the hardware module.
    /// </param>
    public static NetworkSummary NetworkAndPeripherals(
        IReadOnlyList<JsonElement> network, IReadOnlyList<JsonElement> peripherals, IReadOnlyList<JsonElement>? hardware = null)
    {
        var displays = peripherals.ToDictionary(r => Str(r, "serialNumber"), Displays, StringComparer.OrdinalIgnoreCase);
        foreach (var row in hardware ?? [])
        {
            var serial = Str(row, "serialNumber");
            if (serial.Length == 0) continue;
            displays[serial] = Math.Max(displays.GetValueOrDefault(serial), Displays(row));
        }

        var rooms = peripherals
            .Where(r => Printers(r) > 0 && Room(r).Length > 0)
            .GroupBy(Room, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Room: g.Key, Printers: g.Sum(Printers)))
            .OrderByDescending(g => g.Printers)
            .ThenBy(g => g.Room, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        return new NetworkSummary(
            network.Count(r => ConnectionKey(r) == "wired"),
            network.Count(r => ConnectionKey(r) == "wifi"),
            network.Count(IsWeakSignal),
            peripherals.Sum(Printers),
            displays.Values.Sum(),
            rooms);
    }
}
