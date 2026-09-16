using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace ProxmoxClient.App.Localization;

/// <summary>
///     화면 문자열 공급자. XAML 은 <c>{loc:Tr 키}</c>, 코드는 <see cref="T" /> 로 읽는다.
///     인덱서 바인딩이라 언어를 바꾸면 이미 표시 중인 창의 글자까지 즉시 갱신된다(재시작 불필요).
///     값이 없는 키는 키 이름을 그대로 돌려줘, 번역이 비어도 화면이 비지 않는다.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>설정에 저장하는 "시스템 언어 따름" 값.</summary>
    public const string AutoLanguage = "auto";

    private static readonly ResourceManager Resources =
        new("ProxmoxClient.App.Localization.Strings", typeof(Loc).Assembly);

    private static bool _wpfLanguageOverridden;

    private Loc()
    {
    }

    /// <summary>XAML 바인딩 원본(단일 인스턴스).</summary>
    public static Loc Current { get; } = new();

    /// <summary>현재 표시 언어. <see cref="AutoLanguage" /> 이면 Windows 표시 언어를 따른다.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>키로 문자열 조회 — 바인딩 경로 <c>[키]</c> 가 여기로 들어온다.</summary>
    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>코드에서 문자열 조회.</summary>
    public static string T(string key)
    {
        return Current[key];
    }

    /// <summary>코드에서 서식 문자열 조회({0}, {1} 자리 채움).</summary>
    public static string T(string key, params object?[] args)
    {
        return string.Format(Culture, Current[key], args);
    }

    /// <summary>
    ///     표시 언어 변경. 문자열·날짜·숫자 서식과 WPF 텍스트 처리(FlowDirection·줄바꿈 규칙)에 함께 적용하고,
    ///     모든 인덱서 바인딩을 다시 읽게 한다.
    /// </summary>
    public static void SetLanguage(string? language)
    {
        var culture = string.IsNullOrWhiteSpace(language) || language == AutoLanguage
            ? CultureInfo.CurrentUICulture
            : TryCreate(language) ?? CultureInfo.CurrentUICulture;

        Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;

        ApplyToWpf(culture);
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    /// <summary>
    ///     WPF 요소의 <c>Language</c>(숫자·날짜 서식, 줄바꿈 규칙) 반영.
    ///     OverrideMetadata 는 같은 속성에 두 번 호출하면 예외가 나므로 최초 1회만 하고,
    ///     이후 전환에서는 이미 열려 있는 창의 Language 를 직접 바꾼다.
    /// </summary>
    private static void ApplyToWpf(CultureInfo culture)
    {
        var language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        if (!_wpfLanguageOverridden)
        {
            _wpfLanguageOverridden = true;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement), new FrameworkPropertyMetadata(language));
        }

        if (Application.Current is { } app)
            foreach (Window window in app.Windows)
                window.Language = language; // 자식 요소로 상속된다
    }

    private static CultureInfo? TryCreate(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return null; // 알 수 없는 언어 코드는 시스템 언어로 대체
        }
    }
}