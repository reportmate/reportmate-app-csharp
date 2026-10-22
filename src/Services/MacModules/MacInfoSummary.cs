// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ReportMate.App.Services.MacModules.MacJson;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A fleet Mac's Info tab: the six overview widgets (Inventory, System, Hardware,
/// Management, Security, Network), read from the Mac client's module shapes the way
/// the Mac app's InfoTab reads them through ReportMateKit's SystemInfo, HardwareInfo,
/// ManagementInfo, SecurityOverview and NetworkInfo. Only the fields the widgets show
/// are read; the per-module tabs carry the rest.
/// </summary>
public sealed class MacInfoSummary
{
    /// <summary>Badge colouring, as the Mac app's SecurityOverview.Tone.</summary>
    public enum Tone { Success, Warning, Error, Info }

    public required MacInventorySummary Inventory { get; init; }
    public required MacSystemSummary System { get; init; }
    public required MacHardwareSummary Hardware { get; init; }
    public required MacManagementSummary Management { get; init; }
    public required MacSecuritySummary Security { get; init; }
    public required MacNetworkSummary Network { get; init; }

    /// <summary>The modules the Info tab draws from.</summary>
    public static readonly string[] Modules = ["inventory", "system", "hardware", "management", "security", "network"];

    /// <summary>
    /// Reads the overview from the raw modules. <paramref name="fallbackName"/> and
    /// <paramref name="fallbackSerial"/> are what the API calls the device, used when
    /// the inventory module does not say.
    /// </summary>
    public static MacInfoSummary Read(IReadOnlyDictionary<string, JsonElement> modules, string? fallbackName = null, string? fallbackSerial = null)
    {
        JsonElement? Module(string id) => modules.TryGetValue(id, out var v) ? Unwrap(v) : null;
        var inventory = Module("inventory");
        var system = Module("system");
        var hardware = Module("hardware");
        var management = Module("management");
        var security = Module("security");
        var network = Module("network");
        var identity = Module("identity");

        return new MacInfoSummary
        {
            Inventory = MacInventorySummary.Read(inventory, hardware, system, fallbackName, fallbackSerial),
            System = MacSystemSummary.Read(system),
            Hardware = MacHardwareSummary.Read(hardware),
            Management = MacManagementSummary.Read(management, inventory, hardware, system),
            Security = MacSecuritySummary.Read(security, identity, management),
            Network = MacNetworkSummary.Read(network, system),
        };
    }

    /// <summary>True when any module the tab draws from is present.</summary>
    public static bool HasAny(IReadOnlyDictionary<string, JsonElement> modules) =>
        Modules.Any(m => modules.TryGetValue(m, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array);

    /// <summary>The API stores some modules as a one-element array.</summary>
    internal static JsonElement? Unwrap(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 1) return a[0];
        if (value is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        return value;
    }

    internal static bool IsEmpty(JsonElement? value) => value switch
    {
        null => true,
        { ValueKind: JsonValueKind.Object } o => !o.EnumerateObject().Any(),
        { ValueKind: JsonValueKind.Array } a => a.GetArrayLength() == 0,
        _ => false,
    };

    /// <summary>A JSON value given either as an object or as a string holding one (osquery's "output").</summary>
    internal static JsonElement? Parsed(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } s)
        {
            try { return JsonDocument.Parse(s.GetString() ?? "").RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        return value;
    }
}

// ── Inventory ───────────────────────────────────────────────────────

public sealed class MacInventorySummary
{
    public string DeviceName { get; init; } = "Unknown Device";
    public string? AssetTag { get; init; }
    public string? SerialNumber { get; init; }

    /// <summary>The assignment fields the default inventory mapping shows, in its order.</summary>
    public List<(string Label, string Value)> Assignments { get; init; } = [];

    // The default mapping (SettingsDocument.defaultInventoryFields): Area and Fleet
    // are hidden, and the Asset Tag sits with the identity column instead.
    private static readonly (string Key, string Label)[] VisibleFields =
        [("usage", "Usage"), ("catalog", "Catalog"), ("department", "Department"), ("location", "Location"), ("owner", "Owner")];

    internal static MacInventorySummary Read(JsonElement? inventory, JsonElement? hardware, JsonElement? system, string? fallbackName, string? fallbackSerial)
    {
        var hwSystem = Node(hardware, "system");
        return new MacInventorySummary
        {
            DeviceName = Str(inventory, "deviceName")
                ?? Str(hwSystem, "computerName", "hostname")
                ?? Str(Node(system, "systemDetails"), "computerName")
                ?? (string.IsNullOrWhiteSpace(fallbackName) ? null : fallbackName)
                ?? "Unknown Device",
            AssetTag = Str(inventory, "assetTag"),
            SerialNumber = Str(inventory, "serialNumber") ?? Str(hwSystem, "hardwareSerial") ?? fallbackSerial,
            Assignments = VisibleFields
                .Select(f => (f.Label, Value: Str(inventory, f.Key)))
                .Where(f => f.Value is not null)
                .Select(f => (f.Label, f.Value!))
                .ToList(),
        };
    }
}

// ── System ──────────────────────────────────────────────────────────

public sealed class MacSystemSummary
{
    public enum UpdateState { UpToDate, Pending, DeferredOnly }

