using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>How a value should read once it is out of the JSON.</summary>
public enum ValueFormat
{
    Text,
    Bytes,
    Megabytes,
    Count,
    RelativeTime,
}

/// <summary>
/// One addressable value in a report row. Paths are dotted and may cross an array
/// with <c>[]</c>, e.g. <c>storage[].type</c>, which yields every element's value.
/// </summary>
/// <param name="Platform">
/// Restricts the field to one platform's devices. Several fields exist only on
/// Windows and the API reports them as false on a Mac rather than omitting them, so
/// counting every device turns "does not apply here" into "switched off" -- which on
/// a security report is the difference between antivirus being enabled on almost
/// every Windows machine and appearing to be missing from half the fleet.
/// </param>
/// <param name="Derive">
/// Computes the value from the whole row instead of reading one path. Some
/// measurements do not exist as a field: the two clients answer "how was this
/// enrolled" in different vocabularies, and the answer for a Windows device is
/// partly in a different field again, so the honest value has to be derived the
/// way the web report derives it.
/// </param>
public sealed record Field(
    string Label,
    string Path,
    ValueFormat Format = ValueFormat.Text,
    string? Platform = null,
    Func<JsonElement, string?>? Derive = null)
{
    /// <summary>
    /// The paths to try, in order. The two clients name the same measurement
    /// differently -- physical memory is memory.totalPhysical on Windows and
    /// memory.physical_memory on a Mac -- so a field that reads only one of them
    /// silently covers one platform while the page says it covers the fleet.
    /// </summary>
    private string[] Paths => Path.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The value as a display string, or empty when no path resolves.</summary>
    public string Read(JsonElement row) => Derive is not null
        ? Derive(row) ?? ""
        : FirstValue(row) is { } v ? Format switch
    {
        ValueFormat.Bytes => Json.Bytes(v),
        ValueFormat.Megabytes => Json.Megabytes(v),
        _ => Json.Text(v),
    } : "";

    private JsonElement? FirstValue(JsonElement row)
    {
        foreach (var path in Paths)
            if (Json.ReadOne(row, path) is { } v) return v;
        return null;
    }

    /// <summary>Every value at this path — one for a scalar, many across an array.</summary>
    public IEnumerable<string> ReadAll(JsonElement row) =>
        Derive is not null
            ? (Derive(row) is { Length: > 0 } d ? [d] : Array.Empty<string>())
            : Paths.Select(p => Json.ReadMany(row, p).ToList()).FirstOrDefault(v => v.Count > 0)
            ?.Select(v => Format switch
        {
            ValueFormat.Bytes => Json.Bytes(v),
            ValueFormat.Megabytes => Json.Megabytes(v),
            _ => Json.Text(v),
        }).Where(s => !string.IsNullOrWhiteSpace(s)) ?? [];
}

/// <summary>
/// A query key a link can carry, bound to the field it narrows. Plural keys hold a
/// comma-separated list and a row matches any of them, which is how the web report
/// pages carry their multi-select filters.
/// </summary>
public sealed record LinkFilter(string Key, string Path, bool MultiValue = false);

/// <summary>A table column in a report.</summary>
public sealed record ReportColumn(string Header, Field Field, double? Width = null, bool Mono = false, bool Star = false);

