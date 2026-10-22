// Explicit usings: this file is also compiled into the test project.
using System;
using System.Diagnostics;

namespace ReportMate.App.Services;

/// <summary>
/// The one way the app opens a link in the browser. Links come from data a device
/// reported -- a CVE's reference, a release note -- and ShellExecute will run
/// whatever it is handed: a <c>file:</c> path, a UNC share, an <c>ms-settings:</c> or
/// other protocol handler. Only an absolute http or https address with a host is opened.
/// </summary>
public static class SafeLinks
{
    /// <summary>The link as a web address safe to open, or null when it is anything else.</summary>
    public static Uri? Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrEmpty(uri.Host) || uri.IsUnc || uri.IsFile) return null;
        return uri;
    }

    public static bool IsSafe(string? link) => Parse(link) is not null;

    /// <summary>The link when it is safe to open, else an empty string, for binding into a table.</summary>
    public static string Sanitise(string? link) => Parse(link)?.AbsoluteUri ?? "";

    /// <summary>Open a web link in the default browser; anything else is refused.</summary>
    public static bool TryOpen(string? link)
    {
        if (Parse(link) is not { } uri) return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
