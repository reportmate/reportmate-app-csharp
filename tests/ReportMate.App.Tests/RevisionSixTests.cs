using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>Revision 6: the User Accounts card, the Tools Logs report, CSV export and the identity report's new filters.</summary>
public class RevisionSixTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string Fleet = """
        {"windowHours":24,
         "tools":[
           {"tool":"installs","name":"Cimian","platform":"Windows","devicesWithErrors":3,"devicesWithWarnings":1,"errorLines":40,"warningLines":5,
            "lastSeen":"2026-10-22T17:00:00Z","trend":[{"label":"00","errors":1,"warnings":0},{"label":"12","errors":8,"warnings":2}]},
           {"tool":"mdm","name":"Intune","devicesWithErrors":9,"errorLines":900},
           {"tool":"reports","name":"ReportMate","devicesWithErrors":1,"errorLines":3}],
         "messages":[
           {"message":"<ts> -<n>:<n> [ERR] Failed to download <n>","tool":"installs","devices":2,"lines":20},
           {"message":"Failed to download <n>","tool":"installs","devices":1,"lines":5},
           {"message":"[ERR] Device registration failed for <guid>","tool":"mdm","devices":9,"lines":90}],
         "devices":[
           {"serialNumber":"SAMPLE1","deviceName":"Sample Desktop","platform":"Windows","tool":"installs","errors":4,"warnings":1,"lastAt":"2026-10-22T16:00:00Z"},
           {"serialNumber":"SAMPLE3","deviceName":"Sample Mac","tool":"mdm","errors":9}]}
        """;

    [Fact]
    public void TheToolsLogsReportLeavesOutTheMdmAgentAndGroupsMessages()
    {
        var r = ManagementLogsReport.Read(Json(Fleet));
        Assert.Equal(["Cimian", "ReportMate"], r.Tools.Select(t => t.Name));
        var message = Assert.Single(r.Messages);
        Assert.Equal("Failed to download <n>", message.Text);
        Assert.Equal(3, message.Devices);
        Assert.Equal(InsightCards.MessageId("Failed to download <n>"), message.Id);
        Assert.Equal("SAMPLE1", Assert.Single(r.Devices).Serial);
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
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("-5", "'-5")]
    public void CsvFieldsAreQuotedAndFormulasDefused(string value, string expected) =>
        Assert.Equal(expected, Csv.Field(value));

    [Fact]
    public void TheUpdateDebtLatestSliceLinksToTheLatestDevices()
    {
        var view = InsightCards.Build("updateDebt", Json("""{"onLatest":4,"pctOnLatest":80,"behind":{"count":1},"unsupportedMac":{"count":0},"windows10":{"count":0}}"""));
        Assert.Equal("reportmate://system?patch=latest", view.Parts.OfType<DonutPart>().Single().Slices[0].Link!.ToAppUrl());
    }
}
