using ProxmoxClient.Core.Rdp;
using ProxmoxClient.UI.Views.Console;

/// <summary>RDP 키 변환(keysym → 스캔코드·유니코드)·클립보드 없는 연결.</summary>
public static class RdpTests
{
    public static void SpecialKeysToScancodes(Checker c)
    {
        c.Equal(0x1C, RdpKeyMap.SpecialScancode(KeySyms.Return), "Return");
        c.Equal(0x0E, RdpKeyMap.SpecialScancode(KeySyms.BackSpace), "BackSpace");
        c.Equal(0x0F, RdpKeyMap.SpecialScancode(KeySyms.Tab), "Tab");
        c.Equal(0x01, RdpKeyMap.SpecialScancode(KeySyms.Escape), "Escape");
        c.Equal(0xE053, RdpKeyMap.SpecialScancode(KeySyms.Delete), "Delete 확장");
        c.Equal(0xE052, RdpKeyMap.SpecialScancode(KeySyms.Insert), "Insert 확장");
        c.Equal(0xE047, RdpKeyMap.SpecialScancode(KeySyms.Home), "Home 확장");
        c.Equal(0xE04F, RdpKeyMap.SpecialScancode(KeySyms.End), "End 확장");
        c.Equal(0xE049, RdpKeyMap.SpecialScancode(KeySyms.PageUp), "PgUp 확장");
        c.Equal(0xE051, RdpKeyMap.SpecialScancode(KeySyms.PageDown), "PgDn 확장");
        c.Equal(0xE04B, RdpKeyMap.SpecialScancode(KeySyms.Left), "←");
        c.Equal(0xE048, RdpKeyMap.SpecialScancode(KeySyms.Up), "↑");
        c.Equal(0xE04D, RdpKeyMap.SpecialScancode(KeySyms.Right), "→");
        c.Equal(0xE050, RdpKeyMap.SpecialScancode(KeySyms.Down), "↓");
        c.Equal(0x3B, RdpKeyMap.SpecialScancode(KeySyms.F1), "F1");
        c.Equal(0x44, RdpKeyMap.SpecialScancode(KeySyms.F1 + 9), "F10");
        c.Equal(0x57, RdpKeyMap.SpecialScancode(KeySyms.F1 + 10), "F11");
        c.Equal(0x58, RdpKeyMap.SpecialScancode(KeySyms.F1 + 11), "F12");
        c.Equal(0x2A, RdpKeyMap.SpecialScancode(KeySyms.ShiftL), "Shift_L");
        c.Equal(0x1D, RdpKeyMap.SpecialScancode(KeySyms.ControlL), "Control_L");
        c.Equal(0xE01D, RdpKeyMap.SpecialScancode(0xFFE4), "Control_R");
        c.Equal(0x38, RdpKeyMap.SpecialScancode(KeySyms.AltL), "Alt_L");
        c.Equal(0xE038, RdpKeyMap.SpecialScancode(0xFFEA), "Alt_R");
        c.Equal(0xE05B, RdpKeyMap.SpecialScancode(KeySyms.SuperL), "Super_L");
        c.Equal(0xE05D, RdpKeyMap.SpecialScancode(0xFF67), "Menu");
        c.Equal(0x3A, RdpKeyMap.SpecialScancode(0xFFE5), "Caps_Lock");
        c.Equal(0x52, RdpKeyMap.SpecialScancode(0xFFB0), "KP_0");
        c.Equal(0x49, RdpKeyMap.SpecialScancode(0xFFB9), "KP_9");
        c.Equal(0, RdpKeyMap.SpecialScancode('a'), "글자는 특수 키 아님");
    }

