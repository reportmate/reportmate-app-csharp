using System.Text.Json;
using ReportMate.App.Services;
using ReportMate.WindowsClient.Models.Modules;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// A fleet device's report read from the API onto the snapshot the device page draws,
/// shaped like GET /api/v1/device/{serial} and its /events and /logs/{tool}.
/// </summary>
public class RemoteDeviceTests
{
    private const string Device = """
        {"success":true,"device":{
          "serialNumber":"SAMPLE1","deviceId":"00000000-0000-0000-0000-000000000001","name":"Sample Desktop",
          "platform":"Windows","clientVersion":"2026.10.20.1200","lastSeen":"2026-10-22T18:00:00+00:00","createdAt":"2025-01-15T09:00:00+00:00",
          "archived":false,
          "modules":{
            "inventory":{"deviceName":"Sample Desktop","serialNumber":"SAMPLE1","assetTag":"A0001","collectedAt":"2026-10-22T17:55:00Z"},
            "system":[{"operatingSystem":{"name":"Windows 11 Enterprise","architecture":"x64"},"pendingWindowsUpdatesCount":"3"}],
            "management":{"logs":{"platform":"Windows","roots":[{"tool":"installs","name":"Managed Installs","errorCount":4,"tails":[]}]}},
            "network":{"hostname":12345}
          }}}
        """;

    private const string Events = """
        {"success":true,"count":2,"events":[
          {"id":"7","kind":"warning","message":"Older","raw":{"module_id":"installs","warning_messages":["Slow"]},"ts":"2026-10-21T10:00:00+00:00"},
          {"id":"9","kind":"error","message":"Newer","raw":{"moduleId":"security"},"ts":"2026-10-22T10:00:00+00:00"}]}
        """;

    private static DeviceSnapshot Snapshot(string? events = Events) => RemoteDevice.FromApi("SAMPLE1",
        JsonDocument.Parse(Device).RootElement,
        events is null ? null : JsonDocument.Parse(events).RootElement);

    [Fact]
    public void TheDeviceAndItsModulesAreRead()
    {
        var s = Snapshot();
        Assert.True(s.IsRemote);
        Assert.Equal("SAMPLE1", s.SerialNumber);
        Assert.Equal("Sample Desktop", s.DeviceName);
        Assert.Equal("A0001", s.AssetTag);
        Assert.Equal("Windows", s.Platform);
        Assert.Equal("2026.10.20.1200", s.ClientVersion);
        Assert.Equal("Windows 11 Enterprise", s.System!.OperatingSystem.Name);
        Assert.Equal(3, s.System.PendingWindowsUpdatesCount);
        Assert.Equal("installs", s.Management!.Logs!.Roots.Single().Tool);
        Assert.True(s.HasModule("inventory"));
        Assert.False(s.HasModule("security"));
    }

    [Fact]
    public void AModuleIsDatedByItsOwnCollectionTimeOrTheDevicesLastCheckIn()
    {
        var s = Snapshot();
        Assert.Equal(new DateTime(2026, 10, 22, 17, 55, 0, DateTimeKind.Utc), s.CollectedAtFor("inventory")!.Value.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 10, 22, 18, 0, 0, DateTimeKind.Utc), s.CollectedAtFor("system")!.Value.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 10, 22, 18, 0, 0, DateTimeKind.Utc), s.LastSeen!.Value.ToUniversalTime());
        Assert.Equal(new DateTime(2025, 1, 15, 9, 0, 0, DateTimeKind.Utc), s.CreatedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void ANumberWhereTheModelHasTextDoesNotCostTheModule()
    {
        Assert.Equal("12345", Snapshot().Network!.Hostname);
    }

    [Fact]
    public void AnUnreadableModuleIsAnErrorForItsTabNotForThePage()
    {
        var broken = Device.Replace("\"pendingWindowsUpdatesCount\":\"3\"", "\"pendingWindowsUpdatesCount\":{\"nested\":true}");
        var s = RemoteDevice.FromApi("SAMPLE1", JsonDocument.Parse(broken).RootElement);
        Assert.Null(s.System);
        Assert.NotNull(s.ModuleError("system"));
        Assert.NotNull(s.Inventory);
    }

    [Fact]
    public void EventsArriveNewestFirstWithTheirModuleAndDetails()
    {
        var events = Snapshot().Events;
        Assert.Equal(["Newer", "Older"], events.Select(e => e.Message));
        Assert.Equal(("error", "security"), (events[0].EventType, events[0].ModuleId));
        Assert.Equal("installs", events[1].ModuleId);
        Assert.True(events[1].Details.ContainsKey("warning_messages"));
        Assert.Empty(Snapshot(events: null).Events);
    }

    [Fact]
    public void ALogRootIsReadWithItsTails()
    {
        var root = RemoteDevice.LogRootFrom(JsonDocument.Parse("""
            {"tool":"installs","root":{"tool":"installs","name":"Managed Installs","errorCount":2,
              "tails":[{"file":"run.log","lines":["[ERROR] one","[WARN] two"],"truncated":false,"bytes":40}]}}
            """).RootElement);
        Assert.NotNull(root);
        Assert.Equal(["[ERROR] one", "[WARN] two"], root!.Tails.Single().Lines);
        Assert.Null(RemoteDevice.LogRootFrom(JsonDocument.Parse("""{"tool":"installs","root":null}""").RootElement));
    }

    [Fact]
    public void EachModuleIsKeptAsSentForAMacsTabs()
    {
        var mac = Device.Replace("\"platform\":\"Windows\"", "\"platform\":\"macOS\"");
        var s = RemoteDevice.FromApi("SAMPLE1", JsonDocument.Parse(mac).RootElement);
        Assert.True(s.IsRemoteMac);
        Assert.Equal("Sample Desktop", s.RawModules["inventory"].GetProperty("deviceName").GetString());
        // A module stored as a one-element list is kept as the module itself.
        Assert.Equal(JsonValueKind.Object, s.RawModules["system"].ValueKind);
        Assert.False(Snapshot().IsRemoteMac);
    }

    [Fact]
    public async Task TheRunLogAndLogRootsAreReadOnRequest()
    {
        var asked = new List<string>();
        var s = RemoteDevice.FromApi("SAMPLE1", JsonDocument.Parse(Device).RootElement, null,
            (tool, _) => { asked.Add(tool); return Task.FromResult<LogRoot?>(new LogRoot { Tool = tool }); },
            _ => Task.FromResult<string?>("run log"));
        Assert.Equal("run log", await s.FetchRunLog!(default));
        Assert.Equal("installs", (await s.FetchLogRoot!("installs", default))!.Tool);
        Assert.Equal(["installs"], asked);
    }

    [Fact]
    public void AMacsLogRootsAreReadEvenWhenItsManagementModuleIsNot()
    {
        var management = JsonDocument.Parse("""
            {"mdm_enrollment":{"enrolled":"true"},"installed_profiles":"not a list",
             "logs":{"platform":"macOS","roots":[{"tool":"installs","name":"Managed Installs","errorCount":3,"tails":[]}]}}
            """).RootElement;
        var logs = RemoteDevice.LogsFrom(management);
        Assert.Equal("installs", logs!.Roots.Single().Tool);
        Assert.Equal(3, logs.Roots[0].ErrorCount);
        Assert.Null(RemoteDevice.LogsFrom(JsonDocument.Parse("""{"logs":{"roots":[]}}""").RootElement));
    }

    [Fact]
    public void ThisMachinesReportIsNotRemote()
    {
        var local = new DeviceSnapshot();
        Assert.False(local.IsRemote);
        Assert.Equal(Environment.MachineName, local.DeviceName);
    }
}
