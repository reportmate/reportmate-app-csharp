using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>The web app's fleet events view.</summary>
public sealed class EventsPage : FleetPage
{
    protected override (string, string, Accent)? Heading =>
        ("Events", "What the fleet has reported", Accent.Cyan);

    protected override async Task<UIElement> BuildAsync()
    {
        var result = await FleetApiClient.Instance.GetEventsAsync(500);
        if (!result.Ok) return FleetUnavailable(result.Status, result.Detail);

        var events = result.Data!.Events;
        var page = new StackPanel();
        page.Children.Add(Ui.TabHeader("Events", "What the fleet has reported", "", Accent.Cyan,
            Ui.Caption($"{events.Count:N0} events")));

        if (events.Count == 0)
        {
            page.Children.Add(Ui.Card(Ui.EmptyState("No events have been reported.")));
            return page;
        }

        var rows = events
            .OrderByDescending(e => e.When ?? DateTime.MinValue)
            .Select(e => new EventRow
            {
                Kind = Format.Capitalize(NormalizeKind(e.Kind ?? e.EventType)),
                RawKind = NormalizeKind(e.Kind ?? e.EventType),
                KindTone = Classify(e.Kind ?? e.EventType),
                Device = e.DeviceName ?? e.SerialNumber ?? e.Device ?? "",
                Message = e.Message ?? "",
                When = e.When,
            })
            .ToList();

        var table = new FilteredTable<EventRow>("Events", "{0} of {1} events", rows,
            (r, q) => r.Matches(q),
            [
                Col.Pill("Kind", "Kind", "KindTone", 110),
                Col.Text("Device", "Device", 220),
                Col.Text("Message", "Message", star: true, wrap: true),
                Col.Text("When", "WhenLabel", 130),
            ], "Search events...", "No events match the current filters")
            // The web app filters on all six kinds it recognises, not just the two
            // that mean something is wrong. Offering only errors and warnings made
            // the other 999 of every 1000 events reachable in one undifferentiated
            // heap: this fleet reports 985 info, 9 success, 5 system and 1 warning
            // in a thousand, so "everything that is not a problem" was the bulk of
            // the page and could not be narrowed at all.
            .Filter([
                new("all", "All", rows.Count),
                new("error", "Errors", rows.Count(r => r.Kinds("error"))),
                new("warning", "Warnings", rows.Count(r => r.Kinds("warning"))),
                new("success", "Success", rows.Count(r => r.Kinds("success"))),
                new("system", "System", rows.Count(r => r.Kinds("system"))),
                new("info", "Info", rows.Count(r => r.Kinds("info"))),
            ], (r, k) => k == "all" || r.Kinds(k), initial: InitialFilter())
            .WithQuery(Filter("q"))
            .Build();

        table.Margin = new Thickness(0, 20, 0, 0);
        page.Children.Add(table);
        return page;
    }

    /// <summary>
    /// The events route carries its filter as a comma-separated list and has a failures
    /// sub-route; both mean the same thing to a single-choice filter here, so the first
    /// recognised kind wins.
    /// </summary>
    private string InitialFilter()
    {
        if (string.Equals(Link?.Argument, "failures", StringComparison.OrdinalIgnoreCase)) return "error";
        var raw = Filter("filter");
        if (raw is null) return "all";
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            switch (part.ToLowerInvariant())
            {
                case "errors" or "error": return "error";
                case "warnings" or "warning": return "warning";
                case "successes" or "success": return "success";
                case "system": return "system";
                case "info": return "info";
            }
        return "all";
    }

    /// <summary>
    /// The kind this event filters under. data_collection is the runner's own
    /// routine upload and reads as info everywhere else in the product, so it is
    /// folded in rather than given a filter of its own that would almost always
    /// be empty.
    /// </summary>
    private static string NormalizeKind(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "error" or "failed" or "failure" => "error",
        "warning" or "warn" => "warning",
        "success" or "installed" => "success",
        "system" => "system",
        _ => "info",
    };

    private static Tone Classify(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "error" or "failed" or "failure" => Tone.Error,
        "warning" or "warn" => Tone.Warning,
        "success" or "installed" => Tone.Success,
        _ => Tone.Neutral,
    };

    private sealed class EventRow
    {
        public string Kind { get; init; } = "";
        public string RawKind { get; init; } = "";
        public Tone KindTone { get; init; }

        public bool Kinds(string key) => RawKind == key;
        public string Device { get; init; } = "";
        public string Message { get; init; } = "";
        public DateTime? When { get; init; }

        public string WhenLabel
        {
            get
            {
                if (When is null) return "unknown";
                var delta = DateTime.UtcNow - When.Value.ToUniversalTime();
                if (delta < TimeSpan.FromMinutes(1)) return "just now";
                if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m ago";
                if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours}h ago";
                return $"{(int)delta.TotalDays}d ago";
            }
        }

        public bool Matches(string query) =>
            string.IsNullOrWhiteSpace(query)
            || $"{Kind} {Device} {Message}".Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
