using Avalonia.Input;
using ProxmoxClient.Core.Terminal;
using ProxmoxClient.UI.Views.Terminal;

/// <summary>입력 — 키·마우스 부호, 붙여넣기 거르기, 입력 나누기, 입력에 쓰는 버퍼 모드.</summary>
public static class InputTests
{
    private static string? Key(Key key, KeyModifiers mods = KeyModifiers.None, string? symbol = null,
        bool keypad = false, bool newLine = false, PhysicalKey physical = PhysicalKey.None) =>
        TerminalKeys.Encode(key, mods, false, keypad, newLine, symbol, physical);

    public static void Keys(Checker c)
    {
        c.Equal("\x1b[Z", Key(Avalonia.Input.Key.Tab, KeyModifiers.Shift), "Shift+Tab");
        c.Equal("\x1b[Z", TerminalKeys.WithModifiers("\t", false, false, true), "고정 Shift + Tab");
        c.Equal("\x1b>", Key(Avalonia.Input.Key.OemPeriod, KeyModifiers.Alt | KeyModifiers.Shift, ">"), "Alt+Shift+. (KeySymbol)");
        c.Equal("\x1b>", Key(Avalonia.Input.Key.OemPeriod, KeyModifiers.Alt | KeyModifiers.Shift), "Alt+Shift+. (KeySymbol 없음)");
        c.Equal("\x1b!", Key(Avalonia.Input.Key.D1, KeyModifiers.Alt | KeyModifiers.Shift), "Alt+Shift+1");
        c.Equal("\x1f", Key(Avalonia.Input.Key.OemQuestion, KeyModifiers.Control), "Ctrl+/");
        c.Equal("\x7f", Key(Avalonia.Input.Key.OemQuestion, KeyModifiers.Control | KeyModifiers.Shift), "Ctrl+?");
        c.Equal("\x1b\r", Key(Avalonia.Input.Key.Enter, KeyModifiers.Shift), "Shift+Enter");
        c.Equal("\x1b\r", TerminalKeys.WithModifiers("\r", false, false, true), "고정 Shift + Enter");
        c.Equal("\r\n", Key(Avalonia.Input.Key.Enter, newLine: true), "LNM 에서 Enter");
        c.Equal("\x1b[1;2A", TerminalKeys.Encode(Avalonia.Input.Key.Up, KeyModifiers.Shift, true), "Shift+↑");
        c.Equal("\x1b[1;2P", Key(Avalonia.Input.Key.F13), "F13 = Shift+F1");
        c.Equal("\x1b[15;2~", Key(Avalonia.Input.Key.F17), "F17 = Shift+F5");
        c.Equal("\x1b[5;2~", Key(Avalonia.Input.Key.PageUp, KeyModifiers.Shift), "Shift+PgUp");
        c.Equal("A", TerminalKeys.WithModifiers("a", false, false, true), "고정 Shift + a");
        c.Equal("\x04", TerminalKeys.WithModifiers("d", true, false), "고정 Ctrl + d");
    }

