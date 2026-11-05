using System;
using System.Collections.Generic;
using System.Linq;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Identity › Sign-in &amp; Recovery Posture: what each device last reported about
/// local admin passwords, auto-login, UAC, Hello, Platform SSO, tokens and recovery
/// keys. A value the device did not report reads "Unknown"; a check that belongs to
/// the other platform reads "Not applicable".
/// </summary>
public static partial class TabReports
{
    /// <summary>The quick views: key, label and the platform each one applies to.</summary>
    public static readonly (string Key, string Label, string Platform)[] PostureViews =
    [
        ("noLaps", "No LAPS", "Windows"),
        ("autoLogin", "Auto-login on", "Windows"),
        ("expiredPssoToken", "Expired Platform SSO token", "macOS"),
        ("activationLock", "Activation Lock on", "macOS"),
        ("noRecoveryKey", "No FileVault recovery key", "macOS"),
    ];

    public static string Tri(bool? value, string yes = "Yes", string no = "No") => value switch { true => yes, false => no, _ => "Unknown" };

    private static string Num(double? value, string unit = "") => value is { } v ? Invariant(v) + unit : "Unknown";

    private static string Reported(TabRow r, string key) => r.Str("reported." + key) ?? "";

    /// <summary>The text of a posture column, null where the column's platform is not the device's.</summary>
    public static string? PostureText(TabRow r, string column)
    {
        var scope = PostureColumns.FirstOrDefault(c => c.Key == column).Scope;
        if (scope is not null and not "all" && !string.Equals(scope, r.Platform, StringComparison.Ordinal)) return null;
        string P(string key) => Reported(r, key);
        bool? B(string key) => r.Bool("reported." + key);
        double? N(string key) => r.Num("reported." + key);
        return column switch
        {
            "deviceName" => r.DeviceName,
            "platform" => r.Platform,
            "inventoryState" => InventoryLabel(r.Str("inventoryState")),
            "laps" => P("laps") switch
            {
                "configured" => r.Str("reported.lapsBackupDirectory") is { } dir ? $"Configured ({dir})" : "Configured",
                "not_configured" => "Not configured",
                _ => "Unknown",
            },
            "autoLogin" => B("autoLogin") == true ? (B("autoLoginStoredPassword") == true ? "On, stored password" : "On") : Tri(B("autoLogin"), "On", "Off"),
            "uac" => B("uacEnabled") == false ? "Off" : r.Str("reported.uacLevel") ?? (B("uacEnabled") == true ? "On" : "Unknown"),
            "passwordPolicy" => $"Min length {Num(N("minPasswordLength"))} · Lockout after {Num(N("lockoutThreshold"))} for {Num(N("lockoutDurationMinutes"), " min")}",
            "hello" => $"{r.Str("reported.helloStatus") ?? "Status unknown"} · " + (r.Node("reported.helloProviders") is null
                ? "Providers unknown"
                : r.Strs("reported.helloProviders") is { Count: > 0 } providers ? string.Join(", ", providers) : "No providers"),
            "credentialGuard" => Tri(B("credentialGuard"), "On", "Off"),
            "tpm" => B("tpmOwned") == true ? (B("tpmReady") == false ? "Owned, not ready" : "Owned") : Tri(B("tpmOwned"), "Owned", "Not owned"),
            "failedLogins" => Num(N("failedLoginsLast7Days")),
            "psso" => P("pssoState") switch
            {
                "valid" => "Registered, tokens valid",
                "expired" => "Token expired",
                "not_registered" => "Not registered",
                "no_registered_users" => "Device registered, no users",
                _ => "Unknown",
            },
            "secureToken" => N("secureTokenGranted") is { } granted ? $"{Invariant(granted)} granted · {Num(N("secureTokenMissing"))} missing" : "Unknown",
            "bootstrapToken" => Tri(B("bootstrapTokenEscrowed"), "Escrowed", "Not escrowed"),
            "activationLock" => P("activationLock") switch
            {
                "on" => "On",
                "likely" => "Likely on (inferred from Find My)",
                "off" => "Off",
                _ => "Unknown",
            },
            "findMy" => Tri(B("findMy"), "On", "Off"),
            "recoveryKey" => P("recoveryKey") switch
            {
                "present" => string.Join(" + ", new[] { B("personalRecoveryKey") == true ? "Personal" : null, B("institutionalRecoveryKey") == true ? "Institutional" : null }.Where(s => s is not null)),
                "none" => "None on device",
                "filevault_off" => "FileVault off",
                _ => "Unknown",
            },
            "identityCollectedAt" => r.Str("identityCollectedAt"),
            "securityCollectedAt" => r.Str("securityCollectedAt"),
            "lastSeen" => r.Str("lastSeen"),
            _ => null,
        };
    }

    public static readonly (string Key, string Label, string Scope)[] PostureColumns =
    [
        ("deviceName", "Device", "all"), ("platform", "Platform", "all"), ("inventoryState", "Inventory", "all"),
        ("laps", "LAPS", "Windows"), ("autoLogin", "Auto-login", "Windows"), ("uac", "UAC", "Windows"),
        ("passwordPolicy", "Password policy", "Windows"), ("hello", "Windows Hello", "Windows"),
        ("credentialGuard", "Credential Guard", "Windows"), ("tpm", "TPM ownership", "Windows"),
        ("failedLogins", "Failed sign-ins (7 days)", "Windows"),
        ("psso", "Platform SSO", "macOS"), ("secureToken", "Secure Token", "macOS"), ("bootstrapToken", "Bootstrap Token", "macOS"),
        ("activationLock", "Activation Lock", "macOS"), ("findMy", "Find My", "macOS"), ("recoveryKey", "FileVault recovery key", "macOS"),
        ("identityCollectedAt", "Identity collection", "all"), ("securityCollectedAt", "Security collection", "macOS"),
        ("lastSeen", "Last seen", "all"),
    ];

