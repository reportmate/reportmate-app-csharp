using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The two clients spell the same answer differently, so a chart over the raw field
/// shows vocabularies instead of categories. These cases pin the normalizations,
/// including the ones the web's own ladder gets wrong.
/// </summary>
public class NormalizationTests
{
    private static string? Arch(string json) =>
        Hardware.Normalize(JsonDocument.Parse(json).RootElement);

    private static string? Conn(string type) =>
        Network.ConnectionKind(JsonDocument.Parse(
            "{\"raw\":{\"activeConnection\":{\"connectionType\":"
            + JsonSerializer.Serialize(type) + "}}}").RootElement);

    [Theory]
    [InlineData("arm64")]
    [InlineData("aarch64")]
    [InlineData("ARM64")]
    public void ArmSpellings_AreArm64(string arch) =>
        Assert.Equal("ARM64", Arch("{\"architecture\":" + JsonSerializer.Serialize(arch) + "}"));

    [Fact]
    public void ArmSixtyFourBitProcessor_IsArm64_NotX64()
    {
        // 23 Snapdragon X Elite machines report this. It contains "64-bit", so a
        // ladder that tests for x64 first calls every one of them x64 -- which is
        // what the web's architecture donut does today.
        Assert.Equal("ARM64", Arch("""{"architecture":"ARM 64-bit Processor"}"""));
    }

    [Fact]
    public void SnapdragonOverridesTheArchitectureString() =>
        Assert.Equal("ARM64", Arch("""
            {"architecture":"64-bit","processor":{"name":"Snapdragon X Elite"}}
            """));

    [Fact]
    public void AppleSiliconWithNoArchitectureAtAll_IsStillArm64()
    {
        // Four devices report no architecture. The processor says M1 or M3, so
        // they are not unknown -- the web charts them as Unknown because it
        // stringifies the processor object and matches "[object Object]".
        Assert.Equal("ARM64", Arch("""{"processor":{"name":"M1"}}"""));
        Assert.Equal("ARM64", Arch("""{"graphics":{"name":"Apple M3"}}"""));
    }

    [Theory]
    [InlineData("64-bit")]
    [InlineData("x86_64")]
    [InlineData("AMD64")]
    [InlineData("x64")]
    public void IntelSpellings_AreX64(string arch) =>
        Assert.Equal("x64", Arch("{\"architecture\":" + JsonSerializer.Serialize(arch) + "}"));

    [Fact]
    public void IntelProcessorDoesNotTriggerTheArmOverride() =>
        Assert.Equal("x64", Arch("""
            {"architecture":"64-bit","processor":{"name":"Core i9-9900K"},
             "graphics":{"name":"GeForce RTX 3080"}}
            """));

    [Fact]
    public void NoArchitectureAndNoArmProcessor_IsNull() =>
        Assert.Null(Arch("""{"processor":{"name":"Core i7-8700"}}"""));

    [Theory]
    [InlineData("Wired", "Wired")]
    [InlineData("Ethernet", "Wired")]
    [InlineData("WiFi", "Wireless")]
    [InlineData("Wireless", "Wireless")]
    [InlineData("Wi-Fi", "Wireless")]
    public void TheTwoVocabulariesCollapseToTwoAnswers(string reported, string expected) =>
        Assert.Equal(expected, Conn(reported));

    [Fact]
    public void AnUnrecognisedConnectionKeepsItsOwnLabel() =>
        // Not an unknown: the device genuinely has no active connection.
        Assert.Equal("None", Conn("None"));

    [Fact]
    public void NoConnectionTypeField_IsNull() =>
        Assert.Null(Network.ConnectionKind(JsonDocument.Parse("""{"raw":{}}""").RootElement));
}
