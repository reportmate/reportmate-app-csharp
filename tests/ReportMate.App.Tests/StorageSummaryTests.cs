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

    /// <summary>The Macs omit the flag on the built-in disk, so absent means internal.</summary>
    [Fact]
    public void AbsentInternalFlagCountsAsInternal()
    {
        var s = Storage("""{"storage":[{"capacity":1000000000000,"freeSpace":400000000000}]}""");
        Assert.Equal("372.53 GB free of 931.32 GB", s);
    }

    [Theory]
    [InlineData("""{"storage":[]}""")]
    [InlineData("""{"storage":[{"capacity":0,"freeSpace":0}]}""")]
    [InlineData("""{"storage":[{"capacity":1000,"freeSpace":0}]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"storage":"nope"}""")]
    public void NothingUsableYieldsNothing(string json) => Assert.Null(Storage(json));
}
