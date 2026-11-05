namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>글자 꾸밈(SGR).</summary>
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128
}

/// <summary>
///     화면 한 칸. 글자는 유니코드 코드 포인트(0 이면 빈칸), 넓은 글자(한글·한자)는 두 칸을 차지하며 둘째 칸은 Width=0.
///     색은 <see cref="TerminalColor" /> 로 부호화한다(기본 · 팔레트 0~255 · 24비트).
/// </summary>
public struct TerminalCell
{
    public int Rune;
    public byte Width;
    public CellFlags Flags;
    public uint Foreground;
    public uint Background;

    public static TerminalCell Blank(uint background) => new() { Width = 1, Background = background };

    public readonly bool SameStyle(in TerminalCell other)
    {
        return Flags == other.Flags && Foreground == other.Foreground && Background == other.Background;
    }
}

/// <summary>색 부호: 0 = 기본색, 0x01_0000xx = 팔레트 xx, 0x02_rrggbb = 24비트.</summary>
public static class TerminalColor
{
    public const uint Default = 0;
    private const uint IndexedTag = 0x0100_0000;
    private const uint RgbTag = 0x0200_0000;

    public static uint Indexed(int index) => IndexedTag | (uint)(index & 0xFF);

    public static uint Rgb(int r, int g, int b) => RgbTag | (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | b & 0xFF);

    public static bool IsDefault(uint color) => color == Default;

    public static bool IsIndexed(uint color, out int index)
    {
        index = (int)(color & 0xFF);
        return (color & 0xFF00_0000) == IndexedTag;
    }

    public static bool IsRgb(uint color, out uint rgb)
    {
        rgb = color & 0x00FF_FFFF;
        return (color & 0xFF00_0000) == RgbTag;
    }
}

/// <summary>동아시아 넓은 글자·폭 없는 글자(결합 문자) 판정 — 셸(wcwidth)과 같은 칸 수를 쓰도록.</summary>
public static class CharWidth
{
    public static int Of(int cp)
    {
        if (cp < 0x300) return 1;
        if (IsZeroWidth(cp)) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsZeroWidth(int cp)
    {
        return cp is >= 0x0300 and <= 0x036F // 결합 발음 부호
            or >= 0x1160 and <= 0x11FF // 한글 중성·종성 자모(조합용)
            or >= 0x200B and <= 0x200F // 폭 없는 공백·방향 표시
            or >= 0x20D0 and <= 0x20FF
            or >= 0xFE00 and <= 0xFE0F // 이체자 선택자
            or 0xFEFF
            or >= 0x1AB0 and <= 0x1AFF
            or >= 0x1DC0 and <= 0x1DFF
            or >= 0xE0100 and <= 0xE01EF;
    }

    private static bool IsWide(int cp)
    {
        return cp is >= 0x1100 and <= 0x115F // 한글 초성
            or >= 0x231A and <= 0x231B
            or >= 0x2329 and <= 0x232A
            or >= 0x23E9 and <= 0x23EC
            or 0x23F0 or 0x23F3
            or >= 0x25FD and <= 0x25FE
            or >= 0x2614 and <= 0x2615
            or >= 0x2648 and <= 0x2653
            or 0x267F or 0x2693 or 0x26A1 or >= 0x26AA and <= 0x26AB or >= 0x26BD and <= 0x26BE
            or >= 0x26C4 and <= 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or >= 0x26F2 and <= 0x26F3 or 0x26F5
            or 0x26FA or 0x26FD or 0x2705 or >= 0x270A and <= 0x270B or 0x2728 or 0x274C or 0x274E
            or >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF
            or >= 0x2B1B and <= 0x2B1C or 0x2B50 or 0x2B55
            or >= 0x2E80 and <= 0x303E // CJK 부수·기호
            or >= 0x3041 and <= 0x33FF // 가나·한글 호환 자모·CJK 호환
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3 // 한글 음절
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60 // 전각
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F // 그림 문자
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x1FA70 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;
    }
}
