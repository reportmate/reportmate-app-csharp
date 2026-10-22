using System.Text.Json;
using ReportMate.App.Services.MacModules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The Mac Hardware tab reads the Mac client's module, which spells the Windows
/// facts its own way (snake_case, 1/0 flags, a Neural Engine block). Fixtures follow
/// the shape a Mac uploads, with invented values.
/// </summary>
public class MacHardwareInfoTests
{
    private const string AppleSilicon = """
        {
          "manufacturer": "Apple",
          "model": "Mac mini (2024)",
          "model_identifier": "Mac16,11",
          "formFactor": "desktop",
          "processor": { "architecture": "ARM64", "cores": 14, "efficiency_cores": 4, "performance_cores": 10, "logical_cores": 14, "name": "M4 Pro" },
          "graphics": { "cores": 20, "manufacturer": "Apple", "metal_support": "Metal 4", "name": "Apple M4 Pro" },
          "memory": { "manufacturer": "Sample Memory Co", "physical_memory": 51539607552, "type": "LPDDR5", "unified_memory": 1 },
          "npu": { "cores": 16, "has_npu": 1, "isAvailable": 1, "name": "Apple Neural Engine (M4)", "performance_tops": "38" },
          "battery": { "condition": "Normal", "cycle_count": 0, "percent_remaining": 0, "power_source": "AC Power" },
          "bluetooth": { "bluetoothVersion": "5.0", "isAvailable": 1, "status": "On" },
          "wireless": { "isAvailable": 1, "protocol": "Wi-Fi 6E (802.11ax)", "status": "Off", "wifiGeneration": "Wi-Fi 6E", "wifiVersion": "802.11ax", "name": "en1" },
          "displays": [
            { "display_type": "Retina", "firmware_version": "1.2.3", "is_main_display": 1, "name": "Sample Display", "online": 1,
              "resolution": "5120 x 2880", "scaled_resolution": "2560 x 1440 @ 60.00Hz", "serial_number": "sample-display-1", "type": "external" }
          ],
          "storage": [
            { "capacity": 2000000000000, "free_space": 500000000000, "file_system": "APFS", "health": "Good", "interface": "Apple Fabric",
              "is_internal": 1, "model": "Sample SSD", "name": "Macintosh HD - Data", "type": "SSD", "lastAnalyzed": "2026-10-01T12:00:00Z",
              "rootDirectories": [
                { "category": "Applications", "fileCount": 2, "name": "Applications", "path": "/Applications", "percentageOfDrive": 2.5, "size": 50000000000,
                  "subdirectories": [ { "category": "Applications", "name": "Sample.app", "path": "/Applications/Sample.app", "size": 1000000000, "subdirectories": [] } ] },
                { "category": "Users", "name": "Users", "path": "/Users", "percentageOfDrive": 10, "size": 200000000000, "subdirectories": [] }
              ] },
            { "capacity": 4000000000000, "free_space": 1800000000000, "file_system": "Case-sensitive APFS", "health": "Unknown", "interface": "USB",
              "is_internal": 0, "model": "Sample External", "name": "Backup", "smart_status": "Not Supported", "type": "SSD" }
          ],
          "system": { "hardware_vendor": "Apple Inc.", "model_name": "Mac mini (2024)", "hardware_model": "Mac16,11" }
        }
        """;

    private static MacHardwareInfo Read(string json, string? system = null) =>
        MacHardwareInfo.Read(JsonDocument.Parse(json).RootElement, system is null ? null : JsonDocument.Parse(system).RootElement);

    [Fact]
    public void ReadsTheIdentityAndChip()
    {
        var hw = Read(AppleSilicon);
        Assert.True(hw.HasData);
        Assert.Equal("Apple", hw.Manufacturer);
        Assert.Equal("Mac mini (2024)", hw.Model);
        Assert.Equal("Mac16,11", hw.ModelIdentifier);
        Assert.Equal("M4 Pro", hw.ProcessorName);
        Assert.Equal("ARM64", hw.Architecture);
        Assert.Equal(14, hw.CpuCores);
        Assert.Equal(10, hw.PerformanceCores);
        Assert.Equal(4, hw.EfficiencyCores);
        Assert.True(hw.HasAppleSilicon);
        Assert.True(hw.IsUnifiedMemory);
    }

