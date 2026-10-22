// Explicit usings: this file is also compiled into the test project.
using System;
using System.Text.Json;

namespace ReportMate.App.Services;

/// <summary>
/// Reads a JSON number or boolean into a string property. The API types some
/// identifiers as numbers -- an event's id is an integer -- and the default
/// converter throws on those, which failed the whole payload and took the
/// Dashboard and Events pages down over one field neither of them displays.
/// </summary>
public sealed class TolerantStringConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Cannot read a {reader.TokenType} as a string."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

