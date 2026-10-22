// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static ReportMate.App.Services.MacModules.MacJson;

namespace ReportMate.App.Services.MacModules;

/// <summary>How a security check reads, before any colour is chosen.</summary>
public enum MacSeverity { Unknown, Ok, Neutral, Warning, Danger }

/// <summary>
/// A Mac's security module, read the way the Mac app's SecurityInfo does
/// (ReportMateKit/Processing/SecurityInfo.swift, its Mac branches): the flags the
/// six posture cards show, the certificate list, the SOFA CVE list and the threat
/// detections. The Mac client spells a few keys differently from what the Mac and
/// web apps look for (stealthModeEnabled, allowSignedEnabled, securityReleaseInfo,
/// unpatchedCVEs), so each lookup takes both spellings.
/// </summary>
public sealed class MacSecurityInfo
{
    public JsonElement Security { get; }
    /// <summary>Remote management lives in the management module on the Mac, with the security module's copy as fallback.</summary>
    public JsonElement? RemoteManagement { get; }

    private MacSecurityInfo(JsonElement security, JsonElement? remoteManagement)
    {
        Security = security;
        RemoteManagement = remoteManagement;
    }

    /// <summary>Null when the module is absent or empty.</summary>
    public static MacSecurityInfo? From(JsonElement? security, JsonElement? management = null)
    {
        if (Unwrap(security) is not { } sec) return null;
        var empty = sec.ValueKind switch
        {
            JsonValueKind.Object => !sec.EnumerateObject().Any(),
            JsonValueKind.Array => sec.GetArrayLength() == 0,
            _ => true,
        };
        if (empty) return null;
        var rm = Node(Unwrap(management), "remoteManagement") ?? Node(sec, "remoteManagement");
        return new MacSecurityInfo(sec, rm);
    }

