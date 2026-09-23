using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Guest.Tabs;

/// <summary>
///     게스트 창의 콘솔 탭. 콘솔은 독립 창으로 띄운다 —
///     게스트 창을 닫아도 작업이 끊기지 않고, 작업 표시줄에서 따로 전환할 수 있다.
/// </summary>
public partial class ConsoleTab : UserControl
{
    private readonly ProxmoxApiClient _api;
    private readonly bool _canPowerManage;
    private readonly PveResource _guest;
    private readonly GuestPowerRunner _runPower;

    public ConsoleTab(ProxmoxApiClient api, PveResource guest, GuestPowerRunner runPower, bool canPowerManage)
    {
        InitializeComponent();
        _api = api;
        _guest = guest;
        _runPower = runPower;
        _canPowerManage = canPowerManage;

        var isCt = guest.Kind == ResourceKind.Lxc;
        KindIcon.Data = TryFindResource(isCt ? "IconTerminal" : "IconMonitor") as Geometry;
        DescriptionText.Text = Loc.T(isCt ? "ConsoleTab_DescCt" : "ConsoleTab_DescVm");
    }

    private void OnOpenConsole(object sender, RoutedEventArgs e)
    {
        if (_guest.IsTemplate)
        {
            StatusText.Text = Loc.T("MainWindow_M07");
            return;
        }

        var title = Loc.T("GuestWindow_Title", _guest.Kind.Label(), _guest.VmId, _guest.Name);
        try
        {
            // Owner 미지정: 부모창과 독립된 최상위 창(작업 표시줄 개별 표시, 부모 최소화에 영향받지 않음)
            Window console = _guest.Kind == ResourceKind.Lxc
                ? new TerminalWindow(_api, _guest, title, _runPower, _canPowerManage)
                : new ConsoleWindow(_api, _guest, title, _runPower, _canPowerManage);
            console.Show();
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("ConsoleWindow_M07", ex.Message);
        }
    }
}
