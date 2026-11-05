using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Main;

/// <summary>노드 탭: 선택 노드의 상태 값·깨우기·노드 관리 화면, 그리고 스토리지·작업 줄 더블클릭.</summary>
public sealed partial class MainViewModel
{
    private bool _waking;

    public bool IsWaking
    {
        get => _waking;
        private set
        {
            if (!SetProperty(ref _waking, value)) return;
            OnPropertyChanged(nameof(CanWakeNode));
        }
    }

    /// <summary>깨우기 버튼 — 연타로 요청이 여러 번 가지 않게 보내는 동안 잠근다(WPF 와 같다).</summary>
    public bool CanWakeNode => ShowWakeButton && !IsWaking;

    public string NodeKernelText => NodeStatus?.KernelVersion is { Length: > 0 } kernel ? kernel : "-";

    public string NodeCoresText => NodeStatus is { } status
        ? status.CpuCores.ToString(CultureInfo.CurrentCulture)
        : "-";

    public string NodeLoad1Text => NodeStatus is { } s ? s.LoadAverage1.ToString("F2", CultureInfo.CurrentCulture) : "-";

    public string NodeLoad5Text => NodeStatus is { } s ? s.LoadAverage5.ToString("F2", CultureInfo.CurrentCulture) : "-";

    public string NodeLoad15Text => NodeStatus is { } s ? s.LoadAverage15.ToString("F2", CultureInfo.CurrentCulture) : "-";

    public string NodeUptimeText => NodeStatus is { UptimeSeconds: > 0 } s ? TableFormats.FormatUptime(s.UptimeSeconds) : "-";

    /// <summary>노드 메모리 막대 — 상태를 못 읽었으면 빈 막대에 "-".</summary>
    public UsageBar NodeMemBar => NodeStatus is { } s
        ? new UsageBar(s.MemUsedBytes, s.MemTotalBytes,
            $"{ByteFormatter.Format(s.MemUsedBytes)} / {ByteFormatter.Format(s.MemTotalBytes)}")
        : UsageBar.Empty;

    public UsageBar NodeSwapBar => NodeStatus is { } s
        ? new UsageBar(s.SwapUsedBytes, s.SwapTotalBytes,
            $"{ByteFormatter.Format(s.SwapUsedBytes)} / {ByteFormatter.Format(s.SwapTotalBytes)}")
        : UsageBar.Empty;

    public UsageBar NodeRootBar => NodeStatus is { } s
        ? new UsageBar(s.RootFsUsedBytes, s.RootFsTotalBytes,
            $"{ByteFormatter.Format(s.RootFsUsedBytes)} / {ByteFormatter.Format(s.RootFsTotalBytes)}")
        : UsageBar.Empty;

    /// <summary>온라인 노드의 상태를 읽는다 — 다른 노드를 고르는 사이 이전 노드 값으로 덮지 않는다.</summary>
    private async Task LoadNodeStatusAsync()
    {
        if (_disposed || SelectedNode is not { IsOnline: true } node) return;

        try
        {
            var status = await Session.Api.GetNodeStatusAsync(node.Node).ConfigureAwait(true);
            if (ReferenceEquals(SelectedNode, node)) NodeStatus = status;
        }
        catch (Exception ex) when (IsTransientError(ex))
        {
            // 일시 오류 — 다음 주기에 다시 읽는다
        }
    }

    /// <summary>꺼진 노드를 Wake-on-LAN 으로 깨운다 — 요청은 연결된(켜진) 노드가 받아 패킷을 보낸다.</summary>
    [RelayCommand]
    private async Task WakeNodeAsync()
    {
        if (SelectedNode is not { IsOnline: false } node || IsWaking) return;

        IsWaking = true;
        try
        {
            await Session.Api.Nodes.WakeOnLanAsync(node.Node).ConfigureAwait(true);
            StatusMessage = L.T("NodeWake_Sent", node.Node);
        }
        catch (WriteCancelledException)
        {
            // 사용자가 변경 확인을 거절함
        }
        catch (Exception ex)
        {
            StatusMessage = L.T("NodeWake_Failed", node.Node, ex.Message);
        }
        finally
        {
            IsWaking = false;
        }
    }

    /// <summary>"노드 관리" — 선택한 노드의 관리 화면을 연다(등록 전이면 버튼이 잠긴다).</summary>
    [RelayCommand]
    private Task OpenSelectedNodeAsync()
    {
        return SelectedNode is { } node ? OpenNodeAsync(node) : Task.CompletedTask;
    }

    /// <summary>노드 목록 더블클릭 — 노드 관리 화면.</summary>
    public Task OpenNodeAsync(PveNode node)
    {
        return FeatureRegistry.Node is { } create
            ? AppServices.Navigator.PushViewAsync(node.Node, create(Session, node))
            : Task.CompletedTask;
    }

    /// <summary>저장소 줄 더블클릭 — 그 저장소의 콘텐츠 화면(백업·ISO·템플릿·디스크).</summary>
    [RelayCommand]
    private Task OpenStorageAsync(PveStorage storage)
    {
        return FeatureRegistry.Storage is { } create
            ? AppServices.Navigator.PushViewAsync($"{storage.Storage} ({storage.Node})", create(Session, storage))
            : Task.CompletedTask;
    }

    /// <summary>작업 줄 더블클릭 — 그 작업의 로그 화면.</summary>
    [RelayCommand]
    private Task OpenTaskAsync(PveTask task)
    {
        return AppServices.Navigator.PushAsync(new TaskLogViewModel(Session, task));
    }
}
