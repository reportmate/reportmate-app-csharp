using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The web app's Failed Check-ins page: devices that reached the server and were
/// turned away.
/// </summary>
/// <remarks>
/// This is the one report whose subject appears nowhere else in the app. A device
/// whose check-in is rejected never becomes a device: it is absent from the device
/// list, contributes to no report and raises no warning, so a machine failing every
/// upload looks exactly like a machine that is switched off. The only place it exists
/// is here.
///
/// Three different questions share one table, and the outcome switch is what keeps
/// them apart. Only <c>rejected</c> means "did not get in and nothing has arrived
/// since". <c>retried</c> is an upload that dropped in transport and landed on the
/// client's retry -- the device reported. <c>accepted</c> is a check-in the server
/// stored after repairing a client defect on the way in. The latter two run to
/// thousands of rows a day at fleet scale; folding either into a view titled Failed
/// Check-ins would bury the handful of devices that are genuinely stuck.
/// </remarks>
public sealed class FailuresPage : FleetPage
{
    private static readonly (int Hours, string Label)[] Windows =
        [(24, "24 hours"), (168, "7 days"), (720, "30 days")];

    // Keyed by the API's own vocabulary so the query string is the state. "Repaired"
    // is the label because "accepted" reads as success, and a repaired check-in is a
    // client defect the server papered over.
    private static readonly Dictionary<string, (string Chip, string Title, string Blurb, string Noun, Tone Tone, Accent Accent)> Outcomes = new()
    {
        ["rejected"] = ("Rejected", "Failed Check-ins",
            "Devices that reached the server but were turned away and have sent nothing since",
            "rejection", Tone.Error, Accent.Red),
        ["retried"] = ("Retried", "Retried Check-ins",
            "The upload dropped in transport and the client's retry landed — the device reported",
            "retried check-in", Tone.Neutral, Accent.Gray),
        ["accepted"] = ("Repaired", "Repaired Check-ins",
            "Stored, but the server fixed a client defect on the way in — watch these fall to zero as the client fix rolls out",
            "repaired check-in", Tone.Info, Accent.Cyan),
    };

    private string _outcome = "rejected";
    private int _hours = 168;
    private string? _reason;
    private bool _seeded;

    private (string Chip, string Title, string Blurb, string Noun, Tone Tone, Accent Accent) Copy =>
        Outcomes.TryGetValue(_outcome, out var c) ? c : Outcomes["rejected"];

    protected override (string, string, Accent)? Heading => (Copy.Title, Copy.Blurb, Copy.Accent);

