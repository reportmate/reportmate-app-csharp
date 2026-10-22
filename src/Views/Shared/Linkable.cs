using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Shared;

/// <summary>
/// Makes a piece of a dashboard widget -- a number, a bar, a legend row, a chip, a
/// list item -- a way into the report that owns it, filtered to what it shows. The
/// target is a <see cref="DeepLink"/>, so the click opens exactly what the same link
/// opens from the web or the Mac app.
/// </summary>
public static class Linkable
{
    /// <summary>Wrap content so a click, Enter or Space follows the link.</summary>
    public static Border Wrap(UIElement content, DeepLink link, string? tip = null)
    {
        var border = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
        };
        Attach(border, link, tip);
        return border;
    }

    /// <summary>
    /// Make an element already in place follow a link: hover and keyboard focus tint
    /// it, so it reads as something to click, and its tooltip names where it goes.
    /// </summary>
    public static void Attach(Border target, DeepLink link, string? tip = null)
    {
        var rest = target.Background;
        target.Cursor = Cursors.Hand;
        target.Focusable = true;
        target.ToolTip = tip ?? Describe(link);
        System.Windows.Automation.AutomationProperties.SetName(target, target.ToolTip?.ToString() ?? "");
        KeyboardNavigation.SetIsTabStop(target, true);

        // A chip already sits on the subtle fill, so it lights one step further.
        var lit = rest is SolidColorBrush { Color.A: > 0 } ? Ui.Brush("SubtleFillHoverBrush") : Ui.Brush("SubtleFillBrush");
        void Lit(bool on) => target.Background = on ? lit : rest;
        target.MouseEnter += (_, _) => Lit(true);
        target.MouseLeave += (_, _) => Lit(target.IsKeyboardFocused);
        target.GotKeyboardFocus += (_, _) => Lit(true);
        target.LostKeyboardFocus += (_, _) => Lit(target.IsMouseOver);

        var pressed = false;
        target.MouseLeftButtonDown += (_, e) => { pressed = true; e.Handled = true; };
        target.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            DashboardView.Follow(target, link);
        };
        target.MouseLeave += (_, _) => pressed = false;
        target.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            DashboardView.Follow(target, link);
        };
    }

    /// <summary>
    /// Make a <see cref="Ui.StatBlock"/> card's header open its report, unfiltered,
    /// the way every dashboard card header behaves on the web and the Mac.
    /// </summary>
    public static Border LinkHeader(Border card, DeepLink link, string? tip = null)
    {
        if (card.Child is not StackPanel { Children.Count: > 0 } root || root.Children[0] is not Grid header) return card;
        // The header is a Grid; host it in a Border so it can carry the hover tint.
        root.Children.RemoveAt(0);
        var margin = header.Margin;
        header.Margin = new Thickness(0);
        var host = new Border { Child = header, Padding = margin, Background = Brushes.Transparent, FocusVisualStyle = null };
        root.Children.Insert(0, host);
        Attach(host, link, tip ?? $"Open {Describe(link)}");
        return card;
    }

    /// <summary>"the management report, provider Unmanaged" -- where a link goes, in words.</summary>
    public static string Describe(DeepLink link)
    {
        var place = link.Section switch
        {
            "devices" => "the device list",
            "events" when link.Argument == "failures" => "failed check-ins",
            "events" => "events",
            "applications" when link.Argument?.StartsWith("usage/", StringComparison.OrdinalIgnoreCase) == true
                => $"usage for {link.Argument["usage/".Length..]}",
            _ => $"the {link.Section} report",
        };
        var filters = link.Query.AllKeys
            .Where(k => !string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(link.Query[k]))
            .Select(k => $"{k} {link.Query[k]}")
            .ToList();
        return filters.Count == 0 ? place : $"{place}, filtered to {string.Join(", ", filters)}";
    }
}
