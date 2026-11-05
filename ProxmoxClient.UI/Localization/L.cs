using System.Globalization;
using System.Resources;

namespace ProxmoxClient.UI.Localization;

/// <summary>
///     화면 문구. 기본(중립) 리소스가 한국어, Strings.en.resx 가 영어다.
///     기기 언어가 한국어가 아니면 영어로 보여 준다(다른 언어 번역이 없으므로 한국어보다 영어가 낫다).
/// </summary>
public static class L
{
    private static readonly ResourceManager Manager =
        new("ProxmoxClient.UI.Localization.Strings", typeof(L).Assembly);

    /// <summary>앱 시작 시 한 번 — UI 언어를 정한다(Core 메시지도 같은 문화권을 따른다).</summary>
    public static void InitializeCulture()
    {
        var current = CultureInfo.CurrentUICulture;
        var ui = current.TwoLetterISOLanguageName == "ko" ? current : CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;
    }

    public static string T(string key)
    {
        return Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    public static string T(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentUICulture, T(key), args);
    }
}
