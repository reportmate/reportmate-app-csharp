using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The readable lines inside an event payload: which items failed, which warned,
/// and which installed.
/// </summary>
/// <remarks>
/// An event's message says a run finished; the payload says what happened in it.
/// Without this the events page can report that a device had a bad run and never
/// say which package it was, which is the only part anyone acts on.
///
/// Munki and Cimian describe the same three outcomes in five different shapes,
/// and one fleet carries all of them at once, so every shape is read rather than
/// the newest one:
///
///   Munki     errors: "a; b"                        semicolon-joined string
///   Cimian    warning_items: ["Name"]               array of names
///   Installs  failed_items: [{displayName, error}]  array of objects
///   Generic   error_messages / warning_messages     either of the above
///   Success   {"Managed Safari": "15.6.1"}          a flat name to version map
///
/// The success shape is why the envelope keys are named explicitly: a payload's
/// own counters and identifiers are strings at the top level too, and without a
/// list to skip they would each read as an installed package.
/// </remarks>
public static class EventPayload
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "count", "errors", "warnings", "error_items", "warning_items", "failed_items",
        "error_messages", "warning_messages", "module_status", "warning_count", "error_count",
        "run_type", "session_id", "modules", "modules_processed", "message", "summary",
        // Measured against real payloads rather than taken from the web app, whose
        // list predates these: action is "install" or "remove" and was rendering as
        // an installed package called "action", and the counters and item arrays
        // below are envelope too. items and removed_items are read as detail just
        // under here, so they must not also fall through as successes.
        "action", "duration_seconds", "items", "removed_items",
        "item_warning_count", "operational_warning_count",
    };

    public sealed record Details(
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Successes,
        IReadOnlyList<string> Removed)
    {
        public static readonly Details Empty = new([], [], [], []);
        public int Count => Errors.Count + Warnings.Count + Successes.Count + Removed.Count;
    }

    public static Details Extract(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return Details.Empty;

        var errors = new List<string>();
        var warnings = new List<string>();
        var successes = new List<string>();
        var removed = new List<string>();

        Joined(errors, payload, "errors");
        Joined(warnings, payload, "warnings");
        Items(errors, payload, "error_messages");
        Items(warnings, payload, "warning_messages");
        Items(errors, payload, "error_items");
        Items(warnings, payload, "warning_items");
        Items(errors, payload, "failed_items");
        // What the run actually did, which the web app does not read at all: a
        // successful Cimian run puts its installs in items and its uninstalls in
        // removed_items, so without these the commonest success event on this
        // fleet expands to nothing.
        Items(successes, payload, "items");
        Items(removed, payload, "removed_items");

        foreach (var property in payload.EnumerateObject())
        {
            if (Reserved.Contains(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString()?.Trim();
            if (!string.IsNullOrEmpty(value)) successes.Add($"{property.Name} {value}");
        }

        return new Details(Distinct(errors), Distinct(warnings), Distinct(successes), Distinct(removed));
    }

    /// <summary>Munki's shape: one string holding several messages, joined by semicolons.</summary>
    private static void Joined(List<string> target, JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.String) return;
        foreach (var part in (node.GetString() ?? "").Split(';'))
        {
            var line = part.Trim();
            if (line.Length > 0) target.Add(line);
        }
    }

    /// <summary>
    /// An array of either names or objects. An object carries the item and the
    /// reason separately; both are kept, because "Adobe Acrobat" alone does not
    /// say what went wrong and "exit code 1603" alone does not say what to fix.
    /// </summary>
    private static void Items(List<string> target, JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array) return;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var line = (item.GetString() ?? "").Trim();
                if (line.Length > 0) target.Add(line);
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = Text(item, "displayName") ?? Text(item, "name") ?? "";
            // "message" as well as error/warning: Cimian's warning_items carry the
            // reason under message, and reading only the other two spellings showed
            // the bare item name -- "Outlook" instead of "Outlook: Could not process
            // item Outlook for install. No pkginfo found in catalogs".
            var detail = Text(item, "error") ?? Text(item, "warning") ?? Text(item, "message") ?? "";
            // With no reason, the version is the useful second half: an install
            // entry is a name and what it went to.
            var version = Text(item, "version") ?? "";
            var row = detail.Length > 0
                ? (name.Length > 0 ? $"{name}: {detail}" : detail)
                : (version.Length > 0 && name.Length > 0 ? $"{name} {version}" : name);
            if (row.Length > 0) target.Add(row);
        }
    }

    private static string? Text(JsonElement node, string key) =>
        node.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>
    /// Deduplicated in place order. A run that retries the same package logs it
    /// each time, and five identical lines describe one problem.
    /// </summary>
    private static List<string> Distinct(List<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return lines.Where(seen.Add).ToList();
    }
}
