// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ReportMate.App.Services.MacModules.MacJson;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A Mac's identity module, read the way the Mac app's IdentityInfo.swift reads it:
/// accounts, groups, sessions and login history from the identity module, plus the
/// bootstrap token, Activation Lock and FileVault recovery keys the Identity tab
/// borrows from the security module. The Mac branches only: Windows enrollment and
/// Administrators-group enrichment belong to the Windows tab.
/// </summary>
public sealed class MacIdentityInfo
{
    public sealed record UserAccount(
        string Username, string? RealName, int Uid, int Gid, string? HomeDirectory, string? Shell, string? Uuid,
        string? AccountType, bool IsAdmin, bool IsEnabled, bool IsDisabled, bool? SshAccess, bool? ScreenSharingAccess,
        bool? AutoLoginEnabled, string? CreationTime, string? PasswordLastSet, string? LastLogon, int FailedLoginCount,
        string? LastFailedLogin, string? LinkedAppleId, string? GroupMembership)
    {
        public string Key => Username + Uid.ToString(CultureInfo.InvariantCulture);

        public IReadOnlyList<string> Groups => (GroupMembership ?? "").Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToList();

        internal static UserAccount From(JsonElement u)
        {
            var isLocal = Bool(u, "isLocal") ?? false;
            var groups = Node(u, "groupMemberships") is { ValueKind: JsonValueKind.Array } arr
                ? string.Join(", ", arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
                : Str(u, "groupMembership");
            return new UserAccount(
                Username: Str(u, "username", "user") ?? "",
                RealName: Str(u, "realName", "fullName", "description"),
                Uid: Int(u, "uid") ?? 0,
                Gid: Int(u, "gid") ?? 0,
                HomeDirectory: Str(u, "homeDirectory", "directory"),
                Shell: Str(u, "shell"),
                Uuid: Str(u, "uuid"),
                AccountType: Str(u, "accountType") ?? (isLocal ? "Local" : "Domain"),
                IsAdmin: Bool(u, "isAdmin") ?? false,
                IsEnabled: Bool(u, "isEnabled") ?? (Bool(u, "isDisabled") is { } d ? !d : true),
                IsDisabled: Bool(u, "isDisabled") ?? false,
                SshAccess: Bool(u, "sshAccess"),
                ScreenSharingAccess: Bool(u, "screenSharingAccess"),
                AutoLoginEnabled: Bool(u, "autoLoginEnabled"),
                CreationTime: Str(u, "creationTime", "accountCreated", "createdAt"),
                PasswordLastSet: Str(u, "passwordLastSet"),
                LastLogon: Str(u, "lastLogon", "time"),
                FailedLoginCount: Int(u, "failedLoginCount") ?? 0,
                LastFailedLogin: Str(u, "lastFailedLogin"),
                LinkedAppleId: Str(u, "linkedAppleId"),
                GroupMembership: string.IsNullOrWhiteSpace(groups) ? null : groups);
        }
    }

    public sealed record UserGroup(string Name, int Gid, string? Members, string? Comment)
    {
        internal static UserGroup From(JsonElement g) => new(
            Str(g, "groupname", "groupName", "name") ?? "",
            Int(g, "gid") ?? 0,
            Node(g, "members") is { ValueKind: JsonValueKind.Array } arr
                ? string.Join(", ", arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
                : Str(g, "members"),
            Str(g, "comment", "description"));
    }

    public sealed record LoggedInUser(string User, string? Tty, string? Host, string? Time, int? Pid, string? LoginTime)
    {
        internal static LoggedInUser From(JsonElement s) => new(
            Str(s, "user", "username") ?? "", Str(s, "tty"), Str(s, "host", "domain"),
            Str(s, "time", "loginTime"), Int(s, "pid", "sessionId"), Str(s, "loginTime", "time"));
    }

    public sealed record LoginHistoryEntry(string Username, string? Tty, string? LoginTime, string? LogoutTime, string? Duration, string? EventType, string? SourceIp)
    {
        internal static LoginHistoryEntry From(JsonElement e) => new(
            Str(e, "username", "user") ?? "", Str(e, "tty"), Str(e, "loginTime", "time"), Str(e, "logoutTime"),
            Str(e, "duration"), Str(e, "eventType", "type"), Str(e, "sourceIp", "host"));
    }

    public sealed record BtmdbHealth(bool Exists, string Path, double SizeMB, string Status, string StatusMessage,
        int JetsamKillsLast7Days, string? LastJetsamEvent, int RegisteredItemCount, int LocalUserCount)
    {
        internal static BtmdbHealth From(JsonElement b) => new(
            Bool(b, "exists") ?? false,
            Str(b, "path") ?? "/private/var/db/com.apple.backgroundtaskmanagement",
            Num(b, "sizeMB", "sizeMb") ?? 0,
            Str(b, "status") ?? "unknown",
            Str(b, "statusMessage") ?? "",
            Int(b, "jetsamKillsLast7Days") ?? 0,
            Str(b, "lastJetsamEvent"),
            Int(b, "registeredItemCount") ?? 0,
            Int(b, "localUserCount") ?? 0);
    }

    public sealed record DirectoryServices(bool AdBound, string? AdDomain, bool LdapBound, string? LdapServer, string? DirectoryNodes)
    {
        /// <summary>Anything worth a Directory Services card on a Mac.</summary>
        public bool HasMacBinding => AdBound || LdapBound || AdDomain is not null || LdapServer is not null;

        internal static DirectoryServices From(JsonElement ds)
        {
            var ad = Node(ds, "activeDirectory", "active_directory");
            return new DirectoryServices(
                (Bool(ad, "bound") ?? false) || (Bool(ad, "isDomainJoined") ?? false),
                Str(ad, "domain", "domainName"),
                Bool(ds, "ldap.bound") ?? false,
                Str(ds, "ldap.server"),
                Str(ds, "directoryNodes"));
        }
    }

    public sealed record SecureToken(IReadOnlyList<string> UsersWithToken, IReadOnlyList<string> UsersWithoutToken,
        int TotalUsersChecked, int TokenGrantedCount, int TokenMissingCount)
    {
        internal static SecureToken From(JsonElement s) => new(
            Strings(s, "usersWithToken"), Strings(s, "usersWithoutToken"),
            Int(s, "totalUsersChecked") ?? 0, Int(s, "tokenGrantedCount") ?? 0, Int(s, "tokenMissingCount") ?? 0);
    }

    public sealed record PlatformSsoUser(string Username, int? Uid, bool Registered, string? UserPrincipalName, string? LoginUserName,
        string? State, string? LastLoginDate, bool TokensPresent, string? TokenReceived, string? TokenExpiration, bool? TokenExpired, string? ProbeStatus)
    {
        public string? DisplayName => UserPrincipalName ?? LoginUserName ?? (Username.Length == 0 ? null : Username);

        internal static PlatformSsoUser From(JsonElement u) => new(
            Str(u, "username") ?? "", Int(u, "uid"), Bool(u, "registered") ?? false, Str(u, "userPrincipalName"),
            Str(u, "loginUserName"), Str(u, "state"), Str(u, "lastLoginDate"), Bool(u, "tokensPresent") ?? false,
            Str(u, "tokenReceived"), Str(u, "tokenExpiration"), Bool(u, "tokenExpired"), Str(u, "probeStatus"));
    }

    public sealed record PlatformSso(bool Supported, bool DeviceRegistered, string? Provider, string? Method, string? ExtensionIdentifier,
        string? OrganizationName, int LoginFrequency, string? OfflineGracePeriod, int RegisteredUserCount, int UnregisteredUserCount,
        int TokenPresentCount, IReadOnlyList<string> NonPlatformSsoAccounts, IReadOnlyList<PlatformSsoUser> Users)
    {
        /// <summary>The account holding the registration: tokens first, then registered. Never the first user.</summary>
        public PlatformSsoUser? PrimaryUser => Users.FirstOrDefault(u => u.TokensPresent) ?? Users.FirstOrDefault(u => u.Registered);

        internal static PlatformSso From(JsonElement p) => new(
            Bool(p, "supported") ?? false, Bool(p, "deviceRegistered") ?? false, Str(p, "provider"), Str(p, "method"),
            Str(p, "extensionIdentifier"), Str(p, "organizationName"), Int(p, "loginFrequency") ?? 0, Str(p, "offlineGracePeriod"),
            Int(p, "registeredUserCount") ?? 0, Int(p, "unregisteredUserCount") ?? 0, Int(p, "tokenPresentCount") ?? 0,
            Strings(p, "nonPlatformSSOAccounts"), Arr(p, "users").Select(PlatformSsoUser.From).ToList());
    }

    public sealed record SessionHistoryEntry(string Username, string Timestamp, string? EndTime, string? Duration, double? DurationMinutes,
        int EventId, string EventType, string? SourceAddress)
    {
        public string DurationText => Duration ?? (DurationMinutes is { } m ? Minutes(m) : "—");

        internal static SessionHistoryEntry From(JsonElement e) => new(
            Str(e, "username", "user") ?? "", Str(e, "timestamp", "time") ?? "", Str(e, "endTime"), Str(e, "duration"),
            Num(e, "durationMinutes"), Int(e, "eventId") ?? 0, Str(e, "eventType") ?? "Unknown", Str(e, "sourceAddress"));
    }

    public sealed record SessionSummary(int TotalSessions, int UniqueUsers, double AvgSessionMinutes, double MedianSessionMinutes,
        IReadOnlyDictionary<string, int> SessionsByHour)
    {
        /// <summary>Top five hours by session count.</summary>
        public IReadOnlyList<(string Hour, int Count)> PeakHours => SessionsByHour
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(5).Select(kv => (kv.Key, kv.Value)).ToList();

        internal static SessionSummary From(JsonElement s)
        {
            var byHour = new Dictionary<string, int>();
            if (Node(s, "sessionsByHour") is { ValueKind: JsonValueKind.Object } h)
                foreach (var p in h.EnumerateObject())
                    if (Int(h, p.Name) is { } n) byHour[p.Name] = n;
            return new SessionSummary(Int(s, "totalSessions") ?? 0, Int(s, "uniqueUsers") ?? 0,
                Num(s, "avgSessionMinutes") ?? 0, Num(s, "medianSessionMinutes") ?? 0, byHour);
        }
    }

    public sealed record IdentitySummary(int TotalUsers, int AdminUsers, int DisabledUsers, int CurrentlyLoggedIn)
    {
        internal static IdentitySummary From(JsonElement? s) => new(
            Int(s, "totalUsers") ?? 0, Int(s, "adminUsers") ?? 0, Int(s, "disabledUsers") ?? 0, Int(s, "currentlyLoggedIn") ?? 0);
    }

    /// <summary>security.bootstrapToken as the Tokens card reads it.</summary>
    public sealed record BootstrapTokenState(bool Escrowed, bool Supported)
    {
        public string Label => Escrowed ? "Escrowed" : Supported ? "Not Escrowed" : "Not Supported";
    }

    /// <summary>security.activationLock as the Authentication card reads it.</summary>
    public sealed record ActivationLockState(string? Status, bool Locked, bool FindMyMac, string? Account);

    /// <summary>The FileVault recovery keys the Tokens card shows.</summary>
    public sealed record RecoveryKeys(bool Personal, bool Institutional);

    public IReadOnlyList<UserAccount> Users { get; private init; } = [];
    public IReadOnlyList<UserGroup> Groups { get; private init; } = [];
    public IReadOnlyList<LoggedInUser> LoggedInUsers { get; private init; } = [];
    public IReadOnlyList<LoginHistoryEntry> LoginHistory { get; private init; } = [];
    public BtmdbHealth? Btmdb { get; private init; }
    public DirectoryServices? Directory { get; private init; }
    public SecureToken? SecureTokenUsers { get; private init; }
    public PlatformSso? PlatformSSO { get; private init; }
    public IReadOnlyList<SessionHistoryEntry> SessionHistory { get; private init; } = [];
    public SessionSummary? Sessions { get; private init; }
    public IdentitySummary Summary { get; private init; } = new(0, 0, 0, 0);
    public BootstrapTokenState? BootstrapToken { get; private init; }
    public ActivationLockState? ActivationLock { get; private init; }
    public RecoveryKeys? FileVault { get; private init; }
    public bool IsMac { get; private init; }
    public bool HasData { get; private init; }

    /// <summary>Reads the identity module, with the security module for the cross-module facts.</summary>
    public static MacIdentityInfo? Read(JsonElement? identity, JsonElement? security = null, string? platform = null)
    {
        var id = Unwrap(identity);
        if (id is null) return null;
        var sec = Unwrap(security);
        var hasData = id.Value.ValueKind switch
        {
            JsonValueKind.Object => id.Value.EnumerateObject().Any(),
            JsonValueKind.Array => id.Value.GetArrayLength() > 0,
            _ => false,
        };
        var looksMac = Node(id, "btmdbHealth") is not null || Node(id, "secureTokenUsers") is not null || Node(id, "platformSSOUsers") is not null;

        var bt = Node(sec, "bootstrapToken");
        var lockNode = Node(sec, "activationLock");
        var fv = Node(sec, "fileVault");
        ActivationLockState? activation = null;
        if (lockNode is not null)
        {
            var status = Str(lockNode, "status");
            activation = new ActivationLockState(status, status is "Enabled" or "Likely Enabled",
                Str(lockNode, "findMyMac") == "Enabled", Str(lockNode, "email") ?? Str(lockNode, "ownerDisplayName"));
        }

        return new MacIdentityInfo
        {
            HasData = hasData,
            IsMac = (platform ?? "").Contains("mac", StringComparison.OrdinalIgnoreCase) || looksMac,
            Users = Arr(id, "users").Select(UserAccount.From).ToList(),
            Groups = Arr(id, "groups").Select(UserGroup.From).ToList(),
            LoggedInUsers = Arr(id, "loggedInUsers").Select(LoggedInUser.From).ToList(),
            LoginHistory = Arr(id, "loginHistory").Select(LoginHistoryEntry.From).ToList(),
            Btmdb = Node(id, "btmdbHealth") is { } b ? BtmdbHealth.From(b) : null,
            Directory = Node(id, "directoryServices") is { } ds ? DirectoryServices.From(ds) : null,
            SecureTokenUsers = Node(id, "secureTokenUsers") is { } st ? SecureToken.From(st) : null,
            PlatformSSO = Node(id, "platformSSOUsers") is { } p ? PlatformSso.From(p) : null,
            SessionHistory = Arr(id, "sessionHistory").Select(SessionHistoryEntry.From).ToList(),
            Sessions = Node(id, "sessionSummary") is { } ss ? SessionSummary.From(ss) : null,
            Summary = IdentitySummary.From(Node(id, "summary")),
            BootstrapToken = bt is null ? null : new BootstrapTokenState(Bool(bt, "escrowed") ?? false, Bool(bt, "supported") ?? false),
            ActivationLock = activation,
            FileVault = fv is null ? null : new RecoveryKeys(On(fv, "personalRecoveryKey"), On(fv, "institutionalRecoveryKey")),
        };
    }

    /// <summary>Sessions with a real user, not the orphaned TTY rows utmpx leaves behind.</summary>
    public IReadOnlyList<LoggedInUser> ActiveUserSessions => LoggedInUsers.Where(s => s.User.Trim().Length > 0).ToList();

    public IReadOnlyList<string> UniqueLoggedInUsers => ActiveUserSessions.Select(s => s.User).Distinct().ToList();

    /// <summary>On a Mac every account is listed; the Windows service accounts are the Windows tab's concern.</summary>
    public IReadOnlyList<UserAccount> VisibleUsers => Users;

    /// <summary>
    /// A Mac account counts as logged in only when its own last-logon says "still";
    /// the Mac app does not infer it from the session list.
    /// </summary>
    public bool IsLoggedIn(UserAccount user) => ParseLoginTime(user.LastLogon).IsActive;

    public bool HasSecureToken(UserAccount user) => SecureTokenUsers?.UsersWithToken.Contains(user.Username) ?? false;

    /// <summary>Accounts missing a SecureToken, as the Tokens card's footnote: three names, then "+N more".</summary>
    public string? MissingTokenSummary
    {
        get
        {
            var missing = SecureTokenUsers?.UsersWithoutToken ?? [];
            if (missing.Count == 0) return null;
            return string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? $" +{missing.Count - 3} more" : "");
        }
    }

    public static string Minutes(double m) =>
        m > 60 ? (m / 60).ToString("0.0", CultureInfo.InvariantCulture) + "h" : $"{(int)Math.Round(m, MidpointRounding.AwayFromZero)}m";

    /// <summary>
    /// The history pill for an event type. The Mac client reports utmpx record types
    /// ("7" for a login); anything not named falls back to Logon, as on the web and Mac.
    /// </summary>
    public static string EventLabel(string? type) => type switch
    {
        "Failed" or "Logoff" or "Disconnect" or "Active" or "Reconnect" => type,
        _ => "Logon",
    };

    public readonly record struct LoginTimeParsed(string Text, bool IsActive, DateTime? Date);

    /// <summary>
    /// The Identity tab's formatDate: UNIX seconds or milliseconds, ISO strings, "Jan 22 09:08"
    /// without a year (this year is assumed), and a "still" suffix marking an open session.
    /// Text is the input itself when nothing parses; the caller formats Date.
    /// </summary>
    public static LoginTimeParsed ParseLoginTime(string? value, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(value)) return new("Unknown", false, null);
        var cleaned = value;
        var active = false;
        if (value.Contains("still", StringComparison.OrdinalIgnoreCase))
        {
            active = true;
            cleaned = Regex.Replace(value, @"\s*still\s*", "", RegexOptions.IgnoreCase).Trim();
        }
        DateTime? date = null;
        if (Regex.IsMatch(cleaned, @"^\d{10,}$") && double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            var seconds = n > 9_999_999_999 ? n / 1000 : n;
            if (seconds * 1000 is var ms && ms <= 253_402_300_799_999)
                date = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
        }
        else if (Regex.IsMatch(cleaned, @"^[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}$"))
        {
            var year = (now ?? DateTime.Now).Year;
            var normalized = Regex.Replace(cleaned, @"\s+", " ") + " " + year.ToString(CultureInfo.InvariantCulture);
            if (DateTime.TryParseExact(normalized, "MMM d HH:mm yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                date = d;
        }
        else if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var iso))
        {
            date = iso;
        }
        return new(value, active, date);
    }

    private static JsonElement? Unwrap(JsonElement? e) => e switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.Array } a when a.GetArrayLength() == 1 => a[0],
        _ => e,
    };

    private static IReadOnlyList<string> Strings(JsonElement? parent, string path) =>
        Arr(parent, path).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToList();

    /// <summary>SecurityInfo.on: true, 1 or "true", nothing looser.</summary>
    private static bool On(JsonElement? parent, string path) => Node(parent, path) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.Number } v => v.TryGetInt32(out var i) && i == 1,
        { ValueKind: JsonValueKind.String } v => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
