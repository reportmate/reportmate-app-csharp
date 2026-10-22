using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ReportMate.App.Views.Shared;

/// <summary>
/// A module's JSON as labelled rows and expandable sections, for data the app has
/// no dedicated layout for: every value the device sent, readable, nothing dropped.
/// </summary>
public static class JsonTree
{
    private const int MaxItems = 100;

    public static UIElement Build(JsonElement element, int depth = 0)
    {
        var panel = new StackPanel();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    panel.Children.Add(Entry(Label(p.Name), p.Value, depth));
                if (panel.Children.Count == 0) panel.Children.Add(Ui.Caption("Empty"));
                break;
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                for (var i = 0; i < Math.Min(items.Count, MaxItems); i++)
                    panel.Children.Add(Entry(ItemTitle(items[i], i), items[i], depth));
                if (items.Count > MaxItems) panel.Children.Add(Ui.Caption($"and {items.Count - MaxItems:N0} more"));
                if (items.Count == 0) panel.Children.Add(Ui.Caption("None"));
                break;
            default:
                panel.Children.Add(Ui.Text(Scalar(element)));
                break;
        }
        return panel;
    }

    private static UIElement Entry(string label, JsonElement value, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // A short list of plain values reads best on one line.
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= 12
                && value.EnumerateArray().All(v => v.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                return Row(label, string.Join(", ", value.EnumerateArray().Select(Scalar)));

            var count = value.ValueKind == JsonValueKind.Array ? $"  ·  {value.GetArrayLength():N0}" : "";
            return new Expander
            {
                Header = Ui.Text(label + count),
                IsExpanded = depth == 0 && value.ValueKind == JsonValueKind.Object,
                Content = new Border { Padding = new Thickness(16, 4, 0, 6), Child = Build(value, depth + 1) },
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = Ui.Brush("TextPrimaryBrush"),
            };
        }
        return Row(label, Scalar(value));
    }

    private static UIElement Row(string label, string value)
    {
        var row = Ui.Row(label, value);
        row.Margin = new Thickness(0, 2, 0, 2);
        return row;
    }

    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Null => "—",
        _ => v.ToString(),
    };

    /// <summary>A list item's own name when it has one, else its position.</summary>
    private static string ItemTitle(JsonElement item, int index)
    {
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "displayName", "display_name", "name", "title", "label", "username", "id" })
                if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    return s;
        return item.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? $"#{index + 1}" : Scalar(item);
    }

    /// <summary>"physical_memory" and "physicalMemory" alike as "Physical memory".</summary>
    public static string Label(string name)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(name.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
        return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }
}
