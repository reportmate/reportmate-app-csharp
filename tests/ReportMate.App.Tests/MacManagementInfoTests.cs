using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The Mac management reader, on payloads shaped like the Mac client's: osquery
/// rows with flags as strings, the identity certificate, installed profiles with
/// their payloads, and the managed-preference domains.
/// </summary>
public class MacManagementInfoTests
{
    private const string Sample = """
        {
          "ade_configuration": {"activated": "true", "assigned": "true", "organization": "Example College", "support_email": "", "support_phone": "555-0100"},
          "device_identifiers": {"asset_tag": "Staff", "hardware_model": "Mac16,11", "uuid": "00000000-0000-0000-0000-000000000001"},
          "mdm_enrollment": {
            "access_rights": "8191", "checkin_url": "https://i.manage.microsoft.com/checkin", "dep_capable": "true",
            "enrolled": "true", "has_scep_payload": "", "installed_from_dep": "true",
            "server_url": "https://i.manage.microsoft.com/server", "user_approved": "true"
          },
          "mdm_certificate": {
            "certificate_enrollment_method": "ACME", "certificate_expires": "Sep 16 23:10:40 2027 GMT",
            "certificate_issuer": "00000000-0000-0000-0000-000000000002", "certificate_name": "00000000-0000-0000-0000-000000000002",
            "identity_payload_type": "com.apple.security.acme", "mdm_provider": "Microsoft Intune",
            "push_topic": "com.apple.mgmt.External.00000000-0000-0000-0000-000000000003",
            "scep_url": "https://example.org/acme"
          },
          "mdm_info": {"is_supervised": "true", "org_name": "Example College"},
          "installed_profiles": [
            {
              "identifier": "com.example.wifi", "install_date": "2026-10-18 03:51:47 +0000", "method": "Native",
              "name": "Wi-Fi", "organization": "Example College", "description": "Campus network",
              "payload_count": 1, "removal_disallowed": 1, "user": "System Level",
              "uuid": "00000000-0000-0000-0000-000000000010", "verification_state": "verified",
              "payloads": [
                {"display_name": "Wi-Fi", "type": "com.apple.wifi.managed", "settings": {"SSID_STR": "Example", "AutoJoin": true, "Retries": 3, "EAPClientConfiguration": {"UserName": "x"}}}
              ]
            },
            {
              "identifier": "com.example.legacy", "install_date": "Jan 5, 2026 at 10:00", "method": "Emulated",
              "name": "Legacy", "payload_count": 2, "payloads": [], "removal_disallowed": 0, "user": "User Level",
              "verification_state": "not verified"
            },
            {
              "identifier": "com.example.raw", "name": "Raw", "user": "System Level",
              "payloads": [{"type": "com.example.custom", "settings": {}, "raw_data": "<plist/>"}]
            }
          ],
          "managed_policies": [
            {"domain": "ManagedInstalls", "setting_count": 2, "settings": [
              {"name": "InstallAppleSoftwareUpdates", "value": "1", "scope": "System Level"},
              {"name": "SoftwareRepoURL", "value": "https://example.org/repo", "scope": "System Level"}
            ]},
            {"domain": "com.example.empty", "setting_count": 4, "settings": []}
          ],
          "logs": {"platform": "macOS", "roots": []}
        }
        """;

    private static MacManagementInfo Read(string json, string? platform = "macOS")
    {
        var info = MacManagementInfo.From(JsonDocument.Parse(json).RootElement, platform);
        Assert.NotNull(info);
        return info!;
    }

    [Fact]
    public void Enrollment_FromStringFlags()
    {
        var m = Read(Sample);
        Assert.True(m.IsMac);
        Assert.True(m.IsEnrolled);
        Assert.True(m.InstalledFromDep);
        Assert.True(m.UserApproved);
        Assert.True(m.DepCapable);
        Assert.True(m.IsSupervised);
        Assert.Equal("Automated Device Enrollment", m.EnrollmentType);
        Assert.Equal("https://i.manage.microsoft.com/server", m.ServerUrl);
        Assert.Equal("https://i.manage.microsoft.com/checkin", m.CheckinUrl);
        Assert.Equal("00000000-0000-0000-0000-000000000001", m.DeviceUuid);
    }

    [Fact]
    public void Provider_ComesFromTheServerUrl() => Assert.Equal("Microsoft Intune", Read(Sample).Provider);

    [Fact]
    public void Provider_TheLiveServerBeatsAStaleCertificate()
    {
        var cert = new MacManagementInfo.Certificate(null, null, null, null, "Jamf CA", null, null, null, null, null, null);
        Assert.Equal("MicroMDM", MacManagementInfo.DetectProvider("https://micromdm.example.org/mdm", cert));
        Assert.Equal("Jamf Pro", MacManagementInfo.DetectProvider(null, cert));
        Assert.Null(MacManagementInfo.DetectProvider("https://example.org", MacManagementInfo.Certificate.Empty));
    }

