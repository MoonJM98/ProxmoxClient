using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.ViewModels;
using ProxmoxClient.UI.Views.Main;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     콘솔 열기 규칙 한 곳(WPF ConsoleWindows 와 같다) — CT 는 termproxy 터미널, VM 은 디스플레이가 RDP 면 RDP,
///     아니면 VNC. VM 직렬 터미널과 노드 셸(업그레이드·Ceph 설치 명령 포함)도 같은 터미널 화면을 쓴다.
/// </summary>
public static class ConsoleLauncher
{
    /// <summary>게스트 기본 콘솔.</summary>
    public static async Task OpenGuestAsync(ServerSession session, PveResource guest)
    {
        if (guest.Kind == ResourceKind.Lxc)
        {
            await AppServices.Navigator.PushAsync(new TerminalViewModel(session.Api,
                ConsoleTarget.ForGuest(guest.Node, guest.Kind, guest.VmId), Title(guest), guest));
            return;
        }

        var kinds = await GuestConsoleKinds.DetectAsync(session.Api, guest);
        await AppServices.Navigator.PushAsync(new ConsoleViewModel(session, guest,
            kinds.Rdp ? ConsoleProtocol.Rdp : ConsoleProtocol.Vnc));
    }

    /// <summary>VM 직렬 터미널(웹 UI 의 xterm.js) — VM 에 직렬 포트가 있어야 한다.</summary>
    public static Task OpenSerialAsync(ServerSession session, PveResource guest)
    {
        return AppServices.Navigator.PushAsync(new TerminalViewModel(session.Api,
            ConsoleTarget.ForGuest(guest.Node, guest.Kind, guest.VmId), L.T("Console_SerialTitle", Title(guest)), guest));
    }

    /// <summary>노드 셸 — command 가 있으면(upgrade·ceph_install) 로그인 셸 대신 그 명령.</summary>
    public static Task OpenNodeShellAsync(ProxmoxApiClient api, string node, string? command = null)
    {
        var title = command is null ? L.T("Console_NodeShellTitle", node) : $"{node} · {command}";
        return AppServices.Navigator.PushAsync(new TerminalViewModel(api, ConsoleTarget.ForNode(node, command), title));
    }

    private static string Title(PveResource guest)
    {
        return $"{guest.VmId} · {guest.Name}";
    }
}
