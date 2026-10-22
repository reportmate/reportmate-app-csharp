using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

public class MacSystemInfoTests
{
    private const string Payload = """
    {
      "operatingSystem": { "name": "macOS", "version": "15.4.1", "majorVersion": 15, "minorVersion": 4, "patchVersion": 1,
        "buildNumber": "24E263", "architecture": "arm64", "platform": "Darwin", "kernelVersion": "24.4.0" },
      "systemDetails": { "bootTime": "2026-10-20T00:22:57Z", "keyboardLayouts": ["Canadian", "U.S."], "locale": "en_US",
        "timeZone": "America/Vancouver", "computerName": "Sample Mac" },
      "uptime": 214159,
      "uptimeString": "2d 11h 29m",
      "environment": { "SHLVL": "1", "PATH": "/usr/bin:/bin:/usr/sbin", "HOSTS_FILE": "127.0.0.1\tlocalhost\n", "_": "/usr/bin/env" },
      "pendingAppleUpdates": [
        { "name": "macOS Sequoia 15.5", "version": "15.5", "buildVersion": "24F74", "restartRequired": 1 },
        { "name": "Safari 19", "version": "19.0", "deferred": true, "deferredUntil": "2026-11-03T00:00:00Z" }
      ],
      "installHistory": [
        { "packageId": "com.example.tool", "packageFilename": "", "version": "1.2", "installTime": "@TOOL@.25" },
        { "packageId": "com.apple.pkg.XProtectPayloads", "packageFilename": "XProtect.pkg", "version": "5300", "installTime": "@APPLE@" },
        { "packageId": "org.example.agent", "version": "3.0", "installTime": "" }
      ],
      "services": [
        { "label": "com.apple.sample.daemon", "path": "/System/Library/LaunchDaemons/com.apple.sample.daemon.plist", "pid": 412,
          "status": "Running", "type": "LaunchDaemon", "source": "Apple", "runAtLoad": 1, "keepAlive": 1, "disabled": 0,
          "plistContent": "{\"Label\":\"com.apple.sample.daemon\",\"RunAtLoad\":true}" },
        { "label": "org.example.helper", "path": "/Library/LaunchAgents/org.example.helper.plist", "pid": null,
          "status": "Stopped", "type": "LaunchAgent", "source": "System", "runAtLoad": 0, "keepAlive": 0, "disabled": 0, "program": "" },
        { "label": "", "name": "org.example.user", "path": "/Users/sample/Library/LaunchAgents/org.example.user.plist",
          "status": "Stopped", "type": "LaunchAgent", "source": "User", "disabled": 1 }
      ],
      "scheduledTasks": [
        { "name": "org.example.task", "path": "", "status": "Enabled", "type": "LaunchDaemon", "source": "System", "runAtLoad": 0 },
        { "name": "com.apple.loaded", "path": "", "status": "Loaded", "type": "LaunchAgent", "source": "Apple" }
      ],
      "loginItems": [ { "name": "Sample App", "path": "/Applications/Sample App.app", "type": "Application", "enabled": 1 }, { "name": "Bare" } ],
      "systemExtensions": [
        { "identifier": "org.example.wdav.epsext", "state": "activated_enabled", "category": "com.apple.system_extension.endpoint_security",
          "extensionCategory": "", "appName": "", "bundlePath": "", "type": "EndpointSecurity", "teamId": "SampleTeam" },
        { "identifier": "com.example.SampleApp.QuickLookPreview", "state": "enabled", "appName": "", "bundlePath": "/Applications/Sample App.app/Contents/PlugIns/Preview.appex" },
        { "identifier": "com.example.dropbox.finderext", "state": "waiting for user" },
        { "identifier": "com.example.superWidgetsPro.share", "state": "disabled" }
      ],
      "kernelExtensions": [],
      "privilegedHelperTools": [
        { "name": "org.example.helper", "path": "/Library/PrivilegedHelperTools/org.example.helper", "bundleIdentifier": "org.example.helper",
          "teamId": "SampleTeam", "signed": 1, "size": 221808, "modifiedDate": "2024-12-04T07:31:59Z" }
      ]
    }
    """;

