// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A Mac's management module, read the way the Mac app's ManagementDetail
/// (ReportMateKit Processing/ManagementDetail.swift) reads it: MDM enrollment and
/// the identity certificate, ADE, the push topic, installed configuration profiles
/// with their payloads, and the managed-preference domains.
/// </summary>
public sealed class MacManagementInfo
{
    public sealed record Certificate(
        string? PushTopic, string? ScepUrl, string? Name, string? Subject, string? Issuer, string? Expires,
        string? EnrollmentMethod, string? IdentityPayloadType, string? CheckinUrl, string? ServerUrl, string? Provider)
    {
        public bool HasDetails => Name is not null || Issuer is not null;
        public static Certificate Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, null);
    }

    /// <summary>How a payload setting is shown: a Yes/No flag, a JSON block, or text.</summary>
    public enum SettingKind { Flag, Structured, Text }

    public sealed record Setting(string Key, SettingKind Kind, string Text, bool Flag);

    public sealed record Payload(int Index, string? DisplayName, string? Type, IReadOnlyList<Setting> Settings, string? RawData)
    {
        public string Title => DisplayName ?? Type ?? "Unknown Payload";
    }

    public sealed record Profile(
        string Identifier, string Name, string? Uuid, string? Organization, string? Description, string? InstallDate,
        IReadOnlyList<Payload> Payloads, int PayloadCount, string Scope, bool IsVerified, bool IsRemovalDisallowed, bool IsLegacyMcx)
    {
        public bool Matches(string query)
        {
            var q = query.Trim();
            return q.Length == 0
                || Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Identifier.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (Organization ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"Jan 5, 2026 at 10:00" style dates keep the date part only.</summary>
        public string? InstallDateText
        {
            get
            {
                if (InstallDate is not { } d) return null;
                if (!d.Contains(" at ")) return d;
                var head = d.Split(" at ")[0];
                return string.Join(", ", head.Split(", ").Take(2));
            }
        }
    }

    public sealed record PolicySetting(string Name, string Value)
    {
        /// <summary>"1"/"true" read as Yes, "0"/"false" as No, anything else as text.</summary>
        public bool? Flag => Value switch { "1" or "true" => true, "0" or "false" => false, _ => null };
    }

    public sealed record PolicyDomain(string Domain, IReadOnlyList<PolicySetting> Settings, int SettingCount)
    {
        public bool Matches(string query)
        {
            var q = query.Trim();
            return q.Length == 0
                || Domain.Contains(q, StringComparison.OrdinalIgnoreCase)
                || Settings.Any(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Value.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum Expiry { Unknown, Expired, Soon, Fine }

    public bool IsMac { get; private init; }
    public bool IsEnrolled { get; private init; }
    public string? ServerUrl { get; private init; }
    public string? CheckinUrl { get; private init; }
    public string? ManagementUrl { get; private init; }
    public string? Provider { get; private init; }
    public string? EnrollmentMethod { get; private init; }
    public string? EnrollmentType { get; private init; }
    public string? DeviceAuthStatus { get; private init; }
    public bool InstalledFromDep { get; private init; }
    public bool UserApproved { get; private init; }
    public bool DepCapable { get; private init; }
    public bool? IsSupervised { get; private init; }
    public Certificate Cert { get; private init; } = Certificate.Empty;
    public string? DeviceUuid { get; private init; }
    public bool AdeAssigned { get; private init; }
    public bool AdeActivated { get; private init; }
    public bool AdeActive => AdeAssigned || AdeActivated;
    public string? AdeOrganization { get; private init; }
    public string? AdeSupportPhone { get; private init; }
    public string? AdeSupportEmail { get; private init; }
    public string? ManagementName { get; private init; }
    public string? PrimaryUser { get; private init; }
    public string? EnrolledBy { get; private init; }
    public string? IntuneDeviceId { get; private init; }
    public string? EntraObjectId { get; private init; }
    public string? LastSync { get; private init; }
    public IReadOnlyList<Profile> Profiles { get; private init; } = [];
    public IReadOnlyList<PolicyDomain> ManagedPolicies { get; private init; } = [];
    private int _bareProfileCount;

    public int ProfileCount => Profiles.Count == 0 ? _bareProfileCount : Profiles.Count;
    public int UserScopedProfileCount => Profiles.Count(p => p.Scope == "User Level");

    public string EnrollmentUrlLabel => EnrollmentMethod switch
    {
        "ACME" => "ACME Directory",
        "SCEP" => "SCEP Server",
        _ => "Certificate Enrollment",
    };

    public bool HasDeviceDetails => ManagementName is not null || PrimaryUser is not null || EnrolledBy is not null
        || IntuneDeviceId is not null || EntraObjectId is not null || LastSync is not null;

    /// <summary>Intune usually reports the same account for both; show the pair only when they differ.</summary>
    public bool ShowPrimaryUser => PrimaryUser is not null
        && !string.Equals(StripTenantSuffix(PrimaryUser), StripTenantSuffix(EnrolledBy), StringComparison.OrdinalIgnoreCase);

    private static readonly string[] MacFields =
        ["installed_from_dep", "user_approved", "dep_capable", "has_scep_payload", "checkin_url"];

    /// <summary>The module from a device record; null when it is absent or empty.</summary>
    public static MacManagementInfo? From(IReadOnlyDictionary<string, JsonElement>? modules, string? platform) =>
        modules is not null && modules.TryGetValue("management", out var m) ? From(m, platform) : null;

    public static MacManagementInfo? From(JsonElement? module, string? platform)
    {
        if (module is not { } raw) return null;
        // The API stores some modules as [ {...} ].
        if (raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() == 1) raw = raw[0];
        if (raw.ValueKind != JsonValueKind.Object || !raw.EnumerateObject().Any()) return null;
        JsonElement? m = raw;

        var mdm = MacJson.Node(m, "mdm_enrollment");
        var cert = ParseCertificate(MacJson.Node(m, "mdm_certificate"));
        var deviceDetails = MacJson.Node(m, "device_details");
        var ade = MacJson.Node(m, "ade_configuration");
        var mdmInfo = MacJson.Node(m, "mdm_info");

        var isMac = (platform ?? "").Contains("mac", StringComparison.OrdinalIgnoreCase)
            || (platform ?? "").Contains("darwin", StringComparison.OrdinalIgnoreCase)
            || MacFields.Any(f => MacJson.Node(mdm, f) is not null);
        var enrolled = Flag(mdm, "enrolled") || Flag(mdm, "is_enrolled");
        var serverUrl = MacJson.Str(mdm, "server_url", "checkin_url");
        var fromDep = Flag(mdm, "installed_from_dep");
        var userApproved = Flag(mdm, "user_approved");

        string? method;
        if (cert.EnrollmentMethod is { } em) method = em;
        else if (cert.IdentityPayloadType?.Split('.').LastOrDefault() is { Length: > 0 } tail) method = tail.ToUpperInvariant();
        else if (Flag(mdm, "has_scep_payload")) method = "SCEP";
        else method = null;

        string? type;
        if (isMac) type = fromDep ? "Automated Device Enrollment" : userApproved ? "User Approved Enrollment" : enrolled ? "MDM Enrolled" : null;
        else type = MacJson.Str(mdm, "enrollment_type") ?? MacJson.Str(m, "device_state.status");

        var installed = MacJson.Arr(m, "installed_profiles");
        IReadOnlyList<Profile> profiles = installed.Count > 0
            ? installed.Select((p, i) => ReadProfile(p, i,
                MacJson.Str(p, "user") ?? (MacJson.Str(p, "type") == "User" ? "User Level" : "System Level"),
                MacJson.Str(p, "identifier"), MacJson.Str(p, "name"), MacJson.Str(p, "organization"))).ToList()
            : MacJson.Arr(m, "configuration_profiles").Select((p, i) =>
            {
                var identifier = MacJson.Str(p, "identifier", "uuid", "profile_name") ?? $"profile-{i}";
                var name = MacJson.Str(p, "profile_name", "name") ?? identifier;
                var scope = (MacJson.Str(p, "type") ?? "Device") == "User" ? "User Level" : "System Level";
                return ReadProfile(p, i, scope, identifier, name, MacJson.Str(p, "organization", "source", "category"));
            }).ToList();

        var policies = MacJson.Arr(m, "managed_policies").Select((pol, i) =>
        {
            var settings = MacJson.Arr(pol, "settings")
                .Select(s => new PolicySetting(MacJson.Str(s, "name") ?? "", ValueText(MacJson.Node(s, "value"))))
                .ToList();
            var count = settings.Count > 0 ? settings.Count : MacJson.Int(pol, "setting_count") ?? 0;
            return new PolicyDomain(MacJson.Str(pol, "domain") ?? $"domain-{i}", settings, count);
        }).ToList();

        return new MacManagementInfo
        {
            IsMac = isMac,
            IsEnrolled = enrolled,
            ServerUrl = serverUrl,
            CheckinUrl = MacJson.Str(mdm, "checkin_url"),
            ManagementUrl = MacJson.Str(mdm, "managementUrl"),
            // The live server URL wins; the certificate can be stale after a migration between MDMs.
            Provider = DetectProvider(serverUrl, cert) ?? MacJson.Str(mdm, "provider"),
            EnrollmentMethod = method,
            EnrollmentType = type,
            DeviceAuthStatus = MacJson.Str(deviceDetails, "device_auth_status"),
            InstalledFromDep = fromDep,
            UserApproved = userApproved,
            DepCapable = Flag(mdm, "dep_capable"),
            IsSupervised = MacJson.Bool(mdmInfo, "is_supervised"),
            Cert = cert,
            DeviceUuid = MacJson.Str(m, "device_identifiers.uuid"),
            AdeAssigned = Flag(ade, "assigned"),
            AdeActivated = Flag(ade, "activated"),
            AdeOrganization = MacJson.Str(ade, "organization"),
            AdeSupportPhone = MacJson.Str(ade, "support_phone"),
            AdeSupportEmail = MacJson.Str(ade, "support_email"),
            ManagementName = MacJson.Str(deviceDetails, "management_name"),
            PrimaryUser = MacJson.Str(deviceDetails, "primary_user"),
            EnrolledBy = MacJson.Str(mdm, "user_principal_name"),
            IntuneDeviceId = MacJson.Str(deviceDetails, "intune_device_id"),
            EntraObjectId = MacJson.Str(deviceDetails, "entra_object_id"),
            LastSync = MacJson.Str(m, "last_sync"),
            Profiles = profiles,
            ManagedPolicies = policies,
            _bareProfileCount = MacJson.Arr(m, "profiles").Count,
        };
    }

    private static bool Flag(JsonElement? parent, string path) => MacJson.Bool(parent, path) ?? false;

    private static Profile ReadProfile(JsonElement p, int index, string scope, string? identifier, string? name, string? organization)
    {
        var id = identifier ?? $"profile-{index}";
        var payloads = MacJson.Arr(p, "payloads").Select((pl, i) =>
        {
            var settings = MacJson.Node(pl, "settings") is { ValueKind: JsonValueKind.Object } obj
                ? obj.EnumerateObject().OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => ReadSetting(e.Name, e.Value)).ToList()
                : [];
            return new Payload(i, MacJson.Str(pl, "display_name"), MacJson.Str(pl, "type"), settings, MacJson.Str(pl, "raw_data"));
        }).ToList();
        return new Profile(
            id, name ?? id, MacJson.Str(p, "uuid"), organization, MacJson.Str(p, "description"), MacJson.Str(p, "install_date"),
            payloads, payloads.Count > 0 ? payloads.Count : MacJson.Int(p, "payload_count") ?? 0, scope,
            MacJson.Str(p, "verification_state") == "verified",
            Flag(p, "removal_disallowed"),
            MacJson.Str(p, "method") == "Emulated");
    }

    private static Setting ReadSetting(string key, JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => new Setting(key, SettingKind.Flag, "Yes", true),
        JsonValueKind.False => new Setting(key, SettingKind.Flag, "No", false),
        JsonValueKind.Object or JsonValueKind.Array => new Setting(key, SettingKind.Structured, Pretty(v), false),
        _ => new Setting(key, SettingKind.Text, ValueText(v), false),
    };

    /// <summary>A value as text: strings as they are, numbers and flags coerced, containers as JSON.</summary>
    private static string ValueText(JsonElement? v) => v switch
    {
        null => "",
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Number } n => n.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : n.GetDouble().ToString(CultureInfo.InvariantCulture),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Object or JsonValueKind.Array } c => Pretty(c),
        _ => "",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static string Pretty(JsonElement v) => JsonSerializer.Serialize(v, Indented);

    /// <summary>
    /// <c>parseMdmCertificate</c>: the osquery row wraps JSON in <c>output</c>, with
    /// semicolons leaking inside the quotes.
    /// </summary>
    public static Certificate ParseCertificate(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object }) return Certificate.Empty;
        var source = raw;
        if (MacJson.Node(raw, "output") is { ValueKind: JsonValueKind.String } output)
        {
            var clean = output.GetString() ?? "";
            foreach (var (pattern, rep) in CertificateCleanups) clean = Regex.Replace(clean, pattern, rep);
            try
            {
                using var doc = JsonDocument.Parse(clean.Trim());
                source = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return new Certificate(
            MacJson.Str(source, "push_topic"), MacJson.Str(source, "scep_url"),
            MacJson.Str(source, "certificate_name"), MacJson.Str(source, "certificate_subject"),
            MacJson.Str(source, "certificate_issuer"), MacJson.Str(source, "certificate_expires"),
            MacJson.Str(source, "certificate_enrollment_method"), MacJson.Str(source, "identity_payload_type"),
            MacJson.Str(source, "checkin_url"), MacJson.Str(source, "server_url"), MacJson.Str(source, "mdm_provider"));
    }

    private static readonly (string Pattern, string Replacement)[] CertificateCleanups =
    [
        (@""";\\"",\s*", "\","), (@""";\\""\n", "\"\n"), (@""";\s*,", "\","), (@""";\s*\n", "\"\n"),
        (@"\\"";\s*,", "\","), (@"\\"";\s*\n", "\"\n"), (@""";""", "\",\""),
    ];

    /// <summary><c>detectMdmProvider</c>: the server URL first, then the certificate's own provider and issuer.</summary>
    public static string? DetectProvider(string? serverUrl, Certificate cert)
    {
        if (serverUrl?.ToLowerInvariant() is { } url)
        {
            if (url.Contains("micromdm")) return "MicroMDM";
            if (url.Contains("nanomdm")) return "NanoMDM";
            if (url.Contains("jamf")) return "Jamf Pro";
            if (url.Contains("manage.microsoft.com") || url.Contains("intune")) return "Microsoft Intune";
            if (url.Contains("mosyle")) return "Mosyle";
            if (url.Contains("kandji")) return "Kandji";
            if (url.Contains("addigy")) return "Addigy";
            if (url.Contains("simplemdm")) return "SimpleMDM";
            if (url.Contains("airwatch.com") || url.Contains("workspaceone")) return "Workspace ONE";
            if (url.Contains("meraki")) return "Cisco Meraki";
            if (url.Contains("maas360")) return "MaaS360";
            if (url.Contains("mobileiron") || url.Contains("ivanti")) return "Ivanti";
        }
        if (cert.Provider is { } p) return p;
        if (cert.Issuer?.ToLowerInvariant() is { } issuer)
        {
            if (issuer.Contains("micromdm")) return "MicroMDM";
            if (issuer.Contains("nanomdm")) return "NanoMDM";
            if (issuer.Contains("jamf")) return "Jamf Pro";
            if (issuer.Contains("microsoft") || issuer.Contains("intune")) return "Microsoft Intune";
            if (issuer.Contains("mosyle")) return "Mosyle";
            if (issuer.Contains("kandji")) return "Kandji";
            if (issuer.Contains("addigy")) return "Addigy";
            if (issuer.Contains("simplemdm")) return "SimpleMDM";
            if (issuer.Contains("workspace one") || issuer.Contains("vmware")) return "Workspace ONE";
            if (issuer.Contains("meraki")) return "Cisco Meraki";
        }
        return null;
    }

    /// <summary><c>user@domain@tenant-guid</c> keeps only the principal name.</summary>
    public static string? StripTenantSuffix(string? upn) =>
        upn is null ? null : Regex.Replace(upn, @"@[0-9a-fA-F-]{36}$", "");

    /// <summary>
    /// A certificate date: openssl's "Sep 16 23:10:40 2027 GMT", the client's
    /// "2026-10-18 03:51:47 +0000", or anything ISO-like. UTC.
    /// </summary>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        string[] formats = ["MMM d HH:mm:ss yyyy 'GMT'", "MMM d HH:mm:ss yyyy", "yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss 'UTC'"];
        var withColon = Regex.Replace(t, @"([+-]\d{2})(\d{2})$", "$1:$2");
        if (DateTime.TryParseExact(withColon, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
            return exact;
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var any))
            return any;
        return null;
    }

    public static Expiry ExpiryOf(DateTime? date, DateTime? now = null)
    {
        if (date is not { } d) return Expiry.Unknown;
        var days = (int)Math.Ceiling((d - (now ?? DateTime.UtcNow)).TotalDays);
        return days < 0 ? Expiry.Expired : days < 30 ? Expiry.Soon : Expiry.Fine;
    }
}
