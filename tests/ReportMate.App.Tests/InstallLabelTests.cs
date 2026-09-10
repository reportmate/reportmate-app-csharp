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
    [InlineData("completed")]
    [InlineData("success")]
    public void TheRunCompletedSpellings_AreOneAnswer(string status) =>
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
    public void CompletedCountsAsInstalled_EvenWithNoVersion()
    {
        // The 53 rows spelled this way are script items with no installed version,
        // which looks like a reason to separate them. It is not: a script item has
        // no version to carry, and having run to completion is what installed means
        // for that item type. The web's ladder and this app's own verdict ladder
        // both say so, and one app must not disagree with itself.
        Assert.Equal("Installed", Label("completed"));
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
