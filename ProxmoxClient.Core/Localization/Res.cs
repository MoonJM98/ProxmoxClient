using System.Globalization;
using System.Resources;

namespace ProxmoxClient.Core.Localization;

/// <summary>
///     Core(서버 통신·콘솔) 메시지 문자열. 예외 메시지와 진행 상태 안내가 화면에 그대로 보이므로 번역 대상이다.
///     언어는 현재 UI 문화권을 따른다 — 앱이 언어를 바꾸면(CultureInfo.DefaultThreadCurrentUICulture) 함께 바뀐다.
///     값이 없는 키는 키 이름을 그대로 돌려줘, 번역이 비어도 메시지가 사라지지 않는다.
/// </summary>
internal static class Res
{
    private static readonly ResourceManager Manager =
        new("ProxmoxClient.Core.Localization.CoreStrings", typeof(Res).Assembly);

    public static string T(string key)
    {
        return Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    public static string T(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentUICulture, T(key), args);
    }
}