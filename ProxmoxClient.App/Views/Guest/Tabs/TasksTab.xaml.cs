using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     작업 기록 탭 — 무엇을 보여 줄지는 만드는 쪽이 정한다(게스트는 자기 작업만, 노드는 전체).
/// </summary>
public partial class TasksTab : UserControl
{
    private readonly ProxmoxApiClient? _api;
    private readonly string _emptyKey;
    private readonly Func<Task<IReadOnlyList<PveTask>>> _load;
    private bool _busy;

    /// <param name="load">표시할 작업 목록을 읽어 오는 함수(필터링 포함).</param>
    /// <param name="hintKey">목록 위 안내 문구 리소스 키.</param>
    /// <param name="emptyKey">목록이 비었을 때의 문구 리소스 키.</param>
    /// <param name="api">주면 작업 로그 보기·실행 중인 작업 중지 버튼이 생긴다.</param>
    public TasksTab(Func<Task<IReadOnlyList<PveTask>>> load, string hintKey, string emptyKey,
        ProxmoxApiClient? api = null)
    {
        InitializeComponent();
        _load = load;
        _emptyKey = emptyKey;
        _api = api;
        HintText.Text = Loc.T(hintKey);
        LogButton.Visibility = StopButton.Visibility = api is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async void OnLog(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is PveTask task) await ShowLogAsync(task);
    }

    /// <summary>행 더블클릭 → 그 작업의 로그(헤더·빈 영역 더블클릭은 무시).</summary>
    private async void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TaskLogViewer.RowTask(e) is not { } task) return;

        e.Handled = true;
        await ShowLogAsync(task);
    }

    /// <summary>로그 창(중지·다운로드 포함) — 닫으면 목록을 다시 읽어 바뀐 상태를 보여 준다.</summary>
    private async Task ShowLogAsync(PveTask task)
    {
        if (_api is null) return;

        TaskLogViewer.Show(_api, task, Window.GetWindow(this));
        await ReloadAsync();
    }

    /// <summary>실행 중인 작업을 멈춘다 — 끝난 작업은 멈출 게 없으므로 알려만 준다.</summary>
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_api is null || TaskGrid.SelectedItem is not PveTask task) return;

        if (!task.IsRunning)
        {
            StatusText.Text = Loc.T("TasksTab_NotRunning");
            return;
        }

        try
        {
            await _api.Tasks.StopAsync(task.Node, task.Upid);
            StatusText.Text = Loc.T("TasksTab_Stopped");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            var tasks = await _load();
            TaskGrid.ItemsSource = tasks;
            StatusText.Text = tasks.Count == 0 ? Loc.T(_emptyKey) : string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("MainViewModel_M12", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }
}
