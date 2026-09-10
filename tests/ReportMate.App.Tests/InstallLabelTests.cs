using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Cimian and Munki spell the same install states differently and one endpoint
/// returns both, so the raw field carries seventeen values for about eight states.
/// </summary>
public class InstallLabelTests
{
    private static string? Label(string status) =>
        InstallLabel.Status(JsonDocument.Parse(
            "{\"currentStatus\":" + JsonSerializer.Serialize(status) + "}").RootElement);

    [Theory]
    [InlineData("installed")]
    [InlineData("Installed")]
    [InlineData("install_succeeded")]
    public void TheInstalledSpellings_AreOneAnswer(string status) =>
        Assert.Equal("Installed", Label(status));

    [Theory]
    [InlineData("pending_install", "Pending install")]
    [InlineData("Pending Install", "Pending install")]
    [InlineData("removed", "Removed")]
    [InlineData("install_failed", "Failed")]
    [InlineData("Failed", "Failed")]
    public void SeparatorsAndCaseFold(string status, string expected) =>
        Assert.Equal(expected, Label(status));

    [Fact]
    public void CompletedIsNotAnInstall()
    {
        // All 53 rows spelled this way are script actions -- CimianPreflight,
        // osquery, SystemKeepTime -- and not one carries an installed version. The
        // word means the action ran. Folding it into Installed would have claimed
        // 53 installs that never happened.
        Assert.Equal("Completed", Label("completed"));
    }

    [Fact]
    public void PendingAndPendingInstallStayApart() =>
        // "Pending" alone does not say pending what, so it is not assumed to mean
        // a pending install.
        Assert.NotEqual(Label("Pending"), Label("pending_install"));

    [Theory]
    [InlineData("Warning", "Warning")]
    [InlineData("Error", "Error")]
    [InlineData("Not Available", "Not available")]
    public void UnrecognisedStatesKeepThemselves(string status, string expected) =>
        Assert.Equal(expected, Label(status));

    [Fact]
    public void NoStatusIsNull()
    {
        Assert.Null(InstallLabel.Status(JsonDocument.Parse("{}").RootElement));
        Assert.Null(Label("   "));
    }
}
