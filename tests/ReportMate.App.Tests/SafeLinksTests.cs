using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>Only a web address from device-reported data ever reaches ShellExecute.</summary>
public class SafeLinksTests
{
    [Theory]
    [InlineData("https://support.apple.com/en-us/100100", true)]
    [InlineData("http://www.example.org/notes", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"\\server.example.org\share\run.exe", false)]
    [InlineData("file://server.example.org/share/run.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:windowsupdate", false)]
    [InlineData("ms-msdt:/id PCWDiagnostic", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("calc.exe", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnAbsoluteWebAddressWithAHostIsSafe(string? link, bool safe)
    {
        Assert.Equal(safe, SafeLinks.IsSafe(link));
        Assert.Equal(safe, SafeLinks.Sanitise(link).Length > 0);
    }

    [Fact]
    public void AnUnsafeLinkIsNeverOpened() =>
        Assert.False(SafeLinks.TryOpen("ms-settings:windowsupdate"));
}
