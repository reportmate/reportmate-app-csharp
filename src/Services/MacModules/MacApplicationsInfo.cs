// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// Usage attached to an installed application: Windows attaches it to the app, macOS
/// keys watcher sessions by bundle path, and both send a per-day history keyed by
/// name. The three are merged, longest view wins.
/// </summary>
public sealed record MacApplicationUsage
{
    public long LaunchCount { get; init; }
    public double TotalSeconds { get; init; }
    public string? LastUsed { get; init; }
    public string? FirstSeen { get; init; }
    public IReadOnlyList<string> Users { get; init; } = [];
    public bool ActiveNow { get; init; }
    public int DaysSeen { get; init; }

    public int UniqueUserCount => Users.Count;
    public double? AverageSessionSeconds => LaunchCount > 0 ? TotalSeconds / LaunchCount : null;
    public bool HasUsage => LaunchCount > 0 || TotalSeconds > 0;
}

/// <summary>One installed application row (port of the Mac app's ApplicationItem).</summary>
public sealed record MacApplicationItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "Unknown";
    public string Publisher { get; init; } = "Unknown Publisher";
    public string Category { get; init; } = "Uncategorized";
    public string? InstallDate { get; init; }
    public string? Size { get; init; }
    public string? Path { get; init; }
    public string? BundleId { get; init; }
    public string? Source { get; init; }
    public string? Architecture { get; init; }
    public bool Nested { get; init; }
    public string? SignedBy { get; init; }
    public string? ObtainedFrom { get; init; }
    public string? BuildVersion { get; init; }
    public string? MinimumSystemVersion { get; init; }
    public MacApplicationUsage? Usage { get; init; }

    /// <summary>Name without the <c>.app</c> suffix.</summary>
    public string CleanName => Regex.Replace(DisplayName.Length == 0 ? Name : DisplayName, @"\.app$", "");

    public bool HasUsage => Usage?.HasUsage ?? false;
    public double UsageSeconds => Usage?.TotalSeconds ?? 0;
    public bool IsRunning => Usage?.ActiveNow ?? false;

    /// <summary>Publisher, or the bundle id or path when the publisher is unknown.</summary>
    public string SecondaryLine
    {
        get
        {
            if (Publisher != "Unknown Publisher" && Publisher.Length > 0)
                return BundleId is { } b ? $"{Publisher} · {b}" : Publisher;
            return BundleId ?? Path ?? "";
        }
    }

    /// <summary>The category as a person reads it: "public.app-category.developer-tools" is "Developer Tools".</summary>
    public string? CategoryLabel => MacApplicationsInfo.CategoryLabel(Category);
}

/// <summary>
/// A Mac's applications module, read as the Mac app's ApplicationsInfo reads it
/// (ReportMateKit Processing/ApplicationsInfo.swift).
/// </summary>
public sealed class MacApplicationsInfo
{
    public IReadOnlyList<MacApplicationItem> Applications { get; }
    public int ActiveSessionCount { get; }
    public int HistoryDays { get; }
    public bool HasData { get; }

    /// <summary>Non-helper apps, most used first.</summary>
    public IReadOnlyList<MacApplicationItem> BaseApps => Applications.Where(a => !a.Nested).ToList();
    public int NestedCount => Applications.Count(a => a.Nested);
    public IReadOnlyList<MacApplicationItem> UsedApps => BaseApps.Where(a => a.HasUsage).ToList();
    public IReadOnlyList<MacApplicationItem> RunningApps => BaseApps.Where(a => a.IsRunning).ToList();
    public IReadOnlyList<MacApplicationItem> UnusedApps => BaseApps.Where(a => !a.HasUsage).ToList();
    public double TotalUsageSeconds => UsedApps.Sum(a => a.UsageSeconds);
    public int DistinctUsers => Applications.SelectMany(a => a.Usage?.Users ?? []).Distinct().Count();
    public IReadOnlyList<MacApplicationItem> TopApps => UsedApps.Where(a => a.UsageSeconds > 0).Take(8).ToList();