    [Fact]
    public void ReadsMemoryGraphicsAndNeuralEngine()
    {
        var hw = Read(AppleSilicon);
        Assert.Equal(51539607552, hw.TotalMemoryBytes);
        Assert.Equal("LPDDR5", hw.MemoryType);
        Assert.Equal("Sample Memory Co", hw.MemoryManufacturer);
        Assert.Empty(hw.MemoryModules);
        Assert.Equal(20, hw.GpuCores);
        Assert.Equal("Metal 4", hw.GraphicsMetalSupport);
        Assert.True(hw.HasNpu);
        Assert.Equal("38", hw.NpuTops);
        Assert.Equal(16, hw.NpuCores);
    }

    /// <summary>The GPU shares the chip's name; repeating it under the chip title says nothing.</summary>
    [Fact]
    public void StripsTheVendorAndChipFromTheGraphicsName()
    {
        var hw = Read(AppleSilicon);
        Assert.Equal("M4 Pro", hw.CleanGraphicsName);
        Assert.Equal("Integrated Graphics", hw.DisplayGraphicsName);
    }

    /// <summary>A plugged-in backup disk is not the machine's headroom.</summary>
    [Fact]
    public void TotalsOnlyInternalDrives()
    {
        var hw = Read(AppleSilicon);
        Assert.Equal(2, hw.StorageDevices.Count);
        Assert.Single(hw.InternalDrives);
        Assert.Equal(2000000000000, hw.TotalStorageBytes);
        Assert.Equal(500000000000, hw.FreeStorageBytes);
        Assert.Equal(75, hw.StorageDevices[0].UsedPercent);
        Assert.False(hw.StorageDevices[1].IsInternal);
        Assert.Equal("Unknown", hw.StorageDevices[1].Health);
        Assert.Equal("Case-sensitive APFS", hw.StorageDevices[1].FileSystem);
    }

