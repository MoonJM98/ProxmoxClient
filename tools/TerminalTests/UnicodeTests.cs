using ProxmoxClient.UI.Views.Terminal;

/// <summary>글자 폭·결합 문자·자소 묶음(한글 첫가끝·ZWJ 이모지·국기).</summary>
public static class UnicodeTests
{
    private static (int X, int Y) Cursor(TerminalBuffer t)
    {
        lock (t.Gate) return (t.CursorX, t.CursorY);
    }

    private static TerminalBuffer.Line Row(TerminalBuffer t, int row)
    {
        lock (t.Gate) return t.GetVisibleLine(row, 0);
    }

    private static string Copy(TerminalBuffer t, int row) => t.GetText(t.AbsoluteRow(row, 0), 0, t.AbsoluteRow(row, 0), t.Columns - 1);

    public static void WidthTable(Checker c)
    {
        c.Equal(1, CharWidth.Of('A'), "A");
        c.Equal(2, CharWidth.Of('한'), "한");
        c.Equal(2, CharWidth.Of(0x1F004), "🀄");
        c.Equal(2, CharWidth.Of(0x1F7E0), "🟠");
        c.Equal(1, CharWidth.Of(0x00AD), "소프트 하이픈");
        c.Equal(0, CharWidth.Of(0x200B), "폭 없는 공백");
        c.Equal(0, CharWidth.Of(0x200D), "ZWJ");
        c.Equal(0, CharWidth.Of(0xFE0F), "VS16");
        c.Equal(0, CharWidth.Of(0x0301), "결합 예음 부호");
        c.Equal(1, CharWidth.Of(0x273B), "✻ (EAW N)");
        c.Equal(1, CharWidth.Of(0x23FA), "⏺ (EAW N, 그림 표시 아님)");
        c.Equal(2, CharWidth.Of(0x1112), "한글 초성");
        c.Equal(0, CharWidth.Of(0x1161), "한글 중성");
        c.Equal(0, CharWidth.Of(0x11AB), "한글 종성");
        c.Equal(2, CharWidth.Of(0xA960), "옛한글 초성 확장");
        c.Equal(0, CharWidth.Of(0xD7B0), "옛한글 중성 확장");
        c.Equal(1, CharWidth.Of(0x1F1F0), "지역 표시 문자 하나");
        c.Equal(2, CharWidth.Of(0x1F600), "😀");
        c.Equal(1, CharWidth.Of(0x263A), "☺ (텍스트 표시)");
        c.Equal(2, CharWidth.Of(0x2E80), "CJK 부수");
        c.Equal(2, CharWidth.Of(0x3FFFD), "빈 CJK 자리");
        c.Equal(1, CharWidth.Of(0x00E9), "é");
        c.Equal(0, CharWidth.Of(0x302A), "Mn 이면서 W 인 글자는 0");
    }

