namespace ProxmoxClient.UI.Views.Console;

/// <summary>
///     화면(소프트) 키보드 글자 → 게스트 키 누름. 물리 키 정보가 없으므로 미국 배열 기준 키 위치(XT 스캔코드)와 Shift 로 바꿔
///     QEMU 확장 키 이벤트로 보낸다 — keysym 만 보내면 QEMU 가 대문자·Shift 기호를 Shift 없이 눌러("Hello!" → "hello1")
///     글자가 바뀐다. 한글은 두벌식 자판 키 순서로 풀어 보낸다(게스트 입력기가 한글 모드일 때 그대로 조합된다).
/// </summary>
public static class SoftKeys
{
    public const int XtShiftL = 0x2A, XtControlL = 0x1D, XtAltL = 0x38, XtSuperL = 0xE05B, XtReturn = 0x1C;

    /// <summary>한/영 키(한국어 키보드, QEMU qcode lang1 = set1 0xF2).</summary>
    public const int XtHangul = 0xF2;

    /// <summary>오른쪽 Alt — 한국어 Windows(101/104키 배열)에서 한/영으로 쓰는 경우가 많다.</summary>
    public const int XtAltR = 0xE038;

    /// <summary>키 하나 누름 — 스캔코드, Shift 를 함께 누를지, 서버가 확장 키를 모를 때 쓸 keysym, CapsLock 영향(라틴 글자).</summary>
    public readonly record struct Stroke(int Xt, bool Shift, int Keysym, bool IsLetter);

    /// <summary>미국 배열 — Shift 없이 / Shift 로 나오는 글자와 그 키.</summary>
    private static readonly (char Plain, char Shifted, int Xt)[] UsKeys =
    [
        ('`', '~', 0x29), ('1', '!', 0x02), ('2', '@', 0x03), ('3', '#', 0x04), ('4', '$', 0x05), ('5', '%', 0x06),
        ('6', '^', 0x07), ('7', '&', 0x08), ('8', '*', 0x09), ('9', '(', 0x0A), ('0', ')', 0x0B), ('-', '_', 0x0C),
        ('=', '+', 0x0D),
        ('q', 'Q', 0x10), ('w', 'W', 0x11), ('e', 'E', 0x12), ('r', 'R', 0x13), ('t', 'T', 0x14), ('y', 'Y', 0x15),
        ('u', 'U', 0x16), ('i', 'I', 0x17), ('o', 'O', 0x18), ('p', 'P', 0x19), ('[', '{', 0x1A), (']', '}', 0x1B),
        ('\\', '|', 0x2B),
        ('a', 'A', 0x1E), ('s', 'S', 0x1F), ('d', 'D', 0x20), ('f', 'F', 0x21), ('g', 'G', 0x22), ('h', 'H', 0x23),
        ('j', 'J', 0x24), ('k', 'K', 0x25), ('l', 'L', 0x26), (';', ':', 0x27), ('\'', '"', 0x28),
        ('z', 'Z', 0x2C), ('x', 'X', 0x2D), ('c', 'C', 0x2E), ('v', 'V', 0x2F), ('b', 'B', 0x30), ('n', 'N', 0x31),
        ('m', 'M', 0x32), (',', '<', 0x33), ('.', '>', 0x34), ('/', '?', 0x35)
    ];

    private static readonly Dictionary<char, (int Xt, bool Shift)> Ascii = BuildAscii();

    /// <summary>특수키 keysym → 스캔코드(특수키 막대 · 입력 칸의 지우기·엔터).</summary>
    private static readonly Dictionary<int, int> SpecialXt = new()
    {
        [KeySyms.BackSpace] = 0x0E, [KeySyms.Tab] = 0x0F, [KeySyms.Return] = XtReturn, [KeySyms.Escape] = 0x01,
        [KeySyms.Delete] = 0xE053, [KeySyms.Insert] = 0xE052, [KeySyms.Home] = 0xE047, [KeySyms.End] = 0xE04F,
        [KeySyms.PageUp] = 0xE049, [KeySyms.PageDown] = 0xE051, [KeySyms.Left] = 0xE04B, [KeySyms.Up] = 0xE048,
        [KeySyms.Right] = 0xE04D, [KeySyms.Down] = 0xE050,
        [KeySyms.ShiftL] = XtShiftL, [KeySyms.ControlL] = XtControlL, [KeySyms.AltL] = XtAltL,
        [KeySyms.SuperL] = XtSuperL,
        [KeySyms.F1] = 0x3B, [KeySyms.F1 + 1] = 0x3C, [KeySyms.F1 + 2] = 0x3D, [KeySyms.F1 + 3] = 0x3E,
        [KeySyms.F1 + 4] = 0x3F, [KeySyms.F1 + 5] = 0x40, [KeySyms.F1 + 6] = 0x41, [KeySyms.F1 + 7] = 0x42,
        [KeySyms.F1 + 8] = 0x43, [KeySyms.F1 + 9] = 0x44, [KeySyms.F1 + 10] = 0x57, [KeySyms.F1 + 11] = 0x58
    };

    // 두벌식 — 초성 19 · 중성 21 · 종성 28(0 = 없음), 호환 자모 ㄱ(U+3131)~ㅣ(U+3163) 51 자. 대문자 = Shift
    private static readonly string[] Initials = "r R s e E f a q Q t T d w W c z x v g".Split(' ');

