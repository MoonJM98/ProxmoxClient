namespace ProxmoxClient.UI.Platform;

/// <summary>
///     SPICE 콘솔 실행기 — 서버가 준 .vv 연결 파일을 이 기기의 SPICE 클라이언트로 연다.
///     구현은 <see cref="SpiceLaunchers.CreateDefault" /> 한 곳에서 고른다: Windows·Linux·macOS 는 remote-viewer 를 실행하고,
///     Android 는 헤드 프로젝트(ProxmoxClient.Android)가 ACTION_VIEW 인텐트로 넘기는 구현을 넣는다.
/// </summary>
public interface ISpiceLauncher
{
    /// <summary>이 기기에서 SPICE 클라이언트를 찾았는지 확인한다(없으면 설치 안내를 보인다).</summary>
    SpiceViewerStatus Probe();

    /// <summary>
    ///     .vv 내용(비밀번호 포함)을 SPICE 클라이언트로 연다. 진행·결과 문구는 <paramref name="report" /> 로 알린다.
    ///     클라이언트가 없으면 <see cref="SpiceOpenState.ViewerMissing" />.
    /// </summary>
    Task<SpiceOpenResult> OpenAsync(string vvContent, string title, Action<string> report);

    /// <summary>이 기기의 설치 안내(설치 방법 문구·받을 곳·자동 설치 가능 여부).</summary>
    SpiceSetupGuide Guide { get; }

    /// <summary>자동 설치 도구(Windows 의 VirtViewer MSI). 지원하지 않으면 null.</summary>
    ISpiceViewerInstaller? Installer { get; }
}

/// <summary>SPICE 클라이언트 검사 결과 — <see cref="Location" /> 은 실행 파일 경로(데스크톱) 또는 앱 이름(Android).</summary>
public sealed record SpiceViewerStatus(bool Available, string? Location);

public enum SpiceOpenState
{
    /// <summary>클라이언트를 열었다(연결은 클라이언트가 맡는다).</summary>
    Started,

    /// <summary>클라이언트가 없다 — 설치 안내가 필요하다.</summary>
    ViewerMissing,

    /// <summary>열기 자체가 실패했다(<see cref="SpiceOpenResult.Message" />).</summary>
    Failed
}

public sealed record SpiceOpenResult(SpiceOpenState State, string? Message = null);

/// <summary>
///     OS 별 설치 안내. <see cref="StepKeys" /> 는 화면에 줄마다 보이는 문구 키(Loc.T), <see cref="PageUrl" /> 은 받을 곳.
/// </summary>
public sealed record SpiceSetupGuide(string TitleKey, IReadOnlyList<string> StepKeys, string? PageUrl,
    string PageButtonKey, string InstallAskKey);

/// <summary>자동 설치 도구 — 지금은 Windows 의 VirtViewer MSI 만 있다.</summary>
public interface ISpiceViewerInstaller
{
    /// <summary>
    ///     최신 설치 파일을 받아 실행하고 설치 프로그램이 끝날 때까지 기다린다. 반환: 종료 코드(시작 못 하면 -1).
    ///     <paramref name="state" /> 는 단계 문구, <paramref name="progress" /> 는 (받은 바이트, 전체 바이트 또는 -1).
    /// </summary>
    Task<int> InstallAsync(Action<string> state, Action<long, long> progress, CancellationToken ct);
}
