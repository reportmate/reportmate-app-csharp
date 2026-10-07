using System.Windows;
using ModernWpf;

namespace ReportMate.App.Views.Shared;

/// <summary>
/// Where the dashboard's palette and styles come from.
/// </summary>
/// <remarks>
/// The ReportMate app loads Themes/Palette.*.xaml and Themes/Styles.xaml into its
/// application resources (App.xaml), and every lookup resolves there. An app that
/// embeds the dashboard has resources of its own, often under the same keys, so
/// there the dashboard keeps its own copy instead: one palette dictionary, refilled
/// when the theme changes, and the styles, both merged into the view and each page
/// it shows. The host's resources are never touched.
/// </remarks>
public static class DashboardResources
{
    private static ResourceDictionary? _palette;
    private static ResourceDictionary? _styles;

    /// <summary>True when the dashboard carries its own resources rather than reading the application's.</summary>
    public static bool IsScoped => _styles is not null;

    internal static void EnableScoped()
    {
        if (_styles is not null) return;
        _palette = new ResourceDictionary();
        _styles = Load("Styles");
        ApplyTheme();
        ThemeManager.Current.ActualApplicationThemeChanged += (_, _) => ApplyTheme();
    }

    /// <summary>
    /// Give an element the dashboard's resources when they are scoped; a no-op when
    /// they live in the application's resources.
    /// </summary>
    public static void Adopt(FrameworkElement element)
    {
        if (_palette is null || _styles is null) return;
        var merged = element.Resources.MergedDictionaries;
        if (merged.Contains(_styles)) return;
        merged.Add(_palette);
        merged.Add(_styles);
    }

    /// <summary>A dashboard resource by key: its own copy when scoped, else the application's.</summary>
    public static object? TryFind(object key)
    {
        if (_palette is not null && _palette.Contains(key)) return _palette[key];
        if (_styles is not null && _styles.Contains(key)) return _styles[key];
        return Application.Current?.TryFindResource(key);
    }

    // Refilled in place rather than replaced, so every DynamicResource already
    // pointing into it picks up the new theme's brushes.
    private static void ApplyTheme()
    {
        if (_palette is null) return;
        var dark = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;
        var source = Load(dark ? "Palette.Dark" : "Palette.Light");
        foreach (var key in source.Keys) _palette[key] = source[key];
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/ReportMate.UI;component/Themes/{name}.xaml", UriKind.Absolute),
    };
}
