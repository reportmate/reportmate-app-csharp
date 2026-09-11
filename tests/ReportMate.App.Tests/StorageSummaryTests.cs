using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Internal free space, summed per device. Every device reports it and no report
/// showed it; the web's column for it never fills because the payload writes
/// freeSpace and the page reads free and available.
/// </summary>
public class StorageSummaryTests
{
    private static string? Storage(string json) =>
        Hardware.Storage(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void SumsTheInternalDisks()
    {
        var s = Storage("""
            {"storage":[{"capacity":1000000000000,"freeSpace":400000000000,"isInternal":true},
                        {"capacity":500000000000,"freeSpace":100000000000,"isInternal":true}]}
            """);
        Assert.Equal("465.66 GB free of 1.36 TB", s);
    }

    /// <summary>
    /// A 4 TB drive plugged in is not 4 TB of headroom, and the question this
    /// answers is whether the machine is about to run out.
    /// </summary>
    [Fact]
    public void ExternalDisksAreExcluded()
    {
        var s = Storage("""
            {"storage":[{"capacity":1000000000000,"freeSpace":400000000000,"isInternal":true},
                        {"capacity":4000000000000,"freeSpace":3900000000000,"isInternal":false}]}
            """);
        Assert.Equal("372.53 GB free of 931.32 GB", s);
    }

    /// <summary>
    /// Only an explicit false excludes a drive. Measured across the fleet, the flag
    /// is present but null on all 505 Mac entries and on 40 of the Windows ones,
    /// against 394 true and 19 false -- so a rule written as "isInternal == true"
    /// drops every Mac's boot disk and leaves those devices with no storage at all.
    /// The null case is the one that actually occurs; absent is covered because
    /// nothing guarantees the key.
    /// </summary>
    [Theory]
    [InlineData("""{"capacity":1000000000000,"freeSpace":400000000000}""")]
    [InlineData("""{"capacity":1000000000000,"freeSpace":400000000000,"isInternal":null}""")]
    [InlineData("""{"capacity":1000000000000,"freeSpace":400000000000,"isInternal":true}""")]
    public void OnlyAnExplicitFalseExcludesADrive(string disk)
    {
        Assert.Equal("372.53 GB free of 931.32 GB", Storage($$"""{"storage":[{{disk}}]}"""));
    }

    [Theory]
    [InlineData("""{"storage":[]}""")]
    [InlineData("""{"storage":[{"capacity":0,"freeSpace":0}]}""")]
    [InlineData("""{"storage":[{"capacity":1000,"freeSpace":0}]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"storage":"nope"}""")]
    public void NothingUsableYieldsNothing(string json) => Assert.Null(Storage(json));
}
