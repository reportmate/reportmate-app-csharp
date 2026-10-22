// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static ReportMate.App.Services.MacModules.MacJson;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A Mac's peripherals module, read the way the Mac app's PeripheralsInfo reads it
/// (itself a port of the web tab): the Mac's nested input object or the Windows
/// flat list regrouped by type, audio split into outputs and microphones, and
/// Bluetooth pruned to real peripherals. Displays are not here: the web and Mac
/// apps show them on the Hardware tab.
/// </summary>
public sealed class MacPeripheralsInfo
{
    public sealed record UsbDevice(string Name, string? Vendor, string? VendorId, string? ProductId, string? SerialNumber,
        string? Speed, string? LinkSpeed, string? LocationId, string? PowerAllocated, string? UsbVersion,
        bool? IsRemovable, string? DeviceType, string? ConnectionType);

    public sealed record InputDevice(string Name, string? Vendor, string? VendorId, string? ProductId, string? SerialNumber,
        bool? IsBuiltIn, string? ConnectionType, string? DeviceType, bool? SupportsForceTouch, string? TabletType);

    public sealed record AudioDevice(string Name, string? Manufacturer, string? Type, bool IsDefault, bool IsInput, bool IsOutput,
        bool? IsBuiltIn, string? ConnectionType);

    public sealed record BluetoothDevice(string Name, string? Address, bool IsConnected, bool IsPaired,
        string? DeviceType, string? DeviceCategory, int? BatteryLevel)
    {
        /// <summary>HomePods, phones, watches and other Macs are not peripherals.</summary>
        public bool IsPeripheral
        {
            get
            {
                var n = Name.ToLowerInvariant();
                var c = (DeviceCategory ?? "").ToLowerInvariant();
                string[] words = ["homepod", "apple watch", "iphone", "ipad", "macbook", "imac", "mac mini", "mac pro", "mac studio"];
                if (words.Any(n.Contains)) return false;
                return c is not ("computer" or "phone" or "tablet" or "watch");
            }
        }

        public bool IsKeyboard => DeviceType == "Keyboard" || DeviceCategory == "Keyboard";
    }

    public sealed record ThunderboltDevice(string Name, string? Vendor, string? DeviceId, string? Uid, string? DeviceType);

    public sealed record CupsFilter(string Name, string? Path, string? Version);

    public sealed record Printer(int Index, string Name, string? Uri, string? ConnectionType, string? Status,
        IReadOnlyList<string> StateReasons, bool IsDefault, string? Manufacturer, string? Model, string? Identifier,
        string? Ppd, string? CupsVersion, string? ScanningSupport, string? FaxSupport, IReadOnlyList<CupsFilter> CupsFilters);

    public sealed record Scanner(string Name, string? Manufacturer, string? ConnectionType, string? Status, string? ScannerType);

    public sealed record ExternalStorage(string Name, string? DevicePath, string? MountPoint, string? FileSystem,
        string? TotalSize, string? Protocol, string? StorageType)
    {
        /// <summary>The volume name, else the last mount-point segment (the web's getStorageName).</summary>
        public string DisplayName
        {
            get
            {
                if (Name.Length > 0 && Name != "External Storage") return Name;
                if (MountPoint is { } mp)
                {
                    if (mp == "/") return "System Drive";
                    return mp.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "External Storage";
                }
                return "External Storage";
            }
        }
    }

    /// <summary>The filter categories, in the web's order: id, label.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Categories =
    [
        ("storage", "External Storage"),
        ("usb-thunderbolt", "USB & Thunderbolt"),
        ("audio", "Audio"),
        ("input", "Input Devices"),
        ("printers", "Printers"),
        ("scanners", "Scanners"),
        ("microphones", "Microphones"),
        ("bluetooth", "Bluetooth"),
    ];

    public const string PenInputLabel = "Pen Input";

