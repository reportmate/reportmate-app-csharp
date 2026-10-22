using System.Linq;
using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The Mac peripherals reader, on payloads shaped like the Mac client's: flags as
/// 0/1, a nested inputDevices object, Bluetooth that includes phones and Macs.
/// </summary>
public class MacPeripheralsInfoTests
{
    private static MacPeripheralsInfo Read(string json) => new(JsonDocument.Parse(json).RootElement);

    private const string MacPayload = """
        {
          "collectedAt": "2026-01-01T00:00:00Z",
          "audioDevices": [
            {"name":"Sample Speakers","manufacturer":"Example Inc.","type":"Output","isDefault":1,"isInput":0,"isOutput":1,"isBuiltIn":0,"connectionType":"External"},
            {"name":"Sample Mic","manufacturer":"Example Inc.","type":"Input","isDefault":0,"isInput":1,"isOutput":0,"isBuiltIn":1,"connectionType":"Built-in"}
          ],
          "bluetoothDevices": [
            {"name":"Sample Keyboard","address":"00:00:00:00:00:01","deviceCategory":"Keyboard","deviceType":"Keyboard","isConnected":0,"isPaired":1},
            {"name":"Other Keyboard","address":"00:00:00:00:00:02","deviceCategory":"Keyboard","deviceType":"Keyboard","isConnected":1,"isPaired":1,"batteryLevel":"80"},
            {"name":"Sample iPhone","address":"00:00:00:00:00:03","deviceCategory":"Phone","isConnected":0,"isPaired":1},
            {"name":"Sample MacBook Pro","address":"00:00:00:00:00:04","isConnected":0,"isPaired":1},
            {"name":"Desk Speaker","address":"00:00:00:00:00:05","deviceCategory":"Headphones","isConnected":1,"isPaired":1}
          ],
          "externalStorage": [
            {"name":"","mountPoint":"/Volumes/Backup","devicePath":"/dev/disk9s1","fileSystem":"apfs","totalSize":"1 TB","protocol":"USB","storageType":"External Storage"}
          ],
          "inputDevices": {
            "keyboards": [{"name":"Sample Keyboard","vendor":"Example Inc.","vendorId":"004C","productId":"0321","isBuiltIn":0,"connectionType":"Bluetooth","deviceType":"Keyboard"}],
            "mice": [],
            "tablets": [],
            "trackpads": [{"name":"Sample Trackpad","vendorId":"004C","productId":"0324","isBuiltIn":0,"connectionType":"Bluetooth","supportsForcTouch":1}]
          },
          "printers": [
            {"name":"Hall Printer","uri":"lpd://print.example.org/hall","status":"Idle","isDefault":false,"stateReasons":["none"],"make":"Example","model":"Model One"},
            {"name":"Sample Printer","uri":"ipp://print.example.org/sample","status":"Idle","isDefault":"true","manufacturer":"Example","model":"Model Two",
             "stateReasons":["toner-low","none",""],"ppd":"Sample PPD","cupsVersion":"CUPS/2.3.4","faxSupport":"no","scanningSupport":"No",
             "cupsFilters":[{"name":"sampletops","path":"/Library/Printers/Example/sampletops","version":"1.0"},{"path":"/no/name"}]}
          ],
          "scanners": [],
          "thunderboltDevices": [{"name":"Thunderbolt/USB4 Bus 1","vendor":"Example Inc.","deviceId":"","uid":"SAMPLE-UID","deviceType":"Thunderbolt Device"}],
          "usbDevices": [
            {"name":"Sample Hub","vendor":"Example","vendorId":"05ac","productId":"800b","isRemovable":1,"speed":"480","linkSpeed":"","deviceType":"USB Hub","connectionType":"Removable"},
            {"model":"Unnamed Thing","modelId":"1234"}
          ]
        }
        """;

    [Fact]
    public void NullOrEmpty_HasNoData()
    {
        Assert.False(new MacPeripheralsInfo(null).HasData);
        Assert.False(Read("{}").HasData);
        Assert.True(Read(MacPayload).HasData);
    }

    [Fact]
    public void SingletonArray_ReadsAsItsElement()
    {
        var p = Read("""[{"scanners":[{"name":"Sample Scanner","scannerType":"Flatbed"}]}]""");
        Assert.True(p.HasData);
        Assert.Equal("Flatbed", Assert.Single(p.Scanners).ScannerType);
    }

    [Fact]
    public void Audio_SplitsIntoOutputsAndMicrophones()
    {
        var p = Read(MacPayload);
        var output = Assert.Single(p.AudioOutputs);
        Assert.True(output.IsDefault);
        Assert.False(output.IsBuiltIn);
        var mic = Assert.Single(p.Microphones);
        Assert.Equal("Sample Mic", mic.Name);
        Assert.True(mic.IsBuiltIn);
    }