    protected override async Task<UIElement> BuildAsync()
    {
        // A link may name the view it wants. Taken once: a refresh five minutes later
        // must not undo a switch the person made in the meantime.
        if (!_seeded)
        {
            _seeded = true;
            if (Filter("outcome") is { } wanted && Outcomes.ContainsKey(wanted)) _outcome = wanted;
            if (Filter("reason") is { Length: > 0 } r) _reason = r;
            if (int.TryParse(Filter("hours"), out var h) && Windows.Any(w => w.Hours == h)) _hours = h;
        }

        var page = new StackPanel();
        var query = $"/api/v1/events/failures?hours={_hours}&outcome={_outcome}&limit=500"
            + (_reason is null ? "" : $"&reason={Uri.EscapeDataString(_reason)}");
        var result = await FleetApiClient.Instance.GetRawAsync(query);

        var window = new Border { Margin = new Thickness(0, 0, 0, 0), Child = Ui.Segmented(
            Windows.Select(w => (w.Hours.ToString(), w.Label)), _hours.ToString(),
            key => { if (int.TryParse(key, out var h) && h != _hours) { _hours = h; _ = ReloadAsync(); } }) };

        page.Children.Add(Ui.TabHeader(Copy.Title, Copy.Blurb, "", Copy.Accent, window));

        if (!result.Ok)
        {
            page.Children.Add(FleetUnavailable(result.Status, result.Detail));
            return page;
        }

        var root = result.Data!.RootElement;
        var counts = root.TryGetProperty("counts", out var c) ? c : default;

        // The three counts are always all present, so the switch doubles as the
        // proportion: five rejections beside forty thousand repairs is a different
        // fleet from five beside none, and both read off the same row.
        var switcher = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0),
        };
        foreach (var (key, copy) in Outcomes)
        {
            var chip = Ui.FilterChip(copy.Chip, CoveragePage.Int(counts, key), copy.Tone, key == _outcome);
            var target = key;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (target == _outcome) return;
                _outcome = target;
                // A reason code belongs to the view it was clicked in; carrying it
                // across usually lands on an empty table for no visible cause.
                _reason = null;
                _ = ReloadAsync();
            };
            switcher.Children.Add(chip);
        }
        page.Children.Add(switcher);

        var total = CoveragePage.Int(root, "total");

        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
            page.Children.Add(ReasonCard(summary));

        var rows = new List<FailureRow>();
        if (root.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array)
            foreach (var f in failures.EnumerateArray())
            {
                // A rejected check-in usually has no device name: the name is
                // something the server learns from a check-in that succeeded.
                var name = CoveragePage.Str(f, "deviceName");
                var code = CoveragePage.Str(f, "reason");
                rows.Add(new FailureRow
                {
                    Device = name.Length > 0 ? name : "Unknown device",
                    Serial = CoveragePage.Str(f, "serialNumber"),
                    Platform = CoveragePage.Str(f, "platform"),
                    Reason = FailureReason.Label(code),
                    Code = code,
                    Kind = CoveragePage.Str(f, "failureType"),
                    Status = CoveragePage.Str(f, "statusCode"),
                    Client = CoveragePage.Str(f, "clientVersion"),
                    When = Relative(CoveragePage.Str(f, "ts")),
                    At = Instant(CoveragePage.Str(f, "ts")),
                    Detail = CoveragePage.Str(f, "detail"),
                });
            }

        if (rows.Count == 0)
        {
            var empty = Ui.EmptyCard(Copy.Chip + " check-ins",
                _reason is null
                    ? $"Nothing was {Copy.Chip.ToLowerInvariant()} in the last {WindowLabel()}."
                    : $"No {Copy.Noun} in the last {WindowLabel()} matches that reason.");
            empty.Margin = new Thickness(0, 14, 0, 0);
            page.Children.Add(empty);
            return page;
        }

        // total counts every row the filter matched; the table holds at most 500 of
        // them, and saying so is the difference between a complete list and a page.
        var caption = rows.Count < total
            ? $"{{0}} of {{1}} shown · {total:N0} {Copy.Noun}s in the last {WindowLabel()}"
            : $"{{0}} of {{1}} {Copy.Noun}s";

        var table = new FilteredTable<FailureRow>(Copy.Chip + " check-ins", caption, rows,
            (r, q) => r.Matches(q),
            [
                Col.Text("Device", "Device", star: true, sub: "Serial"),
                Col.Text("Platform", "Platform", 100),
                Col.Text("Reason", "Reason", 170),
                Col.Text("Type", "Kind", 100),
                Col.Text("Status", "Status", 80),
                Col.Text("Client", "Client", 150),
                Col.Text("When", "When", 110, sortBy: "At"),
                Col.Text("Detail", "Detail", 300, wrap: true),
            ], $"Search {Copy.Noun}s...", $"No {Copy.Noun} matches the current filters")
            .Build();
        table.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(table);
        return page;
    }

    /// <summary>
    /// Why they were turned away, and across how many machines. One device failing
    /// fifty times and fifty devices failing once are the same count and different
    /// problems, so the reason carries both, and clicking one narrows the table.
    /// </summary>
    private FrameworkElement ReasonCard(JsonElement summary)
    {
        var reasons = new StackPanel();
        var worst = summary.EnumerateArray()
            .OrderByDescending(r => CoveragePage.Int(r, "count")).Take(8).ToList();
        var top = worst.Count > 0 ? CoveragePage.Int(worst[0], "count") : 0;

        foreach (var reason in worst)
        {
            var code = CoveragePage.Str(reason, "reason");
            var devices = CoveragePage.Int(reason, "devices");
            var label = FailureReason.Label(code);
            var bar = Charts.Bar(
                $"{label} · {devices:N0} {(devices == 1 ? "device" : "devices")}",
                CoveragePage.Int(reason, "count"), top, Copy.Tone);

            var row = new Border
            {
                Child = bar,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = code == _reason ? Ui.Tint(Copy.Tone, 0.10) : System.Windows.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
            };
            var target = code;
            row.MouseLeftButtonUp += (_, _) =>
            {
                _reason = _reason == target ? null : target;
                _ = ReloadAsync();
            };
            reasons.Children.Add(row);
        }

        if (reasons.Children.Count == 0)
            reasons.Children.Add(Ui.Caption($"Nothing was {Copy.Chip.ToLowerInvariant()} in this window."));

        UIElement? clear = null;
        if (_reason is not null)
            clear = Ui.IconButton("", "Clear", (_, _) => { _reason = null; _ = ReloadAsync(); });

        var card = Ui.StatBlock("Reasons", $"{summary.GetArrayLength():N0} distinct", "", Copy.Accent,
            new Border { Padding = new Thickness(20, 4, 20, 16), Child = reasons }, clear);
        card.Margin = new Thickness(0, 18, 0, 0);
        return card;
    }

    private string WindowLabel() =>
        Windows.FirstOrDefault(w => w.Hours == _hours).Label ?? $"{_hours} hours";

    private static DateTime Instant(string timestamp) =>
        DateTime.TryParse(timestamp, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            ? when : DateTime.MinValue;

    private static string Relative(string timestamp)
    {
        if (!DateTime.TryParse(timestamp, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var when))
            return "";

        var delta = DateTime.UtcNow - when;
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    public sealed class FailureRow
    {
        public string Device { get; init; } = "";
        public string Serial { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Reason { get; init; } = "";
        public string Code { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Status { get; init; } = "";
        public string Client { get; init; } = "";
        public string When { get; init; } = "";

        /// <summary>What the When column sorts by; the words in it do not order.</summary>
        public DateTime At { get; init; }
        public string Detail { get; init; } = "";

        // Code as well as label: someone reading the API's own vocabulary in a log
        // should be able to paste it here and find the rows.
        public bool Matches(string q) => string.IsNullOrWhiteSpace(q)
            || $"{Device} {Serial} {Platform} {Reason} {Code} {Kind} {Client} {Detail}"
                .Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
