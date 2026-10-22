// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportMate.App.Services.MacModules;

/// <summary>The five states the web app and the Mac app give a managed item.</summary>
public enum MacInstallState
{
    Installed,
    Pending,
    Warning,
    Error,
    Removed,
}

/// <summary>A run message attached to a managed item, or to the run itself.</summary>
public sealed record MacInstallMessage(string Id, string Code, string Text, string? Details, DateTime? Timestamp, string Package);

/// <summary>One managed item as the Installs tab lists it.</summary>
public sealed class MacInstallPackage
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public MacInstallState Status { get; set; } = MacInstallState.Pending;

    /// <summary>
    /// When the item was acted on, set only for items the latest run touched: the
    /// "Last Run" filter and the Date Processed column both read it, so an item the run
    /// merely found present carries no date.
    /// </summary>
    public DateTime? LastUpdate { get; init; }

    public long? ItemSizeBytes { get; init; }
    public string Category { get; init; } = "";
    public string Developer { get; init; } = "";
    public string PendingReason { get; init; } = "";
    public int FailureCount { get; init; }
    public string? LastAttemptStatus { get; init; }
    public List<MacInstallMessage> Errors { get; } = [];
    public List<MacInstallMessage> Warnings { get; } = [];

    public bool InLastRun => LastUpdate is not null;

    /// <summary>Whether the row opens: it has a message to show, or a reason it is still pending.</summary>
    public bool HasExpandableContent =>
        Errors.Count > 0 || Warnings.Count > 0 || (Status == MacInstallState.Pending && PendingReason.Trim().Length > 0);

    /// <summary>Item size rendered as "1.2 GB", as the Mac app does; empty when unknown.</summary>
    public string FormattedItemSize
    {
        get
        {
            if (ItemSizeBytes is not { } bytes || bytes <= 0) return "";
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            var i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return i == 0 ? $"{(long)size} {units[i]}" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} {1}", size, units[i]);
        }
    }
}

/// <summary>The configuration card: what Munki is pointed at and how it last ran.</summary>
public sealed class MacInstallsConfig
{
    public string Type { get; init; } = "Munki";
    public string Version { get; init; } = "";
    public string SoftwareRepoUrl { get; init; } = "";
    public string Manifest { get; init; } = "";
    public string RunType { get; init; } = "Auto";
    public DateTime? LastRun { get; init; }
    public string Duration { get; init; } = "Unknown";
    public double? DurationSeconds { get; init; }
    public string Catalogs { get; init; } = "Not configured";
}

/// <summary>A run-level problem no item can carry: manifest, catalog or preflight failure.</summary>
public sealed record MacSystemProblem(bool IsError, string Message);

/// <summary>The latest run's system-level problems, the banner above the items table.</summary>
public sealed class MacSystemProblems
{
    public List<MacSystemProblem> Problems { get; init; } = [];
    public string? SessionId { get; init; }
    public DateTime? Time { get; init; }

    /// <summary>The run failed before evaluating the manifest, so it reported no items at all.</summary>
    public bool FailedWithoutItems { get; init; }

    public bool IsEmpty => Problems.Count == 0 && !FailedWithoutItems;
    public bool IsError => FailedWithoutItems || Problems.Any(p => p.IsError);
}

/// <summary>
/// A Mac's installs module (Munki) read the way the Mac app's InstallsInfo reads it,
/// which is itself a port of the web app's <c>extractInstalls</c>, so the three apps
/// agree about which items are in error, which the last run touched and what the
/// counts are. Only the Munki branches are ported: a Mac never sends Cimian data.
/// </summary>
/// <remarks>
/// Item states go through the shared <see cref="InstallStatus"/> ladder (the same one
/// the Windows tab and the API's ingest use), so a Mac item and a Windows item with the
/// same fields land in the same state.
/// </remarks>
public sealed class MacInstallsInfo
{
    public bool HasData { get; private init; }
    public bool IsMunki { get; private init; }
    public string SystemName { get; private init; } = "";
    public MacInstallsConfig? Config { get; private init; }
    public List<MacInstallPackage> Packages { get; private init; } = [];
    public List<MacInstallMessage> RunErrors { get; } = [];
    public List<MacInstallMessage> RunWarnings { get; } = [];
    public double? CacheSizeMb { get; private init; }
    public MacSystemProblems SystemProblems { get; private init; } = new();

