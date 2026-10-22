using System;
using System.Linq;
using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// A fleet Mac's installs module (Munki) read the way the Mac app and the web read it,
/// so the Windows app's Mac Installs tab shows the same states, counts and last-run
/// markers. The payloads are shaped like a real Mac client's upload, with invented values.
/// </summary>
public class MacInstallsInfoTests
{
    private static MacInstallsInfo Read(string json) => MacInstallsInfo.Read(JsonDocument.Parse(json).RootElement);

    /// <summary>The current Mac client's shape: items, snake_case sessions, blank strings for absent fields.</summary>
    private const string Modern = """
    {
      "munki": {
        "version": "2026.10.16.0001",
        "manifestName": "Assigned/Staff/Sample",
        "clientIdentifier": "Assigned/Staff/Sample",
        "softwareRepoURL": "https://munki.example.org/repo",
        "catalogs": ["production", "testing"],
        "endTime": "2026-10-22T15:01:12.000Z",
        "startTime": "2026-10-22T14:59:22.000Z",
        "errorItems": [],
        "warningItems": [{ "name": "", "message": "Manifest has an unknown key" }],
        "sessions": [
          { "session_id": "2026-10-22-0759", "status": "completed", "run_type": "auto", "duration_seconds": 109,
            "start_time": "2026-10-22T07:59:22.545-07:00", "end_time": "2026-10-22T08:01:12.363-07:00",
            "summary": { "total_actions": 2, "failures": 0 }, "error_items": [], "warning_items": [] },
          { "session_id": "2026-10-22-0658", "status": "completed", "run_type": "manual", "duration_seconds": 39,
            "summary": { "total_actions": 0 } }
        ],
        "items": [
          { "id": "outlook", "name": "Outlook", "displayName": "Sample Mail", "itemName": "Outlook", "category": "Productivity",
            "developer": "Example", "currentStatus": "Installed", "status": "installed", "version": "", "installedVersion": "16.1",
            "latestVersion": "16.1", "installedSize": 2048, "lastError": "", "lastWarning": "", "lastSeenInSession": "",
            "lastAttemptTime": "2026-10-22T08:01:14.108-07:00", "hasInstallLoop": 0 },
          { "id": "osquery", "name": "osquery", "displayName": "osquery", "category": "Management",
            "currentStatus": "Installed", "status": "install_succeeded", "version": "", "installedVersion": "5.24.0",
            "lastSeenInSession": "2026-10-22-0759", "lastAttemptTime": "2026-10-22T08:01:14.108-07:00", "lastError": "" },
          { "id": "broken", "name": "Broken", "displayName": "Broken App", "category": "Utilities",
            "currentStatus": "Error", "status": "install_failed", "version": "2.0", "installedVersion": "1.0",
            "lastSeenInSession": "2026-10-22-0759", "lastError": "Install of Broken-2.0 failed\ninstaller: exit 1",
            "recentAttempts": [{ "status": "failed", "timestamp": "2026-10-22T08:00:30-07:00" }] },
          { "id": "looping", "name": "Looping", "currentStatus": "Installed", "status": "installed",
            "version": "3.0", "hasInstallLoop": "1", "lastSeenInSession": "" },
          { "id": "waiting", "name": "Waiting", "currentStatus": "Pending", "status": "pending_install",
            "version": "4.0", "pendingReason": "Waiting for the user to log out", "lastSeenInSession": "" }
        ]
      },
      "cacheStatus": { "cacheSizeMb": 512.5 },
      "runLog": "[2026-10-22 07:59:22] INFO Session started"
    }
    """;

    [Fact]
    public void Missing_or_empty_module_has_no_data()
    {
        Assert.False(MacInstallsInfo.Read(null).HasData);
        Assert.False(Read("{}").HasData);
        Assert.False(Read("[]").HasData);
    }

    [Fact]
    public void Reads_the_munki_configuration()
    {
        var info = Read(Modern);
        Assert.True(info.HasData);
        Assert.True(info.IsMunki);
        Assert.Equal("Munki", info.SystemName);
        var c = info.Config!;
        Assert.Equal("2026.10.16.0001", c.Version);
        Assert.Equal("Assigned/Staff/Sample", c.Manifest);
        Assert.Equal("https://munki.example.org/repo", c.SoftwareRepoUrl);
        Assert.Equal("production, testing", c.Catalogs);
        Assert.Equal(new DateTime(2026, 10, 22, 15, 1, 12, DateTimeKind.Utc), c.LastRun);
        Assert.Equal(info.Config!.LastRun, info.LastRunTimestamp);
        Assert.Equal(512.5, info.CacheSizeMb);
    }

