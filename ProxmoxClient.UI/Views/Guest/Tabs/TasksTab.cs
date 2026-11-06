using Avalonia.Controls;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     작업 기록 탭 — 무엇을 보여 줄지는 만드는 쪽이 정한다(게스트는 자기 작업만, 노드·데이터센터는 전체).
///     api 를 주면 로그 보기·실행 중인 작업 중지 버튼이 생긴다.
/// </summary>
public sealed class TasksTab : UserControl
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new() { Key = "status", HeaderKey = "MainWindow_42", Width = 110 },
        new() { Key = "type", HeaderKey = "MainWindow_54", Width = 200 },
        new() { Key = "start", HeaderKey = "MainWindow_TaskStart", Width = 150 },
        new() { Key = "end", HeaderKey = "MainWindow_TaskEnd", Width = 150 },
        new() { Key = "user", HeaderKey = "MainWindow_60", Width = 0 }
    ];

    private readonly Func<Task<IReadOnlyList<PveTask>>> _load;
    private readonly string _emptyKey;
    private readonly TableTab _table;
    private readonly TextBlock _empty = new() { Classes = { "subtle" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private List<PveTask> _tasks = [];

    /// <param name="load">표시할 작업 목록을 읽어 오는 함수(필터링 포함).</param>
    /// <param name="hintKey">목록 위 안내 문구 리소스 키.</param>
    /// <param name="emptyKey">목록이 비었을 때의 문구 리소스 키.</param>
    /// <param name="api">주면 작업 로그 보기·실행 중인 작업 중지 버튼이 생긴다.</param>
    public TasksTab(Func<Task<IReadOnlyList<PveTask>>> load, string hintKey, string emptyKey,
        ProxmoxApiClient? api = null)
    {
        _load = load;
        _emptyKey = emptyKey;

        var actions = new List<TableAction>();
        if (api is not null)
        {
            actions.Add(LogAction(api));
            actions.Add(StopAction(api));
        }

        _table = new TableTab(LoadAsync, Columns, hintKey, actions);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_empty, 1);
        grid.Children.Add(_table);
        grid.Children.Add(_empty);
        Content = grid;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> LoadAsync()
    {
        _tasks = (await _load()).ToList();
        _empty.Text = _tasks.Count == 0 ? Loc.T(_emptyKey) : string.Empty;
        _empty.IsVisible = _tasks.Count == 0;
        return _tasks.Select(ToRow).ToList();
    }

    private static IReadOnlyDictionary<string, string> ToRow(PveTask task)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["upid"] = task.Upid,
            ["status"] = task.Status,
            ["type"] = TaskTypes.Describe(task),
            ["start"] = Format.LocalTime(task.StartTimeUtc),
            ["end"] = task.EndTimeUtc is { } end ? Format.LocalTime(end) : "-",
            ["user"] = task.User
        };
    }

    private PveTask? FindTask(IReadOnlyDictionary<string, string>? row)
    {
        return row is null ? null : _tasks.FirstOrDefault(t => t.Upid == FormHelpers.Value(row, "upid"));
    }

    private TableAction LogAction(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "TasksTab_Log", IconKey = "IconList", NeedsSelection = true,
            Run = (row, owner) =>
            {
                if (FindTask(row) is { } task) TaskLogViewer.Show(api, task, owner);
                return Task.FromResult<string?>(null);
            }
        };
    }

    /// <summary>실행 중인 작업을 멈춘다 — 끝난 작업은 멈출 게 없으므로 알려만 준다.</summary>
    private TableAction StopAction(ProxmoxApiClient api)
    {
        return new TableAction
        {
            LabelKey = "TasksTab_Stop", IconKey = "IconSquare", NeedsSelection = true,
            Run = async (row, _) =>
            {
                if (FindTask(row) is not { } task) return null;
                if (!task.IsRunning) return Loc.T("TasksTab_NotRunning");

                await api.Tasks.StopAsync(task.Node, task.Upid);
                return Loc.T("TasksTab_Stopped");
            }
        };
    }
}
