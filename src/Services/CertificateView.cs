// Explicit usings: this file is also compiled into the test project, which does not
// enable implicit usings.
using System;
using System.Text.Json;

namespace ReportMate.App.Services;

/// <summary>Who has to act on a certificate, as the API stamps it at ingest.</summary>
public enum CertificateClass
{
    /// <summary>Someone must renew it: org PKI identities, app-installed certs, deployed roots.</summary>
    Actionable,
    /// <summary>The OS or an agent issues and rotates it, or it is a keyless reference copy.</summary>
    SystemManaged,
    /// <summary>An OS root or intermediate the vendor ships and replaces.</summary>
    TrustStore,
}

/// <summary>A certificate's validity measured now, not when it was collected.</summary>
public enum CertificateStatus
{
    Valid,
    ExpiringSoon,
    Expired,
}

/// <summary>
/// How a certificate reads on the device and fleet views. The classification itself
/// belongs to the API, which stamps <c>class</c> on each certificate; the apps only
/// read it, so the web, Mac and Windows apps cannot disagree about what is actionable.
/// </summary>
public static class CertificateView
{
    /// <summary>Days ahead that count as expiring soon, the API's window.</summary>
    public const int ExpiringWindowDays = 30;

    /// <summary>The toggle that reveals everything that is not actionable, on every app.</summary>
    public const string ShowSystemManagedLabel = "Show system-managed";

    /// <summary>
    /// The class to show. A stamped class wins. An unstamped certificate is
    /// actionable, as the API treats it, except one the client itself marked as an
    /// OS-trusted root: that flag is the collector's own, and calling every bundled
    /// root actionable would bury the certificates someone has to renew.
    /// </summary>
    public static CertificateClass Classify(string? stampedClass, bool isOsTrustedRoot = false) =>
        stampedClass?.Trim().ToLowerInvariant() switch
        {
            "actionable" => CertificateClass.Actionable,
            "system-managed" => CertificateClass.SystemManaged,
            "trust-store" => CertificateClass.TrustStore,
            _ => isOsTrustedRoot ? CertificateClass.TrustStore : CertificateClass.Actionable,
        };

    /// <summary>The badge text, the same words the web and Mac apps use.</summary>
    public static string Label(CertificateClass cls) => cls switch
    {
        CertificateClass.SystemManaged => "System-managed",
        CertificateClass.TrustStore => "Trust store",
        _ => "Actionable",
    };

    /// <summary>
    /// The status now. The stored status, days and flags were frozen when the
    /// certificate was collected, so a certificate collected as valid a month ago
    /// may have expired since; <paramref name="notAfter"/> is the live answer and the
    /// stored fields are used only when it is missing.
    /// </summary>
    public static (CertificateStatus Status, int? Days) Live(
        DateTime? notAfter, DateTime now,
        string? storedStatus = null, bool storedExpired = false, bool storedExpiringSoon = false, int? storedDays = null)
    {
        if (notAfter is { } end)
        {
            var days = (int)Math.Floor((ToUtc(end) - ToUtc(now)).TotalDays);
            var status = days < 0 ? CertificateStatus.Expired
                : days <= ExpiringWindowDays ? CertificateStatus.ExpiringSoon
                : CertificateStatus.Valid;
            return (status, days);
        }

        var stored = storedStatus?.Trim().ToLowerInvariant();
        if (storedExpired || stored == "expired") return (CertificateStatus.Expired, storedDays);
        if (storedExpiringSoon || stored is "expiringsoon" or "expiring") return (CertificateStatus.ExpiringSoon, storedDays);
        return (CertificateStatus.Valid, storedDays);
    }

    private static DateTime ToUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    /// <summary>
    /// A readable name: the stamped display name, then the common name, then the
    /// CN out of the subject, then the subject itself.
    /// </summary>
    public static string DisplayName(string? displayName, string? commonName, string? subject) =>
        FirstNonEmpty(displayName, commonName, CommonNameOf(subject), subject) ?? "";

    /// <summary>The issuer as a name rather than a distinguished name.</summary>
    public static string IssuerName(string? issuerName, string? issuer) =>
        FirstNonEmpty(issuerName, CommonNameOf(issuer), issuer) ?? "";

    /// <summary>The CN of a distinguished name, or null when it has none.</summary>
    public static string? CommonNameOf(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return p[3..].Trim() is { Length: > 0 } cn ? cn : null;
        }
        return null;
    }

    /// <summary>
    /// The certificate verdict for one device row of <c>/api/v1/security</c>, the
    /// web report's buckets. The counts are actionable certificates only, so a
    /// device whose only expired certificates are OS roots or agent-rotated ones
    /// reads as Valid. Null when the row carries no certificate counts at all.
    /// </summary>
    public static string? FleetLabel(JsonElement row)
    {
        var expired = Int(row, "userExpiredCertCount") ?? Int(row, "expiredCertCount");
        var expiring = Int(row, "expiringSoonCertCount");
        if (expired is null && expiring is null) return null;
        if (expired > 0) return "Has Expired";
        if (expiring > 0) return "Expiring Soon";
        return "Valid";
    }

    /// <summary>The certificate column for one device row: what needs renewing, or Valid.</summary>
    public static string? FleetSummary(JsonElement row)
    {
        var expired = Int(row, "userExpiredCertCount") ?? Int(row, "expiredCertCount");
        var expiring = Int(row, "expiringSoonCertCount");
        if (expired is null && expiring is null) return null;
        var parts = new System.Collections.Generic.List<string>();
        if (expired > 0) parts.Add($"{expired} expired");
        if (expiring > 0) parts.Add($"{expiring} expiring");
        return parts.Count == 0 ? "Valid" : string.Join(", ", parts);
    }

    private static int? Int(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }
}
