namespace ProxmoxClient.Core.Rdp;

/// <summary>keysym 키를 RDP 로 보내는 방법.</summary>
public enum RdpKeyRoute
{
    /// <summary>보낼 수 없다(제어 문자·알 수 없는 keysym).</summary>
    Drop,

    /// <summary>XT 스캔코드(0xE0nn = 확장).</summary>
    Scancode,

    /// <summary>유니코드 글자(빠른 경로 유니코드 키 입력) — 게스트 키보드 배치와 상관없이 그 글자가 들어간다.</summary>
    Unicode
}

/// <summary>
///     스캔코드 없이 keysym 만 온 키(소프트 키보드·키 막대·IME 글자) → RDP 입력.
///     특수 키는 스캔코드로, 글자는 유니코드로 보낸다. Ctrl·Alt·Win 을 누른 채 누른 영문·숫자·기호는
///     단축키(Ctrl+C 등)가 되도록 US 배치 스캔코드로 보낸다 — 유니코드 입력은 단축키로 해석되지 않는다.
/// </summary>
public static class RdpKeyMap
{
    /// <summary>스캔코드가 0 인 키 이벤트를 어떻게 보낼지 고른다. code 는 스캔코드 또는 유니코드 코드 포인트.</summary>
    public static (RdpKeyRoute Route, int Code) Resolve(int keysym, bool shortcutModifierHeld)
    {
        if (SpecialScancode(keysym) is var special and not 0) return (RdpKeyRoute.Scancode, special);

        var codePoint = CodePoint(keysym);
        if (codePoint < 0) return (RdpKeyRoute.Drop, 0);

        if (shortcutModifierHeld && UsScancode(codePoint) is var us and not 0) return (RdpKeyRoute.Scancode, us);

        return (RdpKeyRoute.Unicode, codePoint);
    }

    /// <summary>keysym → 유니코드 코드 포인트(글자가 아니면 -1). Latin-1 은 그대로, 그 밖은 0x01000000 + 코드 포인트.</summary>
    public static int CodePoint(int keysym)
    {
        int cp;
        if (keysym is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF) cp = keysym;
        else if ((keysym & 0xFF000000) == 0x01000000) cp = keysym & 0x00FFFFFF;
        else return -1;

        // 제어 문자·서로게이트(글자 하나가 아니다)·범위 밖은 보내지 않는다
        if (cp < 0x20 || cp is >= 0x7F and < 0xA0 || cp is >= 0xD800 and <= 0xDFFF || cp > 0x10FFFF) return -1;

        return cp;
    }

    /// <summary>특수 키 keysym(X11) → XT 스캔코드(0xE0nn = 확장), 아니면 0.</summary>
    public static int SpecialScancode(int keysym)
    {
        return keysym switch
        {
            0xFF08 => 0x0E, // BackSpace
            0xFF09 => 0x0F, // Tab
            0xFE20 => 0x0F, // ISO_Left_Tab(Shift+Tab)
            0xFF0D => 0x1C, // Return
            0xFF1B => 0x01, // Escape
            0xFFFF => 0xE053, // Delete
            0xFF50 => 0xE047, // Home
            0xFF51 => 0xE04B, // Left
            0xFF52 => 0xE048, // Up
            0xFF53 => 0xE04D, // Right
            0xFF54 => 0xE050, // Down
            0xFF55 => 0xE049, // Page_Up
            0xFF56 => 0xE051, // Page_Down
            0xFF57 => 0xE04F, // End
            0xFF63 => 0xE052, // Insert
            0xFF61 => 0xE037, // Print
            0xFF67 => 0xE05D, // Menu
            0xFF14 => 0x46, // Scroll_Lock
            0xFF7F => 0x45, // Num_Lock
            0xFFE5 => 0x3A, // Caps_Lock
            0xFFE1 => 0x2A, // Shift_L
            0xFFE2 => 0x36, // Shift_R
            0xFFE3 => 0x1D, // Control_L
            0xFFE4 => 0xE01D, // Control_R
            0xFFE9 or 0xFFE7 => 0x38, // Alt_L · Meta_L
            0xFFEA or 0xFFE8 or 0xFE03 => 0xE038, // Alt_R · Meta_R · ISO_Level3_Shift(AltGr)
            0xFFEB => 0xE05B, // Super_L
            0xFFEC => 0xE05C, // Super_R
            >= 0xFFBE and <= 0xFFC7 => 0x3B + (keysym - 0xFFBE), // F1~F10
            0xFFC8 => 0x57, // F11
            0xFFC9 => 0x58, // F12
            0xFF8D => 0xE01C, // KP_Enter
            0xFFAA => 0x37, // KP_Multiply
            0xFFAB => 0x4E, // KP_Add
            0xFFAD => 0x4A, // KP_Subtract
            0xFFAE or 0xFF9F => 0x53, // KP_Decimal · KP_Delete
            0xFFAF => 0xE035, // KP_Divide
            >= 0xFFB0 and <= 0xFFB9 => KeypadDigits[keysym - 0xFFB0],
            _ => 0
        };
    }

    /// <summary>KP_0~KP_9 스캔코드.</summary>
    private static readonly int[] KeypadDigits = [0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49];

    /// <summary>US 배치에서 그 글자를 만드는 키의 스캔코드(Shift 상태는 따로) — 단축키용, 없으면 0.</summary>
    public static int UsScancode(int codePoint)
    {
        if (codePoint is >= 'A' and <= 'Z') codePoint += 'a' - 'A';
        return codePoint switch
        {
            >= 'a' and <= 'z' => LetterScancodes[codePoint - 'a'],
            >= '1' and <= '9' => 0x02 + (codePoint - '1'),
            '0' => 0x0B,
            ' ' => 0x39,
            '-' or '_' => 0x0C,
            '=' or '+' => 0x0D,
            '[' or '{' => 0x1A,
            ']' or '}' => 0x1B,
            ';' or ':' => 0x27,
            '\'' or '"' => 0x28,
            '`' or '~' => 0x29,
            '\\' or '|' => 0x2B,
            ',' or '<' => 0x33,
            '.' or '>' => 0x34,
            '/' or '?' => 0x35,
            _ => 0
        };
    }

    /// <summary>a~z 스캔코드(US 배치).</summary>
    private static readonly int[] LetterScancodes =
    [
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C
    ];

    /// <summary>단축키를 만드는 수식 키(Ctrl·Alt·Win, AltGr 제외) 스캔코드인가.</summary>
    public static bool IsShortcutModifier(int xtScanCode)
    {
        return xtScanCode is 0x1D or 0xE01D or 0x38 or 0xE05B or 0xE05C;
    }
}
