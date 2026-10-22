using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Management tab below the header and the log viewer, which
/// ManagementTab draws itself: the enrollment and certificate cards, the installed
/// configuration profiles with their payloads, and the managed preferences. Port of
/// the Mac app's ManagementTab (ReportMateUI) over <see cref="MacManagementInfo"/>.
/// </summary>
public static class MacManagementView
{
    public static UIElement? Build(DeviceSnapshot s)
    {
        if (MacManagementInfo.From(s.RawModules, s.Platform ?? "macOS") is not { } m) return null;

        var page = new StackPanel();
        void Add(UIElement e) { if (e is FrameworkElement fe && page.Children.Count > 0) fe.Margin = new Thickness(0, 24, 0, 0); page.Children.Add(e); }

        // Top row: enrollment (60%) and, once enrolled, the identity certificate (40%).
        if (m.IsEnrolled)
        {
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            layout.Children.Add(EnrollmentCard(m));
            var cert = CertificateCard(m);
            Grid.SetColumn(cert, 2);
            layout.Children.Add(cert);
            Add(layout);
        }
        else Add(EnrollmentCard(m));

        if (m.Profiles.Count > 0) Add(ProfilesCard(m.Profiles, m.UserScopedProfileCount));
        if (m.IsMac && m.ManagedPolicies.Count > 0) Add(PoliciesCard(m.ManagedPolicies));
        return page;
    }

    // ── Enrollment ───────────────────────────────────────────────────

    private static Border EnrollmentCard(MacManagementInfo m)
    {
        var body = new StackPanel();
        void Section(string title)
        {
            body.Children.Add(Ui.Spacer(14));
            body.Children.Add(Ui.SectionHeader(title));
            body.Children.Add(Ui.Spacer(6));
        }

        body.Children.Add(Ui.SectionHeader("Enrollment"));
        body.Children.Add(Ui.Spacer(6));
        body.Children.Add(Ui.Row("Enrollment Status", Ui.Pill(m.IsEnrolled ? "Enrolled" : "Not Enrolled", m.IsEnrolled ? Tone.Success : Tone.Error)));
        if (m.EnrollmentType is { } type) body.Children.Add(Ui.Row("Enrollment Type", Ui.Pill(type, Tone.Success)));
        if (m.DeviceAuthStatus is { } auth)
            body.Children.Add(Ui.Row("Device Authentication", Ui.Pill(auth == "SUCCESS" ? "Success" : auth, auth == "SUCCESS" ? Tone.Success : Tone.Error)));

        if (m.IsEnrolled && m.HasDeviceDetails)
        {
            Section("Device Details");
            if (m.ManagementName is { } name) body.Children.Add(Ui.Row("Management Name", name, semibold: false));
            if (m.ShowPrimaryUser) body.Children.Add(Ui.Row("Primary User", CopyValue(MacManagementInfo.StripTenantSuffix(m.PrimaryUser)!)));
            if (m.EnrolledBy is { } by) body.Children.Add(Ui.Row("Enrolled By", CopyValue(MacManagementInfo.StripTenantSuffix(by)!)));
            if (m.IntuneDeviceId is { } intune) body.Children.Add(Ui.Row("Intune ID", CopyValue(intune, mono: true)));
            if (m.EntraObjectId is { } entra) body.Children.Add(Ui.Row("Object ID", CopyValue(entra, mono: true)));
            if (m.LastSync is { } sync) body.Children.Add(Ui.Row("Last Sync", Format.ShortDate(sync), semibold: false));
        }

        if (m.IsEnrolled && m.IsMac)
        {
            Section("Enrollment Details");
            body.Children.Add(Ui.Columns(2, 16,
                Ui.VStack(YesNo("ADE Enrolled", m.InstalledFromDep), YesNo("ADE Capable", m.DepCapable),
                    m.IsSupervised is { } supervised ? YesNo("Supervised", supervised) : null),
                Ui.VStack(YesNo("User Approved", m.UserApproved),
                    Ui.Row("Identity Certificate", Ui.Pill(m.EnrollmentMethod ?? "Unknown", m.EnrollmentMethod is null ? Tone.Neutral : Tone.Success)))));
            if (m.DeviceUuid is { } uuid) { body.Children.Add(Ui.Spacer(10)); body.Children.Add(Ui.Stat("UUID", uuid, mono: true, copy: true, truncate: true)); }
            if (m.ServerUrl is { } server) { body.Children.Add(Ui.Spacer(10)); body.Children.Add(Ui.Stat("Server URL", server, mono: true, copy: true, truncate: true)); }
            if (m.CheckinUrl is { } checkin) { body.Children.Add(Ui.Spacer(10)); body.Children.Add(Ui.Stat("Check-in URL", checkin, mono: true, copy: true, truncate: true)); }
        }

        if (m.IsEnrolled && m.IsMac && m.Cert.PushTopic is { } topic)
        {
            Section("Push Notification");
            body.Children.Add(Ui.Stat("APNs Push Topic", topic, mono: true, copy: true, truncate: true));
        }

        if (m.IsEnrolled && m.IsMac && m.AdeActive)
        {
            Section("ADE Configuration");
            body.Children.Add(Ui.Columns(2, 16, YesNo("Assigned", m.AdeAssigned), YesNo("Activated", m.AdeActivated)));
            if (m.AdeOrganization is { } org) body.Children.Add(Ui.Row("Organization", org, semibold: false));
            if (m.AdeSupportPhone is { } phone) body.Children.Add(Ui.Row("Support Phone", phone, semibold: false));
            if (m.AdeSupportEmail is { } email) body.Children.Add(Ui.Row("Support Email", email, semibold: false));
        }

        if (m.ManagementUrl is { } url)
        {
            body.Children.Add(Ui.Spacer(14));
            body.Children.Add(Ui.Stat("Management URL", url, mono: true, copy: true, truncate: true));
        }
        return Ui.Card(body, new Thickness(20, 16, 20, 18));
    }

