using ProxmoxClient.UI.Views.Terminal;

/// <summary>제어열 해석·질의 응답(DECRQSS·XTGETTCAP·XTVERSION·DA·DSR)·원격 복사.</summary>
public static class ParserTests
{
    public static void Charsets(Checker c)
    {
        var t = Checker.Term();
        // G2 지정(ESC * 0)은 G0(지금 쓰는 집합)을 바꾸면 안 된다
        Checker.Feed(t, "\x1b*0qqq");
        c.Equal("qqq", Checker.Line(t, 0), "ESC * 0 뒤에도 G0 는 그대로");
        // 96글자 지정(ESC - A)은 'A' 를 먹고 글자로 찍지 않는다
        t = Checker.Term();
        Checker.Feed(t, "\x1b-Axy");
        c.Equal("xy", Checker.Line(t, 0), "ESC - A 는 다음 글자를 먹는다");
        // G0 를 DEC 그래픽으로
        t = Checker.Term();
        Checker.Feed(t, "\x1b(0qx\x1b(Bq");
        c.Equal("─│q", Checker.Line(t, 0), "ESC ( 0 → 선 그리기");
    }

    public static void PendingWrapClearedByErase(Checker c)
    {
        var t = Checker.Term(5, 3);
        // 끝 칸에 쓴 뒤 줄 끝 지우기(EL) → 다음 글자는 같은 줄 끝 칸에(넘어가지 않음)
        Checker.Feed(t, "abcde\x1b[KZ");
        c.Equal("abcdZ", Checker.Line(t, 0), "EL 뒤 같은 줄");
        c.Equal("", Checker.Line(t, 1), "다음 줄 비어 있음");
    }

    public static void UnterminatedStrings(Checker c)
    {
        var t = Checker.Term();
        // 끝나지 않은 OSC 뒤에 새 제어열 — OSC 는 버리고 뒤 출력은 그대로 보여야 한다
        Checker.Feed(t, "\x1b]0;잘린 제목\x1b[1mOK");
        c.Equal("OK", Checker.Line(t, 0), "잘린 OSC 뒤 출력");
        t = Checker.Term();
        Checker.Feed(t, "\x1bPzzz\x1b[2Ccd");
        c.Equal("  cd", Checker.Line(t, 0), "잘린 DCS 뒤 제어열 실행");
        t = Checker.Term();
        Checker.Feed(t, "\x1b]2;x\u0018ab"); // \x 는 16진 네 자리까지 먹으므로 \u
        c.Equal("ab", Checker.Line(t, 0), "CAN 이 OSC 를 끊음");
        t = Checker.Term();
        Checker.Feed(t, "\x1b]2;제목\u009cab");
        c.Is(Checker.Line(t, 0) == "ab" && t.Title == "제목", "C1 ST 로 끝남", Checker.Line(t, 0) + " / " + t.Title);
    }

    public static void C1Controls(Checker c)
    {
        var t = Checker.Term();
        // 0x9B = CSI. 글자로 그리지 않고 제어열로
        Checker.Feed(t, "ab\u009b5Gc");
        c.Equal("ab  c", Checker.Line(t, 0), "C1 CSI");
        t = Checker.Term();
        Checker.Feed(t, "a\u0085b");
        c.Is(Checker.Line(t, 0) == "a" && Checker.Line(t, 1) == "b", "C1 NEL", Checker.Screen(t));
    }

    public static void Queries(Checker c)
    {
        var t = Checker.Term(30, 10);
        c.Equal("\x1b[?62;22c", Checker.Feed(t, "\x1b[c"), "DA1");
        c.Equal("\x1b[>1;277;0c", Checker.Feed(t, "\x1b[>c"), "DA2(판 277 — vim SGR 마우스)");
        c.Equal("\x1bP!|00000000\x1b\\", Checker.Feed(t, "\x1b[=c"), "DA3");
        c.Equal("\x1b[?62;22c", Checker.Feed(t, "\x1bZ"), "DECID");
        c.Is(Checker.Feed(t, "\x1b[>q").StartsWith("\x1bP>|ProxmoxClient("), "XTVERSION", Checker.Feed(t, "\x1b[>q"));
        c.Equal("\x1b[?997;1n", Checker.Feed(t, "\x1b[?996n"), "색 테마(어두움)");
        c.Equal("", Checker.Feed(t, "\x1b[?5n"), "?5n 은 답 없음");
        c.Equal("\x1b[?1;1;1R", Checker.Feed(t, "\x1b[?6n"), "DECXCPR");
        c.Equal("\x1b[?2031;2$y", Checker.Feed(t, "\x1b[?2031$p"), "DECRQM 2031 꺼짐");
        Checker.Feed(t, "\x1b[?2031h\x1b[?12h");
        c.Equal("\x1b[?2031;1$y\x1b[?12;1$y", Checker.Feed(t, "\x1b[?2031$p\x1b[?12$p"), "DECRQM 2031·12 켜짐");
    }

