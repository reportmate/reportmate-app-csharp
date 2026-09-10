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
/// The fold is deliberately shallow. Case and separators are normalised, which is
/// pure spelling, and only two synonyms are asserted beyond that. In particular
/// "completed" is NOT folded into Installed: all 53 of those rows are script actions
/// -- CimianPreflight, osquery, SystemKeepTime -- with no installed version, so the
/// word means the action ran, not that a package is present. Folding it would have
/// claimed 53 installs that never happened.
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
            // Every one of these 147 rows carries an installed version, so the item
            // is installed and only the word differs.
            "install succeeded" => "installed",
            "install failed" => "failed",
            _ => normalized,
        };

        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }
}
