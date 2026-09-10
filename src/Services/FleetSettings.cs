using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReportMate.App.Services;

/// <summary>
/// The fleet's own settings document, shared by every client that reads the API.
/// </summary>
/// <remarks>
/// Distinct from this machine's configuration. The registry holds what this copy of
/// the dashboard needs in order to read the fleet -- an API address and a read
/// credential -- while these are settings about the fleet itself, stored once at
/// /api/v1/settings and seen the same way by the web app, the Mac app and this one.
///
/// The settings page conflated the two for its whole existence, because it was
/// inherited from the collection agent: it offered an osquery path, a collection
/// interval, a Cimian integration toggle and a retry count, none of which a
/// dashboard has any use for, and none of the fleet settings the web app actually
/// exposes.
/// </remarks>
public sealed class FleetSettingsDocument
{
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("value")] public FleetSettingsValue? Value { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; set; }
}

public sealed class FleetSettingsValue
{
    [JsonPropertyName("kiosk")] public KioskSettings? Kiosk { get; set; }

    // The API carries these three alongside kiosk. They are null on this fleet
    // today, so the page says so rather than drawing an editor over nothing.
    [JsonPropertyName("general")] public JsonElement? General { get; set; }
    [JsonPropertyName("security")] public JsonElement? Security { get; set; }
    [JsonPropertyName("inventory")] public JsonElement? Inventory { get; set; }
    [JsonPropertyName("schemaVersion")] public int? SchemaVersion { get; set; }
}

/// <summary>How a kiosk display presents the dashboard.</summary>
public sealed class KioskSettings
{
    [JsonPropertyName("zoom")] public double? Zoom { get; set; }
    [JsonPropertyName("theme")] public string? Theme { get; set; }
    [JsonPropertyName("homePath")] public string? HomePath { get; set; }
    [JsonPropertyName("idleMinutes")] public int? IdleMinutes { get; set; }
}

public static class FleetSettings
{
    public static Task<FleetApiClient.FleetResult<FleetSettingsDocument>> GetAsync(CancellationToken ct = default) =>
        FleetApiClient.Instance.GetTypedAsync<FleetSettingsDocument>("/api/v1/settings", ct);
}
