using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Guest.Tabs;

/// <summary>
///     VM 의 QEMU 모니터 — 명령을 보내고 결과를 이어 붙여 보여 준다(웹 UI 의 Monitor 탭).
///     Enter 로 보내고 위·아래 화살표로 이전 명령을 다시 불러온다. 위험한 명령은 서버가 권한으로 막는다.
/// </summary>
public sealed class MonitorTab : UserControl
{
    /// <summary>출력이 끝없이 늘지 않도록 — 넘으면 앞부분을 잘라 낸다.</summary>
    private const int MaxOutputChars = 200_000;

    private readonly ProxmoxApiClient _api;
    private readonly PveResource _guest;
    private readonly List<string> _history = [];
    private readonly TextBox _output;
    private readonly TextBox _command;
    private readonly Button _run;
    private int _historyIndex;
    private bool _busy;

    public MonitorTab(ProxmoxApiClient api, PveResource guest)
    {
        _api = api;
        _guest = guest;

        _output = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12,
            MinHeight = 220, VerticalContentAlignment = VerticalAlignment.Top
        };
        _command = new TextBox
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            PlaceholderText = "info status", MinWidth = 120
        };
        _command.KeyDown += (_, e) => OnCommandKeyDown(e);

        _run = ActionBar.IconButton(Loc.T("MonitorTab_Run"), "IconPlay", this);
        _run.MinWidth = 96;
        _run.Click += async (_, _) => await RunAsync();

        var input = new DockPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                Docked(_run, Dock.Right),
                _command
            }
        };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        var hint = new TextBlock
        {
            Text = Loc.T("MonitorTab_Hint"), Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(hint, 0);
        Grid.SetRow(_output, 1);
        Grid.SetRow(input, 2);
        grid.Children.Add(hint);
        grid.Children.Add(_output);
        grid.Children.Add(input);
        Content = grid;

        AttachedToVisualTree += (_, _) => _command.Focus();
    }

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private void OnCommandKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = RunAsync();
            return;
        }

        if (_history.Count == 0 || e.Key is not (Key.Up or Key.Down)) return;

        _historyIndex = Math.Clamp(_historyIndex + (e.Key == Key.Up ? -1 : 1), 0, _history.Count);
        _command.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
        _command.CaretIndex = _command.Text.Length;
        e.Handled = true;
    }

    private async Task RunAsync()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (_busy || command.Length == 0) return;

        _busy = true;
        _run.IsEnabled = false;
        _history.Add(command);
        _historyIndex = _history.Count;
        _command.Text = string.Empty;
        try
        {
            var output = await _api.Guests.MonitorAsync(_guest.Node, _guest.VmId, command);
            Append($"# {command}\n{output.Replace("\r\n", "\n")}");
        }
        catch (WriteCancelledException)
        {
            // 확인에서 취소 — 명령은 보내지 않았으므로 입력을 되돌려 둔다
            _command.Text = command;
        }
        catch (Exception ex)
        {
            Append($"# {command}\n{Loc.T("TableTab_ActionFailed", ex.Message)}");
        }
        finally
        {
            _busy = false;
            _run.IsEnabled = true;
            _command.Focus();
        }
    }

    private void Append(string text)
    {
        var combined = _output.Text + text + "\n";
        _output.Text = combined.Length > MaxOutputChars ? combined[^MaxOutputChars..] : combined;
        _output.CaretIndex = _output.Text.Length;
    }
}
