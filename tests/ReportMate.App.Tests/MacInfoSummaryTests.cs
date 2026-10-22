using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>The Info tab's overview, read from a Mac's modules as the Mac client sends them.</summary>
public class MacInfoSummaryTests
{
    private static Dictionary<string, JsonElement> Modules(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    // Shaped like a real Mac client upload; every identifying value is invented.
    private const string Sample = """
    {
      "inventory": {
        "assetTag": "A0001", "catalog": "Staff", "department": "IT", "deviceName": "Sample Mac",
        "fleet": "Assigned Staff IT", "location": "Room 101", "owner": "Sample Owner",
        "serialNumber": "SAMPLE1", "usage": "Assigned", "uuid": "00000000-0000-4000-8000-000000000001"
      },
      "system": {
        "operatingSystem": { "architecture": "arm64", "buildNumber": "26A100", "majorVersion": 27, "minorVersion": 0,
                             "name": "macOS", "patchVersion": 1, "platform": "Darwin", "version": "27.0.1" },
        "pendingAppleUpdates": [],
        "systemDetails": { "computerName": "Sample Mac", "keyboardLayouts": ["Canadian"], "locale": "en_US",
                           "timeZone": "America/Vancouver", "systemUUID": "00000000-0000-4000-8000-000000000001" },
        "uptime": 214159, "uptimeString": "2d 11h 29m"
      },
      "hardware": {
        "formFactor": "desktop", "manufacturer": "Apple", "model": "Mac mini (2024)", "model_identifier": "Mac16,11",
        "processor": { "architecture": "ARM64", "cores": 14, "efficiency_cores": 4, "logical_cores": 14, "name": "M4 Pro", "performance_cores": 10 },
        "graphics": { "cores": 20, "manufacturer": "Apple", "name": "Apple M4 Pro" },
        "npu": { "cores": 16, "has_npu": 1, "isAvailable": 1, "name": "Apple Neural Engine", "performance_tops": "38", "performanceTops": "38" },
        "memory": { "physical_memory": 51539607552, "type": "LPDDR5", "unified_memory": 1 },
        "battery": { "cycle_count": 0, "percent_remaining": 0, "condition": "Normal" },
        "storage": [
          { "name": "Macintosh HD - Data", "capacity": 1000000000000, "free_space": 250000000000, "is_internal": 1, "interface": "Apple Fabric", "type": "SSD" },
          { "name": "Backup", "capacity": 4000000000000, "free_space": 1000000000000, "interface": "USB", "type": "SSD" }
        ],
        "system": { "computer_name": "Sample Mac", "hardware_model": "Mac16,11", "model_name": "Mac mini (2024)" }
      },
      "management": {
        "mdm_enrollment": { "dep_capable": "true", "enrolled": "true", "installed_from_dep": "true",
                            "server_url": "https://mdm.example.org/mdm/server", "user_approved": "true" },
        "mdm_certificate": { "certificate_expires": "Sep  6 23:10:40 2027 GMT", "certificate_issuer": "Sample Issuer",
                             "server_url": "https://mdm.example.org/mdm/server" },
        "mdm_info": { "is_supervised": "true" },
        "ade_configuration": { "organization": "" },
        "remote_management": { "screen_sharing_enabled": "true" }
      },
      "security": {
        "fileVault": { "enabled": 1, "status": "Enabled" },
        "bootstrapToken": { "escrowed": 1, "status": "Escrowed" },
        "gatekeeper": { "enabled": 1, "status": "Enabled" },
        "systemIntegrityProtection": { "enabled": 1, "status": "Enabled" },
        "activationLock": { "status": "Disabled" },
        "firewall": { "enabled": 0, "status": "Disabled" }
      },
      "identity": {
        "platformSSOUsers": { "deviceRegistered": 1, "method": "Secure enclave key",
          "users": [ { "username": "admin", "registered": 0, "tokensPresent": 0 },
                     { "username": "sample", "registered": 1, "tokensPresent": 1 } ] }
      },
      "network": {
        "hostname": "Sample Mac",
        "activeConnection": { "connectionType": "Ethernet", "interface": "en0", "ipAddress": "192.0.2.10", "isPrimary": 1 },
        "interfaces": [
          { "name": "en0", "type": "Ethernet", "isUp": 0, "macAddress": "02:00:00:00:00:01", "addresses": [ { "address": "fe80::1%en0", "family": "IPv6" } ] },
          { "name": "en0", "type": "Ethernet", "isUp": 1, "macAddress": "02:00:00:00:00:01", "addresses": [ { "address": "192.0.2.10", "family": "IPv4" } ] },
          { "name": "en1", "type": "WiFi", "isUp": 1, "macAddress": "02:00:00:00:00:02", "addresses": [ { "address": "192.0.2.11", "family": "IPv4" } ] },
          { "name": "utun3", "type": "Other", "isUp": 1, "addresses": [ { "address": "fe80::2%utun3", "family": "IPv6" } ] }
        ],
        "currentWiFiNetwork": { "interface": "en1", "mode": "Station", "ssid": "SampleWiFi" },
        "wifiInfo": { "knownNetworks": [ { "ssid": "SampleWiFi" } ] },
        "vpnConnections": [ { "name": "Sample VPN", "status": "Connected", "type": "IPSec" } ],
        "networkQuality": { "dl_rating": "High", "dl_throughput": "760.088", "idle_latency": "27.950", "ul_rating": "Medium", "ul_throughput": "791.754" }
      }
    }
    """;

    private static MacInfoSummary Read(string json = Sample) => MacInfoSummary.Read(Modules(json), "Api Name", "SAMPLE1");

    [Fact]
    public void Inventory_shows_identity_and_the_default_visible_assignments()
    {
        var inv = Read().Inventory;
        Assert.Equal("Sample Mac", inv.DeviceName);
        Assert.Equal("A0001", inv.AssetTag);
        Assert.Equal("SAMPLE1", inv.SerialNumber);
        Assert.Equal(["Usage", "Catalog", "Department", "Location", "Owner"], inv.Assignments.Select(a => a.Label));
    }

    [Fact]
    public void Inventory_falls_back_to_the_hardware_name_then_the_api()
    {
        Assert.Equal("Sample Mac", MacInfoSummary.Read(Modules("""{ "hardware": { "system": { "computer_name": "Sample Mac" } } }""")).Inventory.DeviceName);
        var bare = MacInfoSummary.Read(Modules("""{ "system": {} }"""), "Api Name", "SAMPLE1").Inventory;
        Assert.Equal("Api Name", bare.DeviceName);
        Assert.Equal("SAMPLE1", bare.SerialNumber);
    }

    [Fact]
    public void System_reads_version_build_and_localisation()
    {
        var sys = Read().System;
        Assert.True(sys.HasData);
        Assert.Equal("macOS 27", sys.MarketingName);
        Assert.Equal("27.0.1", sys.Version);
        Assert.Equal("26A100", sys.Build);
        Assert.Equal("Canadian", sys.KeyboardLayout);
        Assert.Equal("America/Vancouver", sys.TimeZone);
        Assert.Equal("en_US", sys.Locale);
        Assert.Equal("2d 11h 29m", sys.Uptime);
        Assert.Equal(MacSystemSummary.UpdateState.UpToDate, sys.Updates);
    }

    [Theory]
    [InlineData("26.1", "Tahoe")]
    [InlineData("15.6.1", "Sequoia")]
    [InlineData("10.15.7", "Catalina")]
    [InlineData("10.14", "Mojave")]
    [InlineData("9.1", "macOS")]
    public void Marketing_names_follow_the_major_version(string version, string expected) =>
        Assert.Equal(expected, MacSystemSummary.MarketingNameFor(version));

    [Fact]
    public void Pending_and_deferred_updates_are_counted_apart()
    {
        var json = """
        { "system": { "operatingSystem": { "version": "15.5" }, "pendingAppleUpdates": [
            { "name": "macOS 15.6", "deferred": false },
            { "name": "Safari", "deferred": true, "deferredUntil": "2026-11-20T00:00:00Z" },
            { "name": "XProtect", "deferredUntil": "2026-11-05T00:00:00Z" } ] } }
        """;
        var sys = MacInfoSummary.Read(Modules(json)).System;
        Assert.Equal("15.5.0", sys.Version);
        Assert.Equal(MacSystemSummary.UpdateState.Pending, sys.Updates);
        Assert.Equal(1, sys.UpdateCount);
        Assert.Equal(2, sys.DeferredCount);
        Assert.Equal(new DateTime(2026, 11, 5), sys.SoonestDeferral!.Value.Date);
    }

    [Fact]
    public void Only_deferred_updates_read_as_deferred()
    {
        var json = """{ "system": { "operatingSystem": { "version": "15.5" }, "pendingAppleUpdates": [ { "name": "A", "deferred": "true" } ] } }""";
        var sys = MacInfoSummary.Read(Modules(json)).System;
        Assert.Equal(MacSystemSummary.UpdateState.DeferredOnly, sys.Updates);
        Assert.Equal(1, sys.UpdateCount);
        Assert.Null(sys.SoonestDeferral);
    }

    [Fact]
    public void Uptime_is_formatted_from_seconds_when_no_string_is_sent()
    {
        var sys = MacInfoSummary.Read(Modules("""{ "system": { "operatingSystem": { "version": "15.5" }, "uptime": 214159 } }""")).System;
        Assert.Equal("2d 11h 29m", sys.Uptime);
    }

    [Fact]
    public void Hardware_reads_apple_silicon_specs()
    {
        var hw = Read().Hardware;
        Assert.True(hw.HasData);
        Assert.Equal("Mac mini (2024)", hw.Model);
        Assert.Equal("Mac16,11", hw.ModelIdentifier);
        Assert.Equal("M4 Pro", hw.Chip);
        Assert.Equal("14 cores (10P + 4E)", hw.CpuLabel);
        Assert.Equal("20 cores", hw.GpuLabel);
        Assert.Equal("48 GB", hw.MemoryLabel);
        Assert.Equal("38 TOPS", hw.NpuLabel);
        Assert.Null(hw.BatteryLabel);
    }

    [Fact]
    public void Storage_totals_count_internal_drives_only()
    {
        var hw = Read().Hardware;
        Assert.Equal(1000000000000d, hw.StorageTotalBytes);
        Assert.Equal(250000000000d, hw.StorageFreeBytes);
        Assert.Contains("free", hw.StorageLabel);
    }

    [Fact]
    public void A_portable_reports_its_battery()
    {
        var json = """
        { "hardware": { "formFactor": "laptop", "model": "MacBook Air", "processor": "Apple M2",
          "battery": { "cycle_count": 140, "percent_remaining": 82, "health": "Good" }, "graphics": { "name": "Apple M2" } } }
        """;
        var hw = MacInfoSummary.Read(Modules(json)).Hardware;
        Assert.Equal("Apple M2", hw.Chip);
        Assert.Equal("Unknown", hw.CpuLabel);
        Assert.Equal("Apple M2", hw.GpuLabel);
        Assert.Equal("82% · 140 cycles · Good", hw.BatteryLabel);
    }

    [Fact]
    public void Management_reads_the_osquery_enrollment()
    {
        var m = Read().Management;
        Assert.True(m.HasData);
        Assert.True(m.IsEnrolled);
        Assert.Null(m.Provider);
        Assert.Equal("Automated Device Enrollment", m.EnrollmentType);
        Assert.Equal(MacInfoSummary.Tone.Success, m.EnrollmentTone);
        Assert.True(m.IsSupervised);
        Assert.Equal("mdm.example.org", m.ServerHost);
        Assert.Equal(new DateTime(2027, 9, 6, 23, 10, 40), m.CertificateExpires);
        Assert.Null(m.Organization);
        Assert.Equal("00000000-0000-4000-8000-000000000001", m.HardwareUuid);
        Assert.False(m.ProviderIsIntune);
    }

    [Fact]
    public void Management_provider_comes_from_the_certificate_then_the_url()
    {
        var json = """
        { "management": { "mdm_enrollment": { "enrolled": "true", "user_approved": "true", "server_url": "https://i.manage.microsoft.com/x" },
          "mdm_certificate": { "output": "{\"mdm_provider\":\"Microsoft Intune\"}" } } }
        """;
        var m = MacInfoSummary.Read(Modules(json)).Management;
        Assert.Equal("Microsoft Intune", m.Provider);
        Assert.True(m.ProviderIsIntune);
        Assert.Equal("User Approved Enrollment", m.EnrollmentType);
        Assert.Equal(MacInfoSummary.Tone.Warning, m.EnrollmentTone);
        Assert.Equal("Jamf Pro", MacManagementSummary.DetectProvider("https://sample.jamfcloud.com"));
        Assert.Equal("MicroMDM", MacManagementSummary.DetectProvider(null, null, "MicroMDM Push CA"));
    }

    [Fact]
    public void Security_lists_the_mac_protections_in_order()
    {
        var rows = Read().Security.Rows;
        Assert.Equal(
            ["FileVault", "Bootstrap Token", "Platform SSO", "Token", "Method", "Gatekeeper", "System Integrity Protection", "Activation Lock", "Firewall", "Screen Sharing"],
            rows.Select(r => r.Label));
        var byLabel = rows.ToDictionary(r => r.Label);
        Assert.Equal(("Enabled", MacInfoSummary.Tone.Success), (byLabel["FileVault"].Value, byLabel["FileVault"].Tone));
        Assert.True(byLabel["Bootstrap Token"].Indented);
        Assert.Equal("Present", byLabel["Token"].Value);
        Assert.Equal("Secure enclave key", byLabel["Method"].Value);
        Assert.Equal(("Disabled", MacInfoSummary.Tone.Success), (byLabel["Activation Lock"].Value, byLabel["Activation Lock"].Tone));
        Assert.Equal(("Disabled", MacInfoSummary.Tone.Info), (byLabel["Firewall"].Value, byLabel["Firewall"].Tone));
        Assert.Equal(("Enabled", MacInfoSummary.Tone.Warning), (byLabel["Screen Sharing"].Value, byLabel["Screen Sharing"].Tone));
    }

    [Fact]
    public void Security_without_filevault_is_unknown_and_a_warning()
    {
        var rows = MacInfoSummary.Read(Modules("""{ "security": { "firewall": { "enabled": 1 } } }""")).Security.Rows;
        Assert.Equal(("Unknown", MacInfoSummary.Tone.Warning), (rows[0].Value, rows[0].Tone));
        Assert.Equal("Enabled", rows.Single(r => r.Label == "Firewall").Value);
    }

    [Fact]
    public void Network_merges_interface_entries_and_finds_both_connections()
    {
        var net = Read().Network;
        Assert.Equal("Sample Mac", net.Hostname);
        Assert.Equal("192.0.2.10", net.PrimaryIp);
        Assert.Equal("192.0.2.10", net.Ethernet!.IpAddress);
        Assert.Equal("02:00:00:00:00:01", net.Ethernet.MacAddress);
        Assert.Equal("192.0.2.11", net.Wireless!.IpAddress);
        Assert.Equal("SampleWiFi", net.WifiSsid);
        Assert.Null(net.WifiProtocol);
        Assert.Equal(1, net.Interfaces.Count(i => i.Name == "en0"));
        Assert.True(net.VpnActive);
        Assert.Equal("Sample VPN", net.VpnName);
    }

    [Fact]
    public void Network_quality_reads_the_parsed_fields()
    {
        var net = Read().Network;
        Assert.True(net.HasQuality);
        Assert.Equal("760 Mbps", net.Download);
        Assert.Equal("792 Mbps", net.Upload);
        Assert.Equal("High", net.Responsiveness);
        Assert.Equal("28 ms", net.Latency);
    }

    [Fact]
    public void Network_quality_reads_the_command_output()
    {
        var json = """
        { "network": { "interfaces": [ { "name": "en0", "type": "WiFi", "isUp": 1, "addresses": [ { "address": "192.0.2.20", "family": "IPv4" } ] } ],
          "networkQuality": { "output": "Uplink capacity: 20.4 Mbps\nDownlink capacity: 300.6 Mbps\nUplink Responsiveness: Low\nDownlink Responsiveness: Medium\nIdle Latency: 12.2 milliseconds" } } }
        """;
        var net = MacInfoSummary.Read(Modules(json)).Network;
        Assert.Equal("192.0.2.20", net.PrimaryIp);
        Assert.Null(net.Ethernet);
        Assert.NotNull(net.Wireless);
        Assert.Equal("301 Mbps", net.Download);
        Assert.Equal("20 Mbps", net.Upload);
        Assert.Equal("Medium", net.Responsiveness);
        Assert.Equal("12 ms", net.Latency);
    }

    [Theory]
    [InlineData("802.11ax", "WiFi 6")]
    [InlineData("802.11ac", "WiFi 5")]
    [InlineData("802.11be", "WiFi 7")]
    [InlineData("Station", null)]
    public void Wifi_generation_comes_from_a_phy_mode(string mode, string? expected) =>
        Assert.Equal(expected, MacNetworkSummary.ProtocolFromMode(mode));

    [Fact]
    public void A_vpn_tunnel_is_swapped_for_the_physical_interface()
    {
        var json = """
        { "network": { "activeConnection": { "interface": "utun4", "ipAddress": "198.51.100.5" },
          "interfaces": [ { "name": "en0", "type": "Ethernet", "isUp": 1, "macAddress": "02:00:00:00:00:03",
                            "addresses": [ { "address": "192.0.2.30", "family": "IPv4" } ] } ] } }
        """;
        var net = MacInfoSummary.Read(Modules(json)).Network;
        Assert.Equal("192.0.2.30", net.IpAddress);
        Assert.Equal("Ethernet", net.ConnectionType);
        Assert.Equal("02:00:00:00:00:03", net.MacAddress);
        Assert.True(net.VpnActive);
    }

    [Fact]
    public void Missing_modules_read_as_not_available()
    {
        var modules = Modules("""{ "applications": {} }""");
        Assert.False(MacInfoSummary.HasAny(modules));
        var info = MacInfoSummary.Read(modules);
        Assert.False(info.System.HasData);
        Assert.False(info.Hardware.HasData);
        Assert.False(info.Management.HasData);
        Assert.False(info.Security.HasData);
        Assert.Null(info.Network.PrimaryIp);
        Assert.True(MacInfoSummary.HasAny(Modules("""{ "network": [ { "hostname": "x" } ] }""")));
    }
}
