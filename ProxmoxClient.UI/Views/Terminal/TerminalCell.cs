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
    Strike = 128,

    /// <summary>결합 문자·ZWJ 이모지·국기 등이 붙은 칸 — 붙은 글자는 <see cref="TerminalBuffer.Line.GetExtra" /> 에 있다.</summary>
    Combined = 0x8000
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

/// <summary>
///     글자 폭 — 셸(glibc wcwidth)과 같은 칸 수를 쓰도록. 표는 유니코드 16 에서 만든 <see cref="CharWidthTable" />.
///     이체자 선택자(FE0F)는 폭을 늘리지 않는다(glibc·xterm.js 와 같다. Node string-width 는 '☺️' 를 2칸으로 보지만
///     셸은 wcwidth 로 커서를 옮기므로 그쪽에 맞춘다). 국기 글자(지역 표시 문자)는 하나면 1칸, 둘이 짝이면 해석기가 2칸 한 묶음으로.
/// </summary>
public static class CharWidth
{
    public static int Of(int cp)
    {
        if (cp < 0x300) return 1;
        if (In(CharWidthTable.Zero, cp)) return 0;
        return In(CharWidthTable.Wide, cp) ? 2 : 1;
    }

    /// <summary>지역 표시 문자(국기 글자 🇦~🇿).</summary>
    public static bool IsRegionalIndicator(int cp) => cp is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>[시작, 끝] 쌍 배열에서 이진 탐색.</summary>
    private static bool In(int[] ranges, int cp)
    {
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cp < ranges[mid * 2]) hi = mid - 1;
            else if (cp > ranges[mid * 2 + 1]) lo = mid + 1;
            else return true;
        }

        return false;
    }
}
