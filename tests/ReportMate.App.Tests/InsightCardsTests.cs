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
        Assert.Equal("reportmate://management?enrolled=0", Find(View("unmanagedDevices"), "not enrolled in MDM").Link!.ToAppUrl());
        Assert.Equal("reportmate://installs?filter=errors&item=Google%20Chrome", Find(View("installHealth"), "Google Chrome").Link!.ToAppUrl());
        Assert.Equal("reportmate://events/failures?hours=24&reason=invalid_passphrase",
            Find(View("checkinGaps"), "Wrong passphrase").Link!.ToAppUrl());
        var app = Find(View("unusedSoftware"), "Adobe Photoshop 2026").Link!;
        Assert.Equal(("applications", "usage/Adobe Photoshop 2026", "30"), (app.Section, app.Argument, app["days"]));
    }

    [Fact]
    public void ADeviceLinkOpensTheDeviceListNarrowedToTheSerial()
    {
        var link = Find(View("unmanagedDevices"), "Sample Laptop").Link!;
        Assert.Equal("reportmate://devices?search=SAMPLE1", link.ToAppUrl());
        Assert.Equal("macOS", Find(View("unmanagedDevices"), "Sample Laptop").Platform);
        Assert.Equal("reportmate://devices?search=SAMPLE2", Find(View("localAdminExposure"), "Sample Desktop").Link!.ToAppUrl());
    }

    [Fact]
    public void ConnectivityLinksCarryTheNetworkFiltersTheServerLeavesOff()
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

    [Fact]
    public void UnusedSoftwareListsShelfwareFirstWithItsUsageRate()
    {
        var bars = View("unusedSoftware").Parts.OfType<BarsPart>().ToList();
        Assert.StartsWith("LEAST USED", bars[0].Heading);
        Assert.Equal("2 of 40", bars[0].Items[0].Shown);
        Assert.Equal(0.05, bars[0].Items[0].Fraction);
        Assert.Equal("MOST USED", bars[1].Heading);
    }

    [Fact]
    public void HardwareAtRiskNamesIntelMacsAgainstAllMacs()
    {
        var intel = Find(View("hardwareAtRisk"), "Intel Macs");
        Assert.Equal(3, intel.Count);
        Assert.StartsWith("of 5 Macs", intel.Detail);
        Assert.Equal("reportmate://hardware?arch=Intel", intel.Link!.ToAppUrl());
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
