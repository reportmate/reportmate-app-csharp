using System.Windows;
using System.Windows.Controls;
using Hyperlink = System.Windows.Documents.Hyperlink;
using Run = System.Windows.Documents.Run;
using System.Windows.Input;
using ReportMate.App.Services;
using ReportMate.App.Services.MacModules;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's Security tab, as the Mac app's SecurityTabView and the web
/// SecurityTab draw it: the six posture cards (tampering, protection, detection,
/// encryption, firewall, remote access), then certificates, SOFA CVEs and threat
/// detections.
/// </summary>
public static class MacSecurityView
{
    private const string LockGlyph = "";
    private const string ShieldGlyph = "";
    private const string SearchGlyph = "";
    private const string DriveGlyph = "";
    private const string FirewallGlyph = "";
    private const string TerminalGlyph = "";
    private const string CertificateGlyph = "";
    private const string WarningGlyph = "";
    private const string CheckGlyph = "";

    private sealed record CertRow(string Name, string Issuer, string Status, Tone StatusTone, string From, string Expires, string Keychain);
    private sealed record CveRow(string Cve, string Url, string OsVersion, string PatchedIn, string Status, Tone StatusTone, string Source, Tone SourceTone);
    private sealed record DetectionRow(string Threat, string Severity, Tone SeverityTone, string Category, string Status, Tone StatusTone, string Source, string Detected, MacSecurityInfo.Detection Detection);

    public static UIElement? Build(DeviceSnapshot s)
    {
        if (!s.RawModules.TryGetValue("security", out var raw)) return null;
        s.RawModules.TryGetValue("management", out var management);
        var info = MacSecurityInfo.From(raw, s.RawModules.ContainsKey("management") ? management : null);
        if (info is null) return null;

        var page = new StackPanel();
        void Add(UIElement e, double top = 24) { if (e is FrameworkElement fe && page.Children.Count > 0) fe.Margin = new Thickness(0, top, 0, 0); page.Children.Add(e); }

        UIElement? right = null;
        if (info.LastScan is { } lastScan)
        {
            var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            p.Children.Add(Ui.Caption("Last Scan"));
            var v = Ui.Text(Format.ShortDateTime(lastScan)); v.FontSize = 15; v.FontWeight = FontWeights.SemiBold; v.TextAlignment = TextAlignment.Right;
            p.Children.Add(v);
            right = p;
        }
        var subtitle = "macOS protection and compliance status";
        if (s.CollectedAtFor("security") is { } at) subtitle += $" · collected {Format.RelativeTime(at)}";
        Add(Ui.TabHeader("Security Overview", subtitle, LockGlyph, Accent.Red, right), 0);

        var grid = Ui.CardGrid(360, 20);
        grid.MaxColumns = 6;
        grid.Children.Add(TamperingCard(info));
        grid.Children.Add(ProtectionCard(info));
        grid.Children.Add(DetectionCard(info));
        grid.Children.Add(EncryptionCard(info));
        grid.Children.Add(FirewallCard(info));
        grid.Children.Add(RemoteAccessCard(info));
        Add(grid, 0);

        Add(CertificatesCard(info));
        Add(CveCard(info));
        Add(DetectionsCard(info));
        return page;
    }

    // ── Row helpers ──────────────────────────────────────────────────