    [Fact]
    public void ReadsTheDirectoryBreakdown()
    {
        var drive = Read(AppleSilicon).StorageDevices[0];
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), drive.LastAnalyzed);
        Assert.Equal(2, drive.RootDirectories.Count);
        var apps = drive.RootDirectories[0];
        Assert.Equal("/Applications", apps.Path);
        Assert.Equal("Applications", apps.Category);
        Assert.Equal(2.5, apps.PercentageOfDrive);
        Assert.Equal("Sample.app", Assert.Single(apps.Subdirectories).Name);
    }

    [Fact]
    public void ReadsDisplays()
    {
        var d = Assert.Single(Read(AppleSilicon).Displays);
        Assert.Equal("Sample Display", d.Name);
        Assert.False(d.IsInternal);
        Assert.True(d.IsMainDisplay);
        Assert.True(d.Online);
        Assert.Equal("2560 x 1440 @ 60.00Hz", d.ScaledResolution);
        Assert.Equal("sample-display-1", d.SerialNumber);
        Assert.Equal("1.2.3", d.FirmwareVersion);
    }

    /// <summary>A desktop reports its power source as a battery with no cycles; it has none to show.</summary>
    [Fact]
    public void HidesADesktopsPlaceholderBattery()
    {
        var hw = Read(AppleSilicon);
        Assert.NotNull(hw.Battery);
        Assert.Equal("Normal", hw.Battery!.Condition);
        Assert.False(hw.HasBattery);
    }

    [Fact]
    public void ReadsTheRadios()
    {
        var hw = Read(AppleSilicon);
        Assert.True(hw.Wireless!.IsAvailable);
        Assert.Equal("Wi-Fi 6E", hw.Wireless.Generation);
        Assert.Equal("802.11ax", hw.Wireless.Version);
        Assert.Equal("Wi-Fi 6E (802.11ax)", hw.Wireless.Protocol);
        Assert.Equal("5.0", hw.Bluetooth!.Version);
        Assert.Equal("On", hw.Bluetooth.Status);
    }

    [Fact]
    public void ReadsADiscreteLaptopWithModulesAndABattery()
    {
        var hw = Read("""
            {
              "model": "MacBook Pro (16-inch, 2019)",
              "formFactor": "laptop",
              "processor": "Intel Core i9",
              "graphics": [ { "name": "AMD Radeon Pro 5500M", "manufacturer": "AMD", "memorySize": 4, "driverVersion": "1.0" } ],
              "memory": { "physical_memory": 34359738368, "modules": [
                { "location": "Bank 0", "type": "DDR4", "capacity": 16384, "speed": "2667", "manufacturer": "Sample Memory Co" },
                { "type": "DDR4", "capacity": 16384 } ] },
              "battery": { "cycle_count": 312, "percent_remaining": 87.6, "health": "Good", "is_charging": "true", "minutes_until_empty": 245,
                           "designed_capacity": 8790, "current_capacity": 7600 },
              "storage": [ { "name": "Macintosh HD", "size": 1000000000000, "free": 250000000000 },
                           { "name": "Stick", "capacity": 64000000000, "freeSpace": 1000000000, "type": "Removable" } ]
            }
            """, """{ "operatingSystem": { "architecture": "x86_64", "bootTime": "2026-10-20T08:00:00Z" } }""");

        Assert.Equal("Intel Core i9", hw.ProcessorName);
        Assert.Equal("Intel Core i9", hw.ChipName);
        Assert.Equal(0, hw.CpuCores);
        Assert.False(hw.IsUnifiedMemory);
        Assert.Equal("x86_64", hw.Architecture);
        Assert.Equal("2026-10-20T08:00:00Z", hw.BootTime);

        Assert.Equal("Radeon Pro 5500M", hw.CleanGraphicsName);
        Assert.Equal(4, hw.GraphicsMemoryGB);

        Assert.Equal(2, hw.MemoryModules.Count);
        Assert.Equal("Bank 0", hw.MemoryModules[0].Location);
        Assert.Equal("Slot 2", hw.MemoryModules[1].Location);
        Assert.Equal("DDR4", hw.MemoryType);
        Assert.Equal("Sample Memory Co", hw.MemoryManufacturer);

        Assert.True(hw.HasBattery);
        var b = hw.Battery!;
        Assert.Equal(312, b.CycleCount);
        Assert.Equal(87.6, b.ChargePercent);
        Assert.True(b.IsCharging);
        Assert.Equal("245 min", b.EstimatedRuntime);
        Assert.Equal(8790, b.DesignCapacity);

        Assert.True(hw.StorageDevices[0].IsInternal);
        Assert.False(hw.StorageDevices[1].IsInternal);
        Assert.Equal(1000000000000, hw.TotalStorageBytes);
    }

    [Fact]
    public void FallsBackToTheSystemBlockForIdentity()
    {
        var hw = Read("""{ "system": { "hardware_vendor": "Apple Inc.", "model_name": "iMac", "hardware_model": "iMac21,1" } }""");
        Assert.Equal("Apple Inc.", hw.Manufacturer);
        Assert.Equal("iMac", hw.Model);
        Assert.Equal("iMac21,1", hw.ModelIdentifier);
        Assert.True(hw.HasData);
    }

    [Fact]
    public void AnEmptyModuleHasNothingToDraw()
    {
        var hw = Read("{}");
        Assert.False(hw.HasData);
        Assert.Empty(hw.StorageDevices);
        Assert.Null(hw.Battery);
        Assert.Null(hw.Wireless);
        Assert.Equal("Unknown", hw.CleanGraphicsName);
    }
}
