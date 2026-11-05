using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     칸 격자 그리기. 고정폭 글꼴의 글리프를 한 줄씩 같은 모양 덩어리(GlyphRun)로 그려 글자마다 그리기 호출을 만들지 않는다.
///     글꼴에 없는 글자(한글·이모지 등)는 시스템 대체 글꼴로 칸 가운데에, 선 그리기·블록 문자는 칸에 꼭 맞는 도형으로
///     그려 줄 사이가 끊기지 않게 한다.
/// </summary>
internal sealed class TerminalRenderer
{
    /// <summary>Campbell(WPF 터미널과 같은 16색).</summary>
    private static readonly uint[] Base16 =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2
    ];

    /// <summary>모노 글꼴 후보 — 운영체제마다 처음 있는 것을 쓴다.</summary>
    private static readonly string[] MonoFamilies =
    [
        "Cascadia Mono", "Consolas", "Menlo", "SF Mono", "JetBrains Mono", "DejaVu Sans Mono", "Ubuntu Mono",
        "Liberation Mono", "Droid Sans Mono", "Noto Sans Mono", "Roboto Mono", "monospace"
    ];

    private static readonly uint[] Palette = BuildPalette();

    public const uint DefaultForeground = 0xCCCCCC;
    public const uint DefaultBackground = 0x0C0C0C;

    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = [];
    private readonly Dictionary<(int Rune, bool Bold), Fallback?> _fallbacks = [];
    private readonly List<GlyphInfo> _runGlyphs = [];
    private readonly List<char> _runChars = [];

    private GlyphTypeface _regular = null!;
    private GlyphTypeface _bold = null!;
    private string _family = "monospace";

    /// <param name="preferredFamily">설정에서 고른 글꼴 — 없거나 기본값이면 앱에 넣은 JetBrains Mono.</param>
    public TerminalRenderer(double fontSize, string? preferredFamily = null)
    {
        _preferred = preferredFamily;
        SetFontSize(fontSize);
    }

    private readonly string? _preferred;

    public double FontSize { get; private set; }
    public double CellWidth { get; private set; }
    public double CellHeight { get; private set; }
    private double Baseline { get; set; }

    public void SetFontSize(double size)
    {
        FontSize = size;
        _regular = FindMono(FontWeight.Normal, out _family);
        _bold = FindMono(FontWeight.Bold, out _);
        var m = _regular.Metrics;
        var scale = size / m.DesignEmHeight;
        var advance = _regular.CharacterToGlyphMap.TryGetGlyph('M', out var glyph) &&
                      _regular.TryGetHorizontalGlyphAdvance(glyph, out var adv)
            ? adv
            : m.DesignEmHeight * 0.6;
        CellWidth = advance * scale;
        var ascent = -m.Ascent * scale;
        var descent = m.Descent * scale;
        CellHeight = Math.Ceiling(Math.Max(m.LineSpacing * scale, ascent + descent));
        Baseline = Math.Round((CellHeight - (ascent + descent)) / 2 + ascent);
        _fallbacks.Clear();
    }

    /// <summary>
    ///     기본으로 보는 글꼴 이름 — JetBrains Mono, 그리고 WPF 와 같이 쓰는 설정 파일의 기본값(Cascadia Mono:
    ///     Windows 전용이라 다른 OS 에서는 없으므로 앱 글꼴로 통일).
    /// </summary>
    private static bool IsDefaultChoice(string name)
    {
        return name.Equals("JetBrains Mono", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(Core.Vnc.ConsoleSettings.DefaultFontFamily, StringComparison.OrdinalIgnoreCase);
    }

    private GlyphTypeface FindMono(FontWeight weight, out string family)
    {
        // 설정에서 다른 글꼴을 골랐고 그 글꼴이 깔려 있으면 그것을
        if (_preferred is { Length: > 0 } preferred && !IsDefaultChoice(preferred) &&
            FontManager.Current.TryGetGlyphTypeface(new Typeface(preferred, FontStyle.Normal, weight), out var chosen) &&
            chosen.FamilyName.StartsWith(preferred, StringComparison.OrdinalIgnoreCase))
        {
            family = preferred;
            return chosen;
        }

        // 기본은 앱에 넣은 JetBrains Mono — 모든 기기에서 같은 칸 폭·모양
        if (FontManager.Current.TryGetGlyphTypeface(new Typeface(AppFonts.MonoFamilyName, FontStyle.Normal, weight),
                out var embedded) && embedded.FamilyName.Contains("JetBrains", StringComparison.OrdinalIgnoreCase))
        {
            family = AppFonts.MonoFamilyName;
            return embedded;
        }

        foreach (var name in MonoFamilies)
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(name, FontStyle.Normal, weight), out var face) &&
                (name == "monospace" || face.FamilyName.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                family = name;
                return face;
            }

        family = FontFamily.Default.Name;
        return new Typeface(FontFamily.Default, FontStyle.Normal, weight).GlyphTypeface;
    }

    // ------------------------------------------------------------ 색

    public static uint Resolve(uint color, bool foreground, bool bold)
    {
        if (TerminalColor.IsRgb(color, out var rgb)) return rgb;
        if (TerminalColor.IsIndexed(color, out var index))
            return Palette[foreground && bold && index < 8 ? index + 8 : index]; // 굵은 기본 8색은 밝은 색으로(xterm 관례)

        return foreground ? DefaultForeground : DefaultBackground;
    }

    public IImmutableSolidColorBrush Brush(uint rgb, byte alpha = 0xFF)
    {
        var key = rgb | (uint)alpha << 24;
        if (_brushes.TryGetValue(key, out var brush)) return brush;

        brush = new ImmutableSolidColorBrush(Color.FromUInt32(key));
        if (_brushes.Count > 4096) _brushes.Clear();
        _brushes[key] = brush;
        return brush;
    }

    /// <summary>칸의 실제 글자색·바탕색(반전·숨김·흐림 반영).</summary>
    public (uint Fg, uint Bg, byte FgAlpha) Colors(in TerminalCell cell)
    {
        var bold = cell.Flags.HasFlag(CellFlags.Bold);
        var fg = Resolve(cell.Foreground, true, bold);
        var bg = Resolve(cell.Background, false, false);
        if (cell.Flags.HasFlag(CellFlags.Inverse)) (fg, bg) = (bg, fg);
        if (cell.Flags.HasFlag(CellFlags.Hidden)) fg = bg;
        return (fg, bg, cell.Flags.HasFlag(CellFlags.Faint) ? (byte)0x99 : (byte)0xFF);
    }

    private static uint[] BuildPalette()
    {
        var palette = new uint[256];
        Array.Copy(Base16, palette, 16);
        int[] steps = [0, 95, 135, 175, 215, 255];
        for (var i = 0; i < 216; i++)
            palette[16 + i] = (uint)(steps[i / 36] << 16 | steps[i / 6 % 6] << 8 | steps[i % 6]);
        for (var i = 0; i < 24; i++)
        {
            var v = (uint)(8 + i * 10);
            palette[232 + i] = v << 16 | v << 8 | v;
        }

        return palette;
    }

    // ------------------------------------------------------------ 한 줄 그리기

    /// <summary>한 줄의 바탕 → 글자 → 밑줄·취소선 순으로 그린다.</summary>
    public void DrawLine(DrawingContext context, TerminalCell[] cells, int columns, Point origin)
    {
        var cw = CellWidth;
        var ch = CellHeight;
        var count = Math.Min(columns, cells.Length);

        // 바탕: 같은 색이 이어지는 구간을 한 사각형으로
        for (var c = 0; c < count;)
        {
            var bg = Colors(cells[c]).Bg;
            var start = c;
            while (c < count && Colors(cells[c]).Bg == bg) c++;
            if (bg == DefaultBackground) continue;

            var x0 = Math.Round(origin.X + start * cw);
            var x1 = Math.Round(origin.X + c * cw);
            context.FillRectangle(Brush(bg), new Rect(x0, origin.Y, x1 - x0, ch));
        }

        // 글자
        uint runFg = 0;
        byte runAlpha = 0;
        var runBold = false;
        var runStart = 0;
        for (var c = 0; c < count; c++)
        {
            var cell = cells[c];
            if (cell.Width == 0) continue;

            var (fg, bg, alpha) = Colors(cell);
            var rune = cell.Rune;
            var bold = cell.Flags.HasFlag(CellFlags.Bold);
            var x = origin.X + c * cw;

            if (cell.Flags.HasFlag(CellFlags.Underline) && fg != bg)
                context.FillRectangle(Brush(fg, alpha),
                    new Rect(Math.Round(x), origin.Y + Baseline + Math.Max(1, FontSize / 12), Math.Round(cw * cell.Width), Math.Max(1, Math.Round(FontSize / 14))));
            if (cell.Flags.HasFlag(CellFlags.Strike) && fg != bg)
                context.FillRectangle(Brush(fg, alpha),
                    new Rect(Math.Round(x), origin.Y + Math.Round(ch / 2), Math.Round(cw * cell.Width), Math.Max(1, Math.Round(FontSize / 14))));

            var face = bold ? _bold : _regular;
            var blank = rune is 0 or ' ' || fg == bg;
            var primary = !blank && cell.Width == 1 && rune <= 0xFFFF && !BoxDrawing.Handles(rune) &&
                          face.CharacterToGlyphMap.TryGetGlyph(rune, out _);

            if (_runChars.Count > 0 && (!primary || fg != runFg || alpha != runAlpha || bold != runBold))
                FlushRun(context, origin, runStart, runFg, runAlpha, runBold);

            if (blank) continue;

            if (primary)
            {
                if (_runChars.Count == 0)
                {
                    runStart = c;
                    runFg = fg;
                    runAlpha = alpha;
                    runBold = bold;
                }

                face.CharacterToGlyphMap.TryGetGlyph(rune, out var glyph);
                _runGlyphs.Add(new GlyphInfo(glyph, _runChars.Count, cw));
                _runChars.Add((char)rune);
                continue;
            }

            DrawSingle(context, rune, cell.Width, bold, Brush(fg, alpha), new Point(x, origin.Y));
        }

        if (_runChars.Count > 0) FlushRun(context, origin, runStart, runFg, runAlpha, runBold);
    }

    private void FlushRun(DrawingContext context, Point origin, int start, uint fg, byte alpha, bool bold)
    {
        var chars = _runChars.ToArray();
        var glyphs = _runGlyphs.ToArray();
        _runChars.Clear();
        _runGlyphs.Clear();
        var run = new GlyphRun(bold ? _bold : _regular, FontSize, chars, glyphs,
            new Point(origin.X + start * CellWidth, origin.Y + Baseline));
        context.DrawGlyphRun(Brush(fg, alpha), run);
    }

    /// <summary>글자 하나를 칸(1·2칸) 안에 그린다 — 선·블록 문자는 도형으로, 나머지는 대체 글꼴로 가운데에.</summary>
    public void DrawSingle(DrawingContext context, int rune, int width, bool bold, IBrush brush, Point cellOrigin)
    {
        var span = CellWidth * width;
        if (BoxDrawing.TryDraw(context, rune, brush, new Rect(cellOrigin.X, cellOrigin.Y, span, CellHeight), FontSize))
            return;

        if (Find(rune, bold) is not { } fallback) return;

        // 칸보다 넓은 글리프는 줄여서 옆 칸을 덮지 않게
        var size = FontSize;
        var advance = fallback.Advance * size;
        if (advance > span + 0.5) size *= span / advance;
        var glyphWidth = fallback.Advance * size;
        var text = char.ConvertFromUtf32(rune).ToCharArray();
        var run = new GlyphRun(fallback.Face, size, text, [new GlyphInfo(fallback.Glyph, 0, glyphWidth)],
            new Point(cellOrigin.X + (span - glyphWidth) / 2, cellOrigin.Y + Baseline));
        context.DrawGlyphRun(brush, run);
    }

    private Fallback? Find(int rune, bool bold)
    {
        if (_fallbacks.TryGetValue((rune, bold), out var found)) return found;

        Fallback? result = null;
        var face = bold ? _bold : _regular;
        if (face.CharacterToGlyphMap.TryGetGlyph(rune, out var own))
        {
            result = Make(face, own);
        }
        else if (FontManager.Current.TryGetGlyphTypeface(new Typeface(AppFonts.Family, FontStyle.Normal,
                     bold ? FontWeight.Bold : FontWeight.Normal), out var app) &&
                 app.CharacterToGlyphMap.TryGetGlyph(rune, out var appGlyph))
        {
            // 한글 등은 앱에 넣은 Pretendard 로 — 기기마다 대체 글꼴이 달라도(또는 없어도) 같은 모양
            result = Make(app, appGlyph);
        }
        else if (FontManager.Current.TryMatchCharacter(rune, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal,
                     FontStretch.Normal, new FontFamily(_family), CultureInfo.CurrentUICulture, out var typeface) &&
                 typeface.GlyphTypeface is { } other && other.CharacterToGlyphMap.TryGetGlyph(rune, out var glyph))
        {
            result = Make(other, glyph);
        }

        if (_fallbacks.Count > 8192) _fallbacks.Clear();
        _fallbacks[(rune, bold)] = result;
        return result;

        static Fallback Make(GlyphTypeface face, ushort glyph)
        {
            var advance = face.TryGetHorizontalGlyphAdvance(glyph, out var a) ? a : face.Metrics.DesignEmHeight;
            return new Fallback(face, glyph, (double)advance / face.Metrics.DesignEmHeight);
        }
    }

    /// <summary>대체 글꼴 글리프 — Advance 는 글꼴 크기 1 기준 폭.</summary>
    private sealed record Fallback(GlyphTypeface Face, ushort Glyph, double Advance);
}
