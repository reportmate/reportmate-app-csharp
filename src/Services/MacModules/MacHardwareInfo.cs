// Explicit usings: the files in this folder are also compiled into the test project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ReportMate.App.Services.MacModules;

/// <summary>
/// A Mac's hardware module as the Hardware tab shows it. Port of the Mac app's
/// ReportMateKit/Processing/HardwareInfo.swift, which in turn ports the web's
/// HardwareTab extraction: the Mac client sends the same facts as Windows under
/// its own names (physical_memory, efficiency_cores, metal_support, a Neural
/// Engine block), so the Windows HardwareModule classes read almost none of it.
/// </summary>
public sealed record MacHardwareInfo
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? ModelIdentifier { get; init; }
    public string? FormFactor { get; init; }
    public string? ChipName { get; init; }
    public string? ProcessorName { get; init; }
    public string? ProcessorSpeed { get; init; }
    public double? ProcessorMaxSpeed { get; init; }
    public int CpuCores { get; init; }
    public int PerformanceCores { get; init; }
    public int EfficiencyCores { get; init; }
    public int? LogicalProcessors { get; init; }
    public string? GraphicsName { get; init; }
    public string? GraphicsManufacturer { get; init; }
    public double GraphicsMemoryGB { get; init; }
    public string? GraphicsDriverVersion { get; init; }
    public string? GraphicsMetalSupport { get; init; }
    public int GpuCores { get; init; }
    public string? Vram { get; init; }
    public string? NpuName { get; init; }
    public string? NpuManufacturer { get; init; }
    public int NpuCores { get; init; }
    public int NpuComputeUnits { get; init; }
    public string? NpuTops { get; init; }
    public bool NpuAvailable { get; init; }
    public double TotalMemoryBytes { get; init; }
    public double? AvailableMemoryBytes { get; init; }
    public string? MemoryType { get; init; }
    public string? MemoryManufacturer { get; init; }
    public IReadOnlyList<MacMemoryModule> MemoryModules { get; init; } = [];
    public bool UnifiedMemoryFlag { get; init; }
    public IReadOnlyList<MacStorageDevice> StorageDevices { get; init; } = [];
    public IReadOnlyList<MacDisplay> Displays { get; init; } = [];
    public MacBattery? Battery { get; init; }
    public MacRadio? Wireless { get; init; }
    public MacRadio? Bluetooth { get; init; }
    public string? Architecture { get; init; }
    public string? BootTime { get; init; }

    /// <summary>Something worth drawing: a model, a processor or a memory size.</summary>
    public bool HasData => Model is not null || ProcessorName is not null || ChipName is not null || TotalMemoryBytes > 0;

    /// <summary>Apple silicon reports both performance and efficiency cores.</summary>
    public bool HasAppleSilicon => PerformanceCores > 0 && EfficiencyCores > 0;

    /// <summary>
    /// Unified memory: the client's flag, Apple silicon, or a CPU and GPU that share a
    /// name. It picks the chip layout (CPU, memory, GPU and NPU as one package) over
    /// the discrete spec grid.
    /// </summary>
    public bool IsUnifiedMemory =>
        UnifiedMemoryFlag || HasAppleSilicon || (ProcessorName is not null && ProcessorName == GraphicsName);

    /// <summary>Drives that count toward the totals: internal, with capacity and free space known.</summary>
    public IReadOnlyList<MacStorageDevice> InternalDrives =>
        StorageDevices.Where(d => d.CapacityBytes > 0 && d.FreeBytes > 0 && d.IsInternal).ToList();

    public double TotalStorageBytes => InternalDrives.Sum(d => d.CapacityBytes);
    public double FreeStorageBytes => InternalDrives.Sum(d => d.FreeBytes);

    /// <summary>
    /// A desktop's battery block is the power source's placeholder (AC power, zero
    /// cycles), so only a portable with cycles or charge shows one.
    /// </summary>
    public bool HasBattery => FormFactor != "desktop" && Battery is { } b && (b.CycleCount > 0 || b.ChargePercent > 0);

    public bool HasNpu => NpuAvailable && !string.IsNullOrEmpty(NpuName) && (NpuComputeUnits > 0 || NpuCores > 0);

    /// <summary>GPU name without the vendor prefix.</summary>
    public string CleanGraphicsName
    {
        get
        {
            if (GraphicsName is not { } name) return "Unknown";
            if (GraphicsManufacturer?.ToUpperInvariant() is { Length: > 0 } mfg && name.ToUpperInvariant().StartsWith(mfg, StringComparison.Ordinal))
                name = name[mfg.Length..].Trim();
            foreach (var prefix in new[] { "NVIDIA ", "AMD ", "INTEL " })
                if (name.ToUpperInvariant().StartsWith(prefix, StringComparison.Ordinal))
                    name = name[prefix.Length..].Trim();
            return name.Length == 0 ? GraphicsName : name;
        }
    }

    /// <summary>GPU name with the processor's name stripped on a unified-memory machine, where they repeat.</summary>
    public string DisplayGraphicsName
    {
        get
        {
            var clean = CleanGraphicsName;
            if (IsUnifiedMemory && ProcessorName is { } p && clean.StartsWith(p, StringComparison.Ordinal))
            {
                var rest = clean[p.Length..].Trim();
                return rest.Length == 0 ? "Integrated Graphics" : rest;
            }
            return clean;
        }
    }

    /// <summary>
    /// Reads the hardware module; the system module, when given, supplies the
    /// architecture and boot time the hardware module leaves out.
    /// </summary>
    public static MacHardwareInfo Read(JsonElement hardware, JsonElement? system = null)
    {
        JsonElement hw = Unwrap(hardware);
        var hwSystem = MacJson.Node(hw, "system");
        var os = MacJson.Node(system, "operatingSystem");

        var processor = MacJson.Node(hw, "processor");
        string? processorName, chipName;
        if (processor is { ValueKind: JsonValueKind.String } p)
        {
            processorName = chipName = p.GetString() is { } t && t.Trim().Length > 0 ? t.Trim() : null;
            processor = null;
        }
        else
        {
            processorName = MacJson.Str(processor, "name", "value", "brand");
            chipName = MacJson.Str(processor, "chip", "name");
        }
        var cores = new[] { MacJson.Int(processor, "cores"), MacJson.Int(processor, "cpuCores"), MacJson.Int(processor, "logicalCores"), MacJson.Int(processor, "logicalProcessors") }
            .FirstOrDefault(c => c > 0) ?? 0;

        var graphics = MacJson.Node(hw, "graphics") is { } g ? Unwrap(g) : (JsonElement?)null;
        var npu = MacJson.Node(hw, "npu");

        var memory = MacJson.Node(hw, "memory");
        var modules = MacJson.Arr(memory, "modules").Select((m, i) => new MacMemoryModule(
            MacJson.Str(m, "location") ?? $"Slot {i + 1}",
            MacJson.Str(m, "type") ?? "",
            MacJson.Num(m, "capacity") ?? 0,
            MacJson.Str(m, "speed"),
            MacJson.Str(m, "manufacturer") ?? "")).ToList();

        return new MacHardwareInfo
        {
            Manufacturer = MacJson.Str(hw, "manufacturer") ?? MacJson.Str(hwSystem, "hardwareVendor"),
            Model = MacJson.Str(hw, "model") ?? MacJson.Str(hwSystem, "modelName"),
            ModelIdentifier = MacJson.Str(hw, "modelIdentifier") ?? MacJson.Str(hwSystem, "hardwareModel"),
            FormFactor = MacJson.Str(hw, "formFactor"),
            ProcessorName = processorName,
            ChipName = chipName,
            ProcessorSpeed = MacJson.Str(processor, "speed", "baseSpeed"),
            ProcessorMaxSpeed = MacJson.Num(processor, "maxSpeed"),
            CpuCores = cores,
            PerformanceCores = MacJson.Int(processor, "performanceCores") ?? 0,
            EfficiencyCores = MacJson.Int(processor, "efficiencyCores") ?? 0,
            LogicalProcessors = MacJson.Int(processor, "logicalProcessors") ?? MacJson.Int(processor, "logicalCores"),
            GraphicsName = MacJson.Str(graphics, "name"),
            GraphicsManufacturer = MacJson.Str(graphics, "manufacturer"),
            GraphicsMemoryGB = MacJson.Num(graphics, "memorySize") ?? 0,
            GraphicsDriverVersion = MacJson.Str(graphics, "driverVersion"),
            GraphicsMetalSupport = MacJson.Str(graphics, "metalSupport"),
            GpuCores = MacJson.Int(graphics, "cores") ?? MacJson.Int(graphics, "gpuCores") ?? 0,
            Vram = MacJson.Str(graphics, "vram") ?? MacJson.Str(graphics, "memory"),
            NpuName = MacJson.Str(npu, "name"),
            NpuManufacturer = MacJson.Str(npu, "manufacturer"),
            NpuCores = MacJson.Int(processor, "npuCores") ?? MacJson.Int(npu, "cores") ?? 0,
            NpuComputeUnits = MacJson.Int(npu, "computeUnits") ?? 0,
            NpuTops = MacJson.Str(npu, "performanceTops", "tops"),
            NpuAvailable = Flag(npu, "isAvailable") || Flag(npu, "hasNpu"),
            TotalMemoryBytes = MacJson.Num(memory, "physicalMemory", "totalPhysical", "total", "totalBytes") ?? 0,
            AvailableMemoryBytes = MacJson.Num(memory, "availablePhysical", "available"),
            UnifiedMemoryFlag = Flag(memory, "unifiedMemory"),
            MemoryModules = modules,
            MemoryType = MacJson.Str(memory, "type") ?? modules.FirstOrDefault()?.Type,
            MemoryManufacturer = MacJson.Str(memory, "manufacturer") ?? modules.FirstOrDefault()?.Manufacturer,
            StorageDevices = MacJson.Arr(hw, "storage").Select(Drive).ToList(),
            Displays = MacJson.Arr(hw, "displays").Select(Display).ToList(),
            Battery = MacJson.Node(hw, "battery") is { ValueKind: JsonValueKind.Object } bat ? ReadBattery(bat) : null,
            Wireless = Radio(MacJson.Node(hw, "wireless")),
            Bluetooth = Radio(MacJson.Node(hw, "bluetooth")),
            Architecture = MacJson.Str(processor, "architecture") ?? MacJson.Str(os, "architecture"),
            BootTime = MacJson.Str(os, "bootTime") ?? MacJson.Str(system, "bootTime"),
        };
    }

    /// <summary>The client sometimes wraps a single object in a one-element array.</summary>
    private static JsonElement Unwrap(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 1 ? e[0] : e;

    /// <summary>A flag that is false when absent or unreadable, as the Swift reader's boolish.</summary>
    private static bool Flag(JsonElement? parent, string path) => MacJson.Bool(parent, path) ?? false;

    private static MacStorageDevice Drive(JsonElement drive)
    {
        var type = MacJson.Str(drive, "type", "mediaType", "driveType");
        var iface = MacJson.Str(drive, "interface");
        var removable = (MacJson.Str(drive, "type") ?? "").Contains("removable", StringComparison.OrdinalIgnoreCase)
            || (iface ?? "").Contains("usb", StringComparison.OrdinalIgnoreCase);
        var encrypted = MacJson.Bool(drive, "encrypted") ?? MacJson.Bool(drive, "isEncrypted");
        return new MacStorageDevice(
            Name: MacJson.Str(drive, "name", "volumeName", "mountPoint") ?? "Drive",
            Model: MacJson.Str(drive, "model", "deviceName"),
            SerialNumber: MacJson.Str(drive, "serialNumber"),
            Type: type,
            Interface: iface,
            CapacityBytes: MacJson.Num(drive, "size", "capacity", "totalSize") ?? 0,
            FreeBytes: MacJson.Num(drive, "freeSpace", "free", "availableSpace") ?? 0,
            IsInternal: MacJson.Node(drive, "isInternal") is null ? !removable : Flag(drive, "isInternal"),
            MountPoint: MacJson.Str(drive, "mountPoint", "mount"),
            FileSystem: MacJson.Str(drive, "fileSystem", "filesystem", "format"),
            Health: MacJson.Str(drive, "health", "smartStatus"),
            IsEncrypted: encrypted,
            LastAnalyzed: MacJson.Date(drive, "lastAnalyzed"),
            RootDirectories: MacJson.Arr(drive, "rootDirectories").Select(Directory).ToList());
    }

    private static MacStorageDirectory Directory(JsonElement d) => new(
        Name: MacJson.Str(d, "name") ?? "",
        Path: MacJson.Str(d, "path") ?? MacJson.Str(d, "name") ?? "",
        Size: MacJson.Num(d, "size") ?? 0,
        Category: MacJson.Str(d, "category") ?? "Other",
        FileCount: MacJson.Int(d, "fileCount") ?? 0,
        PercentageOfDrive: MacJson.Num(d, "percentageOfDrive") ?? 0,
        Subdirectories: MacJson.Arr(d, "subdirectories").Select(Directory).ToList());

    private static MacDisplay Display(JsonElement d) => new(
        Name: MacJson.Str(d, "name") ?? "Unknown Display",
        SerialNumber: MacJson.Str(d, "serialNumber"),
        Resolution: MacJson.Str(d, "resolution"),
        ScaledResolution: MacJson.Str(d, "scaledResolution"),
        DisplayType: MacJson.Str(d, "displayType"),
        IsInternal: MacJson.Str(d, "type") == "internal",
        FirmwareVersion: MacJson.Str(d, "firmwareVersion"),
        IsMainDisplay: Flag(d, "isMainDisplay"),
        Online: Flag(d, "online"),
        DiagonalInches: MacJson.Num(d, "diagonalInches"),
        Ppi: MacJson.Int(d, "ppi"),
        ColorGamut: MacJson.Str(d, "colorGamut"),
        BrightnessNits: MacJson.Int(d, "brightnessNits"),
        TrueTone: Flag(d, "trueTone"),
        RefreshRate: MacJson.Str(d, "refreshRate"),
        ConnectionType: MacJson.Str(d, "connectionType"));

    private static MacBattery ReadBattery(JsonElement bat)
    {
        var minutes = MacJson.Num(bat, "minutesUntilEmpty") ?? 0;
        return new MacBattery(
            CycleCount: MacJson.Int(bat, "cycleCount") ?? 0,
            ChargePercent: MacJson.Num(bat, "percentRemaining", "chargePercent", "percent") ?? 0,
            Health: MacJson.Str(bat, "health"),
            Condition: MacJson.Str(bat, "condition", "state"),
            IsCharging: Flag(bat, "charging") || Flag(bat, "isCharging"),
            EstimatedRuntime: minutes > 0 ? $"{(int)minutes} min" : MacJson.Str(bat, "estimatedRuntime"),
            DesignCapacity: MacJson.Num(bat, "designedCapacity", "designCapacity"),
            CurrentCapacity: MacJson.Num(bat, "currentCapacity"),
            MaxCapacity: MacJson.Num(bat, "maxCapacity"));
    }

    private static MacRadio? Radio(JsonElement? r) => r is { ValueKind: JsonValueKind.Object }
        ? new MacRadio(
            Name: MacJson.Str(r, "name"),
            Generation: MacJson.Str(r, "wifiGeneration"),
            Version: MacJson.Str(r, "wifiVersion", "bluetoothVersion"),
            Status: MacJson.Str(r, "status"),
            Protocol: MacJson.Str(r, "protocol"),
            SupportedBands: MacJson.Str(r, "supportedBands"),
            IsAvailable: Flag(r, "isAvailable"))
        : null;
}