    private static Border PostureCard(string glyph, string title, StackPanel body)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Ui.Brush("SubtleFillBrush"), Child = Ui.Icon(glyph, 15, Ui.Brush("TextSecondaryBrush")), Margin = new Thickness(0, 0, 10, 0) };
        ((FrameworkElement)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;
        head.Children.Add(tile);
        var t = Ui.SectionHeader(title); t.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(t);
        var root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(body);
        return Ui.Card(root, new Thickness(20, 16, 20, 16));
    }

    private static TextBlock Heading(string text)
    {
        var t = Ui.Eyebrow(text);
        t.Margin = new Thickness(0, 6, 0, 4);
        return t;
    }

    private static Tone ToneFor(MacSeverity severity) => severity switch
    {
        MacSeverity.Ok => Tone.Success,
        MacSeverity.Warning => Tone.Warning,
        MacSeverity.Danger => Tone.Error,
        _ => Tone.Neutral,
    };

    /// <summary>The web StatusBadge: unknown grey, severity colour wins, else danger red, neutral plain, enabled green.</summary>
    private static Grid Status(string label, bool? enabled, string on = "Enabled", string off = "Disabled", bool neutral = false, bool danger = false, MacSeverity? severity = null)
    {
        if (enabled is null) return Ui.Row(label, Ui.Status("Unknown", Tone.Neutral));
        Tone tone;
        if (severity is { } sev && sev != MacSeverity.Unknown) tone = ToneFor(sev);
        else if (!enabled.Value) tone = danger ? Tone.Error : Tone.Neutral;
        else tone = neutral ? Tone.Neutral : Tone.Success;
        return Ui.Row(label, Ui.Status(enabled.Value ? on : off, tone));
    }

    private static Grid Value(string label, string? value, bool mono = false) => Ui.Row(label, Format.OrUnknown(value), mono: mono, semibold: false);

    // ── Posture cards ────────────────────────────────────────────────

    private static Border TamperingCard(MacSecurityInfo s)
    {
        var body = new StackPanel();
        body.Children.Add(Status("System Integrity Protection", s.SipEnabled, severity: MacSecurityInfo.Severity("sip", s.SipEnabled)));
        body.Children.Add(Status("Secure Enclave", s.SecureEnclavePresent, "Present", "Not Present", neutral: true));
        body.Children.Add(Status("Biometric", s.BiometricSupported, "Supported", "Not Supported", neutral: true));
        if (s.ExternalBoot is { } ext) body.Children.Add(Value("External Boot", ext));
        body.Children.Add(Ui.Divider());
        body.Children.Add(Status("Root User", !s.RootUserEnabled, "Disabled", "Enabled", neutral: true));
        body.Children.Add(Status("Authenticated Root", s.AuthenticatedRootRequired, "Required", "Allowed", neutral: true));
        return PostureCard(ShieldGlyph, "Tampering", body);
    }

    private static Border ProtectionCard(MacSecurityInfo s)
    {
        var body = new StackPanel();
        body.Children.Add(Heading("Built-in Defenses"));
        body.Children.Add(Status("Gatekeeper", s.GatekeeperEnabled));
        body.Children.Add(Value("Allow Apps From", s.AllowAppsFrom));
        body.Children.Add(Ui.Divider());
        body.Children.Add(Status("XProtect", s.XProtectActive, "Active", "Inactive"));
        body.Children.Add(Value("XProtect Version", s.XProtectVersion, mono: true));
        body.Children.Add(Value("Signatures", s.XProtectSignatures, mono: true));
        body.Children.Add(Ui.Divider());
        body.Children.Add(Status("MRT (Malware Removal)", s.MrtInstalled, "Installed", "Not Installed"));
        body.Children.Add(Value("MRT Version", s.MrtVersion, mono: true));
        return PostureCard(ShieldGlyph, "Protection", body);
    }

    private static Border DetectionCard(MacSecurityInfo s)
    {
        var body = new StackPanel();
        var products = s.EdrProducts;
        var extensions = s.EndpointExtensions;
        if (products.Count > 0)
        {
            for (var i = 0; i < products.Count; i++)
            {
                var p = products[i];
                if (i > 0) body.Children.Add(Ui.Divider());
                var name = Ui.Text(p.Name); name.FontWeight = FontWeights.SemiBold;
                var tone = p.Healthy == true || p.Running == true ? Tone.Success : p.Healthy == false ? Tone.Warning : Tone.Neutral;
                var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(name);
                var health = Ui.Pill(p.HealthLabel, tone);
                Grid.SetColumn(health, 1);
                head.Children.Add(health);
                body.Children.Add(head);

                if (p.IsDefender)
                {
                    body.Children.Add(Status("Real-time Protection", p.RealTimeProtection));
                    body.Children.Add(Status("Cloud Protection", p.CloudProtection));
                    body.Children.Add(Status("Tamper Protection", p.TamperProtection));
                    body.Children.Add(Status("Behavior Monitoring", p.BehaviorMonitoring));
                    body.Children.Add(Value("App Version", p.Version));
                    body.Children.Add(Value("Engine", p.EngineVersion));
                    body.Children.Add(Value("Definitions", p.DefinitionsVersion));
                    body.Children.Add(Value("Definitions Updated", p.DefinitionsUpdated is null ? null : Format.ShortDateTime(p.DefinitionsUpdated)));
                    body.Children.Add(Value("Last Scan", p.LastScanTime is null ? "Never" : $"{Format.ShortDateTime(p.LastScanTime)} ({p.LastScanType ?? "unknown"})"));
                    if (p.FilesScanned is > 0 and var n) body.Children.Add(Value("Files Scanned", n.ToString("N0")));
                    body.Children.Add(Value("Threats Detected", p.ThreatsDetected.ToString()));
                    body.Children.Add(Status("Licensed", p.Licensed));
                    body.Children.Add(Status("Full Disk Access", p.FullDiskAccess, "Granted", "Not Granted"));
                    if (p.HealthIssues.Count > 0)
                    {
                        var issues = new StackPanel();
                        var title = Ui.Status("Health Issues:", Tone.Warning, 11.5, FontWeights.SemiBold);
                        issues.Children.Add(title);
                        foreach (var issue in p.HealthIssues)
                        {
                            var line = Ui.Status($"• {issue}", Tone.Warning, 11.5, FontWeights.Normal);
                            line.TextWrapping = TextWrapping.Wrap;
                            issues.Children.Add(line);
                        }
                        body.Children.Add(new Border { Background = Ui.Tint(Tone.Warning, 0.1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 0), Child = issues });
                    }
                }
                else if (p.IsCrowdStrike)
                {
                    body.Children.Add(Value("Version", p.Version));
                    body.Children.Add(Value("State", p.State));
                    body.Children.Add(Value("Agent ID", p.AgentId, mono: true));
                }
                else
                {
                    body.Children.Add(Value("Version", p.Version));
                    body.Children.Add(Status("Running", p.Running));
                }
            }
        }
        else if (extensions.Count > 0)
        {
            body.Children.Add(Heading("Endpoint Security Extensions"));
            foreach (var e in extensions.Take(3))
            {
                var detail = string.Join("  ", new[] { e.Version, e.TeamId }.Where(x => !string.IsNullOrWhiteSpace(x)));
                body.Children.Add(Ui.ListItem(e.Name, detail.Length > 0 ? detail : null, e.Active ? "Active" : "Inactive", e.Active ? Tone.Success : Tone.Neutral));
            }
            if (extensions.Count > 3) body.Children.Add(Ui.Caption($"+{extensions.Count - 3} more extensions"));
        }
        else
        {
            body.Children.Add(Ui.Caption("No EDR products detected"));
        }
        return PostureCard(SearchGlyph, "Detection", body);
    }

    private static Border EncryptionCard(MacSecurityInfo s)
    {
        var body = new StackPanel();
        body.Children.Add(Heading("FileVault Disk Encryption"));
        body.Children.Add(Value("Encrypted Volumes", s.EncryptedVolumesLabel));
        body.Children.Add(Status("Status", s.FileVaultEnabled, "Encrypted", "Not Encrypted", severity: MacSecurityInfo.Severity("encryption", s.FileVaultEnabled)));
        return PostureCard(DriveGlyph, "Encryption", body);
    }

    private static Border FirewallCard(MacSecurityInfo s)
    {
        var body = new StackPanel();
        body.Children.Add(Status("Global State", s.FirewallEnabled, "On", "Off", severity: MacSecurityInfo.Severity("firewall", s.FirewallEnabled)));
        body.Children.Add(Status("Stealth Mode", s.StealthMode, neutral: true));
        body.Children.Add(Status("Logging", s.FirewallLogging, neutral: true));
        body.Children.Add(Status("Allow Signed Apps", s.AllowSignedApps, "Allowed", "Blocked", neutral: true));
        return PostureCard(FirewallGlyph, "Firewall", body);
    }

    private static Border RemoteAccessCard(MacSecurityInfo s)
    {
        var body = new StackPanel();
        body.Children.Add(Status("Secure Shell (Remote Login)", s.SshEnabled, severity: MacSecurityInfo.Severity("ssh", s.SshEnabled)));
        if (s.SshServiceEnabled)
        {
            body.Children.Add(Value("Authorized Users", s.SshAuthorizedUsers));
            if (s.SshAuthentication is { } auth) body.Children.Add(Value("Authentication", auth));
        }
        body.Children.Add(Ui.Divider());
        body.Children.Add(Status("Remote Management", s.ArdEnabled, neutral: true));
        body.Children.Add(Status("Screen Sharing", s.ScreenSharingEnabled, neutral: true));
        if (s.ArdAllowedUsers is { } allowed) body.Children.Add(Value("Allowed Users", allowed));
        return PostureCard(TerminalGlyph, "Remote Access", body);
    }

    // ── Certificates ─────────────────────────────────────────────────

    /// <summary>
    /// Built once, with the subtitle, filter row and store groups redrawn in place,
    /// so the search box keeps its focus and text while it filters.
    /// </summary>
    private static UIElement CertificatesCard(MacSecurityInfo s)
    {
        var all = s.Certificates;
        var osRootExpired = s.OsRootExpiredCount;
        var certFilter = "all";
        string? storeFilter = null;
        var selfSignedOnly = false;
        var showOsRoots = false;
        var search = "";
        var openStores = new HashSet<string>();

        var subtitle = Ui.Text("", "SubtitleTextStyle");
        var filterHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var groupsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var searchBox = new SearchBox("Search certificates...", _ => { });

        void Render()
        {
            var visible = showOsRoots ? all : all.Where(c => !(c.IsOsTrustedRoot && c.IsExpired)).ToList();
            var expired = (!showOsRoots ? s.UserExpiredCount : null) ?? visible.Count(c => c.IsExpired);
            var expiring = visible.Count(c => c.IsExpiringSoon);
            var valid = visible.Count(c => c.Status == MacSecurityInfo.CertStatus.Valid);
            var selfSigned = visible.Count(c => c.IsSelfSigned);
            var stores = visible.Select(c => c.Store).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var filtered = visible.Where(c =>
            {
                if (certFilter == "valid" && c.Status != MacSecurityInfo.CertStatus.Valid) return false;
                if (certFilter == "expiringsoon" && !c.IsExpiringSoon) return false;
                if (certFilter == "expired" && !c.IsExpired) return false;
                if (storeFilter is not null && c.Store != storeFilter) return false;
                if (selfSignedOnly && !c.IsSelfSigned) return false;
                return c.Matches(search);
            }).ToList();

            subtitle.Text = $"{visible.Count} certificates" + (!showOsRoots && osRootExpired > 0 ? $" ({osRootExpired} expired OS roots hidden)" : "");

            var pills = new WrapPanel { Margin = new Thickness(20, 10, 20, 10) };
            void Toggle(string label, bool active, Tone tone, Action onClick)
            {
                var pill = Ui.Pill(label, tone);
                pill.Cursor = Cursors.Hand; pill.Margin = new Thickness(0, 0, 6, 4); pill.BorderThickness = new Thickness(2);
                pill.BorderBrush = active ? Ui.StatusBrush(tone) : System.Windows.Media.Brushes.Transparent;
                pill.MouseLeftButtonUp += (_, _) => { onClick(); openStores.UnionWith(stores); Render(); };
                pills.Children.Add(pill);
            }
            if (stores.Count > 1)
                foreach (var store in stores)
                    Toggle($"{store} ({visible.Count(c => c.Store == store)})", storeFilter == store, Tone.Info, () => storeFilter = storeFilter == store ? null : store);
            Toggle($"Valid ({valid})", certFilter == "valid", Tone.Success, () => certFilter = certFilter == "valid" ? "all" : "valid");
            Toggle($"Expiring Soon ({expiring})", certFilter == "expiringsoon", Tone.Warning, () => certFilter = certFilter == "expiringsoon" ? "all" : "expiringsoon");
            Toggle($"Expired ({expired})", certFilter == "expired", Tone.Error, () => certFilter = certFilter == "expired" ? "all" : "expired");
            Toggle($"Self-signed ({selfSigned})", selfSignedOnly, Tone.Info, () => selfSignedOnly = !selfSignedOnly);
            if (osRootExpired > 0)
                Toggle($"{(showOsRoots ? "Hide" : "Show")} expired OS roots ({osRootExpired})", showOsRoots, Tone.Neutral, () => showOsRoots = !showOsRoots);
            if (certFilter != "all" || storeFilter is not null || selfSignedOnly || showOsRoots || search.Trim().Length > 0)
            {
                var clear = new Button { Content = "Clear Filters", Padding = new Thickness(8, 2, 8, 2), FontSize = 11.5, Margin = new Thickness(0, 0, 6, 4) };
                clear.Click += (_, _) =>
                {
                    certFilter = "all"; storeFilter = null; selfSignedOnly = false; showOsRoots = false;
                    search = "";
                    searchBox.Text = "";
                    Render();
                };
                pills.Children.Add(clear);
            }
            filterHost.Content = all.Count == 0 ? null : pills;

            if (all.Count == 0)
            {
                groupsHost.Content = Ui.EmptyState("No certificates collected. Certificate data will appear after the next device check-in.", CertificateGlyph);
                return;
            }
            if (filtered.Count == 0)
            {
                groupsHost.Content = Ui.EmptyState("No certificates match the selected filters", CertificateGlyph);
                return;
            }
            var groups = new StackPanel { Margin = new Thickness(20, 0, 20, 12) };
            foreach (var group in filtered.GroupBy(c => c.Store).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var rows = group.Select(c => new CertRow(
                    c.DisplayName + (c.IsSelfSigned ? " (self-signed)" : ""),
                    c.Issuer.Length == 0 ? "" : c.DisplayIssuer,
                    c.Status switch
                    {
                        MacSecurityInfo.CertStatus.Expired => "Expired",
                        MacSecurityInfo.CertStatus.ExpiringSoon => c.DaysUntilExpiry is { } d ? $"{d}d" : "Expiring",
                        _ => "Valid",
                    },
                    c.Status switch
                    {
                        MacSecurityInfo.CertStatus.Expired => Tone.Error,
                        MacSecurityInfo.CertStatus.ExpiringSoon => Tone.Warning,
                        _ => Tone.Success,
                    },
                    c.NotBefore.Length == 0 ? "—" : Format.ShortDate(c.NotBefore),
                    c.NotAfter.Length == 0 ? "—" : Format.ShortDate(c.NotAfter),
                    c.KeychainName)).ToList();
                var table = Table.Build(rows,
                    Col.Text("Name / Subject / Issuer", "Name", star: true, sub: "Issuer"),
                    Col.Pill("Status", "Status", "StatusTone", 100),
                    Col.Text("Valid From", "From", 120),
                    Col.Text("Expires", "Expires", 120),
                    Col.Text("Keychain", "Keychain", 200, mono: true));
                var store = group.Key;
                var expander = Ui.Collapsible($"{store}  ·  {Format.Plural(rows.Count, "certificate")}", table, openStores.Contains(store));
                expander.Expanded += (_, _) => openStores.Add(store);
                expander.Collapsed += (_, _) => openStores.Remove(store);
                groups.Children.Add(expander);
            }
            groupsHost.Content = groups;
        }

        searchBox.TextChanged += (_, _) => { search = searchBox.Text ?? ""; openStores.UnionWith(all.Select(c => c.Store)); Render(); };
        Render();

        var card = new Border { Style = (Style)Ui.Res("CardStyle") };
        var root = new StackPanel();
        var header = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Ui.Text("Certificates", "TitleTextStyle"));
        titles.Children.Add(subtitle);
        header.Children.Add(titles);
        searchBox.VerticalAlignment = VerticalAlignment.Center;
        searchBox.MinWidth = 220;
        Grid.SetColumn(searchBox, 1);
        header.Children.Add(searchBox);
        root.Children.Add(header);
        root.Children.Add(filterHost);
        root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        root.Children.Add(groupsHost);
        card.Child = root;
        return card;
    }

    // ── CVEs ─────────────────────────────────────────────────────────

    private static UIElement CveCard(MacSecurityInfo s)
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var filter = "all";
        void Render() => host.Content = CveCardBody(s, filter, f => { filter = f; Render(); });
        Render();
        return host;
    }

    private static UIElement CveCardBody(MacSecurityInfo s, string filter, Action<string> setFilter)
    {
        var all = s.Cves;
        var unpatched = all.Where(c => c.IsUnpatched).ToList();
        var patched = all.Where(c => c.Status == "Patched").ToList();
        var exploited = unpatched.Count(c => c.ActivelyExploited);
        var release = s.Release;
        var total = release?.UniqueCvesCount is > 0 and var n ? n : all.Count;
        var shown = MacSecurityInfo.SortCves(filter == "unpatched" ? unpatched : filter == "patched" ? patched : all);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Badge(string text, Tone tone) { var b = Ui.Pill(text, tone); b.Margin = new Thickness(0, 0, 6, 0); badges.Children.Add(b); }
        if (s.PendingRebootRequired) Badge("Reboot Required", Tone.Warning);
        if (exploited > 0) Badge($"{exploited} Actively Exploited", Tone.Error);
        if (all.Count > 0) Badge($"{total} total CVEs", Tone.Neutral);
        if (patched.Count > 0 && unpatched.Count > 0)
        {
            foreach (var (key, label, tone) in new[] { ("all", $"All ({all.Count})", Tone.Neutral), ("unpatched", $"Unpatched ({unpatched.Count})", Tone.Error), ("patched", $"Patched ({patched.Count})", Tone.Success) })
            {
                var pill = Ui.Pill(label, tone);
                pill.Cursor = Cursors.Hand; pill.Margin = new Thickness(0, 0, 6, 0); pill.BorderThickness = new Thickness(2);
                pill.BorderBrush = filter == key ? Ui.StatusBrush(tone) : System.Windows.Media.Brushes.Transparent;
                pill.MouseLeftButtonUp += (_, _) => setFilter(filter == key && key != "all" ? "all" : key);
                badges.Children.Add(pill);
            }
        }

        var subtitle = "SOFA Security Intelligence • " + (unpatched.Count > 0 ? $"{unpatched.Count} unpatched" : "All patched") + (patched.Count > 0 ? $" • {patched.Count} patched" : "");

        var body = new StackPanel();
        if (release?.UpdateName is { } name)
        {
            var line = new TextBlock { Margin = new Thickness(20, 10, 20, 10), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Brush("TextSecondaryBrush") };
            void Part(string label, string value)
            {
                if (line.Inlines.Count > 0) line.Inlines.Add(new Run("    "));
                line.Inlines.Add(new Run(label + " ") { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new Run(value));
            }
            Part("Release:", name);
            if (release.ReleaseDate is { } date) Part("Date:", Format.ShortDate(date));
            if (release.DaysSincePreviousRelease is { } days) Part("Days since last:", days.ToString());
            if (release.SecurityInfo is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (line.Inlines.Count > 0) line.Inlines.Add(new Run("    "));
                var link = new Hyperlink(new Run("Apple Security Notes →")) { NavigateUri = uri };
                link.RequestNavigate += (_, e) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); } catch { }
                    e.Handled = true;
                };
                line.Inlines.Add(link);
            }
            body.Children.Add(line);
            body.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        }

        if (shown.Count == 0)
        {
            body.Children.Add(Ui.EmptyState(filter == "patched"
                ? "No Patched CVEs Found"
                : "No Unpatched Vulnerabilities. This device is running a fully patched version of macOS.", CheckGlyph));
        }
        else
        {
            var rows = shown.Select(c => new CveRow(c.Id, c.Url ?? "", Format.OrDash(c.OsVersion), Format.OrDash(c.PatchedVersion),
                c.Status == "Patched" ? "Patched" : c.ActivelyExploited ? "Exploited" : "Unpatched",
                c.Status == "Patched" ? Tone.Success : c.ActivelyExploited ? Tone.Error : Tone.Warning,
                "SOFA", Tone.Purple)).ToList();
            body.Children.Add(Table.Build(rows,
                Col.Link("CVE ID", "Cve", "Url", 170),
                Col.Text("OS Version", "OsVersion", 140),
                Col.Text("Patched In", "PatchedIn", star: true),
                Col.Pill("Status", "Status", "StatusTone", 110),
                Col.Pill("Source", "Source", "SourceTone", 90)));
        }
        return Ui.TableCard("Common Vulnerabilities and Exposures", subtitle, body, badges);
    }

    // ── Detections ───────────────────────────────────────────────────

    private static UIElement DetectionsCard(MacSecurityInfo s)
    {
        var list = s.Detections;
        var summary = s.Summary;
        var rows = list.Select(d => new DetectionRow(
            d.ThreatName,
            Format.OrUnknown(d.Severity),
            (d.Severity ?? "").ToLowerInvariant() switch { "severe" => Tone.Error, "high" => Tone.Orange, "moderate" => Tone.Warning, "low" => Tone.Info, _ => Tone.Neutral },
            Format.OrDash(d.Category),
            Format.OrUnknown(d.Status),
            d.Mark switch { MacSecurityInfo.StatusMark.Good => Tone.Success, MacSecurityInfo.StatusMark.Attention => Tone.Warning, MacSecurityInfo.StatusMark.Bad => Tone.Error, _ => Tone.Neutral },
            Format.OrUnknown(d.Source),
            d.DetectedAt is null ? "—" : Format.ShortDateTime(d.DetectedAt),
            d)).ToList();

        var subtitle = rows.Count > 0 ? $"{Format.Plural(rows.Count, "detection")} from AV/EDR products (last 30 days)" : "No threats detected in the last 30 days";
        var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (rows.Count > 0 && (summary.Total30d > 0 || summary.Blocked30d > 0 || summary.Cleaned30d > 0))
        {
            void B(string text, Tone tone) { var p = Ui.Pill(text, tone); p.Margin = new Thickness(0, 0, 6, 0); badges.Children.Add(p); }
            B($"{summary.Total30d} total", summary.HasActiveThreats ? Tone.Error : Tone.Neutral);
            if (summary.Blocked30d > 0) B($"{summary.Blocked30d} blocked", Tone.Info);
            if (summary.Cleaned30d > 0) B($"{summary.Cleaned30d} cleaned", Tone.Success);
            if (summary.Allowed30d > 0) B($"{summary.Allowed30d} allowed", Tone.Error);
        }

        UIElement body;
        if (rows.Count == 0) body = Ui.EmptyState("No Threats Detected. No malware, PUA, or security alerts in the last 30 days.", CheckGlyph);
        else
        {
            var grid = Table.Build(rows,
                Col.Text("Threat", "Threat", star: true),
                Col.Pill("Severity", "Severity", "SeverityTone", 100),
                Col.Text("Category", "Category", 130),
                Col.Pill("Status", "Status", "StatusTone", 120),
                Col.Text("Source", "Source", 140),
                Col.Text("Detected", "Detected", 150));
            Table.RowDetails(grid, item => item is DetectionRow { Detection: { HasDetails: true } d } ? DetectionDetails(d) : new Border());
            body = grid;
        }
        return Ui.TableCard("Threat Detections", subtitle, body, badges);
    }

    private static UIElement DetectionDetails(MacSecurityInfo.Detection d)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 6, 20, 12) };
        void Line(string label, string? value, bool mono = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = label, Width = 90, FontSize = 11.5, Foreground = Ui.Brush("TextSecondaryBrush") });
            var v = new TextBlock { Text = value, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("TextPrimaryBrush") };
            if (mono) v.FontFamily = (System.Windows.Media.FontFamily)Ui.Res("MonoFont");
            row.Children.Add(v);
            panel.Children.Add(row);
        }
        Line("Rule ID", d.ThreatId, true);
        Line("Path", d.FilePath, true);
        Line("Process", d.ProcessName, true);
        Line("User", d.User);
        Line("Action", d.ActionTaken);
        Line("Details", d.Description);
        Line("Event ID", d.EventId, true);
        return panel;
    }
}
