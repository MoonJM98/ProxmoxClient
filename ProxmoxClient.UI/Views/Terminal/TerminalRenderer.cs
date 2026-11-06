using System.Globalization;
using System.Text;
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
    /// <summary>모노 글꼴 후보 — 운영체제마다 처음 있는 것을 쓴다.</summary>
    private static readonly string[] MonoFamilies =
    [
        "Cascadia Mono", "Consolas", "Menlo", "SF Mono", "JetBrains Mono", "DejaVu Sans Mono", "Ubuntu Mono",
        "Liberation Mono", "Droid Sans Mono", "Noto Sans Mono", "Roboto Mono", "monospace"
    ];

    /// <summary>처음 기본색 — 실제 색은 터미널마다(<see cref="ScreenForeground" />·<see cref="ScreenBackground" />).</summary>
    public const uint DefaultForeground = TerminalColor.DefaultForegroundRgb;

    public const uint DefaultBackground = TerminalColor.DefaultBackgroundRgb;

    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = [];
    private readonly Dictionary<(int Rune, bool Bold), Fallback?> _fallbacks = [];
    private readonly List<GlyphInfo> _runGlyphs = [];
    private readonly List<char> _runChars = [];
    private readonly Dictionary<(string Text, bool Bold, uint Color), FormattedText> _clusters = [];

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
        _clusters.Clear();
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

    /// <summary>색을 읽을 터미널(팔레트·기본색·화면 반전) — 없으면 기본값. 그리기 전에 <see cref="BeginFrame" /> 로 정한다.</summary>
    public TerminalBuffer? Buffer { get; private set; }

    /// <summary>깜박이는 글자(SGR 5)를 지금 감춘다(깜박임의 꺼진 때).</summary>
    public bool BlinkOff { get; set; }

    /// <summary>이번 그림에 깜박이는 글자가 있었다 — 있을 때만 깜박임 시계를 돌린다.</summary>
    public bool DrewBlink { get; private set; }

    /// <summary>한 화면을 그리기 전에 — 색을 읽을 터미널을 정하고 깜박임 표시를 지운다.</summary>
    public void BeginFrame(TerminalBuffer buffer)
    {
        Buffer = buffer;
        DrewBlink = false;
    }

    /// <summary>지금 화면의 기본 글자색(OSC 10, 화면 반전이면 바탕색).</summary>
    public uint ScreenForeground => Buffer is not { } b ? DefaultForeground :
        b.ReverseVideo ? b.DefaultBackground : b.DefaultForeground;

    /// <summary>지금 화면의 기본 바탕색(OSC 11, 화면 반전이면 글자색).</summary>
    public uint ScreenBackground => Buffer is not { } b ? DefaultBackground :
        b.ReverseVideo ? b.DefaultForeground : b.DefaultBackground;

    /// <param name="brighten">SGR 30~37 로 고른 굵은 글자 — 기본 8색을 밝은 색으로(xterm 관례).</param>
    public uint Resolve(uint color, bool foreground, bool brighten)
    {
        if (TerminalColor.IsRgb(color, out var rgb)) return rgb;
        if (TerminalColor.IsIndexed(color, out var index))
        {
            if (brighten && index < 8) index += 8;
            return Buffer?.PaletteColor(index) ?? TerminalColor.DefaultPalette[index];
        }

        return foreground ? ScreenForeground : ScreenBackground;
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

    /// <summary>칸의 실제 글자색·바탕색(반전·숨김·흐림·깜박임 반영). FgAlpha 는 늘 0xFF(흐림은 바탕 쪽으로 섞는다).</summary>
    public (uint Fg, uint Bg, byte FgAlpha) Colors(in TerminalCell cell)
    {
        var flags = cell.Flags;
        var bold = (flags & CellFlags.Bold) != 0;
        var fg = Resolve(cell.Foreground, true, bold && (flags & CellFlags.BasicColor) != 0);
        var bg = Resolve(cell.Background, false, false);
        if ((flags & CellFlags.Inverse) != 0) (fg, bg) = (bg, fg);
        if ((flags & CellFlags.Faint) != 0) fg = Mix(fg, bg);
        if ((flags & CellFlags.Blink) != 0)
        {
            DrewBlink = true;
            if (BlinkOff) fg = bg;
        }

        if ((flags & CellFlags.Hidden) != 0) fg = bg;
        return (fg, bg, 0xFF);
    }

    /// <summary>두 색의 가운데(흐린 글자 — 반투명보다 바탕 위에서 고르게 보인다).</summary>
    private static uint Mix(uint a, uint b)
    {
        return ((a >> 16 & 0xFF) + (b >> 16 & 0xFF)) / 2 << 16 | ((a >> 8 & 0xFF) + (b >> 8 & 0xFF)) / 2 << 8 |
               ((a & 0xFF) + (b & 0xFF)) / 2;
    }

    // ------------------------------------------------------------ 한 줄 그리기

    /// <summary>한 줄 — 결합 문자가 붙은 칸(<see cref="CellFlags.Combined" />)까지 그린다.</summary>
    public void DrawLine(DrawingContext context, TerminalBuffer.Line line, int columns, Point origin)
    {
        DrawLine(context, line.Cells, columns, origin, line);
    }

    /// <summary>한 줄의 바탕 → 밑줄·윗줄·취소선 → 글자 순으로 그린다.</summary>
    public void DrawLine(DrawingContext context, TerminalCell[] cells, int columns, Point origin,
        TerminalBuffer.Line? line = null)
    {
        var cw = CellWidth;
        var ch = CellHeight;
        var count = Math.Min(columns, cells.Length);
        var screenBg = ScreenBackground;

        // 바탕: 같은 색이 이어지는 구간을 한 사각형으로
        for (var c = 0; c < count;)
        {
            var bg = Colors(cells[c]).Bg;
            var start = c;
            while (c < count && Colors(cells[c]).Bg == bg) c++;
            if (bg == screenBg) continue;

            var x0 = Math.Round(origin.X + start * cw);
            var x1 = Math.Round(origin.X + c * cw);
            context.FillRectangle(Brush(bg), new Rect(x0, origin.Y, x1 - x0, ch));
        }

        DrawDecorations(context, cells, count, origin);

        // 글자
        uint runFg = 0;
        byte runAlpha = 0;
        var runBold = false;
        var runItalic = false;
        var runStart = 0;
        for (var c = 0; c < count; c++)
        {
            var cell = cells[c];
            if (cell.Width == 0) continue;

            var (fg, bg, alpha) = Colors(cell);
            var rune = cell.Rune;
            var bold = (cell.Flags & CellFlags.Bold) != 0;
            var italic = (cell.Flags & CellFlags.Italic) != 0;
            var x = origin.X + c * cw;

            var face = bold ? _bold : _regular;
            var cluster = cell.Flags.HasFlag(CellFlags.Combined) ? line?.GetExtra(c) : null;
            var blank = cluster is null && rune is 0 or ' ' || fg == bg;
            var primary = !blank && cluster is null && cell.Width == 1 && rune <= 0xFFFF && !BoxDrawing.Handles(rune) &&
                          face.CharacterToGlyphMap.TryGetGlyph(rune, out _);

            if (_runChars.Count > 0 &&
                (!primary || fg != runFg || alpha != runAlpha || bold != runBold || italic != runItalic))
                FlushRun(context, origin, runStart, runFg, runAlpha, runBold, runItalic);

            if (blank) continue;

            if (primary)
            {
                if (_runChars.Count == 0)
                {
                    runStart = c;
                    runFg = fg;
                    runAlpha = alpha;
                    runBold = bold;
                    runItalic = italic;
                }

                face.CharacterToGlyphMap.TryGetGlyph(rune, out var glyph);
                _runGlyphs.Add(new GlyphInfo(glyph, _runChars.Count, cw));
                _runChars.Add((char)rune);
                continue;
            }

            if (cluster is not null)
                DrawCluster(context, rune, cluster, cell.Width, bold, fg, alpha, new Point(x, origin.Y));
            else
                DrawSingle(context, rune, cell.Width, bold, Brush(fg, alpha), new Point(x, origin.Y), italic);
        }

        if (_runChars.Count > 0) FlushRun(context, origin, runStart, runFg, runAlpha, runBold, runItalic);
    }

    /// <summary>밑줄(한 줄·두 줄·물결·점·끊은 줄, SGR 58 색)·윗줄(53)·취소선(9) — 같은 꾸밈이 이어지는 구간을 한 번에.</summary>
    private void DrawDecorations(DrawingContext context, TerminalCell[] cells, int count, Point origin)
    {
        const CellFlags lines = CellFlags.Underline | CellFlags.Overline | CellFlags.Strike;
        var cw = CellWidth;
        for (var c = 0; c < count;)
        {
            var cell = cells[c];
            if ((cell.Flags & lines) == 0)
            {
                c++;
                continue;
            }

            var (fg, bg, _) = Colors(cell);
            var key = Decoration(cell, fg);
            var start = c;
            for (c++; c < count; c++)
            {
                var next = cells[c];
                if ((next.Flags & lines) == 0) break;

                var nextFg = Colors(next).Fg;
                if (nextFg != fg || Decoration(next, nextFg) != key) break;
            }

            if (fg == bg) continue; // 숨긴 글자·깜박임의 꺼진 때

            var x0 = Math.Round(origin.X + start * cw);
            var x1 = Math.Round(origin.X + c * cw);
            var t = Math.Max(1, Math.Round(FontSize / 14));
            var brush = Brush(fg);
            if ((cell.Flags & CellFlags.Strike) != 0)
                context.FillRectangle(brush, new Rect(x0, origin.Y + Math.Round(CellHeight / 2), x1 - x0, t));
            if ((cell.Flags & CellFlags.Overline) != 0)
                context.FillRectangle(brush, new Rect(x0, origin.Y, x1 - x0, t));
            if (key.Style != UnderlineStyle.None)
                DrawUnderline(context, key.Style, Brush(key.Color), x0, x1, origin.Y, t);
        }
    }

    /// <summary>구간을 나눌 꾸밈 — 밑줄 모양·밑줄 색·윗줄·취소선.</summary>
    private (UnderlineStyle Style, uint Color, CellFlags Lines) Decoration(in TerminalCell cell, uint fg)
    {
        var style = cell.Underline;
        var color = fg;
        if (style != UnderlineStyle.None && Buffer?.UnderlineColor(cell) is { } code && code != TerminalColor.Default)
            color = Resolve(code, true, false);
        return (style, color, cell.Flags & (CellFlags.Overline | CellFlags.Strike));
    }

    private void DrawUnderline(DrawingContext context, UnderlineStyle style, IBrush brush, double x0, double x1,
        double top, double t)
    {
        var bottom = top + CellHeight;
        var y = Math.Round(top + Baseline + Math.Max(1, FontSize / 12));
        switch (style)
        {
            case UnderlineStyle.Double:
            {
                y = Math.Min(y, bottom - 3 * t);
                context.FillRectangle(brush, new Rect(x0, y, x1 - x0, t));
                context.FillRectangle(brush, new Rect(x0, y + 2 * t, x1 - x0, t));
                return;
            }
            case UnderlineStyle.Curly:
            {
                // 칸마다 물결 한 번 — 이웃 칸과 이어진다
                var amp = Math.Max(1.5, t * 1.5);
                var mid = Math.Min(y + t / 2, bottom - amp - t / 2);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(x0, mid), false);
                    var half = CellWidth / 2;
                    var up = true;
                    for (var x = x0; x < x1 - 0.5; x += half, up = !up)
                        g.QuadraticBezierTo(new Point(x + half / 2, mid + (up ? -2 : 2) * amp),
                            new Point(Math.Min(x + half, x1), mid));
                    g.EndFigure(false);
                }

                context.DrawGeometry(null, new Pen(brush, t), geometry);
                return;
            }
            case UnderlineStyle.Dotted or UnderlineStyle.Dashed:
            {
                y = Math.Min(y, bottom - t);
                var dash = style == UnderlineStyle.Dotted ? new DashStyle([1, 1], 0) : new DashStyle([3, 2], 0);
                context.DrawLine(new Pen(brush, t, dash), new Point(x0, y + t / 2), new Point(x1, y + t / 2));
                return;
            }
            default:
                context.FillRectangle(brush, new Rect(x0, Math.Min(y, bottom - t), x1 - x0, t));
                return;
        }
    }

    /// <summary>
    ///     기울인 글자(SGR 3) — 앱에 넣은 글꼴에 기울임꼴이 없어 글리프를 비스듬히 민다. 기준점은 글자 높이 가운데쯤이라
    ///     위는 오른쪽·아래는 왼쪽으로 조금씩 나가 칸에서 크게 벗어나지 않는다.
    /// </summary>
    private DrawingContext.PushedState? Italic(DrawingContext context, bool italic, Point baseline)
    {
        if (!italic) return null;

        var pivotY = baseline.Y - CellHeight * 0.3;
        return context.PushTransform(Matrix.CreateTranslation(0, -pivotY) * Matrix.CreateSkew(-0.2, 0) *
                                     Matrix.CreateTranslation(0, pivotY));
    }

    private void FlushRun(DrawingContext context, Point origin, int start, uint fg, byte alpha, bool bold, bool italic)
    {
        var chars = _runChars.ToArray();
        var glyphs = _runGlyphs.ToArray();
        _runChars.Clear();
        _runGlyphs.Clear();
        var baseline = new Point(origin.X + start * CellWidth, origin.Y + Baseline);
        var run = new GlyphRun(bold ? _bold : _regular, FontSize, chars, glyphs, baseline);
        using (Italic(context, italic, baseline))
        {
            context.DrawGlyphRun(Brush(fg, alpha), run);
        }
    }

    /// <summary>글자 하나를 칸(1·2칸) 안에 그린다 — 선·블록 문자는 도형으로, 나머지는 대체 글꼴로 가운데에.</summary>
    public void DrawSingle(DrawingContext context, int rune, int width, bool bold, IBrush brush, Point cellOrigin,
        bool italic = false)
    {
        var span = CellWidth * width;
        var cell = new Rect(cellOrigin.X, cellOrigin.Y, span, CellHeight);
        if (BoxDrawing.TryDraw(context, rune, brush, cell, FontSize))
            return;

        if (Find(rune, bold) is not { } fallback)
        {
            // 어느 글꼴에도 없는 글자 — 빈칸 대신 작은 네모(두부)로 무언가 있음을 보인다
            var box = new Rect(Math.Round(cell.X + span * 0.15) + 0.5, Math.Round(cell.Y + CellHeight * 0.2) + 0.5,
                Math.Max(2, Math.Round(span * 0.7) - 1), Math.Max(2, Math.Round(CellHeight * 0.6) - 1));
            context.DrawRectangle(null, new Pen(brush), box);
            return;
        }

        // 칸보다 넓은 글리프는 줄여서 옆 칸을 덮지 않게
        var size = FontSize;
        var advance = fallback.Advance * size;
        if (advance > span + 0.5) size *= span / advance;
        var glyphWidth = fallback.Advance * size;
        var text = char.ConvertFromUtf32(rune).ToCharArray();
        var baseline = new Point(cellOrigin.X + (span - glyphWidth) / 2, cellOrigin.Y + Baseline);
        var run = new GlyphRun(fallback.Face, size, text, [new GlyphInfo(fallback.Glyph, 0, glyphWidth)], baseline);
        using (Italic(context, italic, baseline))
        {
            context.DrawGlyphRun(brush, run);
        }
    }

    /// <summary>
    ///     결합 문자가 붙은 칸 — 글자 묶음을 NFC 로 합쳐(한글 첫가끝 자모 → 음절, e+◌́ → é) 한 글자가 되면
    ///     <see cref="DrawSingle" /> 로, 아니면(ZWJ 이모지·국기 등) 글꼴 대체까지 하는 FormattedText 로 칸 안에 그린다.
    /// </summary>
    public void DrawCluster(DrawingContext context, int rune, string extra, int width, bool bold, uint fg, byte alpha,
        Point cellOrigin)
    {
        var text = (rune is 0 ? " " : char.ConvertFromUtf32(rune)) + extra;
        try
        {
            text = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // 짝 없는 서러게이트 등 — 그대로 그린다
        }

        if (text.EnumerateRunes().Count() == 1)
        {
            DrawSingle(context, char.ConvertToUtf32(text, 0), width, bold, Brush(fg, alpha), cellOrigin);
            return;
        }

        var key = (text, bold, fg | (uint)alpha << 24);
        if (!_clusters.TryGetValue(key, out var formatted))
        {
            var span = CellWidth * width;
            var typeface = new Typeface(new FontFamily(_family + ", " + AppFonts.FamilyName), FontStyle.Normal,
                bold ? FontWeight.Bold : FontWeight.Normal);
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                FontSize, Brush(fg, alpha));
            // 칸보다 넓으면 줄여서 옆 칸을 덮지 않게
            if (formatted.WidthIncludingTrailingWhitespace > span + 0.5)
                formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                    FontSize * span / formatted.WidthIncludingTrailingWhitespace, Brush(fg, alpha));
            if (_clusters.Count > 512) _clusters.Clear();
            _clusters[key] = formatted;
        }

        var w = formatted.WidthIncludingTrailingWhitespace;
        context.DrawText(formatted,
            new Point(cellOrigin.X + (CellWidth * width - w) / 2, cellOrigin.Y + Baseline - formatted.Baseline));
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
