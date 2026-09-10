namespace ReportMate.App.Services;

/// <summary>Which platform's devices the fleet pages are showing.</summary>
public enum PlatformScope
{
    All,
    Windows,
    Mac,
}

/// <summary>
/// The toolbar's platform filter, applied by every fleet page.
/// </summary>
/// <remarks>
/// A single setting shared by every page rather than a control per page, because
/// it is the same question each time and the web treats it the same way: choosing
/// Windows on the dashboard and then opening a report should not quietly widen back
/// to the whole fleet.
///
/// Matching is by prefix, not equality. The rows do not agree on what a platform is
/// called -- "Windows" in one module, "Windows 11" and "Windows 10" in another,
/// "macOS" in both -- so an equality test against "Windows" silently drops every
/// device in the modules that carry a version.
/// </remarks>
public static class PlatformFilter
{
    private static PlatformScope _current = PlatformScope.All;

    /// <summary>Raised when the scope changes, so open pages can reload.</summary>
    public static event Action? Changed;

    public static PlatformScope Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Selecting the platform already showing goes back to the whole fleet.</summary>
    public static void Toggle(PlatformScope scope) =>
        Current = _current == scope ? PlatformScope.All : scope;

    /// <summary>Whether a row's reported platform is in scope.</summary>
    public static bool Includes(string? platform)
    {
        if (_current == PlatformScope.All) return true;
        if (string.IsNullOrWhiteSpace(platform)) return false;

        return _current switch
        {
            PlatformScope.Windows => platform.StartsWith("Windows", StringComparison.OrdinalIgnoreCase),
            PlatformScope.Mac => platform.StartsWith("mac", StringComparison.OrdinalIgnoreCase)
                || platform.StartsWith("OS X", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    /// <summary>The scope as it reads in a caption, or null when the fleet is whole.</summary>
    public static string? Label => _current switch
    {
        PlatformScope.Windows => "Windows",
        PlatformScope.Mac => "macOS",
        _ => null,
    };
}
