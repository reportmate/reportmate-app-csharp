using System.Text.Json;
using System.Text.RegularExpressions;

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
}
