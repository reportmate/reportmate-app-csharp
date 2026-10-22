using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

public class MacApplicationsInfoTests
{
    // Shaped like a Mac client's applications module: installedApplications from
    // Launch Services, applicationUsage from the SQLite watcher (isActive as 0/1),
    // and the per-day history keyed by app name.
    private const string Sample = """
    {
      "installedApplications": [
        { "name": "Sample App.app", "path": "/Applications/Sample App.app", "bundleIdentifier": "com.example.sample",
          "version": "2.1", "buildVersion": "210", "category": "public.app-category.developer-tools", "source": "Local",
          "minimumSystemVersion": "14.0" },
        { "name": "Notes.app", "path": "/System/Applications/Notes.app", "bundleIdentifier": "com.example.notes",
          "version": "4.0", "category": "", "source": "Apple" },
        { "name": "Sample Helper.app", "path": "/Applications/Sample App.app/Contents/Helpers/Sample Helper.app",
          "bundleIdentifier": "com.example.sample.helper", "version": "2.1", "source": "Local" },
        { "name": "Idle Tool.app", "path": "/Users/sample/Applications/Idle Tool.app", "version": "", "source": "User" }
      ],
      "applicationUsage": {
        "captureMethod": "SQLiteWatcher",
        "activeSessions": [
          { "name": "Sample App", "path": "/Applications/Sample App.app", "durationSeconds": 120, "isActive": 1,
            "startTime": "2026-01-02T10:00:00Z", "user": "alex" },
          { "name": "Sample App", "path": "/Applications/Sample App.app", "durationSeconds": 30, "isActive": 0,
            "startTime": "2026-01-02T08:00:00Z", "user": "root" },
          { "name": "Notes", "path": "/System/Applications/Notes.app", "durationSeconds": "45", "isActive": "0",
            "startTime": "2026-01-01T09:00:00Z", "user": "alex" }
        ]
      },
      "dailyUsageHistory": [
        { "appName": "Sample App", "date": "2026-01-01", "launches": 2, "totalSeconds": 3600, "users": ["alex"] },
        { "appName": "Sample App Helper (Sample App)", "date": "2026-01-02", "launches": 1, "totalSeconds": 600, "users": ["sam"] },
        { "appName": "Notes.app", "date": "2026-01-01", "launches": 0, "totalSeconds": 20, "users": [] }
      ]
    }
    """;

