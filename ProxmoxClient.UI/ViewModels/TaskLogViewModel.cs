using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>작업 로그 — 진행 중이면 2초마다 이어서 읽는다(새 줄만 뒤에 붙인다).</summary>
public sealed partial class TaskLogViewModel : ViewModelBase
{
    private const int PageSize = 500;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicRefresher _refresher;
    private int _lineCount;
    private bool _refreshing;

    public TaskLogViewModel(ServerSession session, PveTask task) : this(session.Api, task)
    {
    }

    public TaskLogViewModel(ProxmoxApiClient api, PveTask task)
    {
        Api = api;
        TaskItem = task;
        _refresher = new PeriodicRefresher(TimeSpan.FromSeconds(2), RefreshAsync);
    }

    public ProxmoxApiClient Api { get; }

    public PveTask TaskItem { get; }

    public override string Title => TaskTypes.Describe(TaskItem);

    /// <summary>로그 줄 — 덧붙이기만 한다(가상화 목록이 새 줄만 그린다).</summary>
    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>줄이 없을 때의 안내(로그 없음·오류). 줄이 있으면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>첫 읽기가 끝났다(그 전에는 진행 표시).</summary>
    [ObservableProperty] public partial bool IsLoaded { get; private set; }

    /// <summary>전체 로그 문자열(복사용).</summary>
    public string LogText => string.Join(Environment.NewLine, Lines);

    public override void OnAppearing()
    {
        _refresher.Start();
        _ = _refresher.RefreshNowAsync();
    }

    public override void OnDisappearing(bool removed)
    {
        _refresher.Stop();
        if (!removed) return;

        _refresher.Dispose();
        _lifetime.Cancel();
    }

    [RelayCommand]
    private Task CopyAll()
    {
        return Lines.Count == 0 ? Task.CompletedTask : Views.ClipboardHelper.SetTextAsync(LogText);
    }

    private async Task RefreshAsync()
    {
        // 겹쳐 돌면 같은 시작 줄로 두 번 읽어 줄이 겹친다 — 새로 고침기 밖에서 불려도 하나만
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            // 한 번에 PageSize 줄씩 — 모자라게 오면 끝까지 읽은 것
            while (true)
            {
                var rows = await Api.Tasks.LogAsync(TaskItem.Node, TaskItem.Upid, PageSize, _lineCount,
                    _lifetime.Token);
                foreach (var row in rows) Lines.Add(row.GetValueOrDefault("t") ?? string.Empty);
                _lineCount += rows.Count;
                if (rows.Count < PageSize) break;
            }

            Message = Lines.Count > 0 ? null : L.T("Task_LogEmpty");

            if (TaskItem.IsRunning)
            {
                var status = await Api.GetTaskStatusAsync(TaskItem.Upid, _lifetime.Token);
                TaskItem.CopyFrom(status);
            }

            if (!TaskItem.IsRunning) _refresher.Stop();
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (Lines.Count == 0) Message = ErrorText.Describe(ex);
        }
        finally
        {
            _refreshing = false;
            IsLoaded = true;
        }
    }
}