    public static void KeysymCodePoints(Checker c)
    {
        c.Equal('a', RdpKeyMap.CodePoint('a'), "ASCII 그대로");
        c.Equal(0xE9, RdpKeyMap.CodePoint(0xE9), "Latin-1 그대로");
        c.Equal('한', RdpKeyMap.CodePoint(KeySyms.FromChar('한')), "유니코드 keysym(0x01000000|cp)");
        c.Equal(0x1F600, RdpKeyMap.CodePoint(KeySyms.FromChar(0x1F600)), "BMP 밖");
        c.Equal(-1, RdpKeyMap.CodePoint(0x01000000 | 0xD800), "서로게이트 반쪽은 버림");
        c.Equal(-1, RdpKeyMap.CodePoint(0x01000000 | 0x07), "제어 문자 버림");
        c.Equal(-1, RdpKeyMap.CodePoint(0x9F), "C1 제어 문자 버림");
        c.Equal(-1, RdpKeyMap.CodePoint(0xFE50), "알 수 없는 keysym 버림");
    }

    public static void RouteSelection(Checker c)
    {
        c.Equal((RdpKeyRoute.Unicode, (int)'한'), RdpKeyMap.Resolve(KeySyms.FromChar('한'), false), "한글은 유니코드");
        c.Equal((RdpKeyRoute.Unicode, (int)'A'), RdpKeyMap.Resolve('A', false), "대문자도 유니코드(배치 무관)");
        c.Equal((RdpKeyRoute.Scancode, 0x1C), RdpKeyMap.Resolve(KeySyms.FromChar('\n'), false), "줄바꿈은 Return 키");
        c.Equal((RdpKeyRoute.Scancode, 0xE053), RdpKeyMap.Resolve(KeySyms.Delete, true), "특수 키는 늘 스캔코드");
        c.Equal((RdpKeyRoute.Scancode, 0x2E), RdpKeyMap.Resolve('c', true), "Ctrl+c 는 US 스캔코드(단축키)");
        c.Equal((RdpKeyRoute.Scancode, 0x2E), RdpKeyMap.Resolve('C', true), "Ctrl+C 대문자도 같은 키");
        c.Equal((RdpKeyRoute.Scancode, 0x0B), RdpKeyMap.Resolve('0', true), "Alt+0");
        c.Equal((RdpKeyRoute.Scancode, 0x35), RdpKeyMap.Resolve('/', true), "Ctrl+/");
        c.Equal((RdpKeyRoute.Unicode, (int)'한'), RdpKeyMap.Resolve(KeySyms.FromChar('한'), true),
            "US 키가 없는 글자는 수식 키가 있어도 유니코드");
        c.Equal((RdpKeyRoute.Drop, 0), RdpKeyMap.Resolve(0x01000000 | 0x01, false), "제어 문자는 버림");
        c.Is(RdpKeyMap.IsShortcutModifier(0x1D) && RdpKeyMap.IsShortcutModifier(0x38) &&
             RdpKeyMap.IsShortcutModifier(0xE05B), "Ctrl·Alt·Win 은 단축키 수식 키");
        c.Is(!RdpKeyMap.IsShortcutModifier(0x2A) && !RdpKeyMap.IsShortcutModifier(0xE038),
            "Shift·AltGr 는 단축키 수식 키 아님");
    }

    /// <summary>
    ///     Windows 가 아니면 클립보드 공유를 켜 두어도 user32 를 찾지 않고 연결을 시작한다(예전에는 DllNotFound 로 실패).
    ///     닫힌 포트로 연결하므로 연결 오류가 나야 정상이다.
    /// </summary>
    public static void ConnectWithoutWindowsClipboard(Checker c)
    {
        if (OperatingSystem.IsWindows()) return;

        var api = new ProxmoxClient.Core.Api.ProxmoxApiClient(
            new ProxmoxClient.Core.Profiles.ConnectionProfile { Host = "127.0.0.1", Port = 1 });
        using var session = new ProxmoxRdpSession(api) { ShareClipboard = true };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext()); // UI 스레드 흉내
        Exception? error = null;
        try
        {
            session.ConnectAsync("pve", 100).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        c.Is(error is not (DllNotFoundException or EntryPointNotFoundException), "user32 를 찾지 않는다", error?.GetType());
        c.Is(!session.ClipboardActive && !ProxmoxRdpSession.ClipboardSupported, "클립보드 없이 연결");
    }
}
