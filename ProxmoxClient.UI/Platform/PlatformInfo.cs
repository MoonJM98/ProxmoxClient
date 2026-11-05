namespace ProxmoxClient.UI.Platform;

/// <summary>화면 형태 — 같은 뷰모델을 데스크톱은 목록+상세 분할 화면으로, 모바일은 페이지 스택으로 보여 준다.</summary>
public enum FormFactor
{
    Mobile,
    Desktop
}

public static class PlatformInfo
{
    /// <summary>앱 시작 시 수명 주기 형식으로 정한다(창이 있는 데스크톱 = Desktop).</summary>
    public static FormFactor FormFactor { get; internal set; } = FormFactor.Mobile;

    public static bool IsDesktop => FormFactor == FormFactor.Desktop;
}
