using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     VM 의 QEMU 모니터 — 명령을 보내고 결과를 이어 붙여 보여 준다(웹 UI 의 Monitor 탭).
///     위험한 명령은 서버가 권한(Sys.Modify·root)으로 막는다.
/// </summary>
public partial class MonitorTab : UserControl
{
    /// <summary>출력이 끝없이 늘지 않도록 — 넘으면 앞부분을 잘라 낸다.</summary>
    private const int MaxOutputChars = 200_000;

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly List<string> _history = [];
    private int _historyIndex;
    private bool _busy;

    public MonitorTab(ProxmoxApiClient api, PveResource guest)
    {
        InitializeComponent();
        _api = api;
        _guest = guest;
        Loaded += (_, _) => CommandBox.Focus();
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        await RunAsync();
    }

    /// <summary>위·아래 화살표로 이전 명령을 다시 불러온다.</summary>
    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (_history.Count == 0 || e.Key is not (Key.Up or Key.Down)) return;

        _historyIndex = Math.Clamp(_historyIndex + (e.Key == Key.Up ? -1 : 1), 0, _history.Count);
        CommandBox.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
        CommandBox.CaretIndex = CommandBox.Text.Length;
        e.Handled = true;
    }

    private async Task RunAsync()
    {
        var command = CommandBox.Text.Trim();
        if (_busy || command.Length == 0) return;

        _busy = true;
        _history.Add(command);
        _historyIndex = _history.Count;
        CommandBox.Clear();
        try
        {
            var output = await _api.Guests.MonitorAsync(_guest.Node, _guest.VmId, command);
            var text = output.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            Append($"# {command}{Environment.NewLine}{text}");
        }
        catch (Exception ex)
        {
            Append($"# {command}{Environment.NewLine}{Loc.T("TableTab_ActionFailed", ex.Message)}");
        }
        finally
        {
            _busy = false;
            CommandBox.Focus();
        }
    }

    private void Append(string text)
    {
        var combined = OutputBox.Text + text + Environment.NewLine;
        OutputBox.Text = combined.Length > MaxOutputChars ? combined[^MaxOutputChars..] : combined;
        OutputBox.ScrollToEnd();
    }
}