    public bool HasData { get; init; }
    /// <summary>"Tahoe", "Sequoia", ... ; "macOS" when the version is not one it knows.</summary>
    public string MarketingName { get; init; } = "Unknown";
    /// <summary>major.minor.patch.</summary>
    public string Version { get; init; } = "Unknown";
    public string? Build { get; init; }
    public string? KeyboardLayout { get; init; }
    public string? TimeZone { get; init; }
    public string? Uptime { get; init; }
    public string? Locale { get; init; }

    public UpdateState Updates { get; init; }
    /// <summary>Pending updates not deferred (Pending), or the deferred count (DeferredOnly).</summary>
    public int UpdateCount { get; init; }
    public int DeferredCount { get; init; }
    /// <summary>When the soonest deferred update becomes available.</summary>
    public DateTime? SoonestDeferral { get; init; }

    internal static MacSystemSummary Read(JsonElement? system)
    {
        var os = Node(system, "operatingSystem");
        var details = Node(system, "systemDetails");
        var name = Str(os, "name", "productName");
        var version = Str(os, "version");
        if (name is null && version is null) return new MacSystemSummary();

        var updates = Arr(system, "pendingAppleUpdates");
        bool IsDeferred(JsonElement u) => Bool(u, "deferred") == true || Str(u, "deferredUntil") is not null;
        var active = updates.Count(u => !IsDeferred(u));
        var deferred = updates.Where(IsDeferred).ToList();
        var soonest = deferred.Select(u => Date(u, "deferredUntil")).Where(d => d is not null).Min();

        return new MacSystemSummary
        {
            HasData = true,
            MarketingName = MarketingNameFor(version),
            Version = FormattedVersion(version),
            Build = Str(os, "build", "buildNumber"),
            KeyboardLayout = Str(os, "activeKeyboardLayout") ?? Layouts(os) ?? Layouts(details),
            TimeZone = Str(os, "timeZone", "timezone") ?? Str(details, "timeZone"),
            Locale = Str(os, "locale") ?? Str(details, "locale"),
            Uptime = Str(system, "uptimeString") ?? Str(details, "uptimeString")
                ?? UptimeFromSeconds(Num(system, "uptime")) ?? Str(system, "uptime"),
            Updates = active > 0 ? UpdateState.Pending : deferred.Count > 0 ? UpdateState.DeferredOnly : UpdateState.UpToDate,
            UpdateCount = active > 0 ? active : deferred.Count,
            DeferredCount = deferred.Count,
            SoonestDeferral = soonest,
        };
    }

    private static string? Layouts(JsonElement? parent)
    {
        var list = Arr(parent, "keyboardLayouts").Select(Text).Where(s => s is not null).ToList();
        return list.Count > 0 ? string.Join(", ", list) : null;
    }

    private static string? Text(JsonElement e) => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()) ? e.GetString()!.Trim() : null;

    /// <summary>The Mac client sends uptime in seconds beside its own string; this covers a report with only the number.</summary>
    private static string? UptimeFromSeconds(double? seconds)
    {
        if (seconds is not > 0) return null;
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    /// <summary>OSNames.macOSMarketingName.</summary>
    public static string MarketingNameFor(string? version)
    {
        var parts = (version ?? "").Split('.');
        if (!int.TryParse(parts[0], out var major)) return "macOS";
        if (major == 10)
        {
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
            return minor switch { 15 => "Catalina", 14 => "Mojave", 13 => "High Sierra", 12 => "Sierra", _ => "macOS" };
        }
        return major switch
        {
            27 => "macOS 27", 26 => "Tahoe", 15 => "Sequoia", 14 => "Sonoma", 13 => "Ventura",
            12 => "Monterey", 11 => "Big Sur", _ => "macOS",
        };
    }

    /// <summary>OSNames.formattedVersion for a Mac: always major.minor.patch.</summary>
    public static string FormattedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "Unknown";
        var p = version.Split('.');
        return $"{(p.Length > 0 && p[0].Length > 0 ? p[0] : "0")}.{(p.Length > 1 ? p[1] : "0")}.{(p.Length > 2 ? p[2] : "0")}";
    }
}

// ── Hardware ────────────────────────────────────────────────────────

public sealed class MacHardwareSummary
{
    public bool HasData { get; init; }
    public string? Model { get; init; }
    public string? ModelIdentifier { get; init; }
    public string? Chip { get; init; }
    public int CpuCores { get; init; }
    public int PerformanceCores { get; init; }
    public int EfficiencyCores { get; init; }
    public int GpuCores { get; init; }
    public string GraphicsName { get; init; } = "Unknown";
    public double MemoryBytes { get; init; }
    public int NpuCores { get; init; }
    public string? NpuTops { get; init; }
    public double StorageTotalBytes { get; init; }
    public double StorageFreeBytes { get; init; }
    public double? BatteryPercent { get; init; }
    public int BatteryCycles { get; init; }
    public string? BatteryHealth { get; init; }

