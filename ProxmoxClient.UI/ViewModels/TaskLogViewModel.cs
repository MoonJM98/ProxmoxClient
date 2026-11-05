using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>작업 로그 — 진행 중이면 2초마다 이어서 읽는다.</summary>
public sealed partial class TaskLogViewModel : ViewModelBase
{
    private const int PageSize = 500;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicRefresher _refresher;
    private readonly StringBuilder _log = new();
    private int _lineCount;

    public TaskLogViewModel(ServerSession session, PveTask task)
    {
        Session = session;
        TaskItem = task;
        _refresher = new PeriodicRefresher(TimeSpan.FromSeconds(2), RefreshAsync);
    }

    public ServerSession Session { get; }

    public PveTask TaskItem { get; }

    public override string Title => TaskTypes.Describe(TaskItem);

    [ObservableProperty] public partial string LogText { get; private set; } = string.Empty;

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

    private async Task RefreshAsync()
    {
        try
        {
            // 한 번에 PageSize 줄씩 — 모자라게 오면 끝까지 읽은 것
            while (true)
            {
                var rows = await Session.Api.Tasks.LogAsync(TaskItem.Node, TaskItem.Upid, PageSize, _lineCount,
                    _lifetime.Token);
                foreach (var row in rows) _log.AppendLine(row.GetValueOrDefault("t") ?? string.Empty);
                _lineCount += rows.Count;
                if (rows.Count < PageSize) break;
            }

            LogText = _log.Length > 0 ? _log.ToString() : L.T("Task_LogEmpty");

            if (TaskItem.IsRunning)
            {
                var status = await Session.Api.GetTaskStatusAsync(TaskItem.Upid, _lifetime.Token);
                TaskItem.CopyFrom(status);
            }

            if (!TaskItem.IsRunning) _refresher.Stop();
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_log.Length == 0) LogText = ErrorText.Describe(ex);
        }
    }
}
