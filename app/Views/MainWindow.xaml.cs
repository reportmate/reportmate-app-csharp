using System.Windows;
using ReportMate.App.Services;

namespace ReportMate.App.Views;

/// <summary>The ReportMate window: the dashboard, plus what belongs to a window.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ProtocolHandler.LinkReceived += OpenDeepLink;
    }

    /// <summary>Bring the window forward and open the view a reportmate:// link names.</summary>
    public void OpenDeepLink(DeepLink link)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Dashboard.OpenDeepLink(link);
    }
}
