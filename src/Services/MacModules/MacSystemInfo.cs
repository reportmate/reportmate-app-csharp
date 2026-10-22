// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A Mac's system module as the System tab draws it. Port of the Mac app's
/// SystemInfo and SystemItems (ReportMateKit/Processing), Mac branches only: this
/// reader only ever sees a Mac's report, so the Windows-only fields (activation,
/// feature update, pending Windows updates) have nothing to read.
/// </summary>
public sealed record MacSystemInfo(
    MacOperatingSystem OperatingSystem,
    string? Uptime,
    string? BootTimeText,
    DateTime? BootTime,
    IReadOnlyList<MacPendingUpdate> PendingUpdates,
    IReadOnlyList<MacInstallHistoryItem> InstallHistory,
    IReadOnlyList<MacLaunchdItem> Services,
    IReadOnlyList<MacLaunchdItem> ScheduledTasks,
    IReadOnlyList<MacLoginItem> LoginItems,
    IReadOnlyList<MacExtension> Extensions,
    IReadOnlyList<MacKernelExtension> KernelExtensions,
    IReadOnlyList<MacHelperTool> HelperTools,
    IReadOnlyList<MacEnvironmentVariable> Environment)
{
    public bool HasData => OperatingSystem.Name is not null || OperatingSystem.Version is not null;

    /// <summary>
    /// Background Activity: the client reports launchd jobs twice, once as
    /// scheduled tasks (osquery's view) and once as services (the plists), and the
    /// web and Mac apps list both in one table, tasks first.
    /// </summary>
    public IReadOnlyList<MacLaunchdItem> LaunchdItems => [.. ScheduledTasks, .. Services];

    public int BackgroundItemCount => Services.Count + ScheduledTasks.Count;

    /// <summary>Running counts services only, as the web's runningServices does.</summary>
    public int RunningServiceCount => Services.Count(s => s.IsRunning);

    public IReadOnlyList<MacPendingUpdate> DeferredUpdates => PendingUpdates.Where(u => u.Deferred || u.DeferredUntil is not null).ToList();

    public IReadOnlyList<MacPendingUpdate> ActiveUpdates => PendingUpdates.Where(u => !u.Deferred && u.DeferredUntil is null).ToList();

    /// <summary>
    /// The Apple receipts the macOS Updates card lists under the pending updates:
    /// the first ten in the order the client sent them, which is not sorted, as the
    /// web and Mac apps take them.
    /// </summary>
    public IReadOnlyList<MacInstallHistoryItem> RecentSystemUpdates => InstallHistory.Where(i => i.IsSystemUpdate).Take(10).ToList();

    /// <summary>Install History, newest first; a receipt with no time sorts last.</summary>
    public IReadOnlyList<MacInstallHistoryItem> InstallHistoryNewestFirst =>
        InstallHistory.OrderByDescending(i => i.InstallDate ?? DateTime.MinValue).ToList();

    public static MacSystemInfo? From(JsonElement? module)
    {
        if (module is not { } raw) return null;
        var sys = UnwrapSingleton(raw);
        if (sys.ValueKind != JsonValueKind.Object) return null;

        var os = MacJson.Node(sys, "operatingSystem");
        var details = MacJson.Node(sys, "systemDetails");

        var version = MacJson.Str(os, "version");
        string? displayVersion = MacJson.Str(os, "displayVersion");
        if (displayVersion is null)
        {
            if (MacJson.Int(os, "majorVersion") is { } major)
                displayVersion = $"{major}.{MacJson.Int(os, "minorVersion") ?? 0}.{MacJson.Int(os, "patchVersion") ?? 0}";
            else if (MacJson.Int(os, "major") is { } m)
                displayVersion = $"{m}.{MacJson.Int(os, "minor") ?? 0}.{MacJson.Int(os, "patch") ?? 0}";
        }

        string? keyboard = MacJson.Str(os, "activeKeyboardLayout");
        if (keyboard is null)
        {
            var layouts = Strings(os, "keyboardLayouts");
            if (layouts.Count == 0) layouts = Strings(details, "keyboardLayouts");
            if (layouts.Count > 0) keyboard = string.Join(", ", layouts);
        }

        var operatingSystem = new MacOperatingSystem(
            Name: First(os, "name", "productName"),
            Version: version,
            DisplayVersion: displayVersion,
            Build: First(os, "build", "buildNumber"),
            Architecture: First(os, "architecture", "arch"),
            Locale: MacJson.Str(os, "locale") ?? MacJson.Str(details, "locale"),
            TimeZone: First(os, "timeZone", "timezone") ?? MacJson.Str(details, "timeZone"),
            KeyboardLayout: keyboard,
            Platform: MacJson.Str(os, "platform"),
            KernelVersion: MacJson.Str(os, "kernelVersion"));

        var bootText = First(sys, "lastBootTime", "bootTime") ?? MacJson.Str(details, "bootTime");

        return new MacSystemInfo(
            operatingSystem,
            Uptime: UptimeText(First(sys, "uptimeString", "uptime") ?? MacJson.Str(details, "uptimeString")),
            BootTimeText: bootText,
            BootTime: ParseTime(bootText),
            PendingUpdates: Items(sys, "pendingAppleUpdates", MacPendingUpdate.From),
            InstallHistory: Items(sys, "installHistory", MacInstallHistoryItem.From),
            Services: Items(sys, "services", MacLaunchdItem.From),
            ScheduledTasks: Items(sys, "scheduledTasks", MacLaunchdItem.From),
            LoginItems: Items(sys, "loginItems", MacLoginItem.From),
            Extensions: Items(sys, "systemExtensions", MacExtension.From),
            KernelExtensions: Items(sys, "kernelExtensions", MacKernelExtension.From),
            HelperTools: Items(sys, "privilegedHelperTools", MacHelperTool.From),
            Environment: MacEnvironmentVariable.Read(MacJson.Node(sys, "environment")));
    }

    /// <summary>
    /// The API has at times wrapped a module in a one-element array; the Mac app
    /// unwraps it before reading.
    /// </summary>
    private static JsonElement UnwrapSingleton(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 1 ? e[0] : e;

    private static IReadOnlyList<T> Items<T>(JsonElement parent, string key, Func<JsonElement, T> make) =>
        MacJson.Arr(parent, key).Where(e => e.ValueKind == JsonValueKind.Object).Select(make).ToList();

    private static List<string> Strings(JsonElement? parent, string key) =>
        MacJson.Arr(parent, key).Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

    /// <summary>
    /// The first key with a non-blank value. MacJson.Str stops at the first key
    /// present, so an empty "label" would hide a usable "name"; the Mac app's
    /// firstString skips blanks, and so does this.
    /// </summary>
    /// <summary>
    /// Uptime as a duration. The Mac client sends uptimeString when it has one and
    /// the bare seconds otherwise, which read as "1296000" on the tab.
    /// </summary>
    public static string? UptimeText(string? value)
    {
        if (value is null || !double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return value;
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    internal static string? First(JsonElement? e, params string[] keys)
    {
        foreach (var key in keys)
            if (MacJson.Str(e, key) is { } v) return v;
        return null;
    }

    internal static bool Flag(JsonElement e, params string[] keys) => keys.Any(k => MacJson.Bool(e, k) == true);

    /// <summary>
    /// Receipt and boot times arrive as ISO text or as Unix seconds with a
    /// fraction, which DateTime parsing alone does not read.
    /// </summary>
    internal static DateTime? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            if (n <= 1e8) return null;
            var seconds = n > 1e11 ? n / 1000 : n;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime;
        }
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            ? when : null;
    }
}

public sealed record MacOperatingSystem(
    string? Name,
    string? Version,
    string? DisplayVersion,
    string? Build,
    string? Architecture,
    string? Locale,
    string? TimeZone,
    string? KeyboardLayout,
    string? Platform,
    string? KernelVersion)
{
    /// <summary>The headline tile: the release's name, from the version's major number.</summary>
    public string MarketingName => MacOsNames.MarketingName(Version);

    /// <summary>The Version tile: major.minor.patch, padded with zeros.</summary>
    public string FormattedVersion => MacOsNames.FormattedVersion(DisplayVersion);
}

/// <summary>Port of the Mac app's OSNames, Mac half.</summary>
public static class MacOsNames
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [27] = "macOS 27", [26] = "Tahoe", [15] = "Sequoia", [14] = "Sonoma", [13] = "Ventura",
        [12] = "Monterey", [11] = "Big Sur", [10] = "Catalina",
    };

    private static readonly Dictionary<int, string> TenNames = new()
    {
        [15] = "Catalina", [14] = "Mojave", [13] = "High Sierra", [12] = "Sierra",
    };

    public static string MarketingName(string? version)
    {
        var parts = (version ?? "").Split('.');
        if (!int.TryParse(parts[0], out var major)) return "macOS";
        if (major == 10)
        {
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
            return TenNames.TryGetValue(minor, out var old) ? old : "macOS";
        }
        return Names.TryGetValue(major, out var name) ? name : "macOS";
    }

    public static string FormattedVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return "Unknown";
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string Part(int i) => parts.Length > i ? parts[i] : "0";
        return $"{Part(0)}.{Part(1)}.{Part(2)}";
    }
}