    private static MacApplicationsInfo Read(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void ReadsTheInventoryAndCountsHelpersApart()
    {
        var info = Read(Sample);
        Assert.True(info.HasData);
        Assert.Equal(4, info.Applications.Count);
        Assert.Equal(3, info.BaseApps.Count);
        Assert.Equal(1, info.NestedCount);
        Assert.True(info.Applications.Single(a => a.Name == "Sample Helper.app").Nested);
    }

    [Fact]
    public void MapsTheMacFieldsOntoTheItem()
    {
        var app = Read(Sample).Applications.Single(a => a.Name == "Sample App.app");
        Assert.Equal("Sample App", app.CleanName);
        Assert.Equal("com.example.sample", app.BundleId);
        Assert.Equal("2.1", app.Version);
        Assert.Equal("Unknown Publisher", app.Publisher);
        Assert.Equal("com.example.sample", app.SecondaryLine);
        Assert.Equal("Developer Tools", app.CategoryLabel);
        Assert.Equal("210", app.BuildVersion);
        Assert.Equal("14.0", app.MinimumSystemVersion);

        var idle = Read(Sample).Applications.Single(a => a.Name == "Idle Tool.app");
        Assert.Equal("Unknown", idle.Version);
        Assert.Equal("/Users/sample/Applications/Idle Tool.app", idle.SecondaryLine);
        Assert.Null(idle.Usage);
    }

    [Fact]
    public void MergesHistoryAndSessionsLongestViewWins()
    {
        var info = Read(Sample);
        var app = info.Applications.Single(a => a.Name == "Sample App.app");
        var u = app.Usage!;
        // History by name: 3 launches and 4200 s (the helper row folds into its parent name);
        // sessions by path: 2 launches and 150 s. Each figure takes the larger.
        Assert.Equal(3, u.LaunchCount);
        Assert.Equal(4200, u.TotalSeconds);
        Assert.True(u.ActiveNow);
        Assert.Equal(["alex", "root", "sam"], u.Users);
        Assert.Equal("2026-01-02T10:00:00Z", u.LastUsed);
        Assert.Equal("2026-01-01T00:00:00Z", u.FirstSeen);
        Assert.Equal(2, u.DaysSeen);

        var notes = info.Applications.Single(a => a.Name == "Notes.app").Usage!;
        Assert.Equal(1, notes.LaunchCount);
        Assert.Equal(45, notes.TotalSeconds);
        Assert.False(notes.ActiveNow);
    }

    [Fact]
    public void SummarisesUsageAndSortsMostUsedFirst()
    {
        var info = Read(Sample);
        Assert.Equal(3, info.ActiveSessionCount);
        Assert.Equal(2, info.HistoryDays);
        Assert.Equal(2, info.UsedApps.Count);
        Assert.Single(info.RunningApps);
        Assert.Single(info.UnusedApps);
        Assert.Equal(3, info.DistinctUsers);
        Assert.Equal(4245, info.TotalUsageSeconds);
        Assert.Equal(["Sample App", "Notes", "Idle Tool", "Sample Helper"], info.Applications.Select(a => a.CleanName));
        Assert.Equal(2, info.TopApps.Count);
    }

    [Fact]
    public void FiltersByUsageAndHelpers()
    {
        var info = Read(Sample);
        Assert.Equal(3, info.Filtered("all", hideNested: true).Count);
        Assert.Equal(4, info.Filtered("all", hideNested: false).Count);
        Assert.Equal("Sample App", info.Filtered("active", hideNested: true).Single().CleanName);
        Assert.Equal("Idle Tool", info.Filtered("unused", hideNested: true).Single().CleanName);
    }

    [Fact]
    public void SearchRanksNameMatchesAboveOtherFields()
    {
        var apps = Read(Sample).Applications;
        var hits = MacApplicationsInfo.Search(apps, "sample");
        Assert.Equal("Sample App", hits[0].CleanName);
        Assert.Contains(hits, a => a.CleanName == "Idle Tool");
        Assert.Equal("Notes", MacApplicationsInfo.Search(apps, "com.example.notes").Single().CleanName);
        Assert.Equal("Notes", MacApplicationsInfo.Search(apps, "notes").First().CleanName);
        Assert.Empty(MacApplicationsInfo.Search(apps, "nothing-like-this"));
    }

    [Fact]
    public void ReadsBareNamesAndSingletonModules()
    {
        var info = Read("""[{ "applications": ["Sample App.app", "Other"], "dailyUsageHistory": [{ "appName": "Other", "date": "2026-01-01", "launches": "3", "totalSeconds": "90" }] }]""");
        Assert.Equal(2, info.Applications.Count);
        var other = info.Applications[0];
        Assert.Equal("Other", other.Name);
        Assert.Equal("Application", other.Category);
        Assert.Equal(3, other.Usage!.LaunchCount);
        Assert.Equal("Sample App", info.Applications[1].Name);
    }

    [Fact]
    public void EmptyModuleHasNoData()
    {
        Assert.False(Read("{}").HasData);
        Assert.False(Read("""{ "installedApplications": [] }""").HasData);
    }

    [Fact]
    public void DeduplicatesSameNamePublisherAndPath()
    {
        var info = Read("""
        { "installedApplications": [
          { "name": "Dup", "path": "/Applications/Dup.app", "version": "1.0" },
          { "name": "Dup", "path": "/Applications/Dup.app", "version": "2.0" },
          { "name": "Dup", "path": "/Applications/Other/Dup.app", "version": "1.0" } ] }
        """);
        var rows = MacApplicationsInfo.Deduplicate(info.Applications);
        Assert.Equal(2, rows.Count);
        Assert.Equal("2.0", rows.Single(r => r.Path == "/Applications/Dup.app").Version);
    }

    [Theory]
    [InlineData("Safari", "safari")]
    [InlineData("Safari.app", "safari")]
    [InlineData("Web Content (Safari)", "safari")]
    [InlineData(null, "")]
    public void UsageKeyNormalisesHistoryNames(string? name, string expected) =>
        Assert.Equal(expected, MacApplicationsInfo.UsageKey(name));

    [Theory]
    [InlineData(0, "-")]
    [InlineData(45, "45s")]
    [InlineData(720, "12m")]
    [InlineData(3 * 3600 + 300, "3h 5m")]
    [InlineData(7200, "2h")]
    [InlineData(4 * 86400 + 6 * 3600, "4d 6h")]
    public void FormatsDurationsAsTheMacAppDoes(double seconds, string expected) =>
        Assert.Equal(expected, MacApplicationsInfo.FormatDuration(seconds));

    [Theory]
    [InlineData("public.app-category.utilities", "Utilities")]
    [InlineData("public.app-category.social-networking", "Social Networking")]
    [InlineData("", null)]
    [InlineData("Uncategorized", null)]
    [InlineData("Games", "Games")]
    public void LabelsLaunchServicesCategories(string category, string? expected) =>
        Assert.Equal(expected, MacApplicationsInfo.CategoryLabel(category));
}
