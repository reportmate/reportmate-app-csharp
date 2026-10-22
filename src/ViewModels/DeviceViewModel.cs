using CommunityToolkit.Mvvm.ComponentModel;
using ReportMate.App.Services;

namespace ReportMate.App.ViewModels;

/// <summary>Header state for the device page; the tabs read the snapshot directly.</summary>
public partial class DeviceViewModel : ObservableObject
{
    /// <summary>The fleet device this page shows, or null for this machine's own report.</summary>
    public string? RemoteSerial { get; }

    public DeviceViewModel(string? remoteSerial = null) => RemoteSerial = remoteSerial;

    /// <summary>When the report on screen was read.</summary>
    public DateTime? LoadedAt { get; private set; }

    [ObservableProperty] private DeviceSnapshot _snapshot = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    public string DeviceName => Snapshot.DeviceName;
    public string SerialNumber => Snapshot.SerialNumber;
    public string AssetTag => Snapshot.AssetTag;
    public string ClientVersion => Snapshot.ClientVersion;
    public bool HasData => !Snapshot.IsEmpty;

    public string? IpAddress
    {
        get
        {
            var active = Snapshot.Network?.Interfaces?
                .FirstOrDefault(i => i.IsActive && i.IpAddresses is { Count: > 0 });
            var ipv4 = active?.IpAddresses?.FirstOrDefault(IsIpv4);
            return ipv4 ?? Snapshot.Network?.ActiveConnection?.IpAddress;
        }
    }

    public string LastSeenLabel => (Snapshot.LastSeen ?? Snapshot.CollectedAt) is { } when ? Format.RelativeTime(when) : "never";

    /// <summary>
    /// Every module's collection time, newest first, for the Collected pill's
    /// tooltip. Without it the pill's one number cannot be checked against the tab
    /// actually on screen.
    /// </summary>
    public string? CollectionDetail
    {
        get
        {
            if (Snapshot.ModuleCollectedAt.Count == 0) return null;
            var lines = Snapshot.ModuleCollectedAt
                .OrderByDescending(m => m.Value)
                .Select(m => $"{m.Key}: {Format.RelativeTime(m.Value)}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Missing after 72 hours without a run, stale after 24, matching the web pills.</summary>
    public string? StatusPill
    {
        get
        {
            if ((Snapshot.LastSeen ?? Snapshot.CollectedAt) is not { } when) return null;
            var hours = (DateTime.Now - when).TotalHours;
            return hours > 72 ? "Missing" : hours > 24 ? "Stale" : null;
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            if (RemoteSerial is null)
                Snapshot = await DeviceSnapshotStore.Instance.LoadAsync();
            else
            {
                // A failed read keeps the report already on screen.
                var (snapshot, error) = await RemoteDeviceLoader.LoadAsync(RemoteSerial);
                if (snapshot is not null) Snapshot = snapshot;
                Error = error;
            }
            if (Error is null) LoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(string.Empty);
        }
    }

    public static bool IsIpv4(string ip)
        => System.Net.IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}
