using System;
using System.Linq;
using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The Mac identity reader against payloads shaped like the Mac client's: flags as
/// 0/1, osquery rows as strings, utmpx login types, and the bootstrap token,
/// Activation Lock and FileVault keys borrowed from the security module.
/// </summary>
public class MacIdentityInfoTests
{
    private const string Identity = """
        {
          "directoryServices": {
            "activeDirectory": {"bound": 0, "domain": ""},
            "directoryNodes": "/Local/Default",
            "ldap": {"bound": 0, "server": ""}
          },
          "groups": [
            {"comment": "", "gid": "80", "group_sid": "", "groupname": "admin"},
            {"comment": "", "gid": "20", "group_sid": "", "groupname": "staff"}
          ],
          "loggedInUsers": [
            {"host": "", "pid": "1", "time": "1792455777", "tty": "", "user": ""},
            {"host": "", "pid": "412", "time": "1792455777", "tty": "console", "user": "sampleuser"},
            {"host": "", "pid": "913", "time": "1792455800", "tty": "ttys007", "user": "sampleuser"}
          ],
          "loginHistory": [
            {"pid": "4100", "time": "1792635735", "tty": "ttys001", "type": "7", "username": "sampleuser"},
            {"pid": "4101", "time": "1792635800", "tty": "ttys002", "type": "Failed", "username": "other"}
          ],
          "platformSSOUsers": {
            "deviceRegistered": 1, "extensionIdentifier": "org.example.sso.extension",
            "loginFrequency": 64800, "method": "Secure enclave key", "nonPlatformSSOAccounts": ["localadmin"],
            "offlineGracePeriod": "0 hours", "organizationName": "Example Org", "provider": "Example SSO",
            "registeredUserCount": 1, "supported": 1, "tokenPresentCount": 1, "unregisteredUserCount": 2,
            "users": [
              {"username": "localadmin", "uid": 502, "registered": 0, "tokensPresent": 0, "userPrincipalName": "", "probeStatus": "excluded", "tokenExpired": null},
              {"username": "sampleuser", "uid": 501, "registered": 1, "tokensPresent": 1, "userPrincipalName": "sampleuser@example.org", "tokenExpiration": "2026-11-01T00:00:00Z", "tokenExpired": false, "probeStatus": "ok"}
            ]
          },
          "secureTokenUsers": {
            "tokenGrantedCount": 1, "tokenMissingCount": 2, "totalUsersChecked": 3,
            "usersWithToken": ["sampleuser"], "usersWithoutToken": ["localadmin", "helper"]
          },
          "summary": {"adminUsers": 2, "currentlyLoggedIn": 1, "disabledUsers": 0, "totalUsers": 3},
          "users": [
            {"username": "sampleuser", "realName": "Sample User", "uid": 501, "gid": 20, "isAdmin": 1, "isDisabled": 0,
             "homeDirectory": "/Users/sampleuser", "shell": "/bin/zsh", "groupMembership": "admin, staff, everyone",
             "lastLogon": "", "sshAccess": 1, "screenSharingAccess": 0, "failedLoginCount": 0, "linkedAppleId": ""},
            {"username": "localadmin", "realName": "Local Admin", "uid": 502, "gid": 20, "isAdmin": "1", "isDisabled": 0,
             "lastLogon": "Jan 22 09:08 still logged in", "groupMembership": "admin"},
            {"username": "helper", "realName": "", "uid": "503", "gid": 20, "isAdmin": 0, "isDisabled": "true"}
          ]
        }
        """;

