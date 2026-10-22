using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The Mac security reader, on payloads shaped like the Mac client's: flags as
/// 0/1 integers, remote management in snake_case strings, and the client's own
/// spellings for keys the Mac and web apps look up under other names.
/// </summary>
public class MacSecurityInfoTests
{
    private const string Sample = """
        {
          "fileVault": {"enabled": 1, "status": "Enabled", "personalRecoveryKey": 1},
          "systemIntegrityProtection": {"enabled": 1, "configFlags": {"allow_unauthenticated_root": 0, "sip": 1}},
          "gatekeeper": {"enabled": 1, "developerIdEnabled": 1},
          "xprotect": {"enabled": 1, "version": "5000", "signatureCount": 250},
          "mrt": {"enabled": 1, "version": "1.93"},
          "secureEnclave": {"present": 1, "touchIdSupported": 0},
          "secureBoot": {"externalBootAllowed": "Disallowed"},
          "rootUser": {"enabled": 0},
          "firewall": {"enabled": 0, "globalState": 0, "stealthModeEnabled": 1, "loggingEnabled": 0, "allowSignedEnabled": 1},
          "ssh": {"enabled": 1, "authorizedUsers": "all", "pubkeyAuthentication": "yes", "passwordAuthentication": ""},
          "remoteManagement": {"ard_enabled": "true", "ard_allowed_users": "true", "remote_login_enabled": "true", "screen_sharing_enabled": "false"},
          "edrProducts": {"count": 1, "products": [{
            "name": "Microsoft Defender for Endpoint", "identifier": "com.microsoft.wdav", "healthy": 1,
            "realTimeProtection": 1, "cloudProtection": 0, "tamperProtection": 1, "behaviorMonitoring": 1,
            "version": "101.1", "engineVersion": "1.1", "definitionsVersion": "1.2",
            "definitionsUpdated": "2026-01-02T03:04:05Z", "lastScanTime": "2026-01-02T06:00:00Z", "lastScanType": "quick",
            "lastScanFilesScanned": 6000, "totalThreatsDetected": 3, "licensed": 1, "fullDiskAccess": 1,
            "healthIssues": ["definitions out of date"]}]},
          "endpointSecurity": {"extensions": [{"name": "Example Extension", "enabled": 0, "state": "activated enabled", "version": "1.0", "teamId": "team-1"}]},
          "certificates": [
            {"commonName": "Example Root", "issuer": "/O=Example/CN=Example Root", "status": "Valid", "storeLocation": "System",
             "notBefore": "2020-01-01T00:00:00Z", "notAfter": "2040-01-01T00:00:00Z", "isSelfSigned": 1,
             "path": "/Library/Keychains/System.keychain", "daysUntilExpiry": 4000},
            {"commonName": "", "subject": "CN=old.example.org, O=Example", "status": "", "isExpired": 1, "storeLocation": "User",
             "path": "/Users/sample/Library/Keychains/login.keychain-db"},
            {"commonName": "soon.example.org", "status": "ExpiringSoon", "daysUntilExpiry": 12, "storeLocation": "User"}
          ],
          "securityReleaseInfo": {"update_name": "macOS Example 27.0.1", "release_date": "2026-09-28T00:00:00Z",
            "days_since_previous_release": "14", "security_info": "https://example.org/notes", "os_version": "27.0.1", "unique_cves_count": "5"},
          "unpatchedCVEs": [
            {"cve": "CVE-2026-0002", "patched_version": "27.0.2", "actively_exploited": false},
            {"cve": "CVE-2026-0001", "patched_version": "27.0.2", "actively_exploited": true, "url": "https://example.org/cve"}
          ]
        }
        """;

    private static MacSecurityInfo Read(string json, string? management = null) =>
        MacSecurityInfo.From(JsonDocument.Parse(json).RootElement,
            management is null ? null : JsonDocument.Parse(management).RootElement)!;

    [Fact]
    public void AbsentOrEmptyModule_ReadsAsNothing()
    {
        Assert.Null(MacSecurityInfo.From(null));
        Assert.Null(MacSecurityInfo.From(JsonDocument.Parse("{}").RootElement));
        Assert.Null(MacSecurityInfo.From(JsonDocument.Parse("[]").RootElement));
    }

    [Fact]
    public void SingletonArray_IsUnwrapped() =>
        Assert.True(Read("""[{"fileVault":{"enabled":1}}]""").FileVaultEnabled);

