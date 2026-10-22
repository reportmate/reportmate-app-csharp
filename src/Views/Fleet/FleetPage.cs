using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Shared plumbing for the fleet-wide pages (Dashboard, Devices, Events, Reports).
/// Each one loads once when shown, refreshes itself on a timer, and renders a
/// specific explanation instead of an empty page when the API cannot serve it.
/// </summary>
public abstract class FleetPage : Page
{
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(28, 22, 28, 32),
    };

    private readonly LoadingStrip _strip = new();
    private DispatcherTimerHolder? _timer;
    private bool _loading;
    private bool _skeleton;
    private DateTime? _loadedAt;

    /// <summary>
    /// How old a page's data may be when it is shown again before it refreshes in
    /// the background. Coming back to a page shows what it had at once either way.
    /// </summary>
    private static readonly TimeSpan RefreshOnReturn = TimeSpan.FromMinutes(1);

    protected FleetPage()
    {
        var root = new Grid();
        root.Children.Add(_scroll);
        root.Children.Add(_strip);
        Content = root;
        Loaded += async (_, _) =>
        {
            _timer ??= DispatcherTimerHolder.Every(TimeSpan.FromMinutes(5), async () => await ReloadAsync());
            if (_scroll.Content is null) await ReloadAsync();
            else if (_loadedAt is null || DateTime.UtcNow - _loadedAt > RefreshOnReturn) await ReloadAsync();
        };
        Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
    }

    /// <summary>
    /// Take over what the page this one replaces was showing, so a page rebuilt for
    /// a new link or platform scope opens on the last data, marked as updating,
    /// rather than on a skeleton. Skeletons are for the very first load only.
    /// </summary>
    public void Adopt(FleetPage previous)
    {
        if (_scroll.Content is not null || previous._skeleton || previous._scroll.Content is not UIElement body) return;
        previous._scroll.Content = null;
        _scroll.Content = body;
        _scroll.ScrollToVerticalOffset(previous._scroll.VerticalOffset);
    }

    /// <summary>
    /// How many of the page's sources have answered, for the loading strip's
    /// determinate bar. Pages that do not say get the indeterminate sweep.
    /// </summary>
    protected void Progress(int done, int total) => _strip.Progress(done, total);

    /// <summary>Fetch and render. Implementations return the page body.</summary>
    protected abstract Task<UIElement> BuildAsync();

    /// <summary>
    /// Title, subtitle and accent, known before any data arrives. A page that shows
    /// only a spinner while it fetches takes its own identity off the screen, and
    /// these pages can spend most of a minute fetching, so the heading is drawn
    /// straight away and the body fills in underneath it.
    /// </summary>
    protected virtual (string Title, string Subtitle, Accent Accent)? Heading => null;

    /// <summary>
    /// The link that opened this page, if it was opened by one. Taken once, so a later
    /// refresh re-renders the same filters without a link re-applying itself.
    /// </summary>
    protected DeepLink? Link { get; private set; }

    /// <summary>A filter value from the arriving link, or null.</summary>
    protected string? Filter(string key) => Link?[key];

    public async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            Link ??= Views.Shared.DashboardView.TakePendingLink();
            // The first load draws a skeleton; any later one leaves the page as it
            // was and shows only the strip until the new body is ready.
            if (_scroll.Content is null)
            {
                _scroll.Content = Loading();
                _skeleton = true;
            }
            _strip.Start(labelled: _skeleton);
            var body = await BuildAsync();
            _scroll.Content = body;
            _skeleton = false;
            _loadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _scroll.Content = Ui.Card(Ui.EmptyState($"This page could not be built: {ex.Message}"));
            _skeleton = false;
        }
        finally
        {
            _loading = false;
            _strip.Stop();
        }
    }

    /// <summary>
    /// The page as it will look, in skeleton, while it loads: the real heading when
    /// the page knows it, and grey blocks where its cards and table will be.
    /// </summary>
    private UIElement Loading()
    {
        var page = new StackPanel();
        page.Children.Add(Heading is (string title, string subtitle, Accent accent)
            ? Ui.TabHeader(title, subtitle, "", accent)
            : Skeleton.Pulse(Skeleton.PageHeader()));
        page.Children.Add(Skeleton.Pulse(LoadingBody()));
        return page;
    }

    /// <summary>The skeleton under the heading; a report's cards and table unless a page says otherwise.</summary>
    protected virtual UIElement LoadingBody() => Skeleton.ReportBody();

    /// <summary>
    /// The page body for a fleet read that did not succeed. The distinction matters:
    /// a forbidden read is a provisioning gap, not an outage, and saying so saves
    /// whoever reads it from chasing the network.
    /// </summary>
    protected static UIElement FleetUnavailable(FleetApiClient.FleetStatus status, string? detail)
    {
        var (title, guidance) = status switch
        {
            FleetApiClient.FleetStatus.NotConfigured =>
                // The detail line underneath names the exact setting that is missing.
                ("This device cannot read the fleet yet",
                 "The fleet pages need an API address and a way to read from it."),
            FleetApiClient.FleetStatus.Forbidden =>
                ("This device cannot read fleet data",
                 "The endpoint's API key is scoped for reporting data in, not reading the fleet back out. "
                 + "A read-scoped credential is needed before the fleet pages can show anything."),
            FleetApiClient.FleetStatus.Unauthorized =>
                ("The API rejected this device's credentials",
                 "The configured API key was not accepted."),
            FleetApiClient.FleetStatus.Malformed =>
                ("The API response could not be read",
                 "The fleet endpoint answered with something this version does not understand."),
            _ =>
                ("The ReportMate API could not be reached",
                 "The fleet pages need the API; the per-device page keeps working from the local cache."),
        };

        var panel = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
        var heading = Ui.Text(title, "TitleTextStyle");
        heading.TextAlignment = TextAlignment.Center;
        panel.Children.Add(heading);
        var body = Ui.Text(guidance);
        body.TextAlignment = TextAlignment.Center;
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(body);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var d = Ui.Caption(detail);
            d.TextAlignment = TextAlignment.Center;
            d.TextWrapping = TextWrapping.Wrap;
            d.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(d);
        }
        return Ui.Card(panel);
    }
}

/// <summary>A dispatcher timer that stops cleanly when a page goes away.</summary>
public sealed class DispatcherTimerHolder
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    private DispatcherTimerHolder(System.Windows.Threading.DispatcherTimer timer) => _timer = timer;

    public static DispatcherTimerHolder Every(TimeSpan interval, Func<Task> action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
        timer.Tick += async (_, _) => await action();
        timer.Start();
        return new DispatcherTimerHolder(timer);
    }

    public void Stop() => _timer.Stop();
}