    /// <summary>"14 cores (10P + 4E)" when the split adds up, else "14 cores".</summary>
    public string CpuLabel => CpuCores <= 0 ? "Unknown"
        : PerformanceCores > 0 && EfficiencyCores > 0 && PerformanceCores + EfficiencyCores == CpuCores
            ? $"{CpuCores} cores ({PerformanceCores}P + {EfficiencyCores}E)"
            : $"{CpuCores} cores";

    public string GpuLabel => GpuCores > 0 ? $"{GpuCores} cores" : GraphicsName;
    public string MemoryLabel => MemoryBytes > 0 ? Format.Bytes(MemoryBytes) : "Unknown";
    public bool HasNpu => NpuCores > 0 || NpuTops is not null;
    public string? NpuLabel => NpuTops is not null ? $"{NpuTops} TOPS" : NpuCores > 0 ? $"{NpuCores} cores" : null;
    public string StorageLabel => StorageTotalBytes > 0 ? $"{Format.Bytes(StorageTotalBytes)} • {Format.Bytes(StorageFreeBytes)} free" : "Unknown";

    /// <summary>"82% · 140 cycles · Normal"; null on a desktop or when nothing is reported.</summary>
    public string? BatteryLabel
    {
        get
        {
            if (BatteryPercent is not { } pct) return null;
            var s = $"{(int)pct}%";
            if (BatteryCycles > 0) s += $" · {BatteryCycles} cycles";
            if (BatteryHealth is not null) s += $" · {BatteryHealth}";
            return s;
        }
    }

    internal static MacHardwareSummary Read(JsonElement? hw)
    {
        if (MacInfoSummary.IsEmpty(hw)) return new MacHardwareSummary();
        var hwSystem = Node(hw, "system");

        var processor = Node(hw, "processor");
        string? processorName, chip;
        if (processor is { ValueKind: JsonValueKind.String } ps)
            processorName = chip = string.IsNullOrWhiteSpace(ps.GetString()) ? null : ps.GetString()!.Trim();
        else
        {
            processorName = Str(processor, "name", "value", "brand");
            chip = Str(processor, "chip", "name");
        }
        var cores = new[] { Int(processor, "cores"), Int(processor, "cpuCores"), Int(processor, "logicalCores"), Int(processor, "logicalProcessors") }
            .FirstOrDefault(c => c > 0) ?? 0;

        var graphics = MacInfoSummary.Unwrap(Node(hw, "graphics"));
        var npu = Node(hw, "npu");
        var memoryBytes = Num(Node(hw, "memory"), "physicalMemory", "totalPhysical", "total", "totalBytes") ?? 0;
        var model = Str(hw, "model") ?? Str(hwSystem, "modelName");

        // Drives that count toward the totals: internal, with capacity and free space known.
        double total = 0, free = 0;
        foreach (var drive in Arr(hw, "storage"))
        {
            var capacity = Num(drive, "size", "capacity", "totalSize") ?? 0;
            var available = Num(drive, "freeSpace", "free", "availableSpace") ?? 0;
            var removable = (Str(drive, "type") ?? "").Contains("removable", StringComparison.OrdinalIgnoreCase)
                || (Str(drive, "interface") ?? "").Contains("usb", StringComparison.OrdinalIgnoreCase);
            var isInternal = Node(drive, "isInternal") is null ? !removable : Bool(drive, "isInternal") == true;
            if (capacity <= 0 || available <= 0 || !isInternal) continue;
            total += capacity;
            free += available;
        }

        // A battery only on a portable that reports one.
        double? batteryPercent = null;
        int cycles = 0;
        string? health = null;
        var battery = Node(hw, "battery");
        if (battery is { ValueKind: JsonValueKind.Object } && !string.Equals(Str(hw, "formFactor"), "desktop", StringComparison.OrdinalIgnoreCase))
        {
            var pct = Num(battery, "percentRemaining", "chargePercent", "percent") ?? 0;
            cycles = Int(battery, "cycleCount") ?? 0;
            if (cycles > 0 || pct > 0)
            {
                batteryPercent = pct;
                health = Str(battery, "health");
            }
            else cycles = 0;
        }

        return new MacHardwareSummary
        {
            HasData = model is not null || processorName is not null || chip is not null || memoryBytes > 0,
            Model = model,
            ModelIdentifier = Str(hw, "modelIdentifier") ?? Str(hwSystem, "hardwareModel"),
            Chip = chip,
            CpuCores = cores,
            PerformanceCores = Int(processor, "performanceCores") ?? 0,
            EfficiencyCores = Int(processor, "efficiencyCores") ?? 0,
            GpuCores = Int(graphics, "cores") ?? Int(graphics, "gpuCores") ?? 0,
            GraphicsName = CleanGraphicsName(Str(graphics, "name"), Str(graphics, "manufacturer")),
            MemoryBytes = memoryBytes,
            NpuCores = Int(processor, "npuCores") ?? Int(npu, "cores") ?? 0,
            NpuTops = Str(npu, "performanceTops", "tops"),
            StorageTotalBytes = total,
            StorageFreeBytes = free,
            BatteryPercent = batteryPercent,
            BatteryCycles = cycles,
            BatteryHealth = health,
        };
    }

