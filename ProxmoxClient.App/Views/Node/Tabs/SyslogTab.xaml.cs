using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Node.Tabs;

/// <summary>노드 창의 시스템 로그 탭 — 최근 줄을 가져와 맨 아래(최신)로 스크롤한다.</summary>
public partial class SyslogTab : UserControl
{
    private const int LineCount = 500;

    private readonly ProxmoxApiClient _api;
    private readonly string _node;
    private bool _busy;

    public SyslogTab(ProxmoxApiClient api, string node)
    {
        InitializeComponent();
        _api = api;
        _node = node;
        Loaded += async (_, _) => await ReloadAsync();
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
            var lines = await _api.GetNodeSyslogAsync(_node, LineCount);
            LogBox.Text = string.Join(Environment.NewLine, lines);
            LogBox.ScrollToEnd();
            StatusText.Text = Loc.T("SyslogTab_Loaded", lines.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }
}
