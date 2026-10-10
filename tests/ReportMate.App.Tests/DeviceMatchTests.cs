using System.Collections.Generic;
using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>Return in a host's search field opens the device the query most plainly means.</summary>
public class DeviceMatchTests
{
    private sealed record Row(string Name, string Serial, string? Asset = null, string? Host = null);

    private static IReadOnlyList<string?> Fields(Row r) => [r.Name, r.Serial, r.Asset, r.Host];

    [Fact]
    public void AnExactSerialBeatsANameThatOnlyContainsIt()
    {
        var rows = new[] { new Row("lab-C02X1", "AAA"), new Row("studio-7", "C02X1") };
        Assert.Equal("studio-7", DeviceMatch.Best(rows, "c02x1", Fields)!.Name);
    }

    [Fact]
    public void APrefixBeatsAMatchInTheMiddle()
    {
        var rows = new[] { new Row("old-mac-mini", "S1"), new Row("mac-studio", "S2") };
        Assert.Equal("mac-studio", DeviceMatch.Best(rows, "mac", Fields)!.Name);
    }

    [Fact]
    public void TheNameWinsATieWithAnIdentifier()
    {
        var rows = new[] { new Row("x", "PC-1"), new Row("PC-1", "y") };
        Assert.Equal("PC-1", DeviceMatch.Best(rows, "pc-1", Fields)!.Name);
    }

    [Fact]
    public void TheEarlierDeviceWinsAnEqualMatch()
    {
        var rows = new[] { new Row("pc-a", "1"), new Row("pc-b", "2") };
        Assert.Equal("pc-a", DeviceMatch.Best(rows, "pc", Fields)!.Name);
    }

    [Fact]
    public void AssetTagAndHostnameAreSearched()
    {
        var rows = new[] { new Row("a", "1", Asset: "T-100"), new Row("b", "2", Host: "host-9") };
        Assert.Equal("a", DeviceMatch.Best(rows, "t-100", Fields)!.Name);
        Assert.Equal("b", DeviceMatch.Best(rows, "host-9", Fields)!.Name);
    }

    [Fact]
    public void NoMatchOrABlankQueryFindsNothing()
    {
        var rows = new[] { new Row("a", "1") };
        Assert.Null(DeviceMatch.Best(rows, "zzz", Fields));
        Assert.Null(DeviceMatch.Best(rows, "  ", Fields));
    }
}