/// <summary>An update softwareupdate offers, perhaps held back by a deferral profile.</summary>
public sealed record MacPendingUpdate(
    string Name,
    string? Version,
    string? BuildVersion,
    string? ProductKey,
    string? Size,
    bool Recommended,
    bool IsSecurity,
    bool RestartRequired,
    bool Deferred,
    string? DeferredUntil,
    string? FirstOfferedAt,
    string? UpdateType)
{
    public static MacPendingUpdate From(JsonElement u) => new(
        Name: MacSystemInfo.First(u, "name", "displayName", "title") ?? "",
        Version: MacJson.Str(u, "version"),
        BuildVersion: MacJson.Str(u, "buildVersion"),
        ProductKey: MacJson.Str(u, "productKey"),
        Size: MacJson.Str(u, "size"),
        Recommended: MacSystemInfo.Flag(u, "recommended", "isRecommended"),
        IsSecurity: MacSystemInfo.Flag(u, "isSecurity"),
        RestartRequired: MacSystemInfo.Flag(u, "restartRequired", "rebootRequired"),
        Deferred: MacSystemInfo.Flag(u, "deferred"),
        DeferredUntil: MacJson.Str(u, "deferredUntil"),
        FirstOfferedAt: MacJson.Str(u, "firstOfferedAt"),
        UpdateType: MacJson.Str(u, "updateType"));

    /// <summary>The status badge: deferral wins over a restart, as on the web.</summary>
    public string StatusLabel => Deferred ? "Deferred" : RestartRequired ? "Restart Required" : "Pending";

    /// <summary>The line under the badge: when a deferred update becomes available, or failing that when it was first offered.</summary>
    public string? StatusNote =>
        !Deferred ? null
        : DeferredUntil is not null ? $"Available {ShortDate(DeferredUntil)}"
        : FirstOfferedAt is not null ? $"Offered {ShortDate(FirstOfferedAt)}"
        : null;

    /// <summary>The line under the name: the build, else the product key.</summary>
    public string? Detail => BuildVersion ?? ProductKey;

    private static string ShortDate(string text) =>
        MacSystemInfo.ParseTime(text) is { } d ? d.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) : text;
}

