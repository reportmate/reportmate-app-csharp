using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// Flattening the per-device installs payload into item rows. This exists because
/// the flat endpoint returns items grouped by device and caps at 5,000 rows, so the
/// report was drawing fleet charts over the first fifty machines.
/// </summary>
public class InstallsFlattenTests
{
    private const string Payload = """
        [
          {"deviceName":"A","serialNumber":"S1","lastSeen":"2026-09-08T00:00:00Z",
           "modules":{"inventory":{"catalog":"Staff","location":"B1115","assetTag":"A1","usage":"Assigned"},
             "installs":{"cimian":{"items":[
               {"itemName":"PowerShell","currentStatus":"Installed","latestVersion":"7.6.1.0","installedVersion":""},
               {"itemName":"Firefox","currentStatus":"Removed","latestVersion":"140"}]}}}},
          {"deviceName":"B","serialNumber":"S2",
           "modules":{"installs":{"cimian":{"items":[
               {"itemName":"Chrome","currentStatus":"Installed"}]}}}},
          {"deviceName":"Mac","serialNumber":"S3",
           "modules":{"installs":{"cimian":{"items":[]}}}}
        ]
        """;

    private static List<JsonElement> Rows(string json)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "itemName", "currentStatus", "installedVersion", "latestVersion" };
        var (_, rows) = Installs.Flatten(JsonDocument.Parse(json), keep);
        return rows;
    }

    private static string? Get(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public void OneRowPerItem_AcrossEveryDevice()
    {
        var rows = Rows(Payload);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["PowerShell", "Firefox", "Chrome"], rows.Select(r => Get(r, "itemName")));
    }

    [Fact]
    public void EachRowCarriesItsDevice()
    {
        var rows = Rows(Payload);
        Assert.Equal(["A", "A", "B"], rows.Select(r => Get(r, "deviceName")));
        Assert.Equal(["S1", "S1", "S2"], rows.Select(r => Get(r, "serialNumber")));
    }

    [Fact]
    public void InventoryFieldsComeAlong_SoTheCatalogFilterStillBinds()
    {
        // The catalog is a device fact in this payload but a row filter in the
        // report, so it has to be copied down or every catalog link goes empty.
        var rows = Rows(Payload);
        Assert.Equal("Staff", Get(rows[0], "catalog"));
        Assert.Equal("B1115", Get(rows[0], "location"));
        Assert.Null(Get(rows[2], "catalog"));
    }

    [Fact]
    public void EveryRowIsWindows_BecauseOnlyCimianReportsHere() =>
        Assert.All(Rows(Payload), r => Assert.Equal("Windows", Get(r, "platform")));

    [Fact]
    public void ADeviceWithNoItems_ContributesNoRows() =>
        // The 493 Macs are in the payload with an empty Cimian structure. They must
        // not become rows, or the item counts gain a device that reported nothing.
        Assert.DoesNotContain(Rows(Payload), r => Get(r, "deviceName") == "Mac");

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"deviceName":"A","modules":{}}]""")]
    [InlineData("""[{"deviceName":"A","modules":{"installs":{"cimian":{}}}}]""")]
    [InlineData("""[{"deviceName":"A","modules":{"installs":null}}]""")]
    public void AMissingOrEmptyPayloadIsNotAnError(string json) =>
        Assert.Empty(Rows(json));

    [Fact]
    public void ItemFieldsSurviveIntact()
    {
        var row = Rows(Payload)[0];
        Assert.Equal("Installed", Get(row, "currentStatus"));
        Assert.Equal("7.6.1.0", Get(row, "latestVersion"));
        Assert.Equal("", Get(row, "installedVersion"));
    }
}