    private static readonly string[] PostureTimeColumns = ["identityCollectedAt", "securityCollectedAt", "lastSeen"];
    private static readonly string[] PostureFixed = ["deviceName", "platform", "inventoryState", "identityCollectedAt", "securityCollectedAt", "lastSeen"];

    public static readonly TabReportDef Posture = new()
    {
        Module = "identity", Tab = "posture", Title = "Sign-in & Recovery Posture",
        Description = "Sign-in and recovery posture each device last reported.",
        Footnote = "Values are what each device last reported. Unknown means the device did not report it; it is never read as healthy.",
        Api = "/api/v1/identity/posture", FiltersApi = null,
        RowsProperty = "devices", PageSize = 500, RowNoun = "devices",
        ApiParams = WithScope("views", "freshness", "maxAgeHours"),
        RowKey = r => r.Serial,
        Choices =
        [
            new TabChoice("views", "Quick views")
            {
                AllLabel = "All devices", Prominent = true,
                From = c => PostureViews.Where(v => c.Platform == "all" || c.Platform == v.Platform)
                    .Select(v => new TabOption(v.Key, v.Label + (c.Data is { } d
                        ? $" {Count(d.Summary(v.Key))}" + (d.Summary(v.Key, "unknown") is > 0 and var unknown ? $" · {unknown} unknown" : "")
                        : ""))).ToList(),
            },
            .. Scope("Identity collection age", "168", rooms: true),
        ],
        Columns = PostureColumns.Select(c => new TabColumn(c.Key, c.Label, r => PostureText(r, c.Key),
            c.Key == "deviceName" ? TabCell.Device : PostureTimeColumns.Contains(c.Key) ? TabCell.Time : TabCell.Text)
        {
            Empty = "Not applicable",
            Width = c.Key is "passwordPolicy" or "hello" ? 260 : 150,
            Rank = c.Key == "failedLogins" ? r => r.IsPlatform("Windows") ? r.Num("reported.failedLoginsLast7Days") : null : null,
            Display = PostureTimeColumns.Contains(c.Key)
                ? r => PostureText(r, c.Key) is { } at ? (TabJson.Time(at) is { } t ? Format.RelativeTime(t) : at) : (c.Scope == "all" || c.Scope == r.Platform ? "Unknown" : null)
                : null,
        }).ToList(),
        Available = (c, col) => PostureColumns.First(p => p.Key == col.Key).Scope is var scope && (scope == "all" || c.Platform == "all" || scope == c.Platform),
        DefaultColumns = c => c.Platform switch
        {
            "Windows" => ["deviceName", "laps", "autoLogin", "uac", "passwordPolicy", "hello", "credentialGuard", "tpm", "failedLogins", "identityCollectedAt"],
            "macOS" => ["deviceName", "psso", "secureToken", "bootstrapToken", "activationLock", "findMy", "recoveryKey", "identityCollectedAt", "securityCollectedAt"],
            _ => ["deviceName", "platform", "laps", "autoLogin", "psso", "activationLock", "recoveryKey", "inventoryState", "identityCollectedAt"],
        },
        RowLink = (r, _) => DeviceLink(r, "identity"),
        CsvName = "identity-posture",
        CsvHeaders = _ =>
        [
            "Device Name", "Computer Name", "Serial Number", "Asset Tag", "Platform", "Inventory State", "Archived in ReportMate",
            "Usage", "Catalog", "Area", "Fleet", "Location", "Room",
            .. PostureColumns.Where(c => !PostureFixed.Contains(c.Key)).Select(c => c.Label),
            "Platform SSO Users", .. PostureViews.Select(v => $"Quick View: {v.Label}"),
            "Identity Collection Time", "Identity Collection Freshness", "Security Collection Time", "Security Collection Freshness",
            "Last Seen", "Report Generated At", "Report URL",
        ],
        CsvRow = (r, c, url) =>
        [
            r.Str("deviceName"), r.Str("computerName"), r.Serial, r.Str("assetTag"), r.Platform,
            InventoryLabel(r.Str("inventoryState")), r.Bool("archived") == true ? "Yes" : "No",
            r.Str("usage"), r.Str("catalog"), r.Str("area"), r.Str("fleet"), r.Str("location"), r.Str("room"),
            .. PostureColumns.Where(p => !PostureFixed.Contains(p.Key)).Select(p => PostureText(r, p.Key) ?? "Not applicable"),
            string.Join("\n", r.Items("reported.pssoUsers").Select(u =>
                $"{u.Str("username") ?? "Unknown user"}: {u.Str("token")}" + (u.Str("tokenExpiration") is { } exp ? $" (expires {exp})" : ""))),
            .. PostureViews.Select(v => v.Platform != r.Platform ? "Not applicable" : Tri(r.Bool("checks." + v.Key))),
            r.Str("identityCollectedAt"), r.Str("identityFreshness"), r.Str("securityCollectedAt"), r.Str("securityFreshness"),
            r.Str("lastSeen"), c.Data?.GeneratedAt, url,
        ],
    };

    private static bool IsPlatform(this TabRow r, string platform) => string.Equals(r.Platform, platform, StringComparison.Ordinal);
}