/// <summary>A package receipt from the last 90 days.</summary>
public sealed record MacInstallHistoryItem(string PackageId, string? PackageFilename, string? Version, string? InstallTime, DateTime? InstallDate)
{
    public static MacInstallHistoryItem From(JsonElement i)
    {
        var time = MacSystemInfo.First(i, "installTime", "install_time");
        return new(
            MacSystemInfo.First(i, "packageId", "package_id") ?? "",
            MacSystemInfo.First(i, "packageFilename", "package_filename"),
            MacJson.Str(i, "version"),
            time,
            MacSystemInfo.ParseTime(time));
    }

    /// <summary>Apple system and security updates among the receipts, by package id.</summary>
    public bool IsSystemUpdate
    {
        get
        {
            var p = PackageId.ToLowerInvariant();
            return p.Contains("com.apple") || p.Contains("macos") || p.Contains("securityupdate") || p.Contains("safari") || p.Contains("xprotect");
        }
    }
}

public enum LaunchdScope { System, User, Apple }

public enum LaunchdKind { Daemon, Agent }

public enum LaunchdStatus { Running, Stopped, Disabled }

/// <summary>
/// A launchd daemon or agent. Services and scheduled tasks both read through
/// this, as the Mac app's Background Activity list reads them, so a task's
/// "Enabled" or "Loaded" status counts as running there.
/// </summary>
public sealed record MacLaunchdItem(
    string Name,
    string? Label,
    string Status,
    int? Pid,
    bool RunAtLoad,
    bool KeepAlive,
    bool Disabled,
    string Path,
    string? Program,
    string Type,
    string Source,
    bool ManagedByProfile,
    string? ProfileIdentifier,
    string? PlistContent,
    string? Username)
{
    public static MacLaunchdItem From(JsonElement s)
    {
        var pid = MacJson.Int(s, "pid");
        return new(
            Name: MacSystemInfo.First(s, "label", "name") ?? "",
            Label: MacJson.Str(s, "label"),
            Status: MacJson.Str(s, "status") ?? (pid is not null ? "Running" : "Stopped"),
            Pid: pid,
            RunAtLoad: MacSystemInfo.Flag(s, "runAtLoad"),
            KeepAlive: MacSystemInfo.Flag(s, "keepAlive"),
            Disabled: MacSystemInfo.Flag(s, "disabled"),
            Path: MacSystemInfo.First(s, "path", "binaryPath") ?? "",
            Program: MacJson.Str(s, "program"),
            Type: MacJson.Str(s, "type") ?? "",
            Source: MacJson.Str(s, "source") ?? "",
            ManagedByProfile: MacSystemInfo.Flag(s, "managedByProfile"),
            ProfileIdentifier: MacJson.Str(s, "profileIdentifier"),
            PlistContent: MacJson.Str(s, "plistContent"),
            Username: MacJson.Str(s, "username"));
    }

    public string DisplayName => Label ?? Name;

    /// <summary>The path under the name, or the program when the plist has no path.</summary>
    public string Location => Path.Length > 0 ? Path : Program ?? "";

    /// <summary>The services statistic's test: a running status or a live pid.</summary>
    public bool IsRunning
    {
        get
        {
            var s = Status.ToLowerInvariant();
            return s.Contains("running") || s.Contains("started") || Pid is not null;
        }
    }

    public LaunchdScope Scope
    {
        get
        {
            var source = Source.ToLowerInvariant();
            var name = DisplayName.ToLowerInvariant();
            var path = Path.ToLowerInvariant();
            if (source == "apple" || name.StartsWith("com.apple.") || path.Contains("/system/")) return LaunchdScope.Apple;
            if (source == "user" || path.Contains("/users/")) return LaunchdScope.User;
            return LaunchdScope.System;
        }
    }

    public LaunchdKind Kind =>
        Type.Contains("agent", StringComparison.OrdinalIgnoreCase) || Path.Contains("launchagents", StringComparison.OrdinalIgnoreCase)
            ? LaunchdKind.Agent : LaunchdKind.Daemon;

    /// <summary>
    /// Disabled beats everything; "loaded" and "enabled" count as running, because
    /// a task's status is launchd's load state rather than a process.
    /// </summary>
    public LaunchdStatus LaunchdStatus
    {
        get
        {
            var status = Status.ToLowerInvariant();
            if (Disabled || status is "disabled" or "unloaded") return LaunchdStatus.Disabled;
            if (Pid is not null || status is "running" or "loaded" or "enabled") return LaunchdStatus.Running;
            return LaunchdStatus.Stopped;
        }
    }

    /// <summary>The plist the client captured, indented when it is JSON, as the Mac app shows it.</summary>
    public string? PlistPretty
    {
        get
        {
            if (PlistContent is null) return null;
            try
            {
                using var doc = JsonDocument.Parse(PlistContent);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return PlistContent;
            }
        }
    }
}

