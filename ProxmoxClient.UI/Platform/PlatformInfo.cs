namespace ProxmoxClient.UI.Platform;

/// <summary>
///     화면 형태 — 같은 뷰모델·화면 코드를 쓰고 배치만 나눈다.
///     Desktop: WPF 와 같은 배치·크기. Tablet: PC 와 같은 배치(표·분할 화면)에 손가락 크기.
///     Mobile(휴대폰): 페이지 스택·카드 목록·손가락 크기.
/// </summary>
public enum FormFactor
{
    Mobile,
    Desktop,
    Tablet
}

public static class PlatformInfo
{
    /// <summary>
    ///     화면 형태 — 각 헤드가 앱을 만들기 전에 정한다(데스크톱 헤드 = Desktop,
    ///     Android = 짧은 변 600dp 이상이면 Tablet, 아니면 Mobile).
    /// </summary>
    public static FormFactor FormFactor { get; set; } = FormFactor.Mobile;

    /// <summary>PC 형 배치(DataGrid 표, 왼쪽 탭 + 내용, 메인 창) — 데스크톱과 태블릿.</summary>
    public static bool IsDesktop => FormFactor != FormFactor.Mobile;

    /// <summary>손가락으로 다루는 화면 — 휴대폰과 태블릿(버튼·행·입력칸을 크게).</summary>
    public static bool IsTouch => FormFactor != FormFactor.Desktop;

    public static bool IsTablet => FormFactor == FormFactor.Tablet;
}
