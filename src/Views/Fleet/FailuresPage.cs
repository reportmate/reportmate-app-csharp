using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// The rejected count is not readable on its own, which is why accepted and retried
/// sit beside it. Five rejections against forty-six thousand accepted uploads is
/// noise; the same five against a hundred would not be.
/// </remarks>
public sealed class FailuresPage : FleetPage
{
    private const int WindowHours = 168;

    protected override (string, string, Accent)? Heading =>
        ("Failed Check-ins", "Devices the server turned away", Accent.Red);

    protected override async Task<UIElement> BuildAsync()
    {
        var page = new StackPanel();
        var result = await FleetApiClient.Instance.GetRawAsync(
            $"/api/v1/events/failures?hours={WindowHours}&outcome=rejected&limit=500");

        if (!result.Ok)
        {
            page.Children.Add(Ui.TabHeader("Failed Check-ins", "Devices the server turned away", "", Accent.Red));
            page.Children.Add(FleetUnavailable(result.Status, result.Detail));
            return page;
        }

        var root = result.Data!.RootElement;
        var counts = root.TryGetProperty("counts", out var c) ? c : default;
        var rejected = CoveragePage.Int(counts, "rejected");
        var retried = CoveragePage.Int(counts, "retried");
        var accepted = CoveragePage.Int(counts, "accepted");
        var attempted = rejected + retried + accepted;

        page.Children.Add(Ui.TabHeader("Failed Check-ins", "Devices the server turned away", "", Accent.Red,
            Ui.Caption($"Last {WindowHours / 24} days")));

        var cards = new UniformGrid { Columns = 2, Margin = new Thickness(0, 20, 0, 0) };

        // Proportion first: the absolute number of rejections says nothing without
        // the number of check-ins that went through.
        var outcome = new StackPanel();
        outcome.Children.Add(Charts.Bar("Accepted", accepted, attempted, Tone.Success));
        outcome.Children.Add(Charts.Bar("Retried", retried, attempted, Tone.Warning));
        outcome.Children.Add(Charts.Bar("Rejected", rejected, attempted, Tone.Error));
        Add(cards, Ui.StatBlock("Check-ins", $"{attempted:N0} attempted", "", Accent.Red,
            new Border { Padding = new Thickness(20, 4, 20, 16), Child = outcome }));

        // Why they were turned away, and across how many machines. One device failing
        // fifty times and fifty devices failing once are the same count and different
        // problems, so the reason carries both.
        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
        {
            var reasons = new StackPanel();
            var worst = summary.EnumerateArray()
                .OrderByDescending(r => CoveragePage.Int(r, "count")).Take(6).ToList();
            var top = worst.Count > 0 ? CoveragePage.Int(worst[0], "count") : 0;

            foreach (var reason in worst)
            {
                var devices = CoveragePage.Int(reason, "devices");
                reasons.Children.Add(Charts.Bar(
                    $"{CoveragePage.Str(reason, "reason")} · {devices:N0} {(devices == 1 ? "device" : "devices")}",
                    CoveragePage.Int(reason, "count"), top, Tone.Error));
            }

            if (reasons.Children.Count == 0)
                reasons.Children.Add(Ui.Caption("Nothing was rejected in this window."));

            Add(cards, Ui.StatBlock("Reasons", $"{summary.GetArrayLength():N0} distinct", "", Accent.Red,
                new Border { Padding = new Thickness(20, 4, 20, 16), Child = reasons }));
        }

        page.Children.Add(cards);

        var rows = new List<FailureRow>();
        if (root.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array)
            foreach (var f in failures.EnumerateArray())
            {
                // A rejected check-in usually has no device name: the name is
                // something the server learns from a check-in that succeeded.
                var name = CoveragePage.Str(f, "deviceName");
                rows.Add(new FailureRow
                {
                    Device = name.Length > 0 ? name : "Unknown device",
                    Serial = CoveragePage.Str(f, "serialNumber"),
                    Platform = CoveragePage.Str(f, "platform"),
                    Reason = CoveragePage.Str(f, "reason"),
                    Kind = CoveragePage.Str(f, "failureType"),
                    Status = CoveragePage.Str(f, "statusCode"),
                    Client = CoveragePage.Str(f, "clientVersion"),
                    When = Relative(CoveragePage.Str(f, "ts")),
                    Detail = CoveragePage.Str(f, "detail"),
                });
            }

        if (rows.Count == 0)
        {
            var empty = Ui.EmptyCard("Rejected check-ins",
                $"No check-in was rejected in the last {WindowHours / 24} days.");
            empty.Margin = new Thickness(0, 14, 0, 0);
            page.Children.Add(empty);
            return page;
        }

        var table = new FilteredTable<FailureRow>("Rejected check-ins", "{0} of {1} rejections", rows,
            (r, q) => r.Matches(q),
            [
                Col.Text("Device", "Device", star: true, sub: "Serial"),
                Col.Text("Platform", "Platform", 100),
                Col.Text("Reason", "Reason", 170),
                Col.Text("Type", "Kind", 100),
                Col.Text("Status", "Status", 80),
                Col.Text("Client", "Client", 150),
                Col.Text("When", "When", 110),
                Col.Text("Detail", "Detail", 300, wrap: true),
            ], "Search rejections...", "No rejection matches the current filters")
            .Build();
        table.Margin = new Thickness(0, 14, 0, 0);
        page.Children.Add(table);
        return page;
    }

    private static void Add(UniformGrid grid, FrameworkElement card)
    {
        card.Margin = new Thickness(0, 0, 14, 14);
        grid.Children.Add(card);
    }

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
        public string Kind { get; init; } = "";
        public string Status { get; init; } = "";
        public string Client { get; init; } = "";
        public string When { get; init; } = "";
        public string Detail { get; init; } = "";

        public bool Matches(string q) => string.IsNullOrWhiteSpace(q)
            || $"{Device} {Serial} {Platform} {Reason} {Kind} {Client} {Detail}"
                .Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
