using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// API numbers that are NaN, infinite, huge or negative never throw and never
/// print as int.MinValue: they read as absent and show a dash.
/// </summary>
public class NumberSafetyTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e30)]
    [InlineData(-1e30)]
    public void AnUnusableDoubleIsNoInt(double value)
    {
        Assert.Null(Format.ToInt(value));
        Assert.Null(Format.ToLong(value));
    }

    [Fact]
    public void AnOrdinaryDoubleIsItsInt()
    {
        Assert.Equal(42, Format.ToInt(42.9));
        Assert.Equal(-3, Format.ToInt(-3));
        Assert.Null(Format.ToInt(null));
    }

    [Theory]
    [InlineData(double.NaN, "—")]
    [InlineData(double.PositiveInfinity, "—")]
    [InlineData(1e300, "—")]
    [InlineData(-5, "0 h")]
    [InlineData(950.4, "950 h")]
    public void CompactHoursNeverThrows(double hours, string expected) =>
        Assert.Equal(expected, InsightCards.CompactHours(hours));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public void DurationsShowADashForAnImpossibleSpan(double seconds)
    {
        Assert.Equal("—", Format.Duration(seconds));
        Assert.Equal("—", Format.DurationLong(seconds));
    }

    [Fact]
    public void ANegativeDurationIsZero()
    {
        Assert.Equal("0s", Format.Duration(-10));
        Assert.Equal("—", Format.Bytes(double.NaN));
        Assert.Equal("0 Bytes", Format.Bytes(-5d));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("99999999999999999999")]
    public void AnImpossibleTimestampIsNoDate(string value) => Assert.Null(Format.ParseDate(value));

    [Fact]
    public void NumericStringsThatAreNotNumbersReadAsAbsent()
    {
        var row = JsonDocument.Parse("""{"nan":"NaN","inf":"Infinity","huge":"1e400","big":1e30,"neg":-4}""").RootElement;
        Assert.Null(Insights.Number(row, "nan"));
        Assert.Null(Insights.Number(row, "inf"));
        Assert.Null(Insights.Number(row, "huge"));
        Assert.Equal(0, Insights.Int(row, "big"));
        Assert.Equal(-4, Insights.Int(row, "neg"));
    }
}
