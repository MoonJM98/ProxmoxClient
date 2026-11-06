using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.Presentation.Shared;

/// <summary>
///     공용 화면 로직이 앱에 맡기는 일 — 입력 대화상자·확인·보기 창. WPF 는 모달 창(주인 = Window),
///     Avalonia 는 화면 위 대화상자·화면 스택(주인 = Control, 쓰지 않을 때가 많다)으로 한다.
///     주인(owner)은 공용 코드가 들여다보지 않고 그대로 넘긴다.
/// </summary>
public interface IAppUi
{
    /// <summary>입력 대화상자 — 확인하면 입력 값, 취소하면 null.</summary>
    /// <param name="target">입력을 보낼 API 요청 — 서버가 모르는 파라미터 칸은 두지 않는다.</param>
    Task<IReadOnlyDictionary<string, string>?> FormAsync(object? owner, string title, IReadOnlyList<FormField> fields,
        Func<IReadOnlyDictionary<string, string>, string?>? validate, ApiFeature? target);

    /// <summary>
    ///     입력을 받은 뒤 서버를 바꾸는 동안 — Avalonia 는 쓰기 확인 흐름(이 작업 이름, confirmed 면 이미 물었으니 다시 묻지 않는다),
    ///     WPF 는 없음(null).
    /// </summary>
    IDisposable? BeginWrite(string title, bool confirmed);

    /// <summary>표 등 화면 하나를 띄운다(WPF 모달 창 · Avalonia 새 화면). 결과 문구(없으면 null).</summary>
    string? ShowView(object? owner, string title, ViewSpec content);

    /// <summary>긴 글을 보기 창으로. 결과 문구(없으면 null).</summary>
    string? ShowText(object? owner, string title, string text);

    /// <summary>경고 예/아니오 확인 — 예면 true. destructive 면 Avalonia 는 확인 단추를 위험 표시(빨강)로 한다.</summary>
    Task<bool> ConfirmAsync(object? owner, string text, string title, bool destructive);

    /// <summary>
    ///     되돌릴 수 없는 작업 버튼의 아이콘 — Avalonia 는 휴지통(확인 창이 위험 표시가 된다), WPF 는 작업에 맞는 아이콘 그대로.
    /// </summary>
    string DestructiveIcon(string icon);
}

/// <summary>앱이 시작할 때 넣는 <see cref="IAppUi" /> 와, 공용 코드가 부르는 짧은 이름들.</summary>
public static class AppUi
{
    private static IAppUi? _current;

    public static IAppUi Current
    {
        get => _current ?? throw new InvalidOperationException("AppUi.Current 를 앱 시작 때 넣어야 한다");
        set => _current = value;
    }

    public static Task<IReadOnlyDictionary<string, string>?> FormAsync(object? owner, string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, string?>? validate = null,
        ApiFeature? target = null)
    {
        return Current.FormAsync(owner, title, fields, validate, target);
    }

    public static IDisposable? BeginWrite(string title, bool confirmed = false)
    {
        return Current.BeginWrite(title, confirmed);
    }

    public static string? ShowView(object? owner, string title, ViewSpec content)
    {
        return Current.ShowView(owner, title, content);
    }

    public static string? ShowText(object? owner, string title, string text)
    {
        return Current.ShowText(owner, title, text);
    }

    public static Task<bool> ConfirmAsync(object? owner, string text, string title, bool destructive = false)
    {
        return Current.ConfirmAsync(owner, text, title, destructive);
    }

    public static string DestructiveIcon(string icon)
    {
        return Current.DestructiveIcon(icon);
    }
}
