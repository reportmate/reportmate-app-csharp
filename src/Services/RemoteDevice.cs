// Explicit usings: this file is also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReportMate.WindowsClient.Models.Modules;

namespace ReportMate.App.Services;

/// <summary>
/// A fleet device's report, read from the API and laid onto the same
/// <see cref="DeviceSnapshot"/> the device page draws for this machine.
/// </summary>
/// <remarks>
/// The API stores each module as the client uploaded it, so the runner's own module
/// classes read it back: <c>GET /api/v1/device/{serial}</c> carries every module under
/// <c>device.modules</c>, and <c>/events</c> the device's event history. A module that
/// cannot be read is recorded as a module error, and the tab says so, rather than the
/// whole page failing over one field.
/// </remarks>
public static class RemoteDevice
{
    /// <summary>
    /// The cache reader's options, plus tolerance for a number or flag where the
    /// model has a string: one odd field should not cost a whole module.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(), new TolerantStringConverter() },
    };

    private static JsonElement? Prop(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (parent.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return v;
        foreach (var p in parent.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        return null;
    }

    private static string? Str(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static DateTime? Date(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
            if (Str(parent, name) is { } text
                && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                return when.ToLocalTime();
        return null;
    }

    /// <summary>
    /// The snapshot for one device response, and its events when they were read.
    /// </summary>
    /// <param name="response">The body of <c>GET /api/v1/device/{serial}</c>.</param>
    /// <param name="events">The body of <c>GET /api/v1/device/{serial}/events</c>, if read.</param>
    public static DeviceSnapshot FromApi(string serial, JsonElement response, JsonElement? events = null,
        Func<string, CancellationToken, Task<LogRoot?>>? fetchLogRoot = null,
        Func<CancellationToken, Task<string?>>? fetchRunLog = null)
    {
        var device = Prop(response, "device") ?? response;
        var modules = Prop(device, "modules") ?? default;
        var lastSeen = Date(device, "lastSeen");
        var collectedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raws = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        T? Module<T>(string id) where T : class
        {
            if (Prop(modules, id) is not { } raw) return null;
            // A module stored as a one-element list is the same module.
            if (raw.ValueKind == JsonValueKind.Array) raw = raw.EnumerateArray().FirstOrDefault();
            if (raw.ValueKind != JsonValueKind.Object) return null;
            raws[id] = raw.Clone();
            try
            {
                var data = raw.Deserialize<T>(JsonOptions);
                if (data is null) return null;
                // The module's own collection time when it carries one; otherwise the
                // device's last check-in, which is when the API last stored it.
                if ((Date(raw, "collectedAt", "collected_at") ?? lastSeen) is { } when) collectedAt[id] = when;
                return data;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                errors[id] = ex.Message;
                return null;
            }
        }

        var platform = Str(device, "platform");
        return new DeviceSnapshot
        {
            RemoteSerial = Str(device, "serialNumber") is { Length: > 0 } s ? s : serial,
            RemoteName = Str(device, "name"),
            Platform = platform,
            LastSeen = lastSeen,
            CreatedAt = Date(device, "createdAt", "registrationDate"),
            Archived = Prop(device, "archived") is { ValueKind: JsonValueKind.True },
            FetchLogRoot = fetchLogRoot,
            FetchRunLog = fetchRunLog,
            Metadata = new EventMetadata
            {
                SerialNumber = Str(device, "serialNumber") ?? serial,
                DeviceId = Str(device, "deviceId") ?? "",
                ClientVersion = Str(device, "clientVersion") ?? "",
                Platform = platform ?? "",
                CollectedAt = lastSeen ?? DateTime.MinValue,
            },
            Inventory = Module<InventoryData>("inventory"),
            System = Module<SystemData>("system"),
            Hardware = Module<HardwareData>("hardware"),
            Management = Module<ManagementData>("management"),
            Installs = Module<InstallsData>("installs"),
            Security = Module<SecurityData>("security"),
            Identity = Module<IdentityData>("identity"),
            Network = Module<NetworkData>("network"),
            Peripherals = Module<PeripheralsModuleData>("peripherals"),
            Applications = Module<ApplicationsData>("applications"),
            Events = events is { } e ? Events(e) : [],
            ModuleCollectedAt = collectedAt,
            ModuleErrors = errors,
            RawModules = raws,
        };
    }

    /// <summary>The device's events from <c>/events</c>, newest first, in the cache's event shape.</summary>
    public static List<ReportMateEvent> Events(JsonElement response)
    {
        var list = Prop(response, "events") is { ValueKind: JsonValueKind.Array } a ? a
            : response.ValueKind == JsonValueKind.Array ? response : default;
        if (list.ValueKind != JsonValueKind.Array) return [];

        var events = new List<ReportMateEvent>();
        foreach (var e in list.EnumerateArray())
        {
            var details = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var raw = Prop(e, "raw") ?? Prop(e, "details");
            if (raw is { ValueKind: JsonValueKind.Object } r)
                foreach (var p in r.EnumerateObject()) details[p.Name] = p.Value.Clone();
            var module = raw is { } rr ? Str(rr, "module_id") ?? Str(rr, "moduleId") ?? Str(rr, "module") : null;
            events.Add(new ReportMateEvent
            {
                EventType = Str(e, "kind") ?? Str(e, "eventType") ?? Str(e, "type") ?? "info",
                Message = Str(e, "message") ?? "",
                Timestamp = (Date(e, "ts", "timestamp", "createdAt") ?? DateTime.MinValue).ToUniversalTime(),
                ModuleId = module ?? "",
                Details = details,
            });
        }
        return events.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>
    /// The management tools' log roots from a management module, read on their own.
    /// A Mac's management module does not fit the Windows class, so the whole module
    /// can fail to read while its log roots -- the same shape on both platforms -- are fine.
    /// </summary>
    public static ManagementLogs? LogsFrom(JsonElement management)
    {
        if (Prop(management, "logs") is not { ValueKind: JsonValueKind.Object } logs) return null;
        try
        {
            var read = logs.Deserialize<ManagementLogs>(JsonOptions);
            return read is { Roots.Count: > 0 } ? read : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>One tool's log root with its tails, from <c>/logs/{tool}</c>; null when the device has none.</summary>
    public static LogRoot? LogRootFrom(JsonElement response)
    {
        if (Prop(response, "root") is not { ValueKind: JsonValueKind.Object } root) return null;
        try { return root.Deserialize<LogRoot>(JsonOptions); }
        catch (JsonException) { return null; }
    }
}
