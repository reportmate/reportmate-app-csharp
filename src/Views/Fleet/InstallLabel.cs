using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The display label for a managed item's status.
/// </summary>
/// <remarks>
/// Cimian and Munki spell the same states differently and the endpoint returns both,
/// so the raw field carries seventeen values for about eight states: "installed"
/// 77,038 beside "Installed" 40,492, "pending_install" 371 beside "Pending Install"
/// 23, and so on. Charted raw it reads as a platform split rather than a status.
///
/// The fold is deliberately shallow: case and separators, which is pure spelling,
/// plus the synonyms the web's own ladder asserts.
///
/// "completed" is one of those, and it is worth saying why, because the data argues
/// the other way at first glance: all 53 rows spelled that way are script items --
/// CimianPreflight, osquery, SystemKeepTime -- and not one carries an installed
/// version. But a script item has no version to carry, and having run to completion
/// is exactly what being installed means for that item type. The web treats
/// install_succeeded, completed and success as one state in lib/installs/status.ts,
/// and this app's own verdict ladder in InstallStatus already agrees with it, so
/// splitting them here would have made one app disagree with itself.
/// </remarks>
public static class InstallLabel
{
    public static string? Status(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("currentStatus", out var v)
            || v.ValueKind != JsonValueKind.String) return null;

        var raw = v.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var words = raw.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = string.Join(' ', words).ToLowerInvariant();

        normalized = normalized switch
        {
            // The run-completed states, as the web's ladder lists them.
            "install succeeded" or "completed" or "success" => "installed",
            "install failed" => "failed",
            _ => normalized,
        };

        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }
}