    public bool HasData { get; }
    public IReadOnlyList<UsbDevice> UsbDevices { get; }
    public IReadOnlyList<ThunderboltDevice> ThunderboltDevices { get; }
    public IReadOnlyList<InputDevice> Keyboards { get; }
    public IReadOnlyList<InputDevice> Mice { get; }
    public IReadOnlyList<InputDevice> Trackpads { get; }
    public IReadOnlyList<InputDevice> Tablets { get; }
    public IReadOnlyList<AudioDevice> AudioOutputs { get; }
    public IReadOnlyList<AudioDevice> Microphones { get; }
    public IReadOnlyList<BluetoothDevice> BluetoothDevices { get; }
    public IReadOnlyList<Printer> Printers { get; }
    public IReadOnlyList<Scanner> Scanners { get; }
    public IReadOnlyList<ExternalStorage> ExternalStorageDevices { get; }

    public int InputCount => Keyboards.Count + Mice.Count + Trackpads.Count + Tablets.Count;

    /// <summary>Reads the peripherals module as sent; null or empty reads as no data.</summary>
    public MacPeripheralsInfo(JsonElement? module)
    {
        var raw = Unwrap(module);
        HasData = raw is { } r && r.ValueKind switch
        {
            JsonValueKind.Object => r.EnumerateObject().Any(),
            JsonValueKind.Array => r.GetArrayLength() > 0,
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };

        UsbDevices = Arr(raw, "usbDevices").Select(ReadUsb).ToList();
        ThunderboltDevices = Arr(raw, "thunderboltDevices").Select(d => new ThunderboltDevice(
            Str(d, "name") ?? "Thunderbolt Device", Str(d, "vendor"), Str(d, "deviceId"), Str(d, "uid"), Str(d, "deviceType"))).ToList();

        var allBluetooth = Arr(raw, "bluetoothDevices").Select(d => new BluetoothDevice(
            Str(d, "name") ?? "", Str(d, "address"), Bool(d, "isConnected") ?? false, Bool(d, "isPaired") ?? false,
            Str(d, "deviceType"), Str(d, "deviceCategory"), Int(d, "batteryLevel"))).ToList();
        BluetoothDevices = allBluetooth.Where(b => b.IsPeripheral).ToList();

        // Mac: a nested object of lists. Windows: one flat list tagged by deviceType.
        var input = Node(raw, "inputDevices");
        List<InputDevice> kb, mi, tp, tb;
        if (input is { ValueKind: JsonValueKind.Array } flat)
        {
            var all = flat.EnumerateArray().Select(ReadInput).ToList();
            List<InputDevice> Of(params string[] types) => all.Where(d => types.Contains((d.DeviceType ?? "").ToLowerInvariant())).ToList();
            kb = Of("keyboard"); mi = Of("mouse"); tp = Of("trackpad", "touchpad"); tb = Of("graphics tablet", "tablet", "pen display", "pen tablet");
        }
        else
        {
            kb = Arr(input, "keyboards").Select(ReadInput).ToList();
            mi = Arr(input, "mice").Select(ReadInput).ToList();
            tp = Arr(input, "trackpads").Select(ReadInput).ToList();
            tb = Arr(input, "tablets").Select(ReadInput).ToList();
        }
        // Bluetooth keyboards join the keyboard list. The web and Mac apps add every
        // one; a Mac also reports a paired keyboard under inputDevices, so one already
        // listed by name is not added twice.
        var listed = new HashSet<string>(kb.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
        kb.AddRange(allBluetooth.Where(b => b.IsKeyboard && !listed.Contains(b.Name))
            .Select(b => new InputDevice(b.Name, null, null, null, null, false, "Bluetooth", "Keyboard", null, null)));
        Keyboards = kb; Mice = mi; Trackpads = tp; Tablets = tb;

        var audio = Arr(raw, "audioDevices").Select(d => new AudioDevice(
            Str(d, "name") ?? "", Str(d, "manufacturer"), Str(d, "type"),
            Bool(d, "isDefault") ?? false, Bool(d, "isInput") ?? false, Bool(d, "isOutput") ?? false,
            Bool(d, "isBuiltIn"), Str(d, "connectionType"))).ToList();
        AudioOutputs = audio.Where(a => a.IsOutput || a.Type == "Output").ToList();
        Microphones = audio.Where(a => a.IsInput || a.Type == "Input").ToList();

        Printers = Arr(raw, "printers").Select(ReadPrinter)
            .OrderBy(p => p.IsDefault ? 0 : 1).ThenBy(p => p.Index).ToList();
        Scanners = Arr(raw, "scanners").Select(d => new Scanner(
            Str(d, "name") ?? "Scanner", Str(d, "manufacturer"), Str(d, "connectionType"), Str(d, "status"), Str(d, "scannerType"))).ToList();
        ExternalStorageDevices = Arr(raw, "externalStorage").Select(d => new ExternalStorage(
            Str(d, "name") ?? "", Str(d, "devicePath"), Str(d, "mountPoint"), Str(d, "fileSystem"),
            Str(d, "totalSize"), Str(d, "protocol"), Str(d, "storageType"))).ToList();
    }

    public int Count(string category) => category switch
    {
        "storage" => ExternalStorageDevices.Count,
        "usb-thunderbolt" => UsbDevices.Count + ThunderboltDevices.Count,
        "audio" => AudioOutputs.Count,
        "input" => InputCount,
        "printers" => Printers.Count,
        "scanners" => Scanners.Count,
        "microphones" => Microphones.Count,
        "bluetooth" => BluetoothDevices.Count,
        _ => 0,
    };

    /// <summary>A module sent as a one-element array reads as that element.</summary>
    private static JsonElement? Unwrap(JsonElement? module) =>
        module is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 1 ? a[0] : module;

    private static UsbDevice ReadUsb(JsonElement d) => new(
        Str(d, "name") ?? Str(d, "model") ?? "Unknown USB Device",
        Str(d, "vendor"), Str(d, "vendorId"), Str(d, "productId") ?? Str(d, "modelId"), Str(d, "serialNumber"),
        Str(d, "speed"), Str(d, "linkSpeed"), Str(d, "locationId"), Str(d, "powerAllocated"), Str(d, "usbVersion"),
        Bool(d, "isRemovable"), Str(d, "deviceType"), Str(d, "connectionType"));

    private static InputDevice ReadInput(JsonElement d) => new(
        Str(d, "name") ?? "", Str(d, "vendor"), Str(d, "vendorId"), Str(d, "productId") ?? Str(d, "modelId"),
        Str(d, "serialNumber"), Bool(d, "isBuiltIn"), Str(d, "connectionType"), Str(d, "deviceType"),
        // The Mac client spells it "supportsForcTouch".
        Bool(d, "supportsForcTouch") ?? Bool(d, "supportsForceTouch"), Str(d, "tabletType"));

    private static Printer ReadPrinter(JsonElement d, int index) => new(
        index, Str(d, "name") ?? "", Str(d, "uri"), Str(d, "connectionType"), Str(d, "status"),
        Arr(d, "stateReasons").Where(r => r.ValueKind == JsonValueKind.String)
            .Select(r => r.GetString() ?? "").Where(r => r.Length > 0 && r != "none").ToList(),
        Bool(d, "isDefault") ?? false, Str(d, "manufacturer") ?? Str(d, "make"), Str(d, "model"), Str(d, "identifier"),
        Str(d, "ppd"), Str(d, "cupsVersion"), Str(d, "scanningSupport"), Str(d, "faxSupport"),
        Arr(d, "cupsFilters").Select(f => Str(f, "name") is { } n ? new CupsFilter(n, Str(f, "path"), Str(f, "version")) : null)
            .OfType<CupsFilter>().ToList());
}