/// <summary>
/// A whole fleet report: the charted distributions across the top and the table
/// underneath, matching how the web report pages are laid out.
/// </summary>
/// <param name="RowNoun">
/// What one row is. Most reports are a row per device, but the applications and
/// installs reports are a row per installed item, so calling those rows "devices"
/// would misstate the fleet by a factor of a hundred.
/// </param>
/// <param name="Limit">
/// Rows to request. The item-level reports run to six figures fleet-wide, which is
/// more than a table should pull down or a reader should scroll.
/// </param>
public sealed record ReportSpec(
    string Module,
    IReadOnlyList<Field> Distributions,
    IReadOnlyList<ReportColumn> Columns,
    string RowNoun = "devices",
    int? Limit = null,
    IReadOnlyList<LinkFilter>? LinkFilters = null)
{
    /// <summary>The link keys this report narrows by, empty when it takes none.</summary>
    public IReadOnlyList<LinkFilter> Filters => LinkFilters ?? [];

    public static ReportSpec? For(string module) => All.GetValueOrDefault(module);

    // Paths below are the report endpoints' own row shape, which is flattened and
    // NOT the nested per-device module JSON. They were read off the live responses;
    // guessing from the module shapes produced columns that were silently blank.
    private static readonly Dictionary<string, ReportSpec> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hardware"] = new("hardware",
            [
                new("Manufacturer", "manufacturer"),
                new("Model", "model"),
                new("Architecture", "", Derive: Hardware.Normalize),
                new("Processor", "processor.name"),
                new("Graphics", "graphics.name"),
                new("Memory", "memory.totalPhysical|memory.physical_memory", ValueFormat.Bytes),
                new("Storage type", "storage[].type"),
                // Displays are charted from the hardware module, not peripherals:
                // this is the only copy the Macs populate. Resolution and type both
                // read on either client; the manufacturer and model of a display
                // are Windows-mostly, so they are not charted.
                new("Display", "displays[].resolution"),
                new("Display type", "displays[].type"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Asset Tag", new("Asset Tag", "assetTag"), 110, Mono: true),
                new("Model", new("Model", "model"), 200),
                new("Processor", new("Processor", "processor.name"), 200),
                new("Memory", new("Memory", "memory.totalPhysical|memory.physical_memory", ValueFormat.Bytes), 100),
                new("Graphics", new("Graphics", "graphics.name"), 170),
                new("Displays", new("Displays", "displays[].name"), 190),
                new("Architecture", new("Architecture", "", Derive: Hardware.Normalize), 150),
            ]),

        ["system"] = new("system",
            [
                new("Operating system", "operatingSystem"),
                new("Version", "osVersion"),
                new("Display version", "displayVersion"),
                new("Edition", "edition", Platform: "Windows"),
                new("Architecture", "architecture"),
                new("Activation", "activationStatus"),
                new("Locale", "locale"),
                new("Time zone", "timeZone"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("OS", new("OS", "operatingSystem"), 150),
                new("Version", new("Version", "displayVersion"), 110),
                new("Build", new("Build", "buildNumber"), 110),
                new("Edition", new("Edition", "edition"), 150),
                new("Uptime", new("Uptime", "uptimeString"), 120),
                new("Pending", new("Pending", "pendingUpdatesCount"), 90),
            ], LinkFilters:
            [
                new("osVersion", "osVersion"),
                new("edition", "edition"),
                new("architecture", "architecture"),
            ]),

        ["security"] = new("security",
            [
                // Antivirus, TPM, Secure Boot, tamper protection and Smart App
                // Control are Windows-only, and the API reports them as false on a
                // Mac rather than leaving them out.
                // Counted across the whole fleet they read as failures: antivirus
                // showed as absent on 56% of devices when it is enabled on 394 of
                // 395 Windows machines, and TPM as missing on 56% when every
                // Windows machine has one.
                new("Antivirus", "antivirusName", Platform: "Windows"),
                new("Antivirus enabled", "antivirusEnabled", Platform: "Windows"),
                new("Encryption", "encryptionEnabled"),
                // Firewall stays fleet-wide. macOS reports it for real -- eight Macs
                // have the application firewall on -- so scoping it to Windows would
                // hide that it is off on nearly every Mac, which is a finding rather
                // than a field that does not apply.
                new("Firewall", "firewallEnabled"),
                new("TPM present", "tpmPresent", Platform: "Windows"),
                new("Secure Boot", "secureBootEnabled", Platform: "Windows"),
                new("Tamper protection", "tamperProtected", Platform: "Windows"),
                new("Smart App Control", "smartAppControlState", Platform: "Windows"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Antivirus", new("Antivirus", "antivirusName"), 160),
                new("Encrypted", new("Encrypted", "encryptionEnabled"), 100),
                new("Firewall", new("Firewall", "firewallEnabled"), 95),
                new("Secure Boot", new("Secure Boot", "secureBootEnabled"), 110),
                new("Threats", new("Threats", "activeThreatCount"), 90),
                new("Critical CVEs", new("Critical CVEs", "criticalCveCount"), 110),
            ]),

        ["network"] = new("network",
            [
                new("Connection", "", Derive: Network.ConnectionKind),
                // No interface distribution. The Windows client puts the interface
                // INDEX in primaryInterface, friendlyName and interfaceName alike, so
                // the chart read "9 - 9%, 10 - 8%, 7 - 8%" and said nothing; macOS
                // reports no primaryInterface at all. There is no interface name in
                // this payload to chart.
                new("DNS server", "raw.dns.servers[]"),
                new("Wi-Fi SSID", "raw.activeConnection.activeWifiSsid"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Hostname", new("Hostname", "raw.hostname"), 180, Mono: true),
                new("IP Address", new("IP Address", "raw.activeConnection.ipAddress"), 140, Mono: true),
                new("MAC", new("MAC", "raw.activeConnection.macAddress"), 150, Mono: true),
                new("Connection", new("Connection", "", Derive: Network.ConnectionKind), 120),
                new("Gateway", new("Gateway", "raw.activeConnection.gateway"), 140, Mono: true),
            ]),

        ["identity"] = new("identity",
            [
                new("Domain joined", "directoryServices.activeDirectory.isDomainJoined"),
                new("Entra joined", "directoryServices.azureAd.joined"),
                new("Workgroup", "directoryServices.workgroup"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Users", new("Users", "summary.totalUsers"), 85),
                new("Admins", new("Admins", "summary.adminUsers"), 85),
                new("Disabled", new("Disabled", "summary.disabledUsers"), 90),
                new("Groups", new("Groups", "summary.groupCount"), 85),
                new("Logged in", new("Logged in", "summary.currentlyLoggedIn"), 100),
            ]),

        ["management"] = new("management",
            [
                new("Provider", "provider"),
                // No separate "Enrolled" chart. isEnrolled and enrollmentStatus
                // partition the fleet identically -- true/false against
                // Enrolled/Not Enrolled, 878 and 10 either way -- so charting both
                // put two bars side by side saying one thing twice. The web report
                // charts the status and uses the boolean only as a field.
                new("Enrollment status", "enrollmentStatus"),
                new("Bootstrap method", "", Derive: Management.BootstrapMethod),
                new("Tenant", "tenantName"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Provider", new("Provider", "provider"), 150),
                new("Status", new("Status", "enrollmentStatus"), 140),
                new("Bootstrap", new("Bootstrap", "", Derive: Management.BootstrapMethod), 140),
                new("Tenant", new("Tenant", "tenantName"), 170),
            ]),

        ["applications"] = new("applications",
            [
                new("Publisher", "publisher"),
                new("Category", "category"),
                new("Architecture", "architecture"),
                new("Application", "name"),
            ],
            [
                new("Application", new("Application", "name"), Star: true),
                new("Version", new("Version", "version"), 140),
                new("Publisher", new("Publisher", "publisher"), 190),
                new("Device", new("Device", "deviceName"), 190),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Architecture", new("Architecture", "architecture"), 110),
            ], RowNoun: "installed applications", Limit: 5000, LinkFilters:
            [
                new("apps", "name", MultiValue: true),
                new("publishers", "publisher", MultiValue: true),
                new("versions", "version", MultiValue: true),
                new("usages", "usage", MultiValue: true),
                new("catalogs", "catalog", MultiValue: true),
                new("rooms", "room", MultiValue: true),
                new("areas", "area", MultiValue: true),
                new("fleets", "fleet", MultiValue: true),
            ]),

        ["installs"] = new("installs",
            [
                new("Status", "currentStatus"),
                new("Item", "itemName"),
                new("Catalog", "catalog"),
                new("Platform", "platform"),
            ],
            [
                new("Item", new("Item", "itemName"), Star: true),
                new("Status", new("Status", "currentStatus"), 130),
                new("Installed", new("Installed", "installedVersion"), 150),
                new("Latest", new("Latest", "latestVersion"), 150),
                new("Device", new("Device", "deviceName"), 190),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
            ], RowNoun: "managed items", Limit: 5000, LinkFilters:
            [
                new("filter", "currentStatus"),
                new("items", "itemName", MultiValue: true),
                new("catalogs", "catalog", MultiValue: true),
                new("usages", "usage", MultiValue: true),
                // No fleets filter here: the installs rows carry the key but it is
                // empty on every one, so binding it would make any link using it
                // return nothing and read as missing data.
            ]),

        ["peripherals"] = new("peripherals",
            [
                new("Printer", "printers[].manufacturer"),
                new("USB vendor", "usbDevices[].vendor"),
                new("Audio", "audioDevices[].manufacturer"),
                // No display distribution here. peripherals.displayDevices is empty
                // on all 488 Macs, so a chart over it counts 282 Windows devices
                // while the card says it covers the fleet. The displays those Macs
                // do have are in the hardware module, which is where this report's
                // display charts live. The web's fleet peripherals page reads this
                // same field and shows every Mac with no displays at all.
                // cameras[].name, not manufacturer: the manufacturer is null on all
                // 488 Macs, so charting it counted 348 Windows devices under a card
                // that said it covered the fleet. The name reads on both and is 74
                // distinct values, not hundreds.
                new("Camera", "cameras[].name"),
            ],
            [
                new("Device", new("Device", "deviceName"), Star: true),
                new("Serial", new("Serial", "serialNumber"), 150, Mono: true),
                new("Printers", new("Printers", "printers[].name"), 200),
                new("USB", new("USB", "usbDevices[].name"), 220),
                new("Audio", new("Audio", "audioDevices[].name"), 180),
                new("Cameras", new("Cameras", "cameras[].name"), 170),
            ]),
    };

}

/// <summary>Reading values out of an arbitrary report row by dotted path.</summary>
public static class Json
{
    public static JsonElement? ReadOne(JsonElement root, string path) =>
        ReadMany(root, path).Select(e => (JsonElement?)e).FirstOrDefault();

    /// <summary>
    /// Every value at a dotted path. A <c>[]</c> segment fans out across an array,
    /// so <c>storage[].type</c> yields one value per disk.
    /// </summary>
    public static IEnumerable<JsonElement> ReadMany(JsonElement root, string path)
    {
        IEnumerable<JsonElement> current = [root];
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var fanOut = rawSegment.EndsWith("[]", StringComparison.Ordinal);
            var name = fanOut ? rawSegment[..^2] : rawSegment;
            current = Step(current, name, fanOut).ToList();
            if (!current.Any()) return [];
        }
        return current.Where(e => e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
    }

    private static IEnumerable<JsonElement> Step(IEnumerable<JsonElement> nodes, string name, bool fanOut)
    {
        foreach (var node in nodes)
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!TryGet(node, name, out var next)) continue;

            if (fanOut && next.ValueKind == JsonValueKind.Array)
                foreach (var item in next.EnumerateArray()) yield return item;
            else
                yield return next;
        }
    }

    /// <summary>Property lookup that tolerates the casing differences between modules.</summary>
    private static bool TryGet(JsonElement node, string name, out JsonElement value)
    {
        if (node.TryGetProperty(name, out value)) return true;
        foreach (var property in node.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l.ToString("N0") : e.GetDouble().ToString("0.##"),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Text).Where(s => s.Length > 0)),
        JsonValueKind.Object => "",
        _ => "",
    };

    public static string Bytes(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var bytes) ? Format.Bytes(bytes) : Text(e);

    public static string Megabytes(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var mb) ? Format.Bytes(mb * 1024L * 1024L) : Text(e);
}
