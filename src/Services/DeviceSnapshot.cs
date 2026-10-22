using ReportMate.WindowsClient.Models.Modules;

namespace ReportMate.App.Services;

/// <summary>
/// Everything the device page renders, assembled from the runner's local cache.
/// Each module is the newest copy found across recent cache runs, so a partial
/// run (one or two modules) does not blank the rest of the report.
/// </summary>
public sealed class DeviceSnapshot
{
    public EventMetadata? Metadata { get; init; }
    public List<ReportMateEvent> Events { get; init; } = [];

    public InventoryData? Inventory { get; init; }
    public SystemData? System { get; init; }
    public HardwareData? Hardware { get; init; }
    public ManagementData? Management { get; init; }
    public InstallsData? Installs { get; init; }
    public SecurityData? Security { get; init; }
    public IdentityData? Identity { get; init; }
    public NetworkData? Network { get; init; }
    public PeripheralsModuleData? Peripherals { get; init; }
    public ApplicationsData? Applications { get; init; }

    /// <summary>When each module file was written, keyed by module id.</summary>
    public Dictionary<string, DateTime> ModuleCollectedAt { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // ── A fleet device read from the API ────────────────────────────

    /// <summary>The serial a fleet device was read for; null for this machine's own report.</summary>
    public string? RemoteSerial { get; init; }

    /// <summary>True for a fleet device read from the API rather than this machine's cache.</summary>
    public bool IsRemote => RemoteSerial is not null;

    /// <summary>The device's name as the API resolves it.</summary>
    public string? RemoteName { get; init; }

    /// <summary>The platform the API records for the device: Windows or macOS.</summary>
    public string? Platform { get; init; }

    /// <summary>When the API last heard from the device.</summary>
    public DateTime? LastSeen { get; init; }

    /// <summary>True when the API has the device archived.</summary>
    public bool Archived { get; init; }

    /// <summary>
    /// Reads one management tool's log root with its tails. A fleet device's
    /// management module arrives without them, since they are most of its size, so
    /// the log viewer asks for the root it is showing. Null for this machine, whose
    /// cache already holds every tail.
    /// </summary>
    public Func<string, CancellationToken, Task<LogRoot?>>? FetchLogRoot { get; init; }

    /// <summary>Directory the newest run wrote to, or null when nothing has ever run.</summary>
    public string? NewestRunDirectory { get; init; }

    /// <summary>Most recent module write across the snapshot: the local "last seen".</summary>
    public DateTime? CollectedAt => ModuleCollectedAt.Count == 0 ? null : ModuleCollectedAt.Values.Max();

    /// <summary>
    /// The oldest module's collection time. The pair matters because the modules run
    /// on different schedules and one page shows all of them: applications and
    /// peripherals are every four hours, hardware's deep scan is daily, and a module
    /// whose file the runner never wrote is read from an older run's event.json. A
    /// single "collected" time taken from the newest module describes the page as
    /// fresher than most of what is on it.
    /// </summary>
    public DateTime? OldestCollectedAt =>
        ModuleCollectedAt.Count == 0 ? null : ModuleCollectedAt.Values.Min();

    /// <summary>When one module was collected, for the tab that renders it.</summary>
    public DateTime? CollectedAtFor(string moduleId) =>
        ModuleCollectedAt.TryGetValue(moduleId, out var when) ? when : null;

    public bool IsEmpty => Inventory is null && System is null && Hardware is null && Management is null
        && Installs is null && Security is null && Identity is null && Network is null
        && Peripherals is null && Applications is null;

    public bool HasModule(string moduleId) => ModuleCollectedAt.ContainsKey(moduleId);

    /// <summary>Modules whose JSON was on disk but unreadable, keyed by module id.</summary>
    public IReadOnlyDictionary<string, string> ModuleErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parse error for a module, or null when it simply was not collected.</summary>
    public string? ModuleError(string moduleId) =>
        ModuleErrors.TryGetValue(moduleId, out var e) ? e : null;

    // ── Identity helpers used by the header and every tab ────────────

    public string SerialNumber =>
        FirstNonEmpty(Inventory?.SerialNumber, Metadata?.SerialNumber, RemoteSerial) ?? "";

    // This machine's name is a fallback for this machine's report only: a fleet
    // device without an inventory name is called what the API calls it.
    public string DeviceName =>
        FirstNonEmpty(Inventory?.DeviceName, Management?.DeviceState?.DeviceName, Network?.Hostname,
                      IsRemote ? RemoteName : Environment.MachineName, RemoteSerial) ?? "Unknown Device";

    public string AssetTag => Inventory?.AssetTag ?? "";

    public string DeviceId => FirstNonEmpty(Inventory?.UUID, Metadata?.DeviceId) ?? "";

    public string ClientVersion => Metadata?.ClientVersion ?? "";

    public static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