    /// <summary>HardwareInfo.cleanGraphicsName: the GPU name without its vendor prefix.</summary>
    public static string CleanGraphicsName(string? name, string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        var cleaned = name.Trim();
        if (!string.IsNullOrWhiteSpace(manufacturer) && cleaned.StartsWith(manufacturer.Trim(), StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[manufacturer.Trim().Length..].Trim();
        foreach (var prefix in new[] { "NVIDIA ", "AMD ", "INTEL " })
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[prefix.Length..].Trim();
        return cleaned.Length > 0 ? cleaned : name.Trim();
    }
}

// ── Management ──────────────────────────────────────────────────────

public sealed class MacManagementSummary
{
    public bool HasData { get; init; }
    public bool IsEnrolled { get; init; }
    public string? Provider { get; init; }
    public string? EnrollmentType { get; init; }
    public bool? IsSupervised { get; init; }
    public string? ServerHost { get; init; }
    /// <summary>The MDM certificate's expiry, parsed when the format allows.</summary>
    public DateTime? CertificateExpires { get; init; }
    /// <summary>The expiry as the client sent it, for when it does not parse.</summary>
    public string? CertificateExpiresText { get; init; }
    public string? Organization { get; init; }
    /// <summary>The hardware UUID, shown when the provider is not Intune (which has its own id).</summary>
    public string? HardwareUuid { get; init; }

    public bool ProviderIsIntune => (Provider ?? "").Contains("intune", StringComparison.OrdinalIgnoreCase);

    /// <summary>The enrollment type's badge colour, as the Mac app's widget picks it.</summary>
    public MacInfoSummary.Tone EnrollmentTone =>
        (EnrollmentType ?? "").Contains("Automated") || (EnrollmentType ?? "").Contains("Entra Joined") ? MacInfoSummary.Tone.Success
        : (EnrollmentType ?? "").Contains("User Approved") ? MacInfoSummary.Tone.Warning
        : MacInfoSummary.Tone.Info;

    internal static MacManagementSummary Read(JsonElement? management, JsonElement? inventory, JsonElement? hardware, JsonElement? system)
    {
        if (MacInfoSummary.IsEmpty(management)) return new MacManagementSummary();
        var mdm = Node(management, "mdmEnrollment");
        var cert = Node(management, "mdmCertificate");
        if (Node(cert, "output") is { ValueKind: JsonValueKind.String } output) cert = MacInfoSummary.Parsed(output);

        var enrolled = Bool(mdm, "enrolled") == true || Bool(mdm, "isEnrolled") == true;
        var serverUrl = Str(mdm, "serverUrl");
        var installedFromDep = Bool(mdm, "installedFromDep") == true;
        var userApproved = Bool(mdm, "userApproved") == true;
        var expiresText = Str(cert, "certificateExpires");

        return new MacManagementSummary
        {
            HasData = true,
            IsEnrolled = enrolled,
            Provider = Str(mdm, "provider") ?? DetectProvider(serverUrl, Str(cert, "mdmProvider"), Str(cert, "certificateIssuer")),
            EnrollmentType = installedFromDep ? "Automated Device Enrollment"
                : userApproved ? "User Approved Enrollment"
                : enrolled ? "MDM Enrolled" : null,
            IsSupervised = Bool(Node(management, "mdmInfo"), "isSupervised"),
            ServerHost = serverUrl is null ? null : HostOf(serverUrl),
            CertificateExpires = ParseCertificateDate(expiresText),
            CertificateExpiresText = expiresText,
            Organization = Str(Node(management, "adeConfiguration"), "organization") ?? Str(Node(management, "tenantDetails"), "tenantName"),
            HardwareUuid = Str(inventory, "uuid") ?? Str(Node(hardware, "system"), "uuid") ?? Str(Node(system, "systemDetails"), "systemUUID"),
        };
    }

    public static string HostOf(string url)
    {
        var s = url.Trim();
        foreach (var p in new[] { "https://", "http://" })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) s = s[p.Length..];
        var slash = s.IndexOf('/');
        return slash >= 0 ? s[..slash] : s;
    }

    /// <summary>ManagementInfo.detectProvider: the certificate first, then the server URL.</summary>
    public static string? DetectProvider(string? serverUrl, string? certificateProvider = null, string? certificateIssuer = null)
    {
        if (!string.IsNullOrWhiteSpace(certificateProvider)) return certificateProvider;
        var issuer = (certificateIssuer ?? "").ToLowerInvariant();
        if (issuer.Contains("micromdm")) return "MicroMDM";
        if (issuer.Contains("nanomdm")) return "NanoMDM";
        if (issuer.Contains("jamf")) return "Jamf Pro";
        if (issuer.Contains("mosyle")) return "Mosyle";
        if (issuer.Contains("kandji")) return "Kandji";
        if (issuer.Contains("microsoft")) return "Microsoft Intune";
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        var url = serverUrl.ToLowerInvariant();
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
        if (url.Contains("hexnode")) return "Hexnode";
        if (url.Contains("filewave")) return "FileWave";
        return null;
    }

    /// <summary>openssl's "Sep 16 23:10:40 2027 GMT" (day space-padded), or any ISO date.</summary>
    public static DateTime? ParseCertificateDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        if (DateTime.TryParseExact(collapsed, ["MMM d HH:mm:ss yyyy 'GMT'", "MMM d HH:mm:ss yyyy"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var openssl))
            return openssl;
        return DateTime.TryParse(collapsed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var any)
            ? any : null;
    }
}

// ── Security ────────────────────────────────────────────────────────

public sealed record MacSecurityRow(string Label, string Value, MacInfoSummary.Tone Tone, bool Indented = false);

public sealed class MacSecuritySummary
{
    public bool HasData { get; init; }
    public List<MacSecurityRow> Rows { get; init; } = [];

