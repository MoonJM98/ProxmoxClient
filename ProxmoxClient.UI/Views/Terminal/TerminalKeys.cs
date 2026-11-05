using Avalonia.Input;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     키 → 터미널 입력(xterm 규칙). 글자 키는 TextInput 으로 들어오므로 여기서는 특수 키와 Ctrl/Alt 조합만 만든다.
/// </summary>
public static class TerminalKeys
{
    /// <summary>특수 키·조합 키의 입력 문자열. 글자로 들어올 키(Ctrl·Alt 없는 글자)는 null.</summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursor)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        switch (key)
        {
            case Key.Enter:
                return alt ? "\x1b\r" : "\r";
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
        }

        if (!ctrl && !alt) return null;

        // Ctrl+글자 → 제어 문자, Alt+글자 → ESC 접두
        var c = Character(key, shift);
        if (c is null) return null;

        var ch = c.Value;
        if (ctrl)
        {
            var control = Control(ch);
            if (control is null) return null;

            ch = control.Value;
        }

        return alt ? "\x1b" + ch : ch.ToString();
    }

    /// <summary>Ctrl 을 고정해 둔 상태에서 들어온 글자(특수 키 막대) → 제어 문자. 대응이 없으면 그대로.</summary>
    public static string WithModifiers(string text, bool ctrl, bool alt)
    {
        if (text.Length == 0) return text;

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
            '_' or '-' or '7' => '\x1f',
            '?' or '8' => '\x7f',
            _ => null
        };
    }

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

    private static string Cursor(char final, int mod, bool application)
    {
        if (mod != 1) return $"\x1b[1;{mod}{final}";
        return application ? $"\x1bO{final}" : $"\x1b[{final}";
    }

    private static string Tilde(int code, int mod)
    {
        return mod == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{mod}~";
    }

    /// <summary>마우스 보고(SGR 1006 또는 기본 X10 부호). 버튼: 0 왼쪽 1 가운데 2 오른쪽 64/65 휠, 32 를 더하면 끌기.</summary>
    public static string Mouse(int button, int column, int row, bool release, bool sgr)
    {
        if (sgr) return $"\x1b[<{button};{column + 1};{row + 1}{(release ? 'm' : 'M')}";

        var b = release ? 3 : button;
        var x = Math.Min(column + 1, 223);
        var y = Math.Min(row + 1, 223);
        return $"\x1b[M{(char)(32 + b)}{(char)(32 + x)}{(char)(32 + y)}";
    }
}
