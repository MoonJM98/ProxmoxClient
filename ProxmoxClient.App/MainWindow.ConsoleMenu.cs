using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;
using ProxmoxClient.App.Services;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App;

/// <summary>
///     콘솔 버튼 옆 ▾ — 웹 UI 처럼 콘솔 종류를 고른다(VM): 내장 그래픽 콘솔(noVNC 대신), RDP, SPICE,
///     직렬 터미널(내장 터미널). RDP 는 디스플레이가 rdp, SPICE 는 qxl 일 때, 직렬 터미널은 직렬 포트가 있을 때만 —
///     설정을 읽어 켜고 끈다.
/// </summary>
public partial class MainWindow
{
    private async void OnConsoleMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _vm.SelectedGuest is not { } guest || _vm.Api is not { } api) return;

        var title = $"{guest.Kind.Label()} {guest.VmId} — {guest.Name}";
        var canPower = _vm.Permissions?.CanPowerMgmt ?? true;
        var rdp = MenuItem("Console_KindRdp", "IconMonitor", false,
            async () => await OpenConsoleAsync(Core.Vnc.ConsoleProtocol.Rdp));
        rdp.Visibility = Visibility.Collapsed; // 설정을 읽어 RDP 디스플레이일 때만 보인다
        var spice = MenuItem("Console_KindSpice", "IconExternal", false,
            async () => await SpiceLauncher.LaunchAsync(this, api, guest.Node, guest.VmId, title,
                text => _vm.StatusMessage = text));
        var serial = MenuItem("Console_KindSerial", "IconTerminal", false,
            () => ConsoleWindows.ShowSerial(api, guest, title, _vm.RunGuestPowerForAsync, canPower));

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuItem("Console_KindVnc", "IconMonitor", true,
            async () => await OpenConsoleAsync(Core.Vnc.ConsoleProtocol.Vnc)));
        menu.Items.Add(rdp);
        menu.Items.Add(spice);
        menu.Items.Add(serial);
        menu.IsOpen = true;

        var kinds = await GuestConsoleKinds.DetectAsync(api, guest);
        // 디스플레이가 SPICE·RDP 가 아니면 그 항목은 아예 두지 않는다 — SPICE 는 실행 중일 때만 누를 수 있다
        // (RDP 창은 VNC 창처럼 꺼져 있어도 열어 두고 시작하면 붙는다)
        spice.Visibility = kinds.Spice ? Visibility.Visible : Visibility.Collapsed;
        spice.IsEnabled = kinds.Spice && guest.IsRunning;
        rdp.Visibility = kinds.Rdp || !kinds.Known ? Visibility.Visible : Visibility.Collapsed;
        rdp.IsEnabled = true;
        serial.IsEnabled = kinds.Serial && guest.IsRunning;
    }

    private MenuItem MenuItem(string labelKey, string iconKey, bool enabled, Action run)
    {
        var item = new MenuItem { Header = Loc.T(labelKey), IsEnabled = enabled };
        if (TryFindResource(iconKey) is Geometry icon) IconAssist.SetIcon(item, icon);
        item.Click += (_, _) => run();
        return item;
    }
}
