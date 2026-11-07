using Avalonia.Input;

namespace ProxmoxClient.UI.Views.Console;

/// <summary>X11 keysym — VNC 키 이벤트의 키 번호.</summary>
public static class KeySyms
{
    public const int BackSpace = 0xFF08, Tab = 0xFF09, Return = 0xFF0D, Escape = 0xFF1B, Delete = 0xFFFF;
    public const int Home = 0xFF50, Left = 0xFF51, Up = 0xFF52, Right = 0xFF53, Down = 0xFF54;
    public const int PageUp = 0xFF55, PageDown = 0xFF56, End = 0xFF57, Insert = 0xFF63;
    public const int F1 = 0xFFBE;
    public const int ShiftL = 0xFFE1, ControlL = 0xFFE3, AltL = 0xFFE9, AltR = 0xFFEA, SuperL = 0xFFEB;

    /// <summary>한/영 전환(Hangul).</summary>
    public const int Hangul = 0xFF31;

    /// <summary>글자 하나의 keysym — Latin-1 은 그대로, 그 밖은 0x01000000 + 유니코드.</summary>
    public static int FromChar(int codePoint)
    {
        return codePoint switch
        {
            '\n' or '\r' => Return,
            '\t' => Tab,
            '\b' => BackSpace,
            < 0x100 => codePoint,
            _ => 0x01000000 | codePoint
        };
    }

    /// <summary>물리 키(데스크톱 키보드·하드웨어 키) → keysym. 글자 키는 TextInput 으로 따로 보낸다(0 반환).</summary>
    public static int FromKey(Key key)
    {
        return key switch
        {
            Key.Back => BackSpace,
            Key.Tab => Tab,
            Key.Enter => Return,
            Key.Escape => Escape,
            Key.Delete => Delete,
            Key.Insert => Insert,
            Key.Home => Home,
            Key.End => End,
            Key.PageUp => PageUp,
            Key.PageDown => PageDown,
            Key.Left => Left,
            Key.Up => Up,
            Key.Right => Right,
            Key.Down => Down,
            Key.LeftShift or Key.RightShift => ShiftL,
            Key.LeftCtrl or Key.RightCtrl => ControlL,
            Key.LeftAlt or Key.RightAlt => AltL,
            Key.LWin or Key.RWin => SuperL,
            >= Key.F1 and <= Key.F12 => F1 + (key - Key.F1),
            _ => 0
        };
    }

    /// <summary>Ctrl·Alt 를 누른 채 누른 글자 키(TextInput 이 오지 않는다) → 소문자 keysym.</summary>
    public static int LetterOrDigit(Key key)
    {
        return key switch
        {
            >= Key.A and <= Key.Z => 'a' + (key - Key.A),
            >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
            _ => 0
        };
    }
}
