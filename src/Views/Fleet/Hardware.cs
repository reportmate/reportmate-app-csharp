using System.Text.Json;
using System.Text.RegularExpressions;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>Derived hardware values.</summary>
public static class Hardware
{
    /// <summary>
    /// The device's CPU architecture as one of ARM64, x64 or x86.
    ///
    /// The clients spell one architecture three ways -- "arm64" on a Mac,
    /// "64-bit" and "ARM 64-bit Processor" on Windows -- so the raw field charts
    /// as categories that are really vocabularies. The web report normalises this
    /// too, but its ladder tests for "64-bit" before it tests for ARM, so all 23
    /// Snapdragon X Elite machines whose architecture reads "ARM 64-bit Processor"
    /// are counted as x64 there, and the four Apple M-series machines that report
    /// no architecture at all fall out as Unknown. The web's guard against exactly
    /// that -- an ARM64 override taken from the processor name -- never fires,
    /// because it stringifies the processor object and compares "[object Object]".
    ///
    /// So this reads the processor and graphics names properly and tests ARM
    /// before x64: ARM64 512, x64 371, nothing unknown, against 485/394/4.
    /// </summary>
    public static string? Normalize(JsonElement row)
    {
        var processor = Name(row, "processor");
        var graphics = Name(row, "graphics");
        if (Mentions(processor, "snapdragon", "apple m", "apple silicon")
            || AppleSilicon.IsMatch(processor)
            || Mentions(graphics, "qualcomm adreno", "apple gpu", "apple m"))
            return "ARM64";

        var arch = Text(row, "architecture").Trim();
        if (arch.Length == 0) return null;

        var n = arch.ToLowerInvariant();
        if (Mentions(n, "arm64", "aarch64")) return "ARM64";
        // Before the x64 test, not after it: "ARM 64-bit Processor" satisfies both.
        if (n.Contains("arm") && n.Contains("64")) return "ARM64";
        if (Mentions(n, "x86_64", "x64", "amd64", "64-bit")) return "x64";
        if (n.Contains("x86")) return "x86";
        if (n.Contains("ia64")) return "IA64";
        return arch;
    }

    /// <summary>
    /// A bare M-number, which is how a Mac names its processor: "M1", "M2 Pro",
    /// "M4 Max". Anchored, because a loose "m1" would match inside other names.
    /// It covers all 489 Apple Silicon machines in the fleet and no Intel or AMD
    /// part, and it is the only signal for the three that report no graphics name.
    /// </summary>
    private static readonly Regex AppleSilicon = new(@"^M\d", RegexOptions.IgnoreCase);

    private static bool Mentions(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Name(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(property, out var o)
        && o.ValueKind == JsonValueKind.Object
            ? Text(o, "name")
            : "";

    private static string Text(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>
    /// Internal storage as "free of total", summed across a device's internal
    /// disks.
    /// </summary>
    /// <remarks>
    /// Free space is reported by every device -- 883 of 883 rows carry a positive
    /// freeSpace -- and no report showed it. The web's fleet hardware page has a
    /// column for it that never fills, because the payload writes freeSpace and the
    /// page reads free and available, neither of which either client emits.
    ///
    /// External disks are excluded: a device with a 4 TB drive plugged in is not a
    /// device with 4 TB of headroom, and the question this answers is whether the
    /// machine is about to run out.
    /// </remarks>
    public static string? Storage(JsonElement row) =>
        StorageBytes(row) is { Total: > 0 } s ? $"{Format.Bytes(s.Free)} free of {Format.Bytes(s.Total)}" : null;

    /// <summary>
    /// Internal capacity and free space in bytes, summed across the device's
    /// internal disks; zero total when it reports none. The dashboard's low-storage
    /// count and the report's storage column both read this, so they agree.
    /// </summary>
    public static (long Total, long Free) StorageBytes(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("storage", out var disks)
            || disks.ValueKind != JsonValueKind.Array) return (0, 0);

        long total = 0, free = 0;
        foreach (var disk in disks.EnumerateArray())
        {
            if (disk.ValueKind != JsonValueKind.Object) continue;
            // Absent means internal: the Macs omit the flag on the built-in disk.
            if (disk.TryGetProperty("isInternal", out var internalFlag)
                && internalFlag.ValueKind == JsonValueKind.False) continue;

            var capacity = Bytes(disk, "capacity");
            var available = Bytes(disk, "freeSpace");
            if (capacity <= 0 || available <= 0) continue;
            total += capacity;
            free += available;
        }
        return (total, free);
    }

    /// <summary>
    /// Physical memory in bytes, or zero. Windows calls it memory.totalPhysical and
    /// a Mac memory.physical_memory, so reading one name covers half the fleet.
    /// </summary>
    public static double MemoryBytes(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("memory", out var memory)) return 0;
        if (memory.ValueKind == JsonValueKind.Number) return memory.GetDouble();
        if (memory.ValueKind != JsonValueKind.Object) return 0;
        foreach (var name in new[] { "totalPhysical", "physical_memory", "physicalMemory" })
        {
            if (!memory.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        }
        return 0;
    }

    private static readonly string[] DesktopWords =
        ["desktop", "optiplex", "precision", "workstation", "tower", "sff", "imac", "mac pro", "mac studio"];

    private static readonly string[] LaptopWords =
        ["laptop", "book", "thinkpad", "macbook", "pavilion", "inspiron", "latitude", "elitebook", "probook", "yoga", "ideapad", "surface laptop"];

    /// <summary>
    /// Whether the model names a laptop. Desktop words are tested first, as the web
    /// and the Mac app do, because "Precision" names a tower line as well as a laptop one, and the Mac
    /// app counts it as a desktop.
    /// </summary>
    public static bool IsLaptop(JsonElement row)
    {
        var model = Text(row, "model");
        if (model.Length == 0) return false;
        if (Mentions(model, DesktopWords)) return false;
        return Mentions(model, LaptopWords);
    }

    private static long Bytes(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : 0;
}
