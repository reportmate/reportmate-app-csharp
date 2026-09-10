using System.Text.Json;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// Derived management values. The clients answer "how did this device get
/// enrolled" in two different vocabularies and neither answer is complete on its
/// own, so the question is settled here rather than charted raw.
/// </summary>
public static class Management
{
    /// <summary>
    /// How the device was bootstrapped into MDM: Automated, User Approved or
    /// Manual. Ported from the web management report's ladder, order included.
    ///
    /// Charting <c>enrollmentType</c> directly does not answer this. macOS says
    /// "Automated Device Enrollment" and Windows says "MDM Enrolled" -- the first
    /// describes a method, the second only repeats that the device is enrolled --
    /// so the raw chart read as two platform-shaped categories and said nothing
    /// about how anything was provisioned. The Windows answer is in
    /// <c>autopilotConfig</c> instead, which is why that is consulted first: 373
    /// Windows devices are Autopilot-provisioned and every one of them charted as
    /// "MDM Enrolled" before this.
    ///
    /// Returns null when nothing in the row answers the question, so those rows
    /// count as Unknown rather than quietly leaving the population.
    /// </summary>
    public static string? BootstrapMethod(JsonElement row)
    {
        if (Truthy(Read(Read(row, "autopilotConfig"), "activated")))
            return "Automated";

        var type = Text(Read(row, "enrollmentType"));
        var status = Text(Read(row, "enrollmentStatus"));

        return type switch
        {
            "Automated Device Enrollment" => "Automated",
            "User Approved Enrollment" => "User Approved",
            "MDM Enrolled" => "Manual",
            // An enrolled device carrying some other method still enrolled somehow,
            // and "N/A" is the string the clients send for no method at all.
            _ when status == "Enrolled" && type is not ("" or "N/A" or "Unknown") => "Manual",
            // A device that is not enrolled has no bootstrap method, and that is an
            // answer rather than a gap: saying so beats dropping it into Unknown
            // beside the rows we genuinely cannot account for.
            _ when status is "Not Enrolled" or "Unenrolled" => "Not Enrolled",
            _ => null,
        };
    }

    // Read inline rather than through the report path resolver: that lives with the
    // report specs, which pull in the UI, and this ladder has to stay testable.
    private static JsonElement? Read(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v)
            ? v : null;

    private static string Text(JsonElement? v) => v?.ValueKind switch
    {
        JsonValueKind.String => v.Value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => v.Value.ToString(),
        _ => "",
    };

    // The clients disagree on whether this is a boolean or the string "true".
    private static bool Truthy(JsonElement? v) =>
        Text(v).Equals("true", StringComparison.OrdinalIgnoreCase);
}