    internal static MacSecuritySummary Read(JsonElement? security, JsonElement? identity, JsonElement? management)
    {
        if (MacInfoSummary.IsEmpty(security)) return new MacSecuritySummary();
        const MacInfoSummary.Tone ok = MacInfoSummary.Tone.Success, bad = MacInfoSummary.Tone.Error,
            warn = MacInfoSummary.Tone.Warning, info = MacInfoSummary.Tone.Info;
        static MacInfoSummary.Tone StatusTone(bool? on) => on is null ? MacInfoSummary.Tone.Warning : on.Value ? MacInfoSummary.Tone.Success : MacInfoSummary.Tone.Error;
        static string OnOff(bool on) => on ? "Enabled" : "Disabled";
        var rows = new List<MacSecurityRow>();

        // FileVault, with the bootstrap token and personal recovery key beneath it.
        // Default security settings: encryption on is good, off is bad, unknown a warning.
        var fv = Node(security, "fileVault");
        bool? fvEnabled = fv is null ? null : Bool(fv, "enabled") == true || string.Equals(Str(fv, "status"), "on", StringComparison.OrdinalIgnoreCase);
        rows.Add(new("FileVault", fv is null ? "Unknown" : Str(fv, "status") ?? OnOff(fvEnabled == true), StatusTone(fvEnabled)));
        if (Bool(Node(security, "bootstrapToken"), "escrowed") is { } bt)
            rows.Add(new("Bootstrap Token", bt ? "Escrowed" : "Not Escrowed", bt ? ok : bad, true));
        if (Bool(fv, "recoveryKeyEscrowed") is { } prk)
            rows.Add(new("Personal Recovery Key", prk ? "Escrowed" : "Not Escrowed", prk ? ok : bad, true));

        var psso = Node(identity, "platformSSOUsers");
        if (psso is not null)
        {
            var registered = Bool(psso, "deviceRegistered") == true;
            rows.Add(new("Platform SSO", registered ? "Registered" : "Not Registered", StatusTone(registered)));
            var users = Arr(psso, "users");
            var user = users.Cast<JsonElement?>().FirstOrDefault(u => Bool(u, "tokensPresent") == true)
                ?? users.Cast<JsonElement?>().FirstOrDefault(u => Bool(u, "registered") == true);
            var tokens = Bool(user, "tokensPresent") == true;
            rows.Add(new("Token", tokens ? "Present" : "Missing", StatusTone(tokens), true));
            rows.Add(new("Method", Str(psso, "method") ?? "Unknown", info, true));
        }

        // "enabled" first, then the status text the client also sends ("Enabled"/"Disabled").
        if (Node(security, "gatekeeper") is { } gk)
        {
            var on = Bool(gk, "enabled", "status") == true;
            rows.Add(new("Gatekeeper", OnOff(on), on ? ok : bad));
        }
        if (Node(security, "systemIntegrityProtection") is { } sip)
        {
            var on = Bool(sip, "enabled", "status") == true;
            rows.Add(new("System Integrity Protection", OnOff(on), on ? ok : bad));
        }
        if (Node(security, "activationLock") is { } al)
        {
            var on = Bool(al, "enabled", "status") == true;
            rows.Add(new("Activation Lock", OnOff(on), on ? bad : ok));
        }

        var firewall = Node(security, "firewall");
        var firewallStatus = Str(firewall, "statusDisplay")
            ?? (firewall is null ? "Unknown" : OnOff(Bool(firewall, "isEnabled") == true || Bool(firewall, "enabled") == true));
        rows.Add(new("Firewall", firewallStatus, info));

        var remote = Node(management, "remoteManagement") ?? Node(security, "remoteManagement");
        if (Bool(remote, "screenSharing", "screenSharingEnabled") is { } ss)
            rows.Add(new("Screen Sharing", OnOff(ss), ss ? warn : ok));

        return new MacSecuritySummary { HasData = true, Rows = rows };
    }
}

// ── Network ─────────────────────────────────────────────────────────

public sealed class MacNetworkInterface
{
    public string Name { get; init; } = "Unknown";
    public string? Type { get; init; }
    public string? IpAddress { get; init; }
    public List<string> IpAddresses { get; init; } = [];
    public string? MacAddress { get; init; }
    public bool IsActive { get; init; }
    public string? WirelessProtocol { get; set; }
    public string? Ssid { get; set; }