    public static void HangulNfd(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "한x");
        c.Equal((3, 0), Cursor(t), "커서(2칸 뒤 x 다음)");
        var line = Row(t, 0);
        c.Equal(0x1112, line.Cells[0].Rune, "첫 칸은 초성");
        c.Equal(2, (int)line.Cells[0].Width, "2칸");
        c.Equal((int)'x', line.Cells[2].Rune, "다음 글자는 2번 칸");
        c.Equal("ᅡᆫ", line.GetExtra(0), "붙은 중성·종성");
        c.Equal("한x", Copy(t, 0), "복사는 받은 코드 포인트 그대로");
        c.Equal("한x", Copy(t, 0).Normalize(), "NFC 로는 한");
    }

    public static void CombiningMark(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "éz");
        c.Equal((2, 0), Cursor(t), "e+◌́ 는 1칸");
        c.Equal((int)'z', Row(t, 0).Cells[1].Rune, "z 는 1번 칸");
        c.Equal("éz", Copy(t, 0), "복사에 결합 문자 포함");

        // 넓은 글자 뒤 결합 문자는 앞 칸(넓은 글자)에 붙는다
        Checker.Feed(t, "\r\n한́");
        c.Equal("́", Row(t, 1).GetExtra(0), "넓은 글자의 앞 칸에 붙음");
        c.Equal((2, 1), Cursor(t), "커서는 그대로");

        // 줄 처음의 결합 문자는 버린다
        Checker.Feed(t, "\r\ńa");
        c.Equal("a", Copy(t, 2), "줄 처음 결합 문자 버림");

        // 줄 끝(미룬 줄바꿈)에서도 마지막 칸에 붙는다
        var u = Checker.Term(4, 3);
        Checker.Feed(u, "abcd́");
        c.Equal("́", Row(u, 0).GetExtra(3), "줄 끝 칸에 붙음");
        c.Equal((3, 0), Cursor(u), "줄 끝 커서");
        Checker.Feed(u, "e");
        c.Equal("e", Copy(u, 1), "다음 글자는 다음 줄");
    }

    public static void ZwjEmoji(Checker c)
    {
        var t = Checker.Term();
        // 👨‍👩‍👧
        Checker.Feed(t, "\U0001F468‍\U0001F469‍\U0001F467x");
        c.Equal((3, 0), Cursor(t), "가족 이모지는 2칸");
        c.Equal((int)'x', Row(t, 0).Cells[2].Rune, "x 는 2번 칸");
        c.Equal("\U0001F468‍\U0001F469‍\U0001F467x", Copy(t, 0), "복사");

        // VS16 은 폭을 늘리지 않는다(wcwidth 와 같다)
        var u = Checker.Term();
        Checker.Feed(u, "☺️x");
        c.Equal((2, 0), Cursor(u), "☺️ 는 1칸");
        c.Equal("☺️x", Copy(u, 0), "VS16 복사");
    }

    public static void Flags(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "\U0001F1F0\U0001F1F7x");
        c.Equal((3, 0), Cursor(t), "🇰🇷 는 2칸");
        var line = Row(t, 0);
        c.Equal(2, (int)line.Cells[0].Width, "한 묶음 2칸");
        c.Equal(0, (int)line.Cells[1].Width, "뒤 칸");
        c.Equal("\U0001F1F0\U0001F1F7x", Copy(t, 0), "복사");

        // 국기 셋째 글자는 새 짝을 시작한다: 🇰🇷🇯🇵 = 4칸
        var u = Checker.Term();
        Checker.Feed(u, "\U0001F1F0\U0001F1F7\U0001F1EF\U0001F1F5");
        c.Equal((4, 0), Cursor(u), "국기 둘은 4칸");

        // 짝 없는 하나는 1칸
        var v = Checker.Term();
        Checker.Feed(v, "\U0001F1F0a");
        c.Equal((2, 0), Cursor(v), "지역 표시 문자 하나는 1칸");
    }

    public static void EraseClearsExtras(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "é하");
        Checker.Feed(t, "\r\x1b[K");
        var line = Row(t, 0);
        c.Is(line.GetExtra(0) is null && line.GetExtra(1) is null, "줄 지우기 뒤 붙은 글자 없음");

        // 덮어쓰기
        Checker.Feed(t, "é\rz");
        c.Equal("z", Copy(t, 0), "덮어쓴 칸에 결합 문자 남지 않음");
        Checker.Feed(t, "̂");
        c.Equal("ẑ", Copy(t, 0), "새 글자에만 붙음");

        // 넓은 글자의 뒤 칸을 덮으면 앞 칸 묶음도 지워진다
        Checker.Feed(t, "\r\n한\x1b[2Gy");
        c.Equal(" y", Copy(t, 1), "반쪽 덮어 쓴 묶음은 지워짐");
        c.Is(Row(t, 1).GetExtra(0) is null, "앞 칸 붙은 글자도 지워짐");

        // 글자 지우기(ECH)·화면 지우기
        Checker.Feed(t, "\r\náb́\r\x1b[X");
        c.Equal(" b́", Copy(t, 2), "ECH 는 그 칸만");
        Checker.Feed(t, "\x1b[2J");
        c.Is(Row(t, 2).Extra is null, "화면 지우기 뒤 표 비움");
    }

    public static void InsertDeleteShiftExtras(Checker c)
    {
        var t = Checker.Term(10, 2);
        Checker.Feed(t, "aéb");
        // 맨 앞에 두 칸 삽입 → 붙은 글자도 따라간다
        Checker.Feed(t, "\r\x1b[2@");
        c.Equal("  aéb", Copy(t, 0), "삽입 뒤");
        c.Equal("́", Row(t, 0).GetExtra(3), "붙은 글자 3번 칸으로");
        // 세 칸 삭제
        Checker.Feed(t, "\x1b[3P");
        c.Equal("éb", Copy(t, 0), "삭제 뒤");
        c.Equal("́", Row(t, 0).GetExtra(0), "붙은 글자 0번 칸으로");
        // 묶음 칸을 지우면 표에서 빠진다
        Checker.Feed(t, "\x1b[P");
        c.Equal("b", Copy(t, 0), "묶음 칸 삭제");
        c.Is(Row(t, 0).Extra is null, "표 비움");
    }

    public static void ResizeDropsExtras(Checker c)
    {
        var t = Checker.Term(10, 2);
        Checker.Feed(t, "abcdefgh́");
        var line = Row(t, 0);
        line.Resize(5);
        c.Is(line.Extra is null, "줄이 줄면 넘친 칸의 붙은 글자도 버림");
    }

    public static void ScrollbackKeepsExtras(Checker c)
    {
        var t = Checker.Term(10, 2);
        Checker.Feed(t, "é\r\n1\r\n2\r\n");
        c.Equal(2, t.ScrollbackCount, "스크롤백 2줄");
        lock (t.Gate) c.Equal("́", t.GetVisibleLine(0, 2).GetExtra(0), "스크롤백 줄에도 붙은 글자");
        c.Equal("é", t.GetText(t.DroppedLines, 0, t.DroppedLines, 9), "스크롤백 복사");
    }

    public static void CombiningCap(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "a" + string.Concat(Enumerable.Repeat("́", 200)) + "b");
        c.Is(Row(t, 0).GetExtra(0)!.Length <= TerminalBuffer.Line.MaxExtra, "붙는 글자 수 제한");
        c.Equal((2, 0), Cursor(t), "커서");
    }
}