    [Fact]
    public void Bluetooth_DropsPhonesAndComputers()
    {
        var p = Read(MacPayload);
        Assert.Equal(["Sample Keyboard", "Other Keyboard", "Desk Speaker"], p.BluetoothDevices.Select(b => b.Name));
        Assert.Equal(80, p.BluetoothDevices[1].BatteryLevel);
        Assert.True(p.BluetoothDevices[1].IsConnected);
        Assert.Equal(3, p.Count("bluetooth"));
    }

    [Fact]
    public void NestedInput_ReadsEachList_AndAddsBluetoothKeyboardsOnce()
    {
        var p = Read(MacPayload);
        Assert.Equal(["Sample Keyboard", "Other Keyboard"], p.Keyboards.Select(k => k.Name));
        Assert.Equal("004C", p.Keyboards[0].VendorId);
        Assert.Equal("Bluetooth", p.Keyboards[1].ConnectionType);
        Assert.False(p.Keyboards[1].IsBuiltIn);
        var trackpad = Assert.Single(p.Trackpads);
        Assert.True(trackpad.SupportsForceTouch);
        Assert.Equal(3, p.InputCount);
    }

    [Fact]
    public void FlatInput_IsGroupedByType()
    {
        var p = Read("""
            {"inputDevices":[
              {"name":"Kb","deviceType":"Keyboard"},{"name":"Ms","deviceType":"Mouse"},
              {"name":"Tp","deviceType":"Touchpad"},{"name":"Pen","deviceType":"Pen Tablet","tabletType":"Pen Display"},
              {"name":"Other","deviceType":"Gamepad"}]}
            """);
        Assert.Equal("Kb", Assert.Single(p.Keyboards).Name);
        Assert.Equal("Ms", Assert.Single(p.Mice).Name);
        Assert.Equal("Tp", Assert.Single(p.Trackpads).Name);
        Assert.Equal("Pen Display", Assert.Single(p.Tablets).TabletType);
        Assert.Equal(4, p.InputCount);
    }

    [Fact]
    public void Printers_DefaultFirst_WithFiltersAndReasons()
    {
        var p = Read(MacPayload);
        Assert.Equal(["Sample Printer", "Hall Printer"], p.Printers.Select(x => x.Name));
        var sample = p.Printers[0];
        Assert.True(sample.IsDefault);
        Assert.Equal(["toner-low"], sample.StateReasons);
        var filter = Assert.Single(sample.CupsFilters);
        Assert.Equal(("sampletops", "1.0"), (filter.Name, filter.Version));
        Assert.Equal("Sample PPD", sample.Ppd);
        Assert.Equal("Example", p.Printers[1].Manufacturer);
        Assert.Empty(p.Printers[1].StateReasons);
    }

    [Fact]
    public void Usb_FallsBackToModelAndDropsBlanks()
    {
        var p = Read(MacPayload);
        var hub = p.UsbDevices[0];
        Assert.True(hub.IsRemovable);
        Assert.Null(hub.LinkSpeed);
        Assert.Equal("480", hub.Speed);
        Assert.Equal(("Unnamed Thing", "1234"), (p.UsbDevices[1].Name, p.UsbDevices[1].ProductId));
        var tb = Assert.Single(p.ThunderboltDevices);
        Assert.Null(tb.DeviceId);
        Assert.Equal(3, p.Count("usb-thunderbolt"));
    }

    [Theory]
    [InlineData("""{"name":"Backup"}""", "Backup")]
    [InlineData("""{"name":"","mountPoint":"/Volumes/Backup"}""", "Backup")]
    [InlineData("""{"name":"External Storage","mountPoint":"/"}""", "System Drive")]
    [InlineData("""{"name":""}""", "External Storage")]
    public void Storage_DisplayName(string item, string expected) =>
        Assert.Equal(expected, Assert.Single(Read($$"""{"externalStorage":[{{item}}]}""").ExternalStorageDevices).DisplayName);

    [Fact]
    public void Counts_FollowTheCategories()
    {
        var p = Read(MacPayload);
        Assert.Equal(8, MacPeripheralsInfo.Categories.Count);
        Assert.Equal(1, p.Count("storage"));
        Assert.Equal(1, p.Count("audio"));
        Assert.Equal(1, p.Count("microphones"));
        Assert.Equal(2, p.Count("printers"));
        Assert.Equal(0, p.Count("scanners"));
    }
}