/// <summary>One memory module; Apple silicon reports none, its memory being on the package.</summary>
public sealed record MacMemoryModule(string Location, string Type, double CapacityMB, string? Speed, string Manufacturer);

public sealed record MacStorageDevice(
    string Name, string? Model, string? SerialNumber, string? Type, string? Interface,
    double CapacityBytes, double FreeBytes, bool IsInternal, string? MountPoint, string? FileSystem,
    string? Health, bool? IsEncrypted, DateTime? LastAnalyzed, IReadOnlyList<MacStorageDirectory> RootDirectories)
{
    public int UsedPercent => CapacityBytes > 0
        ? Math.Clamp((int)Math.Round((CapacityBytes - FreeBytes) / CapacityBytes * 100, MidpointRounding.AwayFromZero), 0, 100)
        : 0;
}

public sealed record MacStorageDirectory(
    string Name, string Path, double Size, string Category, int FileCount, double PercentageOfDrive,
    IReadOnlyList<MacStorageDirectory> Subdirectories);

public sealed record MacDisplay(
    string Name, string? SerialNumber, string? Resolution, string? ScaledResolution, string? DisplayType,
    bool IsInternal, string? FirmwareVersion, bool IsMainDisplay, bool Online, double? DiagonalInches,
    int? Ppi, string? ColorGamut, int? BrightnessNits, bool TrueTone, string? RefreshRate, string? ConnectionType);

public sealed record MacBattery(
    int CycleCount, double ChargePercent, string? Health, string? Condition, bool IsCharging,
    string? EstimatedRuntime, double? DesignCapacity, double? CurrentCapacity, double? MaxCapacity);

public sealed record MacRadio(
    string? Name, string? Generation, string? Version, string? Status, string? Protocol,
    string? SupportedBands, bool IsAvailable);
