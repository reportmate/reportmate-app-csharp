using System.Text.Json;
using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Actionable-only certificate reporting. The class comes from the API, the status
/// from notAfter measured now, and the fleet counts are actionable certificates only.
/// Every fixture is a SAMPLE value, not a real certificate or device.
/// </summary>
public class CertificateViewTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("actionable", CertificateClass.Actionable)]
    [InlineData("system-managed", CertificateClass.SystemManaged)]
    [InlineData("trust-store", CertificateClass.TrustStore)]
    [InlineData("System-Managed", CertificateClass.SystemManaged)]
    public void StampedClass_IsRead(string stamped, CertificateClass expected) =>
        Assert.Equal(expected, CertificateView.Classify(stamped));

    [Fact]
    public void Unstamped_IsActionable() =>
        Assert.Equal(CertificateClass.Actionable, CertificateView.Classify(null));

    [Theory]
    [InlineData(CertificateClass.Actionable, "Actionable")]
    [InlineData(CertificateClass.SystemManaged, "System-managed")]
    [InlineData(CertificateClass.TrustStore, "Trust store")]
    public void Labels_MatchTheOtherApps(CertificateClass cls, string label) =>
        Assert.Equal(label, CertificateView.Label(cls));

    [Fact]
    public void ToggleLabel_MatchesTheOtherApps() =>
        Assert.Equal("Show system-managed", CertificateView.ShowSystemManagedLabel);

    [Fact]
    public void Live_OverridesAFrozenValidStatus()
    {
        // Collected as valid with 40 days left; it has since expired.
        var (status, days) = CertificateView.Live(Now.AddDays(-3), Now, "Valid", storedExpired: false, storedExpiringSoon: false, storedDays: 40);
        Assert.Equal(CertificateStatus.Expired, status);
        Assert.Equal(-3, days);
    }

    [Theory]
    [InlineData(0.5, CertificateStatus.ExpiringSoon, 0)]
    [InlineData(30.5, CertificateStatus.ExpiringSoon, 30)]
    [InlineData(31.5, CertificateStatus.Valid, 31)]
    [InlineData(-0.5, CertificateStatus.Expired, -1)]
    public void Live_UsesTheApiWindow(double daysAhead, CertificateStatus expected, int expectedDays)
    {
        var (status, days) = CertificateView.Live(Now.AddDays(daysAhead), Now);
        Assert.Equal(expected, status);
        Assert.Equal(expectedDays, days);
    }

    [Fact]
    public void Live_FallsBackToStoredFields_WithoutNotAfter()
    {
        Assert.Equal(CertificateStatus.Expired, CertificateView.Live(null, Now, "Expired").Status);
        Assert.Equal(CertificateStatus.ExpiringSoon, CertificateView.Live(null, Now, storedExpiringSoon: true, storedDays: 5).Status);
        Assert.Equal(CertificateStatus.Valid, CertificateView.Live(null, Now, "Valid").Status);
    }

    [Fact]
    public void DisplayName_PrefersStampedThenCommonNameThenSubjectCn()
    {
        Assert.Equal("SAMPLE Display", CertificateView.DisplayName("SAMPLE Display", "SAMPLE-CN", "CN=SAMPLE-SUBJECT"));
        Assert.Equal("SAMPLE-CN", CertificateView.DisplayName(null, "SAMPLE-CN", "CN=SAMPLE-SUBJECT"));
        Assert.Equal("SAMPLE-SUBJECT", CertificateView.DisplayName("", "", "CN=SAMPLE-SUBJECT, O=SAMPLE Org"));
    }

    [Fact]
    public void IssuerName_IsTheIssuerCn()
    {
        Assert.Equal("SAMPLE Issuing CA", CertificateView.IssuerName(null, "CN=SAMPLE Issuing CA, O=SAMPLE Org, C=XX"));
        Assert.Equal("SAMPLE Stamped CA", CertificateView.IssuerName("SAMPLE Stamped CA", "CN=Other"));
        Assert.Equal("O=SAMPLE Org", CertificateView.IssuerName(null, "O=SAMPLE Org"));
    }

    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FleetLabel_UsesTheActionableCounts()
    {
        Assert.Equal("Has Expired", CertificateView.FleetLabel(Row("""{"serialNumber":"SAMPLE-0001","expiredCertCount":2,"expiringSoonCertCount":0}""")));
        Assert.Equal("Expiring Soon", CertificateView.FleetLabel(Row("""{"expiredCertCount":0,"expiringSoonCertCount":1}""")));
        // Expired trust-store and system-managed certificates do not make a finding.
        Assert.Equal("Valid", CertificateView.FleetLabel(Row("""
            {"expiredCertCount":0,"expiringSoonCertCount":0,"osRootExpiredCertCount":7,
             "systemManagedExpiredCertCount":3,"actionableCertCount":4,"certificateCount":120}
            """)));
    }

    [Fact]
    public void FleetLabel_IsNullWithoutCounts() =>
        Assert.Null(CertificateView.FleetLabel(Row("""{"serialNumber":"SAMPLE-0002"}""")));

    [Fact]
    public void FleetSummary_SaysWhatNeedsRenewing()
    {
        Assert.Equal("2 expired, 1 expiring", CertificateView.FleetSummary(Row("""{"expiredCertCount":2,"expiringSoonCertCount":1}""")));
        Assert.Equal("Valid", CertificateView.FleetSummary(Row("""{"expiredCertCount":"0","expiringSoonCertCount":0}""")));
    }
}
