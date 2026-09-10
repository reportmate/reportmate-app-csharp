using System.IO;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Turns the per-device installs payload into one row per managed item.
/// </summary>
/// <remarks>
/// The flat /api/v1/installs endpoint returns managed items grouped by device and
/// caps at 5,000 rows, so a request for the fleet came back holding every item from
/// the first fifty machines and nothing from the other eight hundred and thirty. The
/// page said "first 5,000 managed items, from 50 devices" and was honest about it,
/// but a status chart drawn over fifty machines is not a fleet status chart no
/// matter what the caption says.
///
/// /api/v1/installs/full returns one entry per device for the whole fleet, and the
/// items nested inside it: 42,733 items across the 387 devices that run Cimian, in a
/// single request. It costs about 21 seconds against the flat endpoint's 6, which is
/// the right trade for going from 6% of the fleet to all of it.
///
/// Paging the flat endpoint instead would take nine requests and about 50 seconds to
/// reach the same place.
/// </remarks>
public static class Installs
{
    /// <summary>Where the fleet-wide payload lives.</summary>
    public const string Endpoint = "/api/v1/installs/full?limit=5000";

    /// <summary>
    /// Flattens the payload to item rows carrying the device fields the report
    /// shows. The returned document owns every row, so the caller has to hold it
    /// for as long as it holds the rows.
    ///
    /// Only the properties in <paramref name="keep"/> are carried across. An item
    /// has 29 fields and the report displays ten; copying the rest builds a
    /// document nothing can read from.
    /// </summary>
    public static (JsonDocument Owner, List<JsonElement> Rows) Flatten(
        JsonDocument payload, IReadOnlySet<string> keep)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            if (payload.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var device in payload.RootElement.EnumerateArray())
                    WriteDeviceItems(w, device, keep);
            w.WriteEndArray();
        }

        buffer.Position = 0;
        var owner = JsonDocument.Parse(buffer);
        return (owner, owner.RootElement.EnumerateArray().ToList());
    }

    private static void WriteDeviceItems(Utf8JsonWriter w, JsonElement device, IReadOnlySet<string> keep)
    {
        var items = Path(device, "modules", "installs", "cimian", "items");
        if (items is not { ValueKind: JsonValueKind.Array } list) return;

        var inventory = Path(device, "modules", "inventory");

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            w.WriteStartObject();

            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!keep.Contains(property.Name)) continue;
                own.Add(property.Name);
                property.WriteTo(w);
            }

            // The device is the same for every item under it, so its fields are
            // copied onto each row rather than looked up again while rendering.
            Copy(w, own, device, "deviceName");
            Copy(w, own, device, "serialNumber");
            Copy(w, own, device, "lastSeen");
            if (inventory is { } inv)
            {
                Copy(w, own, inv, "catalog");
                Copy(w, own, inv, "location");
                Copy(w, own, inv, "assetTag");
                Copy(w, own, inv, "usage");
            }

            // Only Cimian reports here, so every row is a Windows row. The device
            // entries carry no platform of their own in this payload, and leaving
            // it absent would let a platform chart read as though the fleet's Macs
            // had simply answered nothing.
            if (own.Add("platform")) w.WriteString("platform", "Windows");
            w.WriteEndObject();
        }
    }

    private static void Copy(Utf8JsonWriter w, HashSet<string> own, JsonElement source, string name)
    {
        if (own.Contains(name)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        w.WritePropertyName(name);
        value.WriteTo(w);
    }

    private static JsonElement? Path(JsonElement start, params string[] steps)
    {
        var cur = start;
        foreach (var step in steps)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(step, out var next))
                return null;
            cur = next;
        }
        return cur;
    }
}
