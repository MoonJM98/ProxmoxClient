using System.Windows;
using ProxmoxClient.App.Services;

namespace ProxmoxClient.App.Views;

/// <summary>SPICE — virt-viewer 로 여는 대체 콘솔(메인 창의 콘솔 메뉴와 같은 <see cref="SpiceLauncher" />).</summary>
public partial class ConsoleWindow
{
    private async void OnOpenSpice(object sender, RoutedEventArgs e)
    {
        BtnSpice.IsEnabled = false;
        try
        {
            // 창을 닫은 뒤 늦게 온 결과는 상태 줄에 쓰지 않는다
            await SpiceLauncher.LaunchAsync(this, _api, _node, _vmid, _guestTitle,
                text => { if (!_closed) SetState(text); });
        }
        finally
        {
            BtnSpice.IsEnabled = true;
        }
    }
}
