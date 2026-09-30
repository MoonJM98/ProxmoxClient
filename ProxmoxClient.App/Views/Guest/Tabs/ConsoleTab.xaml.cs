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

    private async void OnOpenConsole(object sender, RoutedEventArgs e)
    {
        if (_guest.IsTemplate)
        {
            StatusText.Text = Loc.T("MainWindow_M07");
            return;
        }

        var title = Loc.T("GuestWindow_Title", _guest.Kind.Label(), _guest.VmId, _guest.Name);
        try
        {
            // Owner 미지정 최상위 창 — 같은 게스트 콘솔이 이미 열려 있으면 그 창을 앞으로
            await Services.ConsoleWindows.ShowPreferredAsync(_api, _guest, title, _runPower, _canPowerManage);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("ConsoleWindow_M07", ex.Message);
        }
    }
}