    public bool IsWireless
    {
        get
        {
            var t = (Type ?? "").ToLowerInvariant();
            return t.Contains("wifi") || t.Contains("wireless") || (Name == "en0" && t.Length == 0);
        }
    }

    public bool IsEthernet
    {
        get
        {
            var t = (Type ?? "").ToLowerInvariant();
            return t.Contains("ethernet") || Name == "en1";
        }
    }
}

public sealed class MacNetworkSummary
{
    public string? Hostname { get; init; }
    public string? IpAddress { get; init; }
    public string? MacAddress { get; init; }
    public string? ConnectionType { get; init; }
    public List<MacNetworkInterface> Interfaces { get; init; } = [];
    public MacNetworkInterface? Ethernet => Interfaces.FirstOrDefault(i => i.IsActive && i.IsEthernet);
    public MacNetworkInterface? Wireless => Interfaces.FirstOrDefault(i => i.IsActive && i.IsWireless);

    /// <summary>The Wi-Fi connection's name, protocol and addresses (NetworkInfo.wifiInterface).</summary>
    public string? WifiSsid { get; init; }
    public string? WifiProtocol { get; init; }
    public string? WifiIpAddress { get; init; }
    public string? WifiMacAddress { get; init; }

    public bool VpnActive { get; init; }
    public string? VpnName { get; init; }

    public string? Download { get; init; }
    public string? Upload { get; init; }
    public string? Responsiveness { get; init; }
    public string? Latency { get; init; }
    public bool HasQuality => Download is not null || Upload is not null;

    /// <summary>The address the widget leads with; no address means no network card body.</summary>
    public string? PrimaryIp => Ethernet?.IpAddress ?? Wireless?.IpAddress ?? IpAddress;

    private const string LocationServicesRequired = "[Location Services Required]";

