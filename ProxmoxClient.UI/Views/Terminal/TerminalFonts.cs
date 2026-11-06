using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     터미널 대체 글꼴 — 기본 글꼴(JetBrains Mono)·Pretendard 에 없는 글자를 기기 글꼴에 기대지 않고 그린다.
///     <list type="bullet">
///         <item>기호(Assets/TerminalFonts, 약 7MB): DejaVu Sans Mono(그리스·키릴·화살표·수학·도형), Noto Sans Math,
///             Noto Sans Symbols 1·2, Symbols Nerd Font Mono(프롬프트 아이콘·Powerline), Noto Emoji(흑백 — 기기에
///             컬러 이모지가 없을 때).</item>
///         <item>한자·가나·중국어(Assets/CjkFonts): Noto Sans Mono CJK KR(16MB). 크니까 그런 글자가 처음 나올 때 읽는다.
///             굵은 글씨는 보통 굵기로 그린다(굵은 파일을 더 넣으면 16MB 가 또 는다).</item>
///     </list>
///     글꼴 묶음은 터미널이 처음 쓸 때 등록한다(앱 시작이 느려지지 않게).
/// </summary>
public static class TerminalFonts
{
    private const string SymbolsKey = "fonts:TermSymbols";
    private const string CjkKey = "fonts:TermCjk";

    /// <summary>기호 글꼴 — 앞에서부터 찾는다(고정폭 DejaVu 를 먼저: 칸에 맞는 모양).</summary>
    private static readonly string[] SymbolFamilies =
        ["DejaVu Sans Mono", "Noto Sans Math", "Noto Sans Symbols", "Noto Sans Symbols 2", "Symbols Nerd Font Mono"];

    private static readonly Lock Gate = new();
    private static bool _symbolsAdded;
    private static bool _cjkAdded;

    /// <summary>글꼴 이름(fonts:...#이름)에서 글자를 찾는다.</summary>
    public static (GlyphTypeface Face, ushort Glyph)? TryGlyph(string family, int rune, FontWeight weight)
    {
        return FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out var face) &&
               face.CharacterToGlyphMap.TryGetGlyph(rune, out var glyph) && glyph != 0
            ? (face, glyph)
            : null;
    }

    public static (GlyphTypeface Face, ushort Glyph)? Symbol(int rune, FontWeight weight)
    {
        Ensure(ref _symbolsAdded, SymbolsKey, "TerminalFonts");
        foreach (var family in SymbolFamilies)
            if (Exact(SymbolsKey, family, rune, weight) is { } hit)
                return hit;
        return null;
    }

    public static (GlyphTypeface Face, ushort Glyph)? Emoji(int rune, FontWeight weight)
    {
        Ensure(ref _symbolsAdded, SymbolsKey, "TerminalFonts");
        return Exact(SymbolsKey, "Noto Emoji", rune, weight);
    }

    /// <summary>한자·가나 등 — 그런 글자 범위일 때만 큰 글꼴을 읽는다.</summary>
    public static (GlyphTypeface Face, ushort Glyph)? Cjk(int rune, FontWeight weight)
    {
        if (!IsCjkRange(rune)) return null;

        Ensure(ref _cjkAdded, CjkKey, "CjkFonts");
        return Exact(CjkKey, "Noto Sans Mono CJK KR", rune, FontWeight.Normal);
    }

    /// <summary>기기의 컬러 이모지를 먼저 쓸 글자(이모지 표시가 기본인 글자).</summary>
    public static bool IsEmoji(int rune)
    {
        return rune is >= 0x1F000 and <= 0x1FAFF && rune is not (>= 0x1FB00 and <= 0x1FBFF) ||
               rune is >= 0x2600 and <= 0x27BF && CharWidth.Of(rune) == 2 ||
               rune is 0x231A or 0x231B or 0x23E9 or 0x23EA or 0x23EB or 0x23EC or 0x23F0 or 0x23F3 or 0x2B50 or 0x2B55;
    }

    /// <summary>
    ///     한중일 글자 범위 — CJK 기호·가나·한자(확장 A~)·호환 한자·전각 꼴·괘선 밖 한글 자모 호환 등. 이 범위가 아니면
    ///     큰 글꼴을 읽지 않는다.
    /// </summary>
    private static bool IsCjkRange(int rune)
    {
        return rune is >= 0x2E80 and <= 0x9FFF or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF or
            >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFFEF or >= 0x1100 and <= 0x11FF or
            >= 0x20000 and <= 0x3FFFF or >= 0x1F200 and <= 0x1F2FF or >= 0x3000 and <= 0x303F;
    }

    /// <summary>묶음 안의 그 이름 글꼴에서만(가까운 다른 글꼴로 바꿔 주지 않게 이름을 확인한다).</summary>
    private static (GlyphTypeface Face, ushort Glyph)? Exact(string key, string family, int rune, FontWeight weight)
    {
        return FontManager.Current.TryGetGlyphTypeface(new Typeface(key + "#" + family, FontStyle.Normal, weight),
                   out var face) && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) &&
               face.CharacterToGlyphMap.TryGetGlyph(rune, out var glyph) && glyph != 0
            ? (face, glyph)
            : null;
    }

    private static void Ensure(ref bool added, string key, string folder)
    {
        if (Volatile.Read(ref added)) return;

        lock (Gate)
        {
            if (added) return;

            FontManager.Current.AddFontCollection(new EmbeddedFontCollection(new Uri(key, UriKind.Absolute),
                new Uri($"avares://ProxmoxClient.UI/Assets/{folder}", UriKind.Absolute)));
            Volatile.Write(ref added, true);
        }
    }
}
