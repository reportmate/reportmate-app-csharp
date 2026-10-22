// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// Reading a Mac's module JSON the way the Mac app's readers do (ReportMateKit's
/// Processing/*Info.swift): the Mac client writes some fields in snake_case and
/// some in camelCase, numbers and flags sometimes as strings, and osquery rows as
/// strings throughout, so every lookup tries each spelling and coerces the value.
/// </summary>
public static class MacJson
{
    /// <summary>The first of several names (or dotted paths) present on the object.</summary>
    public static JsonElement? Node(JsonElement? parent, params string[] paths)
    {
        if (parent is not { } p) return null;
        foreach (var path in paths)
        {
            JsonElement? current = p;
            foreach (var step in path.Split('.'))
            {
                current = Child(current, step);
                if (current is null) break;
            }
            if (current is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } found) return found;
        }
        return null;
    }

    private static JsonElement? Child(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } o) return null;
        if (o.TryGetProperty(name, out var v)) return v;
        var snake = Snake(name);
        foreach (var prop in o.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(prop.Name, snake, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Snake(prop.Name), snake, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        return null;
    }

    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>Text, from a string, number or flag; null when absent or blank.</summary>
    public static string? Str(JsonElement? parent, params string[] paths) => Node(parent, paths) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null,
        { ValueKind: JsonValueKind.Number } v => v.ToString(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    /// <summary>A number, from a JSON number or a numeric string.</summary>
    public static double? Num(JsonElement? parent, params string[] paths) => Node(parent, paths) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static int? Int(JsonElement? parent, params string[] paths) => Num(parent, paths) is { } d ? (int)d : null;

    public static long? Long(JsonElement? parent, params string[] paths) => Num(parent, paths) is { } d ? (long)d : null;

    /// <summary>A flag: true/false, 1/0, "true"/"yes"/"1"/"enabled"/"on" and their opposites.</summary>
    public static bool? Bool(JsonElement? parent, params string[] paths) => Node(parent, paths) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } v => v.GetDouble() != 0,
        { ValueKind: JsonValueKind.String } v => (v.GetString() ?? "").Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "enabled" or "on" => true,
            "false" or "no" or "0" or "disabled" or "off" => false,
            _ => null,
        },
        _ => null,
    };

    /// <summary>The elements of an array, empty when absent.</summary>
    public static IReadOnlyList<JsonElement> Arr(JsonElement? parent, params string[] paths) =>
        Node(parent, paths) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : [];

    public static DateTime? Date(JsonElement? parent, params string[] paths)
    {
        if (Str(parent, paths) is not { } text) return null;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
            return when;
        // Unix seconds, as osquery reports times.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 1e8)
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime;
        return null;
    }
}