    internal static MacNetworkSummary Read(JsonElement? network, JsonElement? system)
    {
        if (MacInfoSummary.IsEmpty(network)) return new MacNetworkSummary();
        string? ip = null, mac = null, type = null, ifaceName = null, vpnName = null, hostname = null;
        var vpnActive = false;
        var rawInterfaces = Arr(network, "interfaces");

        // The active connection; a VPN tunnel is swapped for the physical interface under it.
        var active = Node(network, "activeConnection");
        if (active is not null)
        {
            ifaceName = Str(active, "interfaceName", "interface", "friendlyName");
            ip = Str(active, "ipAddress");
            type = Str(active, "connectionType");
            var tunnel = (ifaceName ?? "").StartsWith("utun") || (ifaceName ?? "").StartsWith("ppp");
            if (tunnel)
            {
                var physical = rawInterfaces.Cast<JsonElement?>().FirstOrDefault(i =>
                {
                    var hasIp = Arr(i, "addresses").Any(a => Str(a, "family") == "IPv4" && Str(a, "address") is { } x && !x.StartsWith("127."))
                        || (Str(i, "address") is { } addr && !addr.StartsWith("127."));
                    return hasIp && Str(i, "type") is "Ethernet" or "WiFi" or "Wireless";
                });
                if (physical is not null)
                {
                    ifaceName = Str(physical, "displayName", "name", "interface");
                    ip = Arr(physical, "addresses").Where(a => Str(a, "family") == "IPv4").Select(a => Str(a, "address")).FirstOrDefault(a => a is not null)
                        ?? Str(physical, "address") ?? ip;
                    type = Str(physical, "type") ?? "Ethernet";
                }
            }
            mac = Str(active, "macAddress");
            vpnName = Str(active, "vpnName");
            vpnActive = Bool(active, "isVpnActive") == true || tunnel;
            if (mac is null)
            {
                var match = rawInterfaces.Cast<JsonElement?>().FirstOrDefault(i => (Str(i, "name") ?? Str(i, "interface")) == ifaceName);
                if (Str(match, "macAddress", "mac") is null)
                    match = rawInterfaces.Cast<JsonElement?>().FirstOrDefault(i => Str(i, "macAddress", "mac") is not null && Str(i, "type") is "Ethernet" or "WiFi" or "Wireless");
                mac = Str(match, "macAddress", "mac");
            }
        }

        // A connected VPN, by name.
        var connectedVpn = Arr(network, "vpnConnections")
            .Where(v => Str(v, "name") is { } n && n != "Unknown VPN")
            .Cast<JsonElement?>()
            .FirstOrDefault(v => string.Equals(Str(v, "status"), "connected", StringComparison.OrdinalIgnoreCase));
        if (connectedVpn is not null)
        {
            vpnActive = true;
            vpnName = Str(connectedVpn, "name");
        }

        hostname = Str(Node(network, "dns", "dnsConfiguration"), "domainName");

        var interfaces = ReadInterfaces(rawInterfaces);

        // The Wi-Fi interface gains the current network's name and protocol.
        var current = Node(network, "currentWiFiNetwork");
        var details = Node(current, "output") is { ValueKind: JsonValueKind.String } o ? MacInfoSummary.Parsed(o) : current;
        if (details is { ValueKind: JsonValueKind.Object })
        {
            var detailsIface = Str(details, "interface");
            var target = interfaces.FirstOrDefault(i => (detailsIface is not null && i.Name == detailsIface) || i.IsWireless);
            if (target is not null)
            {
                target.WirelessProtocol ??= ProtocolFromMode(Str(details, "mode")) ?? Str(details, "wifiVersion");
                var ssid = Str(details, "ssid");
                if (ssid is not null && ssid != LocationServicesRequired) target.Ssid = ssid;
                else if (ssid == LocationServicesRequired)
                    target.Ssid = Arr(Node(network, "wifiInfo"), "knownNetworks").Select(k => Str(k, "ssid")).FirstOrDefault();
            }
        }

        if (active is null && interfaces.FirstOrDefault(i => i.IsActive) is { } firstActive)
        {
            ip ??= firstActive.IpAddress;
            mac ??= firstActive.MacAddress;
            type ??= firstActive.Type;
            if (firstActive.WirelessProtocol is not null) type = firstActive.WirelessProtocol;
        }

        // The Wi-Fi connection: osquery's current network, wifiInfo's, or the wireless interface.
        string? wifiSsid = null, wifiProtocol = null;
        var hasWifi = false;
        var wifiInfo = Node(network, "wifiInfo");
        if (wifiInfo is not null)
        {
            var currentNetwork = Node(wifiInfo, "currentNetwork");
            var parsedCurrent = MacInfoSummary.Parsed(Node(current, "output"));
            if (parsedCurrent is { ValueKind: JsonValueKind.Object })
            {
                var s = Str(parsedCurrent, "ssid");
                wifiSsid = s == LocationServicesRequired ? null : s;
                wifiProtocol = ProtocolFromMode(Str(parsedCurrent, "mode"));
                hasWifi = true;
            }
            else if (currentNetwork is not null)
            {
                wifiSsid = Str(currentNetwork, "ssid", "networkName");
                hasWifi = true;
            }
            if (type == "WiFi" && (Str(wifiInfo, "wifiProtocol") ?? ProtocolFromMode(Str(wifiInfo, "phyMode"))) is { } proto) type = proto;
        }
        var wireless = interfaces.FirstOrDefault(i => i.IsActive && i.IsWireless);
        if (wireless is not null)
        {
            hasWifi = true;
            wifiSsid ??= wireless.Ssid;
            wifiProtocol ??= wireless.WirelessProtocol;
        }

        // Hostname: the module's own, else the system environment's.
        hostname = Str(network, "hostname") ?? hostname
            ?? Arr(system, "environment").Where(e => Str(e, "name") is "COMPUTERNAME" or "HOSTNAME").Select(e => Str(e, "value")).FirstOrDefault(v => v is not null);

        // Network quality: macOS networkQuality's text output, or the client's parsed fields.
        string? download = null, upload = null, responsiveness = null, latency = null;
        var nq = Node(network, "networkQuality");
        if (Str(nq, "output") is { } text && Node(nq, "output") is { ValueKind: JsonValueKind.String })
        {
            string? Match(string pattern) => Regex.Match(text, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            download = Mbps(Match(@"Downlink capacity:\s*([\d.]+)\s*Mbps"));
            upload = Mbps(Match(@"Uplink capacity:\s*([\d.]+)\s*Mbps"));
            responsiveness = Match(@"Downlink Responsiveness:\s*(\w+)") ?? Match(@"Uplink Responsiveness:\s*(\w+)");
            latency = Match(@"Idle Latency:\s*([\d.]+)\s*milliseconds") is { } l && double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var lv)
                ? $"{Math.Round(lv, MidpointRounding.AwayFromZero)} ms" : null;
        }
        else if (nq is not null)
        {
            download = Num(nq, "dlThroughput") is { } dl ? $"{Math.Round(dl, MidpointRounding.AwayFromZero)} Mbps" : null;
            upload = Num(nq, "ulThroughput") is { } ul ? $"{Math.Round(ul, MidpointRounding.AwayFromZero)} Mbps" : null;
            responsiveness = Str(nq, "dlRating") ?? Str(nq, "ulRating");
            latency = Num(nq, "idleLatency") is { } il ? $"{Math.Round(il, MidpointRounding.AwayFromZero)} ms" : null;
        }

        // No address from the active connection: the first active interface with an IPv4 one.
        if (ip is null && interfaces.FirstOrDefault(i => i.IsActive && IsIPv4(i.IpAddress)) is { } withIp)
        {
            ip = withIp.IpAddress;
            mac = withIp.MacAddress;
            if (withIp.Name == "en0") type = "WiFi";
            else if (Regex.IsMatch(withIp.Name, @"^en\d+$")) type = "Ethernet";
        }

        return new MacNetworkSummary
        {
            Hostname = hostname,
            IpAddress = ip,
            MacAddress = mac,
            ConnectionType = type,
            Interfaces = interfaces,
            WifiSsid = hasWifi ? wifiSsid : null,
            WifiProtocol = hasWifi ? wifiProtocol : null,
            WifiIpAddress = wireless?.IpAddress,
            WifiMacAddress = wireless?.MacAddress,
            VpnActive = vpnActive,
            VpnName = vpnName,
            Download = download,
            Upload = upload,
            Responsiveness = responsiveness,
            Latency = latency,
        };
    }

