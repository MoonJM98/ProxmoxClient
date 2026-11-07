using Avalonia;
using ProxmoxClient.UI.Views.Console;

/// <summary>VNC 콘솔 — 화면 키보드 글자 → 키(미국 배열·두벌식), 화면 배치(Viewport), RDP 해상도 계산.</summary>
public static class ConsoleTests
{
    /// <summary>이벤트를 "↓2a ↓23 ↑23 ↑2a" 처럼(스캔코드만).</summary>
    private static string Codes(List<(int Xt, int Keysym, bool Down)> events) =>
        string.Join(" ", events.Select(e => (e.Down ? "↓" : "↑") + e.Xt.ToString("x2")));

    private static List<(int Xt, int Keysym, bool Down)> Type(string text, bool capsLock = false, List<int>? unsent = null)
    {
        var events = new List<(int Xt, int Keysym, bool Down)>();
        foreach (var rune in text.EnumerateRunes()) SoftKeys.CharEvents(rune.Value, capsLock, events, unsent);
        return events;
    }

    /// <summary>대문자·Shift 기호는 왼쪽 Shift 로 감싼다 — "Hello!" 가 "hello1" 로 가던 문제.</summary>
    public static void ShiftedCharacters(Checker c)
    {
        var ev = Type("Hello!");
        c.Equal("↓2a ↓23 ↑23 ↑2a ↓12 ↑12 ↓26 ↑26 ↓26 ↑26 ↓18 ↑18 ↓2a ↓02 ↑02 ↑2a", Codes(ev), "Hello!");
        c.Equal(0x48, ev[1].Keysym, "H 의 keysym(확장 키를 모르는 서버용)");
        c.Equal("↓2a ↓28 ↑28 ↑2a ↓39 ↑39 ↓2b ↑2b ↓2a ↓0d ↑0d ↑2a", Codes(Type("\" \\+")), "따옴표·공백·역슬래시·더하기");
        c.Equal("↓1c ↑1c ↓0f ↑0f", Codes(Type("\n\t")), "엔터·탭");
        c.Equal(KeySyms.Return, Type("\r")[0].Keysym, "CR 은 Return");
    }

    /// <summary>게스트 CapsLock 이 켜져 있으면 라틴 글자의 Shift 를 뒤집는다(기호는 그대로).</summary>
    public static void CapsLockInvertsLetters(Checker c)
    {
        c.Equal("↓23 ↑23 ↓2a ↓17 ↑17 ↑2a ↓2a ↓02 ↑02 ↑2a", Codes(Type("Hi!", capsLock: true)), "CapsLock 켜짐 Hi!");
    }

    /// <summary>한글 음절 → 두벌식 키 순서(겹받침·겹모음·된소리 포함), 호환 자모도.</summary>
    public static void HangulDubeolsik(Checker c)
    {
        // 한 = ㅎ(g 22) ㅏ(k 25) ㄴ(s 1f), 글 = ㄱ(r 13) ㅡ(m 32) ㄹ(f 21)
        c.Equal("↓22 ↑22 ↓25 ↑25 ↓1f ↑1f ↓13 ↑13 ↓32 ↑32 ↓21 ↑21", Codes(Type("한글")), "한글");
        // 꽉 = ㄲ(Shift+r) ㅘ(h k) ㄱ(r)
        c.Equal("↓2a ↓13 ↑13 ↑2a ↓23 ↑23 ↓25 ↑25 ↓13 ↑13", Codes(Type("꽉")), "꽉");
        // 값 = ㄱ ㅏ ㅄ(q t), 뷁 = ㅂ ㅞ(n p) ㄺ(f r)
        c.Equal("↓13 ↑13 ↓25 ↑25 ↓10 ↑10 ↓14 ↑14", Codes(Type("값")), "값");
        c.Equal("↓10 ↑10 ↓31 ↑31 ↓19 ↑19 ↓21 ↑21 ↓13 ↑13", Codes(Type("뷁")), "뷁");
        // 예 = ㅇ ㅖ(Shift+p)
        c.Equal("↓20 ↑20 ↓2a ↓19 ↑19 ↑2a", Codes(Type("예")), "예");
        // 호환 자모: ㄳ = r t, ㅢ = m l, ㅃ = Shift+q
        c.Equal("↓13 ↑13 ↓14 ↑14 ↓32 ↑32 ↓26 ↑26 ↓2a ↓10 ↑10 ↑2a", Codes(Type("ㄳㅢㅃ")), "호환 자모");
        // 한글 자모는 CapsLock 으로 Shift 를 뒤집지 않는다(ㄱ 이 ㄲ 이 되면 안 됨)
        c.Equal("↓13 ↑13 ↓25 ↑25", Codes(Type("가", capsLock: true)), "CapsLock 켜진 한글");
        // 경계: 가(AC00)·힣(D7A3)
        c.Equal("↓22 ↑22 ↓26 ↑26 ↓22 ↑22", Codes(Type("힣")), "힣");
    }

