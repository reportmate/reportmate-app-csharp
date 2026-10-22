using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The insight cards read from <c>GET /api/v1/dashboard/insights</c>, against a
/// fixture shaped from the API's own card tests (reportmate-api PR #158).
/// </summary>
public class InsightCardsTests
{
    private static readonly JsonElement Document = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dashboard-insights.json"))).RootElement;

    private static JsonElement Card(string key) => Document.GetProperty("cards").GetProperty(key);

    private static CardView View(string key, bool scoped = false) => InsightCards.Build(key, Card(key), scoped);

    private static IEnumerable<Metric> Metrics(CardView view) => view.Parts.SelectMany(p => p switch
    {
        FiguresPart f => f.Items,
        BarsPart b => b.Items,
        ChipsPart c => c.Items,
        DonutPart d => d.Slices,
        ListPart l => l.Items,
        _ => [],
    });

    private static Metric Find(CardView view, string label) => Metrics(view).First(m => m.Label == label);

    [Fact]
    public void EveryCardInTheDocumentIsOneTheAppKnows()
    {
        var keys = Document.GetProperty("cards").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(InsightCards.Keys, keys);
        foreach (var key in InsightCards.Keys)
            Assert.DoesNotContain("does not know", View(key).Error ?? "");
    }

    [Fact]
    public void AFailedCardShowsItsErrorAndTheOthersStillRender()
    {
        Assert.Equal("canceling statement due to statement timeout", View("expiringCertificates").Error);
        Assert.Null(View("rebootDebt").Error);
        Assert.NotNull(InsightCards.Build("rebootDebt", null).Error);
    }

    [Fact]
    public void ServerLinksBecomeAppLinks()
    {
        Assert.Equal("reportmate://system?uptime=30d%2B", Find(View("rebootDebt"), "up 30+ days").Link!.ToAppUrl());
        Assert.Equal("reportmate://system?pending=1-5%2C6-10%2C10%2B", Find(View("rebootDebt"), "with pending updates").Link!.ToAppUrl());
        Assert.Equal("reportmate://installs?filter=errors&item=Google%20Chrome", Find(View("installHealth"), "Google Chrome").Link!.ToAppUrl());
        Assert.Equal("reportmate://events/failures?hours=24&reason=invalid_passphrase",
            Find(View("checkinGaps"), "Wrong passphrase").Link!.ToAppUrl());
    }

    [Fact]
    public void ConnectivityLinksCarryTheNetworkFilters()
    {
        var view = View("connectivityProblems");
        Assert.Equal("reportmate://network?signal=weak", Find(view, "on a weak Wi-Fi signal").Link!.ToAppUrl());
        Assert.Equal("reportmate://network?quality=poor", Find(view, "with a poor network rating").Link!.ToAppUrl());
        Assert.Equal("reportmate://network?vpn=1", Find(view, "on VPN").Link!.ToAppUrl());
    }

    [Fact]
    public void ProtectionGapsKeepTheServersWorstFirstOrderAndShowTheShare()
    {
        var gaps = View("protectionGaps").Parts.OfType<BarsPart>().Single().Items;
        Assert.Equal(["Firewall off", "Disk encryption off", "Secure Boot off"], gaps.Select(g => g.Label));
        Assert.Equal("6 of 10", gaps[0].Shown);
        Assert.Equal(0.6, gaps[0].Fraction);
        Assert.Equal(Severity.Neutral, gaps[2].Severity);
    }

    [Fact]
    public void OsCurrencyDrawsADonutFromTheServersShare()
    {
        var donut = View("osCurrency").Parts.OfType<DonutPart>().Single();
        Assert.Equal("86%", donut.Centre);
        Assert.Equal([6.0, 1.0], donut.Slices.Select(s => s.Count));
        Assert.Equal("reportmate://system?patch=behind", donut.Slices[1].Link!.ToAppUrl());
        Assert.Equal("reportmate://system?osVersion=15.5", Find(View("osCurrency"), "macOS 15.5").Link!.ToAppUrl());
    }

    [Fact]
    public void CheckinsSayAllPlatformsOnlyWhenTheDashboardIsScoped()
    {
        Assert.DoesNotContain(View("checkinGaps").Parts.OfType<NotePart>(), n => n.Text.Contains("All platforms"));
        Assert.Contains(View("checkinGaps", scoped: true).Parts.OfType<NotePart>(), n => n.Text.Contains("All platforms"));
    }

    [Theory]
    [InlineData("""{"devices":9,"lowStorage":{"count":1}}""")]
    [InlineData("""{"hardwareAtRisk":{"devices":9,"lowStorage":{"count":1}}}""")]
    [InlineData("""{"generatedAt":"2026-10-22T18:00:00Z","cards":{"hardwareAtRisk":{"devices":9,"lowStorage":{"count":1}}}}""")]
    [InlineData("""{"card":"hardwareAtRisk","platform":"all","includeArchived":false,"generatedAt":"2026-10-22T18:00:00Z","data":{"devices":9,"lowStorage":{"count":1}}}""")]
    public void APerCardResponseIsReadInAnyOfItsShapes(string json)
    {
        var section = InsightCards.SectionOf(JsonDocument.Parse(json).RootElement, "hardwareAtRisk");
        Assert.Equal(9, section.GetProperty("devices").GetInt32());
    }

    [Fact]
    public void A404SaysTheEndpointIsNotThereYet()
    {
        Assert.Contains("404", InsightCards.FailureText(404, "The API returned 404."));
        Assert.Equal("The API returned 502.", InsightCards.FailureText(502, "The API returned 502."));
    }

