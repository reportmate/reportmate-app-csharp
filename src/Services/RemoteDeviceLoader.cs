using System.Text.Json;
using ReportMate.WindowsClient.Models.Modules;

namespace ReportMate.App.Services;

/// <summary>Reads a fleet device's report from the API for the device page.</summary>
public static class RemoteDeviceLoader
{
    /// <summary>
    /// The device and its events, read together, each under the dashboard's
    /// 45-second deadline. Events are optional: the page draws without them.
    /// </summary>
    public static async Task<(DeviceSnapshot? Snapshot, string? Error)> LoadAsync(string serial, CancellationToken ct = default)
    {
        var path = $"/api/v1/device/{Uri.EscapeDataString(serial)}";
        JsonElement? device = null, events = null;
        string? error = null;

        var deviceRead = ReportQueue.RunAsync(async t =>
        {
            var r = await FleetApiClient.Instance.GetRawAsync(path, t);
            if (!r.Ok)
            {
                error = r.HttpStatus == 404 ? "The API has no report for this device." : r.Detail ?? "The device could not be read.";
                return new FetchOutcome(error, r.Transient);
            }
            using var doc = r.Data!;
            device = doc.RootElement.Clone();
            return FetchOutcome.Success;
        }, ReportQueue.DefaultTimeout, ct);

        var eventsRead = ReportQueue.RunAsync(async t =>
        {
            var r = await FleetApiClient.Instance.GetRawAsync(path + "/events?limit=200", t);
            if (!r.Ok) return new FetchOutcome(r.Detail ?? "");
            using var doc = r.Data!;
            events = doc.RootElement.Clone();
            return FetchOutcome.Success;
        }, ReportQueue.DefaultTimeout, ct);

        var outcome = await deviceRead;
        await eventsRead;
        if (device is not { } d) return (null, error ?? outcome.Error ?? "The device could not be read.");
        return (RemoteDevice.FromApi(serial, d, events, (tool, t) => LogRootAsync(serial, tool, t)), null);
    }

    /// <summary>One tool's log root with its tails, for the Management tab's viewer.</summary>
    public static async Task<LogRoot?> LogRootAsync(string serial, string tool, CancellationToken ct)
    {
        LogRoot? root = null;
        await ReportQueue.RunAsync(async t =>
        {
            var r = await FleetApiClient.Instance.GetRawAsync(
                $"/api/v1/device/{Uri.EscapeDataString(serial)}/logs/{Uri.EscapeDataString(tool)}", t);
            if (!r.Ok) return new FetchOutcome(r.Detail ?? "");
            using var doc = r.Data!;
            root = RemoteDevice.LogRootFrom(doc.RootElement);
            return FetchOutcome.Success;
        }, ReportQueue.DefaultTimeout, ct);
        return root;
    }
}
