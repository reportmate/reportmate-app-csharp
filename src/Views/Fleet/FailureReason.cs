namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The rejection reason codes the ingest API records, and the sentence each one
/// reads as on screen.
/// </summary>
/// <remarks>
/// Kept apart from the page that draws it, and free of any UI dependency, so the
/// mapping can be tested: the list grows upstream, and a code the API adds before
/// this table does is the failure worth catching.
/// </remarks>
public static class FailureReason
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["invalid_passphrase"] = "Wrong passphrase",
        ["invalid_api_key"] = "Wrong API key",
        ["invalid_bearer_token"] = "Rejected bearer token",
        ["invalid_internal_secret"] = "Wrong internal secret",
        ["missing_credentials"] = "No credentials",
        ["insufficient_scope"] = "Missing scope",
        ["upload_aborted"] = "Upload aborted",
        ["body_unreadable"] = "Body unreadable",
        ["empty_body"] = "Empty body",
        ["malformed_json"] = "Malformed JSON",
        ["invalid_payload"] = "Invalid payload",
        ["empty_serial"] = "Empty serial",
        ["sentinel_serial"] = "Placeholder serial",
        ["short_serial"] = "Serial too short",
        ["hostname_serial"] = "Hostname as serial",
        ["letters_only_serial"] = "Hostname-like serial",
        ["serial_equals_hostname"] = "Serial matches hostname",
        ["usage_out_of_bounds"] = "Usage out of bounds",
        ["nul_in_payload"] = "NUL characters stripped",
        ["rate_limited"] = "Rate limited",
        ["internal_error"] = "Server error",
        ["usage_write_failed"] = "Usage rows lost",
    };

    /// <summary>
    /// The human sentence for a reason code, or the code itself when nobody has
    /// labelled it yet. Deliberately not "Unknown": an unlabelled code is still the
    /// real answer, and showing it raw is what makes the missing label visible
    /// instead of hiding a whole class of rejection behind one word.
    /// </summary>
    public static string Label(string? code) =>
        string.IsNullOrEmpty(code) ? "" : Labels.TryGetValue(code, out var label) ? label : code;
}
