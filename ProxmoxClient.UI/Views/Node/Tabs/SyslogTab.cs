using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Node.Tabs;

/// <summary>노드 창의 시스템 로그 탭 — 최근 줄을 가져와 맨 아래(최신)로 스크롤한다.</summary>
public sealed class SyslogTab : UserControl
{
    private const int LineCount = 500;

    private readonly ProxmoxApiClient _api;
    private readonly string _node;
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, Margin = new Thickness(0, 10, 0, 0) };
    private bool _busy;

    public SyslogTab(ProxmoxApiClient api, string node)
    {
        _api = api;
        _node = node;

        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);

        var refresh = ActionBar.IconButton(Loc.T("MainWindow_03"), "IconRefresh", this);
        refresh.MinWidth = 96;
        refresh.Click += async (_, _) => await ReloadAsync();
        DockPanel.SetDock(refresh, Dock.Right);
        var top = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                refresh,
                new TextBlock
                {
                    Text = Loc.T("SyslogTab_Hint"), Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        // 데스크톱은 WPF 와 같은 여백(20,16), 폰은 좌우 16px
        var pad = PlatformInfo.IsDesktop ? new Thickness(20, 16) : new Thickness(16);
        Content = new Grid
        {
            Margin = pad,
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            Children = { top, _log, _status }
        };
        Grid.SetRow(_log, 1);
        Grid.SetRow(_status, 2);
        AttachedToVisualTree += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            var lines = await _api.GetNodeSyslogAsync(_node, LineCount);
            _log.Text = string.Join(Environment.NewLine, lines);
            ScrollToEnd();
            _status.Text = Loc.T("SyslogTab_Loaded", lines.Count);
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>WPF 의 LogBox.ScrollToEnd 와 같다 — 글이 바뀐 뒤 맨 아래로 내린다.</summary>
    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => _log.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