    private static readonly string[] Medials = "k o i O j p u P h hk ho hl y n nj np nl b m ml l".Split(' ');

    private static readonly string[] Finals =
        ("- r R rt s sw sg e f fr fa fq ft fx fv fg a q qt t T d w c z x v g").Split(' ');

    private static readonly string[] CompatJamo =
        ("r R rt s sw sg e E f fr fa fq ft fx fv fg a q Q qt t T d w W c z x v g " +
         "k o i O j p u P h hk ho hl y n nj np nl b m ml l").Split(' ');

    private static Dictionary<char, (int, bool)> BuildAscii()
    {
        var map = new Dictionary<char, (int, bool)> { [' '] = (0x39, false) };
        foreach (var (plain, shifted, xt) in UsKeys)
        {
            map[plain] = (xt, false);
            map[shifted] = (xt, true);
        }

        return map;
    }

    /// <summary>특수키 keysym 의 스캔코드 — 모르면 0(keysym 으로만 보낸다).</summary>
    public static int XtForKeysym(int keysym)
    {
        return SpecialXt.GetValueOrDefault(keysym);
    }

    /// <summary>
    ///     글자 하나를 누를 키들로 바꾼다. 못 바꾸는 글자(미국 배열에 없는 기호·한글 외 문자)는 false —
    ///     그때는 keysym 으로만 보내 보고(대개 게스트가 버린다) 클립보드 붙여넣기를 권한다.
    /// </summary>
    public static bool TryStrokes(int codePoint, List<Stroke> output)
    {
        switch (codePoint)
        {
            case '\n' or '\r':
                output.Add(new Stroke(XtReturn, false, KeySyms.Return, false));
                return true;
            case '\t':
                output.Add(new Stroke(0x0F, false, KeySyms.Tab, false));
                return true;
            case '\b':
                output.Add(new Stroke(0x0E, false, KeySyms.BackSpace, false));
                return true;
        }

        if (codePoint < 0x80 && Ascii.TryGetValue((char)codePoint, out var key))
        {
            output.Add(new Stroke(key.Xt, key.Shift, codePoint, char.IsAsciiLetter((char)codePoint)));
            return true;
        }

        return TryHangul(codePoint, output);
    }

    /// <summary>한글 음절(가~힣)·호환 자모(ㄱ~ㅣ) → 두벌식 키 순서. 예: 한 = g k s, 꽉 = R h k r.</summary>
    public static bool TryHangul(int codePoint, List<Stroke> output)
    {
        if (codePoint is >= 0xAC00 and <= 0xD7A3)
        {
            var s = codePoint - 0xAC00;
            AddKeys(Initials[s / 588], output);
            AddKeys(Medials[s % 588 / 28], output);
            if (s % 28 != 0) AddKeys(Finals[s % 28], output);
            return true;
        }

        if (codePoint is >= 0x3131 and <= 0x3163)
        {
            AddKeys(CompatJamo[codePoint - 0x3131], output);
            return true;
        }

        return false;
    }

    private static void AddKeys(string keys, List<Stroke> output)
    {
        foreach (var c in keys)
        {
            var (xt, shift) = Ascii[c];
            output.Add(new Stroke(xt, shift, c, false)); // 한글 자모는 CapsLock 으로 뒤집지 않는다
        }
    }

    /// <summary>
    ///     글자 하나 → 게스트에 보낼 이벤트(스캔코드, keysym, 누름). 키로 못 바꾸는 글자는 keysym 만 보내 보고
    ///     (QEMU 는 대개 버린다) 라틴 문자 밖이면 <paramref name="unsent" /> 에 남긴다.
    /// </summary>
    public static void CharEvents(int codePoint, bool capsLock, List<(int Xt, int Keysym, bool Down)> output,
        List<int>? unsent = null)
    {
        var strokes = new List<Stroke>(6);
        if (TryStrokes(codePoint, strokes))
        {
            foreach (var stroke in strokes) AddEvents(stroke, capsLock, output);
            return;
        }

        var keysym = KeySyms.FromChar(codePoint);
        output.Add((0, keysym, true));
        output.Add((0, keysym, false));
        if (codePoint >= 0x80) unsent?.Add(codePoint);
    }

    /// <summary>
    ///     키 누름을 게스트에 보낼 이벤트 순서로(스캔코드, keysym, 누름). Shift 가 필요하면 왼쪽 Shift 로 감싼다.
    ///     게스트 CapsLock 이 켜져 있으면 라틴 글자의 Shift 를 뒤집는다(켜진 채 'A' = Shift 없이 a 키).
    /// </summary>
    public static void AddEvents(Stroke stroke, bool capsLock, List<(int Xt, int Keysym, bool Down)> output)
    {
        var shift = stroke.Shift ^ (capsLock && stroke.IsLetter);
        if (shift) output.Add((XtShiftL, KeySyms.ShiftL, true));
        output.Add((stroke.Xt, stroke.Keysym, true));
        output.Add((stroke.Xt, stroke.Keysym, false));
        if (shift) output.Add((XtShiftL, KeySyms.ShiftL, false));
    }
}
