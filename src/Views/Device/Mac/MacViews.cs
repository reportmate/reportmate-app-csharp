using System.Windows;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Device.Mac;

/// <summary>
/// A fleet Mac's tabs, laid out as the web and Mac apps lay them out, from the Mac
/// client's own module shapes. Each tab has a view of its own (Mac&lt;Tab&gt;View);
/// a view returns null when it has nothing to draw from, and the tab falls back to
/// showing the module as the Mac sent it.
/// </summary>
public static class MacViews
{
    public static UIElement? Build(string tab, DeviceSnapshot s) => tab switch
    {
        "info" => MacInfoView.Build(s),
        "installs" => MacInstallsView.Build(s),
        "applications" => MacApplicationsView.Build(s),
        "system" => MacSystemView.Build(s),
        "management" => MacManagementView.Build(s),
        "identity" => MacIdentityView.Build(s),
        "hardware" => MacHardwareView.Build(s),
        "peripherals" => MacPeripheralsView.Build(s),
        "security" => MacSecurityView.Build(s),
        "network" => MacNetworkView.Build(s),
        _ => null,
    };
}
