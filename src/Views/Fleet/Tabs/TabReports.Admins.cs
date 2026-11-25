using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

public static partial class TabReports
{
    private sealed record AdminAssignment(string Username, string? CreationTime);
    public sealed record AdminWidget(string Title, IReadOnlyList<(string Label, int Count)> Entries);

    private static IEnumerable<TabRow> AdminDevices(JsonElement root, string platform) =>
        root.EnumerateArray().Select(device => new TabRow(device))
            .Where(device => device.Serial.Length > 0 && (platform == "all" || device.Platform == platform));

    private static IEnumerable<AdminAssignment> AdminAssignments(TabRow device)
    {
        var metadata = device.Items("adminAccounts")
            .Where(account => account.Str("username") is not null)
            .ToLookup(account => account.Str("username")!, StringComparer.OrdinalIgnoreCase);
        return device.Strs("adminUsernames").Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new AdminAssignment(name, metadata[name].FirstOrDefault()?.Str("creationTime")));
    }

    private static int? AdminAgeDays(string? created, DateTime snapshot) =>
        TabJson.Time(created) is { } time && time <= snapshot ? (int)(snapshot - time).TotalDays : null;

    public static IReadOnlyList<AdminWidget> AdminWidgets(TabContext context)
    {
        if (context.Data is not { Root.ValueKind: JsonValueKind.Array } data) return [];
        var accounts = AdminDevices(data.Root, context.Platform).Select(device => AdminAssignments(device).ToArray()).ToArray();
        var now = data.Snapshot;
        var ages = accounts.SelectMany(rows => rows).Select(row => AdminAgeDays(row.CreationTime, now)).ToArray();
        return
        [
            new("Admin Account Age",
            [
                ("Under 30 days", ages.Count(age => age is < 30)),
                ("30–364 days", ages.Count(age => age is >= 30 and < 365)),
                ("365 days or more", ages.Count(age => age is >= 365)),
                ("Unknown", ages.Count(age => age is null)),
            ]),
            new("Administrators per Device",
            [
                ("None reported", accounts.Count(rows => rows.Length == 0)),
                ("1 administrator", accounts.Count(rows => rows.Length == 1)),
                ("2 or more administrators", accounts.Count(rows => rows.Length >= 2)),
            ]),
            new("Administrator Names", accounts.SelectMany(rows => rows)
                .GroupBy(row => row.Username, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Label: group.Key, Count: group.Count()))
                .OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToArray()),
        ];
    }

    public static readonly TabReportDef Admins = new()
    {
        Module = "identity", Tab = "admins", Title = "Admins Report",
        Description = "Local administrator accounts reported by devices.",
        Api = "/api/v1/identity", ApiParams = [], Paged = false,
        RowNoun = "admin assignments",
        ReadArrayRows = (root, platform) => AdminDevices(root, platform)
            .SelectMany(device => AdminAssignments(device).Select(account => JsonSerializer.SerializeToElement(new
            {
                admin = account.Username,
                deviceName = device.DeviceName,
                serialNumber = device.Serial,
                platform = device.Platform,
                creationTime = account.CreationTime,
                lastSeen = device.Str("lastSeen"),
            }))),
        Choices =
        [
            new TabChoice("admin", "Administrator", ChoiceMode.Single)
            {
                Visible = _ => false,
                Client = (row, name, _) => string.Equals(row.Str("admin"), name, StringComparison.OrdinalIgnoreCase),
            },
        ],
        ColumnChooser = false,
        DefaultSort = "admin",
        Columns =
        [
            new("admin", "Admin Account", row => row.Str("admin"), TabCell.Mono),
            new("deviceName", "Device", row => row.DeviceName, TabCell.Device),
            new("serialNumber", "Serial Number", row => row.Serial, TabCell.Mono),
            new("platform", "Platform", row => row.Platform),
            new TabColumn("accountAge", "Account Age", row => row.Str("creationTime"))
            {
                Rank = row => AdminAgeDays(row.Str("creationTime"), row.Report!.Snapshot),
                Display = row => AdminAgeDays(row.Str("creationTime"), row.Report!.Snapshot) switch
                {
                    null => "Unknown",
                    1 => "1 day",
                    var days => $"{days:N0} days",
                },
            },
            new("lastSeen", "Last Seen", row => row.Str("lastSeen"), TabCell.Time),
        ],
        RowLink = (row, _) => DeviceLink(row, "identity"),
        CsvName = "local-admins",
        CsvHeaders = _ => ["Admin Account", "Device Name", "Serial Number", "Platform", "Account Created", "Last Seen"],
        CsvRow = (row, _, _) => [row.Str("admin"), row.DeviceName, row.Serial, row.Platform, row.Str("creationTime"), row.Str("lastSeen")],
    };
}