    private static Grid YesNo(string label, bool on) => Ui.Row(label, Ui.Pill(on ? "Yes" : "No", on ? Tone.Success : Tone.Neutral));

    private static FrameworkElement CopyValue(string value, bool mono = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var t = mono ? Ui.Mono(value) : Ui.Text(value);
        t.Margin = new Thickness(0); t.FontWeight = FontWeights.SemiBold; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; t.MaxWidth = 280; t.ToolTip = value;
        panel.Children.Add(t);
        panel.Children.Add(Ui.CopyButton(value));
        return panel;
    }

    // ── Certificate ──────────────────────────────────────────────────

    private static Border CertificateCard(MacManagementInfo m)
    {
        var c = m.Cert;
        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = Ui.Brush("IconYellowBackground"), Child = Ui.Icon("\uEB95", 18, Ui.Brush("IconYellowForeground")), Margin = new Thickness(0, 0, 12, 0) };
        ((FrameworkElement)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;
        head.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text("Certificate", "TitleTextStyle"));
        titles.Children.Add(Ui.Text("Device authentication", "SubtitleTextStyle"));
        head.Children.Add(titles);
        body.Children.Add(head);

        void Field(FrameworkElement stat) { body.Children.Add(stat); body.Children.Add(Ui.Spacer(10)); }
        if (c.Name is { } name) Field(Ui.Stat("Identity", name, truncate: true));
        if (c.Issuer is { } issuer)
        {
            var st = Ui.Stat("Issued By", issuer, truncate: true);
            if (st is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock vt) vt.Foreground = Ui.StatusBrush(Tone.Warning);
            Field(st);
        }
        if (c.Expires is { } expires)
        {
            var date = MacManagementInfo.ParseDate(expires);
            var tone = MacManagementInfo.ExpiryOf(date) switch
            {
                MacManagementInfo.Expiry.Expired => Tone.Error,
                MacManagementInfo.Expiry.Soon => Tone.Warning,
                MacManagementInfo.Expiry.Fine => Tone.Success,
                _ => Tone.Neutral,
            };
            var st = Ui.Stat("Valid Until", date is null ? expires : Format.ShortDate(date));
            if (tone != Tone.Neutral && st is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock vt) vt.Foreground = Ui.StatusBrush(tone);
            Field(st);
        }
        if (m.AdeOrganization is { } org) Field(Ui.Stat("Organization", org));
        if (c.ScepUrl is { } scep) Field(Ui.Stat(m.EnrollmentUrlLabel, scep, mono: true, copy: true, truncate: true));
        if (!c.HasDetails) body.Children.Add(Ui.Caption("Certificate details not available"));

