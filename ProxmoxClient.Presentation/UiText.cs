using System.Globalization;

namespace ProxmoxClient.Presentation;

/// <summary>
///     화면 문구 조회 — 문구는 각 앱의 리소스(WPF Strings.resx · Avalonia Localization/*.resx)에 있다.
///     앱이 시작할 때 <see cref="Use" /> 로 자기 조회(WPF Loc.T · Avalonia L.T)를 넣는다. 넣기 전에는 키를 그대로 돌려준다.
/// </summary>
public static class UiText
{
    private static Func<string, string> _text = key => key;

    private static Func<string, object?[], string> _format =
        (key, args) => string.Format(CultureInfo.CurrentUICulture, key, args);

    public static void Use(Func<string, string> text, Func<string, object?[], string> format)
    {
        _text = text;
        _format = format;
    }

    public static string T(string key)
    {
        return _text(key);
    }

    public static string T(string key, params object?[] args)
    {
        return _format(key, args);
    }
}
