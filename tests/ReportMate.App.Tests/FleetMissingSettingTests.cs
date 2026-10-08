using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// A fleet page that cannot read for want of configuration names the setting
/// to fill in, by the label it has in Settings.
/// </summary>
public class FleetMissingSettingTests
{
    [Fact]
    public void NoAddressNamesTheApiUrl()
    {
        Assert.Contains("API URL", FleetApiClient.MissingSetting(hasApiUrl: false, hostSignIn: false));
    }

    [Fact]
    public void NoCredentialNamesTheReadPassphrase()
    {
        var text = FleetApiClient.MissingSetting(hasApiUrl: true, hostSignIn: false);
        Assert.Contains("Read passphrase", text);
        Assert.DoesNotContain("API URL", text);
    }

    [Fact]
    public void AHostWithoutATokenIsNamedAlongsideThePassphrase()
    {
        var text = FleetApiClient.MissingSetting(hasApiUrl: true, hostSignIn: true);
        Assert.Contains("sign-in token", text);
        Assert.Contains("Read passphrase", text);
    }
}
