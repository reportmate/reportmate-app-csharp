using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

public class TabReportsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TabRow Row(string json, TabReportData? data = null) => new(J(json), data);

    private static TabReportData Data(string json, string rows = "rows") =>
        new(J(json), J(json).TryGetProperty(rows, out var r) ? r.EnumerateArray() : []);

    private static TabQuery Q(params (string, string?)[] pairs) => new TabQuery().With(pairs);

    public static IEnumerable<object[]> AllReports() =>
    [
        [TabReports.Services], [TabReports.Updates], [TabReports.Background], [TabReports.Extensions],
        [TabReports.Profiles], [TabReports.Preferences], [TabReports.VulnerabilitySummary], [TabReports.VulnerabilityRows],
        [TabReports.Threats], [TabReports.Posture],
    ];

    // ── The link state ───────────────────────────────────────────────────

    [Fact]
    public void Lists_read_json_arrays_and_comma_lists()
    {
        var q = Q(("serviceNames", "[\"Spooler\",\"a, b\"]"), ("states", "running, stopped"));
        Assert.Equal(["Spooler", "a, b"], q.List("serviceNames"));
        Assert.Equal(["running", "stopped"], q.List("states"));
    }

    [Fact]
    public void An_unreadable_json_list_is_no_selection()
    {
        Assert.Empty(Q(("items", "[not json")).List("items"));
        Assert.Empty(Q(("items", "[1,2]")).List("items"));
    }

    [Fact]
    public void Toggling_adds_and_removes_a_value()
    {
        var q = new TabQuery().Toggle("states", "running").Toggle("states", "stopped").Toggle("states", "running");
        Assert.Equal("stopped", q["states"]);
        Assert.Null(q.Toggle("states", "stopped")["states"]);
    }

    [Fact]
    public void A_view_links_with_the_web_route_and_its_query()
    {
        var link = Q(("serviceNames", "[\"Spooler\"]")).Link("system", "services");
        Assert.Equal("reportmate://system?tab=services&serviceNames=%5B%22Spooler%22%5D", link.ToAppUrl());
        Assert.Equal("https://host.example.org/system?tab=services&serviceNames=%5B%22Spooler%22%5D", link.ToWebUrl("https://host.example.org"));
    }

    [Fact]
    public void A_device_link_still_carries_its_tab_in_the_fragment()
    {
        Assert.Equal("https://host.example.org/device/TESTSERIAL01#security",
            DeepLink.For("device", "TESTSERIAL01", ("tab", "security")).ToWebUrl("https://host.example.org"));
    }

    [Theory]
    [InlineData("https://host.example.org/system?tab=services", "services")]
    [InlineData("reportmate://system?tab=updates&states=pending", "updates")]
    [InlineData("reportmate://system/background", "background")]
    [InlineData("https://host.example.org/management?tab=logs", "logs")]
    [InlineData("reportmate://management/logs", "logs")]
    [InlineData("https://host.example.org/management?tab=preferences", "preferences")]
    [InlineData("https://host.example.org/security?tab=vulnerabilities&view=devices", "vulnerabilities")]
    [InlineData("https://host.example.org/identity?tab=posture", "posture")]
    public void A_link_names_its_module_tab(string url, string tab) =>
        Assert.Equal(tab, ModuleTabs.TabOf(DeepLink.Parse(url)!));

    [Theory]
    [InlineData("https://host.example.org/system")]
    [InlineData("https://host.example.org/system?tab=os")]
    [InlineData("https://host.example.org/system?tab=nonsense")]
    [InlineData("https://host.example.org/hardware?tab=services")]
    public void A_link_to_the_module_report_itself_names_no_tab(string url) =>
        Assert.Null(ModuleTabs.TabOf(DeepLink.Parse(url)!));

    [Fact]
    public void A_link_reopens_its_own_selections()
    {
        var link = DeepLink.Parse("reportmate://system?tab=services&serviceNames=%5B%22Spooler%22%5D&states=stopped")!;
        var q = TabQuery.From(link);
        Assert.Null(q["tab"]);
        Assert.Equal(["Spooler"], q.List("serviceNames"));
        Assert.Equal(link.ToAppUrl(), q.Link("system", "services").ToAppUrl());
    }

    [Fact]
    public void Every_module_tab_but_the_module_report_has_a_definition()
    {
        foreach (var (module, tabs) in ModuleTabs.All)
            foreach (var tab in tabs.Skip(1).Where(t => t.Key != "logs"))
                Assert.NotNull(TabReports.Find(module, tab.Key, new TabQuery()));
    }

    // ── Requests ─────────────────────────────────────────────────────────

    [Fact]
    public void Services_sends_report_filters_but_not_view_state()
    {
        var q = Q(("serviceNames", "[\"Spooler\"]"), ("states", "stopped"), ("checkin", "stale"), ("evidence", "none"), ("sort", "lastSeen"), ("search", "lab"));
        var path = TabReports.Services.Path(q, "Windows", 500);
        Assert.StartsWith("/api/v1/system/services?", path);
        Assert.Contains("serviceNames=%5B%22Spooler%22%5D", path);
        Assert.Contains("states=stopped", path);
        Assert.Contains("platform=Windows", path);
        Assert.Contains("offset=500&limit=500", path);
        Assert.DoesNotContain("checkin", path);
        Assert.DoesNotContain("evidence", path);
        Assert.DoesNotContain("sort", path);
    }

    [Fact]
    public void The_item_reports_name_the_platform_in_lower_case()
    {
        Assert.Contains("platform=macos", TabReports.Extensions.Path(new TabQuery(), "macOS"));
        Assert.Contains("platform=all", TabReports.Background.OptionsPath(new TabQuery(), "all"));
    }

    [Fact]
    public void Profiles_ask_for_their_kind_and_read_a_fleet_wide_catalog()
    {
        var q = Q(("identifiers", "[\"com.example.wifi\"]"), ("preferenceKey", "Key"), ("includeArchived", "true"));
        Assert.Contains("kind=profile", TabReports.Profiles.Path(q, "macOS"));
        Assert.DoesNotContain("preferenceKey", TabReports.Profiles.Path(q, "macOS"));
        Assert.Contains("preferenceKey=Key", TabReports.Preferences.Path(q, "macOS"));
        Assert.Equal("/api/v1/management/profiles/filters?includeArchived=true", TabReports.Profiles.OptionsPath(q, "macOS"));
    }

    [Fact]
    public void Threats_always_send_a_detection_window()
    {
        Assert.Contains("days=30", TabReports.Threats.Path(new TabQuery(), "all"));
        Assert.Contains("days=0", TabReports.Threats.Path(Q(("days", "0")), "all"));
        Assert.Contains("days=30", TabReports.Threats.Path(Q(("days", "-4")), "all"));
    }

    [Fact]
    public void The_vulnerability_view_picks_summary_or_device_rows()
    {
        var summary = TabReports.Find("security", "vulnerabilities", new TabQuery())!;
        Assert.Same(TabReports.VulnerabilitySummary, summary);
        Assert.Contains("view=summary", summary.Path(new TabQuery(), "all"));
        Assert.DoesNotContain("offset", summary.Path(new TabQuery(), "all"));
        Assert.Same(TabReports.VulnerabilityRows, TabReports.Find("security", "vulnerabilities", Q(("view", "devices"))));
    }

    [Theory]
    [InlineData("services", false)]
    [InlineData("updates", true)]
    [InlineData("background", false)]
    [InlineData("posture", true)]
    public void Some_reports_wait_for_a_selection(string tab, bool runs)
    {
        var module = tab == "posture" ? "identity" : "system";
        Assert.Equal(runs, TabReports.Find(module, tab, new TabQuery())!.HasQuery(new TabQuery()));
    }

    [Fact]
    public void Failed_tasks_and_unsigned_helpers_run_without_selections()
    {
        Assert.True(TabReports.Background.HasQuery(Q(("lastRuns", "failed"))));
        Assert.True(TabReports.Extensions.HasQuery(Q(("signatures", "unsigned"))));
        Assert.False(TabReports.Extensions.HasQuery(Q(("signatures", "signed"))));
        Assert.True(TabReports.Background.HasQuery(Q(("items", "[\"task:\\\\Example\\\\Task\"]"))));
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private static Func<string, Task<TabFetch>> Pages(params string[] pages)
    {
        var i = 0;
        return _ => Task.FromResult(new TabFetch(true, 200, null, J(pages[Math.Min(i++, pages.Length - 1)])));
    }

    [Fact]
    public async Task Every_page_is_read()
    {
        var load = await TabReports.Updates.LoadAsync(new TabQuery(), "all", Pages(
            """{"generatedAt":"2026-01-01T00:00:00Z","total":3,"offset":0,"rows":[{"serialNumber":"TESTSERIAL01","reportedState":"none"},{"serialNumber":"TESTSERIAL02","reportedState":"none"}]}""",
            """{"generatedAt":"2026-01-01T00:00:00Z","total":3,"offset":2,"rows":[{"serialNumber":"TESTSERIAL03","reportedState":"none"}]}"""));
        Assert.True(load.Ok);
        Assert.Equal(["TESTSERIAL01", "TESTSERIAL02", "TESTSERIAL03"], load.Data!.Rows.Select(r => r.Serial));
    }

    [Fact]
    public async Task A_report_that_changes_between_pages_is_refused()
    {
        var load = await TabReports.Updates.LoadAsync(new TabQuery(), "all", Pages(
            """{"total":3,"offset":0,"rows":[{"serialNumber":"TESTSERIAL01","reportedState":"none"}]}""",
            """{"total":4,"offset":1,"rows":[{"serialNumber":"TESTSERIAL02","reportedState":"none"}]}"""));
        Assert.False(load.Ok);
        Assert.Contains("changed", load.Error);
    }

    [Fact]
    public async Task A_repeated_device_is_refused()
    {
        var load = await TabReports.Services.LoadAsync(new TabQuery(), "all", Pages(
            """{"total":2,"offset":0,"devices":[{"serialNumber":"TESTSERIAL01"},{"serialNumber":"TESTSERIAL01"}]}"""));
        Assert.False(load.Ok);
    }

    [Fact]
    public async Task An_empty_page_short_of_the_total_is_refused()
    {
        var load = await TabReports.Threats.LoadAsync(new TabQuery(), "all", Pages("""{"total":5,"offset":0,"rows":[]}"""));
        Assert.False(load.Ok);
        Assert.Contains("incomplete", load.Error);
    }

    [Fact]
    public async Task A_missing_endpoint_says_so()
    {
        var load = await TabReports.Posture.LoadAsync(new TabQuery(), "all", _ => Task.FromResult(new TabFetch(false, 404, "Not Found", null)));
        Assert.True(load.NotFound);
        Assert.Contains("not available", load.Error);
    }

    [Fact]
    public async Task A_response_without_rows_is_an_error_not_an_empty_report()
    {
        var load = await TabReports.Threats.LoadAsync(new TabQuery(), "all", Pages("""{"total":0,"offset":0}"""));
        Assert.False(load.Ok);
    }

    [Fact]
    public async Task Too_many_item_rows_ask_for_narrower_filters()
    {
        var load = await TabReports.Background.LoadAsync(Q(("lastRuns", "failed")), "all", Pages("""{"total":60001,"offset":0,"rows":[{"serialNumber":"TESTSERIAL01"}]}"""));
        Assert.False(load.Ok);
        Assert.Contains("narrow", load.Error);
    }

    [Fact]
    public async Task The_summary_is_one_request()
    {
        var calls = 0;
        var load = await TabReports.VulnerabilitySummary.LoadAsync(new TabQuery(), "all", path =>
        {
            calls++;
            return Task.FromResult(new TabFetch(true, 200, null, J("""{"total":1,"cves":[{"identifier":"CVE-2026-0001","devices":3}]}""")));
        });
        Assert.Equal(1, calls);
        Assert.Single(load.Data!.Rows);
    }

    // ── Honest states ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllReports))]
    public void A_bare_row_reads_without_inventing_values(TabReportDef def)
    {
        var data = Data("""{"generatedAt":"2026-01-01T00:00:00Z","rows":[{"serialNumber":"TESTSERIAL01","somethingNew":{"x":1}}]}""");
        var context = new TabContext(new TabQuery(), "all", null, data);
        var row = data.Rows[0];
        foreach (var column in def.Columns)
        {
            var text = TabReports.Display(column, row);
            Assert.DoesNotContain(text, new[] { "Running", "Installed", "Compliant", "Signed", "Activated", "Succeeded", "Patched", "Configured" });
        }
        Assert.Equal(def.CsvHeaders(context).Count, def.CsvRow(row, context, "https://host.example.org/").Count());
    }

    [Theory]
    [MemberData(nameof(AllReports))]
    public void Fields_of_the_wrong_type_read_as_not_reported(TabReportDef def)
    {
        var data = Data("""{"rows":[{"serialNumber":"TESTSERIAL01","state":7,"services":"x","applications":{},"reported":[],"checks":"y","cves":5,"lastSeen":"not a date","archived":"maybe"}]}""");
        var context = new TabContext(new TabQuery(), "all", null, data);
        foreach (var column in def.Columns) TabReports.Display(column, data.Rows[0]);
        Assert.Equal(def.CsvHeaders(context).Count, def.CsvRow(data.Rows[0], context, "").Count());
    }

    [Fact]
    public void A_service_whose_collection_is_unknown_says_what_it_last_reported()
    {
        var row = Row("""{"serialNumber":"TESTSERIAL01","state":"unknown","reportedState":"running","services":[{"name":"Spooler","status":"Running"}]}""");
        var state = TabReports.Services.Columns.First(c => c.Key == "state");
        Assert.Equal("Unknown (last reported: Running)", TabReports.Display(state, row));
        Assert.Equal("Not found", TabReports.ServiceStateLabel("missing"));
    }

    [Fact]
    public void A_device_with_no_matching_service_is_not_called_clean()
    {
        var services = TabReports.Services.Columns.First(c => c.Key == "services");
        Assert.Equal("No matching service reported; coverage unknown", TabReports.Display(services, Row("""{"serialNumber":"A","state":"unknown","services":[]}""")));
        Assert.Equal("Not found in service inventory", TabReports.Display(services, Row("""{"serialNumber":"A","state":"missing","services":[]}""")));
    }

    [Fact]
    public void Update_flags_and_export_keep_unreported_apart_from_no()
    {
        var row = Row("""{"serialNumber":"TESTSERIAL01","updateKey":"k1","state":"pending","isSecurity":true,"mandatory":false,"downloaded":null}""");
        Assert.Equal(["Security"], TabReports.UpdateFlags(row));
        var context = new TabContext(new TabQuery(), "all");
        var csv = TabReports.Updates.CsvRow(row, context, "").ToList();
        var headers = TabReports.Updates.CsvHeaders(context);
        Assert.Equal("Yes", csv[headers.ToList().IndexOf("Security")]);
        Assert.Equal("No", csv[headers.ToList().IndexOf("Mandatory")]);
        Assert.Equal("Not reported", csv[headers.ToList().IndexOf("Downloaded")]);
    }

    [Fact]
    public void A_device_without_valid_update_collection_says_so()
    {
        var state = TabReports.Updates.Columns.First(c => c.Key == "state");
        Assert.Equal("Unknown (no valid update collection)", TabReports.Display(state, Row("""{"serialNumber":"A","state":"unknown","collectionValid":false}""")));
        Assert.Equal("None pending reported", TabReports.UpdateStateLabel("none"));
    }

    [Fact]
    public void Task_scheduler_sentinel_times_read_as_never_and_not_scheduled()
    {
        Assert.Equal("Never", TabReports.TaskTime("1999-11-30T00:00:00", "Never"));
        Assert.Equal("Not scheduled", TabReports.TaskTime("1899-12-30T00:00:00", "Not scheduled"));
        Assert.Equal("2026-01-02T03:04:05Z", TabReports.TaskTime("2026-01-02T03:04:05Z", "Never"));
    }

    [Fact]
    public void A_column_for_another_item_kind_reads_not_applicable()
    {
        var row = Row("""{"serialNumber":"A","kind":"launchd","lastRun":"failed"}""");
        var lastRun = TabReports.Background.Columns.First(c => c.Key == "lastRun");
        Assert.Equal("Not applicable", TabReports.Display(lastRun, row));
        var context = new TabContext(new TabQuery(), "all");
        var headers = TabReports.Background.CsvHeaders(context).ToList();
        Assert.Equal("Not applicable", TabReports.Background.CsvRow(row, context, "").ToList()[headers.IndexOf("Last result")]);
        Assert.Equal("Failed", TabReports.Display(lastRun, Row("""{"serialNumber":"A","kind":"task","lastRun":"failed"}""")));
    }

    [Fact]
    public void Extensions_do_not_apply_to_windows()
    {
        Assert.Equal("Windows", TabReports.Extensions.NotApplicableOn);
        Assert.Null(TabReports.Background.NotApplicableOn);
    }

    [Fact]
    public void Posture_checks_for_the_other_platform_are_not_applicable()
    {
        var mac = Row("""{"serialNumber":"A","platform":"macOS","reported":{"pssoState":"expired","recoveryKey":"present","personalRecoveryKey":true}}""");
        Assert.Null(TabReports.PostureText(mac, "laps"));
        Assert.Equal("Token expired", TabReports.PostureText(mac, "psso"));
        Assert.Equal("Personal", TabReports.PostureText(mac, "recoveryKey"));
        Assert.Equal("Unknown", TabReports.PostureText(mac, "activationLock"));
        Assert.Equal("Unknown", TabReports.PostureText(mac, "findMy"));

        var win = Row("""{"serialNumber":"B","platform":"Windows","reported":{"laps":"not_configured","autoLogin":true,"autoLoginStoredPassword":true}}""");
        Assert.Equal("Not configured", TabReports.PostureText(win, "laps"));
        Assert.Equal("On, stored password", TabReports.PostureText(win, "autoLogin"));
        Assert.Equal("Min length Unknown · Lockout after Unknown for Unknown", TabReports.PostureText(win, "passwordPolicy"));
        Assert.Equal("Status unknown · Providers unknown", TabReports.PostureText(win, "hello"));
    }

    [Fact]
    public void Posture_offers_only_the_columns_of_the_platform_in_scope()
    {
        var keys = TabReports.Posture.AvailableColumns(new TabContext(new TabQuery(), "Windows")).Select(c => c.Key).ToList();
        Assert.Contains("laps", keys);
        Assert.DoesNotContain("psso", keys);
        Assert.Equal("psso", TabReports.Posture.VisibleColumns(new TabContext(new TabQuery(), "macOS"))[1].Key);
    }

    [Fact]
    public void The_posture_export_marks_other_platform_quick_views_not_applicable()
    {
        var row = Row("""{"serialNumber":"A","platform":"Windows","checks":{"noLaps":true,"autoLogin":null}}""");
        var context = new TabContext(new TabQuery(), "all");
        var headers = TabReports.Posture.CsvHeaders(context).ToList();
        var csv = TabReports.Posture.CsvRow(row, context, "").ToList();
        Assert.Equal("Yes", csv[headers.IndexOf("Quick View: No LAPS")]);
        Assert.Equal("Unknown", csv[headers.IndexOf("Quick View: Auto-login on")]);
        Assert.Equal("Not applicable", csv[headers.IndexOf("Quick View: Activation Lock on")]);
        Assert.Equal("Not applicable", csv[headers.IndexOf("Platform SSO")]);
    }

    [Fact]
    public void The_coverage_line_counts_silent_devices_as_unknown_not_clean()
    {
        var data = Data("""{"coverage":{"devices":10,"reporting":7,"notReporting":3,"listMayBeIncomplete":2},"cves":[]}""", "cves");
        var text = TabReports.CoverageText(data, detections: false)!;
        Assert.Contains("3 do not (coverage unknown, not counted as clean)", text);
        Assert.Contains("2 macOS devices reached", text);
        Assert.Null(TabReports.CoverageText(Data("""{"cves":[]}""", "cves"), false));
    }

    [Fact]
    public void A_cve_opens_the_devices_that_reported_it()
    {
        var link = TabReports.VulnerabilitySummary.RowLink(Row("""{"identifier":"CVE-2026-0001"}"""), Q(("sort", "devices"), ("statuses", "unpatched")))!;
        var q = TabQuery.From(link);
        Assert.Equal("vulnerabilities", link["tab"]);
        Assert.Equal("devices", q["view"]);
        Assert.Equal(["CVE-2026-0001"], q.List("cves"));
        Assert.Equal("unpatched", q["statuses"]);
        Assert.Null(q["sort"]);
    }

    [Fact]
    public void Device_rows_open_the_device_page_on_the_report_tab()
    {
        var row = Row("""{"serialNumber":"TESTSERIAL01"}""");
        Assert.Equal("reportmate://device/TESTSERIAL01?tab=security", TabReports.VulnerabilityRows.RowLink(row, new TabQuery())!.ToAppUrl());
        Assert.Equal("reportmate://device/TESTSERIAL01?tab=system", TabReports.Services.RowLink(row, new TabQuery())!.ToAppUrl());
        Assert.Equal("reportmate://device/TESTSERIAL01?tab=identity", TabReports.Posture.RowLink(row, new TabQuery())!.ToAppUrl());
    }

    [Fact]
    public void Only_web_links_go_to_reference_pages()
    {
        Assert.Equal("https://nvd.nist.gov/vuln/detail/CVE-2026-0001", TabReports.VulnerabilityHref("CVE-2026-0001", null, "SOFA"));
        Assert.Equal("", TabReports.SafeHttpUrl("javascript:alert(1)"));
        Assert.Equal("https://example.com/a", TabReports.SafeHttpUrl("https://example.com/a"));
    }

    // ── Selections ───────────────────────────────────────────────────────

    [Fact]
    public void Selections_come_from_the_catalog()
    {
        var catalog = J("""{"services":[{"name":"Spooler","displayName":"Print Spooler","devices":12,"startups":["automatic"]}],"applications":[],"options":{"usages":["Shared"],"inventoryStates":["storage"]}}""");
        var context = new TabContext(Q(("service", "spool")), "all", catalog);
        var services = TabReports.Services.Selections[0];
        Assert.Equal(new TabOption("Spooler", "Print Spooler", 12), services.Options(context).Single());
        // An old link's substring filter resolves to the names devices reported.
        Assert.Equal(["Spooler"], services.Current(context));
        var startups = TabReports.Services.Choices.First(c => c.Param == "startups").Resolve(context);
        Assert.Equal("Automatic", startups.Single().Label);
        Assert.Equal(["Shared"], context.Scope("usages"));
    }

    [Fact]
    public void Profile_selections_follow_the_platform_in_scope()
    {
        var catalog = J("""{"profiles":[{"identifier":"a","name":"A","devices":1,"platforms":["macOS"]},{"identifier":"b","name":"B","devices":1,"platforms":["Windows"]},{"identifier":"c","name":"C","devices":1,"platforms":[]}]}""");
        var options = TabReports.Profiles.Selections[0].Options(new TabContext(new TabQuery(), "macOS", catalog));
        Assert.Equal(["a", "c"], options.Select(o => o.Value));
    }

    [Fact]
    public void Update_state_choices_follow_whether_updates_are_selected()
    {
        var states = TabReports.Updates.Choices.First(c => c.Param == "states");
        Assert.DoesNotContain(states.Resolve(new TabContext(new TabQuery(), "all")), o => o.Value == "installed");
        Assert.DoesNotContain(states.Resolve(new TabContext(Q(("updates", "[\"k\"]")), "all")), o => o.Value == "none");
    }

    [Fact]
    public void Posture_quick_views_show_counts_and_unknowns()
    {
        var data = Data("""{"summary":{"noLaps":4},"unknown":{"noLaps":2},"devices":[]}""", "devices");
        var views = TabReports.Posture.Choices.First(c => c.Param == "views").Resolve(new TabContext(new TabQuery(), "Windows", null, data));
        Assert.Equal(["No LAPS 4 · 2 unknown", "Auto-login on 0"], views.Select(v => v.Label));
    }

    // ── The view ─────────────────────────────────────────────────────────

    [Fact]
    public void Evidence_and_checkin_filter_the_loaded_rows()
    {
        var data = Data("""
        {"generatedAt":"2026-01-10T00:00:00Z","devices":[
          {"serialNumber":"A","lastSeen":"2026-01-09T00:00:00Z","services":[{"name":"x"}],"applications":[]},
          {"serialNumber":"B","lastSeen":"2025-12-01T00:00:00Z","services":[],"applications":[]},
          {"serialNumber":"C","services":[],"applications":[]}
        ]}
        """, "devices");
        string[] View(params (string, string?)[] q) =>
            TabReports.Services.ViewRows(new TabContext(Q(q), "all", null, data)).Select(r => r.Serial).ToArray();
        Assert.Equal(["A"], View(("evidence", "services")));
        Assert.Equal(["B", "C"], View(("evidence", "none")));
        Assert.Equal(["B"], View(("checkin", "stale")));
        Assert.Equal(["C"], View(("checkin", "unknown")));
        Assert.Equal(["A", "B"], View(("checkin", "recent"), ("checkinAgeHours", "2000")));
    }

    [Fact]
    public void Unknown_values_sort_last_in_either_direction()
    {
        var data = Data("""{"rows":[{"serialNumber":"A","ageDays":10},{"serialNumber":"B"},{"serialNumber":"C","ageDays":9}]}""");
        string[] Sorted(string dir) => TabReports.Updates
            .ViewRows(new TabContext(Q(("sort", "ageDays"), ("direction", dir), ("columns", "deviceName,ageDays")), "all", null, data))
            .Select(r => r.Serial).ToArray();
        Assert.Equal(["C", "A", "B"], Sorted("asc"));
        Assert.Equal(["A", "C", "B"], Sorted("desc"));
    }

    [Fact]
    public void A_sort_on_a_hidden_column_falls_back_to_the_default()
    {
        var context = new TabContext(Q(("sort", "room"), ("direction", "desc")), "all");
        Assert.Equal(("deviceName", true), TabReports.Updates.SortOf(context));
    }

    [Fact]
    public void Threats_sort_newest_first_by_default()
    {
        var data = Data("""{"rows":[{"serialNumber":"A","lastSeenAt":"2026-01-01T00:00:00Z"},{"serialNumber":"B","detectedAt":"2026-02-01T00:00:00Z"}]}""");
        Assert.Equal(["B", "A"], TabReports.Threats.ViewRows(new TabContext(new TabQuery(), "all", null, data)).Select(r => r.Serial));
    }

    [Fact]
    public void Exploited_cves_sort_ahead_of_unreported_ones()
    {
        var data = Data("""{"cves":[{"identifier":"CVE-1","activelyExploited":false},{"identifier":"CVE-2"},{"identifier":"CVE-3","activelyExploited":true}]}""", "cves");
        var rows = TabReports.VulnerabilitySummary.ViewRows(new TabContext(Q(("sort", "exploited")), "all", null, data));
        Assert.Equal(["CVE-3", "CVE-2", "CVE-1"], rows.Select(r => r.Str("identifier")));
    }

    [Fact]
    public void Columns_come_from_the_link_and_always_keep_the_device()
    {
        var keys = TabReports.Services.VisibleColumns(new TabContext(Q(("columns", "room,lastSeen")), "all")).Select(c => c.Key);
        Assert.Equal(["deviceName", "room", "lastSeen"], keys);
        // Application columns only when an application is selected.
        Assert.DoesNotContain(TabReports.Services.VisibleColumns(new TabContext(new TabQuery(), "all")), c => c.Key == "applications");
    }

    [Fact]
    public void Natural_order_compares_numbers_as_numbers()
    {
        Assert.True(SortKey.Natural("item9", "item10") < 0);
        Assert.True(SortKey.Natural("B", "a") > 0);
    }

    [Fact]
    public void The_export_quotes_what_a_spreadsheet_would_run()
    {
        var data = Data("""{"generatedAt":"2026-01-01T00:00:00Z","rows":[{"serialNumber":"=cmd","threatName":"a,b"}]}""");
        var context = new TabContext(new TabQuery(), "all", null, data);
        var csv = TabReports.Threats.Csv(context, data.Rows, "https://host.example.org/security?tab=threats");
        Assert.StartsWith("Threat,Threat ID,", csv);
        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"'=cmd\"", csv);
    }

    // ── Enrollment health ────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_unknown_value_never_matches_a_problem_filter()
    {
        var unknown = J("""{"platform":"macOS","complianceState":null,"lastMdmSync":null,"mdmCertificateExpires":null}""");
        Assert.False(EnrollmentHealth.Matches(unknown, new(true, null, null), Now));
        Assert.False(EnrollmentHealth.Matches(unknown, new(false, 7, null), Now));
        Assert.False(EnrollmentHealth.Matches(unknown, new(false, null, 30), Now));
        Assert.True(EnrollmentHealth.Matches(unknown, new(false, null, null), Now));
    }

    [Fact]
    public void Problem_filters_match_reported_problems()
    {
        var row = J("""{"platform":"Windows","complianceState":"non_compliant","lastMdmSync":"2026-05-01T00:00:00Z","mdmCertificateExpires":"2026-06-10T00:00:00Z"}""");
        Assert.True(EnrollmentHealth.Matches(row, new(true, 30, 30), Now));
        Assert.False(EnrollmentHealth.Matches(row, new(false, 31, null), Now));
        Assert.False(EnrollmentHealth.Matches(row, new(false, null, 5), Now));
    }

    [Fact]
    public void Enrollment_health_reads_unknown_and_not_applicable_honestly()
    {
        var win = J("""{"platform":"Windows","complianceState":"compliant","lastMdmSync":"2026-05-31T12:00:00Z","mdmCertificateExpires":"2026-05-29T00:00:00Z"}""");
        Assert.Equal("Compliant", EnrollmentHealth.Compliance(win));
        Assert.Equal("Today", EnrollmentHealth.LastSync(win, Now));
        Assert.Equal("Expired 3 days ago", EnrollmentHealth.Certificate(win, Now));
        Assert.Equal("Not applicable", EnrollmentHealth.AppleMdm(win));

        var mac = J("""{"platform":"macOS","userApprovedMdm":true,"depCapable":null}""");
        Assert.Equal("Unknown", EnrollmentHealth.Compliance(mac));
        Assert.Equal("Unknown", EnrollmentHealth.LastSync(mac, Now));
        Assert.Equal("User approved: Yes · ADE installed: Unknown · DEP capable: Unknown", EnrollmentHealth.AppleMdm(mac));
        Assert.Equal(EnrollmentHealth.CsvHeaders.Length, EnrollmentHealth.CsvValues(mac).Length);
    }

    [Fact]
    public void Enrollment_health_filters_round_trip_through_a_link()
    {
        var link = DeepLink.Parse("https://host.example.org/management?compliance=non_compliant&syncOlderThan=7&certExpiring=abc")!;
        var filters = EnrollmentHealth.Filters.From(k => link[k]);
        Assert.Equal(new EnrollmentHealth.Filters(true, 7, null), filters);
        Assert.Equal(2, filters.Active);
    }
}