    [Fact]
    public void EnrollmentType_LaddersDownWithoutAde()
    {
        Assert.Equal("User Approved Enrollment", Read("""{"mdm_enrollment":{"enrolled":"true","installed_from_dep":"false","user_approved":"true"}}""").EnrollmentType);
        Assert.Equal("MDM Enrolled", Read("""{"mdm_enrollment":{"enrolled":"1","installed_from_dep":"false","user_approved":"false"}}""").EnrollmentType);
        Assert.Null(Read("""{"mdm_enrollment":{"enrolled":"false","dep_capable":"false"}}""").EnrollmentType);
    }

    [Fact]
    public void Certificate_AndItsEnrollmentMethod()
    {
        var m = Read(Sample);
        Assert.Equal("ACME", m.EnrollmentMethod);
        Assert.Equal("ACME Directory", m.EnrollmentUrlLabel);
        Assert.Equal("com.apple.mgmt.External.00000000-0000-0000-0000-000000000003", m.Cert.PushTopic);
        Assert.Equal("https://example.org/acme", m.Cert.ScepUrl);
        Assert.True(m.Cert.HasDetails);
    }

    [Fact]
    public void EnrollmentMethod_FallsBackToThePayloadTypeThenScep()
    {
        Assert.Equal("SCEP", Read("""{"mdm_enrollment":{"enrolled":"true"},"mdm_certificate":{"identity_payload_type":"com.apple.security.scep"}}""").EnrollmentMethod);
        var scep = Read("""{"mdm_enrollment":{"enrolled":"true","has_scep_payload":"true"}}""");
        Assert.Equal("SCEP", scep.EnrollmentMethod);
        Assert.Equal("SCEP Server", scep.EnrollmentUrlLabel);
        Assert.Null(Read("""{"mdm_enrollment":{"enrolled":"true"}}""").EnrollmentMethod);
    }

    [Fact]
    public void Certificate_OsqueryOutputWithLeakedSemicolons()
    {
        var json = """{"mdm_certificate":{"output":"{\"push_topic\": \"com.apple.mgmt.example\";, \"certificate_issuer\": \"Example CA\"}"},"mdm_enrollment":{"enrolled":"true"}}""";
        var m = Read(json);
        Assert.Equal("com.apple.mgmt.example", m.Cert.PushTopic);
        Assert.Equal("Example CA", m.Cert.Issuer);
    }

    [Fact]
    public void Ade_ActiveWhenAssignedOrActivated()
    {
        var m = Read(Sample);
        Assert.True(m.AdeActive);
        Assert.Equal("Example College", m.AdeOrganization);
        Assert.Equal("555-0100", m.AdeSupportPhone);
        Assert.Null(m.AdeSupportEmail);
        Assert.False(Read("""{"mdm_enrollment":{"enrolled":"true"},"ade_configuration":{"activated":"false","assigned":"false"}}""").AdeActive);
    }

    [Fact]
    public void Profiles_ScopeFlagsAndPayloads()
    {
        var m = Read(Sample);
        Assert.Equal(3, m.ProfileCount);
        Assert.Equal(1, m.UserScopedProfileCount);
        var wifi = m.Profiles[0];
        Assert.Equal("Wi-Fi", wifi.Name);
        Assert.Equal("System Level", wifi.Scope);
        Assert.True(wifi.IsVerified);
        Assert.True(wifi.IsRemovalDisallowed);
        Assert.False(wifi.IsLegacyMcx);
        Assert.Equal(1, wifi.PayloadCount);
        Assert.Equal("Campus network", wifi.Description);

        var settings = wifi.Payloads[0].Settings;
        Assert.Equal(["AutoJoin", "EAPClientConfiguration", "Retries", "SSID_STR"], settings.Select(s => s.Key));
        Assert.Equal(MacManagementInfo.SettingKind.Flag, settings[0].Kind);
        Assert.True(settings[0].Flag);
        Assert.Equal(MacManagementInfo.SettingKind.Structured, settings[1].Kind);
        Assert.Contains("\"UserName\"", settings[1].Text);
        Assert.Equal("3", settings[2].Text);
        Assert.Equal("Example", settings[3].Text);
    }

    [Fact]
    public void Profiles_LegacyMcxAndPayloadCountWithoutPayloads()
    {
        var legacy = Read(Sample).Profiles[1];
        Assert.True(legacy.IsLegacyMcx);
        Assert.False(legacy.IsVerified);
        Assert.Equal(2, legacy.PayloadCount);
        Assert.Equal("Unknown Organization", legacy.Organization ?? "Unknown Organization");
        Assert.Equal("Jan 5, 2026", legacy.InstallDateText);
    }

