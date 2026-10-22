using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device;

/// <summary>
/// One device-page tab. Each tab rebuilds its content from the snapshot on demand;
/// the page only realises a tab the first time it is selected and again after a refresh.
/// </summary>
public abstract class DeviceTab : ContentControl
{
    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract string Glyph { get; }
    public abstract Accent Accent { get; }
    public abstract string Description { get; }

    private DeviceSnapshot? _rendered;

    protected DeviceTab()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    public void Render(DeviceSnapshot snapshot)
    {
        if (ReferenceEquals(_rendered, snapshot)) return;
        _rendered = snapshot;
        Content = snapshot.IsRemoteMac && MacModules.Length > 0 ? BuildMac(snapshot) : Build(snapshot);
    }

    /// <summary>
    /// The modules this tab draws from, for a Mac's report. Empty for a tab whose
    /// data is the same shape on both platforms (Events), which draws as usual.
    /// </summary>
    protected virtual string[] MacModules => [Id];

    /// <summary>
    /// A fleet Mac's report. The Mac client sends its own module shapes, which this
    /// app's module classes -- the Windows client's -- only partly read, so the tab
    /// shows every value the Mac sent, by module, rather than a Windows layout with
    /// gaps where the Mac's fields did not map.
    /// </summary>
    protected virtual UIElement BuildMac(DeviceSnapshot s)
    {
        var page = new StackPanel();
        page.Children.Add(Ui.TabHeader(Label, Description, Glyph, Accent));
        AddMacModules(page, s);
        return page;
    }

    protected void AddMacModules(Panel page, DeviceSnapshot s)
    {
        var note = Ui.Caption("This Mac's report as it sent it. The Mac and web apps draw these fields in their own layouts.");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 0, 0, 12);
        page.Children.Add(note);
        foreach (var module in MacModules)
        {
            UIElement card = s.RawModules.TryGetValue(module, out var raw)
                ? Ui.StatBlock(Format.Capitalize(module),
                    s.CollectedAtFor(module) is { } at ? $"Collected {Format.RelativeTime(at)}" : null,
                    Glyph, Accent, Views.Shared.JsonTree.Build(raw))
                : ModuleMissing(module, s);
            if (card is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 16);
            page.Children.Add(card);
        }
    }

    public void Invalidate() => _rendered = null;

    protected abstract UIElement Build(DeviceSnapshot snapshot);

    /// <summary>
    /// Body shown when a module has no data. A module whose JSON was on disk but
    /// unreadable is a different problem from one the endpoint has not collected,
    /// and saying "not collected" for a parse failure hides a real bug.
    /// </summary>
    protected UIElement ModuleMissing(string moduleId, DeviceSnapshot? snapshot = null)
    {
        var error = snapshot?.ModuleError(moduleId);
        return Ui.Card(Ui.EmptyState(error is null
            ? $"No {moduleId} data has been reported for this device yet."
            : $"The {moduleId} data on this device could not be read: {error}"));
    }
}
