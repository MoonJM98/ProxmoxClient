using System.Diagnostics;
using ProxmoxClient.UI.Views.Terminal;

/// <summary>창 크기 변경 — 주 화면·스크롤백 다시 접기, 대체 화면 자르기, 탭 위치.</summary>
public static class ReflowTests
{
    /// <summary>스크롤백·화면 전체 글자(자동 줄바꿈은 붙인다).</summary>
    private static string All(TerminalBuffer t) =>
        t.GetText(t.DroppedLines, 0, t.AbsoluteRow(t.Rows - 1, 0), t.Columns - 1).TrimEnd('\n');

    public static void LongLine(Checker c)
    {
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "0123456789abcdefghijABCDEFGHIJKLMNO");
        c.Equal((15, 1), (t.CursorX, t.CursorY), "처음 커서");

        t.Resize(10, 5);
        c.Equal("0123456789\nabcdefghij\nABCDEFGHIJ\nKLMNO\n", Checker.Screen(t), "10열 — 네 줄");
        c.Equal((5, 3), (t.CursorX, t.CursorY), "10열 커서");
        c.Equal("0123456789abcdefghijABCDEFGHIJKLMNO", All(t), "이어 붙인 글자");

        t.Resize(20, 5);
        c.Equal("0123456789abcdefghij\nABCDEFGHIJKLMNO\n\n\n", Checker.Screen(t), "20열 — 원래대로");
        c.Equal((15, 1), (t.CursorX, t.CursorY), "20열 커서");
        Checker.Feed(t, "Z");
        c.Equal("ABCDEFGHIJKLMNOZ", Checker.Line(t, 1), "이어 쓰기");
    }

    public static void TextAfterCursor(Checker c)
    {
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "hello world\r\x1b[2C");
        t.Resize(5, 5);
        c.Equal("hello\n worl\nd\n\n", Checker.Screen(t), "5열");
        c.Equal((2, 0), (t.CursorX, t.CursorY), "커서는 글자 위 그대로");
        t.Resize(20, 5);
        c.Equal("hello world", Checker.Line(t, 0), "되돌림");
        c.Equal((2, 0), (t.CursorX, t.CursorY), "되돌린 커서");

        // 커서가 글자 뒤 빈칸에 있으면 그 자리도 지킨다
        var u = Checker.Term(20, 5);
        Checker.Feed(u, "abc\x1b[1;16H");
        u.Resize(10, 5);
        u.Resize(20, 5);
        c.Equal((15, 0), (u.CursorX, u.CursorY), "빈칸 위 커서");
    }

    public static void PendingWrap(Checker c)
    {
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "01234567890123456789");
        t.Resize(10, 5);
        c.Equal((9, 1), (t.CursorX, t.CursorY), "줄 끝 커서");
        Checker.Feed(t, "X");
        c.Equal("X", Checker.Line(t, 2), "다음 글자는 다음 줄");
        t.Resize(20, 5);
        c.Equal("01234567890123456789\nX", Checker.Screen(t).TrimEnd('\n'), "되돌림");
    }

    public static void WideChars(Checker c)
    {
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "abcdefgh한글");
        t.Resize(9, 5);
        c.Equal("abcdefgh\n한글", Checker.Screen(t).TrimEnd('\n'), "넓은 글자는 다음 줄로");
        c.Is(t.GetVisibleLine(0, 0).Wrapped, "앞 줄은 이어짐");
        c.Equal((4, 1), (t.CursorX, t.CursorY), "커서");
        t.Resize(20, 5);
        c.Equal("abcdefgh한글", Checker.Line(t, 0), "되돌리면 빈칸 없이");
        c.Equal((12, 0), (t.CursorX, t.CursorY), "되돌린 커서");

        // 셸이 줄 끝에서 넘긴 넓은 글자(Print 가 남긴 빈칸)도 붙일 때 건너뛴다
        var u = Checker.Term(10, 5);
        Checker.Feed(u, "abcdefghi한");
        c.Equal("abcdefghi\n한", Checker.Screen(u).TrimEnd('\n'), "10열 출력");
        u.Resize(20, 5);
        c.Equal("abcdefghi한", Checker.Line(u, 0), "20열 — 한 줄");
        u.Resize(3, 5);
        c.Equal("abc\ndef\nghi\n한", Checker.Screen(u).TrimEnd('\n'), "3열");
    }

    public static void ManySizes(Checker c)
    {
        // 창을 끄는 동안 여러 크기를 거쳐도 처음과 같다
        var t = Checker.Term(30, 6);
        Checker.Feed(t, "첫 줄입니다 hello world 그리고 계속되는 글자\r\n둘째\r\n$ ls -al /var/lib");
        var before = Checker.Screen(t);
        var cursor = (t.CursorX, t.CursorY);
        for (var w = 29; w >= 4; w--) t.Resize(w, 6);
        for (var w = 5; w <= 30; w++) t.Resize(w, 6);
        c.Equal(before, Checker.Screen(t), "화면");
        c.Equal(cursor, (t.CursorX, t.CursorY), "커서");
    }

    public static void Scrollback(Checker c)
    {
        var t = Checker.Term(20, 3);
        var text = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var s = $"line{i}-" + new string((char)('a' + i), 20);
            text.Add(s);
            Checker.Feed(t, s + "\r\n");
        }

        Checker.Feed(t, "$ ");
        var expected = string.Join("\n", text) + "\n$";
        c.Equal(expected, All(t), "처음");

        t.Resize(10, 3);
        c.Equal(expected, All(t), "10열 — 스크롤백까지 다시 접음");
        c.Equal("$", Checker.Line(t, t.CursorY), "커서 줄은 화면에");
        c.Equal(t.ScrollbackCount, (int)(t.AbsoluteRow(0, 0) - t.DroppedLines), "절대 줄 번호");

        t.Resize(20, 3);
        c.Equal(expected, All(t), "20열 — 되돌림");
        c.Equal(text[0][..20], Checker.Line(t, 0, t.ScrollbackCount), "스크롤백 맨 위 줄");
        c.Equal((2, 2), (t.CursorX, t.CursorY), "커서");
    }

    public static void ScrollbackLimit(Checker c)
    {
        var t = new TerminalBuffer(20, 3, 10);
        for (var i = 0; i < 10; i++) Checker.Feed(t, $"{i:00}" + new string('x', 18) + "\r\n");
        var dropped = t.DroppedLines;
        t.Resize(10, 3);
        c.Is(t.ScrollbackCount <= 10, "스크롤백 한도", t.ScrollbackCount);
        c.Is(t.DroppedLines > dropped, "버린 줄 수가 는다", t.DroppedLines);
        c.Is(t.GetAbsoluteLine(t.DroppedLines) is not null, "맨 위 절대 줄");
    }

    public static void KeepsView(Checker c)
    {
        // 위로 올려 보던 줄이 다시 접은 뒤에도 맨 위에
        var t = Checker.Term(20, 3);
        for (var i = 0; i < 20; i++) Checker.Feed(t, $"row{i:00}" + new string('-', 15) + "\r\n");
        var offset = 10;
        var top = Checker.Line(t, 0, offset);
        offset = t.Resize(10, 3, offset);
        c.Equal(top[..10], Checker.Line(t, 0, offset), "좁힘");
        offset = t.Resize(20, 3, offset);
        c.Equal(top, Checker.Line(t, 0, offset), "넓힘");
        c.Equal(0, t.Resize(15, 3, 0), "맨 아래면 그대로");
    }

    public static void AltScreen(Checker c)
    {
        var t = Checker.Term(20, 3);
        for (var i = 1; i <= 5; i++) Checker.Feed(t, $"L{i}\r\n");
        Checker.Feed(t, "L6");
        c.Equal((2, 2), (t.CursorX, t.CursorY), "주 화면 커서");

        Checker.Feed(t, "\x1b[?1049h\x1b[HALTALTALTALTALTALT");
        t.Resize(20, 5);
        c.Is(t.IsAlternateScreen, "대체 화면 그대로");
        c.Equal("ALTALTALTALTALTALT", Checker.Line(t, 0), "대체 화면 글자");
        t.Resize(10, 5);
        c.Equal("ALTALTALTA", Checker.Line(t, 0), "대체 화면은 자르기만");
        c.Equal("", Checker.Line(t, 1), "다시 접지 않음");

        Checker.Feed(t, "\x1b[?1049l");
        c.Equal("L6", Checker.Line(t, t.CursorY), "되돌아온 커서 줄");
        c.Equal((2, 4), (t.CursorX, t.CursorY), "저장한 커서도 옮겨짐");
        c.Equal("L2\nL3\nL4\nL5\nL6", Checker.Screen(t), "주 화면(스크롤백에서 가져옴)");
    }

    public static void AltScreenReflowMain(Checker c)
    {
        // 대체 화면 중에 열 수가 바뀌어도 주 화면은 다시 접고 저장한 커서를 옮긴다
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "$ 0123456789abcdefghijKLM");
        Checker.Feed(t, "\x1b[?1049hvim");
        t.Resize(10, 5);
        t.Resize(20, 5);
        Checker.Feed(t, "\x1b[?1049l");
        c.Equal("$ 0123456789abcdefgh\nijKLM", Checker.Screen(t).TrimEnd('\n'), "주 화면");
        c.Equal((5, 1), (t.CursorX, t.CursorY), "저장한 커서");
    }

    public static void TabStops(Checker c)
    {
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "\x1b[3g\x1b[1;6H\x1bH\r");
        t.Resize(30, 5);
        Checker.Feed(t, "\t");
        c.Equal(5, t.CursorX, "직접 정한 탭");
        Checker.Feed(t, "\t");
        c.Equal(24, t.CursorX, "새 열은 8칸마다");
        t.Resize(10, 5);
        Checker.Feed(t, "\r\t");
        c.Equal(5, t.CursorX, "좁혀도 남음");
    }

    public static void ColoredBlanks(Checker c)
    {
        var t = Checker.Term(20, 5);
        Checker.Feed(t, "ab\x1b[41m   \x1b[0m");
        int Red() => Enumerable.Range(0, t.Rows).Sum(r => t.GetVisibleLine(r, 0).Cells.Count(x => x.Background != 0));
        c.Equal(3, Red(), "처음");
        t.Resize(3, 5);
        c.Equal(3, Red(), "3열 — 색 빈칸이 다음 줄로");
        t.Resize(20, 5);
        c.Equal(3, Red(), "되돌림");
        c.Equal(TerminalColor.Indexed(1), t.GetVisibleLine(0, 0).Cells[4].Background, "같은 자리");
    }

    public static void PromptVisible(Checker c)
    {
        var t = Checker.Term(20, 5);
        for (var i = 0; i < 4; i++) Checker.Feed(t, new string((char)('a' + i), 19) + "\r\n");
        Checker.Feed(t, "root@pve:~# ");
        t.Resize(8, 5);
        c.Is(t.CursorY is >= 0 and < 5, "커서가 화면 안", t.CursorY);
        c.Equal(":~#", Checker.Line(t, t.CursorY), "커서 줄");
        c.Equal("root@pve", Checker.Line(t, t.CursorY - 1), "프롬프트 앞부분");
        c.Equal(4, t.CursorX, "커서 열");
        t.Resize(8, 2);
        c.Equal(":~#", Checker.Line(t, t.CursorY), "줄 수를 줄여도");
        t.Resize(20, 5);
        c.Equal("root@pve:~#", Checker.Line(t, t.CursorY), "되돌림");
        c.Equal(12, t.CursorX, "되돌린 커서 열");
        c.Equal("ddddddddddddddddddd", Checker.Line(t, t.CursorY - 1), "위 줄");
    }

    public static void Performance(Checker c)
    {
        var warm = new TerminalBuffer(50, 10);
        Checker.Feed(warm, new string('w', 400));
        warm.Resize(30, 10);
        warm.Resize(50, 10);

        var t = new TerminalBuffer(200, 50);
        var line = new string('x', 190) + "\r\n";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 3100; i++) sb.Append(line);
        Checker.Feed(t, sb.ToString());
        c.Is(t.ScrollbackCount >= 3000, "스크롤백 3000줄 이상", t.ScrollbackCount);

        var times = new List<long>();
        foreach (var w in new[] { 199, 120, 200, 150, 200 })
        {
            var sw = Stopwatch.StartNew();
            t.Resize(w, 50);
            sw.Stop();
            times.Add(sw.ElapsedTicks * 1000 / Stopwatch.Frequency);
        }

        c.Is(times.Max() < 20, $"다시 접기 20ms 미만({string.Join(",", times)} ms)", string.Join(",", times) + " ms");
        c.Equal(new string('x', 190), Checker.Line(t, 0, 1), "글자 그대로");
    }
}

/// <summary>다시 감기 + 결합 문자 — 두 기능이 만나는 곳.</summary>
public static class ReflowUnicodeTests
{
    public static void CombiningSurvivesReflow(Checker c)
    {
        var t = Checker.Term(10, 4);
        // NFD 한(ᄒ+ᅡ+ᆫ)과 é(e+U+0301)를 둘째 조각에 둔 긴 줄 → 좁혔다 넓혀도 묶음이 남아야 한다
        Checker.Feed(t, "abcdefgh한xéz");
        t.Resize(5, 6);
        t.Resize(10, 4);
        string Text()
        {
            lock (t.Gate) return t.GetText(t.AbsoluteRow(0, 0), 0, t.AbsoluteRow(3, 0), 9);
        }

        var text = Text();
        c.Is(text.Contains("한") && text.Contains("é"), "다시 감은 뒤 결합 문자", text);
    }
}
