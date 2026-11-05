using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// One row of a module tab report, read tolerantly: a missing field, a field of the
/// wrong type and an explicit null all read as "not reported" (null), and fields this
/// version does not know are ignored. Never a default that would read as healthy.
/// </summary>
public sealed class TabRow
{
    public TabRow(JsonElement element, TabReportData? report = null)
    {
        Element = element.ValueKind == JsonValueKind.Undefined ? default : element.Clone();
        Report = report;
    }

    public JsonElement Element { get; }

    /// <summary>The report the row came from, for values measured against its snapshot time.</summary>
    public TabReportData? Report { get; internal set; }

    public string Serial => Str("serialNumber") ?? "";
    public string Kind => Str("kind") ?? "";
    public string? Platform => Str("platform");

    /// <summary>The device's display name, falling back to the serial as the web does.</summary>
    public string DeviceName => Str("deviceName") ?? Str("computerName") ?? Serial;

    /// <summary>A dotted path into the row, e.g. <c>reported.laps</c>.</summary>
    public JsonElement? Node(string path)
    {
        if (Element.ValueKind != JsonValueKind.Object) return null;
        var current = Element;
        foreach (var name in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next)) return null;
            current = next;
        }
        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }

    public string? Str(string path) => Node(path) is { } e ? TabJson.Text(e) : null;

    public bool? Bool(string path) => Node(path) is { } e ? TabJson.Bool(e) : null;

    public double? Num(string path) => Node(path) is { } e ? TabJson.Number(e) : null;

    public int? Int(string path) => Num(path) is { } d && d is >= int.MinValue and <= int.MaxValue ? (int)Math.Round(d) : null;

    public DateTime? Time(string path) => TabJson.Time(Str(path));

    /// <summary>Every string in an array field; an absent or malformed field is an empty list.</summary>
    public IReadOnlyList<string> Strs(string path) =>
        Node(path) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(TabJson.Text).Where(s => s is { Length: > 0 }).Select(s => s!).ToList()
            : [];

    /// <summary>The objects in an array field, as rows of their own.</summary>
    public IReadOnlyList<TabRow> Items(string path) =>
        Node(path) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => new TabRow(e, Report)).ToList()
            : [];
}

/// <summary>The tolerant scalar readers behind <see cref="TabRow"/>.</summary>
public static class TabJson
{
    public static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() is { Length: > 0 } s ? s : null,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static bool? Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetDouble(out var d) ? d != 0 : null,
        JsonValueKind.String => e.GetString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => null,
        },
        _ => null,
    };

    public static double? Number(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetDouble(out var d) ? d : null,
        JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null,
    };

    public static DateTime? Time(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    public static JsonElement? Prop(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v
            : null;

    public static IEnumerable<JsonElement> Array(JsonElement? e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    public static string? Str(JsonElement? e, string name) => Prop(e, name) is { } v ? Text(v) : null;

    public static int? Int(JsonElement? e, string name) =>
        Prop(e, name) is { } v && Number(v) is { } d && d is >= int.MinValue and <= int.MaxValue ? (int)Math.Round(d) : null;
}