    [Fact]
    public void Payload_RawDataWhenNoSettings()
    {
        var payload = Read(Sample).Profiles[2].Payloads[0];
        Assert.Empty(payload.Settings);
        Assert.Equal("<plist/>", payload.RawData);
        Assert.Equal("com.example.custom", payload.Title);
    }

    [Fact]
    public void Profiles_SearchByNameIdentifierOrOrganization()
    {
        var wifi = Read(Sample).Profiles[0];
        Assert.True(wifi.Matches("wi-fi"));
        Assert.True(wifi.Matches("COM.EXAMPLE.WIFI"));
        Assert.True(wifi.Matches("college"));
        Assert.False(wifi.Matches("printer"));
    }

    [Fact]
    public void Profiles_FallBackToConfigurationProfiles()
    {
        var m = Read("""{"mdm_enrollment":{"enrolled":"true"},"configuration_profiles":[{"profile_name":"Printers","uuid":"00000000-0000-0000-0000-000000000020","type":"User","source":"Example"}]}""");
        var p = Assert.Single(m.Profiles);
        Assert.Equal("Printers", p.Name);
        Assert.Equal("00000000-0000-0000-0000-000000000020", p.Identifier);
        Assert.Equal("User Level", p.Scope);
        Assert.Equal("Example", p.Organization);
    }

    [Fact]
    public void ManagedPolicies_SettingsAndCounts()
    {
        var m = Read(Sample);
        Assert.Equal(2, m.ManagedPolicies.Count);
        var installs = m.ManagedPolicies[0];
        Assert.Equal("ManagedInstalls", installs.Domain);
        Assert.Equal(2, installs.SettingCount);
        Assert.True(installs.Settings[0].Flag);
        Assert.Null(installs.Settings[1].Flag);
        Assert.True(installs.Matches("repo"));
        Assert.Equal(4, m.ManagedPolicies[1].SettingCount);
    }

    [Fact]
    public void Dates_OpensslAndClientForms()
    {
        Assert.Equal(new DateTime(2027, 9, 16, 23, 10, 40, DateTimeKind.Utc), MacManagementInfo.ParseDate("Sep 16 23:10:40 2027 GMT"));
        Assert.Equal(new DateTime(2027, 9, 6, 1, 2, 3, DateTimeKind.Utc), MacManagementInfo.ParseDate("Sep  6 01:02:03 2027 GMT"));
        Assert.Equal(new DateTime(2026, 10, 18, 3, 51, 47, DateTimeKind.Utc), MacManagementInfo.ParseDate("2026-10-18 03:51:47 +0000"));
        Assert.Null(MacManagementInfo.ParseDate("soon"));
    }

    [Fact]
    public void Expiry_ByDaysLeft()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(MacManagementInfo.Expiry.Expired, MacManagementInfo.ExpiryOf(now.AddDays(-1), now));
        Assert.Equal(MacManagementInfo.Expiry.Soon, MacManagementInfo.ExpiryOf(now.AddDays(10), now));
        Assert.Equal(MacManagementInfo.Expiry.Fine, MacManagementInfo.ExpiryOf(now.AddDays(90), now));
        Assert.Equal(MacManagementInfo.Expiry.Unknown, MacManagementInfo.ExpiryOf(null, now));
    }

    [Fact]
    public void DeviceDetails_PrimaryUserOnlyWhenItDiffers()
    {
        var same = Read("""{"mdm_enrollment":{"enrolled":"true","user_principal_name":"a@example.org@00000000-0000-0000-0000-000000000001"},"device_details":{"primary_user":"A@example.org"}}""");
        Assert.True(same.HasDeviceDetails);
        Assert.False(same.ShowPrimaryUser);
        Assert.Equal("a@example.org", MacManagementInfo.StripTenantSuffix(same.EnrolledBy));
        var other = Read("""{"mdm_enrollment":{"enrolled":"true","user_principal_name":"a@example.org"},"device_details":{"primary_user":"b@example.org"}}""");
        Assert.True(other.ShowPrimaryUser);
    }

    [Fact]
    public void SingletonArray_IsUnwrapped_AndEmptyIsNull()
    {
        Assert.True(Read("""[{"mdm_enrollment":{"enrolled":"true"}}]""").IsEnrolled);
        Assert.Null(MacManagementInfo.From(JsonDocument.Parse("{}").RootElement, "macOS"));
        Assert.Null(MacManagementInfo.From((JsonElement?)null, "macOS"));
    }

    [Fact]
    public void NotEnrolled_ReadsAsSuch()
    {
        var m = Read("""{"mdm_enrollment":{"enrolled":"false"},"installed_profiles":[]}""");
        Assert.False(m.IsEnrolled);
        Assert.Equal(0, m.ProfileCount);
    }
}