    [Fact]
    public void Run_type_and_duration_come_from_the_latest_session_that_did_something()
    {
        var c = Read(Modern).Config!;
        Assert.Equal("auto", c.RunType);
        Assert.Equal(109, c.DurationSeconds);
        Assert.Equal("109s", c.Duration);
    }

    [Fact]
    public void Blank_version_falls_back_to_the_installed_version()
    {
        var outlook = Read(Modern).Packages.Single(p => p.Id == "outlook");
        Assert.Equal("16.1", outlook.Version);
        Assert.Equal("Sample Mail", outlook.DisplayName);
        Assert.Equal("Outlook", outlook.Name);
        Assert.Equal("Productivity", outlook.Category);
    }

    [Fact]
    public void Items_take_the_ladder_states()
    {
        var p = Read(Modern).Packages.ToDictionary(x => x.Id);
        Assert.Equal(MacInstallState.Installed, p["outlook"].Status);
        Assert.Equal(MacInstallState.Installed, p["osquery"].Status);
        Assert.Equal(MacInstallState.Error, p["broken"].Status);
        // An item that reinstalls every run is not healthy, however it reports.
        Assert.Equal(MacInstallState.Warning, p["looping"].Status);
        Assert.Equal(MacInstallState.Pending, p["waiting"].Status);
    }

    [Fact]
    public void Only_items_the_latest_run_touched_get_a_processed_date()
    {
        var info = Read(Modern);
        var p = info.Packages.ToDictionary(x => x.Id);
        Assert.Null(p["outlook"].LastUpdate);
        Assert.Equal(new DateTime(2026, 10, 22, 15, 1, 14, 108, DateTimeKind.Utc), p["osquery"].LastUpdate);
        Assert.True(p["broken"].InLastRun);
        Assert.Equal(2, info.LastRunCount);
        Assert.Equal(1, info.Count(MacInstallState.Installed, lastRunOnly: true));
    }

    [Fact]
    public void Last_error_splits_into_message_and_details_with_the_failed_attempt_time()
    {
        var broken = Read(Modern).Packages.Single(p => p.Id == "broken");
        var e = Assert.Single(broken.Errors);
        Assert.Equal("ERROR", e.Code);
        Assert.Equal("Install of Broken-2.0 failed", e.Text);
        Assert.Equal("installer: exit 1", e.Details);
        Assert.Equal(new DateTime(2026, 10, 22, 15, 0, 30, DateTimeKind.Utc), e.Timestamp);
        Assert.Equal("1.0", broken.InstalledVersion);
        Assert.Equal("2.0", broken.Version);
        Assert.True(broken.HasExpandableContent);
    }

    [Fact]
    public void Pending_reason_makes_a_row_expandable()
    {
        var waiting = Read(Modern).Packages.Single(p => p.Id == "waiting");
        Assert.True(waiting.HasExpandableContent);
        Assert.Equal("Waiting for the user to log out", waiting.PendingReason);
    }

    [Fact]
    public void Item_size_falls_back_to_munki_installed_size_in_kilobytes()
    {
        var outlook = Read(Modern).Packages.Single(p => p.Id == "outlook");
        Assert.Equal(2048L * 1024, outlook.ItemSizeBytes);
        Assert.Equal("2.0 MB", outlook.FormattedItemSize);
    }

    [Fact]
    public void Run_level_messages_without_a_name_belong_to_the_run_and_count_as_failed()
    {
        var info = Read(Modern);
        var w = Assert.Single(info.RunWarnings);
        Assert.Equal("Manifest has an unknown key", w.Text);
        Assert.Empty(info.RunErrors);
        // One error, one warning item, one run warning.
        Assert.Equal(3, info.Failed);
    }

    [Fact]
    public void A_clean_latest_session_has_no_system_problems()
    {
        Assert.True(Read(Modern).SystemProblems.IsEmpty);
    }

    [Fact]
    public void A_failed_session_with_no_items_is_reported_with_its_problems()
    {
        var info = Read("""
        { "munki": {
            "sessions": [ { "session_id": "2026-10-22-0900", "status": "failed", "end_time": "2026-10-22T09:00:00Z",
              "error_items": [ { "message": "Could not retrieve manifest Assigned/Staff/Sample" } ] } ],
            "items": [] } }
        """);
        var sp = info.SystemProblems;
        Assert.True(sp.FailedWithoutItems);
        Assert.True(sp.IsError);
        Assert.Equal("2026-10-22-0900", sp.SessionId);
        Assert.Equal("Could not retrieve manifest Assigned/Staff/Sample", Assert.Single(sp.Problems).Message);
        Assert.Empty(info.Packages);
        Assert.True(info.HasManagementSystem);
    }

