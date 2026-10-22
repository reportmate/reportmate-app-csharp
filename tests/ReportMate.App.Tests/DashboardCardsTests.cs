using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Insight cards 5 to 12, and the report filters their links open. A card's count
/// and the rows its link opens are worked out by the same rule, so these check both.
/// </summary>
public class DashboardCardsTests
{
    private static List<JsonElement> Rows(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();

    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    private static List<string> Serials(string module, string key, string value, List<JsonElement> rows) =>
        ReportFilters.Apply(module, key, value, rows, rows)!.Select(r => r.GetProperty("serialNumber").GetString()!).ToList();

    // ── Install health and software risk ────────────────────────────────

    private static readonly List<JsonElement> InstallRows = Rows("""
        [{"serialNumber":"SAMPLE1","itemName":"Chrome","currentStatus":"Error","installedVersion":"1","latestVersion":"2","platform":"macOS"},
         {"serialNumber":"SAMPLE2","itemName":"Chrome","currentStatus":"install_failed","installedVersion":"2","latestVersion":"2","platform":"Windows"},
         {"serialNumber":"SAMPLE2","itemName":"Zoom","currentStatus":"Warning","installedVersion":"5","latestVersion":"6","platform":"Windows"},
         {"serialNumber":"SAMPLE3","itemName":"Zoom","currentStatus":"installed","installedVersion":"5","latestVersion":"6","platform":"macOS"},
         {"serialNumber":"SAMPLE3","itemName":"Office","currentStatus":"pending_install","installedVersion":"","latestVersion":"16","platform":"macOS"}]
        """);

    [Fact]
    public void TopFailingItemsCountDistinctDevices()
    {
        var items = InstallRows.Select(Insights.ToInstallItem).OfType<Insights.InstallItem>().ToList();
        Assert.Equal([("Chrome", 2)], Insights.TopFailing(items));
        // Behind needs both versions known: a pending install is not behind.
        Assert.Equal([("Zoom", 2), ("Chrome", 1)], Insights.TopBehind(items));
    }

    [Fact]
    public void TheInstallsFilterReadsErrorsAndWarningsAsClasses()
    {
        Assert.Equal(["SAMPLE1", "SAMPLE2"], Serials("installs", "filter", "errors", InstallRows));
        Assert.Equal(["SAMPLE2"], Serials("installs", "filter", "warnings", InstallRows));
        Assert.Equal(["SAMPLE2", "SAMPLE3", "SAMPLE3"], Serials("installs", "item", "zoom,office", InstallRows));
    }

    [Fact]
    public void StaleAgentsComeFromTheFiltersEndpoint()
    {
        var filters = Doc("""
            {"staleAgentThresholdDays":7,"staleAgents":[
              {"serialNumber":"SAMPLE1","platform":"macOS","staleDays":12},
              {"serialNumber":"SAMPLE2","platform":"Windows","staleDays":9}]}
            """);
        var all = Insights.StaleAgents(filters);
        Assert.Equal(2, all.Count);
        Assert.Equal(7, all.ThresholdDays);
        var macs = Insights.StaleAgents(filters, p => p.StartsWith("mac", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["SAMPLE1"], macs.Serials);
    }

    // ── Check-in gaps ────────────────────────────────────────────────────

    [Fact]
    public void CheckInsReadTheCountsAndReasonsWorstFirst()
    {
        var c = Insights.CheckIns(Doc("""
            {"counts":{"rejected":5,"retried":40,"accepted":3},
             "summary":[{"reason":"invalid_passphrase","count":2,"devices":1},{"reason":"empty_serial","count":3,"devices":3}]}
            """));
        Assert.Equal((5, 40, 3), (c.Rejected, c.Retried, c.Accepted));
        Assert.Equal("empty_serial", c.Reasons[0].Reason);
        Assert.Equal(3, c.Reasons[0].Devices);
    }

    // ── OS currency ──────────────────────────────────────────────────────

    private static readonly List<JsonElement> SystemRows = Rows("""
        [{"serialNumber":"SAMPLE1","platform":"macOS","operatingSystem":"macOS","osVersion":"15.6.1"},
         {"serialNumber":"SAMPLE2","platform":"macOS","operatingSystem":"macOS","osVersion":"15.5"},
         {"serialNumber":"SAMPLE3","platform":"macOS","operatingSystem":"macOS","osVersion":"12.7.6"},
         {"serialNumber":"SAMPLE4","platform":"Windows","operatingSystem":"Windows 11 Enterprise","osVersion":"10.0.26100","buildNumber":"26100.4652"},
         {"serialNumber":"SAMPLE5","platform":"Windows","operatingSystem":"Windows 11 Enterprise","osVersion":"10.0.26100","buildNumber":"26100.4061"},
         {"serialNumber":"SAMPLE6","platform":"Windows","operatingSystem":"Windows 10 Pro","osVersion":"10.0.19045","buildNumber":"19045.6093"}]
        """);

    [Fact]
    public void OsCurrencyComparesEachDeviceWithTheHighestPatchOfItsVersion()
    {
        var c = Insights.OsCurrency(SystemRows);
        Assert.Equal(6, c.Total);
        Assert.Equal(2, c.Behind);
        Assert.Equal(4, c.OnLatest);
        Assert.Equal(1, c.UnsupportedMac);
        Assert.Equal(1, c.Windows10);
    }

    [Fact]
    public void OneBetaMacDoesNotPutItsMajorBehind()
    {
        var fleet = Enumerable.Range(1, 5).Select(i => $$"""{"serialNumber":"SAMPLE{{i}}","platform":"macOS","osVersion":"15.6.1"}""")
            .Append("""{"serialNumber":"SAMPLE6","platform":"macOS","osVersion":"15.7"}""")
            .Append("""{"serialNumber":"SAMPLE7","platform":"macOS","osVersion":"15.5"}""");
        var rows = Rows("[" + string.Join(",", fleet) + "]");

        var c = Insights.OsCurrency(rows);
        // 15.6.1 is on five devices and is the latest; the one 15.7 is ahead, not the bar.
        Assert.Equal(1, c.Behind);
        Assert.Equal(6, c.OnLatest);
        Assert.Equal(["SAMPLE7"], Serials("system", "patch", "behind", rows));
    }

    [Fact]
    public void OneBetaOfTheNextMajorDoesNotMakeAnOlderOneUnsupported()
    {
        var fleet = Enumerable.Range(1, 5).Select(i => $$"""{"serialNumber":"SAMPLE{{i}}","platform":"macOS","osVersion":"15.6.1"}""")
            .Append("""{"serialNumber":"SAMPLE6","platform":"macOS","osVersion":"16.0"}""")
            .Append("""{"serialNumber":"SAMPLE7","platform":"macOS","osVersion":"13.7.6"}""")
            .Append("""{"serialNumber":"SAMPLE8","platform":"macOS","osVersion":"12.7.6"}""");
        var rows = Rows("[" + string.Join(",", fleet) + "]");

        // 15 is the newest major five devices run, so 13 is supported and 12 is not.
        Assert.Equal(1, Insights.OsCurrency(rows).UnsupportedMac);
        Assert.Equal(["SAMPLE8"], Serials("system", "os", "unsupported", rows));
    }

    [Fact]
    public void TheSystemReportHonoursTheOsCurrencyLinks()
    {
        Assert.Equal(["SAMPLE2", "SAMPLE5"], Serials("system", "patch", "behind", SystemRows));
        Assert.Equal(["SAMPLE3"], Serials("system", "os", "unsupported", SystemRows));
        Assert.Equal(["SAMPLE6"], Serials("system", "os", "windows10", SystemRows));
        Assert.Equal(["SAMPLE2"], Serials("system", "osVersion", "15.5", SystemRows));
    }

    [Fact]
    public void UptimeAndPendingBucketsUseTheSharedKeys()
    {
        var rows = Rows("""
            [{"serialNumber":"SAMPLE1","uptime":3600,"pendingUpdatesCount":0},
             {"serialNumber":"SAMPLE2","uptimeString":"5d 2h","pendingUpdatesCount":3,"deferredUpdatesCount":1},
             {"serialNumber":"SAMPLE3","uptime":3000000,"pendingUpdatesCount":12}]
            """);
        Assert.Equal(["SAMPLE1"], Serials("system", "uptime", "lt1d", rows));
        Assert.Equal(["SAMPLE2"], Serials("system", "uptime", "3-7d", rows));
        Assert.Equal(["SAMPLE3"], Serials("system", "uptime", "30d+", rows));
        Assert.Equal(["SAMPLE2", "SAMPLE3"], Serials("system", "pending", Insights.AnyPending, rows));
        Assert.Equal(["SAMPLE1"], Serials("system", "pending", "up-to-date", rows));
        Assert.Equal(["SAMPLE2"], Serials("system", "deferred", "1", rows));
    }

    // ── Security ─────────────────────────────────────────────────────────

    private static readonly List<JsonElement> SecurityRows = Rows("""
        [{"serialNumber":"SAMPLE1","platform":"macOS","encryptionEnabled":true,"firewallEnabled":false,"antivirusEnabled":false,"sipEnabled":false,"criticalCveCount":2,"activelyExploitedCveCount":1,"userExpiredCertCount":1},
         {"serialNumber":"SAMPLE2","platform":"Windows","encryptionEnabled":false,"firewallEnabled":true,"antivirusEnabled":false,"secureBootEnabled":false,"activeThreatCount":3,"expiringSoonCertCount":2},
         {"serialNumber":"SAMPLE3","platform":"Windows","encryptionEnabled":true,"firewallEnabled":true,"antivirusEnabled":true,"secureBootEnabled":true}]
        """);

    [Fact]
    public void TheSecurityReportHonoursEveryPostureLink()
    {
        Assert.Equal(["SAMPLE2"], Serials("security", "encryption", "off", SecurityRows));
        Assert.Equal(["SAMPLE1"], Serials("security", "firewall", "off", SecurityRows));
        // A Mac that names no antivirus product is not counted as having it off.
        Assert.Equal(["SAMPLE2"], Serials("security", "antivirus", "off", SecurityRows));
        Assert.Equal(["SAMPLE1"], Serials("security", "sip", "off", SecurityRows));
        Assert.Equal(["SAMPLE2"], Serials("security", "secureBoot", "off", SecurityRows));
        Assert.Equal(["SAMPLE2"], Serials("security", "threats", "1", SecurityRows));
        Assert.Equal(["SAMPLE1"], Serials("security", "certs", "expired", SecurityRows));
        Assert.Equal(["SAMPLE2"], Serials("security", "certs", "expiring", SecurityRows));
        Assert.Equal(["SAMPLE1"], Serials("security", "cve", "critical", SecurityRows));
        Assert.Equal(["SAMPLE1"], Serials("security", "cve", "exploited", SecurityRows));
    }

    [Fact]
    public void ThePostureRowsCarryTheirLinkKeys()
    {
        var keys = Insights.Security(SecurityRows).Rows.Select(r => r.Key).ToList();
        Assert.Equal(["encryption", "firewall", "antivirus", "sip", "secureBoot"], keys);
        var risk = Insights.SoftwareRisk(SecurityRows);
        Assert.Equal((1, 1), (risk.CriticalDevices, risk.ExploitedDevices));
    }

    [Fact]
    public void CertificatesCountExpiringCertsAndDevicesAndListTheSoonest()
    {
        var certs = Rows("""
            [{"serialNumber":"SAMPLE1","commonName":"wifi.example.org","daysUntilExpiry":12},
             {"serialNumber":"SAMPLE2","commonName":"wifi.example.org","daysUntilExpiry":12},
             {"serialNumber":"SAMPLE2","commonName":"vpn.example.org","daysUntilExpiry":3},
             {"serialNumber":"SAMPLE3","commonName":"later.example.org","daysUntilExpiry":45}]
            """);
        var c = Insights.Certificates(certs, SecurityRows);
        Assert.Equal(3, c.ExpiringCerts);
        Assert.Equal(2, c.ExpiringDevices);
        Assert.Equal(1, c.ExpiredDevices);
        Assert.Equal(("vpn.example.org", 3, 1), c.Soonest[0]);
        Assert.Equal(("wifi.example.org", 12, 2), c.Soonest[1]);
    }

    // ── Management and hardware ──────────────────────────────────────────

    [Fact]
    public void UnmanagedIsAProviderTheReportCanFilterBy()
    {
        var rows = Rows("""
            [{"serialNumber":"SAMPLE1","provider":"Microsoft Intune (co-managed)","isEnrolled":true},
             {"serialNumber":"SAMPLE2","provider":"","isEnrolled":false},
             {"serialNumber":"SAMPLE3","isEnrolled":"false"}]
            """);
        Assert.Equal(["SAMPLE2", "SAMPLE3"], Serials("management", "provider", "Unmanaged", rows));
        Assert.Equal(["SAMPLE1"], Serials("management", "provider", "Microsoft Intune", rows));
        Assert.Equal(["SAMPLE2", "SAMPLE3"], Serials("management", "enrolled", "0", rows));
        Assert.Equal(["SAMPLE1"], Serials("management", "enrolled", "1", rows));
    }

    [Fact]
    public void TheHardwareReportHonoursTheHardwareCardLinks()
    {
        var rows = Rows("""
            [{"serialNumber":"SAMPLE1","platform":"macOS","model":"MacBook Pro","processor":{"name":"Apple M3 Pro"},"architecture":"arm64","memory":{"physical_memory":8589934592},
              "storage":[{"capacity":1000,"freeSpace":50}]},
             {"serialNumber":"SAMPLE2","platform":"Windows","model":"OptiPlex 7090","processor":{"name":"Intel Core i7"},"architecture":"x64","memory":{"totalPhysical":34359738368},
              "storage":[{"capacity":1000,"freeSpace":500}]}]
            """);
        Assert.Equal(["SAMPLE1"], Serials("hardware", "arch", "Apple Silicon", rows));
        Assert.Equal(["SAMPLE2"], Serials("hardware", "arch", "x64", rows));
        Assert.Equal(["SAMPLE1"], Serials("hardware", "storage", "low", rows));
        Assert.Equal(["SAMPLE1"], Serials("hardware", "memory", "le8", rows));
        Assert.Equal(["SAMPLE1"], Serials("hardware", "type", "laptop", rows));
        Assert.Equal(["SAMPLE2"], Serials("hardware", "type", "desktop", rows));
        Assert.Equal(["SAMPLE2"], Serials("hardware", "processor", "Intel", rows));
    }

    // ── Identity ─────────────────────────────────────────────────────────

    private static readonly List<JsonElement> IdentityRows = Rows("""
        [{"serialNumber":"SAMPLE1","usage":"Shared","summary":{"adminUsers":3},"directoryServices":{"azureAd":{"joined":true},"activeDirectory":{"isDomainJoined":true}}},
         {"serialNumber":"SAMPLE2","usage":"Assigned","summary":{"adminUsers":1},"directoryServices":{"azureAd":{"joined":"true"}}},
         {"serialNumber":"SAMPLE3","usage":"Assigned","summary":{"adminUsers":2},"directoryServices":{"activeDirectory":{"isDomainJoined":true}}},
         {"serialNumber":"SAMPLE4","summary":{"adminUsers":1}}]
        """);

    [Fact]
    public void JoinTypeFollowsTheTwoDirectoryFlags()
    {
        Assert.Equal(["hybrid", "entra", "domain", "workgroup"], IdentityRows.Select(Insights.JoinType));
        var s = Insights.Identity(IdentityRows);
        Assert.Equal(2, s.ExtraAdmins);
        Assert.Equal((1, 2), (s.Shared, s.Assigned));
        Assert.Equal(4, s.Joins.Count);
    }

    [Fact]
    public void TheIdentityReportHonoursTheIdentityCardLinks()
    {
        Assert.Equal(["SAMPLE1", "SAMPLE3"], Serials("identity", "admins", "extra", IdentityRows));
        Assert.Equal(["SAMPLE2"], Serials("identity", "join", "entra", IdentityRows));
        Assert.Equal(["SAMPLE4"], Serials("identity", "join", "workgroup", IdentityRows));
        Assert.Equal(["SAMPLE2", "SAMPLE3"], Serials("identity", "usage", "assigned", IdentityRows));
    }

    [Fact]
    public void TheIdentityReportFiltersByExactAdminCount()
    {
        var rows = Rows("""
            [{"serialNumber":"SAMPLE1","summary":{"adminUsers":1}},
             {"serialNumber":"SAMPLE2","summary":{"adminUsers":2}},
             {"serialNumber":"SAMPLE3","summary":{"adminUsers":3}},
             {"serialNumber":"SAMPLE4","summary":{"adminUsers":4}},
             {"serialNumber":"SAMPLE5","summary":{"adminUsers":7}}]
            """);
        Assert.Equal(["SAMPLE2"], Serials("identity", "admins", "2", rows));
        Assert.Equal(["SAMPLE3"], Serials("identity", "admins", "3", rows));
        Assert.Equal(["SAMPLE4", "SAMPLE5"], Serials("identity", "admins", "4+", rows));
        Assert.Equal(["SAMPLE2", "SAMPLE3", "SAMPLE4", "SAMPLE5"], Serials("identity", "admins", "extra", rows));
        Assert.Equal(["SAMPLE2", "SAMPLE4", "SAMPLE5"], Serials("identity", "admins", "2,4+", rows));
    }

    [Fact]
    public void TheManagementReportFiltersByPlatformAndLastSeen()
    {
        string Ago(double hours) => DateTime.UtcNow.AddHours(-hours).ToString("o");
        var rows = Rows($$"""
            [{"serialNumber":"SAMPLE1","platform":"macOS","lastSeen":"{{Ago(2)}}"},
             {"serialNumber":"SAMPLE2","platform":"Windows","lastSeen":"{{Ago(30)}}"},
             {"serialNumber":"SAMPLE3","platform":"Windows 11","lastSeen":"{{Ago(24 * 9)}}"},
             {"serialNumber":"SAMPLE4","platform":"macOS"}]
            """);
        Assert.Equal(["SAMPLE1", "SAMPLE4"], Serials("management", "platform", "macOS", rows));
        Assert.Equal(["SAMPLE2", "SAMPLE3"], Serials("management", "platform", "Windows", rows));
        Assert.Equal(["SAMPLE1"], Serials("management", "lastSeen", "24h", rows));
        Assert.Equal(["SAMPLE2"], Serials("management", "lastSeen", "1-7d", rows));
        // No last-seen time counts as missing, as the device list counts it.
        Assert.Equal(["SAMPLE3", "SAMPLE4"], Serials("management", "lastSeen", "7d+", rows));
    }

    [Theory]
    [InlineData("reportmate://management?platform=macOS&lastSeen=7d%2B", "management", "lastSeen", "7d+")]
    [InlineData("reportmate://identity?admins=4%2B", "identity", "admins", "4+")]
    public void TheNewManagementAndIdentityLinksParse(string url, string section, string key, string value)
    {
        var link = DeepLink.Parse(url)!;
        Assert.Equal((section, value), (link.Section, link[key]));
    }

    // ── App usage ────────────────────────────────────────────────────────

    [Fact]
    public void AppUsageListsTheMostUsedAndTheLeastUsedOfTheWidelyInstalled()
    {
        var (most, least) = Insights.AppUsage(Doc("""
            {"applications":[
              {"name":"Photoshop","totalHours":900,"activeDeviceCount":40,"installedDeviceCount":50},
              {"name":"Word","totalHours":1200,"activeDeviceCount":300,"installedDeviceCount":310},
              {"name":"Blender","totalHours":10,"activeDeviceCount":2,"installedDeviceCount":40},
              {"name":"Niche","totalHours":1,"activeDeviceCount":0,"installedDeviceCount":3}]}
            """));
        Assert.Equal(["Word", "Photoshop", "Blender", "Niche"], most.Select(a => a.Name));
        // Niche is on fewer than ten devices, so it is not a candidate for least used.
        Assert.Equal(["Blender", "Photoshop", "Word"], least.Select(a => a.Name));
    }

    // ── Network and peripherals ──────────────────────────────────────────

    private static readonly List<JsonElement> NetworkRows = Rows("""
        [{"serialNumber":"SAMPLE1","raw":{"activeConnection":{"connectionType":"Wireless","wifiSignalStrength":18}}},
         {"serialNumber":"SAMPLE2","raw":{"activeConnection":{"connectionType":"WiFi"},"wifiNetworks":[{"isCurrent":true,"rssi":-58}]}},
         {"serialNumber":"SAMPLE3","raw":{"activeConnection":{"connectionType":"Ethernet"}}},
         {"serialNumber":"SAMPLE4","raw":{"activeConnection":{"connectionType":"Wired"}}}]
        """);

    [Fact]
    public void WifiSignalIsAPercentageOnBothPlatforms()
    {
        Assert.Equal(18, Insights.WifiSignalPercent(NetworkRows[0]));
        Assert.Equal(84, Insights.WifiSignalPercent(NetworkRows[1]));
        Assert.Null(Insights.WifiSignalPercent(NetworkRows[2]));
    }

    [Fact]
    public void TheNetworkAndPeripheralsReportsHonourTheirLinks()
    {
        Assert.Equal(["SAMPLE3", "SAMPLE4"], Serials("network", "connection", "wired", NetworkRows));
        Assert.Equal(["SAMPLE1", "SAMPLE2"], Serials("network", "connection", "wifi", NetworkRows));
        Assert.Equal(["SAMPLE1"], Serials("network", "signal", "weak", NetworkRows));

        var peripherals = Rows("""
            [{"serialNumber":"SAMPLE1","location":"Room 101","printers":[{},{}],"displayDevices":[]},
             {"serialNumber":"SAMPLE2","location":"Room 202","printers":[{}],"displayDevices":[{},{}]},
             {"serialNumber":"SAMPLE3","location":"Room 101","printers":[],"displayDevices":[]}]
            """);
        Assert.Equal(["SAMPLE1", "SAMPLE2"], Serials("peripherals", "kind", "printers", peripherals));
        Assert.Equal(["SAMPLE2"], Serials("peripherals", "kind", "displays", peripherals));
        Assert.Equal(["SAMPLE1", "SAMPLE3"], Serials("peripherals", "room", "room 101", peripherals));

        var hardware = Rows("""[{"serialNumber":"SAMPLE3","displays":[{}]}]""");
        var s = Insights.NetworkAndPeripherals(NetworkRows, peripherals, hardware);
        Assert.Equal((2, 2, 1), (s.Wired, s.Wifi, s.Weak));
        Assert.Equal(3, s.Printers);
        // The Mac reports its display in the hardware module, not peripherals.
        Assert.Equal(3, s.Displays);
        Assert.Equal(("Room 101", 2), s.Rooms[0]);
    }

    [Fact]
    public void AnUnknownKeyIsLeftToThePlainFieldFilters()
    {
        Assert.Null(ReportFilters.Apply("security", "nonsense", "1", SecurityRows, SecurityRows));
        Assert.Null(ReportFilters.Apply("applications", "apps", "Word", SecurityRows, SecurityRows));
    }

    // ── Links ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("reportmate://management?provider=Unmanaged", "management", "provider", "Unmanaged")]
    [InlineData("reportmate://security?certSearch=wifi.example.org&certStatus=expiring", "security", "certSearch", "wifi.example.org")]
    [InlineData("reportmate://system?pending=1-5%2C6-10%2C10%2B", "system", "pending", "1-5,6-10,10+")]
    [InlineData("reportmate://installs?filter=errors&item=Google%20Chrome", "installs", "item", "Google Chrome")]
    [InlineData("reportmate://devices?platform=macOS", "devices", "platform", "macOS")]
    [InlineData("https://reportmate.example.org/peripherals?kind=printers&room=Room%20101", "peripherals", "room", "Room 101")]
    public void EveryDashboardLinkParsesWithItsFilter(string url, string section, string key, string value)
    {
        var link = DeepLink.Parse(url)!;
        Assert.Equal(section, link.Section);
        Assert.Equal(value, link[key]);
    }

    [Fact]
    public void ACardLinkRoundTripsThroughTheAppForm()
    {
        var link = DeepLink.For("events", "failures", ("hours", "24"), ("reason", "invalid_passphrase"));
        Assert.Equal("reportmate://events/failures?hours=24&reason=invalid_passphrase", link.ToAppUrl());
        var back = DeepLink.Parse(link.ToAppUrl())!;
        Assert.Equal(("events", "failures", "24"), (back.Section, back.Argument, back["hours"]));

        var usage = DeepLink.For("applications", "usage/Adobe Photoshop 2025", ("days", "30"));
        Assert.Equal("reportmate://applications/usage/Adobe%20Photoshop%202025?days=30", usage.ToAppUrl());
    }
}
