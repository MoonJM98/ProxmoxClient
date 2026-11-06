using ProxmoxClient.UI.Views.Terminal;

/// <summary>기본 동작 — 검사 틀이 도는지.</summary>
public static class SmokeTests
{
    public static void Basic(Checker c)
    {
        var t = Checker.Term();
        Checker.Feed(t, "hello\r\n세계");
        c.Equal("hello", Checker.Line(t, 0), "첫 줄");
        c.Equal("세계", Checker.Line(t, 1), "한글 줄");
        c.Equal("\x1b[2;5R", Checker.Feed(t, "\x1b[6n"), "커서 위치 응답");
    }
}
