using System.Windows;
using ProxmoxClient.App.Views;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Services;

/// <summary>
///     콘솔·셸 창을 대상마다 하나만 연다 — 이미 열려 있으면 새로 만들지 않고 그 창을 앞으로 가져온다.
///     대상 = 서버(주소:포트) + 게스트 VMID 또는 노드 + 실행 명령. 창이 닫히면 목록에서 빠진다.
/// </summary>
internal static class ConsoleWindows
{
    private static readonly Dictionary<string, Window> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>게스트 콘솔 — VM 은 그래픽 콘솔(VNC 또는 RDP, 종류마다 한 창), CT 는 termproxy 터미널.</summary>
    public static void ShowGuest(ProxmoxApiClient api, PveResource guest, string title, GuestPowerRunner runPower,
        bool canPowerManage, ConsoleProtocol protocol = ConsoleProtocol.Vnc)
    {
        ShowOrActivate(GuestKey(api, guest, protocol), () => guest.Kind == ResourceKind.Lxc
            ? new TerminalWindow(api, guest, title, runPower, canPowerManage)
            : new ConsoleWindow(api, guest, title, runPower, canPowerManage, protocol));
    }

    /// <summary>
    ///     기본 콘솔 — 웹 UI 처럼 VM 디스플레이가 RDP 면 RDP, 아니면 VNC. 이 게스트 콘솔 창이 이미 열려 있으면
    ///     설정을 읽지 않고 그 창을 앞으로 가져온다.
    /// </summary>
    public static async Task ShowPreferredAsync(ProxmoxApiClient api, PveResource guest, string title,
        GuestPowerRunner runPower, bool canPowerManage)
    {
        if (guest.Kind == ResourceKind.Lxc)
        {
            ShowGuest(api, guest, title, runPower, canPowerManage);
            return;
        }

        foreach (var protocol in (ConsoleProtocol[])[ConsoleProtocol.Rdp, ConsoleProtocol.Vnc])
            if (Open.TryGetValue(GuestKey(api, guest, protocol), out var existing))
            {
                BringToFront(existing);
                return;
            }

        var kinds = await GuestConsoleKinds.DetectAsync(api, guest);
        ShowGuest(api, guest, title, runPower, canPowerManage, kinds.Rdp ? ConsoleProtocol.Rdp : ConsoleProtocol.Vnc);
    }

    private static string GuestKey(ProxmoxApiClient api, PveResource guest, ConsoleProtocol protocol)
    {
        var kind = protocol == ConsoleProtocol.Rdp ? "rdp" : "guest";
        return $"{Server(api)}|{kind}|{guest.VmId}";
    }

    /// <summary>VM 직렬 터미널(웹 UI 의 xterm.js 대신 내장 터미널) — VM 에 직렬 포트가 있어야 한다. 그래픽 콘솔과 따로 한 창.</summary>
    public static void ShowSerial(ProxmoxApiClient api, PveResource guest, string title, GuestPowerRunner runPower,
        bool canPowerManage)
    {
        ShowOrActivate($"{Server(api)}|serial|{guest.VmId}",
            () => new TerminalWindow(api, guest, title, runPower, canPowerManage));
    }

    /// <summary>노드 셸 — command 가 있으면(업그레이드·Ceph 설치) 그 명령마다 따로 한 창.</summary>
    public static void ShowNodeShell(ProxmoxApiClient api, string node, string? command = null)
    {
        ShowOrActivate($"{Server(api)}|node|{node}|{command}", () => new TerminalWindow(api, node, command));
    }

    private static string Server(ProxmoxApiClient api)
    {
        // 연결(클라이언트)마다 따로 — 다시 연결하면 끊긴 연결에 묶인 옛 창 대신 새 창을 연다
        var connection = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(api);
        return $"{api.Profile.Host}:{api.Profile.Port}#{connection}";
    }

    private static void ShowOrActivate(string key, Func<Window> create)
    {
        if (Open.TryGetValue(key, out var existing))
        {
            BringToFront(existing);
            return;
        }

        var window = create();
        Open[key] = window;
        window.Closed += (_, _) =>
        {
            if (Open.TryGetValue(key, out var current) && ReferenceEquals(current, window)) Open.Remove(key);
        };
        window.Show();
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Focus();
    }
}