    private static JsonElement AppUsage => InsightCards.SectionOf(JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "insights-appUsage.json"))).RootElement, "appUsage");

    [Fact]
    public void InstallHealthSplitsByAgentAndCountsItemsPendingAndSilent()
    {
        var view = View("installHealth");
        var figures = view.Parts.OfType<FiguresPart>().First().Items;
        Assert.Equal(["devices with errors", "devices with warnings"], figures.Select(f => f.Label));
        var agents = view.Parts.OfType<ListPart>().Single(l => l.Heading == "BY AGENT").Items;
        Assert.Equal(["Munki", "Cimian"], agents.Select(a => a.Label));
        Assert.Equal("macOS", agents[0].Platform);
        Assert.Equal("reportmate://installs?agent=munki&filter=errors", agents[0].Link!.ToAppUrl());
        Assert.Equal("reportmate://installs?filter=pending", Find(view, "pending on 3 devices").Link!.ToAppUrl());
        Assert.Equal(7, Find(view, "pending on 3 devices").Count);
        Assert.Equal(1, Find(view, "agents silent over 7 days").Count);
    }

    [Fact]
    public void ManagementToolsListToolsByDevicesWithErrorsAndTheTopMessages()
    {
        var view = View("managementToolErrors");
        var tools = view.Parts.OfType<BarsPart>().Single().Items;
        Assert.Equal(["Cimian", "ReportMate"], tools.Select(t => t.Label));
        Assert.Equal("Windows", tools[0].Platform);
        Assert.Null(tools[1].Platform);
        Assert.Equal("reportmate://devices?level=error&platform=Windows&tool=installs", tools[0].Link!.ToAppUrl());
        var top = view.Parts.OfType<ListPart>().Single().Items.Single();
        Assert.StartsWith("Failed to download", top.Label);
    }

    [Fact]
    public void StorageShowsBucketsCapacityAndTheFullest()
    {
        var view = View("storage");
        Assert.Equal(3, Find(view, "devices under 10% free").Count);
        Assert.Equal("reportmate://hardware?storage=lt5", Find(view, "Under 5% free").Link!.ToAppUrl());
        Assert.Equal("reportmate://hardware?capacity=le256", Find(view, "256 GB or less").Link!.ToAppUrl());
        Assert.Equal("reportmate://hardware?platform=macOS&storage=low", Find(view, "macOS").Link!.ToAppUrl());
        var fullest = Find(view, "Sample Laptop");
        Assert.Equal("reportmate://devices?search=SAMPLE1", fullest.Link!.ToAppUrl());
        Assert.StartsWith("3.2% free", fullest.Detail);
    }

    [Theory]
    [InlineData("all", "Entra joined", "reportmate://identity?join=entra")]
    [InlineData("windows", "Workgroup", "reportmate://identity?join=workgroup&platform=Windows")]
    [InlineData("mac", "Platform SSO / Entra", "reportmate://identity?directory=psso&platform=macOS")]
    public void DirectoryJoinShowsTheChosenView(string viewKey, string label, string link)
    {
        var view = InsightCards.Build("directoryJoin", Card("directoryJoin"), view: viewKey);
        var donut = view.Parts.OfType<DonutPart>().Single();
        Assert.Equal(link, donut.Slices.First(s => s.Label == label).Link!.ToAppUrl());
    }

    [Fact]
    public void AppUsageListsTheTopAppsAndItsFilters()
    {
        var view = InsightCards.Build("appUsage", AppUsage);
        var apps = view.Parts.OfType<BarsPart>().Single().Items;
        Assert.Equal(["Adobe Photoshop 2026", "Word"], apps.Select(a => a.Label));
        var link = apps[0].Link!;
        Assert.Equal(("applications", "usage/Adobe Photoshop 2026", "Shared"), (link.Section, link.Argument, link["usages"]));
        var (usages, catalogs) = InsightCards.AppUsageFilters(AppUsage);
        Assert.Equal(["Shared", "Assigned"], usages.Select(u => u.Value));
        Assert.Equal(("Production", 90), catalogs.Single());
        Assert.Equal("reportmate://applications?period=30&type=usage&usages=Shared", InsightCards.HeaderLink(AppUsage)!.ToAppUrl());
    }

    [Fact]
    public void TheNetworkReportHonoursPoorQualityAndVpn()
    {
        var rows = JsonDocument.Parse("""
            [{"serialNumber":"SAMPLE1","raw":{"networkQuality":{"rating":"Low"},"activeConnection":{"isVpnActive":true}}},
             {"serialNumber":"SAMPLE2","raw":{"networkQuality":{"rating":"","dlRating":"low"},"activeConnection":{"interfaceName":"utun3"}}},
             {"serialNumber":"SAMPLE3","raw":{"networkQuality":{"rating":"High"},"vpnConnections":[{"name":"Corp VPN","status":"Connected"}]}},
             {"serialNumber":"SAMPLE4","raw":{"networkQuality":{"rating":"Medium"},"vpnConnections":[{"name":"Unknown VPN","status":"Connected"}]}}]
            """).RootElement.EnumerateArray().ToList();
        List<string> Serials(string key, string value) =>
            ReportFilters.Apply("network", key, value, rows, rows)!.Select(r => r.GetProperty("serialNumber").GetString()!).ToList();

        Assert.Equal(["SAMPLE1", "SAMPLE2"], Serials("quality", "poor"));
        Assert.Equal(["SAMPLE1", "SAMPLE2", "SAMPLE3"], Serials("vpn", "1"));
    }
}