public sealed record MacLoginItem(string Name, string? Path, string Type, bool Enabled)
{
    public static MacLoginItem From(JsonElement i) => new(
        MacJson.Str(i, "name") ?? "",
        MacJson.Str(i, "path"),
        MacJson.Str(i, "type") ?? "LoginItem",
        MacJson.Bool(i, "enabled") ?? true);
}

public enum ExtensionStatus { Other, Enabled, Waiting, Disabled }

/// <summary>A system or app extension, grouped the way System Settings groups them.</summary>
public sealed record MacExtension(
    string Identifier,
    string? Version,
    string State,
    string? TeamId,
    string? BundlePath,
    string? Category,
    string? Type,
    string? ExtensionCategory,
    string? AppName,
    bool ManagedByProfile,
    string? ProfileIdentifier)
{
    public static MacExtension From(JsonElement e) => new(
        Identifier: MacJson.Str(e, "identifier") ?? "",
        Version: MacJson.Str(e, "version"),
        State: MacJson.Str(e, "state") ?? "unknown",
        TeamId: MacSystemInfo.First(e, "teamId", "team"),
        BundlePath: MacJson.Str(e, "bundlePath"),
        Category: MacJson.Str(e, "category"),
        Type: MacJson.Str(e, "type"),
        ExtensionCategory: MacJson.Str(e, "extensionCategory"),
        AppName: MacJson.Str(e, "appName"),
        ManagedByProfile: MacSystemInfo.Flag(e, "managedByProfile"),
        ProfileIdentifier: MacJson.Str(e, "profileIdentifier"));

    /// <summary>System Settings' category order, which the By Category view keeps.</summary>
    public static readonly string[] Categories =
    [
        "Actions", "Camera Extensions", "Dock Tiles", "Driver Extensions", "Endpoint Security Extensions", "File Providers",
        "File System Extensions", "Finder", "Media Extensions", "Network Extensions", "Photos Editing", "Quick Look", "Sharing", "Spotlight", "Xcode Source Editor",
    ];

    /// <summary>systemextensionsctl's states ("activated_enabled", "waiting for user") reduced to the three System Settings shows.</summary>
    public (string Label, ExtensionStatus Status) NormalizedState
    {
        get
        {
            var s = State.ToLowerInvariant();
            if (s.Contains("waiting")) return ("Waiting", ExtensionStatus.Waiting);
            if (s.Contains("activated") || s.Contains("enabled") || s == "active") return ("Enabled", ExtensionStatus.Enabled);
            if (s is "disabled" or "inactive") return ("Disabled", ExtensionStatus.Disabled);
            return (State.Length == 0 ? "Unknown" : State, ExtensionStatus.Other);
        }
    }

    /// <summary>
    /// The category from the reported category, then the identifier, then the
    /// bundle path, because the client fills the category for system extensions
    /// only and app extensions have to be placed by name.
    /// </summary>
    public string ResolvedCategory
    {
        get
        {
            var c = (ExtensionCategory ?? Category ?? Type ?? "").ToLowerInvariant();
            var id = Identifier.ToLowerInvariant();
            var bp = (BundlePath ?? "").ToLowerInvariant();
            if (c.Contains("network")) return "Network Extensions";
            if (c.Contains("endpoint") || c.Contains("security")) return "Endpoint Security Extensions";
            if (c.Contains("driver")) return "Driver Extensions";
            if (c.Contains("file provider")) return "File Providers";
            if (c.Contains("file system")) return "File System Extensions";
            if (c.Contains("finder")) return "Finder";
            if (c.Contains("quicklook") || c.Contains("quick look")) return "Quick Look";
            if (c.Contains("sharing") || c.Contains("share")) return "Sharing";
            if (c.Contains("spotlight")) return "Spotlight";
            if (c.Contains("photos")) return "Photos Editing";
            if (c.Contains("action")) return "Actions";
            if (c.Contains("camera")) return "Camera Extensions";
            if (c.Contains("media")) return "Media Extensions";
            if (c.Contains("dock")) return "Dock Tiles";
            if (c.Contains("xcode") || c.Contains("source editor")) return "Xcode Source Editor";
            if (id.Contains("quicklook") || id.Contains("qlgenerator")) return "Quick Look";
            if (id.Contains("share") || id.Contains("sharingservice")) return "Sharing";
            if (id.Contains("fileprovider")) return "File Providers";
            if (id.Contains("network")) return "Network Extensions";
            if (id.Contains("endpoint") || id.Contains("edr") || id.Contains("security")) return "Endpoint Security Extensions";
            if (id.Contains("finder")) return "Finder";
            if (id.Contains("spotlight") || id.Contains("mdimporter")) return "Spotlight";
            if (id.Contains("action")) return "Actions";
            if (id.Contains("camera")) return "Camera Extensions";
            if (id.Contains("photos") || id.Contains("photoedit")) return "Photos Editing";
            if (id.Contains("docktile")) return "Dock Tiles";
            if (bp.Contains("quicklook")) return "Quick Look";
            if (bp.Contains("share")) return "Sharing";
            if (bp.Contains("finder")) return "Finder";
            if (bp.Contains("spotlight") || bp.Contains("mdimporter")) return "Spotlight";
            if (bp.Contains("fileprovider")) return "File Providers";
            if (bp.Contains("photoedit")) return "Photos Editing";
            return ExtensionCategory ?? "Actions";
        }
    }

    private static readonly Dictionary<string, string> KnownApps = new()
    {
        ["findmy"] = "Find My", ["1password"] = "1Password", ["icloud"] = "iCloud", ["iwork"] = "iWork", ["keynote"] = "Keynote", ["pages"] = "Pages", ["numbers"] = "Numbers",
        ["safari"] = "Safari", ["mail"] = "Mail", ["notes"] = "Notes", ["photos"] = "Photos", ["preview"] = "Preview", ["textedit"] = "TextEdit", ["xcode"] = "Xcode",
        ["automator"] = "Automator", ["finder"] = "Finder", ["zoom"] = "Zoom", ["slack"] = "Slack", ["dropbox"] = "Dropbox", ["onedrive"] = "OneDrive", ["googledrive"] = "Google Drive",
        ["chrome"] = "Google Chrome", ["firefox"] = "Firefox", ["vscode"] = "VS Code", ["visualstudiocode"] = "VS Code", ["iterm"] = "iTerm", ["iterm2"] = "iTerm2", ["terminal"] = "Terminal",
        ["bartender"] = "Bartender", ["cleanmymac"] = "CleanMyMac", ["raycast"] = "Raycast", ["alfred"] = "Alfred", ["bettertouchtool"] = "BetterTouchTool",
        ["keyboard-maestro"] = "Keyboard Maestro", ["screenflow"] = "ScreenFlow", ["vlc"] = "VLC", ["spotify"] = "Spotify", ["microsoft"] = "Microsoft", ["office"] = "Microsoft Office",
        ["word"] = "Microsoft Word", ["excel"] = "Microsoft Excel", ["powerpoint"] = "Microsoft PowerPoint", ["outlook"] = "Microsoft Outlook", ["teams"] = "Microsoft Teams",
    };

    private static readonly string[] ExtensionWords =
        ["extension", "widget", "intent", "intents", "share", "sharing", "action", "quicklook", "spotlight", "finder", "provider", "network", "endpoint", "security", "helper", "service", "daemon", "agent"];

    /// <summary>
    /// The owning app: the reported name, else the .app in the bundle path, else a
    /// known product in the identifier, else the identifier's first segment after
    /// the vendor that is not an extension word, spaced out of camel case.
    /// </summary>
    public string ResolvedAppName
    {
        get
        {
            if (AppName is not null) return AppName;
            if (BundlePath is not null && Regex.Match(BundlePath, @"/([^/]+)\.app") is { Success: true } m) return m.Groups[1].Value;
            var parts = Identifier.Split('.');
            if (parts.Length < 3) return "System";
            foreach (var p in parts.Skip(2))
                if (KnownApps.TryGetValue(p.ToLowerInvariant(), out var known)) return known;
            var appPart = parts.Skip(2).FirstOrDefault(p =>
            {
                var l = p.ToLowerInvariant();
                return !ExtensionWords.Any(l.Contains) && l.Length > 2;
            }) ?? parts[2];
            var spaced = Regex.Replace(appPart, "([a-z])([A-Z])", "$1 $2");
            spaced = Regex.Replace(spaced, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            spaced = spaced.Replace('-', ' ').Replace('_', ' ');
            return string.Join(" ", spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..])).Trim();
        }
    }
}