    private const string Security = """
        {
          "bootstrapToken": {"escrowed": 1, "supported": 1, "status": "escrowed"},
          "activationLock": {"status": "Disabled", "findMyMac": "Enabled", "email": "", "ownerDisplayName": ""},
          "fileVault": {"enabled": 1, "personalRecoveryKey": 1, "institutionalRecoveryKey": 0}
        }
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static MacIdentityInfo Read(string identity = Identity, string? security = Security) =>
        MacIdentityInfo.Read(Json(identity), security is null ? null : Json(security), "macOS")!;

    [Fact]
    public void Absent_ReturnsNull() => Assert.Null(MacIdentityInfo.Read(null));

    [Fact]
    public void Users_ReadFlagsAsZeroOneAndStrings()
    {
        var id = Read();
        Assert.True(id.IsMac);
        Assert.True(id.HasData);
        Assert.Equal(3, id.Users.Count);
        var sample = id.Users[0];
        Assert.Equal("Sample User", sample.RealName);
        Assert.Equal(501, sample.Uid);
        Assert.True(sample.IsAdmin);
        Assert.True(sample.SshAccess);
        Assert.Equal(["admin", "staff", "everyone"], sample.Groups);
        Assert.Null(sample.LastLogon);
        Assert.Null(sample.LinkedAppleId);
        Assert.True(id.Users[1].IsAdmin);
        var helper = id.Users[2];
        Assert.Equal(503, helper.Uid);
        Assert.Null(helper.RealName);
        Assert.True(helper.IsDisabled);
        Assert.False(helper.IsEnabled);
    }

    [Fact]
    public void Summary_AndDirectory()
    {
        var id = Read();
        Assert.Equal(3, id.Summary.TotalUsers);
        Assert.Equal(2, id.Summary.AdminUsers);
        Assert.NotNull(id.Directory);
        Assert.False(id.Directory!.HasMacBinding);
        Assert.Equal("/Local/Default", id.Directory.DirectoryNodes);
        Assert.Equal(80, id.Groups[0].Gid);
        Assert.Equal("admin", id.Groups[0].Name);
    }

    [Fact]
    public void Directory_BoundToActiveDirectory()
    {
        var id = Read("""{"directoryServices":{"activeDirectory":{"bound":"true","domain":"corp.example.org"},"ldap":{"bound":0}}}""", null);
        Assert.True(id.Directory!.HasMacBinding);
        Assert.True(id.Directory.AdBound);
        Assert.Equal("corp.example.org", id.Directory.AdDomain);
    }

    [Fact]
    public void Sessions_DropOrphanedTtyRows()
    {
        var id = Read();
        Assert.Equal(2, id.ActiveUserSessions.Count);
        Assert.Equal(["sampleuser"], id.UniqueLoggedInUsers);
        Assert.Equal(412, id.ActiveUserSessions[0].Pid);
        Assert.Equal("console", id.ActiveUserSessions[0].Tty);
    }

    [Fact]
    public void LoginHistory_UtmpxTypeIsALogon()
    {
        var id = Read();
        Assert.Equal("7", id.LoginHistory[0].EventType);
        Assert.Equal("Logon", MacIdentityInfo.EventLabel(id.LoginHistory[0].EventType));
        Assert.Equal("Failed", MacIdentityInfo.EventLabel(id.LoginHistory[1].EventType));
        Assert.Equal("ttys001", id.LoginHistory[0].Tty);
        Assert.Equal(new DateTime(2026, 10, 22, 2, 22, 15, DateTimeKind.Utc), MacIdentityInfo.ParseLoginTime(id.LoginHistory[0].LoginTime).Date);
    }

    [Fact]
    public void LoggedIn_OnlyFromAStillSuffix()
    {
        var id = Read();
        Assert.False(id.IsLoggedIn(id.Users[0]));
        Assert.True(id.IsLoggedIn(id.Users[1]));
    }

    [Fact]
    public void LoginTime_ParsesEachForm()
    {
        var now = new DateTime(2026, 6, 1);
        var noYear = MacIdentityInfo.ParseLoginTime("Jan 22 09:08 still", now);
        Assert.True(noYear.IsActive);
        Assert.Equal(new DateTime(2026, 1, 22, 9, 8, 0), noYear.Date);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792635735).UtcDateTime, MacIdentityInfo.ParseLoginTime("1792635735000").Date);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), MacIdentityInfo.ParseLoginTime("2026-03-04T05:06:07Z").Date);
        var junk = MacIdentityInfo.ParseLoginTime("sometime");
        Assert.Null(junk.Date);
        Assert.Equal("sometime", junk.Text);
        Assert.Equal("Unknown", MacIdentityInfo.ParseLoginTime(null).Text);
    }

    [Fact]
    public void PlatformSso_PrimaryUserHoldsTheTokens_NotTheFirst()
    {
        var p = Read().PlatformSSO!;
        Assert.True(p.Supported);
        Assert.True(p.DeviceRegistered);
        Assert.Equal("Example SSO", p.Provider);
        Assert.Equal(64800, p.LoginFrequency);
        Assert.Equal(["localadmin"], p.NonPlatformSsoAccounts);
        Assert.Equal(2, p.Users.Count);
        Assert.Null(p.Users[0].TokenExpired);
        var primary = p.PrimaryUser!;
        Assert.Equal("sampleuser", primary.Username);
        Assert.Equal("sampleuser@example.org", primary.DisplayName);
        Assert.False(primary.TokenExpired);
        Assert.Equal("2026-11-01T00:00:00Z", primary.TokenExpiration);
    }

    [Fact]
    public void SecureToken_AndMissingSummary()
    {
        var id = Read();
        Assert.True(id.HasSecureToken(id.Users[0]));
        Assert.False(id.HasSecureToken(id.Users[1]));
        Assert.Equal(1, id.SecureTokenUsers!.TokenGrantedCount);
        Assert.Equal("localadmin, helper", id.MissingTokenSummary);
        var many = Read("""{"secureTokenUsers":{"usersWithoutToken":["a","b","c","d","e"]}}""", null);
        Assert.Equal("a, b, c +2 more", many.MissingTokenSummary);
    }

    [Fact]
    public void SecurityModule_SuppliesBootstrapActivationLockAndRecoveryKeys()
    {
        var id = Read();
        Assert.Equal("Escrowed", id.BootstrapToken!.Label);
        Assert.False(id.ActivationLock!.Locked);
        Assert.True(id.ActivationLock.FindMyMac);
        Assert.Null(id.ActivationLock.Account);
        Assert.True(id.FileVault!.Personal);
        Assert.False(id.FileVault.Institutional);

        var other = Read(Identity, """{"bootstrapToken":{"escrowed":0,"supported":"true"},"activationLock":{"status":"Likely Enabled","findMyMac":"Disabled","ownerDisplayName":"Sample Owner"}}""");
        Assert.Equal("Not Escrowed", other.BootstrapToken!.Label);
        Assert.True(other.ActivationLock!.Locked);
        Assert.Equal("Sample Owner", other.ActivationLock.Account);
        Assert.Null(other.FileVault);
    }

    [Fact]
    public void NoSecurityModule_LeavesCrossModuleFactsEmpty()
    {
        var id = Read(Identity, null);
        Assert.Null(id.BootstrapToken);
        Assert.Null(id.ActivationLock);
        Assert.Null(id.FileVault);
    }

    [Fact]
    public void Btmdb_AndSessionSummary()
    {
        var id = Read("""
            {
              "btmdbHealth": {"exists": 1, "sizeMB": "3.21", "status": "warning", "jetsamKillsLast7Days": "60", "registeredItemCount": 140, "localUserCount": 3},
              "sessionSummary": {"totalSessions": 12, "uniqueUsers": 2, "avgSessionMinutes": 95, "medianSessionMinutes": 30.4,
                                 "sessionsByHour": {"09": 5, "10": 5, "14": 2, "08": 1, "16": 1, "17": 1}},
              "sessionHistory": [{"username": "sampleuser", "timestamp": "1792635735", "durationMinutes": 125, "eventType": "Active"}]
            }
            """, null);
        Assert.True(id.IsMac);
        var b = id.Btmdb!;
        Assert.Equal(3.21, b.SizeMB, 3);
        Assert.Equal("warning", b.Status);
        Assert.Equal(60, b.JetsamKillsLast7Days);
        Assert.Equal("/private/var/db/com.apple.backgroundtaskmanagement", b.Path);
        var s = id.Sessions!;
        Assert.Equal(12, s.TotalSessions);
        Assert.Equal(["09", "10", "14", "08", "16"], s.PeakHours.Select(p => p.Hour));
        Assert.Equal("2.1h", id.SessionHistory[0].DurationText);
        Assert.Equal("30m", MacIdentityInfo.Minutes(30.4));
    }

    [Fact]
    public void SingletonArray_IsUnwrapped()
    {
        var id = MacIdentityInfo.Read(Json("[" + Identity + "]"), Json("[" + Security + "]"))!;
        Assert.Equal(3, id.Users.Count);
        Assert.NotNull(id.BootstrapToken);
        Assert.True(id.IsMac);
    }

    [Fact]
    public void EmptyObject_HasNoData()
    {
        var id = MacIdentityInfo.Read(Json("{}"))!;
        Assert.False(id.HasData);
        Assert.Empty(id.Users);
        Assert.Null(id.PlatformSSO);
    }
}
