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
        Assert.Equal("reportmate://system?pending=1-5%2C6-10%2C10%2B", Find(View("updateDebt"), "with pending updates").Link!.ToAppUrl());
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
        var donut = View("updateDebt").Parts.OfType<DonutPart>().Single();
        Assert.Equal("86%", donut.Centre);
        Assert.Equal([6.0, 1.0], donut.Slices.Select(s => s.Count));
        Assert.Equal("reportmate://system?patch=behind", donut.Slices[1].Link!.ToAppUrl());
        Assert.Equal("reportmate://system?osVersion=15.5", Find(View("updateDebt"), "macOS 15.5").Link!.ToAppUrl());
    }

    [Fact]
    public void UpdateDebtTakesThePendingAndDeferredCountsAndRebootDebtKeepsUptimeOnly()
    {
        var update = View("updateDebt");
        Assert.Equal("reportmate://system?deferred=1", Find(update, "with deferred updates").Link!.ToAppUrl());
        Assert.Contains(Find(update, "Windows 10"), update.Parts.OfType<ChipsPart>().Single().Items);
        var reboot = View("rebootDebt");
        Assert.DoesNotContain(reboot.Parts.OfType<ChipsPart>(), _ => true);
        Assert.Equal(["updateDebt", "osCurrency"], InsightCards.Endpoints("updateDebt"));

        // An OS Currency section from before the rename draws without the update counts.
        var old = InsightCards.Build("osCurrency", JsonDocument.Parse("""
            {"onLatest":5,"pctOnLatest":100,"minDevices":5,"behind":{"count":0},"unsupportedMac":{"count":0},"windows10":{"count":0}}
            """).RootElement);
        Assert.Null(old.Error);
        Assert.DoesNotContain(old.Parts.OfType<ChipsPart>().Single().Items, m => m.Label == "with pending updates");
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
        var view = View("managementToolLogs");
        var tools = view.Parts.OfType<BarsPart>().Single().Items;
        // The MDM agent's own logs are not this card's subject.
        Assert.Equal(["Cimian", "ReportMate"], tools.Select(t => t.Label));
        Assert.Equal("Windows", tools[0].Platform);
        Assert.Null(tools[1].Platform);
        Assert.Equal("reportmate://devices?level=error&platform=Windows&tool=installs", tools[0].Link!.ToAppUrl());
        // One message logged with and without its timestamp and level is one row.
        var top = view.Parts.OfType<ListPart>().Single().Items.Single();
        Assert.Equal("Failed to download <n> from <path>", top.Label);
        Assert.Equal(5, top.Count);
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
    public void AnEarlierDirectoryJoinCardShowsTheViewForTheToggle(string viewKey, string label, string link)
    {
        var view = InsightCards.Build("directoryServices", Card("directoryServices"), view: viewKey);
        var donut = view.Parts.OfType<DonutPart>().Single();
        Assert.Equal(link, donut.Slices.First(s => s.Label == label).Link!.ToAppUrl());
    }

    [Fact]
    public void DirectoryServicesUsesTheIdentityReportsClassesAndTrust()
    {
        var card = JsonDocument.Parse("""
            {"devices":10,
             "segments":[
               {"key":"Cloud Joined","label":"Cloud Joined","count":4,"link":{"path":"/identity","params":{"directory":"Cloud Joined"}}},
               {"key":"Domain Joined","label":"Domain Joined","count":3,"link":{"path":"/identity","params":{"directory":"Domain Joined"}},
                "breakdown":[
                  {"key":"Trusted","label":"Trusted","count":1,"link":{"path":"/identity","params":{"directory":"Trusted"}}},
                  {"key":"Unconfirmed","label":"Unconfirmed","count":1,"link":{"path":"/identity","params":{"directory":"Unconfirmed"}}},
                  {"key":"Broken","label":"Broken","count":1,"link":{"path":"/identity","params":{"directory":"Broken"}}}]},
               {"key":"Standard","label":"Standard","count":2,"link":{"path":"/identity","params":{"directory":"Standard"}}},
               {"key":"Unjoined","label":"Unjoined","count":1,"link":{"path":"/identity","params":{"directory":"Unjoined"}}}],
             "extraAdmins":{"count":2,"link":{"path":"/identity","params":{"admins":"extra"}}},
             "shared":{"count":1,"link":{"path":"/identity","params":{"usage":"shared"}}},
             "assigned":{"count":3,"link":{"path":"/identity","params":{"usage":"assigned"}}}}
            """).RootElement;
        var view = InsightCards.Build("directoryServices", card);
        var donut = view.Parts.OfType<DonutPart>().Single();
        Assert.Equal(["Cloud Joined", "Domain Joined", "Standard", "Unjoined"], donut.Slices.Select(s => s.Label));
        var trust = view.Parts.OfType<BarsPart>().Single();
        Assert.Equal(["Trusted", "Unconfirmed", "Broken"], trust.Items.Select(t => t.Label));
        Assert.Equal("reportmate://identity?directory=Broken", trust.Items[2].Link!.ToAppUrl());
        Assert.Equal(Severity.Bad, trust.Items[2].Severity);
    }

    [Fact]
    public void DirectoryServicesCountsFromTotalAndUsesTheIdentityReportsColours()
    {
        var card = JsonDocument.Parse("""
            {"total":12,"segments":[
               {"key":"cloud_joined","label":"Cloud Joined","count":5},
               {"key":"domain_joined","label":"Domain Joined","count":3},
               {"key":"standard","label":"Standard","count":2},
               {"key":"unjoined","label":"Unjoined","count":2}]}
            """).RootElement;
        var donut = InsightCards.Build("directoryServices", card).Parts.OfType<DonutPart>().Single();
        Assert.Equal("12", donut.Centre);
        Assert.Equal(["#10b981", "#f59e0b", "#3b82f6", "#ef4444"], donut.Slices.Select(s => s.Swatch));
        Assert.Equal(["#eab308", "#f97316", "#ef4444"],
            new[] { "Trusted", "Unconfirmed", "Broken" }.Select(InsightCards.DirectorySwatch));
    }

    [Theory]
    [InlineData("<ts> -<n>:<n> [ERR] Device registration failed for <guid>", "Device registration failed for <guid>")]
    [InlineData("2026-10-22 14:03:11.123 +02:00 [ERROR] Download failed", "Download failed")]
    [InlineData("ERROR: Manifest not found", "Manifest not found")]
    [InlineData("Plain message with no prefix", "Plain message with no prefix")]
    public void ALogMessageLosesItsTimestampAndLevelBeforeGrouping(string raw, string expected) =>
        Assert.Equal(expected, InsightCards.NormalizeMessage(raw));

    [Fact]
    public void RenamedCardsAreAskedForByTheirOldNamesToo()
    {
        Assert.Equal(["managementToolLogs", "managementToolErrors"], InsightCards.Endpoints("managementToolLogs"));
        Assert.Equal(["directoryServices", "directoryJoin"], InsightCards.Endpoints("directoryServices"));
        Assert.Equal(["storage"], InsightCards.Endpoints("storage"));
        Assert.Null(InsightCards.Build("managementToolErrors", Card("managementToolLogs")).Error);
    }

    [Theory]
    [InlineData(950.4, "950 h")]
    [InlineData(1200, "1.2k h")]
    [InlineData(3000, "3k h")]
    [InlineData(12_400, "12k h")]
    [InlineData(328_025, "328k h")]
    [InlineData(1_240_000, "1.2M h")]
    public void HoursAreCompactedAsTheWebDoes(double hours, string expected) =>
        Assert.Equal(expected, InsightCards.CompactHours(hours));

    [Fact]
    public void AppUsageListsTheTopAppsAndItsFilters()
    {
        var view = InsightCards.Build("appUsage", AppUsage);
        Assert.True(view.Parts.OfType<BarsPart>().Single().Tabular);
        var apps = view.Parts.OfType<BarsPart>().Single().Items;
        Assert.Equal("40", apps[0].Detail);
        Assert.Equal("901 h", apps[0].Shown);
        Assert.Contains("Adobe Photoshop 2026", apps[0].Tip);
        Assert.Equal(["App", "Hours", "Devices"], view.Parts.OfType<BarsPart>().Single().Columns);
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
