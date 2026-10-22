using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The dashboard insight counts, which have to match what the Mac app's dashboard
/// counts over the same report rows.
/// </summary>
public class InsightsTests
{
    private static List<JsonElement> Rows(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();

    [Fact]
    public void AntivirusIsCountedOnlyWhereItIsReported()
    {
        var posture = Insights.Security(Rows("""
            [{"platform":"Windows","antivirusEnabled":true,"encryptionEnabled":true,"secureBootEnabled":true},
             {"platform":"Windows","antivirusEnabled":false,"encryptionEnabled":"true","secureBootEnabled":false},
             {"platform":"macOS","antivirusEnabled":false,"encryptionEnabled":true,"sipEnabled":true},
             {"platform":"macOS","antivirusEnabled":false,"encryptionEnabled":false}]
            """));

        var av = posture.Rows.Single(r => r.Label == "Antivirus protection");
        Assert.Equal((1, 2), (av.On, av.Total));
        var enc = posture.Rows.Single(r => r.Label == "Disk encryption");
        Assert.Equal((3, 4), (enc.On, enc.Total));
        var sip = posture.Rows.Single(r => r.Label == "System Integrity Protection");
        Assert.Equal((1, 1), (sip.On, sip.Total));
        var sb = posture.Rows.Single(r => r.Label == "Secure Boot");
        Assert.Equal((1, 2), (sb.On, sb.Total));
    }

    [Fact]
    public void SecurityChipsCountDevicesNotFindings()
    {
        var posture = Insights.Security(Rows("""
            [{"platform":"Windows","activeThreatCount":3,"criticalCveCount":2},
             {"platform":"Windows","detectionCount":1,"expiredCertCount":4},
             {"platform":"macOS","userExpiredCertCount":0,"expiredCertCount":2}]
            """));
        Assert.Equal(2, posture.WithThreats);
        Assert.Equal(1, posture.ExpiredCerts);
        Assert.Equal(1, posture.CriticalCves);
    }

    [Fact]
    public void ManagementFoldsIntuneSpellingsAndCountsOnlyEnrolledProviders()
    {
        var m = Insights.ManagementOverview(Rows("""
            [{"provider":"Microsoft Intune","isEnrolled":true},
             {"provider":"Microsoft Intune (co-managed)","isEnrolled":"true"},
             {"provider":"MicroMDM","isEnrolled":true},
             {"provider":"MicroMDM","isEnrolled":false}]
            """));
        Assert.Equal(4, m.Total);
        Assert.Equal(3, m.Enrolled);
        Assert.Equal(("Microsoft Intune", 2), m.TopProviders[0]);
        Assert.Equal(("MicroMDM", 1), m.TopProviders[1]);
    }

    [Theory]
    [InlineData("""{"platform":"macOS","processor":{"name":"Apple M3 Pro"}}""", "Apple Silicon")]
    [InlineData("""{"platform":"macOS","architecture":"x86_64","processor":{"name":"Intel Core i7"}}""", "Intel")]
    [InlineData("""{"platform":"Windows","architecture":"ARM 64-bit Processor"}""", "ARM64")]
    [InlineData("""{"platform":"Windows","architecture":"64-bit"}""", "x64")]
    [InlineData("""{"platform":"macOS"}""", "Unknown")]
    public void ArchitectureFamilies(string json, string family) =>
        Assert.Equal(family, Insights.Family(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void HardwareChips()
    {
        var h = Insights.HardwareOverview(Rows("""
            [{"platform":"Windows","model":"Latitude 7440","memory":{"totalPhysical":8589934592},
              "storage":[{"capacity":1000,"freeSpace":50}]},
             {"platform":"macOS","model":"MacBook Pro","memory":{"physical_memory":17179869184},
              "storage":[{"capacity":1000,"freeSpace":500}]},
             {"platform":"Windows","model":"OptiPlex 7010","memory":{"totalPhysical":"4294967296"}}]
            """));
        Assert.Equal(1, h.LowStorage);
        Assert.Equal(2, h.LowMemory);
        Assert.Equal(2, h.Laptops);
    }

    [Theory]
    [InlineData("3d 4h", 3 * 86_400 + 4 * 3_600)]
    [InlineData("5h, 12m", 5 * 3_600 + 12 * 60)]
    [InlineData("45m", 45 * 60)]
    public void UptimeStringsReadBackToSeconds(string text, double seconds) =>
        Assert.Equal(seconds, Insights.ParseUptime(text));

    [Fact]
    public void SystemBucketsUptimeAndCountsUpdates()
    {
        var s = Insights.SystemOverview(Rows("""
            [{"uptime":3600,"pendingUpdatesCount":2},
             {"uptimeString":"2d 1h","deferredUpdatesCount":1},
             {"uptime":3000000},
             {}]
            """));
        Assert.Equal(1, s.Uptime.Single(b => b.Label == "< 1 day").Count);
        Assert.Equal(1, s.Uptime.Single(b => b.Label == "1-3 days").Count);
        Assert.Equal(1, s.Uptime.Single(b => b.Label == "30+ days").Count);
        Assert.Equal(1, s.Pending);
        Assert.Equal(1, s.Deferred);
    }
}
