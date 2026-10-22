using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Entra sign-in: the same rules as the Mac app's resolvingEntra, so an audience is
/// only ever taken silently from an administrator-set https address, and only as an
/// api:// URI.
/// </summary>
public class EntraSignInTests
{
    private const string Audience = "api://reportmate.example.org";
    private const string Api = "https://api.reportmate.example.org";

    [Theory]
    [InlineData("api://reportmate.example.org", true)]
    [InlineData("api://00000000-0000-0000-0000-000000000001", true)]
    [InlineData("api://example.org/reportmate", true)]
    [InlineData("00000003-0000-0000-c000-000000000000", false)]
    [InlineData("https://graph.microsoft.com", false)]
    [InlineData("https://management.azure.com/", false)]
    [InlineData("api://", false)]
    [InlineData("api://example.org & calc", false)]
    [InlineData("api://example.org\"&calc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnApiUriIsAcceptedFromDiscovery(string? audience, bool accepted) =>
        Assert.Equal(accepted, EntraSignIn.IsAcceptableAudience(audience));

    [Theory]
    [InlineData("api://reportmate.example.org", true)]
    [InlineData("00000000-0000-0000-0000-000000000001", true)]
    [InlineData("https://graph.microsoft.com", false)]
    [InlineData("0000-not-a-guid", false)]
    [InlineData("00000000-0000-0000-0000-000000000001 && calc", false)]
    public void AConfiguredAudienceMayAlsoBeAnApplicationId(string resource, bool accepted) =>
        Assert.Equal(accepted, EntraSignIn.IsSafeResource(resource));

    [Theory]
    [InlineData("https://api.example.org", true)]
    [InlineData("http://api.example.org", false)]
    [InlineData("http://localhost:8000", true)]
    [InlineData("http://127.0.0.1:8000", true)]
    [InlineData("ftp://api.example.org", false)]
    [InlineData("not a url", false)]
    public void DiscoveryRunsOnlyOverHttps(string url, bool trusted) =>
        Assert.Equal(trusted, EntraSignIn.IsTrustedDiscoveryUrl(url));

    [Theory]
    [InlineData(ApiUrlSource.Client, true)]
    [InlineData(ApiUrlSource.Policy, true)]
    [InlineData(ApiUrlSource.Environment, true)]
    [InlineData(ApiUrlSource.Settings, false)]
    [InlineData(ApiUrlSource.Host, false)]
    [InlineData(ApiUrlSource.None, false)]
    public void OnlyAnAdministratorSetAddressIsAskedForItsAudience(ApiUrlSource source, bool discovers) =>
        Assert.Equal(discovers, EntraSignIn.MayDiscover(source, Api));

    [Theory]
    [InlineData("https://api.example.org", "https://api.example.org/api/v1/auth/config")]
    [InlineData("https://api.example.org/", "https://api.example.org/api/v1/auth/config")]
    [InlineData("https://api.example.org/api/v1", "https://api.example.org/api/v1/auth/config")]
    [InlineData("https://api.example.org/api", "https://api.example.org/api/v1/auth/config")]
    public void TheDiscoveryEndpointToleratesEitherFormOfTheAddress(string api, string expected) =>
        Assert.Equal(expected, EntraSignIn.AuthConfigUrl(api));

    [Fact]
    public void TheAuthConfigIsRead()
    {
        var config = AuthConfig.Parse("""
            {"credentials":["Authorization: Bearer","X-API-Key"],
             "oidc":{"enabled":true,"issuers":["https://login.microsoftonline.com/t/v2.0"],"audience":"api://reportmate.example.org"}}
            """);
        Assert.NotNull(config);
        Assert.True(config!.OidcEnabled);
        Assert.Equal(Audience, config.Audience);
        Assert.Single(config.Issuers);
        Assert.Null(AuthConfig.Parse("not json"));
        Assert.Null(AuthConfig.Parse("""{"credentials":[]}"""));
    }

    [Fact]
    public void AnAudienceIsChosenOnlyWhenEveryRuleHolds()
    {
        var advertised = new AuthConfig(true, Audience, []);
        Assert.Equal(Audience, EntraSignIn.ChooseAudience(null, ApiUrlSource.Client, Api, advertised));
        // Typed into Settings: the operator has not seen the audience, so none is taken.
        Assert.Null(EntraSignIn.ChooseAudience(null, ApiUrlSource.Settings, Api, advertised));
        Assert.Null(EntraSignIn.ChooseAudience(null, ApiUrlSource.Client, "http://api.example.org", advertised));
        Assert.Null(EntraSignIn.ChooseAudience(null, ApiUrlSource.Client, Api, advertised with { OidcEnabled = false }));
        Assert.Null(EntraSignIn.ChooseAudience(null, ApiUrlSource.Client, Api, advertised with { Audience = "https://graph.microsoft.com" }));
        Assert.Null(EntraSignIn.ChooseAudience(null, ApiUrlSource.Client, Api, advertised with { Audience = "00000003-0000-0000-c000-000000000000" }));
        // An administrator's own audience wins, wherever the address came from.
        Assert.Equal("api://configured", EntraSignIn.ChooseAudience("api://configured", ApiUrlSource.Settings, Api, advertised));
        Assert.Null(EntraSignIn.ChooseAudience("https://vault.azure.net", ApiUrlSource.Client, Api, advertised));
    }

    private static EntraSession Session(Func<string, string?> mint, out List<string> discovered, out List<string> minted, AuthConfig? advertised = null)
    {
        var d = discovered = [];
        var m = minted = [];
        return new EntraSession
        {
            Discover = (url, _) => { d.Add(url); return Task.FromResult(advertised ?? new AuthConfig(true, Audience, [])); },
            Mint = (resource, _) => { m.Add(resource); return Task.FromResult(mint(resource)); },
        };
    }

    [Fact]
    public async Task ATokenIsMintedForTheAdvertisedAudienceAndKeptInMemory()
    {
        var session = Session(_ => "token-1", out var discovered, out var minted);
        Assert.Equal("token-1", await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
        Assert.Equal("token-1", await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
        Assert.Single(discovered);
        Assert.Equal([Audience], minted);
        Assert.Equal(Audience, session.SignedInAudience);

        session.Invalidate();
        Assert.Equal("token-1", await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
        Assert.Equal(2, minted.Count);
    }

    [Fact]
    public async Task AnAddressFromSettingsNeverReachesDiscoveryOrAz()
    {
        var session = Session(_ => "token", out var discovered, out var minted);
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Settings, Api, default));
        Assert.Empty(discovered);
        Assert.Empty(minted);
    }

    [Fact]
    public async Task WhenAzCannotMintTheStoredCredentialIsUsedAndAzIsNotAskedOnEveryRead()
    {
        var session = Session(_ => null, out _, out var minted);
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Policy, Api, default));
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Policy, Api, default));
        Assert.Single(minted);
    }

    [Fact]
    public async Task AnUnacceptableAdvertisedAudienceIsNeverMinted()
    {
        var session = Session(_ => "token", out _, out var minted, new AuthConfig(true, "https://graph.microsoft.com", []));
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
        Assert.Empty(minted);
    }

    [Fact]
    public async Task ADiscoveryFailureLeavesEntraOff()
    {
        var session = new EntraSession
        {
            Discover = (_, _) => throw new HttpRequestException("offline"),
            Mint = (_, _) => Task.FromResult<string?>("token"),
        };
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
    }

    [Fact]
    public async Task ADiscoveryTimeoutLeavesEntraOffInsteadOfFailingTheRead()
    {
        // HttpClient reports its own Timeout as a TaskCanceledException, which is an
        // OperationCanceledException the caller never asked for.
        var minted = 0;
        var session = new EntraSession
        {
            Discover = (_, _) => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 8 seconds elapsing."),
            Mint = (_, _) => { minted++; return Task.FromResult<string?>("token"); },
        };
        Assert.Null(await session.AudienceAsync(null, ApiUrlSource.Client, Api, default));
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
        Assert.Equal(0, minted);
    }

    [Fact]
    public async Task AMintTimeoutFallsBackToo()
    {
        var session = new EntraSession
        {
            Discover = (_, _) => Task.FromResult<AuthConfig?>(new AuthConfig(true, Audience, [])),
            Mint = (_, _) => throw new OperationCanceledException("az timed out"),
        };
        Assert.Null(await session.TokenAsync(null, ApiUrlSource.Client, Api, default));
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillPropagates()
    {
        using var cts = new CancellationTokenSource();
        var session = new EntraSession
        {
            Discover = (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<AuthConfig?>(null); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.AudienceAsync(null, ApiUrlSource.Client, Api, cts.Token));
    }

    [Fact]
    public void TheMissingSettingNamesAzLoginWhenTheApiSignsInWithEntra()
    {
        var text = FleetSetupHints.MissingSetting(hasApiUrl: true, hostSignIn: false, entraAudience: Audience);
        Assert.Contains("az login", text);
        Assert.Contains("Read passphrase", text);
    }
}