    public static void Decrqss(Checker c)
    {
        var t = Checker.Term(30, 10);
        Checker.Feed(t, "\x1b[1;3;38;2;1;2;3;48;5;200m");
        c.Equal("\x1bP1$r0;1;3;38:2::1:2:3;48:5:200m\x1b\\", Checker.Feed(t, "\x1bP$qm\x1b\\"), "SGR(24비트 색)");
        Checker.Feed(t, "\x1b[0;31;102m");
        c.Equal("\x1bP1$r0;31;102m\x1b\\", Checker.Feed(t, "\x1bP$qm\x1b\\"), "SGR(기본 색)");
        Checker.Feed(t, "\x1b[2;8r");
        c.Equal("\x1bP1$r2;8r\x1b\\", Checker.Feed(t, "\x1bP$qr\x1b\\"), "스크롤 영역");
        Checker.Feed(t, "\x1b[5 q");
        c.Equal("\x1bP1$r5 q\x1b\\", Checker.Feed(t, "\x1bP$q q\x1b\\"), "커서 모양");
        c.Equal("\x1bP0$r\x1b\\", Checker.Feed(t, "\x1bP$qz\x1b\\"), "모르는 설정");
    }

    public static void Xtgettcap(Checker c)
    {
        var t = Checker.Term();
        static string Hex(string s) => Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(s));
        c.Equal($"\x1bP1+r{Hex("Tc")}\x1b\\", Checker.Feed(t, $"\x1bP+q{Hex("Tc")}\x1b\\"), "Tc(값 없는 참)");
        c.Equal($"\x1bP1+r{Hex("RGB")}={Hex("8/8/8")}\x1b\\", Checker.Feed(t, $"\x1bP+q{Hex("RGB")}\x1b\\"), "RGB");
        c.Equal($"\x1bP1+r{Hex("colors")}={Hex("256")}\x1b\\", Checker.Feed(t, $"\x1bP+q{Hex("colors")}\x1b\\"), "colors");
        c.Equal($"\x1bP0+r{Hex("Ms")}\x1b\\", Checker.Feed(t, $"\x1bP+q{Hex("Ms")}\x1b\\"), "Ms 는 알리지 않음");
    }

    public static void Osc52(Checker c)
    {
        var t = Checker.Term();
        string? copied = null;
        t.ClipboardWrite += s => copied = s;
        Checker.Feed(t, "\x1b]52;c;" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("안녕 copy")) + "\x07");
        c.Equal("안녕 copy", copied, "OSC 52 복사");
        copied = null;
        var reply = Checker.Feed(t, "\x1b]52;c;?\x07");
        c.Is(copied is null && reply == "", "읽기 요청은 무시", reply);
        // 4KB 를 넘는 복사도 잘리지 않는다
        var big = new string('x', 100_000);
        Checker.Feed(t, "\x1b]52;c;" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(big)) + "\x1b\\");
        c.Equal(big.Length, copied?.Length ?? 0, "큰 복사");
    }

    public static void Misc(Checker c)
    {
        var t = Checker.Term(10, 3);
        // REP 은 제어 문자 뒤에는 되풀이하지 않는다
        Checker.Feed(t, "a\r\x1b[3b");
        c.Equal("a", Checker.Line(t, 0), "CR 뒤 REP 없음");
        t = Checker.Term(10, 3);
        Checker.Feed(t, "a\x1b[3b");
        c.Equal("aaaa", Checker.Line(t, 0), "REP");
        // 1047 은 나올 때 대체 화면을 지운다, 47 은 남긴다
        t = Checker.Term(10, 3);
        Checker.Feed(t, "main\x1b[?47hALT\x1b[?47l\x1b[?47h");
        c.Is(Checker.Screen(t).Contains("ALT"), "47 은 대체 화면을 남김", Checker.Screen(t));
        Checker.Feed(t, "\x1b[?47l\x1b[?1047hX\x1b[?1047l\x1b[?1047h");
        c.Is(!Checker.Screen(t).Contains("X"), "1047 은 나올 때 지움", Checker.Screen(t));
        // CNL 은 아래 여백 밖에서 위로 끌어올리지 않는다
        t = Checker.Term(10, 6);
        Checker.Feed(t, "\x1b[1;3r\x1b[5;1H\x1b[E");
        c.Equal("\x1b[6;1R", Checker.Feed(t, "\x1b[6n"), "여백 밖 CNL");
        // 넓은 글자 오른쪽 반에 끼워 넣기(ICH)
        t = Checker.Term(10, 3);
        Checker.Feed(t, "한글\x1b[1;2H\x1b[@");
        c.Equal("   글", Checker.Line(t, 0), "넓은 글자 반쪽 정리(한 은 지워지고 한 칸 밀림)");
        // 잘못된 커서 모양 번호는 무시
        t = Checker.Term();
        Checker.Feed(t, "\x1b[4 q\x1b[9 q");
        c.Equal(4, t.CursorStyle, "DECSCUSR > 6 무시");
    }
}