    /// <summary>키로 못 바꾸는 글자 — keysym 으로만 보내 보고, 알림용으로 모은다.</summary>
    public static void UnmappedCharacters(Checker c)
    {
        var unsent = new List<int>();
        var ev = Type("a€😀", unsent: unsent);
        c.Equal(2, unsent.Count, "못 보낸 글자 수");
        c.Equal(0x20AC, unsent[0], "유로 기호");
        c.Is(ev.Skip(2).All(e => e.Xt == 0), "못 바꾼 글자는 스캔코드 없이 keysym 만");
        c.Equal(0x010020AC, ev[2].Keysym, "유니코드 keysym");
    }

    /// <summary>특수키 막대의 keysym → 스캔코드.</summary>
    public static void SpecialKeyScancodes(Checker c)
    {
        c.Equal(0x01, SoftKeys.XtForKeysym(KeySyms.Escape), "Esc");
        c.Equal(0xE048, SoftKeys.XtForKeysym(KeySyms.Up), "↑(확장)");
        c.Equal(0x58, SoftKeys.XtForKeysym(KeySyms.F1 + 11), "F12");
        c.Equal(0x1D, SoftKeys.XtForKeysym(KeySyms.ControlL), "Ctrl");
        c.Equal(0, SoftKeys.XtForKeysym(0x12345), "모르는 keysym");
    }

    /// <summary>1:1 은 원격 1픽셀 = 화면 물리 1픽셀(화면 배율 반영).</summary>
    public static void ViewportActualUsesPixelScale(Checker c)
    {
        var vp = new Viewport { PixelScale = 2 };
        vp.SetSizes(new Size(400, 300), new Size(1600, 1200)); // 맞춤 0.25
        vp.SetMode(ViewMode.Actual);
        c.Is(Math.Abs(vp.Scale * vp.PixelScale - 1) < 1e-9, "1:1 = 물리 1픽셀", vp.Scale);

        vp.PixelScale = 1;
        vp.SetMode(ViewMode.Actual);
        c.Is(Math.Abs(vp.Scale - 1) < 1e-9, "배율 1 이면 논리 1단위", vp.Scale);
    }

    /// <summary>보기 크기 변경(회전·키보드)은 채우기·1:1 을 다시 맞추고, 원격 크기 변경은 맞춤으로.</summary>
    public static void ViewportResize(Checker c)
    {
        var vp = new Viewport();
        vp.SetSizes(new Size(400, 800), new Size(800, 600));
        vp.SetMode(ViewMode.Fill);
        c.Is(Math.Abs(vp.Scale - 800.0 / 600) < 1e-9, "세로 화면 채우기 = 높이 맞춤", vp.Scale);

        vp.SetSizes(new Size(800, 400), new Size(800, 600)); // 가로로 돌림
        c.Equal(ViewMode.Fill, vp.Mode, "회전 뒤에도 채우기");
        c.Is(Math.Abs(vp.Scale - 1) < 1e-9, "가로 화면 채우기 = 너비 맞춤", vp.Scale);
        c.Is(vp.Offset.X <= 0 && vp.Offset.X >= 800 - 800 * vp.Scale && vp.Offset.Y <= 0, "빈 곳 없음", vp.Offset);

        vp.SetSizes(new Size(800, 400), new Size(1024, 768)); // 게스트 해상도 바뀜
        c.Equal(ViewMode.Fit, vp.Mode, "원격 크기 바뀌면 맞춤");
        c.Equal(1.0, vp.Zoom, "확대 풀림");

        // 직접 확대 — 보던 가운데가 그대로
        vp.SetSizes(new Size(800, 600), new Size(800, 600));
        vp.ZoomAt(2, new Point(200, 150)); // 원격 (200,150) 둘레
        var before = vp.ToRemote(new Point(400, 300));
        vp.SetSizes(new Size(800, 500), new Size(800, 600)); // 키보드가 올라와 줄어듦
        var after = vp.ToRemote(new Point(400, 250));
        c.Equal(ViewMode.Custom, vp.Mode, "직접 확대 유지");
        c.Is(Math.Abs(before.X - after.X) < 1 && Math.Abs(before.Y - after.Y) < 1, "가운데 유지", $"{before} → {after}");
    }

    /// <summary>RDP 해상도 — 보기 × 화면 배율, 짝수, 범위 안. 배치 전 크기는 null.</summary>
    public static void RdpDesktopSize(Checker c)
    {
        c.Equal((1050, 2066), ConsolePage.RdpSizeFor(new Size(400, 787.5), 2.625), "폰 세로");
        c.Equal((1280, 800), ConsolePage.RdpSizeFor(new Size(1280, 800), 1), "데스크톱");
        c.Equal((200, 8192), ConsolePage.RdpSizeFor(new Size(150, 9000), 1), "범위 안으로");
        c.Is(ConsolePage.RdpSizeFor(new Size(0, 0), 1) is null, "배치 전");
    }
}
