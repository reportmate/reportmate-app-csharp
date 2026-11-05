using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Enrollment health on the Device Management Service report: compliance, the last
/// MDM sync, the MDM certificate's expiry and the macOS Apple MDM flags. The API
/// sends null for whatever the device did not collect. Null always reads as Unknown,
/// never as healthy, and an unknown value never matches a problem filter.
/// </summary>
public static class EnrollmentHealth
{
    public const int CertExpiryDays = 30;
    public static readonly int[] SyncAgeChoices = [1, 7, 30];
    public static readonly string[] Params = ["compliance", "syncOlderThan", "certExpiring"];

    /// <summary>The problem filters a link carries: <c>compliance=non_compliant</c>, <c>syncOlderThan=&lt;days&gt;</c>, <c>certExpiring=&lt;days&gt;</c>.</summary>
    public sealed record Filters(bool NonCompliant, int? SyncOlderThanDays, int? CertExpiringDays)
    {
        public int Active => (NonCompliant ? 1 : 0) + (SyncOlderThanDays is null ? 0 : 1) + (CertExpiringDays is null ? 0 : 1);

        public static Filters From(Func<string, string?> read) =>
            new(read("compliance") == "non_compliant", PositiveDays(read("syncOlderThan")), PositiveDays(read("certExpiring")));

        public IEnumerable<(string Key, string? Value)> Pairs() =>
        [
            ("compliance", NonCompliant ? "non_compliant" : null),
            ("syncOlderThan", SyncOlderThanDays?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("certExpiring", CertExpiringDays?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];
    }

    private static int? PositiveDays(string? value) =>
        value is { Length: > 0 } && value.All(char.IsDigit) && int.TryParse(value, out var n) && n is > 0 and <= 3650 ? n : null;

    private static string? Str(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool? Bool(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) ? TabJson.Bool(v) : null;

    public static bool IsMac(JsonElement row) =>
        (Str(row, "platform") ?? Str(row, "osName") ?? "") is var p && (p.Contains("mac", StringComparison.OrdinalIgnoreCase) || p.Contains("darwin", StringComparison.OrdinalIgnoreCase));

    /// <summary>Days since the last MDM sync; null when unknown or in the future.</summary>
    public static double? SyncAgeDays(string? value, DateTime now) =>
        TabJson.Time(value) is { } at && at <= now ? (now - at).TotalDays : null;

    /// <summary>Days until the MDM certificate expires, negative once it has; null when unknown.</summary>
    public static double? CertDaysLeft(string? value, DateTime now) =>
        TabJson.Time(value) is { } at ? (at - now).TotalDays : null;

    public static bool Matches(JsonElement row, Filters filters, DateTime now)
    {
        if (filters.NonCompliant && Str(row, "complianceState") != "non_compliant") return false;
        if (filters.SyncOlderThanDays is { } days && (SyncAgeDays(Str(row, "lastMdmSync"), now) is not { } age || age <= days)) return false;
        if (filters.CertExpiringDays is { } within && (CertDaysLeft(Str(row, "mdmCertificateExpires"), now) is not { } left || left > within)) return false;
        return true;
    }

    public static string ComplianceLabel(string? state) => state switch
    {
        "compliant" => "Compliant",
        "non_compliant" => "Not compliant",
        _ => "Unknown",
    };

    /// <summary>Apple MDM flags exist only on macOS: Windows reads "Not applicable", a missing Mac value "Unknown".</summary>
    public static string AppleFlag(bool? value, bool mac) => !mac ? "Not applicable" : value switch { true => "Yes", false => "No", _ => "Unknown" };

    private static string Days(double n)
    {
        var whole = (int)Math.Floor(Math.Abs(n));
        return whole == 1 ? "1 day" : $"{whole} days";
    }

    // ── The table cells ──────────────────────────────────────────────────

    public static string Compliance(JsonElement row) => ComplianceLabel(Str(row, "complianceState"));

    public static string LastSync(JsonElement row, DateTime now) =>
        SyncAgeDays(Str(row, "lastMdmSync"), now) is { } age ? age < 1 ? "Today" : $"{Days(age)} ago" : "Unknown";

    public static string Certificate(JsonElement row, DateTime now) =>
        CertDaysLeft(Str(row, "mdmCertificateExpires"), now) is { } left
            ? left < 0 ? $"Expired {Days(left)} ago" : $"Expires in {Days(left)}"
            : "Unknown";

    public static string AppleMdm(JsonElement row) => IsMac(row)
        ? $"User approved: {AppleFlag(Bool(row, "userApprovedMdm"), true)} · ADE installed: {AppleFlag(Bool(row, "installedFromDep"), true)} · DEP capable: {AppleFlag(Bool(row, "depCapable"), true)}"
        : "Not applicable";

    // ── The export ───────────────────────────────────────────────────────

    public static readonly string[] CsvHeaders =
        ["Compliance", "Compliance Source", "Last MDM Sync", "MDM Certificate Expires", "User-Approved MDM", "Installed via ADE", "DEP Capable"];

    public static string[] CsvValues(JsonElement row)
    {
        var mac = IsMac(row);
        return
        [
            Compliance(row),
            Str(row, "complianceSource") switch { "reported" => "Reported by device", "policies" => "Evaluated policies", _ => "" },
            Str(row, "lastMdmSync") ?? "Unknown",
            Str(row, "mdmCertificateExpires") ?? "Unknown",
            AppleFlag(Bool(row, "userApprovedMdm"), mac),
            AppleFlag(Bool(row, "installedFromDep"), mac),
            AppleFlag(Bool(row, "depCapable"), mac),
        ];
    }
}