    // Receipt times are Unix seconds; built here so no long digit run sits in the fixture.
    private static readonly DateTime ToolInstalled = new(2026, 10, 19, 8, 7, 48, DateTimeKind.Utc);
    private static readonly DateTime XProtectInstalled = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static string Seconds(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static MacSystemInfo Read(string? json = null) =>
        MacSystemInfo.From(JsonDocument.Parse(json ?? Payload.Replace("@TOOL@", Seconds(ToolInstalled)).Replace("@APPLE@", Seconds(XProtectInstalled))).RootElement)
            ?? throw new InvalidOperationException("no system info");

    [Fact]
    public void ReadsOperatingSystemWithSystemDetailsFallbacks()
    {
        var os = Read().OperatingSystem;
        Assert.Equal("macOS", os.Name);
        Assert.Equal("15.4.1", os.DisplayVersion);
        Assert.Equal("24E263", os.Build);
        Assert.Equal("arm64", os.Architecture);
        Assert.Equal("en_US", os.Locale);
        Assert.Equal("America/Vancouver", os.TimeZone);
        Assert.Equal("Canadian, U.S.", os.KeyboardLayout);
        Assert.Equal("Sequoia", os.MarketingName);
        Assert.Equal("15.4.1", os.FormattedVersion);
    }

    [Theory]
    [InlineData("26.1", "Tahoe")]
    [InlineData("27.0.1", "macOS 27")]
    [InlineData("10.14.6", "Mojave")]
    [InlineData("10.11", "macOS")]
    [InlineData(null, "macOS")]
    public void NamesTheRelease(string? version, string expected) => Assert.Equal(expected, MacOsNames.MarketingName(version));

    [Fact]
    public void PadsTheVersionAndPrefersADisplayVersion()
    {
        Assert.Equal("14.0.0", MacOsNames.FormattedVersion("14"));
        Assert.Equal("Unknown", MacOsNames.FormattedVersion(null));
        var info = Read("""{ "operatingSystem": { "version": "14.2", "displayVersion": "14.2.9", "build": "23C1" } }""");
        Assert.Equal("14.2.9", info.OperatingSystem.DisplayVersion);
        Assert.Equal("23C1", info.OperatingSystem.Build);
    }

    [Fact]
    public void ReadsUptimeAndBootTime()
    {
        var info = Read();
        Assert.Equal("2d 11h 29m", info.Uptime);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 22, 57, DateTimeKind.Utc), info.BootTime);
    }

    [Fact]
    public void ClassifiesPendingAppleUpdates()
    {
        var info = Read();
        Assert.Equal(2, info.PendingUpdates.Count);
        var os = info.PendingUpdates[0];
        Assert.Equal("Restart Required", os.StatusLabel);
        Assert.Equal("24F74", os.Detail);
        Assert.Null(os.StatusNote);
        var safari = info.PendingUpdates[1];
        Assert.Equal("Deferred", safari.StatusLabel);
        Assert.StartsWith("Available Nov ", safari.StatusNote);
        Assert.Single(info.ActiveUpdates);
        Assert.Single(info.DeferredUpdates);
    }

    [Fact]
    public void ReadsInstallHistoryAndPicksAppleReceipts()
    {
        var info = Read();
        Assert.Equal(3, info.InstallHistory.Count);
        var first = info.InstallHistory[0];
        Assert.Null(first.PackageFilename);
        Assert.Equal(ToolInstalled.AddMilliseconds(250), first.InstallDate);
        Assert.Equal(["com.apple.pkg.XProtectPayloads"], info.RecentSystemUpdates.Select(i => i.PackageId));
        Assert.Equal(["com.apple.pkg.XProtectPayloads", "com.example.tool", "org.example.agent"], info.InstallHistoryNewestFirst.Select(i => i.PackageId));
    }

    [Fact]
    public void ListsTasksThenServicesAsBackgroundActivity()
    {
        var info = Read();
        Assert.Equal(5, info.BackgroundItemCount);
        Assert.Equal(1, info.RunningServiceCount);
        Assert.Equal(["org.example.task", "com.apple.loaded", "com.apple.sample.daemon", "org.example.helper", "org.example.user"],
            info.LaunchdItems.Select(i => i.DisplayName));
    }

    [Fact]
    public void ClassifiesLaunchdItems()
    {
        var items = Read().LaunchdItems.ToDictionary(i => i.Name);

        var task = items["org.example.task"];
        Assert.Equal(LaunchdScope.System, task.Scope);
        Assert.Equal(LaunchdKind.Daemon, task.Kind);
        Assert.Equal(LaunchdStatus.Running, task.LaunchdStatus);
        Assert.False(task.IsRunning);

        Assert.Equal(LaunchdScope.Apple, items["com.apple.loaded"].Scope);
        Assert.Equal(LaunchdKind.Agent, items["com.apple.loaded"].Kind);

        var daemon = items["com.apple.sample.daemon"];
        Assert.True(daemon.RunAtLoad && daemon.KeepAlive && daemon.IsRunning);
        Assert.Equal(412, daemon.Pid);
        Assert.Contains("\n", daemon.PlistPretty);

        var helper = items["org.example.helper"];
        Assert.Equal(LaunchdStatus.Stopped, helper.LaunchdStatus);
        Assert.Equal(LaunchdKind.Agent, helper.Kind);
        Assert.Null(helper.Program);
        Assert.Null(helper.PlistPretty);

        var user = items["org.example.user"];
        Assert.Null(user.Label);
        Assert.Equal(LaunchdScope.User, user.Scope);
        Assert.Equal(LaunchdStatus.Disabled, user.LaunchdStatus);
    }

    [Fact]
    public void FallsBackToPidForStatus()
    {
        var item = MacLaunchdItem.From(JsonDocument.Parse("""{ "label": "org.example.x", "pid": "77" }""").RootElement);
        Assert.Equal("Running", item.Status);
        var stopped = MacLaunchdItem.From(JsonDocument.Parse("""{ "label": "org.example.y" }""").RootElement);
        Assert.Equal("Stopped", stopped.Status);
    }

    [Fact]
    public void ReadsLoginItemsWithDefaults()
    {
        var items = Read().LoginItems;
        Assert.Equal("Application", items[0].Type);
        Assert.True(items[0].Enabled);
        Assert.Equal("LoginItem", items[1].Type);
        Assert.True(items[1].Enabled);
        Assert.Null(items[1].Path);
    }

    [Fact]
    public void GroupsExtensionsAsSystemSettingsDoes()
    {
        var ext = Read().Extensions;
        Assert.Equal(("Enabled", ExtensionStatus.Enabled), ext[0].NormalizedState);
        Assert.Equal("Endpoint Security Extensions", ext[0].ResolvedCategory);
        Assert.Equal("Wdav", ext[0].ResolvedAppName);

        Assert.Equal("Quick Look", ext[1].ResolvedCategory);
        Assert.Equal("Sample App", ext[1].ResolvedAppName);

        Assert.Equal(("Waiting", ExtensionStatus.Waiting), ext[2].NormalizedState);
        Assert.Equal("Finder", ext[2].ResolvedCategory);
        Assert.Equal("Dropbox", ext[2].ResolvedAppName);

        Assert.Equal(("Disabled", ExtensionStatus.Disabled), ext[3].NormalizedState);
        Assert.Equal("Sharing", ext[3].ResolvedCategory);
        Assert.Equal("Super Widgets Pro", ext[3].ResolvedAppName);
    }

    [Fact]
    public void ReadsHelpersAndKexts()
    {
        var info = Read();
        Assert.Empty(info.KernelExtensions);
        var helper = Assert.Single(info.HelperTools);
        Assert.True(helper.Signed);
        Assert.Equal("SampleTeam", helper.TeamId);
        Assert.Equal("org.example.helper", helper.BundleIdentifier);

        var kext = MacKernelExtension.From(JsonDocument.Parse("""{ "name": "org.example.kext", "size": 2048 }""").RootElement);
        Assert.True(kext.Loaded);
        Assert.Equal("2.0 KB", kext.SizeText);
    }

    [Fact]
    public void OrdersEnvironmentWithPathAndHostsFirst()
    {
        var env = Read().Environment;
        Assert.Equal(["PATH", "HOSTS_FILE", "SHLVL", "_"], env.Select(e => e.Name));
        Assert.Equal(["/usr/bin", "/bin", "/usr/sbin"], env[0].PathEntries);
        Assert.True(env[1].IsHostsFile);
        Assert.EndsWith("\n", env[1].Value);
    }

    [Fact]
    public void ReadsEnvironmentRows()
    {
        var info = Read("""{ "environment": [ { "name": "PATH", "value": "C:\\a;C:\\b" }, { "key": "HOME", "value": "/home/sample" } ] }""");
        Assert.Equal(["PATH", "HOME"], info.Environment.Select(e => e.Name));
        Assert.Equal(2, info.Environment[0].PathEntries.Count);
    }

    [Fact]
    public void UnwrapsASingleElementArrayAndToleratesSnakeCase()
    {
        var info = Read("""[ { "operating_system": { "version": "14.1", "build_number": "23B74" }, "uptime_string": "1h", "pending_apple_updates": [] } ]""");
        Assert.True(info.HasData);
        Assert.Equal("23B74", info.OperatingSystem.Build);
        Assert.Equal("Sonoma", info.OperatingSystem.MarketingName);
        Assert.Equal("1h", info.Uptime);
        Assert.Empty(info.LaunchdItems);
    }

    [Fact]
    public void ReturnsNullForANonObject()
    {
        Assert.Null(MacSystemInfo.From(JsonDocument.Parse("\"text\"").RootElement));
        Assert.Null(MacSystemInfo.From(null));
    }
}
