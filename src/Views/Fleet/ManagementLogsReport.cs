using System.Globalization;
using System.Text.Json;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The fleet Management Tools Logs report, read from
/// <c>GET /api/v1/management/logs/fleet</c>: per tool, the devices whose logs carried
/// errors and warnings and the trend over the window; the common messages, already
/// normalised; and the devices behind them. The MDM agent's own logs are left out,
/// as the dashboard card leaves them out. Kept free of UI types so it can be tested.
/// </summary>
public static class ManagementLogsReport
{
    public sealed record Filters(string? Tool = null, string? Level = null, int Hours = 24, string? Message = null, string? Platform = null)
    {
        /// <summary>The level, as the API names it: error or warning, or null for both.</summary>
        public string? NormalLevel => (Level ?? "").ToLowerInvariant() switch
        {
            "error" or "errors" => "error",
            "warning" or "warnings" or "warn" => "warning",
            _ => null,
        };

        /// <summary>The report's own window: 24 hours or 7 days.</summary>
        public int NormalHours => Hours >= 168 ? 168 : 24;

        public string Path() =>
            "/api/v1/management/logs/fleet?hours=" + NormalHours
            + (string.IsNullOrWhiteSpace(Tool) ? "" : "&tool=" + Uri.EscapeDataString(Tool))
            + (NormalLevel is { } l ? "&level=" + l : "")
            + (string.IsNullOrWhiteSpace(Message) ? "" : "&message=" + Uri.EscapeDataString(Message))
            + (string.IsNullOrWhiteSpace(Platform) ? "" : "&platform=" + Uri.EscapeDataString(Platform));

        /// <summary>The link that reopens the report with these filters.</summary>
        public DeepLink Link() => DeepLink.For("management", "logs",
            ("tool", Tool), ("level", NormalLevel), ("hours", NormalHours == 24 ? null : NormalHours.ToString(CultureInfo.InvariantCulture)),
            ("message", Message), ("platform", Platform));

        public static Filters From(Func<string, string?> read) => new(
            read("tool"), read("level"),
            int.TryParse(read("hours"), out var h) ? h : 24,
            read("message"), read("platform"));
    }

    public sealed record Point(string Label, int Errors, int Warnings);

    public sealed record Tool(string Key, string Name, string? Platform, int DevicesWithErrors, int DevicesWithWarnings,
        int ErrorLines, int WarningLines, DateTime? LastSeen, IReadOnlyList<Point> Trend);

    public sealed record Message(string Id, string Text, string Tool, string ToolName, int Devices, int Lines, DateTime? LastSeen);

    public sealed record Device(string Serial, string Name, string? Platform, string Tool, string ToolName,
        int Errors, int Warnings, DateTime? LastAt);

    public sealed record Report(IReadOnlyList<Tool> Tools, IReadOnlyList<Message> Messages, IReadOnlyList<Device> Devices, int WindowHours);

    private static bool IsMdm(string tool) =>
        tool.Equals("mdm", StringComparison.OrdinalIgnoreCase) || tool.Equals("enrollment", StringComparison.OrdinalIgnoreCase);

