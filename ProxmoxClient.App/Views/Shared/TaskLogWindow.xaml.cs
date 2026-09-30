using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     작업 로그 창(웹 UI 의 Task viewer) — 로그 전체를 쪽 단위로 읽고, 실행 중이면 1초마다 새 줄을 이어 붙인다.
///     중지는 실행 중일 때만, 다운로드는 읽은 로그 전체를 파일로 저장한다.
/// </summary>
public partial class TaskLogWindow : Window
{
    /// <summary>한 번에 읽는 줄 수.</summary>
    private const int PageSize = 5000;

    /// <summary>창에 담는 최대 줄 수 — 넘으면 더 읽지 않는다(수십만 줄 로그로 창이 멈추지 않게).</summary>
    private const int MaxLines = 200_000;

    /// <summary>웹 UI 작업 창과 같은 1초.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ProxmoxApiClient _api;
    private readonly PveTask _task;
    private readonly DispatcherTimer _timer;
    private int _lineCount;
    private bool _busy;
    private bool _closed;

    public TaskLogWindow(ProxmoxApiClient api, PveTask task)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _api = api;
        _task = task;
        Title = Loc.T("TasksTab_LogTitle", task.Type, task.Id);
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
    }

    /// <summary>새 로그 줄을 이어 붙이고 상태를 다시 읽는다 — 끝난 작업이면 주기 새로 고침을 멈춘다.</summary>
    private async Task RefreshAsync()
    {
        if (_busy || _closed) return;

        _busy = true;
        try
        {
            await ReadNewLinesAsync();
            var status = await _api.Tasks.StatusAsync(_task.Node, _task.Upid);
            if (_closed) return;

            ShowState(status);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReadNewLinesAsync()
    {
        while (_lineCount < MaxLines)
        {
            var page = await _api.Tasks.LogAsync(_task.Node, _task.Upid, PageSize, _lineCount);
            if (_closed || page.Count == 0) return;

            var text = new StringBuilder();
            foreach (var line in page)
            {
                if (_lineCount > 0 || text.Length > 0) text.Append(Environment.NewLine);
                text.Append(line.TryGetValue("t", out var t) ? t : string.Empty);
            }

            AppendKeepingScroll(text.ToString());
            _lineCount += page.Count;
            if (page.Count < PageSize) return;
        }
    }

    /// <summary>끝을 보고 있었으면 새 줄을 따라 내려가고, 위를 읽는 중이면 위치를 그대로 둔다.</summary>
    private void AppendKeepingScroll(string text)
    {
        var atEnd = Body.VerticalOffset + Body.ViewportHeight >= Body.ExtentHeight - 1;
        Body.AppendText(text);
        if (atEnd) Body.ScrollToEnd();
    }

    private void ShowState(IReadOnlyDictionary<string, string> status)
    {
        var running = status.TryGetValue("status", out var s) && s == "running";
        StopButton.IsEnabled = running;
        if (running)
        {
            StateText.Text = Loc.T("TaskLog_Running");
            if (!_timer.IsEnabled) _timer.Start();
            return;
        }

        _timer.Stop();
        var exit = status.TryGetValue("exitstatus", out var e) && e.Length > 0 ? e : s ?? string.Empty;
        StateText.Text = Loc.T("TaskLog_Finished", exit);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        try
        {
            await _api.Tasks.StopAsync(_task.Node, _task.Upid);
            StatusText.Text = Loc.T("TaskLog_StopRequested");
            if (!_timer.IsEnabled) _timer.Start(); // 멈춘 뒤 마지막 로그·결과를 이어서 읽는다
        }
        catch (Exception ex)
        {
            StopButton.IsEnabled = true;
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
    }

    /// <summary>읽은 로그 전체를 사용자가 고른 파일로 저장한다(덮어쓰기는 저장 창이 먼저 묻는다).</summary>
    private void OnDownload(object sender, RoutedEventArgs e)
    {
        var started = _task.StartTimeUtc.ToLocalTime();
        var dialog = new SaveFileDialog
        {
            FileName = SafeFileName($"task-{_task.Node}-{_task.Type}-{_task.Id}-{started:yyyyMMdd-HHmmss}.log"),
            Filter = "Log (*.log)|*.log|Text (*.txt)|*.txt|*.*|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, Body.Text + Environment.NewLine, new UTF8Encoding(false));
            StatusText.Text = Loc.T("TaskLog_Saved", dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
