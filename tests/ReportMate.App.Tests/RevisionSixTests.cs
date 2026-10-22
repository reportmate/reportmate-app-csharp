using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>Revision 6: the User Accounts card, the Tools Logs report, CSV export and the identity report's new filters.</summary>
public class RevisionSixTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // The shape API #163 serves.
    private const string Fleet = """
        {"hours":24,"platform":"all","filters":{"tool":null,"level":null,"message":null},"generatedAt":"2026-10-22T18:00:00Z","computeSeconds":0.4,
         "tools":[
           {"tool":"installs","name":"Cimian","platform":"Windows","devices":5,"devicesWithErrors":3,"devicesWithWarnings":1,"errorLines":40,"warningLines":5,
            "lastAt":"2026-10-22T17:00:00Z","trend":[{"hour":"2026-10-22T00","errors":1,"warnings":0},{"hour":"2026-10-22T12","errors":8,"warnings":2}],
            "link":{"path":"/management/logs","params":{"hours":"24","tool":"installs","level":"error","platform":"Windows"}}},
           {"tool":"mdm","name":"Intune","devicesWithErrors":9,"errorLines":900},
           {"tool":"reports","name":"ReportMate","devicesWithErrors":1,"errorLines":3}],
         "messages":[
           {"id":"a1b2c3d4e5f6","message":"<ts> -<n>:<n> [ERR] Failed to download <n>","tool":"installs","name":"Cimian","devices":2,"lines":20,"lastAt":"2026-10-22T16:00:00Z"},
           {"id":"a1b2c3d4e5f6","message":"Failed to download <n>","tool":"installs","name":"Cimian","devices":1,"lines":5},
           {"id":"ffffffffffff","message":"[ERR] Device registration failed for <guid>","tool":"mdm","name":"Intune","devices":9,"lines":90}],
         "devices":[
           {"serialNumber":"SAMPLE1","deviceName":"Sample Desktop","platform":"Windows","tool":"installs","name":"Cimian","errors":4,"warnings":1,"lastAt":"2026-10-22T16:00:00Z"},
           {"serialNumber":"SAMPLE3","deviceName":"Sample Mac","tool":"mdm","name":"Intune","errors":9}],
         "devicesTruncated":true}
        """;

    [Fact]
    public void TheToolsLogsReportLeavesOutTheMdmAgentAndGroupsMessages()
    {
        var r = ManagementLogsReport.Read(Json(Fleet));
        Assert.Equal(["Cimian", "ReportMate"], r.Tools.Select(t => t.Name));
        var message = Assert.Single(r.Messages);
        Assert.Equal("Failed to download <n>", message.Text);
        Assert.Equal(3, message.Devices);
        Assert.Equal("a1b2c3d4e5f6", message.Id);
        var device = Assert.Single(r.Devices);
        Assert.Equal(("SAMPLE1", "Sample Desktop", "Cimian"), (device.Serial, device.Name, device.ToolName));
        Assert.Equal(new DateTime(2026, 10, 22, 17, 0, 0, DateTimeKind.Utc), r.Tools[0].LastSeen);
        Assert.Equal(24, r.WindowHours);
        Assert.True(r.DevicesTruncated);
        Assert.Equal("▂█", ManagementLogsReport.Sparkline(r.Tools[0].Trend));
    }

    [Fact]
    public void TheReportsFiltersRoundTripThroughItsLinkAndQuery()
    {
        var f = new ManagementLogsReport.Filters("installs", "errors", 168, "abc123", "Windows");
        Assert.Equal("/api/v1/management/logs/fleet?hours=168&tool=installs&level=error&message=abc123&platform=Windows", f.Path());
        var link = f.Link();
        Assert.Equal(("management", "logs"), (link.Section, link.Argument));
        var back = ManagementLogsReport.Filters.From(k => link[k]);
        Assert.Equal(f with { Level = "error" }, back);
        Assert.Equal(24, new ManagementLogsReport.Filters(Hours: 3).NormalHours);
    }

    [Fact]
    public void TheToolsLogsCardLinksIntoTheReport()
    {
        Assert.Equal("reportmate://management/logs?level=error&platform=Windows&tool=installs",
            InsightCards.ToolsLogsLink("installs", "error", "Windows").ToAppUrl());
        var card = Json("""
            {"windowHours":24,"tools":[{"tool":"installs","name":"Cimian","platform":"Windows","devicesWithErrors":3}],
             "topErrors":[{"message":"Failed to download <n>","tool":"installs","name":"Cimian","devices":3}]}
            """);
        var view = InsightCards.Build("managementToolLogs", card);
        var tool = view.Parts.OfType<BarsPart>().Single().Items.Single();
        Assert.Equal("logs", tool.Link!.Argument);
        var message = view.Parts.OfType<ListPart>().Single().Items.Single();
        Assert.Equal(InsightCards.MessageId("Failed to download <n>"), message.Link!["message"]);
    }

    [Fact]
    public void TheCardTakesTheServersReportLinksWhenItSendsThem()
    {
        var card = Json("""
            {"windowHours":24,"tools":[{"tool":"installs","name":"Cimian","devicesWithErrors":3,
               "link":{"path":"/management/logs","params":{"hours":"24","tool":"installs","level":"error"}}}],
             "topErrors":[{"message":"Failed","tool":"installs","name":"Cimian","devices":3,
               "link":{"path":"/management/logs","params":{"hours":"24","message":"a1b2c3d4e5f6","tool":"installs"}}}]}
            """);
        var view = InsightCards.Build("managementToolLogs", card);
        Assert.Equal("24", view.Parts.OfType<BarsPart>().Single().Items.Single().Link!["hours"]);
        Assert.Equal("a1b2c3d4e5f6", view.Parts.OfType<ListPart>().Single().Items.Single().Link!["message"]);
    }

    [Fact]
    public void ABreakdownHidesItsZeroRowsButFixedBucketsKeepThem()
    {
        var accounts = InsightCards.Build("userAccounts", Json("""
            {"total":6,"authentication":[{"key":"modern","label":"Modern","count":6},{"key":"legacy","label":"Legacy","count":0}],
             "adminAccounts":[{"name":"admin","devices":6},{"name":"unused","devices":0}]}
            """));
        Assert.Equal(["Modern"], accounts.Parts.OfType<DonutPart>().Single().Slices.Select(s => s.Label));
        Assert.Equal(["admin"], accounts.Parts.OfType<BarsPart>().Single().Items.Select(i => i.Label));

        var directory = InsightCards.Build("directoryServices", Json("""
            {"total":3,"segments":[{"key":"Domain Joined","label":"Domain Joined","count":3,
               "breakdown":[{"label":"Trusted","count":3},{"label":"Unconfirmed","count":0},{"label":"Broken","count":0}]},
               {"key":"Unjoined","label":"Unjoined","count":0}]}
            """));
        var joined = directory.Parts.OfType<DonutPart>().Single().Slices.Single();
        Assert.Equal("Domain Joined", joined.Label);
        Assert.Equal(["Trusted"], joined.Children!.Select(i => i.Label));
        Assert.Empty(directory.Parts.OfType<BarsPart>());

        var gaps = InsightCards.Build("protectionGaps", Json("""
            {"devices":4,"gaps":[{"key":"firewall","label":"Firewall off","count":1},{"key":"secureBoot","label":"Secure Boot off","count":0}]}
            """));
        Assert.Equal(2, gaps.Parts.OfType<BarsPart>().Single().Items.Count);

        var uptime = InsightCards.Build("rebootDebt", Json("""
            {"uptimeBuckets":[{"key":"lt1d","label":"< 1 day","count":0},{"key":"30d+","label":"30+ days","count":2}]}
            """));
        Assert.Equal(2, uptime.Parts.OfType<BarsPart>().Single().Items.Count);
    }

    [Fact]
    public void UserAccountsReadsTheShapeTheApiServes()
    {
        var view = InsightCards.Build("userAccounts", Json("""
            {"generatedAt":"2026-10-22T18:00:00Z","total":12,
             "authentication":[
               {"key":"modern","label":"Modern","count":6,"link":{"path":"/identity","params":{"auth":"modern"}}},
               {"key":"legacy","label":"Legacy","count":2,"link":{"path":"/identity","params":{"auth":"legacy"}}},
               {"key":"standard","label":"Standard","count":4,"link":{"path":"/identity","params":{"auth":"standard"}}}],
             "adminAccounts":[{"name":"admin","devices":10,"link":{"path":"/identity","params":{"admin":"admin"}}}],
             "extraAdmins":{"count":2,"link":{"path":"/identity","params":{"admins":"extra"}}}}
            """));
        var donut = view.Parts.OfType<DonutPart>().Single();
        Assert.Equal("12", donut.Centre);
        Assert.Equal(["Modern", "Legacy", "Standard"], donut.Slices.Select(x => x.Label));
        Assert.Equal("reportmate://identity?auth=legacy", donut.Slices[1].Link!.ToAppUrl());
        Assert.Equal("reportmate://identity?admin=admin", view.Parts.OfType<BarsPart>().Single().Items.Single().Link!.ToAppUrl());
    }

    [Fact]
    public void UserAccountsDrawsAuthenticationAdminsAndExtraAdmins()
    {
        var view = InsightCards.Build("userAccounts", Json("""
            {"authentication":{"modern":{"count":6},"standard":{"count":4}},
             "adminAccounts":[{"name":"admin","devices":10},{"name":"macadmins","devices":4}],
             "extraAdmins":{"count":2,"link":{"path":"/identity","params":{"admins":"extra"}}}}
            """));
        var donut = view.Parts.OfType<DonutPart>().Single();
        Assert.Equal("reportmate://identity?auth=modern", donut.Slices[0].Link!.ToAppUrl());
        var admins = view.Parts.OfType<BarsPart>().Single().Items;
        Assert.Equal("reportmate://identity?admin=macadmins", admins[1].Link!.ToAppUrl());
        Assert.Equal("reportmate://identity?admins=extra", view.Parts.OfType<ChipsPart>().Single().Items.Single().Link!.ToAppUrl());
    }

    [Fact]
    public void DirectoryServicesNoLongerCarriesTheExtraAdmins()
    {
        var view = InsightCards.Build("directoryServices", Json("""
            {"total":3,"segments":[],"extraAdmins":{"count":2},"shared":{"count":1},"assigned":{"count":2}}
            """));
        Assert.DoesNotContain(view.Parts.OfType<ChipsPart>().Single().Items, m => m.Label.Contains("admin"));
    }

    [Fact]
    public void TheIdentityReportFiltersByAuthenticationAndAdminAccount()
    {
        var rows = JsonDocument.Parse("""
            [{"serialNumber":"SAMPLE1","platformSSOUsers":{"deviceRegistered":true},"adminUsernames":["admin"]},
             {"serialNumber":"SAMPLE2","windowsHello":{"statusDisplay":"Enabled"},"adminUsernames":["Admin","winadmins"]},
             {"serialNumber":"SAMPLE3","windowsHello":{"statusDisplay":"Disabled by policy"},"directoryServices":{"activeDirectory":{"bound":true}}},
             {"serialNumber":"SAMPLE4"}]
            """).RootElement.EnumerateArray().ToList();
        List<string> Serials(string key, string value) =>
            ReportFilters.Apply("identity", key, value, rows, rows)!.Select(r => r.GetProperty("serialNumber").GetString()!).ToList();
        Assert.Equal(["SAMPLE1", "SAMPLE2"], Serials("auth", "modern"));
        Assert.Equal(["SAMPLE3"], Serials("auth", "legacy"));
        Assert.Equal(["SAMPLE4"], Serials("auth", "standard"));
        Assert.Equal(["SAMPLE1", "SAMPLE2"], Serials("admin", "admin"));
        Assert.Equal(["SAMPLE2"], Serials("admin", "winadmins"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("=cmd|' /C calc'!A0", "\"'=cmd|' /C calc'!A0\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-5", "\"'-5\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tTabbed", "\"'\tTabbed\"")]
    [InlineData("\rReturn", "\"'\rReturn\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("a=b", "a=b")]
    public void CsvCellsAreQuotedAndFormulasDefused(string? value, string expected) =>
        Assert.Equal(expected, Csv.Field(value));

    [Fact]
    public void ACsvDocumentDefusesEveryCell() =>
        Assert.Equal("Name,Value\r\n\"'=1+1\",\"'@x\"\r\n", Csv.Document(["Name", "Value"], [["=1+1", "@x"]]));

    [Fact]
    public void TheUpdateDebtLatestSliceLinksToTheLatestDevices()
    {
        var view = InsightCards.Build("updateDebt", Json("""{"onLatest":4,"pctOnLatest":80,"behind":{"count":1},"unsupportedMac":{"count":0},"windows10":{"count":0}}"""));
        Assert.Equal("reportmate://system?patch=latest", view.Parts.OfType<DonutPart>().Single().Slices[0].Link!.ToAppUrl());
    }
}