    [Fact]
    public void PostureFlags_ReadFromIntegers()
    {
        var s = Read(Sample);
        Assert.True(s.FileVaultEnabled);
        Assert.True(s.SipEnabled);
        Assert.True(s.GatekeeperEnabled);
        Assert.False(s.FirewallEnabled);
        Assert.True(s.SecureEnclavePresent);
        Assert.False(s.BiometricSupported);
        Assert.Equal("Disallowed", s.ExternalBoot);
        Assert.False(s.RootUserEnabled);
        Assert.True(s.AuthenticatedRootRequired);
        Assert.Equal("App Store & Known Developers", s.AllowAppsFrom);
        Assert.True(s.XProtectActive);
        Assert.Equal("250", s.XProtectSignatures);
        Assert.Equal("1.93", s.MrtVersion);
    }

    [Fact]
    public void FileVaultStatus_AloneCountsAsOn() =>
        Assert.True(Read("""{"fileVault":{"status":"On"}}""").FileVaultEnabled);

    [Fact]
    public void Firewall_TakesTheClientsOwnKeySpellings()
    {
        var s = Read(Sample);
        Assert.True(s.StealthMode);
        Assert.True(s.AllowSignedApps);
        Assert.False(s.FirewallLogging);
    }

    [Theory]
    [InlineData("""{"firewall":{"globalState":1}}""", true)]
    [InlineData("""{"firewall":{"globalState":2}}""", true)]
    [InlineData("""{"firewall":{"globalState":"on"}}""", true)]
    [InlineData("""{"firewall":{"globalState":"off"}}""", false)]
    [InlineData("""{"firewall":{"isEnabled":true}}""", true)]
    public void FirewallState_ReadsEachShape(string json, bool expected) =>
        Assert.Equal(expected, Read(json).FirewallEnabled);

    [Fact]
    public void RemoteAccess_ReadsSnakeCaseStrings()
    {
        var s = Read(Sample);
        Assert.True(s.SshEnabled);
        Assert.True(s.ArdEnabled);
        Assert.True(s.ScreenSharingEnabled);
        Assert.Equal("All Users", s.ArdAllowedUsers);
        Assert.Equal("All", s.SshAuthorizedUsers);
        Assert.Equal("Public Key", s.SshAuthentication);
    }

    [Fact]
    public void RemoteManagement_PrefersTheManagementModule()
    {
        var s = Read("""{"ssh":{"enabled":0},"remoteManagement":{"ard_enabled":"true"}}""",
            """{"remoteManagement":{"ardEnabled":false,"remoteLoginEnabled":true}}""");
        Assert.False(s.ArdEnabled);
        Assert.True(s.SshEnabled);
        Assert.False(s.SshServiceEnabled);
    }

    [Fact]
    public void Severity_FollowsTheHistoricalDefaults()
    {
        Assert.Equal(MacSeverity.Danger, MacSecurityInfo.Severity("encryption", false));
        Assert.Equal(MacSeverity.Danger, MacSecurityInfo.Severity("sip", false));
        Assert.Equal(MacSeverity.Warning, MacSecurityInfo.Severity("firewall", false));
        Assert.Equal(MacSeverity.Warning, MacSecurityInfo.Severity("ssh", true));
        Assert.Equal(MacSeverity.Ok, MacSecurityInfo.Severity("ssh", false));
        Assert.Equal(MacSeverity.Unknown, MacSecurityInfo.Severity("ssh", null));
    }

    [Fact]
    public void EdrProduct_ReadsDefenderDetail()
    {
        var p = Assert.Single(Read(Sample).EdrProducts);
        Assert.True(p.IsDefender);
        Assert.Equal("Healthy", p.HealthLabel);
        Assert.True(p.RealTimeProtection);
        Assert.False(p.CloudProtection);
        Assert.Equal(6000, p.FilesScanned);
        Assert.Equal(3, p.ThreatsDetected);
        Assert.Equal(["definitions out of date"], p.HealthIssues);
    }

    [Fact]
    public void EndpointExtension_ActiveFromItsState()
    {
        var e = Assert.Single(Read(Sample).EndpointExtensions);
        Assert.True(e.Active);
        Assert.Equal("Example Extension", e.Name);
    }