    /// <summary>A module sent as a one-element array is read as that element.</summary>
    private static JsonElement? Unwrap(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.Array } a when a.GetArrayLength() == 1 => a[0],
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => e,
    };

    private static bool On(JsonElement? parent, params string[] paths) => Bool(parent, paths) == true;

    // ── Severity ─────────────────────────────────────────────────────

    /// <summary>
    /// The historical defaults of the Mac and web security evaluators. Their
    /// inventory-scoped rules (a shared lab Mac with FileVault off reads neutral)
    /// need the settings document, which this app does not read yet.
    /// </summary>
    public static MacSeverity Severity(string check, bool? enabled)
    {
        if (enabled is not { } on) return MacSeverity.Unknown;
        return check switch
        {
            "encryption" or "sip" => on ? MacSeverity.Ok : MacSeverity.Danger,
            "firewall" => on ? MacSeverity.Ok : MacSeverity.Warning,
            "ssh" or "rdp" => on ? MacSeverity.Warning : MacSeverity.Ok,
            _ => on ? MacSeverity.Ok : MacSeverity.Warning,
        };
    }

    // ── Platform flags ───────────────────────────────────────────────

    public bool FileVaultEnabled
    {
        get
        {
            var fv = Node(Security, "fileVault");
            var status = (Str(fv, "status") ?? "").ToLowerInvariant();
            return On(fv, "enabled") || status is "on" or "enabled";
        }
    }

    public bool SipEnabled => On(Security, "systemIntegrityProtection.enabled");
    public bool GatekeeperEnabled => On(Security, "gatekeeper.enabled");

    public bool FirewallEnabled
    {
        get
        {
            var fw = Node(Security, "firewall");
            if (On(fw, "isEnabled") || On(fw, "enabled")) return true;
            if (Node(fw, "globalState") is { ValueKind: JsonValueKind.String } s && !double.TryParse(s.GetString(), out _))
                return string.Equals(s.GetString(), "on", StringComparison.OrdinalIgnoreCase);
            // 1 is on, 2 is on and blocking all incoming connections.
            return Int(fw, "globalState") >= 1;
        }
    }

    public bool SshServiceEnabled => On(Security, "ssh.enabled");
    public bool SshEnabled => SshServiceEnabled || On(RemoteManagement, "remoteLoginEnabled");
    public bool ArdEnabled => On(RemoteManagement, "ardEnabled");
    public bool ScreenSharingEnabled => ArdEnabled || On(RemoteManagement, "screenSharingEnabled");
    public string? LastScan => Str(Security, "antivirus.lastScan", "lastSecurityScan");

    // ── Tampering ────────────────────────────────────────────────────

    public bool SecureEnclavePresent => On(Security, "secureEnclave.present");
    public bool BiometricSupported => On(Security, "secureEnclave.touchIdSupported");
    public string? ExternalBoot => Str(Security, "secureBoot.externalBootAllowed");
    public bool RootUserEnabled => On(Security, "rootUser.enabled");

    /// <summary>The sealed system volume: true unless SIP's config allows an unauthenticated root.</summary>
    public bool AuthenticatedRootRequired =>
        Int(Security, "systemIntegrityProtection.configFlags.allowUnauthenticatedRoot") != 1;

    // ── Protection ───────────────────────────────────────────────────

    public string AllowAppsFrom => On(Security, "gatekeeper.developerIdEnabled") ? "App Store & Known Developers" : "Anywhere";
    public bool XProtectActive => On(Security, "xprotect.enabled");
    public string? XProtectVersion => Str(Security, "xprotect.version");
    public string? XProtectSignatures => Str(Security, "xprotect.signatureCount");
    public bool MrtInstalled => On(Security, "mrt.enabled");
    public string? MrtVersion => Str(Security, "mrt.version");

    // ── Detection ────────────────────────────────────────────────────

    public sealed record EdrProduct(
        string Name, string? Identifier, bool? Healthy, bool? Running, string? Version,
        bool? RealTimeProtection, bool? CloudProtection, bool? TamperProtection, bool? BehaviorMonitoring,
        string? EngineVersion, string? DefinitionsVersion, string? DefinitionsUpdated,
        string? LastScanTime, string? LastScanType, long? FilesScanned, int ThreatsDetected,
        bool? Licensed, bool? FullDiskAccess, IReadOnlyList<string> HealthIssues,
        string? State, string? AgentId)
    {
        public string HealthLabel => Healthy == true || Running == true ? "Healthy" : Healthy == false ? "Issues" : "Unknown";
        public bool IsDefender => Identifier == "com.microsoft.wdav";
        public bool IsCrowdStrike => Identifier == "com.crowdstrike.falcon";
    }

    public IReadOnlyList<EdrProduct> EdrProducts =>
        Arr(Security, "edrProducts.products").Select(p => new EdrProduct(
            Str(p, "name", "vendor") ?? "Unknown EDR", Str(p, "identifier"), Bool(p, "healthy"), Bool(p, "running"), Str(p, "version"),
            Bool(p, "realTimeProtection"), Bool(p, "cloudProtection"), Bool(p, "tamperProtection"), Bool(p, "behaviorMonitoring"),
            Str(p, "engineVersion"), Str(p, "definitionsVersion"), Str(p, "definitionsUpdated"),
            Str(p, "lastScanTime"), Str(p, "lastScanType"), Long(p, "lastScanFilesScanned"), Int(p, "totalThreatsDetected") ?? 0,
            Bool(p, "licensed"), Bool(p, "fullDiskAccess"),
            Arr(p, "healthIssues").Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!.Trim()).Where(i => i.Length > 0).ToList(),
            Str(p, "state"), Str(p, "agentId"))).ToList();

    public sealed record EndpointExtension(string Name, string? Version, string? TeamId, bool Active);

    public IReadOnlyList<EndpointExtension> EndpointExtensions =>
        Arr(Security, "endpointSecurity.extensions").Select(e => new EndpointExtension(
            Str(e, "name", "identifier") ?? "Unknown", Str(e, "version"), Str(e, "teamId"),
            On(e, "enabled") || On(e, "active") || (Str(e, "state") ?? "").Contains("enabled"))).ToList();

    // ── Encryption ───────────────────────────────────────────────────

    public IReadOnlyList<string> EncryptedVolumes =>
        Arr(Security, "fileVault.encryptedVolumes")
            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : Str(v, "name", "volumeName"))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();

    public string EncryptedVolumesLabel =>
        EncryptedVolumes.Count > 0 ? string.Join(", ", EncryptedVolumes) : FileVaultEnabled ? "System Volume" : "None";

    // ── Firewall ─────────────────────────────────────────────────────

    public bool StealthMode => On(Security, "firewall.stealthMode", "firewall.stealthModeEnabled");
    public bool FirewallLogging => On(Security, "firewall.loggingEnabled");
    public bool AllowSignedApps => On(Security, "firewall.allowSignedSoftware", "firewall.allowSignedEnabled");

    // ── Remote access ────────────────────────────────────────────────

    public string SshAuthorizedUsers => Str(Security, "ssh.authorizedUsers") switch
    {
        "all" => "All",
        null => "Not set",
        var u => u,
    };

    /// <summary>"Public Key, Password", or null when neither is reported.</summary>
    public string? SshAuthentication
    {
        get
        {
            var methods = new List<string>();
            if (Str(Security, "ssh.pubkeyAuthentication") == "yes") methods.Add("Public Key");
            if (Str(Security, "ssh.passwordAuthentication") == "yes") methods.Add("Password");
            return methods.Count == 0 ? null : string.Join(", ", methods);
        }
    }

    public string? ArdAllowedUsers => Str(RemoteManagement, "ardAllowedUsers") is { } a ? (a == "true" ? "All Users" : a) : null;

    // ── Certificates ─────────────────────────────────────────────────

    public enum CertStatus { Valid, ExpiringSoon, Expired }

    public sealed record Certificate(
        string CommonName, string Subject, string Issuer, string NotBefore, string NotAfter,
        CertStatus Status, int? DaysUntilExpiry, string Thumbprint, bool IsSelfSigned,
        string Path, string Store, bool IsOsTrustedRoot)
    {
        public bool IsExpired => Status == CertStatus.Expired;
        public bool IsExpiringSoon => Status == CertStatus.ExpiringSoon;

        public string DisplayName
        {
            get
            {
                if (CommonName.Length > 0) return CommonName;
                if (Subject.Length == 0) return "—";
                var first = Subject.Split(',')[0];
                return System.Text.RegularExpressions.Regex.Replace(first, @"^CN\s*=\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        public string KeychainName => Path.Length == 0 ? "—" : Path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? Path;
        public string DisplayIssuer => Issuer.Length > 50 ? Issuer[..50] + "..." : Issuer.Length == 0 ? "—" : Issuer;

        public bool Matches(string query)
        {
            var q = query.Trim();
            return q.Length == 0
                || CommonName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Subject.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Issuer.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Thumbprint.Contains(q, StringComparison.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<Certificate> Certificates => Arr(Security, "certificates").Select(ReadCertificate).ToList();

    private static Certificate ReadCertificate(JsonElement c)
    {
        var status = Str(c, "status") switch
        {
            "Valid" => CertStatus.Valid,
            "ExpiringSoon" => CertStatus.ExpiringSoon,
            "Expired" => CertStatus.Expired,
            _ => On(c, "isExpired") ? CertStatus.Expired : On(c, "isExpiringSoon") ? CertStatus.ExpiringSoon : CertStatus.Valid,
        };
        var location = Str(c, "storeLocation") ?? "";
        return new Certificate(
            Str(c, "commonName") ?? "", Str(c, "subject") ?? "", Str(c, "issuer") ?? "",
            Str(c, "notBefore", "notValidBefore") ?? "", Str(c, "notAfter", "notValidAfter") ?? "",
            status, Int(c, "daysUntilExpiry"), Str(c, "thumbprint", "sha1") ?? "",
            On(c, "isSelfSigned") || On(c, "selfSigned"), Str(c, "path") ?? "",
            location is "LocalMachine" or "System" ? "System" : "User", On(c, "isOsTrustedRoot"));
    }

    /// <summary>Expired OS roots the client counted, so they can be hidden without losing the number.</summary>
    public int OsRootExpiredCount =>
        Int(Security, "certificateSummary.osRootExpiredCount") ?? Certificates.Count(c => c.IsOsTrustedRoot && c.IsExpired);

    public int? UserExpiredCount => Int(Security, "certificateSummary.userExpiredCount");

    // ── CVEs (SOFA) ──────────────────────────────────────────────────

    public sealed record SofaRelease(string? UpdateName, string? ReleaseDate, int? DaysSincePreviousRelease, string? SecurityInfo, string? OsVersion, int? UniqueCvesCount);

    public SofaRelease? Release
    {
        get
        {
            if (Node(Security, "sofaSecurityReleaseInfo", "securityReleaseInfo") is not { ValueKind: JsonValueKind.Object } r) return null;
            return new SofaRelease(Str(r, "updateName"), Str(r, "releaseDate"), Int(r, "daysSincePreviousRelease"),
                Str(r, "securityInfo"), Str(r, "osVersion"), Int(r, "uniqueCvesCount"));
        }
    }

    public sealed record Cve(string Id, string OsVersion, string PatchedVersion, bool ActivelyExploited, string? Url, string Status)
    {
        public bool IsUnpatched => Status == "Unpatched";
    }

    public IReadOnlyList<Cve> Cves
    {
        get
        {
            var release = Release;
            return Arr(Security, "sofaUnpatchedCves", "unpatchedCVEs").Select(c => c.ValueKind == JsonValueKind.String
                ? new Cve(c.GetString() ?? "Unknown", release?.OsVersion ?? "", "", false, null, "Unpatched")
                : new Cve(Str(c, "cve", "cveId") ?? "Unknown", Str(c, "osVersion") ?? release?.OsVersion ?? "",
                    Str(c, "patchedVersion") ?? "", On(c, "activelyExploited"), Str(c, "url"), "Unpatched")).ToList();
        }
    }

    /// <summary>Unpatched first, exploited first within, then by id.</summary>
    public static IReadOnlyList<Cve> SortCves(IEnumerable<Cve> list) =>
        list.OrderBy(c => c.IsUnpatched ? 0 : 1).ThenBy(c => c.ActivelyExploited ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();

    public bool PendingRebootRequired => On(Security, "pendingReboot.required");

    // ── Detections ───────────────────────────────────────────────────

    public enum StatusMark { Good, Attention, Bad, Unknown }

    public sealed record Detection(
        string ThreatName, string? Severity, string? Category, string? Status, string? Source, string? DetectedAt,
        string? ThreatId, string? FilePath, string? ProcessName, string? User, string? ActionTaken, string? Description, string? EventId)
    {
        public bool HasDetails => FilePath != null || ProcessName != null || User != null || Description != null || ThreatId != null;

        public StatusMark Mark => (Status ?? "").ToLowerInvariant() switch
        {
            "cleaned" or "removed" or "quarantined" or "blocked" or "remediated" => StatusMark.Good,
            "detected" or "actiontaken" => StatusMark.Attention,
            "allowed" or "missed" or "remediationfailed" => StatusMark.Bad,
            _ => StatusMark.Unknown,
        };
    }

    public IReadOnlyList<Detection> Detections =>
        Arr(Security, "detections").Select(d => new Detection(
            Str(d, "threatName") ?? "Unknown Threat", Str(d, "severity"), Str(d, "category"), Str(d, "status"), Str(d, "source"),
            Str(d, "detectedAt"), Str(d, "threatId"), Str(d, "filePath"), Str(d, "processName"), Str(d, "user"),
            Str(d, "actionTaken"), Str(d, "description"), Str(d, "eventId"))).ToList();

    public sealed record DetectionSummary(int Total30d, int Blocked30d, int Cleaned30d, int Allowed30d, bool HasActiveThreats);

    public DetectionSummary Summary
    {
        get
        {
            var s = Node(Security, "detectionSummary");
            return new DetectionSummary(Int(s, "totalDetections30d") ?? Detections.Count, Int(s, "totalBlocked30d") ?? 0,
                Int(s, "totalCleaned30d") ?? 0, Int(s, "totalAllowed30d") ?? 0, On(s, "hasActiveThreats"));
        }
    }
}
