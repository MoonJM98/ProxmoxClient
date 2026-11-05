using System.Globalization;
using System.Resources;

namespace ProxmoxClient.UI.Localization;

/// <summary>
///     화면 문구. 기본(중립) 리소스가 한국어, *.en.resx 가 영어다.
///     Localization 폴더의 resx 를 모두 찾아 차례로 본다 — Strings(앱 고유) → App(WPF 앱에서 옮긴 문구) → 기능별 파일.
///     기능을 옮길 때는 그 기능의 resx 를 새로 만들면 되고, 다른 파일을 고칠 필요가 없다(병렬 작업 충돌 방지).
///     기기 언어가 한국어가 아니면 영어로 보여 준다(다른 언어 번역이 없으므로 한국어보다 영어가 낫다).
/// </summary>
public static class L
{
    private const string Prefix = "ProxmoxClient.UI.Localization.";
    private const string Suffix = ".resources";

    private static readonly ResourceManager[] Managers = typeof(L).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) &&
                       name.EndsWith(Suffix, StringComparison.Ordinal))
        .Select(name => name[..^Suffix.Length])
        .OrderBy(name => name switch
        {
            Prefix + "Strings" => 0,
            Prefix + "App" => 1,
            _ => 2
        })
        .ThenBy(name => name, StringComparer.Ordinal)
        .Select(name => new ResourceManager(name, typeof(L).Assembly))
        .ToArray();

    /// <summary>앱 시작 시 한 번 — UI 언어를 정한다(Core 메시지도 같은 문화권을 따른다).</summary>
    public static void InitializeCulture()
    {
        var current = CultureInfo.CurrentUICulture;
        var ui = current.TwoLetterISOLanguageName == "ko" ? current : CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;
    }

    public static bool Has(string key)
    {
        return Managers.Any(m => m.GetString(key, CultureInfo.InvariantCulture) is not null);
    }

    public static string T(string key)
    {
        foreach (var manager in Managers)
            if (manager.GetString(key, CultureInfo.CurrentUICulture) is { } text)
                return text;

        return key;
    }

    public static string T(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentUICulture, T(key), args);
    }
}

/// <summary>WPF 앱의 Loc 와 같은 모양 — 옮겨 온 코드의 Loc.T(...) 를 그대로 쓸 수 있게 한다.</summary>
public static class Loc
{
    public static CultureInfo Culture => CultureInfo.CurrentUICulture;

    public static string T(string key)
    {
        return L.T(key);
    }

    public static string T(string key, params object?[] args)
    {
        return L.T(key, args);
    }
}