    /// <summary>Items in a problem state plus run-level messages, as the web's "failed" count.</summary>
    public int Failed { get; private set; }

    public int TotalPackages => Packages.Count;
    public bool HasManagementSystem => Config is not null || SystemName.Length > 0;
    public int LastRunCount => Packages.Count(p => p.InLastRun);

    /// <summary>When the last run ended: the config's, else Munki's own end time.</summary>
    public DateTime? LastRunTimestamp { get; private init; }

    public int Count(MacInstallState status, bool lastRunOnly = false) =>
        Packages.Count(p => (!lastRunOnly || p.InLastRun) && p.Status == status);

    private static readonly HashSet<string> ProcessedStatuses = ["install_succeeded", "install_failed", "removed"];

    public static MacInstallsInfo Read(JsonElement? module)
    {
        // The Mac app unwraps a module sent as a one-element array.
        var installs = module is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() == 1 ? arr[0] : module;
        var hasData = installs is { } i && i.ValueKind switch
        {
            JsonValueKind.Object => i.EnumerateObject().Any(),
            JsonValueKind.Array => i.GetArrayLength() > 0,
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
        if (!hasData) return new MacInstallsInfo();

        var munki = MacJson.Node(installs, "munki");
        var munkiSessions = MacJson.Arr(munki, "sessions");
        var munkiItems = MacJson.Arr(munki, "items");
        var hasMunkiSessions = munkiSessions.Count > 0;
        var isMunki = munkiItems.Count > 0 || munki is not null;

        // Latest session markers: which run counts as "the last run".
        string latestStart = "", latestEnd = "", latestId = "";
        var recentSessions = MacJson.Arr(installs, "recentSessions");
        if (recentSessions.Count > 0 || hasMunkiSessions)
        {
            var s = recentSessions.Count > 0 ? recentSessions[0] : munkiSessions[0];
            latestStart = MacJson.Str(s, "startTime") ?? "";
            latestEnd = MacJson.Str(s, "endTime") ?? "";
            latestId = MacJson.Str(s, "sessionId") ?? "";
        }
        else if ((MacJson.Str(munki, "startTime") ?? MacJson.Str(munki, "endTime")) is { } t)
        {
            latestStart = t;
            latestEnd = MacJson.Str(munki, "endTime") ?? "";
        }

        var packages = new List<MacInstallPackage>();
        var hasSessions = hasMunkiSessions;

        void AddItem(JsonElement item, string name, string displayName, string? status, string version, string installedVersion,
            string id, string marker, string lastAttemptTime, string lastUpdate)
        {
            var finalStatus = Standardize(status);
            // The ingest ladder overrides the tool's verdict where it finds a problem.
            if (item.ValueKind == JsonValueKind.Object)
            {
                var classified = InstallStatus.Classify(new JsonInstallItem(item), hasSessions);
                if (classified == ItemStatus.Error) finalStatus = MacInstallState.Error;
                else if (classified == ItemStatus.Warning) finalStatus = MacInstallState.Warning;
            }

            DateTime? processed = null;
            if (marker.Length > 0)
            {
                if (IsSessionId(marker))
                {
                    if (latestId.Length > 0 && marker.Trim() == latestId.Trim())
                        processed = ParseDate(new[] { lastAttemptTime, lastUpdate, latestEnd, latestStart }.FirstOrDefault(x => x.Length > 0));
                }
                else if (ParseDate(marker) is { } itemTime && ParseDate(latestStart) is { } sessionTime && itemTime >= sessionTime.AddSeconds(-60))
                    processed = itemTime;
            }

            var pkg = new MacInstallPackage
            {
                Id = id.Length > 0 ? id : name,
                Name = name,
                DisplayName = displayName,
                Version = version.Length > 0 ? version : installedVersion,
                InstalledVersion = installedVersion,
                Status = finalStatus,
                LastUpdate = processed,
                ItemSizeBytes = ItemSize(item),
                Category = MacJson.Str(item, "category") ?? "",
                Developer = MacJson.Str(item, "developer") ?? "",
                PendingReason = MacJson.Str(item, "pendingReason") ?? "",
                FailureCount = MacJson.Int(item, "failureCount") ?? 0,
                LastAttemptStatus = MacJson.Str(item, "lastAttemptStatus"),
            };

            var loop = (MacJson.Bool(item, "hasInstallLoop") ?? false) || (MacJson.Bool(item, "installLoopDetected") ?? false);
            var fallback = ParseDate(lastAttemptTime.Length > 0 ? lastAttemptTime : lastUpdate);
            if (MacJson.Str(item, "lastError") is { } lastError)
            {
                var (msg, details) = SplitLogText(lastError);
                pkg.Errors.Add(new MacInstallMessage($"{pkg.Id}-last-error", loop ? "INSTALL_LOOP" : "ERROR", msg, details,
                    LatestAttempt(item, "failed", "error") ?? fallback, name));
            }
            if (MacJson.Str(item, "lastWarning") is { } lastWarning)
            {
                var (msg, details) = SplitLogText(lastWarning);
                pkg.Warnings.Add(new MacInstallMessage($"{pkg.Id}-last-warning", loop ? "INSTALL_LOOP" : "WARNING", msg, details,
                    LatestAttempt(item, "warning") ?? fallback, name));
            }
            if (pkg.Errors.Count > 0) pkg.Status = MacInstallState.Error;
            else if (pkg.Warnings.Count > 0) pkg.Status = MacInstallState.Warning;
            packages.Add(pkg);
        }

        var recentInstalls = MacJson.Arr(installs, "recentInstalls");
        var pendingNames = MacJson.Arr(munki, "pendingPackages").Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        if (munkiItems.Count > 0)
        {
            foreach (var item in munkiItems)
            {
                var name = S(item, "name", "displayName") ?? "Unknown";
                var status = MacJson.Str(item, "status") ?? "";
                // Legacy Munki has no session marker; an item it processed carries the run's end time instead.
                var marker = MacJson.Str(item, "lastSeenInSession") ?? (ProcessedStatuses.Contains(status) ? MacJson.Str(item, "endTime") ?? "" : "");
                AddItem(item, name, S(item, "displayName", "name") ?? "Unknown", S(item, "currentStatus", "status"),
                    S(item, "version", "installedVersion") ?? "Unknown", MacJson.Str(item, "installedVersion") ?? "",
                    MacJson.Str(item, "id") ?? name.ToLowerInvariant().Replace(' ', '-'), marker,
                    MacJson.Str(item, "lastAttemptTime") ?? "", "");
            }
        }
        else if (recentInstalls.Count > 0)
        {
            foreach (var item in recentInstalls)
            {
                var name = S(item, "name", "displayName") ?? "Unknown";
                AddItem(item, name, S(item, "displayName", "name") ?? name, MacJson.Str(item, "status"),
                    S(item, "version", "installedVersion") ?? "", MacJson.Str(item, "installedVersion") ?? "",
                    MacJson.Str(item, "id") ?? name.ToLowerInvariant(), MacJson.Str(item, "lastSeenInSession") ?? "", "",
                    MacJson.Str(item, "lastUpdate") ?? "");
            }
        }
        else
        {
            foreach (var p in pendingNames)
                packages.Add(new MacInstallPackage
                {
                    Id = p.ToLowerInvariant().Replace(' ', '-'), Name = p, DisplayName = p, Version = "Unknown",
                    Status = MacInstallState.Pending, LastUpdate = MacJson.Date(munki, "endTime"),
                });
        }

        // Run type and duration: the latest run that did something, else the latest that finished.
        var runType = "Manual";
        var duration = "Unknown";
        double? durationSeconds = null;
        if (recentSessions.Count > 0)
        {
            JsonElement chosen = recentSessions.Cast<JsonElement?>().FirstOrDefault(s => MacJson.Str(s, "status") is "completed" or "partial_failure"
                    && new[] { "totalActions", "packagesFailed", "packagesInstalled", "failures", "installs", "updates" }.Any(k => (MacJson.Int(s, k) ?? 0) > 0))
                ?? recentSessions.Cast<JsonElement?>().FirstOrDefault(s => MacJson.Str(s, "status") == "completed"
                    && ((MacJson.Num(s, "durationSeconds") ?? 0) > 0 || MacJson.Str(s, "duration") is { } d && d != "00:00:00"))
                ?? recentSessions[0];
            runType = MacJson.Str(chosen, "runType") ?? "Manual";
            duration = MacJson.Str(chosen, "duration") ?? "Unknown";
            durationSeconds = MacJson.Num(chosen, "durationSeconds");
        }
        else if (hasMunkiSessions)
        {
            JsonElement chosen = munkiSessions.Cast<JsonElement?>().FirstOrDefault(s => MacJson.Str(s, "status") is "completed" or "partial_failure"
                    && ((MacJson.Int(s, "summary.totalActions") ?? 0) > 0 || (MacJson.Int(s, "summary.failures") ?? 0) > 0)
                    && (MacJson.Num(s, "durationSeconds") ?? 0) > 0)
                ?? munkiSessions.Cast<JsonElement?>().FirstOrDefault(s => MacJson.Str(s, "status") != "running")
                ?? munkiSessions[0];
            runType = MacJson.Str(chosen, "runType") ?? "Manual";
            durationSeconds = MacJson.Num(chosen, "durationSeconds");
            duration = durationSeconds is { } secs ? $"{(int)secs}s" : "Unknown";
        }

        var cache = MacJson.Num(installs, "cacheStatus.cacheSizeMb") ?? (recentSessions.Count > 0 ? MacJson.Num(recentSessions[0], "cacheSizeMb") : null);

        var catalogList = MacJson.Arr(munki, "catalogs").Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList();
        MacInstallsConfig? config = null;
        var systemName = "";
        if (isMunki)
        {
            systemName = "Munki";
            config = new MacInstallsConfig
            {
                Type = "Munki",
                Version = MacJson.Str(munki, "version") ?? "",
                SoftwareRepoUrl = S(munki, "softwareRepoURL", "softwareRepoUrl") ?? "",
                Manifest = S(munki, "manifestName", "clientIdentifier") ?? "",
                RunType = hasMunkiSessions ? runType : "Auto",
                LastRun = ParseDate(S(munki, "endTime", "lastRun")),
                Duration = hasMunkiSessions ? duration : "Unknown",
                DurationSeconds = hasMunkiSessions ? durationSeconds : null,
                Catalogs = catalogList.Count > 0 ? string.Join(", ", catalogList) : "Not configured",
            };
        }

        var info = new MacInstallsInfo
        {
            HasData = true,
            IsMunki = isMunki,
            SystemName = systemName,
            Config = config,
            Packages = packages,
            CacheSizeMb = cache,
            LastRunTimestamp = config?.LastRun ?? MacJson.Date(munki, "endTime"),
            SystemProblems = CollectSystemProblems(munki),
        };

        if (isMunki)
        {
            var stamp = MacJson.Date(munki, "endTime");
            if (hasMunkiSessions)
            {
                // The run's own problems that name no item belong to the run.
                foreach (var problem in MacJson.Arr(munki, "errorItems").Where(p => MacJson.Str(p, "name") is null))
                    if (MacJson.Str(problem, "message") is { } m) info.RunErrors.Add(new("munki-run-error-system", "MUNKI_ERROR", m, null, stamp, "Munki"));
                foreach (var problem in MacJson.Arr(munki, "warningItems").Where(p => MacJson.Str(p, "name") is null))
                    if (MacJson.Str(problem, "message") is { } m) info.RunWarnings.Add(new("munki-run-warning-system", "MUNKI_WARNING", m, null, stamp, "Munki"));
            }
            else
            {
                // Legacy Munki reports one ";"-joined string; attribute each message to the item it names.
                if (MacJson.Str(munki, "errors") is { } errors)
                    foreach (var (msg, details) in ParseInstallerBlocks(errors))
                        info.Attach(msg, details, isError: true, stamp);
                if (MacJson.Str(munki, "warnings") is { } warnings)
                    foreach (var msg in warnings.Split(';').Select(w => w.Trim()).Where(w => w.Length > 0))
                        info.Attach(msg, null, isError: false, stamp);
            }

            var problemArray = MacJson.Node(munki, "problemInstallsArray") is { ValueKind: JsonValueKind.Array } pa
                ? pa.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(e => e.Length > 0).ToList()
                : (MacJson.Str(munki, "problemInstalls") ?? "").Split(';', ',').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
            foreach (var name in problemArray)
            {
                var pkg = info.Packages.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (pkg is null)
                {
                    pkg = new MacInstallPackage { Id = name.ToLowerInvariant().Replace(' ', '-'), Name = name, DisplayName = name, Status = MacInstallState.Warning, LastUpdate = stamp };
                    info.Packages.Add(pkg);
                }
                if (!pkg.Warnings.Any(w => w.Code == "MUNKI_PROBLEM_INSTALL"))
                {
                    pkg.Warnings.Add(new($"munki-problem-{pkg.Id}", "MUNKI_PROBLEM_INSTALL", "Reported as a problem install by Munki", null, stamp, name));
                    if (pkg.Status != MacInstallState.Error) pkg.Status = MacInstallState.Warning;
                }
            }
        }

        info.Failed = info.Count(MacInstallState.Error) + info.Count(MacInstallState.Warning)
            + (isMunki ? info.RunErrors.Count + info.RunWarnings.Count : 0);
        return info;
    }

    /// <summary>
    /// Attach a legacy Munki run message to the item it names, to a new item for one
    /// missing from the list, or to the run itself.
    /// </summary>
    private void Attach(string message, string? details, bool isError, DateTime? stamp)
    {
        var lower = message.ToLowerInvariant();
        var matched = Packages.OrderByDescending(p => p.Name.Length).FirstOrDefault(p =>
            (p.Name.Length > 0 && lower.Contains(p.Name.ToLowerInvariant()))
            || (p.DisplayName.Length > 0 && p.DisplayName != p.Name && lower.Contains(p.DisplayName.ToLowerInvariant())));
        if (matched is null && ItemNameFromMunkiMessage(message) is { } itemName)
        {
            matched = Packages.FirstOrDefault(p => string.Equals(p.Name, itemName, StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                matched = new MacInstallPackage
                {
                    Id = itemName.ToLowerInvariant().Replace(' ', '-'), Name = itemName, DisplayName = itemName,
                    Status = isError ? MacInstallState.Error : MacInstallState.Warning, LastUpdate = stamp,
                };
                Packages.Add(matched);
            }
        }
        if (matched is not null)
        {
            if (isError)
            {
                if (matched.Errors.Any(e => e.Text == message)) return;
                matched.Errors.Add(new($"munki-run-error-{matched.Id}", "MUNKI_ERROR", message, details, stamp, matched.Name));
                matched.Status = MacInstallState.Error;
            }
            else
            {
                if (matched.Warnings.Any(w => w.Text == message)) return;
                matched.Warnings.Add(new($"munki-run-warning-{matched.Id}", "MUNKI_WARNING", message, null, stamp, matched.Name));
                if (matched.Status != MacInstallState.Error) matched.Status = MacInstallState.Warning;
            }
        }
        else if (isError) RunErrors.Add(new("munki-run-error-system", "MUNKI_ERROR", message, details, stamp, "Munki"));
        else RunWarnings.Add(new("munki-run-warning-system", "MUNKI_WARNING", message, null, stamp, "Munki"));
    }

    /// <summary>
    /// Port of the web's <c>collectSystemProblems</c>: only the latest session is read,
    /// because a run's problems describe the state that run found and the next clean run
    /// supersedes them.
    /// </summary>
    public static MacSystemProblems CollectSystemProblems(JsonElement? munki)
    {
        if (munki is null) return new MacSystemProblems();
        var itemless = MacJson.Arr(munki, "items").Count == 0;
        var sessions = MacJson.Arr(munki, "sessions");
        if (sessions.Count > 0)
        {
            var session = sessions[0];
            var failedWithoutItems = string.Equals(MacJson.Str(session, "status"), "failed", StringComparison.OrdinalIgnoreCase) && itemless;
            var problems = new List<MacSystemProblem>();
            foreach (var (key, isError) in new[] { ("errorItems", true), ("warningItems", false) })
                foreach (var problem in MacJson.Arr(session, key).Where(p => MacJson.Str(p, "name") is null))
                {
                    var message = CleanLogText(MacJson.Str(problem, "message"));
                    if (message.Length > 0 && !problems.Any(p => p.Message == message)) problems.Add(new MacSystemProblem(isError, message));
                }
            if (problems.Count == 0) return new MacSystemProblems { FailedWithoutItems = failedWithoutItems };
            return new MacSystemProblems
            {
                Problems = problems,
                SessionId = MacJson.Str(session, "sessionId"),
                Time = ParseDate(S(session, "endTime", "startTime")),
                FailedWithoutItems = failedWithoutItems,
            };
        }

        // Legacy Munki: lines of the run's error and warning strings that name no item.
        var errors = SystemLines(MacJson.Str(munki, "errors"));
        var warnings = SystemLines(MacJson.Str(munki, "warnings"));
        var status = (MacJson.Str(munki, "status") ?? "").ToLowerInvariant();
        var failed = MacJson.Bool(munki, "lastRunSuccess") == false || status == "error" || MacJson.Str(munki, "errors") is not null;
        return new MacSystemProblems
        {
            Problems = errors.Select(e => new MacSystemProblem(true, e)).Concat(warnings.Select(w => new MacSystemProblem(false, w))).ToList(),
            FailedWithoutItems = failed && itemless,
        };
    }

    private static List<string> SystemLines(string? raw) => raw is null ? [] :
        CleanLogText(raw).Split(';', '\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !Regex.IsMatch(l, "^-{3,}$") && !l.StartsWith("installer:", StringComparison.OrdinalIgnoreCase) && ItemNameFromMessage(l) is null)
            .ToList();

    // ── Helpers (ports of the web's installs helpers) ────────────────

    /// <summary>Port of <c>standardizeInstallStatus</c>: any client spelling to the standard five.</summary>
    public static MacInstallState Standardize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MacInstallState.Pending;
        var trimmed = raw.Trim();
        if (Enum.TryParse<MacInstallState>(trimmed, ignoreCase: false, out var exact)) return exact;
        return trimmed.ToLowerInvariant() switch
        {
            "installed" or "install" or "success" or "successful" or "completed" or "complete" or "up to date" or "uptodate" or "current" or "ok" or "install_succeeded" => MacInstallState.Installed,
            "warning" or "warnings" or "warn" or "caution" or "needs attention" or "needs_attention" or "partial" or "partially installed" or "outdated" => MacInstallState.Warning,
            "error" or "errors" or "failed" or "failure" or "fail" or "broken" or "corrupt" or "corrupted" or "missing" or "not found" or "not_found" or "invalid" or "timeout" or "cancelled" or "canceled" => MacInstallState.Error,
            "removed" or "uninstalled" or "deleted" or "absent" or "not installed" or "not_installed" => MacInstallState.Removed,
            _ => MacInstallState.Pending,
        };
    }

    /// <summary>Session ids look like <c>2026-08-28-1158</c>; anything else is a timestamp marker.</summary>
    public static bool IsSessionId(string? value) => value is not null && Regex.IsMatch(value.Trim(), @"^\d{4}-\d{2}-\d{2}-\d{4}$");

    /// <summary>
    /// Item size in bytes. The Mac client does not send <c>itemSize</c>; Munki's
    /// <c>installedSize</c> (kilobytes, from the pkginfo) stands in so the column is not
    /// always blank.
    /// </summary>
    private static long? ItemSize(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (MacJson.Long(item, "itemSize") is { } bytes and > 0) return bytes;
        return MacJson.Long(item, "installedSize") is { } kb and > 0 ? kb * 1024 : null;
    }

    /// <summary>The latest of the item's recent attempts with one of the given statuses.</summary>
    private static DateTime? LatestAttempt(JsonElement item, params string[] statuses) =>
        MacJson.Arr(item, "recentAttempts")
            .Where(a => statuses.Contains((MacJson.Str(a, "status") ?? "").ToLowerInvariant()))
            .Select(a => MacJson.Date(a, "timestamp")).Where(d => d is not null).Max();

    /// <summary>
    /// Munki writes real UTC ("Z") and offset times, so they are parsed as written. The
    /// web's <c>normalizeCimianTimestamp</c> shift is for Cimian's local-time-with-Z and
    /// is deliberately not applied here.
    /// </summary>
    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when : null;
    }

    /// <summary>
    /// The first of several fields that has text. <see cref="MacJson.Str"/> stops at the
    /// first field present, and the Mac client sends blank fields ("version": ""), so a
    /// fallback chain has to skip blanks one field at a time.
    /// </summary>
    private static string? S(JsonElement? parent, params string[] names) =>
        names.Select(n => MacJson.Str(parent, n)).FirstOrDefault(v => v is not null);

    private static readonly Regex Ansi = new(@"\u001b\[[0-9;]*[A-Za-z]|\u001b", RegexOptions.Compiled);

    /// <summary>Port of <c>cleanLogText</c>: strip ANSI, normalise line endings, collapse blank runs.</summary>
    public static string CleanLogText(string? raw)
    {
        var text = Ansi.Replace(raw ?? "", "").Replace("\r\n", "\n").Replace('\r', '\n');
        var joined = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()));
        return Regex.Replace(joined, "\n{3,}", "\n\n").Trim();
    }

    /// <summary>Port of <c>splitLogText</c>: the first line is the message, the rest are details.</summary>
    public static (string Message, string? Details) SplitLogText(string? raw)
    {
        var text = CleanLogText(raw);
        var nl = text.IndexOf('\n');
        if (nl < 0) return (text, null);
        var details = text[(nl + 1)..].Trim();
        return (text[..nl].Trim(), details.Length > 0 ? details : null);
    }

    /// <summary>
    /// Group installer log blocks (between <c>----------</c> lines) into one message
    /// each, summarised by the first line that says something.
    /// </summary>
    public static List<(string Message, string? Details)> ParseInstallerBlocks(string raw)
    {
        static string Summary(List<string> lines) => lines.FirstOrDefault(l =>
            !l.StartsWith("installer:phase:", StringComparison.OrdinalIgnoreCase)
            && !l.StartsWith("installer:status:", StringComparison.OrdinalIgnoreCase)
            && !l.StartsWith("installer:%", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(l, @"^installer:\s*Package name is\s*$", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(l, @"^installer:\s*Upgrading at base path", RegexOptions.IgnoreCase)
            && l.Trim().Length > 10) ?? lines.LastOrDefault() ?? "";

        var output = new List<(string, string?)>();
        var block = new List<string>();
        var inBlock = false;
        foreach (var part in raw.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (Regex.IsMatch(part, "^-{10,}$"))
            {
                if (inBlock && block.Count > 0) { output.Add((Summary(block), string.Join("\n", block))); block = []; }
                inBlock = !inBlock;
                continue;
            }
            if (inBlock) block.Add(part); else output.Add((part, null));
        }
        if (block.Count > 0) output.Add((Summary(block), string.Join("\n", block)));
        return output;
    }

    private static readonly Regex[] MunkiMessagePatterns =
    [
        new(@"Could not process item (.+?) for", RegexOptions.IgnoreCase),
        new(@"Install of (.+?)(?:\s+failed|-\d)", RegexOptions.IgnoreCase),
        new(@"Download of (.+?) failed", RegexOptions.IgnoreCase),
        new(@"Could not install (.+?)(?:\.|;|$)", RegexOptions.IgnoreCase),
        new(@"Problem (?:with|installing) (.+?)(?:\.|;|$)", RegexOptions.IgnoreCase),
    ];

    /// <summary>Port of <c>extractItemNameFromMessage</c>, used to attribute legacy run messages.</summary>
    public static string? ItemNameFromMunkiMessage(string message)
    {
        foreach (var re in MunkiMessagePatterns)
            if (re.Match(message) is { Success: true } m) return m.Groups[1].Value.Trim();
        return null;
    }

    private static readonly Regex[] RunWarningPatterns =
    [
        new(@"^(?:Download|Install|Installation|Removal|Update) of (.+?) failed", RegexOptions.IgnoreCase),
        new(@"Could not process item (\S+)", RegexOptions.IgnoreCase),
        new(@"Will not attempt to remove (\S+)", RegexOptions.IgnoreCase),
        new(@"^(\S+) (?:requires|is not|was not|could not)", RegexOptions.IgnoreCase),
    ];

    /// <summary>The item a Munki run message is about, or null when it is about the run (status.ts).</summary>
    public static string? ItemNameFromMessage(string message)
    {
        foreach (var re in RunWarningPatterns)
            if (re.Match(message) is { Success: true } m) return Regex.Replace(m.Groups[1].Value, "[.,:]+$", "");
        return null;
    }

    /// <summary>A Munki item read through the shared ladder's interface.</summary>
    private sealed class JsonInstallItem(JsonElement item) : IInstallItem
    {
        public string? ReportMateStatus => MacJson.Str(item, "reportmateStatus");
        public string? CurrentStatus => MacJson.Str(item, "currentStatus");
        public string? MappedStatus => MacJson.Str(item, "mappedStatus");
        public string? Status => MacJson.Str(item, "status");
        public string? LastAttemptStatus => MacJson.Str(item, "lastAttemptStatus");
        public string? LastError => MacJson.Str(item, "lastError");
        public string? LastWarning => MacJson.Str(item, "lastWarning");
        public string? LastSeenInSession => MacJson.Str(item, "lastSeenInSession");
        public bool HasInstallLoop => MacJson.Bool(item, "hasInstallLoop") ?? false;
        public bool InstallLoopDetected => MacJson.Bool(item, "installLoopDetected") ?? false;
    }
}