    public static void AltGr(Checker c)
    {
        c.Equal(null, Key(Avalonia.Input.Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@"), "Ctrl+Alt+Q 가 '@'(Windows AltGr) — 글자로");
        c.Equal(null, Key(Avalonia.Input.Key.E, KeyModifiers.Alt, "€"), "오른쪽 Alt+E 가 '€'(Android) — 글자로");
        c.Equal(null, Key(Avalonia.Input.Key.Q, KeyModifiers.Alt, "@"), "오른쪽 Alt+Q 가 '@' — 글자로");
        c.Equal("\x1b\x01", Key(Avalonia.Input.Key.A, KeyModifiers.Control | KeyModifiers.Alt), "Ctrl+Alt+a(글자 없음) → ESC ^A");
        c.Equal("\x1b\x01", Key(Avalonia.Input.Key.A, KeyModifiers.Control | KeyModifiers.Alt, "a"), "Ctrl+Alt+a(글자 a) → ESC ^A");
        c.Equal("\u001bb", Key(Avalonia.Input.Key.B, KeyModifiers.Alt, "b"), "Alt+b");
        c.Equal("\u001bB", Key(Avalonia.Input.Key.B, KeyModifiers.Alt | KeyModifiers.Shift, "B"), "Alt+Shift+b");
        c.Equal("\x1bв", Key(Avalonia.Input.Key.D, KeyModifiers.Alt, "в"), "러시아어 배열 Alt+в — AltGr 아님");
        c.Equal("\x03", Key(Avalonia.Input.Key.C, KeyModifiers.Control, "с"), "러시아어 배열 Ctrl+C");
    }

    public static void Keypad(Checker c)
    {
        c.Equal(null, Key(Avalonia.Input.Key.NumPad5), "키패드 5(보통) — 글자로");
        c.Equal("\x1bOu", Key(Avalonia.Input.Key.NumPad5, keypad: true), "응용 키패드 5");
        c.Equal("\x1bOp", Key(Avalonia.Input.Key.NumPad0, keypad: true), "응용 키패드 0");
        c.Equal("\x1bOk", Key(Avalonia.Input.Key.Add, keypad: true), "응용 키패드 +");
        c.Equal("\x1bOm", Key(Avalonia.Input.Key.Subtract, keypad: true), "응용 키패드 -");
        c.Equal("\x1bOj", Key(Avalonia.Input.Key.Multiply, keypad: true), "응용 키패드 *");
        c.Equal("\x1bOo", Key(Avalonia.Input.Key.Divide, keypad: true), "응용 키패드 /");
        c.Equal("\x1bOn", Key(Avalonia.Input.Key.Decimal, keypad: true), "응용 키패드 .");
        c.Equal("\x1bOM", Key(Avalonia.Input.Key.Enter, keypad: true, physical: PhysicalKey.NumPadEnter), "응용 키패드 Enter");
        c.Equal("\r", Key(Avalonia.Input.Key.Enter, keypad: true, physical: PhysicalKey.Enter), "응용 키패드 모드의 보통 Enter");
    }

    public static void Mouse(Checker c)
    {
        c.Equal("\x1b[<0;3;2M", TerminalKeys.Mouse(0, 2, 1, false, true), "SGR 왼쪽 누름");
        c.Equal("\x1b[<0;3;2m", TerminalKeys.Mouse(0, 2, 1, true, true), "SGR 왼쪽 뗌");
        c.Equal("\x1b[<4;1;1M", TerminalKeys.Mouse(0, 0, 0, false, true, KeyModifiers.Shift), "Shift +4");
        c.Equal("\x1b[<8;1;1M", TerminalKeys.Mouse(0, 0, 0, false, true, KeyModifiers.Alt), "Alt +8");
        c.Equal("\x1b[<18;1;1M", TerminalKeys.Mouse(2, 0, 0, false, true, KeyModifiers.Control), "Ctrl+오른쪽 = 18");
        c.Equal("\x1b[<28;1;1M", TerminalKeys.Mouse(0, 0, 0, false, true,
            KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control), "Shift+Alt+Ctrl = 28");
        c.Equal("\x1b[<35;5;3M", TerminalKeys.Mouse(35, 4, 2, false, true), "단추 없이 움직임(35)");
        c.Equal("\x1b[<66;1;1M", TerminalKeys.Mouse(66, 0, 0, false, true), "휠 왼쪽(66)");
        c.Equal("\x1b[<67;1;1M", TerminalKeys.Mouse(67, 0, 0, false, true), "휠 오른쪽(67)");
        c.Equal("\x1b[<65;300;200M", TerminalKeys.Mouse(65, 299, 199, false, true), "SGR 은 큰 좌표 그대로");

        var legacy = TerminalKeys.Mouse(0, 299, 199, false, false);
        c.Equal("\x1b[M \x7f\x7f", legacy, "SGR 아님 — 좌표 95 로 자름(127 넘지 않게)");
        c.Is(legacy.All(ch => ch < 0x80), "SGR 아님 — 모두 7비트", legacy);
        c.Equal("\x1b[M#!!", TerminalKeys.Mouse(0, 0, 0, true, false), "SGR 아님 — 떼면 단추 3");
        c.Equal("\x1b[M3!!", TerminalKeys.Mouse(2, 0, 0, true, false, KeyModifiers.Control), "SGR 아님 — Ctrl 떼기 3+16");
        c.Equal("\x1b[M\x7f!!", TerminalKeys.Mouse(67, 0, 0, false, false, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift),
            "SGR 아님 — 가장 큰 단추 값도 127 이하");
    }

    public static void Paste(Checker c)
    {
        c.Equal("a[31mb\tc\rd", TerminalView.CleanPaste("a\x1b[31mb\x03\tc\x9b\rd\x85"), "괄호 붙여넣기 — C0·C1 제어 문자 빼기");
        c.Equal("한글 😀", TerminalView.CleanPaste("한글 😀"), "보통 글자는 그대로");
    }

    public static void Chunking(Checker c)
    {
        static int Bytes(string m) => System.Text.Encoding.UTF8.GetByteCount(m[(m.IndexOf(':', 2) + 1)..]);
        static int Declared(string m) => int.Parse(m.Split(':')[1]);

        var small = ProxmoxTerminalSession.InputMessages("ls\r").ToList();
        c.Equal("0:3:ls\r", small.Single(), "짧은 입력 — 메시지 하나");

        var big = ProxmoxTerminalSession.InputMessages(new string('a', 5000)).ToList();
        c.Equal(2, big.Count, "5000바이트 → 두 메시지");
        c.Equal("0:4096:", big[0][..7], "첫 메시지 4096바이트");
        c.Equal(904, Declared(big[1]), "둘째 메시지 904바이트");

        // 4095 + 이모지(4바이트) — 이모지를 가르지 않고 다음 메시지로
        var emoji = ProxmoxTerminalSession.InputMessages(new string('a', 4095) + "😀b").ToList();
        c.Equal(2, emoji.Count, "이모지 경계 — 두 메시지");
        c.Equal(4095, Declared(emoji[0]), "첫 메시지는 이모지 앞까지");
        c.Equal("0:5:😀b", emoji[1], "이모지는 통째로 다음 메시지");

        var korean = ProxmoxTerminalSession.InputMessages(string.Concat(Enumerable.Repeat("가", 3000))).ToList();
        c.Is(korean.All(m => Declared(m) == Bytes(m) && Declared(m) <= 4096), "한글 — 길이는 UTF-8 바이트, 4096 이하",
            string.Join(",", korean.Select(Declared)));
        c.Equal(9000, korean.Sum(Declared), "한글 — 모두 합치면 9000바이트");
        c.Equal(string.Concat(Enumerable.Repeat("가", 3000)),
            string.Concat(korean.Select(m => m[(m.IndexOf(':', 2) + 1)..])), "나눈 것을 이으면 원래 글");
    }

    public static void Modes(Checker c)
    {
        var t = Checker.Term();
        c.Is(!t.FocusReporting && t.AlternateScroll && !t.NewLineMode, "처음 — 포커스 보고 꺼짐, 대체 스크롤 켜짐, LNM 꺼짐");
        Checker.Feed(t, "\x1b[?1004h\x1b[?1007l\x1b[20h\x1b=");
        c.Is(t.FocusReporting, "?1004h → 포커스 보고");
        c.Is(!t.AlternateScroll, "?1007l → 대체 스크롤 끔");
        c.Is(t.NewLineMode, "CSI 20 h → LNM");
        c.Is(t.ApplicationKeypad, "ESC = → 응용 키패드");
        c.Equal("\x1b[?1004;1$y", Checker.Feed(t, "\x1b[?1004$p"), "DECRQM 1004");
        c.Equal("\x1b[?1007;2$y", Checker.Feed(t, "\x1b[?1007$p"), "DECRQM 1007");
        Checker.Feed(t, "\x1b[?1003h\x1b[?1006h");
        c.Is(t.MouseTracking == MouseTracking.Any && t.SgrMouse, "?1003·?1006");
        Checker.Feed(t, "\u001bc");
        c.Is(!t.FocusReporting && t.AlternateScroll && !t.NewLineMode && !t.ApplicationKeypad &&
             t.MouseTracking == MouseTracking.None, "RIS — 모두 처음대로");
    }
}
