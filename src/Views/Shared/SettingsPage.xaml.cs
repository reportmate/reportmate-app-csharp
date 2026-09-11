using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.ViewModels;

namespace ReportMate.App.Views.Shared;

public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = new();
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Load();

        // Null is ModernWpf's "follow the system", which is what the app starts on.
        ThemeBox.SelectedIndex = ModernWpf.ThemeManager.Current.ApplicationTheme switch
        {
            ModernWpf.ApplicationTheme.Light => 1,
            ModernWpf.ApplicationTheme.Dark => 2,
            _ => 0,
        };
        _loaded = true;

        Loaded += async (_, _) => await LoadFleetSettingsAsync();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        ModernWpf.ThemeManager.Current.ApplicationTheme = ThemeBox.SelectedIndex switch
        {
            1 => ModernWpf.ApplicationTheme.Light,
            2 => ModernWpf.ApplicationTheme.Dark,
            _ => null,
        };
    }

    private void OnSectionChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || GeneralBody is null) return;
        foreach (var (name, body) in Sections())
            body.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
    }

    private (string, StackPanel)[] Sections() =>
    [
        ("General", GeneralBody), ("Inventory", InventoryBody), ("Security", SecurityBody),
        ("Kiosk", KioskBody), ("Maintenance", MaintenanceBody),
    ];

    // PasswordBox.Password is not bindable by design; push it into the view model by hand.
    private void OnPassphraseChanged(object sender, RoutedEventArgs e) => _vm.Passphrase = PassphraseBox.Password;

    /// <summary>
    /// The fleet's own settings, which every client sees the same way.
    /// </summary>
    /// <remarks>
    /// Read-only, and not as a style choice: GET /api/v1/settings takes
    /// <c>verify_authentication</c>, which the fleet passphrase satisfies, while PUT
    /// takes <c>require_internal_secret</c>. The API's own docstring for the write
    /// path says "Restricted to internal-service callers (the Next.js proxy, which
    /// enforces the admin role). The fleet passphrase and managed identities cannot
    /// reach this." So a native editor could not save even if it drew one, unless it
    /// carried the internal secret -- which would put the credential that gates
    /// fleet-wide writes on every admin's desktop.
    /// </remarks>
    private async Task LoadFleetSettingsAsync()
    {
        foreach (var body in new[] { InventoryBody, SecurityBody, KioskBody, MaintenanceBody })
        {
            body.Children.Clear();
            body.Children.Add(Ui.Caption("Loading…"));
        }

        var result = await FleetSettings.GetAsync();
        foreach (var body in new[] { InventoryBody, SecurityBody, KioskBody, MaintenanceBody })
            body.Children.Clear();

        if (!result.Ok)
        {
            var why = result.Detail ?? "The fleet settings could not be read.";
            foreach (var body in new[] { InventoryBody, SecurityBody, KioskBody, MaintenanceBody })
                body.Children.Add(Ui.EmptyState(why));
            return;
        }

        var value = result.Data!.Value;
        var stamp = result.Data!.UpdatedAt is { } at
            ? $"Last changed {at.ToLocalTime():d MMM yyyy HH:mm}"
              + (string.IsNullOrWhiteSpace(result.Data!.UpdatedBy) ? "" : $" by {result.Data!.UpdatedBy}")
            : null;

        Section(InventoryBody, "Inventory Mapping",
            "How device fields map onto the inventory the fleet reports.",
            value?.Inventory, stamp, "inventory");
        Section(SecurityBody, "Security Rules",
            "The rules the fleet's security reporting is measured against.",
            value?.Security, stamp, "rules");
        Kiosk(value?.Kiosk, stamp);
        Maintenance();
    }

    private static void Section(StackPanel body, string title, string blurb,
        System.Text.Json.JsonElement? content, string? stamp, string section)
    {
        body.Children.Add(Ui.Text(title, "SectionHeaderStyle"));
        var sub = Ui.Caption(blurb);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 2, 0, 10);
        body.Children.Add(sub);

        // Nothing is configured for this section on this fleet. Saying so beats an
        // empty editor, which reads as a failure to load.
        if (content is null or { ValueKind: System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined })
        {
            body.Children.Add(Ui.EmptyState($"No {title.ToLowerInvariant()} are configured for this fleet."));
            OpenInWeb(body, section);
            return;
        }

        var text = new TextBox
        {
            Text = System.Text.Json.JsonSerializer.Serialize(content,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
        };
        body.Children.Add(text);
        if (stamp is not null) body.Children.Add(Stamp(stamp));
        OpenInWeb(body, section);
    }

    private void Kiosk(KioskSettings? kiosk, string? stamp)
    {
        KioskBody.Children.Add(Ui.Text("Kiosk Displays", "SectionHeaderStyle"));
        var sub = Ui.Caption("How a wall display presents the dashboard.");
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 2, 0, 10);
        KioskBody.Children.Add(sub);

        if (kiosk is null)
        {
            KioskBody.Children.Add(Ui.EmptyState("No kiosk display is configured for this fleet."));
            OpenInWeb(KioskBody, "general");
            return;
        }

        foreach (var (label, value) in new (string, string?)[]
                 {
                     ("Home page", kiosk.HomePath),
                     ("Theme", kiosk.Theme),
                     ("Zoom", kiosk.Zoom is { } z ? $"{z:0.##}x" : null),
                     ("Idle before returning home", kiosk.IdleMinutes is { } m ? $"{m} minutes" : null),
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var name = Ui.Caption(label);
            name.Width = 200;
            row.Children.Add(name);
            row.Children.Add(Ui.Text(value ?? "Not set", "BodyTextStyle"));
            KioskBody.Children.Add(row);
        }

        if (stamp is not null) KioskBody.Children.Add(Stamp(stamp));
        OpenInWeb(KioskBody, "general");
    }

    /// <summary>
    /// The three operations the web's Maintenance section runs, named rather than
    /// wired.
    /// </summary>
    /// <remarks>
    /// Not wired because of where the authorisation lives. The web gates this
    /// section on an Entra role, <c>useHasRole(ADMIN_ROLE)</c>, but that check is in
    /// the page; the API behind it takes <c>verify_authentication</c>, which the
    /// fleet passphrase satisfies. So the role is enforced by the web UI and not by
    /// the endpoint, and a desktop client holding a read passphrase would reach
    /// DELETE /api/v1/device/{serial} -- a permanent deletion of a device and all
    /// its data -- with no role check anywhere in the path.
    ///
    /// That is a question about the API's authorisation rather than about this page,
    /// so this app declines to be the client that exercises it.
    /// </remarks>
    private void Maintenance()
    {
        MaintenanceBody.Children.Add(Ui.Text("Maintenance", "SectionHeaderStyle"));
        var sub = Ui.Caption(
            "Fleet maintenance runs from the web app, which gates it on an administrator "
            + "role. These operations delete data permanently and are deliberately not "
            + "available here.");
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 2, 0, 10);
        MaintenanceBody.Children.Add(sub);

        foreach (var item in new[]
                 {
                     "Clear stale install errors and warnings, older than a chosen age",
                     "Delete one device, by serial number",
                     "Bulk delete devices, up to 100 at a time",
                 })
        {
            var row = Ui.Caption("· " + item);
            row.Margin = new Thickness(0, 0, 0, 4);
            MaintenanceBody.Children.Add(row);
        }

        OpenInWeb(MaintenanceBody, "maintenance");
    }

    /// <summary>
    /// A link to the same section in the web app, which is where these are edited.
    /// A read-only pane that does not say where the switch is just looks broken.
    /// </summary>
    private static void OpenInWeb(StackPanel body, string section)
    {
        var web = ConfigManager.Instance.WebDashboardUrl;
        if (string.IsNullOrWhiteSpace(web)) return;

        var link = new ModernWpf.Controls.HyperlinkButton
        {
            Content = "Edit in the web app",
            NavigateUri = new Uri($"{web}/settings#{section}"),
            Margin = new Thickness(-10, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        body.Children.Add(link);
    }

    private static TextBlock Stamp(string text)
    {
        var stamp = Ui.Caption(text);
        stamp.Margin = new Thickness(0, 10, 0, 0);
        return stamp;
    }
}