    [Fact]
    public void Legacy_munki_attributes_run_messages_to_items_and_problem_installs()
    {
        var info = Read("""
        { "munki": {
            "version": "6.6.0", "endTime": "2026-10-22T09:00:00Z", "startTime": "2026-10-22T08:58:00Z",
            "items": [
              { "name": "Firefox", "status": "installed", "version": "130.0" },
              { "name": "Zoom", "status": "install_failed", "version": "6.0", "endTime": "2026-10-22T09:00:00Z" }
            ],
            "errors": "Install of Zoom-6.0 failed with return code 1;Could not install Slack.",
            "warnings": "Firefox is not signed;Catalog cache is stale",
            "problemInstalls": "Chrome" } }
        """);
        var p = info.Packages.ToDictionary(x => x.Name);
        Assert.Equal(MacInstallState.Error, p["Zoom"].Status);
        Assert.Contains(p["Zoom"].Errors, e => e.Code == "MUNKI_ERROR");
        // An item a legacy processed status marks gets the run's time as its marker.
        Assert.True(p["Zoom"].InLastRun);
        Assert.Equal(MacInstallState.Warning, p["Firefox"].Status);
        Assert.Equal(MacInstallState.Error, p["Slack"].Status);
        Assert.Equal(MacInstallState.Warning, p["Chrome"].Status);
        Assert.Contains(p["Chrome"].Warnings, w => w.Code == "MUNKI_PROBLEM_INSTALL");
        Assert.Equal("Catalog cache is stale", Assert.Single(info.RunWarnings).Text);
        Assert.Equal("Auto", info.Config!.RunType);
        Assert.Equal("Not configured", info.Config!.Catalogs);
    }

    [Fact]
    public void Legacy_errors_mark_a_failed_run_with_no_items()
    {
        var sp = Read("""{ "munki": { "lastRunSuccess": "0", "errors": "Could not retrieve manifest" } }""").SystemProblems;
        Assert.True(sp.FailedWithoutItems);
        Assert.Equal("Could not retrieve manifest", Assert.Single(sp.Problems).Message);
    }

    [Fact]
    public void Pending_packages_list_when_no_items_are_sent()
    {
        var info = Read("""{ "munki": { "endTime": "2026-10-22T09:00:00Z", "pendingPackages": ["Sample Tool", "Other"] } }""");
        Assert.Equal(2, info.TotalPackages);
        Assert.All(info.Packages, x => Assert.Equal(MacInstallState.Pending, x.Status));
        Assert.Equal("sample-tool", info.Packages[0].Id);
    }

    [Fact]
    public void Installer_blocks_group_into_one_message()
    {
        var blocks = MacInstallsInfo.ParseInstallerBlocks(
            "Install of Sample-1.0 failed;----------;installer:phase:Preparing;installer: The install failed (code 1);----------;Trailing note");
        Assert.Equal(3, blocks.Count);
        Assert.Equal("Install of Sample-1.0 failed", blocks[0].Message);
        Assert.Equal("installer: The install failed (code 1)", blocks[1].Message);
        Assert.Contains("installer:phase:Preparing", blocks[1].Details);
        Assert.Null(blocks[2].Details);
    }

    [Theory]
    [InlineData("Could not process item Sample for optional install", "Sample")]
    [InlineData("Download of Sample Tool failed", "Sample Tool")]
    [InlineData("Catalog cache is stale", null)]
    public void Item_names_come_out_of_munki_messages(string message, string? expected) =>
        Assert.Equal(expected, MacInstallsInfo.ItemNameFromMunkiMessage(message));

    [Theory]
    [InlineData("install_succeeded", MacInstallState.Installed)]
    [InlineData("Update Available", MacInstallState.Pending)]
    [InlineData("not_installed", MacInstallState.Removed)]
    [InlineData("failed", MacInstallState.Error)]
    [InlineData(null, MacInstallState.Pending)]
    public void Standardizes_status_spellings(string? raw, MacInstallState expected) =>
        Assert.Equal(expected, MacInstallsInfo.Standardize(raw));

    [Fact]
    public void Session_ids_are_told_from_timestamps()
    {
        Assert.True(MacInstallsInfo.IsSessionId("2026-10-22-0759"));
        Assert.False(MacInstallsInfo.IsSessionId("2026-10-22T07:59:00Z"));
    }
}