public sealed record MacKernelExtension(string Name, string? Version, string? Path, double Size, bool Loaded)
{
    public static MacKernelExtension From(JsonElement k) => new(
        MacJson.Str(k, "name") ?? "",
        MacJson.Str(k, "version"),
        MacJson.Str(k, "path"),
        MacJson.Num(k, "size") ?? 0,
        MacJson.Bool(k, "loaded") ?? true);

    public string SizeText => Size > 0 ? $"{Size / 1024:0.0} KB" : "Unknown";
}

public sealed record MacHelperTool(string Name, string? Path, string? BundleIdentifier, string? TeamId, bool Signed, string? ModifiedDate)
{
    public static MacHelperTool From(JsonElement h) => new(
        MacJson.Str(h, "name") ?? "",
        MacJson.Str(h, "path"),
        MacSystemInfo.First(h, "bundleIdentifier", "bundle_identifier"),
        MacSystemInfo.First(h, "teamId", "team_id"),
        MacSystemInfo.Flag(h, "signed"),
        MacSystemInfo.First(h, "modifiedDate", "modified_date"));
}

/// <summary>
/// One environment variable. PATH and the hosts file lead the list and open as
/// blocks, because they are what someone diagnosing a Mac reads first.
/// </summary>
public sealed record MacEnvironmentVariable(string Name, string Value)
{
    public bool IsPath => Name.Equals("path", StringComparison.OrdinalIgnoreCase);

    public bool IsHostsFile => Name.Equals("hosts_file", StringComparison.OrdinalIgnoreCase);

    /// <summary>PATH's entries, split on ';' when the value has one and ':' otherwise.</summary>
    public IReadOnlyList<string> PathEntries =>
        Value.Split(Value.Contains(';') ? ';' : ':', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The client sends the environment as an object; older clients sent name/value rows.</summary>
    public static IReadOnlyList<MacEnvironmentVariable> Read(JsonElement? env)
    {
        List<MacEnvironmentVariable> list = env switch
        {
            { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new MacEnvironmentVariable(MacSystemInfo.First(e, "name", "key") ?? "", RawString(MacJson.Node(e, "value")))).ToList(),
            { ValueKind: JsonValueKind.Object } o => o.EnumerateObject()
                .Select(p => new MacEnvironmentVariable(p.Name, RawString(p.Value))).ToList(),
            _ => [],
        };
        return list.OrderBy(Rank).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static int Rank(MacEnvironmentVariable e) => e.IsPath ? 0 : e.IsHostsFile ? 1 : 2;

    /// <summary>Kept untrimmed: the hosts file's layout is its content.</summary>
    private static string RawString(JsonElement? v) => v is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
}