    /// <summary>The usage filter: "all", "used", "active" (running) or "unused".</summary>
    public IReadOnlyList<MacApplicationItem> Filtered(string filter, bool hideNested)
    {
        var source = hideNested ? BaseApps : Applications;
        return filter switch
        {
            "used" => source.Where(a => a.HasUsage).ToList(),
            "active" => source.Where(a => a.IsRunning).ToList(),
            "unused" => source.Where(a => !a.HasUsage).ToList(),
            _ => source,
        };
    }

    /// <summary>The applications module, or null when the device sent none.</summary>
    public static MacApplicationsInfo? From(IReadOnlyDictionary<string, JsonElement> modules) =>
        modules.TryGetValue("applications", out var raw) ? new MacApplicationsInfo(raw) : null;

    public MacApplicationsInfo(JsonElement? rawModule)
    {
        var module = Unwrap(rawModule);
        var source = MacJson.Node(module, "installedApplications") is { ValueKind: JsonValueKind.Array }
            ? MacJson.Arr(module, "installedApplications")
            : MacJson.Arr(module, "applications");
        HasData = source.Count > 0;

        var usageData = MacJson.Node(module, "applicationUsage") ?? MacJson.Node(module, "usage");
        var sessions = MacJson.Arr(usageData, "activeSessions");
        ActiveSessionCount = sessions.Count;
        var history = MacJson.Arr(module, "dailyUsageHistory");
        HistoryDays = history.Select(h => MacJson.Str(h, "date")).Where(d => d is not null).Distinct().Count();

        var byName = new Dictionary<string, Agg>();
        foreach (var row in history)
        {
            var key = UsageKey(MacJson.Str(row, "appName"));
            if (key.Length == 0) continue;
            var e = byName.TryGetValue(key, out var found) ? found : new Agg();
            e.Launches += MacJson.Long(row, "launches") ?? 0;
            e.Seconds += MacJson.Num(row, "totalSeconds") ?? 0;
            if (MacJson.Str(row, "date") is { } date)
            {
                e.Days.Add(date);
                if (e.Last.Length == 0 || string.CompareOrdinal(date, Prefix(e.Last, 10)) > 0) e.Last = $"{date}T00:00:00Z";
                if (e.First.Length == 0 || string.CompareOrdinal(date, Prefix(e.First, 10)) < 0) e.First = $"{date}T00:00:00Z";
            }
            foreach (var u in Strings(row, "users")) e.Users.Add(u);
            byName[key] = e;
        }

        var byPath = new Dictionary<string, Agg>();
        foreach (var s in sessions)
        {
            if (MacJson.Str(s, "path") is not { } path) continue;
            var e = byPath.TryGetValue(path, out var found) ? found : new Agg();
            e.Launches += 1;
            e.Seconds += MacJson.Num(s, "durationSeconds") ?? 0;
            if (MacJson.Str(s, "startTime") is { } start)
            {
                if (string.CompareOrdinal(start, e.Last) > 0) e.Last = start;
                if (e.First.Length == 0 || string.CompareOrdinal(start, e.First) < 0) e.First = start;
                e.Days.Add(Prefix(start, 10));
            }
            if (MacJson.Str(s, "user") is { } user) e.Users.Add(user);
            if (MacJson.Bool(s, "isActive") == true) e.Active = true;
            byPath[path] = e;
        }

        var apps = new List<MacApplicationItem>();
        for (var index = 0; index < source.Count; index++)
        {
            var app = source[index];
            if (app.ValueKind == JsonValueKind.String)
            {
                var bare = Regex.Replace(app.GetString() ?? "", @"\.app$", "", RegexOptions.IgnoreCase);
                apps.Add(new MacApplicationItem
                {
                    Id = $"app-{index}-{bare}", Name = bare, DisplayName = bare, Category = "Application",
                    Usage = UsageFrom(byName.TryGetValue(UsageKey(bare), out var n) ? n : new Agg()),
                });
                continue;
            }
            if (app.ValueKind != JsonValueKind.Object) continue;

            var appPath = MacJson.Str(app, "path", "installLocation");
            var name = MacJson.Str(app, "name", "displayName") ?? "Unknown Application";
            var merged = new Agg();
            if ((MacJson.Node(app, "usage") ?? MacJson.Node(app, "Usage")) is { ValueKind: JsonValueKind.Object } existing)
            {
                merged.Launches = MacJson.Long(existing, "launchCount") ?? 0;
                merged.Seconds = MacJson.Num(existing, "totalSeconds", "totalUsageSeconds") ?? 0;
                merged.Last = MacJson.Str(existing, "lastUsed", "lastLaunchTime") ?? "";
                merged.First = MacJson.Str(existing, "firstSeen") ?? "";
                foreach (var u in Strings(existing, "users")) merged.Users.Add(u);
            }
            var sources = new[]
            {
                byName.TryGetValue(UsageKey(name), out var hn) ? hn : null,
                appPath is not null && byPath.TryGetValue(appPath, out var hp) ? hp : null,
            };
            foreach (var src in sources)
            {
                if (src is null) continue;
                merged.Launches = Math.Max(merged.Launches, src.Launches);
                merged.Seconds = Math.Max(merged.Seconds, src.Seconds);
                if (string.CompareOrdinal(src.Last, merged.Last) > 0) merged.Last = src.Last;
                if (src.First.Length > 0 && (merged.First.Length == 0 || string.CompareOrdinal(src.First, merged.First) < 0)) merged.First = src.First;
                merged.Users.UnionWith(src.Users);
                merged.Days.UnionWith(src.Days);
                if (src.Active) merged.Active = true;
            }
            if (merged.Launches == 0 && appPath is not null && byPath.TryGetValue(appPath, out var bp)) merged.Launches = bp.Launches;
            var nested = appPath is not null && Regex.IsMatch(appPath, @"\.app/.*\.app$", RegexOptions.IgnoreCase);
            apps.Add(new MacApplicationItem
            {
                Id = MacJson.Str(app, "id") ?? $"{name}-{index}",
                Name = name,
                DisplayName = MacJson.Str(app, "displayName") ?? name,
                Version = MacJson.Str(app, "version", "bundleVersion") ?? "Unknown",
                Publisher = MacJson.Str(app, "publisher", "signedBy", "vendor") ?? "Unknown Publisher",
                Category = MacJson.Str(app, "category") ?? "Uncategorized",
                InstallDate = MacJson.Str(app, "installDate", "lastModified"),
                Size = MacJson.Str(app, "size"),
                Path = appPath,
                BundleId = MacJson.Str(app, "bundleId", "bundleIdentifier"),
                Source = MacJson.Str(app, "source"),
                Architecture = MacJson.Str(app, "architecture"),
                Nested = nested,
                SignedBy = MacJson.Str(app, "signedBy"),
                ObtainedFrom = MacJson.Str(app, "obtainedFrom"),
                BuildVersion = MacJson.Str(app, "buildVersion"),
                MinimumSystemVersion = MacJson.Str(app, "minimumSystemVersion"),
                Usage = UsageFrom(merged),
            });
        }

        Applications = apps
            .OrderByDescending(a => a.UsageSeconds)
            .ThenByDescending(a => a.Usage?.LaunchCount ?? 0)
            .ThenBy(a => a.CleanName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A module sent as a one-element array reads as that element.</summary>
    private static JsonElement? Unwrap(JsonElement? raw) =>
        raw is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 1 ? a[0] : raw;

    private static IEnumerable<string> Strings(JsonElement? parent, string path) =>
        MacJson.Arr(parent, path).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0);

    private static string Prefix(string s, int length) => s.Length <= length ? s : s[..length];

    private static MacApplicationUsage? UsageFrom(Agg agg)
    {
        if (agg.Launches <= 0 && agg.Seconds <= 0 && !agg.Active) return null;
        return new MacApplicationUsage
        {
            LaunchCount = agg.Launches, TotalSeconds = agg.Seconds,
            LastUsed = agg.Last.Length == 0 ? null : agg.Last, FirstSeen = agg.First.Length == 0 ? null : agg.First,
            Users = agg.Users.OrderBy(u => u, StringComparer.Ordinal).ToList(), ActiveNow = agg.Active, DaysSeen = agg.Days.Count,
        };
    }

    private sealed class Agg
    {
        public long Launches;
        public double Seconds;
        public string Last = "";
        public string First = "";
        public HashSet<string> Users = [];
        public HashSet<string> Days = [];
        public bool Active;
    }

    /// <summary>History names come as "Safari", "Safari.app", or "helper (Safari)"; normalise to the app name.</summary>
    public static string UsageKey(string? name)
    {
        if (name is null) return "";
        var baseName = name;
        var m = Regex.Match(name, @"\(([^)]+)\)\s*$");
        if (m.Success) baseName = m.Value.Trim('(', ')', ' ');
        return Regex.Replace(baseName, @"\.app$", "", RegexOptions.IgnoreCase).Trim().ToLowerInvariant();
    }

    /// <summary>Table dedupe: the same name and publisher at the same path collapses to the last one listed.</summary>
    public static IReadOnlyList<MacApplicationItem> Deduplicate(IEnumerable<MacApplicationItem> apps)
    {
        var map = new Dictionary<string, MacApplicationItem>();
        var order = new List<string>();
        foreach (var app in apps)
        {
            var key = $"{app.Name}-{app.Publisher}-{app.Path ?? ""}";
            if (!map.ContainsKey(key)) order.Add(key);
            // The Mac app keeps the higher version or the later row, which is always the later row.
            map[key] = app;
        }
        return order.Select(k => map[k]).ToList();
    }

    /// <summary>Relevance-ranked search across name, publisher, signer, bundle id, version, path and users.</summary>
    public static IReadOnlyList<MacApplicationItem> Search(IEnumerable<MacApplicationItem> apps, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return apps.ToList();
        return apps.Select(a => (Item: a, Score: Relevance(a, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.CleanName, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Item)
            .ToList();
    }

    public static int Relevance(MacApplicationItem app, string q)
    {
        var score = 0;
        var name = app.Name.ToLowerInvariant();
        var display = app.DisplayName.ToLowerInvariant();
        if (name == q || display == q) score += 100;
        else if (name.StartsWith(q, StringComparison.Ordinal) || display.StartsWith(q, StringComparison.Ordinal)) score += 50;
        else if (name.Contains(q) || display.Contains(q)) score += 25;
        if (app.Publisher.ToLowerInvariant().Contains(q)) score += 10;
        if ((app.SignedBy ?? "").ToLowerInvariant().Contains(q)) score += 8;
        if ((app.BundleId ?? "").ToLowerInvariant().Contains(q)) score += 6;
        if (app.Version.ToLowerInvariant().Contains(q)) score += 5;
        if ((app.Path ?? "").ToLowerInvariant().Contains(q)) score += 3;
        if (app.Usage?.Users.Any(u => u.ToLowerInvariant().Contains(q)) == true) score += 4;
        return score;
    }

    /// <summary><c>formatDuration</c>: <c>45s</c>, <c>12m</c>, <c>3h 5m</c>, <c>4d 6h</c>.</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "-";
        if (seconds < 60) return $"{(int)Math.Round(seconds, MidpointRounding.AwayFromZero)}s";
        if (seconds < 3600) return $"{(int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero)}m";
        var hours = (int)(seconds / 3600);
        if (hours >= 48) return $"{(int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero)}d {hours % 24}h";
        var mins = (int)Math.Round(seconds % 3600 / 60, MidpointRounding.AwayFromZero);
        return mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
    }

    public static string ShortUser(string user) => user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;

    /// <summary>
    /// A Launch Services category ("public.app-category.developer-tools") as a person
    /// reads it ("Developer Tools"); other values pass through, and none reads as null.
    /// </summary>
    public static string? CategoryLabel(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || category == "Uncategorized") return null;
        const string prefix = "public.app-category.";
        if (!category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return category;
        var words = category[prefix.Length..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? null : string.Join(' ', words.Select(w => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w)));
    }
}
