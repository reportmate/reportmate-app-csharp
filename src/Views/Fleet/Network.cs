using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>Derived network values.</summary>
public static class Network
{
    /// <summary>
    /// Whether the device's active connection is Wired or Wireless.
    ///
    /// The raw field carries four labels for two concepts -- Windows says Wired
    /// and Wireless, macOS says Ethernet and WiFi -- so charting it produced five
    /// rows that were really two answers split by platform, and neither half's
    /// number meant anything on its own. The web report never charts this field;
    /// it matches the same substrings this does whenever it filters on wired or
    /// wireless, so these are its own two buckets, just made countable.
    /// </summary>
    public static string? ConnectionKind(JsonElement row)
    {
        var type = Read(row);
        if (type.Length == 0) return null;

        if (Has(type, "wireless", "wifi", "wi-fi", "802.11")) return "Wireless";
        if (Has(type, "wired", "ethernet")) return "Wired";
        // A label neither vocabulary covers is a real answer, not an unknown; the
        // one device reporting "None" has no active connection and should say so.
        return type;
    }

    private static bool Has(string value, params string[] needles) =>
        needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    // Network rows nest their module payload under "raw", unlike the flattened
    // reports, so the walk starts there.
    private static string Read(JsonElement row)
    {
        JsonElement? cur = row;
        foreach (var step in new[] { "raw", "activeConnection", "connectionType" })
        {
            if (cur is not { ValueKind: JsonValueKind.Object } o
                || !o.TryGetProperty(step, out var next)) return "";
            cur = next;
        }
        return cur is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
    }
}