    private static DateTime? When(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (Insights.Str(e, name) is { Length: > 0 } text
                && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                return at;
        return null;
    }

    private static IEnumerable<JsonElement> List(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
            if (Insights.Prop(parent, name) is { ValueKind: JsonValueKind.Array } a) return a.EnumerateArray();
        return [];
    }

    public static Report Read(JsonElement root)
    {
        // The response may carry the report directly or under data, as the card envelopes do.
        var r = Insights.Prop(root, "data") is { ValueKind: JsonValueKind.Object } data ? data : root;

        var tools = List(r, "tools")
            .Select(t => (Key: Insights.Str(t, "tool"), Node: t))
            .Where(t => t.Key.Length > 0 && !IsMdm(t.Key))
            .Select(t => new Tool(t.Key,
                Insights.Str(t.Node, "name") is { Length: > 0 } n ? n : t.Key,
                Insights.Str(t.Node, "platform") is { Length: > 0 } p ? p : null,
                Insights.Int(t.Node, "devicesWithErrors"), Insights.Int(t.Node, "devicesWithWarnings"),
                Insights.Int(t.Node, "errorLines"), Insights.Int(t.Node, "warningLines"),
                When(t.Node, "lastSeen", "lastAt"),
                List(t.Node, "trend", "series")
                    .Select(p => new Point(Insights.Str(p, "label") is { Length: > 0 } l ? l : Insights.Str(p, "bucket") is { Length: > 0 } b ? b : Insights.Str(p, "hour"),
                        Insights.Int(p, "errors"), Insights.Int(p, "warnings")))
                    .ToList()))
            .OrderByDescending(t => t.DevicesWithErrors)
            .ThenByDescending(t => t.ErrorLines)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string ToolName(string key, JsonElement node) =>
            Insights.Str(node, "name") is { Length: > 0 } n ? n : tools.FirstOrDefault(t => t.Key == key)?.Name ?? key;

        // Grouped by tool and normalised text, as the card groups them, so one message is one row.
        var messages = List(r, "messages", "topErrors")
            .Select(m => (Tool: Insights.Str(m, "tool"), Text: InsightCards.NormalizeMessage(Insights.Str(m, "message")), Node: m))
            .Where(m => m.Text.Length > 0 && !IsMdm(m.Tool))
            .GroupBy(m => (m.Tool, m.Text))
            .Select(g => new Message(
                Insights.Str(g.First().Node, "id") is { Length: > 0 } id ? id
                    : Insights.Str(g.First().Node, "messageId") is { Length: > 0 } mid ? mid : InsightCards.MessageId(g.Key.Text),
                g.Key.Text, g.Key.Tool, ToolName(g.Key.Tool, g.First().Node),
                g.Sum(m => Insights.Int(m.Node, "devices")), g.Sum(m => Insights.Int(m.Node, "lines")),
                g.Select(m => When(m.Node, "lastSeen", "lastAt")).Max()))
            .OrderByDescending(m => m.Devices)
            .ThenByDescending(m => m.Lines)
            .ToList();

        var devices = List(r, "devices")
            .Select(d => (Tool: Insights.Str(d, "tool"), Node: d))
            .Where(d => !IsMdm(d.Tool))
            .Select(d => new Device(Insights.Str(d.Node, "serialNumber"),
                Insights.Str(d.Node, "deviceName") is { Length: > 0 } n ? n : Insights.Str(d.Node, "name") is { Length: > 0 } n2 ? n2 : Insights.Str(d.Node, "serialNumber"),
                Insights.Str(d.Node, "platform") is { Length: > 0 } p ? p : null,
                d.Tool, ToolName(d.Tool, d.Node),
                Insights.Int(d.Node, "errors") is var e and > 0 ? e : Insights.Int(d.Node, "errorLines"),
                Insights.Int(d.Node, "warnings") is var w and > 0 ? w : Insights.Int(d.Node, "warningLines"),
                When(d.Node, "lastAt", "lastSeen")))
            .Where(d => d.Serial.Length > 0)
            .OrderByDescending(d => d.Errors)
            .ThenByDescending(d => d.LastAt)
            .ToList();

        return new Report(tools, messages, devices, Insights.Int(r, "windowHours") is var h and > 0 ? h : Insights.Int(r, "hours"));
    }

    /// <summary>A tool's trend as a run of block characters, for a table cell: ▁▃▅█.</summary>
    public static string Sparkline(IReadOnlyList<Point> trend)
    {
        if (trend.Count == 0) return "";
        const string Blocks = "▁▂▃▄▅▆▇█";
        var max = Math.Max(1, trend.Max(p => p.Errors + p.Warnings));
        return new string(trend.Select(p => Blocks[(int)Math.Round((double)(p.Errors + p.Warnings) / max * (Blocks.Length - 1))]).ToArray());
    }
}