    /// <summary>
    /// The interfaces, one per name: the Mac client sends an entry per address, so the
    /// addresses merge and the interface is up if any entry is. Virtual adapters drop
    /// out, and the active ones sort first.
    /// </summary>
    private static List<MacNetworkInterface> ReadInterfaces(IReadOnlyList<JsonElement> raw)
    {
        var order = new List<string>();
        var merged = new Dictionary<string, (JsonElement First, List<JsonElement> Addresses, bool Up, bool HasIPv4)>();
        foreach (var iface in raw)
        {
            var name = Str(iface, "name", "interface") ?? "Unknown";
            if (!merged.TryGetValue(name, out var entry))
            {
                order.Add(name);
                entry = (iface, [], false, false);
            }
            var addresses = Arr(iface, "addresses");
            entry.Addresses.AddRange(addresses);
            if (Bool(iface, "isUp") == true) entry.Up = true;
            if (addresses.Any(a => Str(a, "family") == "IPv4" && IsIPv4(Str(a, "address")) && !Str(a, "address")!.StartsWith("127.")))
                entry.HasIPv4 = true;
            merged[name] = entry;
        }

        var all = new List<MacNetworkInterface>();
        foreach (var name in order)
        {
            var (iface, addresses, up, hasIPv4) = merged[name];
            var ips = Arr(iface, "ipAddresses").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
            ips.AddRange(addresses.Select(a => Str(a, "address")).Where(a => a is not null).Select(a => a!));
            if (ips.Count == 0 && Str(iface, "address") is { } single) ips.Add(single);

            var status = Str(iface, "status") ?? "";
            var isUp = status is "Up" or "Active" or "Connected" or "active" || up || ips.Any(i => IsIPv4(i) && !i.StartsWith("127."));
            string? display = null;
            if (ips.Count > 0)
                display = isUp || Bool(iface, "isActive") == true
                    ? ips.FirstOrDefault(IsIPv4) ?? ips[0]
                    : ips.FirstOrDefault(i => !i.StartsWith("fe80::") && !i.StartsWith("169.254.") && !i.StartsWith("127.")) ?? ips[0];
            var hasValidIp = ips.Any(i => IsIPv4(i) && !i.StartsWith("127.") && !i.StartsWith("169.254."));

            all.Add(new MacNetworkInterface
            {
                Name = Str(iface, "name", "interface", "friendlyName") ?? "Unknown",
                Type = Str(iface, "type"),
                IpAddress = display,
                IpAddresses = ips,
                MacAddress = Str(iface, "macAddress", "mac"),
                IsActive = Bool(iface, "isActive") == true || up || hasIPv4 || hasValidIp,
                WirelessProtocol = Str(iface, "wirelessProtocol"),
            });
        }

        string[] virtualMacs = ["00:15:5d", "00:50:56", "08:00:27", "0a:00:27"];
        return all
            .Where(i => Regex.IsMatch(i.Name, @"^en\d+$")
                || (!virtualMacs.Any(v => (i.MacAddress ?? "").StartsWith(v, StringComparison.OrdinalIgnoreCase))
                    && !i.IpAddresses.Any(a => a.StartsWith("172.") || a.StartsWith("10.0.75.") || a.StartsWith("169.254."))))
            .OrderBy(i => i.IsActive ? 0 : 1)
            .ThenBy(i => i.IsActive && i.Type == "Wireless" ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsIPv4(string? s) => s is not null && Regex.IsMatch(s, @"^(\d{1,3}\.){3}\d{1,3}$");

    /// <summary>
    /// The Wi-Fi generation for an 802.11 PHY mode ("802.11ax" is Wi-Fi 6). osquery's
    /// "mode" can be the interface's role instead ("Station"), which is no generation,
    /// so only a mode naming 802.11 counts.
    /// </summary>
    public static string? ProtocolFromMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || !mode.Contains("11")) return null;
        if (mode.Contains("be")) return "WiFi 7";
        if (mode.Contains("ax")) return "WiFi 6";
        if (mode.Contains("ac")) return "WiFi 5";
        if (mode.Contains('n')) return "WiFi 4";
        return mode;
    }

    private static string? Mbps(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? $"{Math.Round(v, MidpointRounding.AwayFromZero)} Mbps" : null;
}
