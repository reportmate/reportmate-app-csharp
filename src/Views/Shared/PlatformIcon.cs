using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ReportMate.App.Views.Shared;

/// <summary>
/// The Apple logo and the Windows four-pane logo as filled vector shapes, the marks
/// the Mac app's toggle uses (SF Symbol <c>apple.logo</c> and a 2×2 grid). Segoe has
/// no Apple logo -- the laptop and tower glyphs stood in for them -- and an emoji
/// renders differently on every machine, so both are drawn as paths.
/// </summary>
public static class PlatformIcon
{
    /// <summary>The Apple logo, on a 24-unit square.</summary>
    public static readonly Geometry AppleGeometry = Frozen(
        "M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 "
        + "1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 "
        + "3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559"
        + "-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052"
        + "-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701");

    /// <summary>The Windows logo: four squares in a 2×2 grid, on a 24-unit square.</summary>
    public static readonly Geometry WindowsGeometry = Frozen(
        "M 0,0 H 11.4 V 11.4 H 0 Z M 12.6,0 H 24 V 11.4 H 12.6 Z M 0,12.6 H 11.4 V 24 H 0 Z M 12.6,12.6 H 24 V 24 H 12.6 Z");

    private static Geometry Frozen(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>"mac", "windows", or null for a platform neither logo stands for.</summary>
    public static string? Kind(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return null;
        var p = platform.ToLowerInvariant();
        if (p.Contains("win")) return "windows";
        if (p.Contains("mac") || p.Contains("darwin") || p.StartsWith("os x")) return "mac";
        return null;
    }

    public static Geometry? GeometryFor(string? platform) => Kind(platform) switch
    {
        "mac" => AppleGeometry,
        "windows" => WindowsGeometry,
        _ => null,
    };

    /// <summary>The platform's logo at <paramref name="size"/>, or null when it has none.</summary>
    public static Path? For(string? platform, double size = 14, Brush? fill = null)
    {
        if (GeometryFor(platform) is not { } geometry) return null;
        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            Fill = fill ?? Ui.Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Kind(platform) == "mac" ? "macOS" : "Windows",
        };
        return path;
    }

    /// <summary>A label with the platform's logo before it.</summary>
    public static UIElement Labelled(string? platform, UIElement label, double size = 13)
    {
        if (For(platform, size) is not { } icon) return label;
        icon.Margin = new Thickness(0, 0, 7, 0);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(label);
        return panel;
    }
}

/// <summary>Platform name to its logo's geometry, for table cells.</summary>
public sealed class PlatformGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PlatformIcon.GeometryFor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