        body.Children.Add(Ui.Divider());
        body.Children.Add(Ui.Row("Configuration Profiles", m.ProfileCount.ToString()));
        return Ui.Card(body, new Thickness(20, 16, 20, 18));
    }

    // ── Searchable cards ─────────────────────────────────────────────

    /// <summary>
    /// A table card whose list redraws under a search box that stays put, so typing
    /// keeps focus; <paramref name="render"/> fills the list and returns the subtitle.
    /// </summary>
    private static Border SearchCard(string title, string placeholder, Func<string, StackPanel, string> render)
    {
        var subtitle = Ui.Text("", "SubtitleTextStyle");
        var list = new StackPanel();
        var query = "";
        void Refresh() { list.Children.Clear(); subtitle.Text = render(query, list); }
        var search = new SearchBox(placeholder, q => { query = q; Refresh(); });
        Refresh();

        var card = new Border { Style = (Style)Ui.Res("CardStyle") };
        var root = new StackPanel();
        var header = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Ui.Text(title, "TitleTextStyle"));
        titles.Children.Add(subtitle);
        header.Children.Add(titles);
        search.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(search, 1);
        header.Children.Add(search);
        root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        root.Children.Add(list);
        card.Child = root;
        return card;
    }

    /// <summary>A clickable row header that toggles a details panel beneath it.</summary>
    private static Border Expandable(Grid row, Func<UIElement?> details, Func<bool> isOpen, Action toggle)
    {
        var stack = new StackPanel();
        var header = new Border { Child = row, Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        var chevron = (ModernWpf.Controls.FontIcon)row.Children[0];
        UIElement? shown = null;
        void Sync()
        {
            var open = isOpen();
            chevron.Glyph = open ? "\uE70D" : "\uE76C";
            if (open && shown is null && details() is { } d) { shown = d; stack.Children.Add(d); }
            else if (!open && shown is not null) { stack.Children.Remove(shown); shown = null; }
        }
        header.MouseLeftButtonUp += (_, _) => { toggle(); Sync(); };
        header.MouseEnter += (_, _) => header.Background = Ui.Brush("SubtleFillBrush");
        header.MouseLeave += (_, _) => header.Background = System.Windows.Media.Brushes.Transparent;
        stack.Children.Add(header);
        Sync();
        return new Border { BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = stack };
    }

    private static Grid RowGrid(Thickness margin)
    {
        var row = new Grid { Margin = margin };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chevron = Ui.Icon("\uE76C", 10, Ui.Brush("TextSecondaryBrush")); chevron.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(chevron);
        return row;
    }

    private static void Badge(StackPanel badges, UIElement badge)
    {
        if (badge is FrameworkElement fe && badges.Children.Count > 0) fe.Margin = new Thickness(6, 0, 0, 0);
        badges.Children.Add(badge);
    }

    // ── Configuration profiles ───────────────────────────────────────

    private static Border ProfilesCard(IReadOnlyList<MacManagementInfo.Profile> profiles, int userScoped)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        return SearchCard("Configuration Profiles", "Search profiles...", (q, list) =>
        {
            var filtered = profiles.Select((p, i) => (Profile: p, Index: i)).Where(e => e.Profile.Matches(q)).ToList();
            if (filtered.Count == 0) list.Children.Add(Ui.EmptyState($"No profiles match \"{q.Trim()}\"."));
            foreach (var (p, index) in filtered)
            {
                var key = p.Identifier + "#" + index;
                var row = RowGrid(new Thickness(20, 10, 20, 10));
                var titles = new StackPanel();
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                var name = Ui.Text(p.Name); name.FontWeight = FontWeights.Medium; name.Margin = new Thickness(0); name.TextTrimming = TextTrimming.CharacterEllipsis;
                nameRow.Children.Add(name);
                if (p.IsRemovalDisallowed)
                {
                    var lockIcon = Ui.Icon("\uE72E", 11, Ui.StatusBrush(Tone.Warning)); lockIcon.Margin = new Thickness(6, 0, 0, 0);
                    lockIcon.ToolTip = "Removal disallowed";
                    nameRow.Children.Add(lockIcon);
                }
                if (p.IsVerified)
                {
                    var v = Ui.Pill("Verified", Tone.Success, 10.5); v.Margin = new Thickness(6, 0, 0, 0);
                    v.ToolTip = "Profile signature verified by the system";
                    nameRow.Children.Add(v);
                }
                titles.Children.Add(nameRow);
                titles.Children.Add(Ui.Caption(p.Organization ?? "Unknown Organization"));
                Grid.SetColumn(titles, 1);
                row.Children.Add(titles);

                var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (p.PayloadCount > 0) Badge(badges, Ui.Pill(Format.Plural(p.PayloadCount, "payload"), Tone.Info));
                Badge(badges, Ui.Pill(p.Scope, Tone.Neutral));
                if (p.IsLegacyMcx)
                {
                    var mcx = Ui.Pill("Legacy MCX", Tone.Orange);
                    mcx.ToolTip = "Emulated via MCX (Managed Client for X) - legacy method";
                    Badge(badges, mcx);
                }
                Grid.SetColumn(badges, 2);
                row.Children.Add(badges);

                list.Children.Add(Expandable(row, () => ProfileDetails(p), () => expanded.Contains(key),
                    () => { if (!expanded.Remove(key)) expanded.Add(key); }));
            }
            var subtitle = $"Management profiles applied to this device ({filtered.Count} of {profiles.Count} profiles";
            if (userScoped > 0) subtitle += $", {profiles.Count - userScoped} system level, {userScoped} user level";
            return subtitle + ")";
        });
    }

    private static Border ProfileDetails(MacManagementInfo.Profile p)
    {
        var body = new StackPanel();
        var installed = p.InstallDateText is { } text
            ? MacManagementInfo.ParseDate(text) is { } date ? Format.ShortDate(date) : text
            : null;
        body.Children.Add(Ui.Columns(3, 16,
            Ui.Stat("Identifier", p.Identifier, mono: true, copy: true, truncate: true),
            p.Uuid is { } uuid && uuid != p.Identifier ? Ui.Stat("UUID", uuid, mono: true, copy: true, truncate: true) : null,
            installed is null ? null : Ui.Stat("Installed", installed)));
        if (p.Description is { } description)
        {
            var d = Ui.Stat("Description", description);
            if (d is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock dt) dt.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(new Border { Background = Ui.Brush("SubtleFillBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 0), Child = d });
        }
        if (p.Payloads.Count > 0)
        {
            var label = Ui.Label("Payloads"); label.Margin = new Thickness(0, 12, 0, 6);
            body.Children.Add(label);
            foreach (var payload in p.Payloads) body.Children.Add(PayloadCard(payload));
        }
        return new Border { Padding = new Thickness(52, 0, 20, 16), Child = body };
    }

    private static Border PayloadCard(MacManagementInfo.Payload payload)
    {
        var card = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var n = Ui.Text(payload.Title); n.FontWeight = FontWeights.Medium; n.Margin = new Thickness(0);
        head.Children.Add(n);
        if (payload.Type is { } type)
        {
            var t = Ui.Mono(type); t.FontSize = 11; t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; t.Foreground = Ui.Brush("TextSecondaryBrush");
            Grid.SetColumn(t, 1);
            head.Children.Add(t);
        }
        card.Children.Add(head);

        if (payload.Settings.Count > 0)
        {
            var table = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var setting in payload.Settings)
            {
                var r = new Grid();
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
                var k = Ui.Mono(setting.Key); k.FontSize = 11.5; k.Margin = new Thickness(0, 0, 12, 0); k.TextTrimming = TextTrimming.CharacterEllipsis; k.TextWrapping = TextWrapping.NoWrap; k.ToolTip = setting.Key;
                r.Children.Add(k);
                UIElement v;
                if (setting.Kind == MacManagementInfo.SettingKind.Flag)
                {
                    var pill = Ui.Pill(setting.Text, setting.Flag ? Tone.Success : Tone.Neutral, 10.5);
                    pill.HorizontalAlignment = HorizontalAlignment.Left;
                    v = pill;
                }
                else
                {
                    var tv = Ui.Mono(setting.Text); tv.FontSize = 11.5; tv.Margin = new Thickness(0); tv.TextWrapping = TextWrapping.Wrap;
                    v = tv;
                }
                Grid.SetColumn(v, 1);
                r.Children.Add(v);
                table.Children.Add(new Border { Padding = new Thickness(0, 4, 0, 4), BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = r });
            }
            card.Children.Add(table);
        }
        else if (payload.RawData is { } raw)
        {
            var pre = Ui.Mono(raw); pre.FontSize = 11; pre.TextWrapping = TextWrapping.Wrap;
            var exp = Ui.Collapsible("View Raw Data", pre);
            exp.Margin = new Thickness(0, 6, 0, 0);
            card.Children.Add(exp);
        }
        return new Border { Background = Ui.Brush("SubtleFillBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = card };
    }

    // ── Managed preferences ──────────────────────────────────────────

    private static Border PoliciesCard(IReadOnlyList<MacManagementInfo.PolicyDomain> policies)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        return SearchCard("Managed Preferences", "Search preferences...", (q, list) =>
        {
            var filtered = policies.Where(p => p.Matches(q)).ToList();
            if (filtered.Count == 0) list.Children.Add(Ui.EmptyState($"No preferences match \"{q.Trim()}\"."));
            foreach (var pol in filtered)
            {
                var key = pol.Domain;
                var row = RowGrid(new Thickness(20, 8, 20, 8));
                var domain = Ui.Mono(pol.Domain); domain.FontWeight = FontWeights.Medium; domain.Margin = new Thickness(0); domain.TextTrimming = TextTrimming.CharacterEllipsis; domain.TextWrapping = TextWrapping.NoWrap;
                domain.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(domain, 1);
                row.Children.Add(domain);
                var count = Ui.Caption(Format.Plural(pol.SettingCount, "setting")); count.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(count, 2);
                row.Children.Add(count);
                list.Children.Add(Expandable(row, () => pol.Settings.Count > 0 ? PolicySettings(pol) : null, () => expanded.Contains(key),
                    () => { if (!expanded.Remove(key)) expanded.Add(key); }));
            }
            return $"Managed settings from /Library/Managed Preferences/ ({filtered.Count} of {policies.Count} domains)";
        });
    }

    private static Border PolicySettings(MacManagementInfo.PolicyDomain pol)
    {
        var table = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        head.Children.Add(Ui.Eyebrow("Key"));
        var valueHead = Ui.Eyebrow("Value"); Grid.SetColumn(valueHead, 1); head.Children.Add(valueHead);
        table.Children.Add(head);
        foreach (var s in pol.Settings)
        {
            var r = new Grid();
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            var k = Ui.Mono(s.Name); k.FontSize = 11.5; k.Margin = new Thickness(0, 0, 12, 0); k.Foreground = Ui.Brush("TextSecondaryBrush");
            k.TextTrimming = TextTrimming.CharacterEllipsis; k.TextWrapping = TextWrapping.NoWrap; k.ToolTip = s.Name;
            r.Children.Add(k);
            UIElement v;
            if (s.Flag is { } on)
            {
                var pill = Ui.Pill(on ? "Yes" : "No", on ? Tone.Success : Tone.Neutral, 10.5);
                pill.HorizontalAlignment = HorizontalAlignment.Left;
                v = pill;
            }
            else
            {
                var tv = Ui.Mono(s.Value); tv.FontSize = 11.5; tv.Margin = new Thickness(0); tv.TextWrapping = TextWrapping.Wrap;
                v = tv;
            }
            Grid.SetColumn(v, 1);
            r.Children.Add(v);
            table.Children.Add(new Border { Padding = new Thickness(0, 3, 0, 3), BorderBrush = Ui.Brush("DividerBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = r });
        }
        return new Border { Background = Ui.Brush("SubtleFillBrush"), Padding = new Thickness(52, 10, 20, 10), Child = table };
    }
}
