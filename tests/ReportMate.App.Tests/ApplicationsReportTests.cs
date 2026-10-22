using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The Applications report's Versions, Usage and Missing modes: the link state they
/// share with the web and Mac apps, the queries they send, and the arithmetic behind
/// what each one shows.
/// </summary>
public class ApplicationsReportTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ApplicationsReport.State State(string link) =>
        ApplicationsReport.State.From(k => DeepLink.Parse(link)![k]);

    // ── Link state ─────────────────────────────────────────────────────

    [Fact]
    public void ReadsEveryParameterTheWebWrites()
    {
        var s = State("reportmate://applications?type=usage&period=90&apps=Sample%20App,Other%20App&versions=Sample:1.0"
            + "&publishers=Sample%20Co&usages=Shared&catalogs=Staff&locations=North&rooms=R101&areas=Design&fleets=Lab"
            + "&platform=macOS&q=draw");

        Assert.True(s.IsUsage);
        Assert.Equal(90, s.Period);
        Assert.Equal(["Sample App", "Other App"], s.Apps);
        Assert.Equal(["Sample:1.0"], s.Versions);
        Assert.Equal(["Sample Co"], s.Publishers);
        Assert.Equal(["Shared"], s.Usages);
        Assert.Equal(["Staff"], s.Catalogs);
        Assert.Equal(["North"], s.Locations);
        Assert.Equal(["R101"], s.Rooms);
        Assert.Equal(["Design"], s.Areas);
        Assert.Equal(["Lab"], s.Fleets);
        Assert.Equal("macOS", s.Platform);
        Assert.Equal("draw", s.Query);
    }

    [Fact]
    public void ALinkWithoutATypeOpensVersions_AsTheReportAlwaysHas()
    {
        var s = State("reportmate://applications?apps=Sample%20App");
        Assert.False(s.IsUsage);
        Assert.False(s.IsMissing);
        Assert.Equal(30, s.Period);
    }

    [Fact]
    public void MissingIsAVersionsMode_AUsageLinkIgnoresIt()
    {
        Assert.True(State("reportmate://applications?type=versions&mode=missing&apps=X").IsMissing);
        Assert.False(State("reportmate://applications?type=usage&mode=missing&apps=X").IsMissing);
    }

    [Fact]
    public void LegacySingleValueNamesStillNarrow()
    {
        var s = State("reportmate://applications?application=Sample%20App&usage=shared&catalog=staff&room=R1&search=x");
        Assert.Equal(["Sample App"], s.Apps);
        Assert.Equal(["shared"], s.Usages);
        Assert.Equal(["staff"], s.Catalogs);
        Assert.Equal(["R1"], s.Rooms);
        Assert.Equal("x", s.Query);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("9999", 548)]
    [InlineData("soon", 30)]
    public void APeriodIsHeldInsideWhatTheApiAccepts(string period, int expected) =>
        Assert.Equal(expected, State($"reportmate://applications?type=usage&period={period}").Period);

    [Fact]
    public void TheLinkRoundTrips()
    {
        var link = "reportmate://applications?apps=Sample%20App&catalogs=Staff&mode=missing&type=versions";
        Assert.Equal(link, State(link).Link().ToAppUrl());

        var usage = State("reportmate://applications?type=usage&period=7&usages=Shared");
        Assert.Equal("reportmate://applications?period=7&type=usage&usages=Shared", usage.Link().ToAppUrl());
    }

    [Fact]
    public void TheDashboardsAppUsageHeaderLinkOpensUsageWithItsScope()
    {
        // The link InsightCards.HeaderLink serves for the App Usage card.
        var s = State("reportmate://applications?period=30&type=usage&usages=Shared");
        Assert.True(s.IsUsage);
        Assert.Equal(30, s.Period);
        Assert.Equal("/api/v1/applications/usage?days=30&usages=Shared&platforms=windows", s.UsagePath("windows"));
    }

    [Fact]
    public void TogglingAnAppAddsOrRemovesIt()
    {
        var s = State("reportmate://applications?apps=Sample%20App");
        Assert.Equal(["Sample App", "Other"], s.ToggleApp("Other").Apps);
        Assert.Empty(s.ToggleApp("sample app").Apps);
    }

    // ── Queries ────────────────────────────────────────────────────────

    [Fact]
    public void UsageQuerySendsTheScopeToTheServer()
    {
        var s = State("reportmate://applications?type=usage&period=45&apps=Sample%20App,Other&catalogs=Staff&rooms=R1&areas=Design&fleets=Lab&locations=North");
        Assert.Equal("/api/v1/applications/usage?days=45&applicationNames=Sample%20App%2COther&catalogs=Staff"
            + "&locations=North&areas=Design&fleets=Lab&rooms=R1&platforms=macos", s.UsagePath("macos"));
    }

    [Fact]
    public void InventoryQueryLeavesVersionsToTheTable()
    {
        var s = State("reportmate://applications?apps=Sample%20App&versions=1.0&publishers=Sample%20Co");
        Assert.Equal("/api/v1/applications?applicationNames=Sample%20App&publishers=Sample%20Co&limit=5000", s.InventoryPath(null));
        Assert.Equal("/api/v1/applications/distribution?applicationNames=Sample%20App", s.DistributionPath(null));
        Assert.Null(State("reportmate://applications").DistributionPath(null));
    }

    [Fact]
    public void TheDrillDownKeepsThePeriodAndScope()
    {
        var s = State("reportmate://applications?type=usage&period=90&usages=Shared&catalogs=Staff");
        Assert.Equal("reportmate://applications/usage/Sample%20App?catalogs=Staff&days=90&usages=Shared",
            s.UsageDrillLink("Sample App").ToAppUrl());
    }

    [Theory]
    [InlineData("macOS", "Windows", "macos")]
    [InlineData(null, "Windows", "windows")]
    [InlineData(null, "macOS", "macos")]
    [InlineData("all", null, null)]
    [InlineData(null, null, null)]
    public void ThePlatformIsNamedAsTheApiExpects(string? link, string? toolbar, string? expected) =>
        Assert.Equal(expected, ApplicationsReport.PlatformToken(link, toolbar));

    // ── Chip cloud ─────────────────────────────────────────────────────

    private const string Filters = """
        {"applicationNames":["Sample App","Other App","Draw Pad"],
         "windowsApplicationNames":["Sample App","Draw Pad"],
         "macApplicationNames":["Other App"],
         "devices":[
           {"serialNumber":"dev-a","name":"Studio A","usage":"Shared","catalog":"Staff","location":"North R101","department":"Design","fleet":"Lab"},
           {"serialNumber":"dev-b","name":"Studio B","usage":"Shared","catalog":"Staff","location":"North R102","department":"Design","fleet":"Lab"},
           {"serialNumber":"dev-c","name":"Studio C","usage":"Assigned","catalog":"Staff","location":"South","department":"Film","fleet":"Lab"},
           {"serialNumber":"","name":"no serial"}
         ]}
        """;

    [Fact]
    public void ChipsAreThePlatformsNames_NarrowedBySearch_WithTheChosenKept()
    {
        var options = ApplicationsReport.ReadOptions(Json(Filters));
        Assert.Equal(3, options.Devices.Count);
        Assert.Equal(["Draw Pad", "Sample App"], ApplicationsReport.ChipNames(options, "windows", null, []));
        Assert.Equal(["Draw Pad"], ApplicationsReport.ChipNames(options, "windows", "draw", []));
        Assert.Equal(["Draw Pad", "Other App"], ApplicationsReport.ChipNames(options, "windows", "draw", ["Other App"]));
        Assert.Equal(3, ApplicationsReport.ChipNames(options, null, "", []).Count);
    }

    // ── Versions and Missing ───────────────────────────────────────────

    private const string Inventory = """
        [
          {"name":"Sample App","version":"2.1","vendor":"Sample Co","deviceName":"Studio A","serialNumber":"dev-a","usage":"Shared","catalog":"Staff","location":"North R101","department":"Design","fleet":"Lab","platform":"Windows"},
          {"name":"Sample App","version":"10.0","vendor":"Sample Co","deviceName":"Studio C","serialNumber":"dev-c","usage":"Assigned","catalog":"Staff","location":"South","department":"Film","fleet":"Lab","platform":"Windows"},
          {"name":"KB123","version":"1","deviceName":"Studio A","serialNumber":"dev-a","platform":"Windows"},
          {"name":"Sample App","version":"2.1","deviceName":"Mac One","serialNumber":"dev-m","usage":"Shared","platform":"macOS"}
        ]
        """;

    [Fact]
    public void BaseFilterDropsJunkAndOtherPlatformsAndHonoursTheSelection()
    {
        var rows = ApplicationsReport.ReadInstalled(Json(Inventory));
        Assert.Equal(4, rows.Count);
        var all = ApplicationsReport.BaseFilter(rows, State("reportmate://applications"), "windows");
        Assert.Equal(["dev-a", "dev-c"], all.Select(r => r.Serial));
        var shared = ApplicationsReport.BaseFilter(rows, State("reportmate://applications?usages=shared"), null);
        Assert.Equal(["dev-a", "dev-m"], shared.Select(r => r.Serial));
    }

    [Fact]
    public void VersionFiltersMatchByNormalisedAppOrPlainVersion()
    {
        var rows = ApplicationsReport.ReadInstalled(Json(Inventory));
        Assert.Equal(["dev-c"], ApplicationsReport.WithVersions(rows, ["Sample:10.0"]).Select(r => r.Serial));
        Assert.Equal(["dev-a", "dev-m"], ApplicationsReport.WithVersions(rows, ["2.1"]).Select(r => r.Serial));
        Assert.Empty(ApplicationsReport.WithVersions(rows, ["Sample:Unknown"]));
    }

    [Fact]
    public void MissingIsTheSelectionLessEveryDeviceThatHasTheApp()
    {
        var options = ApplicationsReport.ReadOptions(Json(Filters));
        var rows = ApplicationsReport.ReadInstalled(Json(Inventory));
        var state = State("reportmate://applications?mode=missing&apps=Sample%20App&rooms=r10&areas=design");
        var missing = ApplicationsReport.MissingDevices(options.Devices, rows, state);
        Assert.Equal(["dev-b"], missing.Select(d => d.Serial));

        Assert.Empty(ApplicationsReport.MissingDevices(options.Devices, rows, State("reportmate://applications?mode=missing")));
        Assert.Equal(["Studio B", "dev-b", "North R102", "Staff", "Sample App"],
            ApplicationsReport.MissingCsvRow(missing[0], state.Apps));
    }

    [Fact]
    public void ServerDistributionWinsAndMergesUnderTheNormalisedName()
    {
        var server = ApplicationsReport.ReadDistribution(Json("""
            {"Sample App":{"totalDevices":5,"versions":{"2.1":3,"10.0":2}},
             "Sample App 2024":{"totalDevices":1,"versions":{"2.1":1}},
             "Broken":{"versions":[1,2]}}
            """));
        Assert.Equal(2, server.Count);

        var analysis = ApplicationsReport.VersionAnalysis(server, []);
        var card = Assert.Single(ApplicationsReport.VersionCards(analysis));
        Assert.Equal("Sample", card.App);
        Assert.Equal(6, card.Total);
        Assert.Equal([("10.0", 2), ("2.1", 4)], card.Versions);
    }

    [Fact]
    public void WithoutTheServerTheInstalledRowsAreFolded()
    {
        var rows = ApplicationsReport.ReadInstalled(Json(Inventory));
        var analysis = ApplicationsReport.VersionAnalysis(null, rows);
        Assert.Equal(2, analysis["Sample"]["2.1"]);
        Assert.Equal("v10.0 (1), v2.1 (2)", ApplicationsReport.VersionsSummary(analysis, "Sample App"));
    }

    [Fact]
    public void TheUsageResponsesDistributionShapeReadsToo()
    {
        var d = ApplicationsReport.ReadDistribution(Json("""
            {"Sample App":{"totalDevices":2,"versions":{"1.0":{"count":2,"devices":[]},"0.9":{"devices":[{},{}]}}}}
            """));
        Assert.Equal(2, d["Sample App"].Versions["1.0"]);
        Assert.Equal(2, d["Sample App"].Versions["0.9"]);
    }

    [Theory]
    [InlineData("10.2", "9.8", 1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("2.0", "2.0.1", -1)]
    [InlineData("1.0", "1.0", 0)]
    public void VersionsCompareNumerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ApplicationsReport.VersionComparer.Instance.Compare(a, b)));

    // ── Usage ──────────────────────────────────────────────────────────

    private const string Usage = """
        {"status":"ok",
         "applications":[
           {"name":"Sample App","totalSeconds":36000,"totalHours":10,"activeSeconds":7200,"activeHours":2,"foregroundHours":3.5,
            "launchCount":12,"deviceCount":9,"userCount":7,"activeDeviceCount":4,"activeUserCount":3,"installedDeviceCount":20,
            "lastUsed":"2026-10-01T12:00:00Z","devices":["dev-a","dev-b"],"isSingleUser":false},
           {"name":"Background Agent","totalSeconds":720000,"totalHours":200,"activeSeconds":0,"activeHours":0,
            "launchCount":3,"deviceCount":30,"userCount":30,"devices":["dev-a"]},
           {"name":"Quiet App","totalSeconds":0,"totalHours":0,"launchCount":0,"deviceCount":0,"userCount":0}
         ],
         "devicesAggregate":[{"serialNumber":"dev-a","deviceName":"Studio A","totalSeconds":3600,"launchCount":5,"department":"Design"}],
         "summary":{"totalAppsTracked":3,"totalActiveHours":2.4,"totalUsageHours":210,"totalLaunches":15,"uniqueUsers":31,"uniqueDevices":30,"noActiveUseAppCount":2}}
        """;

    [Fact]
    public void UsageReadsAppsSummaryAndDevices()
    {
        var r = ApplicationsReport.ReadUsage(Json(Usage));
        Assert.False(r.Unavailable);
        Assert.Equal(3, r.Apps.Count);
        Assert.Equal("3 apps · 30 devices · 2 active hours · 15 launches · 31 unique users · 2 with no active use", r.Headline);

        var device = Assert.Single(r.Devices);
        Assert.Equal(1, device.TotalHours);
        Assert.Equal("Design", device.Area);

        var sample = r.Apps[0];
        Assert.Equal(4, sample.ShownDevices);
        Assert.Equal(3, sample.ShownUsers);
        Assert.Equal("Popular", sample.Status);
        Assert.Equal(30, r.Apps[1].ShownDevices);
        Assert.Equal("No data", r.Apps[2].Status);
    }

    [Fact]
    public void ActiveTimeLeads_NotProcessTime()
    {
        var r = ApplicationsReport.ReadUsage(Json(Usage));
        Assert.Equal(["Sample App", "Background Agent", "Quiet App"], ApplicationsReport.ByActiveTime(r.Apps).Select(a => a.Name));
    }

    [Fact]
    public void AnUnavailableReportSaysWhy()
    {
        var r = ApplicationsReport.ReadUsage(Json("""{"status":"unavailable","message":"Usage tracking not yet deployed"}"""));
        Assert.True(r.Unavailable);
        Assert.Equal("Usage tracking not yet deployed", r.Message);
        Assert.Empty(r.Apps);
    }

    [Fact]
    public void UsageCsvMatchesTheWebsColumns()
    {
        var sample = ApplicationsReport.ReadUsage(Json(Usage)).Apps[0];
        Assert.Equal(11, ApplicationsReport.UsageCsvHeaders.Length);
        Assert.Equal(["Sample App", "2.0", "3.5", "10.0", "12", "4", "20", "3", "7", "2026-10-01T12:00:00.0000000Z", "No"],
            ApplicationsReport.UsageCsvRow(sample));
    }

    [Fact]
    public void CsvFileNamesFollowTheWeb()
    {
        var day = new DateTime(2026, 10, 22);
        Assert.Equal("applications-usage-90days-2026-10-22.csv",
            ApplicationsReport.CsvFileName(State("reportmate://applications?type=usage&period=90"), day));
        Assert.Equal("missing-Sample-App-2026-10-22.csv",
            ApplicationsReport.CsvFileName(State("reportmate://applications?mode=missing&apps=Sample%20App"), day));
        Assert.Equal("applications-2026-10-22.csv", ApplicationsReport.CsvFileName(State("reportmate://applications"), day));
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(2700, "45m")]
    [InlineData(10800, "3h")]
    [InlineData(12000, "3h 20m")]
    public void DurationsReadAsTheWebWritesThem(double seconds, string expected) =>
        Assert.Equal(expected, ApplicationsReport.Duration(seconds));

    // ── Names ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Google Chrome 120.0.1", "Google Chrome")]
    [InlineData("Adobe Photoshop 2025", "Adobe Photoshop")]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Sample Editor 4.2.1", "Sample Editor")]
    [InlineData("Draw Pad Pro", "Draw Pad")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Visual C++ Redistributable")]
    [InlineData("${{ placeholder }}", "")]
    [InlineData("Unknown", "")]
    public void NamesNormaliseAsTheWebs(string raw, string expected) =>
        Assert.Equal(expected, ApplicationsReport.Normalize(raw));

    [Theory]
    [InlineData("Sample App", true)]
    [InlineData("KB123", false)]
    [InlineData("Security Update for Microsoft Office", false)]
    [InlineData("Microsoft.NET.Sdk.Sample", false)]
    [InlineData("  ", false)]
    public void JunkRowsAreLeftOut(string name, bool included) =>
        Assert.Equal(included, ApplicationsReport.ShouldInclude(name));

    [Fact]
    public void AnAdversarialNameNormalisesQuickly()
    {
        var name = "a " + string.Concat(Enumerable.Repeat("1.", 5000)) + "x";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ApplicationsReport.Normalize(name);
        ApplicationsReport.ShouldInclude(name);
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds} ms");
    }
}
