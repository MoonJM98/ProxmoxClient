using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Rdp;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Console;

/// <summary>실제 RDP 서버(xrdp)로 끝까지 시험하며 찾은 문제 — 화면 키보드 한글·rdpproxy 쓰기 확인.</summary>
public static class RdpEndToEndTests
{
    /// <summary>
    ///     RDP 화면 키보드: 한글은 두벌식 키가 아니라 keysym 만(→ 유니코드 입력). 영문은 그대로 키 위치(Shift 포함),
    ///     못 보낸 글자로 알리지도 않는다. VNC(기본)는 그대로 두벌식 키.
    /// </summary>
    public static void RdpSoftKeyboardSendsHangulAsUnicode(Checker c)
    {
        var events = new List<(int Xt, int Keysym, bool Down)>();
        var unsent = new List<int>();
        SoftKeys.CharEvents('한', false, events, unsent, unicode: true);
        c.Equal("0:0100D55C:True 0:0100D55C:False",
            string.Join(" ", events.Select(e => $"{e.Xt:X}:{e.Keysym:X8}:{e.Down}")), "RDP 한 → keysym 만");
        c.Equal(0, unsent.Count, "RDP 한글은 못 보낸 글자가 아니다");
        c.Equal((RdpKeyRoute.Unicode, 0xD55C), RdpKeyMap.Resolve(events[0].Keysym, false), "세션은 유니코드로 보낸다");

        events.Clear();
        SoftKeys.CharEvents('A', false, events, unsent, unicode: true);
        c.Equal("2A 1E 1E 2A", string.Join(" ", events.Select(e => e.Xt.ToString("X"))), "RDP 영문은 Shift + 키 위치 그대로");

        events.Clear();
        SoftKeys.CharEvents('한', false, events, unsent);
        c.Equal("22 25 1F", string.Join(" ", events.Where(e => e.Down).Select(e => e.Xt.ToString("X"))), "VNC 는 두벌식 g k s");
    }

    /// <summary>rdpproxy(RDP 콘솔 표 발급)는 vncproxy 처럼 확인 없이 보낸다 — 콘솔을 열 때마다 쓰기 확인 창이 뜨던 문제.</summary>
    public static void RdpProxyNeedsNoWriteConfirmation(Checker c)
    {
        var task = WriteConfirmation.GuardAsync(new WriteRequest("POST", "nodes/pve1/qemu/101/rdpproxy"), default);
        c.Is(task.IsCompletedSuccessfully && task.Result, "rdpproxy 는 바로 허용", task.Status);
    }
}
