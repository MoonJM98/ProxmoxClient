using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Guest.Hardware;

namespace ProxmoxClient.UI.Views.Guest;

/// <summary>
///     게스트 창의 "콘솔" 탭 — 종류 아이콘·설명·"콘솔 열기" 단추. 실제 화면은 콘솔 화면(<see cref="ConsoleViewModel" />)이 맡는다.
///     WPF ConsoleTab 과 같은 배치(가운데 정렬, 최대 폭 420).
/// </summary>
public sealed class ConsoleTab : UserControl
{
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" }, TextAlignment = TextAlignment.Center };

    public ConsoleTab(ServerSession session, PveResource guest)
    {
        var isCt = guest.Kind == ResourceKind.Lxc;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Classes = { "icon" }, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            Data = HardwareInput.Resource(isCt ? "IconTerminal" : "IconMonitor") as Geometry
        };
        var description = new TextBlock
        {
            Text = Loc.T(isCt ? "ConsoleTab_DescCt" : "ConsoleTab_DescVm"), TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Classes = { "subtle" }, Margin = new Thickness(0, 0, 0, 18)
        };
        var open = HardwareInput.Button(Loc.T("ConsoleTab_Open"), "IconExternal");
        open.Classes.Add("AccentButton");
        open.HorizontalAlignment = HorizontalAlignment.Center;
        open.MinWidth = 170;
        open.Click += async (_, _) => await OpenAsync(session, guest);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon, description, open, _status }
            }
        };
    }

    private async Task OpenAsync(ServerSession session, PveResource guest)
    {
        // 템플릿은 실행할 수 없다 — 콘솔 화면을 열지 않고 안내만 한다(WPF 와 같다)
        if (guest.IsTemplate)
        {
            _status.Text = Loc.T("MainWindow_M07");
            return;
        }

        try
        {
            _status.Text = string.Empty;
            await ConsoleLauncher.OpenGuestAsync(session, guest); // CT 는 터미널, VM 은 RDP/VNC
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("ConsoleWindow_M07", ex.Message);
        }
    }
}
