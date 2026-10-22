using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;
using static ReportMate.App.Services.MacModules.MacIdentityInfo;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Identity tab, as the Mac app's IdentityTab.swift and the web's
/// IdentityTab.tsx draw it: the Users, Directory Services, Authentication (Platform
/// SSO, Activation Lock) and Tokens (bootstrap, SecureToken, recovery keys) cards,
/// the Background Task Management DB footnote, then the user accounts, active
/// sessions, login history and session history tables.
/// </summary>
public static class MacIdentityView
{
    private const string Glyph = "\uE77B";
    private const Accent TabAccent = Accent.Indigo;

    private sealed record SessionRow(string User, string Tty, string Host, string LoginTime, string Pid);
    private sealed record HistoryRow(string User, string Event, Tone EventTone, string Time, string Duration, string Source);
    private sealed record SessionHistoryRow(string User, string Event, Tone EventTone, string Start, string End, string Duration, string Source);

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("identity", out var raw)) return null;
        s.RawModules.TryGetValue("security", out var security);
        var id = Read(raw, s.RawModules.ContainsKey("security") ? security : null, s.Platform ?? "macOS");
        if (id is null) return null;

        var page = new StackPanel();
        void Add(UIElement e, double top = 24) { if (e is FrameworkElement fe && page.Children.Count > 0) fe.Margin = new Thickness(0, top, 0, 0); page.Children.Add(e); }

        var countBadge = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        countBadge.Children.Add(Ui.Caption("User Accounts"));
        var count = Ui.Text(id.Users.Count.ToString()); count.FontSize = 22; count.FontWeight = FontWeights.Bold; count.TextAlignment = TextAlignment.Right;
        countBadge.Children.Add(count);
        Add(Ui.TabHeader("Identity & Users", "macOS user accounts and sessions", Glyph, TabAccent, countBadge), 0);

        if (!id.HasData && id.Users.Count == 0)
        {
            Add(Ui.EmptyCard("No Identity Data", "User account information is not available for this device.", Glyph), 0);
            return page;
        }

        var grid = Ui.CardGrid(340, 20);
        grid.Children.Add(UsersCard(id));
        if (id.Directory is { HasMacBinding: true } ds) grid.Children.Add(DirectoryCard(ds));
        if (id.PlatformSSO is not null || id.ActivationLock is not null) grid.Children.Add(AuthenticationCard(id));
        if (id.SecureTokenUsers is not null || id.BootstrapToken is not null) grid.Children.Add(TokensCard(id));
        Add(grid, 0);

        if (id.Btmdb is { } btm) Add(BtmdbFootnote(btm), 20);

        // Table tabs; the view is rebuilt per snapshot, so its state lives here.
        var table = "users";
        var search = "";
        var adminsOnly = false;
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Render()
        {
            var options = new List<(string, string)>
            {
                ("users", $"User Accounts ({FilterUsers(id, search, adminsOnly).Count})"),
                ("sessions", $"Active Sessions ({id.ActiveUserSessions.Count})"),
                ("history", $"Login History ({id.LoginHistory.Count})"),
            };
            if (id.SessionHistory.Count > 0) options.Add(("sessionHistory", $"Session History ({id.SessionHistory.Count})"));
            var panel = new StackPanel();
            var seg = Ui.Segmented(options, table, key => { table = key; Render(); });
            seg.Margin = new Thickness(0, 0, 0, 14);
            panel.Children.Add(seg);
            panel.Children.Add(table switch
            {
                "sessions" => SessionsTable(id),
                "history" => HistoryTable(id),
                "sessionHistory" => SessionHistoryTable(id),
                _ => UsersTable(id, search, adminsOnly, expanded,
                    q => { if (q == search) return; search = q; Render(); }, () => { adminsOnly = !adminsOnly; Render(); }, Render),
            });
            host.Content = panel;
        }
        Render();
        Add(host);

        Add(Ui.Collapsible("device.modules.identity", JsonTree.Build(raw)), 20);
        return page;
    }

    // ── Cards ────────────────────────────────────────────────────────

    private static Border CardShell(string title, StackPanel body)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Ui.Icon(Glyph, 16, Ui.Brush("TextSecondaryBrush")));
        var t = Ui.SectionHeader(title); t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(t);
        var root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(body);
        return Ui.Card(root, new Thickness(20, 16, 20, 16));
    }

    private static Grid Detail(string label, string? value, Tone? tone = null)
        => Ui.Row(label, tone is null ? (UIElement)Value(value) : Ui.Status(Format.OrUnknown(value), tone.Value));

    private static TextBlock Value(string? value)
    {
        var t = Ui.Text(Format.OrUnknown(value)); t.Margin = new Thickness(0); t.FontWeight = FontWeights.Medium; t.TextAlignment = TextAlignment.Right; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; t.MaxWidth = 220; t.ToolTip = value;
        return t;
    }

    private static Border UsersCard(MacIdentityInfo id)
    {
        var sum = id.Summary;
        var body = new StackPanel();
        body.Children.Add(Detail("Total Users", sum.TotalUsers.ToString()));
        body.Children.Add(Detail("Admin Users", sum.AdminUsers.ToString(), sum.AdminUsers > 0 ? Tone.Warning : Tone.Success));
        body.Children.Add(Detail("Disabled", sum.DisabledUsers.ToString(), sum.DisabledUsers > 0 ? Tone.Neutral : Tone.Success));
        return CardShell("Users", body);
    }

    private static Border DirectoryCard(DirectoryServices ds)
    {
        var body = new StackPanel();
        body.Children.Add(Detail("Active Directory", ds.AdBound ? "Bound" : "Not Bound", ds.AdBound ? Tone.Success : Tone.Neutral));
        if (ds.AdDomain is { } d) body.Children.Add(Detail("AD Domain", d));
        body.Children.Add(Detail("LDAP", ds.LdapBound ? "Bound" : "Not Bound", ds.LdapBound ? Tone.Success : Tone.Neutral));
        return CardShell("Directory Services", body);
    }

    private static Border AuthenticationCard(MacIdentityInfo id)
    {
        var body = new StackPanel();
        if (id.PlatformSSO is { } psso)
        {
            body.Children.Add(Detail("Platform SSO", psso.Provider ?? "Not configured"));
            body.Children.Add(Detail("Registration", psso.DeviceRegistered ? "Registered" : "Not Registered", psso.DeviceRegistered ? Tone.Success : Tone.Warning));
            var user = psso.PrimaryUser;
            if (user?.DisplayName is { } name) body.Children.Add(Detail("SSO User", name));
            if (psso.Users.Count > 0)
            {
                var tokens = user?.TokensPresent ?? false;
                body.Children.Add(Detail("SSO Token", tokens ? "Present" : "Missing", tokens ? Tone.Success : Tone.Warning));
            }
            if (user is { TokensPresent: true, TokenExpiration: { } exp })
                body.Children.Add(Detail("Token Expires", exp, user.TokenExpired == true ? Tone.Warning : Tone.Success));
        }
        if (id.ActivationLock is { } al)
        {
            if (id.PlatformSSO is not null) body.Children.Add(Ui.Divider());
            body.Children.Add(Detail("Activation Lock", al.Locked ? "Locked" : "Unlocked", al.Locked ? Tone.Warning : Tone.Success));
            body.Children.Add(Detail("Find My Mac", al.FindMyMac ? "Enabled" : "Disabled", al.FindMyMac ? Tone.Success : Tone.Neutral));
            if (al.Account is { } account) body.Children.Add(Detail("iCloud Account", account));
        }
        return CardShell("Authentication", body);
    }

    private static Border TokensCard(MacIdentityInfo id)
    {
        var body = new StackPanel();
        if (id.BootstrapToken is { } bt)
            body.Children.Add(Detail("Bootstrap Token", bt.Label, bt.Escrowed ? Tone.Success : bt.Supported ? Tone.Warning : Tone.Neutral));
        if (id.SecureTokenUsers is { } st)
            body.Children.Add(Detail("Secure Token Users", $"{st.TokenGrantedCount} of {st.TotalUsersChecked}", st.TokenMissingCount > 0 ? Tone.Warning : Tone.Success));
        if (id.FileVault is { } fv)
        {
            body.Children.Add(Ui.Divider());
            body.Children.Add(Detail("Personal Recovery Key", fv.Personal ? "Escrowed" : "Not Escrowed", fv.Personal ? Tone.Success : Tone.Warning));
            body.Children.Add(Detail("Institutional Recovery Key", fv.Institutional ? "Escrowed" : "Not Escrowed", fv.Institutional ? Tone.Success : Tone.Neutral));
        }
        if (id.MissingTokenSummary is { } missing)
        {
            var note = Ui.Status($"Missing token: {missing}", Tone.Warning, 12);
            note.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(new Border
            {
                Background = Ui.Tint(Tone.Warning, 0.1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0), Child = note,
            });
        }
        return CardShell("Tokens", body);
    }

    private static UIElement BtmdbFootnote(BtmdbHealth b)
    {
        var tone = b.Status switch { "healthy" => Tone.Success, "warning" => Tone.Warning, "critical" => Tone.Error, _ => Tone.Neutral };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Ui.Text("Background Task Management DB:"); title.Margin = new Thickness(0); title.FontWeight = FontWeights.Medium;
        header.Children.Add(title);
        var size = Ui.Status($"{b.SizeMB:F2} MB", tone, 13); size.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(size);
        if (b.Status != "healthy")
        {
            var warn = Ui.Status("⚠", b.Status == "critical" ? Tone.Error : Tone.Warning, 12); warn.Margin = new Thickness(6, 0, 0, 0);
            header.Children.Add(warn);
        }

        var details = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var stats = new WrapPanel();
        void Stat(string label, string value, Tone? t = null)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 24, 4) };
            p.Children.Add(Ui.Caption($"{label}:"));
            var v = t is { } tt ? Ui.Status(value, tt, 12) : Ui.Text(value);
            v.Margin = new Thickness(4, 0, 0, 0); v.FontSize = 12; v.FontWeight = FontWeights.Medium;
            p.Children.Add(v);
            stats.Children.Add(p);
        }
        Stat("Status", Format.Capitalize(b.Status), tone);
        Stat("Jetsam Kills (7d)", b.JetsamKillsLast7Days.ToString(), b.JetsamKillsLast7Days > 100 ? Tone.Error : b.JetsamKillsLast7Days > 50 ? Tone.Warning : null);
        Stat("Registered Items", b.RegisteredItemCount.ToString());
        Stat("Local Users", b.LocalUserCount.ToString());
        details.Children.Add(stats);
        if (b.Status == "critical" && b.StatusMessage.Length > 0)
        {
            var msg = Ui.Status(b.StatusMessage, Tone.Error, 12); msg.TextWrapping = TextWrapping.Wrap;
            details.Children.Add(msg);
        }
        details.Children.Add(Ui.Caption("Thresholds: Warning >3MB, Critical >3.5MB, Failure >4MB"));

        var expander = new Expander { Header = header, Content = details, IsExpanded = false, Foreground = Ui.Brush("TextPrimaryBrush") };
        return new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = tone is Tone.Error ? Ui.Tint(Tone.Error, 0.06) : tone is Tone.Warning ? Ui.Tint(Tone.Warning, 0.08) : Ui.Brush("SubtleFillBrush"),
            BorderBrush = tone is Tone.Neutral ? Ui.Brush("DividerBrush") : Ui.Tint(tone, 0.35),
            Child = expander,
        };
    }

    // ── Tables ───────────────────────────────────────────────────────

    /// <summary>Login time as the tables show it: the parsed date formatted, or the text as sent.</summary>
    private static string When(string? value)
    {
        var p = ParseLoginTime(value);
        return p.Date is { } d ? Format.ShortDateTime(d) : p.Text;
    }

    private static List<UserAccount> FilterUsers(MacIdentityInfo id, string search, bool adminsOnly)
    {
        var q = search.Trim();
        return id.VisibleUsers
            .Where(u => (q.Length == 0 || u.Username.Contains(q, StringComparison.OrdinalIgnoreCase) || (u.RealName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) && (!adminsOnly || u.IsAdmin))
            .OrderByDescending(u => id.IsLoggedIn(u) ? 1 : 0)
            .ThenByDescending(u => ParseLoginTime(u.LastLogon).Date ?? DateTime.MinValue)
            .ThenByDescending(u => u.LastLogon ?? "", StringComparer.Ordinal)
            .ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static UIElement Check(bool on) => on ? Ui.Status("Yes", Tone.Success, 12) : Ui.Caption("—");

    private static UIElement UsersTable(MacIdentityInfo id, string search, bool adminsOnly, HashSet<string> expandedUsers,
        Action<string> onSearch, Action toggleAdmins, Action rerender)
    {
        var rows = FilterUsers(id, search, adminsOnly);
        var showBootstrap = id.BootstrapToken is not null;

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var admins = Ui.Pill("Admins", Tone.Warning);
        admins.Cursor = Cursors.Hand; admins.BorderThickness = new Thickness(2); admins.BorderBrush = adminsOnly ? Ui.StatusBrush(Tone.Warning) : Brushes.Transparent;
        admins.MouseLeftButtonUp += (_, _) => toggleAdmins();
        controls.Children.Add(admins);
        controls.Children.Add(new SearchBox("Search users...", onSearch) { Text = search });

        var cols = new List<(string Header, GridLength Width)>
        {
            ("Display Name", new GridLength(1.2, GridUnitType.Star)),
            ("UID", new GridLength(60)),
            ("Admin", new GridLength(80)),
            ("Session", new GridLength(80)),
            ("Username", new GridLength(1, GridUnitType.Star)),
            ("Last Login", new GridLength(170)),
            ("Secure Token", new GridLength(100)),
        };
        if (showBootstrap) cols.Add(("Bootstrap", new GridLength(90)));

        var header = new Grid { Margin = new Thickness(20, 8, 20, 8) };
        for (var c = 0; c < cols.Count; c++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[c].Width });
            var t = new TextBlock { Text = cols[c].Header.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("TextSecondaryBrush") };
            Grid.SetColumn(t, c); header.Children.Add(t);
        }
        var list = new StackPanel();
        list.Children.Add(new Border { Background = Ui.Brush("SubtleFillBrush"), BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = header });
        if (rows.Count == 0) list.Children.Add(Ui.EmptyState("No users found matching your criteria"));

        foreach (var u in rows)
        {
            var key = u.Key;
            var isOpen = expandedUsers.Contains(key);
            var loggedIn = id.IsLoggedIn(u);
            var lastLogin = u.LastLogon is null ? "Never" : When(u.LastLogon);
            var g = new Grid { Margin = new Thickness(20, 9, 20, 9) };
            foreach (var col in header.ColumnDefinitions) g.ColumnDefinitions.Add(new ColumnDefinition { Width = col.Width });
            void Cell(UIElement e, int c) { Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(e); }

            var dn = Ui.Text(Format.OrDash(u.RealName)); dn.Margin = new Thickness(0); dn.FontWeight = FontWeights.Medium; dn.TextTrimming = TextTrimming.CharacterEllipsis; dn.TextWrapping = TextWrapping.NoWrap;
            Cell(dn, 0);
            var uid = Ui.Mono(u.Uid.ToString()); uid.Margin = new Thickness(0); uid.Foreground = Ui.Brush("TextSecondaryBrush");
            Cell(uid, 1);
            Cell(u.IsAdmin ? Ui.Pill("Admin", Tone.Warning) : Ui.Caption("—"), 2);
            Cell(loggedIn ? Ui.Pill("Active", Tone.Success) : Ui.Caption("—"), 3);
            var un = new StackPanel { Orientation = Orientation.Horizontal };
            var unText = Ui.Mono(u.Username); unText.Margin = new Thickness(0); unText.TextTrimming = TextTrimming.CharacterEllipsis; unText.TextWrapping = TextWrapping.NoWrap;
            if (u.IsDisabled) unText.Foreground = Ui.Brush("TextSecondaryBrush");
            un.Children.Add(unText);
            if (u.IsDisabled) { var d = Ui.Pill("Disabled", Tone.Neutral, 10.5); d.Margin = new Thickness(6, 0, 0, 0); un.Children.Add(d); }
            Cell(un, 4);
            var ll = Ui.Caption(lastLogin); ll.TextTrimming = TextTrimming.CharacterEllipsis; ll.ToolTip = u.LastLogon;
            Cell(ll, 5);
            var st = new StackPanel { Orientation = Orientation.Horizontal };
            st.Children.Add(Check(id.HasSecureToken(u)));
            if (!showBootstrap) st.Children.Add(Chevron(isOpen));
            Cell(st, 6);
            if (showBootstrap)
            {
                var bt = new StackPanel { Orientation = Orientation.Horizontal };
                bt.Children.Add(Check(id.BootstrapToken!.Escrowed));
                bt.Children.Add(Chevron(isOpen));
                Cell(bt, 7);
            }

            var row = new Border { Child = g, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.MouseLeftButtonUp += (_, _) => { if (!expandedUsers.Remove(key)) expandedUsers.Add(key); rerender(); };
            row.MouseEnter += (_, _) => row.Background = Ui.Brush("SubtleFillBrush");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var stack = new StackPanel();
            stack.Children.Add(row);
            if (isOpen) stack.Children.Add(UserDetails(u, lastLogin, loggedIn));
            list.Children.Add(new Border { BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = stack });
        }
        return Ui.TableCard("User Accounts", $"{rows.Count} of {id.VisibleUsers.Count} accounts", list, controls);
    }

    private static UIElement Chevron(bool open)
    {
        var c = Ui.Icon(open ? "" : "", 10, Ui.Brush("TextSecondaryBrush"));
        c.Margin = new Thickness(8, 0, 0, 0);
        return c;
    }

    private static UIElement UserDetails(UserAccount u, string lastLogin, bool loggedIn)
    {
        var account = new StackPanel();
        account.Children.Add(Ui.Eyebrow("Account Details"));
        account.Children.Add(Ui.Row("Username", u.Username, mono: true, semibold: false));
        account.Children.Add(Ui.Row("UID", u.Uid.ToString(), mono: true, semibold: false));
        if (u.Gid != 0) account.Children.Add(Ui.Row("GID", u.Gid.ToString(), mono: true, semibold: false));
        if (u.RealName is { } r) account.Children.Add(Ui.Row("Full Name", r, semibold: false));
        if (u.HomeDirectory is { } h) account.Children.Add(Ui.Row("Home", h, mono: true, semibold: false));
        if (u.Shell is { } sh) account.Children.Add(Ui.Row("Shell", sh, mono: true, semibold: false));

        var login = new StackPanel();
        login.Children.Add(Ui.Eyebrow("Login & Security"));
        login.Children.Add(Ui.Row("Last Login", loggedIn ? Ui.Status(lastLogin, Tone.Success) : Value(lastLogin)));
        login.Children.Add(Ui.Row("Status", Ui.Status(u.IsDisabled ? "Disabled" : "Active", u.IsDisabled ? Tone.Neutral : Tone.Success)));
        login.Children.Add(Ui.Row("Admin", u.IsAdmin ? Ui.Status("Yes", Tone.Warning) : Value("No")));
        if (u.LinkedAppleId is { } apple) login.Children.Add(Ui.Row("Apple ID", apple, semibold: false));
        if (u.PasswordLastSet is { } pw) login.Children.Add(Ui.Row("Password Set", When(pw), semibold: false));

        var groupsPanel = new StackPanel();
        groupsPanel.Children.Add(Ui.Eyebrow("Group Membership"));
        if (u.Groups.Count == 0) groupsPanel.Children.Add(Ui.Caption("No group memberships"));
        else groupsPanel.Children.Add(Ui.Wrap(6, u.Groups.Select(m => (UIElement)Ui.Pill(m, Tone.Info, 11)).ToArray()));

        return new Border { Padding = new Thickness(20, 4, 20, 16), Background = Ui.Brush("SubtleFillBrush"), Child = Ui.Columns(3, 20, account, login, groupsPanel) };
    }

    private static UIElement SessionsTable(MacIdentityInfo id)
    {
        var rows = id.ActiveUserSessions.Select(x => new SessionRow(
            x.User, Format.OrDash(x.Tty), x.Host ?? "localhost",
            x.LoginTime is not null ? When(x.LoginTime) : x.Time ?? "—",
            x.Pid is { } pid ? pid.ToString() : "—")).ToList();
        return Table.Card("Active Sessions", "Currently logged-in user sessions", rows, null, "No active sessions",
            Col.Text("User", "User", star: true, mono: true), Col.Text("TTY", "Tty", 110, mono: true), Col.Text("Host", "Host", 160),
            Col.Text("Login Time", "LoginTime", 180), Col.Text("PID", "Pid", 90, mono: true));
    }

    private static Tone EventTone(string label) => label switch
    {
        "Failed" => Tone.Error,
        "Logoff" or "Disconnect" => Tone.Neutral,
        "Active" => Tone.Info,
        "Reconnect" => Tone.Warning,
        _ => Tone.Success,
    };

    private static UIElement HistoryTable(MacIdentityInfo id)
    {
        var rows = id.LoginHistory.Select(e =>
        {
            var label = EventLabel(e.EventType);
            return new HistoryRow(e.Username, label, EventTone(label), e.LoginTime is null ? "—" : When(e.LoginTime),
                Format.OrDash(e.Duration), e.SourceIp ?? e.Tty ?? "—");
        }).ToList();
        return Table.Card("Login History", "Recent authentication events", rows, null, "No login history available",
            Col.Text("User", "User", star: true, mono: true), Col.Pill("Event", "Event", "EventTone", 100), Col.Text("Time", "Time", 180),
            Col.Text("Duration", "Duration", 110), Col.Text("Source", "Source", 150));
    }

    private static UIElement SessionHistoryTable(MacIdentityInfo id)
    {
        var panel = new StackPanel();
        if (id.Sessions is { } ss)
        {
            panel.Children.Add(Ui.Columns(4, 12,
                Ui.Metric(ss.TotalSessions.ToString(), "Total Sessions"),
                Ui.Metric(ss.UniqueUsers.ToString(), "Unique Users"),
                Ui.Metric(Minutes(ss.AvgSessionMinutes), "Avg Duration"),
                Ui.Metric(Minutes(ss.MedianSessionMinutes), "Median Duration")));
            if (ss.SessionsByHour.Count > 0)
            {
                var box = new StackPanel();
                box.Children.Add(Ui.Eyebrow("Peak Hours"));
                var peaks = Ui.Wrap(6, ss.PeakHours.Select(p => (UIElement)Ui.Pill($"{p.Hour}:00 ({p.Count})", Tone.Info)).ToArray());
                peaks.Margin = new Thickness(0, 4, 0, 0);
                box.Children.Add(peaks);
                var card = Ui.Card(box, new Thickness(20, 12, 20, 8));
                card.Margin = new Thickness(0, 12, 0, 0);
                panel.Children.Add(card);
            }
            ((FrameworkElement)panel.Children[^1]).Margin = new Thickness(0, panel.Children.Count > 1 ? 12 : 0, 0, 16);
        }
        var rows = id.SessionHistory.Select(e =>
        {
            var label = EventLabel(e.EventType);
            var end = e.EndTime is { } t ? When(t) : label == "Active" ? "Active" : "—";
            return new SessionHistoryRow(e.Username, label, EventTone(label), e.Timestamp.Length == 0 ? "—" : When(e.Timestamp), end, e.DurationText, Format.OrDash(e.SourceAddress));
        }).ToList();
        panel.Children.Add(Table.Card("Session History", "Login session events", rows, null, "No session history available",
            Col.Text("User", "User", star: true, mono: true), Col.Pill("Event", "Event", "EventTone", 110), Col.Text("Start", "Start", 170),
            Col.Text("End", "End", 170), Col.Text("Duration", "Duration", 100), Col.Text("Source", "Source", 150)));
        return panel;
    }
}
