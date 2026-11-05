using Avalonia.Input;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     물리 키 위치 → PC/AT XT 스캔코드(set 1, 0xE0nn = 확장 키). WPF 콘솔과 같은 방식 — 키보드 배열(한글·영문 등)과
///     상관없이 같은 자리의 키를 게스트에 보내 단축키·조합키가 그대로 동작한다(QEMU 확장 키 이벤트).
///     태블릿에 붙인 USB·블루투스 키보드, 데스크톱 키보드 공용.
/// </summary>
public static class PhysicalKeys
{
    private static readonly Dictionary<PhysicalKey, int> Map = new()
    {
        [PhysicalKey.Escape] = 0x01,
        [PhysicalKey.Digit1] = 0x02, [PhysicalKey.Digit2] = 0x03, [PhysicalKey.Digit3] = 0x04,
        [PhysicalKey.Digit4] = 0x05, [PhysicalKey.Digit5] = 0x06, [PhysicalKey.Digit6] = 0x07,
        [PhysicalKey.Digit7] = 0x08, [PhysicalKey.Digit8] = 0x09, [PhysicalKey.Digit9] = 0x0A,
        [PhysicalKey.Digit0] = 0x0B, [PhysicalKey.Minus] = 0x0C, [PhysicalKey.Equal] = 0x0D,
        [PhysicalKey.Backspace] = 0x0E, [PhysicalKey.Tab] = 0x0F,
        [PhysicalKey.Q] = 0x10, [PhysicalKey.W] = 0x11, [PhysicalKey.E] = 0x12, [PhysicalKey.R] = 0x13,
        [PhysicalKey.T] = 0x14, [PhysicalKey.Y] = 0x15, [PhysicalKey.U] = 0x16, [PhysicalKey.I] = 0x17,
        [PhysicalKey.O] = 0x18, [PhysicalKey.P] = 0x19, [PhysicalKey.BracketLeft] = 0x1A,
        [PhysicalKey.BracketRight] = 0x1B, [PhysicalKey.Enter] = 0x1C, [PhysicalKey.ControlLeft] = 0x1D,
        [PhysicalKey.A] = 0x1E, [PhysicalKey.S] = 0x1F, [PhysicalKey.D] = 0x20, [PhysicalKey.F] = 0x21,
        [PhysicalKey.G] = 0x22, [PhysicalKey.H] = 0x23, [PhysicalKey.J] = 0x24, [PhysicalKey.K] = 0x25,
        [PhysicalKey.L] = 0x26, [PhysicalKey.Semicolon] = 0x27, [PhysicalKey.Quote] = 0x28,
        [PhysicalKey.Backquote] = 0x29, [PhysicalKey.ShiftLeft] = 0x2A, [PhysicalKey.Backslash] = 0x2B,
        [PhysicalKey.Z] = 0x2C, [PhysicalKey.X] = 0x2D, [PhysicalKey.C] = 0x2E, [PhysicalKey.V] = 0x2F,
        [PhysicalKey.B] = 0x30, [PhysicalKey.N] = 0x31, [PhysicalKey.M] = 0x32, [PhysicalKey.Comma] = 0x33,
        [PhysicalKey.Period] = 0x34, [PhysicalKey.Slash] = 0x35, [PhysicalKey.ShiftRight] = 0x36,
        [PhysicalKey.NumPadMultiply] = 0x37, [PhysicalKey.AltLeft] = 0x38, [PhysicalKey.Space] = 0x39,
        [PhysicalKey.CapsLock] = 0x3A,
        [PhysicalKey.F1] = 0x3B, [PhysicalKey.F2] = 0x3C, [PhysicalKey.F3] = 0x3D, [PhysicalKey.F4] = 0x3E,
        [PhysicalKey.F5] = 0x3F, [PhysicalKey.F6] = 0x40, [PhysicalKey.F7] = 0x41, [PhysicalKey.F8] = 0x42,
        [PhysicalKey.F9] = 0x43, [PhysicalKey.F10] = 0x44, [PhysicalKey.NumLock] = 0x45,
        [PhysicalKey.ScrollLock] = 0x46, [PhysicalKey.NumPad7] = 0x47, [PhysicalKey.NumPad8] = 0x48,
        [PhysicalKey.NumPad9] = 0x49, [PhysicalKey.NumPadSubtract] = 0x4A, [PhysicalKey.NumPad4] = 0x4B,
        [PhysicalKey.NumPad5] = 0x4C, [PhysicalKey.NumPad6] = 0x4D, [PhysicalKey.NumPadAdd] = 0x4E,
        [PhysicalKey.NumPad1] = 0x4F, [PhysicalKey.NumPad2] = 0x50, [PhysicalKey.NumPad3] = 0x51,
        [PhysicalKey.NumPad0] = 0x52, [PhysicalKey.NumPadDecimal] = 0x53, [PhysicalKey.IntlBackslash] = 0x56,
        [PhysicalKey.F11] = 0x57, [PhysicalKey.F12] = 0x58, [PhysicalKey.IntlRo] = 0x73,
        [PhysicalKey.Convert] = 0x79, [PhysicalKey.NonConvert] = 0x7B, [PhysicalKey.IntlYen] = 0x7D,
        // 한/영 · 한자 키(한국어 키보드) — 리눅스·윈도 게스트가 같은 코드로 받는다
        [PhysicalKey.Lang1] = 0xF2, [PhysicalKey.Lang2] = 0xF1,
        // 확장 키(0xE0 접두)
        [PhysicalKey.NumPadEnter] = 0xE01C, [PhysicalKey.ControlRight] = 0xE01D,
        [PhysicalKey.NumPadDivide] = 0xE035, [PhysicalKey.PrintScreen] = 0xE037, [PhysicalKey.AltRight] = 0xE038,
        [PhysicalKey.Home] = 0xE047, [PhysicalKey.ArrowUp] = 0xE048, [PhysicalKey.PageUp] = 0xE049,
        [PhysicalKey.ArrowLeft] = 0xE04B, [PhysicalKey.ArrowRight] = 0xE04D, [PhysicalKey.End] = 0xE04F,
        [PhysicalKey.ArrowDown] = 0xE050, [PhysicalKey.PageDown] = 0xE051, [PhysicalKey.Insert] = 0xE052,
        [PhysicalKey.Delete] = 0xE053, [PhysicalKey.MetaLeft] = 0xE05B, [PhysicalKey.MetaRight] = 0xE05C,
        [PhysicalKey.ContextMenu] = 0xE05D, [PhysicalKey.Pause] = 0xE046
    };

    /// <summary>XT 스캔코드. 모르는 키면 0(keysym 만 보낸다).</summary>
    public static int ToXt(PhysicalKey key)
    {
        return Map.GetValueOrDefault(key);
    }

    /// <summary>
    ///     키 하나의 keysym — 서버가 확장 키를 모르면 이것만 쓰인다. 기능 키는 표에서, 글자 키는 배열에 따른 글자(KeySymbol)에서,
    ///     Ctrl·Alt 를 누른 글자 키는 소문자에서.
    /// </summary>
    public static int Keysym(KeyEventArgs e)
    {
        var keysym = KeySyms.FromKey(e.Key);
        if (keysym != 0) return keysym;
        if (e.Key is Key.Space) return 0x20;
        if (e.KeySymbol is { Length: > 0 } symbol && !char.IsControl(symbol[0]))
            return KeySyms.FromChar(char.ConvertToUtf32(symbol, 0));
        return KeySyms.LetterOrDigit(e.Key);
    }
}
