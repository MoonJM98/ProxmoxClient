using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;

namespace ProxmoxClient.App.Views.Node.Tabs;

/// <summary>노드 창의 셸 탭 — 노드 셸을 별도 터미널 창으로 연다.</summary>
public partial class NodeShellTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly string _node;

    public NodeShellTab(ProxmoxApiClient api, string node)
    {
        InitializeComponent();
        _api = api;
        _node = node;
    }

    private void OnOpenShell(object sender, RoutedEventArgs e)
    {
        try
        {
            // 게스트 콘솔과 같이 부모창과 독립된 최상위 창으로 연다
            Services.ConsoleWindows.ShowNodeShell(_api, _node);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("ConsoleWindow_M07", ex.Message);
        }
    }
}
