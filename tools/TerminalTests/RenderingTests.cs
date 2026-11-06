using ProxmoxClient.UI.Views.Terminal;

/// <summary>꾸밈(SGR)·색 OSC·화면 반전 — 그리기에 쓰는 칸 상태와 질의 응답.</summary>
public static class RenderingTests
{
    private static TerminalCell Cell(TerminalBuffer t, int row, int col)
    {
        lock (t.Gate)
            return t.GetVisibleLine(row, 0).Cells[col];
    }

    public static void Italic(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\x1b[3ma\x1b[23mb");
        c.Is(Cell(t, 0, 0).Flags.HasFlag(CellFlags.Italic), "SGR 3 은 기울임");
        c.Is(!Cell(t, 0, 1).Flags.HasFlag(CellFlags.Italic), "SGR 23 은 기울임 끔");
    }

    public static void UnderlineStyles(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\x1b[4ma\x1b[4:3mb\x1b[21mc\x1b[4:4md\x1b[4:5me\x1b[4:2mf\x1b[4:0mg\x1b[4mh\x1b[24mi");
        c.Equal(UnderlineStyle.Single, Cell(t, 0, 0).Underline, "SGR 4 은 한 줄");
        c.Equal(UnderlineStyle.Curly, Cell(t, 0, 1).Underline, "SGR 4:3 은 물결");
        c.Equal(UnderlineStyle.Double, Cell(t, 0, 2).Underline, "SGR 21 은 두 줄");
        c.Equal(UnderlineStyle.Dotted, Cell(t, 0, 3).Underline, "SGR 4:4 는 점");
        c.Equal(UnderlineStyle.Dashed, Cell(t, 0, 4).Underline, "SGR 4:5 는 끊은 줄");
        c.Equal(UnderlineStyle.Double, Cell(t, 0, 5).Underline, "SGR 4:2 는 두 줄");
        c.Equal(UnderlineStyle.None, Cell(t, 0, 6).Underline, "SGR 4:0 은 밑줄 끔");
        c.Equal(UnderlineStyle.None, Cell(t, 0, 8).Underline, "SGR 24 는 밑줄 끔");
    }

    public static void UnderlineColor(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\x1b[4;58;2;255;0;10ma\x1b[58:5:3mb\x1b[59mc\x1b[58:2::1:2:3md\x1b[0me");
        c.Equal(TerminalColor.Rgb(255, 0, 10), t.UnderlineColor(Cell(t, 0, 0)), "58;2;r;g;b 저장");
        c.Equal(TerminalColor.Indexed(3), t.UnderlineColor(Cell(t, 0, 1)), "58:5:n 저장");
        c.Equal(TerminalColor.Default, t.UnderlineColor(Cell(t, 0, 2)), "59 는 밑줄 색 되돌림");
        c.Equal(TerminalColor.Rgb(1, 2, 3), t.UnderlineColor(Cell(t, 0, 3)), "58:2::r:g:b 저장");
        c.Equal(TerminalColor.Default, t.UnderlineColor(Cell(t, 0, 4)), "SGR 0 은 밑줄 색도 지움");
        c.Equal(TerminalColor.Default, Cell(t, 0, 0).Foreground, "밑줄 색은 글자색을 바꾸지 않음");
    }

    public static void OverlineStrike(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\x1b[53;9ma\x1b[55;29mb");
        c.Is(Cell(t, 0, 0).Flags.HasFlag(CellFlags.Overline), "SGR 53 은 윗줄");
        c.Is(Cell(t, 0, 0).Flags.HasFlag(CellFlags.Strike), "SGR 9 는 취소선");
        c.Is(!Cell(t, 0, 1).Flags.HasFlag(CellFlags.Overline), "SGR 55 는 윗줄 끔");
        c.Is(!Cell(t, 0, 1).Flags.HasFlag(CellFlags.Strike), "SGR 29 는 취소선 끔");
    }

    public static void BasicColorForBold(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\x1b[1;31ma\x1b[38;5;1mb\x1b[39mc");
        c.Is(Cell(t, 0, 0).Flags.HasFlag(CellFlags.BasicColor), "SGR 31 은 기본 8색 표시(굵으면 밝게)");
        c.Is(!Cell(t, 0, 1).Flags.HasFlag(CellFlags.BasicColor), "38;5;1 은 표시 없음(굵어도 그대로)");
        c.Is(!Cell(t, 0, 2).Flags.HasFlag(CellFlags.BasicColor), "39 는 표시 지움");
    }

    public static void Blink(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\x1b[5ma\x1b[25mb");
        c.Is(Cell(t, 0, 0).Flags.HasFlag(CellFlags.Blink), "SGR 5 는 깜박임");
        c.Is(!Cell(t, 0, 1).Flags.HasFlag(CellFlags.Blink), "SGR 25 는 깜박임 끔");
    }

    public static void PaletteOsc(Checker c)
    {
        var t = Checker.Term();
        c.Equal("", Checker.Feed(t, "\x1b]4;1;rgb:12/34/56;200;#abcdef\x07"), "설정은 답이 없음");
        c.Equal(0x123456u, t.PaletteColor(1), "OSC 4 rgb:");
        c.Equal(0xABCDEFu, t.PaletteColor(200), "OSC 4 #rrggbb(여러 쌍)");
        c.Equal("\x1b]4;1;rgb:1212/3434/5656\x07", Checker.Feed(t, "\x1b]4;1;?\x07"), "OSC 4 질의(BEL)");
        c.Equal("\x1b]4;200;rgb:abab/cdcd/efef\x1b\\", Checker.Feed(t, "\x1b]4;200;?\x1b\\"), "OSC 4 질의(ST)");
        Checker.Feed(t, "\x1b]4;2;rgb:f/8/0\x07\x1b]4;3;rgb:ffff/0000/8080\x07\x1b]4;4;#fff\x07");
        c.Equal(0xFF8800u, t.PaletteColor(2), "rgb: 한 자리 16진");
        c.Equal(0xFF0080u, t.PaletteColor(3), "rgb: 네 자리 16진");
        c.Equal(0xFFFFFFu, t.PaletteColor(4), "#rgb");
        Checker.Feed(t, "\x1b]104;1\x07");
        c.Equal(TerminalColor.DefaultPalette[1], t.PaletteColor(1), "OSC 104;n 은 그 색만 되돌림");
        c.Equal(0xABCDEFu, t.PaletteColor(200), "다른 색은 그대로");
        Checker.Feed(t, "\x1b]104\x07");
        c.Equal(TerminalColor.DefaultPalette[200], t.PaletteColor(200), "OSC 104 는 모두 되돌림");
    }

    public static void DynamicColors(Checker c)
    {
        var t = Checker.Term();
        c.Equal("\x1b]10;rgb:cccc/cccc/cccc\x07", Checker.Feed(t, "\x1b]10;?\x07"), "OSC 10 기본 글자색 질의");
        c.Equal("\x1b]11;rgb:0c0c/0c0c/0c0c\x1b\\", Checker.Feed(t, "\x1b]11;?\x1b\\"), "OSC 11 기본 바탕색 질의");
        Checker.Feed(t, "\x1b]10;#102030\x07\x1b]11;rgb:ff/ff/ff\x07\x1b]12;rgb:0/f/0\x07");
        c.Equal("\x1b]10;rgb:1010/2020/3030\x07", Checker.Feed(t, "\x1b]10;?\x07"), "OSC 10 바꾼 뒤 질의");
        c.Equal("\x1b]11;rgb:ffff/ffff/ffff\x07", Checker.Feed(t, "\x1b]11;?\x07"), "OSC 11 바꾼 뒤 질의");
        c.Equal(0x00FF00u, t.CursorColor, "OSC 12 커서 색");
        Checker.Feed(t, "\x1b]10;#000000;#111111\x07");
        c.Equal(0x111111u, t.DefaultBackground, "OSC 10;글자;바탕 이어서");
        Checker.Feed(t, "\x1b]110\x07\x1b]111\x07\x1b]112\x07");
        c.Equal(TerminalColor.DefaultForegroundRgb, t.DefaultForeground, "OSC 110 은 글자색 되돌림");
        c.Equal(TerminalColor.DefaultBackgroundRgb, t.DefaultBackground, "OSC 111 은 바탕색 되돌림");
        c.Equal(null, t.CursorColor, "OSC 112 는 커서 색 되돌림");
        Checker.Feed(t, "\x1b]0;제목\x07");
        c.Equal("제목", t.Title, "제목 OSC 는 그대로");
    }

    public static void ReverseVideo(Checker c)
    {
        var t = Checker.Term();
        c.Equal("\x1b[?5;2$y", Checker.Feed(t, "\x1b[?5$p"), "처음은 꺼짐");
        Checker.Feed(t, "\x1b[?5h");
        c.Is(t.ReverseVideo, "?5h 는 화면 반전");
        c.Equal("\x1b[?5;1$y", Checker.Feed(t, "\x1b[?5$p"), "DECRQM 켜짐");
        Checker.Feed(t, "\x1b[?5l");
        c.Is(!t.ReverseVideo, "?5l 은 반전 끔");
        Checker.Feed(t, "\x1b[?5h\x1b]11;#ffffff\x07\u001bc");
        c.Is(!t.ReverseVideo, "RIS 는 반전 끔");
        c.Equal(TerminalColor.DefaultBackgroundRgb, t.DefaultBackground, "RIS 는 바탕색 되돌림");
    }
}
