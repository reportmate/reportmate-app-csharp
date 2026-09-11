using System.Windows;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;
using ReportMate.WindowsClient.Models.Modules;

namespace ReportMate.App.Views.Device.Widgets;

/// <summary>Manufacturer, model, processor, graphics, memory and internal storage totals.</summary>
public static class HardwareWidget
{
    public static UIElement Build(DeviceSnapshot s)
    {
        var hw = s.Hardware;
        if (hw is null || (string.IsNullOrWhiteSpace(hw.Model) && string.IsNullOrWhiteSpace(hw.Processor?.Name) && (hw.Memory?.TotalPhysical ?? 0) <= 0))
            return Ui.StatBlock("Hardware", "Device specs", "", Accent.Orange, Ui.EmptyState("Hardware information not available"));

        var manufacturer = Format.OrUnknown(hw.Manufacturer);
        var model = Format.OrUnknown(hw.Model);
        var cores = hw.Processor?.Cores > 0 ? hw.Processor.Cores : hw.Processor?.LogicalProcessors ?? 0;
        var processor = Format.OrUnknown(hw.Processor?.Name);
        if (cores > 0 && processor != "Unknown") processor = $"{processor} ({cores} cores)";

        var graphics = CleanGraphicsName(hw.Graphics?.Name, hw.Graphics?.Manufacturer);
        var memory = hw.Memory?.TotalPhysical > 0 ? Format.Bytes(hw.Memory.TotalPhysical) : "Unknown";
        var (total, free) = InternalStorage(hw.Storage);
        var storage = total > 0 ? $"{Format.Bytes(total)} ({Format.Bytes(free)} free)" : "Unknown";

        var body = Ui.VStack(
            Ui.Columns([3, 7], 12,
                manufacturer != "Unknown" ? Ui.Stat("Manufacturer", manufacturer) : null,
                Ui.Stat("Model", model)),
            Ui.Stat("Processor", processor),
            Ui.Stat("Graphics", graphics),
            Ui.Stat("Memory", memory),
            Ui.Stat("Storage", storage),
            // The web shows the NPU and this did not. It is the part of a recent
            // machine that distinguishes it -- this host reports an AMD "NPU Compute
            // Accelerator Device" -- and it is only drawn when one is reported, so
            // an older machine's card is unchanged rather than carrying a row that
            // says Unknown.
            Npu(hw.Npu));

        return Ui.StatBlock("Hardware", "Device specs", "", Accent.Orange, body);
    }

    private static UIElement? Npu(NpuInfo? npu)
    {
        if (npu is null || string.IsNullOrWhiteSpace(npu.Name)) return null;
        var name = npu.Name.Trim();
        var mfg = (npu.Manufacturer ?? "").Trim();
        // "AMD NPU Compute Accelerator Device", not "AMD AMD NPU..." -- the same
        // doubled-vendor problem the graphics name already guards against.
        if (mfg.Length > 0 && !name.StartsWith(mfg, StringComparison.OrdinalIgnoreCase))
            name = $"{mfg} {name}";
        // TOPS is the number anyone comparing NPUs actually wants, and it is zero
        // on hardware that does not report it, so it only appears when real.
        if (npu.ComputeUnits > 0) name = $"{name} ({npu.ComputeUnits:0.#} TOPS)";
        return Ui.Stat("NPU", name);
    }

    public static string CleanGraphicsName(string? name, string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown";
        var cleaned = name.Trim();
        var mfg = (manufacturer ?? "").Trim();
        if (mfg.Length > 0 && cleaned.StartsWith(mfg, StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[mfg.Length..].Trim();
        foreach (var prefix in new[] { "NVIDIA ", "AMD ", "Intel ", "Intel(R) " })
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[prefix.Length..].Trim();
        return cleaned.Length > 0 ? cleaned : name;
    }

    public static (long Total, long Free) InternalStorage(IEnumerable<StorageDevice>? devices)
    {
        long total = 0, free = 0;
        foreach (var d in devices ?? [])
        {
            if (!d.IsInternal || d.Capacity <= 0 || d.FreeSpace <= 0) continue;
            total += d.Capacity;
            free += d.FreeSpace;
        }
        return (total, free);
    }
}
