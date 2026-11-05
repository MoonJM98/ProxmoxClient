using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Mobile.Localization;
using ProxmoxClient.Mobile.Services;

namespace ProxmoxClient.Mobile.ViewModels;

/// <summary>노드 상세 — CPU·메모리·스왑·루트 디스크·부하, 노드의 게스트 목록.</summary>
public sealed partial class NodeDetailViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicRefresher _refresher;

    public NodeDetailViewModel(ServerSession session, PveNode node, IReadOnlyList<PveResource> guests)
    {
        Session = session;
        Node = node;
        foreach (var guest in guests.OrderBy(g => g.VmId)) Guests.Add(guest);
        _refresher = new PeriodicRefresher(RefreshInterval, RefreshAsync);
    }

    public ServerSession Session { get; }

    public PveNode Node { get; }

    public string Title => Node.Node;

    public ObservableCollection<PveResource> Guests { get; } = [];

    [ObservableProperty] public partial PveNodeStatus? Status { get; private set; }

    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    public string CpuText => Status is { } s
        ? L.T("Node_CpuText", Format.Percent(s.CpuUsagePercent), s.CpuCores, s.CpuSockets)
        : "-";

    public double CpuRatio => Status?.CpuUsagePercent ?? 0;

    public string MemText => Status is { } s ? Format.UsedOf(s.MemUsedBytes, s.MemTotalBytes) : "-";

    public double MemRatio => Status is { } s ? Format.Ratio(s.MemUsedBytes, s.MemTotalBytes) : 0;

    public string SwapText => Status is { } s ? Format.UsedOf(s.SwapUsedBytes, s.SwapTotalBytes) : "-";

    public double SwapRatio => Status is { } s ? Format.Ratio(s.SwapUsedBytes, s.SwapTotalBytes) : 0;

    public string RootFsText => Status is { } s ? Format.UsedOf(s.RootFsUsedBytes, s.RootFsTotalBytes) : "-";

    public double RootFsRatio => Status is { } s ? Format.Ratio(s.RootFsUsedBytes, s.RootFsTotalBytes) : 0;

    public string LoadText => Status is { } s
        ? $"{s.LoadAverage1:0.00} · {s.LoadAverage5:0.00} · {s.LoadAverage15:0.00}"
        : "-";

    public string UptimeText => Status is { } s ? Format.Uptime(s.UptimeSeconds) : "-";

    public string CpuModel => Status?.CpuModel ?? "-";

    public string Kernel => Status?.KernelVersion ?? "-";

    partial void OnStatusChanged(PveNodeStatus? value)
    {
        foreach (var name in new[]
                 {
                     nameof(CpuText), nameof(CpuRatio), nameof(MemText), nameof(MemRatio), nameof(SwapText),
                     nameof(SwapRatio), nameof(RootFsText), nameof(RootFsRatio), nameof(LoadText),
                     nameof(UptimeText), nameof(CpuModel), nameof(Kernel)
                 })
            OnPropertyChanged(name);
    }

    public override void OnAppearing()
    {
        _refresher.Start();
        _ = RefreshAsync();
    }

    public override void OnDisappearing(bool removed)
    {
        _refresher.Stop();
        if (!removed) return;

        _refresher.Dispose();
        _lifetime.Cancel();
    }

    [RelayCommand]
    private Task Reload()
    {
        return RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var statusTask = Session.Api.GetNodeStatusAsync(Node.Node, _lifetime.Token);
            var resourcesTask = Session.Api.GetClusterResourcesAsync(_lifetime.Token);
            Status = await statusTask;
            ErrorMessage = null;

            var guests = (await resourcesTask).Where(g => g.Node == Node.Node).OrderBy(g => g.VmId).ToList();
            CollectionSync.Sync(Guests, guests, g => g.Id, (current, incoming) => current.CopyFrom(incoming));
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorText.Describe(ex);
        }
    }

    [RelayCommand]
    private Task OpenGuest(PveResource guest)
    {
        return AppServices.Navigator.PushAsync(new GuestDetailViewModel(Session, guest));
    }
}
