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
    /// The fleet's own settings, which every client sees the same way. They are
    /// presented read-only: this app is a reader, and an editor here would be the
    /// only writer of a document the web app owns the schema for.
    /// </summary>
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
            value?.Inventory, stamp);
        Section(SecurityBody, "Security Rules",
            "The rules the fleet's security reporting is measured against.",
            value?.Security, stamp);
        Kiosk(value?.Kiosk, stamp);
        Maintenance();
    }

    private static void Section(StackPanel body, string title, string blurb,
        System.Text.Json.JsonElement? content, string? stamp)
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
    }

    /// <summary>
    /// The web's Maintenance section runs destructive fleet-wide operations --
    /// clearing install errors, reclassifying installs, resetting usage baselines.
    /// They are named here rather than wired up: a desktop client is the wrong place
    /// to fire one by accident, and none of them has a confirmation story yet.
    /// </summary>
    private void Maintenance()
    {
        MaintenanceBody.Children.Add(Ui.Text("Maintenance", "SectionHeaderStyle"));
        var sub = Ui.Caption(
            "Fleet maintenance runs from the web app. These operations change data for "
            + "every device and are deliberately not available here.");
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 2, 0, 10);
        MaintenanceBody.Children.Add(sub);

        foreach (var item in new[]
                 {
                     "Clear stale install errors",
                     "Reclassify install statuses",
                     "Reset usage-history baselines",
                 })
        {
            var row = Ui.Caption("· " + item);
            row.Margin = new Thickness(0, 0, 0, 4);
            MaintenanceBody.Children.Add(row);
        }
    }

    private static TextBlock Stamp(string text)
    {
        var stamp = Ui.Caption(text);
        stamp.Margin = new Thickness(0, 10, 0, 0);
        return stamp;
    }
}
