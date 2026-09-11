using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Munki and Cimian describe the same three outcomes in five different payload
/// shapes and one fleet carries all of them, so each shape is pinned separately.
/// </summary>
public class EventPayloadTests
{
    private static EventPayload.Details Extract(string json) =>
        EventPayload.Extract(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void MunkiJoinsMessagesIntoOneStringWithSemicolons()
    {
        var d = Extract("""{"errors":"Adobe failed; Zoom failed","warnings":"Chrome is old"}""");
        Assert.Equal(["Adobe failed", "Zoom failed"], d.Errors);
        Assert.Equal(["Chrome is old"], d.Warnings);
    }

    [Fact]
    public void CimianListsItemsByName()
    {
        var d = Extract("""{"warning_items":["Firefox","Slack"],"error_items":["Acrobat"]}""");
        Assert.Equal(["Firefox", "Slack"], d.Warnings);
        Assert.Equal(["Acrobat"], d.Errors);
    }

    /// <summary>
    /// The item and the reason are kept together: the name alone does not say what
    /// went wrong, and the reason alone does not say what to fix.
    /// </summary>
    [Fact]
    public void FailedItemsPairTheNameWithTheReason()
    {
        var d = Extract("""
            {"failed_items":[{"displayName":"Adobe Acrobat","error":"exit code 1603"},
                             {"name":"Zoom"},
                             {"error":"disk full"}]}
            """);
        Assert.Equal(["Adobe Acrobat: exit code 1603", "Zoom", "disk full"], d.Errors);
    }

    [Fact]
    public void GenericMessageArraysReadTheSameWay()
    {
        var d = Extract("""
            {"error_messages":["boom"],"warning_messages":[{"name":"Teams","warning":"pending reboot"}]}
            """);
        Assert.Equal(["boom"], d.Errors);
        Assert.Equal(["Teams: pending reboot"], d.Warnings);
    }

    [Fact]
    public void SuccessPayloadIsAFlatNameToVersionMap()
    {
        var d = Extract("""{"Managed Safari":"15.6.1","ZoomPrefs":"14.1"}""");
        Assert.Equal(["Managed Safari 15.6.1", "ZoomPrefs 14.1"], d.Successes);
    }

    /// <summary>
    /// The envelope's own strings sit at the top level beside the package names.
    /// Without the reserved list, a run's session id and run type would each read
    /// as a package that installed successfully.
    /// </summary>
    [Fact]
    public void EnvelopeKeysAreNotMistakenForInstalledPackages()
    {
        var d = Extract("""
            {"session_id":"abc-123","run_type":"auto","message":"Run finished",
             "summary":"1 installed","Managed Safari":"15.6.1"}
            """);
        Assert.Equal(["Managed Safari 15.6.1"], d.Successes);
    }

    /// <summary>A retried package logs once per attempt; five identical lines are one problem.</summary>
    [Fact]
    public void RepeatedLinesCollapse()
    {
        var d = Extract("""{"error_items":["Acrobat","Acrobat","Acrobat"]}""");
        Assert.Equal(["Acrobat"], d.Errors);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"just a string\"")]
    public void NothingUsableYieldsNothing(string json)
    {
        var d = Extract(json);
        Assert.Equal(0, d.Count);
    }

    /// <summary>
    /// A number is not a version string. Counters live beside the package map and
    /// only strings are packages, so a stray count cannot become one.
    /// </summary>
    [Fact]
    public void NonStringValuesAreNotSuccesses()
    {
        var d = Extract("""{"moduleCount":14,"installed":true,"Managed Safari":"15.6.1"}""");
        Assert.Equal(["Managed Safari 15.6.1"], d.Successes);
    }

    // ── Shapes measured on the live fleet, which the web app's extractor misses ──

    /// <summary>
    /// The commonest success event on this fleet. The web reads none of this and
    /// expands to nothing; action would additionally have read as a package named
    /// "action" that installed a version called "install".
    /// </summary>
    [Fact]
    public void CimianInstallRunListsWhatItInstalled()
    {
        var d = Extract("""
            {"action":"install","count":3,"duration_seconds":41.2,"module_status":"success",
             "items":[{"name":"PowerPoint","version":"16.112.26083020"},
                      {"name":"Chrome","version":"152.0.7977.83"}]}
            """);
        Assert.Equal(["PowerPoint 16.112.26083020", "Chrome 152.0.7977.83"], d.Successes);
        Assert.Empty(d.Removed);
    }

    [Fact]
    public void RemovalRunListsWhatItUninstalled()
    {
        var d = Extract("""
            {"action":"remove","count":1,
             "removed_items":[{"name":"StaffSingleSignOnPrefs","version":"14.3.2"}]}
            """);
        Assert.Equal(["StaffSingleSignOnPrefs 14.3.2"], d.Removed);
        Assert.Empty(d.Successes);
    }

    /// <summary>
    /// Cimian puts the reason under "message", not "warning". Reading only the
    /// other two spellings showed the bare item name and dropped the one sentence
    /// that says what to do about it.
    /// </summary>
    [Fact]
    public void WarningItemsCarryTheirReasonUnderMessage()
    {
        var d = Extract("""
            {"warning_items":[{"name":"Outlook","version":"",
              "message":"Could not process item Outlook for install. No pkginfo found in catalogs"}]}
            """);
        Assert.Equal(
            ["Outlook: Could not process item Outlook for install. No pkginfo found in catalogs"],
            d.Warnings);
    }

    [Fact]
    public void CountersAndTimingsAreNeverPackages()
    {
        var d = Extract("""
            {"duration_seconds":"41.2","item_warning_count":"2","operational_warning_count":"0",
             "action":"install","Chrome":"152.0.7977.83"}
            """);
        Assert.Equal(["Chrome 152.0.7977.83"], d.Successes);
    }

    [Fact]
    public void BlankAndWhitespaceEntriesAreDropped()
    {
        var d = Extract("""{"errors":" ; Adobe failed ;  ","error_items":["","  "]}""");
        Assert.Equal(["Adobe failed"], d.Errors);
    }
}