    [Fact]
    public void EncryptedVolumes_FallBackToTheSystemVolume()
    {
        Assert.Equal("System Volume", Read(Sample).EncryptedVolumesLabel);
        Assert.Equal("Data, Macintosh HD", Read("""{"fileVault":{"enabled":1,"encryptedVolumes":[{"name":"Data"},"Macintosh HD"]}}""").EncryptedVolumesLabel);
        Assert.Equal("None", Read("""{"fileVault":{"enabled":0}}""").EncryptedVolumesLabel);
    }

    [Fact]
    public void Certificates_ReadStatusStoreAndNames()
    {
        var certs = Read(Sample).Certificates;
        Assert.Equal(3, certs.Count);

        Assert.Equal(MacSecurityInfo.CertStatus.Valid, certs[0].Status);
        Assert.Equal("System", certs[0].Store);
        Assert.True(certs[0].IsSelfSigned);
        Assert.Equal("System.keychain", certs[0].KeychainName);

        Assert.Equal(MacSecurityInfo.CertStatus.Expired, certs[1].Status);
        Assert.Equal("old.example.org", certs[1].DisplayName);
        Assert.Equal("User", certs[1].Store);
        Assert.Equal("login.keychain-db", certs[1].KeychainName);

        Assert.Equal(MacSecurityInfo.CertStatus.ExpiringSoon, certs[2].Status);
        Assert.Equal(12, certs[2].DaysUntilExpiry);
        Assert.Equal("—", certs[2].KeychainName);
        Assert.True(certs[2].Matches("SOON"));
        Assert.False(certs[2].Matches("nothing"));
    }

    [Fact]
    public void OsRootExpiredCount_PrefersTheClientsSummary()
    {
        Assert.Equal(0, Read(Sample).OsRootExpiredCount);
        Assert.Equal(7, Read("""{"certificateSummary":{"osRootExpiredCount":7}}""").OsRootExpiredCount);
        Assert.Equal(1, Read("""{"certificates":[{"status":"Expired","isOsTrustedRoot":true}]}""").OsRootExpiredCount);
    }

    [Fact]
    public void SofaRelease_ReadsTheClientsKeyAndStringNumbers()
    {
        var r = Read(Sample).Release!;
        Assert.Equal("macOS Example 27.0.1", r.UpdateName);
        Assert.Equal(14, r.DaysSincePreviousRelease);
        Assert.Equal(5, r.UniqueCvesCount);
        Assert.Equal("https://example.org/notes", r.SecurityInfo);
    }

    [Fact]
    public void Cves_ReadAndSortExploitedFirst()
    {
        var s = Read(Sample);
        var sorted = MacSecurityInfo.SortCves(s.Cves);
        Assert.Equal(["CVE-2026-0001", "CVE-2026-0002"], sorted.Select(c => c.Id));
        Assert.True(sorted[0].ActivelyExploited);
        Assert.Equal("27.0.1", sorted[1].OsVersion);
        Assert.Equal("27.0.2", sorted[1].PatchedVersion);
        Assert.All(sorted, c => Assert.True(c.IsUnpatched));
    }

    [Fact]
    public void Cves_AlsoReadTheSofaSpelling()
    {
        var s = Read("""{"sofaUnpatchedCves":[{"cveId":"CVE-2026-0003"}],"sofaSecurityReleaseInfo":{"osVersion":"26.1"}}""");
        var c = Assert.Single(s.Cves);
        Assert.Equal("CVE-2026-0003", c.Id);
        Assert.Equal("26.1", c.OsVersion);
    }

    [Fact]
    public void Detections_ReadWithSummaryFallback()
    {
        var s = Read("""
            {"detections":[{"threatName":"Example.Threat","status":"Quarantined","filePath":"/tmp/x"},{"status":"allowed"}],
             "detectionSummary":{"totalBlocked30d":1}}
            """);
        Assert.Equal(2, s.Detections.Count);
        Assert.Equal(MacSecurityInfo.StatusMark.Good, s.Detections[0].Mark);
        Assert.True(s.Detections[0].HasDetails);
        Assert.Equal("Unknown Threat", s.Detections[1].ThreatName);
        Assert.Equal(MacSecurityInfo.StatusMark.Bad, s.Detections[1].Mark);
        Assert.False(s.Detections[1].HasDetails);
        Assert.Equal(2, s.Summary.Total30d);
        Assert.Equal(1, s.Summary.Blocked30d);
    }
}
