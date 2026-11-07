using System.Text;
using Avalonia.Input;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     키 → 터미널 입력(xterm 규칙). 글자 키는 TextInput 으로 들어오므로 여기서는 특수 키와 Ctrl/Alt 조합만 만든다.
/// </summary>
public static class TerminalKeys
{
    /// <summary>
    ///     특수 키·조합 키의 입력 문자열. 글자로 들어올 키(Ctrl·Alt 없는 글자, AltGr 로 만든 글자)는 null.
    ///     <paramref name="keySymbol" /> 은 자판 배열대로 만들어진 글자(KeyEventArgs.KeySymbol) — Alt+기호를 정확히 보내고 AltGr 를 가린다.
    /// </summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursor, bool applicationKeypad = false,
        bool newLineMode = false, string? keySymbol = null, PhysicalKey physicalKey = PhysicalKey.None)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        // 숫자 키패드 — 응용 키패드 모드(DECKPAM)면 ESC O 로 보낸다. 아니면 글자로 들어온다
        if (applicationKeypad && Keypad(key, physicalKey) is { } pad) return "\x1bO" + pad;

        switch (key)
        {
            case Key.Enter:
                var enter = newLineMode ? "\r\n" : "\r";
                return alt || shift && !ctrl ? "\x1b" + enter : enter; // Shift+Enter — 줄바꿈(Claude Code)
            case Key.Back:
                return (alt ? "\x1b" : "") + (ctrl ? "\b" : "\x7f");
            case Key.Tab:
                return shift ? "\x1b[Z" : "\t";
            case Key.Escape:
                return "\x1b";
            case Key.Up:
                return Cursor('A', mod, applicationCursor);
            case Key.Down:
                return Cursor('B', mod, applicationCursor);
            case Key.Right:
                return Cursor('C', mod, applicationCursor);
            case Key.Left:
                return Cursor('D', mod, applicationCursor);
            case Key.Home:
                return Cursor('H', mod, applicationCursor);
            case Key.End:
                return Cursor('F', mod, applicationCursor);
            case Key.Insert:
                return Tilde(2, mod);
            case Key.Delete:
                return Tilde(3, mod);
            case Key.PageUp:
                return Tilde(5, mod);
            case Key.PageDown:
                return Tilde(6, mod);
            case >= Key.F1 and <= Key.F4:
                var f = (char)('P' + (key - Key.F1));
                return mod == 1 ? $"\x1bO{f}" : $"\x1b[1;{mod}{f}";
            case >= Key.F5 and <= Key.F12:
                int[] codes = [15, 17, 18, 19, 20, 21, 23, 24];
                return Tilde(codes[key - Key.F5], mod);
            case >= Key.F13 and <= Key.F20:
                // xterm: F13~F20 = Shift+F1~F8
                return Encode(Key.F1 + (key - Key.F13), modifiers | KeyModifiers.Shift, applicationCursor);
        }

        if (!ctrl && !alt) return null;

        // AltGr(Windows 는 Ctrl+Alt, Android 는 오른쪽 Alt)로 만든 글자 — 글자로 들어오게 둔다.
        // 다른 언어 배열의 글자(러시아어 в 등)는 AltGr 가 아니다 — Alt 만이면 ESC 를 붙여 보낸다
        var symbol = alt ? Printable(keySymbol) : null;
        if (symbol is not null && !IsKeyOwnSymbol(key, symbol) &&
            (ctrl || !(key is >= Key.A and <= Key.Z && char.IsLetter(symbol, 0))))
            return null;

        // Ctrl+글자 → 제어 문자, Alt+글자 → ESC 접두
        if (ctrl)
        {
            if (Character(key, shift) is not { } c || Control(c) is not { } control) return null;

            return alt ? "\x1b" + control : control.ToString();
        }

        // Alt 만 — 자판 배열대로 만든 글자를 쓴다(Alt+Shift+. → ESC >)
        if (symbol is not null) return "\x1b" + symbol;
        if (Shifted(key, shift) is not { } ch) return null;

        return "\x1b" + ch;
    }

    /// <summary>
    ///     고정해 둔 수식키(특수 키 막대)를 들어온 글자 하나에 붙인다 — Ctrl → 제어 문자, Alt → ESC 접두, Shift → 대문자.
    ///     대응이 없으면 그대로.
    /// </summary>
    public static string WithModifiers(string text, bool ctrl, bool alt, bool shift = false)
    {
        if (text.Length == 0) return text;

        if (shift && text.Length == 1)
        {
            if (text == "\r") return "\x1b\r"; // Shift+Enter
            if (text == "\t") return "\x1b[Z";
            text = text.ToUpperInvariant();
        }

        if (ctrl && text.Length == 1 && Control(text[0]) is { } control) text = control.ToString();
        return alt ? "\x1b" + text : text;
    }

    private static char? Control(char c)
    {
        return c switch
        {
            >= 'a' and <= 'z' => (char)(c - 'a' + 1),
            >= 'A' and <= 'Z' => (char)(c - 'A' + 1),
            '@' or ' ' or '2' => '\0',
            '[' or '3' => '\x1b',
            '\\' or '4' => '\x1c',
            ']' or '5' => '\x1d',
            '^' or '6' => '\x1e',
            '_' or '-' or '7' or '/' => '\x1f',
            '?' or '8' => '\x7f',
            _ => null
        };
    }

    /// <summary>Ctrl 조합에 쓰는 글자(Shift 는 글자·?·_·~·+ 에만).</summary>
    private static char? Character(Key key, bool shift)
    {
        if (key is >= Key.A and <= Key.Z) return (char)((shift ? 'A' : 'a') + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9) return (char)('0' + (key - Key.D0));
        return key switch
        {
            Key.Space => ' ',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemPipe or Key.OemBackslash => '\\',
            Key.OemMinus => shift ? '_' : '-',
            Key.OemQuestion => shift ? '?' : '/',
            Key.OemPeriod => '.',
            Key.OemComma => ',',
            Key.OemSemicolon => ';',
            Key.OemQuotes => '\'',
            Key.OemTilde => shift ? '~' : '`',
            Key.OemPlus => shift ? '+' : '=',
            _ => null
        };
    }

    /// <summary>미국 배열에서 이 키가 만드는 글자(KeySymbol 이 없을 때 Alt 조합에 쓴다).</summary>
    private static char? Shifted(Key key, bool shift)
    {
        if (!shift) return Character(key, false);
        if (key is >= Key.D0 and <= Key.D9) return ")!@#$%^&*("[key - Key.D0];
        return key switch
        {
            Key.OemOpenBrackets => '{',
            Key.OemCloseBrackets => '}',
            Key.OemPipe or Key.OemBackslash => '|',
            Key.OemPeriod => '>',
            Key.OemComma => '<',
            Key.OemSemicolon => ':',
            Key.OemQuotes => '"',
            _ => Character(key, true)
        };
    }

    /// <summary>KeySymbol 이 이 키 자체의 글자(미국 배열 기준, Shift 포함)인가 — 아니면 AltGr 등으로 만든 다른 글자.</summary>
    private static bool IsKeyOwnSymbol(Key key, string symbol)
    {
        if (symbol.Length != 1) return false;

        var s = symbol[0];
        return Shifted(key, false) is { } a && char.ToLowerInvariant(a) == char.ToLowerInvariant(s) ||
               Shifted(key, true) is { } b && b == s;
    }

    /// <summary>보이는 글자 하나(제어 문자·빈 값이면 null).</summary>
    private static string? Printable(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return null;
        if (!Rune.TryGetRuneAt(symbol, 0, out var rune) || rune.Utf16SequenceLength != symbol.Length) return null;

        return Rune.IsControl(rune) ? null : symbol;
    }

    /// <summary>응용 키패드의 ESC O 다음 글자.</summary>
    private static char? Keypad(Key key, PhysicalKey physical)
    {
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return (char)('p' + (key - Key.NumPad0));
        if (physical == PhysicalKey.NumPadEnter) return 'M';
        return key switch
        {
            Key.Add => 'k',
            Key.Separator => 'l',
            Key.Subtract => 'm',
            Key.Multiply => 'j',
            Key.Divide => 'o',
            Key.Decimal => 'n',
            _ => null
        };
    }

    private static string Cursor(char final, int mod, bool application)
    {
        if (mod != 1) return $"\x1b[1;{mod}{final}";
        return application ? $"\x1bO{final}" : $"\x1b[{final}";
    }

    private static string Tilde(int code, int mod)
    {
        return mod == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{mod}~";
    }

    /// <summary>SGR 가 아닌 부호의 좌표 한계 — 32+95 = 127. 넘으면 UTF-8 로 여러 바이트가 되어 프로그램이 잘못 읽는다.</summary>
    public const int LegacyMouseMax = 95;

    /// <summary>
    ///     마우스 보고(SGR 1006 또는 기본 X10 부호). 버튼: 0 왼쪽 1 가운데 2 오른쪽 64/65 휠 위·아래 66/67 휠 왼쪽·오른쪽,
    ///     32 를 더하면 끌기(35 = 누른 단추 없이 움직임). 수식키는 Shift +4 · Alt +8 · Ctrl +16.
    /// </summary>
    public static string Mouse(int button, int column, int row, bool release, bool sgr,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var mods = (modifiers.HasFlag(KeyModifiers.Shift) ? 4 : 0) + (modifiers.HasFlag(KeyModifiers.Alt) ? 8 : 0) +
                   (modifiers.HasFlag(KeyModifiers.Control) ? 16 : 0);
        if (sgr) return $"\x1b[<{button + mods};{column + 1};{row + 1}{(release ? 'm' : 'M')}";

        var b = (release ? 3 : button) + mods;
        var x = Math.Clamp(column + 1, 1, LegacyMouseMax);
        var y = Math.Clamp(row + 1, 1, LegacyMouseMax);
        return $"\x1b[M{(char)(32 + b)}{(char)(32 + x)}{(char)(32 + y)}";
    }
}
