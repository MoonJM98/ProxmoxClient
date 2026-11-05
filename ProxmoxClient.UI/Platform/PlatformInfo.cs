namespace ProxmoxClient.UI.Platform;

/// <summary>화면 형태 — 같은 뷰모델을 데스크톱은 목록+상세 분할 화면으로, 모바일은 페이지 스택으로 보여 준다.</summary>
public enum FormFactor
{
    Mobile,
    Desktop
}

public static class PlatformInfo
{
    /// <summary>
    ///     화면 형태 — 각 헤드가 앱을 만들기 전에 정한다(데스크톱 헤드 = Desktop, Android = Mobile).
    ///     테마가 이 값으로 데스크톱(WPF 와 같은 크기)·모바일(손가락 크기) 스타일을 고른다.
    /// </summary>
    public static FormFactor FormFactor { get; set; } = FormFactor.Mobile;

    public static bool IsDesktop => FormFactor == FormFactor.Desktop;
}
