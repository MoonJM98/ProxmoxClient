namespace ProxmoxClient.Core.Vnc;

/// <summary>콘솔 창이 쓰는 그래픽 콘솔 프로토콜.</summary>
public enum ConsoleProtocol
{
    Vnc,
    Rdp
}

/// <summary>
///     그래픽 콘솔 세션(VNC·RDP 공통) — 콘솔 창은 이 모양만 보고 화면을 그리고 입력을 보낸다.
///     이벤트는 백그라운드 스레드에서 발생하므로 UI 에서 디스패처 마샬링이 필요하다.
///     프레임버퍼는 BGRX(픽셀당 4바이트, 행 간격 = 가로 × 4)다.
/// </summary>
public interface IConsoleSession : IDisposable
{
    bool IsConnected { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>프레임버퍼 원본 — 데이터가 바뀌면 <see cref="FrameReceived" /> 가 알린다.</summary>
    byte[]? Framebuffer { get; }

    /// <summary>UI 표시용 상태 문자열.</summary>
    event Action<string>? StatusChanged;

    /// <summary>콘솔 연결 확립. 인자: 가로, 세로.</summary>
    event Action<int, int>? Connected;

    /// <summary>프레임버퍼 갱신 신호(변경 영역 x,y,w,h).</summary>
    event Action<int, int, int, int>? FrameReceived;

    /// <summary>커서 모양 갱신(픽셀·크기·핫스팟, 크기 0 은 숨김).</summary>
    event Action<RfbCursor>? CursorShape;

    /// <summary>게스트가 기본 커서로 돌아갔다(RDP) — 대체 커서를 쓴다.</summary>
    event Action? CursorDefault;

    /// <summary>게스트 키보드 LED 변경(VNC — 서버가 QEMU LED State 를 지원할 때).</summary>
    event Action<KeyboardLeds>? LedState;

    /// <summary>연결 종료. null=정상, 아니면 오류.</summary>
    event Action<Exception?>? Closed;

    /// <summary>게스트 클립보드가 바뀌었다(VNC 확장 클립보드).</summary>
    event Action<string>? ClipboardReceived;

    /// <summary>콘솔을 연다(QEMU VM).</summary>
    Task ConnectAsync(string node, int vmid, CancellationToken ct = default);

    /// <summary>키 이벤트 — XT 스캔코드(0xE0nn = 확장), 스캔코드가 없는 키는 keysym(VNC 만 쓴다).</summary>
    Task SendKeyAsync(int xtScanCode, bool down, int keysym = 0);

    /// <summary>포인터 이벤트 — RFB 버튼 마스크(1 왼쪽, 2 가운데, 4 오른쪽, 8/16 휠 위/아래)와 게스트 좌표.</summary>
    Task SendPointerAsync(int buttonMask, int x, int y);

    Task SendClipboardAsync(string text);
    Task RequestClipboardAsync();
    Task SendCtrlAltDelAsync();

    /// <summary>게스트 해상도를 이 크기로 바꿔 달라고 요청한다(RDP 디스플레이 제어) — 못 하면 무시한다.</summary>
    void RequestDesktopSize(int width, int height);

    Task DisconnectAsync();

    /// <summary>대역폭·프레임 통계 한 줄(호출 시점부터 재측정).</summary>
    string TakeStatsText();
}
